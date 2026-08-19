using System;
using System.Windows.Controls;
using KotonStudio.Library;

namespace KotonPluginHarpsichord
{
    public partial class HarpsichordEditor : UserControl, IKotonEditor
    {
        readonly HarpsichordPlugin _plugin;
        bool _syncing, _loading = true;

        public HarpsichordEditor(HarpsichordPlugin plugin)
        {
            _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
            InitializeComponent();
            PresetCombo.Items.Clear(); PresetCombo.Items.Add("— Custom —");
            foreach (var n in HarpsichordPlugin.PresetNames) PresetCombo.Items.Add(n);
            PresetCombo.SelectedIndex = 0;

            Wire(SustainSlider, SustainValue, "sustain", v => v.ToString("F2"));
            Wire(BrightSlider,  BrightValue,  "brightness", v => v.ToString("F2"));
            Wire(ClickSlider,   ClickValue,   "pluck_click", v => v.ToString("F2"));
            Wire(BodySlider,    BodyValue,    "body_resonance", v => v.ToString("F2"));
            Wire(DetuneSlider,  DetuneValue,  "choir_detune", v => v.ToString("F1") + " ct");
            Wire(MixSlider,     MixValue,     "choir_mix", v => v.ToString("F2"));
            Wire(PolySlider,    PolyValue,    "polyphony", v => v.ToString("F0"));
            Wire(AttackSlider,  AttackValue,  "attack", v => v.ToString("F1") + " ms");
            Wire(ReleaseSlider, ReleaseValue, "release", v => v.ToString("F0") + " ms");
            Wire(VolumeSlider,  VolumeValue,  "volume", v => v.ToString("F1") + " dB");
            Refresh(); _loading = false;
        }
        void Wire(Slider s, TextBlock lbl, string id, Func<double, string> fmt) { s.ValueChanged += (x, e) => { if (_syncing || _loading) return; _plugin.SetParam(id, e.NewValue); lbl.Text = fmt(e.NewValue); if (PresetCombo.SelectedIndex != 0) PresetCombo.SelectedIndex = 0; }; }
        void Refresh()
        {
            _syncing = true;
            foreach (var kp in _plugin.Parameters)
                switch (kp.Id)
                {
                    case "sustain": SustainSlider.Value = kp.Value; SustainValue.Text = kp.Value.ToString("F2"); break;
                    case "brightness": BrightSlider.Value = kp.Value; BrightValue.Text = kp.Value.ToString("F2"); break;
                    case "pluck_click": ClickSlider.Value = kp.Value; ClickValue.Text = kp.Value.ToString("F2"); break;
                    case "body_resonance": BodySlider.Value = kp.Value; BodyValue.Text = kp.Value.ToString("F2"); break;
                    case "choir_detune": DetuneSlider.Value = kp.Value; DetuneValue.Text = kp.Value.ToString("F1") + " ct"; break;
                    case "choir_mix": MixSlider.Value = kp.Value; MixValue.Text = kp.Value.ToString("F2"); break;
                    case "register_4ft": RegisterCombo.SelectedIndex = kp.Value > 0.5 ? 1 : 0; break;
                    case "polyphony": PolySlider.Value = kp.Value; PolyValue.Text = kp.Value.ToString("F0"); break;
                    case "attack": AttackSlider.Value = kp.Value; AttackValue.Text = kp.Value.ToString("F1") + " ms"; break;
                    case "release": ReleaseSlider.Value = kp.Value; ReleaseValue.Text = kp.Value.ToString("F0") + " ms"; break;
                    case "volume": VolumeSlider.Value = kp.Value; VolumeValue.Text = kp.Value.ToString("F1") + " dB"; break;
                }
            _syncing = false;
        }
        void PresetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (_loading || _syncing) return; int idx = PresetCombo.SelectedIndex; if (idx <= 0) return; _plugin.LoadPreset(idx - 1); Refresh(); }
        void RegisterCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (_loading || _syncing) return; _plugin.SetParam("register_4ft", RegisterCombo.SelectedIndex); if (PresetCombo.SelectedIndex != 0) PresetCombo.SelectedIndex = 0; }
        public void OnContextUpdated(KotonRenderContext ctx) { }
    }
}
