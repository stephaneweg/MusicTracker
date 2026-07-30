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
    /// Éditeur générique d'une <see cref="AutomationLane"/> (Pan, Expression, Modulation, Sustain, Réverbe,
    /// Chorus, Pitch bend). Mêmes gestes que la lane de volume : clic gauche = ajouter un point (ou en attraper
    /// un existant), glisser = déplacer, clic droit = supprimer. La plage verticale dépend du paramètre :
    /// unipolaire (0..1) pour la plupart, bipolaire (-1..+1) pour Pan et Pitch bend — la ligne médiane est
    /// alors une référence visible (0 = centre).
    /// </summary>
    public partial class AutomationLaneControl : UserControl
    {
        const double DotR = 4;
        const double HitR = 8;

        TimelineTrack track;
        AutomationLane lane;
        double pxPerBeat, laneH, laneW;
        AutomationPoint dragging;
        bool bipolar;      // true = -1..+1 (Pan, PitchBend), false = 0..1
        Color accent;

        public event Action Changed;

        public AutomationLaneControl() { InitializeComponent(); }

        public void Configure(TimelineTrack track, AutomationLane lane, double pxPerBeat, double laneHeight, double width)
        {
            this.track = track;
            this.lane = lane;
            this.pxPerBeat = pxPerBeat;
            this.laneH = laneHeight;
            this.laneW = width;
            this.bipolar = IsBipolar(lane != null ? lane.Param : AutomationParam.Expression);
            this.accent = AccentColor(lane != null ? lane.Param : AutomationParam.Expression);
            Height = laneHeight; Width = width;
            canvas.Height = laneHeight; canvas.Width = width;
            ToolTip = LaneLabel(lane != null ? lane.Param : AutomationParam.Expression);
            Redraw();
        }

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
            return bipolar ? f * 2 - 1 : f;
        }

        void Redraw()
        {
            canvas.Children.Clear();
            if (track == null || lane == null) return;

            // Ligne médiane (0) pour les lanes bipolaires — repère visuel du centre.
            if (bipolar)
            {
                double y = YForVal(0);
                var mid = new Rectangle { Width = laneW, Height = 1, Fill = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x42)) };
                Canvas.SetLeft(mid, 0); Canvas.SetTop(mid, y); canvas.Children.Add(mid);
            }

            DrawCurve();

            foreach (var p in lane.Points)
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
                    ToolTip = Loc.T("GlisserPourDeplacerClicDroitPour"),
                };
                Canvas.SetLeft(dot, x - DotR); Canvas.SetTop(dot, y - DotR); canvas.Children.Add(dot);
            }
        }

        void DrawCurve()
        {
            var pts = lane.Points.OrderBy(p => p.Beat)
                        .Select(p => new Point(p.Beat * pxPerBeat, YForVal(p.Value)))
                        .ToList();
            double baseY = YForVal(bipolar ? 0.0 : (lane.Param == AutomationParam.Expression ? 1.0 : 0.0));
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
            foreach (var p in lane.Points)
            {
                double dx = p.Beat * pxPerBeat - pos.X, dy = YForVal(p.Value) - pos.Y;
                double d = dx * dx + dy * dy;
                if (d <= bestD) { bestD = d; best = p; }
            }
            return best;
        }

        private void canvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (track == null || lane == null) return;
            var pos = e.GetPosition(canvas);
            var hit = HitPoint(pos);
            // Double-clic sur un point = ramener la valeur à la position neutre (0 pour la plupart,
            // 1 pour Expression, 0 = centre pour Pan/PitchBend). Pratique quand on cherche « la
            // ligne du milieu / le max » sans y arriver précisément à la souris.
            if (e.ClickCount == 2 && hit != null)
            {
                hit.Value = DefaultValue(lane.Param);
                Redraw(); Changed?.Invoke();
                e.Handled = true;
                return;
            }
            if (hit != null) { dragging = hit; canvas.CaptureMouse(); }
            else
            {
                lane.Points.Add(new AutomationPoint { Beat = Math.Max(0, pos.X / pxPerBeat), Value = ValForY(pos.Y) });
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
            if (track == null || lane == null) return;
            var hit = HitPoint(e.GetPosition(canvas));
            if (hit != null) { lane.Points.Remove(hit); Redraw(); Changed?.Invoke(); e.Handled = true; }
        }
    }
}
