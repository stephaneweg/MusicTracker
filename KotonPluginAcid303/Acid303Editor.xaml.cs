using System;
using System.Windows.Controls;
using KotonStudio.Library;

namespace KotonPluginAcid303
{
    public partial class Acid303Editor : UserControl, IKotonEditor
    {
        readonly Acid303Plugin _p; bool _s, _l = true;
        static readonly string[] Ids = { "cutoff", "resonance", "env_mod", "decay", "accent", "slide_ms", "distortion", "volume" };
        static readonly string[] Fmts = { "F0 Hz", "F2", "F2", "F0 ms", "F2", "F0 ms", "F2", "F1 dB" };
        public Acid303Editor(Acid303Plugin plugin)
        {
            _p = plugin ?? throw new ArgumentNullException(nameof(plugin));
            InitializeComponent();
            var sliders = new[] { S1, S2, S3, S4, S5, S6, S7, S8 };
            var vals = new[] { V1, V2, V3, V4, V5, V6, V7, V8 };
            for (int i = 0; i < 8; i++) { int k = i; sliders[i].ValueChanged += (x, e) => { if (_s || _l) return; _p.SetParam(Ids[k], e.NewValue); vals[k].Text = Fmt(e.NewValue, Fmts[k]); }; }
            Refresh(); _l = false;
        }
        static string Fmt(double v, string f) { var parts = f.Split(' '); string form = parts[0]; string suf = parts.Length > 1 ? " " + parts[1] : ""; return v.ToString(form) + suf; }
        void Refresh()
        {
            _s = true;
            var sliders = new[] { S1, S2, S3, S4, S5, S6, S7, S8 };
            var vals = new[] { V1, V2, V3, V4, V5, V6, V7, V8 };
            for (int i = 0; i < 8; i++) foreach (var kp in _p.Parameters) if (kp.Id == Ids[i]) { sliders[i].Value = kp.Value; vals[i].Text = Fmt(kp.Value, Fmts[i]); break; }
            foreach (var kp in _p.Parameters) if (kp.Id == "wave") { WaveCombo.SelectedIndex = (int)Math.Round(kp.Value); break; }
            _s = false;
        }
        void Wave_Changed(object sender, SelectionChangedEventArgs e) { if (_l || _s) return; _p.SetParam("wave", WaveCombo.SelectedIndex); }
        public void OnContextUpdated(KotonRenderContext ctx) { }
    }
}
