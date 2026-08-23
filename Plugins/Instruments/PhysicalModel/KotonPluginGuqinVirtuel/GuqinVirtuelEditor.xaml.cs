using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using KotonStudio.Library;

namespace KotonPluginGuqinVirtuel
{
    /// <summary>
    /// Éditeur : paramètres à gauche, canvas de visualisation à droite. Le canvas peint la SILHOUETTE
    /// du guqin (corps en bois laqué, forme légèrement conique), 7 cordes serrées horizontalement,
    /// 13 marques hui aux ratios exacts, et un rond de doigté par note active. La corde vibre en
    /// sinusoïde amortie sur la partie qui sonne (finger → bridge pour une note stoppée, corde
    /// entière pour une corde à vide).
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
                sldSustain.Value = GetV("sustain");
                sldDamping.Value = GetV("hf_damping");
                sldBright.Value = GetV("pluck_brightness");
                sldBody.Value = GetV("body_resonance");
                sldVolume.Value = GetV("volume");
                _canvas?.InvalidateRender();
            }
            finally { _updating = false; }
        }

        void OnNoteStruck(GuqinVirtuelPlugin.StruckEvent ev) => Dispatcher.BeginInvoke(new Action(() => _canvas.NoteOn(ev.StringIdx, ev.Position, ev.Midi, ev.Velocity)));
        void OnNoteReleased(int midi) => Dispatcher.BeginInvoke(new Action(() => _canvas.NoteOff(midi)));

        public void OnContextUpdated(KotonRenderContext ctx) { }
    }

    /// <summary>Canvas de visualisation du guqin : silhouette du corps, 7 cordes horizontales,
    /// 13 hui aux ratios exacts, doigtés actifs, et VIBRATION animée des cordes frappées.</summary>
    internal sealed class GuqinCanvas : UserControl
    {
        readonly Canvas _root;
        readonly List<Fingering> _fingerings = new List<Fingering>();
        readonly List<Vibration> _vibrations = new List<Vibration>();

        DispatcherTimer _timer;
        DateTime _tRef;

        // Palette
        static readonly Color[] StringColors =
        {
            Color.FromRgb(0xE0, 0x6A, 0x55),
            Color.FromRgb(0xE0, 0x9C, 0x4A),
            Color.FromRgb(0xE3, 0xC6, 0x3E),
            Color.FromRgb(0x6E, 0xC7, 0x77),
            Color.FromRgb(0x1F, 0xB6, 0xC3),
            Color.FromRgb(0x4C, 0x79, 0xD6),
            Color.FromRgb(0x9E, 0x6F, 0xE0),
        };
        static readonly Color BodyDark  = Color.FromRgb(0x2A, 0x1A, 0x12);   // laque brun sombre
        static readonly Color BodyLight = Color.FromRgb(0x4A, 0x2E, 0x1E);   // reflet plus clair
        static readonly Color BodyEdge  = Color.FromRgb(0x5A, 0x3E, 0x28);   // liseré
        static readonly Color SoundHole = Color.FromRgb(0x0A, 0x06, 0x04);   // trous d'ouïe (long chi / feng zhao)

        struct Fingering
        {
            public int StringIdx;
            public double Position;
            public int Midi;
        }

        sealed class Vibration
        {
            public int StringIdx;
            public double Position;    // 0 = corde à vide
            public double StartAmpPx;
            public double DecayPerSec; // amplitude *= exp(-decay*t)
            public double VisualHz;    // fréquence visuelle (rien à voir avec l'acoustique)
            public double PhaseOffset;
            public DateTime StartTime;
            public bool Alive = true;
        }

        public GuqinCanvas()
        {
            _root = new Canvas
            {
                Background = new SolidColorBrush(Color.FromRgb(0x10, 0x14, 0x18)),
                ClipToBounds = true,
            };
            Content = _root;
            SizeChanged += (s, e) => Render();
            _tRef = DateTime.UtcNow;
        }

        public void InvalidateRender() => Render();

        public void NoteOn(int stringIdx, double position, int midi, float velocity)
        {
            _fingerings.Add(new Fingering { StringIdx = stringIdx, Position = position, Midi = midi });
            _vibrations.Add(new Vibration
            {
                StringIdx = stringIdx,
                Position = position,
                StartAmpPx = 3.5 + velocity * 3.5,   // 3.5..7 px selon vélocité
                DecayPerSec = 1.8,                    // ~1s pour tomber à ~15%
                VisualHz = 9.0 + (6 - stringIdx) * 0.6, // graves = plus lent, aigus = plus rapide
                PhaseOffset = 0,
                StartTime = DateTime.UtcNow,
            });
            EnsureTimer();
            Render();
        }

        public void NoteOff(int midi)
        {
            for (int i = _fingerings.Count - 1; i >= 0; i--)
                if (_fingerings[i].Midi == midi) { _fingerings.RemoveAt(i); break; }
            // Ne PAS tuer la vibration : le release naturel de l'enveloppe DSP dure encore, la corde
            // continue de sonner visuellement. Elle sera nettoyée quand l'amplitude tombe sous le seuil.
            Render();
        }

        void EnsureTimer()
        {
            if (_timer != null) return;
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };   // ~60 fps
            _timer.Tick += (s, e) =>
            {
                double now = (DateTime.UtcNow - _tRef).TotalSeconds; _ = now;
                // Nettoyage des vibrations mortes.
                bool anyAlive = false;
                for (int i = _vibrations.Count - 1; i >= 0; i--)
                {
                    var v = _vibrations[i];
                    double elapsed = (DateTime.UtcNow - v.StartTime).TotalSeconds;
                    double amp = v.StartAmpPx * Math.Exp(-v.DecayPerSec * elapsed);
                    if (amp < 0.4) _vibrations.RemoveAt(i);
                    else anyAlive = true;
                }
                if (!anyAlive) { StopAnimation(); }
                Render();
            };
            _timer.Start();
        }

        public void StopAnimation()
        {
            _timer?.Stop();
            _timer = null;
        }

        // -----------------------------------------------------------------------------------------
        // Rendu
        // -----------------------------------------------------------------------------------------
        const double StringSpacingPx = 11;
        const double BodyExtraTop = 26;   // marge corps au-dessus de la corde 7
        const double BodyExtraBot = 34;   // marge corps sous la corde 1 (plus grande, laisse place aux hui)
        const double MarginX = 32;

        void Render()
        {
            _root.Children.Clear();
            double w = ActualWidth, h = ActualHeight;
            if (w < 40 || h < 40) return;

            double stringSpan = w - 2 * MarginX;
            double stringsHeight = (GuqinModel.StringCount - 1) * StringSpacingPx;
            double topStringY = (h - stringsHeight) / 2;
            double botStringY = topStringY + stringsHeight;

            // ==== Corps du guqin (silhouette) ====
            DrawBody(topStringY - BodyExtraTop, botStringY + BodyExtraBot, MarginX - 12, MarginX + stringSpan + 12);

            // ==== Cordes ====
            for (int s = 0; s < GuqinModel.StringCount; s++)
            {
                double y = StringY(s, topStringY);
                double thickness = 0.9 + (GuqinModel.StringCount - 1 - s) * 0.25;  // grave = plus épais
                DrawString(s, y, MarginX, MarginX + stringSpan, thickness);
                // Label numéro corde à gauche du yueshan
                var lbl = new TextBlock
                {
                    Text = "" + (s + 1),
                    Foreground = new SolidColorBrush(StringColors[s]),
                    FontSize = 10, FontWeight = FontWeights.Bold,
                };
                Canvas.SetLeft(lbl, 8);
                Canvas.SetTop(lbl, y - 8);
                _root.Children.Add(lbl);
            }

            // ==== Marques hui : petits ronds nacre entre la corde 1 (basse) et le bas du corps ====
            var huiFill = new SolidColorBrush(Color.FromRgb(0xF0, 0xE8, 0xD2)); huiFill.Freeze();
            var huiBorder = new SolidColorBrush(Color.FromRgb(0x8A, 0x74, 0x50)); huiBorder.Freeze();
            double huiY = botStringY + 14;
            for (int h2 = 0; h2 < GuqinModel.HuiPositions.Length; h2++)
            {
                double x = MarginX + GuqinModel.HuiPositions[h2] * stringSpan;
                bool center = h2 == 6;
                double d = center ? 8 : 5;
                var e = new Ellipse
                {
                    Width = d, Height = d,
                    Fill = huiFill,
                    Stroke = huiBorder,
                    StrokeThickness = 0.8,
                    IsHitTestVisible = false,
                };
                Canvas.SetLeft(e, x - d / 2);
                Canvas.SetTop(e, huiY - d / 2);
                _root.Children.Add(e);
                var lbl = new TextBlock
                {
                    Text = "" + (h2 + 1),
                    Foreground = new SolidColorBrush(Color.FromRgb(0xB0, 0xA0, 0x88)),
                    FontSize = 8,
                };
                Canvas.SetLeft(lbl, x - 4);
                Canvas.SetTop(lbl, huiY + 6);
                _root.Children.Add(lbl);
            }

            // ==== Doigtés actifs ====
            foreach (var f in _fingerings)
            {
                if (f.StringIdx < 0 || f.StringIdx >= GuqinModel.StringCount) continue;
                double y = StringY(f.StringIdx, topStringY);
                double x;
                if (f.Position <= 1e-6)
                {
                    // Corde à vide → petit rond à côté du yueshan (à gauche)
                    x = MarginX - 12;
                }
                else
                {
                    x = MarginX + f.Position * stringSpan;
                }
                var col = StringColors[f.StringIdx];
                var dot = new Ellipse
                {
                    Width = 10, Height = 10,
                    Fill = new SolidColorBrush(col),
                    Stroke = new SolidColorBrush(Color.FromArgb(200, 0, 0, 0)),
                    StrokeThickness = 1.2,
                    IsHitTestVisible = false,
                };
                Canvas.SetLeft(dot, x - 5);
                Canvas.SetTop(dot, y - 5);
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
            // Silhouette guqin simplifiée : trapèze très légèrement conique (yueshan à gauche = plus
            // large de quelques px que le bridge à droite), coins arrondis. Un liseré clair fait la
            // "table" apparente.
            double taper = 6;   // écart vertical entre les 2 extrémités
            var pts = new PointCollection
            {
                new Point(left,  top - taper/2),
                new Point(right, top + taper/2),
                new Point(right, bottom - taper/2),
                new Point(left,  bottom + taper/2),
            };
            // Fill par un Polygon avec gradient vertical bois foncé -> reflet -> bois foncé.
            var grad = new LinearGradientBrush();
            grad.StartPoint = new Point(0.5, 0);
            grad.EndPoint = new Point(0.5, 1);
            grad.GradientStops.Add(new GradientStop(BodyDark, 0));
            grad.GradientStops.Add(new GradientStop(BodyLight, 0.5));
            grad.GradientStops.Add(new GradientStop(BodyDark, 1));
            grad.Freeze();
            var poly = new Polygon
            {
                Points = pts,
                Fill = grad,
                Stroke = new SolidColorBrush(BodyEdge),
                StrokeThickness = 1.5,
                StrokeLineJoin = PenLineJoin.Round,
                IsHitTestVisible = false,
            };
            _root.Children.Add(poly);

            // Deux "sound holes" hintés : long chi (dragon pool, plus grand, vers le milieu-tête) et
            // feng zhao (phoenix pond, plus petit, vers le tail). Rendus en léger contraste sous
            // les cordes — évocation, pas réalisme (les vrais trous sont sous le dessus).
            double bodyH = bottom - top;
            double bodyW = right - left;
            double poolY = bottom - bodyH * 0.28;
            double pondY = bottom - bodyH * 0.20;
            var poolRect = new Rectangle
            {
                Width = bodyW * 0.18, Height = 6,
                Fill = new SolidColorBrush(SoundHole),
                RadiusX = 3, RadiusY = 3,
                Opacity = 0.55,
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(poolRect, left + bodyW * 0.30);
            Canvas.SetTop(poolRect, poolY);
            _root.Children.Add(poolRect);
            var pondRect = new Rectangle
            {
                Width = bodyW * 0.10, Height = 4,
                Fill = new SolidColorBrush(SoundHole),
                RadiusX = 2, RadiusY = 2,
                Opacity = 0.5,
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(pondRect, left + bodyW * 0.76);
            Canvas.SetTop(pondRect, pondY);
            _root.Children.Add(pondRect);
        }

        void DrawString(int stringIdx, double baseY, double leftX, double rightX, double thickness)
        {
            var brush = new SolidColorBrush(Color.FromRgb(0xB8, 0xB0, 0x9A));  // corde couleur soie/métal
            brush.Freeze();
            var strokeVoice = new SolidColorBrush(StringColors[stringIdx]);
            strokeVoice.Freeze();

            // Cherche s'il y a une vibration active sur cette corde. S'il y en a plusieurs (rare car
            // règle "1 note par corde") on prend la plus grosse amplitude.
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
                // Corde au repos : ligne droite.
                _root.Children.Add(new Line
                {
                    X1 = leftX, Y1 = baseY, X2 = rightX, Y2 = baseY,
                    Stroke = brush,
                    StrokeThickness = thickness,
                    IsHitTestVisible = false,
                });
                return;
            }

            // Segment vibrant : de x_finger à x_bridge (droite). Position = 0 → corde à vide, tout
            // vibre depuis leftX.
            double xFinger = leftX + vib.Position * (rightX - leftX);
            if (vib.Position <= 1e-6) xFinger = leftX;

            // Partie avant le doigt : ligne droite (immobile).
            if (xFinger > leftX + 0.5)
            {
                _root.Children.Add(new Line
                {
                    X1 = leftX, Y1 = baseY, X2 = xFinger, Y2 = baseY,
                    Stroke = brush,
                    StrokeThickness = thickness,
                    IsHitTestVisible = false,
                });
            }

            // Partie vibrante : polyline avec N segments. Déplacement Y = amp * sin(pi*u) *
            // sin(2*pi*f*t + phase). Le facteur sin(pi*u) donne un ventre au milieu et zéro aux 2
            // extrémités (mode fondamental d'une corde vibrant entre 2 supports fixes).
            const int Segments = 40;
            double elapsedRender = (DateTime.UtcNow - vib.StartTime).TotalSeconds;
            double phaseNow = 2 * Math.PI * vib.VisualHz * elapsedRender + vib.PhaseOffset;
            double sinPhase = Math.Sin(phaseNow);
            var pts = new PointCollection(Segments + 1);
            for (int i = 0; i <= Segments; i++)
            {
                double u = i / (double)Segments;
                double x = xFinger + u * (rightX - xFinger);
                double ventre = Math.Sin(Math.PI * u);
                double dy = bestAmp * ventre * sinPhase;
                pts.Add(new Point(x, baseY + dy));
            }
            _root.Children.Add(new Polyline
            {
                Points = pts,
                Stroke = strokeVoice,   // teinte de la voix quand ça vibre — feedback visuel supplémentaire
                StrokeThickness = thickness + 0.5,
                StrokeLineJoin = PenLineJoin.Round,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                IsHitTestVisible = false,
            });
        }
    }
}
