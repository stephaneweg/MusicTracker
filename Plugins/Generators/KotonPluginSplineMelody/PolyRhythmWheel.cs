using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace KotonPluginSplineMelody
{
    /// <summary>
    /// Roue polyrythmique : un anneau par voix, découpé en N secteurs. Les pas vides sont gris,
    /// les K coups prennent la couleur de la voix. L'aiguille (si <see cref="SetPlayhead"/> est
    /// appelée) tourne au rythme du cycle du module. Copie standalone de
    /// <c>MusicTracker.Controls.TimelineEditor.PolyDrumWheel</c> pour garder le plugin isolé —
    /// pas de dépendance sur MusicTracker.dll.
    /// </summary>
    public sealed class PolyRhythmWheel : FrameworkElement
    {
        public sealed class Ring
        {
            public int Hits, Steps, Rotation;
            public Color Color;
            public bool Muted;
        }

        List<Ring> _rings = new List<Ring>();
        double _totalBeats;   // durée du cycle du module en temps (pour vitesse aiguille)
        double _cycleBeats;   // affiché au centre (PPCM des cycles)
        int _highlight = -1;
        double _playBeats = -1;

        static readonly Brush Empty      = new SolidColorBrush(Color.FromRgb(0x3A, 0x3F, 0x45));
        static readonly Brush Grid       = new SolidColorBrush(Color.FromRgb(0x2A, 0x2E, 0x33));
        static readonly Brush NeedleBrush= new SolidColorBrush(Color.FromRgb(0xFF, 0xC1, 0x07));
        static readonly Brush TextBrush  = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99));
        static readonly Typeface Face    = new Typeface("Segoe UI");

        static PolyRhythmWheel()
        {
            Empty.Freeze(); Grid.Freeze(); NeedleBrush.Freeze(); TextBrush.Freeze();
        }

        public PolyRhythmWheel() { ClipToBounds = true; }

        public void SetRings(List<Ring> list, double totalBeats, double cycleBeats)
        {
            _rings = list ?? new List<Ring>();
            _totalBeats = totalBeats;
            _cycleBeats = cycleBeats;
            InvalidateVisual();
        }

        public void SetHighlight(int layerIndex)
        {
            if (_highlight == layerIndex) return;
            _highlight = layerIndex; InvalidateVisual();
        }

        public void SetPlayhead(double beats)
        {
            if (Math.Abs(_playBeats - beats) < 1e-4) return;
            _playBeats = beats; InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            if (w < 40 || h < 40) return;
            var centre = new Point(w / 2, h / 2);
            double outer = Math.Min(w, h) / 2 - 14;
            if (outer < 20) return;

            if (_rings.Count == 0)
            {
                var ft0 = Text("Ajoute une voix pour commencer", 11);
                dc.DrawText(ft0, new Point(centre.X - ft0.Width / 2, centre.Y - ft0.Height / 2));
                return;
            }

            double ringSpan = outer / Math.Max(1, _rings.Count);
            double thick = ringSpan / 2;

            for (int i = 0; i < _rings.Count; i++)
            {
                var l = _rings[i];
                int n = Math.Max(1, l.Steps);
                int visualSteps = n;
                const double ringGap = 2;
                double r = outer - i * (thick + ringGap) - thick / 2;
                if (r < thick / 2) break;

                bool dim = _highlight >= 0 && _highlight != i;
                double alpha = l.Muted ? 0.20 : (dim ? 0.35 : 1.0);
                var onBrush  = new SolidColorBrush(l.Color) { Opacity = alpha };
                var offBrush = new SolidColorBrush(((SolidColorBrush)Empty).Color) { Opacity = alpha * 0.75 };

                var pat = SplineMelodyGenerator.EuclidRotate(SplineMelodyGenerator.EuclidPattern(l.Hits, n), l.Rotation);

                double step = 360.0 / visualSteps, gap = Math.Min(2.5, step * 0.12);
                for (int s = 0; s < visualSteps; s++)
                {
                    bool on = pat[s % n];
                    double a0 = -90 + s * step + gap / 2, a1 = -90 + (s + 1) * step - gap / 2;
                    dc.DrawGeometry(null, new Pen(on ? onBrush : offBrush, thick) { StartLineCap = PenLineCap.Flat, EndLineCap = PenLineCap.Flat },
                                    Arc(centre, r, a0, a1));
                }

                // Repères de temps : positions du cycle qui tombent sur un temps.
                int beatsInCycle = (int)Math.Round(_totalBeats);
                if (beatsInCycle > 0)
                    for (int s = 0; s < visualSteps; s++)
                    {
                        if ((s * beatsInCycle) % n != 0) continue;
                        double a = (-90 + s * step) * Math.PI / 180;
                        var p0 = new Point(centre.X + Math.Cos(a) * (r + thick / 2 + 1), centre.Y + Math.Sin(a) * (r + thick / 2 + 1));
                        var p1 = new Point(centre.X + Math.Cos(a) * (r + thick / 2 + 5), centre.Y + Math.Sin(a) * (r + thick / 2 + 5));
                        dc.DrawLine(new Pen(Grid, 1), p0, p1);
                    }
            }

            if (_playBeats >= 0 && _totalBeats > 0)
            {
                double frac = (_playBeats % _totalBeats) / _totalBeats;
                double a = (-90 + frac * 360) * Math.PI / 180;
                var tip = new Point(centre.X + Math.Cos(a) * (outer + 6), centre.Y + Math.Sin(a) * (outer + 6));
                dc.DrawLine(new Pen(NeedleBrush, 2), centre, tip);
                dc.DrawEllipse(NeedleBrush, null, centre, 3, 3);
            }

            if (_cycleBeats > 0)
            {
                var ft = Text("Cycle " + Math.Round(_cycleBeats, 2), 11);
                dc.DrawText(ft, new Point(centre.X - ft.Width / 2, centre.Y + 8));
            }
        }

        FormattedText Text(string s, double size)
            => new FormattedText(s ?? "", System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                                 Face, size, TextBrush, VisualTreeHelper.GetDpi(this).PixelsPerDip);

        static Geometry Arc(Point c, double r, double a0deg, double a1deg)
        {
            double a0 = a0deg * Math.PI / 180, a1 = a1deg * Math.PI / 180;
            var p0 = new Point(c.X + Math.Cos(a0) * r, c.Y + Math.Sin(a0) * r);
            var p1 = new Point(c.X + Math.Cos(a1) * r, c.Y + Math.Sin(a1) * r);
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                ctx.BeginFigure(p0, false, false);
                ctx.ArcTo(p1, new Size(r, r), 0, (a1deg - a0deg) > 180, SweepDirection.Clockwise, true, false);
            }
            g.Freeze();
            return g;
        }
    }
}
