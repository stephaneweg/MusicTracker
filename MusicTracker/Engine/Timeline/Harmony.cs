using System;
using MusicTracker.Engine.Flow;

namespace MusicTracker.Engine.Timeline
{
    /// <summary>
    /// Resolves "which chord is sounding at a given beat" for a melodic line: the arrangement chord grid in structure
    /// mode, else the chord track's PatternGeneratorModule under the cursor. Returns the chord's (root pc, quality,
    /// inversion) so the melodic-line engine can pick chord/passing tones.
    /// </summary>
    public static class Harmony
    {
        public static bool ChordAt(TimelineProject project, Func<Guid, Riff> resolve, double beat, out int root, out int quality, out int inversion)
        {
            root = 0; quality = 0; inversion = 0;
            if (project == null) return false;

            // STRUCTURE mode: read the arrangement chord grid (one ChordCell per cell of ChordSlices slices).
            // The grid describes the FULL bars, so a levée (anacrusis) shifts it: the pickup region borrows chord[0].
            double pickup = project.PickupBeats > 0 ? project.PickupBeats : 0;
            var arr = project.Arrangement;
            if (arr?.Chords != null && arr.Chords.Count > 0)
            {
                int spq = Math.Max(1, arr.SlicesPerQuarter);
                double cellBeats = Math.Max(1e-6, arr.ChordSlices / (double)spq);
                int idx = (int)Math.Floor((beat - pickup) / cellBeats + 1e-9);
                idx = Math.Max(0, Math.Min(arr.Chords.Count - 1, idx));
                root = arr.Chords[idx].Root; quality = arr.Chords[idx].Quality; inversion = 0;
                return true;
            }

            // Else: the first CHORD track whose chord module covers this beat.
            if (project.Tracks != null)
                foreach (var tr in project.Tracks)
                    if (WalkChordTrack(tr, beat, resolve, out root, out quality, out inversion)) return true;
            return false;
        }

        static bool WalkChordTrack(TimelineTrack tr, double beat, Func<Guid, Riff> resolve, out int root, out int quality, out int inversion)
        {
            root = 0; quality = 0; inversion = 0;
            if (tr?.Items == null) return false;
            double cursor = 0;
            foreach (var item in tr.Items)
            {
                cursor += item.SilenceBefore;
                double len = TimelineProject.ItemLength(item, resolve);
                if (item.Module is PatternGeneratorModule pg && Covers(beat, cursor, len))
                { root = pg.Root; quality = pg.Quality; inversion = pg.Inversion; return true; }
                cursor += len;
            }
            return false;
        }

        static bool Covers(double beat, double start, double len) => beat >= start - 1e-9 && beat < start + len - 1e-9;
    }
}
