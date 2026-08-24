using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Windows.Controls;
using KotonStudio.Library;

namespace KotonPluginErhu
{
    /// <summary>
    /// Erhu 二胡 — violon chinois a 2 cordes joue avec un archet entre les cordes. Timbre nasal
    /// mid-focused, tres expressif : vibrato profond (30-50 cents), portamento naturel entre
    /// notes (main sans manche fixe), formant ~1.7 kHz caracteristique du "chant nasillard".
    ///
    /// **DSP** : bowed Karplus-Strong avec excitation continue par sawtooth soft + petit bruit
    /// d'archet. Formant BP centre 1700 Hz (Q=3) qui donne le twang nasal. LEGATO natif comme
    /// le Guqin : nouvelle note pendant sustain/release = glide de la delay-length en log
    /// vers la nouvelle hauteur (direction naturelle respectee).
    /// </summary>
    [KotonInstrument("Erhu", Id = "koton.erhu", Category = "Physical Model", Version = "1.0", Vendor = "Koton Studio")]
    public sealed class ErhuPlugin : IKotonInstrument
    {
        public string Id => "koton.erhu";
        public string DisplayName => "Erhu";

        readonly KotonParameter _bowPressure = new KotonParameter("bow_pressure","Bow pressure", 0.0, 1.0, 0.55);
        readonly KotonParameter _bowNoise    = new KotonParameter("bow_noise",   "Bow noise",    0.0, 1.0, 0.12);
        readonly KotonParameter _formantHz   = new KotonParameter("formant_hz",  "Nasal formant", 800.0, 3000.0, 1700.0, "Hz");
        readonly KotonParameter _formantQ    = new KotonParameter("formant_q",   "Formant Q",    1.0, 12.0, 3.0);
        readonly KotonParameter _brightness  = new KotonParameter("brightness",  "Brightness",   0.0, 1.0, 0.55);
        readonly KotonParameter _glideMs     = new KotonParameter("glide_ms",    "Glide time",   0.0, 500.0, 90.0, "ms");
        readonly KotonParameter _vibRate     = new KotonParameter("vib_rate",    "Vibrato rate", 0.0, 8.0, 5.5, "Hz");
        readonly KotonParameter _vibDepth    = new KotonParameter("vib_depth",   "Vibrato depth",0.0, 80.0, 35.0, "cent");
        readonly KotonParameter _attack      = new KotonParameter("attack",      "Attack",       10.0, 500.0, 60.0, "ms");
        readonly KotonParameter _release     = new KotonParameter("release",     "Release",      50.0, 2000.0, 300.0, "ms");
        readonly KotonParameter _volumeDb    = new KotonParameter("volume",      "Volume",       -30.0, 6.0, -3.0, "dB");
        // Ré-attaque périodique. Modèle AUTO-OSCILLANT : on ne rejoue PAS la note (voir
        // KotonReAttack.ArticulationSec), on met en forme la sortie.
        readonly KotonStudio.Plugins.Shared.KotonReAttack _retrig =
            new KotonStudio.Plugins.Shared.KotonReAttack("Détaché", 14.0, 0.0) { ArticulationSec = 0.03f };

        readonly List<KotonParameter> _params;
        public IReadOnlyList<KotonParameter> Parameters => _params;

        int _sr;
        ErhuVoice _voice;

        public ErhuPlugin()
        {
            _params = new List<KotonParameter> { _bowPressure, _bowNoise, _formantHz, _formantQ, _brightness, _glideMs, _vibRate, _vibDepth, _attack, _release, _volumeDb, _retrig.Rate };
        }
        public bool HasEditor => true;
        public UserControl CreateEditor() => new ErhuEditor(this);
        public void Prepare(int sampleRate, int maxBlockSize)
        {
            _sr = sampleRate; _voice = new ErhuVoice(sampleRate);
            _retrig.Prepare(sampleRate);
        }
        public void Reset()
        {
            _voice?.Kill();
            _retrig.Reset();
        }
        public void NoteOn(int note, int velocity, int sampleOffset = 0)
        {
            _retrig.NoteOn(note, velocity);
            if (_voice == null || velocity == 0) return;
            var p = Build();
            if (_voice.IsActive) _voice.NoteOnLegato(note, velocity / 127f, p);
            else _voice.NoteOnBow(note, velocity / 127f, p);
        }
        public void NoteOff(int note, int sampleOffset = 0) { if (_voice != null && _voice.IsActive && _voice.Note == note) _voice.NoteOff(); }
        public void MidiCC(int cc, int value, int sampleOffset = 0) { if (cc == 123) Reset(); }
        public void SetPitchBend(float value, int sampleOffset = 0) { }

        ErhuParams Build() => new ErhuParams
        {
            BowPressure = (float)_bowPressure.Value, BowNoise = (float)_bowNoise.Value,
            FormantHz = (float)_formantHz.Value, FormantQ = (float)_formantQ.Value,
            Brightness = (float)_brightness.Value, GlideMs = (float)_glideMs.Value,
            VibRate = (float)_vibRate.Value, VibDepthCents = (float)_vibDepth.Value,
            AttackMs = (float)_attack.Value, ReleaseMs = (float)_release.Value,
        };

        public void Render(Span<float> left, Span<float> right)
        {
            if (_voice == null) { left.Clear(); right.Clear(); return; }
            float volLin = (float)Math.Pow(10.0, _volumeDb.Value / 20.0);
            var p = Build(); int n = left.Length;
            for (int i = 0; i < n; i++)
            {
                _retrig.Tick();
                float artG = _retrig.Gain;

                float s = _voice.IsActive ? _voice.RenderSample(p) * volLin : 0f;
                if (s > 1f) s = 1f; else if (s < -1f) s = -1f;
                left[i] = s; right[i] = s;
                            // Enveloppe d'articulation du coup de langue / détaché : la note continue de sonner
                // sous-jacente, c'est la SORTIE qu'on découpe.
                left[i] *= artG; right[i] *= artG;
            }
        }
        public byte[] SaveState() { try { var d = new Dictionary<string, double>(); foreach (var kp in _params) d[kp.Id] = kp.Value; return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(d)); } catch { return Array.Empty<byte>(); } }
        public void LoadState(byte[] state) { if (state == null || state.Length == 0) return; try { var d = JsonSerializer.Deserialize<Dictionary<string, double>>(Encoding.UTF8.GetString(state)); if (d == null) return; foreach (var kp in _params) if (d.TryGetValue(kp.Id, out var v)) kp.Value = v; } catch { } }
        public void Dispose() { }
        public void SetParam(string id, double value) { foreach (var kp in _params) if (kp.Id == id) { kp.Value = value; return; } }

        public static readonly string[] PresetNames = { "Erhu classique", "Erhu doux (lyrique)", "Erhu punchy (traditionnel)" };
        static readonly double[,] PresetValues = {
            //           bpr bnz fHz  fQ  br  gld vibR vibD atk  rel  vol
            /*Classic*/ { 0.55, 0.12, 1700, 3.0, 0.55, 90,  5.5, 35, 60, 300, -3 },
            /*Doux*/    { 0.40, 0.08, 1500, 2.5, 0.45, 130, 5.0, 45, 120, 500, -3 },
            /*Punchy*/  { 0.75, 0.20, 1900, 4.0, 0.70, 60,  6.0, 25, 30, 200, -3 },
        };
        public void LoadPreset(int i)
        {
            if (i < 0 || i >= PresetValues.GetLength(0)) return;
            _bowPressure.Value = PresetValues[i, 0]; _bowNoise.Value = PresetValues[i, 1];
            _formantHz.Value = PresetValues[i, 2]; _formantQ.Value = PresetValues[i, 3];
            _brightness.Value = PresetValues[i, 4]; _glideMs.Value = PresetValues[i, 5];
            _vibRate.Value = PresetValues[i, 6]; _vibDepth.Value = PresetValues[i, 7];
            _attack.Value = PresetValues[i, 8]; _release.Value = PresetValues[i, 9];
            _volumeDb.Value = PresetValues[i, 10];
        }
    }

    internal struct ErhuParams
    {
        public float BowPressure, BowNoise, FormantHz, FormantQ, Brightness;
        public float GlideMs;
        public float VibRate, VibDepthCents;
        public float AttackMs, ReleaseMs;
    }
}
