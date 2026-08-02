using System;
using System.Windows.Controls;
using KotonStudio.Library;

namespace KotonPluginCaveReverb
{
    public partial class CaveEditor : UserControl, IKotonEditor
    {
        readonly CavePlugin _plugin;
        bool _syncing, _loading = true;

        public CaveEditor(CavePlugin plugin)
        {
            _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
            InitializeComponent();
            PresetCombo.Items.Clear();
            PresetCombo.Items.Add("— Custom —");
            foreach (var n in CavePlugin.PresetNames) PresetCombo.Items.Add(n);
            PresetCombo.SelectedIndex = 0;
            Wire(SizeSlider,        SizeValue,        "size",         v => v.ToString("F2"));
            Wire(DecaySlider,       DecayValue,       "decay",        v => v.ToString("F2"));
            Wire(LowBoomSlider,     LowBoomValue,     "low_boom",     v => v.ToString("F2"));
            Wire(DarknessSlider,    DarknessValue,    "darkness",     v => v.ToString("F2"));
            Wire(PreDelaySlider,    PreDelayValue,    "pre_delay",    v => v.ToString("F0") + " ms");
            Wire(DripAmountSlider,  DripAmountValue,  "drip_amount",  v => v.ToString("F2"));
            Wire(DripPitchSlider,   DripPitchValue,   "drip_pitch",   v => v.ToString("F2"));
            Wire(StereoWidthSlider, StereoWidthValue, "stereo_width", v => v.ToString("F2"));
            Wire(MixSlider,         MixValue,         "mix",          v => v.ToString("F2"));
            Wire(OutGainSlider,     OutGainValue,     "out_gain",     v => v.ToString("F1") + " dB");
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
                if (PresetCombo.SelectedIndex != 0) PresetCombo.SelectedIndex = 0;
            };
        }

        void RefreshFromPlugin()
        {
            _syncing = true;
            foreach (var kp in _plugin.Parameters)
            {
                switch (kp.Id)
                {
                    case "size":         SizeSlider.Value        = kp.Value; SizeValue.Text        = kp.Value.ToString("F2"); break;
                    case "decay":        DecaySlider.Value       = kp.Value; DecayValue.Text       = kp.Value.ToString("F2"); break;
                    case "low_boom":     LowBoomSlider.Value     = kp.Value; LowBoomValue.Text     = kp.Value.ToString("F2"); break;
                    case "darkness":     DarknessSlider.Value    = kp.Value; DarknessValue.Text    = kp.Value.ToString("F2"); break;
                    case "pre_delay":    PreDelaySlider.Value    = kp.Value; PreDelayValue.Text    = kp.Value.ToString("F0") + " ms"; break;
                    case "drip_amount":  DripAmountSlider.Value  = kp.Value; DripAmountValue.Text  = kp.Value.ToString("F2"); break;
                    case "drip_pitch":   DripPitchSlider.Value   = kp.Value; DripPitchValue.Text   = kp.Value.ToString("F2"); break;
                    case "stereo_width": StereoWidthSlider.Value = kp.Value; StereoWidthValue.Text = kp.Value.ToString("F2"); break;
                    case "mix":          MixSlider.Value         = kp.Value; MixValue.Text         = kp.Value.ToString("F2"); break;
                    case "out_gain":     OutGainSlider.Value     = kp.Value; OutGainValue.Text     = kp.Value.ToString("F1") + " dB"; break;
                }
            }
            _syncing = false;
        }

        void PresetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || _syncing) return;
            int idx = PresetCombo.SelectedIndex;
            if (idx <= 0) return;
            _plugin.LoadPreset(idx - 1, MixLockCheck.IsChecked == true);
            RefreshFromPlugin();
        }

        public void OnContextUpdated(KotonRenderContext ctx) { }
    }
}
