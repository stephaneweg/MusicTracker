using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace MusicTracker.Controls.TimelineEditor
{
    /// <summary>
    /// One module box on a timeline track lane (title + info). Used both for top-level leaf items and
    /// for the modules drawn inside a Repeat. The host positions it on the lane canvas (Canvas.Left/Top)
    /// and computes the title/info/width; selection raises <see cref="Selected"/>.
    /// </summary>
    public partial class ModuleBoxControl : UserControl
    {
        static readonly Brush Fill = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x33));
        static readonly Brush SelBorder = new SolidColorBrush(Color.FromRgb(0x3B, 0xCE, 0xDA)); // bright teal (app accent)
        static readonly Brush NormalBorder = new SolidColorBrush(Color.FromRgb(0x11, 0x11, 0x11));

        bool interactive;
        Brush normalBorder = NormalBorder; // per-box unselected border (chords use a lighter blue)

        /// <summary>Raised when the user clicks the box (only when configured interactive).</summary>
        public event Action Selected;

        /// <summary>Raised when the user clicks the ✕ delete button.</summary>
        public event Action Deleted;

        /// <summary>Raised on right-click (interactive boxes) — the host shows a context menu for this item.</summary>
        public event Action ContextRequested;

        /// <summary>When true (top-level track items), the box can be dragged horizontally.</summary>
        public bool Draggable { get; set; }

        /// <summary>Raised on drop with the box's new Canvas.Left in px; the host re-lays-out the track.</summary>
        public event Action<double> Dropped;

        Point pressPos;
        double pressLeft;
        bool pressed, dragging;
        const double DragThreshold = 4;

        public ModuleBoxControl()
        {
            InitializeComponent();
            MouseLeftButtonDown += Box_MouseLeftButtonDown;
            MouseMove += Box_MouseMove;
            MouseLeftButtonUp += Box_MouseLeftButtonUp;
            MouseRightButtonUp += (s, e) => { if (interactive) { e.Handled = true; Selected?.Invoke(); ContextRequested?.Invoke(); } };
        }

        // Click selects; a horizontal drag (past a small threshold) moves the box and, on release, reports
        // its new Canvas.Left so the host can re-place it. Non-draggable / ghost boxes just select.
        void Box_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (!interactive) return;
            e.Handled = true;
            var canvas = Parent as IInputElement;
            if (!Draggable || canvas == null) { Selected?.Invoke(); return; }
            pressPos = e.GetPosition(canvas);
            pressLeft = Canvas.GetLeft(this); if (double.IsNaN(pressLeft)) pressLeft = 0;
            pressed = true; dragging = false;
            CaptureMouse();
        }

        void Box_MouseMove(object sender, MouseEventArgs e)
        {
            if (!pressed) return;
            var canvas = Parent as IInputElement;
            if (canvas == null) return;
            double dx = e.GetPosition(canvas).X - pressPos.X;
            if (!dragging && Math.Abs(dx) > DragThreshold) { dragging = true; Panel.SetZIndex(this, 50); }
            if (dragging) Canvas.SetLeft(this, Math.Max(0, pressLeft + dx)); // horizontal only
        }

        void Box_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!pressed) return;
            pressed = false;
            ReleaseMouseCapture();
            e.Handled = true;
            if (dragging)
            {
                dragging = false;
                Panel.SetZIndex(this, 0);
                double nl = Canvas.GetLeft(this); if (double.IsNaN(nl)) nl = 0;
                Dropped?.Invoke(nl);
            }
            else Selected?.Invoke();
        }

        public void Configure(string title, string info, double width, double height, bool selected, bool interactive, double opacity, Brush fill = null, Brush border = null)
        {
            this.interactive = interactive;
            normalBorder = border ?? NormalBorder;
            Width = width; Height = height; Opacity = opacity;
            box.Width = width; box.Height = height;
            box.Background = fill ?? Fill;
            box.BorderBrush = selected ? SelBorder : (interactive ? normalBorder : System.Windows.Media.Brushes.Transparent);
            box.BorderThickness = new Thickness(selected ? 2 : 1);
            Cursor = interactive ? Cursors.Hand : Cursors.Arrow;
            txtTitle.Text = title;
            txtInfo.Text = info;
            btnDel.Visibility = interactive ? Visibility.Visible : Visibility.Collapsed;
            // Ghost copies (non-interactive) ignore the mouse so clicks fall through to the Repeat
            // backdrop behind -> clicking inside a Repeat selects the Repeat (and later moves it).
            IsHitTestVisible = interactive;
        }

        /// <summary>Update only the selection border (cheap — no full reconfigure).</summary>
        public void SetSelected(bool selected)
        {
            box.BorderBrush = selected ? SelBorder : (interactive ? normalBorder : System.Windows.Media.Brushes.Transparent);
            box.BorderThickness = new Thickness(selected ? 2 : 1);
        }

        /// <summary>Show a mini melodic preview (Play-riff boxes); null hides it.</summary>
        public void SetThumbnail(ImageSource img)
        {
            thumb.Source = img;
            thumb.Visibility = img != null ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>Show a big centred label over the thumbnail (chords: the roman degree); null/empty hides it.</summary>
        public void SetBigLabel(string s)
        {
            txtBig.Text = s ?? "";
            txtBig.Visibility = string.IsNullOrEmpty(s) ? Visibility.Collapsed : Visibility.Visible;
        }

        private void btnDel_Click(object sender, RoutedEventArgs e) => Deleted?.Invoke();
    }
}
