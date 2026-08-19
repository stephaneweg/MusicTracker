using System;
using System.Windows.Controls;
using KotonStudio.Library;

namespace KotonPluginBanjo
{
    public partial class BanjoEditor : UserControl, IKotonEditor
    {
        readonly BanjoPlugin _plugin;
        bool _syncing, _loading = true;

        public BanjoEditor(BanjoPlugin plugin)
        {
            _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
            InitializeComponent();
            PresetCombo.Items.Clear(); PresetCombo.Items.Add("— Custom —");
            foreach (var n in BanjoPlugin.PresetNames) PresetCombo.Items.Add(n);
            PresetCombo.SelectedIndex = 0;
            Wire(SustainSlider,  SustainValue,  "sustain",      v => v.ToString("F2"));
            Wire(BrightSlider,   BrightValue,   "brightness",   v => v.ToString("F2"));
            Wire(TwangSlider,    TwangValue,    "twang",        v => v.ToString("F2"));
            Wire(DrumSlider,     DrumValue,     "drum_head",    v => v.ToString("F2"));
            Wire(PluckLenSlider, PluckLenValue, "pluck_length", v => v.ToString("F0") + " ms");
            Wire(PolySlider,     PolyValue,     "polyphony",    v => v.ToString("F0"));
            Wire(AttackSlider,   AttackValue,   "attack",       v => v.ToString("F1") + " ms");
            Wire(ReleaseSlider,  ReleaseValue,  "release",      v => v.ToString("F0") + " ms");
            Wire(VolumeSlider,   VolumeValue,   "volume",       v => v.ToString("F1") + " dB");
            Refresh(); _loading = false;
        }
        void Wire(Slider s, TextBlock lbl, string id, Func<double, string> fmt) { s.ValueChanged += (x, e) => { if (_syncing || _loading) return; _plugin.SetParam(id, e.NewValue); lbl.Text = fmt(e.NewValue); if (PresetCombo.SelectedIndex != 0) PresetCombo.SelectedIndex = 0; }; }
        void Refresh()
        {
            _syncing = true;
            foreach (var kp in _plugin.Parameters)
                switch (kp.Id)
                {
                    case "sustain":     SustainSlider.Value = kp.Value; SustainValue.Text = kp.Value.ToString("F2"); break;
                    case "brightness":  BrightSlider.Value = kp.Value; BrightValue.Text = kp.Value.ToString("F2"); break;
                    case "twang":       TwangSlider.Value = kp.Value; TwangValue.Text = kp.Value.ToString("F2"); break;
                    case "drum_head":   DrumSlider.Value = kp.Value; DrumValue.Text = kp.Value.ToString("F2"); break;
                    case "pluck_length":PluckLenSlider.Value = kp.Value; PluckLenValue.Text = kp.Value.ToString("F0") + " ms"; break;
                    case "polyphony":   PolySlider.Value = kp.Value; PolyValue.Text = kp.Value.ToString("F0"); break;
                    case "attack":      AttackSlider.Value = kp.Value; AttackValue.Text = kp.Value.ToString("F1") + " ms"; break;
                    case "release":     ReleaseSlider.Value = kp.Value; ReleaseValue.Text = kp.Value.ToString("F0") + " ms"; break;
                    case "volume":      VolumeSlider.Value = kp.Value; VolumeValue.Text = kp.Value.ToString("F1") + " dB"; break;
                }
            _syncing = false;
        }
        void PresetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (_loading || _syncing) return; int idx = PresetCombo.SelectedIndex; if (idx <= 0) return; _plugin.LoadPreset(idx - 1); Refresh(); }
        public void OnContextUpdated(KotonRenderContext ctx) { }
    }
}
