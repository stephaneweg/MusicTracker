using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MusicTracker.Dialogs
{
    public enum SaveDiscardResult { Save, Discard, Cancel }

    /// <summary>A small themed confirmation dialog, matching the app's other dialogs (used instead of the
    /// system MessageBox where a consistent look matters, e.g. the first-launch tutorial offer or the
    /// « enregistrer avant de fermer ? » prompt). Code-only.</summary>
    public sealed class ConfirmDialog : Window
    {
        ConfirmDialog(string title, string message)
        {
            WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
            SizeToContent = SizeToContent.WidthAndHeight; ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
            Title = title;
        }

        static Brush Res(string k) => Application.Current != null ? Application.Current.TryFindResource(k) as Brush : null;

        static Button MakeButton(string text, string styleKey, bool isDefault, bool isCancel)
        {
            var b = new Button
            {
                Content = text,
                Padding = new Thickness(styleKey == "okButton" ? 18 : 16, 4, styleKey == "okButton" ? 18 : 16, 4),
                Margin = new Thickness(0, 0, 8, 0),
                Cursor = System.Windows.Input.Cursors.Hand,
                IsDefault = isDefault,
                IsCancel = isCancel,
            };
            if (Application.Current != null) b.Style = Application.Current.TryFindResource(styleKey) as Style;
            return b;
        }

        void Build(string title, string message, params Button[] buttons)
        {
            var fg = Res("CommonForeground") ?? Brushes.White;
            var stack = new StackPanel { Margin = new Thickness(26, 22, 26, 22), MaxWidth = 480 };
            stack.Children.Add(new TextBlock { Text = title, Foreground = fg, FontSize = 16, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 10) });
            stack.Children.Add(new TextBlock { Text = message, Foreground = fg, TextWrapping = TextWrapping.Wrap, Opacity = 0.9 });

            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
            // Trailing margin on the last button would push it off the right edge — drop it.
            if (buttons.Length > 0) buttons[buttons.Length - 1].Margin = new Thickness(0);
            foreach (var b in buttons) row.Children.Add(b);
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
            var d = new ConfirmDialog(title, message) { Owner = owner };
            var cancel = MakeButton(cancelText, "cancelButton", isDefault: false, isCancel: true);
            var ok = MakeButton(okText, "okButton", isDefault: true, isCancel: false);
            cancel.Click += (s, e) => { d.DialogResult = false; };
            ok.Click += (s, e) => { d.DialogResult = true; };
            d.Build(title, message, cancel, ok);
            return d.ShowDialog() == true;
        }

        /// <summary>Save / Don't save / Cancel prompt (à la Word), themed. Escape and the window ✕ = Cancel.</summary>
        public static SaveDiscardResult AskSaveDiscardCancel(Window owner, string title, string message,
                                                             string saveText, string discardText, string cancelText)
        {
            var d = new ConfirmDialog(title, message) { Owner = owner };
            var result = SaveDiscardResult.Cancel;
            var discard = MakeButton(discardText, "cancelButton", isDefault: false, isCancel: false);
            var cancel = MakeButton(cancelText, "cancelButton", isDefault: false, isCancel: true);
            var save = MakeButton(saveText, "okButton", isDefault: true, isCancel: false);
            discard.Click += (s, e) => { result = SaveDiscardResult.Discard; d.DialogResult = true; };
            cancel.Click += (s, e) => { result = SaveDiscardResult.Cancel; d.DialogResult = false; };
            save.Click += (s, e) => { result = SaveDiscardResult.Save; d.DialogResult = true; };
            d.Build(title, message, discard, cancel, save);
            d.ShowDialog();
            return result;
        }
    }
}
