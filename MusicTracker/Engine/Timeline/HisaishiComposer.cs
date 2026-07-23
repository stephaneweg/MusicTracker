using System;
using System.Collections.Generic;
using MusicTracker.Engine;
using MusicTracker.Engine.Flow;
using MusicTracker.Engine.Score;

namespace MusicTracker.Engine.Timeline
{
    /// <summary>
    /// HISAISHI / GHIBLI composer (from the 74-piece corpus analysis). A MINIMALIST broken-chord arpeggio OSTINATO
    /// (88% of the corpus accompaniment is arpeggiated, complementary to the tune), an IMPRESSIONIST MODAL harmony
    /// (I/IV/V/bIII/bVI/bVII, maj7 on the mediants, sus/add9, NO dominant 7th, plagal/mediant motion), and a
    /// PENTATONIC, PHRASED melody from the canonicalized scale-degree MARKOV chain (78% on 5 degrees; leaps resolve
    /// inward 59%) — built as ANTECEDENT/CONSEQUENT PERIODS that breathe. Assembled as an A->B->C->D intensity
    /// automaton (intro / theme / climax / outro). The "Mode" option drives the scale (and is reflected in the timeline).
    /// </summary>
    public sealed class HisaishiComposer : MusicComposer
    {
        public override string Name => "Hisaishi / Ghibli";
        public override string Description => "Ostinato arpégé minimaliste + harmonie modale enrichie + thème pentatonique phrasé (A→B→C→D).";

        // Option "mode" indices -> the scale used (and the MusicalMode FullMode reflected in the timeline).
        static readonly string[] ModeChoices = { "Auto (selon la tonalité)", "Majeur (ionien)", "Lydien (majeur #4)", "Mixolydien (majeur ♭7)", "Mineur", "Éolien (mineur modal)", "Dorien", "Phrygien (mineur ♭2)" };
        static readonly string[] LengthChoices = { "Courte (~12 mes.)", "Moyenne (~24 mes.)", "Longue (~34 mes.)" };
        static readonly string[] BreathChoices = { "Auto (légère)", "Légère", "Marquée" };
        static readonly string[] MeterChoices = { "Aléatoire", "2/4", "3/4", "4/4", "6/8", "9/8", "12/8" };
        static readonly string[] TempoChoices = { "Auto (selon le rythme)", "Lent (~70)", "Modéré (~82)", "Animé (~90)" };
        static readonly string[] RhythmChoices = { "Lent (épuré, notes tenues)", "Modéré", "Enjoué (aérien, type Totoro)" };
        static readonly string[] ArtChoices = { "Auto (varie par section)", "Arpège montant + tenue", "Croches mélodiques + noire", "Arpège roulé (harpe)", "Accords doux (berceuse)", "Balancement (barcarolle)", "Accord morphing", "Arpège en triolets" };
        // meter option index -> (num, den); index 0 = random.
        static readonly (int n, int d)[] MeterMap = { (0, 0), (2, 4), (3, 4), (4, 4), (6, 8), (9, 8), (12, 8) };

        readonly ComposerOption[] options =
        {
            new ComposerOption("mode", "Mode", ModeChoices, 0),
            new ComposerOption("rythme", "Rythme", RhythmChoices, 1),
            new ComposerOption("articulation", "Articulation", ArtChoices, 0),
            new ComposerOption("meter", "Mesure", MeterChoices, 0),
            new ComposerOption("tempo", "Tempo", TempoChoices, 0),
            new ComposerOption("length", "Longueur", LengthChoices, 1),
            new ComposerOption("breath", "Respiration", BreathChoices, 0),
        };
        public override IReadOnlyList<ComposerOption> Options => options;

        public override ComposeResult Compose(ComposeContext ctx)
        {
            var key = ctx.Key ?? new KeySignature();
            int tonicPc = MusicTheory.TonicPc(key);
            var rng = new Random(ctx.Seed);
            var result = new ComposeResult();

            // ---- RHYTHMIC FEEL (lent / modéré / enjoué) — drives the melody & accompaniment DENSITY/articulation,
            // biases the meter (enjoué → ternary, à la Totoro) and, when the tempo is Auto, the BPM. ----
            int feel = ctx.Opt("rythme", 1);

            // ---- METER: random (Auto) or chosen. /8 = TERNARY (compound) → the beat subdivides in THREE. ENJOUÉ is
            // often TERNARY in Hisaishi, so Auto favours 6/8·12/8 there. The timeline adopts the result. ----
            int meterOpt = ctx.Opt("meter");
            int meterNum, meterDen;
            if (meterOpt <= 0)
            {
                int[] w = feel == 2 ? new[] { 4, 42, 6, 30, 5, 8, 3, 12, 2, 8 }   // enjoué: mostly 6/8, 12/8, 9/8
                                     : new[] { 3, 52, 2, 13, 1, 7, 4, 14, 6, 8, 5, 6 }; // else: 4/4 dominant (per the Ghibli template), some 3/4, occasional compound
                int mi = PickWeighted(w, rng); meterNum = MeterMap[mi].n; meterDen = MeterMap[mi].d;
            }
            else { meterNum = MeterMap[meterOpt].n; meterDen = MeterMap[meterOpt].d; }
            bool ternary = meterDen == 8;
            int beatsPerBar = ternary ? Math.Max(1, meterNum / 3) : meterNum;
            int barSlices = beatsPerBar * Spq;
            result.ResultMeterNum = meterNum; result.ResultMeterDen = meterDen;

            // ---- TEMPO: a chosen band, else Auto follows the feel. Kept inside the template's SAFE range 70-90 BPM. ----
            int tempoOpt = ctx.Opt("tempo");
            result.ResultBpm = tempoOpt == 1 ? 70 : tempoOpt == 2 ? 82 : tempoOpt == 3 ? 90
                             : (feel == 0 ? 70 + rng.Next(8) : feel == 2 ? 84 + rng.Next(7) : 78 + rng.Next(9)); // lent 70-77 · modéré 78-86 · enjoué 84-90

            // ---- MODE: resolve the scale + the FullMode the timeline should adopt ----
            int keyEff = MusicalMode.Effective(key);
            int modeOpt = ctx.Opt("mode");
            int fullMode;
            switch (modeOpt)
            {
                case 1: fullMode = 0; break;   // Majeur (ionien)
                case 2: fullMode = 6; break;   // Lydien
                case 3: fullMode = 7; break;   // Mixolydien (majeur ♭7)
                case 4: fullMode = 2; break;   // Mineur (harmonique — sensible pour la levée de V)
                case 5: fullMode = 1; break;   // Éolien (mineur naturel)
                case 6: fullMode = 4; break;   // Dorien
                case 7: fullMode = 5; break;   // Phrygien (mineur ♭2)
                default: fullMode = keyEff; break; // Auto = the project's current mode
            }
            if (modeOpt != 0 && fullMode != keyEff)
                result.ResultKey = new KeySignature { TonicLetter = key.TonicLetter, Accidental = key.Accidental, Mode = MusicalMode.IsMinorish(fullMode) ? 1 : 0, FullMode = fullMode };
            var scale = ScaleSet(tonicPc, MusicalMode.Scale(fullMode));
            bool minor = MusicalMode.IsMinorish(fullMode);

            // ---- LENGTH: section sizes (A intro / B theme / C climax / D outro) ----
            int aB, bB, cB, dB;
            switch (ctx.Opt("length"))
            {
                case 0: aB = 2; bB = 4; cB = 4; dB = 2; break;   // courte
                case 2: aB = 4; bB = 12; cB = 12; dB = 6; break; // longue
                default: aB = 4; bB = 8; cB = 8; dB = 4; break;  // moyenne
            }
            int total = aB + bB + cB + dB;
            int breath = ctx.Opt("breath"); // 0 auto(=1), 1 light, 2 marked
            int breathLvl = breath == 0 ? 1 : breath;

            // ---- HARMONIC RHYTHM by feel: ENJOUÉ changes the degrees FASTER (2 chords/bar); lent/modéré = 1/bar. ----
            int cpb = feel == 2 ? 2 : 1;
            int slot = Math.Max(1, barSlices / cpb);

            // ---- MODULATION: the climax (C) often modulates to a CLOSE KEY, reached by a SECONDARY-DOMINANT pivot at
            // the end of B; the outro D returns home. The TARGET is chosen per the corpus pieces: RELATIVE (Laputa
            // Cm→E♭),  SUBDOMINANT-minor (Nausicaä Cm→Fm), WHOLE-STEP up (Mononoke Cm→Dm), DOMINANT, or PARALLEL (One
            // Summer's Day C→Cm). The MELODY modulates WITH the harmony (PhrasedMelody switches key at the C boundary),
            // so the new key's notes fit even when it doesn't share the home scale. ----
            bool modulate = rng.Next(100) < 65;
            // (offset from the home tonic, target-is-minor) per target index, weighted differently for minor/major home.
            (int off, bool tmin)[] targets = minor
                ? new (int, bool)[] { (3, false), (5, true), (2, true), (7, true), (0, false) }   // rel.maj · subdom.min · whole-step.min · dom.min · parallel.maj
                : new (int, bool)[] { (9, true), (5, false), (2, true), (7, false), (0, true) };   // rel.min · subdom.maj · whole-step.min · dom.maj · parallel.min
            int[] tgw = minor ? new[] { 0, 35, 1, 20, 2, 12, 3, 13, 4, 8 } : new[] { 0, 35, 1, 18, 2, 8, 3, 17, 4, 15 };
            int tgi = PickWeighted(tgw, rng);
            int targetTonic = (tonicPc + targets[tgi].off) % 12; bool targetMinor = targets[tgi].tmin;
            var targetScale = ScaleSet(targetTonic, MusicalMode.Scale(targetMinor ? 1 : 0));   // aeolian / ionian for the target
            // PREPARED (a secondary-dominant pivot at the end of B) vs SUDDEN/UNPREPARED (no pivot — the key just
            // changes after the phrase-end breath; the template allows "a pause then an unprepared modulation").
            bool preparedMod = rng.Next(100) < 60;
            // DYNAMIC harmonic rhythm by section: A+B normal · C (climax) DENSER (+1) · D (outro) SPARSER (-1).
            var prog = new List<(int root, int quality)>();
            prog.AddRange(GrowProgression(tonicPc, minor, (aB + bB) * cpb, cpb, feel, 0, rng));      // A + B (home key)
            if (modulate)
            {
                if (preparedMod && prog.Count > 0) prog[prog.Count - 1] = ((targetTonic + 7) % 12, 8); // SECONDARY-DOMINANT pivot (prepared)
                prog.AddRange(GrowProgression(targetTonic, targetMinor, cB * cpb, cpb, feel, 1, rng)); // C (target key) — denser
            }
            else prog.AddRange(GrowProgression(tonicPc, minor, cB * cpb, cpb, feel, 1, rng));          // C (home) — denser climax
            prog.AddRange(GrowProgression(tonicPc, minor, dB * cpb, cpb, feel, -1, rng));              // D (home outro) — sparser
            // END CONCLUSIVE on a MAJOR tonic — a PICARDY THIRD even in minor (e.g. Nausicaä ends C major from C minor).
            if (prog.Count > 0) prog[prog.Count - 1] = (tonicPc % 12, 13);

            // ARTICULATION figures: 1 rising arp + tenue · 2 melodic 8ths · 3 wide rolled arpeggio (harp) · 4 soft chord
            // stabs (lullaby/oom-pah) · 5 rocking dyad (barcarolle) · 6 chord-morphing. The option LOCKS one figure for
            // the whole piece (artOpt 1-6); "Auto" (0) VARIES the figure BY SECTION (A→B→C→D) for an evolving texture —
            // the analyses show the accompaniment changes per section (sparse intro → flowing theme → fuller climax →
            // calm outro), each section staying ONE coherent figure. Built as a per-SLOT plan `artSlot`.
            int artOpt = ctx.Opt("articulation");
            bool openVoicing = rng.Next(100) < 55;   // OPEN voicing where a section uses the rising arp (figure 1)
            var artSlot = new int[prog.Count];
            if (artOpt != 0) { int a = artOpt == 7 ? 8 : Math.Min(6, Math.Max(1, artOpt)); for (int i = 0; i < artSlot.Length; i++) artSlot[i] = a; } // option idx 7 = triplet arp (figure 8)
            else
            {
                int aArt = PickWeighted(new[] { 5, 40, 4, 30, 3, 30 }, rng);                 // INTRO: calm (rock / stabs / rolled)
                int bArt = PickWeighted(new[] { 3, 28, 2, 22, 1, 24, 6, 18, 8, 12 }, rng);   // THEME: flowing (rolled / melodic / arp / morph / triplet arp)
                int cArt = PickWeighted(new[] { 1, 38, 3, 28, 6, 28, 8, 12 }, rng);          // CLIMAX: fuller (arp / rolled / morph / triplet arp)
                int dArt = PickWeighted(new[] { 4, 40, 5, 35, 1, 25 }, rng);                 // OUTRO: calm (stabs / rock / arp)
                int sA = aB * cpb, sB = (aB + bB) * cpb, sC = (aB + bB + cB) * cpb;
                for (int i = 0; i < artSlot.Length; i++) artSlot[i] = i < sA ? aArt : i < sB ? bArt : i < sC ? cArt : dArt;
            }
            bool pedal = feel == 0 || rng.Next(100) < 30;

            // REHARMONIZATION as development (~45%): the CONSEQUENT half of each melodic period restates the theme
            // VERBATIM (PhrasedMelody does that), so to recolour it — the development device the videos stress — we
            // substitute DIFFERENT chords under those bars (a DIATONIC third down + an extended quality), turning the
            // reused melody notes into 7ths/9ths/colour tones. Applied to `prog` so melody-anchor + accompaniment + bass
            // all agree. The final (Picardy) chord is preserved.
            if (rng.Next(100) < 45)
            {
                int melBarsR = bB + cB;
                for (int ph = 1; ph < melBarsR / 4; ph += 2)        // odd phrases = consequents
                {
                    if (modulate && ph * 4 >= bB) continue;          // C-section consequents are in the TARGET key (home-scale shift would be wrong)
                    for (int bar = 0; bar < 4; bar++)
                        for (int c2 = 0; c2 < cpb; c2++)
                        {
                            int idx = (aB + ph * 4 + bar) * cpb + c2;
                            if (idx <= 0 || idx >= prog.Count - 1) continue;   // keep the Picardy final chord (and slot 0)
                            int newRoot = ((ShiftScale(60 + (prog[idx].root % 12), -2, scale)) % 12 + 12) % 12; // diatonic 3rd down
                            int q = prog[idx].quality;
                            int newQ = (q == 0 || q == 13) ? 6 : (q == 1 || q == 14) ? 7 : (q == 6 ? 15 : q); // enrich to a 7th/9th
                            prog[idx] = (newRoot, newQ);
                        }
                }
            }

            // ---- tracks ----
            var acc = WaveAccompaniment(prog, slot, tonicPc, scale, ternary, feel, artSlot, openVoicing, rng);
            var bass = PedalBass(prog, slot, tonicPc, pedal);

            int melBars = bB + cB;
            var melProg = prog.GetRange(aB * cpb, Math.Min(melBars * cpb, prog.Count - aB * cpb));
            // the melody covers B+C; it switches to the target key at the C boundary (melody-region bar `bB`) when modulating.
            int modBar = modulate ? bB : -1;
            var melCore = PhrasedMelody(tonicPc, scale, minor, melProg, beatsPerBar, barSlices, melBars, slot, ternary, feel, new Random(ctx.Seed * 131 + 7), 72, modBar, targetTonic, targetScale, targetMinor);
            AddBreathing(melCore, beatsPerBar, barSlices, new Random(ctx.Seed * 53), breathLvl);

            var melFull = new List<RiffNote>();
            foreach (var n in melCore.Notes) melFull.Add(new RiffNote(n.Note, n.Start + aB * barSlices, n.Length));
            // PICKUP (anacrusis): 1-2 eighths rising by step into the first theme note, placed in the bar before B
            // (the Ghibli template uses pickups). They sound during the end of the intro.
            if (melCore.Notes.Count > 0)
            {
                int eP = ternary ? Spq / 3 : Spq / 2, nPick = 1 + rng.Next(2);
                int firstStart = aB * barSlices + melCore.Notes[0].Start, firstPitch = melCore.Notes[0].Note + 12;
                for (int k = nPick; k >= 1; k--)
                {
                    int t = firstStart - k * eP; if (t < 0) continue;
                    int p = ScaleStep(firstPitch, -k, scale) - 12;
                    if (p >= 0 && p < 96) melFull.Add(new RiffNote(p, t, eP));
                }
            }
            var melRiff = new Riff { Name = "Mélodie", Notes = melFull, LengthSlices = total * barSlices, SlicesPerQuarter = Spq };

            // CLIMAX strings = a SEPARATE, INDEPENDENT counter-melody (not the melody doubled) + a sustained pad.
            // the counter-melody sounds in C (the modulated section), so it uses the TARGET scale's passing tones.
            var climaxScale = modulate ? targetScale : scale;
            var counterRiff = StringCounter(prog, slot, (aB + bB) * cpb, (aB + bB + cB) * cpb, total * barSlices, climaxScale, ternary, openVoicing, new Random(ctx.Seed * 977 + 3));
            var padRiff = PadChords(prog, slot, (aB + bB) * cpb, (aB + bB + cB) * cpb, 3);   // LOW register (was shrill at oct.4)

            // GLOCKENSPIEL: a SPARSE soft sparkle — only the melody's LONGEST notes (>= a half) and at the melody's OWN
            // octave (NOT an octave up, which was shrill). Per-piece OPTIONAL (~40%) so the climax isn't always busy.
            bool useGlock = rng.Next(100) < 40;
            var glock = new List<RiffNote>();
            if (useGlock) foreach (var n in melCore.Notes) if (n.Start / barSlices >= bB && n.Length >= Spq * 2) { int p = n.Note; if (p >= 0 && p < 96) glock.Add(new RiffNote(p, n.Start + aB * barSlices, n.Length)); }
            var glockRiff = new Riff { Name = "Glockenspiel", Notes = glock, LengthSlices = total * barSlices, SlicesPerQuarter = Spq };

            // CLIMAX timbre GROWTH: the melody DOUBLED an octave BELOW on strings, during C only — the foreground colour
            // thickens at the climax (the corpus pieces grow by adding string octaves, not by changing the harmony).
            var climaxDouble = new List<RiffNote>();
            foreach (var n in melCore.Notes) if (n.Start / barSlices >= bB) { int p = n.Note - 12; if (p >= 0 && p < 96) climaxDouble.Add(new RiffNote(p, n.Start + aB * barSlices, n.Length)); }
            var doubleRiff = new Riff { Name = "Cordes (doublure)", Notes = climaxDouble, LengthSlices = total * barSlices, SlicesPerQuarter = Spq };

            // per-section TEMPO map: the climax (C) lifts ~12%, then D returns (cf. Nausicaä 75→90→75).
            double baseBpm = result.ResultBpm;
            result.ResultTempo = new List<(double, double)>
            {
                (0, baseBpm),
                ((aB + bB) * beatsPerBar, Math.Round(baseBpm * 1.12)),
                ((aB + bB + cB) * beatsPerBar, baseBpm),
            };

            // ---- ORCHESTRATION (soft palette; the MELODY timbre is chosen FIRST, the rest around it). The foreground
            // colour EVOLVES: the theme = a solo woodwind (or piano), the counter-melody = a CONTRASTING solo, and at the
            // climax the melody is doubled an octave down by STRINGS = growth led by timbre.
            // GM: 73 flute · 68 oboe · 71 clarinet · 0 piano · 46 harp · 60 horn · 48/49 strings · 9 glockenspiel.
            int leadInst = PickWeighted(new[] { 73, 34, 68, 24, 71, 18, 0, 24 }, rng);       // melody solo (chosen first)
            int counterInst = PickWeighted(new[] { 60, 30, 71, 22, 68, 20, 48, 28 }, rng);   // counter-melody: a CONTRASTING solo
            if (counterInst == leadInst) counterInst = leadInst == 60 ? 48 : 60;
            int accInst = rng.Next(100) < 35 ? 46 : 0;                                        // arpeggio on harp or piano

            // Track order (TOP → BOTTOM, track[0] = top lane): MELODY on top · COUNTER-MELODY · ORCHESTRATION lines
            // (climax string doubling · pad · glockenspiel) · ACCOMPANIMENT · BASS at the very bottom. The climax
            // orchestration is kept SOFT (lower track volumes) so it cushions the theme instead of overwhelming it.
            AddBarRiffs(result.Tracks, leadInst, "Mélodie", melRiff, total, barSlices, result.Riffs);              // theme solo (B+C)
            result.Tracks[result.Tracks.Count - 1].Volume = 1;
            AddBarRiffs(result.Tracks, counterInst, "Contre-chant", counterRiff, total, barSlices, result.Riffs);  // contrasting solo (C)
            result.Tracks[result.Tracks.Count - 1].Volume = 1;

            AddBarRiffs(result.Tracks, 48, "Cordes (doublure)", doubleRiff, total, barSlices, result.Riffs);       // melody octave-double, climax — orchestration
            result.Tracks[result.Tracks.Count - 1].Volume = 0.1;
            AddBarRiffs(result.Tracks, 49, "Cordes (nappe)", padRiff, total, barSlices, result.Riffs);             // sustained string pad (C)
            result.Tracks[result.Tracks.Count - 1].Volume = 0.1;                                                  // very soft background bed
            if (useGlock)
            {
                AddBarRiffs(result.Tracks, 9, "Glockenspiel", glockRiff, total, barSlices, result.Riffs);          // sparse soft sparkle (C)
                result.Tracks[result.Tracks.Count - 1].Volume = 0.45;
            }
            AddBarRiffs(result.Tracks, accInst, "Accompagnement", acc, total, barSlices, result.Riffs);            // arpeggio: piano or harp
            result.Tracks[result.Tracks.Count - 1].Volume = 0.1;

            AddBarRiffs(result.Tracks, 0, "Basse", bass, total, barSlices, result.Riffs);                          // bass — bottom



            return result;
        }

        // ---- HARMONY (GENERATIVE — no fixed cell bank, no cadence generator). GROW the progression from a modal
        // ROOT-MOTION walk (plagal/mediant/pedal-biased, NO functional dominant) over a SLOW functional rhythm, then
        // lay a SURFACE of colour toggles over each HELD root (Cm→Cm7→Cm9→Cm7 = ONE functional chord = the "faux
        // harmonic rhythm"). This is the FunctionalHarmony-vs-SurfaceHarmony split: the root rarely moves, the colour
        // breathes. Plus an occasional MODAL BORROW (Phrygian bII / Lydian #IV) and a SECONDARY-DOMINANT TEASE (an
        // applied dom7 that promises a modulation then falls back to the tonic). Returns ONE chord per SLOT.
        // Quality indices: Maj=0 Min=1 Sus2=4 Sus4=5 Maj7=6 Min7=7 7(dom)=8 add9=13 m(add9)=14 Maj9=15.
        internal static List<(int root, int quality)> GrowProgression(int tonicPc, bool minor, int slots, int cpb, int feel, int hrBias, Random rng)
        {
            var prog = new List<(int root, int quality)>();
            int deg = 0;            // current functional ROOT degree (0 = tonic), chromatic 0..11
            bool wantTease = rng.Next(100) < 35;   // ~35% of pieces tease ONCE (decided per piece, not per slot)
            bool teased = false, first = true;
            while (prog.Count < slots)
            {
                // SECONDARY-DOMINANT TEASE (once, ~a third of the way in): a dom7 that promises a modulation, then resolves
                // DECEPTIVELY back to the tonic — the "running in place" device. NO colour drift on it.
                if (wantTease && !teased && !first && prog.Count >= slots / 3 && prog.Count < slots - cpb)
                {
                    int troot = ((tonicPc + (minor ? 2 : 4)) % 12 + 12) % 12;   // V7/V (D7 in Cm) · V7/vi (E7 in C)
                    for (int s = 0; s < cpb && prog.Count < slots; s++) prog.Add((troot, 8));
                    deg = 0; teased = true; continue;                          // fall back home
                }
                // FUNCTIONAL rhythm: how many BARS this root holds (lent slow, enjoué fast) → × cpb = slots. hrBias
                // shifts it per SECTION: +1 = DENSER (climax), -1 = SPARSER (outro/rest) — "match the emotional level".
                int funcBars = feel == 0 ? 2 + rng.Next(3) : feel == 2 ? 1 : 1 + rng.Next(2);
                funcBars = Math.Max(1, funcBars - hrBias);
                int root = ((tonicPc + deg) % 12 + 12) % 12;
                var cycle = ColourCycle(FunctionalQuality(deg, minor, rng));    // base quality + related colours (SAME root)
                int flen = Math.Max(1, funcBars * cpb);
                for (int s = 0; s < flen && prog.Count < slots; s++) prog.Add((root, cycle[s % cycle.Length])); // slot0 = base, then drift
                first = false;
                int next = PickWeighted(RootNext(deg, minor), rng);
                if (rng.Next(100) < 8) next = rng.Next(2) == 0 ? 1 : 6;        // occasional Phrygian bII / Lydian #IV colour
                deg = next;
            }
            return prog;
        }

        // Modal ROOT-MOTION Markov (next functional degree | current) — plagal/mediant/pedal biased, NO V→I dominance.
        static int[] RootNext(int deg, bool minor)
        {
            if (minor)
                switch (deg)
                {
                    case 0:  return new[] { 5,20, 3,20, 10,18, 7,14, 8,12, 9,8, 2,8 };  // i → iv · bIII · bVII · V · bVI · vi · ii
                    case 5:  return new[] { 0,38, 10,16, 3,14, 7,12, 8,12, 2,8 };       // iv → i (plagal) · bVII · bIII · V · bVI · ii
                    case 10: return new[] { 0,30, 8,22, 3,20, 5,16, 7,12 };             // bVII → i · bVI · bIII · iv · V
                    case 8:  return new[] { 0,22, 3,24, 10,22, 5,16, 7,16 };            // bVI → i · bIII · bVII · iv · V
                    case 3:  return new[] { 5,24, 8,22, 10,18, 0,18, 7,18 };            // bIII → iv · bVI · bVII · i · V
                    case 7:  return new[] { 0,44, 8,18, 5,16, 3,12, 10,10 };            // V → i (soft) · bVI (deceptive) · iv
                    case 2:  return new[] { 7,30, 5,24, 0,20, 3,14, 8,12 };             // ii → V · iv · i
                    case 9:  return new[] { 5,26, 0,20, 3,20, 10,18, 7,16 };            // vi → iv · i · bIII · bVII
                    case 4:  return new[] { 0,30, 5,24, 8,20, 3,14 };                   // III → i · iv · bVI
                    case 1:  return new[] { 0,42, 5,30, 3,28 };                         // bII (Phrygian) → i · iv · bIII
                    case 6:  return new[] { 0,42, 7,30, 5,28 };                         // #IV (Lydian) → i · V · iv
                    default: return new[] { 0,1 };
                }
            switch (deg)
            {
                case 0:  return new[] { 5,22, 7,18, 9,18, 3,12, 10,10, 4,10, 2,10 };    // I → IV · V · vi · bIII · bVII · iii · ii
                case 5:  return new[] { 0,38, 7,16, 9,16, 2,14, 4,10, 8,6 };            // IV → I · V · vi · ii · iii · bVI
                case 7:  return new[] { 0,40, 9,22, 5,16, 4,10, 8,12 };                 // V → I · vi (deceptive) · IV · bVI
                case 9:  return new[] { 5,26, 2,22, 0,18, 7,16, 4,12 };                 // vi → IV · ii · I · V
                case 2:  return new[] { 7,34, 5,22, 0,18, 9,14 };                       // ii → V · IV · I · vi
                case 4:  return new[] { 9,30, 5,22, 2,16, 0,14 };                       // iii → vi · IV · ii · I
                case 3:  return new[] { 5,26, 8,22, 0,18, 10,16 };                      // bIII → IV · bVI · I · bVII
                case 8:  return new[] { 0,26, 3,22, 5,18, 10,16 };                      // bVI → I · bIII · IV · bVII
                case 10: return new[] { 0,30, 5,22, 8,18, 3,14 };                       // bVII → I · IV · bVI · bIII
                case 1:  return new[] { 0,42, 5,30, 3,28 };                             // bII → I · IV · bIII
                case 6:  return new[] { 0,42, 7,30, 5,28 };                             // #IV → I · V · IV
                default: return new[] { 0,1 };
            }
        }

        // Base CHORD QUALITY for a chromatic scale-degree — modal colours (maj7 mediants, sus, add9), NO functional dom7.
        static int FunctionalQuality(int deg, bool minor, Random rng)
        {
            switch (deg)
            {
                case 0:  return minor ? (rng.Next(2) == 0 ? 14 : 1) : (rng.Next(2) == 0 ? 13 : 0); // i: m(add9)/min · I: add9/maj
                case 3:  return rng.Next(2) == 0 ? 6 : 16;     // bIII maj7/maj9 (lush mediant) — 16 = TRUE maj9 (no b7)
                case 8:  return rng.Next(2) == 0 ? 6 : 16;     // bVI maj7/maj9
                case 10: return rng.Next(2) == 0 ? 0 : 6;      // bVII maj/maj7
                case 5:  return minor ? (rng.Next(2) == 0 ? 7 : 1) : (rng.Next(2) == 0 ? 6 : 13); // iv min7/min · IV maj7/add9
                case 7:  return rng.Next(3) == 0 ? 7 : (rng.Next(2) == 0 ? 4 : 5);  // v/V: sus2/sus4 mostly, sometimes min7 (NEVER dom7)
                case 2:  return 7;                             // ii min7
                case 9:  return 7;                             // vi min7
                case 4:  return minor ? 6 : 7;                 // III maj7 · iii min7
                case 1:  return 6;                             // bII maj7 (Phrygian)
                case 6:  return 6;                             // #IV maj7 (Lydian)
                default: return minor ? 1 : 0;
            }
        }

        // The SURFACE colour cycle for a held root (same root, drifting colour = "faux harmonic rhythm"); base first.
        static int[] ColourCycle(int q)
        {
            switch (q)
            {
                case 1:  return new[] { 1, 7, 14, 7 };     // min · min7 · m(add9) · min7
                case 14: return new[] { 14, 1, 7, 1 };     // m(add9) · min · min7 · min
                case 7:  return new[] { 7, 14, 1, 14 };    // min7 · m(add9) · min · m(add9)
                case 0:  return new[] { 0, 6, 13, 6 };     // maj · maj7 · add9 · maj7
                case 13: return new[] { 13, 0, 6, 0 };     // add9 · maj · maj7 · maj
                case 6:  return new[] { 6, 16, 13, 16 };   // maj7 · maj9 · add9 · maj9 (16 = true maj9, no b7)
                case 16: return new[] { 16, 6, 13, 6 };    // maj9 · maj7 · add9 · maj7
                case 4:  return new[] { 4, 0, 5, 0 };      // sus2 · maj · sus4 · maj
                case 5:  return new[] { 5, 0, 4, 0 };      // sus4 · maj · sus2 · maj
                default: return new[] { q };
            }
        }

        // ---- ACCOMPANIMENT — ONE CONSISTENT figure for the whole piece (the recurring Hisaishi gesture): a RISING
        // ARPEGGIO of a few chord tones (1-3-5-7-8-9) followed by a HELD note. To stay COHERENT we do NOT switch
        // articulation types between bars; only the chord, the reached height (subtle evolution) and the per-piece
        // hold type vary. Hold type (fixed per piece): a single held top note · a two-note dyad · a softly rolled
        // chord (notes one after another, then they ring). LOW register (octave-folded to the bottom of the treble,
        // below the melody). One figure PER CHORD-SLOT, so it speeds up automatically when the harmony does (enjoué).
        // Pace (`e`): ternary triplet-8ths; binary = 8ths (lent/modéré) or 16ths (enjoué). ----
        const int TonicInv = 0;  // the tonic is always voiced at this inversion (root position) — the voice-leading anchor

        // PIANISTIC VOICE-LEADING for a 3-note OPEN arpeggio voicing: among the open voicings of the new chord (each
        // candidate = a chord tone as bottom + 2 open tones stacked, >= a minor 3rd apart), return the one whose notes
        // move the LEAST in total from the previous voicing (pa<pb<pc). Common tones cost ~0, so they stay put and the
        // chord "pivots" around them; the other voices step to the nearest chord tone. Stays in a low-treble register.
        internal static int[] MorphOpenVoicing(int pa, int pb, int pc, HashSet<int> pcs)
        {
            int bestCost = int.MaxValue; int[] best = null;
            foreach (int rootPc in pcs)
            {
                int bpc = ((rootPc % 12) + 12) % 12;
                for (int oct = -1; oct <= 1; oct++)
                {
                    int m = NearestPc(pa, bpc) + 12 * oct;
                    if (m < 38 || m > 60) continue;                       // bottom in a sensible low-treble range
                    int n = NextChordToneUpOpen(m, pcs), t = NextChordToneUpOpen(n, pcs);
                    if (t > 71) continue;                                 // keep the top below the melody
                    int cost = Math.Abs(m - pa) + Math.Abs(n - pb) + Math.Abs(t - pc);
                    if (cost < bestCost) { bestCost = cost; best = new[] { m, n, t }; }
                }
            }
            if (best == null)
            {
                int m = NearestChord(pa, pcs); while (m > 60) m -= 12; while (m < 40) m += 12;
                int n = NextChordToneUpOpen(m, pcs), t = NextChordToneUpOpen(n, pcs);
                best = new[] { m, n, t };
            }
            return best;
        }

        internal static Riff WaveAccompaniment(List<(int root, int quality)> prog, int slotSlices, int tonicPc, HashSet<int> scale, bool ternary, int feel, int[] artPerSlot, bool openVoicing, Random rng)
        {
            // pace `e`: ternary = triplet-8ths; else EIGHTHS dominate (template: 8th/quarter shortest). The arpeggiated
            // SURFACE may occasionally run in 16ths (enjoué ~40%, modéré ~25%) — the harp/piano figuration, not the tune.
            int e = ternary ? Spq / 3 : (feel == 2 ? (rng.Next(100) < 40 ? Spq / 4 : Spq / 2) : feel == 0 ? Spq / 2 : (rng.Next(100) < 25 ? Spq / 4 : Spq / 2));
            e = Math.Max(2, e);
            int figureNotes = (e <= Spq / 4) ? 5 + rng.Next(2) : (feel == 0 ? 3 + rng.Next(2) : 4 + rng.Next(2));
            int blockSize = 2 + rng.Next(2);
            int tripDir = rng.Next(3);   // figure-8 triplet direction (per piece): 0 ascending · 1 descending · 2 Alberti
            int eighth = ternary ? Spq / 3 : Spq / 2;
            var notes = new List<RiffNote>();
            int prevDeg = 0, prevInv = TonicInv, prevStart = 55;   // anchor: tonic at inversion TonicInv, low-treble
            int prevVA = 50, prevVB = 56, prevVC = 62;             // figure-11 voice-leading: the previous 3-note voicing (whole arpeggio)
            for (int si = 0; si < prog.Count; si++)
            {
                var pcs = ChordPcs(prog[si].root, prog[si].quality);
                if (pcs.Count == 0) continue;
                int slotStart = si * slotSlices, deg = (((prog[si].root - tonicPc) % 12) + 12) % 12;
                int artUse = (si < artPerSlot.Length) ? artPerSlot[si] : 1;   // this SLOT's articulation (varies by section in Auto)

                if (artUse == 6)
                {
                    // CHORD-MORPHING (the device the videos say is in EVERY piece): a STATIC upper-structure triad struck
                    // on each beat (mid-treble) over a MOVING low arpeggio of the FULL extended chord → the ear hears
                    // sub-chords change (e.g. Dm7→Fadd6→Fmaj7→F) while ONE chord actually sounds. The upper triad = the
                    // {3rd,5th,7th} of a 7th/9th chord (an upper-structure triad); for a plain triad it's the triad itself.
                    var ct = ChordTones(prog[si].root, 3, prog[si].quality);
                    var upPcs = new List<int>();
                    if (ct.Length >= 4) { upPcs.Add(ct[1] % 12); upPcs.Add(ct[2] % 12); upPcs.Add(ct[3] % 12); }
                    else for (int z = 0; z < ct.Length; z++) upPcs.Add(ct[z] % 12);
                    // stack the upper structure ASCENDING with a MINIMUM gap of a minor 3rd (>=3) → no two notes a 2nd apart.
                    var upTri = new List<int>();
                    int prevT = NearestPc(54, upPcs[0]); while (prevT < 48) prevT += 12; while (prevT > 60) prevT -= 12; upTri.Add(prevT);
                    for (int z = 1; z < upPcs.Count; z++) { int t2 = prevT + 3, pcz = ((upPcs[z] % 12) + 12) % 12; while ((((t2 % 12) + 12) % 12) != pcz) t2++; upTri.Add(t2); prevT = t2; }
                    // STATIC triad on every beat (mid-treble), sustained ~a beat
                    for (int b = 0; b * Spq < slotSlices; b++)
                    {
                        int onset = slotStart + b * Spq, dur = Math.Min(Spq, slotSlices - b * Spq);
                        foreach (var p0 in upTri) { int p = p0 - 12; if (p >= 0 && p < 96) notes.Add(new RiffNote(p, onset, dur)); }
                    }
                    // MOVING low arpeggio of the full chord (root upward), kept a MINOR THIRD BELOW the triad bottom so it
                    // never sounds a 2nd against the held triad.
                    int ceil = upTri[0] - 3;
                    int low = NearestPc(prevStart, (((prog[si].root) % 12) + 12) % 12); while (low > ceil) low -= 12; while (low < ceil - 14) low += 12;
                    var lowFig = new List<int> { low }; int cc = low;
                    for (int kk = 1; kk < figureNotes; kk++) { cc = NextChordToneUp(cc, pcs); if (cc > ceil) cc -= 12; lowFig.Add(cc); }
                    int lp = 0, li = 0;
                    while (lp + e <= slotSlices) { int p = lowFig[li % lowFig.Count] - 12; if (p >= 0 && p < 96) notes.Add(new RiffNote(p, slotStart + lp, e)); lp += e; li++; }
                    prevDeg = deg; prevInv = TonicInv; prevStart = low;
                    continue;
                }

                if (artUse == 7)
                {
                    // BLOCK CHORD + APPOGGIATURA (homophonic intro): an OPEN block (root + 5th) held the whole slot, with
                    // an upper APPOGGIATURA — a non-chord scale tone a step above the top chord tone, struck on beat 1 and
                    // RESOLVING down to it after ~an eighth (the expressive "leaning" dissonance the user wants for the intro).
                    var chB = ChordTones(prog[si].root, 3, prog[si].quality);
                    int b0 = NearestPc(prevStart, chB[0] % 12); while (b0 > 55) b0 -= 12; while (b0 < 43) b0 += 12;
                    int fifth = NextChordToneUpOpen(b0, pcs);                 // open: root + 5th
                    foreach (var m in new[] { b0, fifth }) { int p = m - 12; if (p >= 0 && p < 96) notes.Add(new RiffNote(p, slotStart, slotSlices)); }
                    int top = NextChordToneUpOpen(fifth, pcs);               // the resolution chord tone (top voice)
                    int app = ScaleStep(top, 1, scale);                       // a step above = the appoggiatura (non-chord)
                    int apP = app - 12, resP = top - 12;
                    if (apP >= 0 && apP < 96) notes.Add(new RiffNote(apP, slotStart, eighth));                       // leaning note on beat 1
                    if (resP >= 0 && resP < 96) notes.Add(new RiffNote(resP, slotStart + eighth, slotSlices - eighth)); // resolves down to the chord tone
                    prevDeg = deg; prevInv = TonicInv; prevStart = b0;
                    continue;
                }

                if (artUse == 8)
                {
                    // RONDE EN ARPÈGE EN TRIOLETS: a CONTINUOUS arpeggio from BEAT 1 in EIGHTH-TRIPLETS (Spq/3 = 8 slices,
                    // 3/beat — lighter than 16th-triplets). Each note RINGS to the END of the bar so the chord accumulates
                    // into a sustained "ronde" (early notes hold longest). FEW open-spaced chord tones (3-4) so the ringing
                    // texture stays light & never a 2nd apart. Direction per piece: ascending / descending / Alberti.
                    int tdur = Math.Max(2, Spq / 3);
                    int low = NearestPc(prevStart, ((prog[si].root % 12) + 12) % 12); while (low > 50) low -= 12; while (low < 40) low += 12;
                    var up = new List<int> { low }; int cc = low;
                    int span = 3 + rng.Next(2);
                    for (int k = 1; k < span; k++) { cc = NextChordToneUpOpen(cc, pcs); up.Add(cc); }
                    var pat = new List<int>();
                    if (tripDir == 1) { for (int z = up.Count - 1; z >= 0; z--) pat.Add(up[z]); }                                   // descending
                    else if (tripDir == 2) { int hi = up.Count - 1, mid = up.Count / 2; pat.Add(up[0]); pat.Add(up[hi]); pat.Add(up[mid]); pat.Add(up[hi]); } // Alberti
                    else pat = up;                                                                                                 // ascending
                    int pos = 0, idx = 0;
                    while (pos < slotSlices) { int p = pat[idx % pat.Count] - 12; if (p >= 0 && p < 96) notes.Add(new RiffNote(p, slotStart + pos, slotSlices - pos)); pos += tdur; idx++; } // ring to bar end
                    prevStart = low; prevDeg = deg; prevInv = TonicInv;
                    continue;
                }

                if (artUse == 9)
                {
                    // MONTÉE CINÉMATOGRAPHIQUE: a wide ASCENDING sweep of (open) chord tones from low across ~2 octaves,
                    // each note RINGING — a suspended rising gesture (best over a I-sus2 / V-sus4). Climbs to a high point.
                    int step = Math.Max(3, Spq / 4);   // sixteenths = a smooth sweep
                    int cur = NearestPc(prevStart, ((prog[si].root % 12) + 12) % 12); while (cur > 46) cur -= 12; while (cur < 38) cur += 12;
                    int p0 = 0;
                    while (p0 < slotSlices && cur < 84)
                    {
                        int p = cur - 12; if (p >= 0 && p < 96) notes.Add(new RiffNote(p, slotStart + p0, slotSlices - p0)); // ring to bar end
                        cur = NextChordToneUpOpen(cur, pcs); p0 += step;
                    }
                    prevDeg = deg; prevInv = TonicInv; prevStart = 55;
                    continue;
                }

                if (artUse == 10)
                {
                    // RONDE ARPÉGÉE: the chord ROLLED (arpeggiato, notes ~2 slices apart low→high) then RINGING the WHOLE
                    // bar = a rolled whole-note chord. Open-spaced tones; the fast roll → the score shows one chord + an
                    // arpeggio mark. (The intro's "deux premiers accords en rondes, arpegiato".)
                    int low = NearestPc(prevStart, ((prog[si].root % 12) + 12) % 12); while (low > 52) low -= 12; while (low < 40) low += 12;
                    var stack = new List<int> { low }; int cc = low;
                    int n = 3 + rng.Next(2);
                    for (int k = 1; k < n; k++) { cc = NextChordToneUpOpen(cc, pcs); stack.Add(cc); }
                    for (int z = 0; z < stack.Count; z++) { int off = Math.Min(z * 2, Math.Max(0, slotSlices - 1)); int p = stack[z] - 12; if (p >= 0 && p < 96) notes.Add(new RiffNote(p, slotStart + off, slotSlices - off)); }
                    prevDeg = deg; prevInv = TonicInv; prevStart = low;
                    continue;
                }

                if (artUse == 11)
                {
                    // OPEN-VOICED ascending arpeggio "2 quavers + a HELD top note" per CHORD SLOT, with PIANISTIC VOICE-
                    // LEADING (whole-voicing morph): among the OPEN voicings of the new chord we pick the one whose 3 notes
                    // move the LEAST in total from the previous voicing — COMMON TONES stay put, the others go to the
                    // nearest chord tone (the chord pivots around the held notes, like a pianist re-voicing). The 2 lower
                    // notes are the rising quavers; the top is HELD. Harmonic rhythm (1/2 chords per bar) is set by the trame.
                    int fastN = ternary ? Spq / 3 : Spq / 2;
                    int[] v = MorphOpenVoicing(prevVA, prevVB, prevVC, pcs);
                    int a = v[0], b2 = v[1], top = v[2];
                    if (slotSlices >= 3 * fastN)
                    {
                        int pa = a - 12, pb = b2 - 12, pt = top - 12;
                        if (pa >= 0 && pa < 96) notes.Add(new RiffNote(pa, slotStart, fastN));               // rising quaver 1
                        if (pb >= 0 && pb < 96) notes.Add(new RiffNote(pb, slotStart + fastN, fastN));       // rising quaver 2
                        if (pt >= 0 && pt < 96) notes.Add(new RiffNote(pt, slotStart + 2 * fastN, slotSlices - 2 * fastN)); // HELD top
                    }
                    else { int pt = top - 12; if (pt >= 0 && pt < 96) notes.Add(new RiffNote(pt, slotStart, slotSlices)); }
                    prevVA = a; prevVB = b2; prevVC = top; prevStart = a; prevDeg = deg; prevInv = TonicInv;
                    continue;
                }

                if (artUse == 2)
                {
                    // MELODIC EIGHTHS + a QUARTER: root, 3rd, PASSING tone (≈ the 4th), 5th, 3rd, passing… in 8ths, then
                    // the 5th held as a quarter (e.g. C: Do-Mi-Fa-Sol-Mi-Fa | Sol). Uses the TRIAD (root/3rd/5th — not
                    // the 7th/9th extensions) so the shape is clean for any chord quality.
                    var ch2 = ChordTones(prog[si].root, 3, prog[si].quality);
                    var triad = new HashSet<int> { ch2[0] % 12, ch2[Math.Min(1, ch2.Length - 1)] % 12, ch2[Math.Min(2, ch2.Length - 1)] % 12 };
                    int r = NearestPc(prevStart, ((prog[si].root % 12) + 12) % 12);
                    while (r > 64) r -= 12; while (r < 43) r += 12;
                    int t3 = NextChordToneUp(r, triad), t5 = NextChordToneUp(t3, triad);
                    int pass = ScaleStep(t3, 1, scale); if (pass <= t3 || pass >= t5) pass = t3 + 2; // passing tone between 3rd and 5th
                    int[] cyc = { r, t3, pass, t5, t3, pass };
                    int pos = 0, k = 0, holdAt = Math.Max(eighth, slotSlices - Spq);  // the closing QUARTER takes ~the last beat
                    while (pos < holdAt) { int p = cyc[k % cyc.Length] - 12; if (p >= 0 && p < 96) notes.Add(new RiffNote(p, slotStart + pos, Math.Min(eighth, holdAt - pos))); pos += eighth; k++; }
                    if (pos < slotSlices) { int p = t5 - 12; if (p >= 0 && p < 96) notes.Add(new RiffNote(p, slotStart + pos, slotSlices - pos)); }
                    prevStart = r; prevDeg = deg; prevInv = TonicInv;
                    continue;
                }

                if (artUse == 3)
                {
                    // WIDE ROLLED ARPEGGIO (harp/water): climb the chord tones across ~1.5 octaves then roll back DOWN,
                    // continuous (no held block) — a flowing figure. Uses the full chord (incl. 7th/9th).
                    int low3 = NearestPc(prevStart, ((prog[si].root % 12) + 12) % 12); while (low3 > 50) low3 -= 12; while (low3 < 36) low3 += 12;
                    var up = new List<int> { low3 }; int c3 = low3;
                    for (int kk = 1; kk < figureNotes + 2; kk++) { c3 = NextChordToneUp(c3, pcs); up.Add(c3); }
                    var contour = new List<int>(up);
                    for (int kk = up.Count - 2; kk >= 1; kk--) contour.Add(up[kk]);   // up then back down
                    int pos = 0, idx = 0;
                    while (pos + e <= slotSlices) { int p = contour[idx % contour.Count] - 12; if (p >= 0 && p < 96) notes.Add(new RiffNote(p, slotStart + pos, e)); pos += e; idx++; }
                    prevStart = low3; prevDeg = deg; prevInv = TonicInv;
                    continue;
                }

                if (artUse == 4)
                {
                    // SOFT CHORD STABS (the "pah" of an oom-pah / lullaby): a quiet closed triad on the beats AFTER the
                    // downbeat (the Bass track supplies the "oom" on beat 1). Closed voicing, low-treble.
                    var ch4 = ChordTones(prog[si].root, 3, prog[si].quality);
                    int b0 = NearestPc(prevStart, ch4[0] % 12); while (b0 > 60) b0 -= 12; while (b0 < 48) b0 += 12;
                    var triad4 = new List<int> { b0 }; int cc4 = b0;
                    // stack with a MIN gap of a minor 3rd (skips 2nds, e.g. a sus2's 2nd) → no harsh simultaneous 2nd.
                    for (int z = 1; z < 3; z++) { cc4 = NextChordToneUpOpen(cc4, pcs); triad4.Add(cc4); }
                    for (int b = 1; b * Spq < slotSlices; b++)
                    {
                        int onset = slotStart + b * Spq, dur = Math.Min(eighth, slotSlices - b * Spq);
                        foreach (var m in triad4) { int p = m - 12; if (p >= 0 && p < 96) notes.Add(new RiffNote(p, onset, dur)); }
                    }
                    prevStart = b0; prevDeg = deg; prevInv = TonicInv;
                    continue;
                }

                if (artUse == 5)
                {
                    // ROCKING / BARCAROLLE: a gentle low↔high alternation of chord tones (a cradle rock) in `e` notes.
                    int low5 = NearestPc(prevStart, ((prog[si].root % 12) + 12) % 12); while (low5 > 50) low5 -= 12; while (low5 < 43) low5 += 12;
                    int hi5 = NextChordToneUp(NextChordToneUp(low5, pcs), pcs);   // ~the 5th/7th above
                    int[] rock = { low5, hi5 };
                    int pos = 0, idx = 0;
                    while (pos + e <= slotSlices) { int p = rock[idx % rock.Length] - 12; if (p >= 0 && p < 96) notes.Add(new RiffNote(p, slotStart + pos, e)); pos += e; idx++; }
                    prevStart = low5; prevDeg = deg; prevInv = TonicInv;
                    continue;
                }

                // artUse == 1: RISING ARPEGGIO + a held (fast-rolled) chord, inversions from the voice-leading table.
                var ch = ChordTones(prog[si].root, 3, prog[si].quality);
                var voice = pcs; int inv, startPc;                            // the pitch-classes the figure walks
                if (openVoicing)
                {
                    // OPEN voicing — root, 5th, 9th: OMIT the 3rd (P5/P4 intervals, more emotionally ambiguous).
                    voice = new HashSet<int> { ch[0] % 12, ch[Math.Min(2, ch.Length - 1)] % 12, (ch[0] + 2) % 12 };
                    inv = TonicInv; startPc = ch[0] % 12;                      // start on the root
                }
                else { inv = (deg == 0) ? TonicInv : VoiceLeadTable[prevDeg, prevInv, deg]; startPc = ch[Math.Min(inv, ch.Length - 1)] % 12; }
                int start = NearestPc(prevStart, startPc);
                while (start > 66) start -= 12; while (start < 43) start += 12;
                var fig = new List<int> { start }; int c = start;
                for (int kk = 1; kk < figureNotes; kk++) { c = NextChordToneUp(c, voice); if (c > start + 14) c -= 12; fig.Add(c); }
                int pos2 = 0, nClimb = Math.Min(fig.Count, Math.Max(1, slotSlices / e - 1));
                for (int kk = 0; kk < nClimb && pos2 + e <= slotSlices; kk++) { int p = fig[kk] - 12; if (p >= 0 && p < 96) notes.Add(new RiffNote(p, slotStart + pos2, e)); pos2 += e; }
                int topIdx = Math.Max(0, Math.Min(nClimb - 1, fig.Count - 1)), topPitch = fig[topIdx];
                if (pos2 < slotSlices)
                {
                    // held chord as a FAST ROLL (1-2 slices) → one chord + arpeggio mark in the score/export.
                    int holdLen = slotSlices - pos2, t = topPitch;
                    var stack = new List<int> { t };
                    // step DOWN to the next chord tone at least a MINOR THIRD below (dd>=3) → no two STACKED notes a 2nd apart.
                    for (int n = 1; n < blockSize; n++) { int dn = t; for (int dd = 3; dd <= 14; dd++) if (voice.Contains((((t - dd) % 12) + 12) % 12)) { dn = t - dd; break; } if (dn == t) break; t = dn; stack.Add(t); }
                    stack.Reverse();
                    int roll = (holdLen >= 24) ? 2 : 1;
                    for (int n = 0; n < stack.Count; n++) { int off = Math.Min(n * roll, Math.Max(0, holdLen - 1)); int p = stack[n] - 12; if (p >= 0 && p < 96) notes.Add(new RiffNote(p, slotStart + pos2 + off, holdLen - off)); }
                }
                prevDeg = deg; prevInv = inv; prevStart = start;
            }
            return new Riff { Name = "Accompagnement", Notes = notes, LengthSlices = prog.Count * slotSlices, SlicesPerQuarter = Spq };
        }


        // ---- sustained-root PEDAL bass (one held root per chord-slot, low register). When `pedal`, the BASS instead
        // holds the TONIC under every chord (a BITONAL "morphing chord" device, per the Ghibli template: e.g. an Eb maj7
        // over a sustained low C reads as C min9 — the harmony seems to drift while the chords above stay put). The
        // pedal tonic is re-struck every ~2 chord-slots so it never fully dies away. ----
        internal static Riff PedalBass(List<(int root, int quality)> prog, int slotSlices, int tonicPc, bool pedal)
        {
            var notes = new List<RiffNote>();
            if (pedal)
            {
                int row = (((tonicPc % 12) + 12) % 12) + 24;       // low tonic pedal
                for (int si = 0; si < prog.Count; si += 2)
                {
                    int len = Math.Min(2, prog.Count - si) * slotSlices;
                    notes.Add(new RiffNote(row, si * slotSlices, len));
                }
                return new Riff { Name = "Basse (pédale)", Notes = notes, LengthSlices = prog.Count * slotSlices, SlicesPerQuarter = Spq };
            }
            for (int si = 0; si < prog.Count; si++)
            {
                int row = (((prog[si].root % 12) + 12) % 12) + 24; // ~MIDI 36-47 once played
                notes.Add(new RiffNote(row, si * slotSlices, slotSlices));
            }
            return new Riff { Name = "Basse", Notes = notes, LengthSlices = prog.Count * slotSlices, SlicesPerQuarter = Spq };
        }

        // ---- sustained string PAD for the climax: a SOFT, THIN, LOW bed — NOT a full block chord (those were heavy &
        // shrill). Only root + 5th + ONE upper colour tone (the 7th if the chord has one, else the 3rd), held. ----
        internal static Riff PadChords(List<(int root, int quality)> prog, int slotSlices, int fromSlot, int toSlot, int octave)
        {
            var notes = new List<RiffNote>();
            for (int si = fromSlot; si < toSlot && si < prog.Count; si++)
            {
                var ch = ChordTones(prog[si].root, octave, prog[si].quality);
                var voice = new List<int> { ch[0] };                                  // root
                if (ch.Length > 2) voice.Add(ch[2]);                                  // 5th
                if (ch.Length > 3) voice.Add(ch[3]); else if (ch.Length > 1) voice.Add(ch[1]); // ONE upper colour (7th if present, else the 3rd)
                foreach (var m in voice) { int row = m - 12; if (row >= 0 && row < 96) notes.Add(new RiffNote(row, si * slotSlices, slotSlices)); }
            }
            return new Riff { Name = "Cordes", Notes = notes, LengthSlices = prog.Count * slotSlices, SlicesPerQuarter = Spq };
        }

        // ---- a SEPARATE string COUNTER-MELODY for the climax (NOT the melody doubled): an INDEPENDENT line that walks
        // the chord tones in a mid register (below/around the melody), in long-ish values, drifting in its own
        // direction so its motion vs the melody is MIXED (oblique/parallel/contrary by turns), not systematically
        // parallel. Spans the whole piece length but only sounds in the climax slots [fromSlot, toSlot). ----
        internal static Riff StringCounter(List<(int root, int quality)> prog, int slotSlices, int fromSlot, int toSlot, int lenSlices, HashSet<int> scale, bool ternary, bool ensureThird, Random rng)
        {
            // A rhythmically ACTIVE inner line that keeps momentum when the theme is slow (the videos' key device). It
            // WEAVES chord tones (on strong beats) with PASSING/NEIGHBOUR non-chord scale tones (more dissonance than
            // the melody → the nostalgic colour/ambiguity). Mid register, below the melody; its own drifting contour.
            // When the accompaniment uses OPEN voicings (3rd omitted), this inner voice RESTORES the chord's defining
            // 3rd at least ONCE per chord (the Mononoke device: the omitted emotional pitch is supplied by the counter).
            int e = ternary ? Spq / 3 : Spq / 2;
            var notes = new List<RiffNote>();
            int cur = 62, dir = rng.Next(2) == 0 ? 1 : -1;          // ~D4
            for (int si = fromSlot; si < toSlot && si < prog.Count; si++)
            {
                var cp = ChordPcs(prog[si].root, prog[si].quality);
                int third = -1;
                if (ensureThird) { var ct = ChordTones(prog[si].root, 4, prog[si].quality); if (ct.Length > 1) { int iv = (((ct[1] - ct[0]) % 12) + 12) % 12; if (iv == 3 || iv == 4) third = ct[1] % 12; } }
                bool thirdDone = false;
                int start = si * slotSlices, pos = 0;
                while (pos < slotSlices)
                {
                    int dur = Math.Min((rng.Next(100) < 55 ? Spq : e), slotSlices - pos);  // mix quarters & eighths (active)
                    bool strong = (pos % Spq) == 0;
                    if (strong && third >= 0 && !thirdDone) { cur = NearestChord(cur, new HashSet<int> { third }); thirdDone = true; } // supply the omitted 3rd
                    else if (strong && rng.Next(100) < 60) cur = NearestChord(cur + dir, cp);     // anchor to a chord tone on the beat
                    else { cur = ScaleStep(cur, dir, scale); }                              // a stepwise NON-chord passing/neighbour tone
                    if (rng.Next(100) < 30) dir = -dir;                                       // wander independently
                    while (cur > 71) cur -= 12; while (cur < 55) cur += 12;                   // inner voice (≈ G3–B4)
                    int p = cur - 12; if (p >= 0 && p < 96) notes.Add(new RiffNote(p, start + pos, dur));
                    pos += dur;
                }
            }
            return new Riff { Name = "Cordes (contre-chant)", Notes = notes, LengthSlices = lenSlices, SlicesPerQuarter = Spq };
        }

        // Melody scale-degree MARKOV candidates (canonicalized corpus transitions): flattened {pc, weight, ...}.
        static int[] MelodyCandidates(int deg, bool minor)
        {
            if (minor)
                switch (deg)
                {
                    case 0: return new[] { 0, 25, 3, 15, 7, 14, 10, 13, 2, 10 };
                    case 2: return new[] { 3, 23, 0, 19, 7, 16, 2, 12 };
                    case 3: return new[] { 2, 19, 5, 19, 3, 17, 10, 11 };
                    case 5: return new[] { 5, 24, 7, 20, 3, 15, 0, 12 };
                    case 7: return new[] { 0, 22, 7, 16, 5, 13, 10, 11 };
                    case 8: return new[] { 10, 20, 8, 17, 7, 17, 3, 13 };
                    case 9: return new[] { 9, 30, 7, 14, 10, 13, 2, 12 };
                    case 10: return new[] { 0, 26, 10, 15, 7, 12, 8, 11 };
                    default: return new[] { 0, 1 };
                }
            switch (deg)
            {
                case 0: return new[] { 7, 32, 2, 13, 0, 12, 5, 11 };
                case 2: return new[] { 4, 22, 0, 19, 2, 17, 5, 10 };
                case 4: return new[] { 2, 24, 7, 17, 11, 17, 5, 11 };
                case 5: return new[] { 7, 21, 5, 19, 0, 14, 9, 12 };
                case 6: return new[] { 7, 45, 4, 15, 11, 10, 6, 10 };
                case 7: return new[] { 0, 33, 7, 16, 9, 15, 6, 8 };
                case 9: return new[] { 7, 23, 0, 18, 9, 16, 2, 13 };
                case 11: return new[] { 4, 28, 9, 18, 11, 15, 0, 12 };
                default: return new[] { 0, 1 };
            }
        }

        // Pick the octave of `pc` nearest `cur`, but PREFER direction prefDir (+up / -down) when it's within a fifth —
        // used to shape an ARCH (rise to the phrase middle, then fall).
        internal static int NearestPcDir(int cur, int pc, int prefDir)
        {
            int up = cur, dn = cur;
            for (int r = 0; r < 12; r++) { if ((((cur + r) % 12) + 12) % 12 == pc) { up = cur + r; break; } }
            for (int r = 0; r < 12; r++) { if ((((cur - r) % 12) + 12) % 12 == pc) { dn = cur - r; break; } }
            if (prefDir > 0) return (up - cur <= 7) ? up : dn;
            if (prefDir < 0) return (cur - dn <= 7) ? dn : up;
            return (up - cur <= cur - dn) ? up : dn;
        }

        // The THEME — built CHORDS-FIRST with the PRE-VA-DE method (a "Melody for Composers" strategy the user chose):
        // an 8-bar PERIOD is cut into FOUR 2-bar sub-phrases — PRÉSENTATION · RÉPÉTITION · VARIATION · DÉCONSTRUCTION —
        // each over ITS OWN chords. A single 2-bar RHYTHMIC MOTIF is generated once and REUSED across the four (the
        // motivic identity). Per measure we choose 2 chord-tone ANCHORS by scale-degree PERSONALITY (3rd = emotion,
        // 5th = stability, colour 7th/9th = atmosphere; the ROOT sparingly and NEVER on the downbeat of a chord change,
        // to keep the tune independent of the harmony) and fill the rest with stepwise EMBELLISHMENTS. The VARIATION
        // pushes to the phrase PEAK near its end; the DÉCONSTRUCTION relaxes downward; the final sub-phrase cadences to
        // the tonic. Leaps are capped at a fifth (singable). The melody switches to the modulation target at the C boundary.
        static Riff PhrasedMelody(int tonicPc, HashSet<int> scale, bool minor, List<(int root, int quality)> prog, int beatsPerBar, int barSlices, int melBars, int slot, bool ternary, int feel, Random rng, int center, int modBar, int tonic2, HashSet<int> scale2, bool minor2)
        {
            int total = melBars * barSlices, lo = center - 9, hi = center + 11;
            int cur = NearestPc(center, tonicPc), endTonic = tonicPc;
            int mid = Math.Max(Spq, (beatsPerBar / 2) * Spq);   // the in-bar strong beat for the 2nd anchor
            var notes = new List<RiffNote>();

            for (int p0 = 0; p0 < melBars; p0 += 8)
            {
                int barsHere = Math.Min(8, melBars - p0), nSub = Math.Max(1, barsHere / 2);
                var motif = MakeRhythm(2 * barSlices, feel, ternary, rng);   // ONE 2-bar rhythm, reused by all sub-phrases
                for (int sp = 0; sp < nSub; sp++)
                {
                    int sBar = p0 + sp * 2, sStart = sBar * barSlices;
                    bool isVar = (sp % 4) == 2, isDecon = (sp % 4) == 3, lastSub = (sBar + 2 >= melBars);
                    int dirBias = isVar ? 1 : isDecon ? -1 : 0;             // VARIATION climbs to the peak · DÉCONSTRUCTION descends
                    bool inMod = modBar >= 0 && sBar >= modBar;             // modulation target from the C boundary on
                    int kTonic = inMod ? tonic2 : tonicPc; var kScale = inMod ? scale2 : scale; endTonic = kTonic;

                    // ANCHOR slots (2 per bar) + their chosen pitches.
                    var aSlot = new List<int>(); for (int b = 0; b < 2; b++) { aSlot.Add(b * barSlices); aSlot.Add(b * barSlices + mid); }
                    var aPitch = new List<int>();
                    for (int ai = 0; ai < aSlot.Count; ai++)
                    {
                        var ch = prog[Math.Min((sStart + aSlot[ai]) / slot, prog.Count - 1)];
                        bool downbeatChange = (aSlot[ai] % barSlices) == 0;
                        int forceTonic = (lastSub && ai == aSlot.Count - 1) ? kTonic : -1;   // conclusive landing (final sub-phrase)
                        bool suspensive = !lastSub && ai == aSlot.Count - 1;                 // a non-final phrase ends OPEN on the 5th (hanging)
                        int pc = AnchorPc(ch.root, ch.quality, kScale, downbeatChange, forceTonic, suspensive, rng);
                        int pitch = NearestPcDir(cur, ((pc % 12) + 12) % 12, dirBias);
                        while (pitch > hi) pitch -= 12; while (pitch < lo) pitch += 12;
                        pitch = CapLeap(cur, pitch, kScale, 7);
                        aPitch.Add(pitch); cur = pitch;
                    }
                    if (isVar)   // ensure the PEAK lands on the LAST anchor (near the sub-phrase end)
                    {
                        int top = lo; foreach (var x in aPitch) top = Math.Max(top, x);
                        int peak = Math.Min(hi, top + 2); aPitch[aPitch.Count - 1] = NearestScale(peak, kScale);
                    }

                    // REALIZE the motif: each onset takes its covering ANCHOR pitch, else a stepwise EMBELLISHMENT toward
                    // the next anchor (passing/neighbour tones, non-chord allowed = the energy/colour).
                    int pos = 0, prevPitch = cur;
                    for (int mi2 = 0; mi2 < motif.Count && pos < 2 * barSlices; mi2++)
                    {
                        int dur = motif[mi2], anchorIdx = -1;
                        for (int ai = 0; ai < aSlot.Count; ai++) if (pos <= aSlot[ai] && (pos + dur > aSlot[ai] || mi2 == motif.Count - 1)) { anchorIdx = ai; break; }
                        int pitch;
                        if (anchorIdx >= 0) pitch = aPitch[anchorIdx];
                        else
                        {
                            int target = aPitch[aPitch.Count - 1];
                            for (int ai = 0; ai < aSlot.Count; ai++) if (aSlot[ai] > pos) { target = aPitch[ai]; break; }
                            pitch = ScaleStep(prevPitch, target >= prevPitch ? 1 : -1, kScale);
                        }
                        while (pitch > hi) pitch -= 12; while (pitch < lo) pitch += 12;
                        pitch = CapLeap(prevPitch, pitch, kScale, 7);
                        int abs = sStart + pos, len = Math.Min(dur, 2 * barSlices - pos);
                        if (abs >= 0 && abs < total) notes.Add(new RiffNote(Math.Max(0, Math.Min(95, pitch - 12)), abs, Math.Min(len, total - abs)));
                        prevPitch = pitch; cur = pitch; pos += dur;
                    }
                    // a small BREATH closing each sub-phrase (shorten the last note so a rest precedes the next).
                    if (notes.Count > 0) { var ln = notes[notes.Count - 1]; int br = ternary ? Spq : Spq / 2; if (ln.Length > br * 2) notes[notes.Count - 1] = new RiffNote(ln.Note, ln.Start, ln.Length - br); }
                }
            }
            if (notes.Count > 0) { var ln = notes[notes.Count - 1]; int t = NearestPc(ln.Note + 12, endTonic); notes[notes.Count - 1] = new RiffNote(Math.Max(0, Math.Min(95, t - 12)), ln.Start, ln.Length); }
            return new Riff { Name = "Mélodie", Notes = notes, LengthSlices = total, SlicesPerQuarter = Spq };
        }

        // A 2-bar RHYTHMIC MOTIF (durations tiling `lenSlices`); shortest = an eighth (template), with quarters, dotted
        // quarters and (lent) longer holds. The same motif is reused across the period's sub-phrases.
        internal static List<int> MakeRhythm(int lenSlices, int feel, bool ternary, Random rng)
        {
            int sub = ternary ? Spq / 3 : Spq / 2, q = Spq, dotted = ternary ? Spq + sub : Spq * 3 / 2, half = Spq * 2;
            int[] pool = feel == 0 ? new[] { q, 34, dotted, 22, half, 26, sub, 18 }
                       : feel == 2 ? new[] { sub, 46, q, 34, dotted, 20 }
                       : new[] { sub, 32, q, 38, dotted, 18, half, 12 };
            var r = new List<int>(); int acc = 0;
            while (acc < lenSlices)
            {
                int d = PickWeighted(pool, rng);
                if (acc + d > lenSlices) d = lenSlices - acc;
                if (d <= 0) break;
                r.Add(d); acc += d;
            }
            return r;
        }

        // Pick an ANCHOR pitch-class for a chord by scale-degree PERSONALITY — but ONLY among chord tones that are IN the
        // KEY's scale, so the melody stays PENTATONIC/diatonic & SOFT even when the HARMONY borrows a chromatic chord
        // (e.g. a Phrygian bII / Neapolitan = Db: the melody takes its in-scale tone F, NOT the harsh Db/Ab). This is the
        // corpus rule "the tune stays simple, the harmony has the colour" (78% of melody weight on ~5 notes, tendency
        // tones avoided). The 5th (open/suspensive) leads, then the 3rd (emotion), a colour tone, the root (sparingly,
        // never on a chord-change downbeat). `forceTonic` >= 0 → conclusive tonic. `suspensive` → the open 5th (hanging).
        internal static int AnchorPc(int root, int quality, HashSet<int> scale, bool downbeatChange, int forceTonic, bool suspensive, Random rng)
        {
            if (forceTonic >= 0) return ((forceTonic % 12) + 12) % 12;
            var ct = ChordTones(root, 4, quality);
            int Snap(int pc) { int p = ((pc % 12) + 12) % 12; return scale.Contains(p) ? p : ((NearestScale(60 + p, scale) % 12) + 12) % 12; }
            if (suspensive) return Snap(ct.Length > 2 ? ct[2] : ct[0]);                            // the OPEN 5th (snapped into the key) = a "hanging" end
            var ch = new List<int>(); var wt = new List<int>();
            void AddDiatonic(int pc, int w) { int p = ((pc % 12) + 12) % 12; if (scale.Contains(p)) { ch.Add(p); wt.Add(w); } }
            if (ct.Length > 2) AddDiatonic(ct[2], 34);                                             // 5th (openness/stability) — favoured
            if (ct.Length > 1) AddDiatonic(ct[1], 26);                                             // 3rd (the emotional colour)
            if (ct.Length > 3) AddDiatonic(ct[3 + rng.Next(ct.Length - 3)], 22);                   // a colour tone (7th/9th)
            if (!downbeatChange) AddDiatonic(ct[0], 18);                                           // root (not on a chord-change downbeat)
            if (ch.Count == 0) return Snap(ct[0]);                                                 // chord fully chromatic → snap the root into the key
            int tot = 0; foreach (var w in wt) tot += w; int rr = rng.Next(Math.Max(1, tot)), a = 0;
            for (int i = 0; i < ch.Count; i++) { a += wt[i]; if (rr < a) return ch[i]; }
            return ch[0];
        }
    }
}
