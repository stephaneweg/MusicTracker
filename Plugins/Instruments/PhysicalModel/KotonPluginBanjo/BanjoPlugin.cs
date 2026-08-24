using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Windows.Controls;
using KotonStudio.Library;

namespace KotonPluginBanjo
{
    /// <summary>
    /// Banjo (5-cordes bluegrass) — Karplus + tete tambour vibrante. Signatures : ATTACK sec
    /// "plunk", sustain court a moyen (dampe rapide par la peau), TWANG aigu caracteristique
    /// (harmoniques hautes), resonance de la peau tambour (~80-120 Hz + harmoniques modales).
    ///
    /// **DSP** : Karplus classique avec feedback moderee (~0.94 = decay rapide) + 2 modes
    /// de peau tambour (biquad peaking a 90 Hz et 250 Hz) sommes a la sortie. Excitation =
    /// burst court HP (bruit filtre HP → click du plectre/ongle). Brightness elevee = twang.
    /// </summary>
    [KotonInstrument("Banjo", Id = "koton.banjo", Category = "Physical Model", Version = "1.0", Vendor = "Koton Studio")]
    public sealed class BanjoPlugin : IKotonInstrument
    {
        public string Id => "koton.banjo";
        public string DisplayName => "Banjo";

        readonly KotonParameter _sustain    = new KotonParameter("sustain",     "Sustain",       0.0, 1.0, 0.55);
        readonly KotonParameter _brightness = new KotonParameter("brightness",  "Brightness",    0.0, 1.0, 0.75);
        readonly KotonParameter _twang      = new KotonParameter("twang",       "Twang",         0.0, 1.0, 0.60);
        readonly KotonParameter _drumHead   = new KotonParameter("drum_head",   "Drum head",     0.0, 1.0, 0.55);
        readonly KotonParameter _pluckLen   = new KotonParameter("pluck_length","Pluck length",  2.0, 20.0, 5.0, "ms");
        readonly KotonParameter _attack     = new KotonParameter("attack",      "Attack",        0.5, 20.0, 1.5, "ms");
        readonly KotonParameter _release    = new KotonParameter("release",     "Release",       50.0, 1000.0, 250.0, "ms");
        readonly KotonParameter _polyphony  = new KotonParameter("polyphony",   "Polyphony",     2, 8, 5) { Automatable = false };
        readonly KotonParameter _volumeDb   = new KotonParameter("volume",      "Volume",        -30.0, 6.0, -3.0, "dB");
        // Ré-attaque périodique : rejoue la note tenue tous les 1/taux de seconde.
        // 0 Hz = une seule attaque, donc aucun projet existant ne change.
        readonly KotonStudio.Plugins.Shared.KotonReAttack _retrig =
            new KotonStudio.Plugins.Shared.KotonReAttack("Trémolo", 20.0, 0.0);

        readonly List<KotonParameter> _params;
        public IReadOnlyList<KotonParameter> Parameters => _params;

        int _sr;
        BanjoVoice[] _voices;
        int _stealCursor;
        const int MaxPoly = 8;

        public BanjoPlugin()
        {
            _params = new List<KotonParameter> { _sustain, _brightness, _twang, _drumHead, _pluckLen, _attack, _release, _polyphony, _volumeDb, _retrig.Rate };
        }
        public bool HasEditor => true;
        public UserControl CreateEditor() => new BanjoEditor(this);
        public void Prepare(int sampleRate, int maxBlockSize)
        {
            _sr = sampleRate;
            _voices = new BanjoVoice[MaxPoly];
            for (int i = 0; i < MaxPoly; i++) _voices[i] = new BanjoVoice(sampleRate);
            _retrig.Prepare(sampleRate);
        }
        public void Reset()
        {
            if (_voices != null) foreach (var v in _voices) v.Kill();
            _retrig.Reset();
        }
        public void NoteOn(int note, int velocity, int sampleOffset = 0)
        {
            _retrig.NoteOn(note, velocity);
            if (_voices == null || velocity == 0) return;
            int poly = Math.Max(2, Math.Min(MaxPoly, (int)Math.Round(_polyphony.Value)));
            BanjoVoice t = null;
            // Rejouer la MÊME note reprend sa voix au lieu d'en allouer une neuve : sans ça les coups
            // répétés s'empilent (mesure : pic 0,33 → 0,68 à 9 coups/s). C'est aussi le comportement
            // physique — repincer une corde déjà en vibration l'arrête.
            for (int i = 0; i < poly; i++) if (_voices[i].IsActive && _voices[i].Note == note) { t = _voices[i]; t.Kill(); break; }
            if (t == null) for (int i = 0; i < poly; i++) if (!_voices[i].IsActive) { t = _voices[i]; break; }
            if (t == null) { t = _voices[_stealCursor]; _stealCursor = (_stealCursor + 1) % poly; t.Kill(); }
            t.NoteOn(note, velocity / 127f, Build());
        }
        public void NoteOff(int note, int sampleOffset = 0) { if (_voices == null) return; for (int i = 0; i < _voices.Length; i++) if (_voices[i].IsActive && _voices[i].Note == note) _voices[i].NoteOff(); }
        public void MidiCC(int cc, int value, int sampleOffset = 0) { if (cc == 123) Reset(); }
        public void SetPitchBend(float value, int sampleOffset = 0) { }

        BanjoParams Build() => new BanjoParams { Sustain = (float)_sustain.Value, Brightness = (float)_brightness.Value, Twang = (float)_twang.Value, DrumHead = (float)_drumHead.Value, PluckLenMs = (float)_pluckLen.Value, AttackMs = (float)_attack.Value, ReleaseMs = (float)_release.Value };

        public void Render(Span<float> left, Span<float> right)
        {
            if (_voices == null) { left.Clear(); right.Clear(); return; }
            float volLin = (float)Math.Pow(10.0, _volumeDb.Value / 20.0);
            var p = Build(); int n = left.Length;
            for (int i = 0; i < n; i++)
            {
                // Ré-attaque : à l'échéance, la note tenue est rejouée (BeginStroke neutralise
                // la notification que NoteOn va renvoyer à l'engin).
                if (_retrig.Tick()) { _retrig.BeginStroke(); for (int rt = 0; rt < _retrig.Count; rt++) NoteOn(_retrig.NoteAt(rt), _retrig.VelocityAt(rt)); _retrig.EndStroke(); }

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

        public static readonly string[] PresetNames = { "Banjo bluegrass", "Banjo old-time (doux)", "Banjo punchy (bright)" };
        static readonly double[,] PresetValues = {
            //           sus  br   tw   dh   plkL atk  rel  poly vol
            /*BG*/      { 0.55, 0.75, 0.60, 0.55, 5, 1.5, 250, 5, -3 },
            /*OldTime*/ { 0.65, 0.55, 0.40, 0.45, 8, 3,   400, 5, -3 },
            /*Punchy*/  { 0.50, 0.90, 0.80, 0.60, 3, 1,   180, 5, -3 },
        };
        public void LoadPreset(int i)
        {
            if (i < 0 || i >= PresetValues.GetLength(0)) return;
            _sustain.Value = PresetValues[i, 0]; _brightness.Value = PresetValues[i, 1];
            _twang.Value = PresetValues[i, 2]; _drumHead.Value = PresetValues[i, 3];
            _pluckLen.Value = PresetValues[i, 4]; _attack.Value = PresetValues[i, 5];
            _release.Value = PresetValues[i, 6]; _polyphony.Value = PresetValues[i, 7];
            _volumeDb.Value = PresetValues[i, 8];
        }
    }

    internal struct BanjoParams
    {
        public float Sustain, Brightness, Twang, DrumHead, PluckLenMs;
        public float AttackMs, ReleaseMs;
    }
}
