namespace MusicTracker.Engine.Timeline.Effects
{
    /// <summary>
    /// Référence sérialisable à un constrainer natif Koton posé sur une piste. Persisté dans le
    /// projet .sq via <c>TimelineTrack.NoteConstrainers</c>. Analogue à <see cref="TrackEffectData"/>
    /// pour les effets d'insert audio, mais côté FILTRAGE DE NOTES.
    ///
    /// L'ordre des refs dans la liste = l'ordre d'application (chaque constrainer voit la sortie du
    /// précédent). Un blob de state absent = plugin fraîchement ajouté avec ses valeurs par défaut.
    /// </summary>
    public class NoteConstrainerRef
    {
        /// <summary>Id stable du plugin (KotonGeneratorConstrainerAttribute.Id).</summary>
        public string Id { get; set; }

        /// <summary>Blob d'état sérialisé (base64) — écrit à chaque save via IKotonPlugin.SaveState().
        /// Nullable si jamais sauvegardé.</summary>
        public string StateBlob { get; set; }

        /// <summary>Bypass rapide — l'utilisateur peut désactiver un filtre sans le supprimer. Défaut
        /// = false = filtre actif.</summary>
        public bool Bypass { get; set; }
    }
}
