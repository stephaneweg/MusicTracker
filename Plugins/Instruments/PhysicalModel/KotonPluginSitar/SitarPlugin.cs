using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Windows.Controls;
using KotonStudio.Library;

namespace KotonPluginSitar
{
    /// <summary>
    /// Sitar — Karplus-Strong etendu avec les 2 signatures caracteristiques :
    ///
    /// 1. **JAWARI** (bridge courbe) : la corde touche legerement le bridge quand elle vibre,
    ///    creant un buzz metallique caracteristique. Modelise par un allpass filter cascade
    ///    (chirp de phase) dans la boucle KS + soft-clip tanh doux → distorsion des harmoniques
    ///    qui donne le shimmer typique.
    ///
    /// 2. **CORDES SYMPATHIQUES** (tarab) : 11-13 cordes en resonance sous la table qui
    ///    bourdonnent naturellement quand une note principale est jouee. Simulees par 5 delay
    ///    lines auxiliaires accordees sur des degres de la gamme (Sa Ma Pa + octaves) avec
    ///    excitation croisee → drone qui vit sous chaque note.
    ///
    /// Timbre : brillant, harmoniques riches, sustain long, "shimmer" 1-3 kHz caracteristique.
    /// Ideal pour : ragas, meditation, evocation Inde, drone spirituel.
    /// </summary>
    [KotonInstrument("Sitar", Id = "koton.sitar", Category = "Physical Model", Version = "1.0", Vendor = "Koton Studio")]
    public sealed class SitarPlugin : IKotonInstrument
    {
        public string Id => "koton.sitar";
        public string DisplayName => "Sitar";

        readonly KotonParameter _sustain       = new KotonParameter("sustain",      "Sustain",       0.0, 1.0, 0.90);
        readonly KotonParameter _jawari        = new KotonParameter("jawari",       "Jawari (buzz)", 0.0, 1.0, 0.55);
        readonly KotonParameter _brightness    = new KotonParameter("brightness",   "Brightness",    0.0, 1.0, 0.65);
        readonly KotonParameter _sympathyLevel = new KotonParameter("symp_level",   "Sympathy",      0.0, 1.0, 0.45);
        readonly KotonParameter _sympathyDecay = new KotonParameter("symp_decay",   "Symp decay",    0.0, 1.0, 0.85);
        readonly KotonParameter _pluckLength   = new KotonParameter("pluck_length", "Pluck length",  3.0, 40.0, 15.0, "ms");

        readonly KotonParameter _vibratoRate   = new KotonParameter("vib_rate",     "Vibrato rate",  0.0, 8.0, 4.0, "Hz");
        readonly KotonParameter _vibratoDepth  = new KotonParameter("vib_depth",    "Vibrato depth", 0.0, 60.0, 20.0, "cent");
        readonly KotonParameter _attack        = new KotonParameter("attack",       "Attack",        1.0, 100.0, 4.0, "ms");
        readonly KotonParameter _release       = new KotonParameter("release",      "Release",       50.0, 3000.0, 1000.0, "ms");

        readonly KotonParameter _polyphony     = new KotonParameter("polyphony",    "Polyphony",     1, 6, 4) { Automatable = false };
        readonly KotonParameter _volumeDb      = new KotonParameter("volume",       "Volume",        -30.0, 6.0, -3.0, "dB");
        // Ré-attaque périodique : rejoue la note tenue tous les 1/taux de seconde.
        // 0 Hz = une seule attaque, donc aucun projet existant ne change.
        readonly KotonStudio.Plugins.Shared.KotonReAttack _retrig =
            new KotonStudio.Plugins.Shared.KotonReAttack("Trémolo", 20.0, 0.0);

        readonly List<KotonParameter> _params;
        public IReadOnlyList<KotonParameter> Parameters => _params;

        int _sr;
        SitarVoice[] _voices;
        int _stealCursor;
        const int MaxPoly = 6;

        public SitarPlugin()
        {
            _params = new List<KotonParameter> {
                _sustain, _jawari, _brightness, _sympathyLevel, _sympathyDecay, _pluckLength,
                _vibratoRate, _vibratoDepth, _attack, _release, _polyphony, _volumeDb, _retrig.Rate };
        }
        public bool HasEditor => true;
        public UserControl CreateEditor() => new SitarEditor(this);

        public void Prepare(int sampleRate, int maxBlockSize)
        {
            _sr = sampleRate;
            _voices = new SitarVoice[MaxPoly];
            for (int i = 0; i < MaxPoly; i++) _voices[i] = new SitarVoice(sampleRate);
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
            float vel = velocity / 127f;
            int poly = Math.Max(1, Math.Min(MaxPoly, (int)Math.Round(_polyphony.Value)));
            SitarVoice t = null;
            // Rejouer la MÊME note reprend sa voix au lieu d'en allouer une neuve : sans ça les coups
            // répétés s'empilent (mesure : pic 0,33 → 0,68 à 9 coups/s). C'est aussi le comportement
            // physique — repincer une corde déjà en vibration l'arrête.
            for (int i = 0; i < poly; i++) if (_voices[i].IsActive && _voices[i].Note == note) { t = _voices[i]; t.Kill(); break; }
            if (t == null) for (int i = 0; i < poly; i++) if (!_voices[i].IsActive) { t = _voices[i]; break; }
            if (t == null) { t = _voices[_stealCursor]; _stealCursor = (_stealCursor + 1) % poly; t.Kill(); }
            t.NoteOn(note, vel, Build());
        }
        public void NoteOff(int note, int sampleOffset = 0)
        {
            _retrig.NoteOff(note);
            if (_voices == null) return;
            for (int i = 0; i < _voices.Length; i++)
                if (_voices[i].IsActive && _voices[i].Note == note) _voices[i].NoteOff();
        }
        public void MidiCC(int cc, int value, int sampleOffset = 0) { if (cc == 123) Reset(); }
        public void SetPitchBend(float value, int sampleOffset = 0) { }

        SitarParams Build() => new SitarParams
        {
            Sustain = (float)_sustain.Value, Jawari = (float)_jawari.Value, Brightness = (float)_brightness.Value,
            SympathyLevel = (float)_sympathyLevel.Value, SympathyDecay = (float)_sympathyDecay.Value,
            PluckLengthMs = (float)_pluckLength.Value,
            VibRate = (float)_vibratoRate.Value, VibDepthCents = (float)_vibratoDepth.Value,
            AttackMs = (float)_attack.Value, ReleaseMs = (float)_release.Value,
        };

        public void Render(Span<float> left, Span<float> right)
        {
            if (_voices == null) { left.Clear(); right.Clear(); return; }
            float volLin = (float)Math.Pow(10.0, _volumeDb.Value / 20.0);
            var p = Build();
            int n = left.Length;
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

        public static readonly string[] PresetNames = { "Sitar classique (raga)", "Sitar buzz+++ (jawari fort)", "Sitar drone (sustain infini)" };
        static readonly double[,] PresetValues = {
            //           sus jaw br  spL spD pkL vibR vibD atk rel poly vol
            /*Classic*/ { 0.90, 0.55, 0.65, 0.45, 0.85, 15, 4.0, 20, 4, 1000, 4, -3 },
            /*Buzz*/    { 0.88, 0.85, 0.75, 0.35, 0.85, 10, 3.5, 25, 4, 900, 4, -3 },
            /*Drone*/   { 0.98, 0.35, 0.55, 0.75, 0.95, 20, 3.0, 15, 5, 2500, 5, -4 },
        };
        public void LoadPreset(int i)
        {
            if (i < 0 || i >= PresetValues.GetLength(0)) return;
            _sustain.Value = PresetValues[i, 0]; _jawari.Value = PresetValues[i, 1];
            _brightness.Value = PresetValues[i, 2]; _sympathyLevel.Value = PresetValues[i, 3];
            _sympathyDecay.Value = PresetValues[i, 4]; _pluckLength.Value = PresetValues[i, 5];
            _vibratoRate.Value = PresetValues[i, 6]; _vibratoDepth.Value = PresetValues[i, 7];
            _attack.Value = PresetValues[i, 8]; _release.Value = PresetValues[i, 9];
            _polyphony.Value = PresetValues[i, 10]; _volumeDb.Value = PresetValues[i, 11];
        }
    }

    internal struct SitarParams
    {
        public float Sustain, Jawari, Brightness;
        public float SympathyLevel, SympathyDecay;
        public float PluckLengthMs;
        public float VibRate, VibDepthCents;
        public float AttackMs, ReleaseMs;
    }
}
