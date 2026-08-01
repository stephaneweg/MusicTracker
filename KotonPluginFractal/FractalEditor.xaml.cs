using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using KotonStudio.Library;

namespace KotonPluginFractal
{
    /// <summary>
    /// Éditeur du plugin FractalGenerator. Trois panneaux <see cref="Border"/> pour les paramètres
    /// spécifiques à chaque algo (Voss / Logistic / Lorenz) — un seul visible à la fois selon le
    /// combo <c>AlgoCombo</c>. Les paramètres communs sont toujours visibles au-dessus.
    ///
    /// Toutes les valeurs s'écrivent directement dans les <see cref="KotonParameter"/> du plugin
    /// (live edit) — bouger un slider s'entend au prochain re-flatten du LookaheadBuffer.
    /// </summary>
    public partial class FractalEditor : UserControl, IKotonEditor
    {
        readonly FractalGenerator _plugin;
        bool _syncing;
        bool _loading = true;

        // Numérateur de la signature temporelle — utilisé pour convertir mesures ↔ beats.
        // Initialisé depuis KotonHost.CurrentContext au chargement, ré-actualisé via OnContextUpdated.
        // Fallback = 4/4 si aucun contexte disponible (preview du bloc non posé).
        int _tsNum = 4;

        public FractalEditor(FractalGenerator plugin)
        {
            _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
            InitializeComponent();
            var initCtx = KotonHost.CurrentContext?.Invoke();
            if (initCtx != null && initCtx.TimeSigNum > 0) _tsNum = initCtx.TimeSigNum;
            InitCombos();
            WireSliders();
            RefreshFromPlugin();
            UpdatePanelVisibility();
            _loading = false;
        }

        // =============================================================================================
        // Init combos
        // =============================================================================================
        void InitCombos()
        {
            AlgoCombo.Items.Clear();
            AlgoCombo.Items.Add("Voss-Clarke (1/f)");
            AlgoCombo.Items.Add("Logistic map");
            AlgoCombo.Items.Add("Lorenz attractor");

            NotesPerBeatCombo.Items.Clear();
            foreach (int n in new[] { 1, 2, 3, 4, 6, 8 })
                NotesPerBeatCombo.Items.Add(n.ToString() + " / temps");

            SnapCombo.Items.Clear();
            SnapCombo.Items.Add("Notes de l'accord");
            SnapCombo.Items.Add("Gamme diatonique");
            SnapCombo.Items.Add("Chromatique (aucun snap)");

            ArticulationCombo.Items.Clear();
            ArticulationCombo.Items.Add("Legato");
            ArticulationCombo.Items.Add("Normal");
            ArticulationCombo.Items.Add("Détaché");
            ArticulationCombo.Items.Add("Staccato");

            LorenzDimCombo.Items.Clear();
            LorenzDimCombo.Items.Add("X (chaotique)");
            LorenzDimCombo.Items.Add("Y (mélodique)");
            LorenzDimCombo.Items.Add("Z (cloches)");
        }

        // =============================================================================================
        // Wire sliders — 2-way KotonParameter
        // =============================================================================================
        void WireSliders()
        {
            Wire(OctaveSlider,       OctaveValue,       "base_octave",   v => v.ToString("F0"));
            Wire(RangeSlider,        RangeValue,        "range",         v => v.ToString("F0") + " st");
            Wire(SeedSlider,         SeedValue,         "seed",          v => v.ToString("F0"));
            Wire(VelocitySlider,     VelocityValue,     "velocity",      v => v.ToString("F0"));
            Wire(VelVarSlider,       VelVarValue,       "vel_var",       v => v.ToString("F2"));

            DurationSlider.ValueChanged += (s, e) =>
            {
                if (_syncing || _loading) return;
                int measures = (int)Math.Round(e.NewValue);
                double beats = measures * _tsNum;
                _plugin.DurationBeats = beats;
                DurationValue.Text = measures + (measures == 1 ? " mesure" : " mesures");
                KotonHost.NotifyDurationChanged?.Invoke(beats);
            };

            Wire(VossOctavesSlider,  VossOctavesValue,  "voss_octaves",  v => v.ToString("F0"));
            Wire(LogisticRSlider,    LogisticRValue,    "logistic_r",    v => v.ToString("F3"));
            Wire(LorenzSpeedSlider,  LorenzSpeedValue,  "lorenz_speed",  v => v.ToString("F3"));
        }

        void Wire(Slider slider, TextBlock valueLabel, string paramId, Func<double, string> fmt)
        {
            slider.ValueChanged += (s, e) =>
            {
                if (_syncing || _loading) return;
                _plugin.SetParam(paramId, e.NewValue);
                valueLabel.Text = fmt(e.NewValue);
            };
        }

        // =============================================================================================
        // Refresh (paramètres → UI)
        // =============================================================================================
        void RefreshFromPlugin()
        {
            _syncing = true;
            foreach (var kp in _plugin.Parameters)
            {
                switch (kp.Id)
                {
                    case "algo":
                        AlgoCombo.SelectedIndex = (int)kp.Value;
                        break;
                    case "notes_per_beat":
                        NotesPerBeatCombo.SelectedIndex = NotesPerBeatIndex((int)kp.Value);
                        break;
                    case "base_octave":     OctaveSlider.Value = kp.Value;   OctaveValue.Text   = kp.Value.ToString("F0"); break;
                    case "range":           RangeSlider.Value = kp.Value;    RangeValue.Text    = kp.Value.ToString("F0") + " st"; break;
                    case "snap_mode":       SnapCombo.SelectedIndex = (int)kp.Value; break;
                    case "velocity":        VelocitySlider.Value = kp.Value; VelocityValue.Text = kp.Value.ToString("F0"); break;
                    case "vel_var":         VelVarSlider.Value = kp.Value;   VelVarValue.Text   = kp.Value.ToString("F2"); break;
                    case "seed":            SeedSlider.Value = kp.Value;     SeedValue.Text     = kp.Value.ToString("F0"); break;
                    case "articulation":    ArticulationCombo.SelectedIndex = (int)kp.Value; break;

                    case "voss_octaves":    VossOctavesSlider.Value = kp.Value;  VossOctavesValue.Text  = kp.Value.ToString("F0"); break;
                    case "logistic_r":      LogisticRSlider.Value = kp.Value;    LogisticRValue.Text    = kp.Value.ToString("F3"); break;
                    case "lorenz_speed":    LorenzSpeedSlider.Value = kp.Value;  LorenzSpeedValue.Text  = kp.Value.ToString("F3"); break;
                    case "lorenz_dim":      LorenzDimCombo.SelectedIndex = (int)kp.Value; break;
                }
            }
            int curMeasures = Math.Max(1, Math.Min(32, (int)Math.Round(_plugin.DurationBeats / Math.Max(1, _tsNum))));
            DurationSlider.Value = curMeasures;
            DurationValue.Text = curMeasures + (curMeasures == 1 ? " mesure" : " mesures");
            _syncing = false;
        }

        static int NotesPerBeatIndex(int nb)
        {
            switch (nb)
            {
                case 1: return 0;
                case 2: return 1;
                case 3: return 2;
                case 4: return 3;
                case 6: return 4;
                case 8: return 5;
                default: return 3;
            }
        }

        static int NotesPerBeatValue(int index)
        {
            switch (index)
            {
                case 0: return 1;
                case 1: return 2;
                case 2: return 3;
                case 3: return 4;
                case 4: return 6;
                case 5: return 8;
                default: return 4;
            }
        }

        // =============================================================================================
        // Combo handlers
        // =============================================================================================
        void AlgoCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_syncing || _loading) return;
            _plugin.SetParam("algo", AlgoCombo.SelectedIndex);
            UpdatePanelVisibility();
        }

        void NotesPerBeatCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_syncing || _loading) return;
            _plugin.SetParam("notes_per_beat", NotesPerBeatValue(NotesPerBeatCombo.SelectedIndex));
        }

        void SnapCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_syncing || _loading) return;
            _plugin.SetParam("snap_mode", SnapCombo.SelectedIndex);
        }

        void ArticulationCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_syncing || _loading) return;
            _plugin.SetParam("articulation", ArticulationCombo.SelectedIndex);
        }

        void LorenzDimCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_syncing || _loading) return;
            _plugin.SetParam("lorenz_dim", LorenzDimCombo.SelectedIndex);
        }

        // =============================================================================================
        // Visibilité des panneaux algo-spécifiques
        // =============================================================================================
        void UpdatePanelVisibility()
        {
            int a = AlgoCombo.SelectedIndex;
            VossPanel.Visibility     = a == 0 ? Visibility.Visible : Visibility.Collapsed;
            LogisticPanel.Visibility = a == 1 ? Visibility.Visible : Visibility.Collapsed;
            LorenzPanel.Visibility   = a == 2 ? Visibility.Visible : Visibility.Collapsed;
        }

        // =============================================================================================
        // Boutons
        // =============================================================================================
        void RerollButton_Click(object sender, RoutedEventArgs e)
        {
            _plugin.RerollSeed();
            RefreshFromPlugin();
        }

        void PreviewButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var ctx = KotonHost.CurrentContext?.Invoke() ?? new KotonRenderContext { Tonic = 0, IsMajor = true, Tempo = 120, TimeSigNum = 4, TimeSigDen = 4 };
                var notes = _plugin.RenderNotes(0, _plugin.DurationBeats, ctx);
                var list = new List<KotonGeneratedNote>();
                foreach (var n in notes) list.Add(n);
                KotonHost.PreviewNotes?.Invoke(list);
            }
            catch (Exception ex)
            {
                KotonHost.ReportException?.Invoke(ex, "Fractale");
            }
        }

        void StopButton_Click(object sender, RoutedEventArgs e)
        {
            KotonHost.StopPreview?.Invoke();
        }

        // =============================================================================================
        // IKotonEditor
        // =============================================================================================
        public void OnContextUpdated(KotonRenderContext ctx)
        {
            // Le slider Durée est libellé en MESURES — sa valeur affichée dépend de la signature
            // temporelle. En 4/4 : 1 mesure = 4 beats. En 3/4 : 1 mesure = 3 beats. Etc. Quand la
            // métrique du projet change, on rafraîchit la position du slider pour que le nombre
            // affiché reste musical.
            if (ctx != null && ctx.TimeSigNum > 0)
            {
                int newTs = ctx.TimeSigNum;
                if (newTs != _tsNum)
                {
                    _tsNum = newTs;
                    // Re-calculer la position du slider avec la nouvelle métrique — le stockage
                    // interne est en beats (invariant), seul l'affichage change.
                    RefreshFromPlugin();
                }
            }
        }
    }
}
