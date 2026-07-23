using System;
using System.Collections.Generic;
using MusicTracker.Engine.Flow;
using MusicTracker.Engine.Score;

namespace MusicTracker.Engine.Timeline
{
    /// <summary>
    /// Turns a generative <see cref="TemplateSpec"/> into concrete musical material by PICKING one option from each
    /// per-section bank (seeded random) and, for riffs, transposing a degree-1 phrase MODALLY onto each chord with a
    /// light voice-leading pass. Pure/stateless helpers — TimelineScreen wires the results into modules/tracks.
    /// </summary>
    public static class TemplateComposer
    {
        /// <summary>One chord resolved to concrete pitch material within the key, plus its place in the section.</summary>
        public struct ChordSlot
        {
            public int RootPc;       // pitch-class of the chord root
            public int[] ChordPcs;   // pitch-classes of the chord tones
            public double StartBeat; // beats from the section start
            public int Beats;        // duration in beats
        }

        /// <summary>Pick one entry from a bank (seeded). Returns default when the bank is empty.</summary>
        public static T Pick<T>(IReadOnlyList<T> bank, Random rng)
            => (bank == null || bank.Count == 0) ? default(T) : bank[rng.Next(bank.Count)];

        // ---- flat-triplet [a,b,c, a,b,c, …] parsers ---------------------------------------------------------------

        /// <summary>Articulation motif → note list on the custom voice grid (Note = voice row, clamped to the grid).</summary>
        public static List<RiffNote> ArticulationNotes(TplArticulation art)
        {
            var notes = new List<RiffNote>();
            int maxRow = PatternGenerator.CustomVoiceCount - 1;
            foreach (var (a, b, c) in Triplets(art?.Motif))
            {
                if (c <= 0) continue;
                notes.Add(new RiffNote(Clamp(a, 0, maxRow), Math.Max(0, b), Math.Max(1, c)));
            }
            return notes;
        }

        /// <summary>Melodic cell → note list on the 14-row diatonic grid (Note = degree−1). Degrees are 1-based on the wire.</summary>
        public static List<RiffNote> CellNotes(TplMelodicCell cell)
        {
            var notes = new List<RiffNote>();
            int maxRow = PatternGenerator.MelodicRowCount - 1;
            foreach (var (a, b, c) in Triplets(cell?.Cell))
            {
                if (c <= 0) continue;
                notes.Add(new RiffNote(Clamp(a - 1, 0, maxRow), Math.Max(0, b), Math.Max(1, c)));
            }
            return notes;
        }

        /// <summary>Drum groove → note list (Note = drum lane). gmKey (35..81) → lane via <see cref="DrumPattern.LaneForKey"/>.</summary>
        public static List<RiffNote> DrumNotes(TplDrumGroove g)
        {
            var notes = new List<RiffNote>();
            foreach (var (a, b, c) in Triplets(g?.Motif))
            {
                if (c <= 0) continue;                       // a rest contributes nothing (starts are explicit)
                notes.Add(new RiffNote(DrumPattern.LaneForKey(a), Math.Max(0, b), Math.Max(1, c)));
            }
            return notes;
        }

        // ---- modal riff render ------------------------------------------------------------------------------------

        /// <summary>
        /// Render a phrase (chromatic semitones over a degree-1 tonic chord) into an explicit riff that follows the
        /// section's chords: each note is shifted from the tonic to the current chord's root, then SNAPPED to a chord
        /// tone (on a beat) or a scale tone (off-beat), and voice-led so it never leaps far from the previous note.
        /// A negative length in the phrase is a rest (skipped). Output notes are at <paramref name="rspq"/> slices/beat.
        /// </summary>
        /// <param name="chordAt">Chord active at a given beat-from-section-start (must cover the whole section).</param>
        public static List<RiffNote> RenderRiff(
            TplPhrase phrase, double sectionBeats, Func<double, ChordSlot> chordAt,
            int[] scalePcs, int tonicPc, int register, int rspq)
        {
            var outNotes = new List<RiffNote>();
            int spb = phrase != null && phrase.SlicesPerBeat > 0 ? phrase.SlicesPerBeat : 4;
            var events = new List<(double startBeat, double lenBeat, int chrom)>();
            foreach (var (note, start, len) in Triplets(phrase?.Motif))
            {
                if (len < 0) continue;                                  // rest
                events.Add((start / (double)spb, Math.Max(1, len) / (double)spb, note));
            }
            if (events.Count == 0) return outNotes;

            // Tile/loop the phrase across the whole section (phrases are written ~4 bars; a shorter section truncates).
            double phraseBeats = 0;
            foreach (var e in events) phraseBeats = Math.Max(phraseBeats, e.startBeat + e.lenBeat);
            if (phraseBeats < 0.001) return outNotes;

            // The tonic MIDI the chromatic phrase is written against, placed in the requested register.
            int tonicMidi = NearestMidiWithPc(60 + register, tonicPc);
            int prev = int.MinValue;

            for (double baseOff = 0; baseOff < sectionBeats - 0.001; baseOff += phraseBeats)
            {
                foreach (var e in events)
                {
                    double at = baseOff + e.startBeat;
                    if (at >= sectionBeats - 0.001) continue;
                    var chord = chordAt(at);

                    int wanted = tonicMidi + e.chrom;                   // as written over the tonic
                    int rootDelta = NearestSigned(chord.RootPc - tonicPc); // move tonic→chord root
                    wanted += rootDelta;

                    bool onBeat = Math.Abs(at - Math.Round(at)) < 0.06;
                    int[] pcs = (onBeat && chord.ChordPcs != null && chord.ChordPcs.Length > 0) ? chord.ChordPcs : scalePcs;
                    int midi = SnapToPcs(wanted, pcs);

                    if (prev != int.MinValue)                           // voice-leading: fold octaves toward the last note
                    {
                        while (midi - prev > 7) midi -= 12;
                        while (prev - midi > 7) midi += 12;
                        midi = SnapToPcs(midi, pcs);                    // keep it in the chord/scale after folding
                    }
                    prev = midi;

                    int startSlice = (int)Math.Round(at * rspq);
                    int lenSlice = Math.Max(1, (int)Math.Round(e.lenBeat * rspq));
                    double endBeat = at + e.lenBeat;
                    if (endBeat > sectionBeats) lenSlice = Math.Max(1, (int)Math.Round((sectionBeats - at) * rspq));
                    int noteVal = Clamp(midi - 12, 0, 95);              // app convention: RiffNote.Note 0 = MIDI 12
                    outNotes.Add(new RiffNote(noteVal, startSlice, lenSlice));
                }
            }
            return outNotes;
        }

        /// <summary>Pitch-classes of the chord at a degree+quality-index within the key.</summary>
        public static int[] ChordPcs(KeySignature key, int degree1, int qualityIndex)
        {
            int root = AI.AiTranslate.RootPc(key, degree1);
            var midis = PatternGenerator.ChordNotes(root, 4, qualityIndex, 0);
            var set = new HashSet<int>();
            foreach (var m in midis) set.Add(((m % 12) + 12) % 12);
            return new List<int>(set).ToArray();
        }

        // ---- helpers --------------------------------------------------------------------------------------------

        static IEnumerable<(int a, int b, int c)> Triplets(int[] flat)
        {
            if (flat == null) yield break;
            for (int i = 0; i + 2 < flat.Length; i += 3) yield return (flat[i], flat[i + 1], flat[i + 2]);
        }

        static int SnapToPcs(int midi, int[] pcs) => AI.AiTranslate.SnapMidiToPcs(midi, pcs);

        static int NearestSigned(int pcDelta)
        {
            int d = ((pcDelta % 12) + 12) % 12;
            return d > 6 ? d - 12 : d;
        }

        static int NearestMidiWithPc(int aroundMidi, int pc)
        {
            int baseM = aroundMidi - (((aroundMidi % 12) - pc + 12) % 12);
            if (Math.Abs(baseM + 12 - aroundMidi) < Math.Abs(baseM - aroundMidi)) baseM += 12;
            return baseM;
        }

        static int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);
    }
}
