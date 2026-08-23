using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using KotonStudio.Library;

namespace KotonPluginPianoVisualizer
{
    /// <summary>Éditeur clavier 88 touches (A0-C8, MIDI 21-108). Touches s'illuminent au NoteOn
    /// scheduled, s'éteignent au NoteOff. Utilise le même schema PlaybackStarted / AbsoluteBeat
    /// que le Guqin constrainer pour synchroniser l'animation avec l'audio.</summary>
    public partial class PianoVisualizerEditor : UserControl, IKotonEditor
    {
        readonly PianoVisualizerPlugin _plugin;
        readonly PianoCanvas _canvas;

        public PianoVisualizerEditor(PianoVisualizerPlugin plugin)
        {
            _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
            InitializeComponent();
            _canvas = new PianoCanvas();
            canvasHost.Content = _canvas;

            _plugin.NoteStruck += OnStruck;
            _plugin.NoteReleased += OnReleased;
            Action onStart = () => Dispatcher.BeginInvoke(new Action(() => _canvas.OnPlaybackStarted()));
            Action onStop  = () => Dispatcher.BeginInvoke(new Action(() => _canvas.PurgeAll()));
            KotonHost.PlaybackStarted += onStart;
            KotonHost.PlaybackStopped += onStop;
            Unloaded += (s, e) =>
            {
                _plugin.NoteStruck -= OnStruck;
                _plugin.NoteReleased -= OnReleased;
                KotonHost.PlaybackStarted -= onStart;
                KotonHost.PlaybackStopped -= onStop;
                _canvas.StopAnimation();
            };
        }

        void OnStruck(PianoVisualizerPlugin.StruckEvent ev) => Dispatcher.BeginInvoke(new Action(() => _canvas.EnqueueStruck(ev)));
        void OnReleased(PianoVisualizerPlugin.ReleaseEvent ev) => Dispatcher.BeginInvoke(new Action(() => _canvas.EnqueueReleased(ev)));

        public void OnContextUpdated(KotonRenderContext ctx) { }
    }

    internal sealed class PianoCanvas : UserControl
    {
        readonly Canvas _root;
        DispatcherTimer _timer;
        const int MidiLow = 21, MidiHigh = 108;   // A0..C8

        sealed class Pending { public int Midi; public byte Velocity; public DateTime Deadline; }
        readonly List<Pending> _pendingOn = new List<Pending>();
        readonly List<Pending> _pendingOff = new List<Pending>();
        readonly List<PianoVisualizerPlugin.StruckEvent> _bufOn = new List<PianoVisualizerPlugin.StruckEvent>();
        readonly List<PianoVisualizerPlugin.ReleaseEvent> _bufOff = new List<PianoVisualizerPlugin.ReleaseEvent>();
        DateTime? _playbackStart;

        // Multi-set : combien de « note-on » actifs par MIDI (permet le même MIDI joué plusieurs
        // fois superposé sans qu'un release efface toutes les instances).
        readonly Dictionary<int, int> _activeCount = new Dictionary<int, int>();
        readonly Dictionary<int, byte> _lastVelocity = new Dictionary<int, byte>();

        public PianoCanvas()
        {
            _root = new Canvas
            {
                Background = new SolidColorBrush(Color.FromRgb(0x14, 0x18, 0x1C)),
                ClipToBounds = true,
            };
            Content = _root;
            Loaded += (s, e) => EnsureTimer();
            SizeChanged += (s, e) => Render();
        }

        void EnsureTimer()
        {
            if (_timer != null) return;
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
            _timer.Tick += (s, e) => Tick();
            _timer.Start();
        }
        public void StopAnimation() { _timer?.Stop(); _timer = null; }

        public void EnqueueStruck(PianoVisualizerPlugin.StruckEvent ev)
        {
            if (_playbackStart.HasValue) SchedOn(ev);
            else _bufOn.Add(ev);
            EnsureTimer();
        }
        public void EnqueueReleased(PianoVisualizerPlugin.ReleaseEvent ev)
        {
            if (_playbackStart.HasValue) SchedOff(ev);
            else _bufOff.Add(ev);
            EnsureTimer();
        }
        public void OnPlaybackStarted()
        {
            _playbackStart = DateTime.UtcNow;
            foreach (var e in _bufOn) SchedOn(e);
            foreach (var e in _bufOff) SchedOff(e);
            _bufOn.Clear(); _bufOff.Clear();
            EnsureTimer();
        }
        public void PurgeAll()
        {
            _pendingOn.Clear(); _pendingOff.Clear();
            _bufOn.Clear(); _bufOff.Clear();
            _activeCount.Clear(); _lastVelocity.Clear();
            _playbackStart = null;
            StopAnimation();
            Render();
        }

        void SchedOn(PianoVisualizerPlugin.StruckEvent ev)
        {
            double tempo = ev.Tempo > 0 ? ev.Tempo : 120.0;
            double delay = ev.AbsoluteStartBeat * 60.0 / tempo;
            _pendingOn.Add(new Pending { Midi = ev.Midi, Velocity = (byte)Math.Max(1, Math.Min(127, ev.Velocity)),
                Deadline = _playbackStart.Value.AddSeconds(delay) });
        }
        void SchedOff(PianoVisualizerPlugin.ReleaseEvent ev)
        {
            double tempo = ev.Tempo > 0 ? ev.Tempo : 120.0;
            double delay = ev.AbsoluteAtBeat * 60.0 / tempo;
            _pendingOff.Add(new Pending { Midi = ev.Midi, Deadline = _playbackStart.Value.AddSeconds(delay) });
        }

        void Tick()
        {
            var now = DateTime.UtcNow;
            for (int i = _pendingOn.Count - 1; i >= 0; i--)
            {
                if (_pendingOn[i].Deadline > now) continue;
                var p = _pendingOn[i];
                _pendingOn.RemoveAt(i);
                _activeCount[p.Midi] = (_activeCount.TryGetValue(p.Midi, out var c) ? c : 0) + 1;
                _lastVelocity[p.Midi] = p.Velocity;
            }
            for (int i = _pendingOff.Count - 1; i >= 0; i--)
            {
                if (_pendingOff[i].Deadline > now) continue;
                var p = _pendingOff[i];
                _pendingOff.RemoveAt(i);
                if (_activeCount.TryGetValue(p.Midi, out var c))
                {
                    if (c <= 1) { _activeCount.Remove(p.Midi); _lastVelocity.Remove(p.Midi); }
                    else _activeCount[p.Midi] = c - 1;
                }
            }
            Render();
        }

        static bool IsBlack(int midi)
        {
            int pc = ((midi % 12) + 12) % 12;
            return pc == 1 || pc == 3 || pc == 6 || pc == 8 || pc == 10;
        }
        static int CountWhitesInRange(int lo, int hi)
        {
            int c = 0;
            for (int m = lo; m <= hi; m++) if (!IsBlack(m)) c++;
            return c;
        }

        void Render()
        {
            _root.Children.Clear();
            double w = ActualWidth, h = ActualHeight;
            if (w < 40 || h < 40) return;

            int whiteCount = CountWhitesInRange(MidiLow, MidiHigh);
            double whiteW = w / whiteCount;
            double blackW = whiteW * 0.6;
            double whiteH = h;
            double blackH = h * 0.62;

            // Précalcule le X de chaque touche blanche par MIDI (pour poser les noires par ref).
            var whiteX = new Dictionary<int, double>();
            int wi = 0;
            for (int m = MidiLow; m <= MidiHigh; m++)
            {
                if (IsBlack(m)) continue;
                whiteX[m] = wi * whiteW;
                wi++;
            }

            var whiteBrush = new SolidColorBrush(Color.FromRgb(0xF4, 0xEE, 0xE0)); whiteBrush.Freeze();
            var blackBrush = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1E)); blackBrush.Freeze();
            var whiteRim  = new SolidColorBrush(Color.FromRgb(0x88, 0x82, 0x76));  whiteRim.Freeze();
            var blackRim  = new SolidColorBrush(Color.FromRgb(0x36, 0x36, 0x3C));  blackRim.Freeze();

            // Touches BLANCHES d'abord (fond du clavier).
            for (int m = MidiLow; m <= MidiHigh; m++)
            {
                if (IsBlack(m)) continue;
                double x = whiteX[m];
                bool active = _activeCount.TryGetValue(m, out var _c) && _c > 0;
                Brush fill = whiteBrush;
                if (active)
                {
                    _lastVelocity.TryGetValue(m, out var vel);
                    fill = ActiveWhiteBrush(vel);
                }
                var rect = new Rectangle
                {
                    Width = whiteW - 1, Height = whiteH - 2,
                    Fill = fill, Stroke = whiteRim, StrokeThickness = 0.5,
                    RadiusX = 2, RadiusY = 2, IsHitTestVisible = false,
                };
                Canvas.SetLeft(rect, x + 0.5); Canvas.SetTop(rect, 1);
                _root.Children.Add(rect);

                // Petit label "C" sous chaque Do (repère de tessiture).
                if ((m % 12) == 0)
                {
                    int oct = m / 12 - 1;
                    var lbl = new TextBlock
                    {
                        Text = "C" + oct,
                        Foreground = new SolidColorBrush(Color.FromArgb(160, 0, 0, 0)),
                        FontSize = 9,
                    };
                    Canvas.SetLeft(lbl, x + 2); Canvas.SetTop(lbl, whiteH - 14);
                    _root.Children.Add(lbl);
                }
            }
            // Touches NOIRES au-dessus (recouvrent partiellement les blanches à leur base).
            for (int m = MidiLow; m <= MidiHigh; m++)
            {
                if (!IsBlack(m)) continue;
                // Position : le noire se pose entre 2 blanches, centrée légèrement à droite de la
                // blanche précédente (au 3/4 de son largeur, convention piano).
                int prevWhite = m - 1;
                if (!whiteX.TryGetValue(prevWhite, out var xLeft)) continue;
                double xCenter = xLeft + whiteW * 0.75;
                double x = xCenter - blackW / 2;
                bool active = _activeCount.TryGetValue(m, out var _c) && _c > 0;
                Brush fill = blackBrush;
                if (active)
                {
                    _lastVelocity.TryGetValue(m, out var vel);
                    fill = ActiveBlackBrush(vel);
                }
                var rect = new Rectangle
                {
                    Width = blackW, Height = blackH,
                    Fill = fill, Stroke = blackRim, StrokeThickness = 0.5,
                    RadiusX = 2, RadiusY = 2, IsHitTestVisible = false,
                };
                Canvas.SetLeft(rect, x); Canvas.SetTop(rect, 0);
                _root.Children.Add(rect);
            }
        }

        // Teal Koton pour touche blanche active, intensité selon vélocité.
        static Brush ActiveWhiteBrush(byte vel)
        {
            double t = vel / 127.0;
            byte r = (byte)(0x1F + (0x66 - 0x1F) * t);
            byte g = (byte)(0xB6 + (0xE8 - 0xB6) * t);
            byte b = (byte)(0xC3 + (0xF0 - 0xC3) * t);
            return new SolidColorBrush(Color.FromRgb(r, g, b));
        }
        static Brush ActiveBlackBrush(byte vel)
        {
            double t = vel / 127.0;
            byte r = (byte)(0x0F + (0x4A - 0x0F) * t);
            byte g = (byte)(0x7A + (0xC8 - 0x7A) * t);
            byte b = (byte)(0x88 + (0xD0 - 0x88) * t);
            return new SolidColorBrush(Color.FromRgb(r, g, b));
        }
    }
}
