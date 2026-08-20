using System;

namespace KotonPluginWoodwind
{
    /// <summary>
    /// Guide d'onde numerique pour instruments A ANCHE (clarinette, sax alto/tenor, hautbois,
    /// basson, cor anglais). Formulation STK Cook Clarinet + IMPULSION D'ATTAQUE au NoteOn
    /// pour amorcer l'auto-oscillation (sans impulsion initiale, le systeme reste en equilibre
    /// stable = pas de son).
    ///
    /// Une SEULE ecriture par sample dans la delay line (le pointer doit avancer d'un sample
    /// par sample rendu, sinon frequence effective divisee par 2).
    /// </summary>
    internal sealed class ReedWaveguideVoice
    {
        readonly int _sr;
        readonly float[] _bore;
        readonly int _boreMask;
        int _writeIdx;

        float _delayLen;
        float _reflectSign;    // -0.95 clarinette, +0.95 sax/hautbois/basson/cor

        bool _active;
        int _note;
        float _velocity;

        float _breathEnv;
        float _attackRate, _releaseRate;
        bool _releasing;

        // Transient de pression au NoteOn : amorce l'auto-oscillation. Sans ca, le systeme reste
        // en equilibre stable (breath progressive + tube vide = pas d'accrochage anche).
        float _attackPulse;

        Random _rng;
        float _lpState;

        public bool IsActive => _active;
        public int Note => _note;

        public ReedWaveguideVoice(int sampleRate)
        {
            _sr = sampleRate;
            int size = 1;
            int need = Math.Max(sampleRate / 20, 4096);
            while (size < need) size <<= 1;
            _bore = new float[size];
            _boreMask = size - 1;
        }

        public void NoteOn(int note, float velocity, in WwParams p)
        {
            _note = note;
            _velocity = velocity;

            double f0 = 440.0 * Math.Pow(2.0, (note - 69) / 12.0);
            _delayLen = (float)(_sr / (2.0 * f0));

            int instr = p.InstrumentIdx;
            bool isClarinette = (instr == 1);
            _reflectSign = isClarinette ? -0.95f : +0.95f;

            Array.Clear(_bore, 0, _bore.Length);
            _writeIdx = 0;
            _lpState = 0f;
            _breathEnv = 0f;
            _releasing = false;

            // Impulsion initiale : magnitude 0.4-0.8 selon velocity, decay exp ~0.992/sample
            // (~250 samples pour tomber a 0.1 → 5-6 ms a 44.1 kHz, comme un "coup de langue")
            _attackPulse = 0.4f + velocity * 0.4f;

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
            _attackPulse = 0f;
        }

        // Reed table : rt = offset + slope*pDiff, clip ±1. Slope NEGATIVE : la reed ferme quand
        // la difference de pression grandit (comportement physique reel).
        static float ReedTable(float pDiff, float softness)
        {
            float offset = 0.70f + softness * 0.10f;
            float slope = -0.40f;
            float rt = offset + slope * pDiff;
            if (rt > 1f) return 1f;
            if (rt < -1f) return -1f;
            return rt;
        }

        float ReadDelay(float delaySamples)
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

            // Decroissance de l'impulsion d'attaque
            if (_attackPulse > 0f)
            {
                _attackPulse *= 0.992f;
                if (_attackPulse < 0.001f) _attackPulse = 0f;
            }

            // Pression totale : souffle continu + transient d'attaque + petit bruit
            float noise = (float)(_rng.NextDouble() * 2 - 1) * (p.BreathNoise * 0.05f + 0.01f);
            float userPressure = Math.Max(0.2f, p.AirPressure) * _breathEnv * _velocity;
            float pm = userPressure + _attackPulse + noise;

            // Lire onde retour + LP au pavillon (aigus s'echappent, graves reviennent)
            float waveFromBore = ReadDelay(_delayLen);
            float cutoff = 1500f + p.Brightness * 7000f;
            float alpha = 1f - (float)Math.Exp(-2.0 * Math.PI * cutoff / _sr);
            _lpState += alpha * (waveFromBore - _lpState);
            float reflected = _lpState * _reflectSign;

            // Table d'anche + injection tube
            float pDiff = pm - reflected;
            float rt = ReedTable(pDiff, p.ReedSoftness);
            float waveToBore = 0.5f * pm + rt * pDiff;

            // Ecriture unique
            _bore[_writeIdx] = waveToBore;
            _writeIdx = (_writeIdx + 1) & _boreMask;

            // Sortie audio : pression rayonnee au pavillon, x1.5 pour matcher volume additif
            return (waveToBore - reflected) * 1.5f;
        }
    }
}
