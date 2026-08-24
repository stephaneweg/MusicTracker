using System;
using System.Collections.Generic;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using KotonStudio.Library;

namespace KotonPluginLifeGrid
{
    /// <summary>
    /// Éditeur de <see cref="LifeGrid"/> : deux grilles côte à côte (le motif dessiné à gauche, son
    /// évolution animée à droite) et les réglages de mapping en dessous.
    ///
    /// L'animation de droite rejoue la simulation RÉELLE (<see cref="LifeGrid.Simulate"/>) et pas une
    /// approximation : ce qu'on voit défiler est exactement la suite de générations que le rendu va
    /// sonoriser. Toute modification (dessin, règle, taille, relance) invalide la simulation, qui est
    /// recalculée au tick suivant — recalculer dans le handler ferait ramer le dessin à la souris.
    /// </summary>
    public partial class LifeGridEditor : UserControl, IKotonEditor
    {
        readonly LifeGrid _plugin;
        readonly DispatcherTimer _timer;
        List<byte[]> _gens;
        int _genIndex;
        bool _dirty = true;
        bool _syncing;
        /// <summary>Vrai pendant InitializeComponent : poser Minimum sur un Slider force sa Value et
        /// déclenche ValueChanged AVANT que les autres contrôles n'existent. Sans ce garde-fou, le
        /// premier handler touche un champ encore null et l'éditeur ne s'ouvre jamais.</summary>
        bool _loading = true;

        const int MinPreviewGens = 16, MaxPreviewGens = 256;

        public LifeGridEditor(LifeGrid plugin)
        {
            _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
            InitializeComponent();

            // Item 0 = libellé permanent : le combo se comporte comme un menu d'action (on repose la
            // même amorce autant de fois qu'on veut) au lieu de garder une sélection qui ne veut rien
            // dire une fois le motif dessiné par-dessus.
            StampCombo.Items.Add("Poser une amorce…");
            foreach (var n in LifeGrid.StampNames) StampCombo.Items.Add(n);
            foreach (var n in LifeGrid.RuleNames) RuleCombo.Items.Add(n);
            foreach (var n in LifeGrid.ScaleNames) ScaleCombo.Items.Add(n);
            foreach (var n in LifeGrid.ReadModeNames) ReadCombo.Items.Add(n);
            foreach (var n in LifeGrid.DurModeNames) DurCombo.Items.Add(n);
            foreach (var n in LifeGrid.ReviveNames) ReviveCombo.Items.Add(n);

            SeedCanvas.Editable = true;
            SeedCanvas.CellPainted += OnCellPainted;
            EvoCanvas.Editable = false;

            Wire(GpbSlider, GpbValue, "gens_per_beat", v => v.ToString("F0"), true);
            Wire(GateSlider, GateValue, "gate", v => v.ToString("F2"), false);
            Wire(VoicesSlider, VoicesValue, "max_voices", v => v.ToString("F0"), false);
            Wire(OctSlider, OctValue, "base_octave", v => v.ToString("F0"), false);
            Wire(VelSlider, VelValue, "velocity", v => v.ToString("F0"), false);
            Wire(AccentSlider, AccentValue, "accent", v => v.ToString("F2"), false);
            Wire(DensitySlider, DensityValue, "density", v => "densité " + v.ToString("F2"), true);
            Wire(SeedSlider, null, "rng_seed", null, true);

            Refresh();

            _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(130) };
            _timer.Tick += OnTick;
            Loaded += (s, e) => _timer.Start();
            Unloaded += (s, e) => _timer.Stop();

            _loading = false;
        }

        // ------------------------------------------------------------------ liaison paramètres
        /// <summary>Relie un slider à un paramètre du plugin. <paramref name="resim"/> indique que le
        /// paramètre change la SIMULATION (et pas seulement la sonorisation) — dans ce cas l'aperçu
        /// doit être recalculé.</summary>
        void Wire(Slider s, TextBlock lbl, string id, Func<double, string> fmt, bool resim)
        {
            s.ValueChanged += (o, e) =>
            {
                if (_syncing || _loading) return;
                _plugin.SetParam(id, e.NewValue);
                if (lbl != null && fmt != null) lbl.Text = fmt(e.NewValue);
                if (resim) Invalidate();
            };
        }

        void Invalidate() { _dirty = true; }

        void Refresh()
        {
            _syncing = true;
            var seed = _plugin.Seed;

            ColsSlider.Value = seed.Cols; ColsValue.Text = seed.Cols + " colonnes";
            RowsSlider.Value = seed.Rows; RowsValue.Text = seed.Rows + " lignes";

            int rule = (int)Math.Round(_plugin.GetParam("rule_preset"));
            RuleCombo.SelectedIndex = Math.Max(0, Math.Min(LifeGrid.RuleNames.Length - 1, rule));
            BirthBox.Text = MaskDigits((int)_plugin.GetParam("birth_mask"));
            SurvBox.Text = MaskDigits((int)_plugin.GetParam("surv_mask"));
            bool custom = RuleCombo.SelectedIndex == LifeGrid.RuleNames.Length - 1;
            BirthBox.IsEnabled = custom;
            SurvBox.IsEnabled = custom;

            ScaleCombo.SelectedIndex = (int)Math.Round(_plugin.GetParam("scale"));
            ReadCombo.SelectedIndex = (int)Math.Round(_plugin.GetParam("read_mode"));
            DurCombo.SelectedIndex = (int)Math.Round(_plugin.GetParam("dur_mode"));
            ReviveCombo.SelectedIndex = (int)Math.Round(_plugin.GetParam("revive"));
            ChordCheck.IsChecked = _plugin.GetParam("chord_aware") >= 0.5;

            GpbSlider.Value = _plugin.GetParam("gens_per_beat"); GpbValue.Text = GpbSlider.Value.ToString("F0");
            GateSlider.Value = _plugin.GetParam("gate"); GateValue.Text = GateSlider.Value.ToString("F2");
            VoicesSlider.Value = _plugin.GetParam("max_voices"); VoicesValue.Text = VoicesSlider.Value.ToString("F0");
            OctSlider.Value = _plugin.GetParam("base_octave"); OctValue.Text = OctSlider.Value.ToString("F0");
            VelSlider.Value = _plugin.GetParam("velocity"); VelValue.Text = VelSlider.Value.ToString("F0");
            AccentSlider.Value = _plugin.GetParam("accent"); AccentValue.Text = AccentSlider.Value.ToString("F2");
            DensitySlider.Value = _plugin.GetParam("density"); DensityValue.Text = "densité " + DensitySlider.Value.ToString("F2");
            SeedSlider.Value = _plugin.GetParam("rng_seed");

            SeedCanvas.SetState(seed.Cols, seed.Rows, seed.Cells);
            StampCombo.SelectedIndex = 0;
            _syncing = false;
            Invalidate();
        }

        // ------------------------------------------------------------------ animation
        void OnTick(object sender, EventArgs e)
        {
            if (_dirty || _gens == null)
            {
                int gpb = Math.Max(1, (int)Math.Round(_plugin.GetParam("gens_per_beat")));
                int n = (int)Math.Ceiling(_plugin.DurationBeats * gpb);
                n = Math.Max(MinPreviewGens, Math.Min(MaxPreviewGens, n));
                _gens = _plugin.Simulate(n);
                _genIndex = 0;
                _dirty = false;
            }
            if (_gens == null || _gens.Count == 0) return;

            var seed = _plugin.Seed;
            var cur = _gens[_genIndex];
            var prev = _genIndex > 0 ? _gens[_genIndex - 1] : null;
            EvoCanvas.SetState(seed.Cols, seed.Rows, cur, prev);
            GenLabel.Text = "gén. " + _genIndex + " / " + (_gens.Count - 1);

            _genIndex = (_genIndex + 1) % _gens.Count;
        }

        void Anim_Click(object sender, RoutedEventArgs e)
        {
            if (_timer.IsEnabled) { _timer.Stop(); AnimBtn.Content = "▶ Animation"; }
            else { _timer.Start(); AnimBtn.Content = "⏸ Animation"; }
        }

        // ------------------------------------------------------------------ motif
        void OnCellPainted(int x, int y, bool on)
        {
            _plugin.Seed = _plugin.Seed.WithCell(x, y, on);
            var s = _plugin.Seed;
            SeedCanvas.SetState(s.Cols, s.Rows, s.Cells);
            Invalidate();
        }

        void Stamp_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_syncing || _loading) return;
            int idx = StampCombo.SelectedIndex - 1;   // l'item 0 est le libellé du menu
            if (idx < 0) return;
            var s = _plugin.Seed;
            // Posée à peu près au centre : les amorces classiques font au plus 7 cellules de côté.
            _plugin.Seed = LifeGrid.ApplyStamp(s, idx, Math.Max(0, s.Cols / 2 - 3), Math.Max(0, s.Rows / 2 - 2));
            s = _plugin.Seed;
            SeedCanvas.SetState(s.Cols, s.Rows, s.Cells);
            Invalidate();

            // Retour au libellé pour que re-choisir la même amorce la repose.
            _syncing = true;
            StampCombo.SelectedIndex = 0;
            _syncing = false;
        }

        void Random_Click(object sender, RoutedEventArgs e)
        {
            var s = _plugin.Seed;
            // La graine avance à chaque clic : le bouton sert à PIOCHER, pas à re-tirer la même chose.
            int next = ((int)Math.Round(_plugin.GetParam("rng_seed")) + 1) % 1000;
            _plugin.SetParam("rng_seed", next);
            _plugin.Seed = LifeGrid.RandomPattern(s.Cols, s.Rows, _plugin.GetParam("density"), next);
            s = _plugin.Seed;
            SeedCanvas.SetState(s.Cols, s.Rows, s.Cells);
            _syncing = true; SeedSlider.Value = next; _syncing = false;
            Invalidate();
        }

        void Clear_Click(object sender, RoutedEventArgs e)
        {
            var s = _plugin.Seed;
            _plugin.Seed = LifePattern.Empty(s.Cols, s.Rows);
            s = _plugin.Seed;
            SeedCanvas.SetState(s.Cols, s.Rows, s.Cells);
            Invalidate();
        }

        void Size_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_syncing || _loading) return;
            int cols = (int)Math.Round(ColsSlider.Value), rows = (int)Math.Round(RowsSlider.Value);
            _plugin.Resize(cols, rows);
            var s = _plugin.Seed;
            ColsValue.Text = s.Cols + " colonnes";
            RowsValue.Text = s.Rows + " lignes";
            SeedCanvas.SetState(s.Cols, s.Rows, s.Cells);
            Invalidate();
        }

        // ------------------------------------------------------------------ règle
        void Rule_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_syncing || _loading) return;
            int idx = RuleCombo.SelectedIndex;
            if (idx < 0) return;
            _plugin.ApplyRulePreset(idx);
            bool custom = idx == LifeGrid.RuleNames.Length - 1;
            BirthBox.IsEnabled = custom;
            SurvBox.IsEnabled = custom;
            _syncing = true;
            BirthBox.Text = MaskDigits((int)_plugin.GetParam("birth_mask"));
            SurvBox.Text = MaskDigits((int)_plugin.GetParam("surv_mask"));
            _syncing = false;
            Invalidate();
        }

        void Mask_Changed(object sender, TextChangedEventArgs e)
        {
            if (_syncing || _loading) return;
            _plugin.SetParam("birth_mask", DigitsToMask(BirthBox.Text));
            _plugin.SetParam("surv_mask", DigitsToMask(SurvBox.Text));
            Invalidate();
        }

        /// <summary>« 23 » → bits 2 et 3. Les caractères non numériques sont ignorés, ce qui laisse
        /// l'utilisateur taper librement (« B23 », « 2,3 ») sans casser la saisie.</summary>
        static int DigitsToMask(string s)
        {
            int mask = 0;
            if (string.IsNullOrEmpty(s)) return 0;
            foreach (char c in s) if (c >= '0' && c <= '8') mask |= 1 << (c - '0');
            return mask;
        }

        static string MaskDigits(int mask)
        {
            var sb = new StringBuilder();
            for (int i = 0; i <= 8; i++) if ((mask & (1 << i)) != 0) sb.Append(i);
            return sb.ToString();
        }

        // ------------------------------------------------------------------ combos simples
        void Scale_Changed(object sender, SelectionChangedEventArgs e) { if (!_syncing && !_loading) _plugin.SetParam("scale", ScaleCombo.SelectedIndex); }
        void Read_Changed(object sender, SelectionChangedEventArgs e) { if (!_syncing && !_loading) _plugin.SetParam("read_mode", ReadCombo.SelectedIndex); }
        void Dur_Changed(object sender, SelectionChangedEventArgs e) { if (!_syncing && !_loading) _plugin.SetParam("dur_mode", DurCombo.SelectedIndex); }
        void Chord_Click(object sender, RoutedEventArgs e) { if (!_syncing && !_loading) _plugin.SetParam("chord_aware", ChordCheck.IsChecked == true ? 1 : 0); }

        void Revive_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_syncing || _loading) return;
            _plugin.SetParam("revive", ReviveCombo.SelectedIndex);
            Invalidate();
        }

        // ------------------------------------------------------------------ écoute
        void Play_Click(object sender, RoutedEventArgs e)
        {
            var prev = KotonHost.CurrentContext != null ? KotonHost.CurrentContext() : null;
            KotonHost.PreviewNotes?.Invoke(_plugin.RenderNotes(0, _plugin.DurationBeats, prev));
        }

        void Stop_Click(object sender, RoutedEventArgs e) => KotonHost.StopPreview?.Invoke();

        public void OnContextUpdated(KotonRenderContext ctx) { Invalidate(); }
    }
}
