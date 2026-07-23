using System;
using System.Collections.Generic;
using MusicTracker.Engine;   // RiffNote

namespace MusicTracker.Engine.Timeline
{
    /// <summary>
    /// Executes an authored <see cref="ArrangementPlan"/> (development RECIPE) inside the Orchestrateur: builds a
    /// bespoke <see cref="FormSpec"/> from the plan's sections, and renders each section's melody/counter either from an
    /// authored token string or by putting the seed theme through a chain of the EXISTING <see cref="ArrangementEngine"/>
    /// transforms. This is where the "dial" lives — a section that just says Source="theme" tracks the seed; a section
    /// that carries its own Melody/Counter is hand-composed like ghibli_romance.
    /// </summary>
    public static class RecipeRenderer
    {
        static FormRole MapRole(string r, out string label)
        {
            switch ((r ?? "").Trim().ToLowerInvariant())
            {
                case "intro":   label = "Intro";          return FormRole.Intro;
                case "restate": label = "Reprise";        return FormRole.RestateA;
                case "develop": label = "Développement";  return FormRole.Develop;
                case "climax":  label = "Climax";         return FormRole.Recap;
                case "bridge":  label = "Pont";           return FormRole.Transition;
                case "outro":   label = "Outro";          return FormRole.Outro;
                case "theme":
                default:        label = "Thème";          return FormRole.ThemeA;
            }
        }

        /// <summary>Turn the recipe into a FormSpec (so the existing assembly pipeline runs unchanged), plus a parallel
        /// list of SectionPlans aligned 1:1 with the spec's sections for the melody renderer to consult.</summary>
        public static FormSpec BuildSpec(ArrangementPlan plan, out List<SectionPlan> plans)
        {
            var secs = new List<FormSection>();
            plans = new List<SectionPlan>();
            foreach (var sp in plan.Sections)
            {
                if (sp == null) continue;
                string label;
                var role = MapRole(sp.Role, out label);
                secs.Add(new FormSection(role, Math.Max(1, sp.Bars), KeyArea.Home, sp.Cadence, false, false, label));
                plans.Add(sp);
            }
            return new FormSpec("Recette", secs);
        }

        /// <summary>Render one recipe section into <paramref name="mel"/> / <paramref name="counter"/>. <paramref name="themeA"/>
        /// and <paramref name="themeAChords"/> are in the chosen key (the seed, delta already applied); <paramref name="shift"/>
        /// is this section's key offset; <paramref name="delta"/> is the home→chosen-key offset for AUTHORED tokens (which are
        /// written in the library's home key); <paramref name="localTonicPc"/> = tonic of this section's key.</summary>
        public static void RenderSection(SectionPlan plan,
            List<RiffNote> themeA, List<(int root, int quality)> themeAChords, List<(int root, int quality)> sectionChords,
            int shift, int localTonicPc, int delta, int off, int lim,
            List<RiffNote> mel, List<RiffNote> counter, HashSet<int> scale, int tonicPc, int chordSlices, int barSlices, int spq, Random rng,
            List<RiffNote> themeB = null, string devOp = null)
        {
            int cadPc = plan.Cadence == 0 ? (localTonicPc + 2) % 12 : localTonicPc;

            // ---- MAIN voice ----
            List<RiffNote> main;
            if (!string.IsNullOrWhiteSpace(plan.Melody))
            {
                main = ThemeLibrary.TransposeNotes(ThemeLibrary.ParseMelody(plan.Melody, spq), delta + shift);
            }
            else if (string.Equals(plan.Source, "rest", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(plan.Source, "free", StringComparison.OrdinalIgnoreCase))
            {
                main = new List<RiffNote>();   // accompaniment carries this section
            }
            else if (string.Equals(plan.Source, "recombine", StringComparison.OrdinalIgnoreCase))
            {
                var tb = (themeB != null && themeB.Count > 0) ? ArrangementEngine.TransposeMelLocal(themeB, shift) : null;
                main = Recombine(ArrangementEngine.TransposeMelLocal(themeA, shift), sectionChords, scale, tonicPc, chordSlices, barSlices, rng, tb);
            }
            else
            {
                var src = string.Equals(plan.Source, "fragment", StringComparison.OrdinalIgnoreCase)
                    ? Fragment(themeA, plan.FragFrom, plan.FragTo, barSlices)
                    : new List<RiffNote>(themeA);
                main = ArrangementEngine.TransposeMelLocal(src, shift);
                main = ApplyOps(plan.Ops, main, themeAChords, sectionChords, scale, tonicPc, chordSlices, barSlices, rng);
            }

            // DEV METHOD (dialog "Développement"): apply the chosen transform ON TOP of the reconstructed theme material
            // (theme / fragment / recombine) — keeps the section's normal selection, just colours it. Skips authored
            // gestures (intro/outro/counter have a Melody) and empty sections (free/rest).
            if (!string.IsNullOrEmpty(devOp) && main != null && main.Count > 0 && string.IsNullOrWhiteSpace(plan.Melody)
                && !string.Equals(plan.Source, "free", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(plan.Source, "rest", StringComparison.OrdinalIgnoreCase))
                main = ApplyOps(new List<string> { devOp }, main, themeAChords, sectionChords, scale, tonicPc, chordSlices, barSlices, rng);

            if (main == null || main.Count == 0) return;

            if (plan.Cadence != 2) ArrangementEngine.EndOn(main, cadPc, scale);
            var dest = string.Equals(plan.Voice, "counter", StringComparison.OrdinalIgnoreCase) ? counter : mel;
            foreach (var n in main) ArrangementEngine.AddAt(dest, n, off, lim);

            // ---- COUNTER voice (authored descant, or octave-stacked "à 2") ----
            if (!string.IsNullOrWhiteSpace(plan.Counter))
            {
                var c = ThemeLibrary.TransposeNotes(ThemeLibrary.ParseMelody(plan.Counter, spq), delta + shift);
                if (plan.Cadence != 2) ArrangementEngine.EndOn(c, cadPc, scale);
                foreach (var n in c) ArrangementEngine.AddAt(counter, n, off, lim);
            }
            else if (string.Equals(plan.Voice, "both", StringComparison.OrdinalIgnoreCase))
            {
                // "à 2": a DISTINCT second voice (real counterpoint) — its own rhythm + a contrary-motion / 3rd-6th-below
                // bias over THIS section's chords, resolving to the tonic — NOT a parallel octave double of the lead.
                int maxEnd = 0; foreach (var n in main) if (n.End > maxEnd) maxEnd = n.End;
                int bars = Math.Max(1, (maxEnd + barSlices - 1) / barSlices);
                var c2 = ArrangementEngine.BuildTogetherCounter(main, sectionChords, bars, barSlices, localTonicPc, scale, rng);
                foreach (var n in c2) ArrangementEngine.AddAt(counter, n, off, lim);
            }
        }

        // ---- RECOMBINATION (Phase B): build the line by stitching 2-bar WINDOWS, each copied from a different version of
        // the theme (theme / ornamented / moto / inverted). All candidates are the SAME length over the SAME chord grid,
        // so every window is harmonically valid; only the contour changes window-to-window ("a bit of theme + a bit of
        // variation"). Window boundaries are SEAMLESS-smoothed (a big leap at the seam is folded by octave). Seeded via rng. ----
        static List<RiffNote> Recombine(List<RiffNote> theme, List<(int root, int quality)> ch, HashSet<int> scale,
                                        int tonicPc, int chordSlices, int barSlices, Random rng, List<RiffNote> themeB)
        {
            if (theme == null || theme.Count == 0) return theme;
            var cands = new List<List<RiffNote>>
            {
                theme,
                ArrangementEngine.Ornament(theme, scale),
                ArrangementEngine.MotoPerpetuo(theme, ch, chordSlices, scale),
                ArrangementEngine.InvertContour(theme, scale),
            };
            // CROSS-THEME: stitch in windows from a SECOND theme too ("un bout de A, un bout de B"); refit it to THIS
            // section's chords so its windows stay consonant + interchangeable with theme A's.
            if (themeB != null && themeB.Count > 0)
                cands.Add(ArrangementEngine.RefitTheme(themeB, ch, chordSlices, scale));
            int total = 0; foreach (var n in theme) if (n.End > total) total = n.End;
            int win = Math.Max(1, barSlices) * 2;   // 2-bar windows
            var o = new List<RiffNote>();
            int prevPitch = -999, prevIdx = -1;
            for (int lo = 0; lo < total; lo += win)
            {
                int hi = lo + win;
                int idx = rng.Next(cands.Count);
                if (idx == prevIdx) idx = (idx + 1) % cands.Count;   // never the same source twice in a row
                prevIdx = idx;
                bool first = true;
                foreach (var n in cands[idx])
                {
                    if (n.Start < lo || n.Start >= hi) continue;
                    int note = n.Note;
                    if (first && prevPitch > -900)   // seamless seam: fold a large boundary leap by octave
                    {
                        while (note - prevPitch > 7) note -= 12;
                        while (prevPitch - note > 7) note += 12;
                    }
                    note = ArrangementEngine.Clamp95(note);
                    o.Add(new RiffNote(note, n.Start, n.Length));
                    prevPitch = note; first = false;
                }
            }
            return o;
        }

        // ---- SEAMLESS (Phase C): fold any leap LARGER than an octave between consecutive monophonic notes (chords, i.e.
        // notes sharing a Start, are skipped & preserved). Smooths section joins without flattening musical leaps (≤ octave kept). ----
        public static void SmoothLeaps(List<RiffNote> line)
        {
            if (line == null || line.Count < 2) return;
            line.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : a.Note.CompareTo(b.Note));
            var chordStart = new HashSet<int>();
            for (int i = 1; i < line.Count; i++) if (line[i].Start == line[i - 1].Start) chordStart.Add(line[i].Start);
            int prev = -999;
            for (int i = 0; i < line.Count; i++)
            {
                if (chordStart.Contains(line[i].Start)) { prev = -999; continue; }   // leave chords intact
                int cur = line[i].Note;
                if (prev > -900)
                {
                    int adj = cur;
                    while (adj - prev > 12) adj -= 12;
                    while (prev - adj > 12) adj += 12;
                    adj = ArrangementEngine.Clamp95(adj);
                    if (adj != cur) { line[i] = new RiffNote(adj, line[i].Start, line[i].Length) { Bend = line[i].Bend }; cur = adj; }
                }
                prev = cur;
            }
        }

        // ---- PHRASING / "breathing": templates carry only pitch+rhythm; this makes the line SPEAK without chopping it up.
        // GENTLE on purpose (user: too much silence / too choppy): only TWO gestures, no note-dropping — (a) a small BREATH
        // at phrase ends (every 4th bar), opening at most an ~8th-rest, never gutting the note; (b) a faint DÉTACHÉ that
        // clips a 32nd ONLY off notes longer than an 8th, so running 8ths/16ths stay legato and flowing. Chord clusters
        // (shared Start) preserved. level: 0 = legato (no-op), 1 = light, 2 = a touch more détaché. Run AFTER SmoothLeaps. ----
        public static void Breathe(List<RiffNote> line, int barSlices, int spq, int level, Random rng)
        {
            if (line == null || line.Count == 0 || level <= 0 || barSlices <= 0 || rng == null) return;
            line.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : a.Note.CompareTo(b.Note));
            int six = Math.Max(1, spq / 4);          // a 16th
            int gap = Math.Max(1, six / 2);          // a 32nd — the (tiny) détaché gap
            double detach = level >= 2 ? 0.22 : 0.12;
            var outl = new List<RiffNote>(line.Count);
            for (int i = 0; i < line.Count; i++)
            {
                var n = line[i];
                bool chord = (i > 0 && line[i - 1].Start == n.Start) || (i < line.Count - 1 && line[i + 1].Start == n.Start);
                int barIdx = n.Start / barSlices;
                bool phraseEnd = (barIdx % 4 == 3) && ((n.Start % barSlices) + n.Length >= barSlices - six);
                int len = n.Length;
                // gentle breath at phrase ends: open at most an ~8th-rest, keep >= 3/4 of the note
                if (phraseEnd && len > six * 2) len = Math.Max(len - six * 2, (len * 3) / 4);
                // faint détaché, ONLY on notes longer than an 8th (running 8ths/16ths stay legato → no choppiness)
                else if (!chord && len > six * 2 && rng.NextDouble() < detach) len = len - gap;
                outl.Add(new RiffNote(n.Note, n.Start, len) { Bend = n.Bend });
            }
            if (outl.Count > 0) { line.Clear(); line.AddRange(outl); }
        }

        // ---- ANACRUSIS / "levée": just before a downbeat (the theme's entry), fill the preceding beat (or half-beat) with
        // a short RISING scalar pickup that leads UP into the target note — gives forward impulse into the theme. No-op if
        // there's no room before `atSlice`. Clears whatever sat in the pickup window first (the intro's tail rest). ----
        public static void AddPickup(List<RiffNote> line, int atSlice, HashSet<int> scale, int pickupSlices, int spq)
        {
            if (line == null || atSlice <= 0 || pickupSlices <= 0 || scale == null || scale.Count == 0) return;
            int lo = atSlice - pickupSlices;
            if (lo < 0) return;
            int target = int.MinValue, tStart = int.MaxValue;       // target = the first note at/after the downbeat
            foreach (var n in line) if (n.Start >= atSlice && n.Start < tStart) { tStart = n.Start; target = n.Note; }
            if (target == int.MinValue) return;
            line.RemoveAll(n => n.Start >= lo && n.Start < atSlice); // clear the pickup window
            int eighth = Math.Max(1, spq / 2);
            if (pickupSlices >= 2 * eighth)                          // a full beat -> two rising eighths (scale steps -2, -1)
            {
                int p1 = MusicComposer.ShiftScale(target + 12, -2, scale) - 12;
                int p2 = MusicComposer.ShiftScale(target + 12, -1, scale) - 12;
                line.Add(new RiffNote(ArrangementEngine.Clamp95(p1), lo, eighth));
                line.Add(new RiffNote(ArrangementEngine.Clamp95(p2), atSlice - eighth, eighth));
            }
            else                                                     // a half-beat -> one rising eighth (scale step -1)
            {
                int p1 = MusicComposer.ShiftScale(target + 12, -1, scale) - 12;
                line.Add(new RiffNote(ArrangementEngine.Clamp95(p1), lo, pickupSlices));
            }
        }

        // ---- the seed theme bars [from..to] rebased to start 0 ----
        static List<RiffNote> Fragment(List<RiffNote> theme, int fromBar, int toBar, int barSlices)
        {
            int lo = Math.Max(0, fromBar) * barSlices;
            int hi = toBar < 0 ? int.MaxValue : (toBar + 1) * barSlices;
            var o = new List<RiffNote>();
            foreach (var n in theme)
                if (n.Start >= lo && n.Start < hi) o.Add(new RiffNote(n.Note, n.Start - lo, n.Length) { Bend = n.Bend });
            return o;
        }

        /// <summary>Public entry to a SINGLE book-inspired development op (augment/diminish/expand/retroinvert/spin/
        /// grow/thuemorse/evolve/…) — reused by the procedural "Variation" action. Thin wrapper over <see cref="ApplyOps"/>.</summary>
        public static List<RiffNote> Develop(string op, List<RiffNote> notes, List<(int root, int quality)> chords,
            HashSet<int> scale, int tonicPc, int chordSlices, int barSlices, Random rng)
            => ApplyOps(new List<string> { op }, notes, chords, chords, scale, tonicPc, chordSlices, barSlices, rng);

        // ---- ordered transform chain; each token is "name" or "name:arg" ----
        static List<RiffNote> ApplyOps(List<string> ops, List<RiffNote> notes,
            List<(int root, int quality)> origChords, List<(int root, int quality)> sectionChords,
            HashSet<int> scale, int tonicPc, int chordSlices, int barSlices, Random rng)
        {
            if (ops == null) return notes;
            foreach (var raw in ops)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                string name; string arg;
                int colon = raw.IndexOf(':');
                if (colon >= 0) { name = raw.Substring(0, colon).Trim().ToLowerInvariant(); arg = raw.Substring(colon + 1).Trim(); }
                else { name = raw.Trim().ToLowerInvariant(); arg = ""; }

                switch (name)
                {
                    case "rhythm":      notes = ArrangementEngine.VaryRhythm(notes, ParseInt(arg, 1), scale, rng); break;
                    case "ornament":    notes = ArrangementEngine.Ornament(notes, scale); break;
                    case "invert":      notes = ArrangementEngine.InvertContour(notes, scale); break;
                    case "retrograde":  notes = ArrangementEngine.Retrograde(notes); break;
                    case "moto":        notes = ArrangementEngine.MotoPerpetuo(notes, sectionChords, chordSlices, scale); break;
                    case "borrow":      notes = ArrangementEngine.BorrowMode(notes, tonicPc); break;
                    case "refit":       notes = ArrangementEngine.RefitTheme(notes, sectionChords, chordSlices, scale); break;
                    case "refitmodal":  notes = ArrangementEngine.RefitThemeModal(notes, origChords, sectionChords, chordSlices, scale); break;
                    case "octave":      notes = Octave(notes, ParseInt(arg, 1)); break;
                    case "sequence":    notes = Sequence(notes, arg, barSlices, scale); break;
                    case "run":         notes = Run(notes, scale); break;
                    // --- melodic-development ops (inspired by musiquealgorithmique.fr) ---
                    case "augment":     notes = Augment(notes, ParseInt(arg, 2)); break;                                  // durations ×N (grander)
                    case "diminish":    notes = Diminish(notes, ParseInt(arg, 2)); break;                                 // durations ÷N + tiled (busier)
                    case "expand":      notes = IntervalScale(notes, scale, true); break;                                 // widen the intervals
                    case "contract":    notes = IntervalScale(notes, scale, false); break;                                // narrow the intervals
                    case "retroinvert": notes = ArrangementEngine.InvertContour(ArrangementEngine.Retrograde(notes), scale); break;
                    case "spin":        notes = Fortspinnung(notes, sectionChords, scale, chordSlices, barSlices); break;  // head motif spun out
                    case "grow":        notes = Grow(notes, scale, ParseInt(arg, 1)); break;                              // L-system subdivision
                    case "thuemorse":   notes = ThueMorse(notes, sectionChords, scale, chordSlices, barSlices); break;    // self-similar variation chain
                    case "evolve":      notes = Evolve(notes, sectionChords, scale, tonicPc, chordSlices, barSlices, rng); break; // genetic
                    default: break;     // unknown op = no-op (forward-compatible)
                }
            }
            return notes;
        }

        static List<RiffNote> Octave(List<RiffNote> notes, int octs)
        {
            int semis = 12 * octs;
            var o = new List<RiffNote>(notes.Count);
            foreach (var n in notes) o.Add(new RiffNote(ArrangementEngine.Clamp95(n.Note + semis), n.Start, n.Length) { Bend = n.Bend });
            return o;
        }

        // "sequence:step,times" — repeat the material `times`, each repeat shifted by `step` SCALE STEPS (DIATONIC, stays
        // in key: 2 = up a third), laid end to end. (Chromatic shifting drifted out of the key — user's ear caught it.)
        static List<RiffNote> Sequence(List<RiffNote> frag, string arg, int barSlices, HashSet<int> scale)
        {
            int step = 2, times = 2;
            var parts = (arg ?? "").Split(',');
            if (parts.Length > 0) step = ParseInt(parts[0], 2);
            if (parts.Length > 1) times = Math.Max(1, ParseInt(parts[1], 2));
            int maxEnd = 0; foreach (var n in frag) if (n.End > maxEnd) maxEnd = n.End;
            int fragBars = Math.Max(1, (maxEnd + barSlices - 1) / barSlices);
            var o = new List<RiffNote>();
            for (int r = 0; r < times; r++)
            {
                int dt = r * fragBars * barSlices, steps = r * step;
                foreach (var n in frag)
                    o.Add(new RiffNote(ArrangementEngine.Clamp95(MusicComposer.ShiftScale(n.Note + 12, steps, scale) - 12), n.Start + dt, n.Length) { Bend = n.Bend });
            }
            return o;
        }

        // "run" — replace the material with continuous SCALAR 16th-note runs over its span: a stepwise scale WAVE
        // (turns around at register bounds). A distinct TEXTURE from arpeggios (Vivaldi-style scalar virtuosity).
        static List<RiffNote> Run(List<RiffNote> frag, HashSet<int> scale)
        {
            if (frag == null || frag.Count == 0 || scale == null || scale.Count == 0) return frag;
            int span = 0; foreach (var n in frag) if (n.End > span) span = n.End;
            if (span <= 0) return frag;
            int start = frag[0].Note, lo = start - 3, hi = start + 12, step = 6;   // 16ths, ~an octave range
            var o = new List<RiffNote>();
            int pitch = start, dir = 1, pos = 0, guard = 0;
            while (pos < span && guard++ < 4096)
            {
                o.Add(new RiffNote(ArrangementEngine.Clamp95(pitch), pos, Math.Min(step, span - pos)));
                int next = MusicComposer.ShiftScale(pitch + 12, dir, scale) - 12;
                if (next > hi) { dir = -1; next = MusicComposer.ShiftScale(pitch + 12, dir, scale) - 12; }
                else if (next < lo) { dir = 1; next = MusicComposer.ShiftScale(pitch + 12, dir, scale) - 12; }
                pitch = next; pos += step;
            }
            return o;
        }

        // ---- MELODIC-DEVELOPMENT transforms (inspired by musiquealgorithmique.fr) ----

        // AUGMENTATION: stretch onsets+durations ×f; the render clamps overflow to the section, so the theme's OPENING is
        // heard at 1/f speed — grander, more spacious.
        static List<RiffNote> Augment(List<RiffNote> notes, int f)
        {
            if (f < 2) f = 2;
            var o = new List<RiffNote>();
            foreach (var n in notes) o.Add(new RiffNote(n.Note, n.Start * f, Math.Max(1, n.Length * f)) { Bend = n.Bend });
            return o;
        }

        // DIMINUTION: compress onsets+durations ÷f, then TILE the compressed copy to refill the original span — the motif
        // runs f× faster and recurs (busier, more driving).
        static List<RiffNote> Diminish(List<RiffNote> notes, int f)
        {
            if (f < 2) f = 2;
            int span = 0; foreach (var n in notes) if (n.End > span) span = n.End;
            if (span <= 0) return notes;
            var comp = new List<RiffNote>();
            foreach (var n in notes) comp.Add(new RiffNote(n.Note, n.Start / f, Math.Max(1, n.Length / f)) { Bend = n.Bend });
            int cspan = Math.Max(1, (span + f - 1) / f);
            var o = new List<RiffNote>();
            for (int t = 0; t * cspan < span; t++)
                foreach (var n in comp) { int s = n.Start + t * cspan; if (s < span) o.Add(new RiffNote(n.Note, s, n.Length) { Bend = n.Bend }); }
            return o;
        }

        // INTERVALLIC EXPANSION / CONTRACTION: scale each note's interval from the first note (×3/2 widen, ×2/3 narrow),
        // snapped back to the scale — same rhythm + contour direction, a wider or tighter melodic shape.
        static List<RiffNote> IntervalScale(List<RiffNote> notes, HashSet<int> scale, bool widen)
        {
            if (notes == null || notes.Count == 0) return notes;
            int pivot = notes[0].Note + 12;
            var o = new List<RiffNote>();
            foreach (var n in notes)
            {
                int iv = (n.Note + 12) - pivot;
                int niv = widen ? (iv * 3) / 2 : (iv * 2) / 3;
                int p = MusicComposer.NearestScale(pivot + niv, scale);
                o.Add(new RiffNote(ArrangementEngine.Clamp95(p - 12), n.Start, n.Length) { Bend = n.Bend });
            }
            return o;
        }

        // FORTSPINNUNG ("dévidage"): take the HEAD motif (first 2 bars), reel it out across the section — each repeat
        // shifted a diatonic step (climbing then settling) — and refit to the chords. The baroque developmental engine.
        static List<RiffNote> Fortspinnung(List<RiffNote> notes, List<(int root, int quality)> chords, HashSet<int> scale, int chordSlices, int barSlices)
        {
            int span = 0; foreach (var n in notes) if (n.End > span) span = n.End;
            int win = Math.Max(1, barSlices) * 2;
            var head = new List<RiffNote>();
            foreach (var n in notes) if (n.Start < win) head.Add(n);
            if (head.Count == 0 || span <= 0) return notes;
            var o = new List<RiffNote>();
            for (int r = 0; r * win < span; r++)
            {
                int dt = r * win, steps = (r % 4) - 1;   // within ±a few scale steps (not endlessly upward)
                foreach (var n in head)
                {
                    int s = n.Start + dt; if (s >= span) continue;
                    int p = MusicComposer.ShiftScale(n.Note + 12, steps, scale) - 12;
                    o.Add(new RiffNote(ArrangementEngine.Clamp95(p), s, n.Length) { Bend = n.Bend });
                }
            }
            return ArrangementEngine.RefitTheme(o, chords, chordSlices, scale);
        }

        // L-SYSTEM: rewrite rule N -> N + diatonic neighbour (alternating upper/lower), splitting each note >= an 8th in
        // half, 1-2 iterations — grows a plain line into a florid, self-similar one (generative diminution).
        static List<RiffNote> Grow(List<RiffNote> notes, HashSet<int> scale, int iters)
        {
            var cur = notes;
            int passes = Math.Max(1, Math.Min(2, iters));
            for (int it = 0; it < passes; it++)
            {
                var next = new List<RiffNote>(); int k = 0;
                foreach (var n in cur)
                {
                    if (n.Length >= 12)
                    {
                        int half = n.Length / 2, dir = (k++ % 2 == 0) ? 1 : -1;
                        int nb = MusicComposer.ShiftScale(n.Note + 12, dir, scale) - 12;
                        next.Add(new RiffNote(n.Note, n.Start, half) { Bend = n.Bend });
                        next.Add(new RiffNote(ArrangementEngine.Clamp95(nb), n.Start + half, n.Length - half));
                    }
                    else next.Add(n);
                }
                cur = next;
            }
            return cur;
        }

        // THUE-MORSE: 2-bar windows; window i = theme as-is when the Thue-Morse bit is 0, INVERTED when 1 (the self-similar
        // 0110100110010110… pattern) — structured, non-periodic variety. Refit to the chords.
        static List<RiffNote> ThueMorse(List<RiffNote> notes, List<(int root, int quality)> chords, HashSet<int> scale, int chordSlices, int barSlices)
        {
            int span = 0; foreach (var n in notes) if (n.End > span) span = n.End;
            int win = Math.Max(1, barSlices) * 2;
            var o = new List<RiffNote>();
            for (int r = 0; r * win < span; r++)
            {
                int lo = r * win, hi = lo + win;
                var w = new List<RiffNote>();
                foreach (var n in notes) if (n.Start >= lo && n.Start < hi) w.Add(new RiffNote(n.Note, n.Start - lo, n.Length) { Bend = n.Bend });
                if (ThueMorseBit(r) == 1) w = ArrangementEngine.InvertContour(w, scale);
                foreach (var n in w) o.Add(new RiffNote(n.Note, n.Start + lo, n.Length) { Bend = n.Bend });
            }
            return ArrangementEngine.RefitTheme(o, chords, chordSlices, scale);
        }
        static int ThueMorseBit(int n) { int c = 0; while (n > 0) { c += n & 1; n >>= 1; } return c & 1; }

        // GENETIC: spawn a few candidate variations (random short chains of the cheaper ops), keep the FITTEST — most
        // strong-beat notes on chord tones, all in scale, with some activity. A pragmatic search, not a full GA.
        static List<RiffNote> Evolve(List<RiffNote> notes, List<(int root, int quality)> chords, HashSet<int> scale, int tonicPc, int chordSlices, int barSlices, Random rng)
        {
            string[] genes = { "ornament", "invert", "sequence:2,2", "moto", "grow:1", "expand", "rhythm:1", "spin" };
            var best = notes; double bestF = Fitness(notes, chords, scale, chordSlices);
            for (int k = 0; k < 8; k++)
            {
                var cand = new List<RiffNote>(notes);
                int g = 1 + rng.Next(2);
                for (int j = 0; j < g; j++)
                    cand = ApplyOps(new List<string> { genes[rng.Next(genes.Length)] }, cand, chords, chords, scale, tonicPc, chordSlices, barSlices, rng);
                double f = Fitness(cand, chords, scale, chordSlices);
                if (f > bestF) { bestF = f; best = cand; }
            }
            return best;
        }
        static double Fitness(List<RiffNote> notes, List<(int root, int quality)> chords, HashSet<int> scale, int chordSlices)
        {
            if (notes == null || notes.Count == 0) return -1;
            int inScale = 0, onChord = 0, strong = 0;
            foreach (var n in notes)
            {
                int midi = n.Note + 12;
                if (MusicComposer.NearestScale(midi, scale) == midi) inScale++;
                if (chordSlices > 0 && (n.Start % chordSlices) == 0 && chords != null && chords.Count > 0)
                {
                    strong++;
                    int ci = Math.Min(n.Start / chordSlices, chords.Count - 1);
                    var pcs = MusicComposer.ChordPcs(chords[ci].root, chords[ci].quality);
                    if (pcs.Contains(((midi % 12) + 12) % 12)) onChord++;
                }
            }
            double scaleR = (double)inScale / notes.Count;
            double chordR = strong > 0 ? (double)onChord / strong : 0.5;
            double density = Math.Min(1.0, notes.Count / 24.0);
            return scaleR + chordR * 1.5 + density * 0.3;
        }

        static int ParseInt(string s, int def) => int.TryParse(s, out int v) ? v : def;

        // Keep the LAST `bars` measures of a token melody (the tail that LEADS INTO the next section); fewer/equal -> unchanged.
        // Lets a multi-bar authored intro gesture be cut to the intro length the arrangement needs ("select the needed bars");
        // the join to the theme is then smoothed by SmoothLeaps (run on the whole melody after the section loop).
        static string LastBars(string melody, int bars)
        {
            if (string.IsNullOrWhiteSpace(melody) || bars <= 0) return melody;
            var parts = melody.Split('|');
            if (parts.Length <= bars) return melody;
            var keep = new string[bars];
            Array.Copy(parts, parts.Length - bars, keep, 0, bars);
            return string.Join("|", keep);
        }

        // ---- POOL selection: resolve a section's `Want` into concrete material (anti-"parrot" via SEEDED pick among
        // near-best matches + soft piece-scoped anti-repeat + bounded perturbation). No match -> keep the plan as authored. ----
        public static SectionPlan ResolveWant(SectionPlan plan, List<PoolGesture> pool, int seed, HashSet<string> recent, bool minor, int meterNum, int meterDen, string style, string mood)
        {
            if (plan == null || plan.Want == null || pool == null) return plan;
            var g = Select(pool, "melody", plan.Want, seed, recent, minor, meterNum, meterDen, style, mood);
            if (g == null) return plan;
            if (recent != null && !string.IsNullOrEmpty(g.Id)) recent.Add(g.Id);
            var p = Clone(plan);
            if (!string.IsNullOrWhiteSpace(g.Melody)) { p.Melody = g.Melody; p.Source = "free"; p.Ops = null; }
            else
            {
                p.Source = string.IsNullOrWhiteSpace(g.Source) ? "theme" : g.Source;
                p.FragFrom = g.FragFrom; p.FragTo = g.FragTo; p.Ops = CopyOps(g.Ops);
                if (plan.Fresh > 0.5)   // bounded perturbation: one mild, taste-preserving op (seeded)
                {
                    if (p.Ops == null) p.Ops = new List<string>();
                    p.Ops.Add(((seed & 1) == 0) ? "ornament" : "rhythm:1");
                }
            }
            return p;
        }

        // ---- AUTO-ASSEMBLY: build a recipe by drawing intro / variation / counter / climax / outro from the family POOL
        // (seeded + affinity + piece-scoped anti-repeat). One theme → a different piece per seed. Used when a theme is
        // flagged Auto (or has no explicit arrangement) and the family has a pool. Pool gestures + theme must share key+meter. ----
        public static ArrangementPlan AutoPlan(ThemeEntry entry, List<PoolGesture> pool, int seed, HashSet<string> recent, string style, int reps)
        {
            int tb = entry != null && entry.ThemeBars > 0 ? entry.ThemeBars : 8;
            string mood = entry != null ? entry.Mood : null;
            bool mn = entry != null && entry.Key != null && entry.Key.Minor;
            int mNum = (entry != null && entry.Meter != null && entry.Meter.Num > 0) ? entry.Meter.Num : 4;
            int mDen = (entry != null && entry.Meter != null && entry.Meter.Den > 0) ? entry.Meter.Den : 4;
            var plan = new ArrangementPlan();

            var intro = Select(pool, "intro", WantFor("intro", mood), seed + 1, recent, mn, mNum, mDen, style, mood);
            if (intro != null && !string.IsNullOrWhiteSpace(intro.Melody))
            {
                int gb = intro.Bars > 0 ? intro.Bars : 4;
                int want = Math.Min(gb, 2 + (int)(((uint)seed) % 3));   // seeded 2..4 bars, cut from the gesture's tail (anti-parrot length variety)
                plan.Sections.Add(new SectionPlan { Role = "intro", Bars = want, Cadence = 0, Source = "free", Melody = LastBars(intro.Melody, want) });
            }

            plan.Sections.Add(new SectionPlan { Role = "theme", Bars = tb, Cadence = 1, Source = "theme", Voice = "lead" });

            // DEVELOPMENT: one section per requested variation (dialog "Nombre de variations"). Each rep is built
            // DIFFERENTLY (rotating recombination / a pool variation / an ops-on-theme), seeded per rep + anti-repeat,
            // so N variations = N distinct development sections.
            int nv = Math.Max(1, reps);
            string[] devFallbackOps = { "ornament", "moto", "sequence:2,2" };
            for (int r = 0; r < nv; r++)
            {
                int rseed = seed + 2 + r * 17;
                var dev = new SectionPlan { Role = "develop", Bars = tb, Cadence = 2 };
                int pick = (int)(((uint)rseed) % 3);   // 0 = recombination, 1/2 = a pool variation (else ops-on-theme)
                if (pick == 0) { dev.Source = "recombine"; }
                else
                {
                    var v = Select(pool, "variation", WantFor("build", mood), rseed, recent, mn, mNum, mDen, style, mood);
                    if (v != null && !string.IsNullOrEmpty(v.Id)) recent.Add(v.Id);   // next rep avoids the same gesture
                    if (v != null && !string.IsNullOrWhiteSpace(v.Melody)) { dev.Source = "free"; dev.Melody = v.Melody; }
                    else if (v != null) { dev.Source = string.IsNullOrWhiteSpace(v.Source) ? "theme" : v.Source; dev.FragFrom = v.FragFrom; dev.FragTo = v.FragTo; dev.Ops = CopyOps(v.Ops); }
                    else { dev.Source = "theme"; dev.Ops = new List<string> { devFallbackOps[r % devFallbackOps.Length] }; }
                }
                plan.Sections.Add(dev);
            }

            var cnt = Select(pool, "counter", WantFor("counter", mood), seed + 3, recent, mn, mNum, mDen, style, mood);
            plan.Sections.Add(new SectionPlan { Role = "restate", Bars = tb, Cadence = 1, Source = "theme", Voice = "lead", Counter = cnt != null ? cnt.Melody : null });

            var clx = Select(pool, "variation", WantFor("climax", mood), seed + 5, recent, mn, mNum, mDen, style, mood);
            plan.Sections.Add(new SectionPlan { Role = "climax", Bars = 4, Cadence = 1, Source = "fragment", FragFrom = 4, FragTo = 7,
                                                Ops = (clx != null && clx.Ops != null) ? clx.Ops : new List<string> { "octave:1" }, Voice = "both" });

            var outro = Select(pool, "outro", WantFor("cadence", mood), seed + 4, recent, mn, mNum, mDen, style, mood);
            if (outro != null && !string.IsNullOrWhiteSpace(outro.Melody))
                plan.Sections.Add(new SectionPlan { Role = "outro", Bars = outro.Bars > 0 ? outro.Bars : 4, Cadence = 1, Source = "free", Melody = outro.Melody });
            else
                plan.Sections.Add(new SectionPlan { Role = "outro", Bars = 4, Cadence = 1, Source = "theme", Voice = "lead" });

            return plan;
        }

        // Affinity want for a section: function + an ENERGY derived from the theme's mood (so calm themes prefer low-energy
        // intros/variations, bright themes prefer high-energy ones). The selector weights function (×3) then energy.
        static Intent WantFor(string function, string mood)
        {
            string e = "medium";
            switch ((mood ?? "").Trim().ToLowerInvariant())
            {
                case "enjoué": case "majestueux": e = "high"; break;
                case "calme": case "méditatif": case "mélancolique": e = "low"; break;
            }
            return new Intent { Function = function, Energy = e };
        }

        static PoolGesture Select(List<PoolGesture> pool, string kind, Intent want, int seed, HashSet<string> recent, bool minor, int meterNum, int meterDen, string style, string mood)
        {
            int best = int.MinValue;
            foreach (var g in pool) { if (!Eligible(g, kind, minor, meterNum, meterDen)) continue; int s = ScoreFor(g, want, recent, style, mood); if (s > best) best = s; }
            if (best == int.MinValue) return null;
            var near = new List<PoolGesture>();
            foreach (var g in pool) { if (!Eligible(g, kind, minor, meterNum, meterDen)) continue; if (ScoreFor(g, want, recent, style, mood) >= best - 1) near.Add(g); }
            if (near.Count == 0) return null;
            return near[(int)(((uint)seed) % (uint)near.Count)];   // seeded pick among near-best -> variety across seeds
        }

        // A gesture is eligible if its kind matches and — for a VERBATIM gesture (has a Melody → key+meter-locked) — its
        // mode and meter match the theme. Ops-only gestures (derived from the theme) adapt to any key/meter → not filtered.
        static bool Eligible(PoolGesture g, string kind, bool minor, int meterNum, int meterDen)
        {
            if (g == null || !KindOk(g.Kind, kind)) return false;
            if (!string.IsNullOrWhiteSpace(g.Melody) && meterDen > 0)
            {
                if (g.Minor != minor) return false;
                int gn = (g.Meter != null && g.Meter.Num > 0) ? g.Meter.Num : 4;
                int gd = (g.Meter != null && g.Meter.Den > 0) ? g.Meter.Den : 4;
                if (gn != meterNum || gd != meterDen) return false;
            }
            return true;
        }

        static int ScoreFor(PoolGesture g, Intent want, HashSet<string> recent, string style, string mood)
        {
            int s = Score(g.Intent, want);
            // STYLE and MOOD are first-class tags (each +50): the best gesture matches BOTH (idiom + emotion); a gesture
            // matching only one still beats untagged/generic ones. Untagged (null) tags serve any style/mood as fallback.
            if (!string.IsNullOrEmpty(style) && !string.IsNullOrEmpty(g.Style) && string.Equals(g.Style, style, StringComparison.OrdinalIgnoreCase)) s += 50;
            if (!string.IsNullOrEmpty(mood)  && !string.IsNullOrEmpty(g.Mood)  && string.Equals(g.Mood,  mood,  StringComparison.OrdinalIgnoreCase)) s += 50;
            if (recent != null && !string.IsNullOrEmpty(g.Id) && recent.Contains(g.Id)) s -= 100;   // soft, piece-scoped anti-repeat
            return s;
        }
        static bool KindOk(string gk, string want) => string.IsNullOrEmpty(gk) || string.Equals(gk, want, StringComparison.OrdinalIgnoreCase);
        static int Score(Intent g, Intent want)
        {
            if (g == null || want == null) return 0;
            return MatchW(g.Function, want.Function, 3) + MatchW(g.Energy, want.Energy, 1) + MatchW(g.Register, want.Register, 1)
                 + MatchW(g.Density, want.Density, 1) + MatchW(g.Flavor, want.Flavor, 1);   // function weighted highest
        }
        static int MatchW(string a, string b, int w) => (!string.IsNullOrEmpty(b) && string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) ? w : 0;

        static List<string> CopyOps(List<string> ops) => ops == null ? null : new List<string>(ops);
        static SectionPlan Clone(SectionPlan p) => new SectionPlan
        {
            Role = p.Role, Bars = p.Bars, Transpose = p.Transpose, Cadence = p.Cadence,
            Source = p.Source, FragFrom = p.FragFrom, FragTo = p.FragTo, Ops = CopyOps(p.Ops),
            Voice = p.Voice, Melody = p.Melody, Counter = p.Counter, Chords = p.Chords,
            Want = p.Want, Fresh = p.Fresh
        };
    }
}
