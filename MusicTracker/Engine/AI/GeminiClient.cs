using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MusicTracker.Engine.AI
{
    /// <summary>
    /// Minimal client for Google's Gemini API (generativelanguage.googleapis.com). Free keys at aistudio.google.com.
    /// Same contract as <see cref="MistralClient"/>: returns ONE JSON object (forced via responseMimeType).
    /// </summary>
    public class GeminiClient:AIClient
    {
        const string Base = "https://generativelanguage.googleapis.com/v1beta/models/";

        /// <summary>Ask the model for ONE JSON object. Returns the response text (raw JSON). Throws on API/HTTP error.
        /// <paramref name="thinkingBudget"/>: -1 = auto (default); ≥0 sets the Gemini 2.5 "thinking" token budget (0 = off).</summary>
        public static async Task<string> CompleteJsonAsync(string apiKey, string model, string systemPrompt, string userPrompt, int thinkingBudget = -1, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(apiKey))
                throw new InvalidOperationException("Clé API Gemini manquante.");
            if (string.IsNullOrWhiteSpace(model)) model = "gemini-2.0-flash";


            // No maxOutputTokens cap: 2.5 spends "thinking" tokens, so a low cap truncates it. thinkingConfig is only valid
            // on 2.5 models, so only include it there when the user set an explicit budget.
            bool canThink = thinkingBudget >= 0;//&& model.IndexOf("2.5", StringComparison.Ordinal) >= 0;
            object genConfig = canThink
                ? (object)new { responseMimeType = "application/json", temperature = 0.7, thinkingConfig = new { thinkingBudget = Math.Min(24576, thinkingBudget) } }
                : new { responseMimeType = "application/json", temperature = 0.7 };
            var body = new
            {
                systemInstruction = new { parts = new[] { new { text = systemPrompt } } },
                contents = new[] { new { role = "user", parts = new[] { new { text = userPrompt } } } },
                generationConfig = genConfig,
            };

            string url = Base + Uri.EscapeDataString(model.Trim()) + ":generateContent";
            using (var req = new HttpRequestMessage(HttpMethod.Post, url))
            {
                req.Headers.Add("x-goog-api-key", apiKey.Trim()); // key in header (not the URL query)
                req.Content = JsonBody(body);

                string payload = await SendAsync(req, "Gemini", ct).ConfigureAwait(false);
                try
                {
                    using (var doc = JsonDocument.Parse(payload))
                    {
                        var cand = doc.RootElement.GetProperty("candidates")[0];
                        if (cand.TryGetProperty("finishReason", out var fr) && fr.GetString() == "MAX_TOKENS")
                            throw new InvalidOperationException("Réponse tronquée (limite de tokens atteinte). Réduis la longueur, ou passe en mode « Ligne mélodique ».");
                        var parts = cand.GetProperty("content").GetProperty("parts");
                        var sb = new StringBuilder();
                        foreach (var p in parts.EnumerateArray())
                            if (p.TryGetProperty("text", out var t)) sb.Append(t.GetString());
                        string content = sb.ToString();
                        if (string.IsNullOrWhiteSpace(content)) throw new InvalidOperationException("Réponse vide de Gemini (peut-être bloquée par un filtre de sécurité).");
                        return content;
                    }
                }
                catch (InvalidOperationException) { throw; }
                catch (Exception ex) { throw new InvalidOperationException("Réponse Gemini illisible : " + ex.Message); }
            }
        }
    }
}
