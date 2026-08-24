using System;

namespace KotonPluginWoodwind
{
    internal struct WwParams
    {
        public int InstrumentIdx;
        public float AirPressure;
        public float BreathNoise;
        public float ReedSoftness;
        public float ExcitationType;
        public float Damping;
        public float Brightness;
        public float BoreSize;
        public float VibratoRateHz;
        public float VibratoDepthCents;
        public float ChiffAmount;
        public float AttackSec;
        public float ReleaseSec;
        public float VolumeDb;
    }

    internal sealed class WoodwindVoice
    {
        readonly int _sr;

        const int NumHarmonics = 16; // Augmenté à 16 pour capturer le grain des anches
        readonly double[] _phase = new double[NumHarmonics];
        readonly double[] _phaseInc = new double[NumHarmonics];
        readonly float[] _ampTarget = new float[NumHarmonics];
        readonly float[] _ampNow = new float[NumHarmonics];

        float _vibPhase, _vibInc;
        float _chiffEnv, _chiffDecayRate;
        float _breathState;

        // Formant biquad bandpass (RBJ 0 dB peak), coefs PRECALCULES au NoteOn — le code precedent
        // recalculait Sin/Cos par sample dans RenderSample : catastrophe CPU + artefacts sur slides.
        // RBJ BP 0dB peak : b0 = alpha/a0, b1 = 0, b2 = -b0, a0 = 1+alpha, a1 = -2cos(w0)/a0, a2 = (1-alpha)/a0
        float _fmB0, _fmA1, _fmA2;
        float _fmX1, _fmX2, _fmY1, _fmY2;

        Random _rng;

        enum EnvStage { Idle, Attack, Sustain, Release }
        EnvStage _stage = EnvStage.Idle;
        float _env, _envAttackRate, _envReleaseRate;
        int _tongueDip;    // echantillons restants de la descente d'articulation (voir ReTongue)
        float _tongueFall; // pente de cette descente (filtre a un pole : pas d'angle, pas de clic)
        float _tongueDipTarget;

        bool _active;
        int _note;
        float _velocity;
        float _f0;

        const float SilenceThreshold = 1e-4f;

        public bool IsActive => _active;
        public int Note => _note;

        // Profiles spectraux ajustés (16 partiels)
        static readonly float[][] SpectrumByInstrument =
        {
            /* 0 Flute       */ new float[] { 1.00f, 0.20f, 0.05f, 0.02f, 0.01f, 0.00f, 0.00f, 0.00f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f },
            /* 1 Clarinette  */ new float[] { 1.00f, 0.03f, 0.85f, 0.04f, 0.65f, 0.03f, 0.45f, 0.02f, 0.25f, 0.02f, 0.15f, 0.01f, 0.08f, 0f, 0f, 0f }, // Impaires très dominantes
            /* 2 Hautbois    */ new float[] { 0.25f, 0.70f, 1.00f, 0.90f, 0.75f, 0.60f, 0.45f, 0.35f, 0.25f, 0.15f, 0.10f, 0.05f, 0.02f, 0f, 0f, 0f }, // Fondamentale faible, H3/H4 fortes (nasal)
            /* 3 Basson      */ new float[] { 0.40f, 1.00f, 0.90f, 0.65f, 0.45f, 0.30f, 0.20f, 0.15f, 0.10f, 0.05f, 0.02f, 0f, 0f, 0f, 0f, 0f },
            /* 4 Sax alto    */ new float[] { 0.80f, 1.00f, 0.75f, 0.60f, 0.50f, 0.40f, 0.30f, 0.20f, 0.15f, 0.10f, 0.05f, 0.03f, 0.01f, 0f, 0f, 0f }, // H2 dominante, spectre riche
            /* 5 Sax tenor   */ new float[] { 1.00f, 0.90f, 0.80f, 0.55f, 0.40f, 0.30f, 0.20f, 0.15f, 0.10f, 0.05f, 0.02f, 0f, 0f, 0f, 0f, 0f },
            /* 6 Piccolo     */ new float[] { 1.00f, 0.50f, 0.20f, 0.10f, 0.04f, 0.01f, 0.00f, 0.00f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f },
            /* 7 Cor anglais */ new float[] { 0.60f, 0.80f, 0.90f, 0.65f, 0.45f, 0.30f, 0.20f, 0.12f, 0.08f, 0.04f, 0.01f, 0f, 0f, 0f, 0f, 0f },
        };

        // Formants caractéristiques fixes (Hz) pour injecter l'acoustique du corps
        static readonly float[] FormantFrequences = { 2200f, 1500f, 1100f, 500f, 900f, 650f, 3000f, 950f };

        public WoodwindVoice(int sampleRate) { _sr = sampleRate; }

        public void NoteOn(int note, float velocity, in WwParams p)
        {
            _note = note;
            _velocity = velocity;
            _f0 = (float)(440.0 * Math.Pow(2.0, (note - 69) / 12.0));

            _rng = new Random(note * 7919 + Environment.TickCount);

            int instr = Math.Max(0, Math.Min(SpectrumByInstrument.Length - 1, p.InstrumentIdx));
            var spectrum = SpectrumByInstrument[instr];

            // Formant biquad bandpass RBJ 0 dB peak, coefs figes au NoteOn.
            float formantF = FormantFrequences[instr];
            float formantQ = 2.0f + p.BoreSize * 3.0f;
            double w0 = 2.0 * Math.PI * formantF / _sr;
            double alpha = Math.Sin(w0) / (2.0 * formantQ);
            double cosw0 = Math.Cos(w0);
            double a0 = 1.0 + alpha;
            _fmB0 = (float)(alpha / a0);     // b1 = 0, b2 = -b0 (bandpass 0 dB peak)
            _fmA1 = (float)(-2.0 * cosw0 / a0);
            _fmA2 = (float)((1.0 - alpha) / a0);

            float reedHardness = 1f - p.ReedSoftness;
            float nyquistLimit = _sr * 0.45f;

            for (int i = 0; i < NumHarmonics; i++)
            {
                int h = i + 1;
                double freq = _f0 * h;

                if (freq > nyquistLimit)
                {
                    _ampTarget[i] = 0f;
                    _phaseInc[i] = 0;
                    continue;
                }

                _phase[i] = _rng.NextDouble() * 2 * Math.PI;
                _phaseInc[i] = 2.0 * Math.PI * freq / _sr;

                float baseAmp = spectrum[i];

                // Effet non-linéaire : l'ouverture de l'anche enrichit les aiguës à forte vélocité
                if (h > 2)
                {
                    baseAmp *= (float)Math.Pow(velocity, 0.5f + (h * 0.1f));
                }

                // Ajustement par dureté de l'anche
                if (h >= 4) baseAmp *= (0.5f + reedHardness * 1.5f);
                baseAmp *= (0.6f + p.Brightness * 0.8f);

                _ampTarget[i] = baseAmp * velocity;
                _ampNow[i] = 0f;
            }

            _vibPhase = (float)(_rng.NextDouble() * 2 * Math.PI);
            _vibInc = (float)(2 * Math.PI * p.VibratoRateHz / _sr);

            // Souffle bref au depart de la note. Il est desormais DOSABLE et coupe par defaut : c'est
            // ce petit bruit d'attaque que l'utilisateur ne voulait pas entendre sur des croches jouees.
            float chiffTimeMs = 20f + p.ReedSoftness * 40f;
            _chiffEnv = (0.3f + velocity * 0.5f) * p.ChiffAmount;
            _chiffDecayRate = (float)Math.Exp(-6.907755 / (chiffTimeMs * _sr / 1000.0));

            _envAttackRate = 1f / Math.Max(1f, p.AttackSec * _sr);
            _envReleaseRate = 1f / Math.Max(1f, p.ReleaseSec * _sr);
            _env = 0f;
            _stage = EnvStage.Attack;
            _tongueDip = 0;

            _fmX1 = _fmX2 = _fmY1 = _fmY2 = 0f;
            _breathState = 0f;
            _active = true;
        }

        /// <summary>
        /// Coup de langue sur la note en cours : on relance l'enveloppe et le transitoire d'anche, SANS
        /// retoucher les partiels — leurs phases continuent de tourner, donc aucun clic et le timbre reste
        /// celui de l'instrument.
        ///
        /// Deux raisons de ne pas simplement rappeler <see cref="NoteOn"/> :
        /// l'attaque y est celle réglée par l'utilisateur (0,15 s par défaut, jusqu'à 1,5 s), ce qui fait
        /// s'effondrer le niveau dès qu'on dépasse quelques coups par seconde ; et elle retire aux
        /// partiels leur phase courante, ce qui claque. Un coup de langue est par nature bref : 12 ms fixes.
        ///
        /// Le transitoire vient d'ici, pas d'un bruit ajouté en sortie : c'est le <c>chiff</c> que
        /// l'instrument produit déjà à chaque attaque, donc exactement le grain d'une note réarticulée.
        /// </summary>
        public void ReTongue(float velocity, in WwParams p)
        {
            if (!_active) return;
            _velocity = velocity;
            _chiffEnv = (0.3f + velocity * 0.5f) * p.ChiffAmount;
            // L'enveloppe CREUSE sans jamais couper : elle descend en 5 ms vers un fond partiel, puis
            // remonte en 28 ms. Une fermeture a zero, meme breve, s'entend comme un « tac » et non comme
            // une note articulee — un detache reel creuse le son, il ne l'interrompt pas. Les deux pentes
            // sont douces pour la meme raison : c'est l'angle, pas le niveau, qui fait le clic.
            const float TongueDipLevel = 0.30f, TongueDipSec = 0.005f;
            _tongueDip = (int)(TongueDipSec * _sr);
            _tongueFall = 1f - (float)Math.Exp(-1.0 / Math.Max(1.0, TongueDipSec * _sr * 0.35));
            _tongueDipTarget = TongueDipLevel;
            const double TongueAttackSec = 0.028;
            _envAttackRate = 1f / Math.Max(1f, (float)TongueAttackSec * _sr);
            _stage = EnvStage.Attack;
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
            for (int i = 0; i < NumHarmonics; i++) { _ampNow[i] = 0f; _ampTarget[i] = 0f; }
            _fmX1 = _fmX2 = _fmY1 = _fmY2 = 0f;
        }

        public float RenderSample(in WwParams p)
        {
            if (!_active) return 0f;

            // Envelope ADSR
            if (_tongueDip > 0) { _tongueDip--; _env += (_tongueDipTarget - _env) * _tongueFall; }
            else if (_stage == EnvStage.Attack)
            {
                _env += _envAttackRate;
                if (_env >= 1f) { _env = 1f; _stage = EnvStage.Sustain; }
            }
            else if (_stage == EnvStage.Release)
            {
                _env -= _envReleaseRate;
                if (_env <= 0f) { _env = 0f; _active = false; return 0f; }
            }

            // Vibrato Pitch
            _vibPhase += _vibInc;
            if (_vibPhase > 2 * Math.PI) _vibPhase -= (float)(2 * Math.PI);
            float vibPitchMul = (float)Math.Pow(2.0, (Math.Sin(_vibPhase) * p.VibratoDepthCents) / 1200.0);

            float slew = 1f - (float)Math.Exp(-2.0 * Math.PI * 150f / _sr);
            float envMul = _env * p.AirPressure;

            // 1. Synthèse Additive
            float sum = 0f;
            for (int i = 0; i < NumHarmonics; i++)
            {
                if (_phaseInc[i] == 0) continue;

                sum += _ampNow[i] * (float)Math.Sin(_phase[i]);
                _phase[i] += _phaseInc[i] * vibPitchMul;
                if (_phase[i] > 2 * Math.PI) _phase[i] -= 2 * Math.PI;

                _ampNow[i] += slew * ((_ampTarget[i] * envMul) - _ampNow[i]);
            }

            // 2. Chiff Transient (bruit d'attaque)
            float chiff = 0f;
            if (_chiffEnv > 1e-4f)
            {
                float white = (float)(_rng.NextDouble() * 2 - 1);
                chiff = white * _chiffEnv * 0.25f;
                _chiffEnv *= _chiffDecayRate;
            }

            // 3. Bruit de souffle continu
            float breath = 0f;
            if (p.BreathNoise > 0.001f)
            {
                float white = (float)(_rng.NextDouble() * 2 - 1);
                float alpha = 1f - (float)Math.Exp(-2.0 * Math.PI * 3000f / _sr);
                _breathState += alpha * (white - _breathState);
                breath = _breathState * p.BreathNoise * envMul * 0.4f;
            }

            float rawSignal = sum + chiff + breath;

            // Formant biquad DF-I : y = b0*(x - x2) - a1*y1 - a2*y2  (b1=0, b2=-b0 en RBJ BP)
            float filtered = _fmB0 * (rawSignal - _fmX2) - _fmA1 * _fmY1 - _fmA2 * _fmY2;
            _fmX2 = _fmX1; _fmX1 = rawSignal;
            _fmY2 = _fmY1; _fmY1 = filtered;

            return (rawSignal * 0.6f) + (filtered * 0.4f);
        }
    }
}