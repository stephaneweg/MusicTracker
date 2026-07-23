namespace MusicTracker.Engine.Score
{
    /// <summary>
    /// The scales available for transposition: the seven church modes plus harmonic/melodic minor. Each is a
    /// 7-entry list of semitone offsets from the tonic. Used to remap notes from one mode to another (degree by
    /// degree) when transposing, and to pick the nearest major/minor armure for <see cref="KeySignature"/>.
    /// </summary>
    public static class MusicalMode
    {
        public static readonly string[] Names =
        {
            "Majeur (ionien)",            // 0
            "Mineur naturel (éolien)",    // 1
            "Mineur harmonique",          // 2
            "Mineur mélodique",           // 3
            "Dorien",                     // 4
            "Phrygien",                   // 5
            "Lydien",                     // 6
            "Mixolydien",                 // 7
            "Locrien",                    // 8
        };

        public static readonly int[][] Scales =
        {
            new[] { 0, 2, 4, 5, 7, 9, 11 }, // ionien / majeur
            new[] { 0, 2, 3, 5, 7, 8, 10 }, // éolien / mineur naturel
            new[] { 0, 2, 3, 5, 7, 8, 11 }, // mineur harmonique
            new[] { 0, 2, 3, 5, 7, 9, 11 }, // mineur mélodique (ascendant)
            new[] { 0, 2, 3, 5, 7, 9, 10 }, // dorien
            new[] { 0, 1, 3, 5, 7, 8, 10 }, // phrygien
            new[] { 0, 2, 4, 6, 7, 9, 11 }, // lydien
            new[] { 0, 2, 4, 5, 7, 9, 10 }, // mixolydien
            new[] { 0, 1, 3, 5, 6, 8, 10 }, // locrien
        };

        public static int[] Scale(int mode) => Scales[(mode >= 0 && mode < Scales.Length) ? mode : 0];

        /// <summary>True when the mode has a minor third (b3) — used to choose the nearest minor armure.</summary>
        public static bool IsMinorish(int mode) => Scale(mode)[2] - Scale(mode)[0] == 3;

        /// <summary>The default mode index for a major/minor <see cref="KeySignature"/> (mode 0/1).</summary>
        public static int FromKeyMode(int keyMode) => keyMode == 1 ? 1 : 0;

        /// <summary>The full mode of a key: its stored <see cref="KeySignature.FullMode"/> if set, else derived
        /// from the major/minor <see cref="KeySignature.Mode"/>.</summary>
        public static int Effective(KeySignature k)
            => (k != null && k.FullMode >= 0 && k.FullMode < Scales.Length) ? k.FullMode : FromKeyMode(k?.Mode ?? 0);
    }
}
