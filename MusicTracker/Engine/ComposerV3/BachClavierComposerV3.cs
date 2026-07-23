using System;
using System.Collections.Generic;
using System.Linq;
using MusicTracker.Engine.Score;
using MusicTracker.Engine.ComposerV2;   // kept data layer

namespace MusicTracker.Engine.ComposerV3
{
    /// <summary>
    /// Composer V2 — the BACH KEYBOARD style (Well-Tempered Clavier), driven by the <c>bach_clavier</c>
    /// model. Two movement types, the WTC pairing:
    ///   • PRELUDE — a two-hand moto-perpetuo figuration (RH continuous broken-chord 16ths over an LH bass),
    ///     à la WTC Bk I Prélude 1, over a functional plan with a circle-of-fifths episode and a Picardy close.
    ///   • FUGUE — a real fugal layout: a SUBJECT enters alone, then the ANSWER a fifth up (dominant) with a
    ///     moving COUNTERSUBJECT, then a third entry in the bass; sequential EPISODES (Fortspinnung) between
    ///     entries; a middle entry; and a final entry over a tonic PEDAL into an authentic cadence + Picardy.
    /// Functional harmony (the WTC model is genuine 2-4 voice polyphony, so its harmony is reliable); the free
    /// voices fill the prevailing chord's tones so the counterpoint stays consonant.
    /// </summary>
    public class BachClavierComposerV3 : BaseComposerV3
    {
        public override System.Collections.Generic.IReadOnlyList<string> Styles => new[] { "Prélude", "Fugue" };
        public override string FamilyKey => "bach";

        /// <summary>"prelude" or "fugue", or null = pick one.</summary>
        public string Movement { get; set; }
        public int ChosenTonicPc { get { return tonicPc; } }
        public bool ChosenMinor { get { return minor; } }
        public string ChosenMovement { get { return movement; } }

        string movement = "prelude";

        protected override void Configure()
        {
            base.Configure();
            PickCharacter();   // root context: nudges tempo + prelude/fugue choice
            meterNum = 4; meterDen = 4;

            // tonal major/minor (collapse modal detection noise)
            string mode = Pick(model.ModeDistribution, rng) ?? "ionian";
            minor = MusicMathV2.IsMinorMode(mode);
            scale = minor ? MusicMathV2.MinorScale : MusicMathV2.MajorScale;

            int[] tonics = { 0, 7, 2, 9, 5, 4 };          // C G D A F E (natural-letter keys)
            tonicPc = tonics[rng.Next(tonics.Length)];
            SetKeyLetters(tonicPc);

            double preludeProb = character == "enjouée" ? 0.62 : (character == "majestueux" ? 0.40 : 0.50);  // lively → favour the moto-perpetuo prelude
            movement = string.IsNullOrEmpty(Movement) ? (rng.NextDouble() < preludeProb ? "prelude" : "fugue") : Movement.Trim().ToLowerInvariant();
            bpm = movement == "fugue" ? 72 : 76;
            if (character == "enjouée") bpm *= 1.08; else if (character == "calme") bpm *= 0.90; else if (character == "majestueux") bpm *= 0.88;  // character tempo nudge

            if (movement == "fugue")
            {
                programs = new[] { 6, 6, 6 };             // 3 voices, harpsichord
                partNames = new[] { "Soprano", "Alto", "Basse" };
                partClefs = new[] { ScoreClefKind.Treble, ScoreClefKind.Treble, ScoreClefKind.Bass };
                partVolumes = new[] { 1.0, 0.85, 0.9 };
                title = "Bach Fugue (clavier)";
            }
            else
            {
                programs = new[] { 6, 6 };                // RH, LH, harpsichord
                partNames = new[] { "Clavier (RH)", "Clavier (LH)" };
                partClefs = new[] { ScoreClefKind.Treble, ScoreClefKind.Bass };
                partVolumes = new[] { 1.0, 0.9 };
                title = "Bach Prélude (clavier)";
            }
            partIsDrum = null;
        }

        protected override void Arrange()
        {
            cursor = movement == "fugue" ? RenderFugue() : RenderPrelude();
        }

        // ============ PRELUDE — two-hand broken-chord figuration ============
        int RenderPrelude()
        {
            int bars = 20;
            var plan = ClavierPlan(bars);
            int t = 0;
            for (int bar = 0; bar < plan.Count; bar++)
            {
                var cell = plan[bar];
                int abs = bar * Bar;
                int[] pcs = ChordPcs(cell);

                // RH: chord tones in the treble, arpeggiated up then down across the bar in sixteenths
                var rh = new List<int>();
                foreach (int pc in pcs) { int p = 60 + Mod12(Mod12(pc + tonicPc) - 60); if (p < 60) p += 12; while (p > 82) p -= 12; rh.Add(p); }
                rh = rh.Distinct().OrderBy(x => x).ToList();
                if (rh.Count >= 2) rh.Add(rh[0] + 12 <= 84 ? rh[0] + 12 : rh[rh.Count - 1]); // reach up an octave
                var wave = new List<int>(rh);
                for (int i = rh.Count - 2; i >= 1; i--) wave.Add(rh[i]);                       // …then back down
                for (int k = 0; k < Bar / 6; k++)
                {
                    int p = wave[k % wave.Count];
                    notes.Add(new OutNote { Part = 0, Pitch = p, Start = abs + k * 6, Len = 6, Vel = 64 + (k % 4 == 0 ? 6 : 0) });
                }

                // LH: bass root (+ fifth on beat 3), broad
                int root = 40 + Mod12(Mod12(cell.EffBass + tonicPc) - 40); while (root > 52) root -= 12;
                notes.Add(new OutNote { Part = 1, Pitch = root, Start = abs, Len = 2 * Beat, Vel = 70 });
                int fifth = NearestPitchAbove(root, Mod12(cell.Root + 7 + tonicPc)); if (fifth > 55) fifth -= 12;
                notes.Add(new OutNote { Part = 1, Pitch = fifth, Start = abs + 2 * Beat, Len = 2 * Beat, Vel = 64 });
            }
            t = plan.Count * Bar;
            // final tonic chord (Picardy), rolled, ringing
            var fin = VoicedTriad(0, true);
            for (int i = 0; i < fin.Count; i++) notes.Add(new OutNote { Part = 0, Pitch = fin[i], Start = t + i * 4, Len = 2 * Bar - i * 4, Vel = 66 });
            int bp = 40 + Mod12(tonicPc - 40); while (bp > 52) bp -= 12;
            notes.Add(new OutNote { Part = 1, Pitch = bp, Start = t, Len = 2 * Bar, Vel = 66 });
            return t + 2 * Bar;
        }

        // ============ FUGUE — staggered subject entries over a functional plan ============
        int RenderFugue()
        {
            // entry plan: roots chosen so a tonic subject fits an I-bar and the answer (a 5th up) fits a V-bar.
            int[] roots = { 0, 0, 7, 7, 0, 0,   5, 10, 3, 8,   0, 0,   2, 7,   0, 0, 7, 0 };
            var plan = new List<Cell>();
            for (int i = 0; i < roots.Length; i++)
            {
                bool finalTonic = i == roots.Length - 1;
                bool cadV = roots[i] == 7 && i >= roots.Length - 3;
                plan.Add(MakeCell(roots[i], cadV, finalTonic));
            }

            // SUBJECT (~2 bars, tonic-prolonging) + a COUNTERSUBJECT (complementary line, tonic)
            var tonicProg = new List<Cell> { MakeCell(0, false, false), MakeCell(0, false, false) };
            var subject = Trim2Bars(GenerateLine(model, mm, rng, tonicProg, tonicPc, scale, 57, 76, false, 0, "body"));
            var counter = Trim2Bars(GenerateLine(model, mm, rng, tonicProg, tonicPc, scale, 57, 76, false, 4, "body"));

            const int SOP = 0, ALT = 1, BASS = 2;

            // --- Exposition : voices ACCUMULATE (subject alone → +answer → +bass) ---
            PlaceLine(subject, 0, ALT, 0, 0, 78);                       // 1) subject ALONE, tonic (alto)
            PlaceLine(subject, 2, SOP, 4, 0, 80);                       // 2) answer a 5th up (dominant)
            PlaceLine(counter, 2, ALT, 4, 0, 62);                       //    countersubject (matched to the answer)
            PlaceLine(subject, 4, BASS, 0, -12, 76);                    // 3) subject, tonic, in the bass
            PlaceLine(counter, 4, SOP, 0, 0, 60);                       //    countersubject above (tonic)
            FillHarmony(plan, 4, 6, ALT, 55, 71, 50, new[] { 0, 1 });   // alto fills the 3rd voice at the bass entry

            // --- Episode 1 (circle of fifths) : sequence the subject head, walking bass ---
            var head = subject.Where(n => n.Start < Beat * 2).ToList();
            SequenceMotif(head, 6, 4, SOP, 0, 64);
            FillHarmony(plan, 6, 10, ALT, 55, 71, 48, new[] { 0, 1 });
            WalkBass(plan, 6, 10, BASS, 54);

            // --- Middle entry : subject in the tonic again, soprano, with moving alto ---
            PlaceLine(subject, 10, SOP, 0, 0, 80);
            PlaceLine(counter, 10, ALT, 0, 0, 58);
            WalkBass(plan, 10, 12, BASS, 54);

            // --- Episode 2 ---
            SequenceMotif(head, 12, 2, ALT, 0, 60);
            FillHarmony(plan, 12, 14, SOP, 67, 83, 46, new[] { 0, 1 });
            WalkBass(plan, 12, 14, BASS, 54);

            // --- Final entry over a TONIC PEDAL → cadence + Picardy ---
            PedalBass(14, 4, BASS, 58);                                 // tonic pedal, bars 14-17
            PlaceLine(subject, 14, SOP, 0, 0, 84);                      // triumphant final statement
            FillHarmony(plan, 14, 18, ALT, 55, 71, 50, new[] { 0, 1 });

            return plan.Count * Bar;
        }

        // place a melodic line at a bar offset, transposed by scaleSteps (+4 = a fifth up) and octaveShift
        void PlaceLine(List<MelNote> line, int startBar, int part, int scaleSteps, int octaveShift, int vel)
        {
            int lo = part == 2 ? 36 : (part == 1 ? 52 : 60);
            int hi = part == 2 ? 57 : (part == 1 ? 74 : 86);
            foreach (var n in line)
            {
                int p = TransposeInScale(n.Pitch, scaleSteps, tonicPc, scale) + 12 * octaveShift;
                while (p > hi) p -= 12; while (p < lo) p += 12;
                notes.Add(new OutNote { Part = part, Pitch = p, Start = startBar * Bar + n.Start, Len = n.Len, Vel = vel });
            }
        }

        // sequence a short motif downward a scale step each bar (Fortspinnung)
        void SequenceMotif(List<MelNote> motif, int startBar, int nBars, int part, int octaveShift, int vel)
        {
            int lo = part == 2 ? 36 : (part == 1 ? 52 : 60), hi = part == 2 ? 57 : (part == 1 ? 74 : 86);
            for (int b = 0; b < nBars; b++)
                foreach (var n in motif)
                {
                    int p = TransposeInScale(n.Pitch, -b, tonicPc, scale) + 12 * octaveShift;
                    while (p > hi) p -= 12; while (p < lo) p += 12;
                    notes.Add(new OutNote { Part = part, Pitch = p, Start = (startBar + b) * Bar + n.Start, Len = n.Len, Vel = vel });
                }
        }

        // a free voice playing the prevailing chord's tones (sustained half notes), consonant filler
        void FillHarmony(List<Cell> plan, int fromBar, int toBar, int part, int lo, int hi, int vel, int[] halves)
        {
            for (int bar = fromBar; bar < toBar && bar < plan.Count; bar++)
            {
                int[] pcs = ChordPcs(plan[bar]);
                int abs = bar * Bar;
                if (halves == null)
                {
                    int p = PlaceInRange(Mod12(plan[bar].Root + tonicPc), lo, hi);
                    notes.Add(new OutNote { Part = part, Pitch = p, Start = abs, Len = Bar, Vel = vel });
                }
                else
                {
                    for (int h = 0; h < 2; h++)
                    {
                        int pc = pcs[(bar + h) % pcs.Length];
                        int p = PlaceInRange(Mod12(pc + tonicPc), lo, hi);
                        notes.Add(new OutNote { Part = part, Pitch = p, Start = abs + h * 2 * Beat, Len = 2 * Beat, Vel = vel });
                    }
                }
            }
        }

        // walking bass: chord root then a stepwise approach to the next root, in quarters
        void WalkBass(List<Cell> plan, int fromBar, int toBar, int part, int vel)
        {
            for (int bar = fromBar; bar < toBar && bar < plan.Count; bar++)
            {
                int abs = bar * Bar;
                int root = PlaceInRange(Mod12(plan[bar].EffBass + tonicPc), 38, 55);
                int nextRoot = PlaceInRange(Mod12((bar + 1 < plan.Count ? plan[bar + 1].EffBass : plan[bar].EffBass) + tonicPc), 38, 55);
                int third = NearestPitchAbove(root, Mod12(plan[bar].Root + (DiatonicQuality(plan[bar].Root) == "min" || DiatonicQuality(plan[bar].Root) == "dim" ? 3 : 4) + tonicPc));
                int fifth = NearestPitchAbove(root, Mod12(plan[bar].Root + 7 + tonicPc));
                int approach = ScaleStepDir(nextRoot, -1, tonicPc, scale);
                int[] pat = { root, third > 55 ? third - 12 : third, fifth > 55 ? fifth - 12 : fifth, approach };
                for (int b = 0; b < 4; b++) { int p = pat[b]; while (p < 36) p += 12; while (p > 55) p -= 12; notes.Add(new OutNote { Part = part, Pitch = p, Start = abs + b * Beat, Len = Beat, Vel = vel }); }
            }
        }

        // a held/re-struck tonic pedal in the bass for nBars
        void PedalBass(int fromBar, int nBars, int part, int vel)
        {
            int p = PlaceInRange(tonicPc, 36, 50);
            for (int bar = fromBar; bar < fromBar + nBars; bar++)
                notes.Add(new OutNote { Part = part, Pitch = p, Start = bar * Bar, Len = Bar, Vel = vel });
        }

        // ---------- helpers (functional plan + voicing) ----------
        List<Cell> ClavierPlan(int bodyBars)
        {
            int[] circle = minor ? new[] { 0, 5, 10, 3, 8, 2, 7 } : new[] { 0, 5, 11, 4, 9, 2, 7 };
            var roots = new List<int> { 0 };
            int ci = 1;
            while (roots.Count < Math.Max(2, bodyBars - 4)) { roots.Add(circle[ci % circle.Length]); ci++; }
            roots.Add(2); roots.Add(7); roots.Add(7); roots.Add(0);
            var cells = new List<Cell>();
            for (int i = 0; i < roots.Count; i++)
            {
                bool finalTonic = i == roots.Count - 1;
                bool cadV = roots[i] == 7 && i >= roots.Count - 3;
                cells.Add(MakeCell(roots[i], cadV, finalTonic));
            }
            return cells;
        }

        Cell MakeCell(int root, bool cadentialDominant, bool finalTonic)
        {
            string g;
            if (finalTonic) g = "maj";                                   // Picardy / major tonic
            else if (cadentialDominant) g = rng.NextDouble() < 0.5 ? "dom7" : "maj";
            else g = DiatonicQuality(root);
            return new Cell { Root = root, Canon = GroupToCanon(g) };
        }

        string DiatonicQuality(int root)
        {
            int r = Mod12(root);
            if (minor) { switch (r) { case 0: return "min"; case 2: return "dim"; case 3: return "maj"; case 5: return "min"; case 7: return "min"; case 8: return "maj"; case 10: return "maj"; default: return "min"; } }
            switch (r) { case 0: return "maj"; case 2: return "min"; case 4: return "min"; case 5: return "maj"; case 7: return "maj"; case 9: return "min"; case 11: return "dim"; default: return "maj"; }
        }

        List<int> VoicedTriad(int rootDeg, bool major)
        {
            int third = major ? 4 : 3;
            int[] pcs = { rootDeg, Mod12(rootDeg + third), Mod12(rootDeg + 7) };
            var v = new List<int>();
            foreach (int pc in pcs) { int p = PlaceInRange(Mod12(pc + tonicPc), 60, 82); v.Add(p); if (p + 12 <= 84) v.Add(p + 12); }
            return v.Distinct().OrderBy(x => x).ToList();
        }

        static List<MelNote> Trim2Bars(List<MelNote> line)
        {
            var o = line.Where(n => n.Start < 2 * Bar).ToList();
            if (o.Count == 0 && line.Count > 0) o.Add(line[0]);
            return o;
        }

        static int PlaceInRange(int pc, int lo, int hi) { int p = lo + Mod12(pc - lo); while (p > hi) p -= 12; while (p < lo) p += 12; return p; }
        static int NearestPitchAbove(int from, int pc) { return from + (((pc - from) % 12) + 12) % 12; }

        void SetKeyLetters(int pc)
        {
            tonicAccidental = 0;
            switch (Mod12(pc))
            {
                case 0: tonicLetter = 0; break; case 2: tonicLetter = 1; break; case 4: tonicLetter = 2; break;
                case 5: tonicLetter = 3; break; case 7: tonicLetter = 4; break; case 9: tonicLetter = 5; break;
                case 11: tonicLetter = 6; break; default: tonicLetter = 0; break;
            }
        }
    }
}
