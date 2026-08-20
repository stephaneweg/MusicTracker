using System;

namespace KotonPluginHarpsichord
{
    internal sealed class HarpsichordVoice
    {
        readonly int _sr;
        const int DlSize = 8192;
        readonly float[] _dl1 = new float[DlSize];   // choir 1 (8')
        readonly float[] _dl2 = new float[DlSize];   // choir 2 (8'+ ou 4')
        int _wp1, _wp2;
        int _len1, _len2;
        float _lp1, _lp2;
        float _feedback, _brightCoef;

        // Click transient (burst filtre HP au debut)
        float _click;
        float _clickDecay;

        // Body resonance (biquad peak ~150 Hz)
        BiquadState _body;
        float _bodyMix;

        // Envelope
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

        public HarpsichordVoice(int sr) { _sr = sr; }

        public void NoteOn(int note, float vel, in HarpsichordParams p)
        {
            _note = note; _velocity = vel;
            double f = 440.0 * Math.Pow(2.0, (note - 69) / 12.0);
            _len1 = Math.Max(2, (int)Math.Round(_sr / f));
            // 2e choeur : soit 8' (meme f + detune cents) soit 4' (une octave plus haut)
            double f2 = f * Math.Pow(2.0, p.ChoirDetuneCents / 1200.0);
            if (p.Register4ft > 0.5f) f2 *= 2.0;
            _len2 = Math.Max(2, (int)Math.Round(_sr / f2));
            if (_len1 > DlSize - 4) _len1 = DlSize - 4;
            if (_len2 > DlSize - 4) _len2 = DlSize - 4;

            _feedback = 0.94f + p.Sustain * 0.055f;
            _brightCoef = 0.02f + (1f - p.Brightness) * 0.55f;   // tres ouvert = brillant

            SetBiquadPeaking(ref _body, _sr, 150f, 1.4f, 5f);
            _bodyMix = p.BodyResonance;

            // Excitation clavecin : burst court filtre HP → click sec du plectre
            int burstSamples = 4;
            var rng = new Random(note * 7919 + Environment.TickCount);
            Array.Clear(_dl1, 0, DlSize); Array.Clear(_dl2, 0, DlSize);
            for (int i = 0; i < _len1; i++)
            {
                float noise = (float)(rng.NextDouble() * 2 - 1);
                // Bruit HP filtre : premiere derivee approx
                float hp = i > 0 ? noise - (float)(rng.NextDouble() * 2 - 1) : noise;
                _dl1[i] = hp * vel * 0.5f;
            }
            for (int i = 0; i < _len2; i++)
            {
                float noise = (float)(rng.NextDouble() * 2 - 1);
                float hp = i > 0 ? noise - (float)(rng.NextDouble() * 2 - 1) : noise;
                _dl2[i] = hp * vel * 0.5f * p.ChoirMix;
            }
            _wp1 = _len1; _wp2 = _len2;
            _lp1 = _lp2 = 0f;

            // Click transient : impulsion courte forte au debut
            _click = vel * p.PluckClick;
            _clickDecay = (float)Math.Exp(-1.0 / (0.002 * _sr));   // 2ms

            _atkR = 1f / Math.Max(1, p.AttackMs * _sr / 1000f);
            _relR = 1f / Math.Max(1, p.ReleaseMs * _sr / 1000f);
            _env = 0f; _stage = Stage.Attack;
            _peak = 1f; _active = true;
        }

        public void NoteOff() { if (_active && _stage != Stage.Release) _stage = Stage.Release; }
        public void Kill() { _active = false; _env = 0; _stage = Stage.Idle; _peak = 0; }

        public float RenderSample(in HarpsichordParams p)
        {
            if (!_active) return 0f;
            if (_stage == Stage.Attack) { _env += _atkR; if (_env >= 1f) { _env = 1f; _stage = Stage.Sustain; } }
            else if (_stage == Stage.Release) { _env -= _relR; if (_env <= 0f) _env = 0f; }

            // Choir 1
            int r1 = (_wp1 - _len1 + DlSize) % DlSize;
            float s1 = _dl1[r1];
            _lp1 = _lp1 * _brightCoef + s1 * (1f - _brightCoef);
            _dl1[_wp1] = _lp1 * _feedback;
            _wp1 = (_wp1 + 1) % DlSize;

            // Choir 2
            int r2 = (_wp2 - _len2 + DlSize) % DlSize;
            float s2 = _dl2[r2];
            _lp2 = _lp2 * _brightCoef + s2 * (1f - _brightCoef);
            _dl2[_wp2] = _lp2 * _feedback;
            _wp2 = (_wp2 + 1) % DlSize;

            float mixed = s1 + s2 * p.ChoirMix;

            // Click
            _click *= _clickDecay;

            // Body resonance
            float bodySig = BiquadProcess(ref _body, mixed);
            float outSig = (mixed + _click + bodySig * _bodyMix * 0.35f) * _env * _velocity;

            _peak = Math.Max(_peak * 0.9998f, Math.Abs(outSig));
            if (_stage == Stage.Release && _env <= 0f && _peak < Silence) { _active = false; _stage = Stage.Idle; return 0f; }
            return outSig;
        }

        internal struct BiquadState { public float b0, b1, b2, a1, a2, x1, x2, y1, y2; }
        static void SetBiquadPeaking(ref BiquadState s, int sr, float freq, float q, float gainDb)
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
