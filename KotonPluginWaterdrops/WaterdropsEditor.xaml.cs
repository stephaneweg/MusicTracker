using System;
using System.Windows.Controls;
using KotonStudio.Library;

namespace KotonPluginWaterdrops
{
    public partial class WaterdropsEditor : UserControl, IKotonEditor
    {
        readonly WaterdropsPlugin _plugin;
        bool _syncing;
        bool _loading = true;

        public WaterdropsEditor(WaterdropsPlugin plugin)
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
            PresetCombo.Items.Clear();
            PresetCombo.Items.Add("— Custom —");
            foreach (var n in WaterdropsPlugin.PresetNames) PresetCombo.Items.Add(n);
            PresetCombo.SelectedIndex = 0;
        }

        void WireSliders()
        {
            Wire(DropSizeSlider,     DropSizeValue,     "drop_size",    v => v.ToString("F2"));
            Wire(SplashSlider,       SplashValue,       "splash",       v => v.ToString("F2"));
            Wire(ResonanceSlider,    ResonanceValue,    "resonance",    v => v.ToString("F2"));
            Wire(BrightnessSlider,   BrightnessValue,   "brightness",   v => v.ToString("F2"));
            Wire(RandomnessSlider,   RandomnessValue,   "randomness",   v => v.ToString("F2"));
            Wire(WetSlider,          WetValue,          "wet",          v => v.ToString("F2"));
            Wire(SpaceSlider,        SpaceValue,        "space",        v => v.ToString("F2"));
            Wire(StereoWidthSlider,  StereoWidthValue,  "stereo_width", v => v.ToString("F2"));
            Wire(VolumeSlider,       VolumeValue,       "volume",       v => v.ToString("F1") + " dB");
        }

        void Wire(Slider slider, TextBlock label, string paramId, Func<double, string> fmt)
        {
            slider.ValueChanged += (s, e) =>
            {
                if (_syncing || _loading) return;
                _plugin.SetParam(paramId, e.NewValue);
                label.Text = fmt(e.NewValue);
                if (PresetCombo.SelectedIndex != 0) PresetCombo.SelectedIndex = 0;
            };
        }

        void RefreshFromPlugin()
        {
            _syncing = true;
            foreach (var kp in _plugin.Parameters)
            {
                switch (kp.Id)
                {
                    case "drop_size":    DropSizeSlider.Value    = kp.Value; DropSizeValue.Text    = kp.Value.ToString("F2"); break;
                    case "splash":       SplashSlider.Value      = kp.Value; SplashValue.Text      = kp.Value.ToString("F2"); break;
                    case "resonance":    ResonanceSlider.Value   = kp.Value; ResonanceValue.Text   = kp.Value.ToString("F2"); break;
                    case "brightness":   BrightnessSlider.Value  = kp.Value; BrightnessValue.Text  = kp.Value.ToString("F2"); break;
                    case "randomness":   RandomnessSlider.Value  = kp.Value; RandomnessValue.Text  = kp.Value.ToString("F2"); break;
                    case "wet":          WetSlider.Value         = kp.Value; WetValue.Text         = kp.Value.ToString("F2"); break;
                    case "space":        SpaceSlider.Value       = kp.Value; SpaceValue.Text       = kp.Value.ToString("F2"); break;
                    case "stereo_width": StereoWidthSlider.Value = kp.Value; StereoWidthValue.Text = kp.Value.ToString("F2"); break;
                    case "volume":       VolumeSlider.Value      = kp.Value; VolumeValue.Text      = kp.Value.ToString("F1") + " dB"; break;
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
            RefreshFromPlugin();
        }

        public void OnContextUpdated(KotonRenderContext ctx) { }
    }
}
