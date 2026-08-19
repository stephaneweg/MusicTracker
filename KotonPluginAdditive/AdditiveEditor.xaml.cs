using System;
using System.Windows.Controls;
using KotonStudio.Library;

namespace KotonPluginAdditive
{
    public partial class AdditiveEditor : UserControl, IKotonEditor
    {
        readonly AdditivePlugin _p; bool _s, _l = true;
        static readonly string[] Ids = { "tilt", "detune", "attack", "release", "volume" };
        static readonly string[] Fmts = { "F2", "F3", "F0 ms", "F0 ms", "F1 dB" };
        public AdditiveEditor(AdditivePlugin plugin)
        {
            _p = plugin ?? throw new ArgumentNullException(nameof(plugin));
            InitializeComponent();
            foreach (var n in AdditivePlugin.PresetNames) PresetCombo.Items.Add(n);
            PresetCombo.SelectedIndex = 0;
            var sliders = new[] { S1, S2, S3, S4, S5 };
            var vals = new[] { V1, V2, V3, V4, V5 };
            for (int i = 0; i < 5; i++) { int k = i; sliders[i].ValueChanged += (x, e) => { if (_s || _l) return; _p.SetParam(Ids[k], e.NewValue); vals[k].Text = Fmt(e.NewValue, Fmts[k]); }; }
            Refresh(); _l = false;
        }
        static string Fmt(double v, string f) { var parts = f.Split(' '); string form = parts[0]; string suf = parts.Length > 1 ? " " + parts[1] : ""; return v.ToString(form) + suf; }
        void Refresh()
        {
            _s = true;
            var sliders = new[] { S1, S2, S3, S4, S5 };
            var vals = new[] { V1, V2, V3, V4, V5 };
            for (int i = 0; i < 5; i++) foreach (var kp in _p.Parameters) if (kp.Id == Ids[i]) { sliders[i].Value = kp.Value; vals[i].Text = Fmt(kp.Value, Fmts[i]); break; }
            _s = false;
        }
        void Preset_Changed(object sender, SelectionChangedEventArgs e) { if (_l || _s) return; _p.LoadPreset(PresetCombo.SelectedIndex); }
        public void OnContextUpdated(KotonRenderContext ctx) { }
    }
}
