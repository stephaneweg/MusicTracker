using System.Windows;

namespace MusicTracker.Dialogs
{
    /// <summary>Small themed modeless dialog with a progress bar + status, shown during imports.</summary>
    public partial class ImportProgressDialog : Window
    {
        public ImportProgressDialog()
        {
            InitializeComponent();
        }

        /// <summary>Update the bar (0..1) and status text. Must be called on the UI thread.</summary>
        public void Set(double fraction, string status)
        {
            bar.IsIndeterminate = false;
            bar.Value = fraction;
            txtStatus.Text = status;
        }

        /// <summary>Show an indeterminate (marquee) bar with a status message.</summary>
        public void SetBusy(string status)
        {
            bar.IsIndeterminate = true;
            txtStatus.Text = status;
        }
    }
}

