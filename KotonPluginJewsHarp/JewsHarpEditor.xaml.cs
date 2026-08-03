using System;
using System.Windows.Controls;
using KotonStudio.Library;

namespace KotonPluginJewsHarp
{
    public partial class JewsHarpEditor : UserControl, IKotonEditor
    {
        readonly JewsHarpPlugin _plugin;
        bool _syncing, _loading = true;

        public JewsHarpEditor(JewsHarpPlugin plugin)
        {
            _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
            InitializeComponent();
            PresetCombo.Items.Clear();
            PresetCombo.Items.Add("— Custom —");
            foreach (var n in JewsHarpPlugin.PresetNames) PresetCombo.Items.Add(n);
            PresetCombo.SelectedIndex = 0;
            Wire(LameFreqSlider, LameFreqValue, "lame_freq", v => v.ToString("F0") + " Hz");
            Wire(LameDecaySlider, LameDecayValue, "lame_decay", v => v.ToString("F2"));
            Wire(TwangSlider, TwangValue, "twang", v => v.ToString("F2"));
            Wire(SpringSlider, SpringValue, "spring", v => v.ToString("F2"));
            Wire(FormantQSlider, FormantQValue, "formant_q", v => v.ToString("F1"));
            Wire(FormantMinSlider, FormantMinValue, "formant_min", v => v.ToString("F0") + " Hz");
            Wire(FormantMaxSlider, FormantMaxValue, "formant_max", v => v.ToString("F0") + " Hz");
            Wire(BreathSlider, BreathValue, "breath", v => v.ToString("F2"));
            Wire(BrightnessSlider, BrightnessValue, "brightness", v => v.ToString("F2"));
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
                    case "lame_freq": LameFreqSlider.Value = kp.Value; LameFreqValue.Text = kp.Value.ToString("F0") + " Hz"; break;
                    case "lame_decay": LameDecaySlider.Value = kp.Value; LameDecayValue.Text = kp.Value.ToString("F2"); break;
                    case "twang": TwangSlider.Value = kp.Value; TwangValue.Text = kp.Value.ToString("F2"); break;
                    case "spring": SpringSlider.Value = kp.Value; SpringValue.Text = kp.Value.ToString("F2"); break;
                    case "formant_q": FormantQSlider.Value = kp.Value; FormantQValue.Text = kp.Value.ToString("F1"); break;
                    case "formant_min": FormantMinSlider.Value = kp.Value; FormantMinValue.Text = kp.Value.ToString("F0") + " Hz"; break;
                    case "formant_max": FormantMaxSlider.Value = kp.Value; FormantMaxValue.Text = kp.Value.ToString("F0") + " Hz"; break;
                    case "breath": BreathSlider.Value = kp.Value; BreathValue.Text = kp.Value.ToString("F2"); break;
                    case "brightness": BrightnessSlider.Value = kp.Value; BrightnessValue.Text = kp.Value.ToString("F2"); break;
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
