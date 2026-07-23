using System;
using System.Collections.Generic;
using MusicTracker.Engine.Flow;
using MusicTracker.Engine.Timeline;

namespace MusicTracker.Engine.Score
{
    public enum ScoreClefKind { Treble, Bass, Alto, Tenor, GrandStaff }

    /// <summary>A pitched note on the score: absolute start (beats), duration (beats), sounding MIDI pitch.</summary>
    public struct ScoreNote
    {
        public double StartBeat;
        public double Beats;
        public int Midi;        // sounding (concert) MIDI pitch
        public int Voice;       // 0 = main (chord/melody); 1 = bass voice (a low note shown apart, stems down)
        public bool Arpeggio;   // notes that were a FAST ROLL (staggered raw onsets snapping to one chord) → draw/export an arpeggio mark
    }

    /// <summary>A track flattened to notation: the notes + the chosen clef + the written-pitch transposition.</summary>
    public class TrackScore
    {
        public List<ScoreNote> Notes = new List<ScoreNote>();
        public double TotalBeats;
        public ScoreClefKind Clef;
        public int Transpose;   // semitones added to the SOUNDING pitch to get the WRITTEN pitch
        public bool IsDrum;
        public KeySignature Key; // concert key of the piece (the score transposes it per Transpose)
    }

    /// <summary>The piece's key signature, set by the user (toolbar) or detected at import. CONCERT pitch.</summary>
    public class KeySignature
    {
        public int TonicLetter; // 0=Do(C) .. 6=Si(B)
        public int Accidental;  // -1 flat, 0 natural, +1 sharp (on the tonic)
        public int Mode;        // 0 = major, 1 = minor (drives the armure)

        /// <summary>Full mode index into <see cref="MusicalMode"/> (church modes + harmonic/melodic minor), so a
        /// piece reshaped to e.g. Dorian remembers it for the next transpose. -1 = derive from <see cref="Mode"/>.
        /// Absent in old saves → stays -1 (System.Text.Json keeps the initializer for missing members).</summary>
        public int FullMode { get; set; } = -1;
    }

    /// <summary>A key signature resolved to drawable data (per-letter alterations, count, name, leading tone).</summary>
    public sealed class DerivedKey
    {
        public int[] Acc = new int[7];   // alteration per letter (0=C..6=B): -1/0/+1
        public bool Flats;
        public int Count;
        public string Name = "";
        public int LeadingSharpPc = -1;  // minor leading tone, forced to a sharp spelling
    }

    /// <summary>Key-signature math: derive an armure (with transposition) and detect a key from notes.</summary>
    public static class KeySig
    {
        static readonly int[] LetterFifths = { 0, 2, 4, -1, 1, 3, 5 }; // circle-of-fifths pos of C,D,E,F,G,A,B major
        static readonly int[] LetterPcs = { 0, 2, 4, 5, 7, 9, 11 };
        // Tonic spelling by signature fifths (index = fifths + 7, range -7..7).
        static readonly string[] MajNames = { "Do♭", "Sol♭", "Ré♭", "La♭", "Mi♭", "Si♭", "Fa", "Do", "Sol", "Ré", "La", "Mi", "Si", "Fa♯", "Do♯" };
        static readonly string[] MinNames = { "La♭", "Mi♭", "Si♭", "Fa", "Do", "Sol", "Ré", "La", "Mi", "Si", "Fa♯", "Do♯", "Sol♯", "Ré♯", "La♯" };
        static readonly int[] MajLetter = { 0, 4, 1, 5, 2, 6, 3, 0, 4, 1, 5, 2, 6, 3, 0 };
        static readonly int[] MajAcc = { -1, -1, -1, -1, -1, -1, 0, 0, 0, 0, 0, 0, 0, 1, 1 };
        static readonly int[] MinLetter = { 5, 2, 6, 3, 0, 4, 1, 5, 2, 6, 3, 0, 4, 1, 5 };
        static readonly int[] MinAcc = { -1, -1, -1, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 1 };

        /// <summary>Resolve a (concert) key to its written armure for a part transposed by <paramref name="transpose"/>
        /// semitones. transpose 0 = concert (the user's exact spelling is kept).</summary>
        public static DerivedKey Derive(KeySignature k, int transpose)
        {
            var dk = new DerivedKey();
            if (k == null) k = new KeySignature();
            int letter = Math.Max(0, Math.Min(6, k.TonicLetter));
            int acc = Math.Max(-1, Math.Min(1, k.Accidental));
            int fifths = LetterFifths[letter] + 7 * acc - (k.Mode == 1 ? 3 : 0);
            fifths += transpose * 7;                 // transposing shifts the key on the circle of fifths
            while (fifths > 7) fifths -= 12;
            while (fifths < -7) fifths += 12;
            if (transpose != 0) { if (fifths > 6) fifths -= 12; else if (fifths < -6) fifths += 12; } // re-spell to ≤6

            dk.Flats = fifths < 0;
            dk.Count = Math.Abs(fifths);
            int[] sharpLetters = { 3, 0, 4, 1, 5, 2, 6 }; // F C G D A E B
            int[] flatLetters = { 6, 2, 5, 1, 4, 0, 3 };   // B E A D G C F
            var order = dk.Flats ? flatLetters : sharpLetters;
            for (int i = 0; i < dk.Count && i < 7; i++) dk.Acc[order[i]] = dk.Flats ? -1 : +1;

            int idx = Math.Max(0, Math.Min(14, fifths + 7));
            dk.Name = (k.Mode == 1 ? MinNames[idx] : MajNames[idx]) + (k.Mode == 1 ? " mineur" : " majeur");
            int writtenTonicPc = (((LetterPcs[letter] + acc + transpose) % 12) + 12) % 12;
            dk.LeadingSharpPc = k.Mode == 1 ? (writtenTonicPc + 11) % 12 : -1;
            return dk;
        }

        static readonly string[] LetterNamesFr = { "Do", "Ré", "Mi", "Fa", "Sol", "La", "Si" };
        static string AccSym(int a) => a == 0 ? "" : a > 0 ? new string('♯', a) : new string('♭', -a);

        /// <summary>Spell a pitch class as a French note name RESPECTING the key: a diatonic pc uses its scale letter +
        /// the armure accidental (so pc 3 in Mi♭ majeur reads "Mi♭", not "Ré♯"); a chromatic pc follows the key's
        /// flat/sharp preference. Falls back to a fixed sharp spelling if nothing fits.</summary>
        public static string SpellPc(int pc, KeySignature key)
        {
            pc = ((pc % 12) + 12) % 12;
            var dk = Derive(key ?? new KeySignature(), 0);
            for (int l = 0; l < 7; l++)                                   // diatonic (letter + armure accidental)
                if ((((LetterPcs[l] + dk.Acc[l]) % 12) + 12) % 12 == pc) return LetterNamesFr[l] + AccSym(dk.Acc[l]);
            for (int l = 0; l < 7; l++)                                   // a natural of a letter the armure altered
                if (((LetterPcs[l] % 12) + 12) % 12 == pc) return LetterNamesFr[l];
            int dir = dk.Flats ? -1 : +1;                                 // chromatic: one step in the key's direction
            for (int l = 0; l < 7; l++)
                if ((((LetterPcs[l] + dk.Acc[l] + dir) % 12) + 12) % 12 == pc) return LetterNamesFr[l] + AccSym(dk.Acc[l] + dir);
            string[] fixedFr = { "Do", "Do♯", "Ré", "Ré♯", "Mi", "Fa", "Fa♯", "Sol", "Sol♯", "La", "La♯", "Si" };
            return fixedFr[pc];
        }

        /// <summary>FUNCTIONAL spelling: given the chord's scale DEGREE (0..6 = which letter above the tonic) and its chromatic
        /// ALTER (0 natural · -1 flat · +1 sharp of that degree), spell with that letter — so a ♭VII reads "Si♭", not "La♯".
        /// degree &lt; 0 falls back to the pitch-class spelling <see cref="SpellPc(int,KeySignature)"/>.</summary>
        public static string SpellPc(int pc, KeySignature key, int degree, int alter)
        {
            if (degree < 0) return SpellPc(pc, key);
            key = key ?? new KeySignature();
            var dk = Derive(key, 0);
            int letter = ((((key.TonicLetter + degree) % 7) + 7) % 7);
            return LetterNamesFr[letter] + AccSym(dk.Acc[letter] + alter);
        }

        // Krumhansl-Schmuckler key profiles (perceived tonal-hierarchy weight per scale degree, 0 = tonic).
        static readonly double[] KSMaj = { 6.35, 2.23, 3.48, 2.33, 4.38, 4.09, 2.52, 5.19, 2.39, 3.66, 2.29, 2.88 };
        static readonly double[] KSMin = { 6.33, 2.68, 3.52, 5.38, 2.60, 3.53, 2.54, 4.75, 3.98, 2.69, 3.34, 3.17 };

        /// <summary>Pearson correlation between a 12-bin pitch-class histogram and the K-S profile of the key
        /// (tonic pitch-class, major/minor). The classic Krumhansl-Schmuckler key-finding score.</summary>
        static double CorrKS(double[] w, int tonic, bool minor)
        {
            var prof = minor ? KSMin : KSMaj;
            double mw = 0, mp = 0;
            for (int p = 0; p < 12; p++) { mw += w[p]; mp += prof[p]; }
            mw /= 12; mp /= 12;
            double num = 0, dw = 0, dp = 0;
            for (int p = 0; p < 12; p++)
            {
                double a = w[p] - mw;
                double b = prof[(((p - tonic) % 12) + 12) % 12] - mp;
                num += a * b; dw += a * a; dp += b * b;
            }
            return (dw <= 0 || dp <= 0) ? -1 : num / Math.Sqrt(dw * dp);
        }

        /// <summary>Detect a (concert) key from a duration-weighted pitch-class histogram + the first strong beat,
        /// via the Krumhansl-Schmuckler algorithm: correlate the profile against all 24 major/minor keys and keep
        /// the best. The first/lowest strong note nudges the relative-major/minor choice (they share a collection,
        /// so K-S alone can confuse them) toward the pitch the piece actually rests on.</summary>
        public static KeySignature Detect(double[] w12, int firstLowPc, System.Collections.Generic.HashSet<int> firstPcs)
        {
            if (w12 == null) return new KeySignature();
            double total = 0; foreach (var x in w12) total += x;
            if (total <= 0) return new KeySignature();

            double best = double.NegativeInfinity; int bestT = 0; bool bestMinor = false;
            for (int k = 0; k < 12; k++)
            {
                double cm = CorrKS(w12, k, false), cn = CorrKS(w12, k, true);
                if (k == firstLowPc) { cm += 0.06; cn += 0.06; }                  // tonic prior
                else if ((k + 3) % 12 == firstLowPc) cm += 0.03;                  // its relative minor rests on the 6th
                if (cm > best) { best = cm; bestT = k; bestMinor = false; }
                if (cn > best) { best = cn; bestT = k; bestMinor = true; }
            }
            int majPc = bestMinor ? (bestT + 3) % 12 : bestT;                     // signature is the relative major's
            int fMaj = (7 * majPc) % 12; if (fMaj > 6) fMaj -= 12;
            return FromFifths(fMaj, bestMinor);
        }

        /// <summary>Build a KeySignature from an EXPLICIT circle-of-fifths position (e.g. MuseScore's
        /// &lt;KeySig&gt;&lt;accidental&gt;) + a major/minor flag.</summary>
        public static KeySignature FromFifths(int fifths, bool minor)
        {
            int f = Math.Max(-7, Math.Min(7, fifths));
            int i = f + 7;
            return minor
                ? new KeySignature { TonicLetter = MinLetter[i], Accidental = MinAcc[i], Mode = 1 }
                : new KeySignature { TonicLetter = MajLetter[i], Accidental = MajAcc[i], Mode = 0 };
        }

        /// <summary>Given an EXPLICIT key signature (fifths) but no mode, decide major/minor from the first
        /// strong beat (6th on the downbeat) or the raised-5th sensible in the piece.</summary>
        public static KeySignature DetectMode(int fifths, double[] w12, int firstLowPc, System.Collections.Generic.HashSet<int> firstPcs)
        {
            int majPc = (((fifths * 7) % 12) + 12) % 12;       // major tonic of the signature
            int minPc = (majPc + 9) % 12;                      // its relative minor tonic
            double total = 0; if (w12 != null) foreach (var x in w12) total += x;
            if (total <= 0) return FromFifths(fifths, false);
            double cmaj = CorrKS(w12, majPc, false), cmin = CorrKS(w12, minPc, true);
            if (firstLowPc == majPc) cmaj += 0.06; else if (firstLowPc == minPc) cmin += 0.06;
            return FromFifths(fifths, cmin > cmaj);
        }
    }

    /// <summary>
    /// Picks a clef + written-pitch transposition from a track's GM program. Clefs follow the usual
    /// conventions (violin → sol/treble; piano & harp → grande portée sol+fa; alto → ut/alto C clef;
    /// cello/bassoon/trombone/tuba/bass → fa/bass). For TRANSPOSING instruments the written part is the
    /// sounding pitch shifted by a fixed interval, so the score reads as the player would see it:
    ///   Clarinette / Trompette / Sax soprano si♭ → +2 ; Cor anglais / Cor en fa → +7 ;
    ///   Sax alto mi♭ → +9 ; Sax ténor si♭ → +14 ; Sax baryton mi♭ → +21.
    /// Octave-transposing instruments (guitare, contrebasse, piccolo…) are left at concert pitch
    /// (treated as non-transposing) — only the clef is chosen for them.
    /// </summary>
    public static class ScoreClef
    {
        // autoClef = true means "no strong clef preference; pick by the note range" (piano, harp, unknown).
        public static void ForTrack(int gmProgram, bool isDrum, out ScoreClefKind clef, out int transpose, out bool autoClef)
        {
            transpose = 0; autoClef = false;
            if (isDrum) { clef = ScoreClefKind.Bass; return; } // percussion: neutral low staff

            switch (gmProgram)
            {
                // Keyboards + harp span a wide range → a single staff, clef chosen by the note range.
                case 0: case 1: case 2: case 3: case 4: case 5: case 6: case 7:   // pianos / e-pianos
                case 46:                                                          // orchestral harp
                    clef = ScoreClefKind.Treble; autoClef = true; return;

                // Strings.
                case 40: clef = ScoreClefKind.Treble; return;   // Violon → sol
                case 41: clef = ScoreClefKind.Alto;   return;   // Alto → ut (C clef, 3e ligne)
                case 42: clef = ScoreClefKind.Bass;   return;   // Violoncelle → fa
                case 43: clef = ScoreClefKind.Bass;   return;   // Contrebasse → fa (sonne 8vb)

                // Woodwinds at concert pitch.
                case 68: clef = ScoreClefKind.Treble; return;   // Hautbois
                case 70: clef = ScoreClefKind.Bass;   return;   // Basson → fa
                case 72: case 73: case 74: case 75:             // Piccolo / Flûte / Flûte à bec / Flûte de Pan
                    clef = ScoreClefKind.Treble; return;

                // Transposing woodwinds (written = sounding + N).
                case 64: clef = ScoreClefKind.Treble; transpose = 2;  return; // Sax soprano si♭
                case 65: clef = ScoreClefKind.Treble; transpose = 9;  return; // Sax alto mi♭
                case 66: clef = ScoreClefKind.Treble; transpose = 14; return; // Sax ténor si♭
                case 67: clef = ScoreClefKind.Treble; transpose = 21; return; // Sax baryton mi♭
                case 69: clef = ScoreClefKind.Treble; transpose = 7;  return; // Cor anglais (fa)
                case 71: clef = ScoreClefKind.Treble; transpose = 2;  return; // Clarinette si♭

                // Brass.
                case 56: clef = ScoreClefKind.Treble; transpose = 2; return;  // Trompette si♭
                case 59: clef = ScoreClefKind.Treble; transpose = 2; return;  // Trompette bouchée si♭
                case 60: clef = ScoreClefKind.Treble; transpose = 7; return;  // Cor en fa
                case 57: clef = ScoreClefKind.Bass;   return;                 // Trombone → fa
                case 58: clef = ScoreClefKind.Bass;   return;                 // Tuba → fa
                case 61: case 62: case 63: clef = ScoreClefKind.Treble; return; // Brass / synth brass (concert)

                // Basses → fa.
                case 32: case 33: case 34: case 35: case 36: case 37: case 38: case 39:
                    clef = ScoreClefKind.Bass; return;
            }

            // Default: unknown family → pick the clef by the note range.
            clef = ScoreClefKind.Treble; autoClef = true;
        }
    }

    /// <summary>Flattens a timeline track into absolute-beat pitched notes — mirrors TimelinePlayer's
    /// flattening (repeats expanded, riffs/patterns/drums resolved) so the score matches what plays.</summary>
    public static class ScoreBuilder
    {
        const int NoteCount = 96; // app note index range (note -> MIDI = note + 12)

        /// <summary>Global toggle for ROLLED-CHORD (arpégiato) detection, driven by the "Arpegiato" checkbox next to the
        /// Partition toggle. Default OFF: clusters of staggered/overlapping notes are notated as SEPARATE notes (no
        /// arpeggio wave). When ON, the heuristic below re-collapses genuine rolls into one chord + an arpeggio mark.</summary>
        public static bool DetectRolls = false;

        public static TrackScore Build(TimelineProject project, TimelineTrack track, Func<Guid, Riff> resolveRiff)
            => Build(project, track, resolveRiff, true);

        /// <param name="resolveLoops">false when the caller already called <see cref="TimelineProject.ResolveLoops"/>
        /// once (e.g. building several tracks in PARALLEL — ResolveLoops mutates the project, so it must run a single
        /// time on one thread first; the per-track builds below then only read).</param>
        /// <param name="melodic">true = build the MELODIC-CELL layer (each chord's attached melody) as its OWN staff,
        /// instead of the chords — so a chord track that carries melodic cells shows a 2nd staff above.</param>
        public static TrackScore Build(TimelineProject project, TimelineTrack track, Func<Guid, Riff> resolveRiff, bool resolveLoops, bool melodic = false)
        {
            var s = new TrackScore();
            if (project == null || track == null) return s;
            Flow.PatternGenerator.Ternary = project.TimeSigDen == 8; // harp-roll subdivision follows the meter (score == playback)
            var key = project.Key ?? new KeySignature();

            if (resolveLoops) TimelineProject.ResolveLoops(project, resolveRiff); // size looping Repeats to fill up to the end
            double scale = project.TimeSigScale > 0 ? project.TimeSigScale : 1.0; // 1.5 for 4/4-in-triplets → 12/8
            double cursor = 0;
            var carry = new[] { -1, -1, -1 };   // cross-module continuity: last melodic-line pitch per voice (matches playback)
            foreach (var item in track.Items)
            {
                cursor += item.SilenceBefore;
                PlaceItem(s.Notes, item, cursor, resolveRiff, scale, melodic, key, project, carry);
                cursor += TimelineProject.ItemLength(item, resolveRiff);
            }
            s.TotalBeats = Math.Max(cursor, TimelineProject.TrackEnd(track, resolveRiff)) * scale;
            s.IsDrum = track.Type == TimelineTrackType.Drum;
            // If the notes carry EXPLICIT voices (score note-input), keep them and skip the auto voice detection; else
            // auto-split a low bass pedal + a held note over a figure.
            bool explicitVoices = false; foreach (var n in s.Notes) if (n.Voice > 0) { explicitVoices = true; break; }
            if (!explicitVoices) { MarkBassVoice(s.Notes); MarkSustainVoice(s.Notes); }
            ScoreClef.ForTrack(track.Instrument, s.IsDrum, out var clef, out int tr, out bool auto);
            if (track.Clef.HasValue) clef = track.Clef.Value;      // explicit clef from import wins
            else if (auto) clef = ClefByRange(s.Notes, tr);        // else pick by the note range (bass voice ignored)
            s.Clef = clef; s.Transpose = tr;
            s.Key = project.Key ?? new KeySignature();
            return s;
        }

        // Treble if the average WRITTEN pitch (bass voice excluded) is at/above middle C, else bass — so an added
        // low bass voice doesn't drag a treble-range chord part down into the bass clef.
        static ScoreClefKind ClefByRange(List<ScoreNote> notes, int transpose)
        {
            if (notes == null || notes.Count == 0) return ScoreClefKind.Treble;
            double sum = 0; int n = 0;
            foreach (var no in notes) if (no.Voice == 0) { sum += no.Midi + transpose; n++; }
            if (n == 0) return ScoreClefKind.Treble;
            return sum / n >= 60 ? ScoreClefKind.Treble : ScoreClefKind.Bass;
        }

        // Mark a low "bass voice": at each onset, tag the lowest note Voice=1 (shown as a separate stems-down voice)
        // ONLY when it is BOTH an octave-ish below the rest (≥ 7 semitones, e.g. the generated "basse fondamentale")
        // AND lasts LONGER than the notes above it — i.e. a sustained pedal under a quicker line. Superposed notes of
        // the SAME value are a plain chord and stay fused (one stem + one flag); monophonic riffs never tag (no second
        // note at the onset).
        static void MarkBassVoice(List<ScoreNote> notes)
        {
            if (notes == null || notes.Count < 2) return;
            var byOnset = new Dictionary<long, List<int>>();
            for (int i = 0; i < notes.Count; i++)
            {
                long k = (long)Math.Round(notes[i].StartBeat * 48);
                if (!byOnset.TryGetValue(k, out var l)) byOnset[k] = l = new List<int>();
                l.Add(i);
            }
            foreach (var grp in byOnset.Values)
            {
                if (grp.Count < 2) continue;
                int lowIdx = grp[0], secondLow = int.MaxValue;
                double maxOtherBeats = 0;
                foreach (int idx in grp) if (notes[idx].Midi < notes[lowIdx].Midi) lowIdx = idx;
                foreach (int idx in grp) if (idx != lowIdx) { secondLow = Math.Min(secondLow, notes[idx].Midi); maxOtherBeats = Math.Max(maxOtherBeats, notes[idx].Beats); }
                bool sustainedBass = notes[lowIdx].Beats > maxOtherBeats + 1e-6;   // longer than the notes above → a real separate voice
                if (secondLow - notes[lowIdx].Midi >= 7 && sustainedBass) { var bn = notes[lowIdx]; bn.Voice = 1; notes[lowIdx] = bn; }
            }
        }

        // Split off a SUSTAIN voice: at an onset with several notes, when ONE note lasts clearly LONGER than the rest
        // (a held note over a quicker figure — e.g. an arpeggio in a chord motif), tag it as its own voice so it keeps
        // its real duration instead of being fused into a max-duration chord. The held TOP note → Voice=2 (drawn stems
        // UP, above the figure); the held BOTTOM note → Voice=1 (the bass voice, stems down). A middle held note is left
        // fused (can't cleanly separate). Notes already assigned to a voice (the low bass) are skipped.
        static void MarkSustainVoice(List<ScoreNote> notes)
        {
            if (notes == null || notes.Count < 2) return;
            var byOnset = new Dictionary<long, List<int>>();
            for (int i = 0; i < notes.Count; i++)
            {
                long k = (long)Math.Round(notes[i].StartBeat * 48);
                if (!byOnset.TryGetValue(k, out var l)) byOnset[k] = l = new List<int>();
                l.Add(i);
            }
            foreach (var grp in byOnset.Values)
            {
                int longIdx = -1, hi = -1, lo = -1; double maxB = -1, secondB = -1;
                foreach (int idx in grp)
                {
                    if (notes[idx].Voice != 0) continue;                 // skip the already-split bass
                    if (hi < 0 || notes[idx].Midi > notes[hi].Midi) hi = idx;
                    if (lo < 0 || notes[idx].Midi < notes[lo].Midi) lo = idx;
                    double b = notes[idx].Beats;
                    if (b > maxB) { secondB = maxB; maxB = b; longIdx = idx; }
                    else if (b > secondB) secondB = b;
                }
                if (longIdx < 0 || secondB < 0) continue;                 // need ≥2 free notes at the onset
                if (maxB <= secondB * 1.5 + 1e-6) continue;              // not clearly longer → a plain chord, stays fused
                int voice = longIdx == hi ? 2 : (longIdx == lo ? 1 : 0);
                if (voice == 0) continue;                                // held note in the MIDDLE → leave fused
                var n = notes[longIdx]; n.Voice = voice; notes[longIdx] = n;
            }
        }

        static void PlaceItem(List<ScoreNote> outNotes, TimelineItem item, double startBeat, Func<Guid, Riff> resolve, double scale, bool melodic, KeySignature key, TimelineProject project, int[] carry)
        {
            if (item.Module != null) PlaceLeaf(outNotes, item.Module, startBeat, resolve, scale, melodic, key, project, carry);
        }

        const double DisplayQuantum = 0.125; // smallest displayed note value (a 1/32 note) — used as the floor
        // Snap starts/durations to the NEARER of the 1/8-beat (binary) or 1/6-beat (ternary) grid, so a note that
        // takes a third of a beat survives as a triplet in a SIMPLE meter instead of being rounded to 3/8.
        static double SnapQ(double beats)
        {
            double b8 = Math.Round(beats * 8.0) / 8.0; // nearest 1/8 (binary: eighth, sixteenth…)
            double b6 = Math.Round(beats * 6.0) / 6.0; // nearest 1/6 (ternary: triplet-eighth = 2/6, etc.)
            return Math.Abs(b8 - beats) <= Math.Abs(b6 - beats) ? b8 : b6;
        }

        static void PlaceLeaf(List<ScoreNote> outNotes, FlowModule m, double startBeat, Func<Guid, Riff> resolve, double scale, bool melodic, KeySignature key, TimelineProject project, int[] carry)
        {
            var riff = RiffForModule(m, resolve, melodic, key, project, startBeat, carry);
            if (riff?.Notes == null) return;
            int spq = riff.SlicesPerQuarter > 0 ? riff.SlicesPerQuarter : 4; // slices per quarter = per beat

            // The generators (and live recordings) leave a tiny "détaché" gap so a held note re-attacks cleanly in
            // AUDIO. For NOTATION that gap shortens a whole-bar chord to ~95/96 of a bar → an ugly double-dotted
            // figure. So absorb any gap up to that détaché size by extending a note to the next onset (or bar end):
            // the smallest INTENDED rest is a 1/32 = spq/8 slices, strictly larger than the détaché spq/12, so real
            // rests/staccato are preserved. Audio is unaffected (TimelinePlayer keeps the raw lengths).
            int gapSlices = Math.Max(1, spq / 12);
            int riffLen = riff.LengthSlices;
            var onsets = new List<int>(riff.Notes.Count);
            foreach (var n in riff.Notes) onsets.Add(n.Start);
            onsets.Sort();

            // ROLLED-CHORD (arpégiato) detection: a cluster of >=2 notes whose STARTS fall within a small roll window AND
            // that are HELD to ~the same end (staggered attacks, common release) → notate as ONE chord at the cluster's
            // start, held for its (common) duration, with an ARPEGGIO mark. This recognises a rolled chord whatever its
            // length (1 beat, 1/2 beat…). A BLOCK chord (identical starts) is NOT a roll; a MELODIC arpeggio (spread-out
            // starts) doesn't cluster.
            int rollWin = Math.Max(2, spq / 3);
            var byStart = new List<RiffNote>();
            foreach (var n in riff.Notes) if (n.Note >= 0 && n.Note < NoteCount) byStart.Add(n);
            byStart.Sort((a, b) => a.Start.CompareTo(b.Start));
            var rollOf = new Dictionary<int, (int cs, int ce)>();   // index in byStart → (common start slice, common end slice)
            int ix = 0;
            while (DetectRolls && ix < byStart.Count)
            {
                int j = ix, minS = byStart[ix].Start, maxS = minS, minE = byStart[ix].End, maxE = minE;
                while (j + 1 < byStart.Count && byStart[j + 1].Start - minS <= rollWin)
                { j++; if (byStart[j].Start > maxS) maxS = byStart[j].Start; if (byStart[j].End < minE) minE = byStart[j].End; if (byStart[j].End > maxE) maxE = byStart[j].End; }
                // A genuine ROLLED chord OVERLAPS — the first-released note still sounds when the last note attacks
                // (minE > maxS). Without this, consecutive melodic SIXTEENTHS (starts only spq/4 apart, sequential
                // ends) were mistaken for a roll and stacked on one stem → "doubles-croches qui se superposent".
                if (j - ix + 1 >= 2 && maxS > minS && maxE - minE <= rollWin && minE > maxS)
                    for (int k = ix; k <= j; k++) rollOf[k] = (minS, maxE);
                ix = j + 1;
            }

            for (int k = 0; k < byStart.Count; k++)
            {
                var n = byStart[k];
                int startSlice, end; bool isArp;
                if (rollOf.TryGetValue(k, out var r)) { startSlice = r.cs; end = r.ce; isArp = true; } // rolled chord: common onset + held end
                else
                {
                    startSlice = n.Start; end = n.Start + n.Length;
                    int nextOnset = riffLen;                       // nothing after → the bar end
                    foreach (int o in onsets) if (o >= end) { nextOnset = o; break; } // first onset at/after this note
                    if (nextOnset > end && nextOnset - end <= gapSlices) end = nextOnset; // absorb the détaché micro-gap
                    isArp = false;
                }
                int extLen = Math.Max(1, end - startSlice);

                // Scale BEFORE snapping so triplet-eighths (0.333 beat × 1.5 = 0.5) land on a clean 1/8 grid.
                double start = SnapQ((startBeat + (double)startSlice / spq) * scale);     // snap onset to 1/8 beat
                double beats = Math.Max(DisplayQuantum, SnapQ((double)extLen / spq * scale)); // snap duration too
                outNotes.Add(new ScoreNote { StartBeat = start, Beats = beats, Midi = n.Note + 12, Arpeggio = isArp, Voice = n.Voice });
            }
        }

        static Riff RiffForModule(FlowModule m, Func<Guid, Riff> resolve, bool melodic, KeySignature key, TimelineProject project, double startBeat, int[] carry = null)
        {
            // MELODIC layer: only a chord's attached melodic cell contributes (everything else is silent on that staff).
            if (melodic) return m is PatternGeneratorModule mp && mp.HasMelodic ? PatternGenerator.GenerateMelodic(mp, key) : null;
            switch (m)
            {
                case PlayRiffModule pr: return resolve?.Invoke(pr.RiffId);
                case PatternGeneratorModule pg: return PatternGenerator.Generate(pg);
                case DrumPatternModule d: return DrumPattern.Generate(d);
                case CadenceModule cm: return PatternGenerator.GenerateCadence(cm);
                case MelodicLineModule ml: return MelodicLineEngine.GenerateLine(ml, project, resolve, key, startBeat, carry);
                default: return null;
            }
        }

        /// <summary>True if any chord on the track carries a melodic cell → it deserves an extra melodic staff.</summary>
        public static bool TrackHasMelodic(TimelineTrack track)
        {
            if (track?.Items == null) return false;
            foreach (var item in track.Items)
            {
                if (item == null) continue;
                if (item.Module is PatternGeneratorModule p && p.HasMelodic) return true;
            }
            return false;
        }
    }
}
