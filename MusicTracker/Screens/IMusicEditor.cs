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

        /// <summary>Le document a-t-il changé depuis le dernier enregistrement (ou son ouverture) ? Sert à
        /// l'astérisque de l'onglet et à la confirmation avant fermeture — sans quoi une heure de travail
        /// disparaît d'un clic sur ✕, sans le moindre avertissement.</summary>
        bool IsDirty { get; }

        // Open a file into this editor. The editor decides from the extension how to load it (its own
        // native format, or import a .mid/.mscz/.mscx). Keeps MainWindow's OpenPath thin.
        void LoadFile(string path);
    }
}
