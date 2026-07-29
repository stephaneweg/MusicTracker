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
                // PolyChord : on résout l'accord actif à la position DANS le module (beat − cursor). Sans cette
                // branche, une ligne mélodique posée sous un PolyChord ne verrait aucune harmonie.
                if (item.Module is PolyChordModule pc && Covers(beat, cursor, len)
                    && PolyChord.ChordAt(pc, beat - cursor, out var it, out _))
                { root = it.Root; quality = it.Quality; inversion = it.Inversion; return true; }
                // CadenceModule : mêmes accords « invisibles » que le trou historique — combler tant qu'on y est.
                if (item.Module is CadenceModule cm && Covers(beat, cursor, len) && cm.Chords != null && cm.Chords.Count > 0)
                {
                    int cellBeats = Math.Max(1, cm.BeatsPerBar);
                    int idx = (int)Math.Floor((beat - cursor) / cellBeats + 1e-9);
                    idx = Math.Max(0, Math.Min(cm.Chords.Count - 1, idx));
                    var cc = cm.Chords[idx];
                    root = cc.Root; quality = cc.Quality; inversion = cc.Inversion;
                    return true;
                }
                cursor += len;
            }
            return false;
        }

        static bool Covers(double beat, double start, double len) => beat >= start - 1e-9 && beat < start + len - 1e-9;
    }
}
