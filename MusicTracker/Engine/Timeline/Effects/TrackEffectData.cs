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

        public static TrackEffectData From(IAudioEffect fx, bool enabled)
        {
            return new TrackEffectData
            {
                Kind = fx?.Kind ?? "",
                Enabled = enabled,
                Params = fx != null ? fx.Save() : new Dictionary<string, double>(),
            };
        }
    }
}
