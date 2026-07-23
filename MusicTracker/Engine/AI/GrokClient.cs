using System;
using System.Threading;
using System.Threading.Tasks;

namespace MusicTracker.Engine.AI
{
    /// <summary>
    /// Client for xAI's Grok OpenAI-compatible chat-completions API (api.x.ai). Keys at console.x.ai.
    /// Same contract as <see cref="GroqClient"/> (Bearer + response_format json_object): returns ONE JSON object.
    /// Round-trip + error handling are shared in <see cref="AIClient"/>.
    /// </summary>
    public class GrokClient : AIClient
    {
        const string Endpoint = "https://api.x.ai/v1/chat/completions";
        const string DefaultModel = "grok-4";

        /// <summary>Ask the model for ONE JSON object. Returns the assistant message content (raw JSON).</summary>
        public static Task<string> CompleteJsonAsync(string apiKey, string model, string systemPrompt, string userPrompt, CancellationToken ct = default)
            => OpenAiCompatibleJsonAsync(Endpoint, "Grok", apiKey, model, DefaultModel, systemPrompt, userPrompt, ct);
    }
}
