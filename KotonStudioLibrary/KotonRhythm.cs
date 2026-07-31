using System;

namespace KotonStudio.Library
{
    /// <summary>
    /// Motif rythmique éditable — séquence de notes (start + longueur) sur une seule voix, unité =
    /// slices depuis le début du motif. Utilisé par les plugins qui exposent un mode "rythme
    /// personnalisé" (ex. arpégiateur) plutôt qu'une subdivision régulière.
    ///
    /// **Sérialisable JSON** : le plugin embarque cette structure dans son <see cref="IKotonPlugin.SaveState"/>
    /// pour la persister dans le projet. L'hôte fournit un éditeur graphique via
    /// <see cref="KotonHost.CreateRhythmGrid"/> — le plugin ne peint rien lui-même, il ne fait que
    /// stocker et interpréter le résultat.
    ///
    /// **Coordonnées** : chaque note commence à <c>StartSlices[i]</c> et dure <c>LenSlices[i]</c>
    /// slices. Le motif fait au total <see cref="Beats"/> × <see cref="SlicesPerBeat"/> slices.
    /// Un plugin peut looper le motif dans un bloc plus long (bloc = 8 beats, motif = 2 beats →
    /// motif joué 4 fois).
    /// </summary>
    public class KotonRhythm
    {
        /// <summary>Durée du motif en beats (1 beat = 1 noire en binaire, 1 noire pointée en ternaire).
        /// Distinct de la durée du bloc générateur — le motif se loop dans le bloc.</summary>
        public int Beats = 2;

        /// <summary>Résolution rythmique : nombre de subdivisions par beat (4 = doubles-croches,
        /// 6 = triplet de doubles, 8 = triples, etc.). Multiple de la vraie résolution audio pour
        /// tomber sur des durées exactes en export.</summary>
        public int SlicesPerBeat = 4;

        /// <summary>Position de départ (en slices depuis le début du motif) de chaque note. Trié
        /// croissant. Longueur == <see cref="LenSlices"/>.Length.</summary>
        public int[] StartSlices = Array.Empty<int>();

        /// <summary>Durée (en slices) de chaque note, dans le même ordre que <see cref="StartSlices"/>.
        /// Minimum 1 slice. Une note peut chevaucher la fin du motif (l'hôte tronque à la lecture).</summary>
        public int[] LenSlices = Array.Empty<int>();

        /// <summary>Nombre total de slices du motif (Beats × SlicesPerBeat). Renvoyée pour éviter le
        /// recalcul côté plugin.</summary>
        public int TotalSlices => Beats * SlicesPerBeat;
    }

}
