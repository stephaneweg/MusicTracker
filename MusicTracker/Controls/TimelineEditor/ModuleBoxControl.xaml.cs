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
        bool isSelected;                   // kept so a recolour can restore the right border without a reconfigure
        Brush normalBorder = NormalBorder; // per-box unselected border (chords use a lighter blue)

        // Readability thresholds (px): below each one the element is HIDDEN rather than allowed to overflow — the
        // box is never widened to fit its text (that would shift the whole lane out of step with the ruler).
        // The order follows the functional spec: the title goes first, then the secondary info.
        const double MinTitlePx = 60, MinInfoPx = 34, MinDelPx = 46, MinBigLabelPx = 26, MinThumbPx = 14;
        double lastWidth = double.MaxValue;   // width of the last Configure (drives the thresholds above)
        double thumbScale = 1;                // requested horizontal thumbnail scale (= the timeline zoom)


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
            isSelected = selected;
            normalBorder = border ?? NormalBorder;
            lastWidth = width;
            Width = width; Height = height; Opacity = opacity;
            box.Width = width; box.Height = height;
            box.Background = fill ?? Fill;
            box.BorderBrush = selected ? SelBorder : (interactive ? normalBorder : System.Windows.Media.Brushes.Transparent);
            box.BorderThickness = new Thickness(selected ? 2 : 1);
            Cursor = interactive ? Cursors.Hand : Cursors.Arrow;
            txtTitle.Text = title;
            txtInfo.Text = info;
            // Too narrow for its content: drop the title, then the info, then the ✕ — the fill and the border
            // (including the turquoise selection border) always stay, so a 3 px box is still spottable.
            txtTitle.Visibility = width >= MinTitlePx ? Visibility.Visible : Visibility.Collapsed;
            txtInfo.Visibility = width >= MinInfoPx ? Visibility.Visible : Visibility.Collapsed;
            btnDel.Visibility = (interactive && width >= MinDelPx) ? Visibility.Visible : Visibility.Collapsed;
            // Ghost copies (non-interactive) ignore the mouse so clicks fall through to the Repeat
            // backdrop behind -> clicking inside a Repeat selects the Repeat (and later moves it).
            IsHitTestVisible = interactive;
            ApplyThumbScale();   // the width just changed, so the thumbnail's fit clamp must be recomputed
        }

        /// <summary>Update only the selection border (cheap — no full reconfigure).</summary>
        public void SetSelected(bool selected)
        {
            isSelected = selected;
            box.BorderBrush = selected ? SelBorder : (interactive ? normalBorder : System.Windows.Media.Brushes.Transparent);
            box.BorderThickness = new Thickness(selected ? 2 : 1);
        }

        /// <summary>Update only the instrument-family tint (cheap — no full reconfigure), preserving the selection
        /// border. Used when a track's instrument changes, so its boxes re-tint without rebuilding the timeline.</summary>
        public void SetColors(Brush fill, Brush border)
        {
            normalBorder = border ?? NormalBorder;
            box.Background = fill ?? Fill;
            box.BorderBrush = isSelected ? SelBorder : (interactive ? normalBorder : System.Windows.Media.Brushes.Transparent);
        }

        /// <summary>Show a mini melodic preview (Play-riff boxes); null hides it.</summary>
        public void SetThumbnail(ImageSource img)
        {
            thumb.Source = img;
            thumb.Visibility = (img != null && lastWidth >= MinThumbPx) ? Visibility.Visible : Visibility.Collapsed;
            ApplyThumbScale();   // the fit clamp below depends on the bitmap's own width
        }

        /// <summary>HORIZONTAL display scale of the thumbnail. The bitmap is rendered once and for all at the
        /// reference scale (60 px/beat) and scaled at display time, so no image is re-generated (nor re-cached) per
        /// zoom level. Vertical untouched: the zoom is purely horizontal.
        /// (thumb is HorizontalAlignment=Left with the default RenderTransformOrigin 0,0 → the scale starts at the
        /// left edge, i.e. at the module's own start.)</summary>
        public void SetThumbnailScale(double sx)
        {
            thumbScale = sx;
            ApplyThumbScale();
        }

        // The bitmap is ceil(len·60) px wide while the box is only len·PxPerBeat − 2, and inside it the thumbnail
        // loses the border (≤2 px each side) and its own 4 px margins. At 100 % the bitmap is therefore ~12 px WIDER
        // than the room available, so the last notes of the line fell outside — invisible before, and actually cut off
        // once ClipToBounds was added to protect the neighbouring boxes.
        // Fix: never let the drawn thumbnail exceed the usable width. The clamp only bites when the bitmap would
        // overflow (a barely perceptible squeeze of a few %), and it keeps the WHOLE line visible at every zoom level
        // without giving up the clipping.
        void ApplyThumbScale()
        {
            double sx = thumbScale;
            double bmp = thumb.Source == null ? 0 : thumb.Source.Width;
            if (bmp > 0 && !double.IsInfinity(lastWidth))
            {
                const double BorderReserve = 4;   // 2 px of border each side (worst case = a selected box)
                double usable = lastWidth - BorderReserve - thumb.Margin.Left - thumb.Margin.Right;
                if (usable > 0 && bmp * sx > usable) sx = usable / bmp;
            }
            thumb.RenderTransform = (Math.Abs(sx - 1) < 1e-6) ? null : new ScaleTransform(sx, 1);
        }

        /// <summary>Show a custom panel INSIDE the box (accords polyrythmiques : découpage par zones + labels).
        /// Null hides it — mutually exclusive with the thumbnail (both live in row 1). Kept as a helper so the
        /// box control stays render-agnostic (fabrique un panneau, ne connaît pas les modules).</summary>
        public void SetContentPanel(FrameworkElement panel)
        {
            customPanel.Content = panel;
            customPanel.Visibility = panel != null ? Visibility.Visible : Visibility.Collapsed;
            if (panel != null) thumb.Visibility = Visibility.Collapsed;
        }

        /// <summary>Show a big centred label over the thumbnail (chords: the roman degree); null/empty hides it.</summary>
        public void SetBigLabel(string s)
        {
            txtBig.Text = s ?? "";
            txtBig.Visibility = (string.IsNullOrEmpty(s) || lastWidth < MinBigLabelPx) ? Visibility.Collapsed : Visibility.Visible;
        }

        private void btnDel_Click(object sender, RoutedEventArgs e) => Deleted?.Invoke();
    }
}
