using System;
using System.Configuration;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MusicTracker.Engine.BugReport;

namespace MusicTracker.Engine.Update
{
    /// <summary>Parsed result of the update check.</summary>
    public sealed class UpdateInfo
    {
        public Version Version;       // version advertised by the manifest
        public string InstallerPath;  // repo-relative path of the installer in the releases repo
        public string Notes;          // optional human-readable release notes
        public bool IsNewer;          // true when Version is strictly newer than the running build

        public string InstallerFileName =>
            string.IsNullOrWhiteSpace(InstallerPath) ? "MusicTrackerSetup.exe" : Path.GetFileName(InstallerPath);
    }

    /// <summary>
    /// Startup update check. Reads a <c>latest.json</c> manifest from the (private) releases repo via the GitHub
    /// contents API, compares its version to the running build, and — on the user's confirmation — downloads the
    /// installer and launches it. Authentication reuses the build-injected token (<see cref="BugReportConfig.Token"/>);
    /// that token must have Contents:read on the releases repo. The repo is read from App.config (GitHubReleasesRepo).
    ///
    /// Manifest shape (latest.json at the repo root):
    ///   { "version": "1.1.0.0", "installer": "MusicTrackerSetup-1.1.0.exe",
    ///     "notes": "…(fr, fallback)…", "notesFr": "…", "notesEn": "…" }
    /// The changelog shown in the update popup follows the UI language (notesEn in English, notesFr in French),
    /// falling back to the language-neutral "notes" field when a localized one is absent.
    /// </summary>
    public static class UpdateChecker
    {
        /// <summary>Byte progress of the installer download (Total ≤ 0 when the server omits Content-Length).</summary>
        public struct DownloadProgress { public long Received; public long Total; }

        public static Version CurrentVersion =>
            Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0, 0);

        /// <summary>"owner/name" of the releases repo (App.config key GitHubReleasesRepo).</summary>
        static string ReleasesRepo
        {
            get
            {
                try
                {
                    string r = ConfigurationManager.AppSettings["GitHubReleasesRepo"];
                    return string.IsNullOrWhiteSpace(r) ? "stephaneweg/MusicTracker_Releases" : r.Trim();
                }
                catch { return "stephaneweg/MusicTracker_Releases"; }
            }
        }

        /// <summary>True when this build can check for updates (a token was injected and a repo is configured).</summary>
        public static bool IsConfigured => !string.IsNullOrWhiteSpace(BugReportConfig.Token) && ReleasesRepo.Contains("/");

        /// <summary>
        /// Fetch and parse the manifest. Returns null when updates aren't configured or the manifest is unusable;
        /// throws on a network / API error (callers treat the check as best-effort and swallow it).
        /// </summary>
        public static async Task<UpdateInfo> CheckAsync(CancellationToken ct)
        {
            if (!IsConfigured) return null;

            string json;
            using (var http = NewClient(TimeSpan.FromSeconds(30)))
            using (var req = NewGet("latest.json"))
            {
                var resp = await http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
                if (resp.StatusCode == HttpStatusCode.NotFound) return null; // no manifest published yet
                if (!resp.IsSuccessStatusCode)
                    throw new InvalidOperationException("GitHub a répondu " + (int)resp.StatusCode + " en lisant latest.json.");
                json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            }

            using (var doc = JsonDocument.Parse(json))
            {
                var root = doc.RootElement;
                if (!root.TryGetProperty("version", out var vEl) || vEl.ValueKind != JsonValueKind.String) return null;
                if (!Version.TryParse(vEl.GetString(), out var v)) return null;
                string installer = root.TryGetProperty("installer", out var iEl) && iEl.ValueKind == JsonValueKind.String ? iEl.GetString() : null;
                string notes = root.TryGetProperty("notes", out var nEl) && nEl.ValueKind == JsonValueKind.String ? nEl.GetString() : "";
                // Language-specific changelog (falls back to the neutral "notes" field).
                string Field(string key) => root.TryGetProperty(key, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;
                string localized = MusicTracker.Localization.Loc.IsEnglish ? (Field("notesEn") ?? Field("notes_en")) : (Field("notesFr") ?? Field("notes_fr"));
                if (!string.IsNullOrWhiteSpace(localized)) notes = localized;
                return new UpdateInfo { Version = v, InstallerPath = installer, Notes = notes, IsNewer = v > CurrentVersion };
            }
        }

        /// <summary>
        /// Download the installer referenced by the manifest to <paramref name="destPath"/> (streamed to a ".part"
        /// file, moved into place on success), reporting byte progress. Throws on any failure.
        /// </summary>
        public static async Task DownloadInstallerAsync(UpdateInfo info, string destPath, IProgress<DownloadProgress> progress, CancellationToken ct)
        {
            if (info == null || string.IsNullOrWhiteSpace(info.InstallerPath))
                throw new InvalidOperationException("Le manifeste de mise à jour ne référence aucun installeur.");

            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            string part = destPath + ".part";
            if (File.Exists(part)) File.Delete(part);

            using (var http = NewClient(TimeSpan.FromMinutes(30)))
            using (var req = NewGet(info.InstallerPath))
            using (var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                if (!resp.IsSuccessStatusCode)
                    throw new InvalidOperationException("GitHub a répondu " + (int)resp.StatusCode + " en téléchargeant l'installeur.");
                long total = resp.Content.Headers.ContentLength ?? -1L;
                progress?.Report(new DownloadProgress { Received = 0, Total = total });

                using (var src = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false))
                using (var dst = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
                {
                    var buffer = new byte[1 << 16];
                    long got = 0;
                    int n;
                    while ((n = await src.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false)) > 0)
                    {
                        await dst.WriteAsync(buffer, 0, n, ct).ConfigureAwait(false);
                        got += n;
                        progress?.Report(new DownloadProgress { Received = got, Total = total });
                    }
                }
            }

            if (File.Exists(destPath)) File.Delete(destPath);
            File.Move(part, destPath);
        }

        /// <summary>Launch the downloaded installer (shell-executed) so it can replace this app after it exits.</summary>
        public static void LaunchInstaller(string installerPath)
        {
            Process.Start(new ProcessStartInfo(installerPath) { UseShellExecute = true });
        }

        // ---- helpers ----

        static HttpClient NewClient(TimeSpan timeout) => new HttpClient { Timeout = timeout };

        // A GET against the releases repo's contents API, asking for the RAW bytes (so a file streams back directly
        // rather than the base64-wrapped JSON). Works for private repos with a Contents:read token.
        static HttpRequestMessage NewGet(string repoRelativePath)
        {
            string url = "https://api.github.com/repos/" + ReleasesRepo + "/contents/" + EscapePath(repoRelativePath);
            var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.UserAgent.ParseAdd("MusicTracker-Updater");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", BugReportConfig.Token.Trim());
            req.Headers.Accept.ParseAdd("application/vnd.github.raw");
            req.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            return req;
        }

        // Percent-escape each path segment but keep the '/' separators.
        static string EscapePath(string path)
        {
            var parts = path.Replace('\\', '/').Split('/');
            for (int i = 0; i < parts.Length; i++) parts[i] = Uri.EscapeDataString(parts[i]);
            return string.Join("/", parts);
        }
    }
}
