using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using KotonStudio.Library;

namespace KotonPluginFmSynth
{
    /// <summary>
    /// Éditeur WPF du FM synth. Construit dynamiquement une grille de sliders (une ligne par
    /// <see cref="KotonParameter"/> exposé par le plugin), un combo de presets, et une zone
    /// oscilloscope qui affiche les derniers samples émis par le plugin.
    ///
    /// **Binding** : chaque slider est en two-way sur la Value du paramètre. Le paramètre expose un
    /// événement Changed → si l'automation externe (ou un chargement de preset) modifie la valeur,
    /// le slider se met à jour. Pas de crash-loop : KotonParameter.Value = X n'émet Changed que si
    /// X != valeur actuelle.
    ///
    /// **Oscilloscope** : DispatcherTimer 30 Hz lit le ring buffer du plugin (sans lock) et redessine
    /// un Polyline. Race bénigne — la trace peut être un peu déchirée entre deux frames, invisible à
    /// l'œil. Le timer est arrêté à Unloaded pour ne pas fuir quand la fenêtre est fermée.
    /// </summary>
    public partial class FmSynthEditor : UserControl
    {
        readonly FmSynthPlugin _plugin;
        DispatcherTimer _scopeTimer;
        Polyline _scopeLine;
        readonly float[] _scopeBuffer = new float[FmSynthPlugin.ScopeSize];
        bool _presetSyncingFromCode;   // évite qu'un LoadPreset déclenche SelectionChanged qui re-appelle LoadPreset

        public FmSynthEditor(FmSynthPlugin plugin)
        {
            InitializeComponent();
            _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));

            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        void OnLoaded(object sender, RoutedEventArgs e)
        {
            // Peupler le combo presets.
            _presetSyncingFromCode = true;
            PresetCombo.Items.Clear();
            foreach (var name in _plugin.PresetNames) PresetCombo.Items.Add(name);
            if (_plugin.CurrentPreset >= 0 && _plugin.CurrentPreset < PresetCombo.Items.Count)
                PresetCombo.SelectedIndex = _plugin.CurrentPreset;
            _presetSyncingFromCode = false;

            BuildParamsGrid();
            InitScope();
        }

        void OnUnloaded(object sender, RoutedEventArgs e)
        {
            if (_scopeTimer != null) { _scopeTimer.Stop(); _scopeTimer = null; }
        }

        void BuildParamsGrid()
        {
            ParamsGrid.RowDefinitions.Clear();
            ParamsGrid.Children.Clear();

            int row = 0;
            foreach (var p in _plugin.Parameters)
            {
                ParamsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                // Colonne 0 : label.
                var lbl = new TextBlock
                {
                    Text = p.Name,
                    Margin = new Thickness(0, 6, 10, 6),
                    VerticalAlignment = VerticalAlignment.Center,
                    FontSize = 12,
                    MinWidth = 90,
                };
                Grid.SetRow(lbl, row);
                Grid.SetColumn(lbl, 0);
                ParamsGrid.Children.Add(lbl);

                // Colonne 1 : slider.
                var sl = new Slider
                {
                    Minimum = p.Min,
                    Maximum = p.Max,
                    Value = p.Value,
                    Style = (Style)FindResource("KotonSlider"),
                    Margin = new Thickness(0, 6, 10, 6),
                    VerticalAlignment = VerticalAlignment.Center,
                    // Petit pas pour les paramètres en temps (ms) sinon le slider "saute".
                    SmallChange = Math.Max((p.Max - p.Min) / 200.0, 0.001),
                    LargeChange = Math.Max((p.Max - p.Min) / 20.0, 0.01),
                };
                Grid.SetRow(sl, row);
                Grid.SetColumn(sl, 1);
                ParamsGrid.Children.Add(sl);

                // Colonne 2 : valeur affichée (mise à jour à la volée).
                var val = new TextBlock
                {
                    Text = FormatValue(p),
                    Margin = new Thickness(0, 6, 0, 6),
                    VerticalAlignment = VerticalAlignment.Center,
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 11,
                    MinWidth = 65,
                    TextAlignment = TextAlignment.Right,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0xA0, 0xA6)),
                };
                Grid.SetRow(val, row);
                Grid.SetColumn(val, 2);
                ParamsGrid.Children.Add(val);

                // Bidirectionnel : slider → paramètre + paramètre → slider (pour absorber les
                // LoadPreset). Un flag interne évite la boucle infinie sur les cas où l'écriture
                // change la valeur (KotonParameter clamp, un Value = même valeur n'émet pas Changed
                // donc pas de vrai risque, mais on garde la garde par sécurité).
                bool syncing = false;
                sl.ValueChanged += (s, ev) =>
                {
                    if (syncing) return;
                    syncing = true;
                    p.Value = sl.Value;
                    val.Text = FormatValue(p);
                    syncing = false;
                };
                p.Changed += v =>
                {
                    // L'événement peut être levé depuis n'importe quel thread (l'audio thread peut
                    // poser une valeur via automation). On marshalle sur le thread UI.
                    Dispatcher.BeginInvoke((Action)(() =>
                    {
                        if (syncing) return;
                        syncing = true;
                        sl.Value = v;
                        val.Text = FormatValue(p);
                        syncing = false;
                    }));
                };

                row++;
            }
        }

        static string FormatValue(KotonParameter p)
        {
            // Précision selon la plage : les valeurs sub-1 (sustain, LFO depth) → 2 décimales ; les
            // temps → 3 (ms), les gros ratios / index → 1.
            string num;
            double v = p.Value;
            if (Math.Abs(v) < 1.0) num = v.ToString("0.00", CultureInfo.InvariantCulture);
            else if (Math.Abs(v) < 10.0) num = v.ToString("0.0#", CultureInfo.InvariantCulture);
            else num = v.ToString("0.#", CultureInfo.InvariantCulture);
            return string.IsNullOrEmpty(p.Unit) ? num : (num + " " + p.Unit);
        }

        void PresetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_presetSyncingFromCode) return;
            int idx = PresetCombo.SelectedIndex;
            if (idx < 0) return;
            _plugin.LoadPreset(idx);
            // Les sliders se rafraîchissent tout seul via KotonParameter.Changed → notre handler ci-dessus.
        }

        // ---- Oscilloscope --------------------------------------------------------------------------

        void InitScope()
        {
            _scopeLine = new Polyline
            {
                Stroke = new SolidColorBrush(Color.FromRgb(0x1F, 0xB6, 0xC3)),  // teal
                StrokeThickness = 1.2,
                StrokeLineJoin = PenLineJoin.Round,
                SnapsToDevicePixels = true,
            };
            ScopeCanvas.Children.Clear();
            ScopeCanvas.Children.Add(_scopeLine);

            _scopeTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(33)   // ~30 Hz
            };
            _scopeTimer.Tick += (s, e) => RefreshScope();
            _scopeTimer.Start();

            // Une rafraîchissement immédiat pour ne pas avoir un canvas vide pendant 33 ms.
            RefreshScope();
        }

        void RefreshScope()
        {
            if (_scopeLine == null || ScopeCanvas == null) return;
            double w = ScopeCanvas.ActualWidth;
            double h = ScopeCanvas.ActualHeight;
            if (w <= 0 || h <= 0) return;

            _plugin.GetOscilloscopeSamples(_scopeBuffer);

            // On sous-échantillonne le buffer sur la largeur du canvas (pas la peine d'un point par
            // sample — un point par colonne suffit largement).
            int cols = Math.Max(2, (int)w);
            var pts = new PointCollection(cols);
            int n = _scopeBuffer.Length;
            for (int i = 0; i < cols; i++)
            {
                int srcIdx = (int)((long)i * n / cols);
                if (srcIdx >= n) srcIdx = n - 1;
                float s = _scopeBuffer[srcIdx];
                // Clip visuel à ±1 (le plugin peut dépasser en polyphonie extrême, on tronque à la
                // fenêtre visible plutôt que d'écraser tout le tracé).
                if (s > 1f) s = 1f; else if (s < -1f) s = -1f;
                double y = (0.5 - 0.45 * s) * h;   // 0.45 pour laisser une marge en haut/bas
                pts.Add(new Point(i, y));
            }
            _scopeLine.Points = pts;
        }
    }
}
