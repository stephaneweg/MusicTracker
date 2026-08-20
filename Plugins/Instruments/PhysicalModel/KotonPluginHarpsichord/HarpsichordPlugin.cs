using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Windows.Controls;
using KotonStudio.Library;

namespace KotonPluginHarpsichord
{
    /// <summary>
    /// Harpsichord / Clavecin — instrument a plectre mecanique (touche → sautereau → plectre pince
    /// la corde). Signatures : ATTACK tres nette avec CLICK metallique initial (le plectre),
    /// sustain long (pas de sourdine), 2 CHOEURS de cordes legerement desaccordes (8'+8' ou 8'+4')
    /// → chorus naturel typique du clavecin. Timbre brillant, riche en harmoniques.
    ///
    /// **DSP** : par voix, 2 Karplus paralleles (delay lines) accordees a +0/±3 cents pour le
    /// chorus des choeurs. Excitation = burst court (~3-5 ms) filtre HP → click sec du plectre.
    /// LP feedback tres ouvert (brillant). Pas de re-pluck legato : chaque note re-declenche.
    /// </summary>
    [KotonInstrument("Harpsichord", Id = "koton.harpsichord", Category = "Physical Model", Version = "1.0", Vendor = "Koton Studio")]
    public sealed class HarpsichordPlugin : IKotonInstrument
    {
        public string Id => "koton.harpsichord";
        public string DisplayName => "Harpsichord";

        readonly KotonParameter _sustain      = new KotonParameter("sustain",      "Sustain",      0.0, 1.0, 0.88);
        readonly KotonParameter _brightness   = new KotonParameter("brightness",   "Brightness",   0.0, 1.0, 0.75);
        readonly KotonParameter _pluckClick   = new KotonParameter("pluck_click",  "Pluck click",  0.0, 1.0, 0.50);
        readonly KotonParameter _choirDetune  = new KotonParameter("choir_detune", "Choir detune", 0.0, 12.0, 3.5, "cent");
        readonly KotonParameter _choirMix     = new KotonParameter("choir_mix",    "Choir mix",    0.0, 1.0, 0.60);
        readonly KotonParameter _register4ft  = new KotonParameter("register_4ft", "4' register",  0.0, 1.0, 0.0);   // 0 = 8'+8', 1 = 8'+4'
        readonly KotonParameter _bodyRes      = new KotonParameter("body_resonance","Body resonance", 0.0, 1.0, 0.30);
        readonly KotonParameter _attack       = new KotonParameter("attack",       "Attack",       0.5, 20.0, 2.0, "ms");
        readonly KotonParameter _release      = new KotonParameter("release",      "Release",      50.0, 2000.0, 600.0, "ms");
        readonly KotonParameter _polyphony    = new KotonParameter("polyphony",    "Polyphony",    2, 12, 8);
        readonly KotonParameter _volumeDb     = new KotonParameter("volume",       "Volume",       -30.0, 6.0, -4.0, "dB");

        readonly List<KotonParameter> _params;
        public IReadOnlyList<KotonParameter> Parameters => _params;

        int _sr;
        HarpsichordVoice[] _voices;
        int _stealCursor;
        const int MaxPoly = 12;

        public HarpsichordPlugin()
        {
            _params = new List<KotonParameter> { _sustain, _brightness, _pluckClick, _choirDetune, _choirMix, _register4ft, _bodyRes, _attack, _release, _polyphony, _volumeDb };
        }
        public bool HasEditor => true;
        public UserControl CreateEditor() => new HarpsichordEditor(this);
        public void Prepare(int sampleRate, int maxBlockSize)
        {
            _sr = sampleRate;
            _voices = new HarpsichordVoice[MaxPoly];
            for (int i = 0; i < MaxPoly; i++) _voices[i] = new HarpsichordVoice(sampleRate);
        }
        public void Reset() { if (_voices != null) foreach (var v in _voices) v.Kill(); }
        public void NoteOn(int note, int velocity, int sampleOffset = 0)
        {
            if (_voices == null || velocity == 0) return;
            int poly = Math.Max(2, Math.Min(MaxPoly, (int)Math.Round(_polyphony.Value)));
            HarpsichordVoice t = null;
            for (int i = 0; i < poly; i++) if (!_voices[i].IsActive) { t = _voices[i]; break; }
            if (t == null) { t = _voices[_stealCursor]; _stealCursor = (_stealCursor + 1) % poly; t.Kill(); }
            t.NoteOn(note, velocity / 127f, Build());
        }
        public void NoteOff(int note, int sampleOffset = 0)
        {
            if (_voices == null) return;
            for (int i = 0; i < _voices.Length; i++) if (_voices[i].IsActive && _voices[i].Note == note) _voices[i].NoteOff();
        }
        public void MidiCC(int cc, int value, int sampleOffset = 0) { if (cc == 123) Reset(); }
        public void SetPitchBend(float value, int sampleOffset = 0) { }

        HarpsichordParams Build() => new HarpsichordParams
        {
            Sustain = (float)_sustain.Value, Brightness = (float)_brightness.Value, PluckClick = (float)_pluckClick.Value,
            ChoirDetuneCents = (float)_choirDetune.Value, ChoirMix = (float)_choirMix.Value,
            Register4ft = (float)_register4ft.Value, BodyResonance = (float)_bodyRes.Value,
            AttackMs = (float)_attack.Value, ReleaseMs = (float)_release.Value,
        };

        public void Render(Span<float> left, Span<float> right)
        {
            if (_voices == null) { left.Clear(); right.Clear(); return; }
            float volLin = (float)Math.Pow(10.0, _volumeDb.Value / 20.0);
            var p = Build(); int n = left.Length;
            for (int i = 0; i < n; i++)
            {
                float sum = 0;
                for (int v = 0; v < _voices.Length; v++) if (_voices[v].IsActive) sum += _voices[v].RenderSample(p);
                float s = sum * volLin;
                if (s > 1f) s = 1f; else if (s < -1f) s = -1f;
                left[i] = s; right[i] = s;
            }
        }
        public byte[] SaveState() { try { var d = new Dictionary<string, double>(); foreach (var kp in _params) d[kp.Id] = kp.Value; return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(d)); } catch { return Array.Empty<byte>(); } }
        public void LoadState(byte[] state) { if (state == null || state.Length == 0) return; try { var d = JsonSerializer.Deserialize<Dictionary<string, double>>(Encoding.UTF8.GetString(state)); if (d == null) return; foreach (var kp in _params) if (d.TryGetValue(kp.Id, out var v)) kp.Value = v; } catch { } }
        public void Dispose() { }
        public void SetParam(string id, double value) { foreach (var kp in _params) if (kp.Id == id) { kp.Value = value; return; } }

        public static readonly string[] PresetNames = { "Clavecin classique (8'+8')", "Clavecin brillant (8'+4')", "Clavecin doux (feutré)" };
        static readonly double[,] PresetValues = {
            //           sus br  clk cdt cmix reg4 body atk rel poly vol
            /*Class*/   { 0.88, 0.75, 0.50, 3.5, 0.60, 0.0, 0.30, 2, 600, 8, -4 },
            /*Brillant*/{ 0.86, 0.85, 0.65, 3.0, 0.65, 1.0, 0.25, 1.5, 500, 8, -4 },
            /*Doux*/    { 0.85, 0.50, 0.30, 4.5, 0.55, 0.0, 0.45, 4,  800, 8, -4 },
        };
        public void LoadPreset(int i)
        {
            if (i < 0 || i >= PresetValues.GetLength(0)) return;
            _sustain.Value = PresetValues[i, 0]; _brightness.Value = PresetValues[i, 1];
            _pluckClick.Value = PresetValues[i, 2]; _choirDetune.Value = PresetValues[i, 3];
            _choirMix.Value = PresetValues[i, 4]; _register4ft.Value = PresetValues[i, 5];
            _bodyRes.Value = PresetValues[i, 6]; _attack.Value = PresetValues[i, 7];
            _release.Value = PresetValues[i, 8]; _polyphony.Value = PresetValues[i, 9];
            _volumeDb.Value = PresetValues[i, 10];
        }
    }

    internal struct HarpsichordParams
    {
        public float Sustain, Brightness, PluckClick;
        public float ChoirDetuneCents, ChoirMix, Register4ft, BodyResonance;
        public float AttackMs, ReleaseMs;
    }
}
