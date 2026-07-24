using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MusicTracker
{
    /// <summary>
    /// Resolves app data files/folders relative to the ASSEMBLY's directory (where the .exe lives),
    /// NOT the current working directory. The two differ when the app is launched via a file
    /// association or "Open with" — the working directory is then the opened file's folder (or
    /// System32), so a plain relative path like "SoundFont\\roland.sf2" or "recent.json" would miss.
    /// </summary>
    public static class AppPaths
    {
        /// <summary>The directory containing the running executable (trailing separator included).</summary>
        public static string BaseDir { get; } = AppContext.BaseDirectory;

        /// <summary>Resolve <paramref name="relative"/> against the assembly directory. An already-rooted
        /// (absolute) path is returned unchanged; an empty value returns the base directory.</summary>
        public static string Local(string relative)
        {
            if (string.IsNullOrEmpty(relative)) return BaseDir;
            return Path.IsPathRooted(relative) ? relative : Path.Combine(BaseDir, relative);
        }

        /// <summary>The per-user, roaming app-data folder for MusicTracker (<c>%AppData%\MusicTracker</c>), created
        /// on demand. User-writable state (settings, recent list, user data) lives here — NOT next to the .exe,
        /// which is read-only once the app is installed under Program Files.</summary>
        public static string RoamingDir { get; } =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MusicTracker");

        /// <summary>Resolve <paramref name="relative"/> against <see cref="RoamingDir"/>, ensuring the parent folder
        /// exists. An already-rooted path is returned unchanged.</summary>
        public static string Roaming(string relative)
        {
            if (string.IsNullOrEmpty(relative)) { TryCreate(RoamingDir); return RoamingDir; }
            string full = Path.IsPathRooted(relative) ? relative : Path.Combine(RoamingDir, relative);
            TryCreate(Path.GetDirectoryName(full));
            return full;
        }

        /// <summary>Roaming path for a user-data file, migrating a legacy copy (an older build wrote it next to the
        /// .exe) into the roaming folder on first use, so upgrading users keep their settings/recents.</summary>
        public static string UserFile(string relative)
        {
            string roaming = Roaming(relative);
            try
            {
                string legacy = Local(relative);
                if (!File.Exists(roaming) && File.Exists(legacy))
                    File.Copy(legacy, roaming, false);
            }
            catch { /* best-effort migration */ }
            return roaming;
        }

        static void TryCreate(string dir)
        {
            try { if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir); } catch { }
        }

        // ---- Data overlay: bundled assets (next to the .exe, read-only once installed) + user-created files
        //      (roaming, writable). Reads prefer the roaming copy; new/generated files are written to roaming. ----

        /// <summary>Resolve a bundled-or-user Data file, preferring the user's roaming copy when it exists,
        /// otherwise the bundled one next to the .exe.</summary>
        public static string DataRead(string relative)
        {
            string roaming = Roaming(relative);
            return File.Exists(roaming) ? roaming : Local(relative);
        }

        /// <summary>Where a newly created / imported / AI-generated Data file must be written: the roaming folder
        /// (Program Files is read-only once installed). The parent directory is created.</summary>
        public static string DataWrite(string relative) => Roaming(relative);

        /// <summary>All files matching <paramref name="pattern"/> in a Data sub-folder, merged across the bundled
        /// and roaming locations — a user copy shadows a bundled one of the same name.</summary>
        public static List<string> DataFiles(string relativeDir, string pattern)
        {
            var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var dir in new[] { Local(relativeDir), Roaming(relativeDir) })
            {
                try { if (Directory.Exists(dir)) foreach (var f in Directory.GetFiles(dir, pattern)) byName[Path.GetFileName(f)] = f; }
                catch { }
            }
            return byName.Values.ToList();
        }
    }
}
