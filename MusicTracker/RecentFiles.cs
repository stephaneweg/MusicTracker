using System;
using System.Collections.ObjectModel;

namespace MusicTracker
{
    public class RecentEntry
    {
        public string Path { get; set; }
        public string Mode { get; set; }   // "Graphe" or "Séquenceur"
        public string Name { get; set; }

        /// <summary>The containing folder (for a subtle second line in the recents list). Not persisted.</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public string Folder
        {
            get { try { return string.IsNullOrEmpty(Path) ? "" : System.IO.Path.GetDirectoryName(Path); } catch { return ""; } }
        }
    }

    /// <summary>Recently saved/opened musics, persisted to recent.json.</summary>
    public class RecentFiles
    {
        static readonly RecentFiles _instance = Load();
        public static RecentFiles Instance { get { return _instance; } }

        public ObservableCollection<RecentEntry> Entries { get; set; } = new ObservableCollection<RecentEntry>();

        public void Add(string path, string mode)
        {
            for (int i = Entries.Count - 1; i >= 0; i--)
            {
                if (string.Equals(Entries[i].Path, path, StringComparison.OrdinalIgnoreCase))
                    Entries.RemoveAt(i);
            }
            Entries.Insert(0, new RecentEntry
            {
                Path = path,
                Mode = mode,
                Name = System.IO.Path.GetFileNameWithoutExtension(path),
            });
            while (Entries.Count > 12) Entries.RemoveAt(Entries.Count - 1);
            Save();
        }

        public void Save()
        {
            try { System.IO.File.WriteAllText(AppPaths.Local("recent.json"), System.Text.Json.JsonSerializer.Serialize(this)); }
            catch { /* best-effort */ }
        }

        public static RecentFiles Load()
        {
            string path = AppPaths.Local("recent.json");
            if (System.IO.File.Exists(path))
            {
                try { return System.Text.Json.JsonSerializer.Deserialize<RecentFiles>(System.IO.File.ReadAllText(path)); }
                catch { }
            }
            return new RecentFiles();
        }
    }
}
