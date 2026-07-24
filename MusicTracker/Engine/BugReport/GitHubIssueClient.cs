using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MusicTracker.Engine.BugReport
{
    /// <summary>
    /// Minimal GitHub REST client that creates an issue on the project repo (used by the "Signaler un bug" dialog).
    /// Auth is a token BAKED IN AT BUILD TIME (see the GenerateBuildSecrets target in the .csproj + <see cref="BugReportConfig"/>),
    /// scoped to Issues:write on the single repo — never committed to the repository.
    ///
    /// A fresh <see cref="HttpClient"/> is created per call on purpose (same rationale as <see cref="AI.AIClient"/>:
    /// avoids a wedged "a request is already in progress" state after a cancellation/crash).
    /// </summary>
    public static class GitHubIssueClient
    {
        /// <summary>
        /// Create an issue with <paramref name="title"/> / <paramref name="body"/> (+ optional labels) on the
        /// configured repo. Returns the public URL of the new issue. Throws a readable
        /// <see cref="InvalidOperationException"/> on any configuration or API error.
        /// </summary>
        public static async Task<string> CreateIssueAsync(string title, string body, string[] labels, CancellationToken ct)
        {
            string token = BugReportConfig.Token;
            if (string.IsNullOrWhiteSpace(token))
                throw new InvalidOperationException(
                    "Aucun jeton GitHub n'est intégré à cette version : le rapport de bug n'est pas configuré pour ce build.");

            string repo = BugReportConfig.Repo; // "owner/name"
            if (string.IsNullOrWhiteSpace(repo) || !repo.Contains("/"))
                throw new InvalidOperationException("Dépôt GitHub non configuré (clé GitHubRepo dans App.config).");

            string endpoint = $"https://api.github.com/repos/{repo}/issues";

            object payload = (labels != null && labels.Length > 0)
                ? (object)new { title, body, labels }
                : new { title, body };

            using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) })
            using (var req = new HttpRequestMessage(HttpMethod.Post, endpoint))
            {
                // GitHub requires a User-Agent; token auth via Bearer; pin the REST API version.
                req.Headers.UserAgent.ParseAdd("MusicTracker-BugReporter");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());
                req.Headers.Accept.ParseAdd("application/vnd.github+json");
                req.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
                req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

                HttpResponseMessage resp;
                try { resp = await http.SendAsync(req, ct).ConfigureAwait(false); }
                catch (TaskCanceledException) when (!ct.IsCancellationRequested)
                { throw new InvalidOperationException("Délai dépassé en contactant GitHub."); }
                catch (Exception ex)
                { throw new InvalidOperationException("Impossible de contacter GitHub : " + ex.Message); }

                string respBody = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                    throw new InvalidOperationException($"GitHub a répondu {(int)resp.StatusCode} : {ShortError(respBody)}");

                try
                {
                    using (var doc = JsonDocument.Parse(respBody))
                    {
                        if (doc.RootElement.TryGetProperty("html_url", out var url) && url.ValueKind == JsonValueKind.String)
                            return url.GetString();
                    }
                }
                catch { /* fall through to a generic success */ }
                return $"https://github.com/{repo}/issues";
            }
        }

        // Pull a readable message out of a GitHub error body ({"message": "...", "errors":[...]}), else the raw text.
        static string ShortError(string payload)
        {
            try
            {
                using (var doc = JsonDocument.Parse(payload))
                {
                    if (doc.RootElement.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String)
                        return m.GetString();
                }
            }
            catch { }
            return payload.Length > 300 ? payload.Substring(0, 300) + "…" : payload;
        }
    }
}
