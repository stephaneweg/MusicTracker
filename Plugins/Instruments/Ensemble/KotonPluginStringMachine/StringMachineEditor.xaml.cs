using System;
using System.Windows;
using System.Windows.Controls;
using KotonStudio.Library;

namespace KotonPluginStringMachine
{
    /// <summary>Éditeur du plugin String Machine. Layout classique de synthé soustractif :
    /// oscillateurs/filtre à gauche, ADSR au centre, chorus/sortie à droite. Un checkbox Paraphonique
    /// bascule entre le mode Solina (1 filtre partagé) et le mode poly standard (filtre par voix).</summary>
    public partial class StringMachineEditor : UserControl, IKotonEditor
    {
        readonly StringMachinePlugin _plugin;
        bool _syncing;
        bool _loading = true;

        public StringMachineEditor(StringMachinePlugin plugin)
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
            foreach (var name in StringMachinePlugin.PresetNames) PresetCombo.Items.Add(name);
            PresetCombo.SelectedIndex = 0;
        }

        void WireSliders()
        {
            Wire(SubLevelSlider,      SubLevelValue,      "sub_level",       v => v.ToString("F2"));
            Wire(CutoffSlider,        CutoffValue,        "cutoff",          v => v.ToString("F0") + " Hz");
            Wire(ResonanceSlider,     ResonanceValue,     "resonance",       v => v.ToString("F2"));
            Wire(AttackSlider,        AttackValue,        "attack_time",     v => v.ToString("F2") + " s");
            Wire(DecaySlider,         DecayValue,         "decay_time",      v => v.ToString("F2") + " s");
            Wire(SustainSlider,       SustainValue,       "sustain_level",   v => v.ToString("F2"));
            Wire(ReleaseSlider,       ReleaseValue,       "release_time",    v => v.ToString("F2") + " s");
            Wire(ChorusRateSlider,    ChorusRateValue,    "chorus_rate",     v => v.ToString("F2") + " Hz");
            Wire(ChorusDepthSlider,   ChorusDepthValue,   "chorus_depth",    v => v.ToString("F2"));
            Wire(ChorusMixSlider,     ChorusMixValue,     "chorus_mix",      v => v.ToString("F2"));
            Wire(StereoWidthSlider,   StereoWidthValue,   "stereo_width",    v => v.ToString("F2"));
            Wire(VolumeSlider,        VolumeValue,        "volume",          v => v.ToString("F1") + " dB");
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
                    case "sub_level":      SubLevelSlider.Value      = kp.Value; SubLevelValue.Text      = kp.Value.ToString("F2"); break;
                    case "cutoff":         CutoffSlider.Value        = kp.Value; CutoffValue.Text        = kp.Value.ToString("F0") + " Hz"; break;
                    case "resonance":      ResonanceSlider.Value     = kp.Value; ResonanceValue.Text     = kp.Value.ToString("F2"); break;
                    case "paraphonic":     ParaphonicCheck.IsChecked = kp.Value >= 0.5; break;
                    case "attack_time":    AttackSlider.Value        = kp.Value; AttackValue.Text        = kp.Value.ToString("F2") + " s"; break;
                    case "decay_time":     DecaySlider.Value         = kp.Value; DecayValue.Text         = kp.Value.ToString("F2") + " s"; break;
                    case "sustain_level":  SustainSlider.Value       = kp.Value; SustainValue.Text       = kp.Value.ToString("F2"); break;
                    case "release_time":   ReleaseSlider.Value       = kp.Value; ReleaseValue.Text       = kp.Value.ToString("F2") + " s"; break;
                    case "chorus_rate":    ChorusRateSlider.Value    = kp.Value; ChorusRateValue.Text    = kp.Value.ToString("F2") + " Hz"; break;
                    case "chorus_depth":   ChorusDepthSlider.Value   = kp.Value; ChorusDepthValue.Text   = kp.Value.ToString("F2"); break;
                    case "chorus_mix":     ChorusMixSlider.Value     = kp.Value; ChorusMixValue.Text     = kp.Value.ToString("F2"); break;
                    case "stereo_width":   StereoWidthSlider.Value   = kp.Value; StereoWidthValue.Text   = kp.Value.ToString("F2"); break;
                    case "volume":         VolumeSlider.Value        = kp.Value; VolumeValue.Text        = kp.Value.ToString("F1") + " dB"; break;
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
            RefreshFromPlugin();
        }

        void ParaphonicCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading || _syncing) return;
            _plugin.SetParam("paraphonic", ParaphonicCheck.IsChecked == true ? 1.0 : 0.0);
            if (PresetCombo.SelectedIndex != 0) PresetCombo.SelectedIndex = 0;
        }

        public void OnContextUpdated(KotonRenderContext ctx) { }
    }
}
