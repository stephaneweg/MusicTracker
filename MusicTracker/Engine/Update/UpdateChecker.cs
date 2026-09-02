using System;
using System.Configuration;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
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
        public string InstallerPath;  // repo-relative path of the installer (Inno) in the releases repo
        public string PortablePath;   // repo-relative path of the portable zip, when the manifest ships one
        public string Notes;          // optional human-readable release notes
        public bool IsNewer;          // true when Version is strictly newer than the running build

        public string InstallerFileName =>
            string.IsNullOrWhiteSpace(InstallerPath) ? "MusicTrackerSetup.exe" : Path.GetFileName(InstallerPath);

        public string PortableFileName =>
            string.IsNullOrWhiteSpace(PortablePath) ? null : Path.GetFileName(PortablePath);
    }

    /// <summary>
    /// Startup update check. Reads a <c>latest.json</c> manifest from the (private) releases repo via the GitHub
    /// contents API, compares its version to the running build, and — on the user's confirmation — either runs the
    /// Inno installer (mode installé) or downloads the portable zip and hands off to <c>KotonStudioUpdater.exe</c>
    /// (mode portable, détecté par la présence d'un fichier <c>.portable</c> à côté de l'exe).
    /// Authentication reuses the build-injected token (<see cref="BugReportConfig.Token"/>) with Contents:read on
    /// the releases repo. Repo read from App.config (GitHubReleasesRepo).
    ///
    /// Manifest shape (latest.json at the repo root):
    ///   { "version": "1.1.0.0", "installer": "MusicTrackerSetup-1.1.0.exe", "portable": "KotonStudioPortable-1.1.0.zip",
    ///     "notes": "…(fr, fallback)…", "notesFr": "…", "notesEn": "…" }
    /// The changelog shown in the update popup follows the UI language (notesEn in English, notesFr in French),
    /// falling back to the language-neutral "notes" field when a localized one is absent.
    /// </summary>
    public static class UpdateChecker
    {
        /// <summary>Byte progress of the installer/zip download (Total ≤ 0 when Content-Length is missing).</summary>
        public struct DownloadProgress { public long Received; public long Total; }

        /// <summary>Nom du dossier temporaire (à côté de l'exe) où le zip portable est extrait avant que l'updater
        /// ne remplace les fichiers. Nettoyé au démarrage suivant de Koton Studio (l'updater n'a pas à se supprimer
        /// lui-même). Commence par un point → discret dans un explorateur qui ne montre pas les fichiers cachés.</summary>
        public const string PortableStagingDirName = ".update";

        /// <summary>Sous-dossier de <see cref="PortableStagingDir"/> où le zip est EXTRAIT. Le zip lui-même est
        /// téléchargé à la racine du staging : séparer les deux est ce qui permet de nettoyer une extraction
        /// précédente sans emporter l'archive qu'on s'apprête à ouvrir.</summary>
        public const string PortableContentDirName = "content";

        /// <summary>Nom du fichier marqueur présent SEULEMENT dans le zip portable (jamais dans l'installeur Inno).
        /// Sa présence à côté de l'exe = installation portable, donc chemin de mise à jour = updater + zip.</summary>
        public const string PortableMarkerFileName = ".portable";

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

        /// <summary>Vrai si l'installation courante est un déploiement portable (marqueur <c>.portable</c> à côté de
        /// l'exe). Décide du chemin de mise à jour utilisé et n'est PAS lié à l'endroit où l'app est installée.</summary>
        public static bool IsPortableInstall => File.Exists(Path.Combine(AppPaths.BaseDir, PortableMarkerFileName));

        /// <summary>Dossier local (à côté de l'exe) où le zip portable est téléchargé + extrait par le chemin de MàJ
        /// portable. Toujours SUR LE MÊME VOLUME que la cible → move/copie rapides et sans souci de droits.</summary>
        public static string PortableStagingDir => Path.Combine(AppPaths.BaseDir, PortableStagingDirName);

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
                string Field(string key) => root.TryGetProperty(key, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;
                string installer = Field("installer");
                string portable  = Field("portable");
                string notes     = Field("notes") ?? "";
                // Language-specific changelog (falls back to the neutral "notes" field).
                string localized = MusicTracker.Localization.Loc.IsEnglish ? (Field("notesEn") ?? Field("notes_en")) : (Field("notesFr") ?? Field("notes_fr"));
                if (!string.IsNullOrWhiteSpace(localized)) notes = localized;
                return new UpdateInfo { Version = v, InstallerPath = installer, PortablePath = portable, Notes = notes, IsNewer = v > CurrentVersion };
            }
        }

        /// <summary>
        /// Download the installer referenced by the manifest to <paramref name="destPath"/>, reporting byte progress.
        /// Throws on any failure. See <see cref="DownloadPortableZipAsync"/> pour le zip portable.
        /// </summary>
        public static Task DownloadInstallerAsync(UpdateInfo info, string destPath, IProgress<DownloadProgress> progress, CancellationToken ct)
        {
            if (info == null || string.IsNullOrWhiteSpace(info.InstallerPath))
                throw new InvalidOperationException("Le manifeste de mise à jour ne référence aucun installeur.");
            return DownloadRepoFileAsync(info.InstallerPath, destPath, progress, ct);
        }

        /// <summary>Télécharge le ZIP portable du manifeste (champ <c>portable</c>) vers <paramref name="destPath"/>.
        /// Même mécanique de streaming/.part-file que l'installeur.</summary>
        public static Task DownloadPortableZipAsync(UpdateInfo info, string destPath, IProgress<DownloadProgress> progress, CancellationToken ct)
        {
            if (info == null || string.IsNullOrWhiteSpace(info.PortablePath))
                throw new InvalidOperationException("Le manifeste de mise à jour ne référence aucun zip portable.");
            return DownloadRepoFileAsync(info.PortablePath, destPath, progress, ct);
        }

        /// <summary>Launch the downloaded installer (shell-executed) so it can replace this app after it exits.</summary>
        public static void LaunchInstaller(string installerPath)
        {
            Process.Start(new ProcessStartInfo(installerPath) { UseShellExecute = true });
        }

        /// <summary>Extrait le zip portable dans <see cref="PortableStagingDir"/> et renvoie le dossier de contenu
        /// (le zip contient un dossier racine <c>KotonStudio-&lt;ver&gt;/</c>, on renvoie CE dossier). Écrase toute
        /// extraction incomplète précédente.</summary>
        public static string ExtractPortableZip(string zipPath)
        {
            // Extraction dans un SOUS-DOSSIER du staging, et nettoyage de ce seul sous-dossier.
            //
            // La version précédente vidait le dossier de staging entier avant d'extraire — or c'est
            // exactement là que l'appelant vient de télécharger le zip (MainWindow : zipDest =
            // PortableStagingDir\<nom>.zip). Elle effaçait donc son propre fichier d'entrée, puis
            // échouait sur « Could not find file …\.update\KotonStudioPortable-x.y.z.zip ». La mise à
            // jour portable ne pouvait pas aboutir, quel que soit le contenu de l'archive.
            string content = Path.Combine(PortableStagingDir, PortableContentDirName);
            if (Directory.Exists(content))
            {
                try { Directory.Delete(content, recursive: true); }
                catch (IOException) { }             // reste d'un run précédent partiellement verrouillé → on l'écrase par-dessus
                catch (UnauthorizedAccessException) { }
            }
            Directory.CreateDirectory(content);
            ZipFile.ExtractToDirectory(zipPath, content);

            // Le zip contient un unique dossier racine (KotonStudio-<ver>/). On l'identifie sans le nommer, au cas
            // où release.ps1 changerait la convention.
            var dirs = Directory.GetDirectories(content);
            if (dirs.Length == 0) throw new InvalidOperationException("Le zip portable ne contient aucun dossier racine.");
            if (dirs.Length > 1)  throw new InvalidOperationException("Le zip portable contient plusieurs dossiers racine (attendu : un seul).");
            return dirs[0];
        }

        /// <summary>Lance <c>KotonStudioUpdater.exe</c> depuis le dossier extrait <paramref name="sourceDir"/> pour
        /// remplacer les fichiers de <paramref name="targetDir"/> puis relancer <paramref name="launchExe"/>.
        /// L'updater a le PID du processus courant en paramètre — il attendra que Koton Studio se termine avant
        /// d'écrire. L'appelant doit lui-même quitter l'app juste après cet appel.</summary>
        public static void LaunchPortableUpdater(string sourceDir, string targetDir, string launchExe)
        {
            string updater = Path.Combine(sourceDir, "KotonStudioUpdater.exe");
            if (!File.Exists(updater)) throw new FileNotFoundException("KotonStudioUpdater.exe absent du zip portable.", updater);
            int pid = Process.GetCurrentProcess().Id;
            var psi = new ProcessStartInfo(updater)
            {
                UseShellExecute = false,
                CreateNoWindow  = true,
                WorkingDirectory = sourceDir,
                Arguments = string.Format(
                    "--pid {0} --source \"{1}\" --target \"{2}\" --launch \"{3}\"",
                    pid, sourceDir, targetDir, launchExe),
            };
            Process.Start(psi);
        }

        /// <summary>Supprime le dossier de staging portable si présent — appelé UNE FOIS au démarrage de Koton Studio.
        /// C'est le seul moment sûr : l'updater a nécessairement quitté (il nous a relancés) et plus aucun fichier de
        /// staging n'est verrouillé. Silencieux : un échec n'empêche pas le démarrage, on retente au prochain boot.</summary>
        public static void CleanupPortableStaging()
        {
            string stage = PortableStagingDir;
            if (!Directory.Exists(stage)) return;
            for (int i = 0; i < 5; i++)
            {
                try { Directory.Delete(stage, recursive: true); return; }
                catch (IOException) { Thread.Sleep(200); }
                catch (UnauthorizedAccessException) { Thread.Sleep(200); }
            }
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

        // Streaming download avec fichier .part → renommage atomique sur succès.
        static async Task DownloadRepoFileAsync(string repoRelativePath, string destPath, IProgress<DownloadProgress> progress, CancellationToken ct)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            string part = destPath + ".part";
            if (File.Exists(part)) File.Delete(part);

            using (var http = NewClient(TimeSpan.FromMinutes(30)))
            using (var req  = NewGet(repoRelativePath))
            using (var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                if (!resp.IsSuccessStatusCode)
                    throw new InvalidOperationException("GitHub a répondu " + (int)resp.StatusCode + " en téléchargeant " + repoRelativePath + ".");
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
    }
}
