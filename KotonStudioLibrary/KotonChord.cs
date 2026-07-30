using System.Collections.Generic;

namespace KotonStudio.Library
{
    /// <summary>
    /// Un accord musical publié par l'hôte à travers <see cref="KotonHost.GetChordAt"/> — représentation
    /// abstraite (racine + qualité + éventuelle basse d'inversion), indépendante d'un instrument, d'un
    /// registre ou d'une note MIDI concrète. C'est un « accord logique » que le plugin peut ensuite
    /// projeter dans le registre de son choix via <see cref="KotonChordExtensions.GetMidiNotes"/>.
    ///
    /// **Pourquoi une struct** : passé par valeur dans les callbacks de rendu (le plugin en itère
    /// beaucoup), pas d'alloc, sémantique de copie qui évite les partages accidentels quand le plugin
    /// mémorise l'accord courant.
    ///
    /// **Bass note vs. inversion** : <see cref="BassNote"/> est le degré 0..11 mis à la basse quand
    /// l'accord est joué en inversion ou en slash-chord (ex. C/E → Root=C, BassNote=E). <c>null</c> =
    /// position fondamentale, la racine EST la basse. Le plugin est libre d'ignorer ce champ s'il joue
    /// l'accord en voicing large où la basse importe peu.
    /// </summary>
    public struct KotonChord
    {
        /// <summary>Pitch class 0..11 de la fondamentale (C=0, C#=1, D=2, ..., B=11).</summary>
        public int Root;

        /// <summary>Nature harmonique — triade ou tétrade, avec ou sans altération.</summary>
        public KotonChordQuality Quality;

        /// <summary>Pitch class 0..11 de la note à la basse pour une inversion / slash-chord ;
        /// <c>null</c> = position fondamentale (la racine EST la basse).</summary>
        public int? BassNote;
    }

    /// <summary>Nature harmonique d'un <see cref="KotonChord"/>. Enrichissable — un plugin qui rencontre
    /// une valeur inconnue (blob d'accord d'une future version) doit dégrader gracieusement, typiquement
    /// en la traitant comme <see cref="Major"/>.</summary>
    public enum KotonChordQuality
    {
        Major,          // 0-4-7        (fondamentale, tierce majeure, quinte juste)
        Minor,          // 0-3-7        (fondamentale, tierce mineure, quinte juste)
        Diminished,     // 0-3-6        (tierce mineure, quinte diminuée)
        Augmented,      // 0-4-8        (tierce majeure, quinte augmentée)
        Sus2,           // 0-2-7        (seconde suspendue à la place de la tierce)
        Sus4,           // 0-5-7        (quarte suspendue à la place de la tierce)
        Power,          // 0-7          (fondamentale + quinte — pas de tierce, ambigu majeur/mineur)
        Dominant7,      // 0-4-7-10     (majeur + septième mineure)
        Major7,         // 0-4-7-11     (majeur + septième majeure)
        Minor7,         // 0-3-7-10     (mineur + septième mineure)
        MinorMajor7,    // 0-3-7-11     (mineur + septième majeure)
        Diminished7,    // 0-3-6-9      (diminuée + septième diminuée)
        HalfDim7,       // 0-3-6-10     (diminuée + septième mineure)
        Augmented7,     // 0-4-8-10     (augmentée + septième mineure)
    }

    /// <summary>
    /// Helpers pour projeter un <see cref="KotonChord"/> logique dans un registre MIDI concret. Séparé
    /// de la struct pour ne pas polluer la surface d'appel côté hôte (qui ne fait que produire des
    /// accords, jamais les résoudre en notes).
    /// </summary>
    public static class KotonChordExtensions
    {
        /// <summary>Voicing serré (root position) du <paramref name="chord"/> à partir de la note MIDI
        /// <paramref name="baseMidi"/> pour la fondamentale (ex. 60 pour C4). Retourne au moins la
        /// triade — 4 notes pour les qualités de 7e. La basse d'inversion n'est PAS ajoutée : les
        /// plugins qui veulent la basse la lisent depuis <see cref="KotonChord.BassNote"/> et l'ajoutent
        /// eux-mêmes (au voicing / à l'octave qu'ils choisissent).</summary>
        public static int[] GetMidiNotes(this KotonChord chord, int baseMidi)
        {
            int[] intervals = IntervalsFor(chord.Quality);
            var notes = new int[intervals.Length];
            for (int i = 0; i < intervals.Length; i++) notes[i] = baseMidi + intervals[i];
            return notes;
        }

        /// <summary>Intervalles en demi-tons depuis la fondamentale pour chaque qualité — la table de
        /// vérité harmonique. Un ajout ici (nouvelle qualité) demande aussi une entrée dans
        /// <see cref="KotonChordQuality"/> et une entrée symétrique dans une future sérialisation.
        /// Retour d'un tableau *neuf* pour que l'appelant puisse le muter sans effet de bord.</summary>
        static int[] IntervalsFor(KotonChordQuality q)
        {
            switch (q)
            {
                case KotonChordQuality.Major:       return new[] { 0, 4, 7 };
                case KotonChordQuality.Minor:       return new[] { 0, 3, 7 };
                case KotonChordQuality.Diminished:  return new[] { 0, 3, 6 };
                case KotonChordQuality.Augmented:   return new[] { 0, 4, 8 };
                case KotonChordQuality.Sus2:        return new[] { 0, 2, 7 };
                case KotonChordQuality.Sus4:        return new[] { 0, 5, 7 };
                case KotonChordQuality.Power:       return new[] { 0, 7 };
                case KotonChordQuality.Dominant7:   return new[] { 0, 4, 7, 10 };
                case KotonChordQuality.Major7:      return new[] { 0, 4, 7, 11 };
                case KotonChordQuality.Minor7:      return new[] { 0, 3, 7, 10 };
                case KotonChordQuality.MinorMajor7: return new[] { 0, 3, 7, 11 };
                case KotonChordQuality.Diminished7: return new[] { 0, 3, 6, 9 };
                case KotonChordQuality.HalfDim7:    return new[] { 0, 3, 6, 10 };
                case KotonChordQuality.Augmented7:  return new[] { 0, 4, 8, 10 };
                // Défensif — une valeur inconnue (plugin qui reçoit un accord d'une future version)
                // dégrade en triade majeure plutôt que jeter, symétrique à la politique côté plugin.
                default:                            return new[] { 0, 4, 7 };
            }
        }
    }
}
