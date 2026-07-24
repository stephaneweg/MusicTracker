using System.Windows;
using MusicTracker.Engine.Update;

namespace MusicTracker.Dialogs
{
    /// <summary>Prompts the user that a newer version is available (with notes), returning DialogResult=true to update.</summary>
    public partial class UpdateDialog : Window
    {
        public UpdateDialog(UpdateInfo info)
        {
            InitializeComponent();
            txtVersions.Text = "Version " + info.Version + " (actuelle : " + UpdateChecker.CurrentVersion + ").";
            if (string.IsNullOrWhiteSpace(info.Notes))
            {
                txtNotesLabel.Visibility = Visibility.Collapsed;
                notesBox.Visibility = Visibility.Collapsed;
            }
            else txtNotes.Text = info.Notes;
        }

        private void btnLater_Click(object sender, RoutedEventArgs e) { DialogResult = false; Close(); }
        private void btnUpdate_Click(object sender, RoutedEventArgs e) { DialogResult = true; Close(); }
    }
}
