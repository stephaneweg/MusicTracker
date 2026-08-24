using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace KotonPluginLifeGrid
{
    /// <summary>
    /// Grille de cellules dessinée à la main — utilisée DEUX FOIS dans l'éditeur, avec le même code :
    /// à gauche le motif de départ (<see cref="Editable"/> = true, on peint à la souris), à droite
    /// l'évolution (lecture seule, rafraîchie par le timer d'animation). Le parallèle avec les deux
    /// panneaux de Newscool est volontaire : on dessine à gauche, on regarde vivre à droite.
    ///
    /// **Convention d'axe** : la ligne 0 est en BAS. C'est l'orientation du piano-roll et celle du
    /// mapping du générateur (ligne 0 = note la plus grave), donc ce qu'on dessine en haut de la
    /// grille est bien ce qu'on entend en haut du registre.
    ///
    /// **Peinture par glissement** : le premier clic décide de la valeur posée (inverse de la cellule
    /// cliquée) et le glissement l'applique aux suivantes. Sans ça, traîner sur une zone mixte fait
    /// clignoter les cellules une à une au lieu de dessiner un trait.
    /// </summary>
    public sealed class LifeGridCanvas : FrameworkElement
    {
        public int Cols { get; private set; } = 16;
        public int Rows { get; private set; } = 12;

        byte[] _cells;
        byte[] _prev;

        /// <summary>Vrai si la souris peint les cellules (panneau « motif de départ »).</summary>
        public bool Editable { get; set; }

        /// <summary>Teinte principale des cellules vivantes.</summary>
        public Color CellColor { get; set; } = Color.FromRgb(0x4F, 0xA8, 0xE0);

        /// <summary>Teinte des cellules qui viennent de NAÎTRE (panneau évolution). Ignorée tant que
        /// <see cref="SetState"/> n'a pas reçu d'état précédent.</summary>
        public Color BornColor { get; set; } = Color.FromRgb(0xE8, 0x8A, 0x3C);

        /// <summary>Levé quand l'utilisateur peint une cellule (x, y, état posé).</summary>
        public event Action<int, int, bool> CellPainted;

        public LifeGridCanvas()
        {
            _cells = new byte[Cols * Rows];
            Focusable = false;
            SnapsToDevicePixels = true;
        }

        /// <summary>Pose l'état affiché. <paramref name="prev"/> (facultatif) sert à colorer les
        /// naissances et à laisser une traînée sur les cellules qui viennent de mourir — c'est ce qui
        /// rend l'animation lisible quand la grille bouge vite.</summary>
        public void SetState(int cols, int rows, byte[] cells, byte[] prev = null)
        {
            if (cells == null) return;
            Cols = Math.Max(1, cols);
            Rows = Math.Max(1, rows);
            _cells = cells;
            _prev = prev != null && prev.Length == cells.Length ? prev : null;
            InvalidateVisual();
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            // Sans contrainte (dans un StackPanel par exemple), on se donne une taille de repli
            // proportionnelle à la grille plutôt que zéro — sinon le contrôle disparaît.
            double w = double.IsInfinity(availableSize.Width) ? Cols * 14 : availableSize.Width;
            double h = double.IsInfinity(availableSize.Height) ? Rows * 14 : availableSize.Height;
            return new Size(w, h);
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            if (w <= 0 || h <= 0 || _cells == null) return;

            var bg = new SolidColorBrush(Color.FromRgb(0x11, 0x16, 0x19));
            bg.Freeze();
            dc.DrawRoundedRectangle(bg, null, new Rect(0, 0, w, h), 4, 4);

            // Cellules carrées, grille centrée : une grille 32×6 ne doit pas s'étirer en spaghetti.
            double cell = Math.Min(w / Cols, h / Rows);
            if (cell <= 0) return;
            double ox = (w - cell * Cols) / 2, oy = (h - cell * Rows) / 2;

            var gridPen = new Pen(new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)), 0.5);
            gridPen.Freeze();
            for (int x = 0; x <= Cols; x++)
                dc.DrawLine(gridPen, new Point(ox + x * cell, oy), new Point(ox + x * cell, oy + cell * Rows));
            for (int y = 0; y <= Rows; y++)
                dc.DrawLine(gridPen, new Point(ox, oy + y * cell), new Point(ox + cell * Cols, oy + y * cell));

            var live = new SolidColorBrush(CellColor); live.Freeze();
            var born = new SolidColorBrush(BornColor); born.Freeze();
            var ghost = new SolidColorBrush(Color.FromArgb(0x38, BornColor.R, BornColor.G, BornColor.B)); ghost.Freeze();

            double pad = Math.Max(0.5, cell * 0.12);
            double size = cell - pad * 2;
            double radius = Math.Min(3, size * 0.25);

            for (int y = 0; y < Rows; y++)
            {
                // Ligne 0 en bas : on inverse l'axe vertical à l'affichage.
                double py = oy + (Rows - 1 - y) * cell + pad;
                for (int x = 0; x < Cols; x++)
                {
                    int i = y * Cols + x;
                    if (i >= _cells.Length) continue;
                    bool on = _cells[i] != 0;
                    bool was = _prev != null && _prev[i] != 0;

                    Brush b = null;
                    if (on) b = (_prev != null && !was) ? born : live;
                    else if (was) b = ghost;              // traînée : la cellule vient de mourir
                    if (b == null) continue;

                    dc.DrawRoundedRectangle(b, null, new Rect(ox + x * cell + pad, py, size, size), radius, radius);
                }
            }
        }

        // ------------------------------------------------------------------ peinture
        bool _painting;
        bool _paintValue;

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);
            if (!Editable) return;
            if (!HitCell(e.GetPosition(this), out int x, out int y)) return;
            _paintValue = _cells[y * Cols + x] == 0;   // le premier clic décide du sens du trait
            _painting = true;
            CaptureMouse();
            Paint(x, y);
            e.Handled = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!_painting) return;
            if (HitCell(e.GetPosition(this), out int x, out int y)) Paint(x, y);
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonUp(e);
            if (!_painting) return;
            _painting = false;
            ReleaseMouseCapture();
        }

        void Paint(int x, int y)
        {
            int i = y * Cols + x;
            if (i < 0 || i >= _cells.Length) return;
            if ((_cells[i] != 0) == _paintValue) return;
            CellPainted?.Invoke(x, y, _paintValue);
        }

        bool HitCell(Point p, out int x, out int y)
        {
            x = y = -1;
            double w = ActualWidth, h = ActualHeight;
            double cell = Math.Min(w / Cols, h / Rows);
            if (cell <= 0) return false;
            double ox = (w - cell * Cols) / 2, oy = (h - cell * Rows) / 2;
            int cx = (int)Math.Floor((p.X - ox) / cell);
            int cy = (int)Math.Floor((p.Y - oy) / cell);
            if (cx < 0 || cy < 0 || cx >= Cols || cy >= Rows) return false;
            x = cx;
            y = Rows - 1 - cy;   // retour à l'axe « ligne 0 en bas »
            return true;
        }
    }
}
