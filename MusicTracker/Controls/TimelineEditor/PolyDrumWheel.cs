using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using MusicTracker.Engine.Flow;

namespace MusicTracker.Controls.TimelineEditor
{
    /// <summary>
    /// La roue polyrythmique : un anneau par calque, découpé en N secteurs. Les pas vides sont gris, les K coups
    /// prennent la couleur de famille de l'instrument. Une aiguille tourne pendant la lecture.
    ///
    /// C'est la représentation canonique des rythmes euclidiens, et elle montre d'un coup d'œil ce que les nombres
    /// cachent : la régularité de la répartition, l'effet du décalage, et surtout le déphasage entre des anneaux
    /// de longueurs différentes — précisément ce qu'on ne peut pas lire sur une grille rectangulaire.
    /// </summary>
    public class PolyDrumWheel : FrameworkElement
    {
        PolyDrumModule module;
        int highlight = -1;          // calque survolé / sélectionné : mis en avant, les autres estompés
        double playBeats = -1;       // position de l'aiguille, en temps depuis le début du module (< 0 = cachée)

        static readonly Brush Empty = new SolidColorBrush(Color.FromRgb(0x3A, 0x3F, 0x45));
        static readonly Brush Grid = new SolidColorBrush(Color.FromRgb(0x2A, 0x2E, 0x33));
        static readonly Brush NeedleBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xC1, 0x07));
        static readonly Brush TextBrush = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99));
        static readonly Typeface Face = new Typeface("Segoe UI");

        static PolyDrumWheel()
        {
            Empty.Freeze(); Grid.Freeze(); NeedleBrush.Freeze(); TextBrush.Freeze();
        }

        public PolyDrumWheel() { ClipToBounds = true; }

        public void SetModule(PolyDrumModule m) { module = m; InvalidateVisual(); }

        public void SetHighlight(int layerIndex)
        {
            if (highlight == layerIndex) return;
            highlight = layerIndex; InvalidateVisual();
        }

        /// <summary>Position de l'aiguille en temps depuis le début du module ; négatif pour la masquer.</summary>
        public void SetPlayhead(double beats)
        {
            if (Math.Abs(playBeats - beats) < 1e-4) return;
            playBeats = beats; InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            if (w < 40 || h < 40) return;
            var centre = new Point(w / 2, h / 2);
            double outer = Math.Min(w, h) / 2 - 14;
            if (outer < 20) return;

            var layers = new List<EuclidLayer>();
            if (module?.Layers != null) foreach (var l in module.Layers) if (l != null) layers.Add(l);

            if (layers.Count == 0)
            {
                var ft0 = Text(Localization.Loc.T("AjouteUnCalquePourCommencer"), 12);
                dc.DrawText(ft0, new Point(centre.X - ft0.Width / 2, centre.Y - ft0.Height / 2));
                return;
            }

            // Le premier calque occupe l'anneau EXTÉRIEUR : c'est en général la grosse caisse, la fondation.
            double ringSpan = outer / Math.Max(1, layers.Count);
            double thick = Math.Min(22, ringSpan * 0.62);

            for (int i = 0; i < layers.Count; i++)
            {
                var l = layers[i];
                int n = Math.Max(1, l.Steps);
                double r = outer - i * ringSpan - thick / 2;
                if (r < thick) break;

                bool dim = highlight >= 0 && highlight != i;
                double alpha = l.Muted ? 0.20 : (dim ? 0.35 : 1.0);
                var col = DrumColors.ForLane(l.Lane);
                var onBrush = new SolidColorBrush(col) { Opacity = alpha };
                var offBrush = new SolidColorBrush(((SolidColorBrush)Empty).Color) { Opacity = alpha * 0.75 };

                var pat = EuclideanRhythm.Rotate(EuclideanRhythm.Pattern(l.Hits, n), l.Rotation);

                // Un secteur par pas. On laisse un petit jeu angulaire pour que les pas restent distincts même
                // quand N est grand (à N=16 les secteurs font 22,5°, le jeu évite qu'ils se touchent).
                double step = 360.0 / n, gap = Math.Min(2.5, step * 0.12);
                for (int s = 0; s < n; s++)
                {
                    double a0 = -90 + s * step + gap / 2, a1 = -90 + (s + 1) * step - gap / 2;
                    dc.DrawGeometry(null, new Pen(pat[s] ? onBrush : offBrush, thick) { StartLineCap = PenLineCap.Flat, EndLineCap = PenLineCap.Flat },
                                    Arc(centre, r, a0, a1));
                }

                // Repères de temps : les positions du cycle qui tombent sur un temps. C'est ce qui permet de voir
                // qu'un motif est syncopé, et ce que le décalage fait bouger.
                int spb = DrumPattern.SlicesPerQuarter, stp = Math.Max(1, l.StepSlices);
                for (int s = 0; s < n; s++)
                {
                    if ((s * stp) % spb != 0) continue;
                    double a = (-90 + s * step) * Math.PI / 180;
                    var p0 = new Point(centre.X + Math.Cos(a) * (r + thick / 2 + 1), centre.Y + Math.Sin(a) * (r + thick / 2 + 1));
                    var p1 = new Point(centre.X + Math.Cos(a) * (r + thick / 2 + 5), centre.Y + Math.Sin(a) * (r + thick / 2 + 5));
                    dc.DrawLine(new Pen(Grid, 1), p0, p1);
                }
            }

            // L'aiguille : une seule pour toute la roue, sur le tour du MODULE — les anneaux plus courts bouclent
            // plusieurs fois pendant qu'elle fait un tour, ce qui rend le déphasage visible.
            if (playBeats >= 0)
            {
                double totalBeats = Math.Max(1, module.BeatsPerBar) * Math.Max(1, module.Repeats);
                double frac = (playBeats % totalBeats) / totalBeats;
                double a = (-90 + frac * 360) * Math.PI / 180;
                var tip = new Point(centre.X + Math.Cos(a) * (outer + 6), centre.Y + Math.Sin(a) * (outer + 6));
                dc.DrawLine(new Pen(NeedleBrush, 2), centre, tip);
                dc.DrawEllipse(NeedleBrush, null, centre, 3, 3);
            }

            // Le cycle commun, au centre : au bout de combien de temps tous les calques retombent ensemble.
            double cyc = PolyDrum.CycleBeats(module);
            if (cyc > 0)
            {
                var ft = Text(Localization.Loc.T("Cycle") + " " + Math.Round(cyc, 2), 11);
                dc.DrawText(ft, new Point(centre.X - ft.Width / 2, centre.Y + 8));
            }
        }

        FormattedText Text(string s, double size)
            => new FormattedText(s ?? "", System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                                 Face, size, TextBrush, VisualTreeHelper.GetDpi(this).PixelsPerDip);

        // Arc de cercle entre deux angles (en degrés, 0 = est, sens horaire).
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
