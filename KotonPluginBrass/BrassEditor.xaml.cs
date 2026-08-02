using System;
using System.Windows.Controls;
using KotonStudio.Library;

namespace KotonPluginBrass
{
    /// <summary>Éditeur du plugin Brass. Combo instrument (Trompette/Trombone/…) qui charge tous
    /// les défauts du preset.</summary>
    public partial class BrassEditor : UserControl, IKotonEditor
    {
        readonly BrassPlugin _plugin;
        bool _syncing;
        bool _loading = true;

        public BrassEditor(BrassPlugin plugin)
        {
            _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
            InitializeComponent();
            InitCombo();
            WireSliders();
            RefreshFromPlugin();
            _loading = false;
        }

        void InitCombo()
        {
            InstrumentCombo.Items.Clear();
            foreach (var n in BrassPlugin.InstrumentNames) InstrumentCombo.Items.Add(n);
        }

        void WireSliders()
        {
            Wire(BreathPressureSlider, BreathPressureValue, "breath_pressure", v => v.ToString("F2"));
            Wire(BreathNoiseSlider,    BreathNoiseValue,    "breath_noise",    v => v.ToString("F2"));
            Wire(LipTensionSlider,     LipTensionValue,     "lip_tension",     v => v.ToString("F2"));
            Wire(DampingSlider,        DampingValue,        "damping",         v => v.ToString("F2"));
            Wire(BrightnessSlider,     BrightnessValue,     "brightness",      v => v.ToString("F2"));
            Wire(BellSizeSlider,       BellSizeValue,       "bell_size",       v => v.ToString("F2"));
            Wire(VibratoRateSlider,    VibratoRateValue,    "vibrato_rate",    v => v.ToString("F1") + " Hz");
            Wire(VibratoDepthSlider,   VibratoDepthValue,   "vibrato_depth",   v => v.ToString("F0") + " ct");
            Wire(AttackSlider,         AttackValue,         "attack_time",     v => v.ToString("F2") + " s");
            Wire(ReleaseSlider,        ReleaseValue,        "release_time",    v => v.ToString("F2") + " s");
            Wire(StereoWidthSlider,    StereoWidthValue,    "stereo_width",    v => v.ToString("F2"));
            Wire(VolumeSlider,         VolumeValue,         "volume",          v => v.ToString("F1") + " dB");
        }

        void Wire(Slider slider, TextBlock label, string paramId, Func<double, string> fmt)
        {
            slider.ValueChanged += (s, e) =>
            {
                if (_syncing || _loading) return;
                _plugin.SetParam(paramId, e.NewValue);
                label.Text = fmt(e.NewValue);
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
                    case "breath_pressure":  BreathPressureSlider.Value = kp.Value; BreathPressureValue.Text = kp.Value.ToString("F2"); break;
                    case "breath_noise":     BreathNoiseSlider.Value    = kp.Value; BreathNoiseValue.Text    = kp.Value.ToString("F2"); break;
                    case "lip_tension":      LipTensionSlider.Value     = kp.Value; LipTensionValue.Text     = kp.Value.ToString("F2"); break;
                    case "damping":          DampingSlider.Value        = kp.Value; DampingValue.Text        = kp.Value.ToString("F2"); break;
                    case "brightness":       BrightnessSlider.Value     = kp.Value; BrightnessValue.Text     = kp.Value.ToString("F2"); break;
                    case "bell_size":        BellSizeSlider.Value       = kp.Value; BellSizeValue.Text       = kp.Value.ToString("F2"); break;
                    case "vibrato_rate":     VibratoRateSlider.Value    = kp.Value; VibratoRateValue.Text    = kp.Value.ToString("F1") + " Hz"; break;
                    case "vibrato_depth":    VibratoDepthSlider.Value   = kp.Value; VibratoDepthValue.Text   = kp.Value.ToString("F0") + " ct"; break;
                    case "attack_time":      AttackSlider.Value         = kp.Value; AttackValue.Text         = kp.Value.ToString("F2") + " s"; break;
                    case "release_time":     ReleaseSlider.Value        = kp.Value; ReleaseValue.Text        = kp.Value.ToString("F2") + " s"; break;
                    case "stereo_width":     StereoWidthSlider.Value    = kp.Value; StereoWidthValue.Text    = kp.Value.ToString("F2"); break;
                    case "volume":           VolumeSlider.Value         = kp.Value; VolumeValue.Text         = kp.Value.ToString("F1") + " dB"; break;
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
