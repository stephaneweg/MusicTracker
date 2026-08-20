using System;
using System.Windows.Controls;
using KotonStudio.Library;

namespace KotonPluginStorm
{
    public partial class StormEditor : UserControl, IKotonEditor
    {
        readonly StormPlugin _plugin;
        bool _syncing, _loading = true;

        public StormEditor(StormPlugin plugin)
        {
            _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
            InitializeComponent();
            PresetCombo.Items.Clear();
            PresetCombo.Items.Add("— Custom —");
            foreach (var n in StormPlugin.PresetNames) PresetCombo.Items.Add(n);
            PresetCombo.SelectedIndex = 0;
            Wire(WindSlider,        WindValue,        "wind",         v => v.ToString("F2"));
            Wire(WindRateSlider,    WindRateValue,    "wind_rate",    v => v.ToString("F2"));
            Wire(RainSlider,        RainValue,        "rain",         v => v.ToString("F2"));
            Wire(RainDensitySlider, RainDensityValue, "rain_density", v => v.ToString("F2"));
            Wire(LightningSlider,   LightningValue,   "lightning",    v => v.ToString("F2"));
            Wire(ThunderSlider,     ThunderValue,     "thunder",      v => v.ToString("F2"));
            Wire(StereoWidthSlider, StereoWidthValue, "stereo_width", v => v.ToString("F2"));
            Wire(MixSlider,         MixValue,         "mix",          v => v.ToString("F2"));
            Wire(OutGainSlider,     OutGainValue,     "out_gain",     v => v.ToString("F1") + " dB");
            Refresh();
            _loading = false;
        }
        void Wire(Slider s, TextBlock lbl, string id, Func<double, string> fmt)
        {
            s.ValueChanged += (x, e) => {
                if (_syncing || _loading) return;
                _plugin.SetParam(id, e.NewValue); lbl.Text = fmt(e.NewValue);
                if (PresetCombo.SelectedIndex != 0) PresetCombo.SelectedIndex = 0;
            };
        }
        void Refresh()
        {
            _syncing = true;
            foreach (var kp in _plugin.Parameters)
            {
                switch (kp.Id)
                {
                    case "wind":         WindSlider.Value        = kp.Value; WindValue.Text        = kp.Value.ToString("F2"); break;
                    case "wind_rate":    WindRateSlider.Value    = kp.Value; WindRateValue.Text    = kp.Value.ToString("F2"); break;
                    case "rain":         RainSlider.Value        = kp.Value; RainValue.Text        = kp.Value.ToString("F2"); break;
                    case "rain_density": RainDensitySlider.Value = kp.Value; RainDensityValue.Text = kp.Value.ToString("F2"); break;
                    case "lightning":    LightningSlider.Value   = kp.Value; LightningValue.Text   = kp.Value.ToString("F2"); break;
                    case "thunder":      ThunderSlider.Value     = kp.Value; ThunderValue.Text     = kp.Value.ToString("F2"); break;
                    case "stereo_width": StereoWidthSlider.Value = kp.Value; StereoWidthValue.Text = kp.Value.ToString("F2"); break;
                    case "mix":          MixSlider.Value         = kp.Value; MixValue.Text         = kp.Value.ToString("F2"); break;
                    case "out_gain":     OutGainSlider.Value     = kp.Value; OutGainValue.Text     = kp.Value.ToString("F1") + " dB"; break;
                }
            }
            _syncing = false;
        }
        void PresetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || _syncing) return;
            int idx = PresetCombo.SelectedIndex;
            if (idx <= 0) return;
            _plugin.LoadPreset(idx - 1);
            Refresh();
        }
        public void OnContextUpdated(KotonRenderContext ctx) { }
    }
}
