using System;
using NAudio.Midi;
using MusicTracker.Engine.Flow;

namespace MusicTracker.Engine.Timeline
{
    /// <summary>
    /// Writes a <see cref="TimelineProject"/> to a Standard MIDI File: one MIDI track per timeline track (drums on
    /// channel 10), notes at their real 24-slices-per-quarter timing, plus a tempo and time-signature event. The
    /// time signature is exported in REAL quarters per bar (a reinterpreted x/8 stays faithful — its triplet timing
    /// is preserved, bars align), so the file round-trips through the importer.
    /// </summary>
    public static class MidiTimelineExporter
    {
        const int Ppq = 480, Spq = 24;            // MIDI ticks/quarter, timeline slices/quarter
        const double TicksPerSlice = (double)Ppq / Spq; // 20

        public static void Export(string path, TimelineProject project, Func<Guid, Riff> resolve)
        {
            TimelineProject.ResolveLoops(project, resolve); // expand looping repeats before flattening
            var events = new MidiEventCollection(1, Ppq);

            // Track 0: tempo + time signature.
            var meta = events.AddTrack();
            double bpm = project.MainBpm > 0 ? project.MainBpm : 120;
            meta.Add(new TempoEvent((int)Math.Round(60_000_000.0 / bpm), 0));
            int barQuarters = project.TimeSigDen == 8 ? Math.Max(1, project.TimeSigNum / 3) : Math.Max(1, project.TimeSigNum);
            meta.Add(new TimeSignatureEvent(0, barQuarters, 2, 24, 8)); // numerator/4

            int nextChannel = 1;
            foreach (var t in project.Tracks)
            {
                if (t.Items == null || t.Items.Count == 0) continue;
                bool drum = t.Type == TimelineTrackType.Drum;
                int channel = drum ? 10 : (nextChannel == 10 ? ++nextChannel : nextChannel); // skip 10 for melodic
                if (!drum) nextChannel = channel + 1;

                var tr = events.AddTrack();
                tr.Add(new TextEvent(string.IsNullOrEmpty(t.Name) ? "Track" : t.Name, MetaEventType.SequenceTrackName, 0));
                if (!drum) tr.Add(new PatchChangeEvent(0, channel, Math.Max(0, Math.Min(127, t.Instrument))));

                int baseVel = Math.Max(1, Math.Min(127, (int)Math.Round(100 * (t.Volume > 0 ? t.Volume : 1.0))));
                var src = TimelineImporter.FlattenForExport(t, resolve); // notes at absolute slices, 24/quarter
                foreach (var n in src.Notes)
                {
                    int pitch = Math.Max(0, Math.Min(127, n.Pitch));
                    long on = (long)Math.Round(n.StartSlice * TicksPerSlice);
                    int dur = Math.Max(1, (int)Math.Round(n.LengthSlices * TicksPerSlice));
                    int vel = drum ? Math.Max(1, Math.Min(127, n.Velocity)) : baseVel;
                    var noteOn = new NoteOnEvent(on, channel, pitch, vel, dur);
                    tr.Add(noteOn);
                    tr.Add(noteOn.OffEvent);
                }
            }

            events.PrepareForExport(); // sorts each track and appends the End-of-Track meta events
            MidiFile.Export(path, events);
        }
    }
}
