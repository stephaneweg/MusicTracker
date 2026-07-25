using System;
using System.IO;
using System.Windows;
using MusicTracker.Engine;
using MusicTracker.Localization;

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
        public static bool EnsureReady(Window owner = null, string action = "Playback")
        {
            if (IsReady) return true;

            MessageBox.Show(owner ?? Application.Current?.MainWindow,
                            BuildMessage(), Loc.T(action), MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        /// <summary>The diagnostic text, exposed separately so a status bar / startup notice can reuse it.</summary>
        public static string BuildMessage()
        {
            string folder = AppPaths.LocalData(AppSettings.SoundFontFolder);
            string attempted = InstrumentCatalog.LastAttemptedSoundFont;
            string reason = InstrumentCatalog.SoundFontProblem ?? Loc.T("NoSoundFontLoaded");

            var sb = new System.Text.StringBuilder();
            sb.AppendLine(Loc.T("NoUsableSoundFontNoSoundCan"));
            sb.AppendLine();
            sb.AppendLine(Loc.T("Reason") + reason);
            if (!string.IsNullOrEmpty(attempted))
                sb.AppendLine(Loc.T("ExpectedFile") + attempted);
            sb.AppendLine();
            sb.AppendLine(Loc.T("ToFixThisPlaceASf2"));
            sb.AppendLine("    " + folder);
            sb.AppendLine(Loc.T("ThenSelectItInSettingsAudio"));
            sb.AppendLine();
            sb.Append(Loc.T("SoundFontsAreNotShippedWithThe"));
            sb.Append(Loc.T("MuseScoreGeneralSf2IsAGood"));
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
