using System.Windows;

namespace MusicTracker.Dialogs
{
    /// <summary>Themed options for a graph import: how many measures per riff and the slice grid.</summary>
    public partial class ImportGraphOptionsDialog : Window
    {
        public int MeasuresPerRiff { get; private set; } = 1;
        public int SlicesPerBeat { get; private set; } = 24;
        public bool ImportVolume { get; private set; } = true;

        public ImportGraphOptionsDialog()
        {
            InitializeComponent();
        }

        /// <summary>Hide the volume option (timeline import always imports dynamics as automation points,
        /// without splitting riffs — so the checkbox isn't relevant there).</summary>
        public void HideVolumeOption()
        {
            chkVolume.Visibility = System.Windows.Visibility.Collapsed;
            txtVolumeNote.Visibility = System.Windows.Visibility.Collapsed;
            ImportVolume = true;
        }

        static int ParseBox(System.Windows.Controls.ComboBox box, int fallback, int min, int max)
        {
            // IsEditable combo: the chosen/typed value is in Text (ComboBoxItem.Content for presets).
            string s = (box.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content?.ToString() ?? box.Text;
            if (int.TryParse((s ?? "").Trim(), out int v) && v >= min && v <= max) return v;
            return fallback;
        }

        private void btnOk_Click(object sender, RoutedEventArgs e)
        {
            MeasuresPerRiff = ParseBox(cboMeasures, 1, 1, 999);
            SlicesPerBeat = ParseBox(cboPrecision, 24, 1, 192);
            // When the volume option is hidden (timeline import), always import volume (as points).
            ImportVolume = chkVolume.Visibility != System.Windows.Visibility.Visible || chkVolume.IsChecked == true;
            DialogResult = true;
        }

        private void btnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}

