using System;
using System.Collections.Generic;
using System.Linq;
using MusicTracker.Engine.Flow;

namespace MusicTracker.Engine.Timeline
{
    /// <summary>
    /// Builds a <see cref="TimelineProject"/> from an imported score: one track per staff (melodic →
    /// instrument track of riffs, percussion → drum track of custom drum patterns), measure-groups become
    /// modules, identical consecutive drum bars collapse into a Repeat, silences become each next item's
    /// SilenceBefore, and (optionally) note dynamics become base volume + volume-automation points.
    /// Shares the slicing logic with the graph import via <see cref="ScoreSlicing"/>.
    /// </summary>
    public static class TimelineImporter
    {
        public class Result
        {
            public TimelineProject Project;
            public List<Riff> Riffs;
            public bool MeterUncertain; // declared 4/4 (the MIDI default) or absent → ask the user
            public bool Ternary;        // the rhythm is ternary (triplet-dominant) → suggest a compound x/8
        }

        public static Result Build(MuseScoreImporter.Score score, int measuresPerRiff, int slicesPerBeat, bool importVolume)
        {
            measuresPerRiff = Math.Max(1, measuresPerRiff);
            int dstSpq = Math.Max(1, slicesPerBeat);

            var project = new TimelineProject();
            var newRiffs = new List<Riff>();

            var boundaries = new List<int>(score.MeasureStartSlices);
            if (boundaries.Count == 0) return new Result { Project = project, Riffs = newRiffs };
            boundaries.Add(Math.Max(score.SliceCount, boundaries[boundaries.Count - 1] + 1)); // timeline end
            int measureCount = boundaries.Count - 1;

            project.Tempo = new List<TempoChange> { new TempoChange { Beat = 0, Bpm = score.Bpm > 0 ? score.Bpm : 120 } };

            int spq = Math.Max(1, (int)Math.Round(score.SpeedFactor * 4)); // source slices per quarter
            int maxNote = 12 * 8;
            var riffCache = new Dictionary<string, Riff>();

            // ---- Time signature + ternary detection (BEFORE slicing, so notes get snapped on the right grid) ----
            // A declared 4/4 is the MIDI default (suspect); a declared NON-4/4 is intentional. Detect a TERNARY
            // rhythm (durations that are thirds of a beat, not halves → triplet feel): with a default meter, a
            // ternary rhythm means a compound piece exported as 4/4-triplets → read it as a compound x/8 (6/8 for
            // now; finer bar grouping comes later) with a ×1.5 SCORE-display scale ONLY. The note DATA is never
            // rescaled — at 24 slices/quarter triplets are already exact (24 ÷ 3 = 8, ÷ 8 = 3, ÷ 4 = 6).
            int total = 0, trip = 0;
            foreach (var tr in score.Tracks) if (!tr.IsDrum) foreach (var n in tr.Notes)
            {
                double d = n.LengthSlices / (double)spq; if (d <= 1e-6) continue; total++;
                bool t3 = Math.Abs(d * 3 - Math.Round(d * 3)) < 0.08; // a third of a beat (triplet)
                bool t2 = Math.Abs(d * 2 - Math.Round(d * 2)) < 0.08; // a half of a beat (duple)
                if (t3 && !t2) trip++;
            }
            bool ternary = total > 0 && trip / (double)total > 0.5;
            bool declaredDefault = !score.HasTimeSig || (score.TimeSigN == 4 && score.TimeSigD == 4);

            if (!declaredDefault) { project.TimeSigNum = Math.Max(1, score.TimeSigN); project.TimeSigDen = Math.Max(1, score.TimeSigD); project.TimeSigScale = 1.0; }
            else if (ternary) { project.TimeSigNum = 6; project.TimeSigDen = 8; project.TimeSigScale = 1.5; } // 4/4-in-triplets → 6/8
            else { project.TimeSigNum = 4; project.TimeSigDen = 4; project.TimeSigScale = 1.0; }
            Flow.PatternGenerator.Ternary = project.TimeSigDen == 8; // generators (harp roll) follow the imported meter

            // Snap every note onset + length to the meter grid (cleans up live-recorded MIDI jitter): simple x/4 →
            // 1/8 of a beat, compound x/8 → 1/6 of a beat (so triplets survive). On the 24-res data: 24/8 = 3, 24/6 = 4.
            int snapQ = Math.Max(1, project.TimeSigDen == 8 ? (int)Math.Round(spq / 6.0) : (int)Math.Round(spq / 8.0));
            foreach (var tr in score.Tracks) foreach (var n in tr.Notes)
            {
                n.StartSlice = (int)Math.Round(n.StartSlice / (double)snapQ) * snapQ;
                n.LengthSlices = Math.Max(snapQ, (int)Math.Round(n.LengthSlices / (double)snapQ) * snapQ);
            }

            // Re-segment the measures for a REINTERPRETED compound meter: the croche is a raw triplet-eighth
            // (spq/3 slices), so one bar = num × spq/3 slices — half a 4/4 bar for 6/8. The importer cut measures
            // on the declared 4/4 grid (96 slices), so a riff would span two 6/8 bars; rebuild the boundaries on
            // the compound bar length so ONE riff = ONE displayed bar. Explicit meters keep the file's boundaries.
            if (declaredDefault && ternary)
            {
                int barSlices = Math.Max(1, project.TimeSigNum * Math.Max(1, (int)Math.Round(spq / 3.0)));
                int end = boundaries[boundaries.Count - 1];
                boundaries = new List<int>();
                for (int s = 0; s < end; s += barSlices) boundaries.Add(s);
                boundaries.Add(end);
                measureCount = boundaries.Count - 1;
            }

            foreach (var track in score.Tracks)
            {
                if (track.Notes.Count == 0) continue;
                project.Tracks.Add(track.IsDrum
                    ? BuildDrumTrack(track, boundaries, measureCount, measuresPerRiff, spq, dstSpq)
                    : BuildMelodicTrack(track, boundaries, measureCount, measuresPerRiff, spq, dstSpq, maxNote, importVolume, riffCache, newRiffs));
            }

            // Detect the concert key from the melodic notes + the first strong beat played.
            var w12 = new double[12];
            int firstSlice = int.MaxValue;
            foreach (var tr in score.Tracks) if (!tr.IsDrum) foreach (var n in tr.Notes)
            { w12[((n.Pitch % 12) + 12) % 12] += Math.Max(1, n.LengthSlices); if (n.StartSlice < firstSlice) firstSlice = n.StartSlice; }
            if (firstSlice != int.MaxValue)
            {
                int low = int.MaxValue, firstLowPc = -1; var firstPcs = new HashSet<int>();
                foreach (var tr in score.Tracks) if (!tr.IsDrum) foreach (var n in tr.Notes)
                    if (n.StartSlice <= firstSlice + Math.Max(1, spq / 8))
                    { int pc = ((n.Pitch % 12) + 12) % 12; firstPcs.Add(pc); if (n.Pitch < low) { low = n.Pitch; firstLowPc = pc; } }
                // Explicit armure from the file → keep it; use its explicit mode if present, else detect the
                // mode; with no explicit armure, detect everything from the notes.
                if (score.KeyFifths.HasValue)
                    project.Key = score.KeyIsMinor.HasValue
                        ? MusicTracker.Engine.Score.KeySig.FromFifths(score.KeyFifths.Value, score.KeyIsMinor.Value)
                        : MusicTracker.Engine.Score.KeySig.DetectMode(score.KeyFifths.Value, w12, firstLowPc, firstPcs);
                else
                    project.Key = MusicTracker.Engine.Score.KeySig.Detect(w12, firstLowPc, firstPcs);
            }

            return new Result { Project = project, Riffs = newRiffs, MeterUncertain = declaredDefault, Ternary = ternary };
        }

        /// <summary>
        /// Re-bars every track for a new measure length (in real beats = the temps count): each track is flattened
        /// to one continuous note stream (riffs + repeats + silences merged, in absolute slices) then re-cut into
        /// bars of <paramref name="barBeats"/> beats — exactly "merge a channel into one riff, then re-subdivide".
        /// Instrument/clef/volume are kept; only Items are rebuilt. Returns the new riffs (add them to the library).
        /// </summary>
        public static List<Riff> ReSegment(TimelineProject project, int barBeats, Func<Guid, Riff> resolve)
        {
            const int spq = 24, dstSpq = 24, maxNote = 12 * 8;
            barBeats = Math.Max(1, barBeats);
            var newRiffs = new List<Riff>();
            if (project == null || project.Tracks.Count == 0) return newRiffs;
            TimelineProject.ResolveLoops(project, resolve); // expand loops before flattening

            // 1) Flatten each track to a slice-note Track (absolute positions @ 24/quarter); track the total length.
            var flat = new List<MuseScoreImporter.Track>();
            int totalSlices = 1;
            foreach (var t in project.Tracks)
            {
                var src = FlattenTrack(t, resolve, spq);
                flat.Add(src);
                foreach (var n in src.Notes) totalSlices = Math.Max(totalSlices, n.StartSlice + Math.Max(1, n.LengthSlices));
            }

            // 2) New measure boundaries on the new bar length.
            int barSlices = Math.Max(1, barBeats * spq);
            var boundaries = new List<int>();
            for (int s = 0; s < totalSlices; s += barSlices) boundaries.Add(s);
            boundaries.Add(Math.Max(totalSlices, boundaries.Count > 0 ? boundaries[boundaries.Count - 1] + 1 : barSlices));
            int measureCount = boundaries.Count - 1;

            // 3) Rebuild each track's items (one riff per bar). Keep the track's instrument/clef/volume.
            // Tracks built from CHORD/CADENCE generators keep their modules AS-IS (re-cutting would flatten the
            // cadence into riffs and lose it) — only imported/riff content is re-barred.
            var cache = new Dictionary<string, Riff>();
            for (int i = 0; i < project.Tracks.Count; i++)
            {
                if (TrackHasGenerator(project.Tracks[i])) continue; // preserve the cadence/chord modules
                var src = flat[i];
                var rebuilt = src.IsDrum
                    ? BuildDrumTrack(src, boundaries, measureCount, 1, spq, dstSpq)
                    : BuildMelodicTrack(src, boundaries, measureCount, 1, spq, dstSpq, maxNote, false, cache, newRiffs);
                project.Tracks[i].Items.Clear();
                foreach (var it in rebuilt.Items) project.Tracks[i].Items.Add(it);
            }
            return newRiffs;
        }

        // True if the track contains a chord/cadence generator module (top-level or inside a Repeat) — such a track
        // is left untouched by re-segmentation so a meter change preserves the cadence instead of riff-ifying it.
        static bool TrackHasGenerator(TimelineTrack t)
        {
            if (t?.Items == null) return false;
            foreach (var it in t.Items)
            {
               if (it.Module is PatternGeneratorModule || it.Module is CadenceModule) return true;
            }
            return false;
        }

        /// <summary>Flatten a timeline track to absolute slice-notes (24/quarter) — repeats expanded, silences
        /// absorbed into positions. Reused by the MIDI export. Call <see cref="TimelineProject.ResolveLoops"/> first.</summary>
        public static MuseScoreImporter.Track FlattenForExport(TimelineTrack t, Func<Guid, Riff> resolve) => FlattenTrack(t, resolve, 24);

        // Flatten a timeline track into a slice-note Track (absolute positions, 24 slices/quarter), expanding
        // repeats and absorbing silences into note positions — the "merge into one riff" half of re-segmenting.
        static MuseScoreImporter.Track FlattenTrack(TimelineTrack t, Func<Guid, Riff> resolve, int spq)
        {
            var src = new MuseScoreImporter.Track { Name = t.Name, GmProgram = t.Instrument, IsDrum = t.Type == TimelineTrackType.Drum };
            double cur = 0;
            foreach (var item in t.Items)
            {
                cur += item.SilenceBefore;
                FlattenItem(src, item, cur, resolve, spq);
                cur += TimelineProject.ItemLength(item, resolve);
            }
            return src;
        }

        static void FlattenItem(MuseScoreImporter.Track src, TimelineItem item, double startBeat, Func<Guid, Riff> resolve, int spq)
        {
            if (item.Module != null) FlattenLeaf(src, item.Module, startBeat, resolve, spq);
        }

        static void FlattenLeaf(MuseScoreImporter.Track src, FlowModule m, double startBeat, Func<Guid, Riff> resolve, int spq)
        {
            if (src.IsDrum && m is DrumPatternModule dp) // drum hits: read the lane grid → GM keys (round-trips losslessly)
            {
                var grid = DrumPattern.Generate(dp);
                var sl = grid?.Slices;
                if (sl == null) return;
                int gspq = grid.SlicesPerQuarter > 0 ? grid.SlicesPerQuarter : DrumPattern.SlicesPerQuarter;
                for (int s = 0; s < sl.Length; s++)
                    for (int lane = 0; lane < DrumPattern.LaneCount; lane++)
                        if (sl[s].On(lane))
                            src.Notes.Add(new MuseScoreImporter.Note { Pitch = DrumPattern.KeyForLane(lane), StartSlice = (int)Math.Round((startBeat + (double)s / gspq) * spq), LengthSlices = 1, Velocity = 100 });
                return;
            }
            Riff riff = m is PlayRiffModule pr ? resolve?.Invoke(pr.RiffId)
                      : m is PatternGeneratorModule pg ? PatternGenerator.Generate(pg)
                      : m is CadenceModule cm ? PatternGenerator.GenerateCadence(cm)
                      : null;
            if (riff?.Notes == null) return;
            int rspq = riff.SlicesPerQuarter > 0 ? riff.SlicesPerQuarter : 4;
            foreach (var n in riff.Notes)
                src.Notes.Add(new MuseScoreImporter.Note
                {
                    Pitch = n.Note + 12, // RiffNote index → MIDI (note 0 == MIDI 12, matching GetOrCreateMeasureRiff)
                    StartSlice = (int)Math.Round((startBeat + (double)n.Start / rspq) * spq),
                    LengthSlices = Math.Max(1, (int)Math.Round((double)n.Length / rspq * spq)),
                    Velocity = 100,
                });
        }

        static TimelineTrack BuildMelodicTrack(MuseScoreImporter.Track track, List<int> boundaries, int measureCount,
            int measuresPerRiff, int spq, int dstSpq, int maxNote, bool importVolume,
            Dictionary<string, Riff> riffCache, List<Riff> newRiffs)
        {
            var tl = new TimelineTrack
            {
                Type = TimelineTrackType.Instrument,
                Instrument = Math.Max(0, Math.Min(127, track.GmProgram)),
                Name = string.IsNullOrEmpty(track.Name) ? InstrumentCatalog.GmName(track.GmProgram) : track.Name,
                Clef = track.Clef, // explicit clef from the file (e.g. piano's bass staff)
            };

            double cursorBeat = 0;   // running end of placed items (beats); silence is absorbed here
            double lastVol = -1;
            bool baseVolSet = false;

            for (int m = 0; m < measureCount; m += measuresPerRiff)
            {
                int mEnd = Math.Min(measureCount, m + measuresPerRiff);
                int ms = boundaries[m], me = boundaries[mEnd];
                var measNotes = track.Notes.Where(nn => nn.StartSlice >= ms && nn.StartSlice < me
                    && (nn.Pitch - 12) >= 0 && (nn.Pitch - 12) < maxNote).OrderBy(nn => nn.StartSlice).ToList();
                if (measNotes.Count == 0) continue; // fully silent group: absorbed by the next item's SilenceBefore

                // One riff for the whole measure-group (volume does NOT split the riff here — it's just
                // automation points on the track's volume lane).
                var riff = ScoreSlicing.GetOrCreateMeasureRiff(track, ms, me, me - ms, maxNote, riffCache, newRiffs, m, spq, dstSpq);
                double startBeat = ms / (double)spq;
                double len = riff.Slices.Length / (double)Math.Max(1, riff.SlicesPerQuarter);
                tl.Items.Add(new TimelineItem
                {
                    Module = new PlayRiffModule { RiffId = riff.Id },
                    SilenceBefore = Math.Max(0, startBeat - cursorBeat),
                });
                cursorBeat = startBeat + len;

                // Dynamics -> base volume + automation points (first note sets the base; later changes
                // add a point at the note's beat). No riff splitting.
                if (importVolume)
                    foreach (var nn in measNotes)
                    {
                        double v = Math.Round(nn.Velocity / 127.0, 2);
                        if (!baseVolSet) { tl.Volume = v; baseVolSet = true; lastVol = v; }
                        else if (Math.Abs(v - lastVol) >= 0.06)
                        {
                            tl.VolumeAutomation.Add(new VolumePoint { Beat = nn.StartSlice / (double)spq, Volume = v });
                            lastVol = v;
                        }
                    }
            }
            return tl;
        }

        static TimelineTrack BuildDrumTrack(MuseScoreImporter.Track track, List<int> boundaries, int measureCount,
            int measuresPerRiff, int spq, int dstSpq)
        {
            var tl = new TimelineTrack
            {
                Type = TimelineTrackType.Drum,
                Instrument = InstrumentCatalog.DrumIndex,
                Name = string.IsNullOrEmpty(track.Name) ? "Batterie" : track.Name,
            };

            // Per measure-group: the drum grid (or null if silent) + its length in beats.
            var gMs = new List<int>();
            var gGrid = new List<SequencerSlice[]>();
            var gBeats = new List<int>();
            for (int m = 0; m < measureCount; m += measuresPerRiff)
            {
                int mEnd = Math.Min(measureCount, m + measuresPerRiff);
                int ms = boundaries[m], me = boundaries[mEnd];
                gMs.Add(ms);
                gGrid.Add(ScoreSlicing.BuildDrumLaneGrid(track, ms, me, spq, dstSpq));
                gBeats.Add(Math.Max(1, (int)Math.Round((double)(me - ms) / spq)));
            }

            double cursorBeat = 0;
            int i = 0;
            while (i < gGrid.Count)
            {
                if (gGrid[i] == null) { i++; continue; } // silent group: absorbed by the next item's SilenceBefore

                int run = 1;
                while (i + run < gGrid.Count && ScoreSlicing.SameGrid(gGrid[i + run], gGrid[i])) run++;

                var drum = new DrumPatternModule { Style = DrumPattern.CustomStyle, BeatsPerBar = gBeats[i], Repeats = 1 };
                drum.SetCustom(gGrid[i], dstSpq);
                double oneLen = gGrid[i].Length / (double)dstSpq; // one cycle's length in beats
                double startBeat = gMs[i] / (double)spq;

                var item = new TimelineItem { Module = drum };
                item.SilenceBefore = Math.Max(0, startBeat - cursorBeat);
                tl.Items.Add(item);

                cursorBeat = startBeat + oneLen * run;
                i += run;
            }
            return tl;
        }
    }
}
