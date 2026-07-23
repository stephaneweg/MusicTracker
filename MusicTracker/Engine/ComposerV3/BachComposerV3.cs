using System;
using System.Collections.Generic;
using System.Linq;
using MusicTracker.Engine.Score;
using MusicTracker.Engine.ComposerV2;   // kept data layer

namespace MusicTracker.Engine.ComposerV3
{
    /// <summary>
    /// Composer V2 — the BACH SOLO style (cello suites, violin sonatas/partitas, flute partita),
    /// derived from <see cref="BaseComposerV3"/> and driven by the <c>bach_solo</c> corpus model
    /// (harmony qualities + the melodic chains). On top of the model it layers what is idiomatic of
    /// Bach's unaccompanied (and continuo) solo writing:
    ///   • IMPLIED POLYPHONY / compound melody — one line that outlines two voices by alternating a low
    ///     pedal (the implied bass) with an upper broken-chord voice (the cello-suite-prelude shape);
    ///   • MOTO PERPETUO — a prelude of continuous sixteenths (Fortspinnung);
    ///   • SEQUENCE — a diatonic descending-fifths circle (the Bach spinning-forth episode);
    ///   • DOMINANT PEDAL / bariolage — a fixed ringing note alternating with a moving voice before the close;
    ///   • functional harmony with a real leading-tone dominant, an authentic cadence and a PICARDY third;
    ///   • optional BASSO CONTINUO — a melodic solo line over a realized harpsichord + a walking bass.
    /// Solo (unaccompanied) = the implied-polyphony prelude; with continuo = melodic line + BC.
    /// </summary>
    public class BachComposerV3 : BaseComposerV3
    {
        /// <summary>The movements/dances this style exposes (the dialog's "Style" list).</summary>
        public override System.Collections.Generic.IReadOnlyList<string> Styles =>
            new[] { "Prélude", "Fugue", "Toccata", "Allemande", "Courante", "Sarabande", "Gigue", "Menuet", "Bourrée", "Gavotte", "Chaconne" };
        public override string FamilyKey => "bach";

        /// <summary>"violin" / "cello" / "flute", or null = pick one (weighted to the corpus).</summary>
        public string Instrument { get; set; }
        /// <summary>false = unaccompanied solo (implied polyphony); true = solo + basso continuo.</summary>
        public bool WithContinuo { get; set; }
        /// <summary>Movement / dance whose meter + characteristic rhythm to use: "prelude", "allemande",
        /// "courante", "sarabande", "gigue", "menuet", "bourree", "gavotte", "chaconne". null = pick one.</summary>
        public string Movement { get; set; }

        int loSolo, hiSolo;     // full instrument range (the figuration's implied bass reaches the bottom)
        int pedalBar = -1;      // bar rendered as a dominant pedal / bariolage
        int barSlices = 96, beatUnit = 24, beatsPerBar = 4;  // meter-derived (a 3/4 bar = 72, a 6/8 bar = 72 in 2)
        string dance = "prelude";

        /// <summary>Key + movement chosen at Configure (valid after Compose) — for callers/tests/UI.</summary>
        public int ChosenTonicPc { get { return tonicPc; } }
        public bool ChosenMinor { get { return minor; } }
        public string ChosenMovement { get { return dance; } }

        protected override void Configure()
        {
            base.Configure();
            PickCharacter();   // root context: biases the movement choice + tempo (like the mode)

            // Bach is functional/tonal, not modal: collapse the corpus modes to plain major / minor.
            string mode = Pick(model.ModeDistribution, rng) ?? "aeolian";
            minor = MusicMathV2.IsMinorMode(mode);
            scale = minor ? MusicMathV2.MinorScale : MusicMathV2.MajorScale;

            // MOVEMENT: pick a dance (or the prelude) and take its meter + tempo + characteristic rhythm.
            // The continuo path is melodic and 4/4-bound (GenerateLine), so it ignores 3/4-time dances.
            dance = string.IsNullOrEmpty(Movement) ? PickDance() : Movement.Trim().ToLowerInvariant();
            if (WithContinuo && (dance != "allemande" && dance != "gavotte" && dance != "bourree")) dance = "allemande";
            SetMeterForDance(dance);

            string inst = string.IsNullOrEmpty(Instrument) ? PickInstrument() : Instrument.Trim().ToLowerInvariant();
            int[] tonics; int prog; ScoreClefKind clef; string instName;
            if (inst == "cello")
            { tonics = new[] { 0, 7, 2, 9 }; loSolo = 36; hiSolo = 81; loMel = 48; hiMel = 76; prog = 42; clef = ScoreClefKind.Bass; instName = "Violoncelle"; }
            else if (inst == "flute")
            { tonics = new[] { 2, 9, 7, 4, 0 }; loSolo = 62; hiSolo = 91; loMel = 67; hiMel = 89; prog = 73; clef = ScoreClefKind.Treble; instName = "Flûte"; }
            else
            { inst = "violin"; tonics = new[] { 7, 2, 9, 4, 0 }; loSolo = 55; hiSolo = 88; loMel = 62; hiMel = 86; prog = 40; clef = ScoreClefKind.Treble; instName = "Violon"; }

            tonicPc = tonics[rng.Next(tonics.Length)];
            SetKeyLetters(tonicPc);
            // SetMeterForDance set bpm; continuo runs a touch quicker as it is melodic, not figurated.
            if (WithContinuo) bpm += 6;
            // character nudges the dance's nominal tempo a touch
            if (character == "enjouée") bpm *= 1.06; else if (character == "calme") bpm *= 0.90; else if (character == "majestueux") bpm *= 0.94;

            if (WithContinuo)
            {
                programs = new[] { prog, 6, 43 };                 // solo, harpsichord, continuo bass
                partNames = new[] { instName, "Clavecin", "Basse continue" };
                partClefs = new[] { clef, ScoreClefKind.Treble, ScoreClefKind.Bass };
                partVolumes = new[] { 1.0, 0.7, 0.8 };
                title = "Bach " + DanceTitle(dance) + " (" + instName + ") & continuo";
            }
            else
            {
                programs = new[] { prog };
                partNames = new[] { instName };
                partClefs = new[] { clef };
                partVolumes = new[] { 1.0 };
                title = "Bach " + DanceTitle(dance) + " (" + instName + ")";
            }
            partIsDrum = null;
        }

        string PickInstrument()
        {
            double r = rng.NextDouble();
            return r < 0.45 ? "violin" : (r < 0.85 ? "cello" : "flute");
        }

        // pick a movement — biased by the melody CHARACTER (fast dances when lively, slow ones when calm/noble)
        string PickDance()
        {
            string[] dances;
            switch (character)
            {
                case "enjouée":    dances = new[] { "gigue", "courante", "bourree", "gavotte" }; break;        // lively, fast
                case "calme":      dances = new[] { "sarabande", "prelude", "allemande" }; break;              // slow, flowing
                case "majestueux": dances = new[] { "sarabande", "chaconne", "allemande", "menuet" }; break;   // stately
                default:           dances = new[] { "allemande", "menuet", "gavotte", "courante", "prelude" }; break; // modérée
            }
            return dances[rng.Next(dances.Length)];
        }

        // each dance sets its METER + TEMPO; barSlices/beatUnit drive the bar grid (24 slices = a quarter).
        void SetMeterForDance(string d)
        {
            int num, den, b;
            switch (d)
            {
                case "courante": num = 3; den = 4; b = 116; break;   // fast running triple (Italian corrente)
                case "sarabande": num = 3; den = 4; b = 58; break;   // slow, stately, beat-2 emphasis
                case "chaconne": num = 3; den = 4; b = 66; break;    // moderate triple over a ground
                case "menuet": num = 3; den = 4; b = 126; break;     // elegant triple
                case "gigue": num = 6; den = 8; b = 116; break;      // fast compound, lilting
                case "bourree": num = 2; den = 2; b = 120; break;    // lively duple (cut time)
                case "gavotte": num = 4; den = 4; b = 104; break;    // moderate duple
                case "allemande": num = 4; den = 4; b = 68; break;   // flowing common time
                default: d = "prelude"; num = 4; den = 4; b = 66; break; // moto perpetuo
            }
            meterNum = num; meterDen = den; bpm = b;
            barSlices = num * 96 / den;            // 3/4→72, 6/8→72, 2/2→96, 4/4→96
            beatUnit = den == 8 ? 36 : 24;         // compound 6/8 beat = dotted quarter; else quarter
            beatsPerBar = Math.Max(1, barSlices / beatUnit);
        }

        // the characteristic per-bar rhythm of each dance (note durations in slices; sum = barSlices).
        // null = the prelude (model-driven, handled separately).
        int[] DanceRhythm(string d)
        {
            switch (d)
            {
                case "sarabande":
                case "chaconne": return new[] { 24, 36, 12 };                         // q · q.(beat-2 stress) · 8
                case "courante": return new[] { 12, 12, 12, 12, 12, 12 };             // running eighths
                case "menuet": return new[] { 24, 12, 12, 24 };                       // q 8 8 q
                case "gigue": return new[] { 24, 12, 24, 12 };                        // (q 8)×2 compound lilt
                case "bourree": return new[] { 12, 12, 24, 12, 12, 24 };              // 8 8 q | 8 8 q
                case "gavotte": return new[] { 24, 24, 12, 12, 24 };                  // q q 8 8 q
                case "allemande": return new[] { 12, 6, 6, 12, 6, 6, 12, 6, 6, 12, 6, 6 }; // (8 16 16)×4 flowing
                default: return null;                                                 // prelude
            }
        }

        static string DanceTitle(string d)
        {
            switch (d)
            {
                case "allemande": return "Allemande"; case "courante": return "Courante"; case "sarabande": return "Sarabande";
                case "gigue": return "Gigue"; case "menuet": return "Menuet"; case "bourree": return "Bourrée";
                case "gavotte": return "Gavotte"; case "chaconne": return "Chaconne"; default: return "Prélude";
            }
        }

        // key signature letters for the (natural) tonics we use: Do=0 Re=1 Mi=2 Fa=3 Sol=4 La=5 Si=6
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

        protected override void Arrange()
        {
            bool slow = dance == "sarabande" || dance == "chaconne";
            int bodyBars = WithContinuo ? 16 : (slow ? 12 : 18);
            var plan = dance == "chaconne" ? ChaconnePlan(bodyBars) : BachPlan(bodyBars);
            cursor = WithContinuo ? RenderContinuo(plan, 0) : RenderSolo(plan, 0);
        }

        // chaconne = variations over a short repeating GROUND (the descending-tetrachord bass in minor).
        List<Cell> ChaconnePlan(int bars)
        {
            int[] ground = minor ? new[] { 0, 10, 8, 7 } : new[] { 0, 9, 5, 7 }; // i-VII-VI-V (min) / I-vi-IV-V (maj)
            var cells = new List<Cell>();
            for (int i = 0; i < bars; i++)
            {
                bool finalTonic = i == bars - 1;
                int root = finalTonic ? 0 : ground[i % ground.Length];
                cells.Add(MakeCell(root, root == 7 && !finalTonic, finalTonic)); // each V reiterates the leading tone
            }
            return cells;
        }

        // ---------- harmonic plan: tonic → diatonic descending-fifths sequence → ii–V pedal → V–I (Picardy) ----------
        List<Cell> BachPlan(int bodyBars)
        {
            int[] circle = minor ? new[] { 0, 5, 10, 3, 8, 2, 7 } : new[] { 0, 5, 11, 4, 9, 2, 7 }; // diatonic circle of 5ths
            var roots = new List<int> { 0 };                          // I
            int ci = 1;
            while (roots.Count < Math.Max(2, bodyBars - 4)) { roots.Add(circle[ci % circle.Length]); ci++; }
            roots.Add(2);                                             // ii (pre-dominant)
            roots.Add(7);                                             // V
            pedalBar = roots.Count;                                   // dominant-pedal / bariolage bar
            roots.Add(7);                                             // V (pedal)
            roots.Add(0);                                             // I

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
            // Bach solo harmony = strictly DIATONIC triads. The model's QualityByDegree is unreliable here
            // (chord detection on a single arpeggiating line invents 7ths/extensions → chromatic cross-relations
            // like Eb against E natural → atonal). The only non-diatonic notes are the idiomatic raised leading
            // tone (cadential V) and the Picardy third (final tonic).
            string g;
            if (finalTonic) g = "maj";                               // major tonic — Picardy third in minor
            else if (cadentialDominant) g = rng.NextDouble() < 0.5 ? "dom7" : "maj"; // raised leading-tone V at the cadence
            else g = DiatonicQuality(root);
            return new Cell { Root = root, Canon = GroupToCanon(g) };
        }

        string DiatonicQuality(int root)
        {
            int r = Mod12(root);
            if (minor)
            {
                // natural-minor diatonic triads: i ii° III iv v VI VII (v stays minor away from the cadence)
                switch (r) { case 0: return "min"; case 2: return "dim"; case 3: return "maj"; case 5: return "min"; case 7: return "min"; case 8: return "maj"; case 10: return "maj"; default: return "min"; }
            }
            switch (r) { case 0: return "maj"; case 2: return "min"; case 4: return "min"; case 5: return "maj"; case 7: return "maj"; case 9: return "min"; case 11: return "dim"; default: return "maj"; }
        }

        // ============ UNACCOMPANIED SOLO: a movement (dance or prelude) in implied polyphony ============
        int RenderSolo(List<Cell> plan, int start)
        {
            int[] rhythm = DanceRhythm(dance);                    // null = prelude (model-driven)
            for (int bar = 0; bar < plan.Count; bar++)
                RenderFigurationBar(plan[bar], start + bar * barSlices, bar == pedalBar, rhythm, 70);
            int t = start + plan.Count * barSlices;
            return RenderFinalChord(t);
        }

        // one bar outlining the chord as Bach's compound melody: a low pedal (implied bass) anchors the
        // beats while an arching upper broken-chord voice fills between. The note durations are the dance's
        // CHARACTERISTIC RHYTHM (sarabande q·q.·8, gigue lilt, courante running 8ths, …); the prelude
        // (rhythm == null) draws varied durations from the corpus rhythm chain. Pedal bar = even bariolage.
        void RenderFigurationBar(Cell cell, int abs, bool pedal, int[] rhythm, int baseVel)
        {
            int[] pcs = ChordPcs(cell);
            int bassAbsPc = Mod12(cell.EffBass + tonicPc);
            int bass = loSolo + Mod12(bassAbsPc - loSolo);   // lowest sounding at/above the instrument bottom
            var ladder = BuildLadder(bass, pcs);
            if (ladder.Count < 2) ladder.Add(Math.Min(hiSolo, bass + 12));

            // faster dances arpeggiate (bass on every beat); slow/elegant dances keep the bass on the
            // downbeat with a melodic upper voice over the bar.
            bool bassEveryBeat = dance == "courante" || dance == "gigue" || dance == "allemande" || dance == "bourree" || dance == "gavotte";

            // bariolage on the dominant-pedal bar — but only where a busy 16th flourish fits (prelude + fast
            // dances). Slow dances (sarabande, menuet, chaconne) keep their own rhythm on the dominant.
            if (pedal && (rhythm == null || bassEveryBeat))
            {
                const int sub = 6; int n = barSlices / sub;               // even 16ths
                int top = ladder[ladder.Count - 1], li = 1;               // ringing pedal note (open-string bariolage)
                for (int k = 0; k < n; k++)
                {
                    int p = (k % 2 == 0) ? top : ladder[1 + (li++ % Math.Max(1, ladder.Count - 1))];
                    while (p > hiSolo) p -= 12; while (p < loSolo) p += 12;
                    notes.Add(new OutNote { Part = 0, Pitch = p, Start = abs + k * sub, Len = sub, Vel = Math.Max(1, baseVel + (k % 2 == 0 ? 4 : -4)) });
                }
                return;
            }
            int upIdx = 1, dir = 1, pos = 0;
            int idx = 0;
            // prelude (no dance rhythm): note VALUES come from the corpus rhythm CELLS (the prelude is always 4/4)
            var preludeSlots = (rhythm == null) ? GenerateCellRhythm(model.RhythmCell, MusicMathV2.Order(model.Orders, "rhythmCell", 8), rng, barSlices, 0, Math.Max(1, meterNum), character + "/body") : null;
            int psi = 0;
            while (pos < barSlices)
            {
                bool atBeat = (pos % beatUnit) == 0;
                bool barStart = pos == 0;

                int dur;
                if (rhythm != null) dur = idx < rhythm.Length ? rhythm[idx] : (barSlices - pos);   // the dance's rhythm
                else
                {
                    // prelude: consume cell durations in order; clamp to 16th..quarter for a moto-perpetuo flow
                    dur = (preludeSlots != null && psi < preludeSlots.Count) ? preludeSlots[psi++][1] : 12;
                    if (dur < 6) dur = 6; if (dur > 24) dur = 24;
                }
                if (pos + dur > barSlices) dur = barSlices - pos;
                if (dur <= 0) break;

                bool bassHere = barStart || (bassEveryBeat && atBeat) || (rhythm == null && (pos % (2 * beatUnit) == 0));
                int pitch;
                if (bassHere) pitch = ladder[0];                          // implied bass
                else
                {
                    pitch = ladder[Math.Min(ladder.Count - 1, Math.Max(1, upIdx))];
                    upIdx += dir; if (upIdx >= ladder.Count - 1) dir = -1; if (upIdx <= 1) dir = 1;
                }
                while (pitch > hiSolo) pitch -= 12; while (pitch < loSolo) pitch += 12;

                int vel = baseVel + (barStart ? 8 : (atBeat ? 2 : -3));
                if ((dance == "sarabande" || dance == "chaconne") && pos == beatUnit) vel += 7; // beat-2 agogic stress
                notes.Add(new OutNote { Part = 0, Pitch = pitch, Start = abs + pos, Len = dur, Vel = Math.Max(1, Math.Min(127, vel)) });
                pos += dur; idx++;
            }
        }

        // ascending ladder of chord pitches from the bass up to the top of the range
        List<int> BuildLadder(int bass, int[] pcs)
        {
            var abs = new List<int>();
            foreach (int pc in pcs) abs.Add(Mod12(pc + tonicPc));
            var lad = new List<int> { bass };
            int p = bass;
            for (int i = 0; i < 7; i++)
            {
                int nx = -1;
                for (int d = 1; d <= 12; d++) if (abs.Contains(Mod12(p + d))) { nx = p + d; break; }
                if (nx < 0 || nx > hiSolo) break;
                lad.Add(nx); p = nx;
            }
            return lad;
        }

        // final tonic chord (major = Picardy in minor) arpeggiated up and left ringing
        int RenderFinalChord(int t)
        {
            int[] pcs = { 0, 4, 7 };
            var voiced = new List<int>();
            foreach (int pc in pcs)
            {
                int p = loSolo + Mod12(Mod12(pc + tonicPc) - loSolo);
                voiced.Add(p);
                if (p + 12 <= hiSolo) voiced.Add(p + 12);
            }
            voiced = voiced.Distinct().OrderBy(x => x).ToList();
            int len = 2 * barSlices;
            for (int i = 0; i < voiced.Count; i++)
                notes.Add(new OutNote { Part = 0, Pitch = voiced[i], Start = t + i * 6, Len = len - i * 6, Vel = 60 });
            return t + len;
        }

        // ============ SOLO + CONTINUO: melodic line over a realized harpsichord + a walking bass ============
        int RenderContinuo(List<Cell> plan, int start)
        {
            var solo = GenerateLine(model, mm, rng, plan, tonicPc, scale, loMel, hiMel, true, 0, "body");
            EmitMelody(notes, solo, start, 0, 82, true, tonicPc, loMel, hiMel, 0, 2);
            for (int bar = 0; bar < plan.Count; bar++)
            {
                ContinuoChords(plan[bar], start + bar * Bar, 52);
                ContinuoBass(plan[bar], bar + 1 < plan.Count ? plan[bar + 1] : plan[bar], start + bar * Bar, 64);
            }
            int t = start + plan.Count * Bar;
            var fin = new Cell { Root = 0, Canon = GroupToCanon("maj") };
            ContinuoChords(fin, t, 48);
            ContinuoBass(fin, fin, t, 60);
            return t + 2 * Bar;
        }

        // the continuo realization lives on part 1 (harpsichord → guitar when fingerstyle is on)
        protected override int AccompPartIndex => 1;

        // harpsichord realization: a quiet broken-chord in eighths, mid register
        void ContinuoChords(Cell cell, int abs, int vel)
        {
            if (GuitarFingerstyle) { RenderFingerstyleBar(notes, cell, abs, tonicPc, vel); return; }
            if (LearnedAccomp) { RenderLearnedAccompBar(notes, cell, abs, tonicPc, vel); return; }
            int[] pcs = ChordPcs(cell);
            var voiced = new List<int>();
            foreach (int pc in pcs) { int p = 48 + Mod12(Mod12(pc + tonicPc) - 48); while (p < 48) p += 12; while (p > 71) p -= 12; voiced.Add(p); }
            voiced = voiced.Distinct().OrderBy(x => x).ToList();
            if (voiced.Count == 0) return;
            int k = 0;
            for (int pos = 0; pos < Bar; pos += 12)
            {
                notes.Add(new OutNote { Part = 1, Pitch = voiced[k % voiced.Count], Start = abs + pos, Len = 12, Vel = vel });
                k++;
            }
        }

        // walking continuo bass: root – 3rd – 5th – stepwise approach to the next root (quarter notes)
        void ContinuoBass(Cell cell, Cell next, int abs, int vel)
        {
            int rootPc = Mod12(cell.EffBass + tonicPc);
            int root = 36 + Mod12(rootPc - 36); while (root > 52) root -= 12;
            int thirdPc = Mod12(cell.Root + (DiatonicQuality(cell.Root) == "min" || DiatonicQuality(cell.Root) == "dim" ? 3 : 4) + tonicPc);
            int fifthPc = Mod12(cell.Root + 7 + tonicPc);
            int third = NearestPitchAbove(root, thirdPc);
            int fifth = NearestPitchAbove(root, fifthPc);
            int nextRootPc = Mod12(next.EffBass + tonicPc);
            int nextRoot = 36 + Mod12(nextRootPc - 36); while (nextRoot > 52) nextRoot -= 12;
            int approach = ScaleStepDir(nextRoot, -1, tonicPc, scale);

            int[] pat = { root, third, fifth, approach };
            for (int b = 0; b < 4; b++)
            {
                int p = pat[b]; while (p < 34) p += 12; while (p > 55) p -= 12;
                notes.Add(new OutNote { Part = 2, Pitch = p, Start = abs + b * Beat, Len = Beat, Vel = vel });
            }
        }

        static int NearestPitchAbove(int from, int pc) { return from + (((pc - from) % 12) + 12) % 12; }
    }
}
