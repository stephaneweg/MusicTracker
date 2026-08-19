using System;

namespace KotonPluginErhu
{
    internal sealed class ErhuVoice
    {
        readonly int _sr;
        const int DlSize = 8192;
        readonly float[] _dl = new float[DlSize];
        int _writePos;

        // Delay length fractionnaire pour supporter le glide legato
        double _readOffsetSamples;
        double _targetOffsetSamples;
        double _glideStepPerSample;
        bool _gliding;

        float _lpState;
        float _feedback;
        float _brightCoef;

        // Sawtooth d'excitation (bowed = signal continu injecte dans la delay)
        double _sawPhase;

        // Formant BP
        BiquadState _formant;
        BiquadState _lpFinal;

        // Envelope ADSR
        enum Stage { Idle, Attack, Sustain, Release }
        Stage _stage;
        float _env, _atkR, _relR;

        // Vibrato
        double _vibPhase, _vibInc;

        // Bruit archet
        Random _rng;
        float _noiseState;

        int _note;
        float _velocity;
        bool _active;
        float _peak;
        const float Silence = 5e-5f;

        public bool IsActive => _active;
        public int Note => _note;

        public ErhuVoice(int sr) { _sr = sr; }

        double NoteToDelay(float m) { double f = 440.0 * Math.Pow(2.0, (m - 69) / 12.0); return Math.Max(2.0, _sr / f); }

        public void NoteOnBow(int note, float vel, in ErhuParams p)
        {
            _note = note; _velocity = vel;
            _readOffsetSamples = NoteToDelay(note);
            _targetOffsetSamples = _readOffsetSamples;
            _gliding = false;
            _rng = new Random(note * 7919 + Environment.TickCount);

            _feedback = 0.980f + p.BowPressure * 0.019f;   // sustain long
            _brightCoef = 0.15f + (1f - p.Brightness) * 0.55f;

            Array.Clear(_dl, 0, DlSize);
            _writePos = 0;
            _lpState = 0;
            _sawPhase = 0;

            SetBqBP(ref _formant, _sr, p.FormantHz, p.FormantQ);
            SetBqLP(ref _lpFinal, _sr, 5500f, 0.707f);

            _atkR = 1f / Math.Max(1, p.AttackMs * _sr / 1000f);
            _relR = 1f / Math.Max(1, p.ReleaseMs * _sr / 1000f);
            _env = 0f; _stage = Stage.Attack;
            _vibPhase = 0; _vibInc = 2.0 * Math.PI * p.VibRate / _sr;
            _peak = 1f; _active = true;
        }

        public void NoteOnLegato(int note, float vel, in ErhuParams p)
        {
            _note = note;
            _targetOffsetSamples = NoteToDelay(note);
            _feedback = 0.980f + p.BowPressure * 0.019f;
            _brightCoef = 0.15f + (1f - p.Brightness) * 0.55f;
            SetBqBP(ref _formant, _sr, p.FormantHz, p.FormantQ);
            if (p.GlideMs < 1f) { _readOffsetSamples = _targetOffsetSamples; _gliding = false; }
            else
            {
                double logDist = Math.Log(_targetOffsetSamples / _readOffsetSamples, 2);
                double glideSamples = p.GlideMs * _sr / 1000.0;
                _glideStepPerSample = logDist / glideSamples;
                _gliding = true;
            }
            _velocity = vel;
            if (_stage == Stage.Release)
            {
                _stage = Stage.Sustain;
                if (_env < vel) _env = vel;
            }
        }

        public void NoteOff() { if (_active && _stage != Stage.Release) _stage = Stage.Release; }
        public void Kill() { _active = false; _env = 0; _stage = Stage.Idle; _peak = 0; }

        public float RenderSample(in ErhuParams p)
        {
            if (!_active) return 0f;
            if (_stage == Stage.Attack) { _env += _atkR; if (_env >= 1f) { _env = 1f; _stage = Stage.Sustain; } }
            else if (_stage == Stage.Release) { _env -= _relR; if (_env <= 0f) _env = 0f; }

            if (_gliding)
            {
                double logCur = Math.Log(_readOffsetSamples, 2);
                logCur += _glideStepPerSample;
                _readOffsetSamples = Math.Pow(2, logCur);
                bool doneUp = _glideStepPerSample > 0 && _readOffsetSamples >= _targetOffsetSamples;
                bool doneDown = _glideStepPerSample < 0 && _readOffsetSamples <= _targetOffsetSamples;
                if (doneUp || doneDown) { _readOffsetSamples = _targetOffsetSamples; _gliding = false; }
            }

            double delayNow = _readOffsetSamples;
            if (p.VibDepthCents > 0.01f && p.VibRate > 0.01f)
            {
                _vibPhase += _vibInc;
                if (_vibPhase > 2 * Math.PI) _vibPhase -= 2 * Math.PI;
                double cents = Math.Sin(_vibPhase) * p.VibDepthCents;
                delayNow *= Math.Pow(2, -cents / 1200.0);
            }
            if (delayNow < 2.0) delayNow = 2.0;
            if (delayNow > DlSize - 4) delayNow = DlSize - 4;

            double readIdx = _writePos - delayNow;
            while (readIdx < 0) readIdx += DlSize;
            int rInt = (int)readIdx; double rFrac = readIdx - rInt;
            int r0 = rInt % DlSize; int r1 = (r0 + 1) % DlSize;
            float sample = (float)(_dl[r0] * (1 - rFrac) + _dl[r1] * rFrac);

            // LP dans le feedback
            _lpState = _lpState * _brightCoef + sample * (1f - _brightCoef);

            // Excitation continue par saw + bruit d'archet
            _sawPhase += 1.0 / _readOffsetSamples;
            if (_sawPhase >= 1) _sawPhase -= 1;
            float saw = (float)(2.0 * _sawPhase - 1.0);
            float bowNoise = (float)(_rng.NextDouble() * 2 - 1);
            _noiseState = _noiseState * 0.85f + bowNoise * 0.15f;
            float excite = (saw * 0.05f + _noiseState * p.BowNoise * 0.1f) * p.BowPressure * _env * _velocity;

            _dl[_writePos] = _lpState * _feedback + excite;
            _writePos = (_writePos + 1) % DlSize;

            // Formant BP mid nasal + LP final
            float formant = BiquadProcess(ref _formant, sample);
            float withFormant = sample + formant * 0.65f;
            float final = BiquadProcess(ref _lpFinal, withFormant);

            float outSig = final * _env * _velocity * 0.9f;
            _peak = Math.Max(_peak * 0.9998f, Math.Abs(outSig));
            if (_stage == Stage.Release && _env <= 0f && _peak < Silence) { _active = false; _stage = Stage.Idle; return 0f; }
            return outSig;
        }

        internal struct BiquadState { public float b0, b1, b2, a1, a2, x1, x2, y1, y2; }
        static void SetBqLP(ref BiquadState s, int sr, float freq, float q)
        {
            if (freq < 20f) freq = 20f; if (freq > sr * 0.45f) freq = sr * 0.45f;
            double w0 = 2.0 * Math.PI * freq / sr, alpha = Math.Sin(w0) / (2.0 * q), cosw0 = Math.Cos(w0), a0 = 1.0 + alpha;
            s.b0 = (float)((1.0 - cosw0) / 2.0 / a0); s.b1 = (float)((1.0 - cosw0) / a0); s.b2 = s.b0;
            s.a1 = (float)(-2.0 * cosw0 / a0); s.a2 = (float)((1.0 - alpha) / a0);
        }
        static void SetBqBP(ref BiquadState s, int sr, float freq, float q)
        {
            if (freq < 20f) freq = 20f; if (freq > sr * 0.45f) freq = sr * 0.45f;
            double w0 = 2.0 * Math.PI * freq / sr, alpha = Math.Sin(w0) / (2.0 * q), cosw0 = Math.Cos(w0), a0 = 1.0 + alpha;
            s.b0 = (float)(alpha / a0); s.b1 = 0; s.b2 = (float)(-alpha / a0);
            s.a1 = (float)(-2.0 * cosw0 / a0); s.a2 = (float)((1.0 - alpha) / a0);
        }
        static float BiquadProcess(ref BiquadState s, float x)
        {
            float y = s.b0 * x + s.b1 * s.x1 + s.b2 * s.x2 - s.a1 * s.y1 - s.a2 * s.y2;
            s.x2 = s.x1; s.x1 = x; s.y2 = s.y1; s.y1 = y;
            return y;
        }
    }
}
