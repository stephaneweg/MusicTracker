using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Windows.Controls;
using KotonStudio.Library;

namespace KotonPluginCaveReverb
{
    /// <summary>
    /// Cave Reverb — reverb monumentale sombre pour ambiances gothiques/dark ambient/temple.
    /// Le troisième effet ambiance de Koton, complémentaire d'Ocean et Forest :
    ///
    /// - **Ocean** : espace ouvert, brumeux, en mouvement (shimmer/tide/foam)
    /// - **Forest** : espace fragmenté, court et mate, texture organique (rustle)
    /// - **Cave** : espace ENFERMÉ énorme, queue TRÈS LONGUE (5-30s), résonance sous-basse
    ///   caractéristique (le "boom" à ~80 Hz), drips métalliques aléatoires optionnels
    ///
    /// **Signatures techniques uniques** :
    /// - Peak biquad à 80 Hz dans le feedback (LowBoom) = résonance sous-basse persistante
    /// - Drip generator (événements Poisson) = gouttes métalliques aléatoires injectées dans la
    ///   reverb pour résonner. Densité DripAmount, pitch aléatoire autour de DripPitch.
    /// - Lignes FDN longues (100-400 ms base × Size) = queue véritablement monumentale
    ///
    /// **Combos** : super avec Waterdrops (les drips synchros avec les vraies gouttes = illusion
    /// parfaite de grotte), avec Handpan (l'espace amplifie la sympathie), avec Blown Flute (le
    /// ney/shakuhachi dans une cave = son méditatif ultime).
    /// </summary>
    [KotonEffect("Cave Reverb", Id = "koton.cavereverb", Category = "Reverb", Version = "1.0", Vendor = "Koton Studio")]
    public sealed class CavePlugin : IKotonEffect
    {
        public string Id => "koton.cavereverb";
        public string DisplayName => "Cave Reverb";

        readonly KotonParameter _size       = new KotonParameter("size",         "Size",         0.0, 1.0, 0.70);
        readonly KotonParameter _decay      = new KotonParameter("decay",        "Decay",        0.0, 1.0, 0.80);
        readonly KotonParameter _lowBoom    = new KotonParameter("low_boom",     "Low boom",     0.0, 1.0, 0.55);
        readonly KotonParameter _darkness   = new KotonParameter("darkness",     "Darkness",     0.0, 1.0, 0.65);
        readonly KotonParameter _preDelay   = new KotonParameter("pre_delay",    "Pre-delay",    0.0, 300.0, 60.0, "ms");
        readonly KotonParameter _dripAmount = new KotonParameter("drip_amount",  "Drips (densité)", 0.0, 1.0, 0.15);
        readonly KotonParameter _dripPitch  = new KotonParameter("drip_pitch",   "Drip pitch",   0.0, 1.0, 0.50);
        readonly KotonParameter _dripVolume = new KotonParameter("drip_volume",  "Drip volume",  0.0, 2.0, 1.00);
        readonly KotonParameter _stereoWidth= new KotonParameter("stereo_width", "Stereo width", 0.0, 1.0, 0.95);
        readonly KotonParameter _mix        = new KotonParameter("mix",          "Mix",          0.0, 1.0, 0.45);
        readonly KotonParameter _outGain    = new KotonParameter("out_gain",     "Output",       -30.0, 6.0, -2.0, "dB");

        readonly List<KotonParameter> _params;
        public IReadOnlyList<KotonParameter> Parameters => _params;

        int _sampleRate;
        int _maxBlockSize;
        CaveCore _core;

        public CavePlugin()
        {
            _params = new List<KotonParameter>
            {
                _size, _decay, _lowBoom, _darkness, _preDelay,
                _dripAmount, _dripPitch, _dripVolume, _stereoWidth, _mix, _outGain,
            };
        }

        public bool HasEditor => true;
        public UserControl CreateEditor() => new CaveEditor(this);

        public void Prepare(int sampleRate, int maxBlockSize)
        {
            _sampleRate = sampleRate;
            _maxBlockSize = maxBlockSize;
            _core = new CaveCore(sampleRate);
        }
        public void Reset() => _core?.Reset();
        public void Process(Span<float> left, Span<float> right)
        {
            if (_core == null) return;
            var p = new CaveParams
            {
                Size = (float)_size.Value, Decay = (float)_decay.Value,
                LowBoom = (float)_lowBoom.Value, Darkness = (float)_darkness.Value,
                PreDelayMs = (float)_preDelay.Value,
                DripAmount = (float)_dripAmount.Value, DripPitch = (float)_dripPitch.Value,
                DripVolume = (float)_dripVolume.Value,
                StereoWidth = (float)_stereoWidth.Value, Mix = (float)_mix.Value,
                OutGainDb = (float)_outGain.Value,
            };
            _core.Process(left, right, p);
        }

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
                foreach (var kp in _params) if (dict.TryGetValue(kp.Id, out var v)) kp.Value = v;
            }
            catch { }
        }
        public void Dispose() { }

        // Presets
        public static readonly string[] PresetNames = { "Petite grotte", "Cathedrale de pierre", "Cave humide (drips)", "Temple gothique", "Mine profonde", "Cave freezing (drone)" };
        static readonly double[,] PresetValues = {
            //          size decay boom dark preD drip dripP wid mix  out
            /*Petite*/  { 0.35, 0.55, 0.30, 0.45, 25,  0.10, 0.50, 0.85, 0.35, -2.0 },
            /*Cathed*/  { 0.90, 0.92, 0.65, 0.55, 90,  0.05, 0.30, 0.95, 0.45, -3.0 },
            /*Humide*/  { 0.65, 0.75, 0.50, 0.65, 50,  0.55, 0.65, 0.90, 0.50, -2.0 },
            /*Temple*/  { 0.80, 0.90, 0.75, 0.75, 80,  0.20, 0.35, 0.90, 0.55, -3.0 },
            /*Mine*/    { 0.55, 0.70, 0.85, 0.85, 40,  0.35, 0.55, 0.85, 0.55, -1.0 },
            /*Freeze*/  { 1.00, 0.98, 0.70, 0.70,150,  0.30, 0.75, 1.00, 0.75, -4.0 },
        };
        public void LoadPreset(int index, bool keepMix)
        {
            if (index < 0 || index >= PresetValues.GetLength(0)) return;
            double keptMix = _mix.Value;
            _size.Value        = PresetValues[index, 0];
            _decay.Value       = PresetValues[index, 1];
            _lowBoom.Value     = PresetValues[index, 2];
            _darkness.Value    = PresetValues[index, 3];
            _preDelay.Value    = PresetValues[index, 4];
            _dripAmount.Value  = PresetValues[index, 5];
            _dripPitch.Value   = PresetValues[index, 6];
            _stereoWidth.Value = PresetValues[index, 7];
            _mix.Value         = keepMix ? keptMix : PresetValues[index, 8];
            _outGain.Value     = PresetValues[index, 9];
        }
        public void SetParam(string id, double value)
        {
            foreach (var kp in _params) if (kp.Id == id) { kp.Value = value; return; }
        }
    }
}
