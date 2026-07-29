using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using MusicTracker.Engine.Timeline;

namespace MusicTracker.Controls.TimelineEditor
{
    /// <summary>The section-marker band drawn just above the measure ruler: one teal pennant + name per
    /// marker, snapped to the ruler's barlines. Pure view + gesture detection — the host (TimelineScreen)
    /// owns the model, the undo and the localized strings. 100% code (no XAML): there is no static visual
    /// tree, everything is built from the model on each Configure.</summary>
    public sealed class MarkerLaneControl : Canvas
    {
        static readonly Brush LaneBg = new SolidColorBrush(Color.FromRgb(0x1B, 0x1B, 0x22)); // a touch darker than the ruler (#1F1F28)
        static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(0x1F, 0xB6, 0xC3)); // AccentBrush
        static readonly Brush AccentBright = new SolidColorBrush(Color.FromRgb(0x3B, 0xCE, 0xDA));
        static readonly Brush LabelFg = new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xE4));
        const double DragThreshold = 4;
        const double PennantW = 12;   // pennant width: the label starts after it

        /// <summary>Double-click on empty space; the beat is already snapped to a barline.</summary>
        public event Action<double> CreateRequested;
        /// <summary>Single click on a marker (no drag) — the host moves the play-start point there.</summary>
        public event Action<SectionMarker> MarkerClicked;
        /// <summary>Double-click on a marker — the host opens the rename dialog.</summary>
        public event Action<SectionMarker> MarkerDoubleClicked;
        /// <summary>A drag just passed the threshold — the host captures its undo pre-state (once per drag).</summary>
        public event Action<SectionMarker> DragStarted;
        /// <summary>Drop; the beat is already snapped and clamped by the host's snap callback.</summary>
        public event Action<SectionMarker, double> MarkerDropped;
        /// <summary>Right-click on a marker — the host shows the context menu, anchored on the element.</summary>
        public event Action<SectionMarker, FrameworkElement> ContextRequested;

        double pxPerBeat = 60;
        IList<SectionMarker> markers;                    // borrowed until the NEXT Configure — never cached longer
        Func<double, double> snap = b => b;
        Func<SectionMarker, string> tooltip;

        // drag state
        SectionMarker dragMarker;
        FrameworkElement dragEl;
        Point pressPos;
        double pressLeft;
        bool pressed, dragging;

        public MarkerLaneControl()
        {
            Background = LaneBg;                          // a Canvas without a Background gets NO mouse events
            MouseLeftButtonDown += Lane_MouseLeftButtonDown;
            MouseMove += Lane_MouseMove;
            MouseLeftButtonUp += Lane_MouseLeftButtonUp;
            // No right-click handler on the background on purpose: right-clicking empty space must open nothing.
        }

        /// <param name="snap">beat -> nearest barline beat (already clamped to the piece by the host).</param>
        /// <param name="tooltip">full name + bar number for one marker.</param>
        /// <param name="emptyHint">tooltip of the band itself ("double-click to add a marker").</param>
        public void Configure(double width, double height, double pxPerBeat,
                              IList<SectionMarker> markers,
                              Func<double, double> snap,
                              Func<SectionMarker, string> tooltip,
                              string emptyHint)
        {
            this.pxPerBeat = pxPerBeat > 0 ? pxPerBeat : 60;
            this.markers = markers;
            this.snap = snap ?? (b => b);
            this.tooltip = tooltip;
            Width = Math.Max(0, width);
            Height = Math.Max(0, height);
            ToolTip = string.IsNullOrEmpty(emptyHint) ? null : emptyHint;
            Redraw();
        }

        void Redraw()
        {
            // A redraw during a drag would destroy the element being dragged (and its mouse capture).
            if (dragging) return;
            Children.Clear();
            dragMarker = null; dragEl = null; pressed = false;
            if (markers == null || markers.Count == 0) return;

            var sorted = new List<SectionMarker>(markers);
            sorted.Sort((a, b) => a.Beat.CompareTo(b.Beat));

            for (int i = 0; i < sorted.Count; i++)
            {
                var m = sorted[i];
                if (m == null) continue;
                double x = m.Beat * pxPerBeat;
                if (x >= Width) continue;      // past the end of the piece: still in the model, just off screen (§4.5)

                // Room for the name = up to the next marker (or the end of the band). Math.Max(0,…) is REQUIRED:
                // two markers at the same beat (hand-edited file) would otherwise give a negative Width -> throw.
                double nextX = Width;
                for (int j = i + 1; j < sorted.Count; j++)
                {
                    if (sorted[j] == null) continue;
                    nextX = Math.Min(Width, sorted[j].Beat * pxPerBeat);
                    break;
                }
                double labelW = Math.Max(0, nextX - x - PennantW - 2);

                var el = new Canvas
                {
                    Width = PennantW + labelW,
                    Height = Height,
                    Background = Brushes.Transparent,     // hit-testable over its whole box
                    Cursor = Cursors.Hand,
                    Tag = m,
                };
                if (tooltip != null) el.ToolTip = tooltip(m);

                var line = new Rectangle { Width = 1, Height = Height, Fill = AccentBright };
                SetLeft(line, 0); SetTop(line, 0);
                el.Children.Add(line);

                var flag = new Polygon
                {
                    Points = new PointCollection { new Point(1, 1), new Point(10, 1), new Point(10, 6), new Point(5.5, 11), new Point(1, 6) },
                    Fill = Accent,
                };
                el.Children.Add(flag);

                var txt = new TextBlock
                {
                    Text = m.Name ?? "",
                    FontSize = 10,
                    Foreground = LabelFg,
                    Width = labelW,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    IsHitTestVisible = false,             // clicks belong to the container
                };
                SetLeft(txt, PennantW); SetTop(txt, Math.Max(0, (Height - 13) / 2));
                el.Children.Add(txt);

                el.MouseLeftButtonDown += Marker_MouseLeftButtonDown;
                el.MouseRightButtonUp += Marker_MouseRightButtonUp;
                SetLeft(el, x); SetTop(el, 0);
                Children.Add(el);
            }
        }

        // ---- gestures ----------------------------------------------------------------------------------------

        void Marker_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            var el = sender as FrameworkElement;
            var m = el?.Tag as SectionMarker;
            if (m == null) return;
            e.Handled = true;                             // never let the band's "create here" double-click fire too
            if (e.ClickCount == 2)
            {
                if (pressed) { pressed = false; dragging = false; el.ReleaseMouseCapture(); }
                dragMarker = null; dragEl = null;
                MarkerDoubleClicked?.Invoke(m);
                return;
            }
            pressPos = e.GetPosition(this);
            pressLeft = GetLeft(el); if (double.IsNaN(pressLeft)) pressLeft = 0;
            dragMarker = m; dragEl = el;
            pressed = true; dragging = false;
            el.CaptureMouse();
        }

        void Lane_MouseMove(object sender, MouseEventArgs e)
        {
            if (!pressed || dragEl == null) return;
            double dx = e.GetPosition(this).X - pressPos.X;
            if (!dragging && Math.Abs(dx) > DragThreshold)
            {
                dragging = true;
                Panel.SetZIndex(dragEl, 50);
                DragStarted?.Invoke(dragMarker);
            }
            // Horizontal only, and SNAPPED live (§3.5: it clicks from barline to barline under the mouse) — so the
            // pennant never rests between two bars, and the drop position is already the snapped one.
            if (dragging) SetLeft(dragEl, snap(Math.Max(0, pressLeft + dx) / pxPerBeat) * pxPerBeat);
        }

        void Lane_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!pressed) return;
            pressed = false;
            var el = dragEl; var m = dragMarker;
            dragEl = null; dragMarker = null;
            if (el != null) { el.ReleaseMouseCapture(); Panel.SetZIndex(el, 0); }
            e.Handled = true;
            if (dragging)
            {
                dragging = false;
                double left = el == null ? 0 : GetLeft(el); if (double.IsNaN(left)) left = 0;
                if (m != null) MarkerDropped?.Invoke(m, snap(left / pxPerBeat));
            }
            else if (m != null) MarkerClicked?.Invoke(m);
        }

        void Marker_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            var el = sender as FrameworkElement;
            var m = el?.Tag as SectionMarker;
            if (m == null) return;
            e.Handled = true;
            ContextRequested?.Invoke(m, el);
        }

        // Double-click on EMPTY band space = create a marker at the nearest barline. A click that started on a
        // marker never reaches here (those handlers set e.Handled).
        void Lane_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount != 2) return;
            e.Handled = true;
            CreateRequested?.Invoke(snap(e.GetPosition(this).X / pxPerBeat));
        }
    }
}
