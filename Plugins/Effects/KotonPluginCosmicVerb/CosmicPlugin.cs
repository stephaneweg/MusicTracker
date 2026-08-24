using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Windows.Controls;
using KotonStudio.Library;

namespace KotonPluginCosmicVerb
{
    /// <summary>
    /// Cosmic Verb — reverb longue / delay morphing basé sur un Feedback Delay Network (FDN)
    /// modulé, avec 21 modes cosmologiques (Gemini, Andromeda, Sirius, Great Annihilator...).
    /// Écrit from scratch d'après la littérature publique (Jot 1991, Dattorro 1997) — aucun code
    /// binaire décompilé n'a été réutilisé ; l'analyse d'un plugin de référence a servi
    /// uniquement à cataloguer les 21 comportements musicaux distincts (nombre de delays,
    /// predelay, densité de matrice, envelope d'attaque).
    ///
    /// **Utilisation typique** :
    /// - Modes Gemini / Hydra → tap delays discrets, très musicaux sur voix ou lead
    /// - Modes Cassiopeia / Aquarius → cross-over delay ↔ reverb via Density
    /// - Modes Andromeda / Sirius / Great Annihilator → reverbs ambient très longues, à
    ///   utiliser en send avec Mix bas
    /// - Modes Cirrus → predelay long pour effets rythmiques
    /// - Modes Leo / Libra → decay filtré (sombre) pour drones
    ///
    /// **Warp** : à 0 %, toutes les delays ont la même longueur (échos flanger). À 100 %, ratios
    /// premier-based (Jot) pour un mélange modal maximal (pas de résonance parasite).
    /// </summary>
    [KotonEffect("Cosmic Verb", Id = "koton.cosmicverb", Category = "Reverb", Version = "1.0", Vendor = "Koton Studio")]
    public sealed class CosmicPlugin : IKotonEffect
    {
        public string Id => "koton.cosmicverb";
        public string DisplayName => "Cosmic Verb";

        readonly KotonParameter _mode      = new KotonParameter("mode",       "Mode",       0, CosmicModes.All.Length - 1, 12) { Automatable = false };   // Andromeda par défaut
        readonly KotonParameter _delayMs   = new KotonParameter("delay_ms",   "Delay",      10, 3000, 350, "ms");
        readonly KotonParameter _warp      = new KotonParameter("warp",       "Warp",       0.0, 1.0, 0.70);
        readonly KotonParameter _feedback  = new KotonParameter("feedback",   "Feedback",   0.0, 1.0, 0.75);
        readonly KotonParameter _density   = new KotonParameter("density",    "Density",    0.0, 1.0, 0.60);
        readonly KotonParameter _modDepth  = new KotonParameter("mod_depth",  "Mod depth",  0.0, 1.0, 0.30);
        readonly KotonParameter _modRateHz = new KotonParameter("mod_rate",   "Mod rate",   0.05, 2.0, 0.35, "Hz");
        readonly KotonParameter _width     = new KotonParameter("width",      "Width",      0.0, 1.0, 0.90);
        readonly KotonParameter _highCutHz = new KotonParameter("high_cut",   "High cut",   500, 20000, 8000, "Hz");
        readonly KotonParameter _mix       = new KotonParameter("mix",        "Mix",        0.0, 1.0, 0.40);
        readonly KotonParameter _outGainDb = new KotonParameter("out_gain",   "Output",     -30.0, 6.0, 0.0, "dB");

        readonly List<KotonParameter> _params;
        public IReadOnlyList<KotonParameter> Parameters => _params;

        int _sampleRate;
        CosmicCore _core;

        public CosmicPlugin()
        {
            _params = new List<KotonParameter>
            {
                _mode, _delayMs, _warp, _feedback, _density,
                _modDepth, _modRateHz, _width, _highCutHz, _mix, _outGainDb,
            };
        }

        public bool HasEditor => true;
        public UserControl CreateEditor() => new CosmicEditor(this);

        public void Prepare(int sampleRate, int maxBlockSize)
        {
            _sampleRate = sampleRate;
            _core = new CosmicCore(sampleRate);
        }
        public void Reset() => _core?.Reset();
        public void Process(Span<float> left, Span<float> right)
        {
            if (_core == null) return;
            var p = new CosmicParams
            {
                ModeIndex = (int)Math.Round(_mode.Value),
                DelayMs   = (float)_delayMs.Value,
                Warp      = (float)_warp.Value,
                Feedback  = (float)_feedback.Value,
                Density   = (float)_density.Value,
                ModDepth  = (float)_modDepth.Value,
                ModRateHz = (float)_modRateHz.Value,
                Width     = (float)_width.Value,
                HighCutHz = (float)_highCutHz.Value,
                Mix       = (float)_mix.Value,
                OutGainDb = (float)_outGainDb.Value,
            };
            _core.Process(left, right, p);
        }

        public byte[] SaveState()
        {
            try
            {
                var dict = new Dictionary<string, double>();
                foreach (var kp in _params) dict[kp.Id] = kp.Value;
                return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(dict));
            }
            catch { return Array.Empty<byte>(); }
        }
        public void LoadState(byte[] state)
        {
            if (state == null || state.Length == 0) return;
            try
            {
                var dict = JsonSerializer.Deserialize<Dictionary<string, double>>(Encoding.UTF8.GetString(state));
                if (dict == null) return;
                foreach (var kp in _params) if (dict.TryGetValue(kp.Id, out var v)) kp.Value = v;
            }
            catch { }
        }
        public void Dispose() { }

        public void SetParam(string id, double value)
        {
            foreach (var kp in _params) if (kp.Id == id) { kp.Value = value; return; }
        }

        public string[] ModeNames
        {
            get { var a = new string[CosmicModes.All.Length]; for (int i = 0; i < a.Length; i++) a[i] = CosmicModes.All[i].Name; return a; }
        }
    }
}
