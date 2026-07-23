using System;
using System.Collections.Generic;
using System.Linq;

namespace MusicTracker.Engine
{
    /// <summary>
    /// Parses a Standard MIDI File into the same <see cref="MuseScoreImporter.Score"/> shape used
    /// by the MuseScore importer, so the graph builder can import either source the same way:
    /// per-track notes, measure boundaries, tempo and General MIDI program. Resolution is 24
    /// slices per quarter note (keeps triplets); the riff player derives the tempo from that.
    /// </summary>
    public static class MidiImporter
    {
        const double SlicesPerQuarter = 24.0;

        // The GM program for a channel at a given time: the latest program change on that channel
        // at or before the time, else the channel's earliest program change, else 0 (piano).
        static int ProgramForChannelAt(List<int[]> pcs, int channel, long time)
        {
            int best = -1; long bestT = -1;
            long earliest = long.MaxValue; int earliestPatch = -1;
            foreach (var pc in pcs)
            {
                if (pc[0] != channel) continue;
                if (pc[2] < earliest) { earliest = pc[2]; earliestPatch = pc[1]; }
                if (pc[2] <= time && pc[2] >= bestT) { bestT = pc[2]; best = pc[1]; }
            }
            if (best >= 0) return best;
            return earliestPatch >= 0 ? earliestPatch : 0;
        }

        public static MuseScoreImporter.Score Load(string path)
        {
            var music = new NAudio.Midi.MidiFile(path, false); // tolerant: skip strict checks
            int ppq = music.DeltaTicksPerQuarterNote;
            if (ppq <= 0) ppq = 480;
            double slicesPerTick = SlicesPerQuarter / ppq;

            var score = new MuseScoreImporter.Score(); // SpeedFactor stays at 24/4 = 6

            // First pass: total length, first tempo, first time signature, and ALL program changes.
            // Program changes are keyed by CHANNEL and often live on a different track than the
            // notes they apply to (a MIDI player tracks program per channel globally) -- so we must
            // collect them across every track, not just the note track.
            long maxTick = 0;
            double bpm = 0;
            int tsNum = 4, tsDenExp = 2; // 4/4 (denominator stored as a power of two: 2 -> 4)
            bool tempoFound = false, tsFound = false;
            var programChanges = new List<int[]>(); // {channel, patch, time}
            foreach (var track in music.Events)
            {
                foreach (var ev in track)
                {
                    if (ev.AbsoluteTime > maxTick) maxTick = ev.AbsoluteTime;
                    if (!tempoFound && ev is NAudio.Midi.TempoEvent te) { bpm = te.Tempo; tempoFound = true; }
                    if (!tsFound && ev is NAudio.Midi.TimeSignatureEvent ts) { tsNum = ts.Numerator; tsDenExp = ts.Denominator; tsFound = true; }
                    if (ev is NAudio.Midi.PatchChangeEvent pc) programChanges.Add(new[] { pc.Channel, pc.Patch, (int)ev.AbsoluteTime });
                }
            }
            score.Bpm = bpm > 0 ? Math.Round(bpm) : 120;

            // Measure length in ticks = numerator * (4 * ppq) / 2^denominatorExponent.
            int denom = 1 << Math.Max(0, Math.Min(6, tsDenExp));
            score.TimeSigN = tsNum; score.TimeSigD = denom; score.HasTimeSig = tsFound;
            long measureTicks = (long)tsNum * (4L * ppq) / Math.Max(1, denom);
            if (measureTicks <= 0) measureTicks = 4L * ppq;

            // Measure boundaries (constant time signature). Always emit at least one measure.
            for (long t = 0; t <= maxTick; t += measureTicks)
                score.MeasureStartSlices.Add((int)Math.Round(t * slicesPerTick));
            if (score.MeasureStartSlices.Count == 0) score.MeasureStartSlices.Add(0);

            // Second pass: one Track per MIDI track that has notes.
            foreach (var track in music.Events)
            {
                if (!track.Any(ev => ev is NAudio.Midi.NoteOnEvent on && on.Velocity > 0)) continue;

                var tr = new MuseScoreImporter.Track();
                var firstNote = track.OfType<NAudio.Midi.NoteOnEvent>().FirstOrDefault(no => no.Velocity > 0);
                int channel = firstNote != null ? firstNote.Channel : 1;
                long firstNoteTime = firstNote != null ? firstNote.AbsoluteTime : 0;
                tr.IsDrum = channel == 10;
                // Program for this track's channel, active at its first note (channel map, all tracks).
                tr.GmProgram = ProgramForChannelAt(programChanges, channel, firstNoteTime);

                foreach (var ev in track)
                {
                    if (ev is NAudio.Midi.TextEvent txt && txt.MetaEventType == NAudio.Midi.MetaEventType.SequenceTrackName
                        && string.IsNullOrEmpty(tr.Name))
                        tr.Name = txt.Text;
                }
                if (string.IsNullOrEmpty(tr.Name) && tr.IsDrum) tr.Name = "Drums";

                // Pitch BEND per channel over time: {sliceAbs, channel, milli-semitones}. Default bend range ±2
                // semitones (RPN 0,0 could change it, but almost no file does). Events are time-ordered in the track.
                var pwList = new List<int[]>();
                foreach (var ev in track)
                    if (ev is NAudio.Midi.PitchWheelChangeEvent pw)
                        pwList.Add(new[] { (int)Math.Round(pw.AbsoluteTime * slicesPerTick), pw.Channel, (int)Math.Round((pw.Pitch - 8192) / 8192.0 * 2.0 * 1000.0) });

                // Track channel volume (CC7) and expression (CC11) over time so each note's
                // loudness reflects them, in addition to its own velocity. Events are time-ordered.
                var vol7 = new Dictionary<int, int>();
                var vol11 = new Dictionary<int, int>();
                foreach (var ev in track)
                {
                    if (ev is NAudio.Midi.ControlChangeEvent cc)
                    {
                        if ((int)cc.Controller == 7) vol7[cc.Channel] = cc.ControllerValue;
                        else if ((int)cc.Controller == 11) vol11[cc.Channel] = cc.ControllerValue;
                        continue;
                    }
                    var on = ev as NAudio.Midi.NoteOnEvent;
                    if (on == null || on.Velocity <= 0 || on.OffEvent == null) continue;
                    int start = (int)Math.Round(on.AbsoluteTime * slicesPerTick);
                    int end = (int)Math.Round(on.OffEvent.AbsoluteTime * slicesPerTick);
                    int v7 = vol7.TryGetValue(on.Channel, out var a) ? a : 100;
                    int v11 = vol11.TryGetValue(on.Channel, out var b) ? b : 127;
                    double chan = (v7 / 127.0) * (v11 / 127.0);
                    int vel = (int)Math.Round(on.Velocity * chan);
                    tr.Notes.Add(new MuseScoreImporter.Note
                    {
                        Pitch = on.NoteNumber,
                        StartSlice = start,
                        LengthSlices = Math.Max(1, end - start),
                        Velocity = Math.Max(1, Math.Min(127, vel)),
                        Bend = BuildBend(pwList, on.Channel, start, end),
                    });
                }

                if (tr.Notes.Count > 0) score.Tracks.Add(tr);
            }

            score.SliceCount = Math.Max(1, (int)Math.Round(maxTick * slicesPerTick));
            return score;
        }

        // Build a note's pitch-bend curve from the channel's pitch-wheel events: a point at offset 0 (the bend value
        // active when the note starts) + a point per wheel change DURING the note (offset = slices from note start).
        // Returns null if the bend stays negligible over the note.
        static BendPoint[] BuildBend(List<int[]> pw, int channel, int startSlice, int endSlice)
        {
            if (pw == null || pw.Count == 0) return null;
            float atStart = 0f;
            foreach (var e in pw) if (e[1] == channel && e[0] <= startSlice) atStart = e[2] / 1000f;   // last value at/before the note start
            var curve = new List<BendPoint> { new BendPoint(0, atStart) };
            foreach (var e in pw) if (e[1] == channel && e[0] > startSlice && e[0] < endSlice) curve.Add(new BendPoint(e[0] - startSlice, e[2] / 1000f));
            bool any = false; foreach (var c in curve) if (Math.Abs(c.Semis) >= 0.02f) { any = true; break; }
            return any ? curve.ToArray() : null;
        }
    }
}
