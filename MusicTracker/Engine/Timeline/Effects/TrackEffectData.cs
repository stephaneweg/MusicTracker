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
        /// Chemin absolu vers le fichier plugin (uniquement pour <c>Kind == "vst"</c>). Ignoré par les effets maison.
        /// Persisté tel quel — un projet ouvert sur une autre machine où le plugin est absent affichera un effet no-op
        /// avec une icône d'alerte (le rendu audio n'est pas cassé, l'insert est simplement bypassé).
        /// </summary>
        public string PluginPath { get; set; }
        /// <summary>
        /// État opaque (chunk) sérialisé en base64. Utilisé par les plugins VST pour transporter leur état interne
        /// (patch actif, valeurs de paramètres) que le dictionnaire nom→double ne peut pas représenter. Absent = null,
        /// sérialisation JSON identique aux vieux .sq (rétro-compat totale).
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
                PluginPath = (fx as MusicTracker.Engine.Timeline.Effects.VstEffect)?.PluginPath,
            };
        }
    }
}
