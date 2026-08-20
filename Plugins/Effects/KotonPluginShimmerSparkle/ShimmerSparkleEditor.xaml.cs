using System;
using System.Windows.Controls;
using KotonStudio.Library;

namespace KotonPluginShimmerSparkle
{
    public partial class ShimmerSparkleEditor : UserControl, IKotonEditor
    {
        readonly ShimmerSparklePlugin _plugin;
        bool _syncing, _loading = true;

        static readonly string[] KeyNames = { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };
        static readonly string[] ScaleNames = { "Majeur", "Mineur", "Penta majeur", "Penta mineur", "Chromatique" };

        public ShimmerSparkleEditor(ShimmerSparklePlugin plugin)
        {
            _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
            InitializeComponent();
            PresetCombo.Items.Clear();
            PresetCombo.Items.Add("— Custom —");
            foreach (var n in ShimmerSparklePlugin.PresetNames) PresetCombo.Items.Add(n);
            PresetCombo.SelectedIndex = 0;

            KeyCombo.Items.Clear(); foreach (var k in KeyNames) KeyCombo.Items.Add(k);
            ScaleCombo.Items.Clear(); foreach (var s in ScaleNames) ScaleCombo.Items.Add(s);

            Wire(SizeSlider,          SizeValue,          "size",             v => v.ToString("F2"));
            Wire(DecaySlider,         DecayValue,         "decay",            v => v.ToString("F2"));
            Wire(DampingSlider,       DampingValue,       "damping",          v => v.ToString("F2"));
            Wire(PreDelaySlider,      PreDelayValue,      "pre_delay",        v => v.ToString("F0") + " ms");
            Wire(ShimmerSlider,       ShimmerValue,       "shimmer",          v => v.ToString("F2"));
            Wire(ShimmerSemisSlider,  ShimmerSemisValue,  "shimmer_semis",    v => v.ToString("F0") + " st");
            Wire(SparkleAmtSlider,    SparkleAmtValue,    "sparkle_amount",   v => v.ToString("F2"));
            Wire(SparkleGainSlider,   SparkleGainValue,   "sparkle_gain",     v => v.ToString("F2"));
            Wire(SparkleDecaySlider,  SparkleDecayValue,  "sparkle_decay",    v => v.ToString("F0") + " ms");
            Wire(SparkleLoSlider,     SparkleLoValue,     "sparkle_lo",       v => v.ToString("F0"));
            Wire(SparkleHiSlider,     SparkleHiValue,     "sparkle_hi",       v => v.ToString("F0"));
            Wire(MixSlider,           MixValue,           "mix",              v => v.ToString("F2"));
            Wire(OutGainSlider,       OutGainValue,       "out_gain",         v => v.ToString("F1") + " dB");

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
                    case "size":            SizeSlider.Value = kp.Value; SizeValue.Text = kp.Value.ToString("F2"); break;
                    case "decay":           DecaySlider.Value = kp.Value; DecayValue.Text = kp.Value.ToString("F2"); break;
                    case "damping":         DampingSlider.Value = kp.Value; DampingValue.Text = kp.Value.ToString("F2"); break;
                    case "pre_delay":       PreDelaySlider.Value = kp.Value; PreDelayValue.Text = kp.Value.ToString("F0") + " ms"; break;
                    case "shimmer":         ShimmerSlider.Value = kp.Value; ShimmerValue.Text = kp.Value.ToString("F2"); break;
                    case "shimmer_semis":   ShimmerSemisSlider.Value = kp.Value; ShimmerSemisValue.Text = kp.Value.ToString("F0") + " st"; break;
                    case "sparkle_amount":  SparkleAmtSlider.Value = kp.Value; SparkleAmtValue.Text = kp.Value.ToString("F2"); break;
                    case "sparkle_gain":    SparkleGainSlider.Value = kp.Value; SparkleGainValue.Text = kp.Value.ToString("F2"); break;
                    case "sparkle_decay":   SparkleDecaySlider.Value = kp.Value; SparkleDecayValue.Text = kp.Value.ToString("F0") + " ms"; break;
                    case "sparkle_lo":      SparkleLoSlider.Value = kp.Value; SparkleLoValue.Text = kp.Value.ToString("F0"); break;
                    case "sparkle_hi":      SparkleHiSlider.Value = kp.Value; SparkleHiValue.Text = kp.Value.ToString("F0"); break;
                    case "sparkle_key":     KeyCombo.SelectedIndex = (int)Math.Round(kp.Value); break;
                    case "sparkle_scale":   ScaleCombo.SelectedIndex = (int)Math.Round(kp.Value); break;
                    case "sparkle_trigger": TrigCheck.IsChecked = kp.Value > 0.5; break;
                    case "mix":             MixSlider.Value = kp.Value; MixValue.Text = kp.Value.ToString("F2"); break;
                    case "out_gain":        OutGainSlider.Value = kp.Value; OutGainValue.Text = kp.Value.ToString("F1") + " dB"; break;
                }
            }
            _syncing = false;
        }

        void PresetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || _syncing) return;
            int idx = PresetCombo.SelectedIndex;
            if (idx <= 0) return;
            _plugin.LoadPreset(idx - 1, MixLockCheck.IsChecked == true);
            Refresh();
        }
        void KeyCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || _syncing) return;
            _plugin.SetParam("sparkle_key", KeyCombo.SelectedIndex);
            if (PresetCombo.SelectedIndex != 0) PresetCombo.SelectedIndex = 0;
        }
        void ScaleCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || _syncing) return;
            _plugin.SetParam("sparkle_scale", ScaleCombo.SelectedIndex);
            if (PresetCombo.SelectedIndex != 0) PresetCombo.SelectedIndex = 0;
        }
        void TrigCheck_Changed(object sender, System.Windows.RoutedEventArgs e)
        {
            if (_loading || _syncing) return;
            _plugin.SetParam("sparkle_trigger", TrigCheck.IsChecked == true ? 1 : 0);
            if (PresetCombo.SelectedIndex != 0) PresetCombo.SelectedIndex = 0;
        }

        public void OnContextUpdated(KotonRenderContext ctx) { }
    }
}
