using System;
using System.Windows.Controls;
using KotonStudio.Library;

namespace KotonPluginWindchimes
{
    public partial class WindchimesEditor : UserControl, IKotonEditor
    {
        readonly WindchimesPlugin _plugin;
        bool _syncing, _loading = true;

        public WindchimesEditor(WindchimesPlugin plugin)
        {
            _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
            InitializeComponent();
            PresetCombo.Items.Clear();
            PresetCombo.Items.Add("— Custom —");
            foreach (var n in WindchimesPlugin.PresetNames) PresetCombo.Items.Add(n);
            PresetCombo.SelectedIndex = 0;
            Wire(BrightnessSlider, BrightnessValue, "brightness", v => v.ToString("F2"));
            Wire(DecaySlider, DecayValue, "decay", v => v.ToString("F2"));
            Wire(WindSlider, WindValue, "wind", v => v.ToString("F2"));
            Wire(WindGustSlider, WindGustValue, "wind_gust", v => v.ToString("F2"));
            Wire(StereoSpreadSlider, StereoSpreadValue, "stereo_spread", v => v.ToString("F2"));
            Wire(VolumeSlider, VolumeValue, "volume", v => v.ToString("F1") + " dB");
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
                    case "brightness": BrightnessSlider.Value = kp.Value; BrightnessValue.Text = kp.Value.ToString("F2"); break;
                    case "decay": DecaySlider.Value = kp.Value; DecayValue.Text = kp.Value.ToString("F2"); break;
                    case "wind": WindSlider.Value = kp.Value; WindValue.Text = kp.Value.ToString("F2"); break;
                    case "wind_gust": WindGustSlider.Value = kp.Value; WindGustValue.Text = kp.Value.ToString("F2"); break;
                    case "stereo_spread": StereoSpreadSlider.Value = kp.Value; StereoSpreadValue.Text = kp.Value.ToString("F2"); break;
                    case "volume": VolumeSlider.Value = kp.Value; VolumeValue.Text = kp.Value.ToString("F1") + " dB"; break;
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
