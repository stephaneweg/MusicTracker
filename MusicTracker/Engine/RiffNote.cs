using System;
using System.Collections.Generic;

namespace MusicTracker.Engine
{
    /// <summary>
    /// A single note in a riff: a pitch held for a contiguous span of slices. Unlike the binary slice
    /// grid, this distinguishes two ADJACENT notes of the SAME pitch (they are two RiffNotes), so the
    /// "détaché" slice-removal hack is no longer needed. Start/Length are in slices at the riff's
    /// SlicesPerQuarter resolution.
    /// </summary>
    /// <summary>One point of a pitch-BEND curve on a note: an offset (slices from the note start) and the bend in
    /// SEMITONES at that point (signed). Played back by interpolating linearly between points.</summary>
    public struct BendPoint
    {
        public int Off;       // slices from the note's start
        public float Semis;   // bend in semitones (signed)
        public BendPoint(int off, float semis) { Off = off; Semis = semis; }
    }

    public struct RiffNote
    {
        public int Note;    // 0..95 (app convention: note 0 = C0 = MIDI 12)
        public int Start;   // first slice (>= 0)
        public int Length;  // number of slices (>= 1)
        public BendPoint[] Bend; // optional pitch-bend curve (null = none); offsets are relative to Start
        public int Voice;   // notation voice (0 = default). Score note-input places on the selected voice; overwrite is per-voice.

        public RiffNote(int note, int start, int length) { Note = note; Start = start; Length = Math.Max(1, length); Bend = null; Voice = 0; }

        public int End => Start + Length; // exclusive end slice

        /// <summary>The bend (semitones) at slice-offset `off` from the note start — linear interpolation, 0 if none.</summary>
        public float BendAt(int off) => BendValue(Bend, off);

        /// <summary>Linear-interpolated bend (semitones) of a curve at slice-offset `off`; 0 if the curve is empty.</summary>
        public static float BendValue(BendPoint[] b, int off)
        {
            if (b == null || b.Length == 0) return 0f;
            if (off <= b[0].Off) return b[0].Semis;
            for (int i = 1; i < b.Length; i++)
            {
                if (off <= b[i].Off)
                {
                    int span = b[i].Off - b[i - 1].Off;
                    if (span <= 0) return b[i].Semis;
                    float f = (off - b[i - 1].Off) / (float)span;
                    return b[i - 1].Semis + (b[i].Semis - b[i - 1].Semis) * f;
                }
            }
            return b[b.Length - 1].Semis;
        }
    }

    /// <summary>
    /// Bridges between the note-list riff model and the <see cref="SequencerSlice"/> grid still used
    /// elsewhere (timeline OR-merge, thumbnails, recorder/import output). Notes are the source of truth;
    /// slices are derived where a grid is genuinely required.
    /// </summary>
    public static class RiffNotes
    {
        /// <summary>
        /// Recover notes from a slice grid: one note per maximal contiguous run of each pitch. Adjacent
        /// same-pitch notes can't be told apart in a grid, so they come back merged as a single note —
        /// this is only for grid-sourced data (recorder, MIDI/MuseScore import) where there are no real
        /// note boundaries to lose.
        /// </summary>
        public static List<RiffNote> FromSlices(SequencerSlice[] slices)
        {
            var notes = new List<RiffNote>();
            if (slices == null) return notes;
            int total = slices.Length;
            for (int note = 0; note < 96; note++)
            {
                int s = 0;
                while (s < total)
                {
                    if (!slices[s].On(note)) { s++; continue; }
                    int s0 = s;
                    while (s < total && slices[s].On(note)) s++;
                    notes.Add(new RiffNote(note, s0, s - s0));
                }
            }
            notes.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : a.Note.CompareTo(b.Note));
            return notes;
        }

        /// <summary>Minimum grid length (slices) needed to hold every note.</summary>
        public static int LengthOf(IEnumerable<RiffNote> notes)
        {
            int len = 0;
            foreach (var n in notes) if (n.End > len) len = n.End;
            return len;
        }

        /// <summary>
        /// Render notes onto a slice grid of the given length (OR-merged). The grid loses the distinction
        /// between adjacent same-pitch notes, so only use this where a grid is actually required.
        /// </summary>
        public static SequencerSlice[] ToSlices(IEnumerable<RiffNote> notes, int length)
        {
            if (length < 1) length = 1;
            var slices = new SequencerSlice[length];
            foreach (var n in notes)
            {
                if (n.Note < 0 || n.Note >= 96) continue;
                for (int c = Math.Max(0, n.Start); c < n.End && c < length; c++) slices[c].On(n.Note, true);
            }
            return slices;
        }

        public static SequencerSlice[] ToSlices(IEnumerable<RiffNote> notes) => ToSlices(notes, LengthOf(notes));
    }
}
