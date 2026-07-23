using System;
using System.IO;

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
    }
}
