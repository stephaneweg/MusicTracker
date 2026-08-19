using System;
using System.Windows.Controls;
using KotonStudio.Library;

namespace KotonPluginPolySynth
{
    public partial class PolySynthEditor : UserControl, IKotonEditor
    {
        readonly PolySynthPlugin _p; bool _s, _l = true;
        static readonly string[] Ids = { "detune", "mix_osc2", "cutoff", "resonance", "env_mod", "f_attack", "f_decay", "a_attack", "a_release", "volume", "lfo_rate", "lfo_pitch", "lfo_cut" };
        static readonly string[] Fmts = { "F1 ct", "F2", "F0 Hz", "F2", "F2", "F0 ms", "F0 ms", "F0 ms", "F0 ms", "F1 dB", "F2 Hz", "F0 ct", "F0 Hz" };
        public PolySynthEditor(PolySynthPlugin plugin)
        {
            _p = plugin ?? throw new ArgumentNullException(nameof(plugin));
            InitializeComponent();
            var sliders = new[] { S1, S2, S3, S4, S5, S6, S7, S8, S9, S10, S11, S12, S13 };
            var vals = new[] { V1, V2, V3, V4, V5, V6, V7, V8, V9, V10, V11, V12, V13 };
            for (int i = 0; i < 13; i++) { int k = i; sliders[i].ValueChanged += (x, e) => { if (_s || _l) return; _p.SetParam(Ids[k], e.NewValue); vals[k].Text = Fmt(e.NewValue, Fmts[k]); }; }
            Refresh(); _l = false;
        }
        static string Fmt(double v, string f) { var parts = f.Split(' '); string form = parts[0]; string suf = parts.Length > 1 ? " " + parts[1] : ""; return v.ToString(form) + suf; }
        void Refresh()
        {
            _s = true;
            var sliders = new[] { S1, S2, S3, S4, S5, S6, S7, S8, S9, S10, S11, S12, S13 };
            var vals = new[] { V1, V2, V3, V4, V5, V6, V7, V8, V9, V10, V11, V12, V13 };
            for (int i = 0; i < 13; i++) foreach (var kp in _p.Parameters) if (kp.Id == Ids[i]) { sliders[i].Value = kp.Value; vals[i].Text = Fmt(kp.Value, Fmts[i]); break; }
            _s = false;
        }
        public void OnContextUpdated(KotonRenderContext ctx) { }
    }
}
