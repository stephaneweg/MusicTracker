using System.Collections.Generic;

namespace MusicTracker.Engine.Timeline.Effects
{
    /// <summary>
    /// Reconstitue un <see cref="IAudioEffect"/> à partir de son descripteur sérialisé (<see cref="TrackEffectData"/>)
    /// et fournit la liste des types installés (pour peupler le menu « Ajouter un effet »).
    /// </summary>
    public static class EffectFactory
    {
        /// <summary>Ordre affiché dans le menu « Ajouter un effet ».</summary>
        public static readonly string[] Kinds = new[] { "eq", "comp", "delay", "sat" };

        /// <summary>Clé de localisation associée à un type (le nom d'affichage).</summary>
        public static string LocKey(string kind)
        {
            switch (kind)
            {
                case "eq":    return "FxEq";
                case "comp":  return "FxCompressor";
                case "delay": return "FxDelay";
                case "sat":   return "FxSaturation";
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
                default:      return null;
            }
        }

        /// <summary>Instancie l'effet et lui applique les paramètres — renvoie null pour un type inconnu (ancien .sq d'une version ultérieure inconnue par ex.).</summary>
        public static IAudioEffect Create(TrackEffectData data, int sampleRate)
        {
            if (data == null) return null;
            var fx = Create(data.Kind, sampleRate);
            if (fx == null) return null;
            fx.Load(data.Params);
            return fx;
        }
    }
}
