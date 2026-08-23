using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using KotonStudio.Library;

namespace KotonPluginSplineMelody
{
    /// <summary>
    /// Éditeur 2-colonnes : paramètres généraux à gauche, TabControl à droite (Contours + Rythmes).
    /// Une barre de « chips » colorés dans chaque onglet sélectionne la voix active à éditer ; le
    /// choix survit à un switch d'onglet. Voix ACTIVE unique et partagée entre onglets — cohérent
    /// avec le mental « je bosse sur la voix N, canvas et rythme suivent ».
    /// </summary>
    public partial class SplineMelodyEditor : UserControl, IKotonEditor
    {
        readonly SplineMelodyGenerator _plugin;
        bool _updating;
        int _activeVoice;   // 0-based, valide dans [0, voiceCount)

        SplineCanvas _canvas;
        MultiVoiceRhythmGrid _rhythmGrid;

        public SplineMelodyEditor(SplineMelodyGenerator plugin)
        {
            _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
            InitializeComponent();

            for (int i = 1; i <= SplineMelodyGenerator.MaxVoices; i++) cboVoiceCount.Items.Add(i.ToString());
            for (int i = 1; i <= 16; i++) cboBars.Items.Add(i.ToString(CultureInfo.InvariantCulture));
            cboStartMode.Items.Add("Fixe");
            cboStartMode.Items.Add("Auto");
            cboInterp.Items.Add("Linéaire");
            cboInterp.Items.Add("Spline");
            cboArticulation.Items.Add("Legato");
            cboArticulation.Items.Add("Normal");
            cboArticulation.Items.Add("Détaché");
            cboArticulation.Items.Add("Staccato");

            _canvas = new SplineCanvas(_plugin.GetVoice, () => _activeVoice, GetVoiceCount, IsSplineInterp);
            _canvas.Changed += () => { /* la modif est déjà dans le modèle du plugin */ };
            canvasHost.Content = _canvas;

            _rhythmGrid = new MultiVoiceRhythmGrid(_plugin.GetVoice, GetVoiceCount);
            _rhythmGrid.Changed += () => { /* les modifs sont ecrites directement dans les VoiceSpec.Rhythm */ };
            rhythmHost.Content = _rhythmGrid;

            SyncFromPlugin();
            RebuildVoiceChips();
            _rhythmGrid.ReloadFromModel();

            var ps = _plugin.Parameters;
            foreach (var kp in ps) kp.Changed += _ => Dispatcher.BeginInvoke(new Action(() =>
            {
                SyncFromPlugin();
                RebuildVoiceChips();
                _canvas.Redraw();   // interpolation ou autre param → canvas se re-render (WYSIWYG)
            }));

            cboVoiceCount.SelectionChanged += (s, e) =>
            {
                if (_updating) return;
                SetParam("voice_count", cboVoiceCount.SelectedIndex + 1);
                for (int i = 0; i <= cboVoiceCount.SelectedIndex; i++) _plugin.GetVoice(i);
                if (_activeVoice > cboVoiceCount.SelectedIndex) _activeVoice = cboVoiceCount.SelectedIndex;
                RebuildVoiceChips();
                _rhythmGrid.ReloadFromModel();
                _canvas.Redraw();
            };
            cboBars.SelectionChanged += (s, e) => { if (_updating) return; _plugin.SetDurationBars(cboBars.SelectedIndex + 1); };
            cboStartMode.SelectionChanged += (s, e) => SetParam("start_mode", cboStartMode.SelectedIndex);
            txtStartMidi.LostFocus += (s, e) => { if (int.TryParse(txtStartMidi.Text, out int v)) SetParam("start_midi", Math.Max(0, Math.Min(127, v))); };
            sldAmbitus.ValueChanged += (s, e) => { SetParam("ambitus_semis", sldAmbitus.Value); lblAmbitus.Text = ((int)sldAmbitus.Value).ToString(CultureInfo.InvariantCulture) + " st"; };
            cboInterp.SelectionChanged += (s, e) => SetParam("interpolation", cboInterp.SelectedIndex);
            sldVelocity.ValueChanged += (s, e) => { SetParam("velocity", sldVelocity.Value); lblVelocity.Text = ((int)sldVelocity.Value).ToString(CultureInfo.InvariantCulture); };
            cboArticulation.SelectionChanged += (s, e) => SetParam("articulation", cboArticulation.SelectedIndex);

            Loaded += (s, e) => _canvas.Redraw();
        }

        int GetVoiceCount()
        {
            int c = 1;
            for (int i = 0; i < _plugin.Parameters.Count; i++)
                if (_plugin.Parameters[i].Id == "voice_count") { c = (int)_plugin.Parameters[i].Value; break; }
            return Math.Max(1, Math.Min(SplineMelodyGenerator.MaxVoices, c));
        }

        void SetParam(string id, double value)
        {
            if (_updating) return;
            for (int i = 0; i < _plugin.Parameters.Count; i++)
                if (_plugin.Parameters[i].Id == id) { _plugin.Parameters[i].Value = value; return; }
        }

        void SyncFromPlugin()
        {
            _updating = true;
            try
            {
                double GetV(string id)
                {
                    for (int i = 0; i < _plugin.Parameters.Count; i++)
                        if (_plugin.Parameters[i].Id == id) return _plugin.Parameters[i].Value;
                    return 0;
                }
                int vc = (int)GetV("voice_count");
                cboVoiceCount.SelectedIndex = Math.Max(0, Math.Min(SplineMelodyGenerator.MaxVoices - 1, vc - 1));
                int bars = (int)_plugin.DurationBarsValue;
                cboBars.SelectedIndex = Math.Max(0, Math.Min(15, bars - 1));
                int sm = (int)GetV("start_mode");
                cboStartMode.SelectedIndex = Math.Max(0, Math.Min(1, sm));
                txtStartMidi.Text = ((int)GetV("start_midi")).ToString(CultureInfo.InvariantCulture);
                sldAmbitus.Value = GetV("ambitus_semis");
                lblAmbitus.Text = ((int)sldAmbitus.Value).ToString(CultureInfo.InvariantCulture) + " st";
                int it = (int)GetV("interpolation");
                cboInterp.SelectedIndex = Math.Max(0, Math.Min(1, it));
                sldVelocity.Value = GetV("velocity");
                lblVelocity.Text = ((int)sldVelocity.Value).ToString(CultureInfo.InvariantCulture);
                int ar = (int)GetV("articulation");
                cboArticulation.SelectedIndex = Math.Max(0, Math.Min(3, ar));
            }
            finally { _updating = false; }
        }

        // -----------------------------------------------------------------------------------------
        // Voice chips — construits en 2 exemplaires (Contours + Rythmes), toujours synchronisés
        // -----------------------------------------------------------------------------------------
        void RebuildVoiceChips()
        {
            voiceChipsContours.Children.Clear();
            int n = GetVoiceCount();
            if (_activeVoice < 0) _activeVoice = 0;
            if (_activeVoice >= n) _activeVoice = n - 1;
            for (int i = 0; i < n; i++) voiceChipsContours.Children.Add(BuildChip(i));
        }

        Button BuildChip(int voiceIdx)
        {
            var spec = _plugin.GetVoice(voiceIdx);
            Color color = spec != null ? spec.Color : SplineMelodyGenerator.DefaultColorFor(voiceIdx);
            bool active = voiceIdx == _activeVoice;

            var swatch = new Border
            {
                Width = 12, Height = 12,
                Background = new SolidColorBrush(color),
                CornerRadius = new CornerRadius(6),
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            var text = new TextBlock
            {
                Text = "Voix " + (voiceIdx + 1),
                Foreground = active ? Brushes.White : (Brush)FindResource("KotonTextDim"),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(swatch);
            content.Children.Add(text);

            var btn = new Button
            {
                Content = content,
                Margin = new Thickness(0, 0, 6, 0),
                Padding = new Thickness(8, 3, 10, 3),
                Cursor = System.Windows.Input.Cursors.Hand,
                Tag = voiceIdx,
            };
            if (active)
            {
                btn.Background = new SolidColorBrush(Color.FromArgb(60, color.R, color.G, color.B));
                btn.BorderBrush = new SolidColorBrush(color);
            }
            btn.Click += (s, e) =>
            {
                if (!(btn.Tag is int idx)) return;
                _activeVoice = idx;
                RebuildVoiceChips();
                _canvas.Redraw();
                // La grille rythme montre TOUTES les voix — pas besoin de la recharger sur switch.
            };
            return btn;
        }

        bool IsSplineInterp()
        {
            for (int i = 0; i < _plugin.Parameters.Count; i++)
                if (_plugin.Parameters[i].Id == "interpolation") return _plugin.Parameters[i].Value >= 0.5;
            return false;
        }

        public void OnContextUpdated(KotonRenderContext ctx)
        {
            Dispatcher.BeginInvoke(new Action(SyncFromPlugin));
        }
    }
}
