using System;
using System.Windows;
using System.Windows.Controls;
using KotonStudio.Library;

namespace KotonPluginTapeDelay
{
    public partial class TapeDelayEditor : UserControl, IKotonEditor
    {
        readonly TapeDelayPlugin _plugin;
        bool _syncing, _loading = true;

        public TapeDelayEditor(TapeDelayPlugin plugin)
        {
            _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
            InitializeComponent();
            PresetCombo.Items.Clear();
            PresetCombo.Items.Add("— Custom —");
            foreach (var n in TapeDelayPlugin.PresetNames) PresetCombo.Items.Add(n);
            PresetCombo.SelectedIndex = 0;
            Wire(TimeSlider, TimeValue, "time", v => v.ToString("F0") + " ms");
            Wire(FeedbackSlider, FeedbackValue, "feedback", v => v.ToString("F2"));
            Wire(WowSlider, WowValue, "wow", v => v.ToString("F2"));
            Wire(FlutterSlider, FlutterValue, "flutter", v => v.ToString("F2"));
            Wire(SaturationSlider, SaturationValue, "saturation", v => v.ToString("F2"));
            Wire(HfDecaySlider, HfDecayValue, "hf_decay", v => v.ToString("F2"));
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
                    case "time": TimeSlider.Value = kp.Value; TimeValue.Text = kp.Value.ToString("F0") + " ms"; break;
                    case "feedback": FeedbackSlider.Value = kp.Value; FeedbackValue.Text = kp.Value.ToString("F2"); break;
                    case "wow": WowSlider.Value = kp.Value; WowValue.Text = kp.Value.ToString("F2"); break;
                    case "flutter": FlutterSlider.Value = kp.Value; FlutterValue.Text = kp.Value.ToString("F2"); break;
                    case "saturation": SaturationSlider.Value = kp.Value; SaturationValue.Text = kp.Value.ToString("F2"); break;
                    case "hf_decay": HfDecaySlider.Value = kp.Value; HfDecayValue.Text = kp.Value.ToString("F2"); break;
                    case "ping_pong": PingPongCheck.IsChecked = kp.Value >= 0.5; break;
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
        void PingPongCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading || _syncing) return;
            _plugin.SetParam("ping_pong", PingPongCheck.IsChecked == true ? 1.0 : 0.0);
            if (PresetCombo.SelectedIndex != 0) PresetCombo.SelectedIndex = 0;
        }
        public void OnContextUpdated(KotonRenderContext ctx) { }
    }
}
