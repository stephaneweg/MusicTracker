namespace KotonStudio.Library
{
    /// <summary>
    /// Interface additionnelle qu'un <see cref="IKotonGenerator"/> de type
    /// <see cref="KotonGeneratorType.Chord"/> DOIT implémenter s'il veut que son harmonie soit
    /// utilisable par les autres générateurs de la timeline. L'hôte agrège tous les blocs de la
    /// piste d'accords built-in et tous les <c>KotonGeneratorModule</c> dont l'instance vivante
    /// implémente cette interface pour répondre à <see cref="KotonHost.GetChordAt"/>.
    ///
    /// **Convention beat** : le beat passé à <see cref="GetChordAt"/> est RELATIF au début du bloc
    /// générateur — le plugin n'a pas besoin de connaître sa position absolue dans la timeline
    /// (c'est l'hôte qui décale). Un plugin qui ignore le beat (accord unique sur toute la durée)
    /// renvoie toujours son accord.
    ///
    /// **Silence** : retourner <c>null</c> = pas d'accord actif à ce beat (silence). Un générateur
    /// mélodique qui interroge un beat en silence n'a alors PAS d'accord de référence — libre à lui
    /// de dégrader (jouer la tonique, se taire, jouer la mesure précédente...).
    /// </summary>
    public interface IKotonChordSource
    {
        /// <summary>Renvoie l'accord actif au <paramref name="beat"/> donné (relatif au début du
        /// bloc générateur). <c>null</c> = silence / pas d'accord à cet instant.</summary>
        KotonChord? GetChordAt(double beat);
    }
}
