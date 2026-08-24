using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Windows.Controls;
using KotonStudio.Library;

namespace KotonPluginOceanReverb
{
    /// <summary>
    /// Ocean Reverb — reverb algorithmique moderne "atmosphérique" avec 3 modes signatures :
    /// Abyss (shimmer +12 dans le feedback), Tide (LP variable modulé par LFO lent = vagues),
    /// Foam (diffusion accrue + attaque adoucie = flottant). Freeze pour queue infinie, ducking
    /// intégré pour garder la voix lisible, pré-delay 0..500 ms.
    ///
    /// **Ce n'est pas une reverb "salle"** (concert hall / plate / spring) — pour ça, un
    /// KotonPluginRoomReverb dédié serait plus adapté. Ocean est calibrée pour du cinématique,
    /// ambient, dark ambient, drone, pad shoegaze. Décays 3..30 s, textures brumeuses.
    ///
    /// **Comparaisons** : Valhalla VintageVerb / Blackhole, Baby Audio Crystalline, Klevgrand
    /// Skydust, Eventide Blackhole, Steinberg Sail. Cette famille "atmospheric reverb" est un
    /// standard depuis ~2015 — Ocean en est l'équivalent Koton natif.
    /// </summary>
    [KotonEffect("Ocean Reverb", Id = "koton.oceanreverb", Category = "Reverb", Version = "1.0", Vendor = "Koton Studio")]
    public sealed class OceanReverbPlugin : IKotonEffect
    {
        public string Id => "koton.oceanreverb";
        public string DisplayName => "Ocean Reverb";

        // =============================================================================================
        // Paramètres
        // =============================================================================================
        readonly KotonParameter _mode        = new KotonParameter("mode",        "Mode",        0, 2, 0) { Automatable = false };   // 0=Abyss, 1=Tide, 2=Foam
        readonly KotonParameter _size        = new KotonParameter("size",        "Size",        0.0, 1.0, 0.60);
        readonly KotonParameter _decay       = new KotonParameter("decay",       "Decay",       0.0, 1.0, 0.75);
        readonly KotonParameter _brightness  = new KotonParameter("brightness",  "Brightness",  0.0, 1.0, 0.55);
        readonly KotonParameter _movement    = new KotonParameter("movement",    "Movement",    0.0, 1.0, 0.50);
        readonly KotonParameter _preDelayMs  = new KotonParameter("pre_delay",   "Pre-delay",   0.0, 500.0, 20.0, "ms");
        readonly KotonParameter _hpFilter    = new KotonParameter("hp_filter",   "HP filter",   20.0, 1000.0, 80.0, "Hz");
        readonly KotonParameter _duckDepth   = new KotonParameter("duck_depth",  "Ducking",     0.0, 1.0, 0.0);
        readonly KotonParameter _stereoWidth = new KotonParameter("stereo_width","Stereo width",0.0, 1.0, 0.85);
        readonly KotonParameter _freeze      = new KotonParameter("freeze",      "Freeze",      0, 1, 0);
        readonly KotonParameter _mix         = new KotonParameter("mix",         "Mix",         0.0, 1.0, 0.35);
        readonly KotonParameter _outGainDb   = new KotonParameter("out_gain",    "Output",      -30.0, 6.0, 0.0, "dB");

        readonly List<KotonParameter> _params;
        public IReadOnlyList<KotonParameter> Parameters => _params;

        int _sampleRate;
        int _maxBlockSize;
        OceanReverbCore _core;

        public OceanReverbPlugin()
        {
            _params = new List<KotonParameter>
            {
                _mode, _size, _decay, _brightness, _movement,
                _preDelayMs, _hpFilter, _duckDepth, _stereoWidth,
                _freeze, _mix, _outGainDb,
            };
        }

        public bool HasEditor => true;
        public UserControl CreateEditor() => new OceanReverbEditor(this);

        // =============================================================================================
        // Cycle
        // =============================================================================================
        public void Prepare(int sampleRate, int maxBlockSize)
        {
            _sampleRate = sampleRate;
            _maxBlockSize = maxBlockSize;
            _core = new OceanReverbCore(sampleRate);
        }

        public void Reset()
        {
            _core?.Reset();
        }

        public void Process(Span<float> left, Span<float> right)
        {
            if (_core == null) return;

            var p = new OceanParams
            {
                Mode        = Math.Max(0, Math.Min(2, (int)_mode.Value)),
                Size        = (float)_size.Value,
                Decay       = (float)_decay.Value,
                Brightness  = (float)_brightness.Value,
                Movement    = (float)_movement.Value,
                PreDelayMs  = (float)_preDelayMs.Value,
                HpFilterHz  = (float)_hpFilter.Value,
                DuckDepth   = (float)_duckDepth.Value,
                StereoWidth = (float)_stereoWidth.Value,
                Freeze      = _freeze.Value >= 0.5,
                Mix         = (float)_mix.Value,
                OutGainDb   = (float)_outGainDb.Value,
            };
            _core.Process(left, right, p);
        }

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

        // =============================================================================================
        // Presets
        // =============================================================================================
        public static readonly string[] PresetNames =
        {
            "Ocean (defaut)", "Deep Abyss", "Coastal Tide", "Foam Wash",
            "Church Ambient", "Underwater Freeze", "Cinematic Wide",
        };

        static readonly double[,] PresetValues =
        {
            //          mode size decay bright mov  preD  hp   duck width freez mix  out
            /*Ocean*/   { 2,  0.60, 0.75, 0.55, 0.50, 20,   80,  0.0, 0.85, 0,   0.35, 0.0 },
            /*Abyss*/   { 0,  0.85, 0.90, 0.35, 0.85, 40,  120,  0.0, 0.95, 0,   0.55, -2.0 },
            /*Tide*/    { 1,  0.70, 0.85, 0.60, 0.70, 30,  100,  0.0, 0.90, 0,   0.45, -1.0 },
            /*Foam*/    { 2,  0.55, 0.70, 0.65, 0.40, 80,   90,  0.0, 0.80, 0,   0.50, 0.0 },
            /*Church*/  { 2,  0.90, 0.85, 0.75, 0.30, 30,   60,  0.0, 0.85, 0,   0.40, 0.0 },
            /*Under*/   { 1,  0.75, 0.80, 0.25, 0.90, 15,  200,  0.0, 0.90, 1,   0.85, -3.0 },
            /*Cinema*/  { 0,  0.95, 0.85, 0.50, 0.75, 100, 100,  0.15, 1.0, 0,   0.50, -1.0 },
        };

        public void LoadPreset(int index, bool keepMix)
        {
            if (index < 0 || index >= PresetValues.GetLength(0)) return;
            double keptMix = _mix.Value;
            _mode.Value       = PresetValues[index, 0];
            _size.Value       = PresetValues[index, 1];
            _decay.Value      = PresetValues[index, 2];
            _brightness.Value = PresetValues[index, 3];
            _movement.Value   = PresetValues[index, 4];
            _preDelayMs.Value = PresetValues[index, 5];
            _hpFilter.Value   = PresetValues[index, 6];
            _duckDepth.Value  = PresetValues[index, 7];
            _stereoWidth.Value= PresetValues[index, 8];
            _freeze.Value     = PresetValues[index, 9];
            _mix.Value        = keepMix ? keptMix : PresetValues[index, 10];
            _outGainDb.Value  = PresetValues[index, 11];
        }

        public void SetParam(string id, double value)
        {
            foreach (var kp in _params)
                if (kp.Id == id) { kp.Value = value; return; }
        }
    }
}
