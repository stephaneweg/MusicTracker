using System;

namespace KotonPluginElectricViolin
{
    internal struct EvParams
    {
        public float BowForce;         // 0..1 → force d'appui de l'archet
        public float BowVelocity;      // 0..1 → vitesse (mappée sur -0.3..0.3 dans le stick-slip)
        public float BowPosition;      // 0..0.5 → position sur la corde (0=milieu, chevalet approche)
        public float Damping;          // 0..1 → LP feedback (perte HF au chevalet)
        public float VibratoRateHz;
        public float VibratoDepthCents;
        public float TremoloRateHz;
        public float TremoloDepth;
        public float BodyIntensity;    // 0..1 → gain des 3 formants caisse
        public float Warmth;           // 0..1 → saturation tanh piezo en sortie
        public float AttackSec;
        public float ReleaseSec;
        public float VolumeDb;
    }

    /// <summary>
    /// Une voix de violon électrique — modélisation physique par guide d'onde Smith/McIntyre-
    /// Schumacher-Woodhouse (MSW 1983). Le vrai moteur du son violon = la non-linéarité stick-slip
    /// de la friction de l'archet sur la corde, qui produit naturellement une onde en dents de scie
    /// (spectre riche en harmoniques impaires + paires selon la position d'archet).
    ///
    /// **DSP** :
    /// 1. Guide d'onde = ligne à retard longueur N = SR/f, avec LP feedback au chevalet (perte HF)
    /// 2. Point d'archet à position pilotée par BowPosition. À chaque sample :
    ///    - Lit la vitesse de la corde à cet endroit
    ///    - Calcule la vitesse relative v_rel = bowVelocity - stringVel
    ///    - Applique la friction MSW : forte dans la zone stick (|v_rel| petit), décroissante
    ///      dans la zone slip (|v_rel| grand) → forme d'onde dents de scie
    ///    - Injecte cette friction × bowForce dans la boucle
    /// 3. Vibrato : modulation de la longueur de la ligne (frequence)
    /// 4. Sortie : 3 formants biquad bandpass série (caisse : 300 Hz, 700 Hz, 2500 Hz Q=5-8)
    /// 5. Saturation tanh (Warmth) : simule le pickup piezo légèrement crunchy
    /// </summary>
    internal sealed class ElectricViolinVoice
    {
        readonly int _sr;
        readonly float[] _string;
        int _writeIdx;
        int _size;

        float _lpState;
        float _vibPhase, _vibInc;
        float _tremPhase, _tremInc;

        // Formants biquad série (résonances de la caisse)
        BiquadState _f1, _f2, _f3;

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
            _string = new float[Math.Max(sampleRate / 20, 4096)];
            // Formants classiques du violon : 300 Hz Q=8 (grave chaud), 700 Hz Q=6 (corps),
            // 2500 Hz Q=5 (le "singing" caractéristique)
            SetBiquadBandpass(ref _f1, sampleRate, 300f, 8f);
            SetBiquadBandpass(ref _f2, sampleRate, 700f, 6f);
            SetBiquadBandpass(ref _f3, sampleRate, 2500f, 5f);
        }

        public void NoteOn(int note, float velocity, in EvParams p)
        {
            _note = note;
            _velocity = velocity;

            double freq = 440.0 * Math.Pow(2.0, (note - 69) / 12.0);
            _size = Math.Max(4, Math.Min(_string.Length, (int)Math.Round(_sr / freq)));

            Array.Clear(_string, 0, _size);
            _writeIdx = 0;
            _lpState = 0f;
            _f1.ResetState(); _f2.ResetState(); _f3.ResetState();

            _vibPhase = 0f;
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
            Array.Clear(_string, 0, _string.Length);
        }

        public float RenderSample(in EvParams p)
        {
            if (!_active) return 0f;

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
            int sizeI = Math.Max(4, Math.Min(_size, (int)sizeVib));

            // Lecture au bout de la corde (chevalet)
            int readIdx = _writeIdx - sizeI;
            while (readIdx < 0) readIdx += _size;
            float stringVel = _string[readIdx];

            // LP feedback (perte HF au chevalet — Damping)
            float lpCutoff = 1500f + (1f - p.Damping) * 6000f;
            float lpAlpha = 1f - (float)Math.Exp(-2.0 * Math.PI * lpCutoff / _sr);
            _lpState += lpAlpha * (stringVel - _lpState);
            float returnVel = -0.995f * _lpState;   // réflexion négative au chevalet

            // ARCHET : vitesse effective = bowVelocity × envelope × velocity
            float bowVel = p.BowVelocity * 0.3f * _env * _velocity;   // scaled to -0.3..0.3
            float bowForce = p.BowForce * _env * _velocity;

            // MSW friction stick-slip :
            //   v_rel = bowVel - stringVel_at_bow
            //   stringVel_at_bow ≈ (readVel + writeVel) / 2 ≈ stringVel (approximation valide car bow proche du chevalet)
            //   Zone STICK : |v_rel| < threshold → friction linéaire forte (la corde suit l'archet)
            //   Zone SLIP  : |v_rel| >= threshold → friction décroissante (la corde glisse en arrière)
            float vRel = bowVel - returnVel;
            float aRel = Math.Abs(vRel);
            float friction;
            const float stickThreshold = 0.08f;
            if (aRel < stickThreshold)
            {
                // Stick : friction ~ v_rel × μ_static (forte)
                friction = vRel * 5f;
            }
            else
            {
                // Slip : friction décroit avec |v_rel| (μ_kinetic × exp-like)
                friction = Math.Sign(vRel) * (0.4f / (1f + aRel * 3f));
            }
            // Force appliquée à la corde = friction × bowForce
            float excitation = friction * bowForce * 0.6f;

            // BowPosition : filtre comb (accentue certains harmoniques selon position)
            // 0 = milieu de la corde (fondamentale forte, harmoniques paires atténuées)
            // 0.5 = près du chevalet (harmoniques hautes accentuées)
            // Approximation simple : filtre comb à sizeI × bowPosition
            int combOffset = (int)(sizeI * (0.05f + p.BowPosition * 0.4f));
            if (combOffset > 0 && combOffset < sizeI)
            {
                int combIdx = _writeIdx - combOffset;
                while (combIdx < 0) combIdx += _size;
                excitation -= _string[combIdx] * 0.3f;
            }

            // Écriture : excitation + réflexion (guide d'onde bidirectionnel simplifié)
            _string[_writeIdx] = excitation + returnVel * 0.5f;
            _writeIdx++;
            if (_writeIdx >= _size) _writeIdx = 0;

            // SORTIE : signal brut de la corde, passé par les 3 formants série + saturation piezo
            float bodyIn = stringVel;

            // Formants biquad série avec gain contrôlable par BodyIntensity
            float f1Out = BiquadProcess(ref _f1, bodyIn);
            float f2Out = BiquadProcess(ref _f2, f1Out);
            float f3Out = BiquadProcess(ref _f3, f2Out);
            // Mix dry + formants selon BodyIntensity
            float body = bodyIn * (1f - p.BodyIntensity * 0.6f) + (f1Out * 0.4f + f2Out * 0.3f + f3Out * 0.3f) * p.BodyIntensity * 2f;

            // Saturation piezo (Warmth) : tanh doux
            float driven = body * (1f + p.Warmth * 3f);
            float saturated = (float)Math.Tanh(driven) / (1f + p.Warmth * 2f);   // compense le gain

            // Tremolo LFO en sortie (subtil)
            _tremPhase += _tremInc;
            if (_tremPhase > 2 * Math.PI) _tremPhase -= (float)(2 * Math.PI);
            float trem = 1f - p.TremoloDepth * 0.5f * (1f - (float)Math.Cos(_tremPhase));
            float outSignal = saturated * trem;

            // Détection d'énergie
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
