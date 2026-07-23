using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace MusicTracker.Dialogs
{
    /// <summary>
    /// Dialog that edits a single Riff, hosting the piano-roll <see cref="Controls.RiffGridControl"/>
    /// (same editor as the timeline). Returns DialogResult=true when the user saves. Edits are committed
    /// to the riff only on save (cancel discards).
    /// </summary>
    public partial class RiffEditorWindow : Window
    {
        readonly Engine.Riff riff;

        public RiffEditorWindow(Engine.Riff riff)
        {
            InitializeComponent();
            this.riff = riff;
            Loaded += RiffEditorWindow_Loaded;
        }

        private void RiffEditorWindow_Loaded(object sender, RoutedEventArgs e)
        {
            txtName.Text = riff.Name;
            cboPreview.ItemsSource = Engine.InstrumentCatalog.Names();
            cboPreview.SelectedIndex = 0; // preview-only; not stored in the riff
            grid.Configure(riff, Engine.InstrumentCatalog.GetPreset(cboPreview.SelectedIndex), cboPreview.SelectedIndex);
        }

        private void cboPreview_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (cboPreview.SelectedIndex < 0) return;
            grid.SetPreviewInstrument(Engine.InstrumentCatalog.GetPreset(cboPreview.SelectedIndex), cboPreview.SelectedIndex);
        }

        private void btnSave_Click(object sender, RoutedEventArgs e)
        {
            riff.Name = string.IsNullOrWhiteSpace(txtName.Text) ? "Riff" : txtName.Text.Trim();
            riff.Slices = grid.CurrentSlices();
            riff.SlicesPerQuarter = grid.Spb;
            grid.StopPreview();
            DialogResult = true;
            Close();
        }

        private void btnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        // Chrome-less window: dragging the title strip moves it.
        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed) DragMove();
        }

        private void Window_Closed(object sender, EventArgs e) => grid.StopPreview();
    }
}
