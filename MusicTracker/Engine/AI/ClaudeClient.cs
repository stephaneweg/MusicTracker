using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MusicTracker.Engine.AI
{
    /// <summary>
    /// Minimal client for Anthropic's Claude Messages API (https://api.anthropic.com/v1/messages).
    /// Keys at console.anthropic.com. Same contract as the other providers: returns ONE JSON object
    /// (the system prompt already forces JSON-only output). Uses raw HTTP like the sibling clients.
    /// </summary>
    public class ClaudeClient: AIClient
    {
        const string Endpoint = "https://api.anthropic.com/v1/messages";
        const string ApiVersion = "2023-06-01";


        /// <summary>Ask the model for ONE JSON object. Returns the assistant text (raw JSON). Throws on API/HTTP error.</summary>
        public static async Task<string> CompleteJsonAsync(string apiKey, string model, string systemPrompt, string userPrompt, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(apiKey))
                throw new InvalidOperationException("Clé API Claude manquante.");
            if (string.IsNullOrWhiteSpace(model)) model = "claude-opus-4-8";

            // Opus 4.8 / Sonnet 5 reject temperature and budget_tokens; we omit both. No thinking needed for JSON output.
            var body = new
            {
                model = model.Trim(),
                max_tokens = 32000,                          // room for a full 48-64 bar compact arrangement
                system = systemPrompt,
                messages = new[] { new { role = "user", content = userPrompt } },
            };

            using (var req = new HttpRequestMessage(HttpMethod.Post, Endpoint))
            {
                req.Headers.Add("x-api-key", apiKey.Trim());
                req.Headers.Add("anthropic-version", ApiVersion);
                req.Content = JsonBody(body);

                string payload = await SendAsync( req, "Claude", ct).ConfigureAwait(false);
                try
                {
                    using (var doc = JsonDocument.Parse(payload))
                    {
                        var root = doc.RootElement;
                        if (root.TryGetProperty("stop_reason", out var sr))
                        {
                            string reason = sr.GetString();
                            if (reason == "refusal")
                                throw new InvalidOperationException("Claude a refusé la requête (filtre de sécurité).");
                            if (reason == "max_tokens")
                                throw new InvalidOperationException("Réponse tronquée (limite de tokens atteinte). Réduis la longueur, ou passe en mode « Ligne mélodique ».");
                        }
                        var sb = new StringBuilder();
                        if (root.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
                            foreach (var block in content.EnumerateArray())
                                if (block.TryGetProperty("type", out var t) && t.GetString() == "text" && block.TryGetProperty("text", out var txt))
                                    sb.Append(txt.GetString());
                        string outText = sb.ToString();
                        if (string.IsNullOrWhiteSpace(outText)) throw new InvalidOperationException("Réponse vide de Claude.");
                        return outText;
                    }
                }
                catch (InvalidOperationException) { throw; }
                catch (Exception ex) { throw new InvalidOperationException("Réponse Claude illisible : " + ex.Message); }
            }
        }
    }
}
