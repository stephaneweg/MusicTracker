using System;
using System.Windows.Controls;
using KotonStudio.Library;

namespace KotonPluginGuqin
{
    public partial class GuqinEditor : UserControl, IKotonEditor
    {
        readonly GuqinPlugin _plugin;
        bool _syncing, _loading = true;

        public GuqinEditor(GuqinPlugin plugin)
        {
            _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
            InitializeComponent();
            PresetCombo.Items.Clear();
            PresetCombo.Items.Add("— Custom —");
            foreach (var n in GuqinPlugin.PresetNames) PresetCombo.Items.Add(n);
            PresetCombo.SelectedIndex = 0;

            Wire(SustainSlider,    SustainValue,    "sustain",         v => v.ToString("F2"));
            Wire(HFDampingSlider,  HFDampingValue,  "hf_damping",      v => v.ToString("F2"));
            Wire(BodySlider,       BodyValue,       "body_resonance",  v => v.ToString("F2"));
            Wire(PolySlider,       PolyValue,       "polyphony",       v => v.ToString("F0"));
            Wire(PluckBrSlider,    PluckBrValue,    "pluck_brightness",v => v.ToString("F2"));
            Wire(PluckLenSlider,   PluckLenValue,   "pluck_length",    v => v.ToString("F0") + " ms");
            Wire(GlideMsSlider,    GlideMsValue,    "glide_ms",        v => v.ToString("F0") + " ms");
            Wire(GlideCurveSlider, GlideCurveValue, "glide_curve",     v => v.ToString("F2"));
            Wire(VibRateSlider,    VibRateValue,    "vib_rate",        v => v.ToString("F2") + " Hz");
            Wire(VibDepthSlider,   VibDepthValue,   "vib_depth",       v => v.ToString("F0") + " ct");
            Wire(AttackSlider,     AttackValue,     "attack",          v => v.ToString("F0") + " ms");
            Wire(ReleaseSlider,    ReleaseValue,    "release",         v => v.ToString("F0") + " ms");
            Wire(VolumeSlider,     VolumeValue,     "volume",          v => v.ToString("F1") + " dB");
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
                    case "sustain":         SustainSlider.Value = kp.Value; SustainValue.Text = kp.Value.ToString("F2"); break;
                    case "hf_damping":      HFDampingSlider.Value = kp.Value; HFDampingValue.Text = kp.Value.ToString("F2"); break;
                    case "body_resonance":  BodySlider.Value = kp.Value; BodyValue.Text = kp.Value.ToString("F2"); break;
                    case "polyphony":       PolySlider.Value = kp.Value; PolyValue.Text = kp.Value.ToString("F0"); break;
                    case "pluck_brightness":PluckBrSlider.Value = kp.Value; PluckBrValue.Text = kp.Value.ToString("F2"); break;
                    case "pluck_length":    PluckLenSlider.Value = kp.Value; PluckLenValue.Text = kp.Value.ToString("F0") + " ms"; break;
                    case "glide_ms":        GlideMsSlider.Value = kp.Value; GlideMsValue.Text = kp.Value.ToString("F0") + " ms"; break;
                    case "glide_curve":     GlideCurveSlider.Value = kp.Value; GlideCurveValue.Text = kp.Value.ToString("F2"); break;
                    case "vib_rate":        VibRateSlider.Value = kp.Value; VibRateValue.Text = kp.Value.ToString("F2") + " Hz"; break;
                    case "vib_depth":       VibDepthSlider.Value = kp.Value; VibDepthValue.Text = kp.Value.ToString("F0") + " ct"; break;
                    case "attack":          AttackSlider.Value = kp.Value; AttackValue.Text = kp.Value.ToString("F0") + " ms"; break;
                    case "release":         ReleaseSlider.Value = kp.Value; ReleaseValue.Text = kp.Value.ToString("F0") + " ms"; break;
                    case "volume":          VolumeSlider.Value = kp.Value; VolumeValue.Text = kp.Value.ToString("F1") + " dB"; break;
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
        public void OnContextUpdated(KotonRenderContext ctx) { }
    }
}
