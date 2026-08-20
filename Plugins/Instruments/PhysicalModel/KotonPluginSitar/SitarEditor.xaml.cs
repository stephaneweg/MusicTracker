using System;
using System.Windows.Controls;
using KotonStudio.Library;

namespace KotonPluginSitar
{
    public partial class SitarEditor : UserControl, IKotonEditor
    {
        readonly SitarPlugin _plugin;
        bool _syncing, _loading = true;

        public SitarEditor(SitarPlugin plugin)
        {
            _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
            InitializeComponent();
            PresetCombo.Items.Clear();
            PresetCombo.Items.Add("— Custom —");
            foreach (var n in SitarPlugin.PresetNames) PresetCombo.Items.Add(n);
            PresetCombo.SelectedIndex = 0;

            Wire(SustainSlider,  SustainValue,  "sustain",      v => v.ToString("F2"));
            Wire(JawariSlider,   JawariValue,   "jawari",       v => v.ToString("F2"));
            Wire(BrightSlider,   BrightValue,   "brightness",   v => v.ToString("F2"));
            Wire(PluckLenSlider, PluckLenValue, "pluck_length", v => v.ToString("F0") + " ms");
            Wire(SympLvlSlider,  SympLvlValue,  "symp_level",   v => v.ToString("F2"));
            Wire(SympDecSlider,  SympDecValue,  "symp_decay",   v => v.ToString("F2"));
            Wire(VibRateSlider,  VibRateValue,  "vib_rate",     v => v.ToString("F2") + " Hz");
            Wire(VibDepthSlider, VibDepthValue, "vib_depth",    v => v.ToString("F0") + " ct");
            Wire(PolySlider,     PolyValue,     "polyphony",    v => v.ToString("F0"));
            Wire(AttackSlider,   AttackValue,   "attack",       v => v.ToString("F0") + " ms");
            Wire(ReleaseSlider,  ReleaseValue,  "release",      v => v.ToString("F0") + " ms");
            Wire(VolumeSlider,   VolumeValue,   "volume",       v => v.ToString("F1") + " dB");
            Refresh();
            _loading = false;
        }
        void Wire(Slider s, TextBlock lbl, string id, Func<double, string> fmt)
        {
            s.ValueChanged += (x, e) => { if (_syncing || _loading) return; _plugin.SetParam(id, e.NewValue); lbl.Text = fmt(e.NewValue); if (PresetCombo.SelectedIndex != 0) PresetCombo.SelectedIndex = 0; };
        }
        void Refresh()
        {
            _syncing = true;
            foreach (var kp in _plugin.Parameters)
            {
                switch (kp.Id)
                {
                    case "sustain":     SustainSlider.Value = kp.Value; SustainValue.Text = kp.Value.ToString("F2"); break;
                    case "jawari":      JawariSlider.Value = kp.Value; JawariValue.Text = kp.Value.ToString("F2"); break;
                    case "brightness":  BrightSlider.Value = kp.Value; BrightValue.Text = kp.Value.ToString("F2"); break;
                    case "pluck_length":PluckLenSlider.Value = kp.Value; PluckLenValue.Text = kp.Value.ToString("F0") + " ms"; break;
                    case "symp_level":  SympLvlSlider.Value = kp.Value; SympLvlValue.Text = kp.Value.ToString("F2"); break;
                    case "symp_decay":  SympDecSlider.Value = kp.Value; SympDecValue.Text = kp.Value.ToString("F2"); break;
                    case "vib_rate":    VibRateSlider.Value = kp.Value; VibRateValue.Text = kp.Value.ToString("F2") + " Hz"; break;
                    case "vib_depth":   VibDepthSlider.Value = kp.Value; VibDepthValue.Text = kp.Value.ToString("F0") + " ct"; break;
                    case "polyphony":   PolySlider.Value = kp.Value; PolyValue.Text = kp.Value.ToString("F0"); break;
                    case "attack":      AttackSlider.Value = kp.Value; AttackValue.Text = kp.Value.ToString("F0") + " ms"; break;
                    case "release":     ReleaseSlider.Value = kp.Value; ReleaseValue.Text = kp.Value.ToString("F0") + " ms"; break;
                    case "volume":      VolumeSlider.Value = kp.Value; VolumeValue.Text = kp.Value.ToString("F1") + " dB"; break;
                }
            }
            _syncing = false;
        }
        void PresetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || _syncing) return;
            int idx = PresetCombo.SelectedIndex; if (idx <= 0) return;
            _plugin.LoadPreset(idx - 1); Refresh();
        }
        public void OnContextUpdated(KotonRenderContext ctx) { }
    }
}
