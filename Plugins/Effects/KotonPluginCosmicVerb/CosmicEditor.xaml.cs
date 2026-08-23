using System;
using System.Windows.Controls;
using KotonStudio.Library;

namespace KotonPluginCosmicVerb
{
    public partial class CosmicEditor : UserControl, IKotonEditor
    {
        readonly CosmicPlugin _plugin;
        bool _syncing, _loading = true;

        public CosmicEditor(CosmicPlugin plugin)
        {
            _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
            InitializeComponent();
            ModeCombo.Items.Clear();
            foreach (var n in _plugin.ModeNames) ModeCombo.Items.Add(n);
            Wire(DelaySlider,     DelayValue,     "delay_ms",  v => v.ToString("F0") + " ms");
            Wire(WarpSlider,      WarpValue,      "warp",      v => v.ToString("F2"));
            Wire(FeedbackSlider,  FeedbackValue,  "feedback",  v => v.ToString("F2"));
            Wire(DensitySlider,   DensityValue,   "density",   v => v.ToString("F2"));
            Wire(ModDepthSlider,  ModDepthValue,  "mod_depth", v => v.ToString("F2"));
            Wire(ModRateSlider,   ModRateValue,   "mod_rate",  v => v.ToString("F2") + " Hz");
            Wire(HighCutSlider,   HighCutValue,   "high_cut",  v => v.ToString("F0") + " Hz");
            Wire(WidthSlider,     WidthValue,     "width",     v => v.ToString("F2"));
            Wire(MixSlider,       MixValue,       "mix",       v => v.ToString("F2"));
            Wire(OutGainSlider,   OutGainValue,   "out_gain",  v => v.ToString("F1") + " dB");
            RefreshFromPlugin();
            _loading = false;
        }

        void Wire(Slider s, TextBlock lbl, string id, Func<double, string> fmt)
        {
            s.ValueChanged += (x, e) =>
            {
                if (_syncing || _loading) return;
                _plugin.SetParam(id, e.NewValue);
                lbl.Text = fmt(e.NewValue);
            };
        }

        void RefreshFromPlugin()
        {
            _syncing = true;
            foreach (var kp in _plugin.Parameters)
            {
                switch (kp.Id)
                {
                    case "mode":      ModeCombo.SelectedIndex = (int)Math.Round(kp.Value); break;
                    case "delay_ms":  DelaySlider.Value    = kp.Value; DelayValue.Text    = kp.Value.ToString("F0") + " ms"; break;
                    case "warp":      WarpSlider.Value     = kp.Value; WarpValue.Text     = kp.Value.ToString("F2"); break;
                    case "feedback":  FeedbackSlider.Value = kp.Value; FeedbackValue.Text = kp.Value.ToString("F2"); break;
                    case "density":   DensitySlider.Value  = kp.Value; DensityValue.Text  = kp.Value.ToString("F2"); break;
                    case "mod_depth": ModDepthSlider.Value = kp.Value; ModDepthValue.Text = kp.Value.ToString("F2"); break;
                    case "mod_rate":  ModRateSlider.Value  = kp.Value; ModRateValue.Text  = kp.Value.ToString("F2") + " Hz"; break;
                    case "high_cut":  HighCutSlider.Value  = kp.Value; HighCutValue.Text  = kp.Value.ToString("F0") + " Hz"; break;
                    case "width":     WidthSlider.Value    = kp.Value; WidthValue.Text    = kp.Value.ToString("F2"); break;
                    case "mix":       MixSlider.Value      = kp.Value; MixValue.Text      = kp.Value.ToString("F2"); break;
                    case "out_gain":  OutGainSlider.Value  = kp.Value; OutGainValue.Text  = kp.Value.ToString("F1") + " dB"; break;
                }
            }
            _syncing = false;
        }

        void ModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || _syncing) return;
            _plugin.SetParam("mode", ModeCombo.SelectedIndex);
        }

        public void OnContextUpdated(KotonRenderContext ctx) { }
    }
}
