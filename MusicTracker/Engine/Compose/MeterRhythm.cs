using System;
using System.Collections.Generic;

namespace MusicTracker.Engine.Compose
{
    /// <summary>
    /// Shared METER-AWARE rhythm layer used by every generator (procedural <see cref="ProceduralComposer"/> AND the
    /// learned-model melody via <see cref="Reflow"/>). The beat is always 24 slices; a BINARY meter subdivides it by 2/4
    /// (durations 12/6), a TERNARY (compound x/8) meter by 3/6 (durations 8/4). Notes group by whole beats
    /// (24/48/72/96) and never start mid-beat, for balanced rhythm. Sixteenth-note subdivisions (6 binary / 4 ternary)
    /// are deliberately RARE (they were far too frequent before) — quarters and eighths dominate.
    /// </summary>
    public static class MeterRhythm
    {
        public const int Beat = 24;
        public const int REST = int.MinValue;

        /// <summary>A list of durations that tile <paramref name="total"/>, beat-aligned and meter-correct. Whole-beat and
        /// half-beat values dominate; sixteenths are rare. Multi-beat "held" notes (48/72/96) only span whole beats within
        /// a bar (72 needs ≥3 beats/bar, 96 needs ≥4).</summary>
        public static List<int> GenerateRhythm(int total, int barSlices, bool ternary, Random rng)
        {
            // within-BEAT patterns (each sums to 24). Quarters/dotted-quarters and eighths WEIGHTED; only two patterns
            // carry a sixteenth pair → sixteenths stay occasional.
            int[][] within = ternary
                ? new[] { new[] { 24 }, new[] { 24 }, new[] { 24 }, new[] { 24 }, new[] { 8, 8, 8 }, new[] { 8, 8, 8 }, new[] { 8, 8, 8 }, new[] { 8, 8, 8 }, new[] { 8, 8, 4, 4 } }
                : new[] { new[] { 24 }, new[] { 24 }, new[] { 24 }, new[] { 24 }, new[] { 12, 12 }, new[] { 12, 12 }, new[] { 12, 12 }, new[] { 12, 12 }, new[] { 12, 6, 6 } };
            if (barSlices < Beat) barSlices = 4 * Beat;
            int bpb = Math.Max(1, barSlices / Beat);
            var durs = new List<int>();
            int pos = 0, guard = 0;
            while (pos < total && guard++ < 100000)
            {
                int beatsLeftInBar = bpb - (pos % barSlices) / Beat;
                int maxGroup = 1;
                if (beatsLeftInBar >= 2) maxGroup = 2;                    // 48 (two beats)
                if (bpb >= 3 && beatsLeftInBar >= 3) maxGroup = 3;        // 72 (three beats)
                if (bpb >= 4 && beatsLeftInBar >= 4) maxGroup = 4;        // 96 (four beats)
                if (maxGroup >= 2 && rng.NextDouble() < 0.30)
                {
                    int g = 2 + rng.Next(maxGroup - 1);
                    int d = Math.Min(g * Beat, total - pos);
                    if (d > 0) { durs.Add(d); pos += d; }
                }
                else
                {
                    foreach (int d0 in within[rng.Next(within.Length)])
                    {
                        int d = Math.Min(d0, total - pos);
                        if (d <= 0) { pos = total; break; }
                        durs.Add(d); pos += d;
                        if (pos >= total) break;
                    }
                }
            }
            return durs;
        }

        /// <summary>
        /// Re-time a melody's PITCHES onto a clean, meter-correct rhythm (few sixteenths), keeping the pitch-over-time
        /// contour and preserving rests where the original was silent. Used to make the LEARNED-model melody respect
        /// binary/ternary division and thin out sixteenths, without touching the pitch logic. Notes keep the app's
        /// convention (Note = MIDI − 12).
        /// </summary>
        public static List<RiffNote> Reflow(List<RiffNote> melody, int barSlices, bool ternary, int seed)
        {
            if (melody == null || melody.Count == 0) return melody;
            var notes = new List<RiffNote>(melody);
            notes.Sort((a, b) => a.Start.CompareTo(b.Start));
            int total = 0; foreach (var n in notes) if (n.End > total) total = n.End;
            if (barSlices < Beat) barSlices = 4 * Beat;
            total = ((total + barSlices - 1) / barSlices) * barSlices;   // round up to a whole bar
            if (total <= 0) return melody;

            var durs = GenerateRhythm(total, barSlices, ternary, new Random(seed));
            var outl = new List<RiffNote>();
            int pos = 0;
            foreach (int d in durs)
            {
                int pitch = PitchAt(notes, pos);
                if (pitch != REST) outl.Add(new RiffNote(pitch, pos, d));   // else a rest (original was silent here)
                pos += d;
            }
            return outl.Count > 0 ? outl : melody;
        }

        // The pitch of the note SOUNDING at slice t (Start ≤ t < End); REST if the original was silent there.
        static int PitchAt(List<RiffNote> notes, int t)
        {
            foreach (var n in notes) if (n.Start <= t && t < n.End) return n.Note;
            return REST;
        }
    }
}
