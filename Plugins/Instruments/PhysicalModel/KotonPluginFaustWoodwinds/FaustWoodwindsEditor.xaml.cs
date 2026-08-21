using System;
using System.Windows.Controls;
using KotonStudio.Library;

namespace KotonPluginFaustWoodwinds
{
    public partial class FaustWoodwindsEditor : UserControl, IKotonEditor
    {
        readonly FaustWoodwindsPlugin _plugin;
        bool _syncing, _loading = true;

        public FaustWoodwindsEditor(FaustWoodwindsPlugin plugin)
        {
            _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
            InitializeComponent();
            Wire(PressureSlider,       PressureValue,       "pressure",       v => v.ToString("F2"));
            Wire(ReedStiffnessSlider,  ReedStiffnessValue,  "reed_stiffness", v => v.ToString("F2"));
            Wire(BellOpeningSlider,    BellOpeningValue,    "bell_opening",   v => v.ToString("F2"));
            Wire(BreathNoiseSlider,    BreathNoiseValue,    "breath_noise",   v => v.ToString("F2"));
            Wire(VibratoRateSlider,    VibratoRateValue,    "vibrato_rate",   v => v.ToString("F1") + " Hz");
            Wire(VibratoDepthSlider,   VibratoDepthValue,   "vibrato_depth",  v => v.ToString("F0") + " ct");
            Wire(AttackSlider,         AttackValue,         "attack_time",    v => v.ToString("F3") + " s");
            Wire(ReleaseSlider,        ReleaseValue,        "release_time",   v => v.ToString("F2") + " s");
            Wire(OutputGainSlider,     OutputGainValue,     "output_gain",    v => v.ToString("F2"));
            Wire(VolumeSlider,         VolumeValue,         "volume",         v => v.ToString("F1") + " dB");
            Refresh();
            _loading = false;
        }

        void Wire(Slider s, TextBlock lbl, string id, Func<double, string> fmt)
        {
            s.ValueChanged += (x, e) => { if (_syncing || _loading) return; _plugin.SetParam(id, e.NewValue); lbl.Text = fmt(e.NewValue); };
        }

        void Refresh()
        {
            _syncing = true;
            foreach (var kp in _plugin.Parameters)
            {
                switch (kp.Id)
                {
                    case "pressure":       PressureSlider.Value      = kp.Value; PressureValue.Text      = kp.Value.ToString("F2"); break;
                    case "reed_stiffness": ReedStiffnessSlider.Value = kp.Value; ReedStiffnessValue.Text = kp.Value.ToString("F2"); break;
                    case "bell_opening":   BellOpeningSlider.Value   = kp.Value; BellOpeningValue.Text   = kp.Value.ToString("F2"); break;
                    case "breath_noise":   BreathNoiseSlider.Value   = kp.Value; BreathNoiseValue.Text   = kp.Value.ToString("F2"); break;
                    case "vibrato_rate":   VibratoRateSlider.Value   = kp.Value; VibratoRateValue.Text   = kp.Value.ToString("F1") + " Hz"; break;
                    case "vibrato_depth":  VibratoDepthSlider.Value  = kp.Value; VibratoDepthValue.Text  = kp.Value.ToString("F0") + " ct"; break;
                    case "attack_time":    AttackSlider.Value        = kp.Value; AttackValue.Text        = kp.Value.ToString("F3") + " s"; break;
                    case "release_time":   ReleaseSlider.Value       = kp.Value; ReleaseValue.Text       = kp.Value.ToString("F2") + " s"; break;
                    case "output_gain":    OutputGainSlider.Value    = kp.Value; OutputGainValue.Text    = kp.Value.ToString("F2"); break;
                    case "volume":         VolumeSlider.Value        = kp.Value; VolumeValue.Text        = kp.Value.ToString("F1") + " dB"; break;
                }
            }
            _syncing = false;
        }

        public void OnContextUpdated(KotonRenderContext ctx) { }
    }
}
