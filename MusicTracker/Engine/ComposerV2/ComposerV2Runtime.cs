using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace MusicTracker.Engine.ComposerV2
{
    /// <summary>
    /// Runtime access to the bundled Composer-V2 corpus models. The analyzer writes the model JSON
    /// (Data\*_model_v2.json, copied next to the assembly); here the running app deserializes it on demand
    /// (System.Text.Json — the same serializer the timeline/settings use) and caches it. Used by the
    /// MusicComposer wrappers that expose the V2 styles in the compose dialog.
    /// </summary>
    public static class ComposerV2Runtime
    {
        static readonly Dictionary<string, CorpusModelV2> Cache = new Dictionary<string, CorpusModelV2>();
        static readonly JsonSerializerOptions Opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        /// <summary>The folder holding the bundled + user-created corpus models (next to the assembly).</summary>
        public static string ModelsDir => AppPaths.Local(Path.Combine("Data", "models"));

        /// <summary>Load (and cache) a model by its file name within Data\models\, e.g. "bach_solo_model_v2.json".
        /// Throws if the file is missing/invalid.</summary>
        public static CorpusModelV2 LoadModel(string fileName)
        {
            CorpusModelV2 m;
            if (Cache.TryGetValue(fileName, out m)) return m;
            string path = Path.Combine(ModelsDir, fileName);
            m = JsonSerializer.Deserialize<CorpusModelV2>(File.ReadAllText(path), Opts);
            Cache[fileName] = m;
            return m;
        }

        /// <summary>Drop a freshly-built model's cache entry so a re-analyzed file is reloaded.</summary>
        public static void Invalidate(string fileName) { Cache.Remove(fileName); }

        /// <summary>Deserialize a model from a full path (uncached) — for the dialog to re-read an existing model's
        /// import settings (SourceFolders + Orders).</summary>
        public static CorpusModelV2 ReadFromPath(string fullPath)
        {
            return JsonSerializer.Deserialize<CorpusModelV2>(File.ReadAllText(fullPath), Opts);
        }
    }
}
