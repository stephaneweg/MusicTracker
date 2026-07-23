using System;
using System.IO;
using System.Windows;
using MusicTracker.Engine;

namespace MusicTracker
{
    /// <summary>
    /// The single place that decides whether audio playback is possible, and that tells the user when it
    /// is not. SoundFonts are hundreds of MB, so they are neither version-controlled nor copied by the
    /// build: a fresh install legitimately has none. Before this guard that failed SILENTLY — the preset
    /// table was simply empty, so Play appeared to do nothing at all with no explanation.
    ///
    /// Every playback entry point (timeline, riff / rhythm / chord previews, WAV export) calls
    /// <see cref="EnsureReady"/> first, so the diagnostic message lives in exactly one place.
    /// </summary>
    public static class SoundFontGuard
    {
        /// <summary>True when a usable SoundFont is loaded, i.e. playback can produce sound.</summary>
        public static bool IsReady => InstrumentCatalog.IsSoundFontLoaded;

        /// <summary>
        /// Returns true when playback can proceed. Otherwise explains the problem — and where to put the
        /// file — and returns false so the caller aborts instead of playing silence.
        /// </summary>
        /// <param name="owner">Window to centre the message on (may be null).</param>
        /// <param name="action">What the user was trying to do, e.g. "Lecture", "Export". Used as the title.</param>
        public static bool EnsureReady(Window owner = null, string action = "Lecture")
        {
            if (IsReady) return true;

            MessageBox.Show(owner ?? Application.Current?.MainWindow,
                            BuildMessage(), action, MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        /// <summary>The diagnostic text, exposed separately so a status bar / startup notice can reuse it.</summary>
        public static string BuildMessage()
        {
            string folder = AppPaths.Local(AppSettings.SoundFontFolder);
            string attempted = InstrumentCatalog.LastAttemptedSoundFont;
            string reason = InstrumentCatalog.SoundFontProblem ?? "Aucun SoundFont chargé.";

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("Aucun SoundFont utilisable : aucun son ne peut être produit.");
            sb.AppendLine();
            sb.AppendLine("Raison : " + reason);
            if (!string.IsNullOrEmpty(attempted))
                sb.AppendLine("Fichier attendu : " + attempted);
            sb.AppendLine();
            sb.AppendLine("Pour corriger, placez un fichier .sf2 dans :");
            sb.AppendLine("    " + folder);
            sb.AppendLine("puis sélectionnez-le dans Réglages → Audio.");
            sb.AppendLine();
            sb.Append("Les SoundFonts ne sont pas livrés avec l'application (plusieurs centaines de Mo). ");
            sb.Append("MuseScore_General.sf2 est un bon choix par défaut.");
            return sb.ToString();
        }

        /// <summary>
        /// Startup notice: same diagnostic, shown once so the problem is known before the first Play
        /// rather than being discovered as unexplained silence.
        /// </summary>
        public static void CheckAtStartup(Window owner = null)
        {
            if (IsReady) return;
            MessageBox.Show(owner, BuildMessage(), "SoundFont", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
