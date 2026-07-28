using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;

namespace MusicTracker.Controls
{
    /// <summary>
    /// A panel for the hero action buttons that reflows by available width — button unit = widest child.
    /// Généralisé à N boutons (au lieu de 3 en dur) : on calcule combien de colonnes tiennent à la largeur naturelle
    /// et on pose une grille cols × ceil(N/cols). Le nombre de colonnes descend jusqu'à 1 si la fenêtre est étroite,
    /// et chaque cellule s'étale pour remplir la largeur (pas de blanc à droite).
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

        // Rect par enfant pour une largeur donnée. Cas :
        //   • w >= n·bw + (n-1)·Gap  → une seule rangée au NATUREL, centrée.
        //   • sinon on descend le nombre de colonnes jusqu'à ce que ça tienne, et on étale chaque cellule sur la
        //     largeur totale (dernière rangée éventuellement incomplète : items alignés à gauche, laissant la place).
        //   • cols == 1 → chaque bouton sur sa ligne, pleine largeur (comportement "narrower").
        List<Rect> Layout(double w, double bw)
        {
            var kids = InternalChildren;
            int n = kids.Count;
            var rects = new List<Rect>(n);
            if (n == 0) return rects;

            double H(UIElement c, double width) { c.Measure(new Size(width, double.PositiveInfinity)); return c.DesiredSize.Height; }

            // 1 rangée au naturel si tout tient
            if (w >= n * bw + (n - 1) * Gap)
            {
                double total = n * bw + (n - 1) * Gap, x = (w - total) / 2, h = 0;
                foreach (UIElement c in kids) h = Math.Max(h, H(c, bw));
                foreach (UIElement c in kids) { rects.Add(new Rect(x, 0, bw, h)); x += bw + Gap; }
                return rects;
            }

            // Cas général : on calcule le max de colonnes qui tiennent à la largeur ACTUELLE, plafonné à n.
            int cols;
            if (bw <= 0) cols = 1;
            else cols = Math.Max(1, Math.Min(n, (int)Math.Floor((w + Gap) / (bw + Gap))));

            if (cols == 1)
            {
                double y = 0;
                foreach (UIElement c in kids) { double h = H(c, w); rects.Add(new Rect(0, y, w, h)); y += h + Gap; }
                return rects;
            }

            // Grille cols × ceil(n/cols) — cellules ÉTALÉES pour combler la largeur (plus propre qu'un blanc à droite).
            double cellW = (w - Gap * (cols - 1)) / cols;
            int rows = (n + cols - 1) / cols;
            double yy = 0;
            for (int r = 0; r < rows; r++)
            {
                int start = r * cols;
                int cnt = Math.Min(cols, n - start);       // dernière rangée éventuellement moins remplie
                double rowH = 0;
                for (int c = 0; c < cnt; c++) rowH = Math.Max(rowH, H(kids[start + c], cellW));
                for (int c = 0; c < cnt; c++)
                    rects.Add(new Rect(c * (cellW + Gap), yy, cellW, rowH));
                yy += rowH + Gap;
            }
            return rects;
        }
    }
}
