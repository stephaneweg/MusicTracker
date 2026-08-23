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

            _canvas = new GuqinCanvas(() => _plugin != null && _plugin.Parameters != null
                ? GetParam("diapason_cm", 110.0)
                : 110.0);
            canvasHost.Content = _canvas;

            double GetParam(string id, double fallback)
            {
                for (int i = 0; i < _plugin.Parameters.Count; i++)
                    if (_plugin.Parameters[i].Id == id) return _plugin.Parameters[i].Value;
                return fallback;
            }

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
            // PlaybackStarted : le référentiel wall-clock est défini au moment où l'audio démarre
            // (après le prime buffer). Tous les events reçus avant restent en buffer et sont
            // schedulés à partir de ce moment.
            Action onStart = () => Dispatcher.BeginInvoke(new Action(() => _canvas.OnPlaybackStarted()));
            Action onStop = () => Dispatcher.BeginInvoke(new Action(() => _canvas.PurgeAll()));
            KotonHost.PlaybackStarted += onStart;
            KotonHost.PlaybackStopped += onStop;
            Unloaded += (s, e) =>
            {
                _plugin.NoteStruck -= OnNoteStruck;
                _plugin.NoteReleased -= OnNoteReleased;
                KotonHost.PlaybackStarted -= onStart;
                KotonHost.PlaybackStopped -= onStop;
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

    /// <summary>Canvas guqin : silhouette érable + hui/sillet/chevalet noyer + 7 cordes (aigu en
    /// haut côté joueur) + 13 hui EN HAUT + doigtés actifs schedulés selon StartBeat + tempo.
    /// L'espacement des cordes est proportionnel au diapason (2 cm réels du plugin), donc un mini
    /// guqin (58 cm) affiche des cordes plus serrées qu'un grand (120 cm) pour la même largeur de
    /// canvas — cohérent avec l'instrument physique.</summary>
    internal sealed class GuqinCanvas : UserControl
    {
        readonly Canvas _root;
        readonly List<Fingering> _fingerings = new List<Fingering>();
        readonly List<Vibration> _vibrations = new List<Vibration>();
        DispatcherTimer _timer;
        readonly Func<double> _getDiapasonCm;

        static readonly Color[] StringColors =
        {
            Color.FromRgb(0xE0, 0x6A, 0x55), Color.FromRgb(0xE0, 0x9C, 0x4A), Color.FromRgb(0xE3, 0xC6, 0x3E),
            Color.FromRgb(0x6E, 0xC7, 0x77), Color.FromRgb(0x1F, 0xB6, 0xC3), Color.FromRgb(0x4C, 0x79, 0xD6),
            Color.FromRgb(0x9E, 0x6F, 0xE0),
        };
        // Érable clair : bois vernis chaud. Deux tons pour un gradient subtil qui suggère le vernis.
        static readonly Color MapleLight = Color.FromRgb(0xEB, 0xD4, 0xA6);   // reflet clair (au centre)
        static readonly Color MapleMid   = Color.FromRgb(0xD9, 0xBE, 0x8A);   // ton moyen
        static readonly Color MapleDark  = Color.FromRgb(0xB2, 0x94, 0x62);   // ombre sur les bords
        // Noyer : hui + sillet + chevalet. 2 tons pour une inlay avec profondeur.
        static readonly Color WalnutDark = Color.FromRgb(0x3D, 0x24, 0x15);
        static readonly Color WalnutMid  = Color.FromRgb(0x5C, 0x3B, 0x24);
        static readonly Color WalnutRim  = Color.FromRgb(0x2A, 0x18, 0x0E);
        // Grain : fines lignes plus foncées qui suggèrent le fil du bois. Semi-transparentes.
        static readonly Color GrainLine  = Color.FromRgb(0x8E, 0x6D, 0x3F);

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
        // Buffer des events reçus AVANT que PlaybackStarted ne fixe le référentiel wall-clock —
        // tous les Filter arrivent au flatten (avant que l'audio ne joue), il faut les garder pour
        // les scheduler correctement une fois le référentiel connu.
        readonly List<GuqinConstrainerPlugin.StruckEvent> _bufferedStruck = new List<GuqinConstrainerPlugin.StruckEvent>();
        readonly List<GuqinConstrainerPlugin.ReleaseEvent> _bufferedReleased = new List<GuqinConstrainerPlugin.ReleaseEvent>();
        DateTime? _playbackStartWallClock;   // null tant que la lecture n'a pas démarré

        public GuqinCanvas(Func<double> getDiapasonCm = null)
        {
            _getDiapasonCm = getDiapasonCm ?? (() => 110.0);
            _root = new Canvas { Background = new SolidColorBrush(Color.FromRgb(0x14, 0x18, 0x1C)), ClipToBounds = true };
            Content = _root;
            SizeChanged += (s, e) => Render();
        }

        /// <summary>Reçoit un event. Si le référentiel wall-clock est déjà connu (playback a
        /// démarré), on schedule direct sur `_playbackStartWallClock + AbsoluteStartBeat * 60/tempo`.
        /// Sinon on buffer — sera flushé quand OnPlaybackStarted fixera le référentiel.</summary>
        public void EnqueueStruck(GuqinConstrainerPlugin.StruckEvent ev)
        {
            if (_playbackStartWallClock.HasValue) SchedulePending(ev);
            else _bufferedStruck.Add(ev);
            EnsureTimer();
        }

        public void EnqueueReleased(GuqinConstrainerPlugin.ReleaseEvent ev)
        {
            if (_playbackStartWallClock.HasValue) SchedulePendingRelease(ev);
            else _bufferedReleased.Add(ev);
            EnsureTimer();
        }

        /// <summary>Appelé quand la lecture audio démarre (event KotonHost.PlaybackStarted). Fixe le
        /// référentiel wall-clock et flush tous les events reçus pendant le flatten dans les files
        /// pending, chacun à sa position absolue sur la timeline.</summary>
        public void OnPlaybackStarted()
        {
            _playbackStartWallClock = DateTime.UtcNow;
            foreach (var ev in _bufferedStruck) SchedulePending(ev);
            foreach (var ev in _bufferedReleased) SchedulePendingRelease(ev);
            _bufferedStruck.Clear();
            _bufferedReleased.Clear();
            EnsureTimer();
        }

        void SchedulePending(GuqinConstrainerPlugin.StruckEvent ev)
        {
            double tempo = ev.Tempo > 0 ? ev.Tempo : 120.0;
            double delaySec = ev.AbsoluteStartBeat * 60.0 / tempo;
            _pending.Add(new Pending { Ev = ev, DeadlineUtc = _playbackStartWallClock.Value.AddSeconds(delaySec) });
        }

        void SchedulePendingRelease(GuqinConstrainerPlugin.ReleaseEvent ev)
        {
            double tempo = ev.Tempo > 0 ? ev.Tempo : 120.0;
            double delaySec = ev.AbsoluteAtBeat * 60.0 / tempo;
            _pendingReleased.Add(new Pending
            {
                Ev = new GuqinConstrainerPlugin.StruckEvent { Midi = ev.Midi },
                DeadlineUtc = _playbackStartWallClock.Value.AddSeconds(delaySec),
            });
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

        /// <summary>Purge complète : appelée quand la lecture s'arrête (bouton Stop). Vide TOUT :
        /// buffers, files pending, vibrations, fingerings actifs, référentiel wall-clock. Coupe
        /// l'animation, re-render une fois pour effacer les dots. Le prochain PlaybackStarted
        /// repartira d'un état complètement propre.</summary>
        public void PurgeAll()
        {
            _bufferedStruck.Clear();
            _bufferedReleased.Clear();
            _pending.Clear();
            _pendingReleased.Clear();
            _vibrations.Clear();
            _fingerings.Clear();
            _playbackStartWallClock = null;
            StopAnimation();
            Render();
        }

        // Pad haut = pad bas → corps symétrique autour du champ de cordes.
        const double BodyExtraTop = 22, BodyExtraBot = 22, MarginX = 44;
        // Largeur des pièces en noyer (sillet + chevalet), en cm réels.
        const double NutWidthCm = 3.0, BridgeWidthCm = 4.0;
        // Espacement inter-cordes en cm réels (correspond à un vrai guqin).
        const double StringSpacingCm = 2.0;
        // Gap supplémentaire entre corde 6 et corde 7 pour loger la rangée de hui EN INTERNE
        // (comme les vrais hui inlays entre les 2 cordes aiguës du bord du plateau).
        const double HuiGapExtraPx = 16;

        public void Redraw() => Render();

        double _stringSpacingPx;   // recalculé à chaque Render en fonction du diapason
        double _pxPerCm;

        void Render()
        {
            _root.Children.Clear();
            double w = ActualWidth, h = ActualHeight;
            if (w < 40 || h < 40) return;

            double diapasonCm = _getDiapasonCm();
            if (diapasonCm < 10) diapasonCm = 110;
            double stringSpan = w - 2 * MarginX;
            _pxPerCm = stringSpan / diapasonCm;
            // Espacement des cordes = 2 cm réels, quelle que soit la hauteur du canvas — on garde
            // les proportions du vrai instrument (allongé ~6:1). Si le canvas est plus haut que
            // nécessaire, le corps se centre verticalement avec du fond noir autour ; si trop
            // étroit, on rétrécit sous 2 cm avec un plancher visuel à 6 px.
            _stringSpacingPx = StringSpacingCm * _pxPerCm;
            if (_stringSpacingPx < 6) _stringSpacingPx = 6;

            // Hauteur totale = 6 gaps entre cordes + un extra spécifique entre s6 et s7.
            double stringsHeight = (GuqinModel.StringCount - 1) * _stringSpacingPx + HuiGapExtraPx;
            double topStringY = (h - stringsHeight) / 2;
            double botStringY = topStringY + stringsHeight;

            // Corps érable — le corps déborde LARGEMENT au-dessus des hui et sous la corde 1.
            DrawBody(topStringY - BodyExtraTop, botStringY + BodyExtraBot, MarginX - 18, MarginX + stringSpan + 18);

            // Sillet (nut) à gauche = noyer, chevalet (bridge) à droite = noyer. Le sillet s'étend
            // légèrement au-dessus/sous la 1re et 7e corde.
            DrawWalnutBar(MarginX - NutWidthCm * _pxPerCm, MarginX, topStringY - 8, botStringY + 8);
            DrawWalnutBar(MarginX + stringSpan, MarginX + stringSpan + BridgeWidthCm * _pxPerCm, topStringY - 8, botStringY + 8);

            for (int s = 0; s < GuqinModel.StringCount; s++)
            {
                double y = StringY(s, topStringY);
                double thickness = 0.9 + (GuqinModel.StringCount - 1 - s) * 0.25;
                DrawString(s, y, MarginX, MarginX + stringSpan, thickness);
                var lbl = new TextBlock { Text = "" + (s + 1), Foreground = new SolidColorBrush(StringColors[s]), FontSize = 10, FontWeight = FontWeights.Bold };
                Canvas.SetLeft(lbl, 12); Canvas.SetTop(lbl, y - 8);
                _root.Children.Add(lbl);
            }

            // Hui EN NOYER inlays, positionnés entre corde 7 (top) et corde 6 (juste sous).
            // C'est l'emplacement standard sur un vrai guqin — le musicien voit les hui à côté
            // des cordes aiguës. Centrés au milieu du gap HuiGapExtraPx.
            var huiFill = new SolidColorBrush(WalnutMid); huiFill.Freeze();
            var huiRim  = new SolidColorBrush(WalnutRim); huiRim.Freeze();
            double huiY = topStringY + HuiGapExtraPx / 2;
            for (int h2 = 0; h2 < GuqinModel.HuiPositions.Length; h2++)
            {
                double x = MarginX + GuqinModel.HuiPositions[h2] * stringSpan;
                bool center = h2 == 6;
                double d = center ? 8 : 5;
                var e = new Ellipse
                {
                    Width = d, Height = d,
                    Fill = huiFill,
                    Stroke = huiRim,
                    StrokeThickness = 0.6,
                    IsHitTestVisible = false,
                };
                Canvas.SetLeft(e, x - d / 2); Canvas.SetTop(e, huiY - d / 2);
                _root.Children.Add(e);
            }

            // Doigtés actifs : ligne guide fine du hui jusqu'à la corde + disque sur la corde à
            // la même X que le hui. Guide vertical qui part du hui (vers le haut si c'est la
            // corde 7 au-dessus, vers le bas pour les cordes 1..6 en dessous).
            foreach (var f in _fingerings)
            {
                if (f.StringIdx < 0 || f.StringIdx >= GuqinModel.StringCount) continue;
                double y = StringY(f.StringIdx, topStringY);
                double x = f.Position <= 1e-6 ? MarginX - 12 : MarginX + f.Position * stringSpan;
                var col = StringColors[f.StringIdx];
                if (f.Position > 1e-6)
                {
                    // Guide entre hui et corde : segment court, opacité faible. Directions gérées
                    // automatiquement puisque WPF trace de X1,Y1 à X2,Y2 indépendamment du sens.
                    var guide = new Line
                    {
                        X1 = x, Y1 = huiY, X2 = x, Y2 = y,
                        Stroke = new SolidColorBrush(Color.FromArgb(90, col.R, col.G, col.B)),
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
            // Corde 7 (index 6) au top ; ajoute HuiGapExtraPx APRÈS s7 pour laisser la place aux
            // hui inlays entre s7 et s6. Les cordes 1..6 (index 0..5) descendent normalement avec
            // ce décalage supplémentaire.
            if (stringIdx == GuqinModel.StringCount - 1) return topStringY;
            int offsetSteps = (GuqinModel.StringCount - 1) - stringIdx;   // s6=1, s5=2, ..., s1=6
            return topStringY + HuiGapExtraPx + offsetSteps * _stringSpacingPx;
        }

        /// <summary>Corps rectangulaire aux coins arrondis, fill érable clair vernis (gradient
        /// vertical + grain procédural = fines lignes horizontales quasi-transparentes qui
        /// suggèrent le fil du bois). Rendu unique via un Rectangle avec RadiusX/Y (WPF gère
        /// l'antialiasing des coins arrondis).</summary>
        void DrawBody(double top, double bottom, double left, double right)
        {
            double w = right - left, hgt = bottom - top;

            // Ombre douce sous le corps pour donner de la présence sur le fond sombre.
            var shadow = new Rectangle
            {
                Width = w + 8, Height = hgt + 8,
                Fill = new SolidColorBrush(Color.FromArgb(90, 0, 0, 0)),
                RadiusX = 14, RadiusY = 14,
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(shadow, left - 4); Canvas.SetTop(shadow, top - 2);
            _root.Children.Add(shadow);

            // Corps : gradient vertical clair au centre → foncé sur les bords (effet vernis bombé).
            var grad = new LinearGradientBrush { StartPoint = new Point(0.5, 0), EndPoint = new Point(0.5, 1) };
            grad.GradientStops.Add(new GradientStop(MapleDark, 0));
            grad.GradientStops.Add(new GradientStop(MapleMid, 0.15));
            grad.GradientStops.Add(new GradientStop(MapleLight, 0.5));
            grad.GradientStops.Add(new GradientStop(MapleMid, 0.85));
            grad.GradientStops.Add(new GradientStop(MapleDark, 1));
            grad.Freeze();
            var body = new Rectangle
            {
                Width = w, Height = hgt,
                Fill = grad,
                Stroke = new SolidColorBrush(WalnutRim),
                StrokeThickness = 1.5,
                RadiusX = 12, RadiusY = 12,
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(body, left); Canvas.SetTop(body, top);
            _root.Children.Add(body);

            // Grain procédural : lignes horizontales fines, semi-transparentes, réparties de manière
            // pseudo-aléatoire (seed fixe pour un rendu stable, l'image ne "vibre" pas entre 2 rendus).
            var rng = new Random(1234);
            int grainCount = Math.Max(6, (int)(hgt / 8));
            for (int i = 0; i < grainCount; i++)
            {
                double y = top + rng.NextDouble() * hgt;
                double thickness = 0.5 + rng.NextDouble() * 0.6;
                byte alpha = (byte)(20 + rng.Next(30));
                var line = new Line
                {
                    X1 = left + 6, Y1 = y, X2 = right - 6, Y2 = y + (rng.NextDouble() - 0.5) * 1.5,
                    Stroke = new SolidColorBrush(Color.FromArgb(alpha, GrainLine.R, GrainLine.G, GrainLine.B)),
                    StrokeThickness = thickness,
                    IsHitTestVisible = false,
                };
                _root.Children.Add(line);
            }
            // 2-3 "noeuds" du bois : petits ovales à peine visibles, comme des irrégularités du fil.
            for (int i = 0; i < 3; i++)
            {
                double cx = left + 30 + rng.NextDouble() * (w - 60);
                double cy = top + 10 + rng.NextDouble() * (hgt - 20);
                double d = 4 + rng.NextDouble() * 6;
                var knot = new Ellipse
                {
                    Width = d, Height = d * 0.55,
                    Fill = new SolidColorBrush(Color.FromArgb(35, GrainLine.R, GrainLine.G, GrainLine.B)),
                    IsHitTestVisible = false,
                };
                Canvas.SetLeft(knot, cx - d / 2); Canvas.SetTop(knot, cy - d * 0.275);
                _root.Children.Add(knot);
            }
        }

        /// <summary>Barre en noyer (sillet ou chevalet). Dessinée entre 2 X et sur toute la hauteur
        /// des cordes + un peu au-dessus/sous, coins légèrement arrondis, gradient horizontal pour
        /// suggérer une pièce montée légèrement en relief.</summary>
        void DrawWalnutBar(double leftX, double rightX, double topY, double botY)
        {
            double w = rightX - leftX;
            if (w < 1) w = 1;
            var grad = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
            grad.GradientStops.Add(new GradientStop(WalnutRim, 0));
            grad.GradientStops.Add(new GradientStop(WalnutMid, 0.4));
            grad.GradientStops.Add(new GradientStop(WalnutMid, 0.6));
            grad.GradientStops.Add(new GradientStop(WalnutRim, 1));
            grad.Freeze();
            var bar = new Rectangle
            {
                Width = w, Height = botY - topY,
                Fill = grad,
                Stroke = new SolidColorBrush(WalnutRim),
                StrokeThickness = 0.8,
                RadiusX = 2, RadiusY = 2,
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(bar, leftX); Canvas.SetTop(bar, topY);
            _root.Children.Add(bar);
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
