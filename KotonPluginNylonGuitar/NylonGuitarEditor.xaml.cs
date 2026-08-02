using System;
using System.Windows.Controls;
using KotonStudio.Library;

namespace KotonPluginNylonGuitar
{
    public partial class NylonGuitarEditor : UserControl, IKotonEditor
    {
        readonly NylonGuitarPlugin _plugin;
        bool _syncing, _loading = true;

        public NylonGuitarEditor(NylonGuitarPlugin plugin)
        {
            _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
            InitializeComponent();
            InstrumentCombo.Items.Clear();
            foreach (var n in NylonGuitarPlugin.InstrumentNames) InstrumentCombo.Items.Add(n);
            Wire(PluckSoftnessSlider, PluckSoftnessValue, "pluck_softness", v => v.ToString("F2"));
            Wire(PluckPositionSlider, PluckPositionValue, "pluck_position", v => v.ToString("F2"));
            Wire(StiffnessSlider,     StiffnessValue,     "stiffness",      v => v.ToString("F2"));
            Wire(SustainSlider,       SustainValue,       "sustain",        v => v.ToString("F2"));
            Wire(BrightnessSlider,    BrightnessValue,    "brightness",     v => v.ToString("F2"));
            Wire(BodyMixSlider,       BodyMixValue,       "body_mix",       v => v.ToString("F2"));
            Wire(StereoSpreadSlider,  StereoSpreadValue,  "stereo_spread",  v => v.ToString("F2"));
            Wire(VolumeSlider,        VolumeValue,        "volume",         v => v.ToString("F1") + " dB");
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
                    case "instrument":     InstrumentCombo.SelectedIndex = (int)kp.Value; break;
                    case "pluck_softness": PluckSoftnessSlider.Value = kp.Value; PluckSoftnessValue.Text = kp.Value.ToString("F2"); break;
                    case "pluck_position": PluckPositionSlider.Value = kp.Value; PluckPositionValue.Text = kp.Value.ToString("F2"); break;
                    case "stiffness":      StiffnessSlider.Value     = kp.Value; StiffnessValue.Text     = kp.Value.ToString("F2"); break;
                    case "sustain":        SustainSlider.Value       = kp.Value; SustainValue.Text       = kp.Value.ToString("F2"); break;
                    case "brightness":     BrightnessSlider.Value    = kp.Value; BrightnessValue.Text    = kp.Value.ToString("F2"); break;
                    case "body_mix":       BodyMixSlider.Value       = kp.Value; BodyMixValue.Text       = kp.Value.ToString("F2"); break;
                    case "stereo_spread":  StereoSpreadSlider.Value  = kp.Value; StereoSpreadValue.Text  = kp.Value.ToString("F2"); break;
                    case "volume":         VolumeSlider.Value        = kp.Value; VolumeValue.Text        = kp.Value.ToString("F1") + " dB"; break;
                }
            }
            _syncing = false;
        }

        void InstrumentCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || _syncing) return;
            int idx = InstrumentCombo.SelectedIndex;
            _plugin.SetParam("instrument", idx);
            _plugin.ApplyInstrumentDefaults(idx);
            RefreshFromPlugin();
        }

        public void OnContextUpdated(KotonRenderContext ctx) { }
    }
}
