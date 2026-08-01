using System;
using System.Windows.Controls;
using KotonStudio.Library;

namespace KotonPluginKarplusStrong
{
    /// <summary>
    /// Éditeur du plugin Karplus-Strong. UI minimale : 8 sliders (2 colonnes) + une combo preset.
    /// Toutes les valeurs s'écrivent directement dans les <see cref="KotonParameter"/> du plugin
    /// (2-way immédiat) — bouger un slider s'entend au prochain buffer audio sans avoir à fermer
    /// la fenêtre ni relancer la lecture. Cohérent avec le pattern <c>KotonInstrumentCache</c> :
    /// même instance partagée entre dialog et renderer.
    /// </summary>
    public partial class KarplusStrongEditor : UserControl, IKotonEditor
    {
        readonly KarplusStrongPlugin _plugin;
        bool _syncing;
        bool _loading = true;

        public KarplusStrongEditor(KarplusStrongPlugin plugin)
        {
            _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
            InitializeComponent();
            InitPresetCombo();
            BindSliders();
            RefreshFromPlugin();
            _loading = false;
        }

        void InitPresetCombo()
        {
            PresetCombo.Items.Clear();
            PresetCombo.Items.Add("— Custom —");
            foreach (var name in KarplusStrongPlugin.PresetNames)
                PresetCombo.Items.Add(name);
            PresetCombo.SelectedIndex = 0;
        }

        void BindSliders()
        {
            Wire(DampingSlider,    DampingValue,    p => _plugin.SetParam("damping", p),           "damping",         v => v.ToString("F2"));
            Wire(SustainSlider,    SustainValue,    p => _plugin.SetParam("sustain", p),           "sustain",         v => v.ToString("F2"));
            Wire(ToneSlider,       ToneValue,       p => _plugin.SetParam("tone", p),              "tone",            v => v.ToString("F2"));
            Wire(StiffnessSlider,  StiffnessValue,  p => _plugin.SetParam("stiffness", p),         "stiffness",       v => v.ToString("F2"));
            Wire(BodySlider,       BodyValue,       p => _plugin.SetParam("body_mix", p),          "body_mix",        v => v.ToString("F2"));
            Wire(PluckPosSlider,   PluckPosValue,   p => _plugin.SetParam("pluck_position", p),    "pluck_position",  v => v.ToString("F2"));
            Wire(PluckHardSlider,  PluckHardValue,  p => _plugin.SetParam("pluck_hardness", p),    "pluck_hardness",  v => v.ToString("F2"));
            Wire(WidthSlider,      WidthValue,      p => _plugin.SetParam("stereo_width", p),      "stereo_width",    v => v.ToString("F2"));
            Wire(VolumeSlider,     VolumeValue,     p => _plugin.SetParam("volume", p),            "volume",          v => v.ToString("F1") + " dB");
        }

        void Wire(Slider slider, TextBlock valueLabel, Action<double> setter, string paramId, Func<double, string> fmt)
        {
            slider.ValueChanged += (s, e) =>
            {
                if (_syncing || _loading) return;
                setter(e.NewValue);
                valueLabel.Text = fmt(e.NewValue);
                // Un utilisateur a bougé un slider → l'état ne correspond plus à un preset défini
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
                    case "damping":         DampingSlider.Value    = kp.Value; DampingValue.Text    = kp.Value.ToString("F2"); break;
                    case "sustain":         SustainSlider.Value    = kp.Value; SustainValue.Text    = kp.Value.ToString("F2"); break;
                    case "tone":            ToneSlider.Value       = kp.Value; ToneValue.Text       = kp.Value.ToString("F2"); break;
                    case "stiffness":       StiffnessSlider.Value  = kp.Value; StiffnessValue.Text  = kp.Value.ToString("F2"); break;
                    case "body_mix":        BodySlider.Value       = kp.Value; BodyValue.Text       = kp.Value.ToString("F2"); break;
                    case "pluck_position":  PluckPosSlider.Value   = kp.Value; PluckPosValue.Text   = kp.Value.ToString("F2"); break;
                    case "pluck_hardness":  PluckHardSlider.Value  = kp.Value; PluckHardValue.Text  = kp.Value.ToString("F2"); break;
                    case "stereo_width":    WidthSlider.Value      = kp.Value; WidthValue.Text      = kp.Value.ToString("F2"); break;
                    case "volume":          VolumeSlider.Value     = kp.Value; VolumeValue.Text     = kp.Value.ToString("F1") + " dB"; break;
                }
            }
            _syncing = false;
        }

        void PresetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || _syncing) return;
            int idx = PresetCombo.SelectedIndex;
            if (idx <= 0) return;   // "— Custom —"
            _plugin.LoadPreset(idx - 1);
            RefreshFromPlugin();
        }

        public void OnContextUpdated(KotonRenderContext ctx)
        {
            // Ce plugin n'a rien à adapter au contexte (tempo/tonalité/métrique) — les paramètres
            // sont indépendants de la métrique. On implémente l'interface pour la forme uniquement.
        }
    }
}
