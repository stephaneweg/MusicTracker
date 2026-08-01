using System;
using System.Windows.Controls;
using KotonStudio.Library;

namespace KotonPluginBowedStrings
{
    /// <summary>Éditeur du plugin Bowed Strings. Trois colonnes : ARCHET (excitation) / CORDE
    /// (couleur) / ENSEMBLE (unison + sortie). Live-edit sur tous les paramètres via le pattern
    /// KotonInstrumentCache (instance partagée éditeur/renderer).</summary>
    public partial class BowedStringsEditor : UserControl, IKotonEditor
    {
        readonly BowedStringsPlugin _plugin;
        bool _syncing;
        bool _loading = true;

        public BowedStringsEditor(BowedStringsPlugin plugin)
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
            foreach (var name in BowedStringsPlugin.PresetNames) PresetCombo.Items.Add(name);
            PresetCombo.SelectedIndex = 0;

            UnisonCombo.Items.Clear();
            foreach (int u in new[] { 1, 2, 4, 6, 8 }) UnisonCombo.Items.Add(u == 1 ? "1 (mono)" : (u + " voix"));
        }

        void WireSliders()
        {
            Wire(BowPressureSlider,   BowPressureValue,   "bow_pressure",   v => v.ToString("F2"));
            Wire(BowPositionSlider,   BowPositionValue,   "bow_position",   v => v.ToString("F2"));
            Wire(BowSmoothnessSlider, BowSmoothnessValue, "bow_smoothness", v => v.ToString("F2"));
            Wire(AttackSlider,        AttackValue,        "attack_time",    v => v.ToString("F2") + " s");
            Wire(ReleaseSlider,       ReleaseValue,       "release_time",   v => v.ToString("F2") + " s");
            Wire(DampingSlider,       DampingValue,       "damping",        v => v.ToString("F2"));
            Wire(ToneSlider,          ToneValue,          "tone",           v => v.ToString("F2"));
            Wire(HarmonicsSlider,     HarmonicsValue,     "harmonics",      v => v.ToString("F2"));
            Wire(VibratoRateSlider,   VibratoRateValue,   "vibrato_rate",   v => v.ToString("F1") + " Hz");
            Wire(VibratoDepthSlider,  VibratoDepthValue,  "vibrato_depth",  v => v.ToString("F0") + " ct");
            Wire(DetuneSpreadSlider,  DetuneSpreadValue,  "detune_spread",  v => v.ToString("F0") + " ct");
            Wire(StereoWidthSlider,   StereoWidthValue,   "stereo_width",   v => v.ToString("F2"));
            Wire(VolumeSlider,        VolumeValue,        "volume",         v => v.ToString("F1") + " dB");
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
                    case "bow_pressure":   BowPressureSlider.Value   = kp.Value; BowPressureValue.Text   = kp.Value.ToString("F2"); break;
                    case "bow_position":   BowPositionSlider.Value   = kp.Value; BowPositionValue.Text   = kp.Value.ToString("F2"); break;
                    case "bow_smoothness": BowSmoothnessSlider.Value = kp.Value; BowSmoothnessValue.Text = kp.Value.ToString("F2"); break;
                    case "damping":        DampingSlider.Value       = kp.Value; DampingValue.Text       = kp.Value.ToString("F2"); break;
                    case "tone":           ToneSlider.Value          = kp.Value; ToneValue.Text          = kp.Value.ToString("F2"); break;
                    case "harmonics":      HarmonicsSlider.Value     = kp.Value; HarmonicsValue.Text     = kp.Value.ToString("F2"); break;
                    case "unison_count":   UnisonCombo.SelectedIndex = UnisonIndex((int)kp.Value); break;
                    case "detune_spread":  DetuneSpreadSlider.Value  = kp.Value; DetuneSpreadValue.Text  = kp.Value.ToString("F0") + " ct"; break;
                    case "vibrato_rate":   VibratoRateSlider.Value   = kp.Value; VibratoRateValue.Text   = kp.Value.ToString("F1") + " Hz"; break;
                    case "vibrato_depth":  VibratoDepthSlider.Value  = kp.Value; VibratoDepthValue.Text  = kp.Value.ToString("F0") + " ct"; break;
                    case "attack_time":    AttackSlider.Value        = kp.Value; AttackValue.Text        = kp.Value.ToString("F2") + " s"; break;
                    case "release_time":   ReleaseSlider.Value       = kp.Value; ReleaseValue.Text       = kp.Value.ToString("F2") + " s"; break;
                    case "stereo_width":   StereoWidthSlider.Value   = kp.Value; StereoWidthValue.Text   = kp.Value.ToString("F2"); break;
                    case "volume":         VolumeSlider.Value        = kp.Value; VolumeValue.Text        = kp.Value.ToString("F1") + " dB"; break;
                }
            }
            _syncing = false;
        }

        static int UnisonIndex(int u)
        {
            switch (u)
            {
                case 1: return 0;
                case 2: return 1;
                case 4: return 2;
                case 6: return 3;
                case 8: return 4;
                default: return 2;
            }
        }
        static int UnisonValue(int idx)
        {
            switch (idx)
            {
                case 0: return 1;
                case 1: return 2;
                case 2: return 4;
                case 3: return 6;
                case 4: return 8;
                default: return 4;
            }
        }

        void PresetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || _syncing) return;
            int idx = PresetCombo.SelectedIndex;
            if (idx <= 0) return;
            _plugin.LoadPreset(idx - 1);
            RefreshFromPlugin();
        }

        void UnisonCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || _syncing) return;
            _plugin.SetParam("unison_count", UnisonValue(UnisonCombo.SelectedIndex));
            if (PresetCombo.SelectedIndex != 0) PresetCombo.SelectedIndex = 0;
        }

        public void OnContextUpdated(KotonRenderContext ctx) { }
    }
}
