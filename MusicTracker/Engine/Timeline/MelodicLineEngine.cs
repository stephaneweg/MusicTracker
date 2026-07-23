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

        /// <param name="carry">Optional per-voice pitch of the PREVIOUS module's last note (cross-module continuity): the
        /// line starts near it and, on return, holds this module's last note per voice. Null = independent module.</param>
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
            var rng = new Random(unchecked(1013 * (m.Notes.Count + 7) + contour * 131 + (int)Math.Round(startBeat)));
            int[] arcLen = { 4, 6, 5 };
            int totalSlices = Math.Max(1, m.BeatsPerBar) * spq;
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
                string moves = contour == 7 ? LSystem(vnotes.Count) : null;
                int[] frac = contour == 8 ? FractalCurve(vnotes.Count, band0, rng) : null;

                var gen = new List<(int start, int len, int midi)>();
                for (int idx = 0; idx < vnotes.Count; idx++)
                {
                    var n = vnotes[idx];
                    double absBeat = startBeat + n.Start / (double)spq;
                    if (!Harmony.ChordAt(project, resolve, absBeat, out int root, out int quality, out _)) continue;
                    var chordPcs = new HashSet<int>();
                    foreach (var p in PatternGenerator.ChordNotes(root, 4, quality, 0)) chordPcs.Add(((p % 12) + 12) % 12);
                    double phased = absBeat - pickup;
                    bool strong = Math.Abs(phased - Math.Round(phased)) < 1e-6;
                    int band = band0 + (vnotes.Count > 1 ? slope * idx / (vnotes.Count - 1) : 0); // TENSION SLOPE drift
                    HashSet<int> strongPcs = chordPcs;
                    if (anchor > 0 && !anchorUsed && strong && chordPcs.Count > 0)
                    {
                        int targetPc = (((root + AnchorGuide[Math.Min(anchor - 1, AnchorGuide.Length - 1)]) % 12) + 12) % 12;
                        int bestPc = -1, bestD = 99;
                        foreach (var p in chordPcs) { int d = PcDist(p, targetPc); if (d < bestD) { bestD = d; bestPc = p; } }
                        if (bestPc >= 0) { strongPcs = new HashSet<int> { bestPc }; anchorUsed = true; }
                    }
                    var pcs = strong ? strongPcs : scalePcs;
                    int midi = PickTone(contour, ref dir, ref step, arcLen[v], prev, pcs, chordPcs, band, !strong, rng, idx, moves, frac);
                    if (midi < 0) continue;
                    // CONTINUITÉ: cap the leap from the previous note (incl. the note CARRIED from the previous module) by
                    // pulling toward the nearest allowed tone — which naturally favours a COMMON tone at a chord change.
                    if (prev >= 0 && continuity > 0)
                    {
                        int maxLeap = 12 - continuity * 11 / 100;                    // 0 → 12 (free) … 100 → 1 (very smooth)
                        if (Math.Abs(midi - prev) > maxLeap) { int near = NearestTone(prev, pcs, band); if (near >= 0) midi = near; }
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
        static int PickTone(int contour, ref int dir, ref int step, int arcLen, int prev, HashSet<int> pcs, HashSet<int> chordPcs, int band, bool requireStep, Random rng, int idx, string moves, int[] frac)
        {
            int midi;
            switch (contour)
            {
                case 6: // Thue-Morse — aperiodic self-similar up/down (never exactly repeats → kills the "parrot")
                    dir = ThueMorse(idx) == 0 ? 1 : -1;
                    midi = ToneInDir(prev, pcs, band, dir, requireStep);
                    if (midi < 0) { dir = -dir; midi = ToneInDir(prev, pcs, band, dir, requireStep); }
                    if (midi < 0) midi = NearestTone(prev, chordPcs, band);
                    return midi;
                case 7: // L-system — recursive rewrite → a self-similar (fractal) melodic contour
                    dir = (moves != null && moves.Length > 0 && moves[idx % moves.Length] == 'D') ? -1 : 1;
                    midi = ToneInDir(prev, pcs, band, dir, requireStep);
                    if (midi < 0) { dir = -dir; midi = ToneInDir(prev, pcs, band, dir, requireStep); }
                    if (midi < 0) midi = NearestTone(prev, chordPcs, band);
                    return midi;
                case 8: // Fractal 1/f — a midpoint-displacement guide curve; the harmony picks the nearest tone to it
                {
                    int target = (frac != null && idx < frac.Length) ? frac[idx] : band;
                    midi = NearestTone(target, pcs, band);
                    if (midi < 0) midi = NearestTone(target, chordPcs, band);
                    return midi;
                }
                case 1: // Montante / 2 Descendante — steady climb/fall; at the band edge, jump to the far edge and continue
                case 2:
                {
                    int d = contour == 1 ? 1 : -1;
                    midi = ToneInDir(prev, pcs, band, d, requireStep);
                    if (midi < 0) midi = NearestTone(contour == 1 ? band - 12 : band + 12, pcs, band);
                    if (midi < 0) midi = NearestTone(prev, chordPcs, band);
                    return midi;
                }
                case 3: // Statique — the nearest available tone to the previous note (minimal motion, hovers/repeats)
                    midi = NearestTone(prev, pcs, band);
                    if (midi < 0) midi = NearestTone(prev, chordPcs, band);
                    return midi;
                case 4: // Zigzag — flip direction on EVERY note (angular)
                    dir = -dir;
                    midi = ToneInDir(prev, pcs, band, dir, requireStep);
                    if (midi < 0) { dir = -dir; midi = ToneInDir(prev, pcs, band, dir, requireStep); }
                    if (midi < 0) midi = NearestTone(prev, chordPcs, band);
                    return midi;
                case 5: // Aléatoire — a seeded random direction each note (a controlled walk)
                    dir = rng.Next(2) == 0 ? 1 : -1;
                    midi = ToneInDir(prev, pcs, band, dir, requireStep);
                    if (midi < 0) { dir = -dir; midi = ToneInDir(prev, pcs, band, dir, requireStep); }
                    if (midi < 0) midi = NearestTone(prev, chordPcs, band);
                    return midi;
                default: // 0 = Arc (wave): flip direction every arcLen notes, bounce at the band edges
                    if (++step % arcLen == 0) dir = -dir;
                    midi = ToneInDir(prev, pcs, band, dir, requireStep);
                    if (midi < 0) { dir = -dir; midi = ToneInDir(prev, pcs, band, dir, requireStep); }
                    if (midi < 0) midi = NearestTone(prev, chordPcs, band);
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
        static int[] FractalCurve(int n, int band, Random rng)
        {
            if (n <= 0) return new int[0];
            int size = 2; while (size + 1 < n) size *= 2; size += 1;   // 2^k + 1 ≥ n
            var h = new double[size];
            double amp = 8;
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
                double d = Math.Max(-12, Math.Min(12, h[xi]));
                outp[i] = band + (int)Math.Round(d);
            }
            return outp;
        }

        // The next tone (pc in `pcs`) going in direction `dir` from the previous note, within the register band. For the
        // FIRST note, the nearest tone to the band centre. requireStep = a passing move: reject a jump > 2 semitones.
        static int ToneInDir(int prevMidi, HashSet<int> pcs, int bandCenter, int dir, bool requireStep)
        {
            if (pcs.Count == 0) return -1;
            if (prevMidi < 0) return NearestTone(-1, pcs, bandCenter);
            int lo = bandCenter - 12, hi = bandCenter + 12;
            for (int mi = prevMidi + dir; mi >= lo && mi <= hi; mi += dir)
                if (pcs.Contains((((mi % 12) + 12) % 12)))
                    return (requireStep && Math.Abs(mi - prevMidi) > 2) ? -1 : mi;   // step ≤ 2 semitones on a weak note
            return -1;                                                              // hit the band edge
        }

        // Nearest MIDI whose pc is in `pcs`, close to the previous note (or the band centre for the 1st note), in band.
        static int NearestTone(int prevMidi, HashSet<int> pcs, int bandCenter)
        {
            if (pcs.Count == 0) return -1;
            int anchor = prevMidi >= 0 ? prevMidi : bandCenter, best = -1, bestDist = int.MaxValue;
            for (int mi = bandCenter - 12; mi <= bandCenter + 12; mi++)
            {
                if (!pcs.Contains((((mi % 12) + 12) % 12))) continue;
                int d = Math.Abs(mi - anchor);
                if (d < bestDist) { bestDist = d; best = mi; }
            }
            return best;
        }
    }
}
