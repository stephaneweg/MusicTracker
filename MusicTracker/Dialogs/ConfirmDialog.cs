using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MusicTracker.Dialogs
{
    /// <summary>A small themed yes/no confirmation dialog, matching the app's other dialogs (used instead of the
    /// system MessageBox where a consistent look matters, e.g. the first-launch tutorial offer). Code-only.</summary>
    public sealed class ConfirmDialog : Window
    {
        ConfirmDialog(string title, string message, string okText, string cancelText)
        {
            WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
            SizeToContent = SizeToContent.WidthAndHeight; ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
            Title = title;

            Brush Res(string k) => Application.Current != null ? Application.Current.TryFindResource(k) as Brush : null;
            var fg = Res("CommonForeground") ?? Brushes.White;

            var stack = new StackPanel { Margin = new Thickness(26, 22, 26, 22), MaxWidth = 420 };
            stack.Children.Add(new TextBlock { Text = title, Foreground = fg, FontSize = 16, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 10) });
            stack.Children.Add(new TextBlock { Text = message, Foreground = fg, TextWrapping = TextWrapping.Wrap, Opacity = 0.9 });

            var ok = new Button { Content = okText, Padding = new Thickness(18, 4, 18, 4), Cursor = System.Windows.Input.Cursors.Hand, IsDefault = true };
            var cancel = new Button { Content = cancelText, Padding = new Thickness(16, 4, 16, 4), Margin = new Thickness(0, 0, 8, 0), Cursor = System.Windows.Input.Cursors.Hand, IsCancel = true };
            if (Application.Current != null)
            {
                ok.Style = Application.Current.TryFindResource("okButton") as Style;
                cancel.Style = Application.Current.TryFindResource("cancelButton") as Style;
            }
            ok.Click += (s, e) => { DialogResult = true; };
            cancel.Click += (s, e) => { DialogResult = false; };

            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
            row.Children.Add(cancel); row.Children.Add(ok);
            stack.Children.Add(row);

            Content = new Border
            {
                Background = Res("CommonBackground") ?? new SolidColorBrush(Color.FromRgb(0x24, 0x25, 0x2B)),
                BorderBrush = Res("OutlineColorBrush") ?? new SolidColorBrush(Color.FromRgb(0x1F, 0xB6, 0xC3)),
                BorderThickness = new Thickness(3),
                CornerRadius = new CornerRadius(8),
                Child = stack,
            };
        }

        /// <summary>Show the dialog modally; returns true if the user confirmed (OK).</summary>
        public static bool Ask(Window owner, string title, string message, string okText, string cancelText)
        {
            var d = new ConfirmDialog(title, message, okText, cancelText) { Owner = owner };
            return d.ShowDialog() == true;
        }
    }
}
