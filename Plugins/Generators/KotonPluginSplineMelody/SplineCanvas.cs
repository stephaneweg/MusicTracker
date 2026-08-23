using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace KotonPluginSplineMelody
{
    /// <summary>
    /// Canvas d'édition d'une (ou plusieurs) spline(s) mélodique(s). Chaque voix est dessinée avec
    /// sa couleur ; la voix ACTIVE est en trait plein + points draggables, les autres en trait
    /// atténué (aperçu contextuel). Le zéro est au milieu vertical.
    ///
    /// **Interactions sur la voix active** :
    ///   - Clic gauche sur une zone libre → AJOUT d'un point à (t, y) puis drag immédiat
    ///   - Clic gauche sur un point existant → START drag du point (déplace T et Y)
    ///   - Clic droit sur un point → SUPPRESSION du point (min. 2 points conservés — sinon la
    ///     spline n'a plus de forme)
    ///
    /// **Coordonnées** :
    ///   - X : 0..1 = début..fin du bloc (mappé sur la largeur du canvas)
    ///   - Y : -yScale..+yScale = ambitus visuel (mappé sur la hauteur, INVERSÉ, haut = positif)
    ///     yScale = 10 par défaut (arbitraire grand) — la valeur Y stockée en modèle n'est PAS
    ///     bornée par le canvas (le moteur normalise par |Y| max de toute façon, seule la FORME
    ///     compte).
    /// </summary>
    public sealed class SplineCanvas : UserControl
    {
        // -----------------------------------------------------------------------------------------
        // Modèle par voix — expose 2 callbacks : GetVoice(int) et OnChanged pour que l'éditeur
        // sache que la spline a bougé et propage la modif au plugin.
        // -----------------------------------------------------------------------------------------
        readonly Func<int, SplineMelodyGenerator.VoiceSpec> _getVoice;
        readonly Func<int> _getActiveIndex;
        readonly Func<int> _getVoiceCount;
        readonly Func<bool> _isSplineMode;   // true = Catmull-Rom (courbes), false = linéaire
        public event Action Changed;

        public SplineCanvas(Func<int, SplineMelodyGenerator.VoiceSpec> getVoice,
                            Func<int> getActiveIndex,
                            Func<int> getVoiceCount,
                            Func<bool> isSplineMode)
        {
            _getVoice = getVoice ?? throw new ArgumentNullException(nameof(getVoice));
            _getActiveIndex = getActiveIndex ?? throw new ArgumentNullException(nameof(getActiveIndex));
            _getVoiceCount = getVoiceCount ?? throw new ArgumentNullException(nameof(getVoiceCount));
            _isSplineMode = isSplineMode ?? throw new ArgumentNullException(nameof(isSplineMode));

            _root = new Canvas
            {
                Background = new SolidColorBrush(Color.FromRgb(0x10, 0x14, 0x18)),
                ClipToBounds = true,
                Focusable = true,
            };
            Content = _root;

            SizeChanged += (s, e) => Redraw();
            _root.MouseLeftButtonDown += OnLeftDown;
            _root.MouseRightButtonDown += OnRightDown;
            _root.MouseMove += OnMove;
            _root.MouseLeftButtonUp += OnLeftUp;
            _root.LostMouseCapture += (s, e) => EndDrag();

            MinHeight = 180;
        }

        readonly Canvas _root;

        const double YScale = 10;     // amplitude visuelle max en Y (unités arbitraires du modèle)
        const double PointRadius = 7; // taille du hit-target des points
        const double VoiceLineActive = 4.0;
        const double VoiceLineInactive = 2.4;
        const byte InactiveAlpha = 90;

        // Drag state
        int _dragVoiceIdx = -1;
        int _dragPointIdx = -1;

        // -----------------------------------------------------------------------------------------
        // Rendu
        // -----------------------------------------------------------------------------------------
        public void Redraw()
        {
            _root.Children.Clear();
            double w = ActualWidth, h = ActualHeight;
            if (w < 4 || h < 4) return;

            DrawGrid(w, h);

            int active = _getActiveIndex();
            int voiceCount = _getVoiceCount();
            // Dessine d'abord les voix INACTIVES (dessous), puis la voix ACTIVE (au-dessus).
            for (int v = 0; v < voiceCount; v++)
            {
                if (v == active) continue;
                DrawVoice(v, false, w, h);
            }
            if (active >= 0 && active < voiceCount) DrawVoice(active, true, w, h);
        }

        void DrawGrid(double w, double h)
        {
            var gridBrush = new SolidColorBrush(Color.FromRgb(0x22, 0x28, 0x2E)); gridBrush.Freeze();
            var zeroBrush = new SolidColorBrush(Color.FromRgb(0x50, 0x60, 0x6A)); zeroBrush.Freeze();

            // Verticales : divisions à 1/4, 1/2, 3/4 (pas trop chargé)
            for (int i = 1; i < 4; i++)
            {
                double x = i * w / 4.0;
                _root.Children.Add(new Line { X1 = x, Y1 = 0, X2 = x, Y2 = h, Stroke = gridBrush, StrokeThickness = 1, IsHitTestVisible = false });
            }
            // Horizontales : lignes légères à +/- yScale/2
            double yPlusHalf = MapY(YScale / 2, h);
            double yMinusHalf = MapY(-YScale / 2, h);
            _root.Children.Add(new Line { X1 = 0, Y1 = yPlusHalf, X2 = w, Y2 = yPlusHalf, Stroke = gridBrush, StrokeThickness = 1, IsHitTestVisible = false });
            _root.Children.Add(new Line { X1 = 0, Y1 = yMinusHalf, X2 = w, Y2 = yMinusHalf, Stroke = gridBrush, StrokeThickness = 1, IsHitTestVisible = false });

            // Ligne du zéro : plus visible
            double y0 = MapY(0, h);
            _root.Children.Add(new Line { X1 = 0, Y1 = y0, X2 = w, Y2 = y0, Stroke = zeroBrush, StrokeThickness = 1.5, IsHitTestVisible = false });
        }

        void DrawVoice(int voiceIdx, bool isActive, double w, double h)
        {
            var spec = _getVoice(voiceIdx);
            if (spec == null || spec.Points.Count == 0) return;

            // Trie une copie par T croissant (le modèle ne le garantit pas après un drag).
            var pts = new List<SplineMelodyGenerator.ControlPoint>(spec.Points);
            pts.Sort((a, b) => a.T.CompareTo(b.T));

            Color baseCol = spec.Color;
            Brush lineBrush;
            if (isActive) { var b = new SolidColorBrush(baseCol); b.Freeze(); lineBrush = b; }
            else { var b = new SolidColorBrush(Color.FromArgb(InactiveAlpha, baseCol.R, baseCol.G, baseCol.B)); b.Freeze(); lineBrush = b; }

            // Le rendu suit le mode d'interpolation choisi (WYSIWYG avec la restitution audio) :
            //   - Linéaire : polyline
            //   - Spline   : chemin Catmull-Rom converti en cubiques Bézier
            double thickness = isActive ? VoiceLineActive : VoiceLineInactive;
            bool spline = _isSplineMode();
            Shape shape;
            if (spline && pts.Count >= 2)
            {
                shape = BuildCatmullRomPath(pts, w, h, lineBrush, thickness);
            }
            else
            {
                var poly = new Polyline
                {
                    Stroke = lineBrush,
                    StrokeThickness = thickness,
                    StrokeLineJoin = PenLineJoin.Round,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                    IsHitTestVisible = false,
                };
                foreach (var p in pts) poly.Points.Add(new Point(MapT(p.T, w), MapY(p.Y, h)));
                shape = poly;
            }
            _root.Children.Add(shape);

            if (!isActive) return;   // les voix passives n'ont pas de handles

            // Points draggables : hit-testable, tag = index (dans spec.Points, PAS dans pts trié —
            // on retrouve l'index d'origine).
            var fill = new SolidColorBrush(baseCol); fill.Freeze();
            var stroke = new SolidColorBrush(Color.FromRgb(0x10, 0x14, 0x18)); stroke.Freeze();
            for (int i = 0; i < spec.Points.Count; i++)
            {
                var pt = spec.Points[i];
                var el = new Ellipse
                {
                    Width = PointRadius * 2,
                    Height = PointRadius * 2,
                    Fill = fill,
                    Stroke = stroke,
                    StrokeThickness = 1.5,
                    Cursor = Cursors.SizeAll,
                    Tag = i,
                };
                Canvas.SetLeft(el, MapT(pt.T, w) - PointRadius);
                Canvas.SetTop(el, MapY(pt.Y, h) - PointRadius);
                el.MouseLeftButtonDown += OnPointLeftDown;
                el.MouseRightButtonDown += OnPointRightDown;
                _root.Children.Add(el);
            }
        }

        /// <summary>Construit un Path Catmull-Rom (traversant TOUS les points) rendu comme une suite
        /// de cubiques Bézier. Formule classique : pour 4 points p0..p3, la courbe C-R de p1 à p2
        /// équivaut au Bézier ayant pour handles p1 + (p2-p0)/6 et p2 - (p3-p1)/6. Aux extrémités
        /// on duplique le point (p0=p1 au début, p3=p2 à la fin) — même convention que le moteur
        /// audio, WYSIWYG total entre l'aperçu et le rendu joué.</summary>
        Shape BuildCatmullRomPath(List<SplineMelodyGenerator.ControlPoint> pts, double w, double h, Brush stroke, double thickness)
        {
            var geometry = new PathGeometry();
            var figure = new PathFigure { StartPoint = new Point(MapT(pts[0].T, w), MapY(pts[0].Y, h)), IsClosed = false };
            for (int i = 0; i < pts.Count - 1; i++)
            {
                var p0 = i > 0 ? pts[i - 1] : pts[i];
                var p1 = pts[i];
                var p2 = pts[i + 1];
                var p3 = i + 2 < pts.Count ? pts[i + 2] : pts[i + 1];
                double p0x = MapT(p0.T, w), p0y = MapY(p0.Y, h);
                double p1x = MapT(p1.T, w), p1y = MapY(p1.Y, h);
                double p2x = MapT(p2.T, w), p2y = MapY(p2.Y, h);
                double p3x = MapT(p3.T, w), p3y = MapY(p3.Y, h);
                var c1 = new Point(p1x + (p2x - p0x) / 6, p1y + (p2y - p0y) / 6);
                var c2 = new Point(p2x - (p3x - p1x) / 6, p2y - (p3y - p1y) / 6);
                figure.Segments.Add(new BezierSegment(c1, c2, new Point(p2x, p2y), true));
            }
            geometry.Figures.Add(figure);
            return new System.Windows.Shapes.Path
            {
                Data = geometry,
                Stroke = stroke,
                StrokeThickness = thickness,
                StrokeLineJoin = PenLineJoin.Round,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                IsHitTestVisible = false,
            };
        }

        // -----------------------------------------------------------------------------------------
        // Mapping coordonnées ↔ modèle
        // -----------------------------------------------------------------------------------------
        static double MapT(double t01, double w) => Math.Max(0, Math.Min(1, t01)) * w;
        static double MapY(double y, double h) => h / 2 - (y / YScale) * (h / 2);
        static double UnmapT(double x, double w) { double t = w > 0 ? x / w : 0; if (t < 0) t = 0; if (t > 1) t = 1; return t; }
        static double UnmapY(double px, double h) { double y = (h / 2 - px) * (YScale / (h / 2)); if (y > YScale) y = YScale; if (y < -YScale) y = -YScale; return y; }

        // -----------------------------------------------------------------------------------------
        // Interactions
        // -----------------------------------------------------------------------------------------
        void OnLeftDown(object sender, MouseButtonEventArgs e)
        {
            int active = _getActiveIndex();
            var spec = _getVoice(active);
            if (spec == null) return;
            var pos = e.GetPosition(_root);
            double t = UnmapT(pos.X, ActualWidth);
            double y = UnmapY(pos.Y, ActualHeight);
            spec.Points.Add(new SplineMelodyGenerator.ControlPoint(t, y));
            _dragVoiceIdx = active;
            _dragPointIdx = spec.Points.Count - 1;
            _root.CaptureMouse();
            Changed?.Invoke();
            Redraw();
            e.Handled = true;
        }

        void OnPointLeftDown(object sender, MouseButtonEventArgs e)
        {
            if (!(sender is Ellipse el) || !(el.Tag is int idx)) return;
            int active = _getActiveIndex();
            _dragVoiceIdx = active;
            _dragPointIdx = idx;
            _root.CaptureMouse();
            e.Handled = true;   // sinon l'événement bubble au canvas → ajout d'un doublon
        }

        void OnPointRightDown(object sender, MouseButtonEventArgs e)
        {
            if (!(sender is Ellipse el) || !(el.Tag is int idx)) return;
            int active = _getActiveIndex();
            var spec = _getVoice(active);
            if (spec == null) return;
            if (spec.Points.Count <= 2) { e.Handled = true; return; }   // garde-fou : min 2 points
            if (idx < 0 || idx >= spec.Points.Count) { e.Handled = true; return; }
            spec.Points.RemoveAt(idx);
            Changed?.Invoke();
            Redraw();
            e.Handled = true;
        }

        void OnRightDown(object sender, MouseButtonEventArgs e)
        {
            // Sur zone libre : rien (le clic droit ne fait rien sauf sur un point).
            e.Handled = true;
        }

        void OnMove(object sender, MouseEventArgs e)
        {
            if (_dragVoiceIdx < 0 || _dragPointIdx < 0) return;
            var spec = _getVoice(_dragVoiceIdx);
            if (spec == null || _dragPointIdx >= spec.Points.Count) { EndDrag(); return; }
            var pos = e.GetPosition(_root);
            double t = UnmapT(pos.X, ActualWidth);
            double y = UnmapY(pos.Y, ActualHeight);
            spec.Points[_dragPointIdx] = new SplineMelodyGenerator.ControlPoint(t, y);
            Changed?.Invoke();
            Redraw();
        }

        void OnLeftUp(object sender, MouseButtonEventArgs e) => EndDrag();

        void EndDrag()
        {
            if (_dragVoiceIdx >= 0) _root.ReleaseMouseCapture();
            _dragVoiceIdx = -1;
            _dragPointIdx = -1;
        }
    }
}
