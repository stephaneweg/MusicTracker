using System;

namespace KotonPluginElectricViolin
{
    internal struct EvParams
    {
        public float BowForce;         // 0..1 → amplitude du "mordant" à l'attaque (ouvre le LP briève­ment)
        public float BowVelocity;      // 0..1 → non utilisé en v9 (la vélocité MIDI joue déjà ce rôle)
        public float BowPosition;      // 0..0.5 → position sur la corde : 0 = chevalet (brillant), 0.5 = sultasto (feutré)
        public float Damping;          // 0..1 → non utilisé
        public float VibratoRateHz;    // 4.5..6.5 Hz typique
        public float VibratoDepthCents;// ±15..±25 cents typiques
        public float TremoloRateHz;    // ignoré (source d'effet "ensemble" retiré en v9)
        public float TremoloDepth;     // ignoré
        public float BodyIntensity;    // 0..1 → dosage du bus formants (résonance de caisse)
        public float Warmth;           // 0..1 → drive tanh (arrondit la dent de scie, "crème")
        public float BowScratch;       // 0..1 → intensité du bruit d'accroche à l'attaque (~50 ms HP noise)
        public float AttackSec;
        public float ReleaseSec;
        public float VolumeDb;
    }

    /// <summary>
    /// Electric Violin v9 — RÉÉCRITURE from scratch (2026-08-04) suivant le brief DSP Gemini pour un
    /// son "clean, crémeux, avec un peu de mordant" — sans les parasites de v8 (compresseur qui
    /// pompait, tremolo AM qui donnait l'effet ensemble, deux voies formantes qui sonnaient comme
    /// un deuxième instrument).
    ///
    /// **Chaîne** :
    ///   1. Dent de scie PolyBLEP + PITCH-BEND d'accroche subtil (−12 c → 0 en ~8 ms, élasticité de
    ///      la corde qui se tend sous l'archet)
    ///   2. LP 2-pôles (12 dB/oct) — cutoff DYNAMIQUE : base ≈ 2.5 kHz (crémeux, arrondit les
    ///      angles de la scie) + boost "mordant" au NoteOn qui ouvre briève­ment vers 5 kHz sur
    ///      ~25 ms (décroissance exp), + modulation légère par le vibrato
    ///   3. 3 formants BPF EN PARALLÈLE (Gemini) : ~400 Hz corps/bois, ~1.75 kHz table, ~3.2 kHz
    ///      mordant noble ; sommés avec le signal sec pondéré par BodyIntensity
    ///   4. Saturation tanh très douce (Warmth) — pas pour distordre, juste pour arrondir davantage
    ///   5. Enveloppe A-S-R × velocity, vibrato (délai 200 ms + fade-in 150 ms), makeup gain
    ///
    /// **Retiré volontairement** (sources de parasites en v8) :
    ///   - Compresseur (pompait à toutes les vitesses)
    ///   - Tremolo AM (donnait un effet "ensemble/pupitre")
    ///   - LP dyn 24 dB en cascade (superflu, un 2-pôles suffit et sonne mieux)
    ///   - Formants en peaking EQ additifs saturés (créaient une "seconde voix")
    /// </summary>
    internal sealed class ElectricViolinVoice
    {
        readonly int _sr;

        // ---- Oscillateur PolyBLEP ----
        double _phase;
        double _baseFreq;

        // ---- Enveloppe A-S-R ----
        enum EnvStage { Idle, Attack, Sustain, Release }
        EnvStage _stage = EnvStage.Idle;
        float _env, _envAttackRate, _envReleaseRate;

        // ---- Pitch-bend d'accroche : les 10 premières ms remontent de −12 c vers 0 ----
        float _attackBendCents;
        float _attackBendDecayCoef;   // exp toward 0

        // ---- "Mordant" : boost temporaire du cutoff LP sur les premières ms ----
        float _biteBoost;             // 0..1, décroit exp vers 0
        float _biteDecayCoef;

        // ---- Vibrato : délai + fade-in ----
        float _vibPhase;
        int _vibDelaySamplesRemaining;
        float _vibFadeIn;

        // ---- Filtre principal LP + formants (biquads RBJ) ----
        BiquadState _lpMain;
        BiquadState _form1, _form2, _form3;

        // ---- Bow noise continu (friction crin sur corde) ----
        float _scratchLpState;     // LP state pour construire le HP par soustraction
        Random _scratchRng;

        bool _active;
        int _note;
        float _velocity;

        const float SilenceThreshold = 1e-5f;
        float _peakEnvelope;

        public bool IsActive => _active;
        public int Note => _note;

        public ElectricViolinVoice(int sampleRate)
        {
            _sr = sampleRate;
            // Formants FIXES calés une fois au démarrage (le corps du violon ne change pas avec la
            // note qu'on joue). Q modéré : trop pointu = whistling / résonance identifiable comme
            // un second instrument ; trop large = coloration invisible. 3/4/5 = compromis vécu.
            SetBiquadBPF(ref _form1, sampleRate, 400f,  3f);   // corps / bois
            SetBiquadBPF(ref _form2, sampleRate, 1750f, 4f);   // table
            SetBiquadBPF(ref _form3, sampleRate, 3200f, 5f);   // formant du violon (mordant noble)
        }

        public void NoteOn(int note, float velocity, in EvParams p)
        {
            _note = note;
            _velocity = velocity;
            _baseFreq = 440.0 * Math.Pow(2.0, (note - 69) / 12.0);
            _phase = 0.0;

            _lpMain.ResetState();
            _form1.ResetState(); _form2.ResetState(); _form3.ResetState();

            // Vibrato : délai 200 ms puis fade-in 150 ms — un violon "vivant" sans que le vibrato
            // ne trahisse une note synthétique dès l'attaque.
            _vibDelaySamplesRemaining = (int)(0.2 * _sr);
            _vibFadeIn = 0f;
            _vibPhase = 0f;

            // Pitch-bend d'accroche : −12 cents qui remontent exponentiellement vers 0 (τ = 8 ms,
            // pratiquement 0 après 25 ms). Simule l'élasticité de la corde qui se tend brièvement
            // sous l'archet quand celui-ci s'accroche.
            _attackBendCents = -12f;
            _attackBendDecayCoef = (float)Math.Exp(-1.0 / (0.008 * _sr));

            // Mordant : cutoff LP boosté au NoteOn, décroit exp vers 0 (τ = 20 ms). C'est ce boost
            // qui donne le "cheveu d'archet qui gratte" caractéristique du solo, sans injecter du
            // bruit blanc — la scie contient déjà tout ce qu'il faut, on la laisse juste passer
            // plus large brièvement.
            _biteBoost = 1f;
            _biteDecayCoef = (float)Math.Exp(-1.0 / (0.020 * _sr));

            // Bow noise continu : reset du HP state + reseed RNG determinist
            _scratchLpState = 0f;
            _scratchRng = new Random(note * 7919 + Environment.TickCount);

            float attackSec = Math.Max(0.015f, p.AttackSec);
            float releaseSec = Math.Max(0.02f, p.ReleaseSec);
            _envAttackRate  = 1f / (attackSec  * _sr);
            _envReleaseRate = 1f / (releaseSec * _sr);
            _env = 0f;
            _stage = EnvStage.Attack;

            _peakEnvelope = 1f;
            _active = true;
        }

        public void NoteOff()
        {
            if (_active && _stage != EnvStage.Release) _stage = EnvStage.Release;
        }

        public void Kill()
        {
            _active = false;
            _stage = EnvStage.Idle;
            _env = 0f;
            _peakEnvelope = 0f;
        }

        public float RenderSample(in EvParams p)
        {
            if (!_active) return 0f;

            // --- 1) Enveloppe A-S-R ---
            switch (_stage)
            {
                case EnvStage.Attack:
                    _env += _envAttackRate;
                    if (_env >= 1f) { _env = 1f; _stage = EnvStage.Sustain; }
                    break;
                case EnvStage.Release:
                    _env -= _envReleaseRate;
                    if (_env <= 0f) _env = 0f;
                    break;
            }

            // --- 2) Modulations pitch : bend d'accroche + vibrato ---
            _attackBendCents *= _attackBendDecayCoef;
            if (Math.Abs(_attackBendCents) < 0.01f) _attackBendCents = 0f;

            float vibMod = 0f;
            if (_vibDelaySamplesRemaining > 0) _vibDelaySamplesRemaining--;
            else
            {
                _vibFadeIn += 1f / (0.15f * _sr);
                if (_vibFadeIn > 1f) _vibFadeIn = 1f;
                float vibInc = (float)(2 * Math.PI * p.VibratoRateHz / _sr);
                _vibPhase += vibInc;
                if (_vibPhase > 2 * Math.PI) _vibPhase -= (float)(2 * Math.PI);
                vibMod = (float)Math.Sin(_vibPhase) * p.VibratoDepthCents * _vibFadeIn;
            }

            double totalCents = _attackBendCents + vibMod;
            double effFreq = _baseFreq * Math.Pow(2.0, totalCents / 1200.0);
            double phaseInc = effFreq / _sr;

            // --- 3) Dent de scie PolyBLEP (anti-alias sur le wrap) ---
            float saw = (float)(2.0 * _phase - 1.0);
            if (_phase < phaseInc)
            {
                double t = _phase / phaseInc;
                saw -= (float)(t + t - t * t - 1.0);
            }
            else if (_phase > 1.0 - phaseInc)
            {
                double t = (_phase - 1.0) / phaseInc;
                saw -= (float)(t * t + t + t + 1.0);
            }
            _phase += phaseInc;
            if (_phase >= 1.0) _phase -= 1.0;

            // --- 4) LP 2-pôles dynamique (le CŒUR du "crémeux") ---
            // Base ≈ 2500 Hz, module par BowPosition (chevalet=brillant, sultasto=feutré) et boostée
            // au NoteOn par _biteBoost (mordant qui ouvre briève­ment jusqu'à +2500 Hz). Le vibrato
            // ajoute une modulation subtile (±80 Hz) qui donne l'impression que le son "s'ouvre"
            // quand la note monte — astuce crémeuse Gemini.
            _biteBoost *= _biteDecayCoef;
            float baseCut = 2500f + (0.25f - p.BowPosition) * 3000f;   // 1000..3250 Hz env.
            float bite    = _biteBoost * p.BowForce * 2500f;           // 0..2500 Hz add.
            float vibCut  = vibMod * 5f;                               // ±≈100 Hz max
            float cutoff  = baseCut + bite + vibCut;
            if (cutoff < 400f)  cutoff = 400f;
            if (cutoff > 7000f) cutoff = 7000f;
            SetBiquadLP(ref _lpMain, _sr, cutoff, 0.707f);
            float body = BiquadProcess(ref _lpMain, saw);

            // --- 4b) Bow grain : la note elle-meme devient granuleuse ---
            // Pas un bruit ajoute a cote (ce serait un "chhh" ou "grrr" separe), mais une
            // MODULATION du signal principal par un noise tres basse frequence + un peu de drive
            // tanh. Physique : les micro-perturbations stick-slip du crin sur la corde modulent
            // l'amplitude et introduisent des harmoniques → la note "vibre" avec du grain, comme
            // si le son etait rugueux dans sa nature meme.
            if (p.BowScratch > 0.001f)
            {
                float raw = (float)(_scratchRng.NextDouble() * 2 - 1);
                _scratchLpState += 0.015f * (raw - _scratchLpState);   // LP ~100 Hz (grain percussif)
                float grainMod = _scratchLpState * 6f;                 // amplifie ±1
                if (grainMod > 1f) grainMod = 1f;
                else if (grainMod < -1f) grainMod = -1f;

                // Modulation d'amplitude : jusqu'a ±70 % a BowScratch max
                float grainDepth = p.BowScratch * 0.7f;
                body *= 1f + grainMod * grainDepth;

                // Drive tanh modere : a BowScratch = 1, drive = 2.5 → saturation musicale
                // (grain mordant sans devenir carre/agressif)
                float drive = 1f + p.BowScratch * 1.5f;
                body = (float)Math.Tanh(body * drive) / drive * (1f + p.BowScratch * 0.3f);
            }

            // --- 5) Formants en PARALLÈLE (résonances fixes de caisse) ---
            // 3 BPF sommés au signal sec avec BodyIntensity. Poids décroissants avec la fréquence :
            // le formant grave (400 Hz) porte le corps, l'aigu (3.2 kHz) juste une pointe. Somme
            // additive avec normalisation légère pour rester à ±1.
            float f1 = BiquadProcess(ref _form1, body);
            float f2 = BiquadProcess(ref _form2, body);
            float f3 = BiquadProcess(ref _form3, body);
            float formantMix = f1 * 0.55f + f2 * 0.40f + f3 * 0.30f;
            float withBody = body + formantMix * p.BodyIntensity * 0.6f;
            withBody *= 1f / (1f + p.BodyIntensity * 0.35f);   // normalisation douce

            // --- 6) Warmth : tanh douce pour arrondir davantage (pas pour distordre) ---
            // Drive faible : à Warmth=1 le drive vaut 1.6, tanh(1.6·x)/1.4 reste quasi-linéaire
            // sur les petites amplitudes et compresse gentiment les crêtes. Aucune saturation
            // audible en tant que telle — juste un lissage.
            float driven = withBody * (1f + p.Warmth * 0.6f);
            float warmed = (float)Math.Tanh(driven) * (1f / (1f + p.Warmth * 0.4f));

            // --- 7) Application enveloppe + vélocité + makeup ---
            float outSignal = warmed * _env * _velocity * 0.85f;

            // Détection de silence (fin de release) — libère la voix pour la polyphonie.
            float absOut = Math.Abs(outSignal);
            _peakEnvelope = Math.Max(_peakEnvelope * 0.9998f, absOut);
            if (_stage == EnvStage.Release && _env <= 0f && _peakEnvelope < SilenceThreshold)
            {
                _active = false;
                _stage = EnvStage.Idle;
                return 0f;
            }

            return outSignal;
        }

        // ================== Biquad helpers (RBJ cookbook) ==================
        internal struct BiquadState
        {
            public float b0, b1, b2, a1, a2;
            public float x1, x2, y1, y2;
            public void ResetState() { x1 = x2 = y1 = y2 = 0f; }
        }

        static void SetBiquadLP(ref BiquadState s, int sr, float freq, float q)
        {
            if (freq < 20f) freq = 20f;
            if (freq > sr * 0.45f) freq = sr * 0.45f;
            double w0 = 2.0 * Math.PI * freq / sr;
            double alpha = Math.Sin(w0) / (2.0 * q);
            double cosw0 = Math.Cos(w0);
            double a0 = 1.0 + alpha;
            s.b0 = (float)((1.0 - cosw0) / 2.0 / a0);
            s.b1 = (float)((1.0 - cosw0) / a0);
            s.b2 = s.b0;
            s.a1 = (float)(-2.0 * cosw0 / a0);
            s.a2 = (float)((1.0 - alpha) / a0);
        }

        static void SetBiquadBPF(ref BiquadState s, int sr, float freq, float q)
        {
            if (freq < 20f) freq = 20f;
            if (freq > sr * 0.45f) freq = sr * 0.45f;
            double w0 = 2.0 * Math.PI * freq / sr;
            double alpha = Math.Sin(w0) / (2.0 * q);
            double cosw0 = Math.Cos(w0);
            double a0 = 1.0 + alpha;
            // BPF "constant skirt gain" (peak gain = Q) — donne un caractère formant plus marqué
            // que la variante "constant 0 dB peak", ce qui rend les résonances de caisse audibles.
            s.b0 = (float)(alpha / a0);
            s.b1 = 0f;
            s.b2 = (float)(-alpha / a0);
            s.a1 = (float)(-2.0 * cosw0 / a0);
            s.a2 = (float)((1.0 - alpha) / a0);
        }

        static float BiquadProcess(ref BiquadState s, float x)
        {
            float y = s.b0 * x + s.b1 * s.x1 + s.b2 * s.x2 - s.a1 * s.y1 - s.a2 * s.y2;
            s.x2 = s.x1; s.x1 = x;
            s.y2 = s.y1; s.y1 = y;
            return y;
        }
    }
}
