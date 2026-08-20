using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace KotonPluginWaveMorph
{
    /// <summary>
    /// Vue graphique compacte d'une forme d'onde — un Border avec un Canvas qui trace un Polyline
    /// depuis un buffer float[]. Réutilisable pour toutes les visualisations du plugin (Wave 1,
    /// Résultat, Wave 2, Onde finale) en variant les couleurs et la présence d'un cadre "accent".
    ///
    /// **API** : appeler <see cref="SetSamples"/> avec un tableau ; la vue redessine immédiatement.
    /// Le contrôle NE fait AUCUN timer / polling — c'est le parent qui décide quand rafraîchir (le
    /// scope "live" utilise un DispatcherTimer 30 Hz dans l'éditeur ; les vues statiques
    /// rafraîchissent uniquement quand les params changent).
    ///
    /// **Rendu** : sous-échantillonne le buffer sur la largeur du canvas (1 pixel = 1 point), clampe
    /// à ±1 pour ne pas déborder de la zone visible. Ligne + fill (avec alpha) pour le look "scope
    /// analogique".
    ///
    /// **Autonome** : code-only (pas de XAML) pour éviter le boilerplate InitializeComponent — le
    /// contrôle est instancié par code dans l'éditeur, il n'apparaît jamais dans du XAML statique.
    /// </summary>
    internal sealed class WaveDisplayControl : UserControl
    {
        readonly Canvas _canvas;
        readonly Polyline _line;
        readonly Polygon _fillArea;
        readonly Line _midline;
        float[] _samples;

        // Couleurs (les setters recréent les brushes gelés — évite les allocs par refresh).
        Brush _strokeBrush;
        Brush _fillBrush;

        public WaveDisplayControl()
        {
            _strokeBrush = MakeFrozen(Color.FromRgb(0x88, 0x88, 0x88));
            _fillBrush = MakeFrozen(Color.FromArgb(0x40, 0x88, 0x88, 0x88));

            _canvas = new Canvas
            {
                Background = Brushes.Transparent,
                ClipToBounds = true,
            };

            _midline = new Line
            {
                Stroke = MakeFrozen(Color.FromArgb(0x60, 0x35, 0x35, 0x3F)),
                StrokeThickness = 0.6,
                StrokeDashArray = new DoubleCollection { 2, 4 },
                IsHitTestVisible = false,
            };
            _fillArea = new Polygon
            {
                Fill = _fillBrush,
                IsHitTestVisible = false,
            };
            _line = new Polyline
            {
                Stroke = _strokeBrush,
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

        /// <summary>Change la couleur du trait ET du fill (le fill dérive du trait avec un alpha
        /// faible). Un appel = 2 brushes recréés + un redraw.</summary>
        public void SetColor(Color stroke)
        {
            _strokeBrush = MakeFrozen(stroke);
            _fillBrush = MakeFrozen(Color.FromArgb(0x40, stroke.R, stroke.G, stroke.B));
            _line.Stroke = _strokeBrush;
            _fillArea.Fill = _fillBrush;
        }

        /// <summary>Charge un nouveau buffer et redessine. Une référence est gardée (pas de copie),
        /// donc l'appelant ne doit pas muter le buffer entre deux SetSamples.</summary>
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

            for (int i = 0; i < cols; i++)
            {
                int srcIdx = (int)((long)i * n / cols);
                if (srcIdx >= n) srcIdx = n - 1;
                float s = _samples[srcIdx];
                if (s > 1f) s = 1f; else if (s < -1f) s = -1f;
                double y = (0.5 - 0.45 * s) * h;   // marge 5% haut/bas pour ne pas coller aux bords
                var pt = new Point(i, y);
                linePts.Add(pt);
                fillPts.Add(pt);
            }
            // Fermer le polygon vers le bas pour un fill "scope".
            fillPts.Add(new Point(cols - 1, h));
            fillPts.Add(new Point(0, h));

            _line.Points = linePts;
            _fillArea.Points = fillPts;
        }

        static Brush MakeFrozen(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }
    }
}
