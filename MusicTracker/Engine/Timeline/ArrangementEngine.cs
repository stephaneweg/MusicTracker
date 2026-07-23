using System;
using System.Collections.Generic;
using System.Linq;
using MusicTracker.Engine;
using MusicTracker.Engine.Flow;
using MusicTracker.Engine.Score;
using V2 = MusicTracker.Engine.ComposerV2;
using V3 = MusicTracker.Engine.ComposerV3;

namespace MusicTracker.Engine.Timeline
{
    /// <summary>
    /// ARRANGEMENT ENGINE — the deterministic, ComposedArrangement-bound helpers (chord theory, motif realization,
    /// theme/variation transforms, harmony/bass rebuilds) extracted from <see cref="GhibliComposer"/> into a neutral
    /// static class that does NOT derive from MusicComposer. The UI editing path and the future Orchestrateur consume
    /// these helpers without pulling in the form-orchestration of a particular composer. Bodies are copied VERBATIM;
    /// the only changes vs GhibliComposer are accessibility (everything is public/internal static) and the explicit
    /// qualification of base-engine helpers as MusicComposer.X(...) / HisaishiComposer.X(...).
    /// </summary>
    public static class ArrangementEngine
    {
        // corpus character tokens (mirrors GhibliComposer.CharVals) — used by RebuildBackingV2 to bias the V2 backing.
        static readonly string[] CharVals = { null, "enjouee", "moderee", "calme", "majestueux" };

        // Rebuild the ACCOMPANIMENT + BASS from the (edited) chord trame USING THE V2 STYLE (so editing a chord on the
        // chord lane keeps Vivaldi sounding like Vivaldi, etc.) — the style-aware counterpart of the V1 RebuildHarmony.
        // Returns full-piece note lists for the caller to redistribute into the per-bar Accompagnement / Basse riffs.
        public static (List<RiffNote> accomp, List<RiffNote> bass) RebuildBackingV2(ComposedArrangement arr)
        {
            var empty = (new List<RiffNote>(), new List<RiffNote>());
            if (arr == null || string.IsNullOrEmpty(arr.ModelFile) || arr.Sections == null) return empty;
            V2.CorpusModelV2 model;
            try { model = V2.ComposerV2Runtime.LoadModel(arr.ModelFile); } catch { return empty; }
            var gen = V3.ComposerV3Factory.For(arr.ModelFile);
            bool minor = arr.FullMode == 1;
            int charIdx = (arr.Options != null && arr.Options.TryGetValue("char", out int ci)) ? ci : 0;
            string charTok = CharVals[Math.Min(CharVals.Length - 1, Math.Max(0, charIdx))];
            var segs = new List<KeyValuePair<string, List<(int rootPc, int quality)>>>();
            foreach (var sec in arr.Sections)
            {
                var ch = sec.Bars > 0 ? arr.SectionChords(sec) : null;   // (root,quality) absolute pcs from the edited trame
                if (ch == null || ch.Count == 0) continue;
                var pairs = new List<(int rootPc, int quality)>(); foreach (var c in ch) pairs.Add((c.root, c.quality));
                segs.Add(new KeyValuePair<string, List<(int rootPc, int quality)>>(BackingLabel(sec.Role), pairs));
            }
            var backing = gen.MakeBacking(model, arr.Seed + 5, minor, arr.TonicPc, charTok, segs);
            return (backing.Accomp, backing.Bass);
        }
        static string BackingLabel(string role)
        {
            switch (role) { case "intro": return "intro"; case "theme": return "theme"; case "dev": return "climax"; case "outro": return "outro"; default: return "body"; }
        }

        // A calm, ALWAYS-diatonic tonic colour for the chosen mode (add9 / m(add9) when the 2nd is major, else a plain
        // triad — e.g. Phrygian's b2 forbids add9). Used for the pre-cadential tonic.
        public static int DiatonicTonicQuality(int fullMode)
        {
            int[] sc = MusicalMode.Scale(fullMode);
            bool maj = sc[2] == 4, maj2 = sc[1] == 2;
            return maj ? (maj2 ? 13 : 0) : (maj2 ? 14 : 1);
        }

        // The FINAL tonic chord: a maj7 sparkle when the mode actually has a major 7th (Lydian/Ionian); otherwise a
        // resolved add9 / m(add9) / triad — always diatonic (no dom7 b7 on a Mixolydian tonic, no b2 on Phrygian).
        public static int DiatonicFinalQuality(int fullMode, Random rng)
        {
            int[] sc = MusicalMode.Scale(fullMode);
            bool maj = sc[2] == 4, maj2 = sc[1] == 2, maj7 = sc[6] == 11;
            if (maj) return (maj7 && rng.Next(2) == 0) ? 6 : (maj2 ? 13 : 0);
            return maj2 ? 14 : 1;
        }

        // The DIATONIC chord on a scale degree (0=I … 6=vii) of a mode — root + a quality DERIVED from the scale's own
        // intervals (triad + its diatonic 7th): the engine "chooses the flavour" from just a degree. Used for the
        // suspended V at the theme's end and (later) the degree-based chord lane.
        public static (int root, int quality) DiatonicChord(int tonicPc, int fullMode, int degreeIndex)
        {
            int[] sc = MusicalMode.Scale(fullMode);
            int di = ((degreeIndex % 7) + 7) % 7;
            int root = (((tonicPc + sc[di]) % 12) + 12) % 12;
            int t = ((sc[(di + 2) % 7] - sc[di]) % 12 + 12) % 12;
            int fif = ((sc[(di + 4) % 7] - sc[di]) % 12 + 12) % 12;
            int sev = ((sc[(di + 6) % 7] - sc[di]) % 12 + 12) % 12;
            int triad = (t == 4 && fif == 7) ? 0 : (t == 3 && fif == 7) ? 1 : (t == 3 && fif == 6) ? 2 : (t == 4 && fif == 8) ? 3 : (t == 4 ? 0 : 1);
            int q = triad == 0 ? (sev == 11 ? 6 : sev == 10 ? 8 : 0)       // maj7 / dom7 / maj
                  : triad == 1 ? (sev == 10 ? 7 : 1)                        // min7 / min
                  : triad == 2 ? (sev == 10 ? 9 : sev == 9 ? 10 : 2)        // m7b5 / dim7 / dim
                  : 3;
            return (root, q);
        }

        // Same diatonic chord, but the user picks the COLOUR (0 triade · 1 7e · 2 9e · 3 11e · 4 13e · 5 sus2 · 6 sus4).
        // The root + the triad family (maj/min/dim) come from the scale; the colour selects the right quality index.
        public static (int root, int quality) DiatonicChordColored(int tonicPc, int fullMode, int degreeIndex, int color)
        {
            int[] sc = MusicalMode.Scale(fullMode);
            int di = ((degreeIndex % 7) + 7) % 7;
            int root = (((tonicPc + sc[di]) % 12) + 12) % 12;
            int t = ((sc[(di + 2) % 7] - sc[di]) % 12 + 12) % 12;
            int fif = ((sc[(di + 4) % 7] - sc[di]) % 12 + 12) % 12;
            int sev = ((sc[(di + 6) % 7] - sc[di]) % 12 + 12) % 12;
            int triad = (t == 4 && fif == 7) ? 0 : (t == 3 && fif == 7) ? 1 : (t == 3 && fif == 6) ? 2 : (t == 4 && fif == 8) ? 3 : (t == 4 ? 0 : 1);
            int q;
            switch (color)
            {
                case 1: q = triad == 1 ? 7 : triad == 2 ? 9 : (sev == 11 ? 6 : 8); break;     // 7e : min7 / m7b5 / maj7|dom7
                case 2: q = triad == 1 ? 17 : triad == 2 ? 9 : (sev == 11 ? 16 : 15); break;  // 9e : m9 / m7b5 / maj9|dom9
                case 3: q = 20; break;                                                        // 11e (dom 11)
                case 4: q = 21; break;                                                        // 13e (dom 13)
                case 5: q = 4; break;                                                         // sus2
                case 6: q = 5; break;                                                         // sus4
                default: q = triad; break;                                                    // triade
            }
            return (root, q);
        }

        // ---- RE-EXPOSITION: the SAME theme (rhythm + contour) RE-FITTED onto new chords — strong beats snap to a chord
        // tone of the new chord (scale-filtered), weak notes snap to the scale. "Le même thème transposé sur ces accords." ----
        public static List<RiffNote> RefitTheme(List<RiffNote> theme, List<(int root, int quality)> newCh, int chordSlices, HashSet<int> scale)
        {
            var outl = new List<RiffNote>();
            foreach (var n in theme)
            {
                var c = newCh[Math.Min(n.Start / chordSlices, newCh.Count - 1)];
                var cp = MusicComposer.ChordPcs(c.root, c.quality);
                bool strong = (n.Start % chordSlices) == 0;   // snap to a chord tone at every chord onset (every 2 beats)
                int midi = n.Note + 12, pitch = strong ? MusicComposer.NearestChord(midi, cp) : midi;
                if (!scale.Contains(((pitch % 12) + 12) % 12)) pitch = MusicComposer.NearestScale(pitch, scale);
                outl.Add(new RiffNote(Math.Max(0, Math.Min(95, pitch - 12)), n.Start, n.Length));
            }
            return outl;
        }

        // ---- VARIATION by AJUSTEMENT MODAL: re-harmonize the theme onto new chords. For each note: INVERSE transform it to
        // a DEGREE above its ORIGINAL chord root (= express it on the tonal chord on its tonic), then FORWARD it onto the
        // variation's chord with the ajustement modal (closest TRIAD inversion + the per-inversion degree shift). So a note
        // that was a chord tone stays a chord tone (maybe another), the shape is kept, and the bass voice-leads. ----
        public static List<RiffNote> RefitThemeModal(List<RiffNote> theme, List<(int root, int quality)> origCh, List<(int root, int quality)> newCh, int chordSlices, HashSet<int> scale)
        {
            var outl = new List<RiffNote>();
            if (theme == null || newCh == null || newCh.Count == 0) return outl;
            var notes = new List<RiffNote>(theme); notes.Sort((a, b) => a.Start.CompareTo(b.Start));
            int lastCell = -1, bassPc = 0, newRootPc = 0, prevBassPc = -1;
            HashSet<int> ctSet = null;
            foreach (var n in notes)
            {
                int ci = Math.Min(Math.Max(0, n.Start / chordSlices), newCh.Count - 1);
                if (ci != lastCell)   // a new chord cell → choose its inversion (bass = chord tone nearest the previous bass)
                {
                    lastCell = ci;
                    newRootPc = (((newCh[ci].root) % 12) + 12) % 12;
                    var stack = ChordStackPcs(newCh[ci].root, newCh[ci].quality);
                    ctSet = new HashSet<int>(); foreach (var p in stack) ctSet.Add((((p) % 12) + 12) % 12);
                    bassPc = stack.Count > 0 ? (((stack[0]) % 12) + 12) % 12 : newRootPc;
                    if (prevBassPc >= 0) { int best = 99; foreach (var p in stack) { int pc = (((p) % 12) + 12) % 12, dpc = PcDist(pc, prevBassPc); if (dpc < best) { best = dpc; bassPc = pc; } } }
                    prevBassPc = bassPc;
                }
                int oci = Math.Min(Math.Max(0, n.Start / chordSlices), origCh.Count - 1);
                int oRpc = (((origCh[oci].root) % 12) + 12) % 12, P = n.Note + 12;
                int oRmidi = oRpc; while (oRmidi + 12 <= P) oRmidi += 12; while (oRmidi > P) oRmidi -= 12;   // orig root just at/below the note
                int d = ScaleStepsBetween(oRmidi, P, scale) + 1;                                            // INVERSE: degree above the orig root
                int newBass = MusicComposer.NearestPc(oRmidi, bassPc);                                       // keep the note's register
                int pitch = MusicComposer.ShiftScale(newBass, ModalStep(d, newRootPc, bassPc, ctSet, scale), scale);     // FORWARD ajustement modal
                outl.Add(new RiffNote(Clamp95(pitch - 12), n.Start, n.Length));
            }
            return outl;
        }
        static int PcDist(int a, int b) { int d = ((((a - b) % 12) + 12) % 12); return Math.Min(d, 12 - d); }
        // Number of SCALE steps from fromMidi UP to (the scale tone nearest) toMidi.
        static int ScaleStepsBetween(int fromMidi, int toMidi, HashSet<int> scale)
        {
            if (toMidi <= fromMidi) return 0;
            int m = fromMidi, k = 0, guard = 0;
            while (m < toMidi && guard++ < 60)
            {
                int nx = MusicComposer.ScaleStep(m, 1, scale);
                if (nx > toMidi) return (toMidi - m <= nx - toMidi) ? k : k + 1;
                m = nx; k++;
                if (m == toMidi) return k;
            }
            return k;
        }

        // ---- VARIATION CATALOGUE: named techniques to turn the theme into a variation. Auto cycles them by index, so a
        // "Thème + variations" gets a DIFFERENT character each variation. The riff editor exposes these names per-variation.
        public static readonly string[] VariationNames =
            { "Auto", "Figuration rythmique", "Ajustement modal", "Moto perpetuo", "Rétrograde", "Inversion de contour", "Mode emprunté", "Ornementation" };

        // Apply technique `tech` (0 = Auto → cycles by varIdx) to the theme (in its OWN key). themeChords/sectionChords feed
        // the modal re-harmonization; tonicPc feeds the borrowed-mode colour. Returns the varied line in the SAME key.
        public static List<RiffNote> ApplyVariation(int tech, int varIdx, List<RiffNote> theme, List<(int root, int quality)> themeChords,
            List<(int root, int quality)> sectionChords, int chordSlices, int barSlices, HashSet<int> scale, int tonicPc, Random rng)
        {
            if (theme == null || theme.Count == 0) return new List<RiffNote>();
            if (tech <= 0) tech = 1 + (varIdx % (VariationNames.Length - 1));   // Auto → a different technique each variation
            switch (tech)
            {
                case 2: return RefitThemeModal(theme, themeChords, sectionChords, chordSlices, scale);
                case 3: return MotoPerpetuo(theme, sectionChords, chordSlices, scale);
                case 4: return Retrograde(theme);
                case 5: return InvertContour(theme, scale);
                case 6: return BorrowMode(theme, tonicPc);
                case 7: return Ornament(theme, scale);
                default: return VaryRhythm(theme, 1 + varIdx / 2, scale, rng);   // 1 = rhythmic figuration
            }
        }

        public static List<RiffNote> Retrograde(List<RiffNote> theme)   // the theme backwards in time
        {
            var outl = new List<RiffNote>();
            int total = 0; foreach (var n in theme) total = Math.Max(total, n.Start + n.Length);
            foreach (var n in theme) outl.Add(new RiffNote(n.Note, Math.Max(0, total - (n.Start + n.Length)), n.Length));
            outl.Sort((a, b) => a.Start.CompareTo(b.Start));
            return outl;
        }

        public static List<RiffNote> InvertContour(List<RiffNote> theme, HashSet<int> scale)   // mirror each interval around the first note
        {
            var outl = new List<RiffNote>();
            int pivot = theme[0].Note + 12;
            foreach (var n in theme) outl.Add(new RiffNote(Clamp95(MusicComposer.NearestScale(2 * pivot - (n.Note + 12), scale) - 12), n.Start, n.Length));
            return outl;
        }

        public static List<RiffNote> BorrowMode(List<RiffNote> theme, int tonicPc)   // borrow the parallel mode: major 3rd/6th/7th → minor
        {
            int tp = ((tonicPc % 12) + 12) % 12;
            var outl = new List<RiffNote>();
            foreach (var n in theme)
            {
                int p = n.Note + 12, rel = (((p - tp) % 12) + 12) % 12;
                if (rel == 4 || rel == 9 || rel == 11) p -= 1;
                outl.Add(new RiffNote(Clamp95(p - 12), n.Start, n.Length));
            }
            return outl;
        }

        public static List<RiffNote> Ornament(List<RiffNote> theme, HashSet<int> scale)   // a quick upper-neighbour grace before each longer note
        {
            var outl = new List<RiffNote>();
            int g = Math.Max(2, MusicComposer.Spq / 6);
            foreach (var n in theme)
            {
                if (n.Length > g * 2)
                {
                    outl.Add(new RiffNote(Clamp95(MusicComposer.ScaleStep(n.Note + 12, 1, scale) - 12), n.Start, g));
                    outl.Add(new RiffNote(n.Note, n.Start + g, n.Length - g));
                }
                else outl.Add(n);
            }
            return outl;
        }

        public static List<RiffNote> MotoPerpetuo(List<RiffNote> theme, List<(int root, int quality)> ch, int chordSlices, HashSet<int> scale)
        {   // continuous 16ths rolling up the chord tones (wraps down at the top) — a perpetual-motion variation
            var outl = new List<RiffNote>();
            int total = 0; foreach (var n in theme) total = Math.Max(total, n.Start + n.Length);
            if (total <= 0 || ch == null || ch.Count == 0) return new List<RiffNote>(theme);
            int step = Math.Max(3, MusicComposer.Spq / 4), lo = 50, hi = 74, prev = theme[0].Note + 12;
            for (int s = 0; s < total; s += step)
            {
                var cp = MusicComposer.ChordPcs(ch[Math.Min(s / chordSlices, ch.Count - 1)].root, ch[Math.Min(s / chordSlices, ch.Count - 1)].quality);
                if (cp.Count == 0) continue;
                int next = MusicComposer.NextChordToneUpOpen(prev, cp); if (next > hi) next = MusicComposer.NearestChord(lo, cp);
                outl.Add(new RiffNote(Clamp95(next - 12), s, step)); prev = next;
            }
            return outl;
        }

        // ---- VARIATIONS (modulating build-up): re-state the WHOLE theme in a rising sequence of NEIGHBOURING KEYS (one
        // per repetition, devKeys[r] semitones up = rising fifths), keeping the SAME principal notes (the theme contour
        // transposed) but VARYING THE RHYTHM more and more (VaryRhythm) as the build-up grows. ----
        public static List<RiffNote> BuildVariations(List<RiffNote> theme, int devMult, int themeB, int barSlices, int chordSlices, bool ternary, HashSet<int> scale, int[] devKeys, Random rng)
        {
            var outp = new List<RiffNote>();
            for (int r = 0; r < devMult; r++)
            {
                int off = (devKeys != null && r < devKeys.Length) ? devKeys[r] : 0;
                var repScale = new HashSet<int>(); foreach (var pc in scale) repScale.Add((((pc + off) % 12) + 12) % 12);
                var rep = new List<RiffNote>();
                foreach (var n in theme) { int p = n.Note + off; while (p > 84) p -= 12; while (p < 40) p += 12; rep.Add(new RiffNote(p, n.Start, n.Length)); }
                rep = VaryRhythm(rep, r + 1, repScale, rng);
                int baseSlice = r * themeB * barSlices;
                foreach (var n in rep) outp.Add(new RiffNote(n.Note, baseSlice + n.Start, n.Length));
            }
            return outp;
        }

        // RHYTHM VARIATION — keep the PRINCIPAL (downbeat) notes; on weak notes, increasingly (with `level`) either
        // SUBDIVIDE into the note + a diatonic neighbour, or (level>=2) LENGTHEN a strong note by absorbing the next.
        public static List<RiffNote> VaryRhythm(List<RiffNote> notes, int level, HashSet<int> scale, Random rng)
        {
            var outp = new List<RiffNote>();
            foreach (var n in notes)
            {
                bool strong = (n.Start % MusicComposer.Spq) == 0;
                int half = n.Length / 2;
                if (!strong && n.Length >= MusicComposer.Spq / 2 && half >= 2 && rng.Next(100) < 20 + 20 * level)
                {
                    outp.Add(new RiffNote(n.Note, n.Start, half));
                    int nb = MusicComposer.ScaleStep(n.Note + 12, rng.Next(2) == 0 ? 1 : -1, scale) - 12;
                    outp.Add(new RiffNote(Math.Max(0, Math.Min(95, nb)), n.Start + half, n.Length - half));
                }
                else outp.Add(n);
            }
            if (level >= 2)
            {
                var merged = new List<RiffNote>();
                for (int i = 0; i < outp.Count; i++)
                {
                    if (i + 1 < outp.Count && (outp[i].Start % MusicComposer.Spq) == 0 && (outp[i + 1].Start % MusicComposer.Spq) != 0 && outp[i + 1].Length <= MusicComposer.Spq / 2 && rng.Next(100) < 18)
                    { merged.Add(new RiffNote(outp[i].Note, outp[i].Start, outp[i].Length + outp[i + 1].Length)); i++; }
                    else merged.Add(outp[i]);
                }
                outp = merged;
            }
            return outp;
        }

        public static void AddAt(List<RiffNote> mel, RiffNote n, int off, int total)
        { int abs = n.Start + off; if (abs >= 0 && abs < total) mel.Add(new RiffNote(n.Note, abs, Math.Min(n.Length, total - abs))); }

        // Retune the LAST note of a phrase to the nearest pitch of a target pitch-class — a cadential "question"
        // (e.g. the supertonic, left open) or "answer" (the tonic, resolved) ending. No-op on an empty phrase.
        public static void EndOn(List<RiffNote> notes, int targetPc, HashSet<int> scale)
        {
            if (notes == null || notes.Count == 0) return;
            var ln = notes[notes.Count - 1];
            int p = MusicComposer.NearestPc(ln.Note + 12, ((targetPc % 12) + 12) % 12);
            notes[notes.Count - 1] = new RiffNote(Math.Max(0, Math.Min(95, p - 12)), ln.Start, ln.Length);
        }

        // The lead's MIDI pitch sounding at slice `at` (the covering note, else the last preceding one).
        public static int LeadPitchAt(List<RiffNote> notes, int at)
        {
            int best = -1, bestStart = int.MinValue;
            foreach (var n in notes)
            {
                if (n.Start <= at && at < n.Start + n.Length) return n.Note + 12;
                if (n.Start <= at && n.Start > bestStart) { bestStart = n.Start; best = n.Note + 12; }
            }
            return best >= 0 ? best : (notes.Count > 0 ? notes[0].Note + 12 : 72);
        }

        // A COUNTER voice for a "together" passage that sings WITH the lead but INDEPENDENTLY: its own rhythm (a mix of
        // longer values per bar, not the lead's) and varied motion (diatonic 3rd/6th/octave below, sometimes a 3rd above,
        // with a contrary-motion bias) — never strict parallel. Resolves its final note onto the local tonic.
        public static List<RiffNote> BuildTogetherCounter(List<RiffNote> lead, List<(int root, int quality)> chords, int themeB, int barSlices, int localTonicPc, HashSet<int> scale, Random rng)
        {
            var outp = new List<RiffNote>();
            if (lead == null || lead.Count == 0) return outp;
            // bar rhythms (clamped to the bar for 3/4·6/8). Dropped the dotted-quarter+eighth {36,12,48} (jerky "4+8");
            // added REGULAR-TRIPLET beats (3×8 per beat) for a flowing, even counter instead of the lilt.
            int[][] pats = { new[] { 96 }, new[] { 48, 48 }, new[] { 48, 24, 24 }, new[] { 24, 24, 48 }, new[] { 72, 24 }, new[] { 24, 48, 24 }, new[] { 24, 8, 8, 8, 24, 24 }, new[] { 8, 8, 8, 24, 8, 8, 8, 24 } };
            for (int b = 0; b < themeB; b++)
            {
                var pat = pats[rng.Next(pats.Length)];
                var ch = chords.Count > 0 ? chords[Math.Min(b, chords.Count - 1)] : (localTonicPc, 0);
                var pcs = MusicComposer.ChordPcs(ch.Item1, ch.Item2);
                int pos = 0;
                for (int k = 0; k < pat.Length && pos < barSlices; k++)
                {
                    int dur = Math.Min(pat[k], barSlices - pos);
                    int at = b * barSlices + pos;
                    int leadP = LeadPitchAt(lead, at);
                    // CONSONANT second voice: a CHORD TONE about a 3rd/6th (sometimes an octave) below the lead, so a LONG
                    // held note stays consonant with the harmony even as the lead moves. If the picked tone still clashes
                    // with the lead (a 2nd / 7th / tritone), take a chord tone a clear 3rd+ below instead.
                    int aim = leadP - (rng.Next(2) == 0 ? 3 : 5) - (rng.Next(3) == 0 ? 12 : 0);
                    int p = pcs.Count > 0 ? MusicComposer.NearestChord(aim, pcs) : MusicComposer.ShiftScale(leadP, -2, scale);
                    int ic = Math.Abs(p - leadP) % 12;
                    if (pcs.Count > 0 && (ic == 1 || ic == 2 || ic == 6 || ic == 10 || ic == 11)) p = MusicComposer.NextChordToneDownOpen(leadP, pcs);
                    while (p > 79) p -= 12; while (p < 45) p += 12;   // sensible lower register for the second voice
                    outp.Add(new RiffNote(Math.Max(0, Math.Min(95, p - 12)), at, dur));
                    pos += dur;
                }
            }
            var ln = outp[outp.Count - 1];                            // resolve the final note onto the local tonic
            int fp = MusicComposer.NearestPc(ln.Note + 12, ((localTonicPc % 12) + 12) % 12);
            while (fp > 67) fp -= 12; while (fp < 48) fp += 12;
            outp[outp.Count - 1] = new RiffNote(Math.Max(0, Math.Min(95, fp - 12)), ln.Start, ln.Length);
            return outp;
        }

        // DIALOGUE between the two instruments across the WHOLE body (not just a climax at the end): the lead STATES the
        // theme's first bar alone (the "question"); from the next bar the 2nd instrument joins, then the two converse —
        // "parfois l'un, parfois l'autre, parfois les deux". Per bar in [themeStartBar, endBar), a seeded role:
        //   L  = lead only          → counter rests this bar (the melody's backbone);
        //   C  = counter answers    → the bar's melody is HANDED OFF to the counter (moved from lead → counter, lead rests),
        //                             a literal call/response where the OTHER timbre takes the phrase (line stays continuous);
        //   B  = both together      → the lead keeps its bar and the counter adds a consonant 3rd/6th-below second voice.
        // ADDITIVE & non-destructive: a bar that ALREADY carries counter material (a restate descant / the climax
        // counterpoint) is left untouched — those stay as composed; only the previously-empty body gets the conversation.
        public static void BuildDialogue(List<RiffNote> mel, List<RiffNote> counter, int themeStartBar, int endBar,
            int barSlices, List<(int root, int quality)> prog, int tonicPc, HashSet<int> scale, Random rng)
        {
            if (mel == null || counter == null || barSlices <= 0 || rng == null) return;
            if (themeStartBar < 0) themeStartBar = 0;
            int n = endBar - themeStartBar;
            if (n < 3) return;

            // role plan: bar 0 (first theme bar) = lead alone; bar 1 = the counter's immediate answer; then a lead-dominant
            // mix (L 50% / B 30% / C 20%) with no two counter-solos in a row (so the lead never vanishes for long).
            var roles = new char[n];
            roles[0] = 'L';
            char prev = 'L';
            for (int rel = 1; rel < n; rel++)
            {
                char role;
                if (rel == 1) role = 'C';
                else { int r = rng.Next(100); role = r < 50 ? 'L' : (r < 80 ? 'B' : 'C'); if (role == 'C' && prev == 'C') role = 'B'; }
                roles[rel] = role; prev = role;
            }

            for (int rel = 1; rel < n; rel++)
            {
                char role = roles[rel];
                if (role == 'L') continue;
                int b = themeStartBar + rel;
                int barStart = b * barSlices, barEnd = barStart + barSlices;
                bool hasCounter = false;
                foreach (var c in counter) if (c.Start >= barStart && c.Start < barEnd) { hasCounter = true; break; }
                if (hasCounter) continue;   // restate descant / climax counterpoint already here — leave it
                var barLead = mel.FindAll(x => x.Start >= barStart && x.Start < barEnd);
                if (barLead.Count == 0) continue;
                var chord = (b >= 0 && b < prog.Count) ? prog[b] : (((tonicPc % 12) + 12) % 12, 0);
                if (role == 'C')   // HANDOFF: the other instrument takes this bar; the lead rests
                {
                    foreach (var x in barLead) counter.Add(new RiffNote(x.Note, x.Start, x.Length) { Bend = x.Bend });
                    mel.RemoveAll(x => x.Start >= barStart && x.Start < barEnd);
                }
                else AddBarHarmony(counter, barLead, chord, scale, rng);   // BOTH: consonant 2nd voice under the lead
            }
        }

        // One bar of consonant second voice UNDER the lead: each structural lead note gets a chord tone a 3rd/6th (sometimes
        // an octave) below — same interval grammar as BuildTogetherCounter, but riding the lead's own rhythm and resolving
        // dissonances down to a clear chord tone. Short ornaments (< a triplet-8th) are skipped so the voice stays singable.
        static void AddBarHarmony(List<RiffNote> counter, List<RiffNote> barLead, (int root, int quality) chord, HashSet<int> scale, Random rng)
        {
            var pcs = MusicComposer.ChordPcs(chord.root, chord.quality);
            foreach (var n in barLead)
            {
                if (n.Length < 8) continue;
                int leadP = n.Note + 12;
                int aim = leadP - (rng.Next(2) == 0 ? 3 : 5) - (rng.Next(4) == 0 ? 12 : 0);
                int p = pcs.Count > 0 ? MusicComposer.NearestChord(aim, pcs) : MusicComposer.ShiftScale(leadP, -2, scale);
                int ic = Math.Abs(p - leadP) % 12;
                if (pcs.Count > 0 && (ic == 1 || ic == 2 || ic == 6 || ic == 10 || ic == 11)) p = MusicComposer.NextChordToneDownOpen(leadP, pcs);
                while (p > 79) p -= 12; while (p < 50) p += 12;
                counter.Add(new RiffNote(Clamp95(p - 12), n.Start, n.Length) { Bend = n.Bend });
            }
        }

        // Generate an EXTRA independent melodic voice for a FINISHED arrangement, over its chord trame — for the UI's
        // "add a melodic line" (a new instrument that composes itself, respecting the existing harmony/structure).
        // Reuses BuildTogetherCounter (consonant, contrary-motion, resolves to the tonic) against the current lead, then
        // smooths leaps + lightly phrases it. One chord per bar (handles ChordsPerBar). `seed` varies it per added line.
        public static List<RiffNote> BuildExtraVoice(ComposedArrangement arr, List<RiffNote> lead, int seed)
        {
            var outp = new List<RiffNote>();
            if (arr == null || arr.Chords == null || arr.Chords.Count == 0 || arr.BarSlices <= 0) return outp;
            int cpb = Math.Max(1, arr.ChordsPerBar);
            int totalBars = arr.TotalBars > 0 ? arr.TotalBars : (arr.Chords.Count / cpb);
            if (totalBars <= 0) return outp;
            var prog = new List<(int root, int quality)>();
            for (int b = 0; b < totalBars; b++) { int ci = Math.Min(b * cpb, arr.Chords.Count - 1); prog.Add((arr.Chords[ci].Root, arr.Chords[ci].Quality)); }
            var scale = MusicComposer.ScaleSet(arr.TonicPc, MusicalMode.Scale(arr.FullMode));
            int spq = arr.SlicesPerQuarter > 0 ? arr.SlicesPerQuarter : 24;
            var line = BuildTogetherCounter(lead ?? new List<RiffNote>(), prog, totalBars, arr.BarSlices, arr.TonicPc, scale, new Random(seed));
            RecipeRenderer.SmoothLeaps(line);
            RecipeRenderer.Breathe(line, arr.BarSlices, spq, 1, new Random(seed + 1));
            return line;
        }

        // Transpose a whole line by `semis`, then octave-correct the WHOLE line TOGETHER (one shift for all notes) so the
        // CONTOUR and every interval are preserved — never the per-note octave wrapping that breaks a melody apart.
        public static List<RiffNote> TransposeMelLocal(List<RiffNote> m, int semis)
        {
            var o = new List<RiffNote>();
            if (m == null || m.Count == 0) return o;
            int lo = int.MaxValue, hi = int.MinValue;
            foreach (var n in m) { int p = n.Note + semis; if (p < lo) lo = p; if (p > hi) hi = p; }
            int octShift = 0;
            while (lo + octShift < 40) octShift += 12;
            while (hi + octShift > 84) octShift -= 12;
            foreach (var n in m) o.Add(new RiffNote(Math.Max(0, Math.Min(95, n.Note + semis + octShift)), n.Start, n.Length));
            return o;
        }

        public static int Clamp95(int p) => Math.Max(0, Math.Min(95, p));

        // ---- "APPLIQUER LE THÈME": take an (edited) theme and RE-DERIVE the derived sections, using the stored trame.
        // Returns (riffId → new note list) to overwrite the timeline riffs. Theme = verbatim (on the lead). Ré-expo = the
        // ANSWER (RefitTheme onto the same chords, resolved — lives on the COUNTER track). Variations = BuildVariations
        // (the theme through the modulating keys, rhythm-varied). Recap = RefitTheme (conclusive). Notes RELATIVE to each
        // section start. ----
        public static List<(Guid riffId, List<RiffNote> notes)> RegenerateFromTheme(ComposedArrangement arr, List<RiffNote> newTheme)
        {
            var outp = new List<(Guid, List<RiffNote>)>();
            if (arr == null) return outp;
            var scale = MusicComposer.ScaleSet(arr.TonicPc, MusicalMode.Scale(arr.FullMode));
            int cs = arr.ChordSlices, bs = arr.BarSlices;
            var theme = (newTheme != null && newTheme.Count > 0) ? newTheme : arr.Theme;
            if (theme == null) return outp;

            var secTheme = arr.SectionByRole("theme");
            var secReexpo = arr.SectionByRole("reexpo");
            var secRecap = arr.SectionByRole("recap");

            // PROTECTED sections (user ticked "ne pas écraser") are left untouched.
            if (secTheme != null && !secTheme.Protected && secTheme.MelodyRiffId != Guid.Empty)
                outp.Add((secTheme.MelodyRiffId, new List<RiffNote>(theme)));                                  // the question (verbatim)
            if (secReexpo != null && !secReexpo.Protected && secReexpo.MelodyRiffId != Guid.Empty)
                outp.Add((secReexpo.MelodyRiffId, RefitTheme(theme, arr.SectionChords(secReexpo), cs, scale))); // the answer (counter track)
            if (secRecap != null && !secRecap.Protected && secRecap.MelodyRiffId != Guid.Empty)
            {
                var r = RefitTheme(theme, arr.SectionChords(secRecap), cs, scale);
                if (r.Count > 0) { var ln = r[r.Count - 1]; int t = MusicComposer.NearestPc(ln.Note + 12, arr.TonicPc); r[r.Count - 1] = new RiffNote(Math.Max(0, Math.Min(95, t - 12)), ln.Start, ln.Length); }
                outp.Add((secRecap.MelodyRiffId, r));
            }
            // DEVELOPMENT: regenerate the WHOLE development once (deterministic), then hand each dev SECTION its own
            // slice → one riff per variation. Also works if a legacy project carries a single multi-bar dev section.
            var devSecs = new List<ArrSection>();
            foreach (var s in arr.Sections) if (s.Role == "dev") devSecs.Add(s);
            if (devSecs.Count > 0)
            {
                devSecs.Sort((p, q) => p.StartBar.CompareTo(q.StartBar));
                int devStartBar = devSecs[0].StartBar, totalDevBars = 0;
                foreach (var s in devSecs) totalDevBars += s.Bars;
                int devMult = arr.ThemeBars > 0 ? Math.Max(1, totalDevBars / arr.ThemeBars) : 1;
                var full = BuildVariations(theme, devMult, arr.ThemeBars, bs, cs, arr.Ternary, scale, (arr.DevKeys ?? new List<int>()).ToArray(), new Random(arr.Seed));
                foreach (var sec in devSecs)
                {
                    if (sec.Protected || sec.MelodyRiffId == Guid.Empty) continue;
                    int lo = (sec.StartBar - devStartBar) * bs, hi = lo + sec.Bars * bs;
                    var slice = new List<RiffNote>();
                    foreach (var n in full) if (n.Start >= lo && n.Start < hi) slice.Add(new RiffNote(n.Note, n.Start - lo, Math.Min(n.Length, hi - n.Start)));
                    outp.Add((sec.MelodyRiffId, slice));
                }
            }
            return outp;
        }

        // ---- CHORD-LANE editing: REBUILD the accompaniment + bass from the (edited) trame. Each chord plays its
        // section's degree-MOTIF (re-rooted, coloured by the chord quality, voiced, optionally spread); a chord with no
        // motif uses a simple built-in broken figure. Returns FULL-piece note lists for the caller to redistribute. ----
        public static (List<RiffNote> accomp, List<RiffNote> bass) RebuildHarmony(ComposedArrangement arr)
        {
            if (arr == null) return (new List<RiffNote>(), new List<RiffNote>());
            var prog = new List<(int root, int quality)>();
            foreach (var c in arr.Chords) prog.Add((c.Root, c.Quality));
            var accNotes = new List<RiffNote>();
            int cs = arr.ChordSlices, bs = Math.Max(1, arr.BarSlices), fastN = arr.Ternary ? MusicComposer.Spq / 3 : MusicComposer.Spq / 2;
            var keyScale = MusicComposer.ScaleSet(arr.TonicPc, MusicalMode.Scale(arr.FullMode));
            var mv = new MotifVoices();
            for (int ci = 0; ci < arr.Chords.Count; ci++)
            {
                var c = arr.Chords[ci];
                var pcs = MusicComposer.ChordPcs(c.Root, c.Quality);
                if (pcs.Count == 0) continue;
                int chordStart = ci * cs;
                var motif = MotifForChord(arr, ci);
                if (MotifValid(motif))
                    RealizeMotif(accNotes, motif, c.Root, c.Quality, keyScale, ci, chordStart, cs, bs, mv);
                else   // no motif → a plain built-in broken chord (root · 3rd up · 5th up)
                {
                    int a = 48 + (((c.Root % 12) + 12) % 12), b = MusicComposer.NextChordToneUpOpen(a, pcs), t = MusicComposer.NextChordToneUpOpen(b, pcs);
                    if (cs >= 3 * fastN) { AddNote(accNotes, a, chordStart, fastN); AddNote(accNotes, b, chordStart + fastN, fastN); AddNote(accNotes, t, chordStart + 2 * fastN, cs - 2 * fastN); }
                    else AddNote(accNotes, t, chordStart, cs);
                }
            }
            return (accNotes, RebuildBass(arr, prog, keyScale, cs, bs));
        }

        // PREVIEW for the motif editor: realize on the TONIC chord of the key (root position) so the user can audition it.
        public static int RealizeDegreePreview(int degree, int tonicPc, int fullMode)
        {
            int tp = ((tonicPc % 12) + 12) % 12;
            return RealizeDegree(Math.Max(1, degree), 48 + tp, DiatonicChord(tp, fullMode, 0).quality,
                                 MusicComposer.ScaleSet(tp, MusicalMode.Scale(fullMode)), false);   // sounding MIDI on the tonic chord
        }
        public static List<RiffNote> RealizeMotifPreview(ChordMotif motif, int tonicPc, int fullMode)
        {
            var notes = new List<RiffNote>();
            if (!MotifValid(motif)) return notes;
            int tp = ((tonicPc % 12) + 12) % 12;
            var scale = MusicComposer.ScaleSet(tp, MusicalMode.Scale(fullMode));
            int tq = DiatonicChord(tp, fullMode, 0).quality;
            foreach (var mn in motif.Notes)
                AddNote(notes, RealizeDegree(Math.Max(1, mn.Degree), 48 + tp, tq, scale, motif.OpenVoicing), mn.Start, mn.Length);
            return notes;
        }

        // The BASS line. If any per-section BASS motif is defined, realize it per chord in the BASS register (degree 1 =
        // the chord root — the pedal default); a chord whose section has no bass motif keeps a single held root. With NO
        // bass motif anywhere → the plain root pedal (HisaishiComposer.PedalBass), unchanged from before.
        public static List<RiffNote> RebuildBass(ComposedArrangement arr, List<(int root, int quality)> prog, HashSet<int> keyScale, int cs, int bs)
        {
            bool any = false;
            if (arr.BassMotifs != null)
                for (int ci = 0; ci < arr.Chords.Count && !any; ci++)
                    if (MotifValid(MotifForChordIn(arr, ci, arr.BassMotifs, null))) any = true;
            if (!any) return HisaishiComposer.PedalBass(prog, cs, arr.TonicPc, false).Notes;

            var notes = new List<RiffNote>();
            var bv = new MotifVoices();
            for (int ci = 0; ci < arr.Chords.Count; ci++)
            {
                var c = arr.Chords[ci];
                if (MusicComposer.ChordPcs(c.Root, c.Quality).Count == 0) continue;
                var motif = MotifForChordIn(arr, ci, arr.BassMotifs, null);
                if (MotifValid(motif)) RealizeMotif(notes, motif, c.Root, c.Quality, keyScale, ci, ci * cs, cs, bs, bv, 36);  // bass register
                else notes.Add(new RiffNote((((c.Root % 12) + 12) % 12) + 24, ci * cs, cs));   // held root pedal (~MIDI 36-47)
            }
            return notes;
        }

        public static void AddNote(List<RiffNote> notes, int midi, int at, int dur) { int p = midi - 12; if (p >= 0 && p < 96 && dur > 0) notes.Add(new RiffNote(p, at, dur)); }
        public static bool MotifValid(ChordMotif m) => m != null && m.Notes != null && m.Notes.Count > 0;
        public static ChordMotif MotifGet(Dictionary<string, ChordMotif> dict, string key) => (dict != null && dict.TryGetValue(key, out var m) && MotifValid(m)) ? m : null;

        // The degree-motif applicable to chord `ci`, looked up in `dict` (the accompaniment Motifs or the BassMotifs):
        // a variation OVERRIDE ("dev:v") → the dev base ("dev") → the section role → `legacy` → null (built-in / pedal).
        public static ChordMotif MotifForChordIn(ComposedArrangement arr, int ci, Dictionary<string, ChordMotif> dict, ChordMotif legacy)
        {
            int cpb = Math.Max(1, arr.ChordsPerBar);
            if (arr.Sections != null)
                foreach (var sec in arr.Sections)
                {
                    int sStart = sec.StartBar * cpb, sEnd = (sec.StartBar + sec.Bars) * cpb;
                    if (ci < sStart || ci >= sEnd) continue;
                    if (sec.Role == "dev" && arr.ThemeBars > 0)
                    {
                        int devStart0 = arr.DevStartBar() * cpb, repChords = arr.ThemeBars * cpb;
                        int vIdx = repChords > 0 ? (ci - devStart0) / repChords : 0;
                        return MotifGet(dict, "dev:" + vIdx) ?? MotifGet(dict, "dev") ?? legacy;
                    }
                    return MotifGet(dict, sec.Role) ?? legacy;
                }
            return legacy;
        }
        public static ChordMotif MotifForChord(ComposedArrangement arr, int ci) => MotifForChordIn(arr, ci, arr.Motifs, MotifValid(arr.Motif) ? arr.Motif : null);

        // ---- Render a degree-MOTIF stamped on EVERY chord of the trame (the editor's "apply to the whole line"). ----
        public static List<RiffNote> RenderMotifLine(ComposedArrangement arr, ChordMotif motif)
        {
            var notes = new List<RiffNote>();
            if (arr == null || !MotifValid(motif)) return notes;
            int cs = arr.ChordSlices, bs = Math.Max(1, arr.BarSlices);
            var keyScale = MusicComposer.ScaleSet(arr.TonicPc, MusicalMode.Scale(arr.FullMode));
            var mv = new MotifVoices();
            for (int ci = 0; ci < arr.Chords.Count; ci++)
            {
                var c = arr.Chords[ci];
                if (MusicComposer.ChordPcs(c.Root, c.Quality).Count == 0) continue;
                RealizeMotif(notes, motif, c.Root, c.Quality, keyScale, ci, ci * cs, cs, bs, mv);
            }
            return notes;
        }

        /// <summary>Realize a degree-MOTIF over an explicit chord PROGRESSION (re-rooted+coloured per chord), without
        /// needing a ComposedArrangement — used at INITIAL compose to render a curated JSON accompaniment/bass motif on
        /// the trame. <paramref name="reg"/> = register seed (48 accompaniment / 36 bass). Same realizer as the editor.</summary>
        public static List<RiffNote> RenderMotifOverProgression(ChordMotif motif, List<(int root, int quality)> prog,
                                                                int tonicPc, int fullMode, int cs, int bs, int reg = 48)
        {
            var notes = new List<RiffNote>();
            if (!MotifValid(motif) || prog == null) return notes;
            var keyScale = MusicComposer.ScaleSet(((tonicPc % 12) + 12) % 12, MusicalMode.Scale(fullMode));
            var mv = new MotifVoices();
            for (int ci = 0; ci < prog.Count; ci++)
            {
                var c = prog[ci];
                if (MusicComposer.ChordPcs(c.root, c.quality).Count == 0) continue;
                RealizeMotif(notes, motif, c.root, c.quality, keyScale, ci, ci * cs, cs, bs, mv, reg);
            }
            return notes;
        }

        // Voice-leading memory threaded across chords (Morph on): PcMem keeps each pitch-class at a STABLE octave once it
        // first appears, so a tone common to two chords is literally HELD at the same pitch and the rest move minimally —
        // the chord renverses itself, with no upward/downward drift. PrevPitch seeds a NEW pitch-class near the current
        // register. (The drawn melodic contour may reorganize — that is the cost of true note-by-note voice-leading.)
        public class MotifVoices { public Dictionary<int, int> PcMem = new Dictionary<int, int>(); public int PrevPitch = 48; public bool HasPrev; public int PrevBass = 48; public bool HasBass; }

        // The chord's coloured tones as pitch-classes in STACK order (root, 3rd, 5th, [7th]…), de-duplicated.
        static List<int> ChordStackPcs(int rootPc, int quality)
        {
            int rp = ((rootPc % 12) + 12) % 12;
            var pcs = new List<int>();
            var cn = PatternGenerator.ChordNotes(rp, 4, quality, 0);
            if (cn != null) foreach (var m in cn) { int pc = ((m % 12) + 12) % 12; if (!pcs.Contains(pc)) pcs.Add(pc); }
            if (pcs.Count == 0) pcs.Add(rp);
            return pcs;
        }

        // Cumulative scale-steps of the CHORD TONES going UP from startPc in PITCH order ([0]=startPc, [1]=next chord tone
        // up, …). Walks the scale and marks chord tones, so a 9th/11th/13th folds into its real ascending position.
        static List<int> ChordToneStepsFrom(int startPc, HashSet<int> ctPcs, int count, HashSet<int> scale)
        {
            var res = new List<int> { 0 };
            int m = (((startPc % 12) + 12) % 12) + 48, steps = 0, g = 0;
            while (res.Count <= count && g++ < 160)
            {
                m = MusicComposer.ScaleStep(m, 1, scale); steps++;
                if (ctPcs.Contains((((m % 12) + 12) % 12))) res.Add(steps);
            }
            return res;
        }

        // AJUSTEMENT MODAL — the realized SCALE-STEP above the bass for motif degree `d`. The k-th chord tone above the
        // ROOT (in pitch order) maps to the k-th chord tone above the BASS (so a chord-tone degree ALWAYS lands on a chord
        // tone — any chord, any inversion, any octave); a passing degree keeps the same scale-distance BELOW the next chord
        // tone (so it approaches it, e.g. D-F-G in B D F G). Bass == root → identity. `ctPcs` = the chord's pitch classes.
        static int ModalStep(int d, int rootPc, int bassPc, HashSet<int> ctPcs, HashSet<int> scale)
        {
            int r = Math.Max(0, d - 1), need = r + 2;
            var S = ChordToneStepsFrom(rootPc, ctPcs, need, scale);   // chord tones above the ROOT
            var C = ChordToneStepsFrom(bassPc, ctPcs, need, scale);   // chord tones above the BASS
            int j = 0; while (j + 1 < S.Count && S[j + 1] <= r) j++;
            if (S[j] == r) return C[Math.Min(j, C.Count - 1)];                           // chord tone → its inversion chord tone
            int ju = Math.Min(j + 1, Math.Min(S.Count - 1, C.Count - 1));
            return C[ju] - (S[ju] - r);                                                  // passing → approach the next chord tone
        }

        // Realize one degree-motif onto ONE chord: each MotifNote → a scale degree above the chord root, chord-tone
        // degrees coloured by the quality; the pattern repeats every motif.Bars bars (re-rooted per chord). With Spread
        // (éclatement), notes that strike together are rolled into a quick arpeggio instead of a block.
        static void RealizeMotif(List<RiffNote> outNotes, ChordMotif motif, int rootPc, int quality, HashSet<int> keyScale, int ci, int chordStart, int cs, int bs, MotifVoices vs, int reg = 48)
        {
            int rp = ((rootPc % 12) + 12) % 12;
            int rootMidi = reg + rp;   // close-position register (48 accompaniment / 36 bass); OCTAVE matters only for the seed / Morph off
            int patBars = Math.Max(1, motif.Bars), patBar = ci % patBars;
            int lo = patBar * bs, hi = lo + bs;
            // gather this bar's notes (degree + CLOSE pitch + the OPEN lift +12 on inner chord tones)
            var src = new List<(int at, int len, int deg, int close, int openDelta)>();
            foreach (var mn in motif.Notes)
            {
                if (mn.Start < lo || mn.Start >= hi) continue;
                int rel = mn.Start - lo, len = Math.Max(1, Math.Min(mn.Length, cs - rel));
                int close = RealizeDegree(mn.Degree, rootMidi, quality, keyScale, false);
                int openD = RealizeDegree(mn.Degree, rootMidi, quality, keyScale, motif.OpenVoicing) - close;
                src.Add((chordStart + rel, len, mn.Degree, close, openD));
            }
            // process bass-first within a strike so the lowest tone seeds the register, then the rest stack/hold above it
            src.Sort((a, b) => a.at != b.at ? a.at.CompareTo(b.at) : a.close.CompareTo(b.close));
            var realized = new List<(int at, int len, int snd)>();
            if (motif.SmartVoice)
            {
                // SMART VOICE-LEAD ("ajustement modal"): voice the chord in the INVERSION whose bass is NEAREST the previous
                // bass (a common tone stays put), then re-map the degrees LINEARLY above that bass — with a +1 SHIFT past a
                // threshold so the chord tones realign on the renversement (1st inv: degrees > 3 get +1; 2nd inv: > 2; root:
                // none). The drawn shape rides the renversement; the bass keeps you oriented. (threshold repeats per octave.)
                var pcs = ChordStackPcs(rp, quality);             // FULL stack (root/3/5/7/9… or root/sus/5th)
                var ctSet = new HashSet<int>(); foreach (var p in pcs) ctSet.Add((((p) % 12) + 12) % 12);
                int bassPitch;
                if (!vs.HasBass) bassPitch = MusicComposer.NearestPc(reg + 2, pcs[0]);          // first chord → root position
                else
                {
                    int best = int.MaxValue; bassPitch = vs.PrevBass;
                    foreach (var p in pcs)
                    {
                        int cand = MusicComposer.NearestPc(vs.PrevBass, p), dd = Math.Abs(cand - vs.PrevBass);
                        if (dd < best) { best = dd; bassPitch = cand; }   // renversement = nearest chord tone
                    }
                }
                while (bassPitch < reg - 6) bassPitch += 12; while (bassPitch > reg + 14) bassPitch -= 12;   // bound the bass (no drift)
                int bassPc = (((bassPitch) % 12) + 12) % 12;
                foreach (var s in src)
                    realized.Add((s.at, s.len, MusicComposer.ShiftScale(bassPitch, ModalStep(Math.Max(1, s.deg), rp, bassPc, ctSet, keyScale), keyScale)));
                vs.PrevBass = bassPitch; vs.HasBass = true;
            }
            else
            // VOICE-LEADING (Morph on): each note sounds at the STABLE octave of its pitch-class — held if the pc already
            // sounded (common tone), else placed at the octave nearest the current register (a new voice). Open voicing is
            // added on top (it lifts inner chord tones). Morph off = the literal degrees anchored at a fixed register.
            foreach (var s in src)
            {
                int pc = ((s.close % 12) + 12) % 12, pitch;
                if (motif.Morph)
                {
                    if (!vs.PcMem.TryGetValue(pc, out pitch))
                    {
                        pitch = MusicComposer.NearestPc(vs.HasPrev ? vs.PrevPitch : reg + 2, pc);
                        while (pitch < reg - 6) pitch += 12; while (pitch > reg + 18) pitch -= 12;   // bound a new voice (no drift)
                        vs.PcMem[pc] = pitch;
                    }
                    vs.PrevPitch = pitch; vs.HasPrev = true;
                }
                else pitch = s.close;
                realized.Add((s.at, s.len, pitch + s.openDelta));
            }
            if (motif.Spread && realized.Count > 1)
            {
                realized.Sort((x, y) => x.at != y.at ? x.at.CompareTo(y.at) : x.snd.CompareTo(y.snd));
                int roll = Math.Max(2, MusicComposer.Spq / 8);
                for (int i = 1; i < realized.Count; i++)
                    if (realized[i].at == realized[i - 1].at) realized[i] = (realized[i - 1].at + roll, Math.Max(1, realized[i].len - roll), realized[i].snd);
            }
            foreach (var r in realized) AddNote(outNotes, r.snd, r.at, r.len);
        }

        // A degree D (1 = chord root, scale steps up over 2 octaves) → a sounding MIDI pitch for (rootPc, quality):
        // D-1 scale steps above the root; chord-tone degrees (1,3,5,7,9,11,13) snap to the chord's actual COLOURED tone
        // when it exists (a minor 3rd, a maj7/dom7/min7 7th, an add9's 9th…), else stay a scale passing tone.
        // interval bands (semitones above the root) for the chord-tone positions root/3rd/5th/7th/9th/11th/13th
        static readonly int[] DegBandLo = { 0, 3, 6, 9, 13, 16, 20 };
        static readonly int[] DegBandHi = { 0, 4, 8, 11, 15, 18, 22 };
        static int RealizeDegree(int degree, int rootMidi, int quality, HashSet<int> keyScale, bool open)
        {
            int D = Math.Max(1, degree);
            int rp = ((rootMidi % 12) + 12) % 12;
            int p = MusicComposer.ShiftScale(rootMidi, D - 1, keyScale);   // D-1 scale steps above the chord root (in its chosen register)
            if ((D % 2) == 1)   // a chord-tone degree → colour it with the chord's ACTUAL tone at that position, IF it exists
            {
                int pos = (D - 1) / 2;
                if (pos < DegBandLo.Length)
                {
                    var cn = PatternGenerator.ChordNotes(rp, 4, quality, 0);   // the chord's tones
                    int found = -1;
                    if (cn != null)
                        foreach (var m in cn)
                        {
                            int iv = ((((m % 12) - rp) % 12) + 12) % 12;        // interval class above the root
                            if (iv >= DegBandLo[pos] && iv <= DegBandHi[pos]) { found = iv; break; }
                            if (iv + 12 >= DegBandLo[pos] && iv + 12 <= DegBandHi[pos]) { found = iv + 12; break; }
                        }
                    if (found >= 0)
                    {
                        // OPEN VOICING: once the note is found, raise the INNER voices (3rd / 5th / 7th, first octave)
                        // an octave so the chord opens out — the root stays in the bass, the upper tones jump up so the
                        // gaps become 10ths/12ths instead of stacked 3rds (any triadic motif spreads, not just 3rd+7th).
                        if (open && found < 12 && pos >= 1 && pos <= 3) found += 12;
                        p = rootMidi + found;   // the coloured chord tone (e.g. b3 on a minor, maj7/b7, add9's 9th)
                    }
                    // else: the chord has no tone at this position (a 7th on a triad…) → keep the scale tone (passing)
                }
            }
            return p;
        }

        // ---- AUTO-TRANSPOSE (chord lane, "auto transpose" ON): re-fit the EXISTING melody/counter notes onto the new
        // trame (transpose each to the nearest chord tone of its bar's chord) — NOT a theme re-derivation. The development
        // is refit rep-by-rep in its own (modulating) key. Protected sections are left untouched. Reads current notes via
        // `getNotes`; returns (riffId → refit notes). ----
        public static List<(Guid riffId, List<RiffNote> notes)> RefitMelodyToTrame(ComposedArrangement arr, Func<Guid, List<RiffNote>> getNotes)
        {
            var outp = new List<(Guid, List<RiffNote>)>();
            if (arr == null) return outp;
            int cpb = Math.Max(1, arr.ChordsPerBar), cs = arr.ChordSlices;
            var homeScale = MusicComposer.ScaleSet(arr.TonicPc, MusicalMode.Scale(arr.FullMode));
            foreach (var sec in arr.Sections)
            {
                if (sec.Protected || sec.MelodyRiffId == Guid.Empty) continue;
                var cur = getNotes(sec.MelodyRiffId);
                if (cur == null || cur.Count == 0) continue;
                if (sec.Role == "dev" && arr.ThemeBars > 0)
                {
                    int repSlices = arr.ThemeBars * arr.BarSlices, repChords = arr.ThemeBars * cpb, reps = Math.Max(1, sec.Bars / arr.ThemeBars);
                    int vBase = arr.DevStartBar() >= 0 ? (sec.StartBar - arr.DevStartBar()) / arr.ThemeBars : 0;   // GLOBAL variation index of this section
                    var devChords = arr.SectionChords(sec);
                    var refit = new List<RiffNote>();
                    for (int rp = 0; rp < reps; rp++)
                    {
                        int off = (arr.DevKeys != null && (vBase + rp) < arr.DevKeys.Count) ? arr.DevKeys[vBase + rp] : 0;
                        var repScale = new HashSet<int>(); foreach (var pc in homeScale) repScale.Add((((pc + off) % 12) + 12) % 12);
                        int from = rp * repChords, len = Math.Min(repChords, devChords.Count - from);
                        var repCh = (from >= 0 && len > 0) ? devChords.GetRange(from, len) : new List<(int, int)>();
                        var repNotes = new List<RiffNote>();
                        foreach (var n in cur) if (n.Start >= rp * repSlices && n.Start < (rp + 1) * repSlices) repNotes.Add(new RiffNote(n.Note, n.Start - rp * repSlices, n.Length));
                        foreach (var n in RefitTheme(repNotes, repCh, cs, repScale)) refit.Add(new RiffNote(n.Note, n.Start + rp * repSlices, n.Length));
                    }
                    outp.Add((sec.MelodyRiffId, refit));
                }
                else outp.Add((sec.MelodyRiffId, RefitTheme(cur, arr.SectionChords(sec), cs, homeScale)));
            }
            return outp;
        }
    }
}
