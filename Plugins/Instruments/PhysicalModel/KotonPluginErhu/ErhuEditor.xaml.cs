using System;
using System.Windows.Controls;
using KotonStudio.Library;

namespace KotonPluginErhu
{
    public partial class ErhuEditor : UserControl, IKotonEditor
    {
        readonly ErhuPlugin _plugin;
        bool _syncing, _loading = true;

        public ErhuEditor(ErhuPlugin plugin)
        {
            _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
            InitializeComponent();
            PresetCombo.Items.Clear();
            PresetCombo.Items.Add("— Custom —");
            foreach (var n in ErhuPlugin.PresetNames) PresetCombo.Items.Add(n);
            PresetCombo.SelectedIndex = 0;
            Wire(BowSlider, BowValue, "bow_pressure", v => v.ToString("F2"));
            Wire(NoiseSlider, NoiseValue, "bow_noise", v => v.ToString("F2"));
            Wire(BrightSlider, BrightValue, "brightness", v => v.ToString("F2"));
            Wire(GlideSlider, GlideValue, "glide_ms", v => v.ToString("F0") + " ms");
            Wire(FormantHzSlider, FormantHzValue, "formant_hz", v => v.ToString("F0") + " Hz");
            Wire(FormantQSlider, FormantQValue, "formant_q", v => v.ToString("F1"));
            Wire(VibRateSlider, VibRateValue, "vib_rate", v => v.ToString("F2") + " Hz");
            Wire(VibDepthSlider, VibDepthValue, "vib_depth", v => v.ToString("F0") + " ct");
            Wire(AttackSlider, AttackValue, "attack", v => v.ToString("F0") + " ms");
            Wire(ReleaseSlider, ReleaseValue, "release", v => v.ToString("F0") + " ms");
            Wire(VolumeSlider, VolumeValue, "volume", v => v.ToString("F1") + " dB");
            Refresh(); _loading = false;
        }
        void Wire(Slider s, TextBlock lbl, string id, Func<double, string> fmt) { s.ValueChanged += (x, e) => { if (_syncing || _loading) return; _plugin.SetParam(id, e.NewValue); lbl.Text = fmt(e.NewValue); if (PresetCombo.SelectedIndex != 0) PresetCombo.SelectedIndex = 0; }; }
        void Refresh()
        {
            _syncing = true;
            foreach (var kp in _plugin.Parameters)
                switch (kp.Id)
                {
                    case "bow_pressure": BowSlider.Value = kp.Value; BowValue.Text = kp.Value.ToString("F2"); break;
                    case "bow_noise":    NoiseSlider.Value = kp.Value; NoiseValue.Text = kp.Value.ToString("F2"); break;
                    case "brightness":   BrightSlider.Value = kp.Value; BrightValue.Text = kp.Value.ToString("F2"); break;
                    case "glide_ms":     GlideSlider.Value = kp.Value; GlideValue.Text = kp.Value.ToString("F0") + " ms"; break;
                    case "formant_hz":   FormantHzSlider.Value = kp.Value; FormantHzValue.Text = kp.Value.ToString("F0") + " Hz"; break;
                    case "formant_q":    FormantQSlider.Value = kp.Value; FormantQValue.Text = kp.Value.ToString("F1"); break;
                    case "vib_rate":     VibRateSlider.Value = kp.Value; VibRateValue.Text = kp.Value.ToString("F2") + " Hz"; break;
                    case "vib_depth":    VibDepthSlider.Value = kp.Value; VibDepthValue.Text = kp.Value.ToString("F0") + " ct"; break;
                    case "attack":       AttackSlider.Value = kp.Value; AttackValue.Text = kp.Value.ToString("F0") + " ms"; break;
                    case "release":      ReleaseSlider.Value = kp.Value; ReleaseValue.Text = kp.Value.ToString("F0") + " ms"; break;
                    case "volume":       VolumeSlider.Value = kp.Value; VolumeValue.Text = kp.Value.ToString("F1") + " dB"; break;
                }
            _syncing = false;
        }
        void PresetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (_loading || _syncing) return; int idx = PresetCombo.SelectedIndex; if (idx <= 0) return; _plugin.LoadPreset(idx - 1); Refresh(); }
        public void OnContextUpdated(KotonRenderContext ctx) { }
    }
}
