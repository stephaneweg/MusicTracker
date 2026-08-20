using System;
using System.Windows.Controls;
using KotonStudio.Library;

namespace KotonPluginBlownFlute
{
    public partial class BlownFluteEditor : UserControl, IKotonEditor
    {
        readonly BlownFlutePlugin _plugin;
        bool _syncing, _loading = true;

        public BlownFluteEditor(BlownFlutePlugin plugin)
        {
            _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
            InitializeComponent();
            InstrumentCombo.Items.Clear();
            foreach (var n in BlownFlutePlugin.InstrumentNames) InstrumentCombo.Items.Add(n);
            Wire(BreathPressureSlider,  BreathPressureValue,  "breath_pressure",  v => v.ToString("F2"));
            Wire(BreathNoiseSlider,     BreathNoiseValue,     "breath_noise",     v => v.ToString("F2"));
            Wire(JetInstabilitySlider,  JetInstabilityValue,  "jet_instability",  v => v.ToString("F2"));
            Wire(EmbouchureShiftSlider, EmbouchureShiftValue, "embouchure_shift", v => v.ToString("F2"));
            Wire(DampingSlider,         DampingValue,         "damping",          v => v.ToString("F2"));
            Wire(BrightnessSlider,      BrightnessValue,      "brightness",       v => v.ToString("F2"));
            Wire(VibratoRateSlider,     VibratoRateValue,     "vibrato_rate",     v => v.ToString("F1") + " Hz");
            Wire(VibratoDepthSlider,    VibratoDepthValue,    "vibrato_depth",    v => v.ToString("F0") + " ct");
            Wire(BreathAttackSlider,    BreathAttackValue,    "breath_attack",    v => v.ToString("F2"));
            Wire(ReleaseSlider,         ReleaseValue,         "release_time",     v => v.ToString("F2") + " s");
            Wire(StereoWidthSlider,     StereoWidthValue,     "stereo_width",     v => v.ToString("F2"));
            Wire(VolumeSlider,          VolumeValue,          "volume",           v => v.ToString("F1") + " dB");
            RefreshFromPlugin();
            _loading = false;
        }

        void Wire(Slider s, TextBlock lbl, string id, Func<double, string> fmt)
        {
            s.ValueChanged += (x, e) =>
            {
                if (_syncing || _loading) return;
                _plugin.SetParam(id, e.NewValue);
                lbl.Text = fmt(e.NewValue);
            };
        }

        void RefreshFromPlugin()
        {
            _syncing = true;
            foreach (var kp in _plugin.Parameters)
            {
                switch (kp.Id)
                {
                    case "instrument":       InstrumentCombo.SelectedIndex = (int)kp.Value; break;
                    case "breath_pressure":  BreathPressureSlider.Value  = kp.Value; BreathPressureValue.Text  = kp.Value.ToString("F2"); break;
                    case "breath_noise":     BreathNoiseSlider.Value     = kp.Value; BreathNoiseValue.Text     = kp.Value.ToString("F2"); break;
                    case "jet_instability":  JetInstabilitySlider.Value  = kp.Value; JetInstabilityValue.Text  = kp.Value.ToString("F2"); break;
                    case "embouchure_shift": EmbouchureShiftSlider.Value = kp.Value; EmbouchureShiftValue.Text = kp.Value.ToString("F2"); break;
                    case "damping":          DampingSlider.Value         = kp.Value; DampingValue.Text         = kp.Value.ToString("F2"); break;
                    case "brightness":       BrightnessSlider.Value      = kp.Value; BrightnessValue.Text      = kp.Value.ToString("F2"); break;
                    case "vibrato_rate":     VibratoRateSlider.Value     = kp.Value; VibratoRateValue.Text     = kp.Value.ToString("F1") + " Hz"; break;
                    case "vibrato_depth":    VibratoDepthSlider.Value    = kp.Value; VibratoDepthValue.Text    = kp.Value.ToString("F0") + " ct"; break;
                    case "breath_attack":    BreathAttackSlider.Value    = kp.Value; BreathAttackValue.Text    = kp.Value.ToString("F2"); break;
                    case "release_time":     ReleaseSlider.Value         = kp.Value; ReleaseValue.Text         = kp.Value.ToString("F2") + " s"; break;
                    case "stereo_width":     StereoWidthSlider.Value     = kp.Value; StereoWidthValue.Text     = kp.Value.ToString("F2"); break;
                    case "volume":           VolumeSlider.Value          = kp.Value; VolumeValue.Text          = kp.Value.ToString("F1") + " dB"; break;
                }
            }
            _syncing = false;
        }

        void InstrumentCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || _syncing) return;
            int idx = InstrumentCombo.SelectedIndex;
            _plugin.SetParam("instrument", idx);
            _plugin.ApplyInstrumentDefaults(idx);
            RefreshFromPlugin();
        }

        public void OnContextUpdated(KotonRenderContext ctx) { }
    }
}
