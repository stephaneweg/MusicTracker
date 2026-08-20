using System;
using System.Windows.Controls;
using KotonStudio.Library;

namespace KotonPluginNightCrickets
{
    public partial class NightCricketsEditor : UserControl, IKotonEditor
    {
        readonly NightCricketsPlugin _plugin;
        bool _syncing, _loading = true;

        public NightCricketsEditor(NightCricketsPlugin plugin)
        {
            _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
            InitializeComponent();
            PresetCombo.Items.Clear();
            PresetCombo.Items.Add("— Custom —");
            foreach (var n in NightCricketsPlugin.PresetNames) PresetCombo.Items.Add(n);
            PresetCombo.SelectedIndex = 0;
            Wire(DensitySlider, DensityValue, "density", v => v.ToString("F2"));
            Wire(PitchSlider, PitchValue, "pitch", v => v.ToString("F2"));
            Wire(TempoSlider, TempoValue, "tempo", v => v.ToString("F2"));
            Wire(AmbienceSlider, AmbienceValue, "ambience", v => v.ToString("F2"));
            Wire(OwlSlider, OwlValue, "owl", v => v.ToString("F2"));
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
                if (PresetCombo.SelectedIndex != 0) PresetCombo.SelectedIndex = 0;
            };
        }
        void Refresh()
        {
            _syncing = true;
            foreach (var kp in _plugin.Parameters)
            {
                switch (kp.Id)
                {
                    case "density": DensitySlider.Value = kp.Value; DensityValue.Text = kp.Value.ToString("F2"); break;
                    case "pitch": PitchSlider.Value = kp.Value; PitchValue.Text = kp.Value.ToString("F2"); break;
                    case "tempo": TempoSlider.Value = kp.Value; TempoValue.Text = kp.Value.ToString("F2"); break;
                    case "ambience": AmbienceSlider.Value = kp.Value; AmbienceValue.Text = kp.Value.ToString("F2"); break;
                    case "owl": OwlSlider.Value = kp.Value; OwlValue.Text = kp.Value.ToString("F2"); break;
                    case "stereo_width": StereoWidthSlider.Value = kp.Value; StereoWidthValue.Text = kp.Value.ToString("F2"); break;
                    case "mix": MixSlider.Value = kp.Value; MixValue.Text = kp.Value.ToString("F2"); break;
                    case "out_gain": OutGainSlider.Value = kp.Value; OutGainValue.Text = kp.Value.ToString("F1") + " dB"; break;
                }
            }
            _syncing = false;
        }
        void PresetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || _syncing) return;
            int idx = PresetCombo.SelectedIndex;
            if (idx <= 0) return;
            _plugin.LoadPreset(idx - 1);
            Refresh();
        }
        public void OnContextUpdated(KotonRenderContext ctx) { }
    }
}
