using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;

namespace MusicTracker.Controls
{
    /// <summary>
    /// A panel for the 3 hero action buttons that reflows by available width (button unit = widest child):
    ///   • fits 3 → 1 row (natural width, centered)
    ///   • fits 2 → 2 rows: first button full width, the other two side by side
    ///   • fits 1 → 3 rows, each centered at natural width
    ///   • narrower → 3 rows, each stretched to the full width
    /// </summary>
    public class ReflowButtons : Panel
    {
        public double Gap { get; set; } = 12;

        protected override Size MeasureOverride(Size available)
        {
            foreach (UIElement c in InternalChildren) c.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double bw = ButtonWidth();
            int n = InternalChildren.Count;
            double w = double.IsInfinity(available.Width) ? bw * n + Gap * Math.Max(0, n - 1) : available.Width;

            var rects = Layout(w, bw);
            double h = 0, right = 0;
            foreach (var r in rects) { if (r.Bottom > h) h = r.Bottom; if (r.Right > right) right = r.Right; }
            return new Size(double.IsInfinity(available.Width) ? right : available.Width, h);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            var rects = Layout(finalSize.Width, ButtonWidth());
            int i = 0;
            foreach (UIElement c in InternalChildren) c.Arrange(rects[i++]);
            return finalSize;
        }

        double ButtonWidth()
        {
            double bw = 0;
            foreach (UIElement c in InternalChildren) if (c.DesiredSize.Width > bw) bw = c.DesiredSize.Width;
            return bw;
        }

        // Rect per child for a given panel width; re-measures each child at its target width so wrapped heights are right.
        List<Rect> Layout(double w, double bw)
        {
            var kids = InternalChildren;
            int n = kids.Count;
            var rects = new List<Rect>(n);
            if (n == 0) return rects;

            double H(UIElement c, double width) { c.Measure(new Size(width, double.PositiveInfinity)); return c.DesiredSize.Height; }

            if (w >= n * bw + (n - 1) * Gap)                    // 1 row, natural width, centered
            {
                double total = n * bw + (n - 1) * Gap, x = (w - total) / 2, h = 0;
                foreach (UIElement c in kids) h = Math.Max(h, H(c, bw));
                foreach (UIElement c in kids) { rects.Add(new Rect(x, 0, bw, h)); x += bw + Gap; }
            }
            else if (n == 3 && w >= 2 * bw + Gap)               // 2 rows: row0 full width, row1 = two halves
            {
                double half = (w - Gap) / 2;
                double h0 = H(kids[0], w);
                double h1 = Math.Max(H(kids[1], half), H(kids[2], half));
                rects.Add(new Rect(0, 0, w, h0));
                rects.Add(new Rect(0, h0 + Gap, half, h1));
                rects.Add(new Rect(half + Gap, h0 + Gap, half, h1));
            }
            else if (w >= bw)                                   // 3 rows, centered natural width
            {
                double y = 0;
                foreach (UIElement c in kids) { double h = H(c, bw); rects.Add(new Rect((w - bw) / 2, y, bw, h)); y += h + Gap; }
            }
            else                                               // 3 rows, full width
            {
                double y = 0;
                foreach (UIElement c in kids) { double h = H(c, w); rects.Add(new Rect(0, y, w, h)); y += h + Gap; }
            }
            return rects;
        }
    }
}
