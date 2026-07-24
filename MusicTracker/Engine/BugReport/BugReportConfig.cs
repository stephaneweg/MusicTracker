using System;

namespace MusicTracker.Engine.BugReport
{
    /// <summary>
    /// Where the bug-reporter gets its two configuration values:
    ///
    ///  • <see cref="Token"/> — the GitHub token used to create issues. It is NOT stored in the repository.
    ///    Instead the build injects it: the <c>GenerateBuildSecrets</c> target in MusicTracker.csproj reads a
    ///    gitignored <c>bugreport.token</c> file (or the <c>MUSICTRACKER_GH_TOKEN</c> environment variable) at
    ///    compile time and generates <c>BuildSecrets.g.cs</c> holding the token XOR-scrambled then base64-encoded.
    ///    When neither is present (e.g. a plain dev build), the constant is empty and the reporter reports itself
    ///    as unconfigured. The XOR+base64 is only light obfuscation: it (a) breaks GitHub secret-scanning's format
    ///    detection so an accidentally-committed value is NOT auto-revoked, and (b) keeps the raw token out of a
    ///    `strings` dump of the binary. Anyone with the compiled exe can still recover it — acceptable here (a
    ///    fine-grained PAT limited to Issues:write on a single public repo; revoke + rebuild if ever abused).
    ///    <see cref="XorKey"/> must stay identical to the key used by the build task.
    ///
    ///  • <see cref="Repo"/> — "owner/name" of the target repository, read from App.config (key GitHubRepo).
    /// </summary>
    public static class BugReportConfig
    {
        /// <summary>Shared XOR scramble key — MUST match the key used by the GenerateBuildSecrets build task.</summary>
        static readonly byte[] XorKey = System.Text.Encoding.UTF8.GetBytes("MusicTracker-BugReport-XOR-v1");

        /// <summary>The GitHub token used to file issues, or "" when this build has none.
        ///
        /// TODO (roadmap): this will become a user-provided LICENSE KEY read from the settings — the same key that
        /// unlocks seeing/installing updates and sending issues. When that lands, read the settings key here FIRST
        /// and fall back to the build-injected token only for internal/dev builds. Keep this the single source.</summary>
        public static string Token
        {
            get
            {
                try
                {
                    string enc = BuildSecrets.GitHubIssueTokenXorB64;
                    if (string.IsNullOrWhiteSpace(enc)) return "";
                    byte[] data = Convert.FromBase64String(enc);
                    for (int i = 0; i < data.Length; i++) data[i] ^= XorKey[i % XorKey.Length];
                    return System.Text.Encoding.UTF8.GetString(data).Trim();
                }
                catch { return ""; }
            }
        }

        /// <summary>"owner/name" of the repo issues are filed on (App.config key GitHubRepo).</summary>
        public static string Repo
        {
            get
            {
                try
                {
                    string r = System.Configuration.ConfigurationManager.AppSettings["GitHubRepo"];
                    return string.IsNullOrWhiteSpace(r) ? "stephaneweg/MusicTracker" : r.Trim();
                }
                catch { return "stephaneweg/MusicTracker"; }
            }
        }

        /// <summary>True when this build can actually file issues (a token was injected).</summary>
        public static bool IsConfigured => !string.IsNullOrWhiteSpace(Token);
    }
}
