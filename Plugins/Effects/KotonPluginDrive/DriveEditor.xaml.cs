using System;
using System.Windows.Controls;
using KotonStudio.Library;

namespace KotonPluginDrive
{
    public partial class DriveEditor : UserControl, IKotonEditor
    {
        readonly DrivePlugin _plugin;
        bool _syncing, _loading = true;

        public DriveEditor(DrivePlugin plugin)
        {
            _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
            InitializeComponent();

            PresetCombo.Items.Clear();
            PresetCombo.Items.Add("— Custom —");
            foreach (var n in DrivePlugin.PresetNames) PresetCombo.Items.Add(n);
            PresetCombo.SelectedIndex = 0;

            foreach (var n in DrivePlugin.TypeNames) TypeCombo.Items.Add(n);

            Wire(DriveSlider, DriveValue, "drive", v => v.ToString("F0") + " dB");
            Wire(BassSlider, BassValue, "bass_cut", v => v.ToString("F0") + " Hz");
            Wire(BiasSlider, BiasValue, "bias", v => v.ToString("F2"));
            Wire(ToneSlider, ToneValue, "tone", v => v.ToString("F2"));
            Wire(MixSlider, MixValue, "mix", v => v.ToString("F2"));
            Wire(LevelSlider, LevelValue, "level", v => v.ToString("F1") + " dB");

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
                    case "type": TypeCombo.SelectedIndex = Math.Max(0, Math.Min(DrivePlugin.TypeNames.Length - 1, (int)Math.Round(kp.Value))); break;
                    case "drive": DriveSlider.Value = kp.Value; DriveValue.Text = kp.Value.ToString("F0") + " dB"; break;
                    case "bass_cut": BassSlider.Value = kp.Value; BassValue.Text = kp.Value.ToString("F0") + " Hz"; break;
                    case "bias": BiasSlider.Value = kp.Value; BiasValue.Text = kp.Value.ToString("F2"); break;
                    case "tone": ToneSlider.Value = kp.Value; ToneValue.Text = kp.Value.ToString("F2"); break;
                    case "mix": MixSlider.Value = kp.Value; MixValue.Text = kp.Value.ToString("F2"); break;
                    case "level": LevelSlider.Value = kp.Value; LevelValue.Text = kp.Value.ToString("F1") + " dB"; break;
                }
            }
            _syncing = false;
        }

        void TypeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_syncing || _loading) return;
            _plugin.SetParam("type", TypeCombo.SelectedIndex);
            if (PresetCombo.SelectedIndex != 0) PresetCombo.SelectedIndex = 0;
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
