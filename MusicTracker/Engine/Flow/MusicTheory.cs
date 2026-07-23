using System;
using System.Collections.Generic;
using MusicTracker.Engine.Score;

namespace MusicTracker.Engine.Flow
{
    /// <summary>
    /// Harmony helpers: generate chord progressions in many styles (functional, pop, jazz, modal and exotic) for a
    /// key. Output is a list of (root pitch-class, <see cref="PatternGenerator"/> quality index). Diatonic chords use
    /// 7ths, exotic styles use chromatic/borrowed chords — but the TONIC is always a triad/6/add9 (never a 7th).
    /// "Morceau (auto)" strings sections together so it almost composes a whole piece. Vary the seed for variants.
    /// </summary>
    public static class MusicTheory
    {
        static readonly int[] MajorPcs = { 0, 2, 4, 5, 7, 9, 11 };
        static readonly int[] MinorPcs = { 0, 2, 3, 5, 7, 8, 10 };
        static readonly int[] MajorQual = { 0, 7, 7, 6, 8, 7, 9 }; // I(triad) ii7 iii7 IVΔ7 V7 vi7 viiø7
        static readonly int[] MinorQual = { 1, 9, 6, 7, 8, 6, 8 }; // i(triad) iiø7 IIIΔ7 iv7 V7 VIΔ7 VII7
        static readonly int[] MajorTonic = { 0, 11, 13 };          // I tonic colours (no 7th): triad / 6 / add9
        static readonly int[] MinorTonic = { 1, 12, 14 };          // i tonic colours: triad / m6 / m(add9)
        static readonly int[] DomColour = { 8, 15, 21 };           // V7 / V9 / V13

        static readonly int[][] Next =
        {
            new[] { 1, 2, 3, 4, 5, 6 }, new[] { 4, 6 }, new[] { 3, 5 }, new[] { 0, 1, 4, 6 },
            new[] { 0, 5 }, new[] { 1, 3, 4 }, new[] { 0 },
        };
        static readonly int[] LetterPc = { 0, 2, 4, 5, 7, 9, 11 };

        public static readonly string[] CadenceStyles =
        {
            "Auto (riche)",                 // 0
            "Authentique (V → I)",          // 1
            "Plagale (IV → I)",             // 2
            "Jazz (ii → V → I)",            // 3
            "Anatole (I-vi-ii-V)",          // 4
            "Pop (I-V-vi-IV)",              // 5
            "Doo-wop (I-vi-IV-V)",          // 6
            "EDM (vi-IV-I-V)",              // 7
            "Royal road (IV-V-iii-vi)",     // 8
            "Pachelbel (canon)",            // 9
            "Cycle des quintes ↓ (desc.)",  // 10
            "Blues (I-IV-V)",               // 11
            "Blues mineur",                 // 12
            "Andalouse (i-♭VII-♭VI-V)",     // 13
            "Phrygien / Espagnol",          // 14
            "Dorien (i-IV)",                // 15
            "Mixolydien (I-♭VII)",          // 16
            "Médiantes chromatiques",       // 17
            "Backdoor (ii-♭VII7-I)",        // 18
            "Substitution tritonique",      // 19
            "Demi-cadence (→ V)",           // 20
            "Rompue (V → vi)",              // 21
            "Morceau (auto)",               // 22
            "Coltrane (Giant Steps)",       // 23
            "Forme AABA (structurée)",      // 24
            "Cycle des quintes ↑ (mont.)",  // 25
            "Baroque (cycle → V-I, picardie)", // 26
            "Modale / médiantes (harpe)",   // 27
            "Éolienne / épique (♭VI-♭VII-i)", // 28
            "Riche (mixture modale)",       // 29
        };

        /// <summary>A fitting articulation/rhythm style (index into <see cref="PatternGenerator.StyleNames"/>) for a
        /// given cadence style, so "Auto" in the cadence dialog plays each genre with an idiomatic groove instead of
        /// flat block chords. (jazz→comping, blues→shuffle, pop→bass+chord, Spanish→habanera, canon→arpège, …)</summary>
        public static int AutoRhythmStyle(int cadenceStyle)
        {
            switch (cadenceStyle)
            {
                case 1: return 1;                                            // Authentique → plaqués (noires)
                case 2: return 0;                                            // Plagale → plaqués (tenu, "amen")
                case 3: case 4: case 18: case 19: case 23: case 24: return 6; // jazz/turnarounds/Coltrane/AABA → comping
                case 5: case 8: case 22: return 8;                           // Pop/Royal road/Morceau → basse+accord
                case 6: return 21;                                           // Doo-wop → slow rock (triolets 12-8)
                case 7: case 16: return 7;                                   // EDM/Mixolydien → rock (croches)
                case 9: return 3;                                            // Pachelbel → arpège montant
                case 10: case 25: return 5;                                  // Cycle des quintes → Alberti (Do-Sol-Mi-Sol)
                case 26: return 3;                                           // Baroque → arpège (broken-chord continuo)
                case 27: return 27;                                          // Modale (harpe) → arpège roulé de harpe
                case 28: return 1;                                           // Éolienne/épique → plaqués (noires)
                case 29: return 8;                                           // Riche modale → basse+accord
                case 11: case 12: return 9;                                  // blues → shuffle (triolets)
                case 13: case 14: return 18;                                 // Andalouse/Phrygien → habanera (espagnol)
                case 15: return 11;                                          // Dorien → arpège (croches)
                case 17: return 19;                                          // médiantes chromatiques → ballade
                case 20: case 21: return 1;                                  // Demi/Rompue → plaqués (noires)
                default: return 8;                                           // Auto (riche) + autres → pop
            }
        }

        public static int TonicPc(KeySignature k)
        {
            int l = Math.Max(0, Math.Min(6, k?.TonicLetter ?? 0));
            return ((LetterPc[l] + (k?.Accidental ?? 0)) % 12 + 12) % 12;
        }

        public static int DegreeOf(KeySignature key, int rootPc)
        {
            int tonic = TonicPc(key);
            var pcs = (key?.Mode == 1) ? MinorPcs : MajorPcs;
            int best = 0, bd = 99;
            for (int d = 0; d < 7; d++)
            {
                int dpc = (tonic + pcs[d]) % 12;
                int dist = Math.Min(((dpc - rootPc) % 12 + 12) % 12, ((rootPc - dpc) % 12 + 12) % 12);
                if (dist < bd) { bd = dist; best = d; }
            }
            return best;
        }

        /// <summary>A progression of <paramref name="numChords"/> chords in the chosen style, from the chosen chord
        /// (startDegree) toward the tonic (except Demi-cadence). Returns (root pitch-class, quality index) per chord.</summary>
        public static List<(int root, int quality)> Cadence(KeySignature key, int startDegree, int numChords, int style, int seed)
        {
            numChords = Math.Max(1, numChords);
            var rng = new Random(seed);
            bool minor = key?.Mode == 1;
            int tonic = TonicPc(key);
            var pcs = minor ? MinorPcs : MajorPcs;
            var qual = minor ? MinorQual : MajorQual;
            int s0 = ((startDegree % 7) + 7) % 7;

            (int o, int q) D(int deg) { int d = ((deg % 7) + 7) % 7; return (pcs[d], qual[d]); }  // diatonic chord
            (int o, int q) C(int off, int q) => (((off % 12) + 12) % 12, q);                       // chromatic/borrowed
            int Triad(int q) { switch (q) { case 6: case 8: return 0; case 7: return 1; case 9: return 2; default: return q; } } // 7th → plain triad
            (int o, int q) Dt(int deg) { var c = D(deg); return (c.o, Triad(c.q)); }                // diatonic TRIAD (no 7th)

            // Functional walk (chosen chord → tail), mapped to diatonic chords.
            List<(int, int)> Func(int[] tailDeg)
            {
                var degs = new List<int> { s0 };
                int headLen = Math.Max(1, numChords - tailDeg.Length);
                for (int i = 1; i < headLen; i++) degs.Add(Next[degs[degs.Count - 1]][rng.Next(Next[degs[degs.Count - 1]].Length)]);
                degs.AddRange(tailDeg);
                while (degs.Count > numChords) degs.RemoveAt(0);
                var l = new List<(int, int)>(); foreach (int d in degs) l.Add(D(d)); return l;
            }
            List<(int, int)> Tile(params (int, int)[] pat)
            {
                var l = new List<(int, int)>(); for (int i = 0; i < numChords; i++) l.Add(pat[i % pat.Length]); return l;
            }

            List<(int, int)> cells; bool toTonic = true, triadic = false, picardy = false;
            switch (style)
            {
                case 1: cells = Func(new[] { 4, 0 }); break;                                   // Authentique
                case 2: cells = Func(new[] { 3, 0 }); break;                                   // Plagale
                case 3: cells = Func(new[] { 1, 4, 0 }); break;                                // Jazz ii-V-I
                case 4: cells = Tile(D(0), D(5), D(1), D(4)); break;                           // Anatole I-vi-ii-V
                case 5: cells = Tile(D(0), D(4), D(5), D(3)); break;                           // Pop I-V-vi-IV
                case 6: cells = Tile(D(0), D(5), D(3), D(4)); break;                           // Doo-wop I-vi-IV-V
                case 7: cells = Tile(D(5), D(3), D(0), D(4)); break;                           // EDM vi-IV-I-V
                case 8: cells = Tile(D(3), D(4), D(2), D(5)); break;                           // Royal road IV-V-iii-vi
                case 9: cells = Tile(D(0), D(4), D(5), D(2), D(3), D(0), D(3), D(4)); break;   // Pachelbel
                case 10: { var l = new List<(int, int)>(); int d = s0; for (int i = 0; i < numChords; i++) { l.Add(Dt(d)); d = (d + 3) % 7; } cells = l; triadic = true; } break; // circle of 5ths DESC (triads)
                case 11: cells = Tile(D(0), D(0), D(0), D(0), D(3), D(3), D(0), D(0), D(4), D(3), D(0), D(4)); break; // Blues
                case 12: cells = Tile(C(0, 1), C(0, 1), C(0, 1), C(0, 1), C(5, 1), C(5, 1), C(0, 1), C(0, 1), C(7, 8), C(5, 1), C(0, 1), C(7, 8)); break; // minor blues
                case 13: cells = Tile(C(0, 1), C(10, 0), C(8, 0), C(7, 8)); break;             // Andalouse i-♭VII-♭VI-V
                case 14: cells = Tile(C(0, 1), C(1, 0), C(0, 1), C(7, 8)); break;              // Phrygien / Espagnol i-♭II
                case 15: cells = Tile(C(0, 1), C(5, 0)); break;                               // Dorien i-IV(maj)
                case 16: cells = Tile(C(0, 0), C(10, 0)); break;                              // Mixolydien I-♭VII
                case 17: cells = Tile(C(0, 0), C(3, 0), C(8, 0)); break;                       // chromatic mediants I-♭III-♭VI
                case 18: cells = Tile(D(1), C(10, 8), D(0)); break;                           // Backdoor ii-♭VII7-I
                case 19: cells = Tile(D(1), C(1, 8), D(0)); break;                            // Tritone sub ii-♭II7-I
                case 20: cells = Func(new[] { 4 }); toTonic = false; break;                   // Demi-cadence → V
                case 21: cells = Func(new[] { 4, 5 }); toTonic = false; break;                // Rompue V-vi (ends on vi)
                case 22: cells = Song(numChords, D, rng); break;                              // full-piece auto
                // Coltrane changes: the three key centres a MAJOR THIRD apart (0, ♭VI=+8, III=+4 — an augmented
                // cycle), each approached by its own V7. The non-tonic centres are Δ7; the tonic stays a triad.
                case 23: cells = Tile(C(0, 6), C(3, 8), C(8, 6), C(11, 8), C(4, 6), C(7, 8)); break; // Coltrane / Giant Steps
                // Structured AABA: an 8-bar-style A phrase (I-vi-ii-V) stated, repeated, a contrasting bridge B
                // (IV-IV-V-V), then A again. numChords is split into 4 equal sections.
                case 24:
                {
                    var A = new[] { D(0), D(5), D(1), D(4) };
                    var B = new[] { D(3), D(3), D(4), D(4) };
                    var secs = new[] { A, A, B, A };
                    int per = Math.Max(1, numChords / 4);
                    var l = new List<(int, int)>();
                    for (int si = 0; si < 4; si++) for (int i = 0; i < per; i++) l.Add(secs[si][i % secs[si].Length]);
                    while (l.Count < numChords) l.Add(D(0));
                    while (l.Count > numChords) l.RemoveAt(l.Count - 1);
                    cells = l;
                } break;
                case 25: { var l = new List<(int, int)>(); int d = s0; for (int i = 0; i < numChords; i++) { l.Add(Dt(d)); d = (d + 4) % 7; } cells = l; triadic = true; } break; // cycle of 5ths ASC (I-V-ii-vi…, triads)
                // Baroque: a DESCENDING circle-of-fifths sequence (Bach's staple — in minor it threads i-iv-♭VII-III-
                // VI-ii-V-i, the modal ♭VII included), closed by an authentic V→I, and ENDING on a major tonic in
                // minor (the tierce de Picardie that the digested corpus shows). Matches the harmonic schema analysed
                // from the WTC / Art of Fugue / Goldberg.
                case 26:
                {
                    var l = new List<(int, int)>(); int d = s0;
                    for (int i = 0; i < numChords; i++) { l.Add(Dt(d)); d = (d + 3) % 7; }
                    if (numChords >= 2) l[numChords - 2] = D(4);   // an authentic dominant (V7) right before the tonic
                    cells = l; triadic = true; picardy = minor;
                } break;
                // Modal / mediant oscillation (harp / new-age / celtic / cinematic): tonic-prolonging, NO dominant —
                // I ↔ vi ↔ ♭III ↔ IV, hovering around the tonic with the flat-mediant colour (analysed from the harp
                // piece: vi→I and ♭III→I are the cadences, not V→I).
                case 27: cells = Tile(D(0), D(5), C(3, 0), D(3)); break;                       // I – vi – ♭III – IV
                // Aeolian / "epic" cadence (film/game, and the digested baroque-minor + cadence examples): the modal
                // ♭VI – ♭VII rising to the tonic. Borrowed (modal mixture) in major; diatonic in minor.
                case 28: cells = Tile(C(0, minor ? 1 : 0), C(8, 0), C(10, 0)); break;          // i – ♭VI – ♭VII – (i)
                // RICH modal-mixture cadence (what the examples favour): a functional frame coloured by a borrowed
                // ♭VI as a dramatic pre-dominant — I – vi – ♭VI – V – (I), the vi→♭VI chromatic slip then the dominant.
                case 29: cells = Tile(D(0), D(5), C(8, 0), D(4)); break;                       // I – vi – ♭VI – V – (I)
                default: cells = Func(new[] { rng.Next(2) == 0 ? 1 : 3, 4, 0 }); break;       // Auto (pre-dom → V → I)
            }
            if (toTonic && cells.Count > 0) cells[cells.Count - 1] = (pcs[0], minor ? 1 : 0); // resolve on the tonic

            var outp = new List<(int, int)>(cells.Count);
            foreach (var c in cells)
            {
                int off = ((c.Item1 % 12) + 12) % 12, q = c.Item2;
                if (off == 0) q = triadic ? (minor ? 1 : 0) : (minor ? MinorTonic : MajorTonic)[rng.Next(3)]; // tonic: plain triad for triadic styles, else colour (never a 7th)
                else if (q == 8) q = DomColour[rng.Next(DomColour.Length)];        // dominant colour V7/9/13
                outp.Add(((tonic + off) % 12, q));
            }
            if (picardy && outp.Count > 0 && outp[outp.Count - 1].Item1 == tonic)
                outp[outp.Count - 1] = (tonic, 0);                                 // tierce de Picardie: final i → I (major)
            return outp;
        }

        /// <summary>The diatonic chord at a scale degree in the key (root pitch-class + quality). The tonic is a
        /// triad (no 7th). Used so a chord can be stored as a DEGREE and re-resolved when the key changes.</summary>
        public static (int root, int quality) DiatonicChord(KeySignature key, int degree)
        {
            bool minor = key?.Mode == 1;
            int tonic = TonicPc(key);
            var pcs = minor ? MinorPcs : MajorPcs;
            var qual = minor ? MinorQual : MajorQual;
            int d = ((degree % 7) + 7) % 7;
            int q = (d == 0) ? (minor ? 1 : 0) : qual[d]; // tonic = triad
            return ((tonic + pcs[d]) % 12, q);
        }

        // ---- chord FUNCTION (secondary dominants & co) ---------------------------------------------------------
        // Single source of truth, shared by the timeline's roman-numeral label AND the chord editor's degree combo,
        // so the two can never disagree about what a chord "is".

        /// <summary>The degrees that can be TONICISED by a secondary dominant: ii, iii, IV, V, vi. The tonic is
        /// excluded (V/I is just V) and so is the diminished vii° (a diminished triad cannot be tonicised).</summary>
        public static readonly int[] SecondaryTargets = { 1, 2, 3, 4, 5 };

        /// <summary>The chord's shape read from its ACTUAL intervals (no hard-coded quality-index lists), so
        /// sevenths, tensions and suspensions are classified correctly.</summary>
        public static void ChordShape(int quality, out bool minThird, out bool dimFifth, out bool augFifth, out bool dom7)
        {
            var set = new HashSet<int>();
            var notes = PatternGenerator.ChordNotes(0, 4, quality, 0);
            int b = (notes != null && notes.Length > 0) ? notes[0] : 0;
            if (notes != null) foreach (var n in notes) set.Add(((n - b) % 12 + 12) % 12);
            minThird = set.Contains(3) && !set.Contains(4);
            bool fifth = set.Contains(7);
            dimFifth = set.Contains(6) && !fifth;
            augFifth = set.Contains(8) && !fifth && !set.Contains(3);
            dom7 = set.Contains(10) && !minThird && !dimFifth;   // major third + minor seventh
        }

        /// <summary>Scale index (0..6) of a pitch-class in the key, or −1 when it isn't diatonic.</summary>
        public static int DiatonicDegreeOf(KeySignature key, int pc)
        {
            int tonic = TonicPc(key);
            var scale = MusicalMode.Scale(MusicalMode.Effective(key ?? new KeySignature()));
            for (int d = 0; d < 7; d++) if (((tonic + scale[d]) % 12 + 12) % 12 == (((pc % 12) + 12) % 12)) return d;
            return -1;
        }

        /// <summary>Semitones of the diatonic third stacked on a degree (4 = major, 3 = minor).</summary>
        public static int DiatonicThird(KeySignature key, int degree)
        {
            var s = MusicalMode.Scale(MusicalMode.Effective(key ?? new KeySignature()));
            int d = ((degree % 7) + 7) % 7;
            return ((s[(d + 2) % 7] - s[d]) % 12 + 12) % 12;
        }
        /// <summary>True when the diatonic triad on that degree is DIMINISHED (so it cannot be tonicised).</summary>
        public static bool DiatonicIsDim(KeySignature key, int degree)
        {
            var s = MusicalMode.Scale(MusicalMode.Effective(key ?? new KeySignature()));
            int d = ((degree % 7) + 7) % 7;
            return ((s[(d + 4) % 7] - s[d]) % 12 + 12) % 12 == 6;
        }

        /// <summary>
        /// The degree (0..6) this chord tonicises as a SECONDARY DOMINANT, or −1 when it isn't one. A chord qualifies
        /// when it is major-quality (or any dominant 7th that isn't the key's own V) and its root sits a perfect fifth
        /// above a tonicisable degree — e.g. in C, D major → V/V, C7 → V/IV.
        /// </summary>
        public static int SecondaryDominantTarget(KeySignature key, int rootPc, int quality)
        {
            ChordShape(quality, out bool minThird, out bool dimFifth, out _, out bool dom7);
            if (minThird || dimFifth) return -1;
            int root = ((rootPc % 12) + 12) % 12;
            int deg = DiatonicDegreeOf(key, root);
            bool diatonicMajorHere = deg >= 0 && DiatonicThird(key, deg) == 4;
            bool actsAsSecondary = dom7 ? deg != 4 : !diatonicMajorHere;
            if (!actsAsSecondary) return -1;
            int target = DiatonicDegreeOf(key, ((root - 7) % 12 + 12) % 12);
            if (target < 0 || DiatonicIsDim(key, target)) return -1;
            return target;
        }

        /// <summary>The degree tonicised by a secondary LEADING-TONE chord (a diminished chord a semitone below its
        /// target, on a chromatic root), or −1.</summary>
        public static int SecondaryLeadingToneTarget(KeySignature key, int rootPc, int quality)
        {
            ChordShape(quality, out _, out bool dimFifth, out _, out _);
            int root = ((rootPc % 12) + 12) % 12;
            if (!dimFifth || DiatonicDegreeOf(key, root) >= 0) return -1;
            int target = DiatonicDegreeOf(key, (root + 1) % 12);
            if (target < 0 || DiatonicIsDim(key, target)) return -1;
            return target;
        }

        /// <summary>Root pitch-class of the secondary dominant that tonicises <paramref name="targetDegree"/>.</summary>
        public static int SecondaryDominantRoot(KeySignature key, int targetDegree)
            => ((DiatonicChord(key ?? new KeySignature(), targetDegree).root + 7) % 12 + 12) % 12;

        // Base TRIAD quality per scale degree (major / natural minor), parallel to *Qual (which carry the diatonic 7ths).
        static readonly int[] MajorTriadQual = { 0, 1, 1, 0, 0, 1, 2 }; // I ii iii IV V vi vii° (maj/min/dim indices)
        static readonly int[] MinorTriadQual = { 1, 2, 0, 1, 1, 0, 0 }; // i ii° III iv v VI VII

        /// <summary>PRIMARY chord colours, parallel to the <see cref="DiatonicChord"/> switch — the SINGLE source of truth
        /// for how many primary colours exist (drives both the editor combo AND the DiatonicColour clamp). Append only.</summary>
        public static readonly string[] DiatonicColourNames =
            { "Triade", "Sixte", "7e", "9e (7+9)", "9e (add9)" };

        /// <summary>SECONDARY colour: a suspension (drops the 3rd for a 2nd/4th), applicable to ANY primary. 0 = none.</summary>
        public static readonly string[] SuspensionNames = { "Aucune", "Sus2", "Sus4" };

        /// <summary>MODE override: force the third/fifth/seventh quality instead of following the degree. 0 = auto (diatonic),
        /// 1 = major, 2 = minor, 3 = augmented (♯5), 4 = diminished (♭5), 5 = dominant (major triad + ♭7 → dom7/dom9).</summary>
        public static readonly string[] ModeOverrideNames = { "Auto", "Majeur", "Mineur", "Augmenté", "Diminué", "Dominante" };

        /// <summary>Diatonic chord at a degree with a chosen PRIMARY colour + optional SUSPENSION + optional MODE override.
        /// Primary: 0 = triade, 1 = sixte, 2 = 7e (diatonic Maj7/m7/dom7/ø7), 3 = 9e complète (7th+9th → Maj9/m9/9dom),
        /// 4 = 9e simple (add9, no 7th). Suspension: 0 = none, 1 = sus2, 2 = sus4 — replaces the 3rd (so maj/min collapse;
        /// the 7th/6th/9th stay). Mode: 0 = auto (diatonic), 1 = force MAJOR (maj triad + Maj7), 2 = force MINOR (min triad
        /// + m7). Root unchanged.</summary>
        public static (int root, int quality) DiatonicChord(KeySignature key, int degree, int colour, int suspension = 0, int mode = 0)
        {
            bool minor = key?.Mode == 1;
            int tonic = TonicPc(key);
            var pcs = minor ? MinorPcs : MajorPcs;
            int d = ((degree % 7) + 7) % 7;
            int root = (tonic + pcs[d]) % 12;
            int triad = (minor ? MinorTriadQual : MajorTriadQual)[d];         // 0 maj / 1 min / 2 dim
            int seventh = (d == 0) ? (minor ? 7 : 6) : (minor ? MinorQual : MajorQual)[d]; // 6 Maj7,7 m7,8 dom7,9 ø7
            if (mode == 1) { triad = 0; seventh = 6; }                        // force MAJOR: maj triad + Maj7
            else if (mode == 2) { triad = 1; seventh = 7; }                   // force MINOR: min triad + m7
            else if (mode == 3) { triad = 3; seventh = 34; }                  // force AUGMENTED: aug triad (♯5) + 7♯5
            else if (mode == 4) { triad = 2; seventh = 10; }                  // force DIMINISHED: dim triad (♭5) + dim7
            else if (mode == 5) { triad = 0; seventh = 8; }                   // force DOMINANT: maj triad + ♭7 (dom7/dom9)
            bool maj7 = seventh == 6;
            int q;
            if (suspension == 0)
            {
                switch (colour)
                {
                    case 1: q = triad == 0 ? 11 : triad == 1 ? 12 : triad; break;                       // Sixte
                    case 2: q = seventh; break;                                                          // 7e diatonique
                    case 3: q = seventh == 6 ? 16 : seventh == 7 ? 17 : seventh == 8 ? 15 : seventh; break; // 9e complète
                    case 4: q = triad == 0 ? 13 : triad == 1 ? 14 : triad; break;                       // 9e simple (add9)
                    default: q = triad; break;                                                           // Triade
                }
            }
            else
            {
                bool s4 = suspension == 2;                                     // sus4 else sus2
                switch (colour)
                {
                    case 1: q = s4 ? 27 : 28; break;                          // Sixte  → 6sus4 / 6sus2
                    case 2: q = maj7 ? (s4 ? 29 : 30) : (s4 ? 23 : 24); break; // 7e    → Maj7sus / 7sus
                    case 3: q = maj7 ? (s4 ? 31 : 32) : (s4 ? 25 : 26); break; // 9e cpl→ Maj9sus / 9sus
                    case 4: q = s4 ? 33 : 4; break;                           // add9  → add9sus4 / (sus2 ≡ Sus2)
                    default: q = s4 ? 5 : 4; break;                           // Triade→ Sus4 / Sus2
                }
            }
            return (root, q);
        }

        // ---- transposition (here for consistency with the rest of the harmony) ----

        /// <summary>Semitone shift from one tonic to another. direction: 0 = nearest, 1 = up, 2 = down.</summary>
        public static int NearestInterval(int fromPc, int toPc, int direction)
        {
            int up = ((toPc - fromPc) % 12 + 12) % 12; // 0..11
            switch (direction)
            {
                case 1: return up;                       // up
                case 2: return up == 0 ? 0 : up - 12;    // down
                default: return up > 6 ? up - 12 : up;   // nearest
            }
        }

        /// <summary>Per-pitch-class semitone delta to remap notes from one mode to another (degree by degree),
        /// indexed by pitch-class relative to the tonic. null when the modes are equal. Identical to going via major.</summary>
        public static int[] ModeDelta(int srcMode, int tgtMode)
        {
            if (srcMode == tgtMode) return null;
            var src = MusicalMode.Scale(srcMode);
            var tgt = MusicalMode.Scale(tgtMode);
            var d = new int[12];
            for (int i = 0; i < 7; i++) d[((src[i] % 12) + 12) % 12] = tgt[i] - src[i];
            return d;
        }

        // ---- voice leading: pick each chord's inversion so the notes move the least from the previous chord ----

        /// <summary>For a chord sequence, choose each chord's OCTAVE PLACEMENT (shift −1/0/+1 relative to
        /// <paramref name="baseOctave"/>) and INVERSION so the notes move the least from the previous chord —
        /// while staying in a register band around the first chord, so the progression doesn't drift up over a
        /// cycle of fifths ("the fingers barely move, and the hand stays in place"). Returns (shift, inversion).</summary>
        public static (int shift, int inversion)[] VoiceLead(IList<(int root, int quality)> chords, int baseOctave)
            => VoiceLead(chords, baseOctave, 0);

        /// <summary>Voice-lead a chord sequence: per chord pick the (octave shift, inversion) that moves the least from
        /// the previous chord, kept in register. <paramref name="anchor"/> biases WHICH voice stays closest:
        /// 0 = Auto (minimize TOTAL movement), 1 = Basse proche (smooth BASS line), 2 = Haut proche (smooth TOP line).</summary>
        public static (int shift, int inversion)[] VoiceLead(IList<(int root, int quality)> chords, int baseOctave, int anchor)
        {
            var result = new (int, int)[chords.Count];
            int[] prev = null;
            double target = 0; bool haveTarget = false;
            const double Band = 7;   // keep the average pitch within ~a fifth of the established register
            for (int i = 0; i < chords.Count; i++)
            {
                int root = chords[i].root, quality = chords[i].quality;
                int voices = Math.Max(1, PatternGenerator.ChordNotes(root, baseOctave, quality, 0).Length);
                int bestShift = 0, bestInv = 0; double bestCost = double.MaxValue;
                for (int sh = -1; sh <= 1; sh++)
                    for (int k = 0; k < voices; k++)
                    {
                        var notes = PatternGenerator.ChordNotes(root, baseOctave + sh, quality, k);
                        if (notes.Length == 0) continue;
                        double avg = Avg(notes);
                        double cost;
                        if (!haveTarget) cost = Math.Abs(notes[0] - (baseOctave + 1) * 12); // chord 0: lowest near base-octave C
                        else
                        {
                            cost = VoicingCost(prev, notes);
                            // anchor: heavily weight the low/high voice so IT stays closest; total movement breaks ties
                            if (anchor == 1) cost += 100 * Math.Abs(notes[0] - prev[0]);
                            else if (anchor == 2) cost += 100 * Math.Abs(notes[notes.Length - 1] - prev[prev.Length - 1]);
                            if ((((notes[0] % 12) - (root % 12)) + 12) % 12 == 7) cost += 3; // avoid landing on a 6/4 (fifth in bass = unstable)
                            if (Math.Abs(avg - target) > Band) cost += 1000; // outside the register band → avoid (anti-drift)
                        }
                        if (cost < bestCost) { bestCost = cost; bestShift = sh; bestInv = k; }
                    }
                result[i] = (bestShift, bestInv);
                prev = PatternGenerator.ChordNotes(root, baseOctave + bestShift, quality, bestInv);
                if (!haveTarget) { target = Avg(prev); haveTarget = true; }
            }
            return result;
        }

        static double Avg(int[] n) { if (n.Length == 0) return 0; double s = 0; foreach (int x in n) s += x; return s / n.Length; }

        /// <summary>GREEDY one-step voice-leading: given the PREVIOUS chord's realized notes, pick the (inversion,
        /// octave) for this chord (root/quality) that moves the least from it. <paramref name="anchor"/>: 0 = auto
        /// (total movement), 1 = bass close (smooth lowest voice), 2 = top close (smooth highest voice). Octave search
        /// is ±1 around <paramref name="baseOctave"/>. Used to voice-lead a sequence of stand-alone chord modules.</summary>
        public static (int inversion, int octave) VoiceLeadStep(int[] prevNotes, int root, int quality, int baseOctave, int anchor)
        {
            int voices = Math.Max(1, PatternGenerator.ChordNotes(root, baseOctave, quality, 0).Length);
            int bestInv = 0, bestOct = baseOctave; double bestCost = double.MaxValue;
            if (prevNotes == null || prevNotes.Length == 0) return (0, baseOctave);
            for (int sh = -1; sh <= 1; sh++)
                for (int k = 0; k < voices; k++)
                {
                    var notes = PatternGenerator.ChordNotes(root, baseOctave + sh, quality, k);
                    if (notes.Length == 0) continue;
                    double cost = VoicingCost(prevNotes, notes);
                    if (anchor == 1) cost += 100 * Math.Abs(notes[0] - prevNotes[0]);
                    else if (anchor == 2) cost += 100 * Math.Abs(notes[notes.Length - 1] - prevNotes[prevNotes.Length - 1]);
                    if ((((notes[0] % 12) - (root % 12)) + 12) % 12 == 7) cost += 3; // avoid landing on a 6/4 (fifth in bass = unstable)
                    if (cost < bestCost) { bestCost = cost; bestInv = k; bestOct = baseOctave + sh; }
                }
            return (bestInv, bestOct);
        }

        // Total movement: each new note to its nearest previous note (low = close voicing).
        static double VoicingCost(int[] prev, int[] cur)
        {
            double sum = 0;
            foreach (int c in cur)
            {
                int best = int.MaxValue;
                foreach (int p in prev) best = Math.Min(best, Math.Abs(c - p));
                sum += best;
            }
            return sum;
        }

        // A loose song form: alternate two contrasting loop sections, ending on a ii-V-I cadence.
        static List<(int, int)> Song(int n, Func<int, (int, int)> d, Random rng)
        {
            (int, int)[][] loops =
            {
                new[] { d(0), d(4), d(5), d(3) }, // pop
                new[] { d(0), d(5), d(3), d(4) }, // doo-wop
                new[] { d(5), d(3), d(0), d(4) }, // edm
                new[] { d(3), d(4), d(2), d(5) }, // royal road
                new[] { d(0), d(4), d(5), d(2), d(3), d(0), d(3), d(4) }, // pachelbel
            };
            var verse = loops[rng.Next(loops.Length)];
            var chorus = loops[rng.Next(loops.Length)];
            var l = new List<(int, int)>();
            while (l.Count < n)
            {
                var sec = l.Count < n / 2 ? verse : chorus;
                for (int i = 0; i < sec.Length && l.Count < n; i++) l.Add(sec[i]);
            }
            if (l.Count >= 3) { l[l.Count - 3] = d(1); l[l.Count - 2] = d(4); } // …ii - V - (I forced by toTonic)
            return l;
        }
    }
}
