using System.Collections.Generic;

namespace MusicTracker.Engine.Timeline.Effects
{
    /// <summary>
    /// Reconstitue un <see cref="IAudioEffect"/> à partir de son descripteur sérialisé (<see cref="TrackEffectData"/>)
    /// et fournit la liste des types installés (pour peupler le menu « Ajouter un effet »).
    /// </summary>
    public static class EffectFactory
    {
        /// <summary>Ordre affiché dans le menu « Ajouter un effet » (effets natifs uniquement — les VST ont leur propre sous-menu géré par le browser).</summary>
        public static readonly string[] Kinds = new[] { "eq", "comp", "delay", "sat" };

        /// <summary>Kind interne pour un plugin VST hébergé — non listé dans <see cref="Kinds"/> car ajouté via un browser dédié.</summary>
        public const string VstKind = "vst";

        /// <summary>Clé de localisation associée à un type (le nom d'affichage).</summary>
        public static string LocKey(string kind)
        {
            switch (kind)
            {
                case "eq":    return "FxEq";
                case "comp":  return "FxCompressor";
                case "delay": return "FxDelay";
                case "sat":   return "FxSaturation";
                case "vst":   return "FxVst"; // libellé générique ; le VRAI nom (nom du plugin) est calculé côté UI.
                default:      return kind ?? "";
            }
        }

        public static IAudioEffect Create(string kind, int sampleRate)
        {
            switch (kind)
            {
                case "eq":    return new EqEffect(sampleRate);
                case "comp":  return new CompressorEffect(sampleRate);
                case "delay": return new DelayEffect(sampleRate);
                case "sat":   return new SaturationEffect(sampleRate);
                case "vst":   return new VstEffect(sampleRate); // path/state posés ensuite via LoadState + PluginPath
                default:      return null;
            }
        }

        /// <summary>Instancie l'effet et lui applique les paramètres — renvoie null pour un type inconnu (ancien .sq d'une version ultérieure inconnue par ex.).</summary>
        public static IAudioEffect Create(TrackEffectData data, int sampleRate)
        {
            if (data == null) return null;
            var fx = Create(data.Kind, sampleRate);
            if (fx == null) return null;
            // Ordre important : PluginPath D'ABORD (le VstEffect s'en sert quand LoadState/EnsureLoaded s'exécute),
            // puis Load des params (no-op côté VST), puis LoadState pour restaurer le chunk binaire.
            if (fx is VstEffect vfx && !string.IsNullOrEmpty(data.PluginPath))
                vfx.PluginPath = data.PluginPath;
            fx.Load(data.Params);
            if (!string.IsNullOrEmpty(data.StateBlob))
                fx.LoadState(data.StateBlob);
            return fx;
        }
    }
}
