using System;
using System.Windows.Controls;
using KotonStudio.Library;

namespace KotonPluginElectricViolin
{
    public partial class ElectricViolinEditor : UserControl, IKotonEditor
    {
        readonly ElectricViolinPlugin _plugin;
        bool _syncing, _loading = true;

        public ElectricViolinEditor(ElectricViolinPlugin plugin)
        {
            _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
            InitializeComponent();
            PresetCombo.Items.Clear();
            PresetCombo.Items.Add("— Custom —");
            foreach (var n in ElectricViolinPlugin.PresetNames) PresetCombo.Items.Add(n);
            PresetCombo.SelectedIndex = 0;

            Wire(BowForceSlider,      BowForceValue,      "bow_force",       v => v.ToString("F2"));
            Wire(BowVelocitySlider,   BowVelocityValue,   "bow_velocity",    v => v.ToString("F2"));
            Wire(BowPositionSlider,   BowPositionValue,   "bow_position",    v => v.ToString("F2"));
            Wire(DampingSlider,       DampingValue,       "damping",         v => v.ToString("F2"));
            Wire(BodyIntensitySlider, BodyIntensityValue, "body_intensity",  v => v.ToString("F2"));
            Wire(WarmthSlider,        WarmthValue,        "warmth",          v => v.ToString("F2"));
            Wire(GrainLevelSlider,    GrainLevelValue,    "bow_scratch",     v => v.ToString("F2"));
            Wire(GrainDepthSlider,    GrainDepthValue,    "grain_depth",     v => v.ToString("F2"));
            Wire(GrainDriveSlider,    GrainDriveValue,    "grain_drive",     v => v.ToString("F2"));
            Wire(ReverbSlider,        ReverbValue,        "reverb",          v => v.ToString("F2"));
            Wire(VibratoRateSlider,   VibratoRateValue,   "vibrato_rate",    v => v.ToString("F1") + " Hz");
            Wire(VibratoDepthSlider,  VibratoDepthValue,  "vibrato_depth",   v => v.ToString("F0") + " ct");
            Wire(AttackSlider,        AttackValue,        "attack_time",     v => v.ToString("F2") + " s");
            Wire(ReleaseSlider,       ReleaseValue,       "release_time",    v => v.ToString("F2") + " s");
            Wire(VolumeSlider,        VolumeValue,        "volume",          v => v.ToString("F1") + " dB");

            // Tremolo : 2 sliders, un affichage combiné
            TremoloRateSlider.ValueChanged += (s, e) => {
                if (_syncing || _loading) return;
                _plugin.SetParam("tremolo_rate", e.NewValue);
                UpdateTremoloLabel();
                if (PresetCombo.SelectedIndex != 0) PresetCombo.SelectedIndex = 0;
            };
            TremoloDepthSlider.ValueChanged += (s, e) => {
                if (_syncing || _loading) return;
                _plugin.SetParam("tremolo_depth", e.NewValue);
                UpdateTremoloLabel();
                if (PresetCombo.SelectedIndex != 0) PresetCombo.SelectedIndex = 0;
            };

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
        void UpdateTremoloLabel()
        {
            TremoloValue.Text = TremoloRateSlider.Value.ToString("F1") + " Hz × " + TremoloDepthSlider.Value.ToString("F2");
        }
        void Refresh()
        {
            _syncing = true;
            foreach (var kp in _plugin.Parameters)
            {
                switch (kp.Id)
                {
                    case "bow_force":      BowForceSlider.Value      = kp.Value; BowForceValue.Text      = kp.Value.ToString("F2"); break;
                    case "bow_velocity":   BowVelocitySlider.Value   = kp.Value; BowVelocityValue.Text   = kp.Value.ToString("F2"); break;
                    case "bow_position":   BowPositionSlider.Value   = kp.Value; BowPositionValue.Text   = kp.Value.ToString("F2"); break;
                    case "damping":        DampingSlider.Value       = kp.Value; DampingValue.Text       = kp.Value.ToString("F2"); break;
                    case "body_intensity": BodyIntensitySlider.Value = kp.Value; BodyIntensityValue.Text = kp.Value.ToString("F2"); break;
                    case "warmth":         WarmthSlider.Value        = kp.Value; WarmthValue.Text        = kp.Value.ToString("F2"); break;
                    case "bow_scratch":    GrainLevelSlider.Value    = kp.Value; GrainLevelValue.Text    = kp.Value.ToString("F2"); break;
                    case "grain_depth":    GrainDepthSlider.Value    = kp.Value; GrainDepthValue.Text    = kp.Value.ToString("F2"); break;
                    case "grain_drive":    GrainDriveSlider.Value    = kp.Value; GrainDriveValue.Text    = kp.Value.ToString("F2"); break;
                    case "reverb":         ReverbSlider.Value        = kp.Value; ReverbValue.Text        = kp.Value.ToString("F2"); break;
                    case "vibrato_rate":   VibratoRateSlider.Value   = kp.Value; VibratoRateValue.Text   = kp.Value.ToString("F1") + " Hz"; break;
                    case "vibrato_depth":  VibratoDepthSlider.Value  = kp.Value; VibratoDepthValue.Text  = kp.Value.ToString("F0") + " ct"; break;
                    case "tremolo_rate":   TremoloRateSlider.Value   = kp.Value; break;
                    case "tremolo_depth":  TremoloDepthSlider.Value  = kp.Value; break;
                    case "attack_time":    AttackSlider.Value        = kp.Value; AttackValue.Text        = kp.Value.ToString("F2") + " s"; break;
                    case "release_time":   ReleaseSlider.Value       = kp.Value; ReleaseValue.Text       = kp.Value.ToString("F2") + " s"; break;
                    case "volume":         VolumeSlider.Value        = kp.Value; VolumeValue.Text        = kp.Value.ToString("F1") + " dB"; break;
                }
            }
            UpdateTremoloLabel();
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
