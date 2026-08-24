using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using KotonStudio.Library;

namespace KotonPluginPianoVisualizer
{
    /// <summary>Éditeur clavier 88 touches (A0-C8, MIDI 21-108) surmonté d'une piste de CHUTE type
    /// « piano roll tombant » : chaque note apparaît en haut <c>Anticipation</c> temps avant de
    /// sonner, descend à vitesse constante et TOUCHE le clavier exactement à son attaque. Le
    /// bâtonnet a la longueur de la note : tant qu'elle sonne, il s'enfonce sous le clavier et
    /// disparaît au relâché.
    ///
    /// Synchro : aucune horloge murale — chaque frame lit la tête de lecture AUDIBLE
    /// (<see cref="KotonHost.PlayheadBeat"/>, dérivée des échantillons consommés par le device).
    /// Latence du device, pause, départ au curseur, boucle A-B et carte de tempo sont donc gérés
    /// gratuitement. Fallback horloge murale si l'hôte n'expose pas ce callback.</summary>
    public partial class PianoVisualizerEditor : UserControl, IKotonEditor
    {
        readonly PianoVisualizerPlugin _plugin;
        readonly PianoCanvas _canvas;

        // Le flatten du player émet TOUTES les notes du morceau d'un coup, depuis le thread de
        // rendu : on les empile sous verrou et on ne réveille le thread UI qu'UNE fois par rafale
        // (un BeginInvoke par note ferait des milliers de messages sur un morceau long).
        readonly object _lock = new object();
        List<PianoVisualizerPlugin.StruckEvent> _inbox = new List<PianoVisualizerPlugin.StruckEvent>();
        bool _drainQueued;

        public PianoVisualizerEditor(PianoVisualizerPlugin plugin)
        {
            _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
            InitializeComponent();
            _canvas = new PianoCanvas();
            canvasHost.Content = _canvas;

            leadSlider.Value = _plugin.Lead.Value;
            ApplyLead(_plugin.Lead.Value);
            leadSlider.ValueChanged += (s, e) => { _plugin.Lead.Value = e.NewValue; ApplyLead(e.NewValue); };
            Action<double> onLeadChanged = v => Dispatcher.BeginInvoke(new Action(() =>
            {
                if (Math.Abs(leadSlider.Value - v) > 1e-6) leadSlider.Value = v;
                ApplyLead(v);
            }));
            _plugin.Lead.Changed += onLeadChanged;

            gridCheck.Checked    += (s, e) => _canvas.ShowGrid = true;
            gridCheck.Unchecked  += (s, e) => _canvas.ShowGrid = false;
            namesCheck.Checked   += (s, e) => _canvas.ShowOctaveMarks = true;
            namesCheck.Unchecked += (s, e) => _canvas.ShowOctaveMarks = false;

            _plugin.NoteStruck += OnStruck;
            Action onStart = () => Dispatcher.BeginInvoke(new Action(() => _canvas.OnPlaybackStarted()));
            Action onStop  = () => Dispatcher.BeginInvoke(new Action(() => _canvas.PurgeAll()));
            KotonHost.PlaybackStarted += onStart;
            KotonHost.PlaybackStopped += onStop;
            Unloaded += (s, e) =>
            {
                _plugin.NoteStruck -= OnStruck;
                _plugin.Lead.Changed -= onLeadChanged;
                KotonHost.PlaybackStarted -= onStart;
                KotonHost.PlaybackStopped -= onStop;
                _canvas.StopAnimation();
            };
        }

        void ApplyLead(double beats)
        {
            _canvas.LeadBeats = beats;
            leadValue.Text = beats.ToString("0", CultureInfo.InvariantCulture) + " temps";
        }

        // Thread de rendu audio -> empile ; un seul BeginInvoke par rafale.
        void OnStruck(PianoVisualizerPlugin.StruckEvent ev)
        {
            bool queue;
            lock (_lock)
            {
                _inbox.Add(ev);
                queue = !_drainQueued;
                _drainQueued = true;
            }
            if (queue) Dispatcher.BeginInvoke(new Action(Drain), DispatcherPriority.Background);
        }

        void Drain()
        {
            List<PianoVisualizerPlugin.StruckEvent> batch;
            lock (_lock)
            {
                batch = _inbox;
                _inbox = new List<PianoVisualizerPlugin.StruckEvent>();
                _drainQueued = false;
            }
            _canvas.AddNotes(batch);
        }

        public void OnContextUpdated(KotonRenderContext ctx)
        {
            if (ctx == null) return;
            _canvas.SetContext(ctx.Tempo,
                ctx.TimeSigNum > 0 && ctx.TimeSigDen > 0 ? ctx.TimeSigNum * 4.0 / ctx.TimeSigDen : 4.0);
        }
    }

    /// <summary>Rendu : 5 couches Canvas superposées — fond (colonnes d'octave + ligne de frappe),
    /// grille rythmique défilante, bâtonnets touches blanches, bâtonnets touches noires (au-dessus),
    /// clavier. Les objets visuels sont RÉUTILISÉS d'une frame à l'autre (les touches sont créées
    /// une fois par mise en page, un bâtonnet est créé à son entrée à l'écran et détruit à sa
    /// sortie) — à 60 fps, reconstruire l'arbre visuel entier saccaderait.</summary>
    internal sealed class PianoCanvas : UserControl
    {
        const int MidiLow = 21, MidiHigh = 108;   // A0..C8

        sealed class NoteVis
        {
            public int Midi;
            public byte Vel;
            public double OnBeat, OffBeat;
            public Rectangle Rect;                 // null tant que la note n'est pas à l'écran
        }

        readonly Grid _root = new Grid();
        readonly Canvas _bg    = new Canvas();     // colonnes d'octave + halo/ligne de frappe
        readonly Canvas _grid  = new Canvas();     // lignes de temps / mesure, défilantes
        readonly Canvas _fallW = new Canvas();     // bâtonnets sur touches blanches
        readonly Canvas _fallB = new Canvas();     // bâtonnets sur touches noires (dessus)
        readonly Canvas _kb    = new Canvas();     // clavier

        // Notes TRIÉES par beat d'attaque + curseur sur la 1re non encore expirée : la boucle de
        // frame ne balaye que la fenêtre visible, pas le morceau entier.
        readonly List<NoteVis> _notes = new List<NoteVis>();
        int _head;
        bool _sortDirty;
        double _lastBeat = double.NegativeInfinity;

        readonly List<Line> _gridPool = new List<Line>();
        readonly List<NoteVis> _live = new List<NoteVis>();   // celles qui ont un Rect attaché

        readonly double[] _keyX = new double[128];
        readonly double[] _keyW = new double[128];
        readonly Rectangle[] _keyRect = new Rectangle[128];
        readonly byte[] _active = new byte[128];   // vélocité de la note qui sonne, 0 = touche au repos
        readonly byte[] _shown  = new byte[128];   // dernier état poussé dans les Fill (évite de re-brosser)

        DispatcherTimer _timer;
        double _fallH, _kbH;
        double _tempo = 120.0, _barBeats = 4.0;
        DateTime? _wallStart;                      // fallback si l'hôte n'expose pas la tête de lecture

        double _lead = 4.0;
        public double LeadBeats
        {
            get { return _lead; }
            set
            {
                double v = value < 0.25 ? 0.25 : value;
                if (Math.Abs(v - _lead) < 1e-9) return;
                _lead = v;
                DetachAll();                       // l'échelle change : on repart propre
            }
        }

        bool _showGrid = true;
        public bool ShowGrid
        {
            get { return _showGrid; }
            set { _showGrid = value; if (!value) foreach (var l in _gridPool) l.Visibility = Visibility.Collapsed; }
        }

        bool _showOctaveMarks = true;
        public bool ShowOctaveMarks
        {
            get { return _showOctaveMarks; }
            set { if (_showOctaveMarks == value) return; _showOctaveMarks = value; Layout(); }
        }

        // ---- brosses (figées : partagées par tous les visuels) --------------------------------
        static readonly Brush GroundBrush = Frozen(Color.FromRgb(0x10, 0x14, 0x18));
        static readonly Brush WhiteKey    = Frozen(Color.FromRgb(0xF4, 0xEE, 0xE0));
        static readonly Brush BlackKey    = Frozen(Color.FromRgb(0x1A, 0x1A, 0x1E));
        static readonly Brush WhiteRim    = Frozen(Color.FromRgb(0x88, 0x82, 0x76));
        static readonly Brush BlackRim    = Frozen(Color.FromRgb(0x36, 0x36, 0x3C));
        static readonly Brush OctaveLine  = Frozen(Color.FromArgb(0x30, 0x9A, 0xE6, 0xF0));
        static readonly Brush BeatLine    = Frozen(Color.FromArgb(0x1C, 0xD0, 0xE8, 0xF0));
        static readonly Brush BarLine     = Frozen(Color.FromArgb(0x48, 0x9A, 0xE6, 0xF0));
        static readonly Brush HitLine     = Frozen(Color.FromArgb(0xCC, 0x1F, 0xB6, 0xC3));
        static readonly Brush BarStrokeW  = Frozen(Color.FromArgb(0xE0, 0x9E, 0xF4, 0xFC));
        static readonly Brush BarStrokeB  = Frozen(Color.FromArgb(0xC0, 0x4F, 0xCF, 0xDC));
        static readonly Brush LabelBrush  = Frozen(Color.FromArgb(0xA0, 0x00, 0x00, 0x00));

        static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

        public PianoCanvas()
        {
            _root.Background = GroundBrush;
            _root.ClipToBounds = true;
            _root.Children.Add(_bg);
            _root.Children.Add(_grid);
            _root.Children.Add(_fallW);
            _root.Children.Add(_fallB);
            _root.Children.Add(_kb);
            Content = _root;
            Loaded += (s, e) => { Layout(); EnsureTimer(); };
            SizeChanged += (s, e) => Layout();
        }

        public void SetContext(double tempo, double barBeats)
        {
            if (tempo > 0) _tempo = tempo;
            if (barBeats > 0) _barBeats = barBeats;
        }

        void EnsureTimer()
        {
            if (_timer != null) return;
            _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
            _timer.Tick += (s, e) => Tick();
            _timer.Start();
        }
        public void StopAnimation() { if (_timer != null) { _timer.Stop(); _timer = null; } }

        public void AddNotes(List<PianoVisualizerPlugin.StruckEvent> batch)
        {
            if (batch == null || batch.Count == 0) return;
            foreach (var ev in batch)
            {
                if (ev.Midi < MidiLow || ev.Midi > MidiHigh) continue;
                if (ev.Tempo > 0) _tempo = ev.Tempo;
                if (ev.BarBeats > 0) _barBeats = ev.BarBeats;
                _notes.Add(new NoteVis
                {
                    Midi = ev.Midi,
                    Vel = (byte)Math.Max(1, Math.Min(127, ev.Velocity)),
                    OnBeat = ev.AbsoluteStartBeat,
                    OffBeat = ev.AbsoluteStartBeat + Math.Max(0.03, ev.DurationBeats),
                });
            }
            _sortDirty = true;
            EnsureTimer();
        }

        /// <summary>Le device audio démarre : on prend un référentiel horloge murale AU CAS OÙ
        /// l'hôte n'expose pas <see cref="KotonHost.PlayheadBeat"/> (le chemin nominal l'ignore).</summary>
        public void OnPlaybackStarted()
        {
            _wallStart = DateTime.UtcNow;
            _lastBeat = double.NegativeInfinity;
            EnsureTimer();
        }

        /// <summary>Stop / pause / fin de morceau : la timeline sera remise à plat au prochain Play
        /// (toutes les notes seront ré-émises), donc on repart d'une liste vide — sinon chaque Play
        /// empilerait un doublon de tout le morceau.</summary>
        public void PurgeAll()
        {
            DetachAll();
            _notes.Clear();
            _head = 0;
            _sortDirty = false;
            _wallStart = null;
            _lastBeat = double.NegativeInfinity;
            Array.Clear(_active, 0, _active.Length);
            PushKeyColors();
            foreach (var l in _gridPool) l.Visibility = Visibility.Collapsed;
        }

        void DetachAll()
        {
            foreach (var n in _live) n.Rect = null;
            _live.Clear();
            _fallW.Children.Clear();
            _fallB.Children.Clear();
        }

        // ---- mise en page ---------------------------------------------------------------------

        static bool IsBlack(int midi)
        {
            int pc = ((midi % 12) + 12) % 12;
            return pc == 1 || pc == 3 || pc == 6 || pc == 8 || pc == 10;
        }

        void Layout()
        {
            double w = ActualWidth, h = ActualHeight;
            _kb.Children.Clear();
            _bg.Children.Clear();
            _grid.Children.Clear();
            _gridPool.Clear();
            Array.Clear(_keyRect, 0, _keyRect.Length);
            Array.Clear(_shown, 0, _shown.Length);
            DetachAll();
            if (w < 40 || h < 40) { _fallH = _kbH = 0; return; }

            // Le clavier garde une taille lisible mais ne mange jamais la piste de chute.
            _kbH = Math.Max(52, Math.Min(120, h * 0.28));
            _fallH = h - _kbH;

            int whiteCount = 0;
            for (int m = MidiLow; m <= MidiHigh; m++) if (!IsBlack(m)) whiteCount++;
            double whiteW = w / whiteCount;
            double blackW = whiteW * 0.62;

            int wi = 0;
            for (int m = MidiLow; m <= MidiHigh; m++)
            {
                if (IsBlack(m)) continue;
                _keyX[m] = wi * whiteW;
                _keyW[m] = whiteW;
                wi++;
            }
            for (int m = MidiLow; m <= MidiHigh; m++)
            {
                if (!IsBlack(m)) continue;
                // La noire se pose aux 3/4 de la blanche qui la précède (convention piano).
                double xLeft = _keyX[m - 1];
                _keyX[m] = xLeft + whiteW * 0.75 - blackW / 2;
                _keyW[m] = blackW;
            }

            // Fond : colonnes d'octave (repère de tessiture dans la zone de chute).
            if (_showOctaveMarks)
            {
                for (int m = MidiLow; m <= MidiHigh; m++)
                {
                    if ((m % 12) != 0) continue;   // chaque Do
                    var sep = new Rectangle { Width = 1, Height = _fallH, Fill = OctaveLine, IsHitTestVisible = false };
                    Canvas.SetLeft(sep, Math.Round(_keyX[m])); Canvas.SetTop(sep, 0);
                    _bg.Children.Add(sep);
                }
            }
            // Halo + ligne de frappe : la note « atterrit » exactement ici.
            double glowH = Math.Min(26, _fallH * 0.2);
            var glow = new Rectangle
            {
                Width = w, Height = glowH, IsHitTestVisible = false,
                Fill = new LinearGradientBrush(Color.FromArgb(0x00, 0x1F, 0xB6, 0xC3),
                                               Color.FromArgb(0x38, 0x1F, 0xB6, 0xC3), 90),
            };
            Canvas.SetLeft(glow, 0); Canvas.SetTop(glow, _fallH - glowH);
            _bg.Children.Add(glow);
            var hit = new Rectangle { Width = w, Height = 2, Fill = HitLine, IsHitTestVisible = false };
            Canvas.SetLeft(hit, 0); Canvas.SetTop(hit, _fallH - 1);
            _bg.Children.Add(hit);

            // Clavier : blanches d'abord (fond), puis noires par-dessus.
            for (int m = MidiLow; m <= MidiHigh; m++)
            {
                if (IsBlack(m)) continue;
                var r = new Rectangle
                {
                    Width = Math.Max(1, whiteW - 1), Height = Math.Max(1, _kbH - 2),
                    Fill = WhiteKey, Stroke = WhiteRim, StrokeThickness = 0.5,
                    RadiusX = 2, RadiusY = 2, IsHitTestVisible = false,
                };
                Canvas.SetLeft(r, _keyX[m] + 0.5); Canvas.SetTop(r, _fallH + 1);
                _kb.Children.Add(r);
                _keyRect[m] = r;

                if (_showOctaveMarks && (m % 12) == 0 && whiteW > 11)
                {
                    var lbl = new TextBlock
                    {
                        Text = "C" + (m / 12 - 1), FontSize = 9, Foreground = LabelBrush, IsHitTestVisible = false,
                    };
                    Canvas.SetLeft(lbl, _keyX[m] + 2); Canvas.SetTop(lbl, _fallH + _kbH - 14);
                    _kb.Children.Add(lbl);
                }
            }
            for (int m = MidiLow; m <= MidiHigh; m++)
            {
                if (!IsBlack(m)) continue;
                var r = new Rectangle
                {
                    Width = blackW, Height = Math.Max(1, _kbH * 0.62),
                    Fill = BlackKey, Stroke = BlackRim, StrokeThickness = 0.5,
                    RadiusX = 2, RadiusY = 2, IsHitTestVisible = false,
                };
                Canvas.SetLeft(r, _keyX[m]); Canvas.SetTop(r, _fallH);
                _kb.Children.Add(r);
                _keyRect[m] = r;
            }
            PushKeyColors();
        }

        // ---- frame ------------------------------------------------------------------------------

        void Tick()
        {
            if (_fallH < 4) return;

            double? head = null;
            try { var f = KotonHost.PlayheadBeat; if (f != null) head = f(); } catch { }
            double nowBeat;
            if (head.HasValue) nowBeat = head.Value;
            else if (_wallStart.HasValue) nowBeat = (DateTime.UtcNow - _wallStart.Value).TotalSeconds * _tempo / 60.0;
            else return;                            // rien ne joue : on fige l'image telle quelle

            if (_sortDirty)
            {
                _notes.Sort((a, b) => a.OnBeat.CompareTo(b.OnBeat));
                _sortDirty = false;
                _head = 0;
            }
            // Retour en arrière (boucle A-B, redépart au curseur) : les notes déjà dépassées
            // doivent redevenir éligibles.
            if (nowBeat < _lastBeat - 1e-6) { _head = 0; DetachAll(); }
            _lastBeat = nowBeat;

            while (_head < _notes.Count && _notes[_head].OffBeat <= nowBeat)
            {
                Detach(_notes[_head]);
                _head++;
            }

            Array.Clear(_active, 0, _active.Length);
            double pxPerBeat = _fallH / _lead;
            for (int i = _head; i < _notes.Count; i++)
            {
                var n = _notes[i];
                double dOn = n.OnBeat - nowBeat;
                if (dOn > _lead) break;             // liste triée : tout le reste est encore hors champ
                double dOff = n.OffBeat - nowBeat;
                if (dOff <= 0) { Detach(n); continue; }   // note longue déjà finie que _head n'a pas pu dépasser

                // Le BAS du bâtonnet touche la ligne de frappe à l'attaque ; le HAUT la touche au
                // relâché. Entre les deux, la note « rentre » dans la touche et raccourcit.
                double top = _fallH - dOff * pxPerBeat;
                double bottom = _fallH - dOn * pxPerBeat;
                double vTop = top < 0 ? 0 : top;
                double vBot = bottom > _fallH ? _fallH : bottom;
                double vH = vBot - vTop;
                if (vH < 0.75) { Detach(n); continue; }

                var r = Attach(n);
                r.Height = vH;
                Canvas.SetTop(r, vTop);
                if (dOn <= 0) _active[n.Midi] = n.Vel;
            }

            PushKeyColors();
            UpdateGrid(nowBeat);
        }

        Rectangle Attach(NoteVis n)
        {
            if (n.Rect != null) return n.Rect;
            bool black = IsBlack(n.Midi);
            double w = Math.Max(2, _keyW[n.Midi] - (black ? 1 : 2));
            double t = n.Vel / 127.0;
            var r = new Rectangle
            {
                Width = w,
                Fill = BarBrush(black, t),
                Stroke = black ? BarStrokeB : BarStrokeW,
                StrokeThickness = 0.7,
                RadiusX = Math.Min(3, w / 3), RadiusY = Math.Min(3, w / 3),
                IsHitTestVisible = false,
                Opacity = 0.55 + 0.45 * t,
            };
            Canvas.SetLeft(r, _keyX[n.Midi] + (black ? 0.5 : 1));
            (black ? _fallB : _fallW).Children.Add(r);
            n.Rect = r;
            _live.Add(n);
            return r;
        }

        void Detach(NoteVis n)
        {
            if (n.Rect == null) return;
            (IsBlack(n.Midi) ? _fallB : _fallW).Children.Remove(n.Rect);
            n.Rect = null;
            _live.Remove(n);
        }

        // Teal Koton : bâtonnet clair sur touche blanche, plus profond sur touche noire — même
        // code couleur que la touche illuminée, pour que l'atterrissage se lise d'un coup d'œil.
        static Brush BarBrush(bool black, double t)
        {
            Color a, b;
            if (black)
            {
                a = Color.FromRgb((byte)(0x14 + 0x28 * t), (byte)(0x8E + 0x30 * t), (byte)(0x9C + 0x28 * t));
                b = Color.FromRgb((byte)(0x0B + 0x18 * t), (byte)(0x5E + 0x22 * t), (byte)(0x69 + 0x1E * t));
            }
            else
            {
                a = Color.FromRgb((byte)(0x3A + 0x40 * t), (byte)(0xD4 + 0x24 * t), (byte)(0xE2 + 0x1C * t));
                b = Color.FromRgb((byte)(0x1F + 0x20 * t), (byte)(0xB6 + 0x22 * t), (byte)(0xC3 + 0x1E * t));
            }
            var g = new LinearGradientBrush(a, b, 90);
            g.Freeze();
            return g;
        }

        void PushKeyColors()
        {
            for (int m = MidiLow; m <= MidiHigh; m++)
            {
                byte v = _active[m];
                if (v == _shown[m]) continue;
                _shown[m] = v;
                var r = _keyRect[m];
                if (r == null) continue;
                bool black = IsBlack(m);
                r.Fill = v == 0 ? (black ? BlackKey : WhiteKey) : ActiveKeyBrush(black, v);
            }
        }

        static Brush ActiveKeyBrush(bool black, byte vel)
        {
            double t = vel / 127.0;
            Color c = black
                ? Color.FromRgb((byte)(0x0F + (0x4A - 0x0F) * t), (byte)(0x7A + (0xC8 - 0x7A) * t), (byte)(0x88 + (0xD0 - 0x88) * t))
                : Color.FromRgb((byte)(0x1F + (0x66 - 0x1F) * t), (byte)(0xB6 + (0xE8 - 0xB6) * t), (byte)(0xC3 + (0xF0 - 0xC3) * t));
            var b = new SolidColorBrush(c); b.Freeze(); return b;
        }

        /// <summary>Grille rythmique : une ligne par temps (mesure = ligne plus marquée) qui descend
        /// avec la musique — sans elle, une nappe de bâtonnets ne donne aucune sensation de pulsation.</summary>
        void UpdateGrid(double nowBeat)
        {
            if (!_showGrid)
            {
                foreach (var l in _gridPool) l.Visibility = Visibility.Collapsed;
                return;
            }
            double bar = _barBeats > 0 ? _barBeats : 4.0;
            double pxPerBeat = _fallH / _lead;
            double w = ActualWidth;
            int used = 0;
            for (double beat = Math.Ceiling(nowBeat - 1e-9); beat <= nowBeat + _lead + 1e-9; beat += 1.0)
            {
                double y = Math.Round(_fallH - (beat - nowBeat) * pxPerBeat) + 0.5;
                bool isBar = Math.Abs(beat / bar - Math.Round(beat / bar)) < 1e-6;
                Line l;
                if (used < _gridPool.Count) l = _gridPool[used];
                else
                {
                    l = new Line { X1 = 0, IsHitTestVisible = false };
                    _gridPool.Add(l);
                    _grid.Children.Add(l);
                }
                l.X2 = w;
                l.Y1 = l.Y2 = y;
                l.Stroke = isBar ? BarLine : BeatLine;
                l.StrokeThickness = isBar ? 1.4 : 0.8;
                l.Visibility = Visibility.Visible;
                used++;
                if (used > 128) break;              // garde-fou (tempo aberrant)
            }
            for (int i = used; i < _gridPool.Count; i++) _gridPool[i].Visibility = Visibility.Collapsed;
        }
    }
}
