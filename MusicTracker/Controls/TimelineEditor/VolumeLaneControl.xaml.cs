using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using MusicTracker.Engine.Timeline;

namespace MusicTracker.Controls.TimelineEditor
{
    /// <summary>
    /// The per-track "volume" sub-track: a base-volume line + automation points over time.
    /// Click an empty spot to add a point, drag a point to move it (left/right = beat, up/down = volume),
    /// right-click a point to delete it. Edits mutate the track's <see cref="TimelineTrack.VolumeAutomation"/>
    /// in place and raise <see cref="Changed"/>.
    /// </summary>
    public partial class VolumeLaneControl : UserControl
    {
        const double VMax = 1.5;   // matches the header's base-volume slider max (allows boost up to 150%)
        const double DotR = 5;     // point radius (px)
        const double HitR = 9;     // click tolerance around a point (px)

        TimelineTrack track;
        double pxPerBeat, laneH, laneW;
        VolumePoint dragging;

        /// <summary>Raised after any edit (add / move / delete).</summary>
        public event Action Changed;

        public VolumeLaneControl() { InitializeComponent(); }

        public void Configure(TimelineTrack track, double pxPerBeat, double laneHeight, double width)
        {
            this.track = track;
            this.pxPerBeat = pxPerBeat;
            this.laneH = laneHeight;
            this.laneW = width;
            Height = laneHeight; Width = width;
            canvas.Height = laneHeight; canvas.Width = width;
            Redraw();
        }

        static double Clamp01(double v) => v < 0 ? 0 : (v > 1 ? 1 : v);
        double YForVol(double v) => laneH - Clamp01(v / VMax) * laneH;
        double VolForY(double y) => Math.Max(0, (1 - y / laneH) * VMax);

        void Redraw()
        {
            canvas.Children.Clear();
            if (track == null) return;

            // Base-volume reference line (faint).
            double baseY = YForVol(track.Volume);
            var line = new Rectangle { Width = laneW, Height = 1, Fill = new SolidColorBrush(Color.FromRgb(0x3A, 0x52, 0x42)) };
            Canvas.SetLeft(line, 0); Canvas.SetTop(line, baseY); canvas.Children.Add(line);

            // Automation curve: straight segments through the points (flat at the base before the first
            // point and after the last).
            DrawAutomationCurve();

            foreach (var p in track.VolumeAutomation)
            {
                double x = p.Beat * pxPerBeat, y = YForVol(p.Volume);
                var dot = new Ellipse
                {
                    Width = DotR * 2,
                    Height = DotR * 2,
                    Fill = new SolidColorBrush(Color.FromRgb(0x4F, 0x86, 0xE0)), // blue (a touch brighter than the line)
                    Stroke = new SolidColorBrush(Color.FromRgb(0x10, 0x14, 0x1C)),
                    StrokeThickness = 1,
                    Cursor = Cursors.SizeAll,
                    ToolTip = "Glisser pour déplacer · clic droit pour supprimer",
                };
                Canvas.SetLeft(dot, x - DotR); Canvas.SetTop(dot, y - DotR); canvas.Children.Add(dot);
            }
        }

        // Straight segments through the automation points: a ramp from the track's BASE volume at x=0 to
        // the first point, straight lines between points, then a horizontal tail from the last point to
        // the end. (No spline — it overshot/stretched between two close changes.)
        void DrawAutomationCurve()
        {
            var pts = track.VolumeAutomation
                           .OrderBy(p => p.Beat)
                           .Select(p => new Point(p.Beat * pxPerBeat, YForVol(p.Volume)))
                           .ToList();
            // Always start at the track's BASE volume at x=0.
            double baseY = YForVol(track.Volume);
            var fig = new PathFigure { StartPoint = new Point(0, baseY), IsClosed = false, IsFilled = false };

            if (pts.Count == 0)
            {
                // No automation -> flat at the base volume across the whole lane.
                fig.Segments.Add(new LineSegment(new Point(laneW, baseY), true));
            }
            else
            {
                foreach (var p in pts) fig.Segments.Add(new LineSegment(p, true)); // base -> p0 -> p1 -> ...
                fig.Segments.Add(new LineSegment(new Point(laneW, pts[pts.Count - 1].Y), true)); // flat tail -> end
            }

            var geo = new PathGeometry();
            geo.Figures.Add(fig);
            var path = new Path
            {
                Data = geo,
                Stroke = new SolidColorBrush(Color.FromRgb(0x33, 0x66, 0xCC)), // app accent blue
                StrokeThickness = 1.5,
                IsHitTestVisible = false, // clicks/drags go to the canvas & dots
            };
            canvas.Children.Add(path);
        }

        // Nearest automation point within HitR of pos, or null.
        VolumePoint HitPoint(Point pos)
        {
            VolumePoint best = null;
            double bestD = HitR * HitR;
            foreach (var p in track.VolumeAutomation)
            {
                double dx = p.Beat * pxPerBeat - pos.X, dy = YForVol(p.Volume) - pos.Y;
                double d = dx * dx + dy * dy;
                if (d <= bestD) { bestD = d; best = p; }
            }
            return best;
        }

        private void canvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (track == null) return;
            var pos = e.GetPosition(canvas);
            var hit = HitPoint(pos);
            if (hit != null) { dragging = hit; canvas.CaptureMouse(); } // start moving an existing point
            else
            {
                track.VolumeAutomation.Add(new VolumePoint { Beat = Math.Max(0, pos.X / pxPerBeat), Volume = VolForY(pos.Y) });
                Redraw(); Changed?.Invoke();
            }
        }

        private void canvas_MouseMove(object sender, MouseEventArgs e)
        {
            if (dragging == null) return;
            var pos = e.GetPosition(canvas);
            dragging.Beat = Math.Max(0, pos.X / pxPerBeat);                       // horizontal = time
            dragging.Volume = VolForY(Math.Max(0, Math.Min(laneH, pos.Y)));       // vertical = volume
            Redraw();
        }

        private void canvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (dragging == null) return;
            dragging = null; canvas.ReleaseMouseCapture(); Changed?.Invoke();
        }

        private void canvas_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (track == null) return;
            var hit = HitPoint(e.GetPosition(canvas));
            if (hit != null) { track.VolumeAutomation.Remove(hit); Redraw(); Changed?.Invoke(); e.Handled = true; }
        }
    }
}
