using System;
using System.Threading;
using System.Threading.Tasks;

namespace MusicTracker.Engine.AI
{
    /// <summary>
    /// Client for Alibaba's Qwen models via the DashScope OpenAI-compatible endpoint. Keys at Alibaba Cloud
    /// Model Studio (bailian). Same contract as <see cref="GroqClient"/> (Bearer + response_format json_object).
    ///
    /// IMPORTANT: the OpenAI SDK's <c>base_url</c> ends at <c>/compatible-mode/v1</c> and the SDK appends
    /// <c>/chat/completions</c> itself. Our raw client POSTs to the exact URL, so the FULL path (ending in
    /// <c>/chat/completions</c>) is required — omitting it returns 404.
    ///
    /// The URL below is the account-specific Singapore MaaS deployment Alibaba provided (region ap-southeast-1).
    /// The generic public alternative is https://dashscope-intl.aliyuncs.com/compatible-mode/v1/chat/completions .
    /// </summary>
    public class QwenClient : AIClient
    {
        const string Endpoint = "https://ws-a7k3msh99jygu0ru.ap-southeast-1.maas.aliyuncs.com/compatible-mode/v1/chat/completions";
        const string DefaultModel = "qwen3.7-max";

        /// <summary>Ask the model for ONE JSON object. Returns the assistant message content (raw JSON).</summary>
        public static Task<string> CompleteJsonAsync(string apiKey, string model, string systemPrompt, string userPrompt, CancellationToken ct = default)
            => OpenAiCompatibleJsonAsync(Endpoint, "Qwen", apiKey, model, DefaultModel, systemPrompt, userPrompt, ct);
    }
}
