using System.Windows;
using System.Windows.Controls;

namespace MusicTracker.Dialogs
{
    /// <summary>Small dialog shown before creating a starter template: pick how many measures the new project spans.</summary>
    public partial class TemplateMeasuresDialog : Window
    {
        static readonly int[] Presets = { 16, 32, 48, 64, 96, 128 };

        /// <summary>The chosen number of measures (valid once ShowDialog() returned true).</summary>
        public int Measures { get; private set; } = 32;

        public TemplateMeasuresDialog(string templateName = null)
        {
            InitializeComponent();
            if (!string.IsNullOrEmpty(templateName)) txtTitle.Text = "Modèle : " + templateName;

            foreach (int n in Presets)
            {
                var chip = new RadioButton
                {
                    Content = n.ToString(),
                    Style = (Style)FindResource("CountChip"),
                    IsChecked = n == Measures,
                    Tag = n,
                };
                chip.Checked += (s, e) => { if (((RadioButton)s).Tag is int v) Measures = v; };
                chips.Children.Add(chip);
            }
        }

        void btnOk_Click(object sender, RoutedEventArgs e) => DialogResult = true;
        void btnCancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    }
}
