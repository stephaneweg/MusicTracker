using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using KotonStudio.Library;

namespace KotonPluginAuroraShimmer
{
    /// <summary>Éditeur code-only : génère automatiquement un slider par KotonParameter.
    /// Layout 2 colonnes, chaque param sur une ligne (label + slider + valeur).</summary>
    public sealed class AuroraEditor : UserControl, IKotonEditor
    {
        readonly AuroraPlugin _plugin;
        public AuroraEditor(AuroraPlugin plugin)
        {
            _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
            MinWidth = 520; MinHeight = 360; Background = Brushes.Transparent;
            var grid = new Grid { Margin = new Thickness(14) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            int nParams = _plugin.Parameters.Count;
            int nRows = (nParams + 1) / 2;
            for (int r = 0; r < nRows; r++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (int i = 0; i < nParams; i++)
            {
                var kp = _plugin.Parameters[i];
                var panel = BuildParamPanel(kp);
                Grid.SetRow(panel, i / 2);
                Grid.SetColumn(panel, (i % 2) * 2);
                grid.Children.Add(panel);
            }
            Content = grid;
        }
        StackPanel BuildParamPanel(KotonParameter kp)
        {
            var sp = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
            var header = new Grid();
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var lbl = new TextBlock { Text = kp.Name, Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0)), FontSize = 11 };
            var val = new TextBlock { Text = FormatVal(kp), Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)), FontSize = 10, HorizontalAlignment = HorizontalAlignment.Right };
            Grid.SetColumn(lbl, 0); Grid.SetColumn(val, 1);
            header.Children.Add(lbl); header.Children.Add(val);
            sp.Children.Add(header);
            var s = new Slider { Minimum = kp.Min, Maximum = kp.Max, Value = kp.Value, TickFrequency = (kp.Max - kp.Min) / 100, IsSnapToTickEnabled = false };
            s.ValueChanged += (o, e) => { kp.Value = e.NewValue; val.Text = FormatVal(kp); };
            sp.Children.Add(s);
            return sp;
        }
        static string FormatVal(KotonParameter kp)
        {
            string unit = string.IsNullOrEmpty(kp.Unit) ? "" : " " + kp.Unit;
            return (kp.Max - kp.Min > 10) ? kp.Value.ToString("F0", CultureInfo.InvariantCulture) + unit
                                          : kp.Value.ToString("F2", CultureInfo.InvariantCulture) + unit;
        }
        public void OnContextUpdated(KotonRenderContext ctx) { }
    }
}
