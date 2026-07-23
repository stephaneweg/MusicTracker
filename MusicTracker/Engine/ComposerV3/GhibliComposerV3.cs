using System;
using System.Collections.Generic;
using System.Linq;
using MusicTracker.Engine.Score;
using MusicTracker.Engine.ComposerV2;   // kept data layer

namespace MusicTracker.Engine.ComposerV3
{
    /// <summary>
    /// Composer V2 — the GHIBLI style, derived from <see cref="BaseComposerV3"/>. Adds the
    /// theory-driven devices the template (Ghibli_Analyse_*.txt) stresses, on top of the base toolkit:
    ///   • rhythmic personality — dotted-eighth/sixteenth swing, occasional triplets, phrase anacruses;
    ///   • momentum tricks — fake harmonic rhythm (colour toggle on a held root), chord-morphing
    ///     (static triad over a moving extended-chord arpeggio), a secondary-dominant tease;
    ///   • development/form — a whole-step modulation at the climax (then return), a reharmonized
    ///     reprise (same theme, new chords), a second motif, glockenspiel at the climax, a Picardy close;
    ///   • open voicings (omit the 3rd) in the accompaniment.
    /// Form: Intro 2 · Theme 8 (flute) · Theme-repeat 8 (oboe) · Development 16 (two motifs in
    /// counterpoint, buildup→climax→attenuate, modulated) · Reprise 8 (reharmonized) · Outro 4.
    /// </summary>
    public class GhibliComposerV3 : BaseComposerV3
    {
        public override System.Collections.Generic.IReadOnlyList<string> Styles => new[] { "Chantant", "Berceuse", "Valse", "Aventure", "Mélancolie" };
        public override string FamilyKey => "ghibli";

        bool drumsEnabled;
        int drumPart = -1;
        string chosenMode = "aeolian";

        /// <summary>Mode chosen at Configure (valid after Compose) — for callers/tests/UI. (Character is on the base.)</summary>
        public string ChosenMode { get { return chosenMode; } }
        public bool ChosenMinor { get { return minor; } }

        protected override void Configure()
        {
            base.Configure();                          // A, 76 bpm, registers
            LearnedAccomp = true;                      // accompaniment = the corpus's ARPEGGIATED texture (AccompCell +
                                                       // AccompTone), not block chords — matches Hisaishi's flowing
                                                       // broken-chord left hand. See RenderLearnedAccompBar.

            // ROOT CONTEXT (like the mode): the melody CHARACTER, picked first so it can bias the mode below.
            PickCharacter();

            // ROOT CONTEXT: pick a church MODE from the corpus mode distribution → scale + major-or-minor model.
            // The LIVELY and the MAJESTIC characters lean bright = plain MAJOR (ionian), fewer modal borrowings.
            bool wantMajor = character == "enjouée" || character == "majestueux";
            double majorBias = character == "majestueux" ? 0.95 : 0.85;
            string mode = (wantMajor && rng.NextDouble() < majorBias)
                        ? "ionian"
                        : (Pick(model.ModeDistribution, rng) ?? "aeolian");
            chosenMode = mode;
            minor = MusicMathV2.IsMinorMode(mode);
            scale = MusicMathV2.ScaleForMode(mode);
            tonicPc = 9; tonicLetter = 5; tonicAccidental = 0;

            // tempo follows the character
            if (character == "enjouée") bpm = 92 + rng.Next(14);          // 92-105
            else if (character == "majestueux") bpm = 80 + rng.Next(14);  // 80-93 (broad march)
            else if (character == "calme") bpm = 64 + rng.Next(10);       // 64-73
            else bpm = 76 + rng.Next(10);                                 // 76-85 (modérée)

            int[] melSet = { 0, 40, 73, 68, 24 };      // piano, violin, flute, oboe, nylon guitar
            int[] accSet = { 0, 48, 46, 24 };          // piano, string ensemble, harp, nylon guitar
            int m1 = melSet[rng.Next(melSet.Length)];
            int m2 = melSet[rng.Next(melSet.Length)]; // independent draw: any pairing (incl. same, e.g. piano+violin)
            int acc = accSet[rng.Next(accSet.Length)];
            programs = new[] { m1, m2, acc, 48, 9 };   // melody1, melody2, accompaniment, bass(strings), glockenspiel
            partNames = new[] { GmName(m1), GmName(m2), GmName(acc), "Cordes (basse)", "Glockenspiel" };
            partClefs = new[] { ScoreClefKind.Treble, ScoreClefKind.Treble, ScoreClefKind.Treble, ScoreClefKind.Bass, ScoreClefKind.Treble };
            partVolumes = new[] { 1.0, 0.8, 0.85, 0.85, 0.6 };
            title = "Ghibli V2";

            // LIGHT percussion — probability cascades from the character (lively pieces drum more often,
            // calm pieces rarely). The corpus has drums in ~30% of files.
            double drumProb = character == "enjouée" ? 0.55 : (character == "majestueux" ? 0.45 : (character == "calme" ? 0.08 : 0.30));
            drumsEnabled = model.PiecesWithDrums > 0 && rng.NextDouble() < drumProb;
            if (drumsEnabled)
            {
                programs = programs.Concat(new[] { 0 }).ToArray();          // kit (program ignored on ch.10)
                partNames = partNames.Concat(new[] { "Batterie" }).ToArray();
                partClefs = partClefs.Concat(new[] { ScoreClefKind.Treble }).ToArray();
                partVolumes = partVolumes.Concat(new[] { 0.55 }).ToArray();
                drumPart = programs.Length - 1;
                partIsDrum = new bool[programs.Length];
                partIsDrum[drumPart] = true;
            }
            else partIsDrum = null;
        }

        // light percussion from the corpus model: per-beat drum-class pattern by section -> GM kit notes
        void RenderPercussion(int start, int bars, string sec, int vel)
        {
            if (!drumsEnabled) return;
            for (int b = 0; b < bars; b++)
            {
                int abs = start + b * Bar;
                for (int beat = 0; beat < 4; beat++)
                {
                    string pat = Pick(Dist(model.PercOnset, new[] { "sec|beat", "beat", "" }, new[] { sec + "|" + beat, beat.ToString(), "" }), rng);
                    if (string.IsNullOrEmpty(pat) || pat == "-") continue;
                    foreach (string cls in pat.Split('+'))
                    {
                        int note = DrumNote(cls);
                        if (note > 0) notes.Add(new OutNote { Part = drumPart, Pitch = note, Start = abs + beat * Beat, Len = 6, Vel = vel });
                    }
                }
            }
        }

        static int DrumNote(string cls)
        {
            switch (cls)
            {
                case "kick": return 36; case "snare": return 38; case "rim": return 37; case "hat": return 42; case "hatopen": return 46;
                case "crash": return 49; case "ride": return 51; case "tom": return 45; case "tamb": return 54; case "cowbell": return 56; case "triangle": return 81;
                default: return 39;
            }
        }

        static string GmName(int p)
        {
            switch (p)
            {
                case 40: return "Violon"; case 73: return "Flute"; case 68: return "Hautbois"; case 24: return "Guitare nylon";
                case 0: return "Piano"; case 48: return "Cordes"; case 46: return "Harpe"; default: return "Inst" + p;
            }
        }

        // ---- harmony: add a secondary-dominant tease (resolves to the next chord, no modulation) ----
        protected override List<Cell> SampleProg(CorpusModelV2 model, ModeModels mm, Random rng, int bars, bool tonicStart, bool tonicEnd, string section = "body")
        {
            var cells = base.SampleProg(model, mm, rng, bars, tonicStart, tonicEnd, section);
            // LIVELY + major: snap most modal-borrowed roots (bII/bIII/bVI/bVII/#IV…) to the nearest diatonic
            // major degree so the harmony stays bright — fewer modal colours, more plain major.
            if (character == "enjouée" && !minor) SnapToDiatonicMajor(cells);
            // Keep EVERY chord within the current mode's scale: the model's QualityByDegree can return "maj" on a
            // degree whose major third is out of key (e.g. a MAJOR tonic — C# — in A minor) → re-quality to the
            // diatonic triad. In-scale colour (bVII, bIII, maj7, add9, sus…) is preserved; only clashing
            // accidentals are removed. (Replaces the old chromatic secondary-dominant tease, un-idiomatic for Ghibli.)
            SnapChordsToScale(cells);
            return cells;
        }

        static readonly int[] DiatonicMajorDegrees = { 0, 2, 4, 5, 7, 9, 11 };
        void SnapToDiatonicMajor(List<Cell> cells)
        {
            foreach (var c in cells)
            {
                int r = Mod12(c.Root);
                if (Array.IndexOf(DiatonicMajorDegrees, r) >= 0) continue;   // already diatonic
                if (rng.NextDouble() >= 0.75) continue;                       // keep ~25% borrowings for colour
                int best = 0, bd = 99;
                foreach (int d in DiatonicMajorDegrees) { int dist = Math.Min(Mod12(d - r), Mod12(r - d)); if (dist < bd) { bd = dist; best = d; } }
                c.Root = best;
                c.Canon = GroupToCanon(MajorTriadQuality(best));
                c.BassDeg = -1;
            }
        }
        static string MajorTriadQuality(int deg)
        {
            switch (Mod12(deg)) { case 0: case 5: case 7: return "maj"; case 2: case 4: case 9: return "min"; case 11: return "dim"; default: return "maj"; }
        }

        // Force every chord to lie within the current mode's scale: snap an out-of-scale ROOT to the nearest
        // scale degree, and if any chord tone falls outside the scale, collapse the chord to the diatonic triad
        // built on that scale degree. Keeps modal colour that is already in-scale; removes clashing accidentals.
        void SnapChordsToScale(List<Cell> cells)
        {
            foreach (var c in cells)
            {
                if (Array.IndexOf(scale, Mod12(c.Root)) < 0) { c.Root = NearestScaleDegree(c.Root); c.BassDeg = -1; }
                bool outOfScale = false;
                foreach (int pc in ChordPcs(c)) if (Array.IndexOf(scale, Mod12(pc)) < 0) { outOfScale = true; break; }
                if (outOfScale) { c.Canon = GroupToCanon(ScaleTriadQuality(c.Root)); c.BassDeg = -1; }
            }
        }

        int NearestScaleDegree(int deg)
        {
            int d0 = Mod12(deg);
            for (int d = 0; d <= 6; d++)
            {
                if (Array.IndexOf(scale, Mod12(d0 + d)) >= 0) return Mod12(d0 + d);
                if (Array.IndexOf(scale, Mod12(d0 - d)) >= 0) return Mod12(d0 - d);
            }
            return 0;
        }

        // quality of the diatonic triad on a scale degree (root + scale-third + scale-fifth)
        string ScaleTriadQuality(int rootDeg)
        {
            int idx = Array.IndexOf(scale, Mod12(rootDeg));
            if (idx < 0) return minor ? "min" : "maj";
            int third = Mod12(scale[(idx + 2) % scale.Length] - Mod12(rootDeg));
            int fifth = Mod12(scale[(idx + 4) % scale.Length] - Mod12(rootDeg));
            if (fifth == 6) return "dim";
            return third == 3 ? "min" : "maj";
        }

        // ---- melody: base line + ornamentation (dotted/triplet) + phrase anacruses ----
        protected override List<MelNote> GenerateLine(CorpusModelV2 model, ModeModels mm, Random rng, List<Cell> prog,
                                          int tonicPc, int[] scale, int loMel, int hiMel, bool cadenceFinal, int startDeg,
                                          string section, bool invertArch = false)
        {

            var line = base.GenerateLine(model, mm, rng, prog, tonicPc, scale, loMel, hiMel, cadenceFinal, startDeg, section, invertArch);
            
            if (character == "majestueux") MarchBroaden(line);   // broad, ~one note per beat (almost a march)
            else MakeMelodic(line, tonicPc, scale, rng);         // fill the arc with stepwise passing tones, de-repeat
            Ornament(line, tonicPc, scale, rng);                 // dotted/triplet/anacrusis (character-scaled)
            ShapePhraseArc(line, tonicPc, scale, loMel, hiMel);  // AFTER ornaments → every note follows the arch
            ArticulateForCharacter(line);                        // lively = staccato/picato ; calm = legato
            
            return line;
        }

        // MAJESTIC: broaden the line toward a march — one note per beat, consecutive same pitches merged into
        // longer held notes, so the melody moves in stately quarter/half values instead of busy figuration.
        void MarchBroaden(List<MelNote> line)
        {
            if (line.Count == 0) return;
            line.Sort((x, y) => x.Start.CompareTo(y.Start));
            int total = line[line.Count - 1].Start + line[line.Count - 1].Len;
            var perBeat = new List<MelNote>();
            for (int pos = 0; pos < total; pos += Beat)
            {
                MelNote chosen = null;
                foreach (var n in line) if (n.Start <= pos && n.Start + n.Len > pos) chosen = n;        // note sounding on the beat
                if (chosen == null) foreach (var n in line) if (n.Start >= pos && n.Start < pos + Beat) { chosen = n; break; }
                if (chosen == null) continue;
                perBeat.Add(new MelNote { Pitch = chosen.Pitch, Deg = chosen.Deg, Start = pos, Len = Beat });
            }
            // merge consecutive same-pitch beats into one held note (a sustained march tone)
            var merged = new List<MelNote>();
            foreach (var n in perBeat)
            {
                if (merged.Count > 0 && merged[merged.Count - 1].Pitch == n.Pitch &&
                    merged[merged.Count - 1].Start + merged[merged.Count - 1].Len == n.Start)
                    merged[merged.Count - 1].Len += n.Len;
                else merged.Add(n);
            }
            line.Clear(); line.AddRange(merged);
        }

        // Give each phrase a clear ARCH: rise to a single peak near the golden section (~60% in) then fall to
        // the cadence. A triangular pitch bias (low at the ends, high at the peak) is ADDED to the line while
        // the model's own up/down motion is gently damped, then each note is snapped back to the scale. This
        // keeps the model's melodic character but makes the phrase shape unmistakable.
        void ShapePhraseArc(List<MelNote> line, int tonicPc, int[] scale, int loMel, int hiMel)
        {
            if (line.Count < 3) return;
            line.Sort((x, y) => x.Start.CompareTo(y.Start));
            double height = character == "enjouée" ? 10 : (character == "calme" ? 8 : (character == "majestueux" ? 7 : 9)); // arch height (semitones)
            const double keep = 0.40;                                                       // damp model wiggle more so the arch dominates

            // group by 2-bar windows = the base generator's phrase unit (robust vs internal rests/long notes)
            int phraseSpan = 2 * Bar;
            var byWin = new SortedDictionary<int, List<MelNote>>();
            foreach (var n in line)
            {
                int w = n.Start / phraseSpan;
                List<MelNote> g; if (!byWin.TryGetValue(w, out g)) { g = new List<MelNote>(); byWin[w] = g; }
                g.Add(n);
            }

            foreach (var ph in byWin.Values)
            {
                int N = ph.Count;
                if (N < 3) continue;
                int k = Math.Max(1, Math.Min(N - 2, (int)Math.Round(0.6 * (N - 1))));        // peak index ~60% in
                double mean = ph.Average(n => n.Pitch);
                // centre the arch in a band that leaves headroom for the peak (no ceiling/floor saturation,
                // which would flatten the top and make the peak land early)
                double center = Math.Max(loMel + height * 0.4, Math.Min(hiMel - height * 0.6, mean));
                for (int i = 0; i < N; i++)
                {
                    double frac = i <= k ? (double)i / k : (double)(N - 1 - i) / Math.Max(1, N - 1 - k); // 0 ends, 1 peak
                    double shaped = center + (ph[i].Pitch - mean) * keep + height * (frac - 0.4);        // damp wiggle + add arch
                    int p = SnapScale((int)Math.Round(shaped), tonicPc, scale);
                    while (p > hiMel) p -= 12; while (p < loMel) p += 12;
                    ph[i].Pitch = p; ph[i].Deg = Mod12(p - tonicPc);
                }
            }
        }

        // Make the line sing: fill leaps of a third with a stepwise passing tone, and break up static
        // note-repeats with a neighbour. Applied to every character (a touch more for the lively one).
        void MakeMelodic(List<MelNote> line, int tonicPc, int[] scale, Random rng)
        {
            if (line.Count < 2) return;
            line.Sort((x, y) => x.Start.CompareTo(y.Start));
            double fill = character == "enjouée" ? 0.7 : (character == "calme" ? 0.4 : 0.55);

            // (1) passing tone: a 3rd between two notes (with room) is filled stepwise → conjunct motion
            var outl = new List<MelNote>();
            for (int i = 0; i < line.Count; i++)
            {
                var a = line[i];
                if (i + 1 < line.Count)
                {
                    int iv = line[i + 1].Pitch - a.Pitch, ai = Math.Abs(iv);
                    if ((ai == 3 || ai == 4) && a.Len >= 12 && rng.NextDouble() < fill)
                    {
                        int mid = ScaleStepDir(a.Pitch, Math.Sign(iv), tonicPc, scale);
                        if ((mid - a.Pitch) * (line[i + 1].Pitch - mid) > 0)        // strictly between
                        {
                            int half = Math.Max(6, a.Len / 2);
                            outl.Add(new MelNote { Pitch = a.Pitch, Deg = a.Deg, Start = a.Start, Len = half });
                            outl.Add(new MelNote { Pitch = mid, Deg = Mod12(mid - tonicPc), Start = a.Start + half, Len = a.Len - half });
                            continue;
                        }
                    }
                }
                outl.Add(a);
            }

            // (2) de-repeat: three same pitches in a row → turn the middle into an upper neighbour
            for (int i = 1; i + 1 < outl.Count; i++)
                if (outl[i - 1].Pitch == outl[i].Pitch && outl[i].Pitch == outl[i + 1].Pitch && rng.NextDouble() < fill)
                {
                    int nb = ScaleStepDir(outl[i].Pitch, 1, tonicPc, scale);
                    outl[i].Pitch = nb; outl[i].Deg = Mod12(nb - tonicPc);
                }

            line.Clear(); line.AddRange(outl);
        }

        // Character-driven articulation: calm = legato (fill small gaps). (Lively no longer forces staccato —
        // its rhythm/articulation now comes from the corpus rhythm-cell model + tempo, not an artificial bias.)
        void ArticulateForCharacter(List<MelNote> line)
        {
            if (character != "calme") return;
            line.Sort((x, y) => x.Start.CompareTo(y.Start));
            for (int i = 0; i + 1 < line.Count; i++)
            {
                var n = line[i];
                int gap = line[i + 1].Start - (n.Start + n.Len);
                if (gap > 0 && gap < Beat) n.Len = line[i + 1].Start - n.Start;          // legato slur over small gaps
            }
        }

        void Ornament(List<MelNote> line, int tonicPc, int[] scale, Random rng)
        {
            if (line.Count < 2) return;
            line.Sort((x, y) => x.Start.CompareTo(y.Start));

            // CHARACTER scales ornament density: calm & majestic stay sparse; lively/moderate use the base rate
            // (the LIVELINESS of "enjoué" now comes from the corpus rhythm-CELL model + faster tempo, not an
            // artificial swing/staccato bias — removed once the cell analysis was in).
            double orn = character == "calme" ? 0.4 : (character == "majestueux" ? 0.25 : 1.0);

            // (1) dotted-eighth + sixteenth swing on an on-beat eighth pair
            for (int i = 0; i + 1 < line.Count; i++)
            {
                var a = line[i]; var b = line[i + 1];
                if (a.Len == 12 && b.Len == 12 && a.Start % Beat == 0 && b.Start == a.Start + 12 && rng.NextDouble() < 0.30 * orn)
                { a.Len = 18; b.Start = a.Start + 18; b.Len = 6; }
            }

            // (2) triplets: split an on-beat quarter into 3 eighth-triplets, stepwise toward the next note
            var outl = new List<MelNote>();
            for (int i = 0; i < line.Count; i++)
            {
                var a = line[i];
                if (a.Len == 24 && a.Start % Beat == 0 && i + 1 < line.Count && rng.NextDouble() < 0.18 * orn)
                {
                    int dir = Math.Sign(line[i + 1].Pitch - a.Pitch); if (dir == 0) dir = 1;
                    int p1 = ScaleStepDir(a.Pitch, dir, tonicPc, scale);
                    int p2 = ScaleStepDir(p1, dir, tonicPc, scale);
                    outl.Add(new MelNote { Pitch = a.Pitch, Deg = Mod12(a.Pitch - tonicPc), Start = a.Start, Len = 8 });
                    outl.Add(new MelNote { Pitch = p1, Deg = Mod12(p1 - tonicPc), Start = a.Start + 8, Len = 8 });
                    outl.Add(new MelNote { Pitch = p2, Deg = Mod12(p2 - tonicPc), Start = a.Start + 16, Len = 8 });
                }
                else outl.Add(a);
            }

            // (3) anacrusis: a single pickup eighth (a step below) into each downbeat phrase preceded by a breath.
            var withPickup = new List<MelNote>();
            for (int i = 0; i < outl.Count; i++)
            {
                var a = outl[i];
                int prevEnd = i > 0 ? outl[i - 1].Start + outl[i - 1].Len : -999;
                bool phraseStart = (a.Start - prevEnd) >= Beat && a.Start % Bar == 0 && a.Start >= 12;
                if (phraseStart && rng.NextDouble() < Math.Min(0.92, 0.6 * orn))
                {
                    int pk = ScaleStepDir(a.Pitch, -1, tonicPc, scale);   // step below, leading up into the phrase
                    withPickup.Add(new MelNote { Pitch = pk, Deg = Mod12(pk - tonicPc), Start = a.Start - 12, Len = 12 });
                }
                withPickup.Add(a);
            }
            line.Clear(); line.AddRange(withPickup);
        }

        // ---- accompaniment: ONE coherent style per section (0=base figure, 1=chord-morph, 2=fake-HR, 3=open) ----
        int accStyle = 0;
        void PickAccStyle()
        {
            double d = rng.NextDouble();
            // mostly the MODEL-driven figure (so the accompaniment follows the corpus/source texture);
            // the hardcoded momentum tricks are now only occasional colour.
            accStyle = d < 0.80 ? 0 : (d < 0.88 ? 1 : (d < 0.95 ? 2 : 3)); // base 80% / morph 8% / fakeHR 7% / open 5%
        }
        // CHARACTER cascades to the accompaniment TEXTURE too: calm → sustained pads, lively → keep it moving.
        protected override string PickSectionMotif(CorpusModelV2 model, string section, Random rng)
        {
            string m = base.PickSectionMotif(model, section, rng);
            if (character == "calme" && rng.NextDouble() < 0.5) return "sustain";
            if (character == "enjouée" && (m == "sustain" || m == "block") && rng.NextDouble() < 0.6) return "broken";
            return m;
        }

        protected override void RenderAccomp(List<OutNote> outNotes, List<Cell> prog, int sectionStart, int tonicPc, string motif, int vel)
        {
            for (int bar = 0; bar < prog.Count; bar++)
                RenderBarInStyle(outNotes, prog[bar], sectionStart + bar * Bar, tonicPc, motif, vel, accStyle);
        }
        void RenderBarInStyle(List<OutNote> outNotes, Cell cell, int abs, int tonicPc, string motif, int vel, int style)
        {
            RenderLearnedAccompBar(outNotes, cell, abs, tonicPc, vel);
        }

        // LIVELY accompaniment: short, bouncy chord stabs on a syncopated "1 & 3 &" pattern (staccato).
        void LivelyComp(List<OutNote> outNotes, Cell cell, int abs, int tonicPc, int vel)
        {
            var v = VoicedTones(ChordPcs(cell), tonicPc, false);
            if (v.Count == 0) return;
            int[] hits = { 0, Beat + Beat / 2, 2 * Beat, 3 * Beat + Beat / 2 };   // beat1, &-of-2, beat3, &-of-4
            foreach (int h in hits)
                foreach (int p in v)
                    outNotes.Add(new OutNote { Part = 2, Pitch = p, Start = abs + h, Len = Beat / 3, Vel = Math.Max(1, vel + (h % Beat == 0 ? 5 : -3)) });
        }

        // MAJESTIC accompaniment: a strong chord on every beat (march), near-full length (tenuto), beats 1 & 3 accented.
        void GrandChords(List<OutNote> outNotes, Cell cell, int abs, int tonicPc, int vel)
        {
            var v = VoicedTones(ChordPcs(cell), tonicPc, false);
            if (v.Count == 0) return;
            for (int beat = 0; beat < 4; beat++)
                foreach (int p in v)
                    outNotes.Add(new OutNote { Part = 2, Pitch = p, Start = abs + beat * Beat, Len = Beat - 2, Vel = Math.Max(1, vel + (beat == 0 ? 8 : (beat == 2 ? 4 : 0))) });
        }

        // static simple triad (held, mid register) over a moving EXTENDED-chord arpeggio (low) — the ear
        // hears Dm7→Fadd6→Fmaj7→F while one chord sounds.
        void ChordMorph(List<OutNote> outNotes, Cell cell, int abs, int tonicPc, int vel)
        {
            var pcs = ChordPcs(cell);
            var tri = new List<int>();
            foreach (int pc in pcs) { int p = 60 + Mod12(pc + tonicPc - 60); while (p < 58) p += 12; while (p > 74) p -= 12; tri.Add(p); }
            tri = tri.Distinct().OrderBy(x => x).ToList();
            for (int beatI = 0; beatI < 4; beatI++)
                foreach (int p in tri)
                    outNotes.Add(new OutNote { Part = 2, Pitch = p, Start = abs + beatI * Beat, Len = Beat - 2, Vel = Math.Max(1, vel - 4) });
            // extended chord = chord tones + 7th + 9th, low arpeggio in eighths
            var ext = new List<int>(pcs) { Mod12(cell.Root + 10), Mod12(cell.Root + 2) };
            ext = ext.Distinct().ToList();
            int k = 0;
            for (int pos = 0; pos < Bar; pos += 12)
            {
                int pc = Mod12(ext[k % ext.Count] + tonicPc);
                int p = 40 + Mod12(pc - 40); while (p > 55) p -= 12; while (p < 40) p += 12;
                outNotes.Add(new OutNote { Part = 2, Pitch = p, Start = abs + pos, Len = 12, Vel = Math.Max(1, vel - 8) });
                k++;
            }
        }

        // fake harmonic rhythm: triad first half, then re-attack with an added colour tone (Am -> Am7)
        void FakeHarmonicRhythm(List<OutNote> outNotes, Cell cell, int abs, int tonicPc, int vel)
        {
            var tri = VoicedTones(ChordPcs(cell), tonicPc, false);
            RollChord(outNotes, tri, abs, abs + Bar / 2, vel);
            var col = VoicedTones(new[] { cell.Root, Mod12(cell.Root + 3), Mod12(cell.Root + 7), Mod12(cell.Root + 10) }, tonicPc, false); // + b7 colour
            RollChord(outNotes, col, abs + Bar / 2, abs + Bar, vel);
        }

        // open voicing: omit the 3rd, stack root + 5th + 9th (P4/P5 colour), rolled across the bar
        void OpenVoicing(List<OutNote> outNotes, Cell cell, int abs, int tonicPc, int vel)
        {
            var open = VoicedTones(new[] { cell.Root, Mod12(cell.Root + 7), Mod12(cell.Root + 2) }, tonicPc, false); // root,5th,9th
            RollChord(outNotes, open, abs, abs + Bar, vel);
        }

        static List<int> VoicedTones(IEnumerable<int> degs, int tonicPc, bool wide)
        {
            var v = new List<int>();
            foreach (int pc in degs.Distinct()) { int p = 48 + Mod12(pc + tonicPc - 48); while (p < 50) p += 12; while (p > 67) p -= 12; v.Add(p); }
            return v.Distinct().OrderBy(x => x).ToList();
        }

        // ---- the rich Ghibli form ----
        protected override void Arrange()
        {
            int devTonic = Mod12(tonicPc + 2);   // development modulates up a whole step, returns for the reprise

            var introProg = SampleProg(model, mm, rng, 2, true, false, "intro");
            var themeProg = SampleProg(model, mm, rng, 8, true, false, "theme");
            var devProg = SampleProg(model, mm, rng, 16, true, false, "climax");
            var reprProg = Reharmonize(themeProg);
            reprProg[reprProg.Count - 1] = new Cell { Root = 0, Canon = GroupToCanon(minor ? "madd9" : "add9") };
            var outroProg = new List<Cell> {
                new Cell{ Root=5, Canon=GroupToCanon(minor ? "min7" : "maj7") },   // iv / IV
                new Cell{ Root=0, Canon=GroupToCanon(minor ? "madd9" : "add9") },  // i / I
                new Cell{ Root=0, Canon=GroupToCanon(minor ? "min" : "maj") },
                new Cell{ Root=0, Canon=GroupToCanon("maj") },                    // major tonic to close (Picardy in minor)
            };

            string figIntro = PickSectionMotif(model, "intro", rng);
            string figBody = PickSectionMotif(model, "body", rng);
            string figClimax = PickSectionMotif(model, "climax", rng);
            string figOutro = PickSectionMotif(model, "outro", rng);

            var themeA = GenerateLine(model, mm, rng, themeProg, tonicPc, scale, loMel, hiMel, true, 0, "theme");
            var themeB = GenerateLine(model, mm, rng, devProg.GetRange(8, 4), devTonic, scale, loMel, hiMel, false, 4, "climax"); // 2nd motif (climax variation), modulated

            int t = 0;

            // INTRO 2
            AccompSectionRole = "intro";
            PickAccStyle();
            RenderAccomp(notes, introProg, t, tonicPc, figIntro, 44);
            RenderBass(notes, introProg, t, tonicPc, 46);
            t += introProg.Count * Bar;

            // THEME 8 — flute
            AccompSectionRole = "theme";
            EmitMelody(notes, themeA, t, 0, 78, 0);
            PickAccStyle();
            RenderAccomp(notes, themeProg, t, tonicPc, figBody, 50);
            RenderBass(notes, themeProg, t, tonicPc, 56);
            t += themeProg.Count * Bar;

            // THEME REPEATED 8 — oboe
            AccompSectionRole = "theme";
            EmitMelody(notes, themeA, t, 0, 80, 1);
            PickAccStyle();
            RenderAccomp(notes, themeProg, t, tonicPc, figBody, 50);
            RenderBass(notes, themeProg, t, tonicPc, 56);
            t += themeProg.Count * Bar;

            // DEVELOPMENT 16 — FOUR variations of 4 bars, modulated up a step. ONE accompaniment STYLE per
            // variation; each variation is a distinct treatment of the theme (var 2 = the 2nd motif at the
            // climax). Buildup -> climax (var 2) -> attenuate.
            var varMat = themeA.Where(n => n.Start < 4 * Bar).ToList();   // theme's 4-bar antecedent
            int[] varShift = { 0, 2, 0, 1 };                              // diatonic-step transform per variation
            int[] varVel = { 74, 86, 96, 78 };                            // velocity arc (peak at var 2)
            const int varBars = 4;
            AccompSectionRole = "climax";
            for (int v = 0; v < 4; v++)
            {
                int vStart = t + v * varBars * Bar;
                PickAccStyle();                                            // ONE style for the whole variation
                if (v == 2)
                {
                    EmitMelody(notes, themeB, vStart, 0, varVel[v], 0);    // climax: the 2nd motif
                    foreach (var n in themeB)
                        if (n.Len >= Beat) { int gp = n.Pitch + 12; while (gp > 96) gp -= 12; notes.Add(new OutNote { Part = 4, Pitch = gp, Start = vStart + n.Start, Len = n.Len, Vel = 68 }); }
                }
                else
                {
                    foreach (var n in varMat)
                    {
                        int p = TransposeInScale(n.Pitch + (devTonic - tonicPc), varShift[v], devTonic, scale);
                        while (p > hiMel) p -= 12; while (p < loMel) p += 12;
                        notes.Add(new OutNote { Part = 0, Pitch = p, Start = vStart + n.Start, Len = n.Len, Vel = varVel[v] });
                    }
                }
                for (int bb = 0; bb < varBars; bb++)
                {
                    int bar = v * varBars + bb;
                    RenderBarInStyle(notes, devProg[bar], vStart + bb * Bar, devTonic, figClimax, Math.Max(1, varVel[v] - 30), accStyle);
                    RenderBass(notes, new List<Cell> { devProg[bar] }, vStart + bb * Bar, devTonic, Math.Max(1, varVel[v] - 22));
                }
            }
            // counter (oboe) across the whole development, contrary, modulated
            var devCounter = GenerateLine(model, mm, rng, devProg, devTonic, scale, loCnt, hiCnt, false, 3, "climax", true);
            foreach (var n in devCounter)
            {
                int vv = Math.Min(3, (n.Start / Bar) / varBars);
                notes.Add(new OutNote { Part = 1, Pitch = n.Pitch, Start = t + n.Start, Len = n.Len, Vel = Math.Max(1, varVel[vv] - 28) });
            }
            t += devProg.Count * Bar;

            // REPRISE 8 — back home, theme A REHARMONIZED (same notes, new chords)
            AccompSectionRole = "body";
            EmitMelody(notes, themeA, t, 0, 74, true, tonicPc, loMel, hiMel, 0, 2); // last theme exposition: hold the final note ~2 bars
            var reprCounter = GenerateLine(model, mm, rng, reprProg, tonicPc, scale, loCnt, hiCnt, false, 3, "body", true);
            foreach (var n in reprCounter)
                notes.Add(new OutNote { Part = 1, Pitch = n.Pitch, Start = t + n.Start, Len = n.Len, Vel = 46 });
            PickAccStyle();
            RenderAccomp(notes, reprProg, t, tonicPc, figBody, 48);
            RenderBass(notes, reprProg, t, tonicPc, 54);
            t += reprProg.Count * Bar;

            // OUTRO 4 — soft plagal close with a Picardy major tonic
            AccompSectionRole = "outro";
            PickAccStyle();
            RenderAccomp(notes, outroProg, t, tonicPc, figOutro, 40);
            RenderBass(notes, outroProg, t, tonicPc, 42);
            t += outroProg.Count * Bar;

            // light percussion (corpus-driven), denser at the climax. Section starts: 0,2,10,18,34,42 bars.
            if (drumsEnabled)
            {
                RenderPercussion(0, 2, "intro", 38);
                RenderPercussion(2 * Bar, 8, "body", 46);
                RenderPercussion(10 * Bar, 8, "body", 48);
                RenderPercussion(18 * Bar, 16, "climax", 56);
                RenderPercussion(34 * Bar, 8, "body", 44);
                RenderPercussion(42 * Bar, 4, "outro", 38);
            }

            // FINAL long chord — a held Picardy major tonic ringing ~2 bars
            var fin = new Cell { Root = 0, Canon = GroupToCanon("maj") };
            var voiced = new List<int>();
            foreach (int pc in ChordPcs(fin)) { int p = 50 + Mod12(pc + tonicPc - 50); while (p < 52) p += 12; while (p > 76) p -= 12; voiced.Add(p); }
            voiced = voiced.Distinct().OrderBy(x => x).ToList();
            RollChord(notes, voiced, t, t + 2 * Bar, 42);
            int bpc = Mod12(tonicPc); int bp = 36 + Mod12(bpc - 36); while (bp > 50) bp -= 12; while (bp < 36) bp += 12;
            notes.Add(new OutNote { Part = 3, Pitch = bp, Start = t, Len = 2 * Bar, Vel = 42 });
            t += 2 * Bar;

            cursor = t;
        }

        // reharmonization: recolour the theme's chords (mediant shift / enrichment) so the SAME melody
        // notes become colour tones under the reprise.
        List<Cell> Reharmonize(List<Cell> src)
        {
            var outp = new List<Cell>();
            foreach (var cell in src)
            {
                if (rng.NextDouble() < 0.5)
                    outp.Add(new Cell { Root = Mod12(cell.Root + 9), Canon = GroupToCanon(rng.NextDouble() < 0.5 ? "maj7" : "min7") }); // down a 3rd, lush
                else
                    outp.Add(new Cell { Root = cell.Root, Canon = GroupToCanon("add9") });                                              // enrich in place
            }
            return outp;
        }
    }
}
