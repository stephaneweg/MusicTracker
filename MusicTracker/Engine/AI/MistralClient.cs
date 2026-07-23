using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace MusicTracker.Engine.AI
{
    /// <summary>
    /// Client for Mistral's chat-completions API (https://api.mistral.ai/v1/chat/completions).
    /// Free "Experiment" keys work at console.mistral.ai. Used by the AI arrangement dialog to obtain a
    /// single JSON object (forced via response_format) describing a piece to lay out on the timeline.
    /// Round-trip + error handling are shared in <see cref="AIClient"/>.
    /// </summary>
    public class MistralClient : AIClient
    {
        const string Endpoint = "https://api.mistral.ai/v1/chat/completions";
        const string DefaultModel = "mistral-small-latest";

        /// <summary>Ask the model for ONE JSON object. Returns the assistant message content (raw JSON string).</summary>
        public static Task<string> CompleteJsonAsync(string apiKey, string model, string systemPrompt, string userPrompt, CancellationToken ct = default)
            => OpenAiCompatibleJsonAsync(Endpoint, "Mistral", apiKey, model, DefaultModel, systemPrompt, userPrompt, ct);
    }
}
