using System;
using System.Windows;
using System.Windows.Controls;

namespace MusicTracker.Controls
{
    /// <summary>How a <see cref="ResponsiveWrapPanel"/> spends the extra width once it is at MaxColumns.</summary>
    public enum CapLayoutMode
    {
        /// <summary>Items keep their natural width; the block is centred (wide galleries).</summary>
        Center,
        /// <summary>Items keep their natural width, flush left (stays aligned with the blocks above).</summary>
        Left,
        /// <summary>Items keep sharing the whole width, so they always stay equal AND fill the pane.</summary>
        Stretch,
    }

    /// <summary>
    /// Lays children out in equal cells whose COLUMN COUNT follows the available width: as many columns as fit at
    /// <see cref="ItemWidth"/> (plus <see cref="ItemSpacing"/> between them), capped by <see cref="MaxColumns"/>.
    ///
    /// Below the cap the cells STRETCH to share the whole width — so 2 columns give half-width items, 1 column gives
    /// a full-width item. At the cap the cells keep their natural <see cref="ItemWidth"/> and the block is CENTRED,
    /// which avoids a lone stretched row looking different from the rest.
    ///
    /// A plain WrapPanel cannot do this: it keeps every item at its own width and simply leaves a ragged gap on the
    /// right. Spacing is owned by the panel (not by per-item margins) so the arithmetic stays exact.
    /// </summary>
    public class ResponsiveWrapPanel : Panel
    {
        /// <summary>The item's natural ("full size") width — also the reference used to count columns.</summary>
        public static readonly DependencyProperty ItemWidthProperty = DependencyProperty.Register(
            nameof(ItemWidth), typeof(double), typeof(ResponsiveWrapPanel),
            new FrameworkPropertyMetadata(200.0, FrameworkPropertyMetadataOptions.AffectsMeasure));
        public double ItemWidth { get { return (double)GetValue(ItemWidthProperty); } set { SetValue(ItemWidthProperty, value); } }

        /// <summary>Never lay out more than this many columns, however wide the panel gets.</summary>
        public static readonly DependencyProperty MaxColumnsProperty = DependencyProperty.Register(
            nameof(MaxColumns), typeof(int), typeof(ResponsiveWrapPanel),
            new FrameworkPropertyMetadata(4, FrameworkPropertyMetadataOptions.AffectsMeasure));
        public int MaxColumns { get { return (int)GetValue(MaxColumnsProperty); } set { SetValue(MaxColumnsProperty, value); } }

        /// <summary>What to do with the extra width once <see cref="MaxColumns"/> is reached.</summary>
        public static readonly DependencyProperty CapLayoutProperty = DependencyProperty.Register(
            nameof(CapLayout), typeof(CapLayoutMode), typeof(ResponsiveWrapPanel),
            new FrameworkPropertyMetadata(CapLayoutMode.Center, FrameworkPropertyMetadataOptions.AffectsMeasure));
        public CapLayoutMode CapLayout { get { return (CapLayoutMode)GetValue(CapLayoutProperty); } set { SetValue(CapLayoutProperty, value); } }

        /// <summary>Gap between cells, horizontally and vertically.</summary>
        public static readonly DependencyProperty ItemSpacingProperty = DependencyProperty.Register(
            nameof(ItemSpacing), typeof(double), typeof(ResponsiveWrapPanel),
            new FrameworkPropertyMetadata(12.0, FrameworkPropertyMetadataOptions.AffectsMeasure));
        public double ItemSpacing { get { return (double)GetValue(ItemSpacingProperty); } set { SetValue(ItemSpacingProperty, value); } }

        /// <summary>Columns + cell width for an available width. n cells and (n-1) gaps fit when
        /// available >= n*W + (n-1)*S, i.e. n <= (available + S) / (W + S).</summary>
        void Layout(double available, out int columns, out double cellWidth, out double offsetX)
        {
            double w = Math.Max(1, ItemWidth), s = Math.Max(0, ItemSpacing);
            int cap = Math.Max(1, MaxColumns);
            if (double.IsInfinity(available) || double.IsNaN(available) || available <= 0)
            {                                   // unconstrained (e.g. inside a horizontal scroller): natural size
                columns = cap; cellWidth = w; offsetX = 0; return;
            }
            columns = (int)Math.Floor((available + s) / (w + s));
            columns = Math.Max(1, Math.Min(cap, columns));
            // Below the cap the cells always stretch to share the width; at the cap it depends on CapLayout.
            if (columns < cap || CapLayout == CapLayoutMode.Stretch)
            {
                cellWidth = Math.Max(1, (available - (columns - 1) * s) / columns);
                offsetX = 0;
            }
            else
            {
                cellWidth = w;                                                      // keep the natural size
                offsetX = CapLayout == CapLayoutMode.Center
                    ? Math.Max(0, (available - (columns * w + (columns - 1) * s)) / 2)
                    : 0;
            }
        }

        protected override Size MeasureOverride(Size constraint)
        {
            int cols; double cellW, offX;
            Layout(constraint.Width, out cols, out cellW, out offX);

            double s = Math.Max(0, ItemSpacing);
            double totalH = 0, rowH = 0;
            int col = 0;
            foreach (UIElement child in InternalChildren)
            {
                if (child == null) continue;
                child.Measure(new Size(cellW, double.PositiveInfinity));
                rowH = Math.Max(rowH, child.DesiredSize.Height);
                if (++col == cols) { totalH += rowH + s; rowH = 0; col = 0; }
            }
            if (col > 0) totalH += rowH + s;            // trailing partial row
            if (totalH > 0) totalH -= s;                // no gap after the last row

            double totalW = double.IsInfinity(constraint.Width) ? cols * cellW + (cols - 1) * s : constraint.Width;
            return new Size(Math.Max(0, totalW), Math.Max(0, totalH));
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            int cols; double cellW, offX;
            Layout(finalSize.Width, out cols, out cellW, out offX);

            double s = Math.Max(0, ItemSpacing);
            var children = InternalChildren;
            double y = 0;
            // Two passes per ROW: the row's height is the tallest item in it, and EVERY item is then arranged at that
            // height. So side-by-side cards line up at equal height (e.g. the tip card matches the changelog card)
            // instead of each ending wherever its own content happens to stop.
            for (int i = 0; i < children.Count; i += cols)
            {
                int end = Math.Min(i + cols, children.Count);
                double rowH = 0;
                for (int k = i; k < end; k++)
                    if (children[k] != null) rowH = Math.Max(rowH, children[k].DesiredSize.Height);

                for (int k = i; k < end; k++)
                {
                    var child = children[k];
                    if (child == null) continue;
                    double x = offX + (k - i) * (cellW + s);
                    child.Arrange(new Rect(x, y, cellW, rowH));
                }
                y += rowH + s;
            }
            return finalSize;
        }
    }
}
