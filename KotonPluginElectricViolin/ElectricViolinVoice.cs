using System;

namespace KotonPluginElectricViolin
{
    internal struct EvParams
    {
        public float BowForce;
        public float BowVelocity;
        public float BowPosition;      // 0..0.5 → altère la couleur (via toneHz)
        public float Damping;
        public float VibratoRateHz;
        public float VibratoDepthCents;
        public float TremoloRateHz;
        public float TremoloDepth;
        public float BodyIntensity;    // 0..1 → mix des 3 formants caisse
        public float Warmth;           // 0..1 → saturation tanh piezo
        public float AttackSec;
        public float ReleaseSec;
        public float VolumeDb;
    }

    /// <summary>
    /// Electric Violin — refonte v3 (2026-08-03).
    ///
    /// Après 2 tentatives ratées (v1 MSW ad-hoc → bruit, v2 guide d'onde bidirectionnel + bowTable
    /// Friedlander → "souffle dans bouteille", analysé par librosa : F0 instable, HNR 0.10,
    /// spectrum 79% aigus) — je fork ici le code de KotonPluginBowedStrings qui SONNE proprement
    /// (F0 stable, harmoniques entiers), et j'ajoute la coloration Zeta par-dessus :
    ///
    /// 1. Guide d'onde 1 ligne (KS bowed classique, comme Bowed Strings)
    /// 2. Excitation par bruit blanc filtré modulé par bowPressure × envelope × velocity
    /// 3. LP moyen + LP variable dans la boucle (Damping, Tone via BowPosition)
    /// 4. **3 formants biquad EN PARALLÈLE** sur la sortie (résonances de caisse : 300/700/2500 Hz).
    ///    Parallèle = pas de feedback interne, spectre additif propre.
    /// 5. **Saturation tanh Warmth** en sortie (simule le pickup piezo légèrement crunchy).
    /// 6. Vibrato ample (~5 Hz, 15-25 cents typique).
    /// 7. Tremolo optionnel en sortie.
    ///
    /// Résultat attendu : son de violon avec F0 stable (comme Bowed Strings), harmoniques riches
    /// grâce à la friction du bruit filtré, mais coloré "Zeta jazz fusion" par les formants + piezo.
    /// </summary>
    internal sealed class ElectricViolinVoice
    {
        readonly int _sr;
        readonly float[] _buffer;
        int _writeIdx;
        int _size;

        float _lpPrev;
        float _tonePrev;
        float _bowNoisePrev;
        Random _rng;

        // Vibrato + tremolo
        float _vibPhase, _vibInc;
        float _tremPhase, _tremInc;

        // Body : 3 formants biquad EN PARALLÈLE (résonances caisse violon)
        BiquadState _f1, _f2, _f3;

        // Enveloppe
        enum EnvStage { Idle, Attack, Sustain, Release }
        EnvStage _stage = EnvStage.Idle;
        float _env, _envAttackRate, _envReleaseRate;

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
            _buffer = new float[Math.Max(sampleRate / 20, 4096)];
            // Formants classiques violon (Meyer 1978, mesures Stradivarius) :
            // F1 = 300 Hz Q=6 (grave chaud "chest"), F2 = 700 Hz Q=5 (corps),
            // F3 = 2500 Hz Q=4 (le "singing" caractéristique)
            SetBiquadBandpass(ref _f1, sampleRate, 300f, 6f);
            SetBiquadBandpass(ref _f2, sampleRate, 700f, 5f);
            SetBiquadBandpass(ref _f3, sampleRate, 2500f, 4f);
        }

        public void NoteOn(int note, float velocity, in EvParams p)
        {
            _note = note;
            _velocity = velocity;

            double freq = 440.0 * Math.Pow(2.0, (note - 69) / 12.0);
            _size = Math.Max(4, Math.Min(_buffer.Length, (int)Math.Round(_sr / freq)));

            Array.Clear(_buffer, 0, _size);
            _writeIdx = 0;
            _lpPrev = 0f;
            _tonePrev = 0f;
            _bowNoisePrev = 0f;
            _f1.ResetState(); _f2.ResetState(); _f3.ResetState();

            _rng = new Random(note * 7919 + Environment.TickCount);
            _vibPhase = (float)(_rng.NextDouble() * 2 * Math.PI);
            _vibInc = (float)(2 * Math.PI * p.VibratoRateHz / _sr);
            _tremPhase = 0f;
            _tremInc = (float)(2 * Math.PI * p.TremoloRateHz / _sr);

            float attackSamples = Math.Max(1f, p.AttackSec * _sr);
            _envAttackRate = 1f / attackSamples;
            float releaseSamples = Math.Max(1f, p.ReleaseSec * _sr);
            _envReleaseRate = 1f / releaseSamples;
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
            Array.Clear(_buffer, 0, _buffer.Length);
        }

        public float RenderSample(in EvParams p)
        {
            if (!_active) return 0f;

            // Enveloppe
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

            // Vibrato : longueur de délai modulée
            _vibPhase += _vibInc;
            if (_vibPhase > 2 * Math.PI) _vibPhase -= (float)(2 * Math.PI);
            float vibCents = (float)Math.Sin(_vibPhase) * p.VibratoDepthCents;
            float sizeVib = _size / (float)Math.Pow(2.0, vibCents / 1200.0);
            int sizeI = (int)sizeVib;
            if (sizeI < 4) sizeI = 4;
            if (sizeI > _size) sizeI = _size;

            // Excitation par archet : bruit blanc filtré modulé par bow pressure × env × velocity
            // BowVelocity contrôle la brillance du bruit (dur = bruit brut, doux = LP fort).
            // Fix 2026-08-03 : bruit reduit (0.5→0.18) + LP plus fort pour eliminer le "frottement
            // d'archet" audible rapporte par l'user. Le bruit reste juste assez pour amorcer et
            // colorer les harmoniques, sans etre le composant dominant.
            float noise = (float)(_rng.NextDouble() * 2.0 - 1.0);
            float alphaSmooth = 0.015f + (1f - p.BowVelocity) * 0.15f;   // LP tres agressif → bruit tres filtre
            _bowNoisePrev += alphaSmooth * (noise - _bowNoisePrev);
            float bowInj = _bowNoisePrev * p.BowForce * _env * _velocity * 0.18f;   // niveau divise par ~3

            // Lecture au bout de la ligne à retard
            int readIdx = _writeIdx - sizeI;
            while (readIdx < 0) readIdx += _size;
            float sample = _buffer[readIdx];

            // 1) LP moyen tilt classique KS
            float lp = 0.5f * (sample + _lpPrev);
            _lpPrev = sample;

            // 2) LP variable "tone" : BowPosition contrôle le cutoff.
            //    Bow position 0.05 (près chevalet) = très brillant (8kHz)
            //    Bow position 0.5 (sultasto, près touche) = feutré (500Hz)
            float toneHz = 500f + (0.5f - p.BowPosition) * 15000f;   // range 500..8000 Hz
            if (toneHz < 200f) toneHz = 200f;
            if (toneHz > 8000f) toneHz = 8000f;
            float toneCoef = 1f - (float)Math.Exp(-2.0 * Math.PI * toneHz / _sr);
            _tonePrev += toneCoef * (lp - _tonePrev);
            float toned = _tonePrev;

            // 3) Feedback atténué (comme Bowed Strings) — damping contrôle la couleur/brillance résiduelle
            float gBase = 0.996f - p.Damping * 0.045f;
            float gEff = (float)Math.Pow(gBase, sizeI / 1000.0);
            float outValue = toned * gEff + bowInj;

            _buffer[_writeIdx] = outValue;
            _writeIdx++;
            if (_writeIdx >= _size) _writeIdx = 0;

            // === POST-PROCESSING (coloration Zeta) ===
            // Signal source pour le body = sample tap (le signal "propre" qui sort de la ligne)
            float bodyIn = sample;

            // 3 formants EN PARALLÈLE (pas en série) — additifs sur le dry
            float f1Out = BiquadProcess(ref _f1, bodyIn);
            float f2Out = BiquadProcess(ref _f2, bodyIn);
            float f3Out = BiquadProcess(ref _f3, bodyIn);
            // Body mix : signal dry + formants (chacun avec son propre poids)
            float bodyOut = bodyIn + (f1Out * 0.6f + f2Out * 0.5f + f3Out * 0.4f) * p.BodyIntensity;
            // Normalisation approximative pour éviter explosion quand BodyIntensity haut
            bodyOut *= 1f / (1f + p.BodyIntensity * 0.8f);

            // Warmth : saturation tanh compensée (pickup piezo crunchy)
            float driven = bodyOut * (1f + p.Warmth * 2.5f);
            float saturated = (float)Math.Tanh(driven);

            // Tremolo LFO en sortie (subtil)
            _tremPhase += _tremInc;
            if (_tremPhase > 2 * Math.PI) _tremPhase -= (float)(2 * Math.PI);
            float trem = 1f - p.TremoloDepth * 0.4f * (1f - (float)Math.Cos(_tremPhase));

            float finalOut = saturated * trem;

            // Silence detection basée sur le tap brut (comme Bowed Strings)
            float absOut = Math.Abs(outValue);
            _peakEnvelope = Math.Max(_peakEnvelope * 0.9998f, absOut);
            if (_stage == EnvStage.Release && _env <= 0f && _peakEnvelope < SilenceThreshold)
            {
                _active = false;
                _stage = EnvStage.Idle;
                return 0f;
            }

            return finalOut;
        }

        // === Biquad bandpass RBJ ===
        internal struct BiquadState
        {
            public float b0, b1, b2, a1, a2;
            public float x1, x2, y1, y2;
            public void ResetState() { x1 = x2 = y1 = y2 = 0f; }
        }
        static void SetBiquadBandpass(ref BiquadState s, int sr, float freq, float q)
        {
            double w0 = 2.0 * Math.PI * freq / sr;
            double alpha = Math.Sin(w0) / (2.0 * q);
            double cosw0 = Math.Cos(w0);
            double a0 = 1.0 + alpha;
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
