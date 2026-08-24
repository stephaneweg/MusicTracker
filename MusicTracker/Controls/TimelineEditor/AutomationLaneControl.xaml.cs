using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using MusicTracker.Engine.Timeline;
using MusicTracker.Localization;

namespace MusicTracker.Controls.TimelineEditor
{
    /// <summary>
    /// Éditeur générique d'une courbe d'automation. Deux natures de lane passent par ce même contrôle :
    /// <list type="bullet">
    /// <item>une <see cref="AutomationLane"/> MIDI (Pan, Expression, Modulation, Sustain, Réverbe, Chorus, Pitch bend) ;</item>
    /// <item>une <see cref="PluginAutomationLane"/> qui pilote un paramètre de plugin Koton (vibrato, cutoff,
    /// taille de réverbe…), dont la courbe est NORMALISÉE 0..1 entre les bornes du paramètre.</item>
    /// </list>
    /// Mêmes gestes que la lane de volume : clic gauche = ajouter un point (ou en attraper un existant),
    /// glisser = déplacer, clic droit = supprimer, double-clic = revenir à la valeur neutre. La plage verticale
    /// dépend du paramètre : unipolaire (0..1) pour la plupart, bipolaire (-1..+1) pour Pan et Pitch bend — la
    /// ligne médiane est alors une référence visible (0 = centre).
    /// </summary>
    public partial class AutomationLaneControl : UserControl
    {
        const double DotR = 4;
        const double HitR = 8;

        // Teinte des lanes de paramètre de plugin : l'accent teal de l'app, distinct de toutes les couleurs de
        // contrôleur MIDI — on voit d'un coup d'œil ce qui pilote un plugin et ce qui pilote le canal MIDI.
        static readonly Color PluginAccent = Color.FromRgb(0x1F, 0xB6, 0xC3);

        PluginAutomationLane plugLane;       // non-null en mode plugin, null en mode MIDI (nature de la lane éditée)
        List<AutomationPoint> points;        // la liste éditée, quelle que soit la nature de la lane
        double pxPerBeat, laneH, laneW;
        AutomationPoint dragging;
        bool bipolar;      // true = -1..+1 (Pan, PitchBend), false = 0..1
        double baseVal;    // valeur tenue avant le premier point (départ de la courbe dessinée)
        double resetVal;   // valeur posée par un double-clic sur un point
        Color accent;
        string label;      // dessiné dans la lane en mode plugin (plusieurs lanes se ressemblent sinon)
        string[] steps;    // paliers d'une lane DISCRÈTE (null = courbe continue) — voir StaccatoSteps

        public event Action Changed;

        public AutomationLaneControl() { InitializeComponent(); }

        public void Configure(TimelineTrack track, AutomationLane lane, double pxPerBeat, double laneHeight, double width)
        {
            this.plugLane = null;
            this.points = lane != null ? lane.Points : null;
            var p = lane != null ? lane.Param : AutomationParam.Expression;
            this.bipolar = IsBipolar(p);
            this.accent = AccentColor(p);
            this.baseVal = bipolar ? 0.0 : (p == AutomationParam.Expression ? 1.0 : 0.0);
            this.resetVal = DefaultValue(p);
            this.label = null;
            this.steps = p == AutomationParam.Staccato ? StaccatoSteps : null;
            ToolTip = LaneLabel(p);
            Apply(pxPerBeat, laneHeight, width);
        }

        /// <summary>Configure le contrôle pour une lane de PARAMÈTRE DE PLUGIN. <paramref name="track"/> vaut
        /// <c>null</c> pour une lane du bus master — le contrôle n'a de toute façon besoin que de la liste de
        /// points, la cible du paramètre est résolue au rendu audio.</summary>
        public void Configure(TimelineTrack track, PluginAutomationLane lane, double pxPerBeat, double laneHeight, double width)
        {
            this.plugLane = lane;
            this.points = lane != null ? lane.Points : null;
            this.bipolar = false;                                     // courbe normalisée 0..1 sur les bornes du paramètre
            this.accent = PluginAccent;
            this.baseVal = lane != null ? Clamp01(lane.DefaultNorm) : 0.0;
            this.resetVal = this.baseVal;                             // double-clic = retour à la valeur réglée dans le plugin
            this.steps = null;
            this.label = Engine.Timeline.Effects.PluginAutomation.Label(lane);
            ToolTip = lane == null ? null
                : label + "  (" + Engine.Timeline.Effects.PluginAutomation.FormatValue(lane, 0)
                        + " … " + Engine.Timeline.Effects.PluginAutomation.FormatValue(lane, 1) + ")";
            Apply(pxPerBeat, laneHeight, width);
        }

        void Apply(double pxPerBeat, double laneHeight, double width)
        {
            this.pxPerBeat = pxPerBeat;
            this.laneH = laneHeight;
            this.laneW = width;
            Height = laneHeight; Width = width;
            canvas.Height = laneHeight; canvas.Width = width;
            Redraw();
        }

        static double Clamp01(double v) => v < 0 ? 0 : (v > 1 ? 1 : v);

        /// <summary>Paliers de la lane Staccato, du plus lent au plus rapide. La lane ne prend que ces
        /// valeurs : entre « une croche » et « un triolet » il n'y a rien à viser, et un taux intermédiaire
        /// décalerait le détaché contre le rythme écrit. Les libellés sont ceux qu'un musicien emploie.</summary>
        public static readonly string[] StaccatoSteps =
            { "sans", "1 temps", "croches", "triolets", "doubles", "1/8 temps", "1/16 temps" };

        /// <summary>Vrai si la lane a un centre (0 au milieu, ±1 aux extrêmes) — Pan et Pitch bend. Faux sinon
        /// (Volume, Expression, Modulation, Sustain, Réverbe, Chorus : 0 en bas, 1 en haut).</summary>
        public static bool IsBipolar(AutomationParam p) => p == AutomationParam.Pan || p == AutomationParam.PitchBend;

        /// <summary>Couleur d'accent de la lane (utilisée aussi comme témoin dans l'en-tête).</summary>
        public static Color AccentColor(AutomationParam p)
        {
            switch (p)
            {
                case AutomationParam.Pan:         return Color.FromRgb(0xE0, 0xA8, 0x4F); // ambre
                case AutomationParam.Expression:  return Color.FromRgb(0x66, 0xCC, 0x88); // vert clair
                case AutomationParam.Modulation:  return Color.FromRgb(0xC7, 0x7C, 0xE0); // violet
                case AutomationParam.Sustain:     return Color.FromRgb(0xD0, 0xC0, 0x60); // olive doré
                case AutomationParam.ReverbSend:  return Color.FromRgb(0x4F, 0xC0, 0xD0); // cyan
                case AutomationParam.ChorusSend:  return Color.FromRgb(0x8C, 0xB0, 0xE0); // bleu pastel
                case AutomationParam.PitchBend:   return Color.FromRgb(0xE0, 0x7F, 0x7F); // corail
                case AutomationParam.Staccato:    return Color.FromRgb(0xE0, 0xB0, 0xD0); // rose poudre
                default:                          return Color.FromRgb(0x33, 0x66, 0xCC); // bleu app (Volume)
            }
        }

        /// <summary>Libellé LOCALISÉ du paramètre — pour les menus, les tooltips, l'affichage éventuel.</summary>
        public static string LaneLabel(AutomationParam p)
        {
            switch (p)
            {
                case AutomationParam.Volume:     return Loc.T("AutomationVolume");
                case AutomationParam.Pan:        return Loc.T("AutomationPan");
                case AutomationParam.Expression: return Loc.T("AutomationExpression");
                case AutomationParam.Modulation: return Loc.T("AutomationModulation");
                case AutomationParam.Sustain:    return Loc.T("AutomationSustain");
                case AutomationParam.ReverbSend: return Loc.T("AutomationReverb");
                case AutomationParam.ChorusSend: return Loc.T("AutomationChorus");
                case AutomationParam.PitchBend:  return Loc.T("AutomationPitchBend");
                case AutomationParam.Staccato:   return Loc.T("AutomationStaccato");
                default: return p.ToString();
            }
        }

        /// <summary>Valeur par défaut à l'endroit où l'utilisateur crée un premier point : le centre pour une lane
        /// bipolaire (0), le maximum pour Expression (1 = jouer à fond, quitte à baisser après), 0 sinon.</summary>
        public static double DefaultValue(AutomationParam p)
        {
            if (IsBipolar(p)) return 0.0;
            if (p == AutomationParam.Expression) return 1.0;
            return 0.0;
        }

        double YForVal(double v)
        {
            if (bipolar)
            {
                if (v < -1) v = -1; else if (v > 1) v = 1;
                return (1 - (v + 1) * 0.5) * laneH;
            }
            if (v < 0) v = 0; else if (v > 1) v = 1;
            return laneH - v * laneH;
        }

        double ValForY(double y)
        {
            double f = 1 - y / laneH;
            if (f < 0) f = 0; else if (f > 1) f = 1;
            // Lane discrète : on aimante sur le palier le plus proche, sinon la souris pose des valeurs
            // intermédiaires que le rendu arrondirait de toute façon — le point dessiné mentirait alors
            // sur ce qu'on entend.
            if (steps != null && steps.Length > 1)
                return Math.Round(f * (steps.Length - 1)) / (steps.Length - 1);
            return bipolar ? f * 2 - 1 : f;
        }

        /// <summary>Libellé du palier le plus proche d'une valeur de lane discrète.</summary>
        string StepLabel(double v)
        {
            int i = (int)Math.Round(Math.Max(0, Math.Min(1, v)) * (steps.Length - 1));
            return steps[i];
        }

        void Redraw()
        {
            canvas.Children.Clear();
            if (points == null) return;

            // Ligne médiane (0) pour les lanes bipolaires — repère visuel du centre.
            if (bipolar)
            {
                double y = YForVal(0);
                var mid = new Rectangle { Width = laneW, Height = 1, Fill = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x42)) };
                Canvas.SetLeft(mid, 0); Canvas.SetTop(mid, y); canvas.Children.Add(mid);
            }

            // Repères des paliers d'une lane discrète : sans eux on ne sait pas où viser, et l'aimantation
            // donne l'impression que le point « saute » tout seul.
            if (steps != null && steps.Length > 1)
            {
                var tick = new SolidColorBrush(Color.FromArgb(0x40, accent.R, accent.G, accent.B));
                for (int i = 0; i < steps.Length; i++)
                {
                    double y = YForVal(i / (double)(steps.Length - 1));
                    var ln = new Rectangle { Width = laneW, Height = 1, Fill = tick, IsHitTestVisible = false };
                    Canvas.SetLeft(ln, 0); Canvas.SetTop(ln, y); canvas.Children.Add(ln);
                }
            }

            DrawCurve();

            // Étiquette du paramètre piloté : indispensable dès qu'on empile deux lanes de plugin, qui n'ont
            // sinon aucun signe distinctif (même couleur, même plage). Non cliquable pour ne pas gêner l'édition.
            if (!string.IsNullOrEmpty(label))
            {
                var tb = new TextBlock
                {
                    Text = label,
                    FontSize = 9,
                    Foreground = new SolidColorBrush(Color.FromArgb(0xCC, accent.R, accent.G, accent.B)),
                    IsHitTestVisible = false,
                };
                Canvas.SetLeft(tb, 4); Canvas.SetTop(tb, 1); canvas.Children.Add(tb);
            }

            foreach (var p in points)
            {
                double x = p.Beat * pxPerBeat, y = YForVal(p.Value);
                var dot = new Ellipse
                {
                    Width = DotR * 2,
                    Height = DotR * 2,
                    Fill = new SolidColorBrush(accent),
                    Stroke = new SolidColorBrush(Color.FromRgb(0x10, 0x14, 0x1C)),
                    StrokeThickness = 1,
                    Cursor = Cursors.SizeAll,
                    ToolTip = PointTip(p),
                };
                Canvas.SetLeft(dot, x - DotR); Canvas.SetTop(dot, y - DotR); canvas.Children.Add(dot);
            }
        }

        /// <summary>Infobulle d'un point : sur une lane de plugin on montre la VALEUR RÉELLE (avec son unité)
        /// plutôt que la fraction 0..1, seule information utile quand on règle un cutoff ou un temps de réverbe.</summary>
        string PointTip(AutomationPoint p)
        {
            if (steps != null) return StepLabel(p.Value) + "\n" + Loc.T("GlisserPourDeplacerClicDroitPour");
            if (plugLane == null) return Loc.T("GlisserPourDeplacerClicDroitPour");
            return Engine.Timeline.Effects.PluginAutomation.FormatValue(plugLane, p.Value)
                 + "\n" + Loc.T("GlisserPourDeplacerClicDroitPour");
        }

        void DrawCurve()
        {
            var pts = points.OrderBy(p => p.Beat)
                        .Select(p => new Point(p.Beat * pxPerBeat, YForVal(p.Value)))
                        .ToList();
            double baseY = YForVal(baseVal);
            var fig = new PathFigure { StartPoint = new Point(0, baseY), IsClosed = false, IsFilled = false };
            if (pts.Count == 0)
                fig.Segments.Add(new LineSegment(new Point(laneW, baseY), true));
            else
            {
                foreach (var p in pts) fig.Segments.Add(new LineSegment(p, true));
                fig.Segments.Add(new LineSegment(new Point(laneW, pts[pts.Count - 1].Y), true));
            }
            var geo = new PathGeometry(); geo.Figures.Add(fig);
            canvas.Children.Add(new Path
            {
                Data = geo,
                Stroke = new SolidColorBrush(accent),
                StrokeThickness = 1.5,
                IsHitTestVisible = false,
            });
        }

        AutomationPoint HitPoint(Point pos)
        {
            AutomationPoint best = null;
            double bestD = HitR * HitR;
            foreach (var p in points)
            {
                double dx = p.Beat * pxPerBeat - pos.X, dy = YForVal(p.Value) - pos.Y;
                double d = dx * dx + dy * dy;
                if (d <= bestD) { bestD = d; best = p; }
            }
            return best;
        }

        private void canvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (points == null) return;
            var pos = e.GetPosition(canvas);
            var hit = HitPoint(pos);
            // Double-clic sur un point = ramener la valeur à la position neutre (0 pour la plupart, 1 pour
            // Expression, 0 = centre pour Pan/PitchBend, valeur réglée dans le plugin pour une lane de plugin).
            // Pratique quand on cherche « la ligne du milieu / le max » sans y arriver précisément à la souris.
            if (e.ClickCount == 2 && hit != null)
            {
                hit.Value = resetVal;
                Redraw(); Changed?.Invoke();
                e.Handled = true;
                return;
            }
            if (hit != null) { dragging = hit; canvas.CaptureMouse(); }
            else
            {
                points.Add(new AutomationPoint { Beat = Math.Max(0, pos.X / pxPerBeat), Value = ValForY(pos.Y) });
                Redraw(); Changed?.Invoke();
            }
        }

        private void canvas_MouseMove(object sender, MouseEventArgs e)
        {
            if (dragging == null) return;
            var pos = e.GetPosition(canvas);
            dragging.Beat = Math.Max(0, pos.X / pxPerBeat);
            dragging.Value = ValForY(Math.Max(0, Math.Min(laneH, pos.Y)));
            Redraw();
        }

        private void canvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (dragging == null) return;
            dragging = null; canvas.ReleaseMouseCapture(); Changed?.Invoke();
        }

        private void canvas_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (points == null) return;
            var hit = HitPoint(e.GetPosition(canvas));
            if (hit != null) { points.Remove(hit); Redraw(); Changed?.Invoke(); e.Handled = true; }
        }
    }
}
