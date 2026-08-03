using System;
using System.Windows.Controls;
using KotonStudio.Library;

namespace KotonPluginVowelSynth
{
    public partial class VowelSynthEditor : UserControl, IKotonEditor
    {
        readonly VowelSynthPlugin _plugin;
        bool _syncing, _loading = true;

        public VowelSynthEditor(VowelSynthPlugin plugin)
        {
            _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
            InitializeComponent();
            PresetCombo.Items.Clear();
            PresetCombo.Items.Add("— Custom —");
            foreach (var n in VowelSynthPlugin.PresetNames) PresetCombo.Items.Add(n);
            PresetCombo.SelectedIndex = 0;
            foreach (var n in VowelData.Names) { VowelACombo.Items.Add(n); VowelBCombo.Items.Add(n); }

            Wire(MorphSlider,       MorphValue,       "morph",        v => v.ToString("F2"));
            Wire(FormantQSlider,    FormantQValue,    "formant_q",    v => v.ToString("F1"));
            Wire(FormantGainSlider, FormantGainValue, "formant_gain", v => v.ToString("F2"));
            Wire(WahRateSlider,     WahRateValue,     "wah_rate",     v => v.ToString("F2") + " Hz");
            Wire(WahDepthSlider,    WahDepthValue,    "wah_depth",    v => v.ToString("F2"));
            Wire(DetuneSlider,      DetuneValue,      "detune",       v => v.ToString("F0") + " ct");
            Wire(SubSlider,         SubValue,         "sub",          v => v.ToString("F2"));
            Wire(DriveSlider,       DriveValue,       "drive",        v => v.ToString("F2"));
            Wire(AttackSlider,      AttackValue,      "attack",       v => v.ToString("F0") + " ms");
            Wire(ReleaseSlider,     ReleaseValue,     "release",      v => v.ToString("F0") + " ms");
            Wire(VibDepthSlider,    VibDepthValue,    "vib_depth",    v => v.ToString("F0") + " ct");
            Wire(VibRateSlider,     VibRateValue,     "vib_rate",     v => v.ToString("F2") + " Hz");
            Wire(VolumeSlider,      VolumeValue,      "volume",       v => v.ToString("F1") + " dB");
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
                    case "vowel_a": VowelACombo.SelectedIndex = (int)Math.Round(kp.Value); break;
                    case "vowel_b": VowelBCombo.SelectedIndex = (int)Math.Round(kp.Value); break;
                    case "morph": MorphSlider.Value = kp.Value; MorphValue.Text = kp.Value.ToString("F2"); break;
                    case "formant_q": FormantQSlider.Value = kp.Value; FormantQValue.Text = kp.Value.ToString("F1"); break;
                    case "formant_gain": FormantGainSlider.Value = kp.Value; FormantGainValue.Text = kp.Value.ToString("F2"); break;
                    case "wah_rate": WahRateSlider.Value = kp.Value; WahRateValue.Text = kp.Value.ToString("F2") + " Hz"; break;
                    case "wah_depth": WahDepthSlider.Value = kp.Value; WahDepthValue.Text = kp.Value.ToString("F2"); break;
                    case "detune": DetuneSlider.Value = kp.Value; DetuneValue.Text = kp.Value.ToString("F0") + " ct"; break;
                    case "sub": SubSlider.Value = kp.Value; SubValue.Text = kp.Value.ToString("F2"); break;
                    case "drive": DriveSlider.Value = kp.Value; DriveValue.Text = kp.Value.ToString("F2"); break;
                    case "attack": AttackSlider.Value = kp.Value; AttackValue.Text = kp.Value.ToString("F0") + " ms"; break;
                    case "release": ReleaseSlider.Value = kp.Value; ReleaseValue.Text = kp.Value.ToString("F0") + " ms"; break;
                    case "vib_depth": VibDepthSlider.Value = kp.Value; VibDepthValue.Text = kp.Value.ToString("F0") + " ct"; break;
                    case "vib_rate": VibRateSlider.Value = kp.Value; VibRateValue.Text = kp.Value.ToString("F2") + " Hz"; break;
                    case "volume": VolumeSlider.Value = kp.Value; VolumeValue.Text = kp.Value.ToString("F1") + " dB"; break;
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

        void VowelACombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || _syncing) return;
            _plugin.SetParam("vowel_a", VowelACombo.SelectedIndex);
            if (PresetCombo.SelectedIndex != 0) PresetCombo.SelectedIndex = 0;
        }
        void VowelBCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || _syncing) return;
            _plugin.SetParam("vowel_b", VowelBCombo.SelectedIndex);
            if (PresetCombo.SelectedIndex != 0) PresetCombo.SelectedIndex = 0;
        }

        public void OnContextUpdated(KotonRenderContext ctx) { }
    }
}
