using System;
using System.Windows.Controls;
using KotonStudio.Library;

namespace KotonPluginWoodwind
{
    public partial class WoodwindEditor : UserControl, IKotonEditor
    {
        readonly WoodwindPlugin _plugin;
        bool _syncing;
        bool _loading = true;

        public WoodwindEditor(WoodwindPlugin plugin)
        {
            _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
            InitializeComponent();
            InitCombos();
            WireSliders();
            RefreshFromPlugin();
            _loading = false;
        }

        void InitCombos()
        {
            InstrumentCombo.Items.Clear();
            foreach (var n in WoodwindPlugin.InstrumentNames) InstrumentCombo.Items.Add(n);

            ExcitationCombo.Items.Clear();
            ExcitationCombo.Items.Add("Anche (clarinette, sax)");
            ExcitationCombo.Items.Add("Jet d'air (flute)");
        }

        void WireSliders()
        {
            Wire(AirPressureSlider,    AirPressureValue,    "air_pressure",    v => v.ToString("F2"));
            Wire(BreathNoiseSlider,    BreathNoiseValue,    "breath_noise",    v => v.ToString("F2"));
            Wire(ReedSoftnessSlider,   ReedSoftnessValue,   "reed_softness",   v => v.ToString("F2"));
            Wire(DampingSlider,        DampingValue,        "damping",         v => v.ToString("F2"));
            Wire(BrightnessSlider,     BrightnessValue,     "brightness",      v => v.ToString("F2"));
            Wire(BoreSizeSlider,       BoreSizeValue,       "bore_size",       v => v.ToString("F2"));
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
                    case "excitation_type":  ExcitationCombo.SelectedIndex = (int)kp.Value; break;
                    case "air_pressure":     AirPressureSlider.Value    = kp.Value; AirPressureValue.Text    = kp.Value.ToString("F2"); break;
                    case "breath_noise":     BreathNoiseSlider.Value    = kp.Value; BreathNoiseValue.Text    = kp.Value.ToString("F2"); break;
                    case "reed_softness":    ReedSoftnessSlider.Value   = kp.Value; ReedSoftnessValue.Text   = kp.Value.ToString("F2"); break;
                    case "damping":          DampingSlider.Value        = kp.Value; DampingValue.Text        = kp.Value.ToString("F2"); break;
                    case "brightness":       BrightnessSlider.Value     = kp.Value; BrightnessValue.Text     = kp.Value.ToString("F2"); break;
                    case "bore_size":        BoreSizeSlider.Value       = kp.Value; BoreSizeValue.Text       = kp.Value.ToString("F2"); break;
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

        void ExcitationCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || _syncing) return;
            _plugin.SetParam("excitation_type", ExcitationCombo.SelectedIndex);
        }

        public void OnContextUpdated(KotonRenderContext ctx) { }
    }
}
