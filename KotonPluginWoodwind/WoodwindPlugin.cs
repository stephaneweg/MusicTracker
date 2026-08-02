using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Windows.Controls;
using KotonStudio.Library;

namespace KotonPluginWoodwind
{
    /// <summary>
    /// Woodwind — synthèse par modèle physique de bois : flûte, clarinette, hautbois, basson,
    /// sax alto, sax ténor, piccolo, cor anglais. Cinquième plugin de modélisation physique.
    ///
    /// **Algorithme** : cousin du Brass (tube + non-linéarité + injection continue) mais avec
    /// deux modes d'excitation au choix, correspondant aux deux familles de bois :
    /// - <b>Anche</b> (clarinette, hautbois, sax, basson) : non-linéarité asymétrique qui compresse
    ///   fort à la fermeture (l'anche ferme sous forte pression) et laisse passer plus doucement
    ///   à l'ouverture. Produit un timbre riche en harmoniques impaires (clarinette) ou paires+impaires
    ///   selon la conicité du tube.
    /// - <b>Jet d'air</b> (flûte, piccolo) : non-linéarité symétrique douce + beaucoup de bruit
    ///   d'excitation (le souffle audible caractéristique de la flûte). Timbre plus pur, transitoires
    ///   plus doux.
    ///
    /// **Reed softness** contrôle la dureté de l'anche : dure = attaque nette (sax fort), molle =
    /// timbre chaleureux (clarinette douce). Sur un jet d'air, contrôle le "focus" du jet.
    ///
    /// **Bore size** = LP en sortie modélisant le diamètre relatif du tube : petit bore = clair
    /// et perçant (piccolo, hautbois), gros bore = arrondi (basson, sax baryton).
    /// </summary>
    [KotonInstrument("Woodwind", Id = "koton.woodwind", Category = "Physical Model", Version = "1.0", Vendor = "Koton Studio")]
    public sealed class WoodwindPlugin : IKotonInstrument
    {
        public string Id => "koton.woodwind";
        public string DisplayName => "Woodwind";

        readonly KotonParameter _instrument     = new KotonParameter("instrument",       "Instrument",       0, 7, 0);
        readonly KotonParameter _airPressure    = new KotonParameter("air_pressure",     "Air pressure",     0.0, 1.0, 0.45);
        readonly KotonParameter _breathNoise    = new KotonParameter("breath_noise",     "Breath noise",     0.0, 1.0, 0.30);
        readonly KotonParameter _excitationType = new KotonParameter("excitation_type",  "Excitation",       0, 1, 0);   // 0=anche, 1=jet
        readonly KotonParameter _reedSoftness   = new KotonParameter("reed_softness",    "Reed softness",    0.0, 1.0, 0.50);
        readonly KotonParameter _damping        = new KotonParameter("damping",          "Damping",          0.0, 1.0, 0.35);
        readonly KotonParameter _brightness     = new KotonParameter("brightness",       "Brightness",       0.0, 1.0, 0.50);
        readonly KotonParameter _boreSize       = new KotonParameter("bore_size",        "Bore size",        0.0, 1.0, 0.45);
        readonly KotonParameter _vibratoRate    = new KotonParameter("vibrato_rate",     "Vibrato rate",     0.0, 8.0, 5.0, "Hz");
        readonly KotonParameter _vibratoDepth   = new KotonParameter("vibrato_depth",    "Vibrato depth",    0.0, 30.0, 5.0, "ct");
        readonly KotonParameter _attackTime     = new KotonParameter("attack_time",      "Attack",           0.0, 1.5, 0.15, "s");
        readonly KotonParameter _releaseTime    = new KotonParameter("release_time",     "Release",          0.0, 1.5, 0.20, "s");
        readonly KotonParameter _stereoWidth    = new KotonParameter("stereo_width",     "Stereo width",     0.0, 1.0, 0.25);
        readonly KotonParameter _volumeDb       = new KotonParameter("volume",           "Volume",           -30.0, 6.0, -6.0, "dB");

        readonly List<KotonParameter> _params;
        public IReadOnlyList<KotonParameter> Parameters => _params;

        int _sampleRate;
        int _maxBlockSize;
        WoodwindVoice[] _voices;
        int _stealCursor;
        const int Polyphony = 8;

        public WoodwindPlugin()
        {
            _params = new List<KotonParameter>
            {
                _instrument, _airPressure, _breathNoise, _excitationType, _reedSoftness,
                _damping, _brightness, _boreSize,
                _vibratoRate, _vibratoDepth, _attackTime, _releaseTime,
                _stereoWidth, _volumeDb,
            };
        }

        public bool HasEditor => true;
        public UserControl CreateEditor() => new WoodwindEditor(this);

        // =============================================================================================
        // Presets
        // =============================================================================================
        public static readonly string[] InstrumentNames =
        {
            "Flute", "Clarinette", "Hautbois", "Basson",
            "Sax alto", "Sax tenor", "Piccolo", "Cor anglais",
        };

        static readonly double[,] PresetValues =
        {
            //          air  brNoi excType reed damp brgh bore vibR vibD attk rel  width vol
            /*Flute*/   { 0.40, 0.55, 1,    0.70, 0.30, 0.40, 0.45, 5.0, 4.0, 0.20, 0.20, 0.20, -6.0 },
            /*Clari*/   { 0.45, 0.20, 0,    0.50, 0.35, 0.55, 0.55, 4.5, 5.0, 0.12, 0.20, 0.25, -6.0 },
            /*Hautb*/   { 0.50, 0.15, 0,    0.30, 0.40, 0.70, 0.30, 5.0, 6.0, 0.10, 0.15, 0.20, -6.0 },
            /*Basson*/  { 0.55, 0.15, 0,    0.45, 0.45, 0.35, 0.75, 4.0, 4.0, 0.15, 0.25, 0.15, -5.0 },
            /*SaxA*/    { 0.55, 0.20, 0,    0.35, 0.35, 0.60, 0.50, 5.0, 8.0, 0.10, 0.20, 0.20, -5.0 },
            /*SaxT*/    { 0.60, 0.22, 0,    0.40, 0.40, 0.55, 0.60, 4.5, 8.0, 0.12, 0.25, 0.20, -5.0 },
            /*Piccolo*/ { 0.45, 0.60, 1,    0.75, 0.25, 0.55, 0.20, 5.5, 4.0, 0.18, 0.15, 0.20, -8.0 },
            /*Cor ang*/ { 0.50, 0.15, 0,    0.35, 0.40, 0.55, 0.50, 4.5, 6.0, 0.15, 0.20, 0.20, -6.0 },
        };

        public void ApplyInstrumentDefaults(int index)
        {
            if (index < 0 || index >= PresetValues.GetLength(0)) return;
            _airPressure.Value    = PresetValues[index, 0];
            _breathNoise.Value    = PresetValues[index, 1];
            _excitationType.Value = PresetValues[index, 2];
            _reedSoftness.Value   = PresetValues[index, 3];
            _damping.Value        = PresetValues[index, 4];
            _brightness.Value     = PresetValues[index, 5];
            _boreSize.Value       = PresetValues[index, 6];
            _vibratoRate.Value    = PresetValues[index, 7];
            _vibratoDepth.Value   = PresetValues[index, 8];
            _attackTime.Value     = PresetValues[index, 9];
            _releaseTime.Value    = PresetValues[index, 10];
            _stereoWidth.Value    = PresetValues[index, 11];
            _volumeDb.Value       = PresetValues[index, 12];
        }

        // =============================================================================================
        // Cycle
        // =============================================================================================
        public void Prepare(int sampleRate, int maxBlockSize)
        {
            _sampleRate = sampleRate;
            _maxBlockSize = maxBlockSize;
            _voices = new WoodwindVoice[Polyphony];
            for (int i = 0; i < Polyphony; i++) _voices[i] = new WoodwindVoice(sampleRate);
        }

        public void Reset()
        {
            if (_voices != null) foreach (var v in _voices) v.Kill();
        }

        // =============================================================================================
        // MIDI
        // =============================================================================================
        public void NoteOn(int note, int velocity, int sampleOffset = 0)
        {
            if (_voices == null || velocity == 0) return;
            var p = ToVoiceParams();
            float vel = velocity / 127f;

            WoodwindVoice target = null;
            for (int i = 0; i < _voices.Length; i++)
                if (_voices[i].IsActive && _voices[i].Note == note) { target = _voices[i]; break; }
            if (target == null)
                for (int i = 0; i < _voices.Length; i++) if (!_voices[i].IsActive) { target = _voices[i]; break; }
            if (target == null)
            {
                target = _voices[_stealCursor];
                _stealCursor = (_stealCursor + 1) % _voices.Length;
                target.Kill();
            }
            target.NoteOn(note, vel, p);
        }

        public void NoteOff(int note, int sampleOffset = 0)
        {
            if (_voices == null) return;
            for (int i = 0; i < _voices.Length; i++)
                if (_voices[i].IsActive && _voices[i].Note == note)
                    _voices[i].NoteOff();
        }

        public void MidiCC(int cc, int value, int sampleOffset = 0)
        {
            if (cc == 123) Reset();
        }

        public void SetPitchBend(float value, int sampleOffset = 0) { }

        // =============================================================================================
        // Render
        // =============================================================================================
        public void Render(Span<float> left, Span<float> right)
        {
            if (_voices == null) { left.Clear(); right.Clear(); return; }

            var p = ToVoiceParams();
            float volLin = (float)Math.Pow(10.0, p.VolumeDb / 20.0);
            float width = (float)_stereoWidth.Value;

            int n = left.Length;
            for (int i = 0; i < n; i++)
            {
                float sumL = 0f, sumR = 0f;
                for (int v = 0; v < _voices.Length; v++)
                {
                    var voice = _voices[v];
                    if (!voice.IsActive) continue;
                    float s = voice.RenderSample(p);
                    float noteNorm = (voice.Note - 60) / 24f;
                    if (noteNorm < -1f) noteNorm = -1f; else if (noteNorm > 1f) noteNorm = 1f;
                    float p01 = 0.5f + noteNorm * width * 0.5f;
                    sumL += s * (1f - p01);
                    sumR += s * p01;
                }
                left[i] = sumL * volLin;
                right[i] = sumR * volLin;
            }
        }

        WwParams ToVoiceParams() => new WwParams
        {
            AirPressure       = (float)_airPressure.Value,
            BreathNoise       = (float)_breathNoise.Value,
            ExcitationType    = (float)_excitationType.Value,
            ReedSoftness      = (float)_reedSoftness.Value,
            Damping           = (float)_damping.Value,
            Brightness        = (float)_brightness.Value,
            BoreSize          = (float)_boreSize.Value,
            VibratoRateHz     = (float)_vibratoRate.Value,
            VibratoDepthCents = (float)_vibratoDepth.Value,
            AttackSec         = (float)_attackTime.Value,
            ReleaseSec        = (float)_releaseTime.Value,
            VolumeDb          = (float)_volumeDb.Value,
        };

        // =============================================================================================
        // Persistance
        // =============================================================================================
        public byte[] SaveState()
        {
            try
            {
                var dict = new Dictionary<string, double>();
                foreach (var kp in _params) dict[kp.Id] = kp.Value;
                return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(dict));
            }
            catch { return Array.Empty<byte>(); }
        }

        public void LoadState(byte[] state)
        {
            if (state == null || state.Length == 0) return;
            try
            {
                var dict = JsonSerializer.Deserialize<Dictionary<string, double>>(Encoding.UTF8.GetString(state));
                if (dict == null) return;
                foreach (var kp in _params)
                    if (dict.TryGetValue(kp.Id, out var v)) kp.Value = v;
            }
            catch { }
        }

        public void Dispose() { }

        public void SetParam(string id, double value)
        {
            foreach (var kp in _params)
                if (kp.Id == id) { kp.Value = value; return; }
        }
    }
}
