using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using KotonStudio.Library;

namespace KotonPluginFreeze
{
    public partial class FreezeEditor : UserControl, IKotonEditor
    {
        readonly FreezePlugin _plugin;
        bool _syncing, _loading = true;

        public FreezeEditor(FreezePlugin plugin)
        {
            _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
            InitializeComponent();
            Wire(ToneSlider, ToneValue, "tone", v => v.ToString("F2"));
            Wire(StereoWidthSlider, StereoWidthValue, "stereo_width", v => v.ToString("F2"));
            Wire(MixSlider, MixValue, "mix", v => v.ToString("F2"));
            Wire(OutGainSlider, OutGainValue, "out_gain", v => v.ToString("F1") + " dB");
            Refresh();
            _loading = false;
        }
        void Wire(Slider s, TextBlock lbl, string id, Func<double, string> fmt)
        {
            s.ValueChanged += (x, e) => {
                if (_syncing || _loading) return;
                _plugin.SetParam(id, e.NewValue); lbl.Text = fmt(e.NewValue);
            };
        }
        void Refresh()
        {
            _syncing = true;
            foreach (var kp in _plugin.Parameters)
            {
                switch (kp.Id)
                {
                    case "tone": ToneSlider.Value = kp.Value; ToneValue.Text = kp.Value.ToString("F2"); break;
                    case "stereo_width": StereoWidthSlider.Value = kp.Value; StereoWidthValue.Text = kp.Value.ToString("F2"); break;
                    case "mix": MixSlider.Value = kp.Value; MixValue.Text = kp.Value.ToString("F2"); break;
                    case "out_gain": OutGainSlider.Value = kp.Value; OutGainValue.Text = kp.Value.ToString("F1") + " dB"; break;
                    case "freeze_mode": FreezeCheck.IsChecked = kp.Value >= 0.5; break;
                }
            }
            _syncing = false;
        }
        void CaptureBtn_Click(object sender, RoutedEventArgs e)
        {
            // Trigger edge : 0 → 1 → 0 pour armer une nouvelle capture
            _plugin.SetParam("capture", 1);
            // Reset a 0 apres qq ms (le plugin detecte l'edge montant, puis oublie)
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
            t.Tick += (s, a) => { _plugin.SetParam("capture", 0); t.Stop(); };
            t.Start();
            // Auto-activer le freeze quand on capture
            FreezeCheck.IsChecked = true;
        }
        void FreezeCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading || _syncing) return;
            _plugin.SetParam("freeze_mode", FreezeCheck.IsChecked == true ? 1.0 : 0.0);
        }
        public void OnContextUpdated(KotonRenderContext ctx) { }
    }
}
