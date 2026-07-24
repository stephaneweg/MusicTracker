using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace MusicTracker
{
    /// <summary>
    /// Downloads the default MuseScore SoundFont (MuseScore_General.sf2, ≈206 MB) into the app's SoundFont
    /// folder on a fresh install that has none. The file name matches the leaf of
    /// <see cref="Engine.InstrumentCatalog.DefaultSoundFont"/> so, once present, the engine resolves it as the
    /// default automatically (see <see cref="AppSettings.ResolveSoundFontPath"/>).
    ///
    /// SoundFonts are several hundred MB and ship separately (see the .csproj note + <see cref="SoundFontGuard"/>);
    /// this lets the app get playable out of the box without the user hunting for a .sf2 by hand.
    /// </summary>
    public static class SoundFontDownloader
    {
        /// <summary>Official MuseScore "General" SoundFont (full, uncompressed .sf2), from the OSU Open Source Lab mirror.</summary>
        public const string Url = "https://ftp.osuosl.org/pub/musescore/soundfont/MuseScore_General/MuseScore_General.sf2";

        /// <summary>Target file name — MUST equal the leaf of <see cref="Engine.InstrumentCatalog.DefaultSoundFont"/>.</summary>
        public const string FileName = "MuseScore_General.sf2";

        /// <summary>Approximate download size (bytes), for the confirmation prompt.</summary>
        public const long ApproxBytes = 206L * 1024 * 1024;

        /// <summary>Progress report: bytes received so far and the total (Total ≤ 0 when the server omits Content-Length).</summary>
        public struct Progress { public long Received; public long Total; }

        /// <summary>Absolute path the font is written to: the writable per-user local SoundFont folder
        /// (%LocalAppData%\MusicTracker\SoundFont), since Program Files is read-only once installed.</summary>
        public static string TargetPath => AppPaths.LocalData(Path.Combine(AppSettings.SoundFontFolder, FileName));

        /// <summary>
        /// Download the SoundFont to <see cref="TargetPath"/>, streaming to a temporary ".part" file and moving it
        /// into place only on success (so an interrupted download never leaves a truncated .sf2 the engine would
        /// try to load). Reports byte progress via <paramref name="progress"/>. Throws on any network / IO failure.
        /// </summary>
        public static async Task DownloadAsync(IProgress<Progress> progress, CancellationToken ct = default(CancellationToken))
        {
            string dir = AppPaths.LocalData(AppSettings.SoundFontFolder);
            Directory.CreateDirectory(dir);
            string target = TargetPath;
            string part = target + ".part";
            if (File.Exists(part)) File.Delete(part);

            // .NET 4.8 defaults can negotiate an older protocol; the mirror requires TLS 1.2.
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;

            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) }) // generous whole-download ceiling
            using (var resp = await http.GetAsync(Url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                resp.EnsureSuccessStatusCode();
                long total = resp.Content.Headers.ContentLength ?? -1L;
                progress?.Report(new Progress { Received = 0, Total = total });

                using (var srcStream = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false))
                using (var dstStream = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
                {
                    var buffer = new byte[1 << 16];
                    long received = 0;
                    int n;
                    while ((n = await srcStream.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false)) > 0)
                    {
                        await dstStream.WriteAsync(buffer, 0, n, ct).ConfigureAwait(false);
                        received += n;
                        progress?.Report(new Progress { Received = received, Total = total });
                    }
                }
            }

            if (File.Exists(target)) File.Delete(target);
            File.Move(part, target);
        }
    }
}
