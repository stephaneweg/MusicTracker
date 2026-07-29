using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using MusicTracker.Engine.Timeline;
using MusicTracker.Localization;

namespace MusicTracker.Dialogs
{
    /// <summary>
    /// Per-track MIXER: volume + stereo pan + mute/solo for every track. The rows bind TWO-WAY to the
    /// <see cref="TimelineTrack"/> properties (which notify), so editing here also updates the timeline header
    /// (and vice versa), and the player reads them live each buffer → moving a fader while playing is heard at once.
    /// Non-modal (opened with Show), so the transport stays usable.
    /// </summary>
    public partial class MixerDialog : Window
    {
        public MixerDialog(TimelineProject project, Window owner)
        {
            InitializeComponent();
            Owner = owner;
            list.ItemsSource = project?.Tracks;
        }

        void Center_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is TimelineTrack t) t.Pan = 0;
        }

        // Ramène la piste à la profondeur par défaut (offset 0) — l'état de tout projet antérieur à ce réglage.
        void ReverbDefault_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is TimelineTrack t) t.ReverbOffset = 0;
        }

        void Close_Click(object sender, RoutedEventArgs e) => Close();

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.Key == Key.Escape) Close();
            base.OnKeyDown(e);
        }
    }

    /// <summary>Pan value (-1..+1) → a short label: "G xx%" (left) / "Centre" / "D xx%" (right).</summary>
    public sealed class PanTextConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            double pan = value is double d ? d : 0;
            if (pan < -0.02) return Loc.T("PanL") + " " + Math.Round(-pan * 100) + "%";
            if (pan > 0.02) return Loc.T("PanR") + " " + Math.Round(pan * 100) + "%";
            return Loc.T("PanCentre");
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    }
}
