using System.Collections.ObjectModel;

namespace MusicTracker
{
    /// <summary>
    /// In-memory riffs of the CURRENT music (graph mode). Riffs belong to the music: they are saved
    /// inside the .graph file and reloaded when it is opened — NOT in a global file.
    /// </summary>
    public class RiffLibrary
    {
        static readonly RiffLibrary _instance = new RiffLibrary();
        public static RiffLibrary Instance { get { return _instance; } }

        public ObservableCollection<Engine.Riff> Riffs { get; } = new ObservableCollection<Engine.Riff>();
    }
}
