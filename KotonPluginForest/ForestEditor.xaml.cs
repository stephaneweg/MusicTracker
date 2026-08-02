using System;
using System.Windows.Controls;
using KotonStudio.Library;

namespace KotonPluginForest
{
    public partial class ForestEditor : UserControl, IKotonEditor
    {
        readonly ForestPlugin _plugin;
        bool _syncing;
        bool _loading = true;

        public ForestEditor(ForestPlugin plugin)
        {
            _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
            InitializeComponent();
            InitCombos();
            WireSliders();
            RefreshFromPlugin();
            _loading = false;
        }

        void InitCombos()
        {
            PresetCombo.Items.Clear();
            PresetCombo.Items.Add("— Custom —");
            foreach (var n in ForestPlugin.PresetNames) PresetCombo.Items.Add(n);
            PresetCombo.SelectedIndex = 0;
        }

        void WireSliders()
        {
            Wire(DensitySlider,       DensityValue,       "density",       v => v.ToString("F2"));
            Wire(DecaySlider,         DecayValue,         "decay",         v => v.ToString("F2"));
            Wire(AbsorptionSlider,    AbsorptionValue,    "absorption",    v => v.ToString("F2"));
            Wire(RustleSlider,        RustleValue,        "rustle",        v => v.ToString("F2"));
            Wire(RustleRateSlider,    RustleRateValue,    "rustle_rate",   v => v.ToString("F2"));
            Wire(WindMovementSlider,  WindMovementValue,  "wind_movement", v => v.ToString("F2"));
            Wire(HpFilterSlider,      HpFilterValue,      "hp_filter",     v => v.ToString("F0") + " Hz");
            Wire(PreDelaySlider,      PreDelayValue,      "pre_delay",     v => v.ToString("F0") + " ms");
            Wire(StereoWidthSlider,   StereoWidthValue,   "stereo_width",  v => v.ToString("F2"));
            Wire(MixSlider,           MixValue,           "mix",           v => v.ToString("F2"));
            Wire(OutGainSlider,       OutGainValue,       "out_gain",      v => v.ToString("F1") + " dB");
        }

        void Wire(Slider slider, TextBlock label, string paramId, Func<double, string> fmt)
        {
            slider.ValueChanged += (s, e) =>
            {
                if (_syncing || _loading) return;
                _plugin.SetParam(paramId, e.NewValue);
                label.Text = fmt(e.NewValue);
                if (PresetCombo.SelectedIndex != 0) PresetCombo.SelectedIndex = 0;
            };
        }

        void RefreshFromPlugin()
        {
            _syncing = true;
            foreach (var kp in _plugin.Parameters)
            {
                switch (kp.Id)
                {
                    case "density":       DensitySlider.Value      = kp.Value; DensityValue.Text      = kp.Value.ToString("F2"); break;
                    case "decay":         DecaySlider.Value        = kp.Value; DecayValue.Text        = kp.Value.ToString("F2"); break;
                    case "absorption":    AbsorptionSlider.Value   = kp.Value; AbsorptionValue.Text   = kp.Value.ToString("F2"); break;
                    case "rustle":        RustleSlider.Value       = kp.Value; RustleValue.Text       = kp.Value.ToString("F2"); break;
                    case "rustle_rate":   RustleRateSlider.Value   = kp.Value; RustleRateValue.Text   = kp.Value.ToString("F2"); break;
                    case "wind_movement": WindMovementSlider.Value = kp.Value; WindMovementValue.Text = kp.Value.ToString("F2"); break;
                    case "hp_filter":     HpFilterSlider.Value     = kp.Value; HpFilterValue.Text     = kp.Value.ToString("F0") + " Hz"; break;
                    case "pre_delay":     PreDelaySlider.Value     = kp.Value; PreDelayValue.Text     = kp.Value.ToString("F0") + " ms"; break;
                    case "stereo_width":  StereoWidthSlider.Value  = kp.Value; StereoWidthValue.Text  = kp.Value.ToString("F2"); break;
                    case "mix":           MixSlider.Value          = kp.Value; MixValue.Text          = kp.Value.ToString("F2"); break;
                    case "out_gain":      OutGainSlider.Value      = kp.Value; OutGainValue.Text      = kp.Value.ToString("F1") + " dB"; break;
                }
            }
            _syncing = false;
        }

        void PresetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || _syncing) return;
            int idx = PresetCombo.SelectedIndex;
            if (idx <= 0) return;
            _plugin.LoadPreset(idx - 1, MixLockCheck.IsChecked == true);
            RefreshFromPlugin();
        }

        public void OnContextUpdated(KotonRenderContext ctx) { }
    }
}
