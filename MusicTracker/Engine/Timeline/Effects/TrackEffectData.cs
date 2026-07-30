using System.Collections.Generic;

namespace MusicTracker.Engine.Timeline.Effects
{
    /// <summary>
    /// Description sérialisable d'un insert : le type d'effet (<see cref="Kind"/>) et ses paramètres
    /// (<see cref="Params"/>, plat nom→valeur). Vit dans <see cref="TimelineTrack.Inserts"/> et dans
    /// <see cref="TimelineProject.MasterInserts"/>. Écrit directement dans le .sq — pas de polymorphisme
    /// JSON, pas de discriminant caché : le champ <see cref="Kind"/> suffit à la reconstruction par
    /// <see cref="EffectFactory.Create(TrackEffectData, int)"/>.
    /// </summary>
    public class TrackEffectData
    {
        public string Kind { get; set; } = "";
        public bool Enabled { get; set; } = true;
        public Dictionary<string, double> Params { get; set; } = new Dictionary<string, double>();

        /// <summary>
        /// Chemin absolu vers un plugin externe (VST : le .dll). Rempli pour Kind="vst", <c>null</c> pour les
        /// effets maison. Champ optionnel : les .sq antérieurs à cette version n'ont pas ce champ, System.Text.Json
        /// les désérialise à <c>null</c> sans erreur.
        /// </summary>
        public string PluginPath { get; set; }

        /// <summary>
        /// État opaque du plugin, base64 du chunk natif — sérialisé et rechargé via
        /// <see cref="IAudioEffect.SaveState"/> / <see cref="IAudioEffect.LoadState"/>. Toujours <c>null</c>
        /// pour un effet maison (leur état vit dans <see cref="Params"/>). Champ optionnel, rétro-compat totale.
        /// </summary>
        public string StateBlob { get; set; }

        public static TrackEffectData From(IAudioEffect fx, bool enabled)
        {
            return new TrackEffectData
            {
                Kind = fx?.Kind ?? "",
                Enabled = enabled,
                Params = fx != null ? fx.Save() : new Dictionary<string, double>(),
                StateBlob = fx?.SaveState(),
            };
        }
    }
}
