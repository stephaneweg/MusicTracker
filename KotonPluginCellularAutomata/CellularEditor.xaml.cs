using System;
using System.Windows.Controls;
using KotonStudio.Library;

namespace KotonPluginCellularAutomata
{
    public partial class CellularEditor : UserControl, IKotonEditor
    {
        readonly CellularAutomata _plugin;
        bool _syncing, _loading = true;
        public CellularEditor(CellularAutomata plugin)
        {
            _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
            InitializeComponent();
            foreach (var n in CellularAutomata.ScaleNames) ScaleCombo.Items.Add(n);
            foreach (var n in CellularAutomata.SeedModeNames) ModeCombo.Items.Add(n);
            Wire(RuleSlider, RuleValue, "rule", v => v.ToString("F0"));
            Wire(NpbSlider, NpbValue, "notes_per_beat", v => v.ToString("F0"));
            Wire(WidthSlider, WidthValue, "width", v => v.ToString("F0"));
            Wire(OctSlider, OctValue, "base_octave", v => v.ToString("F0"));
            Wire(RangeSlider, RangeValue, "oct_range", v => v.ToString("F0"));
            Wire(DensitySlider, DensityValue, "density", v => v.ToString("F2"));
            Wire(SeedSlider, SeedValue, "seed", v => v.ToString("F0"));
            Wire(VelSlider, VelValue, "velocity", v => v.ToString("F0"));
            Refresh(); _loading = false;
        }
        void Wire(Slider s, TextBlock lbl, string id, Func<double, string> fmt) { s.ValueChanged += (x, e) => { if (_syncing || _loading) return; _plugin.SetParam(id, e.NewValue); lbl.Text = fmt(e.NewValue); }; }
        void Refresh()
        {
            _syncing = true;
            foreach (var kp in _plugin.Parameters)
                switch (kp.Id)
                {
                    case "rule": RuleSlider.Value = kp.Value; RuleValue.Text = kp.Value.ToString("F0"); break;
                    case "notes_per_beat": NpbSlider.Value = kp.Value; NpbValue.Text = kp.Value.ToString("F0"); break;
                    case "width": WidthSlider.Value = kp.Value; WidthValue.Text = kp.Value.ToString("F0"); break;
                    case "scale": ScaleCombo.SelectedIndex = (int)Math.Round(kp.Value); break;
                    case "base_octave": OctSlider.Value = kp.Value; OctValue.Text = kp.Value.ToString("F0"); break;
                    case "oct_range": RangeSlider.Value = kp.Value; RangeValue.Text = kp.Value.ToString("F0"); break;
                    case "seed": SeedSlider.Value = kp.Value; SeedValue.Text = kp.Value.ToString("F0"); break;
                    case "seed_mode": ModeCombo.SelectedIndex = (int)Math.Round(kp.Value); break;
                    case "density": DensitySlider.Value = kp.Value; DensityValue.Text = kp.Value.ToString("F2"); break;
                    case "velocity": VelSlider.Value = kp.Value; VelValue.Text = kp.Value.ToString("F0"); break;
                    case "articulation": ArtCombo.SelectedIndex = (int)Math.Round(kp.Value); break;
                }
            _syncing = false;
        }
        void Scale_Changed(object sender, SelectionChangedEventArgs e) { if (_loading || _syncing) return; _plugin.SetParam("scale", ScaleCombo.SelectedIndex); }
        void Mode_Changed(object sender, SelectionChangedEventArgs e) { if (_loading || _syncing) return; _plugin.SetParam("seed_mode", ModeCombo.SelectedIndex); }
        void Art_Changed(object sender, SelectionChangedEventArgs e) { if (_loading || _syncing) return; _plugin.SetParam("articulation", ArtCombo.SelectedIndex); }
        public void OnContextUpdated(KotonRenderContext ctx) { }
    }
}
