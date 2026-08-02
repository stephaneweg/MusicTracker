using System;
using System.Windows.Controls;
using KotonStudio.Library;

namespace KotonPluginHandpan
{
    public partial class HandpanEditor : UserControl, IKotonEditor
    {
        readonly HandpanPlugin _plugin;
        bool _syncing, _loading = true;

        public HandpanEditor(HandpanPlugin plugin)
        {
            _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
            InitializeComponent();
            PresetCombo.Items.Clear();
            PresetCombo.Items.Add("— Custom —");
            foreach (var n in HandpanPlugin.PresetNames) PresetCombo.Items.Add(n);
            PresetCombo.SelectedIndex = 0;
            Wire(MalletHardnessSlider, MalletHardnessValue, "mallet_hardness", v => v.ToString("F2"));
            Wire(ResonanceSlider,      ResonanceValue,      "resonance",       v => v.ToString("F2"));
            Wire(BrightnessSlider,     BrightnessValue,     "brightness",      v => v.ToString("F2"));
            Wire(SympathySlider,       SympathyValue,       "sympathy",        v => v.ToString("F2"));
            Wire(ShellMixSlider,       ShellMixValue,       "shell_mix",       v => v.ToString("F2"));
            Wire(StereoSpreadSlider,   StereoSpreadValue,   "stereo_spread",   v => v.ToString("F2"));
            Wire(VolumeSlider,         VolumeValue,         "volume",          v => v.ToString("F1") + " dB");
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
                    case "mallet_hardness": MalletHardnessSlider.Value = kp.Value; MalletHardnessValue.Text = kp.Value.ToString("F2"); break;
                    case "resonance":       ResonanceSlider.Value      = kp.Value; ResonanceValue.Text      = kp.Value.ToString("F2"); break;
                    case "brightness":      BrightnessSlider.Value     = kp.Value; BrightnessValue.Text     = kp.Value.ToString("F2"); break;
                    case "sympathy":        SympathySlider.Value       = kp.Value; SympathyValue.Text       = kp.Value.ToString("F2"); break;
                    case "shell_mix":       ShellMixSlider.Value       = kp.Value; ShellMixValue.Text       = kp.Value.ToString("F2"); break;
                    case "stereo_spread":   StereoSpreadSlider.Value   = kp.Value; StereoSpreadValue.Text   = kp.Value.ToString("F2"); break;
                    case "volume":          VolumeSlider.Value         = kp.Value; VolumeValue.Text         = kp.Value.ToString("F1") + " dB"; break;
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
