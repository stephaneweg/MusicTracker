using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace MusicTracker.Engine.AI
{
    /// <summary>
    /// Client for DeepSeek's OpenAI-compatible chat-completions API (api.deepseek.com). Keys at platform.deepseek.com.
    /// Same contract as <see cref="MistralClient"/> (Bearer + response_format json_object): returns ONE JSON object.
    /// Round-trip + error handling are shared in <see cref="AIClient"/>.
    /// </summary>
    public class DeepSeekClient : AIClient
    {
        const string Endpoint = "https://api.deepseek.com/chat/completions";
        const string DefaultModel = "deepseek-chat";

        /// <summary>Ask the model for ONE JSON object. Returns the assistant message content (raw JSON).</summary>
        public static Task<string> CompleteJsonAsync(string apiKey, string model, string systemPrompt, string userPrompt, CancellationToken ct = default)
            => OpenAiCompatibleJsonAsync(Endpoint, "DeepSeek", apiKey, model, DefaultModel, systemPrompt, userPrompt, ct);
    }
}
