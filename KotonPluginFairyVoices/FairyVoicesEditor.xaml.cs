using System;
using System.Windows.Controls;
using KotonStudio.Library;

namespace KotonPluginFairyVoices
{
    public partial class FairyVoicesEditor : UserControl, IKotonEditor
    {
        readonly FairyVoicesPlugin _plugin;
        bool _syncing, _loading = true;

        public FairyVoicesEditor(FairyVoicesPlugin plugin)
        {
            _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
            InitializeComponent();
            PresetCombo.Items.Clear();
            PresetCombo.Items.Add("— Custom —");
            foreach (var n in FairyVoicesPlugin.PresetNames) PresetCombo.Items.Add(n);
            PresetCombo.SelectedIndex = 0;
            foreach (var n in VowelData.Names) VowelCombo.Items.Add(n);

            Wire(VoicesSlider,     VoicesValue,     "voices",     v => v.ToString("F0"));
            Wire(SpreadSlider,     SpreadValue,     "spread",     v => v.ToString("F0") + " ct");
            Wire(FormantQSlider,   FormantQValue,   "formant_q",  v => v.ToString("F1"));
            Wire(BrightnessSlider, BrightnessValue, "brightness", v => v.ToString("F2"));
            Wire(AirSlider,        AirValue,        "air",        v => v.ToString("F2"));
            Wire(VibRateSlider,    VibRateValue,    "vib_rate",   v => v.ToString("F2") + " Hz");
            Wire(VibDepthSlider,   VibDepthValue,   "vib_depth",  v => v.ToString("F0") + " ct");
            Wire(AttackSlider,     AttackValue,     "attack",     v => v.ToString("F0") + " ms");
            Wire(ReleaseSlider,    ReleaseValue,    "release",    v => v.ToString("F0") + " ms");
            Wire(VolumeSlider,     VolumeValue,     "volume",     v => v.ToString("F1") + " dB");

            Refresh();
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
        void Refresh()
        {
            _syncing = true;
            foreach (var kp in _plugin.Parameters)
            {
                switch (kp.Id)
                {
                    case "voices":     VoicesSlider.Value = kp.Value; VoicesValue.Text = kp.Value.ToString("F0"); break;
                    case "spread":     SpreadSlider.Value = kp.Value; SpreadValue.Text = kp.Value.ToString("F0") + " ct"; break;
                    case "vowel":      VowelCombo.SelectedIndex = (int)Math.Round(kp.Value); break;
                    case "formant_q":  FormantQSlider.Value = kp.Value; FormantQValue.Text = kp.Value.ToString("F1"); break;
                    case "brightness": BrightnessSlider.Value = kp.Value; BrightnessValue.Text = kp.Value.ToString("F2"); break;
                    case "air":        AirSlider.Value = kp.Value; AirValue.Text = kp.Value.ToString("F2"); break;
                    case "vib_rate":   VibRateSlider.Value = kp.Value; VibRateValue.Text = kp.Value.ToString("F2") + " Hz"; break;
                    case "vib_depth":  VibDepthSlider.Value = kp.Value; VibDepthValue.Text = kp.Value.ToString("F0") + " ct"; break;
                    case "attack":     AttackSlider.Value = kp.Value; AttackValue.Text = kp.Value.ToString("F0") + " ms"; break;
                    case "release":    ReleaseSlider.Value = kp.Value; ReleaseValue.Text = kp.Value.ToString("F0") + " ms"; break;
                    case "volume":     VolumeSlider.Value = kp.Value; VolumeValue.Text = kp.Value.ToString("F1") + " dB"; break;
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
        void VowelCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || _syncing) return;
            _plugin.SetParam("vowel", VowelCombo.SelectedIndex);
            if (PresetCombo.SelectedIndex != 0) PresetCombo.SelectedIndex = 0;
        }
        public void OnContextUpdated(KotonRenderContext ctx) { }
    }
}
