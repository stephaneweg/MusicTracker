using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Windows.Controls;
using KotonStudio.Library;

namespace KotonPluginFaustWoodwinds
{
    /// <summary>
    /// Woodwinds physiques portes depuis FAUST physmodels.lib (Grame CNCM Lyon). V1 = uniquement
    /// clarinette (le seul modele bien documente dans physmodels.lib). V2 pourra ajouter sax et
    /// hautbois via variations des parametres (reed slope, tube geometry, formant).
    ///
    /// Port fidele du clarinetModel FAUST avec 2 delays bidirectionnels + reed table lineaire
    /// clippee (slope pilote par stiffness) + smoothing du bell opening.
    /// </summary>
    [KotonInstrument("Clarinette FAUST", Id = "koton.faust.clarinet", Category = "Physical Model", Version = "1.0", Vendor = "Koton Studio")]
    public sealed class FaustWoodwindsPlugin : IKotonInstrument
    {
        public string Id => "koton.faust.clarinet";
        public string DisplayName => "Clarinette FAUST";

        // Defaults ajustes pour que la boucle waveguide amorce l'auto-oscillation des le premier
        // NoteOn : pressure suffisante (0.85), bell "presque ferme" pour maintenir la reflexion
        // haute (0.05, → coefficient de reflexion effectif ~-0.95), reed medium.
        readonly KotonParameter _pressure       = new KotonParameter("pressure",        "Air pressure",     0.0, 1.0, 0.85);
        readonly KotonParameter _reedStiffness  = new KotonParameter("reed_stiffness",  "Reed stiffness",   0.0, 1.0, 0.5);
        readonly KotonParameter _bellOpening    = new KotonParameter("bell_opening",    "Bell opening",     0.0, 1.0, 0.05);
        readonly KotonParameter _breathNoise    = new KotonParameter("breath_noise",    "Breath noise",     0.0, 1.0, 0.15);
        readonly KotonParameter _vibratoRate    = new KotonParameter("vibrato_rate",    "Vibrato rate",     0.0, 8.0, 5.0, "Hz");
        readonly KotonParameter _vibratoDepth   = new KotonParameter("vibrato_depth",   "Vibrato depth",    0.0, 30.0, 4.0, "ct");
        readonly KotonParameter _attackTime     = new KotonParameter("attack_time",     "Attack",           0.005, 0.5, 0.020, "s");
        readonly KotonParameter _releaseTime    = new KotonParameter("release_time",    "Release",          0.02, 1.5, 0.15, "s");
        readonly KotonParameter _outputGain     = new KotonParameter("output_gain",     "Output gain",      0.0, 2.0, 1.0);
        readonly KotonParameter _volumeDb       = new KotonParameter("volume",          "Volume",           -30.0, 6.0, -6.0, "dB");

        readonly List<KotonParameter> _params;
        public IReadOnlyList<KotonParameter> Parameters => _params;

        int _sampleRate;
        FaustClarinetVoice[] _voices;
        int _stealCursor;
        const int Polyphony = 6;

        // Vibrato partage entre voix (LFO global)
        float _vibPhase, _vibInc;

        public FaustWoodwindsPlugin()
        {
            _params = new List<KotonParameter>
            {
                _pressure, _reedStiffness, _bellOpening, _breathNoise,
                _vibratoRate, _vibratoDepth, _attackTime, _releaseTime,
                _outputGain, _volumeDb,
            };
        }

        public bool HasEditor => true;
        public UserControl CreateEditor() => new FaustWoodwindsEditor(this);

        public void Prepare(int sampleRate, int maxBlockSize)
        {
            _sampleRate = sampleRate;
            _voices = new FaustClarinetVoice[Polyphony];
            for (int i = 0; i < Polyphony; i++) _voices[i] = new FaustClarinetVoice(sampleRate);
            _vibPhase = 0f;
        }

        public void Reset()
        {
            if (_voices != null) foreach (var v in _voices) v.Kill();
        }

        public void NoteOn(int note, int velocity, int sampleOffset = 0)
        {
            if (_voices == null || velocity == 0) return;
            float vel = velocity / 127f;

            FaustClarinetVoice t = null;
            for (int i = 0; i < _voices.Length; i++) if (_voices[i].IsActive && _voices[i].Note == note) { t = _voices[i]; break; }
            if (t == null) for (int i = 0; i < _voices.Length; i++) if (!_voices[i].IsActive) { t = _voices[i]; break; }
            if (t == null) { t = _voices[_stealCursor]; _stealCursor = (_stealCursor + 1) % _voices.Length; t.Kill(); }

            t.NoteOn(note, vel,
                (float)_pressure.Value,
                (float)_reedStiffness.Value,
                (float)_bellOpening.Value,
                (float)_attackTime.Value);
        }

        public void NoteOff(int note, int sampleOffset = 0)
        {
            if (_voices == null) return;
            float rel = (float)_releaseTime.Value;
            for (int i = 0; i < _voices.Length; i++)
                if (_voices[i].IsActive && _voices[i].Note == note)
                    _voices[i].NoteOff(rel);
        }

        public void MidiCC(int cc, int value, int sampleOffset = 0) { if (cc == 123) Reset(); }
        public void SetPitchBend(float value, int sampleOffset = 0) { }

        public void Render(Span<float> left, Span<float> right)
        {
            if (_voices == null) { left.Clear(); right.Clear(); return; }
            float volLin = (float)Math.Pow(10.0, _volumeDb.Value / 20.0);
            float outputGain = (float)_outputGain.Value;
            float noiseGain = (float)_breathNoise.Value * 0.2f;
            _vibInc = (float)(2 * Math.PI * _vibratoRate.Value / _sampleRate);
            float vibDepthCents = (float)_vibratoDepth.Value;
            float vibGain = 0.1f * (vibDepthCents / 30f);

            int n = left.Length;
            for (int i = 0; i < n; i++)
            {
                _vibPhase += _vibInc;
                if (_vibPhase > 2 * Math.PI) _vibPhase -= (float)(2 * Math.PI);
                float vibMul = 1f + vibGain * (float)Math.Sin(_vibPhase);

                float sum = 0f;
                for (int v = 0; v < _voices.Length; v++)
                    if (_voices[v].IsActive) sum += _voices[v].Tick(noiseGain, vibMul);

                float s = sum * outputGain * volLin;
                if (s > 1f) s = 1f; else if (s < -1f) s = -1f;
                left[i] = s; right[i] = s;
            }
        }

        public byte[] SaveState()
        {
            try { var d = new Dictionary<string, double>(); foreach (var kp in _params) d[kp.Id] = kp.Value; return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(d)); }
            catch { return Array.Empty<byte>(); }
        }

        public void LoadState(byte[] state)
        {
            if (state == null || state.Length == 0) return;
            try { var d = JsonSerializer.Deserialize<Dictionary<string, double>>(Encoding.UTF8.GetString(state)); if (d == null) return; foreach (var kp in _params) if (d.TryGetValue(kp.Id, out var v)) kp.Value = v; }
            catch { }
        }

        public void Dispose() { }

        public void SetParam(string id, double value)
        {
            foreach (var kp in _params) if (kp.Id == id) { kp.Value = value; return; }
        }
    }
}
