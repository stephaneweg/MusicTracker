using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Windows.Controls;
using KotonStudio.Library;

namespace KotonPluginAcid303
{
    /// <summary>
    /// Acid 303 — monosynth style Roland TB-303. Saw ou square + LP 24dB resonant + envelope
    /// modulation du cutoff (env decay court exp) + accent (boost env + Q) + slide (portamento
    /// entre notes legato). Signature "squelch" acid house / techno / trance.
    /// </summary>
    [KotonInstrument("Acid 303", Id = "koton.acid303", Category = "Synth", Version = "1.0", Vendor = "Koton Studio")]
    public sealed class Acid303Plugin : IKotonInstrument
    {
        public string Id => "koton.acid303";
        public string DisplayName => "Acid 303";

        readonly KotonParameter _wave      = new KotonParameter("wave",       "Wave (0=saw 1=square)", 0, 1, 0);
        readonly KotonParameter _cutoff    = new KotonParameter("cutoff",     "Cutoff",     100, 8000, 1200, "Hz");
        readonly KotonParameter _resonance = new KotonParameter("resonance",  "Resonance",  0.0, 1.0, 0.70);
        readonly KotonParameter _envMod    = new KotonParameter("env_mod",    "Env mod",    0.0, 1.0, 0.60);
        readonly KotonParameter _decay     = new KotonParameter("decay",      "Env decay",  50.0, 1500.0, 300.0, "ms");
        readonly KotonParameter _accent    = new KotonParameter("accent",     "Accent",     0.0, 1.0, 0.40);
        readonly KotonParameter _slideMs   = new KotonParameter("slide_ms",   "Slide time", 0.0, 300.0, 80.0, "ms");
        readonly KotonParameter _distortion= new KotonParameter("distortion", "Distortion", 0.0, 1.0, 0.20);
        readonly KotonParameter _volumeDb  = new KotonParameter("volume",     "Volume",     -30.0, 6.0, -3.0, "dB");

        readonly List<KotonParameter> _params;
        public IReadOnlyList<KotonParameter> Parameters => _params;
        public Acid303Plugin() { _params = new List<KotonParameter> { _wave, _cutoff, _resonance, _envMod, _decay, _accent, _slideMs, _distortion, _volumeDb }; }
        public bool HasEditor => true;
        public UserControl CreateEditor() => new Acid303Editor(this);

        int _sr;
        // Mono state
        double _phase, _phaseInc, _targetInc;
        float _cutoffEnv;   // 1..0 exp decay
        float _envDecayCoef;
        float _amp;
        float _atkR, _relR;
        int _stage;   // 0 idle 1 attack 2 sustain 3 release
        int _note = -1;
        float _vel;
        bool _active;
        double _slideStep;   // increment log par sample vers targetInc

        // Filter state (2 LP12 cascade for 24dB)
        BiquadState _lp1, _lp2;

        public void Prepare(int sampleRate, int maxBlockSize) { _sr = sampleRate; }
        public void Reset() { _active = false; _phase = 0; _amp = 0; _stage = 0; }
        public void NoteOn(int note, int velocity, int sampleOffset = 0)
        {
            if (velocity == 0) return;
            float v = velocity / 127f;
            double f = 440.0 * Math.Pow(2.0, (note - 69) / 12.0);
            _targetInc = f / _sr;
            _note = note;
            bool legato = _active;
            if (!legato)
            {
                _phaseInc = _targetInc;
                _phase = 0;
                _vel = v;
                _atkR = (float)(1.0 / (0.005 * _sr));   // 5ms attack
                _amp = 0; _stage = 1;
                _active = true;
                _lp1 = default; _lp2 = default;
            }
            else if (_slideMs.Value > 0.5)
            {
                double logDist = Math.Log(_targetInc / _phaseInc, 2);
                double glideSamples = _slideMs.Value * _sr / 1000.0;
                _slideStep = logDist / glideSamples;
            }
            else _phaseInc = _targetInc;

            // Trigger cutoff envelope
            _cutoffEnv = 1f;
            _envDecayCoef = (float)Math.Exp(-1.0 / (_decay.Value * _sr / 1000.0));
        }
        public void NoteOff(int note, int sampleOffset = 0) { if (_active && _note == note) { _relR = (float)(1.0 / (0.05 * _sr)); _stage = 3; } }
        public void MidiCC(int cc, int value, int sampleOffset = 0) { if (cc == 123) Reset(); }
        public void SetPitchBend(float value, int sampleOffset = 0) { }

        public void Render(Span<float> left, Span<float> right)
        {
            float volLin = (float)Math.Pow(10.0, _volumeDb.Value / 20.0);
            int wave = (int)Math.Round(_wave.Value);
            float baseCutoff = (float)_cutoff.Value;
            float res = (float)_resonance.Value;
            float envMod = (float)_envMod.Value;
            float accent = (float)_accent.Value;
            float distortion = (float)_distortion.Value;
            int n = left.Length;
            for (int i = 0; i < n; i++)
            {
                if (!_active) { left[i] = 0; right[i] = 0; continue; }
                // Slide
                if (_slideStep != 0)
                {
                    double logCur = Math.Log(_phaseInc, 2) + _slideStep;
                    _phaseInc = Math.Pow(2, logCur);
                    if ((_slideStep > 0 && _phaseInc >= _targetInc) || (_slideStep < 0 && _phaseInc <= _targetInc)) { _phaseInc = _targetInc; _slideStep = 0; }
                }
                // Env amp
                if (_stage == 1) { _amp += _atkR; if (_amp >= 1f) { _amp = 1f; _stage = 2; } }
                else if (_stage == 3) { _amp -= _relR; if (_amp <= 0f) { _amp = 0f; _active = false; continue; } }
                // Env cutoff (exp decay)
                _cutoffEnv *= _envDecayCoef;

                _phase += _phaseInc; if (_phase >= 1) _phase -= 1;
                float src;
                if (wave == 1)
                    src = _phase < 0.5 ? 1f : -1f;
                else
                    src = (float)(2.0 * _phase - 1.0);   // saw

                float dynCutoff = baseCutoff + envMod * _cutoffEnv * 6000f + accent * _vel * 2000f;
                if (dynCutoff < 80) dynCutoff = 80;
                if (dynCutoff > _sr * 0.4f) dynCutoff = _sr * 0.4f;
                float q = 0.5f + res * 8f + accent * _cutoffEnv * 3f;
                if (q > 12f) q = 12f;
                SetBQ(ref _lp1, _sr, dynCutoff, q);
                SetBQ(ref _lp2, _sr, dynCutoff, q);
                float o1 = BQ(ref _lp1, src);
                float o2 = BQ(ref _lp2, o1);
                float shaped = distortion > 0.01f ? (float)Math.Tanh(o2 * (1f + distortion * 3f)) : o2;
                float outv = shaped * _amp * _vel * volLin;
                if (outv > 1f) outv = 1f; else if (outv < -1f) outv = -1f;
                left[i] = outv; right[i] = outv;
            }
        }

        public byte[] SaveState() { try { var d = new Dictionary<string, double>(); foreach (var kp in _params) d[kp.Id] = kp.Value; return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(d)); } catch { return Array.Empty<byte>(); } }
        public void LoadState(byte[] state) { if (state == null || state.Length == 0) return; try { var d = JsonSerializer.Deserialize<Dictionary<string, double>>(Encoding.UTF8.GetString(state)); if (d == null) return; foreach (var kp in _params) if (d.TryGetValue(kp.Id, out var v)) kp.Value = v; } catch { } }
        public void Dispose() { }
        public void SetParam(string id, double value) { foreach (var kp in _params) if (kp.Id == id) { kp.Value = value; return; } }

        internal struct BiquadState { public float b0, b1, b2, a1, a2, x1, x2, y1, y2; public float freq, q; }
        static void SetBQ(ref BiquadState s, int sr, float freq, float q) { if (s.freq == freq && s.q == q) return; s.freq = freq; s.q = q; double w0 = 2.0 * Math.PI * freq / sr, alpha = Math.Sin(w0) / (2.0 * q), cosw0 = Math.Cos(w0), a0 = 1.0 + alpha; s.b0 = (float)((1.0 - cosw0) / 2.0 / a0); s.b1 = (float)((1.0 - cosw0) / a0); s.b2 = s.b0; s.a1 = (float)(-2.0 * cosw0 / a0); s.a2 = (float)((1.0 - alpha) / a0); }
        static float BQ(ref BiquadState s, float x) { float y = s.b0 * x + s.b1 * s.x1 + s.b2 * s.x2 - s.a1 * s.y1 - s.a2 * s.y2; s.x2 = s.x1; s.x1 = x; s.y2 = s.y1; s.y1 = y; return y; }
    }
}
