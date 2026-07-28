using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using MusicTracker.Engine.Flow;

namespace MusicTracker.Controls.TimelineEditor
{
    /// <summary>
    /// La roue polyrythmique : un anneau par calque, découpé en N secteurs. Les pas vides sont gris, les K coups
    /// prennent la couleur du calque. Une aiguille tourne pendant la lecture.
    ///
    /// C'est la représentation canonique des rythmes euclidiens, et elle montre d'un coup d'œil ce que les nombres
    /// cachent : la régularité de la répartition, l'effet du décalage, et surtout le déphasage entre des anneaux
    /// de longueurs différentes — précisément ce qu'on ne peut pas lire sur une grille rectangulaire.
    ///
    /// Générique : sert aussi bien à une batterie polyrythmique (calque = lane, cf. SetModule) qu'à une ligne
    /// mélodique polyrythmique (calque = voix, cf. SetRings) — la géométrie ne connaît que Hits/Steps/Rotation/
    /// StepSlices/Muted/Color, jamais ce qu'un calque représente musicalement.
    /// </summary>
    public class PolyDrumWheel : FrameworkElement
    {
        /// <summary>Description générique d'un anneau : de quoi dessiner son motif euclidien, indépendamment de
        /// ce qu'il représente (une lane de batterie, une voix de ligne mélodique...).</summary>
        public class Ring
        {
            // StepSlices n'existe plus dans le nouveau modèle — un anneau vaut TOUT le cycle du module (Beats
            // temps), redécoupé en Steps parts. Le champ est gardé ici pour la rétro-compat des dessins libres.
            public int Hits, Steps, Rotation, StepSlices;
            public bool Muted;
            public Color Color;
            /// <summary>Motif à afficher. Si non null, il PRIME sur K/N/Rotation — la roue dessine ce qui est
            /// dedans (mode personnalisé : cellules cliquées à la main). Longueur = <see cref="Steps"/>.</summary>
            public bool[] Custom;
            /// <summary>Ce calque est-il éditable au clic ? Contour un peu plus marqué en mode édition.</summary>
            public bool Editable;
        }

        /// <summary>Position dessinée par la roue de la cellule cliquée : (index de l'anneau, index du pas dans le
        /// motif du calque). Renvoie null si le clic tombe hors d'un anneau ou hors d'un anneau éditable.</summary>
        public event Action<int, int> CellClicked;

        List<Ring> rings = new List<Ring>();
        int totalModuleSlices;       // longueur totale du module, en slices — le tour COMPLET que fait l'aiguille
        double totalBeats;           // durée du module en temps, pour la vitesse de l'aiguille
        double cycleBeats;           // PPCM des cycles de tous les calques, affiché au centre
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

        // Cache de la géométrie du dernier rendu : rayon central de chaque anneau + son pas visuel angulaire, pour
        // hit-tester un clic sans redupliquer la logique de OnRender. Recalculé à chaque rendu.
        struct RingGeom { public double Radius, Thick; public int VisualSteps; public double AngleStepDeg; public int PatternSteps; }
        readonly List<RingGeom> geom = new List<RingGeom>();
        Point centreCache;

        public PolyDrumWheel() { ClipToBounds = true; }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);
            if (CellClicked == null || geom.Count == 0) return;
            var p = e.GetPosition(this);
            double dx = p.X - centreCache.X, dy = p.Y - centreCache.Y;
            double r = Math.Sqrt(dx * dx + dy * dy);
            for (int i = 0; i < geom.Count; i++)
            {
                var g = geom[i];
                if (r < g.Radius - g.Thick / 2 || r > g.Radius + g.Thick / 2) continue;
                if (!rings[i].Editable) return;                              // sur un anneau non éditable → on n'avale pas
                double aDeg = Math.Atan2(dy, dx) * 180 / Math.PI + 90;       // 0° = 12h (comme au rendu, -90 => haut)
                if (aDeg < 0) aDeg += 360;
                int visualStep = (int)Math.Floor(aDeg / g.AngleStepDeg);
                if (visualStep < 0 || visualStep >= g.VisualSteps) return;
                int stepInPattern = ((visualStep % g.PatternSteps) + g.PatternSteps) % g.PatternSteps;
                CellClicked(i, stepInPattern);
                e.Handled = true;
                return;
            }
        }

        /// <summary>Batterie polyrythmique : un anneau par lane, coloré par famille d'instrument (DrumColors).</summary>
        public void SetModule(PolyDrumModule m)
        {
            var list = new List<Ring>();
            if (m?.Layers != null)
                foreach (var l in m.Layers)
                    if (l != null) list.Add(new Ring
                    {
                        Hits = l.Hits, Steps = l.Steps, Rotation = l.Rotation, StepSlices = 0,
                        Muted = l.Muted, Color = DrumColors.ForLane(l.Lane),
                        Custom = l.CustomMode ? l.EffectivePattern() : null,
                        Editable = l.CustomMode
                    });
            // Nouveau modèle : la roue montre UN cycle du module (Beats temps) — tous les anneaux bouclent
            // ensemble à la fin du cycle. Chaque anneau est simplement découpé en son propre Steps.
            int beats = PolyDrum.CycleBeats(m);
            int spq = PolyDrum.SpqFor(m);
            SetRings(list, beats * spq, beats, beats);
        }

        /// <summary>Forme générique : n'importe quelle liste de calques euclidiens (ex. une ligne mélodique
        /// polyrythmique, un anneau par voix).</summary>
        public void SetRings(List<Ring> list, int totalModuleSlices, double totalBeats, double cycleBeats)
        {
            rings = list ?? new List<Ring>();
            this.totalModuleSlices = totalModuleSlices;
            this.totalBeats = totalBeats;
            this.cycleBeats = cycleBeats;
            InvalidateVisual();
        }

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
            geom.Clear();
            if (w < 40 || h < 40) return;
            var centre = new Point(w / 2, h / 2);
            centreCache = centre;
            double outer = Math.Min(w, h) / 2 - 14;
            if (outer < 20) return;

            if (rings.Count == 0)
            {
                var ft0 = Text(Localization.Loc.T("AjouteUnCalquePourCommencer"), 12);
                dc.DrawText(ft0, new Point(centre.X - ft0.Width / 2, centre.Y - ft0.Height / 2));
                return;
            }

            // Le premier calque occupe l'anneau EXTÉRIEUR : c'est en général la grosse caisse, la fondation
            // (ou la voix 1, pour une ligne mélodique).
            double ringSpan = outer / Math.Max(1, rings.Count);
            double thick = ringSpan / 2;

            for (int i = 0; i < rings.Count; i++)
            {
                var l = rings[i];
                int n = Math.Max(1, l.Steps);
                // Nouveau modèle : chaque anneau représente EXACTEMENT UN cycle du module, découpé en Steps parts.
                // Plus de « répétitions » du motif à l'intérieur de la roue (StepSlices a disparu).
                int visualSteps = n;
                // On avance d'une épaisseur d'anneau + un petit interstice à chaque calque (pas de tout le
                // ringSpan) : les anneaux restent quasi jointifs même si `thick` est plus fin que le slot alloué.
                const double ringGap = 2;
                double r = outer - i * (thick + ringGap) - thick / 2;
                if (r < thick / 2) break;   // s'arrête seulement si l'anneau déborderait du centre (bord intérieur négatif)

                bool dim = highlight >= 0 && highlight != i;
                double alpha = l.Muted ? 0.20 : (dim ? 0.35 : 1.0);
                var onBrush = new SolidColorBrush(l.Color) { Opacity = alpha };
                var offBrush = new SolidColorBrush(((SolidColorBrush)Empty).Color) { Opacity = alpha * 0.75 };

                var pat = l.Custom != null && l.Custom.Length == n
                    ? l.Custom
                    : EuclideanRhythm.Rotate(EuclideanRhythm.Pattern(l.Hits, n), l.Rotation);

                // Un secteur par pas VISUEL (pas par pas du motif) : à N=32 pas mais un motif qui ne fait qu'un
                // quart du tour, on dessine bien 4×32 secteurs sur le pourtour, colorés selon pat[s % n].
                double step = 360.0 / visualSteps, gap = Math.Min(2.5, step * 0.12);
                for (int s = 0; s < visualSteps; s++)
                {
                    bool on = pat[s % n];
                    double a0 = -90 + s * step + gap / 2, a1 = -90 + (s + 1) * step - gap / 2;
                    dc.DrawGeometry(null, new Pen(on ? onBrush : offBrush, thick) { StartLineCap = PenLineCap.Flat, EndLineCap = PenLineCap.Flat },
                                    Arc(centre, r, a0, a1));
                }

                // Anneau éditable : liseré discret sur toute la couronne, pour signaler "tu peux cliquer ici".
                if (l.Editable && !l.Muted)
                {
                    var edgeBrush = new SolidColorBrush(Color.FromArgb((byte)(alpha * 220), 0xFF, 0xC1, 0x07));
                    dc.DrawEllipse(null, new Pen(edgeBrush, 1), centre, r + thick / 2 + 1, r + thick / 2 + 1);
                    dc.DrawEllipse(null, new Pen(edgeBrush, 1), centre, r - thick / 2 - 1, r - thick / 2 - 1);
                }
                geom.Add(new RingGeom { Radius = r, Thick = thick, VisualSteps = visualSteps, AngleStepDeg = step, PatternSteps = n });

                // Repères de temps : les positions du cycle qui tombent sur un temps. Une cellule k est sur un
                // temps dès que Steps divise k × Beats.
                int beatsInCycle = (int)Math.Round(totalBeats);
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

            // L'aiguille : une seule pour toute la roue, sur le tour du MODULE — les anneaux plus courts bouclent
            // plusieurs fois pendant qu'elle fait un tour, ce qui rend le déphasage visible.
            if (playBeats >= 0 && totalBeats > 0)
            {
                double frac = (playBeats % totalBeats) / totalBeats;
                double a = (-90 + frac * 360) * Math.PI / 180;
                var tip = new Point(centre.X + Math.Cos(a) * (outer + 6), centre.Y + Math.Sin(a) * (outer + 6));
                dc.DrawLine(new Pen(NeedleBrush, 2), centre, tip);
                dc.DrawEllipse(NeedleBrush, null, centre, 3, 3);
            }

            // Le cycle commun, au centre : au bout de combien de temps tous les calques retombent ensemble.
            if (cycleBeats > 0)
            {
                var ft = Text(Localization.Loc.T("Cycle") + " " + Math.Round(cycleBeats, 2), 11);
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
