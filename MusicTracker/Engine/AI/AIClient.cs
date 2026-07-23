using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MusicTracker.Engine.AI
{
    /// <summary>
    /// Shared plumbing for the AI provider clients (Mistral/Groq/DeepSeek/Gemini/Claude). Each provider
    /// exposes its own <c>CompleteJsonAsync</c> that builds the request body + reads the response shape,
    /// but the HTTP round-trip and error handling live here so they stay identical across providers.
    /// </summary>
    public class AIClient
    {
        // Pull a readable message out of an error body ({"message":...} or {"error":{"message":...}}), else the raw text.
        public static string ShortError(string payload)
        {
            try
            {
                using (var doc = JsonDocument.Parse(payload))
                {
                    var root = doc.RootElement;
                    if (root.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String) return m.GetString();
                    if (root.TryGetProperty("error", out var e))
                    {
                        if (e.ValueKind == JsonValueKind.String) return e.GetString();
                        if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty("message", out var em)) return em.GetString();
                    }
                }
            }
            catch { }
            return payload.Length > 300 ? payload.Substring(0, 300) + "…" : payload;
        }

        // Serialize a body object into a JSON request content.
        protected static StringContent JsonBody(object body)
            => new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        /// <summary>
        /// POST <paramref name="req"/> on <paramref name="http"/> and return the raw response payload.
        /// Throws a readable <see cref="InvalidOperationException"/> (named after <paramref name="provider"/>)
        /// on timeout, connection failure, or non-2xx status. Callers own body-building and response parsing.
        /// </summary>
        protected static async Task<string> SendAsync(HttpRequestMessage req, string provider, CancellationToken ct)
        {
            HttpClient http = new HttpClient { Timeout = TimeSpan.FromSeconds(600) };
            HttpResponseMessage resp;
            try { resp = await http.SendAsync(req, ct).ConfigureAwait(false); }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested) { throw new InvalidOperationException($"Délai dépassé en contactant {provider}."); }
            catch (Exception ex) { throw new InvalidOperationException($"Impossible de contacter {provider} : " + ex.Message); }

            string payload = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException($"{provider} a répondu {(int)resp.StatusCode} : {ShortError(payload)}");
            return payload;
        }

        /// <summary>
        /// Shared implementation for OpenAI-compatible chat-completions providers (Mistral/Groq/DeepSeek):
        /// Bearer auth + <c>response_format: json_object</c>, response read from <c>choices[0].message.content</c>.
        /// Returns the assistant message content (raw JSON). Throws on any API/HTTP error.
        /// </summary>
        protected static async Task<string> OpenAiCompatibleJsonAsync(
            string endpoint, string provider,
            string apiKey, string model, string defaultModel,
            string systemPrompt, string userPrompt, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(apiKey))
                throw new InvalidOperationException($"Clé API {provider} manquante.");

            var body = new
            {
                model = string.IsNullOrWhiteSpace(model) ? defaultModel : model,
                messages = new[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user", content = userPrompt },
                },
                response_format = new { type = "json_object" },
                temperature = 0.7,
            };

            using (var req = new HttpRequestMessage(HttpMethod.Post, endpoint))
            {
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
                req.Content = JsonBody(body);

                string payload = await SendAsync(req, provider, ct).ConfigureAwait(false);
                try
                {
                    using (var doc = JsonDocument.Parse(payload))
                    {
                        var content = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
                        if (string.IsNullOrWhiteSpace(content)) throw new InvalidOperationException($"Réponse vide de {provider}.");
                        return content;
                    }
                }
                catch (InvalidOperationException) { throw; }
                catch (Exception ex) { throw new InvalidOperationException($"Réponse {provider} illisible : " + ex.Message); }
            }
        }
    }
}
