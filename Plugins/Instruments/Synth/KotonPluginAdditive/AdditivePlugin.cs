using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Windows.Controls;
using KotonStudio.Library;

namespace KotonPluginAdditive
{
    /// <summary>
    /// Additive — 16 harmoniques sines simultanees, chacune avec amplitude propre. Presets
    /// pre-figures (organ, bell, sine, saw, square, clarinet). Tilt slider assombrit ou eclaircit
    /// le spectre en pondérant les amplitudes des harmoniques hautes.
    /// </summary>
    [KotonInstrument("Additive Synth", Id = "koton.additive", Category = "Synth", Version = "1.0", Vendor = "Koton Studio")]
    public sealed class AdditivePlugin : IKotonInstrument
    {
        public string Id => "koton.additive";
        public string DisplayName => "Additive Synth";

        const int NumHarm = 16;
        // Preset harmonic profiles (16 amplitudes normalisées 0-1)
        public static readonly string[] PresetNames = { "Sine (H1)", "Saw", "Square", "Organ", "Bell", "Clarinet", "Flute", "Bass" };
        static readonly float[][] Presets = new float[][]
        {
            new float[]{1,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0},   // Sine
            new float[]{1f, 1f/2, 1f/3, 1f/4, 1f/5, 1f/6, 1f/7, 1f/8, 1f/9, 1f/10, 1f/11, 1f/12, 1f/13, 1f/14, 1f/15, 1f/16},  // Saw
            new float[]{1f, 0, 1f/3, 0, 1f/5, 0, 1f/7, 0, 1f/9, 0, 1f/11, 0, 1f/13, 0, 1f/15, 0},   // Square
            new float[]{1, 0.8f, 0.6f, 0, 0, 0, 0, 0.4f, 0, 0, 0, 0, 0, 0, 0, 0},   // Organ 8'+4'
            new float[]{1, 0, 0.6f, 0, 0.4f, 0, 0.3f, 0, 0.2f, 0, 0.15f, 0, 0.1f, 0, 0.08f, 0},   // Bell (inharm approximated)
            new float[]{1, 0, 0.75f, 0, 0.5f, 0, 0.35f, 0, 0.2f, 0, 0.1f, 0, 0, 0, 0, 0},   // Clarinet (odd)
            new float[]{1, 0.5f, 0.1f, 0.05f, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0},   // Flute (mostly fundamental)
            new float[]{1, 0.7f, 0.5f, 0.4f, 0.3f, 0.2f, 0.15f, 0.1f, 0, 0, 0, 0, 0, 0, 0, 0},   // Bass
        };
        internal float[] CurrentProfile { get; private set; } = (float[])Presets[0].Clone();
        public void LoadPreset(int idx) { if (idx < 0 || idx >= Presets.Length) return; CurrentProfile = (float[])Presets[idx].Clone(); }

        readonly KotonParameter _tilt      = new KotonParameter("tilt",     "Tilt (dark/bright)", -1.0, 1.0, 0.0);
        readonly KotonParameter _detune    = new KotonParameter("detune",   "H detune",       0.0, 0.02, 0.0);   // desaccord des harmoniques hautes
        readonly KotonParameter _attack    = new KotonParameter("attack",   "Attack",         1.0, 2000.0, 20.0, "ms");
        readonly KotonParameter _release   = new KotonParameter("release",  "Release",        50.0, 3000.0, 400.0, "ms");
        readonly KotonParameter _volumeDb  = new KotonParameter("volume",   "Volume",         -30.0, 6.0, -6.0, "dB");

        readonly List<KotonParameter> _params;
        public IReadOnlyList<KotonParameter> Parameters => _params;
        public AdditivePlugin() { _params = new List<KotonParameter> { _tilt, _detune, _attack, _release, _volumeDb }; }
        public bool HasEditor => true;
        public UserControl CreateEditor() => new AdditiveEditor(this);

        int _sr;
        AdVoice[] _voices;
        int _stealCursor;
        const int MaxPoly = 6;
        public void Prepare(int sampleRate, int maxBlockSize) { _sr = sampleRate; _voices = new AdVoice[MaxPoly]; for (int i = 0; i < MaxPoly; i++) _voices[i] = new AdVoice(sampleRate); }
        public void Reset() { if (_voices != null) foreach (var v in _voices) v.Kill(); }
        public void NoteOn(int note, int velocity, int sampleOffset = 0)
        {
            if (_voices == null || velocity == 0) return;
            AdVoice t = null;
            for (int i = 0; i < MaxPoly; i++) if (!_voices[i].IsActive) { t = _voices[i]; break; }
            if (t == null) { t = _voices[_stealCursor]; _stealCursor = (_stealCursor + 1) % MaxPoly; t.Kill(); }
            t.NoteOn(note, velocity / 127f, (float)_attack.Value, (float)_detune.Value);
        }
        public void NoteOff(int note, int sampleOffset = 0) { if (_voices == null) return; for (int i = 0; i < _voices.Length; i++) if (_voices[i].IsActive && _voices[i].Note == note) _voices[i].NoteOff((float)_release.Value); }
        public void MidiCC(int cc, int value, int sampleOffset = 0) { if (cc == 123) Reset(); }
        public void SetPitchBend(float value, int sampleOffset = 0) { }

        public void Render(Span<float> left, Span<float> right)
        {
            if (_voices == null) { left.Clear(); right.Clear(); return; }
            float volLin = (float)Math.Pow(10.0, _volumeDb.Value / 20.0);
            float tilt = (float)_tilt.Value;
            // Apply tilt : amplitudes des harm h scale par (1..16)^(-tilt)
            var profile = new float[NumHarm];
            for (int h = 0; h < NumHarm; h++)
            {
                float w = (float)Math.Pow(h + 1, -tilt);
                profile[h] = CurrentProfile[h] * w;
            }
            int n = left.Length;
            for (int i = 0; i < n; i++)
            {
                float sum = 0;
                for (int v = 0; v < _voices.Length; v++) if (_voices[v].IsActive) sum += _voices[v].RenderSample(profile);
                float s = sum * volLin;
                if (s > 1f) s = 1f; else if (s < -1f) s = -1f;
                left[i] = s; right[i] = s;
            }
        }

        public byte[] SaveState()
        {
            try { var d = new Dictionary<string, object>(); foreach (var kp in _params) d[kp.Id] = kp.Value; d["_profile"] = CurrentProfile; return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(d)); } catch { return Array.Empty<byte>(); }
        }
        public void LoadState(byte[] state)
        {
            if (state == null || state.Length == 0) return;
            try
            {
                using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(state));
                var r = doc.RootElement;
                foreach (var kp in _params) if (r.TryGetProperty(kp.Id, out var v) && v.ValueKind == JsonValueKind.Number) kp.Value = v.GetDouble();
                if (r.TryGetProperty("_profile", out var p) && p.ValueKind == JsonValueKind.Array)
                {
                    var arr = new float[NumHarm];
                    int i = 0;
                    foreach (var el in p.EnumerateArray()) { if (i >= NumHarm) break; arr[i++] = (float)el.GetDouble(); }
                    if (i == NumHarm) CurrentProfile = arr;
                }
            }
            catch { }
        }
        public void Dispose() { }
        public void SetParam(string id, double value) { foreach (var kp in _params) if (kp.Id == id) { kp.Value = value; return; } }
    }

    internal sealed class AdVoice
    {
        readonly int _sr;
        const int NumHarm = 16;
        double[] _phase = new double[NumHarm];
        double[] _inc = new double[NumHarm];
        float _env, _atkR, _relR;
        int _stage;
        int _note; float _vel; bool _active;
        public bool IsActive => _active;
        public int Note => _note;
        public AdVoice(int sr) { _sr = sr; }
        public void NoteOn(int note, float vel, float atkMs, float detune)
        {
            _note = note; _vel = vel;
            double f = 440.0 * Math.Pow(2.0, (note - 69) / 12.0);
            var rng = new Random(note * 7919);
            for (int h = 0; h < NumHarm; h++)
            {
                double det = 1 + (rng.NextDouble() * 2 - 1) * detune;
                _inc[h] = (f * (h + 1) * det) / _sr;
                _phase[h] = rng.NextDouble();
            }
            _atkR = 1f / Math.Max(1, atkMs * _sr / 1000f);
            _env = 0; _stage = 1;
            _active = true;
        }
        public void NoteOff(float relMs) { _relR = 1f / Math.Max(1, relMs * _sr / 1000f); _stage = 3; }
        public void Kill() { _active = false; _env = 0; _stage = 0; }
        public float RenderSample(float[] profile)
        {
            if (!_active) return 0f;
            if (_stage == 1) { _env += _atkR; if (_env >= 1f) { _env = 1f; _stage = 2; } }
            else if (_stage == 3) { _env -= _relR; if (_env <= 0f) { _env = 0f; _active = false; return 0f; } }
            float sum = 0; float norm = 0;
            for (int h = 0; h < NumHarm; h++)
            {
                if (profile[h] <= 1e-4f) continue;
                if (_inc[h] > 0.45) continue;   // nyquist guard
                _phase[h] += _inc[h]; if (_phase[h] >= 1) _phase[h] -= 1;
                sum += (float)Math.Sin(_phase[h] * 2 * Math.PI) * profile[h];
                norm += profile[h];
            }
            if (norm > 0.001f) sum /= norm;
            return sum * _env * _vel * 0.8f;
        }
    }
}
