using System;
using System.Windows.Controls;
using KotonStudio.Library;

namespace KotonPluginReverseReverb
{
    public partial class ReverseReverbEditor : UserControl, IKotonEditor
    {
        readonly ReverseReverbPlugin _plugin;
        bool _syncing, _loading = true;

        public ReverseReverbEditor(ReverseReverbPlugin plugin)
        {
            _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
            InitializeComponent();
            PresetCombo.Items.Clear();
            PresetCombo.Items.Add("— Custom —");
            foreach (var n in ReverseReverbPlugin.PresetNames) PresetCombo.Items.Add(n);
            PresetCombo.SelectedIndex = 0;
            Wire(LengthSlider, LengthValue, "length", v => v.ToString("F2") + " s");
            Wire(SmoothnessSlider, SmoothnessValue, "smoothness", v => v.ToString("F2"));
            Wire(BrightnessSlider, BrightnessValue, "brightness", v => v.ToString("F2"));
            Wire(StereoWidthSlider, StereoWidthValue, "stereo_width", v => v.ToString("F2"));
            Wire(MixSlider, MixValue, "mix", v => v.ToString("F2"));
            Wire(OutGainSlider, OutGainValue, "out_gain", v => v.ToString("F1") + " dB");
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
                    case "length": LengthSlider.Value = kp.Value; LengthValue.Text = kp.Value.ToString("F2") + " s"; break;
                    case "smoothness": SmoothnessSlider.Value = kp.Value; SmoothnessValue.Text = kp.Value.ToString("F2"); break;
                    case "brightness": BrightnessSlider.Value = kp.Value; BrightnessValue.Text = kp.Value.ToString("F2"); break;
                    case "stereo_width": StereoWidthSlider.Value = kp.Value; StereoWidthValue.Text = kp.Value.ToString("F2"); break;
                    case "mix": MixSlider.Value = kp.Value; MixValue.Text = kp.Value.ToString("F2"); break;
                    case "out_gain": OutGainSlider.Value = kp.Value; OutGainValue.Text = kp.Value.ToString("F1") + " dB"; break;
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
