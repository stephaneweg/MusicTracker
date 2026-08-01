using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using KotonStudio.Library;

namespace KotonPluginWaveMorph
{
    /// <summary>
    /// Editeur WPF du Wave Morph. Le XAML fournit la STRUCTURE (bandes / bordures / hosts nommes),
    /// le code-behind fournit les CONTROLES INTERACTIFS montes dynamiquement (sliders, boutons
    /// segmented, mod matrix, envelopes graphiques) — chaque controle est bindé sur son KotonParameter
    /// en two-way (slider → param, event Changed du param → slider quand le preset / automation pose).
    ///
    /// **Rendu live** : un DispatcherTimer 30 Hz refresh :
    /// - les 3 vues wave (W1/Result/W2), redessinees si un des params osc a change
    /// - la bande onde finale (scope live)
    /// - les envelopes (redessinees quand un param ADSR change)
    ///
    /// **Interactions** :
    /// - Sliders : two-way via ValueChanged + p.Changed
    /// - Boutons segmented (Serial/Parallel, Voice mode) : click = pose la valeur, re-highlight
    /// - Shape picker (mini icones sine/square/tri/saw en haut a droite de chaque wave view) : bouton
    /// - Mod matrix : chaque case = un cycle 0 → +50 → +100 → -50 → -100 → 0 (interaction simple sans
    ///   dialog, pour rester scannable). Le drag n'est pas implemente en v1 (le user pose des points
    ///   discrets suffisants pour prototyper).
    ///
    /// **Timer de refresh** : le meme timer sert a tout (30 Hz). Un flag _lastWaveHash evite de
    /// redessiner les vues quand aucun param osc n'a change entre 2 ticks — economise le CPU quand
    /// le user ne touche a rien.
    /// </summary>
    public partial class WaveMorphEditor : UserControl
    {
        readonly WaveMorphPlugin _plugin;

        DispatcherTimer _refreshTimer;

        // Displays reutilisables
        WaveDisplayControl _wave1Display;
        WaveDisplayControl _wave2Display;
        WaveDisplayControl _resultDisplay;
        WaveDisplayControl _scopeDisplay;

        // Envelope displays (Canvas + Polyline)
        EnvelopeDisplay _envAmpDisplay;
        EnvelopeDisplay _env2Display;
        EnvelopeDisplay _env3Display;

        // LFO displays
        LfoDisplay _lfo1Display;
        LfoDisplay _lfo2Display;

        // Buffers reutilisables (evitent l'alloc par frame)
        readonly float[] _wave1Buf = new float[512];
        readonly float[] _wave2Buf = new float[512];
        readonly float[] _resultBuf = new float[512];
        readonly float[] _scopeBuf = new float[WaveMorphPlugin.ScopeSize];

        // Hash "signature" des params osc pour n'appeler les redraws que si necessaire.
        int _lastWaveHash;

        // Mod matrix : les boutons Button dans la table, cle = (row, col)
        readonly Dictionary<(ModTarget tgt, ModSource src), Button> _mmCells = new Dictionary<(ModTarget, ModSource), Button>();

        // Shape pickers (buttons par forme)
        readonly Button[] _wave1PickerBtns = new Button[WaveOsc.Count];
        readonly Button[] _wave2PickerBtns = new Button[WaveOsc.Count];

        public WaveMorphEditor(WaveMorphPlugin plugin)
        {
            InitializeComponent();
            _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        void OnLoaded(object sender, RoutedEventArgs e)
        {
            BuildWaveDisplays();
            BuildShapePickers();
            BuildEnvelopeDisplays();
            BuildLfoDisplays();
            BuildKvGrids();
            BuildModMatrix();
            BuildFilterPanels();
            BuildOutputPanel();
            HookXFadeSlider();
            HookWaveInfoSync();

            // Init state visuel des toggles routing
            RefreshRoutingButtons();
            _plugin.Parameters[FindParamIndex("f_routing")].Changed += _ => Dispatcher.BeginInvoke((Action)RefreshRoutingButtons);

            // Timer refresh (30 Hz) — synchronise l'affichage live des scopes et les vues wave
            _refreshTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(33)
            };
            _refreshTimer.Tick += (s, ev) => RefreshFrame();
            _refreshTimer.Start();
            RefreshFrame();
        }

        void OnUnloaded(object sender, RoutedEventArgs e)
        {
            if (_refreshTimer != null) { _refreshTimer.Stop(); _refreshTimer = null; }
        }

        // =========================================================================================
        // Wave displays (W1 / Result / W2 / scope live)
        // =========================================================================================

        void BuildWaveDisplays()
        {
            _wave1Display = new WaveDisplayControl();
            _wave1Display.SetColor(Color.FromRgb(0x88, 0x88, 0x88));
            var w1HostContent = new ContentControl { Content = _wave1Display };
            ((ContentPresenter)FindName("Wave1Host")).Content = _wave1Display;

            _wave2Display = new WaveDisplayControl();
            _wave2Display.SetColor(Color.FromRgb(0x88, 0x88, 0x88));
            ((ContentPresenter)FindName("Wave2Host")).Content = _wave2Display;

            _resultDisplay = new WaveDisplayControl();
            _resultDisplay.SetColor(Color.FromRgb(0x1F, 0xB6, 0xC3));
            ((ContentPresenter)FindName("ResultHost")).Content = _resultDisplay;

            _scopeDisplay = new WaveDisplayControl();
            _scopeDisplay.SetColor(Color.FromRgb(0x1F, 0xB6, 0xC3));
            var scopeHost = (Border)FindName("FinalScopeHost");
            scopeHost.Child = _scopeDisplay;
        }

        // =========================================================================================
        // Shape pickers (mini icones sine/square/tri/saw en haut-droite de chaque wave view)
        // =========================================================================================

        void BuildShapePickers()
        {
            BuildOnePicker((StackPanel)FindName("Wave1Picker"), _plugin.Parameters[FindParamIndex("w1_wave")], _wave1PickerBtns);
            BuildOnePicker((StackPanel)FindName("Wave2Picker"), _plugin.Parameters[FindParamIndex("w2_wave")], _wave2PickerBtns);
        }

        void BuildOnePicker(StackPanel host, KotonParameter waveParam, Button[] btns)
        {
            host.Children.Clear();
            for (int i = 0; i < WaveOsc.Count; i++)
            {
                int idx = i;
                var btn = new Button
                {
                    Width = 22, Height = 18,
                    Padding = new Thickness(0),
                    Margin = new Thickness(1),
                    Background = Brushes.Transparent,
                    BorderBrush = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    Cursor = Cursors.Hand,
                    Content = MakeShapeGlyph((WavePrim)i, Color.FromRgb(0x88, 0x88, 0x88)),
                    ToolTip = WaveOsc.Names[i],
                };
                btn.Template = MakeFlatButtonTemplate();
                btn.Click += (s, e) => { waveParam.Value = idx; };
                btns[i] = btn;
                host.Children.Add(btn);
            }
            waveParam.Changed += _ => Dispatcher.BeginInvoke((Action)(() => RefreshPicker(btns, waveParam)));
            RefreshPicker(btns, waveParam);
        }

        void RefreshPicker(Button[] btns, KotonParameter waveParam)
        {
            int sel = (int)Math.Round(waveParam.Value);
            var accent = Color.FromRgb(0x1F, 0xB6, 0xC3);
            var dim = Color.FromRgb(0x88, 0x88, 0x88);
            for (int i = 0; i < btns.Length; i++)
            {
                if (btns[i] == null) continue;
                bool on = i == sel;
                btns[i].Background = on ? new SolidColorBrush(Color.FromArgb(0x30, accent.R, accent.G, accent.B)) : Brushes.Transparent;
                btns[i].Content = MakeShapeGlyph((WavePrim)i, on ? accent : dim);
            }
        }

        static ControlTemplate MakeFlatButtonTemplate()
        {
            // Template minimal (Border transparent + ContentPresenter) — evite le chrome par defaut
            // de WPF qui redessine par-dessus notre Background.
            var tpl = new ControlTemplate(typeof(Button));
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(2));
            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            cp.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            border.AppendChild(cp);
            tpl.VisualTree = border;
            return tpl;
        }

        static FrameworkElement MakeShapeGlyph(WavePrim wave, Color color)
        {
            // Petit path SVG-like (14x10) — la meme silhouette que dans le mockup HTML.
            var canvas = new Canvas { Width = 14, Height = 10 };
            var stroke = new SolidColorBrush(color); stroke.Freeze();
            PathGeometry geom = null;
            switch (wave)
            {
                case WavePrim.Sine:
                    geom = MakePath("M1,5 Q3.5,1 7,5 T13,5");
                    break;
                case WavePrim.Square:
                    geom = MakePath("M1,8 L1,2 L7,2 L7,8 L13,8 L13,2");
                    break;
                case WavePrim.Triangle:
                    geom = MakePath("M1,8 L4,2 L7,8 L10,2 L13,8");
                    break;
                case WavePrim.Sawtooth:
                    geom = MakePath("M1,8 L7,2 L7,8 L13,2");
                    break;
            }
            var path = new Path { Data = geom, Stroke = stroke, StrokeThickness = 1.2, Fill = null };
            canvas.Children.Add(path);
            return canvas;
        }

        static PathGeometry MakePath(string mini)
        {
            // Parseur mini d'une syntaxe SVG-like tres restreinte (M, L, Q, T). Suffit pour nos 4 formes.
            var fig = new PathFigure { IsClosed = false, IsFilled = false };
            var pg = new PathGeometry();
            pg.Figures.Add(fig);
            int i = 0;
            char cur = ' ';
            Point lastCtrl = new Point();
            while (i < mini.Length)
            {
                char c = mini[i];
                if (c == ' ' || c == ',') { i++; continue; }
                if (c == 'M' || c == 'L' || c == 'Q' || c == 'T') { cur = c; i++; continue; }
                // Lire un nombre
                int start = i;
                while (i < mini.Length && (char.IsDigit(mini[i]) || mini[i] == '.' || mini[i] == '-')) i++;
                double x = double.Parse(mini.Substring(start, i - start), CultureInfo.InvariantCulture);
                while (i < mini.Length && (mini[i] == ' ' || mini[i] == ',')) i++;
                start = i;
                while (i < mini.Length && (char.IsDigit(mini[i]) || mini[i] == '.' || mini[i] == '-')) i++;
                double y = double.Parse(mini.Substring(start, i - start), CultureInfo.InvariantCulture);

                if (cur == 'M') { fig.StartPoint = new Point(x, y); }
                else if (cur == 'L') { fig.Segments.Add(new LineSegment(new Point(x, y), true)); }
                else if (cur == 'Q')
                {
                    // Q cx cy x2 y2 → besoin d'un 2e paire
                    while (i < mini.Length && (mini[i] == ' ' || mini[i] == ',')) i++;
                    start = i;
                    while (i < mini.Length && (char.IsDigit(mini[i]) || mini[i] == '.' || mini[i] == '-')) i++;
                    double x2 = double.Parse(mini.Substring(start, i - start), CultureInfo.InvariantCulture);
                    while (i < mini.Length && (mini[i] == ' ' || mini[i] == ',')) i++;
                    start = i;
                    while (i < mini.Length && (char.IsDigit(mini[i]) || mini[i] == '.' || mini[i] == '-')) i++;
                    double y2 = double.Parse(mini.Substring(start, i - start), CultureInfo.InvariantCulture);
                    var ctrl = new Point(x, y);
                    fig.Segments.Add(new QuadraticBezierSegment(ctrl, new Point(x2, y2), true));
                    lastCtrl = ctrl;
                    // Position "courante" implicite pour un T suivant :
                    // T reflete lastCtrl par rapport au point final. Ici on l'utilise a la volee.
                }
                else if (cur == 'T')
                {
                    // T x y : miroir du dernier control point
                    var last = fig.Segments[fig.Segments.Count - 1];
                    Point endPoint;
                    if (last is QuadraticBezierSegment qs) endPoint = qs.Point2;
                    else if (last is LineSegment ls) endPoint = ls.Point;
                    else endPoint = fig.StartPoint;
                    var reflected = new Point(2 * endPoint.X - lastCtrl.X, 2 * endPoint.Y - lastCtrl.Y);
                    fig.Segments.Add(new QuadraticBezierSegment(reflected, new Point(x, y), true));
                    lastCtrl = reflected;
                }
            }
            return pg;
        }

        // =========================================================================================
        // Envelopes graphiques
        // =========================================================================================

        void BuildEnvelopeDisplays()
        {
            _envAmpDisplay = new EnvelopeDisplay();
            ((Border)FindName("EnvAmpHost")).Child = _envAmpDisplay;

            _env2Display = new EnvelopeDisplay();
            ((Border)FindName("Env2Host")).Child = _env2Display;

            _env3Display = new EnvelopeDisplay();
            ((Border)FindName("Env3Host")).Child = _env3Display;

            // Redessin quand un des params ADSR change (evite le polling par timer).
            HookEnvRefresh(_envAmpDisplay, "amp_a", "amp_d", "amp_s", "amp_r");
            HookEnvRefresh(_env2Display, "e2_a", "e2_d", "e2_s", "e2_r");
            HookEnvRefresh(_env3Display, "e3_a", "e3_d", "e3_s", "e3_r");
        }

        void HookEnvRefresh(EnvelopeDisplay disp, string idA, string idD, string idS, string idR)
        {
            var pA = _plugin.Parameters[FindParamIndex(idA)];
            var pD = _plugin.Parameters[FindParamIndex(idD)];
            var pS = _plugin.Parameters[FindParamIndex(idS)];
            var pR = _plugin.Parameters[FindParamIndex(idR)];

            void refresh() => Dispatcher.BeginInvoke((Action)(() =>
                disp.SetAdsr(pA.Value, pD.Value, pS.Value, pR.Value)));

            pA.Changed += _ => refresh();
            pD.Changed += _ => refresh();
            pS.Changed += _ => refresh();
            pR.Changed += _ => refresh();
            disp.Loaded += (s, e) => disp.SetAdsr(pA.Value, pD.Value, pS.Value, pR.Value);
        }

        // =========================================================================================
        // LFO displays
        // =========================================================================================

        void BuildLfoDisplays()
        {
            _lfo1Display = new LfoDisplay();
            ((Border)FindName("Lfo1Host")).Child = _lfo1Display;
            HookLfoRefresh(_lfo1Display, "l1_shape", "l1_rate", "l1_amount", (TextBlock)FindName("Lfo1SubTitle"));

            _lfo2Display = new LfoDisplay();
            ((Border)FindName("Lfo2Host")).Child = _lfo2Display;
            HookLfoRefresh(_lfo2Display, "l2_shape", "l2_rate", "l2_amount", (TextBlock)FindName("Lfo2SubTitle"));
        }

        void HookLfoRefresh(LfoDisplay disp, string idShape, string idRate, string idAmt, TextBlock subTitle)
        {
            var pShape = _plugin.Parameters[FindParamIndex(idShape)];
            var pRate = _plugin.Parameters[FindParamIndex(idRate)];
            var pAmt = _plugin.Parameters[FindParamIndex(idAmt)];
            void refresh() => Dispatcher.BeginInvoke((Action)(() =>
            {
                var shape = Lfo.ShapeFromDouble(pShape.Value);
                disp.SetShape(shape, pAmt.Value);
                subTitle.Text = Lfo.ShapeNames[(int)shape];
            }));
            pShape.Changed += _ => refresh();
            pRate.Changed += _ => refresh();
            pAmt.Changed += _ => refresh();
            disp.Loaded += (s, e) =>
            {
                var shape = Lfo.ShapeFromDouble(pShape.Value);
                disp.SetShape(shape, pAmt.Value);
                subTitle.Text = Lfo.ShapeNames[(int)shape];
            };
        }

        // =========================================================================================
        // KV cells (labels + sliders compacts) sous chaque envelope et LFO
        // =========================================================================================

        void BuildKvGrids()
        {
            BuildAdsrKv((UniformGrid)FindName("EnvAmpKvGrid"), "amp_a", "amp_d", "amp_s", "amp_r");
            BuildAdsrKv((UniformGrid)FindName("Env2KvGrid"), "e2_a", "e2_d", "e2_s", "e2_r");
            BuildAdsrKv((UniformGrid)FindName("Env3KvGrid"), "e3_a", "e3_d", "e3_s", "e3_r");
            BuildLfoKv((UniformGrid)FindName("Lfo1KvGrid"), "l1_rate", "l1_amount", "l1_shape");
            BuildLfoKv((UniformGrid)FindName("Lfo2KvGrid"), "l2_rate", "l2_amount", "l2_shape");
        }

        void BuildAdsrKv(UniformGrid host, string idA, string idD, string idS, string idR)
        {
            host.Children.Clear();
            host.Children.Add(MakeKvSliderCell("A", _plugin.Parameters[FindParamIndex(idA)]));
            host.Children.Add(MakeKvSliderCell("D", _plugin.Parameters[FindParamIndex(idD)]));
            host.Children.Add(MakeKvSliderCell("S", _plugin.Parameters[FindParamIndex(idS)]));
            host.Children.Add(MakeKvSliderCell("R", _plugin.Parameters[FindParamIndex(idR)]));
        }

        void BuildLfoKv(UniformGrid host, string idRate, string idAmt, string idShape)
        {
            host.Children.Clear();
            host.Children.Add(MakeKvSliderCell("Rate", _plugin.Parameters[FindParamIndex(idRate)]));
            host.Children.Add(MakeKvSliderCell("Amt", _plugin.Parameters[FindParamIndex(idAmt)]));
            host.Children.Add(MakeKvComboCell("Shape", _plugin.Parameters[FindParamIndex(idShape)], Lfo.ShapeNames));
        }

        FrameworkElement MakeKvSliderCell(string label, KotonParameter p)
        {
            // Cellule = label en haut, slider + valeur superposes. Compact.
            var border = new Border
            {
                Background = (Brush)FindResource("PanelHi"),
                CornerRadius = new CornerRadius(2),
                Margin = new Thickness(1, 0, 1, 0),
                Padding = new Thickness(3),
            };
            var stack = new StackPanel();
            var lbl = new TextBlock
            {
                Text = label,
                FontSize = 9,
                Foreground = (Brush)FindResource("TextLo"),
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            var val = new TextBlock
            {
                Text = FormatValue(p),
                FontSize = 10,
                FontFamily = new FontFamily("Consolas"),
                Foreground = (Brush)FindResource("Accent"),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 1, 0, 0),
            };
            stack.Children.Add(lbl);
            stack.Children.Add(val);
            border.Child = stack;

            // Interaction : drag vertical sur la cellule = modifie la valeur (rapide, sans slider visible).
            bool dragging = false;
            Point dragOrigin = new Point();
            double startVal = 0;
            border.Cursor = Cursors.SizeNS;
            border.MouseLeftButtonDown += (s, e) =>
            {
                dragging = true;
                dragOrigin = e.GetPosition(border);
                startVal = p.Value;
                border.CaptureMouse();
                e.Handled = true;
            };
            border.MouseMove += (s, e) =>
            {
                if (!dragging) return;
                var cur = e.GetPosition(border);
                double dy = dragOrigin.Y - cur.Y;   // deplacement vers le HAUT = +
                double range = p.Max - p.Min;
                // 200 pixels = plein range (utilisateur fait un long drag pour un gros changement).
                double delta = (dy / 200.0) * range;
                p.Value = startVal + delta;
            };
            border.MouseLeftButtonUp += (s, e) =>
            {
                dragging = false;
                border.ReleaseMouseCapture();
            };
            p.Changed += _ => Dispatcher.BeginInvoke((Action)(() => val.Text = FormatValue(p)));

            return border;
        }

        FrameworkElement MakeKvComboCell(string label, KotonParameter p, string[] names)
        {
            var border = new Border
            {
                Background = (Brush)FindResource("PanelHi"),
                CornerRadius = new CornerRadius(2),
                Margin = new Thickness(1, 0, 1, 0),
                Padding = new Thickness(3),
            };
            var stack = new StackPanel();
            var lbl = new TextBlock
            {
                Text = label,
                FontSize = 9,
                Foreground = (Brush)FindResource("TextLo"),
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            var combo = new ComboBox
            {
                // Style implicite ComboBox du theme partage (KotonPluginTheme.xaml) applique
                // automatiquement — inutile de le referencer par nom (KotonCombo n'existe plus).
                FontSize = 9,
                Padding = new Thickness(2),
                MinWidth = 50,
                MinHeight = 18,
            };
            foreach (var n in names) combo.Items.Add(n);
            int idx = (int)Math.Round(p.Value);
            if (idx < 0) idx = 0; else if (idx >= names.Length) idx = names.Length - 1;
            combo.SelectedIndex = idx;
            bool syncing = false;
            combo.SelectionChanged += (s, e) =>
            {
                if (syncing) return;
                syncing = true;
                p.Value = combo.SelectedIndex;
                syncing = false;
            };
            p.Changed += v => Dispatcher.BeginInvoke((Action)(() =>
            {
                if (syncing) return;
                int i = (int)Math.Round(v);
                if (i < 0) i = 0; else if (i >= names.Length) i = names.Length - 1;
                syncing = true;
                combo.SelectedIndex = i;
                syncing = false;
            }));
            stack.Children.Add(lbl);
            stack.Children.Add(combo);
            border.Child = stack;
            return border;
        }

        // =========================================================================================
        // Mod matrix
        // =========================================================================================

        void BuildModMatrix()
        {
            var grid = (Grid)FindName("ModMatrixGrid");
            grid.Children.Clear();
            grid.RowDefinitions.Clear();
            grid.ColumnDefinitions.Clear();

            int cols = ModMatrix.SourceCount + 1;  // +1 pour la colonne label des targets
            for (int c = 0; c < cols; c++)
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            int rows = ModMatrix.TargetCount + 1;  // +1 pour la ligne header sources
            for (int r = 0; r < rows; r++)
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // Header sources
            for (int s = 0; s < ModMatrix.SourceCount; s++)
            {
                var tb = new TextBlock
                {
                    Text = ModMatrix.SourceNames[s],
                    FontSize = 9,
                    Foreground = (Brush)FindResource("TextDim"),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 2, 0, 4),
                };
                Grid.SetRow(tb, 0);
                Grid.SetColumn(tb, s + 1);
                grid.Children.Add(tb);
            }

            // Labels targets + cellules
            for (int t = 0; t < ModMatrix.TargetCount; t++)
            {
                var lbl = new TextBlock
                {
                    Text = ModMatrix.TargetNames[t],
                    FontSize = 10,
                    Foreground = (Brush)FindResource("Text"),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 1, 4, 1),
                };
                Grid.SetRow(lbl, t + 1);
                Grid.SetColumn(lbl, 0);
                grid.Children.Add(lbl);

                for (int s = 0; s < ModMatrix.SourceCount; s++)
                {
                    var cell = MakeModCell((ModTarget)t, (ModSource)s);
                    Grid.SetRow(cell, t + 1);
                    Grid.SetColumn(cell, s + 1);
                    grid.Children.Add(cell);
                }
            }
            RefreshMatrixVisuals();
        }

        Button MakeModCell(ModTarget tgt, ModSource src)
        {
            var btn = new Button
            {
                Content = "",
                FontSize = 9,
                FontFamily = new FontFamily("Consolas"),
                MinHeight = 18,
                Margin = new Thickness(0),
                Background = Brushes.Transparent,
                Foreground = (Brush)FindResource("TextLo"),
                BorderBrush = (Brush)FindResource("Border"),
                BorderThickness = new Thickness(0, 0, 1, 1),
                Cursor = Cursors.Hand,
                Padding = new Thickness(2),
                Template = MakeFlatButtonTemplate(),
            };
            btn.Click += (s, e) =>
            {
                float cur = _plugin.Matrix.GetAmount(src, tgt);
                float next = CycleAmount(cur);
                _plugin.Matrix.SetSlot(src, tgt, next);
                RefreshMatrixCell(tgt, src);
            };
            _mmCells[(tgt, src)] = btn;
            return btn;
        }

        // Cycle "clic pour changer" : 0 → +50 → +100 → -50 → -100 → 0
        static float CycleAmount(float cur)
        {
            if (Math.Abs(cur - 0f) < 0.001f) return 0.5f;
            if (Math.Abs(cur - 0.5f) < 0.001f) return 1.0f;
            if (Math.Abs(cur - 1.0f) < 0.001f) return -0.5f;
            if (Math.Abs(cur + 0.5f) < 0.001f) return -1.0f;
            return 0f;
        }

        void RefreshMatrixVisuals()
        {
            foreach (var kvp in _mmCells) RefreshMatrixCell(kvp.Key.tgt, kvp.Key.src);
        }

        void RefreshMatrixCell(ModTarget tgt, ModSource src)
        {
            if (!_mmCells.TryGetValue((tgt, src), out var btn)) return;
            float amt = _plugin.Matrix.GetAmount(src, tgt);
            if (Math.Abs(amt) < 0.001f)
            {
                btn.Content = "";
                btn.Background = Brushes.Transparent;
                btn.Foreground = (Brush)FindResource("TextLo");
            }
            else if (amt > 0)
            {
                btn.Content = "+" + ((int)Math.Round(amt * 100));
                btn.Background = new SolidColorBrush(Color.FromArgb(0x30, 0x1F, 0xB6, 0xC3));
                btn.Foreground = (Brush)FindResource("Accent");
            }
            else
            {
                btn.Content = ((int)Math.Round(amt * 100)).ToString();
                btn.Background = new SolidColorBrush(Color.FromArgb(0x30, 0xD9, 0x9B, 0x5C));
                btn.Foreground = new SolidColorBrush(Color.FromRgb(0xD9, 0x9B, 0x5C));
            }
        }

        // =========================================================================================
        // Filter panels (2 panels avec cutoff/res/drive/mix + type + slope + curve visual)
        // =========================================================================================

        void BuildFilterPanels()
        {
            BuildOneFilterPanel((StackPanel)FindName("Filter1Panel"), "F1",
                "f1_type", "f1_slope", "f1_cutoff", "f1_res", "f1_drive", "f1_mix");
            BuildOneFilterPanel((StackPanel)FindName("Filter2Panel"), "F2",
                "f2_type", "f2_slope", "f2_cutoff", "f2_res", "f2_drive", "f2_mix");
        }

        static readonly string[] FilterTypeNames = { "LP", "HP", "BP", "Notch" };

        void BuildOneFilterPanel(StackPanel host, string headLabel,
            string idType, string idSlope, string idCutoff, string idRes, string idDrive, string idMix)
        {
            host.Children.Clear();

            var pType = _plugin.Parameters[FindParamIndex(idType)];
            var pSlope = _plugin.Parameters[FindParamIndex(idSlope)];
            var pCutoff = _plugin.Parameters[FindParamIndex(idCutoff)];
            var pRes = _plugin.Parameters[FindParamIndex(idRes)];
            var pDrive = _plugin.Parameters[FindParamIndex(idDrive)];
            var pMix = _plugin.Parameters[FindParamIndex(idMix)];

            // Header : nom + type + slope
            var headGrid = new Grid();
            headGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            headGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var head = new TextBlock
            {
                Text = headLabel,
                FontSize = 10,
                Foreground = (Brush)FindResource("TextDim"),
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(head, 0);
            headGrid.Children.Add(head);

            var typeStack = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            Grid.SetColumn(typeStack, 1);
            var typeCombo = new ComboBox
            {
                // Style implicite du theme partage — voir MakeKvComboCell pour la meme note.
                MinWidth = 60,
                FontSize = 10,
                Padding = new Thickness(4, 1, 4, 1),
                Margin = new Thickness(0, 0, 4, 0),
            };
            foreach (var n in FilterTypeNames) typeCombo.Items.Add(n);
            typeCombo.SelectedIndex = (int)Math.Round(pType.Value);
            bool tSyncing = false;
            typeCombo.SelectionChanged += (s, e) => { if (!tSyncing) { tSyncing = true; pType.Value = typeCombo.SelectedIndex; tSyncing = false; } };
            pType.Changed += v => Dispatcher.BeginInvoke((Action)(() =>
            {
                if (tSyncing) return;
                tSyncing = true; typeCombo.SelectedIndex = (int)Math.Round(v); tSyncing = false;
            }));
            typeStack.Children.Add(typeCombo);

            var slopeBtn = new Button
            {
                Style = (Style)FindResource("SegToggle"),
                Content = pSlope.Value >= 0.5 ? "24" : "12",
                MinWidth = 30,
                FontSize = 10,
            };
            slopeBtn.Click += (s, e) => { pSlope.Value = pSlope.Value >= 0.5 ? 0 : 1; };
            pSlope.Changed += v => Dispatcher.BeginInvoke((Action)(() =>
            {
                slopeBtn.Content = v >= 0.5 ? "24" : "12";
            }));
            typeStack.Children.Add(slopeBtn);
            headGrid.Children.Add(typeStack);
            host.Children.Add(headGrid);

            // Curve visual (simple representation stylisee — pas un rendu math exact du filtre)
            var curveHost = new FilterCurveDisplay { Height = 50, Margin = new Thickness(0, 6, 0, 6) };
            void refreshCurve() => Dispatcher.BeginInvoke((Action)(() =>
                curveHost.SetFilter(FilterTypeFromDouble(pType.Value), pCutoff.Value, pRes.Value)));
            pType.Changed += _ => refreshCurve();
            pCutoff.Changed += _ => refreshCurve();
            pRes.Changed += _ => refreshCurve();
            curveHost.Loaded += (s, e) => curveHost.SetFilter(FilterTypeFromDouble(pType.Value), pCutoff.Value, pRes.Value);
            host.Children.Add(curveHost);

            // Grille 2x2 : Freq / Res / Drive / Mix
            var knGrid = new UniformGrid { Columns = 2, Margin = new Thickness(0) };
            knGrid.Children.Add(MakeKvSliderCell("Freq", pCutoff));
            knGrid.Children.Add(MakeKvSliderCell("Res", pRes));
            knGrid.Children.Add(MakeKvSliderCell("Drive", pDrive));
            knGrid.Children.Add(MakeKvSliderCell("Mix", pMix));
            host.Children.Add(knGrid);
        }

        static FilterType FilterTypeFromDouble(double v)
        {
            int i = (int)Math.Round(v);
            if (i < 0) i = 0; else if (i > 3) i = 3;
            return (FilterType)i;
        }

        // =========================================================================================
        // Output panel (Volume/Pan/Glide sliders + Voices toggles)
        // =========================================================================================

        void BuildOutputPanel()
        {
            var host = (StackPanel)FindName("OutputPanel");
            host.Children.Clear();
            host.Children.Add(MakeHSliderRow("Volume", _plugin.Parameters[FindParamIndex("out_vol")]));
            host.Children.Add(MakeHSliderRow("Pan", _plugin.Parameters[FindParamIndex("out_pan")]));
            host.Children.Add(MakeHSliderRow("Glide", _plugin.Parameters[FindParamIndex("glide")]));

            var voicesLbl = new TextBlock
            {
                Text = "Voices",
                Style = (Style)FindResource("SectionSub"),
                Margin = new Thickness(0, 8, 0, 4),
            };
            host.Children.Add(voicesLbl);

            var vmParam = _plugin.Parameters[FindParamIndex("voice_mode")];
            var vmRow = new StackPanel { Orientation = Orientation.Horizontal };
            var monoBtn = new Button { Content = "Mono", Style = (Style)FindResource("SegToggle") };
            var poly8Btn = new Button { Content = "Poly 8", Style = (Style)FindResource("SegToggle") };
            var poly16Btn = new Button { Content = "Poly 16", Style = (Style)FindResource("SegToggle") };
            monoBtn.Click += (s, e) => vmParam.Value = 0;
            poly8Btn.Click += (s, e) => vmParam.Value = 1;
            poly16Btn.Click += (s, e) => vmParam.Value = 2;
            void refreshVm()
            {
                int mode = (int)Math.Round(vmParam.Value);
                HighlightSegBtn(monoBtn, mode == 0);
                HighlightSegBtn(poly8Btn, mode == 1);
                HighlightSegBtn(poly16Btn, mode == 2);
            }
            vmParam.Changed += _ => Dispatcher.BeginInvoke((Action)refreshVm);
            refreshVm();
            vmRow.Children.Add(monoBtn);
            vmRow.Children.Add(poly8Btn);
            vmRow.Children.Add(poly16Btn);
            host.Children.Add(vmRow);

            // Unison — expose meme si non implemente dans le rendu v1 (le user peut choisir un mode
            // qui sera respecte quand la v2 branchera le vrai unison).
            var unisonLbl = new TextBlock
            {
                Text = "Unison (visuel v1)",
                Style = (Style)FindResource("SectionSub"),
                Margin = new Thickness(0, 8, 0, 4),
            };
            host.Children.Add(unisonLbl);

            var uParam = _plugin.Parameters[FindParamIndex("unison_mode")];
            var uRow = new StackPanel { Orientation = Orientation.Horizontal };
            string[] uNames = { "Off", "Classic 2", "Wide 3", "Shimmer 5" };
            var uBtns = new Button[uNames.Length];
            for (int i = 0; i < uNames.Length; i++)
            {
                int idx = i;
                uBtns[i] = new Button { Content = uNames[i], Style = (Style)FindResource("SegToggle"), FontSize = 9, Padding = new Thickness(4, 2, 4, 2) };
                uBtns[i].Click += (s, e) => uParam.Value = idx;
                uRow.Children.Add(uBtns[i]);
            }
            void refreshUnison()
            {
                int mode = (int)Math.Round(uParam.Value);
                for (int i = 0; i < uBtns.Length; i++) HighlightSegBtn(uBtns[i], i == mode);
            }
            uParam.Changed += _ => Dispatcher.BeginInvoke((Action)refreshUnison);
            refreshUnison();
            host.Children.Add(uRow);
        }

        FrameworkElement MakeHSliderRow(string label, KotonParameter p)
        {
            var grid = new Grid { Margin = new Thickness(0, 3, 0, 3) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });

            var lbl = new TextBlock
            {
                Text = label,
                FontSize = 11,
                Foreground = (Brush)FindResource("TextDim"),
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(lbl, 0);
            grid.Children.Add(lbl);

            var slider = new Slider
            {
                Style = (Style)FindResource("KotonHSlider"),
                Minimum = p.Min, Maximum = p.Max, Value = p.Value,
                Margin = new Thickness(4, 0, 4, 0),
            };
            Grid.SetColumn(slider, 1);
            grid.Children.Add(slider);

            var valTb = new TextBlock
            {
                Text = FormatValue(p),
                FontFamily = new FontFamily("Consolas"),
                FontSize = 11,
                Foreground = (Brush)FindResource("Accent"),
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Right,
            };
            Grid.SetColumn(valTb, 2);
            grid.Children.Add(valTb);

            bool syncing = false;
            slider.ValueChanged += (s, e) =>
            {
                if (syncing) return;
                syncing = true;
                p.Value = slider.Value;
                valTb.Text = FormatValue(p);
                syncing = false;
            };
            p.Changed += v => Dispatcher.BeginInvoke((Action)(() =>
            {
                if (syncing) return;
                syncing = true; slider.Value = v; valTb.Text = FormatValue(p); syncing = false;
            }));
            return grid;
        }

        void HighlightSegBtn(Button btn, bool on)
        {
            if (on)
            {
                btn.Background = new SolidColorBrush(Color.FromArgb(0x30, 0x1F, 0xB6, 0xC3));
                btn.Foreground = (Brush)FindResource("Accent");
                btn.BorderBrush = (Brush)FindResource("AccentDim");
            }
            else
            {
                btn.Background = (Brush)FindResource("PanelHi");
                btn.Foreground = (Brush)FindResource("TextDim");
                btn.BorderBrush = (Brush)FindResource("Border");
            }
        }

        void RefreshRoutingButtons()
        {
            var pR = _plugin.Parameters[FindParamIndex("f_routing")];
            bool parallel = pR.Value >= 0.5;
            HighlightSegBtn((Button)FindName("RoutingSerialBtn"), !parallel);
            HighlightSegBtn((Button)FindName("RoutingParallelBtn"), parallel);
            // Hook les clicks (une fois — Loaded ne rappelle pas cette methode plus tard).
            var sBtn = (Button)FindName("RoutingSerialBtn");
            var pBtn = (Button)FindName("RoutingParallelBtn");
            if (sBtn.Tag == null)
            {
                sBtn.Click += (s, e) => pR.Value = 0;
                sBtn.Tag = "hooked";
            }
            if (pBtn.Tag == null)
            {
                pBtn.Click += (s, e) => pR.Value = 1;
                pBtn.Tag = "hooked";
            }
        }

        // =========================================================================================
        // XFade slider + wave info sync (le badge du result affiche la valeur du crossfade)
        // =========================================================================================

        void HookXFadeSlider()
        {
            var pXf = _plugin.Parameters[FindParamIndex("xfade")];
            var sl = (Slider)FindName("XFadeSlider");
            var val = (TextBlock)FindName("XFadeValue");
            sl.Value = pXf.Value;
            bool syncing = false;
            sl.ValueChanged += (s, e) =>
            {
                if (syncing) return;
                syncing = true;
                pXf.Value = sl.Value;
                val.Text = pXf.Value.ToString("0.00", CultureInfo.InvariantCulture);
                UpdateResultBadge(pXf.Value);
                syncing = false;
            };
            pXf.Changed += v => Dispatcher.BeginInvoke((Action)(() =>
            {
                if (syncing) return;
                syncing = true;
                sl.Value = v;
                val.Text = v.ToString("0.00", CultureInfo.InvariantCulture);
                UpdateResultBadge(v);
                syncing = false;
            }));
            UpdateResultBadge(pXf.Value);
        }

        void UpdateResultBadge(double xf)
        {
            var rb = (TextBlock)FindName("ResultBadge");
            if (rb != null)
                rb.Text = "Resultat . lerp(W1, W2, " + xf.ToString("0.00", CultureInfo.InvariantCulture) + ")";
        }

        void HookWaveInfoSync()
        {
            // Badge Wave 1 (nom de la forme) + les 3 textes en bas a droite (Amp/Det/Mult)
            var w1WaveP = _plugin.Parameters[FindParamIndex("w1_wave")];
            var w1AmpP = _plugin.Parameters[FindParamIndex("w1_amp")];
            var w1DetP = _plugin.Parameters[FindParamIndex("w1_detune")];
            var w1MultP = _plugin.Parameters[FindParamIndex("w1_mult")];
            var w2WaveP = _plugin.Parameters[FindParamIndex("w2_wave")];
            var w2AmpP = _plugin.Parameters[FindParamIndex("w2_amp")];
            var w2DetP = _plugin.Parameters[FindParamIndex("w2_detune")];
            var w2MultP = _plugin.Parameters[FindParamIndex("w2_mult")];

            void refresh1() => Dispatcher.BeginInvoke((Action)(() =>
            {
                ((TextBlock)FindName("Wave1Badge")).Text = "Wave 1 . " + WaveOsc.Names[(int)WaveOsc.FromDouble(w1WaveP.Value)];
                ((TextBlock)FindName("Wave1AmpText")).Text = w1AmpP.Value.ToString("0.#", CultureInfo.InvariantCulture) + " dB";
                ((TextBlock)FindName("Wave1DetText")).Text = ((int)Math.Round(w1DetP.Value)).ToString() + " ct";
                ((TextBlock)FindName("Wave1MultText")).Text = FreqMult.Labels[(int)Math.Round(w1MultP.Value)];
            }));
            void refresh2() => Dispatcher.BeginInvoke((Action)(() =>
            {
                ((TextBlock)FindName("Wave2Badge")).Text = "Wave 2 . " + WaveOsc.Names[(int)WaveOsc.FromDouble(w2WaveP.Value)];
                ((TextBlock)FindName("Wave2AmpText")).Text = w2AmpP.Value.ToString("0.#", CultureInfo.InvariantCulture) + " dB";
                ((TextBlock)FindName("Wave2DetText")).Text = ((int)Math.Round(w2DetP.Value)).ToString() + " ct";
                ((TextBlock)FindName("Wave2MultText")).Text = FreqMult.Labels[(int)Math.Round(w2MultP.Value)];
            }));
            w1WaveP.Changed += _ => refresh1();
            w1AmpP.Changed += _ => refresh1();
            w1DetP.Changed += _ => refresh1();
            w1MultP.Changed += _ => refresh1();
            w2WaveP.Changed += _ => refresh2();
            w2AmpP.Changed += _ => refresh2();
            w2DetP.Changed += _ => refresh2();
            w2MultP.Changed += _ => refresh2();
            refresh1();
            refresh2();

            // Rend les textes du badge INTERACTIFS : drag vertical pour Amp/Det (continus),
            // click-cycle pour Mult (discret enum). Sans ca l'utilisateur voit les valeurs mais
            // n'a aucun moyen visible de les modifier depuis l'editeur.
            WireBadgeDrag((TextBlock)FindName("Wave1AmpText"), w1AmpP);
            WireBadgeDrag((TextBlock)FindName("Wave1DetText"), w1DetP);
            WireBadgeCycle((TextBlock)FindName("Wave1MultText"), w1MultP);
            WireBadgeDrag((TextBlock)FindName("Wave2AmpText"), w2AmpP);
            WireBadgeDrag((TextBlock)FindName("Wave2DetText"), w2DetP);
            WireBadgeCycle((TextBlock)FindName("Wave2MultText"), w2MultP);
        }

        /// <summary>Rend un <see cref="TextBlock"/> interactif : drag vertical de +/- 200 px pour
        /// couvrir la plage complete du <paramref name="p"/>. Curseur SizeNS pour signaler
        /// l'interactivite, souligne au hover pour feedback visuel supplementaire.</summary>
        void WireBadgeDrag(TextBlock tb, KotonStudio.Library.KotonParameter p)
        {
            if (tb == null) return;
            tb.Cursor = Cursors.SizeNS;
            tb.MouseEnter += (s, e) => tb.TextDecorations = System.Windows.TextDecorations.Underline;
            tb.MouseLeave += (s, e) => tb.TextDecorations = null;
            bool dragging = false;
            Point origin = new Point();
            double startVal = 0;
            tb.MouseLeftButtonDown += (s, e) =>
            {
                dragging = true;
                origin = e.GetPosition(tb);
                startVal = p.Value;
                tb.CaptureMouse();
                e.Handled = true;
            };
            tb.MouseMove += (s, e) =>
            {
                if (!dragging) return;
                var cur = e.GetPosition(tb);
                double dy = origin.Y - cur.Y;
                double range = p.Max - p.Min;
                p.Value = startVal + (dy / 200.0) * range;
            };
            tb.MouseLeftButtonUp += (s, e) =>
            {
                dragging = false;
                tb.ReleaseMouseCapture();
            };
        }

        /// <summary>Pour les params discrets (enum) : click = cycle vers la valeur suivante.
        /// Molette souris = cycle aussi (rapide pour parcourir la liste).</summary>
        void WireBadgeCycle(TextBlock tb, KotonStudio.Library.KotonParameter p)
        {
            if (tb == null) return;
            tb.Cursor = Cursors.Hand;
            tb.MouseEnter += (s, e) => tb.TextDecorations = System.Windows.TextDecorations.Underline;
            tb.MouseLeave += (s, e) => tb.TextDecorations = null;
            tb.MouseLeftButtonDown += (s, e) =>
            {
                int cur = (int)Math.Round(p.Value);
                int next = cur + 1;
                if (next > (int)p.Max) next = (int)p.Min;
                p.Value = next;
                e.Handled = true;
            };
            tb.MouseWheel += (s, e) =>
            {
                int cur = (int)Math.Round(p.Value);
                int next = cur + (e.Delta > 0 ? 1 : -1);
                if (next > (int)p.Max) next = (int)p.Min;
                else if (next < (int)p.Min) next = (int)p.Max;
                p.Value = next;
                e.Handled = true;
            };
        }

        // =========================================================================================
        // Timer refresh (30 Hz) — scope live + wave displays
        // =========================================================================================

        void RefreshFrame()
        {
            // Wave displays : reglobalise le hash des params osc pour n'appeler les redraws que si un
            // changement a eu lieu. Le hash simple (somme int) suffit — collision improbable en
            // pratique et sans consequence (juste 1 redraw supplementaire).
            int hash = ComputeWaveHash();
            if (hash != _lastWaveHash)
            {
                _lastWaveHash = hash;
                _plugin.GetOscWave(0, _wave1Buf);
                _wave1Display.SetSamples(_wave1Buf);
                _plugin.GetOscWave(1, _wave2Buf);
                _wave2Display.SetSamples(_wave2Buf);
                _plugin.GetMorphWave(_resultBuf);
                _resultDisplay.SetSamples(_resultBuf);
            }

            // Scope live : toujours refresh (source dynamique).
            _plugin.GetScopeSamples(_scopeBuf);
            _scopeDisplay.SetSamples(_scopeBuf);

            // Meters RMS/Peak calcules a partir du buffer scope
            float rms = 0, peak = 0;
            for (int i = 0; i < _scopeBuf.Length; i++)
            {
                float s = _scopeBuf[i];
                float ab = s < 0 ? -s : s;
                rms += s * s;
                if (ab > peak) peak = ab;
            }
            rms = (float)Math.Sqrt(rms / _scopeBuf.Length);
            ((TextBlock)FindName("ScopeRms")).Text = FormatDb(rms);
            ((TextBlock)FindName("ScopePeak")).Text = FormatDb(peak);
        }

        int ComputeWaveHash()
        {
            // Hash de tous les params qui influencent l'aspect visuel des vues wave.
            int h = 17;
            unchecked
            {
                h = h * 31 + _plugin.Parameters[FindParamIndex("w1_wave")].Value.GetHashCode();
                h = h * 31 + _plugin.Parameters[FindParamIndex("w2_wave")].Value.GetHashCode();
                h = h * 31 + _plugin.Parameters[FindParamIndex("w1_amp")].Value.GetHashCode();
                h = h * 31 + _plugin.Parameters[FindParamIndex("w2_amp")].Value.GetHashCode();
                h = h * 31 + _plugin.Parameters[FindParamIndex("w1_mult")].Value.GetHashCode();
                h = h * 31 + _plugin.Parameters[FindParamIndex("w2_mult")].Value.GetHashCode();
                h = h * 31 + _plugin.Parameters[FindParamIndex("xfade")].Value.GetHashCode();
            }
            return h;
        }

        static string FormatDb(float lin)
        {
            if (lin < 1e-4f) return "-inf";
            double db = 20.0 * Math.Log10(lin);
            return db.ToString("0.0", CultureInfo.InvariantCulture) + " dB";
        }

        // =========================================================================================
        // Helpers
        // =========================================================================================

        int FindParamIndex(string id)
        {
            for (int i = 0; i < _plugin.Parameters.Count; i++)
                if (_plugin.Parameters[i].Id == id) return i;
            throw new InvalidOperationException("Param not found: " + id);
        }

        static string FormatValue(KotonParameter p)
        {
            double v = p.Value;
            string num;
            if (p.Id != null && (p.Id.EndsWith("_wave") || p.Id.EndsWith("_shape") || p.Id.EndsWith("_type") || p.Id.EndsWith("_slope")
                                 || p.Id == "voice_mode" || p.Id == "unison_mode" || p.Id == "f_routing"))
            {
                num = ((int)Math.Round(v)).ToString();
            }
            else if (p.Id != null && p.Id.EndsWith("_mult"))
            {
                num = FreqMult.Labels[(int)Math.Round(v)];
            }
            else if (Math.Abs(v) >= 1000) num = v.ToString("0", CultureInfo.InvariantCulture);
            else if (Math.Abs(v) < 1.0) num = v.ToString("0.00", CultureInfo.InvariantCulture);
            else if (Math.Abs(v) < 10.0) num = v.ToString("0.0#", CultureInfo.InvariantCulture);
            else num = v.ToString("0.#", CultureInfo.InvariantCulture);
            return string.IsNullOrEmpty(p.Unit) ? num : (num + " " + p.Unit);
        }
    }

    // =============================================================================================
    // Sous-controles graphiques (envelope shape, LFO shape, filter curve) — internes a l'editeur.
    // =============================================================================================

    /// <summary>
    /// Affichage graphique d'une enveloppe ADSR : 4 poignees (attack peak, decay end, sustain start,
    /// release end) + une polyligne teal. Les Y sont normalises (0 = bas, 1 = haut). Les X sont
    /// distribues proportionnellement aux 3 temps A/D/R (le sustain occupe une portion fixe visuellement).
    /// </summary>
    internal sealed class EnvelopeDisplay : UserControl
    {
        readonly Canvas _canvas;
        readonly Polyline _line;
        readonly Polygon _fill;
        readonly Ellipse _dotA, _dotD, _dotS, _dotR;
        double _a, _d, _s, _r;

        public EnvelopeDisplay()
        {
            _canvas = new Canvas { Background = Brushes.Transparent, ClipToBounds = true };
            var accent = Color.FromRgb(0x1F, 0xB6, 0xC3);
            var stroke = new SolidColorBrush(accent); stroke.Freeze();
            var fillBrush = new SolidColorBrush(Color.FromArgb(0x40, accent.R, accent.G, accent.B)); fillBrush.Freeze();
            _fill = new Polygon { Fill = fillBrush, IsHitTestVisible = false };
            _line = new Polyline { Stroke = stroke, StrokeThickness = 1.5, StrokeLineJoin = PenLineJoin.Round, IsHitTestVisible = false };
            _dotA = MakeDot(accent);
            _dotD = MakeDot(accent);
            _dotS = MakeDot(accent);
            _dotR = MakeDot(accent);
            _canvas.Children.Add(_fill);
            _canvas.Children.Add(_line);
            _canvas.Children.Add(_dotA);
            _canvas.Children.Add(_dotD);
            _canvas.Children.Add(_dotS);
            _canvas.Children.Add(_dotR);
            Content = _canvas;
            SizeChanged += (s, e) => Redraw();
        }

        static Ellipse MakeDot(Color c)
        {
            var b = new SolidColorBrush(c); b.Freeze();
            return new Ellipse { Width = 5, Height = 5, Fill = b };
        }

        public void SetAdsr(double a, double d, double s, double r)
        {
            _a = a; _d = d; _s = s; _r = r;
            Redraw();
        }

        void Redraw()
        {
            double w = _canvas.ActualWidth;
            double h = _canvas.ActualHeight;
            if (w <= 0 || h <= 0) return;

            // Normaliser les temps A/D/R sur la largeur (le sustain occupe le reste).
            // On limite la somme A+D+R visuellement a 60% de la largeur, sustain flat sur les 40% restants.
            double sumT = Math.Max(1, _a + _d + _r);
            double aW = _a / sumT * (w * 0.6);
            double dW = _d / sumT * (w * 0.6);
            double rW = _r / sumT * (w * 0.6);
            double sW = w - (aW + dW + rW);
            if (sW < 20) { sW = 20; }

            double y0 = h * 0.9;   // baseline (level 0)
            double yPeak = h * 0.1; // level 1
            double ySustain = y0 - (y0 - yPeak) * _s;

            double x0 = 4;
            double xAttackEnd = x0 + aW;
            double xDecayEnd = xAttackEnd + dW;
            double xSustainEnd = xDecayEnd + sW;
            double xReleaseEnd = xSustainEnd + rW;

            var pts = new PointCollection
            {
                new Point(x0, y0),
                new Point(xAttackEnd, yPeak),
                new Point(xDecayEnd, ySustain),
                new Point(xSustainEnd, ySustain),
                new Point(xReleaseEnd, y0),
            };
            _line.Points = pts;

            var fillPts = new PointCollection(pts);
            fillPts.Add(new Point(xReleaseEnd, h));
            fillPts.Add(new Point(x0, h));
            _fill.Points = fillPts;

            PositionDot(_dotA, xAttackEnd, yPeak);
            PositionDot(_dotD, xDecayEnd, ySustain);
            PositionDot(_dotS, xSustainEnd, ySustain);
            PositionDot(_dotR, xReleaseEnd, y0);
        }

        static void PositionDot(Ellipse e, double x, double y)
        {
            Canvas.SetLeft(e, x - e.Width * 0.5);
            Canvas.SetTop(e, y - e.Height * 0.5);
        }
    }

    /// <summary>
    /// Affichage graphique d'un LFO : 3 cycles de la forme choisie, teal. L'amount ajuste
    /// l'amplitude verticale du trace.
    /// </summary>
    internal sealed class LfoDisplay : UserControl
    {
        readonly Canvas _canvas;
        readonly Polyline _line;
        LfoShape _shape = LfoShape.Sine;
        double _amount = 1.0;

        public LfoDisplay()
        {
            _canvas = new Canvas { Background = Brushes.Transparent, ClipToBounds = true };
            var accent = Color.FromRgb(0x1F, 0xB6, 0xC3);
            var stroke = new SolidColorBrush(accent); stroke.Freeze();
            _line = new Polyline { Stroke = stroke, StrokeThickness = 1.5, IsHitTestVisible = false };
            _canvas.Children.Add(_line);
            Content = _canvas;
            SizeChanged += (s, e) => Redraw();
        }

        public void SetShape(LfoShape shape, double amount)
        {
            _shape = shape; _amount = amount;
            Redraw();
        }

        void Redraw()
        {
            double w = _canvas.ActualWidth;
            double h = _canvas.ActualHeight;
            if (w <= 0 || h <= 0) return;
            const int cycles = 3;
            const int cols = 200;
            var pts = new PointCollection(cols);
            for (int i = 0; i < cols; i++)
            {
                double t = (double)i / (cols - 1) * cycles;
                double localPhase = t - Math.Floor(t);
                double v;
                switch (_shape)
                {
                    case LfoShape.Sine: v = Math.Sin(localPhase * 2 * Math.PI); break;
                    case LfoShape.Triangle: v = localPhase < 0.5 ? localPhase * 4 - 1 : 3 - localPhase * 4; break;
                    case LfoShape.Saw: v = localPhase * 2 - 1; break;
                    case LfoShape.Square: v = localPhase < 0.5 ? 1 : -1; break;
                    case LfoShape.SampleAndHold:
                        // Un tirage par cycle — utilise l'index entier pour rester deterministe.
                        int c = (int)Math.Floor(t);
                        var rr = new Random(c * 31 + 7);
                        v = rr.NextDouble() * 2 - 1;
                        break;
                    case LfoShape.Random:
                        int c2 = (int)Math.Floor(t);
                        var ra = new Random(c2 * 31 + 7);
                        var rb = new Random((c2 + 1) * 31 + 7);
                        double va = ra.NextDouble() * 2 - 1;
                        double vb = rb.NextDouble() * 2 - 1;
                        v = va + (vb - va) * localPhase;
                        break;
                    default: v = 0; break;
                }
                v *= _amount;
                double x = (double)i / (cols - 1) * w;
                double y = (0.5 - 0.45 * v) * h;
                pts.Add(new Point(x, y));
            }
            _line.Points = pts;
        }
    }

    /// <summary>
    /// Affichage stylise d'une courbe de filtre (LP/HP/BP/Notch). Ne calcule pas la reponse
    /// frequentielle exacte du biquad — trace une silhouette caracteristique (cassure a la freq de
    /// cutoff, resonance en pic autour). Objectif : que le user identifie visuellement le type et le
    /// cutoff, pas une analyse spectrale.
    /// </summary>
    internal sealed class FilterCurveDisplay : UserControl
    {
        readonly Canvas _canvas;
        readonly Polyline _line;
        readonly Polygon _fill;
        readonly Ellipse _knob;
        FilterType _type;
        double _cutoff = 1000;
        double _res = 0.2;

        public FilterCurveDisplay()
        {
            _canvas = new Canvas { Background = Brushes.Transparent, ClipToBounds = true };
            var accent = Color.FromRgb(0x1F, 0xB6, 0xC3);
            var stroke = new SolidColorBrush(accent); stroke.Freeze();
            var fillBrush = new SolidColorBrush(Color.FromArgb(0x40, accent.R, accent.G, accent.B)); fillBrush.Freeze();
            _fill = new Polygon { Fill = fillBrush, IsHitTestVisible = false };
            _line = new Polyline { Stroke = stroke, StrokeThickness = 1.5, IsHitTestVisible = false };
            _knob = new Ellipse { Width = 6, Height = 6, Fill = stroke };
            _canvas.Children.Add(_fill);
            _canvas.Children.Add(_line);
            _canvas.Children.Add(_knob);
            Content = _canvas;
            SizeChanged += (s, e) => Redraw();
        }

        public void SetFilter(FilterType type, double cutoff, double res)
        {
            _type = type; _cutoff = cutoff; _res = res;
            Redraw();
        }

        void Redraw()
        {
            double w = _canvas.ActualWidth;
            double h = _canvas.ActualHeight;
            if (w <= 0 || h <= 0) return;

            // Position X du cutoff : mapping log sur 20 Hz .. 20 kHz
            double f = Math.Max(20, Math.Min(20000, _cutoff));
            double xCut = Math.Log10(f / 20) / Math.Log10(1000) * w;
            if (xCut < 4) xCut = 4;
            if (xCut > w - 4) xCut = w - 4;

            double yPass = h * 0.2;   // niveau "pass" (haut)
            double yStop = h * 0.9;   // niveau "cut" (bas)

            var pts = new PointCollection();
            switch (_type)
            {
                case FilterType.LowPass:
                    pts.Add(new Point(0, yPass));
                    pts.Add(new Point(xCut - 6, yPass));
                    // Petit pic de resonance juste avant la cassure
                    double resPeakY = yPass - _res * (yPass - 4);
                    pts.Add(new Point(xCut, resPeakY));
                    pts.Add(new Point(xCut + 20, yStop));
                    pts.Add(new Point(w, yStop));
                    break;
                case FilterType.HighPass:
                    pts.Add(new Point(0, yStop));
                    pts.Add(new Point(xCut - 20, yStop));
                    double resPeakYH = yPass - _res * (yPass - 4);
                    pts.Add(new Point(xCut, resPeakYH));
                    pts.Add(new Point(xCut + 6, yPass));
                    pts.Add(new Point(w, yPass));
                    break;
                case FilterType.BandPass:
                    pts.Add(new Point(0, yStop));
                    pts.Add(new Point(xCut - 30, yStop));
                    pts.Add(new Point(xCut, yPass - _res * (yPass - 4)));
                    pts.Add(new Point(xCut + 30, yStop));
                    pts.Add(new Point(w, yStop));
                    break;
                case FilterType.Notch:
                    pts.Add(new Point(0, yPass));
                    pts.Add(new Point(xCut - 15, yPass));
                    pts.Add(new Point(xCut, yStop));
                    pts.Add(new Point(xCut + 15, yPass));
                    pts.Add(new Point(w, yPass));
                    break;
            }
            _line.Points = pts;
            var fillPts = new PointCollection(pts);
            fillPts.Add(new Point(w, h));
            fillPts.Add(new Point(0, h));
            _fill.Points = fillPts;

            Canvas.SetLeft(_knob, xCut - 3);
            Canvas.SetTop(_knob, yPass - 3);
        }
    }
}
