using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using MusicTracker.Engine.Timeline;

namespace MusicTracker.Dialogs
{
    /// <summary>
    /// Dialogue "Pistes a exporter" — apres qu'un chemin de fichier soit choisi (n'importe quel format
    /// d'export : MIDI, MusicXML, MuseScore .mscx, PDF, WAV/MP3), on demande a l'utilisateur QUELLES
    /// pistes du projet il veut inclure. Cochees par defaut sauf celles qui sont muted (Mute=true)
    /// qui sont decochees. Le user peut cocher/decocher, tout cocher/decocher, puis valider ou
    /// annuler. Les exporteurs iterent uniquement sur les pistes retournees.
    ///
    /// <see cref="Result"/> = <c>null</c> si annule, sinon la liste des pistes cochees dans l'ordre
    /// du projet.
    /// </summary>
    public partial class TrackSelectionDialog : Window
    {
        public sealed class Row
        {
            public TimelineTrack Track { get; set; }
            public string Name { get; set; }
            public string Tag { get; set; }         // sous-libelle type (Instrument, Batterie, Accords, Repeat...)
            public bool Include { get; set; }
        }

        public ObservableCollection<Row> Items { get; } = new ObservableCollection<Row>();
        public List<TimelineTrack> Result { get; private set; }

        public TrackSelectionDialog(TimelineProject project)
        {
            InitializeComponent();
            lstTracks.ItemsSource = Items;
            if (project?.Tracks == null) return;
            foreach (var t in project.Tracks)
            {
                Items.Add(new Row
                {
                    Track = t,
                    Name = string.IsNullOrWhiteSpace(t.Name) ? t.Type.ToString() : t.Name,
                    Tag = t.Type == TimelineTrackType.Drum ? "· batterie"
                        : t.Type == TimelineTrackType.Chord ? "· accords"
                        : "",
                    Include = !t.Mute,   // decoche par defaut les mutees
                });
            }
        }

        void AllCheck_Click(object sender, RoutedEventArgs e) { foreach (var r in Items) r.Include = true; lstTracks.Items.Refresh(); }
        void NoneCheck_Click(object sender, RoutedEventArgs e) { foreach (var r in Items) r.Include = false; lstTracks.Items.Refresh(); }

        void Ok_Click(object sender, RoutedEventArgs e)
        {
            Result = Items.Where(r => r.Include).Select(r => r.Track).ToList();
            DialogResult = true;
            Close();
        }
        void Cancel_Click(object sender, RoutedEventArgs e) { Result = null; DialogResult = false; Close(); }
    }
}
