using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Windows.Controls;
using KotonStudio.Library;

namespace KotonPluginDidgeridoo
{
    /// <summary>
    /// Didgeridoo — drone continu grave avec modulation buccale (formant vowel LFO), rugosite
    /// vocale (bruit + saturation) et beats "toot" (pics harmoniques accentues). Mono voice.
    /// La note MIDI transpose la fondamentale.
    /// </summary>
    [KotonInstrument("Didgeridoo", Id = "koton.didgeridoo", Category = "Physical Model", Version = "1.0", Vendor = "Koton Studio")]
    public sealed class DidgeridooPlugin : IKotonInstrument
    {
        public string Id => "koton.didgeridoo";
        public string DisplayName => "Didgeridoo";

        readonly KotonParameter _formantHz    = new KotonParameter("formant_hz",   "Formant Hz",   400, 2500, 900, "Hz");
        readonly KotonParameter _formantQ     = new KotonParameter("formant_q",    "Formant Q",    1.0, 12.0, 4.0);
        readonly KotonParameter _wobbleRate   = new KotonParameter("wobble_rate",  "Wobble rate",  0.0, 6.0, 1.5, "Hz");
        readonly KotonParameter _wobbleDepth  = new KotonParameter("wobble_depth", "Wobble depth", 0.0, 1.0, 0.55);
        readonly KotonParameter _growl        = new KotonParameter("growl",        "Growl",        0.0, 1.0, 0.35);
        readonly KotonParameter _breath       = new KotonParameter("breath",       "Breath",       0.0, 1.0, 0.20);
        readonly KotonParameter _harmonics    = new KotonParameter("harmonics",    "Harmonics",    0.0, 1.0, 0.55);
        readonly KotonParameter _attack       = new KotonParameter("attack",       "Attack",       10.0, 500.0, 60.0, "ms");
        readonly KotonParameter _release      = new KotonParameter("release",      "Release",      50.0, 2000.0, 400.0, "ms");
        readonly KotonParameter _volumeDb     = new KotonParameter("volume",       "Volume",       -30.0, 6.0, -3.0, "dB");

        readonly List<KotonParameter> _params;
        public IReadOnlyList<KotonParameter> Parameters => _params;
        public DidgeridooPlugin() { _params = new List<KotonParameter> { _formantHz, _formantQ, _wobbleRate, _wobbleDepth, _growl, _breath, _harmonics, _attack, _release, _volumeDb }; }
        public bool HasEditor => true;
        public UserControl CreateEditor() => new DidgeridooEditor(this);

        int _sr;
        DidgeridooVoice _voice;
        public void Prepare(int sampleRate, int maxBlockSize) { _sr = sampleRate; _voice = new DidgeridooVoice(sampleRate); }
        public void Reset() => _voice?.Kill();
        public void NoteOn(int note, int velocity, int sampleOffset = 0)
        {
            if (_voice == null || velocity == 0) return;
            if (_voice.IsActive) _voice.NoteChange(note, velocity / 127f);
            else _voice.NoteOn(note, velocity / 127f, (float)_attack.Value);
        }
        public void NoteOff(int note, int sampleOffset = 0) { if (_voice != null && _voice.IsActive && _voice.Note == note) _voice.NoteOff((float)_release.Value); }
        public void MidiCC(int cc, int value, int sampleOffset = 0) { if (cc == 123) Reset(); }
        public void SetPitchBend(float value, int sampleOffset = 0) { }

        public void Render(Span<float> left, Span<float> right)
        {
            if (_voice == null) { left.Clear(); right.Clear(); return; }
            float volLin = (float)Math.Pow(10.0, _volumeDb.Value / 20.0);
            float fHz = (float)_formantHz.Value, fQ = (float)_formantQ.Value;
            float wR = (float)_wobbleRate.Value, wD = (float)_wobbleDepth.Value;
            float gr = (float)_growl.Value, br = (float)_breath.Value, ha = (float)_harmonics.Value;
            int n = left.Length;
            for (int i = 0; i < n; i++)
            {
                float s = _voice.IsActive ? _voice.RenderSample(fHz, fQ, wR, wD, gr, br, ha) * volLin : 0f;
                if (s > 1f) s = 1f; else if (s < -1f) s = -1f;
                left[i] = s; right[i] = s;
            }
        }
        public byte[] SaveState() { try { var d = new Dictionary<string, double>(); foreach (var kp in _params) d[kp.Id] = kp.Value; return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(d)); } catch { return Array.Empty<byte>(); } }
        public void LoadState(byte[] state) { if (state == null || state.Length == 0) return; try { var d = JsonSerializer.Deserialize<Dictionary<string, double>>(Encoding.UTF8.GetString(state)); if (d == null) return; foreach (var kp in _params) if (d.TryGetValue(kp.Id, out var v)) kp.Value = v; } catch { } }
        public void Dispose() { }
        public void SetParam(string id, double value) { foreach (var kp in _params) if (kp.Id == id) { kp.Value = value; return; } }
    }

    internal sealed class DidgeridooVoice
    {
        readonly int _sr;
        double _phase, _phaseInc;
        double _wobPhase;
        BiquadState _formant, _lpFinal;
        float _env, _atkR, _relR;
        int _stage;   // 0 idle 1 attack 2 sustain 3 release
        Random _rng;
        float _noiseState;
        int _note; float _velocity; bool _active;

        public bool IsActive => _active;
        public int Note => _note;
        public DidgeridooVoice(int sr) { _sr = sr; }

        public void NoteOn(int note, float vel, float atkMs)
        {
            _note = note; _velocity = vel;
            double f = 440.0 * Math.Pow(2.0, (note - 69) / 12.0);
            _phaseInc = f / _sr;
            _phase = 0; _wobPhase = 0;
            _rng = new Random(note * 7919 + Environment.TickCount);
            _atkR = 1f / Math.Max(1, atkMs * _sr / 1000f);
            _env = 0; _stage = 1;
            SetLP(ref _lpFinal, _sr, 3500, 0.707f);
            _active = true;
        }
        public void NoteChange(int note, float vel)
        {
            _note = note; _velocity = vel;
            double f = 440.0 * Math.Pow(2.0, (note - 69) / 12.0);
            _phaseInc = f / _sr;
            if (_stage == 3) { _stage = 2; if (_env < vel) _env = vel; }
        }
        public void NoteOff(float relMs) { _relR = 1f / Math.Max(1, relMs * _sr / 1000f); _stage = 3; }
        public void Kill() { _active = false; _env = 0; _stage = 0; }

        public float RenderSample(float formantHz, float formantQ, float wobRate, float wobDepth, float growl, float breath, float harmonics)
        {
            if (!_active) return 0f;
            if (_stage == 1) { _env += _atkR; if (_env >= 1f) { _env = 1f; _stage = 2; } }
            else if (_stage == 3) { _env -= _relR; if (_env <= 0f) { _env = 0f; _active = false; return 0f; } }

            _phase += _phaseInc;
            if (_phase >= 1) _phase -= 1;

            // Wobble LFO qui module la freq du formant (le formant "bouge" avec la bouche)
            _wobPhase += wobRate / _sr;
            if (_wobPhase >= 1) _wobPhase -= 1;
            float wob = (float)Math.Sin(_wobPhase * 2 * Math.PI);
            float formantMod = formantHz * (1f + wob * wobDepth * 0.5f);
            SetBP(ref _formant, _sr, formantMod, formantQ);

            // Signal riche : sub sine + saw (harmoniques) + noise (breath) + growl (distortion)
            float sine = (float)Math.Sin(_phase * 2 * Math.PI);
            float saw = (float)(2.0 * _phase - 1.0);
            float noise = (float)(_rng.NextDouble() * 2 - 1);
            _noiseState = _noiseState * 0.9f + noise * 0.1f;

            float source = sine * 0.6f + saw * harmonics * 0.4f + _noiseState * breath * 0.15f;
            // Growl = distortion tanh
            if (growl > 0.01f) source = (float)Math.Tanh(source * (1f + growl * 3f)) * (1f - growl * 0.3f);

            // Formant BP ajoute au dry
            float f = BiquadProcess(ref _formant, source);
            float mixed = source + f * 0.5f;
            // LP final pour eviter les aigus rugueux
            mixed = BiquadProcess(ref _lpFinal, mixed);
            return mixed * _env * _velocity * 0.9f;
        }

        internal struct BiquadState { public float b0, b1, b2, a1, a2, x1, x2, y1, y2; }
        static void SetBP(ref BiquadState s, int sr, float freq, float q)
        {
            if (freq < 20f) freq = 20f; if (freq > sr * 0.45f) freq = sr * 0.45f;
            double w0 = 2.0 * Math.PI * freq / sr, alpha = Math.Sin(w0) / (2.0 * q), cosw0 = Math.Cos(w0), a0 = 1.0 + alpha;
            s.b0 = (float)(alpha / a0); s.b1 = 0; s.b2 = (float)(-alpha / a0);
            s.a1 = (float)(-2.0 * cosw0 / a0); s.a2 = (float)((1.0 - alpha) / a0);
        }
        static void SetLP(ref BiquadState s, int sr, float freq, float q)
        {
            if (freq < 20f) freq = 20f; if (freq > sr * 0.45f) freq = sr * 0.45f;
            double w0 = 2.0 * Math.PI * freq / sr, alpha = Math.Sin(w0) / (2.0 * q), cosw0 = Math.Cos(w0), a0 = 1.0 + alpha;
            s.b0 = (float)((1.0 - cosw0) / 2.0 / a0); s.b1 = (float)((1.0 - cosw0) / a0); s.b2 = s.b0;
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
