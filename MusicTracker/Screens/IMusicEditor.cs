namespace MusicTracker.Screens
{
    /// <summary>Common contract for the editor screens hosted by MainWindow (the timeline).</summary>
    public interface IMusicEditor
    {
        string ModeName { get; }       // e.g. "Séquenceur"
        string FileExtension { get; }  // ".sq" (the native save format)
        string CurrentPath { get; set; }
        bool Save(string path);
        void StopAudio();

        // Open a file into this editor. The editor decides from the extension how to load it (its own
        // native format, or import a .mid/.mscz/.mscx). Keeps MainWindow's OpenPath thin.
        void LoadFile(string path);
    }
}
