using System;
using System.Collections.Generic;

namespace MusicTracker
{
    /// <summary>
    /// Shared lookup tables used across the engine and UI.
    /// <para>
    /// <see cref="Frequencies"/> maps a 0-based note index to its pitch in Hz, where index 0 = C0
    /// (MIDI note 12): Frequencies[n] = 440 · 2^((n − 57)/12), for n in 0..127 (note index = MIDI − 12,
    /// as used by RiffGridControl / the riff player). <see cref="NoteStrings"/> maps a pitch class (0..11) to its name.
    /// </para>
    /// </summary>
    public static class Utils
    {
        public static readonly Dictionary<int, double> Frequencies = BuildFrequencies();

        public static readonly Dictionary<int, string> NoteStrings = new Dictionary<int, string>
        {
            { 0, "C" }, { 1, "C#" }, { 2, "D" },  { 3, "D#" }, { 4, "E" },  { 5, "F" },
            { 6, "F#" }, { 7, "G" }, { 8, "G#" }, { 9, "A" },  { 10, "A#" }, { 11, "B" },
        };

        static Dictionary<int, double> BuildFrequencies()
        {
            var d = new Dictionary<int, double>(128);
            for (int n = 0; n < 128; n++)
                d[n] = 440.0 * Math.Pow(2.0, (n - 57) / 12.0);
            return d;
        }
    }
}
