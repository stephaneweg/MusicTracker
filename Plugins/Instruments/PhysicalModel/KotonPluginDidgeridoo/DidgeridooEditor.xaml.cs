using System;
using System.Windows.Controls;
using KotonStudio.Library;

namespace KotonPluginDidgeridoo
{
    public partial class DidgeridooEditor : UserControl, IKotonEditor
    {
        readonly DidgeridooPlugin _p;
        bool _sync, _loading = true;
        Slider[] _sliders;
        TextBlock[] _vals;

        // Ordre identique a celui des sliders S1..S22 dans le XAML.
        static readonly string[] Ids =
        {
            "tube", "resonance", "stretch", "bright", "bass_cut", "drive", "volume",
            "lips", "pressure", "breath", "wobble", "flutter", "toot_ratio", "attack", "release",
            "vowel", "tongue", "mouth_size", "mod_rate", "mod_depth", "slew", "breath_cycle"
        };
        static readonly string[] Fmts =
        {
            "F2", "F2", "F2", "F2", "F2", "F2", "F1 dB",
            "F2", "F2", "F2", "F2", "F1 Hz", "F2 x", "F0 ms", "F0 ms",
            "F2", "F2", "F2 x", "F2 Hz", "F2", "F0 ms", "F1 s"
        };

        public DidgeridooEditor(DidgeridooPlugin plugin)
        {
            _p = plugin ?? throw new ArgumentNullException(nameof(plugin));
            InitializeComponent();
            _sliders = new[] { S1, S2, S3, S4, S5, S6, S7, S8, S9, S10, S11, S12, S13, S14, S15, S16, S17, S18, S19, S20, S21, S22 };
            _vals    = new[] { V1, V2, V3, V4, V5, V6, V7, V8, V9, V10, V11, V12, V13, V14, V15, V16, V17, V18, V19, V20, V21, V22 };
            for (int i = 0; i < Ids.Length; i++)
            {
                int k = i;
                _sliders[i].ValueChanged += (s, e) =>
                {
                    if (_sync || _loading) return;
                    _p.SetParam(Ids[k], e.NewValue);
                    _vals[k].Text = Fmt(e.NewValue, Fmts[k], Ids[k]);
                };
            }
            Refresh();
            _loading = false;
        }

        void Shape_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_sync || _loading) return;
            _p.SetParam("mod_shape", ShapeCombo.SelectedIndex);
        }

        static string Fmt(double v, string f, string id)
        {
            // Deux reglages ont un point mort explicite : mieux vaut l'ecrire que d'afficher "0,0".
            if (id == "breath_cycle" && v < 0.5) return "désactivée";
            if (id == "flutter" && v < 0.2) return "off";
            var parts = f.Split(' ');
            string suf = parts.Length > 1 ? " " + parts[1] : "";
            return v.ToString(parts[0]) + suf;
        }

        void Refresh()
        {
            _sync = true;
            for (int i = 0; i < Ids.Length; i++)
            {
                foreach (var kp in _p.Parameters)
                {
                    if (kp.Id != Ids[i]) continue;
                    _sliders[i].Value = kp.Value;
                    _vals[i].Text = Fmt(kp.Value, Fmts[i], Ids[i]);
                    break;
                }
            }
            foreach (var kp in _p.Parameters)
                if (kp.Id == "mod_shape") { ShapeCombo.SelectedIndex = (int)Math.Round(kp.Value); break; }
            _sync = false;
        }

        public void OnContextUpdated(KotonRenderContext ctx) { }
    }
}
