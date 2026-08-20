using System;

namespace KotonPluginWoodwind
{
    internal sealed class ReedWaveguideVoice
    {
        readonly int _sr;
        readonly float[] _bore;
        readonly int _boreMask;
        int _writeIdx;

        float _delayLen;
        float _reflectSign;

        bool _active;
        int _note;
        float _velocity;

        float _breathEnv;
        float _attackRate, _releaseRate;
        bool _releasing;

        Random _rng;

        // --- Modélisation acoustique avancée ---
        // 1. Filtre du pavillon (Tonehole/Bell filter : LP + HP)
        float _bellLpState;
        float _bellHpState;

        // 2. Résonance mécanique de l'anche (2-pole filter pour simuler la masse/élasticité)
        float _reedState1, _reedState2;

        // 3. Formant du corps (Biquad Bandpass)
        float _fmB0, _fmA1, _fmA2;
        float _fmX1, _fmX2, _fmY1, _fmY2;

        // Fréquences de formant caractéristiques (Hz)
        static readonly float[] InstrumentFormants = {
            0000f, // 0: Flûte (non utilisé ici)
            1500f, // 1: Clarinette (creux caractéristique vers 1.5 kHz)
            1100f, // 2: Hautbois (formant très serré et nasal)
            0500f, // 3: Basson (formant grave très chaleureux)
            0900f, // 4: Sax Alto (corps en cuivre medium)
            0650f, // 5: Sax Ténor (chaleur plus basse)
            3000f, // 6: Piccolo
            0950f  // 7: Cor Anglais
        };

        public bool IsActive => _active;
        public int Note => _note;

        public ReedWaveguideVoice(int sampleRate)
        {
            _sr = sampleRate;
            int size = 1;
            int need = Math.Max(sampleRate / 10, 8192);
            while (size < need) size <<= 1;
            _bore = new float[size];
            _boreMask = size - 1;
        }

        public void NoteOn(int note, float velocity, in WwParams p)
        {
            _note = note;
            _velocity = velocity;

            double f0 = 440.0 * Math.Pow(2.0, (note - 69) / 12.0);
            int instr = Math.Max(0, Math.Min(InstrumentFormants.Length - 1, p.InstrumentIdx));
            bool isClarinette = (instr == 1);

            // Ajustement de longueur physique avec correction de bout (end-correction)
            if (isClarinette)
            {
                _delayLen = (float)(_sr / (4.0 * f0)) - 0.5f;
                _reflectSign = -0.96f;
            }
            else
            {
                _delayLen = (float)(_sr / (2.0 * f0)) - 0.5f;
                _reflectSign = +0.93f;
            }

            // Configuration du formant du corps (RBJ Bandpass)
            float formantFreq = InstrumentFormants[instr];
            if (formantFreq > 0)
            {
                float q = 2.0f + p.BoreSize * 2.0f;
                double w0 = 2.0 * Math.PI * formantFreq / _sr;
                double alpha = Math.Sin(w0) / (2.0 * q);
                double cosw0 = Math.Cos(w0);
                double a0 = 1.0 + alpha;
                _fmB0 = (float)(alpha / a0);
                _fmA1 = (float)(-2.0 * cosw0 / a0);
                _fmA2 = (float)((1.0 - alpha) / a0);
            }
            else
            {
                _fmB0 = 1f; _fmA1 = _fmA2 = 0f;
            }

            Array.Clear(_bore, 0, _bore.Length);
            _writeIdx = 0;
            _bellLpState = _bellHpState = 0f;
            _reedState1 = _reedState2 = 0f;
            _fmX1 = _fmX2 = _fmY1 = _fmY2 = 0f;

            _breathEnv = 0f;
            _releasing = false;

            _attackRate = 1f / Math.Max(1f, p.AttackSec * _sr);
            _releaseRate = 1f / Math.Max(1f, p.ReleaseSec * _sr);

            _rng = new Random(note * 7919 + Environment.TickCount);
            _active = true;
        }

        public void NoteOff() { _releasing = true; }

        public void Kill()
        {
            _active = false;
            _breathEnv = 0f;
            _releasing = false;
        }

        // Table d'anche avec saturation non-linéaire douce (Tangente hyperbolique)
        private static float ReedTableSoft(float pDiff, float softness)
        {
            float offset = 0.65f + softness * 0.15f;
            float slope = -0.75f - (1f - softness) * 0.25f;

            float raw = offset + slope * pDiff;
            // Sature en douceur pour éviter les bruits de hachage trop synthétiques
            return (float)Math.Tanh(raw);
        }

        private float ReadDelay(float delaySamples)
        {
            float readPos = _writeIdx - delaySamples;
            while (readPos < 0) readPos += _bore.Length;
            int i1 = (int)Math.Floor(readPos);
            float frac = readPos - i1;
            i1 &= _boreMask;
            int i2 = (i1 + 1) & _boreMask;
            return _bore[i1] + frac * (_bore[i2] - _bore[i1]);
        }

        public float RenderSample(in WwParams p)
        {
            if (!_active) return 0f;

            // 1. Enveloppe du souffle
            if (!_releasing)
            {
                _breathEnv += _attackRate;
                if (_breathEnv > 1f) _breathEnv = 1f;
            }
            else
            {
                _breathEnv -= _releaseRate;
                if (_breathEnv <= 0f) { _breathEnv = 0f; _active = false; return 0f; }
            }

            // Pression d'injection avec instabilités naturelles du souffle (bruit rose bas-médium)
            float breathNoise = (float)(_rng.NextDouble() * 2 - 1) * (p.BreathNoise * 0.06f + 0.008f);
            float pm = (Math.Max(0.15f, p.AirPressure) * _breathEnv * _velocity) + breathNoise;

            // 2. Onde provenant de la colonne d'air
            float waveFromBore = ReadDelay(_delayLen);

            // 3. Acoustique du Pavillon (Combinaison Passe-Bas + Passe-Haut)
            // Passe-bas (absorption de l'air/bois)
            float lpCutoff = 1800f + p.Brightness * 6000f;
            float alphaLp = 1f - (float)Math.Exp(-2.0 * Math.PI * lpCutoff / _sr);
            _bellLpState += alphaLp * (waveFromBore - _bellLpState);

            // Passe-haut (les fréquences sous la coupure du pavillon s'échappent moins)
            float hpCutoff = 150f + (1f - p.BoreSize) * 200f;
            float alphaHp = 1f - (float)Math.Exp(-2.0 * Math.PI * hpCutoff / _sr);
            _bellHpState += alphaHp * (_bellLpState - _bellHpState);

            // Onde réfléchie vers l'anche
            float reflected = (_bellLpState - _bellHpState) * _reflectSign;

            // 4. Modélisation de la dynamique de l'anche (Inertie mécanique)
            float pDiffTarget = reflected - pm;

            // L'anche agit comme un filtre passe-bas sur la variation de pression (inertie de la lamelle)
            float reedFreq = 2500f + (1f - p.ReedSoftness) * 3500f; // Résonance propre de l'anche
            float alphaReed = 1f - (float)Math.Exp(-2.0 * Math.PI * reedFreq / _sr);
            _reedState1 += alphaReed * (pDiffTarget - _reedState1);

            // 5. Injection de la nouvelle onde dans le tube
            float reedReflection = ReedTableSoft(_reedState1, p.ReedSoftness);
            float waveToBore = pm + _reedState1 * reedReflection;

            // Écriture unique dans la delay line
            _bore[_writeIdx] = waveToBore;
            _writeIdx = (_writeIdx + 1) & _boreMask;

            // 6. Rayonnement sonore extérieur = Onde transmise hors du tube
            float radiatedSignal = waveToBore - reflected;

            // 7. Infiltration des formants du corps de l'instrument
            if (_fmB0 != 1f)
            {
                float filtered = _fmB0 * (radiatedSignal - _fmX2) - _fmA1 * _fmY1 - _fmA2 * _fmY2;
                _fmX2 = _fmX1; _fmX1 = radiatedSignal;
                _fmY2 = _fmY1; _fmY1 = filtered;

                // Mix entre le signal pur du tube et la résonance du corps
                radiatedSignal = (radiatedSignal * 0.65f) + (filtered * 0.35f);
            }

            return radiatedSignal * 1.2f;
        }
    }
}