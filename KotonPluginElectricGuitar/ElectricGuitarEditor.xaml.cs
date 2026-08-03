using System;
using System.Windows.Controls;
using KotonStudio.Library;

namespace KotonPluginElectricGuitar
{
    public partial class ElectricGuitarEditor : UserControl, IKotonEditor
    {
        readonly ElectricGuitarPlugin _plugin;
        bool _syncing, _loading = true;

        public ElectricGuitarEditor(ElectricGuitarPlugin plugin)
        {
            _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
            InitializeComponent();
            PresetCombo.Items.Clear();
            PresetCombo.Items.Add("— Custom —");
            foreach (var n in ElectricGuitarPlugin.PresetNames) PresetCombo.Items.Add(n);
            PresetCombo.SelectedIndex = 0;
            Wire(PluckPositionSlider, PluckPositionValue, "pluck_position", v => v.ToString("F2"));
            Wire(PluckHardnessSlider, PluckHardnessValue, "pluck_hardness", v => v.ToString("F2"));
            Wire(PickupPositionSlider, PickupPositionValue, "pickup_position", v => v.ToString("F2"));
            Wire(DampingSlider, DampingValue, "damping", v => v.ToString("F2"));
            Wire(SustainSlider, SustainValue, "sustain", v => v.ToString("F2"));
            Wire(ToneSlider, ToneValue, "tone", v => v.ToString("F2"));
            Wire(DriveSlider, DriveValue, "drive", v => v.ToString("F2"));
            Wire(BodySlider, BodyValue, "body", v => v.ToString("F2"));
            Wire(StereoWidthSlider, StereoWidthValue, "stereo_width", v => v.ToString("F2"));
            Wire(VolumeSlider, VolumeValue, "volume", v => v.ToString("F1") + " dB");
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
                    case "pluck_position": PluckPositionSlider.Value = kp.Value; PluckPositionValue.Text = kp.Value.ToString("F2"); break;
                    case "pluck_hardness": PluckHardnessSlider.Value = kp.Value; PluckHardnessValue.Text = kp.Value.ToString("F2"); break;
                    case "pickup_position": PickupPositionSlider.Value = kp.Value; PickupPositionValue.Text = kp.Value.ToString("F2"); break;
                    case "damping": DampingSlider.Value = kp.Value; DampingValue.Text = kp.Value.ToString("F2"); break;
                    case "sustain": SustainSlider.Value = kp.Value; SustainValue.Text = kp.Value.ToString("F2"); break;
                    case "tone": ToneSlider.Value = kp.Value; ToneValue.Text = kp.Value.ToString("F2"); break;
                    case "drive": DriveSlider.Value = kp.Value; DriveValue.Text = kp.Value.ToString("F2"); break;
                    case "body": BodySlider.Value = kp.Value; BodyValue.Text = kp.Value.ToString("F2"); break;
                    case "stereo_width": StereoWidthSlider.Value = kp.Value; StereoWidthValue.Text = kp.Value.ToString("F2"); break;
                    case "volume": VolumeSlider.Value = kp.Value; VolumeValue.Text = kp.Value.ToString("F1") + " dB"; break;
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
