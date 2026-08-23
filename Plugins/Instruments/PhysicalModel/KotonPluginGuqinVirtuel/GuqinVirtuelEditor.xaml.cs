using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using KotonStudio.Library;

namespace KotonPluginGuqinVirtuel
{
    /// <summary>
    /// Éditeur : paramètres à gauche, canvas de visualisation à droite.  Le canvas peint 7 lignes
    /// horizontales (cordes) et 13 marques verticales aux positions hui (ratios exacts, donc les
    /// marques sont plus serrées près des extrémités et plus espacées au milieu).  Chaque note
    /// jouée depuis le plugin déclenche `NoteStruck` → on ajoute un rond sur la corde à la
    /// position du doigté ; NoteReleased le retire.  P1 : rond statique. P2 : animation vibration.
    /// </summary>
    public partial class GuqinVirtuelEditor : UserControl, IKotonEditor
    {
        readonly GuqinVirtuelPlugin _plugin;
        readonly GuqinCanvas _canvas;
        bool _updating;

        public GuqinVirtuelEditor(GuqinVirtuelPlugin plugin)
        {
            _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
            InitializeComponent();

            // Combos
            foreach (var t in GuqinModel.AllTunings) cboTuning.Items.Add(t.Name);
            for (int i = 2; i <= 5; i++) cboFingers.Items.Add(i.ToString(CultureInfo.InvariantCulture));
            cboSnap.Items.Add("Rejeter");
            cboSnap.Items.Add("Snap plus proche");

            _canvas = new GuqinCanvas();
            canvasHost.Content = _canvas;

            SyncFromPlugin();

            var ps = _plugin.Parameters;
            foreach (var kp in ps) kp.Changed += _ => Dispatcher.BeginInvoke(new Action(SyncFromPlugin));

            cboTuning.SelectionChanged += (s, e) => SetParam("tuning", cboTuning.SelectedIndex);
            cboFingers.SelectionChanged += (s, e) => SetParam("max_fingers", cboFingers.SelectedIndex + 2);
            cboSnap.SelectionChanged += (s, e) => SetParam("snap_mode", cboSnap.SelectedIndex);
            sldDiapason.ValueChanged += (s, e) => { SetParam("diapason_cm", sldDiapason.Value); lblDiapason.Text = ((int)sldDiapason.Value).ToString(CultureInfo.InvariantCulture) + " cm"; };
            sldSpan.ValueChanged     += (s, e) => { SetParam("span_cm",     sldSpan.Value);     lblSpan.Text     = ((int)sldSpan.Value).ToString(CultureInfo.InvariantCulture) + " cm"; };
            sldSustain.ValueChanged  += (s, e) => SetParam("sustain",          sldSustain.Value);
            sldDamping.ValueChanged  += (s, e) => SetParam("hf_damping",       sldDamping.Value);
            sldBright.ValueChanged   += (s, e) => SetParam("pluck_brightness", sldBright.Value);
            sldBody.ValueChanged     += (s, e) => SetParam("body_resonance",   sldBody.Value);
            sldVolume.ValueChanged   += (s, e) => SetParam("volume",           sldVolume.Value);

            _plugin.NoteStruck += OnNoteStruck;
            _plugin.NoteReleased += OnNoteReleased;
            Unloaded += (s, e) =>
            {
                _plugin.NoteStruck -= OnNoteStruck;
                _plugin.NoteReleased -= OnNoteReleased;
            };
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
                cboTuning.SelectedIndex = Math.Max(0, Math.Min(GuqinModel.AllTunings.Length - 1, (int)GetV("tuning")));
                cboFingers.SelectedIndex = Math.Max(0, Math.Min(3, (int)GetV("max_fingers") - 2));
                cboSnap.SelectedIndex = GetV("snap_mode") >= 0.5 ? 1 : 0;
                sldDiapason.Value = GetV("diapason_cm"); lblDiapason.Text = ((int)sldDiapason.Value).ToString(CultureInfo.InvariantCulture) + " cm";
                sldSpan.Value = GetV("span_cm"); lblSpan.Text = ((int)sldSpan.Value).ToString(CultureInfo.InvariantCulture) + " cm";
                sldSustain.Value = GetV("sustain");
                sldDamping.Value = GetV("hf_damping");
                sldBright.Value = GetV("pluck_brightness");
                sldBody.Value = GetV("body_resonance");
                sldVolume.Value = GetV("volume");
                _canvas?.Redraw();
            }
            finally { _updating = false; }
        }

        void OnNoteStruck(GuqinVirtuelPlugin.StruckEvent ev) => Dispatcher.BeginInvoke(new Action(() => _canvas.AddFingering(ev.StringIdx, ev.Position, ev.Midi)));
        void OnNoteReleased(int midi) => Dispatcher.BeginInvoke(new Action(() => _canvas.RemoveFingering(midi)));

        public void OnContextUpdated(KotonRenderContext ctx) { }
    }

    /// <summary>Canvas WPF qui peint le guqin : 7 lignes horizontales (cordes), 13 marques hui
    /// aux ratios exacts (donc espacées de manière non-uniforme), et un rond de doigté par note
    /// active.</summary>
    internal sealed class GuqinCanvas : UserControl
    {
        readonly Canvas _root;
        readonly List<Fingering> _fingerings = new List<Fingering>();

        // Table de couleurs par corde (7 couleurs distinctes, palette Koton étendue).
        static readonly Color[] StringColors =
        {
            Color.FromRgb(0xE0, 0x6A, 0x55),   // corde 1 : rouge terre (grave)
            Color.FromRgb(0xE0, 0x9C, 0x4A),   // 2 : ambre
            Color.FromRgb(0xE3, 0xC6, 0x3E),   // 3 : or
            Color.FromRgb(0x6E, 0xC7, 0x77),   // 4 : vert
            Color.FromRgb(0x1F, 0xB6, 0xC3),   // 5 : teal Koton
            Color.FromRgb(0x4C, 0x79, 0xD6),   // 6 : bleu
            Color.FromRgb(0x9E, 0x6F, 0xE0),   // 7 : violet (aigu)
        };

        struct Fingering
        {
            public int StringIdx;
            public double Position;
            public int Midi;
        }

        public GuqinCanvas()
        {
            _root = new Canvas
            {
                Background = new SolidColorBrush(Color.FromRgb(0x10, 0x14, 0x18)),
                ClipToBounds = true,
            };
            Content = _root;
            SizeChanged += (s, e) => Redraw();
        }

        public void AddFingering(int stringIdx, double position, int midi)
        {
            _fingerings.Add(new Fingering { StringIdx = stringIdx, Position = position, Midi = midi });
            Redraw();
        }

        public void RemoveFingering(int midi)
        {
            for (int i = _fingerings.Count - 1; i >= 0; i--)
                if (_fingerings[i].Midi == midi) { _fingerings.RemoveAt(i); break; }
            Redraw();
        }

        public void Redraw()
        {
            _root.Children.Clear();
            double w = ActualWidth, h = ActualHeight;
            if (w < 20 || h < 20) return;

            const double marginX = 24;
            const double marginTopBot = 20;
            double stringSpan = w - 2 * marginX;
            double vSpan = h - 2 * marginTopBot;

            // Cordes horizontales — corde 1 en bas (grave), corde 7 en haut (aigu). Convention
            // guqin visuelle où l'aigu est vers le musicien.
            var stringBrush = new SolidColorBrush(Color.FromRgb(0x70, 0x78, 0x82)); stringBrush.Freeze();
            for (int s = 0; s < GuqinModel.StringCount; s++)
            {
                double y = StringY(s, marginTopBot, vSpan);
                _root.Children.Add(new Line
                {
                    X1 = marginX, Y1 = y, X2 = marginX + stringSpan, Y2 = y,
                    Stroke = stringBrush,
                    StrokeThickness = 1.2 + s * 0.2,   // plus épais vers le grave
                    IsHitTestVisible = false,
                });
                // Label corde (numéro)
                var lbl = new TextBlock
                {
                    Text = "" + (s + 1),
                    Foreground = new SolidColorBrush(StringColors[s]),
                    FontSize = 10,
                };
                Canvas.SetLeft(lbl, 4);
                Canvas.SetTop(lbl, y - 8);
                _root.Children.Add(lbl);
            }

            // Marques hui : 13 traits verticaux courts entre les cordes 1 et 7. Un peu plus
            // grands pour hui 7 (milieu) — repère visuel principal.
            var huiBrush = new SolidColorBrush(Color.FromRgb(0xB0, 0xA6, 0x88)); huiBrush.Freeze();
            double topY = StringY(GuqinModel.StringCount - 1, marginTopBot, vSpan);
            double botY = StringY(0, marginTopBot, vSpan);
            for (int h2 = 0; h2 < GuqinModel.HuiPositions.Length; h2++)
            {
                double x = marginX + GuqinModel.HuiPositions[h2] * stringSpan;
                bool center = h2 == 6;   // hui 7 (index 6) = milieu
                double sizeExt = center ? 5 : 3;
                _root.Children.Add(new Line
                {
                    X1 = x, Y1 = topY - sizeExt, X2 = x, Y2 = botY + sizeExt,
                    Stroke = huiBrush,
                    StrokeThickness = center ? 1.5 : 1,
                    IsHitTestVisible = false,
                    Opacity = 0.55,
                });
                // Label hui (numéro sous le trait)
                var lbl = new TextBlock
                {
                    Text = "" + (h2 + 1),
                    Foreground = huiBrush,
                    FontSize = 9,
                    Opacity = 0.75,
                };
                Canvas.SetLeft(lbl, x - 4);
                Canvas.SetTop(lbl, botY + sizeExt + 2);
                _root.Children.Add(lbl);
            }

            // Doigtés actifs : gros rond centré sur (position × corde), rempli de la couleur de la
            // corde, halo sombre autour pour ressortir sur l'écran sombre.
            foreach (var f in _fingerings)
            {
                if (f.StringIdx < 0 || f.StringIdx >= GuqinModel.StringCount) continue;
                double y = StringY(f.StringIdx, marginTopBot, vSpan);
                double x = marginX + f.Position * stringSpan;
                if (f.Position <= 1e-6)
                {
                    // Corde à vide : petit rond à gauche (au niveau du yueshan)
                    x = marginX - 8;
                }
                var col = StringColors[f.StringIdx];
                _root.Children.Add(new Ellipse
                {
                    Width = 14, Height = 14,
                    Fill = new SolidColorBrush(col),
                    Stroke = new SolidColorBrush(Color.FromArgb(180, 0, 0, 0)),
                    StrokeThickness = 1.5,
                    IsHitTestVisible = false,
                });
                Canvas.SetLeft(_root.Children[_root.Children.Count - 1], x - 7);
                Canvas.SetTop(_root.Children[_root.Children.Count - 1], y - 7);
            }
        }

        static double StringY(int stringIdx, double marginTop, double vSpan)
        {
            // Corde 1 (grave) en bas, corde 7 (aigu) en haut. Espacement uniforme.
            double t = (GuqinModel.StringCount - 1 - stringIdx) / (double)(GuqinModel.StringCount - 1);
            return marginTop + t * vSpan;
        }
    }
}
