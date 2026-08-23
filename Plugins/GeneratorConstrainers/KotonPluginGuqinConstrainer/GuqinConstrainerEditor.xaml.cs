using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using KotonStudio.Library;

namespace KotonPluginGuqinConstrainer
{
    /// <summary>
    /// Éditeur : params à gauche, visualisation guqin à droite. Les doigtés apparaissent quand
    /// Filter() est appelé (au flatten du bloc — pas en temps réel audio, mais suffisant pour
    /// vérifier ce que le constrainer décide).
    /// </summary>
    public partial class GuqinConstrainerEditor : UserControl, IKotonEditor
    {
        readonly GuqinConstrainerPlugin _plugin;
        readonly GuqinCanvas _canvas;
        bool _updating;

        public GuqinConstrainerEditor(GuqinConstrainerPlugin plugin)
        {
            _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
            InitializeComponent();

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

            _plugin.NoteStruck += OnNoteStruck;
            _plugin.NoteReleased += OnNoteReleased;
            Unloaded += (s, e) =>
            {
                _plugin.NoteStruck -= OnNoteStruck;
                _plugin.NoteReleased -= OnNoteReleased;
                _canvas.StopAnimation();
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
                _canvas?.Redraw();
            }
            finally { _updating = false; }
        }

        void OnNoteStruck(GuqinConstrainerPlugin.StruckEvent ev) => Dispatcher.BeginInvoke(new Action(() => _canvas.EnqueueStruck(ev)));
        void OnNoteReleased(GuqinConstrainerPlugin.ReleaseEvent ev) => Dispatcher.BeginInvoke(new Action(() => _canvas.EnqueueReleased(ev)));

        public void OnContextUpdated(KotonRenderContext ctx) { }
    }

    /// <summary>Canvas guqin : silhouette + 7 cordes (aigu en haut côté joueur) + 13 hui EN HAUT
    /// (côté joueur qui regarde) + doigtés actifs schedulés selon StartBeat + tempo. Le BlockId
    /// permet de reset le référentiel temporel à chaque nouvelle passe Filter (nouvelle relecture
    /// = nouveau départ), évite les doublons quand le même bloc est ré-évalué (thumbnail refresh
    /// etc.).</summary>
    internal sealed class GuqinCanvas : UserControl
    {
        readonly Canvas _root;
        readonly List<Fingering> _fingerings = new List<Fingering>();
        readonly List<Vibration> _vibrations = new List<Vibration>();
        DispatcherTimer _timer;

        static readonly Color[] StringColors =
        {
            Color.FromRgb(0xE0, 0x6A, 0x55), Color.FromRgb(0xE0, 0x9C, 0x4A), Color.FromRgb(0xE3, 0xC6, 0x3E),
            Color.FromRgb(0x6E, 0xC7, 0x77), Color.FromRgb(0x1F, 0xB6, 0xC3), Color.FromRgb(0x4C, 0x79, 0xD6),
            Color.FromRgb(0x9E, 0x6F, 0xE0),
        };
        static readonly Color BodyDark  = Color.FromRgb(0x2A, 0x1A, 0x12);
        static readonly Color BodyLight = Color.FromRgb(0x4A, 0x2E, 0x1E);
        static readonly Color BodyEdge  = Color.FromRgb(0x5A, 0x3E, 0x28);
        static readonly Color SoundHole = Color.FromRgb(0x0A, 0x06, 0x04);

        struct Fingering { public int StringIdx; public double Position; public int Midi; }
        sealed class Vibration
        {
            public int StringIdx; public double Position; public double StartAmpPx;
            public double DecayPerSec; public double VisualHz; public DateTime StartTime;
        }
        // Événement en attente de son moment de déclenchement (StartBeat * tempo → délai wall-clock).
        sealed class Pending
        {
            public GuqinConstrainerPlugin.StruckEvent Ev;
            public DateTime DeadlineUtc;
        }
        readonly List<Pending> _pending = new List<Pending>();
        readonly List<Pending> _pendingReleased = new List<Pending>();
        long _currentBlockId = -1;
        DateTime _blockStartWallClock;

        public GuqinCanvas()
        {
            _root = new Canvas { Background = new SolidColorBrush(Color.FromRgb(0x10, 0x14, 0x18)), ClipToBounds = true };
            Content = _root;
            SizeChanged += (s, e) => Render();
        }

        /// <summary>Reçoit un événement du plugin. Si BlockId change (nouvelle passe Filter =
        /// nouvelle relecture), on RESET tout : purge des pending précédents, référentiel
        /// temporel redémarré. Ça évite les dots fantômes qui traîneraient d'une passe à l'autre
        /// et le doublement quand un même bloc est redemandé (score refresh, etc.).</summary>
        public void EnqueueStruck(GuqinConstrainerPlugin.StruckEvent ev)
        {
            if (ev.BlockId != _currentBlockId)
            {
                _currentBlockId = ev.BlockId;
                _blockStartWallClock = DateTime.UtcNow;
                _pending.Clear();
                _pendingReleased.Clear();
                _fingerings.Clear();
                _vibrations.Clear();
            }
            double tempo = ev.Tempo > 0 ? ev.Tempo : 120.0;
            double delaySec = ev.StartBeat * 60.0 / tempo;
            _pending.Add(new Pending { Ev = ev, DeadlineUtc = _blockStartWallClock.AddSeconds(delaySec) });
            EnsureTimer();
        }

        public void EnqueueReleased(GuqinConstrainerPlugin.ReleaseEvent ev)
        {
            // Sync avec le référentiel du bloc (comme EnqueueStruck) : si BlockId diffère, reset.
            if (ev.BlockId != _currentBlockId)
            {
                _currentBlockId = ev.BlockId;
                _blockStartWallClock = DateTime.UtcNow;
                _pending.Clear();
                _pendingReleased.Clear();
                _fingerings.Clear();
                _vibrations.Clear();
            }
            double tempo = ev.Tempo > 0 ? ev.Tempo : 120.0;
            double delaySec = ev.AtBeat * 60.0 / tempo;
            // On abuse un peu de la struct Pending qui porte un StruckEvent : on stocke midi dans
            // Ev.Midi et le deadline dans DeadlineUtc, les autres champs sont ignorés.
            _pendingReleased.Add(new Pending
            {
                Ev = new GuqinConstrainerPlugin.StruckEvent { Midi = ev.Midi },
                DeadlineUtc = _blockStartWallClock.AddSeconds(delaySec),
            });
            EnsureTimer();
        }

        void EnsureTimer()
        {
            if (_timer != null) return;
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
            _timer.Tick += (s, e) =>
            {
                var now = DateTime.UtcNow;
                // Débloque les pending arrivés à échéance → visualisation.
                for (int i = _pending.Count - 1; i >= 0; i--)
                {
                    if (_pending[i].DeadlineUtc > now) continue;
                    var p = _pending[i];
                    _pending.RemoveAt(i);
                    _fingerings.Add(new Fingering { StringIdx = p.Ev.StringIdx, Position = p.Ev.Position, Midi = p.Ev.Midi });
                    _vibrations.Add(new Vibration
                    {
                        StringIdx = p.Ev.StringIdx, Position = p.Ev.Position,
                        StartAmpPx = 6.0, DecayPerSec = 1.8,
                        VisualHz = 9.0 + (6 - p.Ev.StringIdx) * 0.6, StartTime = now,
                    });
                }
                for (int i = _pendingReleased.Count - 1; i >= 0; i--)
                {
                    if (_pendingReleased[i].DeadlineUtc > now) continue;
                    int m = _pendingReleased[i].Ev.Midi;
                    _pendingReleased.RemoveAt(i);
                    for (int j = _fingerings.Count - 1; j >= 0; j--)
                        if (_fingerings[j].Midi == m) { _fingerings.RemoveAt(j); break; }
                }
                // Décroissance des vibrations.
                bool anyAlive = _pending.Count > 0 || _pendingReleased.Count > 0;
                for (int i = _vibrations.Count - 1; i >= 0; i--)
                {
                    var v = _vibrations[i];
                    double elapsed = (now - v.StartTime).TotalSeconds;
                    double amp = v.StartAmpPx * Math.Exp(-v.DecayPerSec * elapsed);
                    if (amp < 0.4) _vibrations.RemoveAt(i); else anyAlive = true;
                }
                if (!anyAlive) StopAnimation();
                Render();
            };
            _timer.Start();
        }
        public void StopAnimation() { _timer?.Stop(); _timer = null; }

        const double StringSpacingPx = 11;
        const double BodyExtraTop = 40, BodyExtraBot = 20, MarginX = 32;

        public void Redraw() => Render();

        void Render()
        {
            _root.Children.Clear();
            double w = ActualWidth, h = ActualHeight;
            if (w < 40 || h < 40) return;
            double stringSpan = w - 2 * MarginX;
            double stringsHeight = (GuqinModel.StringCount - 1) * StringSpacingPx;
            double topStringY = (h - stringsHeight) / 2;
            double botStringY = topStringY + stringsHeight;

            DrawBody(topStringY - BodyExtraTop, botStringY + BodyExtraBot, MarginX - 12, MarginX + stringSpan + 12);

            for (int s = 0; s < GuqinModel.StringCount; s++)
            {
                double y = StringY(s, topStringY);
                double thickness = 0.9 + (GuqinModel.StringCount - 1 - s) * 0.25;
                DrawString(s, y, MarginX, MarginX + stringSpan, thickness);
                var lbl = new TextBlock { Text = "" + (s + 1), Foreground = new SolidColorBrush(StringColors[s]), FontSize = 10, FontWeight = FontWeights.Bold };
                Canvas.SetLeft(lbl, 8); Canvas.SetTop(lbl, y - 8);
                _root.Children.Add(lbl);
            }

            // Hui EN HAUT (côté joueur : quand le musicien regarde son instrument, les hui sont
            // sur le bord OPPOSÉ des cordes, visibles au-dessus des cordes dans notre schéma).
            // Numéros AU-DESSUS des marques hui.
            var huiFill = new SolidColorBrush(Color.FromRgb(0xF0, 0xE8, 0xD2)); huiFill.Freeze();
            var huiBorder = new SolidColorBrush(Color.FromRgb(0x8A, 0x74, 0x50)); huiBorder.Freeze();
            double huiY = topStringY - 16;
            for (int h2 = 0; h2 < GuqinModel.HuiPositions.Length; h2++)
            {
                double x = MarginX + GuqinModel.HuiPositions[h2] * stringSpan;
                bool center = h2 == 6;
                double d = center ? 8 : 5;
                var lbl = new TextBlock { Text = "" + (h2 + 1), Foreground = new SolidColorBrush(Color.FromRgb(0xB0, 0xA0, 0x88)), FontSize = 8, Opacity = 0.85 };
                Canvas.SetLeft(lbl, x - 4); Canvas.SetTop(lbl, huiY - 12);
                _root.Children.Add(lbl);
                var e = new Ellipse { Width = d, Height = d, Fill = huiFill, Stroke = huiBorder, StrokeThickness = 0.8, IsHitTestVisible = false };
                Canvas.SetLeft(e, x - d / 2); Canvas.SetTop(e, huiY - d / 2);
                _root.Children.Add(e);
            }

            // Doigtés actifs : ligne guide fine du hui jusqu'à la corde + disque sur la corde à
            // la même X que le hui correspondant. Le disque est aligné VERTICALEMENT avec le hui
            // pour rendre évident quel hui commande quel fingering.
            foreach (var f in _fingerings)
            {
                if (f.StringIdx < 0 || f.StringIdx >= GuqinModel.StringCount) continue;
                double y = StringY(f.StringIdx, topStringY);
                double x = f.Position <= 1e-6 ? MarginX - 12 : MarginX + f.Position * stringSpan;
                var col = StringColors[f.StringIdx];
                // Guide vertical (opacité faible) : du hui vers la corde pressée. Pas de guide pour
                // les cordes à vide (rendues à gauche du yueshan).
                if (f.Position > 1e-6)
                {
                    var guide = new Line
                    {
                        X1 = x, Y1 = huiY + 4, X2 = x, Y2 = y,
                        Stroke = new SolidColorBrush(Color.FromArgb(80, col.R, col.G, col.B)),
                        StrokeThickness = 1,
                        IsHitTestVisible = false,
                    };
                    _root.Children.Add(guide);
                }
                var dot = new Ellipse
                {
                    Width = 11, Height = 11,
                    Fill = new SolidColorBrush(col),
                    Stroke = new SolidColorBrush(Color.FromArgb(220, 0, 0, 0)),
                    StrokeThickness = 1.2,
                    IsHitTestVisible = false,
                };
                Canvas.SetLeft(dot, x - 5.5); Canvas.SetTop(dot, y - 5.5);
                _root.Children.Add(dot);
            }
        }

        double StringY(int stringIdx, double topStringY)
        {
            double t = (GuqinModel.StringCount - 1 - stringIdx) / (double)(GuqinModel.StringCount - 1);
            return topStringY + t * ((GuqinModel.StringCount - 1) * StringSpacingPx);
        }

        void DrawBody(double top, double bottom, double left, double right)
        {
            double taper = 6;
            var pts = new PointCollection
            {
                new Point(left,  top - taper/2), new Point(right, top + taper/2),
                new Point(right, bottom - taper/2), new Point(left,  bottom + taper/2),
            };
            var grad = new LinearGradientBrush { StartPoint = new Point(0.5, 0), EndPoint = new Point(0.5, 1) };
            grad.GradientStops.Add(new GradientStop(BodyDark, 0));
            grad.GradientStops.Add(new GradientStop(BodyLight, 0.5));
            grad.GradientStops.Add(new GradientStop(BodyDark, 1));
            grad.Freeze();
            _root.Children.Add(new Polygon { Points = pts, Fill = grad, Stroke = new SolidColorBrush(BodyEdge), StrokeThickness = 1.5, StrokeLineJoin = PenLineJoin.Round, IsHitTestVisible = false });

            double bodyH = bottom - top, bodyW = right - left;
            double poolY = bottom - bodyH * 0.28, pondY = bottom - bodyH * 0.20;
            var poolRect = new Rectangle { Width = bodyW * 0.18, Height = 6, Fill = new SolidColorBrush(SoundHole), RadiusX = 3, RadiusY = 3, Opacity = 0.55, IsHitTestVisible = false };
            Canvas.SetLeft(poolRect, left + bodyW * 0.30); Canvas.SetTop(poolRect, poolY);
            _root.Children.Add(poolRect);
            var pondRect = new Rectangle { Width = bodyW * 0.10, Height = 4, Fill = new SolidColorBrush(SoundHole), RadiusX = 2, RadiusY = 2, Opacity = 0.5, IsHitTestVisible = false };
            Canvas.SetLeft(pondRect, left + bodyW * 0.76); Canvas.SetTop(pondRect, pondY);
            _root.Children.Add(pondRect);
        }

        void DrawString(int stringIdx, double baseY, double leftX, double rightX, double thickness)
        {
            var brush = new SolidColorBrush(Color.FromRgb(0xB8, 0xB0, 0x9A)); brush.Freeze();
            var strokeVoice = new SolidColorBrush(StringColors[stringIdx]); strokeVoice.Freeze();

            Vibration vib = null;
            double bestAmp = 0;
            foreach (var v in _vibrations)
            {
                if (v.StringIdx != stringIdx) continue;
                double elapsed = (DateTime.UtcNow - v.StartTime).TotalSeconds;
                double amp = v.StartAmpPx * Math.Exp(-v.DecayPerSec * elapsed);
                if (amp > bestAmp) { bestAmp = amp; vib = v; }
            }

            if (vib == null || bestAmp < 0.4)
            {
                _root.Children.Add(new Line { X1 = leftX, Y1 = baseY, X2 = rightX, Y2 = baseY, Stroke = brush, StrokeThickness = thickness, IsHitTestVisible = false });
                return;
            }

            double xFinger = leftX + vib.Position * (rightX - leftX);
            if (vib.Position <= 1e-6) xFinger = leftX;
            if (xFinger > leftX + 0.5)
                _root.Children.Add(new Line { X1 = leftX, Y1 = baseY, X2 = xFinger, Y2 = baseY, Stroke = brush, StrokeThickness = thickness, IsHitTestVisible = false });

            const int Segments = 40;
            double elapsedRender = (DateTime.UtcNow - vib.StartTime).TotalSeconds;
            double sinPhase = Math.Sin(2 * Math.PI * vib.VisualHz * elapsedRender);
            var pts = new PointCollection(Segments + 1);
            for (int i = 0; i <= Segments; i++)
            {
                double u = i / (double)Segments;
                double x = xFinger + u * (rightX - xFinger);
                double dy = bestAmp * Math.Sin(Math.PI * u) * sinPhase;
                pts.Add(new Point(x, baseY + dy));
            }
            _root.Children.Add(new Polyline { Points = pts, Stroke = strokeVoice, StrokeThickness = thickness + 0.5, StrokeLineJoin = PenLineJoin.Round, IsHitTestVisible = false });
        }
    }
}
