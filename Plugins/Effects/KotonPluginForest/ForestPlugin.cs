using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Windows.Controls;
using KotonStudio.Library;

namespace KotonPluginForest
{
    /// <summary>
    /// Forest Ambience — effet de reverb "environnement forestier" plutôt qu'une reverb pure.
    /// Complémentaire d'Ocean Reverb : là où Ocean est un grand espace ouvert brumeux à queue longue,
    /// Forest est un espace fragmenté à queue courte et mate, avec une texture organique ajoutée
    /// (rustle = bruit de feuilles au vent + micro-taps décorrélés = réflexions sur troncs).
    ///
    /// **Usage typique** : nappes ambient forestières, pistes d'ambiance de jeu vidéo/film, textures
    /// naturelles sur voix ou instruments doux (marimba/mallets marchent super bien dans Forest).
    /// Moins pertinent sur de la batterie ou du synthé lead (Ocean est plus adapté à ça).
    ///
    /// **Différence avec un reverb sombre standard** : combine 3 signatures uniques :
    /// - Diffusion doublée (6 all-pass) → transitoires étalés naturellement, jamais de flanger claquant
    /// - Micro-taps décorrélés avec feedback court → crépitement de "branches/troncs" en arrière-plan
    /// - Rustle generator intégré : bruit rose filtré BP 1.5 kHz modulé par LFO amplitude + LFO pan
    ///   → feuilles au vent qui vont et viennent, ajoute une couche organique par-dessus la reverb
    /// </summary>
    [KotonEffect("Forest Ambience", Id = "koton.forest", Category = "Reverb", Version = "1.0", Vendor = "Koton Studio")]
    public sealed class ForestPlugin : IKotonEffect
    {
        public string Id => "koton.forest";
        public string DisplayName => "Forest Ambience";

        readonly KotonParameter _density      = new KotonParameter("density",       "Density",       0.0, 1.0, 0.50);
        readonly KotonParameter _decay        = new KotonParameter("decay",         "Decay",         0.0, 1.0, 0.60);
        readonly KotonParameter _absorption   = new KotonParameter("absorption",    "Absorption",    0.0, 1.0, 0.55);
        readonly KotonParameter _rustle       = new KotonParameter("rustle",        "Rustle",        0.0, 1.0, 0.25);
        readonly KotonParameter _rustleRate   = new KotonParameter("rustle_rate",   "Rustle rate",   0.0, 1.0, 0.40);
        readonly KotonParameter _windMovement = new KotonParameter("wind_movement", "Wind",          0.0, 1.0, 0.30);
        readonly KotonParameter _hpFilter     = new KotonParameter("hp_filter",     "HP filter",     20.0, 800.0, 200.0, "Hz");
        readonly KotonParameter _preDelay     = new KotonParameter("pre_delay",     "Pre-delay",     0.0, 100.0, 15.0, "ms");
        readonly KotonParameter _stereoWidth  = new KotonParameter("stereo_width",  "Stereo width",  0.0, 1.0, 0.85);
        readonly KotonParameter _mix          = new KotonParameter("mix",           "Mix",           0.0, 1.0, 0.40);
        readonly KotonParameter _outGain      = new KotonParameter("out_gain",      "Output",        -30.0, 6.0, 0.0, "dB");

        readonly List<KotonParameter> _params;
        public IReadOnlyList<KotonParameter> Parameters => _params;

        int _sampleRate;
        int _maxBlockSize;
        ForestCore _core;

        public ForestPlugin()
        {
            _params = new List<KotonParameter>
            {
                _density, _decay, _absorption, _rustle, _rustleRate, _windMovement,
                _hpFilter, _preDelay, _stereoWidth, _mix, _outGain,
            };
        }

        public bool HasEditor => true;
        public UserControl CreateEditor() => new ForestEditor(this);

        public void Prepare(int sampleRate, int maxBlockSize)
        {
            _sampleRate = sampleRate;
            _maxBlockSize = maxBlockSize;
            _core = new ForestCore(sampleRate);
        }

        public void Reset() => _core?.Reset();

        public void Process(Span<float> left, Span<float> right)
        {
            if (_core == null) return;
            var p = new ForestParams
            {
                Density       = (float)_density.Value,
                Decay         = (float)_decay.Value,
                Absorption    = (float)_absorption.Value,
                Rustle        = (float)_rustle.Value,
                RustleRate    = (float)_rustleRate.Value,
                WindMovement  = (float)_windMovement.Value,
                HpFilterHz    = (float)_hpFilter.Value,
                PreDelayMs    = (float)_preDelay.Value,
                StereoWidth   = (float)_stereoWidth.Value,
                Mix           = (float)_mix.Value,
                OutGainDb     = (float)_outGain.Value,
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
            "Foret paisible", "Foret dense", "Sous-bois humide", "Clairiere", "Foret d'automne (vent)", "Bosquet sec",
        };

        static readonly double[,] PresetValues =
        {
            //          dens decay absr rust rustR wind hp   preD wid  mix  out
            /*Paisible*/{ 0.50, 0.55, 0.55, 0.20, 0.30, 0.20, 200, 15,  0.80, 0.40,  0.0 },
            /*Dense*/   { 0.75, 0.70, 0.75, 0.35, 0.45, 0.30, 250, 20,  0.90, 0.55, -1.0 },
            /*Humide*/  { 0.60, 0.75, 0.65, 0.15, 0.15, 0.10, 150, 25,  0.75, 0.50, -1.0 },
            /*Clairiere*/{0.35, 0.45, 0.40, 0.30, 0.55, 0.50, 300, 10,  0.85, 0.35,  0.0 },
            /*Automne*/ { 0.55, 0.55, 0.55, 0.65, 0.60, 0.70, 200, 20,  0.95, 0.50, -1.0 },
            /*Bosquet*/ { 0.40, 0.35, 0.30, 0.10, 0.20, 0.15, 350, 8,   0.70, 0.30,  0.0 },
        };

        public void LoadPreset(int index, bool keepMix)
        {
            if (index < 0 || index >= PresetValues.GetLength(0)) return;
            double keptMix = _mix.Value;
            _density.Value      = PresetValues[index, 0];
            _decay.Value        = PresetValues[index, 1];
            _absorption.Value   = PresetValues[index, 2];
            _rustle.Value       = PresetValues[index, 3];
            _rustleRate.Value   = PresetValues[index, 4];
            _windMovement.Value = PresetValues[index, 5];
            _hpFilter.Value     = PresetValues[index, 6];
            _preDelay.Value     = PresetValues[index, 7];
            _stereoWidth.Value  = PresetValues[index, 8];
            _mix.Value          = keepMix ? keptMix : PresetValues[index, 9];
            _outGain.Value      = PresetValues[index, 10];
        }

        public void SetParam(string id, double value)
        {
            foreach (var kp in _params)
                if (kp.Id == id) { kp.Value = value; return; }
        }
    }
}
