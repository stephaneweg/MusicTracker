using System;
using System.Windows;
using System.Windows.Controls;
using KotonStudio.Library;

namespace KotonPluginOceanReverb
{
    /// <summary>Éditeur du plugin Ocean Reverb. Combo mode (Abyss/Tide/Foam) + Freeze en évidence
    /// (le mode définit le CARACTÈRE, tous les autres params s'appliquent par-dessus). Mix lock
    /// permet de parcourir les presets sans que le mix dry/wet saute.</summary>
    public partial class OceanReverbEditor : UserControl, IKotonEditor
    {
        readonly OceanReverbPlugin _plugin;
        bool _syncing;
        bool _loading = true;

        public OceanReverbEditor(OceanReverbPlugin plugin)
        {
            _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
            InitializeComponent();
            InitCombos();
            WireSliders();
            RefreshFromPlugin();
            _loading = false;
        }

        void InitCombos()
        {
            PresetCombo.Items.Clear();
            PresetCombo.Items.Add("— Custom —");
            foreach (var n in OceanReverbPlugin.PresetNames) PresetCombo.Items.Add(n);
            PresetCombo.SelectedIndex = 0;

            ModeCombo.Items.Clear();
            ModeCombo.Items.Add("Abyss (shimmer +12)");
            ModeCombo.Items.Add("Tide (LP variable)");
            ModeCombo.Items.Add("Foam (diffusion douce)");
        }

        void WireSliders()
        {
            Wire(SizeSlider,        SizeValue,        "size",         v => v.ToString("F2"));
            Wire(DecaySlider,       DecayValue,       "decay",        v => v.ToString("F2"));
            Wire(PreDelaySlider,    PreDelayValue,    "pre_delay",    v => v.ToString("F0") + " ms");
            Wire(HpFilterSlider,    HpFilterValue,    "hp_filter",    v => v.ToString("F0") + " Hz");
            Wire(BrightnessSlider,  BrightnessValue,  "brightness",   v => v.ToString("F2"));
            Wire(MovementSlider,    MovementValue,    "movement",     v => v.ToString("F2"));
            Wire(DuckingSlider,     DuckingValue,     "duck_depth",   v => v.ToString("F2"));
            Wire(StereoWidthSlider, StereoWidthValue, "stereo_width", v => v.ToString("F2"));
            Wire(MixSlider,         MixValue,         "mix",          v => v.ToString("F2"));
            Wire(OutGainSlider,     OutGainValue,     "out_gain",     v => v.ToString("F1") + " dB");
        }

        void Wire(Slider slider, TextBlock label, string paramId, Func<double, string> fmt)
        {
            slider.ValueChanged += (s, e) =>
            {
                if (_syncing || _loading) return;
                _plugin.SetParam(paramId, e.NewValue);
                label.Text = fmt(e.NewValue);
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
                    case "mode":         ModeCombo.SelectedIndex = (int)kp.Value; UpdateModeDescription(); break;
                    case "size":         SizeSlider.Value        = kp.Value; SizeValue.Text        = kp.Value.ToString("F2"); break;
                    case "decay":        DecaySlider.Value       = kp.Value; DecayValue.Text       = kp.Value.ToString("F2"); break;
                    case "brightness":   BrightnessSlider.Value  = kp.Value; BrightnessValue.Text  = kp.Value.ToString("F2"); break;
                    case "movement":     MovementSlider.Value    = kp.Value; MovementValue.Text    = kp.Value.ToString("F2"); break;
                    case "pre_delay":    PreDelaySlider.Value    = kp.Value; PreDelayValue.Text    = kp.Value.ToString("F0") + " ms"; break;
                    case "hp_filter":    HpFilterSlider.Value    = kp.Value; HpFilterValue.Text    = kp.Value.ToString("F0") + " Hz"; break;
                    case "duck_depth":   DuckingSlider.Value     = kp.Value; DuckingValue.Text     = kp.Value.ToString("F2"); break;
                    case "stereo_width": StereoWidthSlider.Value = kp.Value; StereoWidthValue.Text = kp.Value.ToString("F2"); break;
                    case "freeze":       FreezeCheck.IsChecked   = kp.Value >= 0.5; break;
                    case "mix":          MixSlider.Value         = kp.Value; MixValue.Text         = kp.Value.ToString("F2"); break;
                    case "out_gain":     OutGainSlider.Value     = kp.Value; OutGainValue.Text     = kp.Value.ToString("F1") + " dB"; break;
                }
            }
            _syncing = false;
        }

        void UpdateModeDescription()
        {
            switch (ModeCombo.SelectedIndex)
            {
                case 0: ModeDescription.Text = "shimmer octave sup dans la queue"; break;
                case 1: ModeDescription.Text = "brillance qui va et vient (vagues)"; break;
                case 2: ModeDescription.Text = "diffusion doubled + attaque diluee"; break;
            }
        }

        void PresetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || _syncing) return;
            int idx = PresetCombo.SelectedIndex;
            if (idx <= 0) return;
            _plugin.LoadPreset(idx - 1, MixLockCheck.IsChecked == true);
            RefreshFromPlugin();
        }

        void ModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || _syncing) return;
            _plugin.SetParam("mode", ModeCombo.SelectedIndex);
            UpdateModeDescription();
            if (PresetCombo.SelectedIndex != 0) PresetCombo.SelectedIndex = 0;
        }

        void FreezeCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading || _syncing) return;
            _plugin.SetParam("freeze", FreezeCheck.IsChecked == true ? 1.0 : 0.0);
            if (PresetCombo.SelectedIndex != 0) PresetCombo.SelectedIndex = 0;
        }

        public void OnContextUpdated(KotonRenderContext ctx) { }
    }
}
