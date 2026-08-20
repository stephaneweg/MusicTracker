using System;

namespace KotonPluginWaveMorph
{
    /// <summary>
    /// Une voix polyphonique du Wave Morph. Contient tout l'état qui doit être PAR NOTE :
    /// - 2 accumulateurs de phase (un par oscillateur)
    /// - 3 enveloppes ADSR (Amp / Env 2 / Env 3)
    /// - 2 LFO (redémarrés au NoteOn)
    /// - 2 filtres biquad, chacun potentiellement doublé pour la pente 24 dB/oct (donc 4 biquads)
    /// - la fréquence cible (avec glide entre notes en mode Mono)
    ///
    /// La chaîne DSP par sample :
    /// <code>
    ///   osc1  ─┐                    ┌── F1 ── F2 ─┐     serial
    ///          ├── xfade ── amp ── amp_env ──     ├── vol/pan ── out
    ///   osc2  ─┘                    └── F1 ┬ F2 ─┘     parallel (F1+F2)/2
    /// </code>
    ///
    /// **Mod matrix par sample** : les 8 sources sont calculées à chaque sample (Env2/3, LFO1/2 =
    /// dynamiques ; Velocity/Note/Aftertouch/ModWheel = statiques par voix). Chaque cible interroge
    /// la matrice via <see cref="ModMatrix.Evaluate"/> et applique la modulation dans son unité
    /// propre (dB, cents, octaves...) — mapping fait ICI (pas dans la matrice) parce que c'est
    /// spécifique au routage DSP.
    ///
    /// **Filtre 24 dB/oct** : deux instances de <see cref="BiquadFilter"/> chaînées produisent une
    /// pente double (12+12=24) — coût CPU 2× vs 12 dB, mais reste léger.
    ///
    /// **Politique de crash** : la voix ne jette jamais. Un paramètre absurde donne une sortie
    /// silencieuse (les biquads clampent, les envelopes forcent 0 en cas de valeurs négatives).
    /// </summary>
    internal sealed class WaveMorphVoice
    {
        // ---- État déclenchement ----
        public bool Active;
        public int Note;
        public float TargetFreq;   // Hz cible (avant glide)
        public float CurrentFreq;  // Hz courant (mis à jour par glide sample par sample)
        public float Velocity;     // 0..1
        public float Aftertouch;   // 0..1 (mis à jour par MidiCC via le plugin)
        public float ModWheel;     // 0..1 (idem)

        // ---- Accumulateurs de phase (0..2π), un par oscillateur ----
        double _phase1;
        double _phase2;

        // ---- Enveloppes ADSR ----
        readonly Envelope _envAmp;
        readonly Envelope _env2;
        readonly Envelope _env3;

        // ---- LFOs (par-voix pour un retrigger sur NoteOn) ----
        readonly Lfo _lfo1;
        readonly Lfo _lfo2;

        // ---- Filtres : 2 étages par filtre pour supporter 24 dB/oct (2e étage bypassé en 12 dB) ----
        readonly BiquadFilter _f1a, _f1b;
        readonly BiquadFilter _f2a, _f2b;

        readonly int _sampleRate;

        // Tableau des sources de modulation, réutilisé par sample. Indexé par (int)ModSource.
        readonly float[] _srcValues = new float[ModMatrix.SourceCount];

        public WaveMorphVoice(int sampleRate)
        {
            _sampleRate = sampleRate > 0 ? sampleRate : 44100;
            _envAmp = new Envelope(_sampleRate);
            _env2 = new Envelope(_sampleRate);
            _env3 = new Envelope(_sampleRate);
            // Seeds distincts pour que les 2 LFOs en mode Random ne soient pas synchronisés.
            _lfo1 = new Lfo(_sampleRate, 0x1F1F);
            _lfo2 = new Lfo(_sampleRate, 0xB6B6);
            _f1a = new BiquadFilter(_sampleRate);
            _f1b = new BiquadFilter(_sampleRate);
            _f2a = new BiquadFilter(_sampleRate);
            _f2b = new BiquadFilter(_sampleRate);
        }

        public void NoteOn(int note, int velocity, bool glideActive, float lastFreq)
        {
            Note = note;
            TargetFreq = (float)(440.0 * Math.Pow(2, (note - 69) / 12.0));
            // Mode glide (mono) : partir de l'ancienne fréquence pour un portamento continu.
            // Mode poly ou première note : sauter direct à la cible (pas de glissando).
            CurrentFreq = glideActive && lastFreq > 0 ? lastFreq : TargetFreq;
            Velocity = Math.Max(1, Math.Min(127, velocity)) / 127f;
            Active = true;

            _envAmp.NoteOn();
            _env2.NoteOn();
            _env3.NoteOn();
            _lfo1.Retrigger();
            _lfo2.Retrigger();

            // On NE reset PAS les biquads : leur état n'est qu'un buffer de 4 samples, garder ce qui
            // était là évite un clic à la note suivante (le filtre "commence" avec un signal cohérent
            // avec le sample précédent). Un reset explicite se fait au Reset() global du plugin.

            // Les phases des oscillateurs ne sont pas réinitialisées non plus — même argument, deux
            // notes qui se croisent gardent des phases désynchronisées (plus naturel qu'un phase-lock).
        }

        public void NoteOff()
        {
            _envAmp.NoteOff();
            _env2.NoteOff();
            _env3.NoteOff();
        }

        public void Reset()
        {
            Active = false;
            _envAmp.Reset();
            _env2.Reset();
            _env3.Reset();
            _f1a.Reset(); _f1b.Reset();
            _f2a.Reset(); _f2b.Reset();
            _phase1 = _phase2 = 0;
        }

        /// <summary>Rend un sample stéréo. Écrit les 2 canaux dans les paramètres out. Retourne
        /// false quand la voix est passée en Idle après ce sample (l'appelant peut décider de la
        /// libérer). Tous les paramètres sont passés en argument pour que le plugin puisse snapshot
        /// une seule fois par buffer et appeler la voix dans une boucle serrée.</summary>
        public void RenderSample(WaveMorphParams p, ModMatrix matrix, out float left, out float right)
        {
            if (!Active) { left = right = 0f; return; }

            // ---- Glide : approcher CurrentFreq de TargetFreq en glideMs ----
            if (p.GlideMs > 0.001f && CurrentFreq != TargetFreq)
            {
                // Approche exponentielle : approach = 1 - exp(-dt / tau). Approximation linéaire
                // ici : chaque sample déplace CurrentFreq d'une fraction fixe de la distance
                // restante. Le facteur dépend de glideMs (temps pour couvrir la moitié de la
                // distance ≈ ln(2)*glideMs/1000 en secondes → coefficient par sample).
                float tau = p.GlideMs * 0.001f;   // secondes
                float alpha = 1f - (float)Math.Exp(-1.0 / (_sampleRate * tau));
                CurrentFreq += (TargetFreq - CurrentFreq) * alpha;
                if (Math.Abs(CurrentFreq - TargetFreq) < 0.01f) CurrentFreq = TargetFreq;
            }
            else
            {
                CurrentFreq = TargetFreq;
            }

            // ---- Avance des enveloppes et LFOs ----
            float envAmpLvl = _envAmp.Advance(p.AmpAttackSec, p.AmpDecaySec, p.AmpSustain, p.AmpReleaseSec);
            float env2Lvl   = _env2.Advance(p.E2AttackSec, p.E2DecaySec, p.E2Sustain, p.E2ReleaseSec);
            float env3Lvl   = _env3.Advance(p.E3AttackSec, p.E3DecaySec, p.E3Sustain, p.E3ReleaseSec);
            float lfo1Val   = _lfo1.Advance(p.Lfo1Shape, p.Lfo1RateHz) * p.Lfo1Amount;
            float lfo2Val   = _lfo2.Advance(p.Lfo2Shape, p.Lfo2RateHz) * p.Lfo2Amount;

            // ---- Peupler les sources de modulation ----
            _srcValues[(int)ModSource.Env2] = env2Lvl;
            _srcValues[(int)ModSource.Env3] = env3Lvl;
            _srcValues[(int)ModSource.Lfo1] = lfo1Val;
            _srcValues[(int)ModSource.Lfo2] = lfo2Val;
            _srcValues[(int)ModSource.Velocity] = Velocity;
            _srcValues[(int)ModSource.Note] = (Note - 60) / 60f;   // normalisé -1..~+1 (C0..C10)
            _srcValues[(int)ModSource.Aftertouch] = Aftertouch;
            _srcValues[(int)ModSource.ModWheel] = ModWheel;

            // ---- Modulations par cible : chaque mod est appliquée dans l'unité DSP appropriée ----
            float xfadeMod    = matrix.Evaluate(ModTarget.XFade, _srcValues);      // ajouté direct (0..1)
            float w1AmpModDb  = matrix.Evaluate(ModTarget.W1Amp, _srcValues) * 24f; // ±24 dB
            float w2AmpModDb  = matrix.Evaluate(ModTarget.W2Amp, _srcValues) * 24f;
            float w1DetMod    = matrix.Evaluate(ModTarget.W1Detune, _srcValues) * 100f; // ±100 cents
            float w2DetMod    = matrix.Evaluate(ModTarget.W2Detune, _srcValues) * 100f;
            float f1FreqOct   = matrix.Evaluate(ModTarget.F1Freq, _srcValues) * 4f;   // ±4 octaves
            float f1ResMod    = matrix.Evaluate(ModTarget.F1Res, _srcValues);         // ±1 sur 0..1
            float f2FreqOct   = matrix.Evaluate(ModTarget.F2Freq, _srcValues) * 4f;
            float f2ResMod    = matrix.Evaluate(ModTarget.F2Res, _srcValues);
            float ampModDb    = matrix.Evaluate(ModTarget.Amp, _srcValues) * 24f;
            float panMod      = matrix.Evaluate(ModTarget.Pan, _srcValues);           // ±1 sur -1..+1

            // ---- Fréquences des oscillateurs ----
            // détune cents → ratio de fréquence : 2^(cents/1200)
            double d1 = (p.W1DetuneCents + w1DetMod) / 1200.0;
            double d2 = (p.W2DetuneCents + w2DetMod) / 1200.0;
            double freq1 = CurrentFreq * p.W1Mult * Math.Pow(2, d1) * p.BendMul;
            double freq2 = CurrentFreq * p.W2Mult * Math.Pow(2, d2) * p.BendMul;

            _phase1 += 2 * Math.PI * freq1 / _sampleRate;
            _phase2 += 2 * Math.PI * freq2 / _sampleRate;
            if (_phase1 > 2 * Math.PI) _phase1 -= 2 * Math.PI;
            if (_phase2 > 2 * Math.PI) _phase2 -= 2 * Math.PI;

            float w1 = WaveOsc.Sample(p.W1Wave, _phase1);
            float w2 = WaveOsc.Sample(p.W2Wave, _phase2);

            // ---- Amps des oscillateurs (dB + mod → linéaire) ----
            float w1AmpLin = DbToLin(p.W1AmpDb + w1AmpModDb);
            float w2AmpLin = DbToLin(p.W2AmpDb + w2AmpModDb);
            float a = w1 * w1AmpLin;
            float b = w2 * w2AmpLin;

            // ---- X-Fade linéaire : s = a + xf * (b - a) (le brief précise : PAS equal-power) ----
            float xf = p.XFade + xfadeMod;
            if (xf < 0f) xf = 0f; else if (xf > 1f) xf = 1f;
            float morphed = a + xf * (b - a);

            // ---- Amp env applied ----
            float sampled = morphed * envAmpLvl;

            // ---- Filtres (chacun avec sa fréquence modulée en octaves, resonance clampée) ----
            float f1Freq = (float)(p.F1Cutoff * Math.Pow(2, f1FreqOct));
            float f1Q    = Clamp01(p.F1Res + f1ResMod) * 9.9f + 0.1f;   // map 0..1 -> ~0.1..10
            float f2Freq = (float)(p.F2Cutoff * Math.Pow(2, f2FreqOct));
            float f2Q    = Clamp01(p.F2Res + f2ResMod) * 9.9f + 0.1f;

            _f1a.UpdateCoefs(p.F1Type, f1Freq, f1Q);
            _f2a.UpdateCoefs(p.F2Type, f2Freq, f2Q);
            if (p.F1Slope24) _f1b.UpdateCoefs(p.F1Type, f1Freq, f1Q);
            if (p.F2Slope24) _f2b.UpdateCoefs(p.F2Type, f2Freq, f2Q);

            float dry = sampled;

            // Drive en entrée de chaque filtre, appliqué symétriquement pour préserver le zero-crossing.
            float f1In = SoftClip(dry * DbToLin(p.F1DriveDb));
            float f2In = SoftClip(dry * DbToLin(p.F2DriveDb));

            float f1Wet = _f1a.Process(f1In);
            if (p.F1Slope24) f1Wet = _f1b.Process(f1Wet);
            float f2Wet = _f2a.Process(f2In);
            if (p.F2Slope24) f2Wet = _f2b.Process(f2Wet);

            // Mix dry/wet PAR FILTRE — permet à un utilisateur de garder une base non-filtrée +
            // ajouter un filtre pour la couleur.
            float f1Out = dry + (f1Wet - dry) * p.F1Mix;
            float f2Out = dry + (f2Wet - dry) * p.F2Mix;

            float filtered;
            if (p.ParallelRouting)
            {
                // Parallèle : les 2 filtres reçoivent le même signal, on somme (moyenné pour éviter
                // le doublement d'amplitude).
                filtered = 0.5f * (f1Out + f2Out);
            }
            else
            {
                // Série : f1Out entre dans f2 (le mix de f1 s'applique avant f2, plus prévisible).
                float f2SerialWet = _f2a.Process(SoftClip(f1Out * DbToLin(p.F2DriveDb)));
                if (p.F2Slope24) f2SerialWet = _f2b.Process(f2SerialWet);
                filtered = f1Out + (f2SerialWet - f1Out) * p.F2Mix;
            }

            // ---- Volume + pan (Amp env déjà appliqué avant filtres, ampModDb = tremolo post-filtre) ----
            float outLin = filtered * DbToLin(p.OutVolumeDb + ampModDb);

            // Pan : -1 = full L, +1 = full R. Loi équi-puissance simple (cos/sin) pour un panning
            // "audio-correct" (constante d'énergie perçue).
            float panFinal = p.OutPan + panMod;
            if (panFinal < -1f) panFinal = -1f; else if (panFinal > 1f) panFinal = 1f;
            float t = (panFinal + 1f) * 0.5f;   // 0..1
            float lGain = (float)Math.Cos(t * Math.PI * 0.5);
            float rGain = (float)Math.Sin(t * Math.PI * 0.5);

            left = outLin * lGain * Velocity;
            right = outLin * rGain * Velocity;

            // ---- Kill silencieux quand l'Amp env est retombée à 0 ----
            if (!_envAmp.IsActive) Active = false;
        }

        static float DbToLin(float db) => (float)Math.Pow(10.0, db / 20.0);

        static float Clamp01(float v) => v < 0 ? 0 : (v > 1 ? 1 : v);

        // tanh soft-clip normalisé — préserve le signal en dessous de ±0.7 et sature doux au-delà.
        // Utilisé comme "drive" de filtre.
        static float SoftClip(float x)
        {
            // tanh est cher ; approximation rationnelle 3x plus rapide et visuellement identique.
            float a = x * x;
            return x * (27f + a) / (27f + 9f * a);
        }
    }
}
