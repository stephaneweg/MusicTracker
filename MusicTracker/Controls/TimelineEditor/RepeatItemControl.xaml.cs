using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace MusicTracker.Controls.TimelineEditor
{
    /// <summary>
    /// The translucent backdrop + title ("Repeat ×N") of a compact Repeat container. The host sizes it
    /// to ONE cycle and positions it on the lane canvas; the inner modules are drawn separately as
    /// <see cref="ModuleBoxControl"/>s on top. Clicking it raises <see cref="Selected"/>.
    /// </summary>
    public partial class RepeatItemControl : UserControl
    {
        static readonly Brush Backdrop = new SolidColorBrush(Color.FromArgb(0x55, 0x4A, 0x3A, 0x66));
        static readonly Brush SelBorder = new SolidColorBrush(Color.FromRgb(0x66, 0xCC, 0x88));
        static readonly Brush NormalBorder = new SolidColorBrush(Color.FromRgb(0x7A, 0x66, 0x99));

        /// <summary>Reserved title band height (px) — the host insets the inner modules by this much.</summary>
        public const double TitleStrip = 16;

        public event Action Selected;

        /// <summary>Raised when the user clicks the ✕ delete button.</summary>
        public event Action Deleted;

        /// <summary>Raised on drop with the backdrop's new Canvas.Left in px; the host re-lays-out the track.</summary>
        public event Action<double> Dropped;

        Point pressPos;
        double pressLeft;
        bool pressed, dragging;
        const double DragThreshold = 4;

        public RepeatItemControl()
        {
            InitializeComponent();
            MouseLeftButtonDown += Repeat_MouseLeftButtonDown;
            MouseMove += Repeat_MouseMove;
            MouseLeftButtonUp += Repeat_MouseLeftButtonUp;
        }

        // Drag the whole Repeat block by its backdrop / title strip (inner modules sit on top and grab
        // their own clicks); a plain click selects it. Drop reports the new Canvas.Left to the host.
        void Repeat_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            var canvas = Parent as IInputElement;
            if (canvas == null) { Selected?.Invoke(); return; }
            pressPos = e.GetPosition(canvas);
            pressLeft = Canvas.GetLeft(this); if (double.IsNaN(pressLeft)) pressLeft = 0;
            pressed = true; dragging = false;
            CaptureMouse();
        }

        void Repeat_MouseMove(object sender, MouseEventArgs e)
        {
            if (!pressed) return;
            var canvas = Parent as IInputElement;
            if (canvas == null) return;
            double dx = e.GetPosition(canvas).X - pressPos.X;
            if (!dragging && Math.Abs(dx) > DragThreshold) { dragging = true; Panel.SetZIndex(this, 50); }
            if (dragging) Canvas.SetLeft(this, Math.Max(0, pressLeft + dx)); // horizontal only
        }

        void Repeat_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
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

        public void Configure(string title, double width, double height, bool selected)
        {
            Width = width; Height = height;
            back.Width = width; back.Height = height;
            back.Background = Backdrop;
            back.BorderBrush = selected ? SelBorder : NormalBorder;
            back.BorderThickness = new Thickness(selected ? 2 : 1);
            txtTitle.Text = title;
        }

        /// <summary>Update only the selection border (cheap — no full reconfigure).</summary>
        public void SetSelected(bool selected)
        {
            back.BorderBrush = selected ? SelBorder : NormalBorder;
            back.BorderThickness = new Thickness(selected ? 2 : 1);
        }

        private void btnDel_Click(object sender, RoutedEventArgs e) => Deleted?.Invoke();
    }
}
