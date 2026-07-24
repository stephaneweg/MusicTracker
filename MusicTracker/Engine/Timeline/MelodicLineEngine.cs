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
            for (int v = 0; v < MelodicLineModule.MaxVoices; v++)
            {
                int band0 = Band[v] + reg;                                          // register centre for this voice (shifted)
                var vnotes = m.Notes.Where(x => x.Note == v).OrderBy(x => x.Start).ToList();
                if (variation == 1) vnotes = SplitRhythm(vnotes, spq);              // rhythm variations act on the SKELETON
                else if (variation == 2) vnotes = GateRhythm(vnotes);
                if (vnotes.Count == 0) continue;

                int prev = (carry != null && v < carry.Length) ? carry[v] : -1;     // continue from the previous module
                int dir = v == 1 ? -1 : 1, step = 0;
                bool anchorUsed = false;
                var starts = new HashSet<int>(vnotes.Select(x => x.Start));         // note onsets (does a beat start with a note?)
                int forceMidi = -1;                                                 // pending resolution of an accented ornament
                int lastStrongMidi = -1, lastStrongRoot = -1;                       // previous ON-BEAT chord tone (avoid repeat within a bar)
                bool hasFortCarry = carry != null && carry.Length >= 9;             // extended carry also remembers the last downbeat
                int lastFortMidi = hasFortCarry ? carry[3 + v] : -1;               // previous DOWNBEAT chord tone (bar-to-bar, across modules)
                int lastFortRoot = hasFortCarry ? carry[6 + v] : -1;              // …and the root of the chord it sat on
                int waveLen = Math.Max(2, m.WaveLength > 0 ? m.WaveLength : arcLen[v]); // notes per arc for the Vague contour
                string moves = contour == 7 ? LSystem(vnotes.Count) : null;
                int[] frac = contour == 8 ? FractalCurve(vnotes.Count, band0, rng, amp) : null;

                var gen = new List<(int start, int len, int midi)>();
                for (int idx = 0; idx < vnotes.Count; idx++)
                {
                    var n = vnotes[idx];
                    double absBeat = startBeat + n.Start / (double)spq;
                    if (!Harmony.ChordAt(project, resolve, absBeat, out int root, out int quality, out _)) continue;
                    var chordPcs = new HashSet<int>();
                    foreach (var p in PatternGenerator.ChordNotes(root, 4, quality, 0)) chordPcs.Add(((p % 12) + 12) % 12);
                    double phased = absBeat - pickup;
                    int band = band0 + (vnotes.Count > 1 ? slope * idx / (vnotes.Count - 1) : 0); // TENSION SLOPE drift

                    // ---- harmonic rules by METRIC position -----------------------------------------------------------
                    // fort (temps 1) = note d'accord (± appoggiature/retard résolus) ; demi-fort (temps fort secondaire)
                    // = idem, plus souple ; faible (autre temps sur le temps) = note d'accord ; entre deux temps = notes
                    // de passage / broderies / appoggiatures selon le contexte (a/b) ; syncope = accent déplacé = accord.
                    int midi;
                    bool forcedResolution = false;
                    if (forceMidi >= 0) { midi = forceMidi; forceMidi = -1; forcedResolution = true; }
                    else
                    {
                        int cls = ClassifyMetric(phased, meterNum, meterDen);       // 0 fort · 1 demi-fort · 2 faible · 3 entre-deux

                        // Context shared by the rules.
                        int beatStartSlice = (n.Start / beatSlices) * beatSlices;
                        bool beatStartsWithNote = starts.Contains(beatStartSlice);
                        var next = idx + 1 < vnotes.Count ? vnotes[idx + 1] : (RiffNote?)null;
                        bool nextContig = next.HasValue && next.Value.Start <= n.Start + n.Length; // no rest before the next note
                        int nextTarget = -1;
                        if (next.HasValue)
                        {
                            double nAbs = startBeat + next.Value.Start / (double)spq;
                            if (Harmony.ChordAt(project, resolve, nAbs, out int nr, out int nq, out _))
                            {
                                var nc = new HashSet<int>();
                                foreach (var p in PatternGenerator.ChordNotes(nr, 4, nq, 0)) nc.Add(((p % 12) + 12) % 12);
                                nextTarget = NearestTone(prev >= 0 ? prev : band, nc, band);
                            }
                        }

                        if (cls == 3)
                        {
                            // SYNCOPE : une note de contretemps qui se PROLONGE par-dessus le temps suivant devient un
                            // accent déplacé → elle doit être une note de l'accord courant, ou de l'accord suivant s'il
                            // change sur ce temps fort.
                            int nextBeat = (n.Start / beatSlices + 1) * beatSlices;
                            bool syncope = (n.Start % beatSlices) != 0 && n.Start + n.Length > nextBeat;
                            if (syncope)
                            {
                                var sPcs = new HashSet<int>(chordPcs);
                                double bAbs = startBeat + nextBeat / (double)spq;
                                if (Harmony.ChordAt(project, resolve, bAbs, out int br, out int bq, out _))
                                    foreach (var p in PatternGenerator.ChordNotes(br, 4, bq, 0)) sPcs.Add(((p % 12) + 12) % 12);
                                midi = NearestTone(prev >= 0 ? prev : band, sPcs.Count > 0 ? sPcs : chordPcs, band);
                            }
                            else
                                midi = PickBetween(prev, nextTarget, chordPcs, scalePcs, band, beatStartsWithNote, nextContig, rng);
                        }
                        else
                        {
                            // ON A BEAT (fort / demi-fort / faible) → a CHORD tone. The first fort/demi note may take the anchor.
                            HashSet<int> strongPcs = chordPcs;
                            bool anchorForced = false;
                            if (anchor > 0 && !anchorUsed && cls <= 1 && chordPcs.Count > 0)
                            {
                                int targetPc = (((root + AnchorGuide[Math.Min(anchor - 1, AnchorGuide.Length - 1)]) % 12) + 12) % 12;
                                int bestPc = -1, bestD = 99;
                                foreach (var p in chordPcs) { int d = PcDist(p, targetPc); if (d < bestD) { bestD = d; bestPc = p; } }
                                if (bestPc >= 0) { strongPcs = new HashSet<int> { bestPc }; anchorUsed = true; anchorForced = true; }
                            }
                            int chordChoice = PickTone(contour, ref dir, ref step, waveLen, prev, strongPcs, chordPcs, band, false, rng, idx, moves, frac, amp);

                            // Avoid RE-LANDING on the same note when the chord hasn't really moved (compared by ROOT, so
                            // Sol7 and Sol both count as "V"), so a static/held harmony makes the line MOVE instead of
                            // bouncing back. Two cases: adjacent on-beats within a bar (Ré … Ré), and — the one the ear
                            // notices most — two consecutive DOWNBEATS on the same chord, INCLUDING across a module
                            // boundary (bar 4 fort = bar 5 fort = Ré under V). A soft nudge to a different chord tone,
                            // only when the anchor didn't force this note.
                            bool repeatOnBeat  = chordChoice == lastStrongMidi && root == lastStrongRoot;
                            bool repeatDownbeat = cls == 0 && chordChoice == lastFortMidi && root == lastFortRoot;
                            if (!anchorForced && chordChoice >= 0 && chordPcs.Count > 1 && (repeatOnBeat || repeatDownbeat))
                            {
                                int alt = MovedChordTone(chordChoice, chordPcs, band, dir);
                                if (alt >= 0) chordChoice = alt;
                            }
                            lastStrongMidi = chordChoice; lastStrongRoot = root;
                            if (cls == 0) { lastFortMidi = chordChoice; lastFortRoot = root; }   // remember this downbeat (carried on)
                            midi = chordChoice;

                            // ACCENTED ORNAMENTS on fort/demi-fort — OFF by default (Ornaments = 0). A suspension (retard:
                            // hold prev if it is a step ABOVE a current chord tone, then resolve down onto it) or a proper
                            // APPOGGIATURA (a scale tone a step ABOVE a chord tone, resolving DOWN to it on the next note).
                            // Both keep the chord tone as the RESOLUTION; the ornament only delays it.
                            bool canOrn = ornaments > 0 && cls <= 1 && prev >= 0 && chordChoice >= 0 && next.HasValue && nextContig;
                            if (canOrn && rng.NextDouble() < (ornaments / 100.0) * (cls == 0 ? 0.55 : 0.30))
                            {
                                int sus = StepDownChordTone(prev, chordPcs);       // prev is a step above a chord tone → retard
                                if (sus >= 0 && Math.Abs(prev - sus) <= 2) { midi = prev; forceMidi = sus; }
                                else
                                {
                                    int appo = ScaleStepAbove(chordChoice, scalePcs);   // a step ABOVE the chord tone…
                                    if (appo >= 0) { midi = appo; forceMidi = chordChoice; }  // …resolving DOWN to it
                                }
                            }
                        }
                    }
                    if (midi < 0) continue;

                    // CONTINUITÉ: cap the leap from the previous note (favours a common tone at chord changes). An
                    // ornament's forced resolution is already a step away, so it is left untouched.
                    if (prev >= 0 && continuity > 0 && !forcedResolution)
                    {
                        int maxLeap = 12 - continuity * 11 / 100;                    // 0 → 12 (free) … 100 → 1 (very smooth)
                        var capPcs = chordPcs.Count > 0 ? chordPcs : scalePcs;
                        if (Math.Abs(midi - prev) > maxLeap) { int near = NearestTone(prev, capPcs, band); if (near >= 0) midi = near; }
                    }
                    prev = midi;
                    gen.Add((n.Start, n.Length, midi));
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

        // A note BETWEEN two beats — the a/b/else rules. (a) the beat started with a note: a passing tone toward the
        // next note, else a neighbour/repetition. (b) the beat started with a rest: an appoggiatura a step from the
        // next note (if it isn't followed by a rest). Otherwise: a chord tone.
        static int PickBetween(int prev, int nextTarget, HashSet<int> chordPcs, HashSet<int> scalePcs, int band, bool beatStartsWithNote, bool nextContig, Random rng)
        {
            var fallback = chordPcs.Count > 0 ? chordPcs : scalePcs;
            if (prev < 0) return NearestTone(band, fallback, band);
            if (beatStartsWithNote)
            {
                if (nextContig && nextTarget >= 0 && nextTarget != prev)
                {
                    int dir = Math.Sign(nextTarget - prev);
                    int pass = ToneInDir(prev, scalePcs, band, dir, true, 12);         // note de passage conjointe vers la cible
                    if (pass >= 0 && (dir > 0 ? pass < nextTarget : pass > nextTarget)) return pass;
                }
                int nb = ScaleStepNeighbor(prev, scalePcs, rng);                       // broderie / note conjointe, sinon répétition
                return (nb >= 0 && rng.NextDouble() < 0.6) ? nb : prev;
            }
            if (nextContig && nextTarget >= 0)                                         // (b) appoggiature vers la note suivante
            {
                int appo = ScaleStepNeighbor(nextTarget, scalePcs, rng);
                if (appo >= 0) return appo;
            }
            return NearestTone(prev, fallback, band);                                  // sinon : note de l'accord
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
