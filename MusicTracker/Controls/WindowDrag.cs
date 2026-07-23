using System.Windows;
using System.Windows.Input;

namespace MusicTracker.Controls
{
    /// <summary>
    /// Attached behavior to make a borderless (WindowStyle=None) window movable. Set
    /// <c>ctl:WindowDrag.Enable="True"</c> on an element — typically the root Border of a dialog — and the
    /// user can drag the whole window by that element. Interactive children (Button, TextBox, ComboBox, …)
    /// already mark the mouse-down as handled, so a drag only ever starts on empty / non-interactive surfaces.
    /// </summary>
    public static class WindowDrag
    {
        public static readonly DependencyProperty EnableProperty =
            DependencyProperty.RegisterAttached("Enable", typeof(bool), typeof(WindowDrag),
                new PropertyMetadata(false, OnEnableChanged));

        public static void SetEnable(DependencyObject o, bool v) => o.SetValue(EnableProperty, v);
        public static bool GetEnable(DependencyObject o) => (bool)o.GetValue(EnableProperty);

        static void OnEnableChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (!(d is UIElement el)) return;
            if ((bool)e.NewValue) el.MouseLeftButtonDown += OnMouseLeftButtonDown;
            else el.MouseLeftButtonDown -= OnMouseLeftButtonDown;
        }

        static void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState != MouseButtonState.Pressed) return;
            var w = Window.GetWindow((DependencyObject)sender);
            // DragMove throws if the button was released between the event and this call — swallow that race.
            if (w != null) { try { w.DragMove(); } catch { } }
        }
    }
}
