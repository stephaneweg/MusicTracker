using System;
using System.Windows.Controls;
using KotonStudio.Library;

namespace KotonPluginChoir
{
    public partial class ChoirEditor : UserControl, IKotonEditor
    {
        readonly ChoirPlugin _p; bool _s, _l = true;
        static readonly string[] Ids = { "sub_voices", "detune", "pan_spread", "morph_rate", "morph_amount", "formant_q", "air", "attack", "release", "volume" };
        static readonly string[] Fmts = { "F0", "F0 ct", "F2", "F3 Hz", "F2", "F1", "F2", "F0 ms", "F0 ms", "F1 dB" };
        public ChoirEditor(ChoirPlugin plugin)
        {
            _p = plugin ?? throw new ArgumentNullException(nameof(plugin));
            InitializeComponent();
            var sliders = new[] { S1, S2, S3, S4, S5, S6, S7, S8, S9, S10 };
            var vals = new[] { V1, V2, V3, V4, V5, V6, V7, V8, V9, V10 };
            for (int i = 0; i < 10; i++) { int k = i; sliders[i].ValueChanged += (x, e) => { if (_s || _l) return; _p.SetParam(Ids[k], e.NewValue); vals[k].Text = Fmt(e.NewValue, Fmts[k]); }; }
            Refresh(); _l = false;
        }
        static string Fmt(double v, string f) { var parts = f.Split(' '); string form = parts[0]; string suf = parts.Length > 1 ? " " + parts[1] : ""; return v.ToString(form) + suf; }
        void Refresh()
        {
            _s = true;
            var sliders = new[] { S1, S2, S3, S4, S5, S6, S7, S8, S9, S10 };
            var vals = new[] { V1, V2, V3, V4, V5, V6, V7, V8, V9, V10 };
            for (int i = 0; i < 10; i++) foreach (var kp in _p.Parameters) if (kp.Id == Ids[i]) { sliders[i].Value = kp.Value; vals[i].Text = Fmt(kp.Value, Fmts[i]); break; }
            _s = false;
        }
        public void OnContextUpdated(KotonRenderContext ctx) { }
    }
}
