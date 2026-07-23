using System;
using System.Collections.Generic;
using MusicTracker.Engine.Score;

namespace MusicTracker.Engine.Flow
{
    /// <summary>
    /// Turns a chord (root + octave + quality + inversion) and a rhythmic style into a looping
    /// <see cref="Riff"/> (note slices only — the instrument/tempo come from the graph context).
    /// The name tables here are the single source of truth for the UI combos and the int indices
    /// stored on <see cref="PatternGeneratorModule"/>, so order matters: keep them in sync.
    /// Grid: 24 slices per quarter (matches the importers). Slice row = MIDI note - 12.
    /// </summary>
    public static class PatternGenerator
    {
        public const int SlicesPerQuarter = 24;

        /// <summary>Project-global meter feel (compound x/8). The beat is ALWAYS 24 slices; when ternary, a beat divides
        /// by 3/6 (croche = 8, double-croche = 4 slices) instead of by 2/4 (12 / 6). Set it from project.TimeSigDen == 8
        /// before generating — currently only the harp roll (style 27) needs it, so the subdivision fits the beat.</summary>
        public static bool Ternary;

        // 0..11 pitch classes (French note names, C = Do).
        public static readonly string[] RootNames =
            { "Do", "Do♯", "Ré", "Ré♯", "Mi", "Fa", "Fa♯", "Sol", "Sol♯", "La", "La♯", "Si" };

        // NOTE: append-only — the stored quality index in saved graphs maps to this order, so never
        // reorder or remove an entry (only add at the end). Triads -> sevenths -> tensions.
        public static readonly string[] QualityNames =
        {
            // triads + sevenths (indices 0..10, unchanged)
            "Majeur", "Mineur", "Diminué", "Augmenté", "Sus2", "Sus4", "Maj7", "Min7", "7 (dom)", "m7♭5", "dim7",
            // tensions (indices 11+)
            "6", "m6", "add9", "m(add9)", "9 (dom)", "Maj9", "m9", "7♭9", "7♯9", "11 (dom)", "13 (dom)", "Maj7♯11",
            "7sus4", "7sus2", "9sus4", "9sus2",
            "6sus4", "6sus2", "Maj7sus4", "Maj7sus2", "Maj9sus4", "Maj9sus2", "add9sus4",
            "7♯5",
        };

        // Semitone offsets from the root, parallel to QualityNames.
        static readonly int[][] QualityIntervals =
        {
            new[] { 0, 4, 7 },      // Majeur
            new[] { 0, 3, 7 },      // Mineur
            new[] { 0, 3, 6 },      // Diminué
            new[] { 0, 4, 8 },      // Augmenté
            new[] { 0, 2, 7 },      // Sus2
            new[] { 0, 5, 7 },      // Sus4
            new[] { 0, 4, 7, 11 },  // Maj7
            new[] { 0, 3, 7, 10 },  // Min7
            new[] { 0, 4, 7, 10 },  // 7 (dominante)
            new[] { 0, 3, 6, 10 },  // m7♭5 (demi-diminué)
            new[] { 0, 3, 6, 9 },   // dim7
            // --- tensions ---
            new[] { 0, 4, 7, 9 },         // 6 (Major 6th)
            new[] { 0, 3, 7, 9 },         // m6 (Minor 6th)
            new[] { 0, 4, 7, 14 },        // add9 (no 7th)
            new[] { 0, 3, 7, 14 },        // m(add9)
            new[] { 0, 4, 7, 10, 14 },    // 9 (dominant 9th)
            new[] { 0, 4, 7, 11, 14 },    // Maj9
            new[] { 0, 3, 7, 10, 14 },    // m9
            new[] { 0, 4, 7, 10, 13 },    // 7♭9
            new[] { 0, 4, 7, 10, 15 },    // 7♯9
            new[] { 0, 4, 7, 10, 14, 17 },// 11 (dominant 11th)
            new[] { 0, 4, 7, 10, 14, 21 },// 13 (dominant 13th)
            new[] { 0, 4, 7, 11, 18 },    // Maj7♯11
            new[] { 0, 5, 7, 10 },        // 7sus4 (dominant, 4th suspended for the 3rd)
            new[] { 0, 2, 7, 10 },        // 7sus2 (dominant, 2nd suspended for the 3rd)
            new[] { 0, 5, 7, 10, 14 },    // 9sus4 (dominant 9, 4th suspended)
            new[] { 0, 2, 7, 10, 14 },    // 9sus2 (dominant 9, 2nd suspended)
            new[] { 0, 5, 7, 9 },         // 6sus4  (sixte, 4th suspended)
            new[] { 0, 2, 7, 9 },         // 6sus2  (sixte, 2nd suspended)
            new[] { 0, 5, 7, 11 },        // Maj7sus4
            new[] { 0, 2, 7, 11 },        // Maj7sus2
            new[] { 0, 5, 7, 11, 14 },    // Maj9sus4
            new[] { 0, 2, 7, 11, 14 },    // Maj9sus2
            new[] { 0, 5, 7, 14 },        // add9sus4 (9th + 4th, no 3rd, no 7th)
            new[] { 0, 4, 8, 10 },        // 7♯5 (augmented 7th: major 3rd + aug 5th + ♭7)
        };

        public static readonly string[] StyleNames =
        {
            "Accords plaqués (tenu)",       // 0
            "Accords plaqués (noires)",     // 1
            "Accords plaqués (croches)",    // 2
            "Arpège montant",               // 3
            "Arpège montant-descendant",    // 4
            "Alberti (Do-Sol-Mi-Sol)",      // 5
            "Jazz comping (Charleston)",    // 6
            "Rock (croches)",               // 7
            "Pop (basse + accord)",         // 8
            "Blues shuffle (triolets)",     // 9
            "Arpège descendant",            // 10
            "Arpège (croches)",             // 11
            "Valse (basse-accord-accord)",  // 12
            "Reggae skank (contretemps)",   // 13
            "Marche (basse-accord)",        // 14
            "Tango (staccato)",             // 15
            "Bossa nova / Latin (syncopé)", // 16
            "Funk (stabs 16e)",             // 17
            "Habanera / Tango (basse)",     // 18
            "Ballade (arpège tenu)",        // 19
            "Country (basse alternée)",     // 20
            "Slow rock (triolets 12-8)",    // 21
            "Arpège : 2 croches + noire",   // 22
            "Arpège : 3 croches + noire pointée", // 23
            "Arpège : 4 croches + blanche", // 24
            "Arpège : triolet + noire",     // 25
            "Arpège : 4 croches + noire",   // 26 (utile en 3/4)
            "Harpe (arpège roulé)",         // 27 -> continuous 16th roll up&down over 2 octaves (pair with "Basse tenue")
            "Personnalisé…",                // last -> uses the module's hand-drawn voice grid
        };

        /// <summary>Index of the "Personnalisé" style (uses the module's custom voice grid).</summary>
        public static readonly int CustomStyle = StyleNames.Length - 1;

        /// <summary>Chord tones as MIDI notes (root + intervals + inversion), sorted ascending.</summary>
        public static int[] ChordNotes(int root, int octave, int quality, int inversion, bool open = false)
        {
            var iv = QualityIntervals[Clamp(quality, 0, QualityIntervals.Length - 1)];
            int rootMidi = ((root % 12) + 12) % 12 + 12 * (octave + 1); // C4 = 60
            var notes = new List<int>();
            foreach (var i in iv) notes.Add(rootMidi + i);
            for (int k = 0; k < inversion; k++)
            {
                int low = notes[0]; notes.RemoveAt(0); notes.Add(low + 12); // raise the bottom note an octave
            }
            notes.Sort();
            // OPEN voicing: spread the chord by lifting the 2nd-from-bottom voice an octave (close C-E-G → open C-G-E').
            // The bass (inversion) is preserved; the spacing opens past an octave. No-op for < 3 notes.
            if (open && notes.Count >= 3) { notes[1] += 12; notes.Sort(); }
            return notes.ToArray();
        }

        public static Riff Generate(PatternGeneratorModule m)
        {
            int repeats = Math.Max(1, m.Repeats);
            var chord = ChordNotes(m.Root, m.Octave, m.Quality, m.Inversion, m.OpenVoicing);
            int rootMidi = RootMidi(m.Root, m.Octave);
            string name = $"{RootName(m.Root)} {QualityNames[Clamp(m.Quality, 0, QualityNames.Length - 1)]}";

            // "Personnalisé": the user's hand-drawn voice grid (with its own bass row at voice 0) — stays slice-based.
            bool custom = m.Style == CustomStyle && m.CustomSlices != null && m.CustomSlices.Length > 0;
            if (custom)
            {
                int barSpq = m.CustomSlicesPerQuarter > 0 ? m.CustomSlicesPerQuarter : SlicesPerQuarter;
                int voiceCount = CustomVoiceCount;     // FIXED rows: bass + degrees 1,3,5,7,9 over two octaves
                int barSlices = m.CustomSlices.Length;

                // NOTE-LIST form (source of truth when present): each note = a voice row → distinct onsets, so adjacent
                // same-voice notes re-articulate (no slice-merge). Falls back to the OR-merged slice grid on old data.
                if (m.CustomNotes != null && m.CustomNotes.Count > 0)
                {
                    var outNotes = new List<RiffNote>();
                    for (int bar = 0; bar < repeats; bar++)
                    {
                        int off = bar * barSlices;
                        foreach (var mn in m.CustomNotes)
                        {
                            if (mn.Note < 0 || mn.Note >= voiceCount) continue;
                            int mv = CustomVoiceNote(chord, rootMidi, mn.Note);
                            if (mv == SkipVoice) continue;                     // degree absent from this chord (triad 7/9)
                            int row = mv - 12; // note row 0 == MIDI 12
                            if (row >= 0 && row < 96) outNotes.Add(new RiffNote(row, off + mn.Start, mn.Length));
                        }
                    }
                    return new Riff { Name = name, Notes = outNotes, SlicesPerQuarter = barSpq };
                }

                var barGrid = m.CustomSlices;
                var slices = new SequencerSlice[barSlices * repeats];
                for (int bar = 0; bar < repeats; bar++)
                {
                    int off = bar * barSlices;
                    for (int s = 0; s < barSlices; s++)
                        for (int v = 0; v < voiceCount; v++)
                            if (barGrid[s].On(v))
                            {
                                int mv = CustomVoiceNote(chord, rootMidi, v);
                                if (mv == SkipVoice) continue;                 // degree absent from this chord (triad 7/9)
                                int row = mv - 12; // slice row 0 == MIDI 12
                                if (row >= 0 && row < 96) slices[off + s].On(row, true);
                            }
                }
                return new Riff { Name = name, Slices = slices, SlicesPerQuarter = barSpq };
            }

            // Built-in styles think in NOTES + durations (the canonical RiffNote model, like the riff editor): each
            // note is emitted at its intended CLEAN length, and two adjacent same-pitch notes stay DISTINCT — no
            // détaché slice-gap hack. The player re-articulates on each onset (release-then-attack), and the score
            // reads clean durations (a held bar = a real whole note).
            int q = SlicesPerQuarter;
            int beats = Math.Max(1, m.BeatsPerBar);
            int barSlicesB = beats * q;
            var events = BuildVoiceEvents(m.Style, beats, Math.Max(1, chord.Length), m.HeldMode, m.ClimbMode, m.HeldVoiceOverride, m.HalveDurations, m.PatternCellOffset, Ternary);

            // Bass = the ROOT placed at the CLOSEST octave below the lowest played note (not a fixed octave below the
            // root) — so an inverted chord doesn't get a huge bass leap. If that root coincides with the lowest note
            // (root position), drop it one octave so the bass still sits under the chord.
            int chordLow = chord.Length > 0 ? chord[0] : rootMidi;
            int rpc = ((m.Root % 12) + 12) % 12;
            int bassMidi = chordLow - (((chordLow - rpc) % 12) + 12) % 12; // highest root ≤ the lowest chord note
            if (bassMidi >= chordLow) bassMidi -= 12;
            int bassRow = bassMidi - 12; // RiffNote.Note (= MIDI − 12)
            var notes = new List<RiffNote>();

            bool addBass = m.Bass && bassRow >= 0 && bassRow < 96;
            for (int bar = 0; bar < repeats; bar++)
            {
                int off = bar * barSlicesB;
                foreach (var ev in events)
                {
                    // Voice → chord tone; a voice index BEYOND the chord's tones wraps up an octave (so an arpeggio can
                    // roll across 2+ octaves — harp style). Existing styles only use voices 0..chord.Length-1.
                    int cl = Math.Max(1, chord.Length);
                    int row = chord[ev.Voice % cl] + 12 * (ev.Voice / cl) - 12;
                    int len = Math.Min(ev.Len, barSlicesB - ev.Start); // clamp to the bar (e.g. a Charleston cell in a 1-beat chord)
                    if (row >= 0 && row < 96 && len > 0) notes.Add(new RiffNote(row, off + ev.Start, len));
                }
                // Bass (fondamentale): the ROOT one octave below, shown as a SEPARATE voice in the score (the chord
                // keeps its rhythm; the score detects the octave-ish-low note and draws it stems-down). Default =
                // ONE held note per MEASURE (a ronde); BassPerBeat = one note per beat (a steady pulse).
                if (addBass)
                {
                    if (m.BassPerBeat) for (int b = 0; b < beats; b++) notes.Add(new RiffNote(bassRow, off + b * q, q));
                    else notes.Add(new RiffNote(bassRow, off, barSlicesB)); // one held note for the whole bar
                }
            }
            return new Riff { Name = name, Notes = notes, LengthSlices = barSlicesB * repeats, SlicesPerQuarter = q };
        }

        /// <summary>Render a <see cref="CadenceModule"/> to ONE riff: generate each chord with the chord generator
        /// (same octave / articulation style / bass / beats-per-chord) and concatenate the bars' NOTES end to end.
        /// This is the single helper the score, PDF, MIDI/MuseScore export and audio all go through.</summary>
        public static Riff GenerateCadence(CadenceModule m)
        {
            int spq = SlicesPerQuarter;
            int barSlices = Math.Max(1, m.BeatsPerBar) * spq;
            var chords = m.Chords ?? new List<CadenceChord>();
            var notes = new List<RiffNote>();
            for (int i = 0; i < chords.Count; i++)
            {
                var c = chords[i];
                var pg = new PatternGeneratorModule
                {
                    Root = c.Root, Quality = c.Quality, Inversion = c.Inversion,
                    Octave = m.Octave + c.OctaveShift, // voice-leading register placement
                    Style = m.Style, Bass = m.Bass, BassPerBeat = m.BassPerBeat, HeldMode = m.HeldMode, ClimbMode = m.ClimbMode,
                    HalveDurations = m.HalveDurations, HeldVoiceOverride = c.HeldVoice, PatternCellOffset = i, // rotate "mixte" across chords
                    BeatsPerBar = m.BeatsPerBar, Repeats = 1,
                    CustomSlices = m.CustomSlices, CustomSlicesPerQuarter = m.CustomSlicesPerQuarter, // "Personnalisé" motif applied per chord
                    CustomNotes = m.CustomNotes,
                    OpenVoicing = m.OpenVoicing,
                };
                var r = Generate(pg);
                int off = i * barSlices;
                // Concatenate NOTES (not slices) so adjacent same-pitch chords across the boundary stay distinct.
                foreach (var n in r.Notes) notes.Add(new RiffNote(n.Note, off + n.Start, n.Length));
            }
            return new Riff { Name = "Cadence", Notes = notes, LengthSlices = barSlices * Math.Max(1, chords.Count), SlicesPerQuarter = spq };
        }

        static string RootName(int r) => RootNames[((r % 12) + 12) % 12];

        // Un-inverted root MIDI (C4 = 60) — the bass uses this so it stays the tonic regardless of inversion.
        static int RootMidi(int root, int octave) => ((root % 12) + 12) % 12 + 12 * (octave + 1);

        // Note for a CUSTOM-grid voice: voice 0 = bass (tonic ONE octave below the root, NOT inverted);
        // The custom voice grid has a FIXED set of rows by DEGREE (not by the chord's tone count), so a hand-drawn
        // motif is portable across chord qualities: bass + degrees {1,3,5,7,9} over TWO octaves. A chord that lacks a
        // degree (a triad has no 7th/9th) falls back to the NEAREST real chord tone (see CustomVoiceNote) so the row
        // still sounds and the motif's rhythm is preserved.
        public const int CustomTonesPerOctave = 5;                          // degrees 1,3,5,7,9 (chord-tone indices 0..4)
        public const int CustomVoiceCount = 1 + CustomTonesPerOctave * 2;   // bass + 2 octaves = 11 rows
        public const int SkipVoice = int.MinValue;                          // this row's degree is absent from the chord

        // Generic interval (semitones above root) for each degree row 1,3,5,7,9 — a GUIDE for where an ABSENT degree
        // would sit, used only to snap it to the nearest real chord tone.
        static readonly int[] DegreeGuide = { 0, 4, 7, 10, 14 };

        // Grid rows (voice 1..10) ordered by ACTUAL PITCH, not by "octave then degree": the 9th (root+14) sits ABOVE the
        // octave root 1' (root+12), so the layout is 1,3,5,7,1',9,3',5',7',9'. Each entry = (octave shift, degree index).
        static readonly (int oct, int deg)[] CustomVoiceMap =
            { (0, 0), (0, 1), (0, 2), (0, 3), (1, 0), (0, 4), (1, 1), (1, 2), (1, 3), (1, 4) };

        // Voice index (1..10) for a (octave, degree) pair — inverse of CustomVoiceMap (for the built-in seed).
        static int CustomVoiceFor(int oct, int deg)
        {
            for (int i = 0; i < CustomVoiceMap.Length; i++)
                if (CustomVoiceMap[i].oct == oct && CustomVoiceMap[i].deg == deg) return i + 1;
            return -1;
        }

        static int CustomVoiceNote(int[] chord, int rootMidi, int v)
        {
            if (v == 0) return rootMidi - 12;
            if (v - 1 >= CustomVoiceMap.Length) return SkipVoice;
            int oct = CustomVoiceMap[v - 1].oct;
            int deg = CustomVoiceMap[v - 1].deg;        // 0..4 = chord-tone index (degrees 1,3,5,7,9)
            if (deg < chord.Length) return chord[deg] + 12 * oct;
            if (chord.Length == 0) return SkipVoice;
            // The chord LACKS this degree (e.g. a triad has no 7th/9th): instead of a silent row (which breaks the motif's
            // rhythm), play the NEAREST real chord tone — in PITCH, so it honours the applied inversion + open voicing.
            // The target is where the degree WOULD sit (root + generic interval, at this row's octave).
            int target = rootMidi + DegreeGuide[Math.Min(deg, DegreeGuide.Length - 1)] + 12 * oct;
            int best = SkipVoice, bestDist = int.MaxValue;
            for (int i = 0; i < chord.Length; i++)
                for (int k = -1; k <= 2; k++)           // try each chord tone at nearby octaves — nearest in PITCH
                {
                    int p = chord[i] + 12 * k;
                    int dist = Math.Abs(p - target);
                    // On a tie, prefer the HIGHER tone (the extension resolves upward): a triad's 7 → octave root,
                    // its 9 → octave third (not the octave root, which is equidistant but below).
                    if (dist < bestDist || (dist == bestDist && p > best)) { bestDist = dist; best = p; }
                }
            return best;
        }

        // ================= MELODIC CELL (optional 2nd voice attached to a chord) =================
        // The grid has 14 rows = the 7 DIATONIC DEGREES of the key over 2 octaves (all scale tones, incl. non-chord),
        // starting from the chord's ANCHOR (its root, or its inversion bass). Polyphonic. Pitch = walk the key's scale.
        public const int MelodicRowCount = 14;

        // Concert MIDI for a melodic grid row, given the key scale/tonic, the anchor pitch-class and the melody octave.
        static int MelodicPitch(int[] scale, int tonicPc, int anchorPc, int melodicOctave, int row)
        {
            if (scale == null || scale.Length < 7) return 60;
            int degree = ((row % 7) + 7) % 7, oct = row / 7;
            anchorPc = ((anchorPc % 12) + 12) % 12; tonicPc = ((tonicPc % 12) + 12) % 12;
            int idx = 0;                                                  // scale index of the anchor (0 if chromatic → tonic)
            for (int i = 0; i < 7; i++) if ((((tonicPc + scale[i]) % 12) + 12) % 12 == anchorPc) { idx = i; break; }
            int anchorMidi = 12 * (melodicOctave + 1) + anchorPc;
            int pos = idx + degree + 7 * oct;
            int delta = (scale[pos % 7] + 12 * (pos / 7)) - scale[idx];   // semitones above the anchor along the scale
            return Math.Max(0, Math.Min(127, anchorMidi + delta));
        }

        /// <summary>Render a chord's optional MELODIC CELL to a Riff (its own voice/staff), or null if it has none. Needs
        /// the KEY (the scale + tonic drive the diatonic degrees). Grid rows are diatonic degrees; the anchor is the chord
        /// root (MelodicAnchor 0) or its inversion bass (MelodicAnchor 1).</summary>
        public static Riff GenerateMelodic(PatternGeneratorModule m, KeySignature key)
        {
            if (m == null || m.MelodicNotes == null || m.MelodicNotes.Count == 0) return null;
            var scale = MusicalMode.Scale(MusicalMode.Effective(key ?? new KeySignature()));
            int tonicPc = MusicTheory.TonicPc(key ?? new KeySignature());
            int rootPc = ((m.Root % 12) + 12) % 12;
            int anchorPc = rootPc;
            if (m.MelodicAnchor == 1)
            {
                var iv = QualityIntervals[Clamp(m.Quality, 0, QualityIntervals.Length - 1)];
                anchorPc = (rootPc + iv[m.Inversion % iv.Length]) % 12;    // bass = root/3rd/5th/7th per inversion
            }
            int melSpq = m.MelodicSlicesPerQuarter > 0 ? m.MelodicSlicesPerQuarter : SlicesPerQuarter;
            int barSlices = Math.Max(1, m.BeatsPerBar) * melSpq;
            int repeats = Math.Max(1, m.Repeats);
            var outNotes = new List<RiffNote>();
            for (int bar = 0; bar < repeats; bar++)
            {
                int off = bar * barSlices;
                foreach (var mn in m.MelodicNotes)
                {
                    if (mn.Note < 0 || mn.Note >= MelodicRowCount) continue;
                    int noteRow = MelodicPitch(scale, tonicPc, anchorPc, m.MelodicOctave, mn.Note) - 12; // app convention: note 0 = MIDI 12
                    if (noteRow >= 0 && noteRow < 96) outNotes.Add(new RiffNote(noteRow, off + mn.Start, mn.Length));
                }
            }
            return new Riff { Name = "Mélodie", Notes = outNotes, LengthSlices = barSlices * repeats, SlicesPerQuarter = melSpq };
        }

        /// <summary>
        /// Seed for the CUSTOM chord grid: a built-in style's pattern placed on the chord's DEGREE rows (bass row 0
        /// stays empty). Maps each built-in voice (tone index i, octave o) onto the pitch-ordered degree layout row
        /// (<see cref="CustomVoiceFor"/>). <paramref name="chordLen"/> = the chord's tone count.
        /// </summary>
        public static SequencerSlice[] VoiceBarForCustom(int style, int beats, int chordLen)
        {
            chordLen = Math.Max(1, chordLen);
            var src = BuildVoiceBar(style, Math.Max(1, beats), chordLen * 2);   // 2 octaves of the chord's tones
            var dst = new SequencerSlice[src.Length];
            for (int s = 0; s < src.Length; s++)
                for (int v = 0; v < chordLen * 2; v++)
                    if (src[s].On(v))
                    {
                        int row = CustomVoiceFor(v / chordLen, v % chordLen);  // pitch-ordered grid row
                        if (row >= 1 && row < CustomVoiceCount) dst[s].On(row, true); // bass row 0 stays empty
                    }
            return dst;
        }

        /// <summary>An empty one-bar voice grid (row = voice index) at the editing resolution.</summary>
        public static SequencerSlice[] NewCustomBar(int beats, int slicesPerBeat)
            => new SequencerSlice[Math.Max(1, beats) * Math.Max(1, slicesPerBeat)];

        /// <summary>Build a built-in style's one-bar voice grid, so it can seed/illustrate the editor.</summary>
        public static SequencerSlice[] VoiceBarForStyle(int style, int beats, int voiceCount)
            => BuildVoiceBar(style, Math.Max(1, beats), Math.Max(1, voiceCount));

        // ---- rasterization (voice grid: row = chord-voice index) -------------------

        // Set one voice ON for [start, start+len).
        static void PutV(SequencerSlice[] g, int start, int len, int voice)
        {
            if (voice < 0 || voice >= 96) return;
            int from = Math.Max(0, start);
            int to = Math.Min(g.Length, start + Math.Max(1, len));
            for (int s = from; s < to; s++) g[s].On(voice, true); // array-of-struct: mutates in place
        }

        // A note event in the one-bar voice pattern: a chord-voice index + start/length in slices (24/quarter).
        struct VoiceEvent { public int Voice, Start, Len; public VoiceEvent(int v, int s, int l) { Voice = v; Start = s; Len = Math.Max(1, l); } }

        static void AddV(List<VoiceEvent> g, int start, int len, int voice) { if (voice >= 0 && len > 0 && start >= 0) g.Add(new VoiceEvent(voice, start, len)); }
        static void AddAll(List<VoiceEvent> g, int start, int len, int voiceCount) { for (int v = 0; v < voiceCount; v++) AddV(g, start, len, v); }

        // The Alberti voice index for step i of a broken chord: root, fifth, third, top-extension (1-5-3-5 triad /
        // 1-5-3-7 seventh; alternates 7th↔9th each 4-group for a ninth+).
        static int AlbertiIdx(int i, int voiceCount)
        {
            int third = Math.Min(1, voiceCount - 1), fifth = Math.Min(2, voiceCount - 1);
            int ext = Math.Min(3, voiceCount - 1), ext2 = Math.Min(4, voiceCount - 1);
            int last = (voiceCount >= 5 && (i / 4) % 2 == 1) ? ext2 : ext;
            int[] pat = { 0, fifth, third, last };
            return pat[i % 4];
        }

        // Arpège: nNotes climbing the chord (each `noteDur` long) then a HELD note of `heldLen`. climbMode: 0 =
        // montant, 1 = descendant, 2 = Alberti (1-5-3…), 3 = mixte (rotate montant/descendant/Alberti per cell).
        // heldMode picks the held voicing: 0 = single note, 1 = full chord (plaqué), 2 = root+fifth, 3 = root+third.
        // For a single held note (mode 0), `heldVoice` (≥0) is the chord-voice to hold (voice-led by the cadence),
        // else the top. `halve` divides every value by 2 (doubles-croches). `cellOffset` shifts the cell counter so
        // "mixte" rotates ACROSS a cadence's chords (each chord generated separately). Tiled across the bar.
        static void ArpHeld(List<VoiceEvent> g, int barSlices, int voiceCount, int nNotes, int noteDur, int heldLen, int heldMode, int climbMode, int heldVoice, bool halve, int cellOffset)
        {
            if (halve) { noteDur = Math.Max(1, noteDur / 2); heldLen = Math.Max(1, heldLen / 2); }
            int cell = nNotes * Math.Max(1, noteDur) + Math.Max(1, heldLen);
            if (cell < 1) return;
            int cellIdx = cellOffset;
            for (int c0 = 0; c0 < barSlices; c0 += cell, cellIdx++)
            {
                // MIXTE (climbMode 3) is VOICE-LED, not a fixed alternation: the climb starts on the chord tone
                // chosen by the cadence's voice-leading (`heldVoice`, = nearest to the previous chord) and runs up
                // or down to avoid a big wrap — so the order is free (starts low/mid/high per context) but never
                // random. Standalone (no heldVoice) falls back to rotating montant/descendant/Alberti per cell.
                int vlStart = (climbMode == 3 && heldVoice >= 0) ? Math.Max(0, Math.Min(heldVoice, voiceCount - 1)) : -1;
                bool vlUp = vlStart <= (voiceCount - 1) / 2;
                int pat = climbMode == 3 ? ((cellIdx % 3) + 3) % 3 : (climbMode == 1 ? 1 : climbMode == 2 ? 2 : 0);
                for (int i = 0; i < nNotes; i++)
                {
                    int pos = c0 + i * noteDur;
                    if (pos >= barSlices) break;
                    int v;
                    if (vlStart >= 0) v = vlUp ? (vlStart + i) % voiceCount : (((vlStart - i) % voiceCount) + voiceCount) % voiceCount; // voice-led free order
                    else v = pat == 2 ? AlbertiIdx(i, voiceCount)
                           : pat == 1 ? (voiceCount - 1 - (i % voiceCount) + voiceCount) % voiceCount  // descendant
                           : (i % voiceCount);                                                          // montant
                    AddV(g, pos, Math.Min(noteDur, barSlices - pos), v);
                }
                int hs = c0 + nNotes * noteDur;
                if (hs >= barSlices) continue;
                int hl = Math.Min(heldLen, barSlices - hs);
                switch (heldMode)
                {
                    case 1: AddAll(g, hs, hl, voiceCount); break;                                  // full chord (plaqué)
                    case 2: AddV(g, hs, hl, 0); if (voiceCount > 2) AddV(g, hs, hl, 2); break;      // root + fifth
                    case 3: AddV(g, hs, hl, 0); if (voiceCount > 1) AddV(g, hs, hl, 1); break;      // root + third
                    default: AddV(g, hs, hl, (heldVoice >= 0 && heldVoice < voiceCount) ? heldVoice : voiceCount - 1); break; // voice-led single note (else top)
                }
            }
        }

        // A built-in style's one-bar pattern as NOTE EVENTS with clean (un-gapped) durations. Re-articulation of
        // adjacent same-pitch notes is handled by them being SEPARATE notes (the player re-attacks on each onset),
        // so no détaché slice-gap is needed and durations stay on a clean grid for notation.
        static List<VoiceEvent> BuildVoiceEvents(int style, int beats, int voiceCount, int heldMode = 0, int climbMode = 0, int heldVoice = -1, bool halve = false, int cellOffset = 0, bool ternary = false)
        {
            int q = SlicesPerQuarter, half = q / 2, six = q / 4; // beat, BINARY eighth, BINARY sixteenth (genre styles)
            // Ternary (compound x/8) subdivides the beat by 3/6 instead of 2/4 — the ARPEGGIO/croche figuration uses
            // these so a "croche" is 1/3 of a beat (8 slices) not 1/2 (12). The beat is always 24 slices.
            int sub = ternary ? 3 : 2;               // croches per beat
            int eighth = ternary ? q / 3 : q / 2;    // arpeggio croche: 8 (ternary) / 12 (binary)
            int sixt = ternary ? q / 6 : q / 4;      // arpeggio double-croche: 4 / 6
            if (voiceCount < 1) voiceCount = 1;
            var g = new List<VoiceEvent>();
            switch (style)
            {
                case 0: AddAll(g, 0, beats * q, voiceCount); break;                                       // plaqués (tenu) — whole bar
                case 1: for (int b = 0; b < beats; b++) AddAll(g, b * q, q, voiceCount); break;            // plaqués (noires)
                case 2: for (int e = 0; e < beats * sub; e++) AddAll(g, e * eighth, eighth, voiceCount); break;  // plaqués (croches)
                case 3: for (int b = 0; b < beats; b++) AddV(g, b * q, q, b % voiceCount); break;          // arpège montant
                case 4: { var seq = ArpUpDown(voiceCount); for (int b = 0; b < beats; b++) AddV(g, b * q, q, seq[b % seq.Length]); } break; // arpège montant-descendant
                case 5: // Alberti broken chord: root-fifth-third-extension (1-5-3-5 triad / 1-5-3-7 seventh; 7th↔9th for a ninth+)
                    for (int e = 0; e < beats * sub; e++) AddV(g, e * eighth, eighth, AlbertiIdx(e, voiceCount));
                    break;
                case 6: // Jazz comping (Charleston) — dotted-quarter + eighth per 2-beat cell
                    for (int u = 0; u * 2 < beats; u++) { int c = u * 2 * q; AddAll(g, c, q + half, voiceCount); if (c + q + half < beats * q) AddAll(g, c + q + half, half, voiceCount); }
                    if (beats % 2 == 1) AddAll(g, (beats - 1) * q, q, voiceCount);
                    break;
                case 7: for (int e = 0; e < beats * sub; e++) AddAll(g, e * eighth, eighth, voiceCount); break;  // rock (croches)
                case 8: for (int b = 0; b < beats; b++) { if (b % 2 == 0) AddV(g, b * q, q, 0); else AddAll(g, b * q, q, voiceCount); } break; // pop (basse + accord)
                case 9: { int t = 2 * q / 3; for (int b = 0; b < beats; b++) { AddAll(g, b * q, t, voiceCount); AddAll(g, b * q + t, q - t, voiceCount); } } break; // blues shuffle (2/3 + 1/3)
                case 10: for (int b = 0; b < beats; b++) AddV(g, b * q, q, (voiceCount - 1 - (b % voiceCount) + voiceCount) % voiceCount); break; // arpège descendant
                case 11: for (int e = 0; e < beats * sub; e++) AddV(g, e * eighth, eighth, e % voiceCount); break; // arpège (croches)
                case 12: for (int b = 0; b < beats; b++) { if (b == 0) AddV(g, 0, q, 0); else AddAll(g, b * q, q, voiceCount); } break; // valse
                case 13: for (int b = 0; b < beats; b++) AddAll(g, b * q + half, half, voiceCount); break; // reggae skank (off-beats)
                case 14: for (int b = 0; b < beats; b++) { AddV(g, b * q, half, 0); AddAll(g, b * q + half, half, voiceCount); } break; // marche (basse-accord)
                case 15: for (int b = 0; b < beats; b++) AddAll(g, b * q, half, voiceCount); break; // tango — eighth stab on each beat
                case 16: // bossa nova / Latin
                    for (int b = 0; b < beats; b += 2) AddV(g, b * q, q, 0);                       // root on strong beats
                    AddAll(g, 0, half, voiceCount);                                                // chord on beat 1
                    for (int b = 0; b < beats; b++) AddAll(g, b * q + half, half, voiceCount);      // each "and"
                    break;
                case 17: for (int b = 0; b < beats; b++) { AddAll(g, b * q, six, voiceCount); AddAll(g, b * q + 2 * six, six, voiceCount); AddAll(g, b * q + 3 * six, six, voiceCount); } break; // funk — 16th stabs
                case 18: // habanera bass figure + chord on the downbeat
                    for (int b = 0; b + 1 < Math.Max(2, beats); b += 2)
                    {
                        int c = b * q, d8 = q / 2 + q / 4; // dotted-eighth
                        AddV(g, c, d8, 0); AddV(g, c + d8, six, 0); AddV(g, c + q, half, 0); AddV(g, c + q + half, half, 0);
                        AddAll(g, c, half, voiceCount);
                    }
                    if (beats % 2 == 1) AddAll(g, (beats - 1) * q, q, voiceCount);
                    break;
                case 19: { int span = beats * q; for (int v = 0; v < voiceCount; v++) { int start = (int)((long)v * span / voiceCount); AddV(g, start, span - start, v); } } break; // ballade (arpège tenu)
                case 20: { int fifth = voiceCount >= 3 ? voiceCount / 2 : (voiceCount - 1); for (int b = 0; b < beats; b++) { if (b % 2 == 0) AddV(g, b * q, q, (b % 4 == 0) ? 0 : fifth); else AddAll(g, b * q, half, voiceCount); } } break; // country (basse alternée)
                case 21: { int t = q / 3; for (int b = 0; b < beats; b++) for (int k = 0; k < 3; k++) AddV(g, b * q + k * t, t, (b * 3 + k) % voiceCount); } break; // slow rock (triolets)
                case 22: ArpHeld(g, beats * q, voiceCount, 2, eighth, q, heldMode, climbMode, heldVoice, halve, cellOffset); break;        // 2 croches + noire
                case 23: ArpHeld(g, beats * q, voiceCount, 3, eighth, q + eighth, heldMode, climbMode, heldVoice, halve, cellOffset); break; // 3 croches + noire pointée
                case 24: ArpHeld(g, beats * q, voiceCount, 4, eighth, 2 * q, heldMode, climbMode, heldVoice, halve, cellOffset); break;    // 4 croches + blanche
                case 25: ArpHeld(g, beats * q, voiceCount, 3, q / 3, q, heldMode, climbMode, heldVoice, halve, cellOffset); break;       // triolet + noire
                case 26: ArpHeld(g, beats * q, voiceCount, 4, eighth, q, heldMode, climbMode, heldVoice, halve, cellOffset); break;        // 4 croches + noire (utile en 3/4)
                case 27: // Harpe — a continuous 16th-note arpeggio ROLLING up then down over ~2 octaves of chord tones
                {
                    int dur = halve ? sixt / 2 : sixt;              // 16ths (32nds if ÷2), the flowing harp figuration (ternary 4 / binary 6)
                    int span = Math.Max(2, voiceCount * 2);         // two octaves of chord tones (voice ≥ count wraps up an 8ve)
                    int cyc = 2 * (span - 1);                        // up-then-down period (triangle, no repeated peak/trough)
                    for (int i = 0, n = (beats * q) / dur; i < n; i++) { int p = i % cyc; AddV(g, i * dur, dur, p < span ? p : cyc - p); }
                } break;
                default: AddAll(g, 0, beats * q, voiceCount); break;
            }
            return g;
        }

        // Rasterize the events to a one-bar voice grid (for the editor seed/illustration). A 1-slice gap is left so
        // adjacent same-voice notes show as separate pads — this is DISPLAY-only; generation uses the events directly.
        static SequencerSlice[] BuildVoiceBar(int style, int beats, int voiceCount)
        {
            int q = SlicesPerQuarter;
            beats = Math.Max(1, beats);
            var g = new SequencerSlice[beats * q];
            foreach (var ev in BuildVoiceEvents(style, beats, Math.Max(1, voiceCount)))
                PutV(g, ev.Start, ev.Len > 1 ? ev.Len - 1 : ev.Len, ev.Voice);
            return g;
        }

        // Up then back down without repeating the endpoints: 0,1,2,3,2,1 for n=4.
        static int[] ArpUpDown(int n)
        {
            if (n <= 1) return new[] { 0 };
            var list = new List<int>();
            for (int i = 0; i < n; i++) list.Add(i);
            for (int i = n - 2; i >= 1; i--) list.Add(i);
            return list.ToArray();
        }

        static int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);
    }
}
