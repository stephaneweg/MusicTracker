using System;
using System.Collections.Generic;
using System.Linq;
using MusicTracker.Engine.Flow;
using MusicTracker.Engine.Score;

namespace MusicTracker.Engine.Timeline
{
    /// <summary>
    /// Renders a <see cref="MelodicLineModule"/> (a rhythm-only skeleton, up to 3 voices) into a pitched Riff: for each
    /// note event, look up the chord in effect at its beat (<see cref="Harmony"/>) and pick a pitch — a CHORD TONE on a
    /// strong beat (all voices), a chord OR passing (scale) tone on a weak position, voice-led (nearest to the previous
    /// note of that voice) and kept inside each voice's register band. Voice 0 is the leading top line.
    /// </summary>
    public static class MelodicLineEngine
    {
        static readonly int[] Band = { 74, 66, 58 }; // register centres per voice (~D5 / F#4 / A#3)

        /// <param name="carry">Optional cross-module continuity state, per voice. Length 3 = last-note pitch only ([0..2]).
        /// Length 9 also carries the last DOWNBEAT chord tone ([3..5]) and its chord root ([6..8]) so a downbeat that
        /// repeats the previous bar's downbeat under the SAME chord — even across a module boundary — gets nudged.
        /// Null = independent module.</param>
        public static Riff GenerateLine(MelodicLineModule m, TimelineProject project, Func<Guid, Riff> resolve, KeySignature key, double startBeat, int[] carry = null)
        {
            if (m?.Notes == null || m.Notes.Count == 0) return null;
            key = key ?? new KeySignature();
            double pickup = project?.PickupBeats > 0 ? project.PickupBeats : 0; // anacrusis: shift the strong-beat grid
            int spq = m.SlicesPerQuarter > 0 ? m.SlicesPerQuarter : 4;
            var scale = MusicalMode.Scale(MusicalMode.Effective(key));
            int tonicPc = MusicTheory.TonicPc(key);
            var scalePcs = new HashSet<int>();
            foreach (var off in scale) scalePcs.Add((((tonicPc + off) % 12) + 12) % 12);

            var outNotes = new List<RiffNote>();
            int reg = m.RegisterShift;   // shift the register bands (bass line = large negative, dev lift = positive)
            int contour = m.Contour;     // 0 Arc (wave) · 1 Montante · 2 Descendante · 3 Statique (pivot) · 4 Zigzag · 5 Aléatoire
            int anchor = m.Anchor;       // strong-beat chord tone: 0 nearest · 1 root · 2 third · 3 fifth
            int continuity = Math.Max(0, Math.Min(100, m.Continuity));            // voice-leading attraction (leap cap)
            int slope = m.TensionSlope;  // register drift from the first note to the last
            int variation = m.Variation; // 0 none · 1 Split · 2 Gate · 3 Rétrograde · 4 Miroir
            int amp = Math.Max(2, Math.Min(24, m.Amplitude > 0 ? m.Amplitude : 12)); // half-width of the register band
            int ornaments = Math.Max(0, Math.Min(100, m.Ornaments));                 // accented-ornament density (0 = off)
            var rng = new Random(unchecked(1013 * (m.Notes.Count + 7) + contour * 131 + (int)Math.Round(startBeat)));
            int[] arcLen = { 4, 6, 5 };
            int totalSlices = Math.Max(1, m.BeatsPerBar) * spq;
            int meterNum = project?.TimeSigNum > 0 ? project.TimeSigNum : 4;
            int meterDen = project?.TimeSigDen > 0 ? project.TimeSigDen : 4;
            bool meterCompound = meterDen == 8 && meterNum % 3 == 0;
            // Slices per MAIN beat (dotted quarter in compound, else the notated beat) — the pulse the a/b and syncope
            // rules reason about, kept consistent with ClassifyMetric.
            int beatSlices = Math.Max(1, (int)Math.Round(spq * (meterCompound ? 1.5 : 4.0 / meterDen)));
            bool hasFortCarry = carry != null && carry.Length >= 9;                 // extended carry also remembers the last downbeat
            for (int v = 0; v < MelodicLineModule.MaxVoices; v++)
            {
                int band0 = Band[v] + reg;                                          // register centre for this voice (shifted)
                var vnotes = m.Notes.Where(x => x.Note == v).OrderBy(x => x.Start).ToList();
                if (variation == 1) vnotes = SplitRhythm(vnotes, spq);              // rhythm variations act on the SKELETON
                else if (variation == 2) vnotes = GateRhythm(vnotes);
                if (vnotes.Count == 0) continue;
                int nc = vnotes.Count;

                // ---- context per note (one harmony lookup each) --------------------------------------------------
                var cls = new int[nc]; var roots = new int[nc]; var chords = new HashSet<int>[nc];
                var bandOf = new int[nc]; var ok = new bool[nc];
                for (int i = 0; i < nc; i++)
                {
                    double absBeat = startBeat + vnotes[i].Start / (double)spq;
                    if (!Harmony.ChordAt(project, resolve, absBeat, out int root, out int quality, out _)) { ok[i] = false; continue; }
                    ok[i] = true; roots[i] = root;
                    var pcs = new HashSet<int>();
                    foreach (var p in PatternGenerator.ChordNotes(root, 4, quality, 0)) pcs.Add(((p % 12) + 12) % 12);
                    chords[i] = pcs;
                    cls[i] = ClassifyMetric(absBeat - pickup, meterNum, meterDen);  // 0 fort · 1 demi-fort · 2 faible · 3 entre-deux
                    bandOf[i] = band0 + (nc > 1 ? slope * i / (nc - 1) : 0);        // TENSION SLOPE drift
                }
                var starts = new HashSet<int>(vnotes.Select(x => x.Start));         // note onsets (does a beat start with a note?)

                // pitch[i] = MinValue while UNPLACED; the passes fill it level by level so each fill sees its neighbours.
                var pitch = new int[nc]; for (int i = 0; i < nc; i++) pitch[i] = int.MinValue;
                int carryPrev = (carry != null && v < carry.Length) ? carry[v] : -1;
                int lastFortMidi = hasFortCarry ? carry[3 + v] : -1;               // previous DOWNBEAT chord tone (bar-to-bar, across modules)
                int lastFortRoot = hasFortCarry ? carry[6 + v] : -1;
                Func<int, int> leftOf  = i => { for (int j = i - 1; j >= 0; j--) if (pitch[j] != int.MinValue) return pitch[j]; return carryPrev; };
                Func<int, int> rightOf = i => { for (int j = i + 1; j < nc; j++) if (pitch[j] != int.MinValue) return pitch[j]; return -1; };

                int dir = v == 1 ? -1 : 1, step = 0; bool anchorUsed = false;
                int waveLen = Math.Max(2, m.WaveLength > 0 ? m.WaveLength : arcLen[v]); // notes per arc for the Vague contour
                string moves = contour == 7 ? LSystem(nc) : null;
                int[] frac = contour == 8 ? FractalCurve(nc, band0, rng, amp) : null;

                // ---- PASS 1 : DOWNBEATS (temps forts) = the harmonic skeleton. The CONTOUR shapes this level; each fort
                //      voice-leads from the previous one (across modules via the carry) and avoids repeating it. ----------
                int prevFort = carryPrev;
                for (int i = 0; i < nc; i++)
                {
                    if (!ok[i] || cls[i] != 0) continue;
                    int root = roots[i], band = bandOf[i]; var pcs = chords[i];
                    HashSet<int> strongPcs = pcs; bool anchorForced = false;
                    if (anchor > 0 && !anchorUsed && pcs.Count > 0)
                    {
                        int apc = AnchorPc(anchor, root, pcs);
                        if (apc >= 0) { strongPcs = new HashSet<int> { apc }; anchorUsed = true; anchorForced = true; }
                    }
                    int choice = PickTone(contour, ref dir, ref step, waveLen, prevFort, strongPcs, pcs, band, false, rng, i, moves, frac, amp);
                    // avoid repeating the previous downbeat under the SAME chord (compared by root: Sol7 == Sol == "V")
                    if (!anchorForced && choice >= 0 && pcs.Count > 1 && choice == lastFortMidi && root == lastFortRoot)
                    {
                        int alt = MovedChordTone(choice, pcs, band, dir); if (alt >= 0) choice = alt;
                    }
                    pitch[i] = choice;
                    if (choice >= 0) { prevFort = choice; lastFortMidi = choice; lastFortRoot = root; }
                }

                // ---- PASS 2 then 3 : demi-forts, then faibles = CHORD tones threaded BETWEEN the fixed neighbours -------
                foreach (int level in new[] { 1, 2 })
                    for (int i = 0; i < nc; i++)
                    {
                        if (!ok[i] || cls[i] != level) continue;
                        int band = bandOf[i]; var pcs = chords[i]; int L = leftOf(i), R = rightOf(i);
                        int choice = -1;
                        if (anchor > 0 && !anchorUsed && level == 1 && pcs.Count > 0)   // no downbeat took the anchor → the first demi does
                        {
                            int apc = AnchorPc(anchor, roots[i], pcs);
                            if (apc >= 0) { choice = NearestTone(L >= 0 && R >= 0 ? (L + R) / 2 : (L >= 0 ? L : band), new HashSet<int> { apc }, band); anchorUsed = true; }
                        }
                        if (choice < 0) choice = ChordToneConnect(L, R, pcs, band);
                        pitch[i] = choice;
                    }

                // ---- PASS 4 : between-beat fills = passing / neighbour / appoggiatura connecting two FIXED notes, or a
                //      chord tone when the note is a SYNCOPE (a contretemps held over the next beat = a displaced accent). --
                for (int i = 0; i < nc; i++)
                {
                    if (!ok[i] || cls[i] != 3) continue;
                    int band = bandOf[i]; var pcs = chords[i]; int L = leftOf(i), R = rightOf(i);
                    int nextBeat = (vnotes[i].Start / beatSlices + 1) * beatSlices;
                    bool syncope = (vnotes[i].Start % beatSlices) != 0 && vnotes[i].Start + vnotes[i].Length > nextBeat;
                    if (syncope)
                    {
                        var sPcs = new HashSet<int>(pcs);
                        double bAbs = startBeat + nextBeat / (double)spq;
                        if (Harmony.ChordAt(project, resolve, bAbs, out int br, out int bq, out _))
                            foreach (var p in PatternGenerator.ChordNotes(br, 4, bq, 0)) sPcs.Add(((p % 12) + 12) % 12);
                        pitch[i] = NearestTone(L >= 0 ? L : band, sPcs.Count > 0 ? sPcs : pcs, band);
                        continue;
                    }
                    int beatStartSlice = (vnotes[i].Start / beatSlices) * beatSlices;
                    bool beatStartsWithNote = starts.Contains(beatStartSlice);
                    pitch[i] = PassingBetween(L, R, pcs, scalePcs, band, beatStartsWithNote, rng);
                }

                // ---- ORNAMENTS (opt-in, Ornaments = 0 by default) : decorate the approach to a fort/demi with an
                //      APPOGGIATURA (a step above the chord tone on the accent, resolving DOWN onto it on the next, weaker
                //      note) or a SUSPENSION (hold the previous note if it is a step above a chord tone, then resolve). ----
                if (ornaments > 0)
                    for (int i = 0; i < nc; i++)
                    {
                        if (!ok[i] || cls[i] > 1 || pitch[i] < 0) continue;
                        int k = i + 1;
                        if (k >= nc || !ok[k] || pitch[k] < 0 || cls[k] < cls[i]) continue;     // resolve onto a note that is NOT stronger
                        if (vnotes[k].Start > vnotes[i].Start + vnotes[i].Length) continue;     // must be contiguous
                        if (rng.NextDouble() >= (ornaments / 100.0) * (cls[i] == 0 ? 0.55 : 0.30)) continue;
                        int chordTone = pitch[i], prevP = leftOf(i);
                        int sus = prevP >= 0 ? StepDownChordTone(prevP, chords[i]) : -1;
                        if (sus >= 0 && Math.Abs(prevP - sus) <= 2) { pitch[i] = prevP; pitch[k] = sus; }        // suspension
                        else { int appo = ScaleStepAbove(chordTone, scalePcs); if (appo >= 0) { pitch[i] = appo; pitch[k] = chordTone; } } // appoggiatura
                    }

                // ---- assemble in time order, cap leaps on the STRUCTURAL notes (passing tones stay free) ---------------
                var gen = new List<(int start, int len, int midi)>();
                int prevEmit = carryPrev;
                for (int i = 0; i < nc; i++)
                {
                    if (!ok[i] || pitch[i] == int.MinValue || pitch[i] < 0) continue;
                    int midi = pitch[i];
                    if (prevEmit >= 0 && continuity > 0 && cls[i] <= 2)
                    {
                        int maxLeap = 12 - continuity * 11 / 100;                    // 0 → 12 (free) … 100 → 1 (very smooth)
                        var capPcs = chords[i].Count > 0 ? chords[i] : scalePcs;
                        if (Math.Abs(midi - prevEmit) > maxLeap) { int near = NearestTone(prevEmit, capPcs, bandOf[i]); if (near >= 0) midi = near; }
                    }
                    prevEmit = midi;
                    gen.Add((vnotes[i].Start, vnotes[i].Length, midi));
                }

                if (variation == 3) Retrograde(gen, totalSlices);                   // reverse in time
                if (variation == 4) Mirror(gen);                                    // invert intervals
                if (variation == 3 || variation == 4) Resnap(gen, project, resolve, startBeat, spq, pickup, scalePcs); // → back onto the chords

                foreach (var g in gen) { int row = g.midi - 12; if (row >= 0 && row < 96) outNotes.Add(new RiffNote(row, g.start, g.len) { Voice = v }); }
                if (carry != null && v < carry.Length && gen.Count > 0)
                {
                    int lastMidi = gen[0].midi, lastStart = gen[0].start;
                    foreach (var g in gen) if (g.start >= lastStart) { lastStart = g.start; lastMidi = g.midi; }
                    carry[v] = lastMidi;
                    if (hasFortCarry) { carry[3 + v] = lastFortMidi; carry[6 + v] = lastFortRoot; } // hand the last downbeat to the next module
                }
            }
            outNotes.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : a.Note.CompareTo(b.Note));
            return new Riff { Name = "Ligne", Notes = outNotes, LengthSlices = totalSlices, SlicesPerQuarter = spq };
        }

        /// <summary>Variation mode names (index = <see cref="MelodicLineModule.Variation"/>).</summary>
        public static readonly string[] VariationNames = { "Aucune", "Split (couper les longues)", "Gate (aérer)", "Rétrograde", "Miroir (inversion)" };

        // SPLIT: cut any note a quarter or longer into two halves — a subdivision "fill" (e.g. to end a section).
        static List<RiffNote> SplitRhythm(List<RiffNote> notes, int spq)
        {
            var outp = new List<RiffNote>();
            foreach (var n in notes)
            {
                if (n.Length >= spq && n.Length >= 2) { int h = n.Length / 2; outp.Add(new RiffNote(n.Note, n.Start, h)); outp.Add(new RiffNote(n.Note, n.Start + h, n.Length - h)); }
                else outp.Add(n);
            }
            return outp;
        }

        // GATE: drop every other note (odd index) → the rhythm is aerated (those onsets become rests).
        static List<RiffNote> GateRhythm(List<RiffNote> notes)
        {
            var outp = new List<RiffNote>();
            for (int i = 0; i < notes.Count; i++) if (i % 2 == 0) outp.Add(notes[i]);
            return outp;
        }

        // RÉTROGRADE: mirror each note's position in time (start → total − (start+len)); pitches ride along (re-snapped after).
        static void Retrograde(List<(int start, int len, int midi)> gen, int total)
        {
            for (int i = 0; i < gen.Count; i++) { var g = gen[i]; int ns = total - (g.start + g.len); if (ns < 0) ns = 0; gen[i] = (ns, g.len, g.midi); }
            gen.Sort((a, b) => a.start.CompareTo(b.start));
        }

        // MIROIR: invert each interval around the first note's pitch (pivot), clamped to a sane melodic range (re-snapped after).
        static void Mirror(List<(int start, int len, int midi)> gen)
        {
            if (gen.Count == 0) return;
            int pivot = gen[0].midi;
            for (int i = 0; i < gen.Count; i++) { var g = gen[i]; int mi = 2 * pivot - g.midi; mi = Math.Max(40, Math.Min(90, mi)); gen[i] = (g.start, g.len, mi); }
        }

        // RE-SNAP: after a transform, pull each note onto the harmony NOW at its beat (strong → nearest chord tone, weak →
        // nearest scale tone) so a retrograde/mirror stays consonant with the FIXED chord progression.
        static void Resnap(List<(int start, int len, int midi)> gen, TimelineProject project, Func<Guid, Riff> resolve, double startBeat, int spq, double pickup, HashSet<int> scalePcs)
        {
            for (int i = 0; i < gen.Count; i++)
            {
                var g = gen[i];
                double absBeat = startBeat + g.start / (double)spq;
                if (!Harmony.ChordAt(project, resolve, absBeat, out int root, out int quality, out _)) continue;
                var chordPcs = new HashSet<int>();
                foreach (var p in PatternGenerator.ChordNotes(root, 4, quality, 0)) chordPcs.Add(((p % 12) + 12) % 12);
                double phased = absBeat - pickup;
                bool strong = Math.Abs(phased - Math.Round(phased)) < 1e-6;
                int snapped = NearestTone(g.midi, strong ? chordPcs : scalePcs, g.midi); // search around the transformed pitch
                if (snapped >= 0) gen[i] = (g.start, g.len, snapped);
            }
        }

        /// <summary>Choose the next pitch for the current CONTOUR mode (0 Arc · 1 Montante · 2 Descendante · 3 Statique ·
        /// 4 Zigzag · 5 Aléatoire). Returns -1 only when no tone is available at all.</summary>
        static int PickTone(int contour, ref int dir, ref int step, int arcLen, int prev, HashSet<int> pcs, HashSet<int> chordPcs, int band, bool requireStep, Random rng, int idx, string moves, int[] frac, int halfBand)
        {
            int midi;
            switch (contour)
            {
                case 6: // Thue-Morse — aperiodic self-similar up/down (never exactly repeats → kills the "parrot")
                    dir = ThueMorse(idx) == 0 ? 1 : -1;
                    midi = ToneInDir(prev, pcs, band, dir, requireStep, halfBand);
                    if (midi < 0) { dir = -dir; midi = ToneInDir(prev, pcs, band, dir, requireStep, halfBand); }
                    if (midi < 0) midi = NearestTone(prev, chordPcs, band, halfBand);
                    return midi;
                case 7: // L-system — recursive rewrite → a self-similar (fractal) melodic contour
                    dir = (moves != null && moves.Length > 0 && moves[idx % moves.Length] == 'D') ? -1 : 1;
                    midi = ToneInDir(prev, pcs, band, dir, requireStep, halfBand);
                    if (midi < 0) { dir = -dir; midi = ToneInDir(prev, pcs, band, dir, requireStep, halfBand); }
                    if (midi < 0) midi = NearestTone(prev, chordPcs, band, halfBand);
                    return midi;
                case 8: // Fractal 1/f — a midpoint-displacement guide curve; the harmony picks the nearest tone to it
                {
                    int target = (frac != null && idx < frac.Length) ? frac[idx] : band;
                    midi = NearestTone(target, pcs, band, halfBand);
                    if (midi < 0) midi = NearestTone(target, chordPcs, band, halfBand);
                    return midi;
                }
                case 1: // Montante / 2 Descendante — steady climb/fall; at the band edge, jump to the far edge and continue
                case 2:
                {
                    int d = contour == 1 ? 1 : -1;
                    midi = ToneInDir(prev, pcs, band, d, requireStep, halfBand);
                    if (midi < 0) midi = NearestTone(contour == 1 ? band - halfBand : band + halfBand, pcs, band, halfBand);
                    if (midi < 0) midi = NearestTone(prev, chordPcs, band, halfBand);
                    return midi;
                }
                case 3: // Statique — the nearest available tone to the previous note (minimal motion, hovers/repeats)
                    midi = NearestTone(prev, pcs, band, halfBand);
                    if (midi < 0) midi = NearestTone(prev, chordPcs, band, halfBand);
                    return midi;
                case 4: // Zigzag — flip direction on EVERY note (angular)
                    dir = -dir;
                    midi = ToneInDir(prev, pcs, band, dir, requireStep, halfBand);
                    if (midi < 0) { dir = -dir; midi = ToneInDir(prev, pcs, band, dir, requireStep, halfBand); }
                    if (midi < 0) midi = NearestTone(prev, chordPcs, band, halfBand);
                    return midi;
                case 5: // Aléatoire — a seeded random direction each note (a controlled walk)
                    dir = rng.Next(2) == 0 ? 1 : -1;
                    midi = ToneInDir(prev, pcs, band, dir, requireStep, halfBand);
                    if (midi < 0) { dir = -dir; midi = ToneInDir(prev, pcs, band, dir, requireStep, halfBand); }
                    if (midi < 0) midi = NearestTone(prev, chordPcs, band, halfBand);
                    return midi;
                default: // 0 = Arc (wave): flip direction every arcLen notes, bounce at the band edges
                    if (++step % arcLen == 0) dir = -dir;
                    midi = ToneInDir(prev, pcs, band, dir, requireStep, halfBand);
                    if (midi < 0) { dir = -dir; midi = ToneInDir(prev, pcs, band, dir, requireStep, halfBand); }
                    if (midi < 0) midi = NearestTone(prev, chordPcs, band, halfBand);
                    return midi;
            }
        }

        /// <summary>Contour mode names (index = <see cref="MelodicLineModule.Contour"/>).</summary>
        public static readonly string[] ContourNames = { "Vague (arcs)", "Montante", "Descendante", "Statique (pivot)", "Zigzag", "Aléatoire", "Thue-Morse", "L-système", "Fractale (1/f)" };

        /// <summary>Start-note anchor names (index = <see cref="MelodicLineModule.Anchor"/>).</summary>
        public static readonly string[] AnchorNames = { "Défaut (au plus proche)", "Fondamentale", "Tierce", "Quinte", "Septième", "Neuvième" };

        // Generic interval (semitones above the root) for each anchor degree: root · 3rd · 5th · 7th · 9th. A degree the
        // chord lacks (a triad's 7th/9th, a sus chord's 3rd) resolves to the NEAREST real chord tone (5th / root / sus).
        static readonly int[] AnchorGuide = { 0, 4, 7, 10, 14 };
        static int PcDist(int a, int b) { int d = (((a - b) % 12) + 12) % 12; return Math.Min(d, 12 - d); }

        // Metric class of a phased position: 0 fort (bar downbeat) · 1 demi-fort (secondary strong, num/2) · 2 faible
        // (other on-beat) · 3 entre-deux (between notated beats). The beat unit is the notated beat (quarter for /4,
        // eighth for /8), so the classical hierarchy holds (temps 3 of 4/4 and the 2nd pulse of 6/8 are demi-fort).
        static int ClassifyMetric(double phased, int num, int den)
        {
            num = Math.Max(1, num); den = Math.Max(1, den);
            bool compound = den == 8 && num % 3 == 0;                 // 6/8, 9/8, 12/8 : the beat is the dotted quarter
            double mainPulseQ = compound ? 1.5 : 4.0 / den;          // duration of a MAIN beat, in quarter-beats
            int mainBeats = compound ? num / 3 : num;                // beats/pulses per bar
            double pos = phased / mainPulseQ;                        // position in MAIN beats
            double frac = pos - Math.Floor(pos + 1e-6);
            if (frac > 1e-4 && frac < 1 - 1e-4) return 3;            // between beats (a subdivision)
            int beat = (((int)Math.Round(pos)) % mainBeats + mainBeats) % mainBeats;
            if (beat == 0) return 0;                                 // fort (downbeat)
            if (mainBeats == 4 && beat == 2) return 1;               // demi-fort = 3rd beat of a QUADRUPLE bar (4/4, 12/8)
            return 2;                                                // faible (on-beat but weak)
        }

        // A chord tone 1–2 semitones BELOW fromMidi — the downward-step resolution of a suspension/appoggiatura.
        static int StepDownChordTone(int fromMidi, HashSet<int> chordPcs)
        {
            for (int d = 1; d <= 2; d++) { int m = fromMidi - d; if (chordPcs.Contains(((m % 12) + 12) % 12)) return m; }
            return -1;
        }

        // A scale tone a STEP (whole tone preferred, else semitone) above or below targetMidi. −1 if none in range.
        static int ScaleStepNeighbor(int targetMidi, HashSet<int> pcs, Random rng)
        {
            var cand = new List<int>();
            foreach (int d in new[] { 2, -2, 1, -1 }) { int m = targetMidi + d; if (pcs.Contains(((m % 12) + 12) % 12)) cand.Add(m); }
            return cand.Count == 0 ? -1 : cand[rng.Next(cand.Count)];
        }

        // The scale tone a STEP ABOVE targetMidi (a proper appoggiatura position — it resolves DOWN to the chord tone).
        static int ScaleStepAbove(int targetMidi, HashSet<int> pcs)
        {
            for (int d = 1; d <= 2; d++) { int m = targetMidi + d; if (pcs.Contains(((m % 12) + 12) % 12)) return m; }
            return -1;
        }

        // The CLOSEST chord tone to fromMidi that is DIFFERENT from it, preferring the current melodic direction so the
        // line keeps moving (Ré → Si down, or Si → Ré up) rather than re-landing on the same pitch. −1 if none.
        static int MovedChordTone(int fromMidi, HashSet<int> chordPcs, int band, int dir)
        {
            int best = -1, bestScore = int.MaxValue;
            for (int m = band - 24; m <= band + 24; m++)
            {
                if (m == fromMidi || !chordPcs.Contains(((m % 12) + 12) % 12)) continue;
                int score = Math.Abs(m - fromMidi) + (dir != 0 && Math.Sign(m - fromMidi) != dir ? 5 : 0); // small penalty against the contour
                if (score < bestScore) { bestScore = score; best = m; }
            }
            return best;
        }

        // The chord-tone pitch class for an anchor degree (root · 3rd · 5th · 7th · 9th), snapped to the NEAREST tone the
        // chord actually has (a triad's missing 7th/9th, a sus chord's 3rd → the closest real chord tone). −1 if none.
        static int AnchorPc(int anchor, int root, HashSet<int> chordPcs)
        {
            if (chordPcs.Count == 0) return -1;
            int targetPc = (((root + AnchorGuide[Math.Min(anchor - 1, AnchorGuide.Length - 1)]) % 12) + 12) % 12;
            int bestPc = -1, bestD = 99;
            foreach (var p in chordPcs) { int d = PcDist(p, targetPc); if (d < bestD) { bestD = d; bestPc = p; } }
            return bestPc;
        }

        // A CHORD tone that threads BETWEEN two fixed neighbours (the note's left and right in the melody): the chord tone
        // nearest their midpoint, PREFERRING one that isn't an endpoint so the line keeps moving (a static harmony still
        // walks the arpeggio: Ré [fort] → Si → Sol [fort]). Falls back to the nearest tone / the band centre when a side
        // is missing. −1 only if the chord has no tone in range.
        static int ChordToneConnect(int left, int right, HashSet<int> pcs, int band)
        {
            if (pcs.Count == 0) return -1;
            bool both = left >= 0 && right >= 0;
            double target = both ? (left + right) / 2.0 : (left >= 0 ? left : (right >= 0 ? right : band));
            int best = -1, bestAny = -1; double bestD = double.MaxValue, bestAnyD = double.MaxValue;
            for (int mi = band - 24; mi <= band + 24; mi++)
            {
                if (!pcs.Contains(((mi % 12) + 12) % 12)) continue;
                double d = Math.Abs(mi - target);
                if (d < bestAnyD) { bestAnyD = d; bestAny = mi; }
                if (mi == left || mi == right) continue;              // skip either fixed endpoint so the line keeps moving
                if (d < bestD) { bestD = d; best = mi; }
            }
            return best >= 0 ? best : bestAny;
        }

        // A note BETWEEN two beats, now knowing BOTH fixed neighbours (left = the note before, right = the note after).
        // First choice: a real PASSING tone — a scale step from `left` toward `right`, strictly between them. If they are
        // only a step apart (no room), fall back on the a/b rules: a beat that STARTED with a note takes a neighbour
        // (broderie) or repeats; a beat that started with a REST takes an appoggiatura a step from the right target.
        static int PassingBetween(int left, int right, HashSet<int> chordPcs, HashSet<int> scalePcs, int band, bool beatStartsWithNote, Random rng)
        {
            var fallback = chordPcs.Count > 0 ? chordPcs : scalePcs;
            if (left < 0) return NearestTone(right >= 0 ? right : band, fallback, band);
            if (right >= 0 && right != left)
            {
                int dir = Math.Sign(right - left);
                int pass = ToneInDir(left, scalePcs, band, dir, true, 12);             // conjunct passing tone toward the target
                if (pass >= 0 && (dir > 0 ? pass < right : pass > right)) return pass;  // …strictly between the two
            }
            if (beatStartsWithNote)
            {
                int nb = ScaleStepNeighbor(left, scalePcs, rng);                       // broderie / neighbour, else repeat
                return (nb >= 0 && rng.NextDouble() < 0.6) ? nb : left;
            }
            if (right >= 0)                                                            // (b) rest before → appoggiatura on the target
            {
                int appo = ScaleStepNeighbor(right, scalePcs, rng);
                if (appo >= 0) return appo;
            }
            return NearestTone(left, fallback, band);                                  // otherwise: a chord tone
        }

        // Thue-Morse t(n): parity of the bit count of n → the aperiodic 0110100110010110… sequence.
        static int ThueMorse(int n) { int c = 0; while (n > 0) { c ^= (n & 1); n >>= 1; } return c; }

        // L-system: expand an up/down axiom by rewriting rules until it covers `count` notes (self-similar contour).
        static string LSystem(int count)
        {
            string s = "U";
            int guard = 0;
            while (s.Length < Math.Max(1, count) && guard++ < 16)
            {
                var sb = new System.Text.StringBuilder(s.Length * 3);
                foreach (char c in s) sb.Append(c == 'U' ? "UUD" : "UDD");   // rules: U→UUD, D→UDD (balanced, locally structured)
                s = sb.ToString();
            }
            return s;
        }

        // Fractal 1/f contour: midpoint displacement (fBm) over a 2^k+1 grid, amplitude halved each level, sampled to
        // `n` target MIDI notes around the band centre (clamped to ±12 semitones). A natural, non-repetitive melodic arc.
        static int[] FractalCurve(int n, int band, Random rng, int halfBand = 12)
        {
            if (n <= 0) return new int[0];
            int size = 2; while (size + 1 < n) size *= 2; size += 1;   // 2^k + 1 ≥ n
            var h = new double[size];
            double amp = Math.Max(2, halfBand * 2 / 3.0);   // initial displacement scaled to the amplitude
            h[0] = (rng.NextDouble() * 2 - 1) * amp; h[size - 1] = (rng.NextDouble() * 2 - 1) * amp;
            for (int seg = size - 1; seg >= 2; seg /= 2)
            {
                for (int i = 0; i + seg < size; i += seg)
                {
                    int mid = i + seg / 2;
                    h[mid] = (h[i] + h[i + seg]) * 0.5 + (rng.NextDouble() * 2 - 1) * amp;
                }
                amp *= 0.55;   // 1/f: halve-ish the displacement each finer level
            }
            var outp = new int[n];
            for (int i = 0; i < n; i++)
            {
                int xi = (int)Math.Round((double)i / Math.Max(1, n - 1) * (size - 1));
                xi = Math.Max(0, Math.Min(size - 1, xi));
                double d = Math.Max(-halfBand, Math.Min(halfBand, h[xi]));
                outp[i] = band + (int)Math.Round(d);
            }
            return outp;
        }

        // The next tone (pc in `pcs`) going in direction `dir` from the previous note, within the register band (± halfBand).
        // For the FIRST note, the nearest tone to the band centre. requireStep = a passing move: reject a jump > 2 semitones.
        static int ToneInDir(int prevMidi, HashSet<int> pcs, int bandCenter, int dir, bool requireStep, int halfBand = 12)
        {
            if (pcs.Count == 0) return -1;
            if (prevMidi < 0) return NearestTone(-1, pcs, bandCenter, halfBand);
            int lo = bandCenter - halfBand, hi = bandCenter + halfBand;
            for (int mi = prevMidi + dir; mi >= lo && mi <= hi; mi += dir)
                if (pcs.Contains((((mi % 12) + 12) % 12)))
                    return (requireStep && Math.Abs(mi - prevMidi) > 2) ? -1 : mi;   // step ≤ 2 semitones on a weak note
            return -1;                                                              // hit the band edge
        }

        // Nearest MIDI whose pc is in `pcs`, close to the previous note (or the band centre for the 1st note), within ± halfBand.
        // A narrow amplitude can leave no tone in-band, so fall back to a full octave rather than dropping the note.
        static int NearestTone(int prevMidi, HashSet<int> pcs, int bandCenter, int halfBand = 12)
        {
            if (pcs.Count == 0) return -1;
            int best = SearchNearest(prevMidi, pcs, bandCenter, halfBand);
            if (best < 0 && halfBand < 12) best = SearchNearest(prevMidi, pcs, bandCenter, 12);
            return best;
        }

        static int SearchNearest(int prevMidi, HashSet<int> pcs, int bandCenter, int halfBand)
        {
            int anchor = prevMidi >= 0 ? prevMidi : bandCenter, best = -1, bestDist = int.MaxValue;
            for (int mi = bandCenter - halfBand; mi <= bandCenter + halfBand; mi++)
            {
                if (!pcs.Contains((((mi % 12) + 12) % 12))) continue;
                int d = Math.Abs(mi - anchor);
                if (d < bestDist) { bestDist = d; best = mi; }
            }
            return best;
        }
    }
}
