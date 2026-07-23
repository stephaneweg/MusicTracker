using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace MusicTracker.Engine.AI
{
    /// <summary>
    /// Shared LLM-provider registry + dispatch, used by every "compose with AI" surface (the full arrangement
    /// dialog and the per-element groove/riff/line generators). Providers, suggested models, per-provider API
    /// keys (persisted in <see cref="AppSettings"/>) and the JSON-completion call all live here so the callers
    /// only pick a provider and build a prompt.
    /// </summary>
    public static class AiProviders
    {
        // Canonical provider ids (also the ComboBox order in the dialogs).
        public static readonly string[] Ids = { "mistral", "gemini", "groq", "deepseek", "claude", "grok", "qwen" };

        public static string Norm(string p) => Array.IndexOf(Ids, p) >= 0 ? p : "mistral";
        public static int IndexOf(string p) { int i = Array.IndexOf(Ids, Norm(p)); return i < 0 ? 0 : i; }
        public static string ForIndex(int i) => (i >= 0 && i < Ids.Length) ? Ids[i] : "mistral";

        public static string Label(string p)
        {
            switch (Norm(p))
            {
                case "gemini": return "Gemini";
                case "groq": return "Groq";
                case "deepseek": return "DeepSeek";
                case "claude": return "Claude";
                case "grok": return "Grok";
                case "qwen": return "Qwen";
                default: return "Mistral";
            }
        }

        public static string DefaultModel(string p)
        {
            switch (Norm(p))
            {
                case "gemini": return "gemini-2.0-flash";
                case "groq": return "llama-3.3-70b-versatile";
                case "deepseek": return "deepseek-chat";
                case "claude": return "claude-opus-4-8";
                case "grok": return "grok-4";
                case "qwen": return "qwen3.7-max";
                default: return "mistral-small-latest";
            }
        }

        // Suggested models per provider (the model ComboBox is editable → a custom id can still be typed).
        static readonly string[] MistralModels = { "mistral-large-latest", "mistral-medium-latest", "mistral-small-latest", "open-mistral-nemo", "magistral-small-latest", "magistral-medium-latest" };
        static readonly string[] GeminiModels = { "gemini-3.5-flash", "gemini-3-flash-preview", "gemini-2.5-flash", "gemini-2.0-flash-lite", "gemini-1.5-flash", "gemini-1.5-pro" };
        static readonly string[] GroqModels = { "llama-3.3-70b-versatile", "llama-3.1-8b-instant", "openai/gpt-oss-120b", "qwen/qwen3.6-27b", "gemma2-9b-it" };
        static readonly string[] DeepSeekModels = { "deepseek-chat", "deepseek-reasoner" };
        static readonly string[] ClaudeModels = { "claude-sonnet-5", "claude-opus-4-8", "claude-haiku-4-5", "claude-opus-4-7", "claude-fable-5" };
        static readonly string[] GrokModels = { "grok-4", "grok-3", "grok-3-mini", "grok-2-latest" };
        static readonly string[] QwenModels = { "qwen3.7-max", "qwen-max", "qwen-plus", "qwen-turbo" };

        public static string[] ModelsFor(string p)
        {
            switch (Norm(p))
            {
                case "gemini": return GeminiModels;
                case "groq": return GroqModels;
                case "deepseek": return DeepSeekModels;
                case "claude": return ClaudeModels;
                case "grok": return GrokModels;
                case "qwen": return QwenModels;
                default: return MistralModels;
            }
        }

        // ---- named API keys (multiple per provider), persisted in AppSettings ----

        // Legacy single key per provider (kept only to migrate into a named "Défaut" entry).
        static string LegacyKey(string p)
        {
            var s = AppSettings.Instance;
            switch (Norm(p))
            {
                case "gemini": return s.GeminiApiKey; case "groq": return s.GroqApiKey; case "deepseek": return s.DeepSeekApiKey;
                case "claude": return s.ClaudeApiKey; case "grok": return s.GrokApiKey; case "qwen": return s.QwenApiKey;
                default: return s.MistralApiKey;
            }
        }

        // One-time migration: turn each legacy single key into a named "Défaut" entry (runs only while ApiKeys is empty).
        public static void EnsureMigrated()
        {
            var s = AppSettings.Instance;
            if (s.ApiKeys == null) s.ApiKeys = new List<ApiKeyEntry>();
            if (s.SelectedKeyName == null) s.SelectedKeyName = new Dictionary<string, string>();
            if (s.ApiKeys.Count > 0) return;

            bool any = false;
            foreach (var p in Ids)
            {
                string legacy = LegacyKey(p);
                if (string.IsNullOrWhiteSpace(legacy)) continue;
                s.ApiKeys.Add(new ApiKeyEntry { Provider = p, Name = "Défaut", Key = legacy.Trim() });
                if (!s.SelectedKeyName.ContainsKey(p)) s.SelectedKeyName[p] = "Défaut";
                any = true;
            }
            if (any) s.Save();
        }

        /// <summary>The named keys stored for a provider.</summary>
        public static List<ApiKeyEntry> KeysFor(string p)
        {
            EnsureMigrated();
            string np = Norm(p);
            return AppSettings.Instance.ApiKeys.Where(k => Norm(k.Provider) == np).ToList();
        }

        /// <summary>The key NAME currently chosen for a provider (falls back to the first stored key's name, or null).</summary>
        public static string GetSelectedKeyName(string p)
        {
            EnsureMigrated();
            var s = AppSettings.Instance; string np = Norm(p);
            var keys = KeysFor(np);
            if (s.SelectedKeyName.TryGetValue(np, out var n) && keys.Any(k => k.Name == n)) return n;
            return keys.FirstOrDefault()?.Name;
        }

        public static void SetSelectedKeyName(string p, string name)
        {
            EnsureMigrated();
            AppSettings.Instance.SelectedKeyName[Norm(p)] = name ?? "";
        }

        public static string KeyByName(string p, string name)
            => KeysFor(p).FirstOrDefault(k => k.Name == name)?.Key;

        /// <summary>The key VALUE to use for a provider = the selected named key (fallback: first key, then legacy field).</summary>
        public static string GetKey(string p)
        {
            EnsureMigrated();
            string name = GetSelectedKeyName(p);
            string val = name != null ? KeyByName(p, name) : null;
            if (!string.IsNullOrWhiteSpace(val)) return val;
            return KeysFor(p).FirstOrDefault()?.Key ?? LegacyKey(p);
        }
        public static string GetModel(string p)
        {
            var s = AppSettings.Instance;
            switch (Norm(p))
            {
                case "gemini": return s.GeminiModel; case "groq": return s.GroqModel; case "deepseek": return s.DeepSeekModel;
                case "claude": return s.ClaudeModel; case "grok": return s.GrokModel; case "qwen": return s.QwenModel;
                default: return s.MistralModel;
            }
        }
        public static void SetModel(string p, string model)
        {
            var s = AppSettings.Instance;
            switch (Norm(p))
            {
                case "gemini": s.GeminiModel = model; break; case "groq": s.GroqModel = model; break; case "deepseek": s.DeepSeekModel = model; break;
                case "claude": s.ClaudeModel = model; break; case "grok": s.GrokModel = model; break; case "qwen": s.QwenModel = model; break;
                default: s.MistralModel = model; break;
            }
        }

        /// <summary>Call the chosen provider's JSON-completion endpoint (only Gemini honours the thinking budget).</summary>
        public static Task<string> CompleteJsonAsync(string provider, string apiKey, string model, string sys, string usr, int thinkingBudget = -1)
        {
            switch (Norm(provider))
            {
                case "gemini": return GeminiClient.CompleteJsonAsync(apiKey, model, sys, usr, thinkingBudget);
                case "groq": return GroqClient.CompleteJsonAsync(apiKey, model, sys, usr);
                case "deepseek": return DeepSeekClient.CompleteJsonAsync(apiKey, model, sys, usr);
                case "claude": return ClaudeClient.CompleteJsonAsync(apiKey, model, sys, usr);
                case "grok": return GrokClient.CompleteJsonAsync(apiKey, model, sys, usr);
                case "qwen": return QwenClient.CompleteJsonAsync(apiKey, model, sys, usr);
                default: return MistralClient.CompleteJsonAsync(apiKey, model, sys, usr);
            }
        }
    }
}
