using System;
using System.Collections.Generic;

namespace MusicTracker.Engine.Timeline
{
    /// <summary>
    /// Anacrusis-aware duplication of a rhythm motif. When a motif's length is NOT a whole number of bars — i.e. it
    /// carries a leading pickup ("levée"), e.g. 7 beats in 3/4 = 2 bars + 1 — a COPY of it (propagation to same-name
    /// modules, or appending the next one) must keep only the bar-aligned tail: drop the leading remainder beats and
    /// shift the content to the front (7 → 6). The original (first) module keeps its full pickup length.
    /// Shared by the melodic line, the chord rhythm motif, and the chord's melodic cell.
    /// </summary>
    public static class MotifCopy
    {
        /// <summary>Leading remainder in beats when <paramref name="totalBeats"/> is not a multiple of <paramref name="barBeats"/> (0 = bar-aligned).</summary>
        public static int LeadRemainder(double totalBeats, int barBeats)
            => barBeats > 0 ? (((int)Math.Round(totalBeats) % barBeats) + barBeats) % barBeats : 0;

        /// <summary>Drop the first <paramref name="cutSlices"/> slices of a note-list motif, shifting the rest to the front
        /// (notes straddling the cut are clipped, notes fully inside it are removed).</summary>
        public static List<RiffNote> TrimNotes(IEnumerable<RiffNote> src, int cutSlices)
        {
            var outN = new List<RiffNote>();
            if (src != null)
                foreach (var n in src)
                {
                    int end = n.Start + n.Length;
                    if (end <= cutSlices) continue;
                    int start = Math.Max(n.Start, cutSlices);
                    int nl = end - start;
                    if (nl > 0) outN.Add(new RiffNote(n.Note, start - cutSlices, nl));
                }
            return outN;
        }

        /// <summary>Drop the first <paramref name="cutSlices"/> cells of a slice grid, shifting the rest to the front.</summary>
        public static SequencerSlice[] TrimSlices(SequencerSlice[] src, int cutSlices)
        {
            if (src == null) return null;
            int n = Math.Max(0, src.Length - cutSlices);
            var outS = new SequencerSlice[n];
            for (int i = 0; i < n; i++) outS[i] = src[i + cutSlices];
            return outS;
        }
    }
}
