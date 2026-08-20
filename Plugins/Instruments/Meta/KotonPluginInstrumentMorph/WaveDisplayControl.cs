using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace KotonPluginInstrumentMorph
{
    /// <summary>
    /// Vue graphique compacte d'une forme d'onde — Canvas + Polyline. Reproduit le meme rendu
    /// que WaveMorph.WaveDisplayControl (scope analog look). Reutilisable pour les 3 vues du plugin
    /// (Wave A, Wave B, Résultat morphé).
    /// </summary>
    internal sealed class WaveDisplayControl : UserControl
    {
        readonly Canvas _canvas;
        readonly Polyline _line;
        readonly Polygon _fillArea;
        readonly Line _midline;
        float[] _samples;

        public WaveDisplayControl()
        {
            _canvas = new Canvas { Background = Brushes.Transparent, ClipToBounds = true };
            _midline = new Line
            {
                Stroke = MakeFrozen(Color.FromArgb(0x60, 0x35, 0x35, 0x3F)),
                StrokeThickness = 0.6,
                StrokeDashArray = new DoubleCollection { 2, 4 },
                IsHitTestVisible = false,
            };
            _fillArea = new Polygon
            {
                Fill = MakeFrozen(Color.FromArgb(0x30, 0x88, 0x88, 0x88)),
                IsHitTestVisible = false,
            };
            _line = new Polyline
            {
                Stroke = MakeFrozen(Color.FromRgb(0x88, 0x88, 0x88)),
                StrokeThickness = 1.4,
                StrokeLineJoin = PenLineJoin.Round,
                SnapsToDevicePixels = true,
                IsHitTestVisible = false,
            };
            _canvas.Children.Add(_midline);
            _canvas.Children.Add(_fillArea);
            _canvas.Children.Add(_line);
            Content = _canvas;
            SizeChanged += (s, e) => Redraw();
        }

        public void SetColor(Color stroke)
        {
            _line.Stroke = MakeFrozen(stroke);
            _fillArea.Fill = MakeFrozen(Color.FromArgb(0x40, stroke.R, stroke.G, stroke.B));
        }

        /// <summary>Charge un nouveau buffer et redessine.</summary>
        public void SetSamples(float[] samples)
        {
            _samples = samples;
            Redraw();
        }

        void Redraw()
        {
            double w = _canvas.ActualWidth;
            double h = _canvas.ActualHeight;
            if (w <= 0 || h <= 0) return;
            _midline.X1 = 0; _midline.X2 = w;
            _midline.Y1 = _midline.Y2 = h * 0.5;
            if (_samples == null || _samples.Length == 0)
            {
                _line.Points = new PointCollection();
                _fillArea.Points = new PointCollection();
                return;
            }
            int cols = Math.Max(2, (int)w);
            var linePts = new PointCollection(cols);
            var fillPts = new PointCollection(cols + 2);
            int n = _samples.Length;
            // Trouver un max peak pour normaliser l'affichage (le signal a bas volume reste visible)
            float peak = 0.01f;
            for (int i = 0; i < n; i++) { float a = _samples[i]; if (a < 0) a = -a; if (a > peak) peak = a; }
            float scale = 0.9f / peak;   // remplit ~90% de la hauteur
            for (int i = 0; i < cols; i++)
            {
                int srcIdx = (int)((long)i * n / cols);
                if (srcIdx >= n) srcIdx = n - 1;
                float s = _samples[srcIdx] * scale;
                if (s > 1f) s = 1f; else if (s < -1f) s = -1f;
                double y = (0.5 - 0.5 * s) * h;
                var pt = new Point(i, y);
                linePts.Add(pt);
                fillPts.Add(pt);
            }
            fillPts.Add(new Point(cols - 1, h * 0.5));
            fillPts.Add(new Point(0, h * 0.5));
            _line.Points = linePts;
            _fillArea.Points = fillPts;
        }

        static Brush MakeFrozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
    }
}
