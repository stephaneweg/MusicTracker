using System;
using System.Collections.Generic;
using System.Linq;
using MusicTracker.Engine.Score;
using MusicTracker.Engine.ComposerV2;   // kept data layer

namespace MusicTracker.Engine.ComposerV3
{
    /// <summary>
    /// Composer V2 — the VIVALDI concerto style, driven by the <c>vivaldi</c> model. A RITORNELLO allegro:
    /// the orchestral ritornello (TUTTI: solo line doubled in parallel 3rds/6ths by the ripieno, over a
    /// driving repeated-note MOTOR bass + harpsichord continuo) alternates with SOLO episodes (the solo
    /// violin alone over light continuo — the ripieno rests — spinning virtuosic sixteenth figuration through
    /// circle-of-fifths SEQUENCES). The ritornello returns transposed (dominant, subdominant) then home.
    /// Functional I-IV-V harmony, triadic arpeggiation — the Italian-concerto signature, distinct from Bach's
    /// dense counterpoint.
    /// </summary>
    public class VivaldiComposerV3 : BaseComposerV3
    {
        // MOVEMENTS of the corpus (Four Seasons spans Largo→Presto): each maps to a character + tempo (see Configure).
        public override System.Collections.Generic.IReadOnlyList<string> Styles => new[] { "Vivace (allegro)", "Modéré", "Lent (largo)" };
        public override string FamilyKey => "vivaldi";

        public int ChosenTonicPc { get { return tonicPc; } }
        public bool ChosenMinor { get { return minor; } }

        const int SOLO = 0, RIP = 1, HARP = 2, BASS = 3;

        protected override void Configure()
        {
            base.Configure();
            // The chosen MOVEMENT (style) drives the character — a concerto movement IS its tempo/mood. Overrides the
            // Mood pick so selecting "Lent" really gives a slow movement, "Vivace" a fast one.
            if (!string.IsNullOrEmpty(CurrentStyle))
            {
                string s = CurrentStyle.ToLowerInvariant();
                if (s.Contains("vivace") || s.Contains("allegro") || s.Contains("presto")) CharacterOverride = "enjouee";
                else if (s.Contains("lent") || s.Contains("largo") || s.Contains("adagio")) CharacterOverride = "calme";
                else if (s.Contains("mod")) CharacterOverride = "moderee";
            }
            PickCharacter();   // root context: sets the movement's tempo feel (allegro vs slow movement)
            meterNum = 4; meterDen = 4;
            if (character == "enjouée") bpm = 120 + rng.Next(16);        // 120-135 vivace
            else if (character == "calme") bpm = 60 + rng.Next(22);      // 60-81 largo/andante (slow movement)
            else if (character == "majestueux") bpm = 88 + rng.Next(16); // 88-103 stately
            else bpm = 104 + rng.Next(16);                               // 104-119 moderate allegro

            string mode = Pick(model.ModeDistribution, rng) ?? "ionian";
            minor = MusicMathV2.IsMinorMode(mode);
            scale = minor ? MusicMathV2.MinorScale : MusicMathV2.MajorScale;

            int[] tonics = { 0, 7, 2, 9, 5 };               // C G D A F
            tonicPc = tonics[rng.Next(tonics.Length)];
            SetKeyLetters(tonicPc);

            programs = new[] { 40, 48, 6, 43 };             // solo violin, ripieno strings, harpsichord, continuo bass
            partNames = new[] { "Violon solo", "Cordes (ripieno)", "Clavecin", "Basse continue" };
            partClefs = new[] { ScoreClefKind.Treble, ScoreClefKind.Treble, ScoreClefKind.Treble, ScoreClefKind.Bass };
            partVolumes = new[] { 1.0, 0.8, 0.6, 0.85 };
            title = "Vivaldi (concerto allegro)";
            partIsDrum = null;
        }

        // Melody now comes from the LEARNED model (GetNote → GenerateLine), not hand-coded figuration: the coded
        // sixteenth waves made every melody sound the same. The character/movement + corpus rhythm cells drive the
        // virtuosity instead. (The bespoke ritornello Arrange below still figures, but the Orchestrateur path uses GetNote.)
        public override bool GeneratesOwnMelody => false;

        // The posed-form per-section melody: continuous sixteenths rolling up-then-down the chord tones (the solo brilliance),
        // over the form's chords. Same figuration as SoloEpisode, but driven by the supplied progression.
        protected override List<MelNote> SectionMelody(List<Cell> prog, int lo, int hi, string section)
        {
            var outl = new List<MelNote>();
            int hiR = System.Math.Min(92, System.Math.Max(hi, 82));
            for (int bar = 0; bar < prog.Count; bar++)
            {
                var lad = Ladder(prog[bar], tonicPc, 64, hiR);
                if (lad.Count < 2) lad.Add(System.Math.Min(hiR, lad[0] + 12));
                var wave = new List<int>(lad);
                for (int i = lad.Count - 2; i >= 1; i--) wave.Add(lad[i]);
                int abs = bar * Bar;
                for (int k = 0; k < Bar / 6; k++)
                    outl.Add(new MelNote { Pitch = wave[k % wave.Count], Start = abs + k * 6, Len = 6, VelOffset = (k % 4 == 0 ? 6 : 0) });
            }
            return outl;
        }

        protected override void Arrange()
        {
            var ritPlan = new List<Cell> { MakeCell(0, false), MakeCell(5, false), MakeCell(7, true), MakeCell(0, false) }; // I-IV-V-I
            var rit2 = ritPlan.GetRange(0, 2);
            var theme = Trim(GenerateLine(model, mm, rng, ritPlan, tonicPc, scale, 67, 86, true, 0, "body"), 4 * Bar);
            var theme2 = theme.Where(n => n.Start < 2 * Bar).ToList();
            int relOffset = minor ? 3 : 9;                  // R3 region: relative major (minor) / submediant (major)

            int t = 0;
            t = Ritornello(theme, ritPlan, t, 0, true);     // R1 — home, tutti
            t = SoloEpisode(t, 0);                          // S1
            t = Ritornello(theme2, rit2, t, 7, true);       // R2 — dominant
            t = SoloEpisode(t, 7);                           // S2 — sequences off the dominant
            t = Ritornello(theme2, rit2, t, relOffset, true); // R3 — relative / submediant
            t = SoloEpisode(t, 0);                          // S3 — home-bound
            t = Ritornello(theme, ritPlan, t, 0, true);     // R4 — home, full
            t = FinalCadence(t);
            cursor = t;
        }

        // ---- TUTTI ritornello: melody + parallel-3rd ripieno + harpsichord chords + motor bass ----
        int Ritornello(List<MelNote> theme, List<Cell> plan, int startSlice, int offset, bool full)
        {
            int loc = tonicPc + offset;
            int startBar = startSlice / Bar;
            // solo line (transposed) + ripieno a third/sixth below in chord tones
            foreach (var n in theme)
            {
                int p = n.Pitch + offset; while (p > 88) p -= 12; while (p < 60) p += 12;
                notes.Add(new OutNote { Part = SOLO, Pitch = p, Start = startSlice + n.Start, Len = n.Len, Vel = AccentVel(96, n.Start) });
                if (full)
                {
                    var cell = plan[Math.Min(plan.Count - 1, n.Start / Bar)];
                    int par = NearestChordTone(p - 4, ChordPcs(cell), loc);   // a 3rd/6th below, snapped to the chord
                    while (par > 79) par -= 12; while (par < 55) par += 12;
                    notes.Add(new OutNote { Part = RIP, Pitch = par, Start = startSlice + n.Start, Len = n.Len, Vel = AccentVel(80, n.Start) });
                }
            }
            HarpChords(plan, startBar, offset, 60);
            MotorBass(plan, startBar, offset, 84);
            return startSlice + plan.Count * Bar;
        }

        // ---- SOLO episode: solo violin virtuosic 16ths over a circle-of-fifths sequence; ripieno tacet ----
        int SoloEpisode(int startSlice, int offset)
        {
            int loc = tonicPc + offset;
            // descending-fifths sequence ending on the local tonic: vi–ii–V–I (major) / corresponding minor
            int[] roots = minor ? new[] { 8, 5, 7, 0 } : new[] { 9, 2, 7, 0 };
            var plan = new List<Cell>();
            for (int i = 0; i < roots.Length; i++) plan.Add(MakeCell(roots[i], roots[i] == 7));
            int startBar = startSlice / Bar;

            for (int bar = 0; bar < plan.Count; bar++)
            {
                var cell = plan[bar];
                int abs = startSlice + bar * Bar;
                var lad = Ladder(cell, loc, 64, 88);
                if (lad.Count < 2) lad.Add(Math.Min(88, lad[0] + 12));
                // continuous sixteenths: up-then-down arpeggio wave (Vivaldi solo brilliance)
                var wave = new List<int>(lad);
                for (int i = lad.Count - 2; i >= 1; i--) wave.Add(lad[i]);
                for (int k = 0; k < Bar / 6; k++)
                {
                    int p = wave[k % wave.Count];
                    notes.Add(new OutNote { Part = SOLO, Pitch = p, Start = abs + k * 6, Len = 6, Vel = 88 + (k % 4 == 0 ? 6 : 0) });
                }
            }
            // light continuo only (ripieno rests → the solo/tutti contrast)
            HarpChords(plan, startBar, offset, 44);
            ContinuoBassWalk(plan, startBar, offset, 70);
            return startSlice + plan.Count * Bar;
        }

        // repeated-note MOTOR bass: eight driving eighths on the chord root, last one steps to the next root
        void MotorBass(List<Cell> plan, int startBar, int offset, int vel)
        {
            int loc = tonicPc + offset;
            for (int bar = 0; bar < plan.Count; bar++)
            {
                int abs = (startBar + bar) * Bar;
                int root = PlaceInRange(Mod12(plan[bar].EffBass + loc), 36, 52);
                int nextRoot = PlaceInRange(Mod12((bar + 1 < plan.Count ? plan[bar + 1].EffBass : plan[bar].EffBass) + loc), 36, 52);
                int approach = ScaleStepDir(nextRoot, nextRoot >= root ? -1 : 1, tonicPc, scale);
                for (int k = 0; k < 8; k++)
                {
                    int p = (k == 7) ? approach : root; while (p < 36) p += 12; while (p > 54) p -= 12;
                    notes.Add(new OutNote { Part = BASS, Pitch = p, Start = abs + k * 12, Len = 11, Vel = vel + (k % 2 == 0 ? 4 : -4) });
                }
            }
        }

        // walking continuo bass (quarter notes) for the lighter solo episodes
        void ContinuoBassWalk(List<Cell> plan, int startBar, int offset, int vel)
        {
            int loc = tonicPc + offset;
            for (int bar = 0; bar < plan.Count; bar++)
            {
                int abs = (startBar + bar) * Bar;
                int root = PlaceInRange(Mod12(plan[bar].EffBass + loc), 36, 52);
                int fifth = NearestPitchAbove(root, Mod12(plan[bar].Root + 7 + loc)); if (fifth > 55) fifth -= 12;
                int nextRoot = PlaceInRange(Mod12((bar + 1 < plan.Count ? plan[bar + 1].EffBass : plan[bar].EffBass) + loc), 36, 52);
                int approach = ScaleStepDir(nextRoot, -1, tonicPc, scale);
                int[] pat = { root, root, fifth, approach };
                for (int b = 0; b < 4; b++) { int p = pat[b]; while (p < 36) p += 12; while (p > 54) p -= 12; notes.Add(new OutNote { Part = BASS, Pitch = p, Start = abs + b * Beat, Len = Beat, Vel = vel }); }
            }
        }

        // harpsichord continuo: broken-chord eighths, mid register
        void HarpChords(List<Cell> plan, int startBar, int offset, int vel)
        {
            int loc = tonicPc + offset;
            for (int bar = 0; bar < plan.Count; bar++)
            {
                if (GuitarFingerstyle) { RenderFingerstyleBar(notes, plan[bar], (startBar + bar) * Bar, loc, vel); continue; }
                if (LearnedAccomp) { RenderLearnedAccompBar(notes, plan[bar], (startBar + bar) * Bar, loc, vel); continue; }
                var v = new List<int>();
                foreach (int pc in ChordPcs(plan[bar])) { int p = 52 + Mod12(Mod12(pc + loc) - 52); while (p < 52) p += 12; while (p > 74) p -= 12; v.Add(p); }
                v = v.Distinct().OrderBy(x => x).ToList();
                if (v.Count == 0) continue;
                int abs = (startBar + bar) * Bar, k = 0;
                for (int pos = 0; pos < Bar; pos += 12) { notes.Add(new OutNote { Part = HARP, Pitch = v[k % v.Count], Start = abs + pos, Len = 12, Vel = vel }); k++; }
            }
        }

        int FinalCadence(int startSlice)
        {
            int startBar = startSlice / Bar;
            // V then a tutti tonic (Picardy) chord, ringing
            var cad = new List<Cell> { MakeCell(7, true), MakeCell(0, false) };
            HarpChords(cad, startBar, 0, 58);
            MotorBass(cad, startBar, 0, 80);
            int t = startSlice + 2 * Bar;
            var fin = new List<int>();
            foreach (int pc in new[] { 0, 4, 7 }) { int p = PlaceInRange(Mod12(pc + tonicPc), 67, 88); fin.Add(p); }
            fin = fin.Distinct().OrderBy(x => x).ToList();
            for (int i = 0; i < fin.Count; i++) notes.Add(new OutNote { Part = SOLO, Pitch = fin[i], Start = t + i * 4, Len = 2 * Bar, Vel = 92 });
            foreach (int pc in new[] { 0, 7 }) { int p = PlaceInRange(Mod12(pc + tonicPc), 55, 74); notes.Add(new OutNote { Part = RIP, Pitch = p, Start = t, Len = 2 * Bar, Vel = 76 }); }
            int bp = PlaceInRange(tonicPc, 36, 50);
            notes.Add(new OutNote { Part = BASS, Pitch = bp, Start = t, Len = 2 * Bar, Vel = 86 });
            return t + 2 * Bar;
        }

        // ---------- helpers ----------
        List<int> Ladder(Cell cell, int localTonic, int lo, int hi)
        {
            var abs = new List<int>();
            foreach (int pc in ChordPcs(cell)) abs.Add(Mod12(pc + localTonic));
            int bass = PlaceInRange(Mod12(cell.EffBass + localTonic), lo, lo + 11);
            var lad = new List<int> { bass };
            int p = bass;
            for (int i = 0; i < 6; i++) { int nx = -1; for (int d = 1; d <= 12; d++) if (abs.Contains(Mod12(p + d))) { nx = p + d; break; } if (nx < 0 || nx > hi) break; lad.Add(nx); p = nx; }
            return lad;
        }

        Cell MakeCell(int root, bool cadentialDominant)
        {
            string g = cadentialDominant ? (rng.NextDouble() < 0.5 ? "dom7" : "maj") : DiatonicQuality(root);
            return new Cell { Root = root, Canon = GroupToCanon(g) };
        }

        string DiatonicQuality(int root)
        {
            int r = Mod12(root);
            if (minor) { switch (r) { case 0: return "min"; case 2: return "dim"; case 3: return "maj"; case 5: return "min"; case 7: return "maj"; case 8: return "maj"; case 10: return "maj"; default: return "min"; } }
            switch (r) { case 0: return "maj"; case 2: return "min"; case 4: return "min"; case 5: return "maj"; case 7: return "maj"; case 9: return "min"; case 11: return "dim"; default: return "maj"; }
        }

        static List<MelNote> Trim(List<MelNote> line, int maxStart) { var o = line.Where(n => n.Start < maxStart).ToList(); if (o.Count == 0 && line.Count > 0) o.Add(line[0]); return o; }
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
