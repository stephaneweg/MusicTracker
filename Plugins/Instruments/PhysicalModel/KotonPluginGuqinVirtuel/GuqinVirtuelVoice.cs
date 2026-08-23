using System;

namespace KotonPluginGuqinVirtuel
{
    /// <summary>
    /// Voix Karplus-Strong pour Guqin virtuel — copie de GuqinVoice (KotonPluginGuqin) : chaque
    /// plugin étant un .ksl autonome, on duplique le DSP plutôt que d'introduire une référence
    /// croisée. Comportement identique : delay-line fractionnaire, feedback + LP, envelope ADSR,
    /// glide optionnel entre notes en legato, vibrato optionnel.
    /// </summary>
    internal sealed class GuqinVirtuelVoice
    {
        readonly int _sr;

        const int DlSize = 8192;
        readonly float[] _dl = new float[DlSize];
        int _writePos;
        double _readOffsetSamples;
        double _targetOffsetSamples;
        double _glideStepPerSample;
        bool _gliding;

        float _lpState;
        float _feedbackGain;
        float _dampingCoef;

        enum EnvStage { Idle, Attack, Sustain, Release }
        EnvStage _stage;
        float _env, _envAtkRate, _envRelRate;

        double _vibPhase;
        double _vibInc;

        int _note;
        float _velocity;
        bool _active;

        BiquadState _body;
        float _bodyMix;

        public bool IsActive => _active;
        public int Note => _note;

        const float SilenceThreshold = 5e-5f;
        float _peak;

        public GuqinVirtuelVoice(int sr) { _sr = sr; }

        double NoteToDelay(float noteMidi)
        {
            double freq = 440.0 * Math.Pow(2.0, (noteMidi - 69) / 12.0);
            return Math.Max(2.0, _sr / freq);
        }

        public void NoteOnPluck(int note, float velocity, in GvParams p)
        {
            _note = note;
            _velocity = velocity;
            _readOffsetSamples = NoteToDelay(note);
            _targetOffsetSamples = _readOffsetSamples;
            _gliding = false;

            _feedbackGain = 0.90f + p.Sustain * 0.099f;
            _dampingCoef = 0.15f + p.HFDamping * 0.60f;

            SetBiquadPeaking(ref _body, _sr, 180f, 1.2f, 6.0f);
            _bodyMix = p.BodyResonance;

            int pluckSamples = Math.Max(4, (int)(p.PluckLengthMs * _sr / 1000.0));
            var rng = new Random(note * 7919 + Environment.TickCount);
            float pluckLpCoef = 0.05f + (1f - p.PluckBrightness) * 0.85f;
            float pluckLp = 0f;
            Array.Clear(_dl, 0, DlSize);
            for (int i = 0; i < pluckSamples; i++)
            {
                float noise = (float)(rng.NextDouble() * 2 - 1);
                pluckLp = pluckLp * pluckLpCoef + noise * (1f - pluckLpCoef);
                _dl[i] = pluckLp * velocity;
            }
            _writePos = pluckSamples;
            _lpState = 0f;

            _envAtkRate = 1f / Math.Max(1, p.AttackMs * _sr / 1000.0f);
            _envRelRate = 1f / Math.Max(1, p.ReleaseMs * _sr / 1000.0f);
            _env = 0f;
            _stage = EnvStage.Attack;

            _vibPhase = 0; _vibInc = 2.0 * Math.PI * p.VibratoRate / _sr;
            _peak = 1f;
            _active = true;
        }

        public void NoteOff()
        {
            if (_active && _stage != EnvStage.Release) _stage = EnvStage.Release;
        }

        public void Kill() { _active = false; _env = 0f; _stage = EnvStage.Idle; _peak = 0f; }

        public float RenderSample(in GvParams p)
        {
            if (!_active) return 0f;

            if (_stage == EnvStage.Attack)
            {
                _env += _envAtkRate;
                if (_env >= 1f) { _env = 1f; _stage = EnvStage.Sustain; }
            }
            else if (_stage == EnvStage.Release)
            {
                _env -= _envRelRate;
                if (_env <= 0f) _env = 0f;
            }

            double delayNow = _readOffsetSamples;
            if (p.VibratoDepthCents > 0.01f && p.VibratoRate > 0.01f)
            {
                _vibPhase += _vibInc;
                if (_vibPhase > 2 * Math.PI) _vibPhase -= 2 * Math.PI;
                double centsMod = Math.Sin(_vibPhase) * p.VibratoDepthCents;
                delayNow *= Math.Pow(2, -centsMod / 1200.0);
            }
            if (delayNow < 2.0) delayNow = 2.0;
            if (delayNow > DlSize - 4) delayNow = DlSize - 4;

            double readIdx = _writePos - delayNow;
            while (readIdx < 0) readIdx += DlSize;
            int rInt = (int)readIdx;
            double rFrac = readIdx - rInt;
            int r0 = rInt % DlSize;
            int r1 = (r0 + 1) % DlSize;
            float sample = (float)(_dl[r0] * (1 - rFrac) + _dl[r1] * rFrac);

            _lpState = _lpState * _dampingCoef + sample * (1f - _dampingCoef);
            float fb = _lpState * _feedbackGain;

            _dl[_writePos] = fb;
            _writePos = (_writePos + 1) % DlSize;

            float bodySig = BiquadProcess(ref _body, sample);
            float mixed = sample + bodySig * _bodyMix * 0.4f;

            float outSig = mixed * _env * _velocity;

            _peak = Math.Max(_peak * 0.9998f, Math.Abs(outSig));
            if (_stage == EnvStage.Release && _env <= 0f && _peak < SilenceThreshold)
            {
                _active = false; _stage = EnvStage.Idle;
                return 0f;
            }
            return outSig;
        }

        internal struct BiquadState
        {
            public float b0, b1, b2, a1, a2, x1, x2, y1, y2;
        }
        static void SetBiquadPeaking(ref BiquadState s, int sr, float freq, float q, float gainDb)
        {
            double A = Math.Pow(10.0, gainDb / 40.0);
            double w0 = 2.0 * Math.PI * freq / sr;
            double alpha = Math.Sin(w0) / (2.0 * q);
            double cosw0 = Math.Cos(w0);
            double a0 = 1.0 + alpha / A;
            s.b0 = (float)((1.0 + alpha * A) / a0);
            s.b1 = (float)(-2.0 * cosw0 / a0);
            s.b2 = (float)((1.0 - alpha * A) / a0);
            s.a1 = (float)(-2.0 * cosw0 / a0);
            s.a2 = (float)((1.0 - alpha / A) / a0);
        }
        static float BiquadProcess(ref BiquadState s, float x)
        {
            float y = s.b0 * x + s.b1 * s.x1 + s.b2 * s.x2 - s.a1 * s.y1 - s.a2 * s.y2;
            s.x2 = s.x1; s.x1 = x;
            s.y2 = s.y1; s.y1 = y;
            return y;
        }
    }

    internal struct GvParams
    {
        public float Sustain, HFDamping, PluckBrightness, PluckLengthMs, BodyResonance;
        public float VibratoRate, VibratoDepthCents;
        public float AttackMs, ReleaseMs;
    }
}
