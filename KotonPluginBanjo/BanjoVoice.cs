using System;

namespace KotonPluginBanjo
{
    internal sealed class BanjoVoice
    {
        readonly int _sr;
        const int DlSize = 4096;
        readonly float[] _dl = new float[DlSize];
        int _wp;
        int _len;
        float _lpState;
        float _feedback, _brightCoef;

        // Twang : peak biquad ~2.5kHz
        BiquadState _twangBq;
        float _twangMix;

        // Drum head : 2 modes (peau tambour)
        BiquadState _head1, _head2;
        float _headMix;

        enum Stage { Idle, Attack, Sustain, Release }
        Stage _stage;
        float _env, _atkR, _relR;

        int _note;
        float _velocity;
        bool _active;
        float _peak;
        const float Silence = 5e-5f;
        public bool IsActive => _active;
        public int Note => _note;

        public BanjoVoice(int sr) { _sr = sr; }

        public void NoteOn(int note, float vel, in BanjoParams p)
        {
            _note = note; _velocity = vel;
            double f = 440.0 * Math.Pow(2.0, (note - 69) / 12.0);
            _len = Math.Max(2, (int)Math.Round(_sr / f));
            if (_len > DlSize - 4) _len = DlSize - 4;

            _feedback = 0.90f + p.Sustain * 0.06f;   // banjo sustain court : max 0.96
            _brightCoef = 0.02f + (1f - p.Brightness) * 0.50f;
            _twangMix = p.Twang;
            _headMix = p.DrumHead;

            SetBqPeaking(ref _twangBq, _sr, 2500f, 3f, 4f);
            SetBqPeaking(ref _head1, _sr, 90f, 2f, 5f);
            SetBqPeaking(ref _head2, _sr, 250f, 3f, 3f);

            // Excitation HP (bruit filtre par HP simple par derivation)
            int burst = Math.Max(2, (int)(p.PluckLenMs * _sr / 1000f));
            var rng = new Random(note * 7919 + Environment.TickCount);
            Array.Clear(_dl, 0, DlSize);
            float prev = 0;
            for (int i = 0; i < _len; i++)
            {
                float n = i < burst ? (float)(rng.NextDouble() * 2 - 1) : 0f;
                float hp = n - prev;   // derivation = HP simple
                prev = n;
                _dl[i] = hp * vel * 0.5f;
            }
            _wp = _len; _lpState = 0;

            _atkR = 1f / Math.Max(1, p.AttackMs * _sr / 1000f);
            _relR = 1f / Math.Max(1, p.ReleaseMs * _sr / 1000f);
            _env = 0f; _stage = Stage.Attack;
            _peak = 1f; _active = true;
        }
        public void NoteOff() { if (_active && _stage != Stage.Release) _stage = Stage.Release; }
        public void Kill() { _active = false; _env = 0; _stage = Stage.Idle; _peak = 0; }

        public float RenderSample(in BanjoParams p)
        {
            if (!_active) return 0f;
            if (_stage == Stage.Attack) { _env += _atkR; if (_env >= 1f) { _env = 1f; _stage = Stage.Sustain; } }
            else if (_stage == Stage.Release) { _env -= _relR; if (_env <= 0f) _env = 0f; }

            int r = (_wp - _len + DlSize) % DlSize;
            float s = _dl[r];
            _lpState = _lpState * _brightCoef + s * (1f - _brightCoef);
            _dl[_wp] = _lpState * _feedback;
            _wp = (_wp + 1) % DlSize;

            float twang = BiquadProcess(ref _twangBq, s) * _twangMix * 0.5f;
            float h1 = BiquadProcess(ref _head1, s);
            float h2 = BiquadProcess(ref _head2, s);
            float head = (h1 * 0.5f + h2 * 0.3f) * _headMix * 0.6f;

            float outSig = (s + twang + head) * _env * _velocity * 0.85f;
            _peak = Math.Max(_peak * 0.9998f, Math.Abs(outSig));
            if (_stage == Stage.Release && _env <= 0f && _peak < Silence) { _active = false; _stage = Stage.Idle; return 0f; }
            return outSig;
        }

        internal struct BiquadState { public float b0, b1, b2, a1, a2, x1, x2, y1, y2; }
        static void SetBqPeaking(ref BiquadState s, int sr, float freq, float q, float gainDb)
        {
            double A = Math.Pow(10.0, gainDb / 40.0);
            double w0 = 2.0 * Math.PI * freq / sr, alpha = Math.Sin(w0) / (2.0 * q), cosw0 = Math.Cos(w0), a0 = 1.0 + alpha / A;
            s.b0 = (float)((1.0 + alpha * A) / a0); s.b1 = (float)(-2.0 * cosw0 / a0); s.b2 = (float)((1.0 - alpha * A) / a0);
            s.a1 = (float)(-2.0 * cosw0 / a0); s.a2 = (float)((1.0 - alpha / A) / a0);
        }
        static float BiquadProcess(ref BiquadState s, float x)
        {
            float y = s.b0 * x + s.b1 * s.x1 + s.b2 * s.x2 - s.a1 * s.y1 - s.a2 * s.y2;
            s.x2 = s.x1; s.x1 = x; s.y2 = s.y1; s.y1 = y;
            return y;
        }
    }
}
