using System;
using MusicTracker.Engine.Score;
using MusicTracker.Engine.Timeline;

namespace MusicTracker.Engine.Flow
{
    /// <summary>
    /// Shared chord-degree helpers (UI-agnostic) used by the chord editor AND the arrangement builders: map a concrete
    /// chord back to (degree, colour, suspension, mode); recover the colour trio of a fixed chord's quality; and
    /// voice-lead a track's chord chain in place.
    /// </summary>
    public static class ChordDegrees
    {
        /// <summary>Map a generated chord (root pc + quality) to a diatonic (degree, PRIMARY colour, SUSPENSION, MODE) so an
        /// inserted chord is DEGREE-LOCKED (follows the key). Returns degree −1 (absolute) for a chromatic root or a quality
        /// that isn't a diatonic colour of its degree. Prefers AUTO mode / no suspension / lower colour.</summary>
        public static (int degree, int colour, int suspension, int mode) DegColour(KeySignature key, int rootPc, int quality)
        {
            int rpc = ((rootPc % 12) + 12) % 12;
            int deg = MusicTheory.DegreeOf(key, rpc);
            foreach (int mode in new[] { 0, 1, 2, 3, 4, 5 })
                foreach (int susp in new[] { 0, 1, 2 })
                    foreach (int col in new[] { 0, 1, 2, 3, 4 })
                    {
                        var d = MusicTheory.DiatonicChord(key, deg, col, susp, mode);
                        if (d.root == rpc && d.quality == quality) return (deg, col, susp, mode);
                    }
            return (-1, 0, 0, 0);
        }

        /// <summary>A FIXED chord = a root note built as degree I of a C-major reference; recover the (colour, suspension,
        /// mode) that yields a given quality (so the editor combos reflect it). Falls back to (triade, none, auto) for a
        /// quality the colour system can't express (rare exotic tensions).</summary>
        public static (int colour, int suspension, int mode) ColourForQuality(int quality)
        {
            var k = new KeySignature { TonicLetter = 0, Accidental = 0, Mode = 0 };
            foreach (int mode in new[] { 0, 1, 2, 3, 4, 5 })
                foreach (int susp in new[] { 0, 1, 2 })
                    foreach (int col in new[] { 0, 1, 2, 3, 4 })
                        if (MusicTheory.DiatonicChord(k, 0, col, susp, mode).quality == quality)
                            return (col, susp, mode);
            return (0, 0, 0);
        }

        /// <summary>Voice-lead the track's CHORD CHAIN in place: each PatternGeneratorModule with VoiceLeadMode != 0 gets its
        /// Inversion (+ Octave) chosen GREEDILY from the previous chord's realized voicing. The first chord keeps its manual
        /// inversion as the seed; "off" chords keep their manual voicing but still seed the next.</summary>
        public static void Revoice(TimelineTrack track)
        {
            if (track?.Items == null) return;
            int[] prev = null; int baseOct = 4; bool haveBase = false;
            Action<PatternGeneratorModule> step = pg =>
            {
                if (pg == null) return;
                if (!haveBase) { baseOct = pg.Octave; haveBase = true; }
                if (pg.VoiceLeadMode != 0 && prev != null)
                {
                    var v = MusicTheory.VoiceLeadStep(prev, pg.Root, pg.Quality, baseOct, pg.VoiceLeadMode - 1);
                    pg.Inversion = v.inversion; pg.Octave = v.octave;
                }
                prev = PatternGenerator.ChordNotes(pg.Root, pg.Octave, pg.Quality, pg.Inversion);
            };
            foreach (var item in track.Items)
            {
                if (item == null) continue;
                if (item.Module is PatternGeneratorModule pg) step(pg);
            }
        }
    }
}
