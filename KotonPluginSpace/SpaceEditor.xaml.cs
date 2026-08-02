using System;
using System.Windows.Controls;
using KotonStudio.Library;

namespace KotonPluginSpace
{
    public partial class SpaceEditor : UserControl, IKotonEditor
    {
        readonly SpacePlugin _plugin;
        bool _syncing, _loading = true;

        public SpaceEditor(SpacePlugin plugin)
        {
            _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
            InitializeComponent();
            PresetCombo.Items.Clear();
            PresetCombo.Items.Add("— Custom —");
            foreach (var n in SpacePlugin.PresetNames) PresetCombo.Items.Add(n);
            PresetCombo.SelectedIndex = 0;
            Wire(SizeSlider, SizeValue, "size", v => v.ToString("F2"));
            Wire(DecaySlider, DecayValue, "decay", v => v.ToString("F2"));
            Wire(OctaveDownSlider, OctaveDownValue, "octave_down", v => v.ToString("F2"));
            Wire(ColdnessSlider, ColdnessValue, "coldness", v => v.ToString("F2"));
            Wire(TwinkleSlider, TwinkleValue, "twinkle", v => v.ToString("F2"));
            Wire(PreDelaySlider, PreDelayValue, "pre_delay", v => v.ToString("F0") + " ms");
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
                    case "size": SizeSlider.Value = kp.Value; SizeValue.Text = kp.Value.ToString("F2"); break;
                    case "decay": DecaySlider.Value = kp.Value; DecayValue.Text = kp.Value.ToString("F2"); break;
                    case "octave_down": OctaveDownSlider.Value = kp.Value; OctaveDownValue.Text = kp.Value.ToString("F2"); break;
                    case "coldness": ColdnessSlider.Value = kp.Value; ColdnessValue.Text = kp.Value.ToString("F2"); break;
                    case "twinkle": TwinkleSlider.Value = kp.Value; TwinkleValue.Text = kp.Value.ToString("F2"); break;
                    case "pre_delay": PreDelaySlider.Value = kp.Value; PreDelayValue.Text = kp.Value.ToString("F0") + " ms"; break;
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
            _plugin.LoadPreset(idx - 1, MixLockCheck.IsChecked == true);
            Refresh();
        }
        public void OnContextUpdated(KotonRenderContext ctx) { }
    }
}
