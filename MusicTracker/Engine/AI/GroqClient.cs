using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace MusicTracker.Engine.AI
{
    /// <summary>
    /// Client for Groq's OpenAI-compatible chat-completions API (api.groq.com). Free keys at console.groq.com.
    /// Same contract as <see cref="MistralClient"/> (Bearer + response_format json_object): returns ONE JSON object.
    /// Round-trip + error handling are shared in <see cref="AIClient"/>.
    /// </summary>
    public class GroqClient : AIClient
    {
        const string Endpoint = "https://api.groq.com/openai/v1/chat/completions";
        const string DefaultModel = "llama-3.3-70b-versatile";

        /// <summary>Ask the model for ONE JSON object. Returns the assistant message content (raw JSON).</summary>
        public static Task<string> CompleteJsonAsync(string apiKey, string model, string systemPrompt, string userPrompt, CancellationToken ct = default)
            => OpenAiCompatibleJsonAsync(Endpoint, "Groq", apiKey, model, DefaultModel, systemPrompt, userPrompt, ct);
    }
}
