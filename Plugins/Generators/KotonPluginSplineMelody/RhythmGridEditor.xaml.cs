using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using KotonStudio.Library;

namespace KotonPluginSplineMelody
{
    /// <summary>
    /// Éditeur graphique de motif rythmique — même contrôle que celui de l'arpégiateur (copié pour
    /// rester autonome : chaque plugin est un .ksl indépendant sans référence croisée). Voir la doc
    /// de KotonPluginArpeggiator.RhythmGridEditor pour les détails d'interaction (draw / erase /
    /// preview in-place / fusion).
    /// </summary>
    public partial class RhythmGridEditor : UserControl
    {
        const double CellW = 24;
        const double CellH = 28;
        const double LabelW = 32;

        static readonly int[] BeatsOptions = { 1, 2, 3, 4, 5, 6, 7, 8 };
        static readonly int[] SpbOptions   = { 2, 3, 4, 6, 8, 12, 16, 24 };

        readonly List<Note> _notes = new List<Note>();
        int _beats = 2;
        int _spb = 4;

        enum Gesture { None, Draw, Erase }
        Gesture _gesture = Gesture.None;
        int _dragBeat = -1;
        int _dragStartSlice = -1;
        int _dragEndSlice = -1;
        Canvas _dragCanvas;
        Rectangle _dragPreview;

        bool _updating;

        public event Action<KotonRhythm> RhythmChanged;

        public RhythmGridEditor()
        {
            InitializeComponent();

            foreach (var b in BeatsOptions) cboBeats.Items.Add(b);
            foreach (var s in SpbOptions) cboSpb.Items.Add(s);

            cboBeats.SelectionChanged += (_, __) =>
            {
                if (_updating) return;
                if (cboBeats.SelectedItem is int b) { _beats = b; TrimOutOfRange(); Rebuild(); Fire(); }
            };
            cboSpb.SelectionChanged += (_, __) =>
            {
                if (_updating) return;
                if (cboSpb.SelectedItem is int s && s != _spb)
                {
                    RequantizeNotes(_spb, s);
                    _spb = s;
                    TrimOutOfRange();
                    Rebuild();
                    Fire();
                }
            };

            Load(new KotonRhythm { Beats = _beats, SlicesPerBeat = _spb });
        }

        public void Load(KotonRhythm r)
        {
            if (r == null) r = new KotonRhythm();
            _beats = Math.Max(1, r.Beats);
            _spb = Math.Max(1, r.SlicesPerBeat);
            _notes.Clear();
            if (r.StartSlices != null && r.LenSlices != null)
            {
                int n = Math.Min(r.StartSlices.Length, r.LenSlices.Length);
                for (int i = 0; i < n; i++)
                    _notes.Add(new Note { Start = r.StartSlices[i], Len = Math.Max(1, r.LenSlices[i]) });
            }
            SyncCombos();
            Rebuild();
        }

        public KotonRhythm Save()
        {
            _notes.Sort((a, b) => a.Start.CompareTo(b.Start));
            var starts = new int[_notes.Count];
            var lens = new int[_notes.Count];
            for (int i = 0; i < _notes.Count; i++) { starts[i] = _notes[i].Start; lens[i] = _notes[i].Len; }
            return new KotonRhythm
            {
                Beats = _beats,
                SlicesPerBeat = _spb,
                StartSlices = starts,
                LenSlices = lens,
            };
        }

        void SyncCombos()
        {
            _updating = true;
            try
            {
                int bi = Array.IndexOf(BeatsOptions, _beats);
                cboBeats.SelectedIndex = bi >= 0 ? bi : 1;
                int si = Array.IndexOf(SpbOptions, _spb);
                cboSpb.SelectedIndex = si >= 0 ? si : 2;
            }
            finally { _updating = false; }
        }

        void RequantizeNotes(int oldSpb, int newSpb)
        {
            if (oldSpb <= 0 || newSpb <= 0 || oldSpb == newSpb) return;
            foreach (var n in _notes)
            {
                n.Start = (int)Math.Round((double)n.Start * newSpb / oldSpb);
                n.Len   = Math.Max(1, (int)Math.Round((double)n.Len * newSpb / oldSpb));
            }
        }

        void TrimOutOfRange()
        {
            int total = _beats * _spb;
            for (int i = _notes.Count - 1; i >= 0; i--)
            {
                if (_notes[i].Start >= total) _notes.RemoveAt(i);
                else if (_notes[i].Start + _notes[i].Len > total) _notes[i].Len = total - _notes[i].Start;
            }
        }

        void Fire() => RhythmChanged?.Invoke(Save());

        int FindNoteCovering(int slice)
        {
            for (int i = 0; i < _notes.Count; i++)
                if (_notes[i].Start <= slice && slice < _notes[i].Start + _notes[i].Len) return i;
            return -1;
        }

        void EraseSlice(int slice)
        {
            int idx = FindNoteCovering(slice);
            if (idx < 0) return;
            var n = _notes[idx];
            if (n.Start == slice && n.Len == 1) { _notes.RemoveAt(idx); return; }
            if (slice == n.Start) { n.Start += 1; n.Len -= 1; if (n.Len <= 0) _notes.RemoveAt(idx); }
            else if (slice == n.Start + n.Len - 1) { n.Len -= 1; if (n.Len <= 0) _notes.RemoveAt(idx); }
            else
            {
                int oldEnd = n.Start + n.Len;
                n.Len = slice - n.Start;
                _notes.Add(new Note { Start = slice + 1, Len = oldEnd - (slice + 1) });
            }
        }

        void Rebuild()
        {
            beatsPanel.Children.Clear();
            for (int b = 0; b < _beats; b++) beatsPanel.Children.Add(BuildBeatRow(b));
        }

        UIElement BuildBeatRow(int beat)
        {
            var host = new Grid();
            host.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(LabelW) });
            host.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(_spb * CellW) });
            host.RowDefinitions.Add(new RowDefinition { Height = new GridLength(CellH) });

            var lbl = new TextBlock
            {
                Text = "T" + (beat + 1),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = Brushes.LightGray,
                FontSize = 11,
            };
            Grid.SetColumn(lbl, 0);
            host.Children.Add(lbl);

            var canvas = new Canvas
            {
                Width = _spb * CellW,
                Height = CellH,
                Background = Brushes.Transparent,
                Tag = beat,
            };
            for (int s = 0; s < _spb; s++)
            {
                var rect = new Rectangle
                {
                    Width = CellW - 3,
                    Height = CellH - 4,
                    Fill = (s == 0) ? new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x44))
                                    : new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x32)),
                    RadiusX = 2,
                    RadiusY = 2,
                    IsHitTestVisible = false,
                };
                Canvas.SetLeft(rect, s * CellW + 1);
                Canvas.SetTop(rect, 2);
                canvas.Children.Add(rect);
            }

            int beatStart = beat * _spb;
            int beatEnd = beatStart + _spb;
            var tealFill = new SolidColorBrush(Color.FromRgb(0x1F, 0xB6, 0xC3)); tealFill.Freeze();
            var tealBorder = new SolidColorBrush(Color.FromRgb(0x0F, 0x6E, 0x7A)); tealBorder.Freeze();
            foreach (var n in _notes)
            {
                if (n.Start < beatStart || n.Start >= beatEnd) continue;
                int offset = n.Start - beatStart;
                int drawLen = Math.Min(n.Len, _spb - offset);
                var noteRect = new Rectangle
                {
                    Width = drawLen * CellW - 4,
                    Height = CellH - 8,
                    Fill = tealFill,
                    Stroke = tealBorder,
                    StrokeThickness = 1,
                    RadiusX = 3,
                    RadiusY = 3,
                    IsHitTestVisible = false,
                };
                Canvas.SetLeft(noteRect, offset * CellW + 2);
                Canvas.SetTop(noteRect, 4);
                canvas.Children.Add(noteRect);
            }

            canvas.MouseLeftButtonDown += Canvas_MouseDown;
            canvas.MouseMove += Canvas_MouseMove;
            canvas.MouseLeftButtonUp += Canvas_MouseUp;
            canvas.LostMouseCapture += Canvas_LostCapture;

            Grid.SetColumn(canvas, 1);
            host.Children.Add(canvas);

            return host;
        }

        static int SliceFromPos(Canvas c, MouseEventArgs e, int spb)
        {
            var pos = e.GetPosition(c);
            int slice = (int)Math.Floor(pos.X / CellW);
            if (slice < 0) slice = 0;
            if (slice >= spb) slice = spb - 1;
            return slice;
        }

        void Canvas_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (!(sender is Canvas c) || !(c.Tag is int beat)) return;
            int localSlice = SliceFromPos(c, e, _spb);
            int absSlice = beat * _spb + localSlice;

            _dragCanvas = c;
            _dragBeat = beat;
            _dragStartSlice = absSlice;
            _dragEndSlice = absSlice;

            if (FindNoteCovering(absSlice) >= 0)
            {
                _gesture = Gesture.Erase;
                EraseSlice(absSlice);
                Rebuild();
                Fire();
            }
            else
            {
                _gesture = Gesture.Draw;
                var tealPrev = new SolidColorBrush(Color.FromArgb(200, 0x1F, 0xB6, 0xC3)); tealPrev.Freeze();
                var tealBord = new SolidColorBrush(Color.FromRgb(0x0F, 0x6E, 0x7A)); tealBord.Freeze();
                _dragPreview = new Rectangle
                {
                    Height = CellH - 8,
                    Fill = tealPrev,
                    Stroke = tealBord,
                    StrokeThickness = 1,
                    RadiusX = 3,
                    RadiusY = 3,
                    IsHitTestVisible = false,
                };
                UpdatePreviewRect();
                c.Children.Add(_dragPreview);
            }
            c.CaptureMouse();
            e.Handled = true;
        }

        void Canvas_MouseMove(object sender, MouseEventArgs e)
        {
            if (_gesture == Gesture.None || !(sender is Canvas c) || _dragCanvas != c) return;
            if (!(c.Tag is int beat) || beat != _dragBeat) return;
            int localSlice = SliceFromPos(c, e, _spb);
            int absSlice = beat * _spb + localSlice;
            if (absSlice == _dragEndSlice) return;

            if (_gesture == Gesture.Draw)
            {
                _dragEndSlice = absSlice;
                UpdatePreviewRect();
            }
            else if (_gesture == Gesture.Erase)
            {
                int lo = Math.Min(_dragEndSlice, absSlice);
                int hi = Math.Max(_dragEndSlice, absSlice);
                for (int s = lo; s <= hi; s++) EraseSlice(s);
                _dragEndSlice = absSlice;
                Rebuild();
                Fire();
            }
        }

        void Canvas_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (_gesture == Gesture.None) return;
            if (sender is Canvas c) c.ReleaseMouseCapture();
            FinishGesture();
        }

        void Canvas_LostCapture(object sender, MouseEventArgs e)
        {
            if (_gesture != Gesture.None) FinishGesture();
        }

        void FinishGesture()
        {
            if (_gesture == Gesture.Draw)
            {
                int lo = Math.Min(_dragStartSlice, _dragEndSlice);
                int hi = Math.Max(_dragStartSlice, _dragEndSlice);
                for (int i = _notes.Count - 1; i >= 0; i--)
                {
                    var n = _notes[i];
                    int nEnd = n.Start + n.Len - 1;
                    if (n.Start <= hi && nEnd >= lo)
                    {
                        lo = Math.Min(lo, n.Start);
                        hi = Math.Max(hi, nEnd);
                        _notes.RemoveAt(i);
                    }
                }
                _notes.Add(new Note { Start = lo, Len = hi - lo + 1 });
                Rebuild();
                Fire();
            }
            _gesture = Gesture.None;
            _dragCanvas = null;
            _dragPreview = null;
            _dragBeat = -1;
            _dragStartSlice = _dragEndSlice = -1;
        }

        void UpdatePreviewRect()
        {
            if (_dragPreview == null) return;
            int lo = Math.Min(_dragStartSlice, _dragEndSlice);
            int hi = Math.Max(_dragStartSlice, _dragEndSlice);
            int beatStart = _dragBeat * _spb;
            int offset = lo - beatStart;
            int len = hi - lo + 1;
            int drawLen = Math.Min(len, _spb - offset);
            Canvas.SetLeft(_dragPreview, offset * CellW + 2);
            Canvas.SetTop(_dragPreview, 4);
            _dragPreview.Width = Math.Max(1, drawLen * CellW - 4);
        }

        class Note
        {
            public int Start;
            public int Len;
        }
    }
}
