using System;
using System.Windows.Controls;
using KotonStudio.Library;

namespace KotonPluginPiano
{
    public partial class PianoEditor : UserControl, IKotonEditor
    {
        readonly PianoPlugin _plugin;
        bool _syncing, _loading = true;

        public PianoEditor(PianoPlugin plugin)
        {
            _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
            InitializeComponent();
            PresetCombo.Items.Clear();
            PresetCombo.Items.Add("— Custom —");
            foreach (var n in PianoPlugin.PresetNames) PresetCombo.Items.Add(n);
            PresetCombo.SelectedIndex = 0;

            Wire(HammerHardnessSlider, HammerHardnessValue, "hammer_hardness", v => v.ToString("F2"));
            Wire(HammerAmountSlider,   HammerAmountValue,   "hammer_amount",   v => v.ToString("F2"));
            Wire(BrightnessSlider,     BrightnessValue,     "brightness",      v => v.ToString("F2"));
            Wire(InharmonicitySlider,  InharmonicityValue,  "inharmonicity",   v => v.ToString("F2"));
            Wire(StringDetuneSlider,   StringDetuneValue,   "string_detune",   v => v.ToString("F2"));
            Wire(DamperTimeSlider,     DamperTimeValue,     "damper_time",     v => v.ToString("F2"));
            Wire(SustainPedalSlider,   SustainPedalValue,   "sustain_pedal",   v => v > 0.5 ? "ON" : "off");
            Wire(BodySlider,           BodyValue,           "body",            v => v.ToString("F2"));
            Wire(StereoWidthSlider,    StereoWidthValue,    "stereo_width",    v => v.ToString("F2"));
            Wire(VolumeSlider,         VolumeValue,         "volume",          v => v.ToString("F1") + " dB");
            Refresh();
            _loading = false;
        }

        void Wire(Slider s, TextBlock lbl, string id, Func<double, string> fmt)
        {
            s.ValueChanged += (x, e) => {
                if (_syncing || _loading) return;
                _plugin.SetParam(id, e.NewValue);
                lbl.Text = fmt(e.NewValue);
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
                    case "hammer_hardness": HammerHardnessSlider.Value = kp.Value; HammerHardnessValue.Text = kp.Value.ToString("F2"); break;
                    case "hammer_amount":   HammerAmountSlider.Value = kp.Value;   HammerAmountValue.Text = kp.Value.ToString("F2"); break;
                    case "brightness":      BrightnessSlider.Value = kp.Value;     BrightnessValue.Text = kp.Value.ToString("F2"); break;
                    case "inharmonicity":   InharmonicitySlider.Value = kp.Value;  InharmonicityValue.Text = kp.Value.ToString("F2"); break;
                    case "string_detune":   StringDetuneSlider.Value = kp.Value;   StringDetuneValue.Text = kp.Value.ToString("F2"); break;
                    case "damper_time":     DamperTimeSlider.Value = kp.Value;     DamperTimeValue.Text = kp.Value.ToString("F2"); break;
                    case "sustain_pedal":   SustainPedalSlider.Value = kp.Value;   SustainPedalValue.Text = kp.Value > 0.5 ? "ON" : "off"; break;
                    case "body":            BodySlider.Value = kp.Value;           BodyValue.Text = kp.Value.ToString("F2"); break;
                    case "stereo_width":    StereoWidthSlider.Value = kp.Value;    StereoWidthValue.Text = kp.Value.ToString("F2"); break;
                    case "volume":          VolumeSlider.Value = kp.Value;         VolumeValue.Text = kp.Value.ToString("F1") + " dB"; break;
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
