using System;
using System.Windows.Controls;
using KotonStudio.Library;

namespace KotonPluginRandomWalk
{
    public partial class RandomWalkEditor : UserControl, IKotonEditor
    {
        readonly RandomWalk _plugin;
        bool _syncing, _loading = true;
        public RandomWalkEditor(RandomWalk plugin)
        {
            _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
            InitializeComponent();
            foreach (var n in RandomWalk.GetScaleNames()) ScaleCombo.Items.Add(n);
            Wire(NpbSlider, NpbValue, "notes_per_beat", v => v.ToString("F0"));
            Wire(StepSlider, StepValue, "step_max", v => v.ToString("F0"));
            Wire(OctSlider, OctValue, "base_octave", v => v.ToString("F0"));
            Wire(RangeSlider, RangeValue, "oct_range", v => v.ToString("F0"));
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
                    case "notes_per_beat": NpbSlider.Value = kp.Value; NpbValue.Text = kp.Value.ToString("F0"); break;
                    case "step_max": StepSlider.Value = kp.Value; StepValue.Text = kp.Value.ToString("F0"); break;
                    case "scale": ScaleCombo.SelectedIndex = (int)Math.Round(kp.Value); break;
                    case "oct_range": RangeSlider.Value = kp.Value; RangeValue.Text = kp.Value.ToString("F0"); break;
                    case "base_octave": OctSlider.Value = kp.Value; OctValue.Text = kp.Value.ToString("F0"); break;
                    case "seed": SeedSlider.Value = kp.Value; SeedValue.Text = kp.Value.ToString("F0"); break;
                    case "velocity": VelSlider.Value = kp.Value; VelValue.Text = kp.Value.ToString("F0"); break;
                    case "articulation": ArtCombo.SelectedIndex = (int)Math.Round(kp.Value); break;
                }
            _syncing = false;
        }
        void Scale_Changed(object sender, SelectionChangedEventArgs e) { if (_loading || _syncing) return; _plugin.SetParam("scale", ScaleCombo.SelectedIndex); }
        void Art_Changed(object sender, SelectionChangedEventArgs e) { if (_loading || _syncing) return; _plugin.SetParam("articulation", ArtCombo.SelectedIndex); }
        public void OnContextUpdated(KotonRenderContext ctx) { }
    }
}
