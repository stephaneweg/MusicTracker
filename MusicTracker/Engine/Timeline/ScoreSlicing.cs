using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using MusicTracker.Engine.Flow;

namespace MusicTracker.Engine.Timeline
{
    /// <summary>
    /// Pure helpers that turn an imported <see cref="MuseScoreImporter.Score"/> track + measure range
    /// into riff slices / drum-lane grids. Used by the timeline import
    /// (<see cref="TimelineImporter"/>). No UI dependency.
    /// </summary>
    public static class ScoreSlicing
    {
        /// <summary>One-bar drum LANE grid for [ms,me) (re-sampled srcSpq->dstSpq), or null if no drum notes.</summary>
        public static SequencerSlice[] BuildDrumLaneGrid(MuseScoreImporter.Track track, int ms, int me, int srcSpq, int dstSpq)
        {
            double scale = (double)dstSpq / Math.Max(1, srcSpq);
            int len = Math.Max(1, (int)Math.Round((me - ms) * scale));
            var g = new SequencerSlice[len];
            bool any = false;
            foreach (var note in track.Notes)
            {
                if (note.StartSlice < ms || note.StartSlice >= me) continue;
                int lane = DrumPattern.LaneForKey(note.Pitch); // note.Pitch = GM drum key
                int s = (int)Math.Round((note.StartSlice - ms) * scale);
                if (s < 0) s = 0; else if (s >= len) s = len - 1;
                g[s].On(lane, true); // 1-slice hit (drums are one-shots)
                any = true;
            }
            return any ? g : null;
        }

        public static bool SameGrid(SequencerSlice[] a, SequencerSlice[] b)
        {
            if (a == null || b == null) return a == b;
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
                if (a[i].NotesLow != b[i].NotesLow || a[i].NotesHigh != b[i].NotesHigh) return false;
            return true;
        }

        /// <summary>Builds (or reuses, by content) the riff for the source measure range [ms,me),
        /// re-sampled from srcSpq to dstSpq. Identical phrases are deduped via <paramref name="cache"/>.</summary>
        public static Riff GetOrCreateMeasureRiff(MuseScoreImporter.Track track, int ms, int me, int srcLen, int maxNote,
            Dictionary<string, Riff> cache, List<Riff> newRiffs, int measureIndex, int srcSpq, int dstSpq)
        {
            double scale = (double)dstSpq / Math.Max(1, srcSpq);
            int len = Math.Max(1, (int)Math.Round(srcLen * scale));

            // Build NOTES directly (full durations). No "détaché" / -1-slice hack: two adjacent same-pitch notes
            // are simply two RiffNotes, which re-articulate on playback.
            var noteList = new List<RiffNote>();
            foreach (var note in track.Notes)
            {
                if (note.StartSlice < ms || note.StartSlice >= me) continue;
                int n = note.Pitch - 12; // note index 0 == MIDI 12
                if (n < 0 || n >= maxNote) continue;
                int localStart = (int)Math.Round((note.StartSlice - ms) * scale);
                int localEnd = (int)Math.Round((Math.Min(me, note.StartSlice + note.LengthSlices) - ms) * scale);
                if (localStart < 0) localStart = 0;
                if (localEnd > len) localEnd = len;
                if (localEnd <= localStart) localEnd = localStart + 1; // never lose a note to rounding
                if (localStart >= len) continue;
                var rn = new RiffNote(n, localStart, localEnd - localStart);
                if (note.Bend != null && note.Bend.Length > 0)
                {
                    var b = new BendPoint[note.Bend.Length];
                    for (int i = 0; i < b.Length; i++) b[i] = new BendPoint((int)Math.Round(note.Bend[i].Off * scale), note.Bend[i].Semis);
                    rn.Bend = b;
                }
                noteList.Add(rn);
            }
            noteList.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : a.Note.CompareTo(b.Note));

            // Dedup identical measures by CONTENT only (instrument is contextual).
            var sb = new StringBuilder();
            sb.Append(len).Append('|');
            foreach (var rn in noteList) { sb.Append(rn.Note).Append(':').Append(rn.Start).Append(':').Append(rn.Length); if (rn.Bend != null) foreach (var bp in rn.Bend) sb.Append('b').Append(bp.Off).Append(',').Append(bp.Semis); sb.Append(';'); }
            string key = sb.ToString();
            if (cache.TryGetValue(key, out var existing)) return existing;

            var riff = new Riff
            {
                Name = (string.IsNullOrEmpty(track.Name) ? Engine.InstrumentCatalog.GmName(track.GmProgram) : track.Name) + " m" + (measureIndex + 1),
                Notes = noteList,
                LengthSlices = len,
                SlicesPerQuarter = dstSpq,
            };
            cache[key] = riff;
            newRiffs.Add(riff);
            return riff;
        }
    }
}
