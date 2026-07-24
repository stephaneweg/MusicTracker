using System.Reflection;
using System.Windows;

namespace MusicTracker.Dialogs
{
    /// <summary>"À propos" screen: app name + version, author, and third-party credits (MeltySynth MIT, etc.).</summary>
    public partial class AboutDialog : Window
    {
        public AboutDialog()
        {
            InitializeComponent();
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            if (v != null) txtVersion.Text = "Version " + v;
        }

        private void btnClose_Click(object sender, RoutedEventArgs e) => Close();
    }
}
