using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NAudio.Midi;
using MusicTracker.Engine.Flow;
using MusicTracker.Engine.Score;
using MusicTracker.Engine.Timeline;
using MusicTracker.Engine.ComposerV2;   // kept data/analysis layer: CorpusModelV2, ModeModels, CondModel, MusicMathV2, ComposerV2Runtime

namespace MusicTracker.Engine.ComposerV3
{
    /// <summary>
    /// Composer V2 — instance BASE class: the reusable toolkit (Witten-Bell back-off sampler, music
    /// helpers, MIDI + MuseScore export) plus a template-method <see cref="Compose"/> that calls
    /// overridable <c>virtual</c> policy hooks (Configure / Arrange / SampleProg / GenerateLine /
    /// RenderAccomp / RenderBass / PickSectionMotif / DurFromBucket). The base's own hooks reproduce the
    /// plain V2 behaviour (intro · theme · theme-repeated · development · reprise · outro, 4 parts).
    /// Derive (e.g. <see cref="GhibliComposer"/>) and override the hooks to specialise the style.
    /// MIDI written with the same NAudio pattern as <see cref="Timeline.MidiTimelineExporter"/>.
    /// </summary>
    public class BaseComposerV3
    {
        protected class MelNote { public int Deg; public int Pitch; public int Start; public int Len; public int VelOffset; }
        protected class OutNote { public int Part; public int Pitch; public int Start; public int Len; public int Vel; }
        protected class Cell { public int Root; public int Canon; public int BassDeg = -1; public int EffBass { get { return BassDeg < 0 ? Root : BassDeg; } } }

        protected const int Spq = 24, Bar = 96, Beat = 24;

        // ---- session state (set by the Compose driver / Configure) ----
        protected CorpusModelV2 model;
        protected Random rng;
        protected ModeModels mm;
        protected bool minor = true;
        protected int tonicPc = 9;          // A
        protected int tonicLetter = 5;      // La
        protected int tonicAccidental = 0;  // -1 flat / 0 / +1 sharp (for the .mscx key signature)
        protected int[] scale;
        protected double bpm = 76;
        protected int meterNum = 4, meterDen = 4;
        protected int loMel = 64, hiMel = 86, loCnt = 55, hiCnt = 74;
        protected int[] programs;
        protected string[] partNames;
        protected double[] partVolumes;
        protected ScoreClefKind[] partClefs;
        protected bool[] partIsDrum;        // a part flagged drum -> MIDI channel 10, not notated in .mscx
        protected string title = "Composer V2";
        protected List<OutNote> notes;
        protected int cursor;

        // ---- universal "guitar fingerstyle accompaniment" option ----
        /// <summary>When set, the chordal accompaniment is realized as fingerstyle guitar (see <see cref="RenderFingerstyleBar"/>).</summary>
        public bool GuitarFingerstyle { get; set; }
        /// <summary>When set, the accompaniment is generated from the corpus AccompCell + AccompTone models,
        /// re-voiced onto the current chord (see <see cref="RenderLearnedAccompBar"/>).</summary>
        public bool LearnedAccomp { get; set; }
        /// <summary>Melody rhythm source. DEFAULT (both flags false) = the PHRASE-COHERENT bar rhythm
        /// (<see cref="GeneratePhraseRhythm"/>): sample one bar of note-bearing corpus cells and reuse it across the
        /// phrase with light variation, no mid-phrase rests — fixes the "brouillon/saccadé/trop de silence" of the
        /// per-beat draw. <see cref="UseMotifRhythm"/>=true → the rigid reused 2-cell MOTIF
        /// (<see cref="GeneratePhraseMotifRhythm"/>). <see cref="UsePerBeatRhythm"/>=true → the old per-beat bounded
        /// chain (<see cref="GenerateMelodyRhythm"/>). Compare via the style-distance metric (<see cref="StyleDistanceV3"/>).</summary>
        public bool UseMotifRhythm { get; set; } = false;
        /// <summary>Revert the melody rhythm to the old per-beat bounded chain (<see cref="GenerateMelodyRhythm"/>) for
        /// A/B comparison. Ignored when <see cref="UseMotifRhythm"/> is true.</summary>
        public bool UsePerBeatRhythm { get; set; } = false;

        /// <summary>Melody PITCH strategy (Nierhaus ch.3). TRUE (default) = decode the whole melody by a GLOBAL VITERBI
        /// pass over the chord grid (<see cref="MelodyResolverV2"/>) — the most-likely degree sequence given the entire
        /// progression, so the line is globally coherent instead of the greedy note-by-note pick that sounds "random".
        /// FALSE = keep the old greedy per-note sampling. Rhythm is generated the same way either way.</summary>
        public bool UseViterbiMelody { get; set; } = true;
        /// <summary>Section role ("intro"/"theme"/"body"/"climax"/"outro") of the accompaniment currently being
        /// rendered — feeds the section dimension of the learned accompaniment RHYTHM. Set per section in Arrange.</summary>
        protected string AccompSectionRole = "body";
        /// <summary>Which part index carries the chordal accompaniment (so fingerstyle can take it over). -1 = none.</summary>
        protected virtual int AccompPartIndex => 2;

        // ---- melody CHARACTER root context (enjouée / modérée / calme / majestueux) — shared by EVERY style ----
        protected string character = "modérée";
        /// <summary>Force a melody character instead of sampling the corpus. ASCII tokens accepted
        /// ("enjouee"/"vif", "moderee", "calme", "majestueux"/"noble"). Null = sample CharacterDistribution.</summary>
        public string CharacterOverride { get; set; }
        /// <summary>The character chosen at Configure (valid after Compose) — for callers/tests/UI.</summary>
        public string ChosenCharacter => character;

        protected static string NormalizeCharacter(string s)
        {
            if (string.IsNullOrEmpty(s)) return "modérée";
            s = s.Trim().ToLowerInvariant();
            if (s.IndexOf('_') > 0) return s;   // ground-truth folder category (e.g. "calme_nostalgique") passes through verbatim
            if (s.StartsWith("enjou") || s == "vif" || s == "lively") return "enjouée";
            if (s.StartsWith("majest") || s.StartsWith("noble") || s == "marche" || s == "march") return "majestueux";
            if (s.StartsWith("calm")) return "calme";
            return "modérée";
        }

        /// <summary>Pick the root-context character: the override if set, else sample the corpus distribution.
        /// Call near the top of Configure so it can bias tempo / sub-style / density.</summary>
        protected void PickCharacter()
        {
            character = !string.IsNullOrEmpty(CharacterOverride)
                      ? NormalizeCharacter(CharacterOverride)
                      : (Pick(model.CharacterDistribution, rng) ?? "modérée");
        }

        // ============ public driver (template method) ============
        public void Compose(CorpusModelV2 m, int seed, string midiPath)
        {
            RunCompose(m, seed);
            WriteMidi(notes, bpm, meterNum, meterDen, programs, partNames, midiPath, partVolumes, partIsDrum);
            ExportMuseScore(notes, meterNum, meterDen, title, Path.ChangeExtension(midiPath, ".mscx"),
                            tonicLetter, tonicAccidental, minor ? 1 : 0, cursor, partClefs, programs, partNames, partIsDrum);
        }

        void RunCompose(CorpusModelV2 m, int seed)
        {
            model = m; rng = new Random(seed); notes = new List<OutNote>(); cursor = 0;
            Configure();
            if (scale == null) scale = minor ? MusicMathV2.MinorScale : MusicMathV2.MajorScale; // Configure may set a modal scale
            // fingerstyle: hand the chordal-accompaniment part to a nylon-string guitar
            if (GuitarFingerstyle && programs != null && AccompPartIndex >= 0 && AccompPartIndex < programs.Length)
            {
                programs[AccompPartIndex] = 24;     // GM nylon acoustic guitar
                if (partNames != null && AccompPartIndex < partNames.Length) partNames[AccompPartIndex] = "Guitare (fingerstyle)";
            }
            mm = minor ? model.Minor : model.Major;
            Arrange();
            SnapMelodyToHarmony();
        }

        // Coordination pass: when the accompaniment is the EXPLICIT learned arpeggio (LearnedAccomp), it spells the
        // chord out note-by-note, so any melody STRONG-beat note that isn't in the sounding chord clashes audibly —
        // especially where the form reuses the theme over DIFFERENT chords (reharmonized reprise, transposed
        // development). Snap each strong-beat melody/counter note to the nearest pitch whose pitch-class IS actually
        // sounding in its bar's accompaniment (+ bass). Weak/passing notes stay free, so the line keeps its motion.
        protected virtual void SnapMelodyToHarmony()
        {
            if (!LearnedAccomp || notes == null || notes.Count == 0) return;
            int accPart = AccompPartIndex, bassPart = accPart + 1;
            var barPcs = new Dictionary<int, HashSet<int>>();
            foreach (var n in notes)
            {
                if (n.Part != accPart && n.Part != bassPart) continue;
                int bar = n.Start / Bar; HashSet<int> set;
                if (!barPcs.TryGetValue(bar, out set)) { set = new HashSet<int>(); barPcs[bar] = set; }
                set.Add(Mod12(n.Pitch));
            }
            foreach (var n in notes)
            {
                if (n.Part != 0 && n.Part != 1) continue;          // melody + counter voices
                if (n.Start % Beat != 0) continue;                 // every BEAT onset (off-beat notes stay free as passing tones)
                HashSet<int> set;
                if (!barPcs.TryGetValue(n.Start / Bar, out set) || set.Count == 0) continue;
                if (set.Contains(Mod12(n.Pitch))) continue;        // already consonant
                int best = n.Pitch, bestd = 99;
                for (int d = -6; d <= 6; d++) { int p = n.Pitch + d; if (set.Contains(Mod12(p)) && Math.Abs(d) < bestd) { bestd = Math.Abs(d); best = p; } }
                n.Pitch = best;
            }
        }

        /// <summary>Compose IN MEMORY (no files written) and hand the result back as a neutral piece the app
        /// can load into the timeline (one part per voice, with its GM program + notes + the chosen key/meter/tempo).</summary>
        public V2Piece ComposeInMemory(CorpusModelV2 m, int seed)
        {
            RunCompose(m, seed);
            var piece = new V2Piece
            {
                Bpm = bpm,
                MeterNum = meterNum,
                MeterDen = meterDen,
                TonicLetter = tonicLetter,
                TonicAccidental = tonicAccidental,
                Minor = minor,
                TotalSlices = cursor,
                Title = title
            };
            for (int p = 0; p < programs.Length; p++)
            {
                var part = new V2Part
                {
                    Program = programs[p],
                    Name = (partNames != null && p < partNames.Length) ? partNames[p] : ("Partie " + (p + 1)),
                    IsDrum = partIsDrum != null && p < partIsDrum.Length && partIsDrum[p]
                };
                foreach (var n in notes)
                    if (n.Part == p) part.Notes.Add(new V2Note { Pitch = n.Pitch, Start = n.Start, Len = n.Len, Vel = n.Vel });
                part.Notes.Sort((a, b) => a.Start.CompareTo(b.Start));
                piece.Parts.Add(part);
            }
            return piece;
        }

        // ============ partial reuse: theme melody + chord grid for another composer ============
        /// <summary>A learned THEME melody and its CHORD GRID, in the app's timeline units, so another composer
        /// (e.g. the Timeline "posed-form" Ghibli) can borrow V2's learned material and apply its OWN repetition /
        /// variation / reprise form. Major or minor only (the corpus model is mode-split).</summary>
        public sealed class V2ThemeChords
        {
            /// <summary>One chord per bar: absolute root pitch-class + PatternGenerator quality index (== Cell.Canon).</summary>
            public List<(int rootPc, int quality)> Chords = new List<(int, int)>();
            /// <summary>Theme line relative to bar 0, 24 slices/quarter; Note = MIDI-12 (the V2→timeline convention).</summary>
            public List<RiffNote> Theme = new List<RiffNote>();
        }

        /// <summary>Generate ONLY a theme melody + its 1-chord-per-bar grid from the corpus model, using THIS style's
        /// learned harmony (<see cref="SampleProg"/>) and melody (<see cref="GenerateLine"/>) hooks, in the given key.
        /// Does not run the full <see cref="Arrange"/> form. <paramref name="bars"/> = theme length in bars.</summary>
        public V2ThemeChords MakeThemeAndChords(CorpusModelV2 m, int seed, int bars, bool wantMinor, int wantTonicPc, string characterOverride = null, string section = "theme")
        {
            model = m;
            rng = new Random(seed);
            notes = new List<OutNote>();
            CharacterOverride = characterOverride;
            Configure();                                               // SAME setup as the rich V2 path: style character +
                                                                       // registers + melody polish context (then override tonality)
            minor = wantMinor;
            tonicPc = Mod12(wantTonicPc);
            scale = minor ? MusicMathV2.MinorScale : MusicMathV2.MajorScale;
            meterNum = 4; meterDen = 4;
            mm = minor ? model.Minor : model.Major;

            int n = Math.Max(1, bars);
            var prog = SampleProg(model, mm, rng, n, true, false, section);   // virtual → the style's SECTION-conditioned harmony
            var line = GenerateLine(model, mm, rng, prog, tonicPc, scale, loMel, hiMel, true, 0, section);

            var res = new V2ThemeChords();
            foreach (var c in prog) res.Chords.Add((Mod12(tonicPc + c.Root), c.Canon));   // degree→absolute pc; Canon == V1 quality
            foreach (var mn in line)
            {
                int p = Math.Max(0, Math.Min(95, mn.Pitch - 12));      // same octave drop as the V2→timeline bridge
                res.Theme.Add(new RiffNote(p, mn.Start, Math.Max(1, mn.Len)));
            }
            res.Theme.Sort((a, b) => a.Start.CompareTo(b.Start));
            return res;
        }

        /// <summary>This style's ACCOMPANIMENT + BASS (rendered by its OWN <see cref="RenderAccomp"/>/<see cref="RenderBass"/>
        /// hooks → Vivaldi's motor, Bach's figuration, Ghibli's arpeggio, etc.) plus its instrument palette + tempo, so a
        /// host (the posed-form hybrid) sounds like the chosen STYLE rather than always the same. <paramref name="segments"/>
        /// = the full piece's chords in order, each tagged with a section role (intro/theme/body/climax/outro) for the
        /// section-conditioned texture. Notes are in timeline units (Note = MIDI-12), positioned from bar 0.</summary>
        public sealed class V2Backing
        {
            public List<RiffNote> Accomp = new List<RiffNote>();
            public List<RiffNote> Bass = new List<RiffNote>();
            public int LeadProgram = 73, CounterProgram = 48, AccompProgram = 0, BassProgram = 48;
            public double Bpm = 76;
        }

        public V2Backing MakeBacking(CorpusModelV2 m, int seed, bool wantMinor, int wantTonicPc, string characterOverride,
                                     List<KeyValuePair<string, List<(int rootPc, int quality)>>> segments)
        {
            model = m;
            rng = new Random(seed);
            notes = new List<OutNote>();
            CharacterOverride = characterOverride;
            Configure();                                        // STYLE: instruments + tempo (+ a modal scale/tonic, overridden next)
            minor = wantMinor;
            tonicPc = Mod12(wantTonicPc);
            scale = minor ? MusicMathV2.MinorScale : MusicMathV2.MajorScale;
            meterNum = 4; meterDen = 4;                         // the posed hybrid is 4/4 regardless of the style's own meter
            mm = minor ? model.Minor : model.Major;

            int bar = 0;
            foreach (var seg in segments)
            {
                var cells = new List<Cell>();
                foreach (var c in seg.Value) cells.Add(new Cell { Root = Mod12(c.rootPc - tonicPc), Canon = c.quality });
                if (cells.Count == 0) continue;
                string fig = PickSectionMotif(model, seg.Key, rng);
                RenderAccomp(notes, cells, bar * Bar, tonicPc, fig, 54);   // virtual → the STYLE's accompaniment renderer
                RenderBass(notes, cells, bar * Bar, tonicPc, 50);
                bar += cells.Count;
            }

            var res = new V2Backing { Bpm = bpm };
            if (programs != null)
            {
                if (programs.Length > 0) res.LeadProgram = programs[0];
                if (programs.Length > 1) res.CounterProgram = programs[1];
                if (programs.Length > 2) res.AccompProgram = programs[2];
                if (programs.Length > 3) res.BassProgram = programs[3];
            }
            int accPart = AccompPartIndex;
            foreach (var n in notes)
            {
                int p = Math.Max(0, Math.Min(95, n.Pitch - 12));
                if (n.Part == accPart) res.Accomp.Add(new RiffNote(p, n.Start, Math.Max(1, n.Len)));
                else if (n.Part == 3) res.Bass.Add(new RiffNote(p, n.Start, Math.Max(1, n.Len)));
            }
            res.Accomp.Sort((a, b) => a.Start.CompareTo(b.Start));
            res.Bass.Sort((a, b) => a.Start.CompareTo(b.Start));
            return res;
        }

        /// <summary>Style flag: true → this style FIGURES its own melody per section (e.g. Vivaldi's running 16ths), so the
        /// posed form asks it for a melody over each section's chords; false → it provides a THEME the form restates/varies.</summary>
        public virtual bool GeneratesOwnMelody => false;

        /// <summary>A variation technique the style PREFERS (a catalogue index in Timeline.GhibliComposer; 0 = let the engine choose).</summary>
        public virtual int ForcedVariationTech() => 0;

        /// <summary>Composer FAMILY id used to look up a curated seed theme in the theme library (Data\themes). A library
        /// entry is keyed by (FamilyKey, Style); "generic" = the base engine, no curated theme unless one is authored for it.</summary>
        public virtual string FamilyKey => "generic";

        /// <summary>Generate this STYLE's melody over a GIVEN chord progression (the posed form's section chords) — so every
        /// melodic section gets the style's character on the form's harmony. Uses the style's <see cref="SectionMelody"/> hook.</summary>
        public List<RiffNote> MakeSectionMelody(CorpusModelV2 m, int seed, List<(int rootPc, int quality)> chords, bool wantMinor, int wantTonicPc, string characterOverride, string section)
        {
            model = m; rng = new Random(seed); notes = new List<OutNote>();
            CharacterOverride = characterOverride;
            Configure();
            minor = wantMinor; tonicPc = Mod12(wantTonicPc);
            scale = minor ? MusicMathV2.MinorScale : MusicMathV2.MajorScale;
            meterNum = 4; meterDen = 4; mm = minor ? model.Minor : model.Major;
            var cells = new List<Cell>();
            foreach (var c in chords) cells.Add(new Cell { Root = Mod12(c.rootPc - tonicPc), Canon = c.quality });
            var outl = new List<RiffNote>();
            if (cells.Count == 0) return outl;
            foreach (var mn in SectionMelody(cells, loMel, hiMel, section ?? "body"))
                outl.Add(new RiffNote(Math.Max(0, Math.Min(95, mn.Pitch - 12)), mn.Start, Math.Max(1, mn.Len)));
            outl.Sort((a, b) => a.Start.CompareTo(b.Start));
            return outl;
        }

        /// <summary>The melody for a section's chords — DEFAULT = the style's learned melodic line (<see cref="GenerateLine"/>).
        /// Override to FIGURE the chords in the style's idiom (e.g. Vivaldi sixteenth arpeggio waves).</summary>
        protected virtual List<MelNote> SectionMelody(List<Cell> prog, int lo, int hi, string section)
            => GenerateLine(model, mm, rng, prog, tonicPc, scale, lo, hi, true, 0, section);

        // ============ ComposerV3 EMITTER API (used by the Orchestrateur) ============
        // The composer is a pure emitter: it produces a chord progression, a voice line, or an accompaniment
        // motif for ONE section, from the learned model — adapted by STYLE + MOOD + section. The Orchestrateur
        // owns the FORM (which sections, which keys, restatements) and calls these to fill each section.

        /// <summary>The textures/movements this style exposes (e.g. Bach: prélude/fugue/allemande). Base = generic.</summary>
        // The base (= the "generic" family) exposes the cross-style GÉNÉRIQUE personalities. Derived composers override.
        public virtual IReadOnlyList<string> Styles => new[] { "Classique", "Romantique", "Ballade", "Contemporain", "Jazz" };

        /// <summary>Set up the session (model/key/scale/meter + the style's Configure for character/ranges) from the context.</summary>
        protected void PrepareSession(EmitContext ctx)
        {
            model = ctx.Model;
            rng = new Random(ctx.Seed);
            notes = new List<OutNote>();
            CharacterOverride = !string.IsNullOrEmpty(ctx.CharacterTag) ? ctx.CharacterTag : MoodToken(ctx.Mood);
            CurrentStyle = ctx.Style;
            Configure();                                   // style: character + registers + instruments (tonic/scale overridden next)
            minor = ctx.Minor;
            tonicPc = Mod12(ctx.TonicPc);
            scale = ctx.Scale ?? (minor ? MusicMathV2.MinorScale : MusicMathV2.MajorScale);
            meterNum = ctx.MeterNum > 0 ? ctx.MeterNum : 4;
            meterDen = ctx.MeterDen > 0 ? ctx.MeterDen : 4;
            mm = minor ? model.Minor : model.Major;
        }

        /// <summary>The chosen style for this emitter session (a value from <see cref="Styles"/>), or null = default.</summary>
        protected string CurrentStyle;

        static string MoodToken(Mood m)
        {
            switch (m)
            {
                case Mood.Calme: return "calme";
                case Mood.Enjoue: return "enjouee";
                case Mood.Majestueux: return "majestueux";
                case Mood.Modere: return "moderee";
                default: return null;          // Auto → sample the corpus character
            }
        }

        /// <summary>Emit a chord progression (figured: absolute root pitch-class + PatternGenerator quality index) for a
        /// section, from the learned harmony — section-conditioned. One chord per bar (held chords repeat).</summary>
        public virtual List<(int rootPc, int quality)> GetChords(EmitContext ctx, SectionContext sec, int measureCount, int chordsPerMeasure)
        {
            PrepareSession(ctx);
            int bars = Math.Max(1, measureCount);
            var cells = SampleProg(model, mm, rng, bars, true, sec.Cad == 1, sec.Role ?? "body");
            var outl = new List<(int, int)>();
            foreach (var c in cells) outl.Add((Mod12(tonicPc + c.Root), c.Canon));
            return outl;
        }

        /// <summary>Emit the notes (timeline units, Note = MIDI-12) for a STAFF over the given chords of a section.
        /// genOwn styles figure the chords; otherwise the learned melodic line; restatements re-use themeNotes.
        /// (Variation catalogue is applied by the Orchestrateur in a later phase.)</summary>
        public virtual List<RiffNote> GetNote(EmitContext ctx, SectionContext sec, Staff staff, List<(int rootPc, int quality)> chords, int measureCount, List<RiffNote> themeNotes)
        {
            PrepareSession(ctx);
            var outl = new List<RiffNote>();
            var cells = new List<Cell>();
            foreach (var c in chords) cells.Add(new Cell { Root = Mod12(c.rootPc - tonicPc), Canon = c.quality });
            if (cells.Count == 0) return outl;

            bool counter = staff == Staff.Counter;
            int lo = counter ? loCnt : loMel;
            int hi = counter ? hiCnt : hiMel;

            List<MelNote> line;
            if (GeneratesOwnMelody)
                line = SectionMelody(cells, lo, hi, sec.Role ?? "body");
            else
                line = GenerateLine(model, mm, rng, cells, tonicPc, scale, lo, hi, sec.Cad != 0, counter ? 4 : 0, sec.Role ?? "body", counter);

            // Item 1 (Nierhaus ch.3): re-pitch the MELODY by a GLOBAL Viterbi decode over the chord grid instead of the
            // greedy per-note sampling. Greedy is locally consonant but globally aimless (= "random"); Viterbi picks the
            // most-likely degree sequence given the WHOLE progression (it uses the following notes too), so the line
            // "goes somewhere". Rhythm is untouched. Melody voice only; the counter keeps its own logic.
            if (UseViterbiMelody && !GeneratesOwnMelody && !counter && line.Count > 1 && model != null)
                ViterbiRepitch(line, cells, lo, hi, sec.Role ?? "body");

            foreach (var mn in line)
                outl.Add(new RiffNote(Math.Max(0, Math.Min(95, mn.Pitch - 12)), mn.Start, Math.Max(1, mn.Len)));
            outl.Sort((a, b) => a.Start.CompareTo(b.Start));
            return outl;
        }

        // Item 1 helper (Nierhaus ch.3): replace the pitches of a generated melody with a GLOBAL Viterbi decode over the
        // chord grid, keeping the rhythm. Reuses MelodyResolverV2 (the SAME learned melody ladder as the greedy path), so
        // no new training is needed. Every note onset is a "hole" slot (bar + strong-beat) → the whole degree sequence is
        // decoded jointly (each note sees its neighbours on both sides). Degrees are placed in the melody register near
        // the previous pitch (nearest octave), with a soft leap cap.
        protected void ViterbiRepitch(List<MelNote> line, List<Cell> prog, int lo, int hi, string section)
        {
            if (line == null || line.Count < 2 || prog == null || prog.Count == 0) return;
            int nb = prog.Count;
            var chords = new MelodyResolverV2.Chord[nb];
            for (int b = 0; b < nb; b++)
            {
                var tones = new HashSet<int>();
                foreach (int pc in ChordPcs(prog[b])) tones.Add(Mod12(pc));
                chords[b] = new MelodyResolverV2.Chord { Root = Mod12(prog[b].Root), Tones = tones };
            }
            var slots = new List<MelodyResolverV2.Slot>(line.Count);
            foreach (var mn in line)
                slots.Add(new MelodyResolverV2.Slot { Bar = Math.Min(nb - 1, mn.Start / Bar), Strong = (mn.Start % (2 * Beat)) == 0, Deg = mn.Deg, Known = false });
            var opts = new MelodyResolverV2.Options { Section = section ?? "body", EmissionWeight = 0.5, UseChordDegreeTiers = true };
            var res = MelodyResolverV2.Resolve(model, minor, chords, slots, opts);
            if (res == null || res.Degrees == null || res.Degrees.Length != line.Count) return;
            int cur = line[0].Pitch;
            for (int i = 0; i < line.Count; i++)
            {
                int pitch = PlaceNear(cur, Mod12(tonicPc + res.Degrees[i]));
                while (pitch > hi) pitch -= 12;
                while (pitch < lo) pitch += 12;
                if (i > 0 && Math.Abs(pitch - cur) > 9) pitch = cur + Math.Sign(pitch - cur) * 9;   // cap wild leaps
                line[i].Pitch = pitch;
                line[i].Deg = Mod12(pitch - tonicPc);
                cur = pitch;
            }
        }

        /// <summary>Emit the editable ACCOMPANIMENT motif (degree pattern) for a section, per style + mood. Base = a gentle
        /// broken chord; styles override. The Orchestrateur realizes it onto each chord via the ArrangementEngine.</summary>
        public virtual ChordMotif GetChordMotif(EmitContext ctx, SectionContext sec)
        {
            var m = new ChordMotif { Bars = 1, SlicesPerQuarter = Spq };
            m.Notes.Add(new MotifNote { Degree = 1, Start = 0, Length = Beat });
            m.Notes.Add(new MotifNote { Degree = 3, Start = Beat, Length = Beat });
            m.Notes.Add(new MotifNote { Degree = 5, Start = 2 * Beat, Length = Beat });
            m.Notes.Add(new MotifNote { Degree = 3, Start = 3 * Beat, Length = Beat });
            return m;
        }

        // ============ policy hooks (override to specialise) ============
        protected virtual void Configure()
        {
            minor = true; tonicPc = 9; tonicLetter = 5; bpm = 76; meterNum = 4; meterDen = 4;
            loMel = 64; hiMel = 86; loCnt = 55; hiCnt = 74;
            programs = new[] { 73, 68, 46, 48 };
            partNames = new[] { "Melodie 1", "Melodie 2", "Accompagnement", "Basse" };
            partClefs = new[] { ScoreClefKind.Treble, ScoreClefKind.Treble, ScoreClefKind.Treble, ScoreClefKind.Bass };
            partVolumes = new[] { 1.0, 0.75, 0.9, 0.9 };
            title = "Composer V2";
        }

        // Fills `notes` (and sets `cursor` = total slices). Base = the plain 4-part V2 form.
        protected virtual void Arrange()
        {
            var introProg = SampleProg(model, mm, rng, 2, true, false, "intro");
            var themeProg = SampleProg(model, mm, rng, 8, true, false, "theme");
            var devProg = SampleProg(model, mm, rng, 16, true, false, "climax");
            var reprProg = themeProg.Select(c => new Cell { Root = c.Root, Canon = c.Canon }).ToList();
            reprProg[reprProg.Count - 1] = new Cell { Root = 0, Canon = GroupToCanon("madd9") };
            var outroProg = new List<Cell> {
                new Cell{ Root=5, Canon=GroupToCanon("min7") },
                new Cell{ Root=0, Canon=GroupToCanon("madd9") },
                new Cell{ Root=0, Canon=GroupToCanon("min") },
                new Cell{ Root=0, Canon=GroupToCanon("madd9") },
            };

            string figIntro = PickSectionMotif(model, "intro", rng);
            string figBody = PickSectionMotif(model, "body", rng);
            string figClimax = PickSectionMotif(model, "climax", rng);
            string figOutro = PickSectionMotif(model, "outro", rng);

            var theme = GenerateLine(model, mm, rng, themeProg, tonicPc, scale, loMel, hiMel, true, 0, "theme");

            int t = 0;
            RenderAccomp(notes, introProg, t, tonicPc, figIntro, 44);
            RenderBass(notes, introProg, t, tonicPc, 46);
            t += introProg.Count * Bar;

            EmitMelody(notes, theme, t, 0, 78, 0);
            RenderAccomp(notes, themeProg, t, tonicPc, figBody, 50);
            RenderBass(notes, themeProg, t, tonicPc, 56);
            t += themeProg.Count * Bar;

            EmitMelody(notes, theme, t, 0, 80, 1);
            RenderAccomp(notes, themeProg, t, tonicPc, figBody, 50);
            RenderBass(notes, themeProg, t, tonicPc, 56);
            t += themeProg.Count * Bar;

            int devBars = 16, climaxBar = 10;
            var motif = theme.Where(n => n.Start < 2 * Bar).ToList();
            int[] devShifts = { 0, 1, 2, 3, 4, 3, 2, 1 };
            for (int blk = 0; blk < 8; blk++)
            {
                int blkBar = blk * 2, blkStart = t + blkBar * Bar;
                var ps = motif.Select(n => TransposeInScale(n.Pitch, devShifts[blk], tonicPc, scale)).ToList();
                int oct = 0, mx = ps.Count > 0 ? ps.Max() : 0, mn = ps.Count > 0 ? ps.Min() : 0;
                while (mx + 12 * oct > hiMel) oct--; while (mn + 12 * oct < loMel) oct++;
                for (int j = 0; j < motif.Count; j++)
                {
                    int bar = blkBar + motif[j].Start / Bar;
                    notes.Add(new OutNote { Part = 0, Pitch = ps[j] + 12 * oct, Start = blkStart + motif[j].Start, Len = motif[j].Len, Vel = DevVel(bar, devBars, climaxBar) });
                }
            }
            var devCounter = GenerateLine(model, mm, rng, devProg, tonicPc, scale, loCnt, hiCnt, false, 4, "climax", true);
            foreach (var n in devCounter)
            {
                int bar = n.Start / Bar;
                notes.Add(new OutNote { Part = 1, Pitch = n.Pitch, Start = t + n.Start, Len = n.Len, Vel = Math.Max(1, DevVel(bar, devBars, climaxBar) - 10) });
            }
            RenderAccompArc(notes, devProg, t, tonicPc, figClimax, devBars, climaxBar);
            RenderBassArc(notes, devProg, t, tonicPc, devBars, climaxBar);
            t += devProg.Count * Bar;

            EmitMelody(notes, theme, t, 0, 74, true, tonicPc, loMel, hiMel, 0);
            var reprCounter = GenerateLine(model, mm, rng, reprProg, tonicPc, scale, loCnt, hiCnt, false, 7, "body", true);
            foreach (var n in reprCounter)
                notes.Add(new OutNote { Part = 1, Pitch = n.Pitch, Start = t + n.Start, Len = n.Len, Vel = 46 });
            RenderAccomp(notes, reprProg, t, tonicPc, figBody, 48);
            RenderBass(notes, reprProg, t, tonicPc, 54);
            t += reprProg.Count * Bar;

            RenderAccomp(notes, outroProg, t, tonicPc, figOutro, 40);
            RenderBass(notes, outroProg, t, tonicPc, 42);
            t += outroProg.Count * Bar;

            cursor = t;
        }

        // ============ harmony hook ============
        // Generate a chord SEQUENCE: each chord's ROOT comes from HarmonyRoot (trained on chord-to-chord motion),
        // and its DURATION in bars from the HarmonicRhythm "cell" model (order 4 + section). The chords tile the
        // requested bars as ONE Cell per bar (a held chord repeats across its bars). This gives a corpus-realistic
        // harmonic rhythm instead of a brand-new chord every bar — while keeping the 1-cell-per-bar contract that
        // the melody and the accompaniment renderers rely on.
        protected virtual List<Cell> SampleProg(CorpusModelV2 model, ModeModels mm, Random rng, int bars, bool tonicStart, bool tonicEnd, string section = "body")
        {
            var cells = new List<Cell>();
            int rootOrder = MusicMathV2.Order(model.Orders, "harmonyRoot", 2);
            int hrOrder = MusicMathV2.Order(model.Orders, "harmonicRhythm", 4);
            var rootHist = new List<string>();   // [0] = most-recent chord root
            var durHist = new List<string>();    // [0] = most-recent chord-duration bucket
            int prevRoot = -1, idx = 0;
            while (cells.Count < bars)
            {
                int root;
                if (idx == 0 && tonicStart) root = 0;
                else
                {
                    string r1 = rootHist.Count > 0 ? rootHist[0] : "^";
                    var rlad = MusicMathV2.BuildLadder("root", MusicMathV2.Hist(rootOrder, k => k < rootHist.Count ? rootHist[k] : "^"),
                        rootOrder, 2, null, null, new[] { "sec|root1", "root1", "" }, new[] { section + "|" + r1, r1, "" });
                    root = PickInt(Dist(mm.HarmonyRoot, rlad.Labels, rlad.Ctx), rng, prevRoot >= 0 ? prevRoot : 0);
                }
                // hold DURATION in bars from the harmonic-rhythm model (ctx aligned to the analyzer's tiers, incl. "sec|dur1")
                string d1 = durHist.Count > 0 ? durHist[0] : "^";
                var hlad = MusicMathV2.BuildLadder("dur", MusicMathV2.Hist(hrOrder, k => k < durHist.Count ? durHist[k] : "^"),
                    hrOrder, 2, null, null, new[] { "sec|dur1", "dur1", "" }, new[] { section + "|" + d1, d1, "" });
                string db = Pick(Dist(model.HarmonicRhythm, hlad.Labels, hlad.Ctx), rng);
                int dur = Math.Min(4, BarsFromDurBucket(db));
                if (dur > bars - cells.Count) dur = bars - cells.Count;
                if (dur < 1) dur = 1;

                string g = Pick(Dist(mm.QualityByDegree, new[] { "sec|deg", "deg", "" }, new[] { section + "|" + root, root.ToString(), "" }), rng) ?? (root == 0 ? (minor ? "min" : "maj") : "maj");
                int canon = GroupToCanon(g);
                int bassDeg = -1;
                string invc = Pick(Dist(model.Inversion, new[] { "func", "" }, new[] { MusicMathV2.ChordFunction(root), "" }), rng) ?? "root";
                if (invc == "inv1" || invc == "inv2")
                {
                    int third = -1, fifth = -1;
                    foreach (int pc in ChordPcs(new Cell { Root = root, Canon = canon })) { int rel = Mod12(pc - root); if (rel == 3 || rel == 4) third = pc; else if (rel == 6 || rel == 7 || rel == 8) fifth = pc; }
                    if (invc == "inv1" && third >= 0) bassDeg = third;
                    else if (invc == "inv2" && fifth >= 0) bassDeg = fifth;
                }
                for (int k = 0; k < dur; k++) cells.Add(new Cell { Root = root, Canon = canon, BassDeg = bassDeg });

                durHist.Insert(0, MusicMathV2.DurBucketBars(dur));
                rootHist.Insert(0, root.ToString());
                prevRoot = root; idx++;
            }
            if (tonicEnd && cells.Count > 0)
            {
                cells[cells.Count - 1].Root = 0;
                cells[cells.Count - 1].BassDeg = -1;
                cells[cells.Count - 1].Canon = GroupToCanon(minor ? "min" : "maj");
            }
            return cells;
        }

        // representative bar-count for a chord-duration bucket (inverse of MusicMathV2.DurBucketBars)
        static int BarsFromDurBucket(string b)
        {
            switch (b) { case "2": return 2; case "3-4": return 3; case "5+": return 5; default: return 1; }  // "<1","1",null → 1 bar
        }

        // ============ melody hooks ============
        protected virtual int DurFromBucket(string b)
        {
            switch (b)
            {
                case "16": return 6; case "8": return 12; case "8.": return 18; case "q": return 24;
                case "q.": return 36; case "h": return 48; case "h.": return 72; case "w": return 96;
                case "w+": return 96; default: return 12;
            }
        }

        protected virtual List<MelNote> GenerateLine(CorpusModelV2 model, ModeModels mm, Random rng, List<Cell> prog,
                                          int tonicPc, int[] scale, int loMel, int hiMel, bool cadenceFinal, int startDeg,
                                          string section, bool invertArch = false)
        {
            var notes = new List<MelNote>();
            int cur = 60 + Mod12(tonicPc + startDeg);
            while (cur < loMel) cur += 12; while (cur > hiMel) cur -= 12;
            cur = SnapScale(cur, tonicPc, scale);
            int prevDeg = Mod12(cur - tonicPc), prev2 = -1, prev3 = -1, prev4 = -1, prev5 = -1, prev6 = -1, prev7 = -1, prev8 = -1;
            int pendingResolve = -1;

            int nBars = prog.Count;
            // 4-bar phrases when possible → the breath/cadence lands every 4 bars (a natural phrase), not every 2.
            int phraseBars = (nBars % 4 == 0) ? 4 : ((nBars % 2 == 0) ? 2 : 1);
            int nPhrases = Math.Max(1, nBars / phraseBars);
            for (int ph = 0; ph < nPhrases; ph++)
            {
                int barOffset = ph * phraseBars;
                int span = phraseBars * Bar;
                int phraseStart = barOffset * Bar;
                bool lastPhrase = ph == nPhrases - 1;
                // BREATHE only at a phrase end, and only the LAST phrase leaves a real SILENCE — interior phrases
                // connect through a held cadence note (no gap), so the line stays homogeneous (no break every 2 bars).
                int hold = lastPhrase ? Beat + Beat / 2 : Beat;
                int rest = lastPhrase ? Beat / 2 : 0;
                int motionEnd = Math.Max(Beat, span - hold - rest);
                int half = motionEnd / 2;

                // RHYTHM: default = the PHRASE-COHERENT bar rhythm (reused bar of corpus cells, no mid-phrase rests).
                // UseMotifRhythm=true → rigid 2-cell motif; UsePerBeatRhythm=true → old per-beat bounded chain.
                int rcOrder = MusicMathV2.Order(model.Orders, "rhythmCell", 8);
                int rcBeat = (phraseStart / Beat) % Math.Max(1, meterNum);
                var slots = UseMotifRhythm
                    ? GeneratePhraseMotifRhythm(model.RhythmCell, rcOrder, rng, motionEnd, rcBeat, meterNum)
                    : UsePerBeatRhythm
                        ? GenerateMelodyRhythm(model.RhythmCell, rcOrder, rng, motionEnd, rcBeat, meterNum, character + "/" + section)
                        : GeneratePhraseRhythm(model.RhythmCell, rcOrder, rng, motionEnd, rcBeat, meterNum, character + "/" + section);
                int si = 0, pos = 0;
                while (si < slots.Count)
                {
                    pos = slots[si][0]; int dur = slots[si][1]; bool isRest = slots[si][2] == 1; si++;
                    if (pos >= motionEnd) break;
                    if (pos + dur > motionEnd) dur = motionEnd - pos;
                    if (dur <= 0) break;

                    bool strong = (pos % (2 * Beat) == 0);
                    string metric = strong ? "S" : "w";

                    if (isRest) continue;   // a breath/rest from the cell — leave a gap, no note

                    int barIn = Math.Min(phraseBars - 1, pos / Bar);
                    var chord = prog[Math.Min(prog.Count - 1, barOffset + barIn)];
                    int[] cpcs = ChordPcs(chord);
                    string func = MusicMathV2.ChordFunction(chord.Root);
                    int dir = (pos < half ? 1 : -1) * (invertArch ? -1 : 1);

                    int nextAbsBar = (phraseStart + pos + dur) / Bar;
                    var nextChord = prog[Math.Min(prog.Count - 1, nextAbsBar)];
                    string nfunc = MusicMathV2.ChordFunction(nextChord.Root);
                    bool approachingChange = nextChord.Root != chord.Root;

                    bool curCt = cpcs.Contains(prevDeg);
                    int rel = Mod12(prevDeg - chord.Root);
                    string role = curCt ? "ct" : (rel == 2 ? "t9" : rel == 5 ? "t11" : rel == 9 ? "t13" : "nct");

                    // #2 — the SAME technique as the rhythm: the learned model decides the PITCH everywhere (not only
                    // weak interior notes). Build the chord-conditioned melody distribution for THIS note (identical
                    // ladder to the analyzer), then sample a degree from it — biased toward chord tones, strongly on
                    // strong beats (consonance), toward the NEXT chord's tones when approaching a change (voice-leading).
                    // Replaces the old hard NearestChordTone heuristic that forced strong beats onto a chord tone.
                    int melOrder = MusicMathV2.Order(model.Orders, "melody", 8);
                    var dHist = MusicMathV2.Hist(melOrder, k =>
                    {
                        int v;
                        switch (k) { case 0: v = prevDeg; break; case 1: v = prev2; break; case 2: v = prev3; break; case 3: v = prev4; break; case 4: v = prev5; break; case 5: v = prev6; break; case 6: v = prev7; break; case 7: v = prev8; break; default: return "^"; }
                        return v >= 0 ? v.ToString() : "^";
                    });
                    string d2s = prev2 >= 0 ? prev2.ToString() : "^";
                    // EXACT chord-degree neighbourhood around the note being generated (chord BEFORE / DURING / AFTER).
                    int xbar = Math.Min(prog.Count - 1, barOffset + barIn);
                    int xcRoot = Mod12(chord.Root), xpRoot = -1, xnRoot = -1;
                    for (int bb = xbar - 1; bb >= 0; bb--) if (Mod12(prog[bb].Root) != xcRoot) { xpRoot = Mod12(prog[bb].Root); break; }
                    for (int bb = xbar + 1; bb < prog.Count; bb++) if (Mod12(prog[bb].Root) != xcRoot) { xnRoot = Mod12(prog[bb].Root); break; }
                    string cdeg = xcRoot.ToString();
                    string pdeg = xpRoot < 0 ? "^" : xpRoot.ToString();
                    string ndeg = xnRoot < 0 ? "^" : xnRoot.ToString();
                    // Item 2 VIEWPOINTS: interval + contour that led INTO the current note (degree-space, byte-identical to analyzer/resolver).
                    int giv = prev2 >= 0 ? MusicMathV2.SignedIv(prev2, prevDeg) : 0;
                    string iv1 = prev2 >= 0 ? giv.ToString() : "^";
                    string cont1 = prev2 >= 0 ? MusicMathV2.Contour(giv) : "^";
                    var melLad = MusicMathV2.BuildLadder("d", dHist, melOrder, 3, null, null,
                        new[] { "sec|d1|metric|role", "d1|pdeg|cdeg|ndeg|role", "d1|cdeg|role", "d1|iv1|role", "d1|cont1", "d1|metric|role|nfunc", "d2|d1|metric|role", "d1|func|role|nfunc", "d1|metric|role", "d1|role", "d1", "" },
                        new[] { character + "/" + section + "|" + prevDeg + "|" + metric + "|" + role, prevDeg + "|" + pdeg + "|" + cdeg + "|" + ndeg + "|" + role, prevDeg + "|" + cdeg + "|" + role, prevDeg + "|" + iv1 + "|" + role, prevDeg + "|" + cont1, prevDeg + "|" + metric + "|" + role + "|" + nfunc, d2s + "|" + prevDeg + "|" + metric + "|" + role, prevDeg + "|" + func + "|" + role + "|" + nfunc, prevDeg + "|" + metric + "|" + role, prevDeg + "|" + role, prevDeg.ToString(), "" });
                    var md = Dist(mm.Melody, melLad.Labels, melLad.Ctx);

                    // chord-tone bias: strong beats lean consonant; an approaching change leads to the NEXT chord
                    int[] biasPcs = approachingChange ? ChordPcs(nextChord) : cpcs;
                    double boost = strong ? 3.0 : (approachingChange ? 2.0 : 1.0);
                    var mdb = md;
                    if (md != null && boost != 1.0 && biasPcs != null)
                    {
                        mdb = new Dictionary<string, double>();
                        foreach (var kv in md) { int dg; double w = kv.Value; if (int.TryParse(kv.Key, out dg) && Array.IndexOf(biasPcs, dg) >= 0) w *= boost; mdb[kv.Key] = w; }
                    }

                    int pitch;
                    if (pendingResolve >= 0) { pitch = pendingResolve; pendingResolve = -1; }
                    else
                    {
                        int nd = PickInt(mdb, rng, prevDeg);
                        int cand = PlaceNear(cur, Mod12(tonicPc + nd));
                        if (Math.Abs(cand - cur) > 4) pitch = ScaleStepDir(cur, dir, tonicPc, scale);
                        else pitch = SnapScale(cand, tonicPc, scale);
                    }
                    // every BEAT onset lands ON a chord tone (the model still chose the region/contour): the arpeggiated
                    // accompaniment spells the chord out, so a non-chord note on a beat clashes audibly — keep the
                    // structural (on-beat) notes consonant while off-beat passing notes stay model-driven.
                    if (pos % Beat == 0 && pendingResolve < 0) pitch = NearestChordTone(pitch, cpcs, tonicPc);
                    while (pitch > hiMel) pitch -= 12; while (pitch < loMel) pitch += 12;
                    if (Math.Abs(pitch - cur) > 7) pitch = cur + Math.Sign(pitch - cur) * 7;

                    notes.Add(new MelNote { Deg = Mod12(pitch - tonicPc), Pitch = pitch, Start = phraseStart + pos, Len = dur, VelOffset = DynOffset(section, phraseStart + pos, Math.Sign(pitch - cur)) });
                    prev8 = prev7; prev7 = prev6; prev6 = prev5; prev5 = prev4; prev4 = prev3; prev3 = prev2; prev2 = prevDeg; prevDeg = Mod12(pitch - tonicPc); cur = pitch;
                }

                int cadDeg;
                if (lastPhrase && cadenceFinal) cadDeg = 0;
                else { cadDeg = PickInt(Dist(mm.Cadence, new[] { "" }, new[] { "" }), rng, 7); if (!lastPhrase && cadDeg == 0) cadDeg = 7; }
                int cadPitch = NearestPitch(cur, Mod12(tonicPc + cadDeg), loMel, hiMel);
                if (Math.Abs(cadPitch - cur) > 4) cadPitch = SnapScale(cur + Math.Sign(cadPitch - cur) * 2, tonicPc, scale);
                notes.Add(new MelNote { Deg = Mod12(cadPitch - tonicPc), Pitch = cadPitch, Start = phraseStart + motionEnd, Len = hold, VelOffset = DynOffset(section, phraseStart + motionEnd, Math.Sign(cadPitch - cur)) });
                prev8 = prev7; prev7 = prev6; prev6 = prev5; prev5 = prev4; prev4 = prev3; prev3 = prev2; prev2 = prevDeg; prevDeg = Mod12(cadPitch - tonicPc); cur = cadPitch;
            }
            return notes;
        }

        // Generate a phrase's RHYTHM as a sequence of beat-CELLS from the corpus RhythmCell chain (cell ∣ prev 3
        // cells × beat-index-in-bar), laid end-to-end. Each cell expands to note/rest slots {start, len, isRest};
        // spanning notes (q., h…) and rests (r8, rq…) fall naturally where they land. Returns the slot list.
        protected List<int[]> GenerateCellRhythm(CondModel cellModel, int order, Random rng, int spanSlices, int beatStartInBar, int meterNum, string section)
        {
            var slots = new List<int[]>();
            string c1 = "^", c2 = "^", c3 = "^", c4 = "^", c5 = "^", c6 = "^", c7 = "^", c8 = "^";
            string sec = section ?? "body";
            int pos = 0, guard = 0, mn = Math.Max(1, meterNum);
            while (pos < spanSlices && guard++ < 256)
            {
                int beatIdx = beatStartInBar + pos / Beat;
                string bp = (beatIdx % mn).ToString();
                var cHist = MusicMathV2.Hist(order, k =>
                {
                    switch (k) { case 0: return c1; case 1: return c2; case 2: return c3; case 3: return c4; case 4: return c5; case 5: return c6; case 6: return c7; case 7: return c8; default: return "^"; }
                });
                var lad = MusicMathV2.BuildLadder("c", cHist, order, 1, "bp", bp,
                    new[] { "sec|bp|c1", "c2|c1", "sec|c1", "c1", "sec|bp", "bp", "" },
                    new[] { sec + "|" + bp + "|" + c1, c2 + "|" + c1, sec + "|" + c1, c1, sec + "|" + bp, bp, "" });
                var dist = Dist(cellModel, lad.Labels, lad.Ctx);
                string cell = PickCell(dist, rng);
                if (string.IsNullOrEmpty(cell)) cell = "8+8";
                bool any = false;
                foreach (var tok in cell.Split('+'))
                {
                    bool isRest = tok.Length > 0 && tok[0] == 'r';
                    int dur = DurFromBucket(isRest ? tok.Substring(1) : tok);
                    if (dur <= 0) dur = 12;
                    if (pos + dur > spanSlices) dur = spanSlices - pos;
                    if (dur <= 0) { pos = spanSlices; break; }
                    slots.Add(new[] { pos, dur, isRest ? 1 : 0 });
                    pos += dur; any = true;
                    if (pos >= spanSlices) break;
                }
                if (!any) pos += Beat;   // safety: a degenerate cell produced nothing
                c8 = c7; c7 = c6; c6 = c5; c5 = c4; c4 = c3; c3 = c2; c2 = c1; c1 = cell;
            }
            return slots;
        }

        // MELODY rhythm — the HYBRID BOUNDED "#1": sample the trained per-beat RhythmCell chain (so the line gets the
        // corpus's real duration variety, conditioned on the metric position), but BOUND it so it cannot spiral into
        // runaway held notes (the raw chain self-loops on "-" and collapses to one giant note — see StyleDistanceV3):
        //   • a "-" beat-cell EXTENDS the current note, but only up to MaxHold consecutive beats;
        //   • an all-rest cell becomes a breath, but only up to MaxRest consecutive beats;
        //   • once a cap is hit (or nothing is sounding yet), a NOTE-bearing cell is FORCED instead, keeping the line
        //     flowing while still drawing its onset figure (8+8, q., 16+16+16+16, …) from the same chain distribution.
        protected List<int[]> GenerateMelodyRhythm(CondModel cellModel, int order, Random rng, int spanSlices, int beatStartInBar, int meterNum, string section)
        {
            const int MaxHold = 2;   // consecutive "-" beats a note may sustain before a fresh onset is forced
            const int MaxRest = 1;   // consecutive rest beats before a fresh onset is forced
            var slots = new List<int[]>();
            string c1 = "^", c2 = "^", c3 = "^", c4 = "^", c5 = "^", c6 = "^", c7 = "^", c8 = "^";
            string sec = section ?? "body";
            int pos = 0, guard = 0, mn = Math.Max(1, meterNum), lastNoteIdx = -1, heldRun = 0, restRun = 0;
            while (pos < spanSlices && guard++ < 256)
            {
                int beatIdx = beatStartInBar + pos / Beat;
                string bp = (beatIdx % mn).ToString();
                var cHist = MusicMathV2.Hist(order, k =>
                {
                    switch (k) { case 0: return c1; case 1: return c2; case 2: return c3; case 3: return c4; case 4: return c5; case 5: return c6; case 6: return c7; case 7: return c8; default: return "^"; }
                });
                var lad = MusicMathV2.BuildLadder("c", cHist, order, 1, "bp", bp,
                    new[] { "sec|bp|c1", "c2|c1", "sec|c1", "c1", "sec|bp", "bp", "" },
                    new[] { sec + "|" + bp + "|" + c1, c2 + "|" + c1, sec + "|" + c1, c1, sec + "|" + bp, bp, "" });
                var dist = Dist(cellModel, lad.Labels, lad.Ctx);
                string cell = PickCell(dist, rng);
                if (string.IsNullOrEmpty(cell)) cell = "8+8";

                if (cell == "-")
                {
                    if (lastNoteIdx >= 0 && heldRun < MaxHold)   // sustain the note, bounded
                    {
                        int ext = Math.Min(Beat, spanSlices - pos);
                        if (ext <= 0) break;
                        slots[lastNoteIdx][1] += ext; pos += ext; heldRun++; restRun = 0;
                        c8 = c7; c7 = c6; c6 = c5; c5 = c4; c4 = c3; c3 = c2; c2 = c1; c1 = cell;
                        continue;
                    }
                    cell = PickCellNoteBearing(dist, rng) ?? "8+8";   // hold cap hit (or nothing sounding) → force an onset
                }
                else if (IsAllRest(cell) && restRun >= MaxRest)
                    cell = PickCellNoteBearing(dist, rng) ?? "8+8";   // rest cap hit → force an onset

                bool hadNote = false, hadAny = false;
                foreach (var tok in cell.Split('+'))
                {
                    bool isRest = tok.Length > 0 && tok[0] == 'r';
                    int dur = DurFromBucket(isRest ? tok.Substring(1) : tok);
                    if (dur <= 0) dur = 12;
                    if (pos + dur > spanSlices) dur = spanSlices - pos;
                    if (dur <= 0) { pos = spanSlices; break; }
                    slots.Add(new[] { pos, dur, isRest ? 1 : 0 });
                    if (!isRest) { lastNoteIdx = slots.Count - 1; hadNote = true; }
                    pos += dur; hadAny = true;
                    if (pos >= spanSlices) break;
                }
                if (!hadAny) pos += Beat;
                if (hadNote) { heldRun = 0; restRun = 0; }
                else restRun++;   // an all-rest cell we kept (a bounded breath)
                c8 = c7; c7 = c6; c6 = c5; c5 = c4; c4 = c3; c3 = c2; c2 = c1; c1 = cell;
            }
            return slots;
        }

        // a cell whose every token is a rest ("rq", "r8+r8") — a pure breath beat (NOT the "-" hold sentinel)
        static bool IsAllRest(string cell)
        {
            if (string.IsNullOrEmpty(cell) || cell == "-") return false;
            foreach (var t in cell.Split('+')) if (!(t.Length > 0 && t[0] == 'r')) return false;
            return true;
        }

        // pick a NOTE-bearing cell (≥1 onset) from the chain distribution — excludes "-" and all-rest cells, but
        // keeps the full duration palette (unlike PickNoteCell, which biases short). Used when a hold/rest cap forces
        // an onset, so the forced beat still draws its figure from the corpus rather than a fixed fallback.
        protected static string PickCellNoteBearing(Dictionary<string, double> dist, Random rng)
        {
            if (dist == null) return null;
            var note = new Dictionary<string, double>();
            foreach (var kv in dist)
            {
                string c = kv.Key;
                if (c.Length == 0 || c == "-" || c == "^" || IsAllRest(c)) continue;
                note[c] = kv.Value;
            }
            return note.Count > 0 ? Pick(note, rng) : null;
        }

        // Build a phrase's RHYTHM from a small, REUSED motif: sample a 2-cell motif of NOTE-bearing cells from the
        // corpus model (conditioned on beat position) and TILE it across the phrase. Reusing the same 1-2 cells gives
        // a homogeneous, recognizable pattern (e.g. dotted-quarter + two eighths, repeated) instead of an independent
        // per-beat draw — and it never injects a mid-phrase rest (breaths live only at the phrase end). All slots = notes.
        protected List<int[]> GeneratePhraseMotifRhythm(CondModel cellModel, int order, Random rng, int spanSlices, int beatStartInBar, int meterNum)
        {
            int mn = Math.Max(1, meterNum);
            Func<int, string, string> sample = (bp, prev) =>
            {
                string bps = (((bp % mn) + mn) % mn).ToString();
                var lad = MusicMathV2.BuildLadder("c", MusicMathV2.Hist(order, k => k == 0 ? (prev ?? "^") : "^"), order, 1, "bp", bps,
                    new[] { "c1", "bp", "" }, new[] { prev ?? "^", bps, "" });
                string c = PickNoteCell(Dist(cellModel, lad.Labels, lad.Ctx), rng);
                return string.IsNullOrEmpty(c) ? "8+8" : c;
            };
            // a 2-cell motif: a "strong-beat" cell + a "weak-beat" cell, reused across the whole phrase
            string cA = sample(beatStartInBar, null);
            string cB = sample(beatStartInBar + 1, cA);
            var motif = new[] { cA, cB };

            var slots = new List<int[]>();
            int pos = 0, mi = 0, guard = 0;
            while (pos < spanSlices && guard++ < 256)
            {
                foreach (var tok in motif[mi % motif.Length].Split('+'))
                {
                    string t = tok.Length > 0 && tok[0] == 'r' ? tok.Substring(1) : tok;   // a stray rest token → sounded (no mid-phrase silence)
                    int dur = DurFromBucket(t); if (dur <= 0) dur = 12;
                    if (pos + dur > spanSlices) dur = spanSlices - pos;
                    if (dur <= 0) { pos = spanSlices; break; }
                    slots.Add(new[] { pos, dur, 0 });
                    pos += dur;
                    if (pos >= spanSlices) break;
                }
                mi++;
            }
            return slots;
        }

        // PHRASE-COHERENT rhythm (the default). Sample ONE bar of NOTE-BEARING cells from the corpus chain (FULL
        // duration palette → real groupings like q.+8 / 8+8 / q / 8+8+8+8), then REUSE that bar across the whole phrase
        // with a light last-cell variation on interior bars. This gives rhythmic CELLS that associate into a coherent
        // phrase (instead of the per-beat independent draw = "brouillon"), and NEVER injects a mid-phrase rest — the
        // phrase-end breath is owned by GenerateLine (its hold/rest tail). Cells stay corpus-conditioned (beat position
        // + previous cells + character/section), so the phrasing is stylistic, not fixed.
        protected List<int[]> GeneratePhraseRhythm(CondModel cellModel, int order, Random rng, int spanSlices, int beatStartInBar, int meterNum, string section)
        {
            int mn = Math.Max(1, meterNum);
            string sec = section ?? "body";
            // sample cells to FILL ~one bar (Bar slices), advancing by each cell's real length so multi-beat groupings appear
            Func<List<string>> sampleBar = () =>
            {
                var cells = new List<string>();
                string c1 = "^", c2 = "^";
                int acc = 0, guard = 0;
                while (acc < Bar && guard++ < 16)
                {
                    int beatIdx = beatStartInBar + acc / Beat;
                    string bp = (((beatIdx % mn) + mn) % mn).ToString();
                    var lad = MusicMathV2.BuildLadder("c", MusicMathV2.Hist(order, k => k == 0 ? c1 : (k == 1 ? c2 : "^")), order, 1, "bp", bp,
                        new[] { "sec|bp|c1", "c2|c1", "sec|c1", "c1", "sec|bp", "bp", "" },
                        new[] { sec + "|" + bp + "|" + c1, c2 + "|" + c1, sec + "|" + c1, c1, sec + "|" + bp, bp, "" });
                    string cell = PickCellNoteBearing(Dist(cellModel, lad.Labels, lad.Ctx), rng);
                    if (string.IsNullOrEmpty(cell)) cell = "8+8";
                    cells.Add(cell); acc += Math.Max(1, CellSlices(cell)); c2 = c1; c1 = cell;
                }
                return cells;
            };

            var bar0 = sampleBar();
            var slots = new List<int[]>();
            int pos = 0, barIdx = 0, guard2 = 0;
            while (pos < spanSlices && guard2++ < 64)
            {
                // reuse bar0 for coherence; on interior odd bars, sometimes swap the LAST cell for a lift (a → a')
                List<string> bar = bar0;
                if (barIdx > 0 && (barIdx % 2 == 1) && rng.NextDouble() < 0.5 && bar0.Count > 0)
                {
                    bar = new List<string>(bar0);
                    var alt = sampleBar();
                    if (alt.Count > 0) bar[bar.Count - 1] = alt[alt.Count - 1];
                }
                foreach (var cell in bar)
                {
                    foreach (var tok in cell.Split('+'))
                    {
                        string t = tok.Length > 0 && tok[0] == 'r' ? tok.Substring(1) : tok;   // no mid-phrase rests
                        int dur = DurFromBucket(t); if (dur <= 0) dur = 12;
                        if (pos + dur > spanSlices) dur = spanSlices - pos;
                        if (dur <= 0) { pos = spanSlices; break; }
                        slots.Add(new[] { pos, dur, 0 });
                        pos += dur;
                        if (pos >= spanSlices) break;
                    }
                    if (pos >= spanSlices) break;
                }
                barIdx++;
            }
            return slots;
        }

        // pick a NOTE-bearing rhythm cell for the reused motif: exclude rests + continuation/sentinel tokens, and
        // PREFER short cells (≤ a dotted quarter) so the MOTION stays melodic (eighths/sixteenths/dotted-eighths) —
        // long held values (half, dotted-half) belong to the phrase-end cadence, not to a repeated motion motif.
        protected static string PickNoteCell(Dictionary<string, double> dist, Random rng)
        {
            if (dist == null) return null;
            var note = new Dictionary<string, double>();   // any note-bearing cell
            var shortc = new Dictionary<string, double>(); // note-bearing AND ≤ 36 slices (a dotted quarter)
            foreach (var kv in dist)
            {
                string c = kv.Key;
                if (c.Length == 0 || c == "-" || c == "^" || c.IndexOf('r') >= 0) continue;
                note[c] = kv.Value;
                if (CellSlices(c) <= 36) shortc[c] = kv.Value;
            }
            return Pick(shortc.Count > 0 ? shortc : note, rng);   // fall back to any note cell if none is short
        }

        // total slices of a rhythm cell (sum of its '+'-joined duration tokens) — static mirror of DurFromBucket
        static int CellSlices(string cell)
        {
            int sum = 0;
            foreach (var tok in cell.Split('+'))
            {
                string t = tok.Length > 0 && tok[0] == 'r' ? tok.Substring(1) : tok;
                switch (t)
                {
                    case "16": sum += 6; break; case "8": sum += 12; break; case "8.": sum += 18; break;
                    case "q": sum += 24; break; case "q.": sum += 36; break; case "h": sum += 48; break;
                    case "h.": sum += 72; break; case "w": case "w+": sum += 96; break; default: sum += 12; break;
                }
            }
            return sum;
        }

        // pick a rhythm cell, excluding the continuation/sentinel tokens that don't start a fresh event
        protected static string PickCell(Dictionary<string, double> dist, Random rng)
        {
            if (dist == null) return null;
            var filt = new Dictionary<string, double>();
            foreach (var kv in dist) if (kv.Key.Length > 0 && kv.Key != "-" && kv.Key != "^") filt[kv.Key] = kv.Value;
            return Pick(filt, rng);
        }

        // ============ accompaniment hooks ============
        protected virtual string PickSectionMotif(CorpusModelV2 model, string section, Random rng)
        {
            string s = Pick(Dist(model.Texture, new[] { "section", "" }, new[] { section, "" }), rng);
            if (string.IsNullOrEmpty(s)) return "broken";
            int bar = s.IndexOf('|');
            return bar > 0 ? s.Substring(0, bar) : s;
        }

        protected virtual void RenderAccomp(List<OutNote> outNotes, List<Cell> prog, int sectionStart, int tonicPc, string motif, int vel)
        {
            if (GuitarFingerstyle)
            {
                for (int bar = 0; bar < prog.Count; bar++) RenderFingerstyleBar(outNotes, prog[bar], sectionStart + bar * Bar, tonicPc, vel);
                return;
            }
            if (LearnedAccomp)
            {
                for (int bar = 0; bar < prog.Count; bar++) RenderLearnedAccompBar(outNotes, prog[bar], sectionStart + bar * Bar, tonicPc, vel);
                return;
            }
            for (int bar = 0; bar < prog.Count; bar++)
            {
                int[] pcs = ChordPcs(prog[bar]);
                var voiced = new List<int>();
                foreach (int pc in pcs) { int p = 48 + Mod12(pc + tonicPc - 48); while (p < 50) p += 12; while (p > 67) p -= 12; voiced.Add(p); }
                voiced = voiced.Distinct().OrderBy(x => x).ToList();
                if (voiced.Count == 0) continue;
                int abs = sectionStart + bar * Bar;

                string fig;
                if (motif == "sustain" || motif == "rest") fig = "rollsilence";
                else if (motif == "alberti") fig = "alberti";
                else if (motif == "arp-down") fig = "arpdown";
                else if (motif == "block" || motif == "broken") fig = "wave";
                else fig = "arp";
                if (bar % 4 == 3 && fig != "rollsilence") fig = "roll";

                if (fig == "rollsilence")
                {
                    RollChord(outNotes, voiced, abs, abs + Beat + Beat / 2, vel);
                    RollChord(outNotes, voiced, abs + 2 * Beat, abs + 2 * Beat + Beat + Beat / 2, Math.Max(1, vel - 6));
                }
                else if (fig == "roll")
                {
                    RollChord(outNotes, voiced, abs, abs + Bar, vel);
                }
                else if (fig == "alberti")
                {
                    int rootP = 48 + Mod12(prog[bar].Root + tonicPc - 48); while (rootP < 50) rootP += 12; while (rootP > 62) rootP -= 12;
                    int up3 = NearestChordTone(rootP + 4, pcs, tonicPc);
                    int up5 = NearestChordTone(rootP + 7, pcs, tonicPc);
                    int[] pat = { rootP, up5, rootP, up3 };
                    int k = 0;
                    for (int pos = 0; pos < Bar; pos += 12) { outNotes.Add(new OutNote { Part = 2, Pitch = pat[k % 4], Start = abs + pos, Len = 14, Vel = vel }); k++; }
                }
                else if (fig == "wave")
                {
                    int k = 0, half = Bar / 2;
                    for (int pos = 0; pos < half; pos += 12)
                    {
                        int p = voiced[k % voiced.Count] + 12 * (k / voiced.Count);
                        if (p > 71) p = voiced[k % voiced.Count];
                        outNotes.Add(new OutNote { Part = 2, Pitch = p, Start = abs + pos, Len = 14, Vel = vel });
                        k++;
                    }
                    RollChord(outNotes, voiced, abs + half, abs + Bar, vel);
                }
                else
                {
                    bool down = fig == "arpdown"; int k = 0;
                    for (int pos = 0; pos < Bar; pos += 12)
                    {
                        int idx = down ? voiced.Count - 1 - (k % voiced.Count) : (k % voiced.Count);
                        int oct = k / voiced.Count;
                        int p = voiced[idx] + (down ? -12 * oct : 12 * oct);
                        if (p > 71 || p < 40) p = voiced[idx];
                        outNotes.Add(new OutNote { Part = 2, Pitch = p, Start = abs + pos, Len = 16, Vel = vel });
                        k++;
                    }
                }
            }
        }

        protected virtual void RenderBass(List<OutNote> outNotes, List<Cell> prog, int sectionStart, int tonicPc, int vel)
        {
            for (int bar = 0; bar < prog.Count; bar++)
            {
                int pc = Mod12(prog[bar].EffBass + tonicPc);
                int p = 36 + Mod12(pc - 36); while (p < 36) p += 12; while (p > 50) p -= 12;
                outNotes.Add(new OutNote { Part = 3, Pitch = p, Start = sectionStart + bar * Bar, Len = Bar, Vel = vel });
            }
        }

        protected void RenderAccompArc(List<OutNote> notes, List<Cell> prog, int start, int tonicPc, string motif, int devBars, int climaxBar)
        {
            for (int bar = 0; bar < prog.Count; bar++)
                RenderAccomp(notes, new List<Cell> { prog[bar] }, start + bar * Bar, tonicPc, motif, Math.Max(1, DevVel(bar, devBars, climaxBar) - 14));
        }
        protected void RenderBassArc(List<OutNote> notes, List<Cell> prog, int start, int tonicPc, int devBars, int climaxBar)
        {
            for (int bar = 0; bar < prog.Count; bar++)
                RenderBass(notes, new List<Cell> { prog[bar] }, start + bar * Bar, tonicPc, Math.Max(1, DevVel(bar, devBars, climaxBar) - 8));
        }

        // ============ guitar fingerstyle accompaniment (universal option) ============
        // Standard tuning open-string MIDI pitches, low E (6th string) → high E (1st string).
        static readonly int[] GuitarOpen = { 40, 45, 50, 55, 59, 64 };

        /// <summary>Voice ONE chord on the six strings as a PLAYABLE hand-span shape: bass on the 6th (or 5th)
        /// string = the effective root, then the lowest chord-tone fret reachable on each higher string
        /// (open strings always allowed). Returns 6 sounding MIDI pitches; -1 = a muted string.</summary>
        protected static int[] VoiceGuitarChord(int[] absPcs, int bassPc)
        {
            var v = new[] { -1, -1, -1, -1, -1, -1 };
            if (absPcs == null || absPcs.Length == 0) return v;
            var pcSet = new HashSet<int>(absPcs);
            int f0 = Mod12(bassPc - GuitarOpen[0]);             // fret on the low-E string for the bass note
            int bassString, capo;
            if (f0 <= 5) { bassString = 0; v[0] = GuitarOpen[0] + f0; capo = Math.Max(0, f0 - 2); }
            else { bassString = 1; int f1 = Mod12(bassPc - GuitarOpen[1]); v[1] = GuitarOpen[1] + f1; capo = Math.Max(0, f1 - 2); }
            for (int s = bassString + 1; s < 6; s++)
            {
                int chosen = -1;
                for (int fret = 0; fret <= capo + 4; fret++)     // open string, or within the hand position
                {
                    if (fret != 0 && fret < capo) continue;
                    if (pcSet.Contains(Mod12(GuitarOpen[s] + fret))) { chosen = GuitarOpen[s] + fret; break; }
                }
                v[s] = chosen;
            }
            return v;
        }

        /// <summary>Realize ONE chord-bar as a Travis-style alternating-bass fingerpick (p-i-m-i) on the
        /// accompaniment part: a beat-1 pinch (bass + top voice), bass rings on beats 1 &amp; 3, fingers fill the
        /// offbeats. Assumes a 96-slice (4/4-grid) bar — every model that routes here uses that grid.</summary>
        protected void RenderFingerstyleBar(List<OutNote> outNotes, Cell cell, int abs, int tonicPc, int vel)
        {
            int part = AccompPartIndex; if (part < 0) return;
            var absPcs = ChordPcs(cell).Select(pc => Mod12(pc + tonicPc)).Distinct().ToArray();
            int bassPc = Mod12(cell.EffBass + tonicPc);
            int[] str = VoiceGuitarChord(absPcs, bassPc);

            var present = new List<int>();
            for (int s = 0; s < 6; s++) if (str[s] >= 0) present.Add(str[s]);
            if (present.Count == 0) return;
            present.Sort();
            int bassA = present[0];
            int bassB = present.Count > 1 ? present[1] : present[0];                 // alternate bass (thumb on beat 3)
            int tTop = present[present.Count - 1];
            int tMid = present.Count >= 2 ? present[present.Count - 2] : tTop;

            int e = Beat / 2;                                                         // an eighth = 12 slices
            AddFs(outNotes, part, bassA, abs + 0 * e, Bar / 2, vel + 6);              // beat 1: bass, rings a half-bar
            AddFs(outNotes, part, tTop, abs + 0 * e, e, vel - 2);                     // beat 1: pinch (top voice)
            AddFs(outNotes, part, tMid, abs + 1 * e, e, vel - 6);
            AddFs(outNotes, part, tTop, abs + 2 * e, e, vel - 3);
            AddFs(outNotes, part, tMid, abs + 3 * e, e, vel - 6);
            AddFs(outNotes, part, bassB, abs + 4 * e, Bar / 2, vel + 2);             // beat 3: alternate bass
            AddFs(outNotes, part, tTop, abs + 5 * e, e, vel - 4);
            AddFs(outNotes, part, tMid, abs + 6 * e, e, vel - 6);
            AddFs(outNotes, part, tTop, abs + 7 * e, e, vel - 3);
        }

        static void AddFs(List<OutNote> outNotes, int part, int pitch, int start, int len, int vel)
        {
            if (pitch < 0) return;
            outNotes.Add(new OutNote { Part = part, Pitch = pitch, Start = start, Len = Math.Max(1, len), Vel = Math.Max(1, Math.Min(127, vel)) });
        }

        // ============ learned (corpus) accompaniment — "like the melody", chord-constrained ============
        // Realize ONE chord-bar from the corpus models: AccompCell drives WHEN notes attack (per-beat rhythm
        // cells), AccompTone drives the chord-RELATIVE pitch ("<ct index>@<octave band>"), re-voiced onto this
        // chord. Falls back gracefully (eighth pulse on the root) when a model has no accompaniment data.
        protected void RenderLearnedAccompBar(List<OutNote> outNotes, Cell cell, int abs, int tonicPc, int vel)
        {
            int part = AccompPartIndex; if (part < 0) return;
            // chord degrees ordered from the root (root, 3rd, 5th, 7th…), matching the analyzer's indexing
            var degs = new List<int>(ChordPcs(cell));
            degs.Sort((x, y) => Mod12(x - cell.Root).CompareTo(Mod12(y - cell.Root)));
            if (degs.Count == 0) return;

            var slots = GenerateCellRhythm(model.AccompCell, MusicMathV2.Order(model.Orders, "accompCell", 8), rng, Bar, 0, meterNum, character + "/" + AccompSectionRole);
            int aOrder = MusicMathV2.Order(model.Orders, "accompTone", 8);   // same order as the melody
            string func = MusicMathV2.ChordFunction(cell.Root);
            string cdeg = Mod12(cell.Root).ToString();
            var ah = new List<string>();
            foreach (var s in slots)
            {
                int pos = s[0], dur = s[1]; bool isRest = s[2] == 1;
                if (pos >= Bar) break;
                if (isRest) continue;
                string metric = (pos % (2 * Beat) == 0) ? "S" : "w";
                string a1 = ah.Count > 0 ? ah[0] : "^";
                string a2 = ah.Count > 1 ? ah[1] : "^";
                var hist = MusicMathV2.Hist(aOrder, k => k < ah.Count ? ah[k] : "^");
                var lad = MusicMathV2.BuildLadder("a", hist, aOrder, 3, null, null,
                    new[] { "a1|cdeg|metric", "a2|a1|metric", "a1|func|metric", "a1|cdeg", "a1|metric", "a1", "" },
                    new[] { a1 + "|" + cdeg + "|" + metric, a2 + "|" + a1 + "|" + metric, a1 + "|" + func + "|" + metric, a1 + "|" + cdeg, a1 + "|" + metric, a1, "" });
                string tok = Pick(Dist(model.AccompTone, lad.Labels, lad.Ctx), rng);
                if (string.IsNullOrEmpty(tok)) tok = "0@1";
                int pitch = VoiceAccompToken(tok, degs, cell.Root, tonicPc);
                int v = vel + (pos % Bar == 0 ? 6 : (pos % Beat == 0 ? 2 : -3));
                outNotes.Add(new OutNote { Part = part, Pitch = pitch, Start = abs + pos, Len = Math.Max(1, dur), Vel = Math.Max(1, Math.Min(127, v)) });
                ah.Insert(0, tok);
            }
        }

        // decode "<ct index>@<octave band>" into an absolute MIDI pitch on the current chord (C3 floor + band)
        static int VoiceAccompToken(string tok, List<int> chordDegsFromRoot, int root, int tonicPc)
        {
            int at = tok.IndexOf('@');
            string idxs = at >= 0 ? tok.Substring(0, at) : tok;
            int oct = 1; if (at >= 0) int.TryParse(tok.Substring(at + 1), out oct);
            int deg;
            if (idxs == "x") deg = Mod12(root + 2);                 // non-chord/passing → a step above the root
            else { int idx; if (!int.TryParse(idxs, out idx)) idx = 0; deg = chordDegsFromRoot[Math.Max(0, Math.Min(chordDegsFromRoot.Count - 1, idx))]; }
            int pc = Mod12(deg + tonicPc);
            int basePitch = 48 + Math.Max(0, Math.Min(2, oct)) * 12;
            int p = basePitch + Mod12(pc - basePitch);
            while (p < 40) p += 12; while (p > 84) p -= 12;
            return p;
        }

        // ============ shared helpers (static toolkit) ============
        protected static int GroupToCanon(string g)
        {
            switch (g)
            {
                case "maj": return 0; case "min": return 1; case "dim": return 2; case "aug": return 3;
                case "sus": return 5; case "maj7": return 6; case "dom7": return 8; case "min7": return 7;
                case "m7b5": return 9; case "add9": return 13; case "madd9": return 14; case "6": return 11;
                case "m6": return 12; default: return 1;
            }
        }

        protected static Dictionary<string, double> Dist(CondModel m, string[] labels, string[] ctx)
        {
            if (m == null || m.Tiers.Count == 0) return null;
            Dictionary<string, double> dist = null;
            // Match each MODEL tier to its runtime context value by the tier's stored Context LABEL (not by position),
            // so a model that gained/lost a tier (e.g. a newly added "sec|..." context) still aligns — an older model
            // simply skips labels the runtime now supplies but it never learned. Iterate model order = specific→general.
            for (int i = m.Tiers.Count - 1; i >= 0; i--)
            {
                int li = LabelIndex(labels, m.Tiers[i].Context);
                if (li < 0 || li >= ctx.Length) continue;
                Dictionary<string, double> counts;
                if (!m.Tiers[i].Table.TryGetValue(ctx[li], out counts) || counts.Count == 0) continue;
                double n = 0; foreach (var v in counts.Values) n += v;
                if (n <= 0) continue;
                int d = counts.Count;
                double lambda = n / (n + d);
                var here = new Dictionary<string, double>();
                foreach (var kv in counts) here[kv.Key] = kv.Value / n;
                if (dist == null) { dist = here; continue; }
                var blend = new Dictionary<string, double>();
                foreach (var kv in here) blend[kv.Key] = lambda * kv.Value;
                foreach (var kv in dist) { double cur; blend.TryGetValue(kv.Key, out cur); blend[kv.Key] = cur + (1 - lambda) * kv.Value; }
                dist = blend;
            }
            return dist;
        }
        static int LabelIndex(string[] labels, string label)
        {
            if (labels == null) return -1;
            string want = label ?? "";
            for (int i = 0; i < labels.Length; i++) if (labels[i] == want) return i;
            return -1;
        }

        protected static string Pick(Dictionary<string, double> dist, Random rng)
        {
            if (dist == null || dist.Count == 0) return null;
            double tot = 0; foreach (var v in dist.Values) tot += v;
            double r = rng.NextDouble() * tot;
            foreach (var kv in dist) { r -= kv.Value; if (r <= 0) return kv.Key; }
            return dist.Keys.First();
        }

        protected static int PickInt(Dictionary<string, double> dist, Random rng, int fallback)
        {
            string s = Pick(dist, rng);
            int v; return (s != null && int.TryParse(s, out v)) ? v : fallback;
        }

        protected static int[] ChordPcs(Cell c)
        {
            var notes = PatternGenerator.ChordNotes(((c.Root % 12) + 12) % 12, 0, c.Canon, 0);
            return notes.Select(n => ((n % 12) + 12) % 12).Distinct().ToArray();
        }

        protected static int Mod12(int x) { int r = x % 12; return r < 0 ? r + 12 : r; }

        protected static int PlaceNear(int around, int absPc)
        {
            int up = around + Mod12(absPc - around); int down = up - 12;
            return (up - around) <= (around - down) ? up : down;
        }
        protected static bool InScale(int pitch, int tonicPc, int[] scale) { return Array.IndexOf(scale, Mod12(pitch - tonicPc)) >= 0; }
        protected static int SnapScale(int pitch, int tonicPc, int[] scale)
        {
            for (int d = 0; d <= 6; d++)
            { if (InScale(pitch + d, tonicPc, scale)) return pitch + d; if (InScale(pitch - d, tonicPc, scale)) return pitch - d; }
            return pitch;
        }
        protected static int ScaleStepDir(int pitch, int dir, int tonicPc, int[] scale)
        {
            int step = dir >= 0 ? 1 : -1, p = pitch + step, guard = 0;
            while (!InScale(p, tonicPc, scale) && guard++ < 12) p += step;
            return p;
        }
        protected static int NearestChordTone(int around, int[] chordPcs, int tonicPc)
        {
            int best = around, bd = 999;
            foreach (int cpc in chordPcs)
            {
                int absPc = Mod12(tonicPc + cpc);
                int up = around + Mod12(absPc - around);
                foreach (int cand in new[] { up, up - 12 })
                { int dd = Math.Abs(cand - around); if (dd < bd) { bd = dd; best = cand; } }
            }
            return best;
        }
        protected static int NearestPitch(int around, int pc, int lo, int hi)
        {
            int up = around + Mod12(pc - around); int down = up - 12;
            int chosen = (up - around) <= (around - down) ? up : down;
            while (chosen < lo) chosen += 12; while (chosen > hi) chosen -= 12;
            return chosen;
        }
        protected static int TransposeInScale(int pitch, int steps, int tonicPc, int[] scale)
        {
            int rel = Mod12(pitch - tonicPc);
            int idx = Array.IndexOf(scale, rel);
            if (idx < 0) { int best = 0, bd = 99; for (int i = 0; i < scale.Length; i++) { int dd = Math.Min(Mod12(scale[i] - rel), Mod12(rel - scale[i])); if (dd < bd) { bd = dd; best = i; } } idx = best; }
            int oct = (pitch - tonicPc - rel) / 12;
            int ni = idx + steps;
            int noct = oct + (int)Math.Floor(ni / (double)scale.Length);
            int nrel = scale[((ni % scale.Length) + scale.Length) % scale.Length];
            return tonicPc + nrel + 12 * noct;
        }

        protected static void RollChord(List<OutNote> outNotes, List<int> voiced, int start, int ringEnd, int vel)
        {
            for (int i = 0; i < voiced.Count; i++)
            {
                int s = start + i * 3;
                int len = Math.Max(6, ringEnd - s);
                outNotes.Add(new OutNote { Part = 2, Pitch = voiced[i], Start = s, Len = len, Vel = vel });
            }
        }

        protected static int DevVel(int bar, int devBars, int climaxBar)
        {
            double f = bar <= climaxBar ? (double)bar / Math.Max(1, climaxBar)
                                        : 1.0 - (double)(bar - climaxBar) / Math.Max(1, devBars - climaxBar);
            return Math.Max(1, Math.Min(127, 70 + (int)Math.Round(f * 34)));
        }

        protected static void EmitMelody(List<OutNote> outNotes, List<MelNote> line, int sectionStart, int octave, int vel, int part)
        {
            foreach (var n in line)
                outNotes.Add(new OutNote { Part = part, Pitch = n.Pitch + 12 * octave, Start = sectionStart + n.Start, Len = n.Len, Vel = Math.Max(1, Math.Min(127, AccentVel(vel, n.Start) + n.VelOffset)) });
        }

        protected static void EmitMelody(List<OutNote> outNotes, List<MelNote> line, int sectionStart, int octave, int vel,
                                         bool forceTonicEnd, int tonicPc, int lo, int hi, int part, int holdBars = 1)
        {
            for (int i = 0; i < line.Count; i++)
            {
                var n = line[i];
                int pitch = n.Pitch + 12 * octave;
                int len = n.Len;
                if (forceTonicEnd && i == line.Count - 1)
                {
                    pitch = NearestPitch(pitch, tonicPc, lo, hi);
                    int barStart = ((sectionStart + n.Start) / Bar) * Bar;
                    len = Math.Max(len, barStart + Math.Max(1, holdBars) * Bar - (sectionStart + n.Start)); // held cadence
                }
                outNotes.Add(new OutNote { Part = part, Pitch = pitch, Start = sectionStart + n.Start, Len = len, Vel = Math.Max(1, Math.Min(127, AccentVel(vel, n.Start) + n.VelOffset)) });
            }
        }

        protected static int AccentVel(int baseVel, int start)
        {
            int inBar = start % Bar;
            int v = baseVel + (inBar == 0 ? 8 : (inBar % Beat == 0 ? 3 : -3));
            return Math.Max(1, Math.Min(127, v));
        }

        // representative velocity for a dynamics bucket (inverse of the analyzer's VelBucket)
        static int BucketVel(string b)
        {
            switch (b) { case "pp": return 32; case "p": return 48; case "mp": return 64; case "f": return 96; case "ff": return 112; default: return 80; } // mf
        }

        // a RELATIVE velocity nudge from the corpus Dynamics model (around mf), conditioned on beat-position +
        // section + melodic contour. Level-independent so a reused phrase keeps the section's dynamic arc.
        protected int DynOffset(string section, int start, int contourSign)
        {
            if (model.Dynamics == null || model.Dynamics.Tiers.Count == 0) return 0;
            string beatPos = (start % Beat == 0) ? "on" : "off";
            string cont = contourSign > 0 ? "up" : (contourSign < 0 ? "down" : "flat");
            string b = Pick(Dist(model.Dynamics, new[] { "beat|sec|cont", "beat|sec", "beat", "" }, new[] { beatPos + "|" + section + "|" + cont, beatPos + "|" + section, beatPos, "" }), rng);
            return b == null ? 0 : (int)Math.Round((BucketVel(b) - 80) * 0.35);
        }

        // ============ MIDI + MuseScore export ============
        protected static void WriteMidi(List<OutNote> notes, double bpm, int num, int den, int[] programs, string[] names, string path, double[] volumes = null, bool[] partIsDrum = null)
        {
            const int Ppq = 480; double tps = (double)Ppq / Spq;
            var events = new MidiEventCollection(1, Ppq);
            var meta = events.AddTrack();
            meta.Add(new TempoEvent((int)Math.Round(60_000_000.0 / bpm), 0));
            int denPow = (int)Math.Round(Math.Log(den, 2));
            meta.Add(new TimeSignatureEvent(0, num, denPow, 24, 8));

            for (int part = 0; part < programs.Length; part++)
            {
                bool drum = partIsDrum != null && part < partIsDrum.Length && partIsDrum[part];
                int channel = drum ? 10 : part + 1;
                var tr = events.AddTrack();
                tr.Add(new TextEvent(names[part], MetaEventType.SequenceTrackName, 0));
                if (!drum) tr.Add(new PatchChangeEvent(0, channel, programs[part]));
                double vol = (volumes != null && part < volumes.Length) ? volumes[part] : 1.0;
                tr.Add(new ControlChangeEvent(0, channel, MidiController.MainVolume, Math.Max(1, Math.Min(127, (int)Math.Round(127 * vol)))));
                foreach (var n in notes.Where(x => x.Part == part).OrderBy(x => x.Start))
                {
                    long on = (long)Math.Round(n.Start * tps);
                    int dur = Math.Max(1, (int)Math.Round(n.Len * tps));
                    int pitch = Math.Max(1, Math.Min(127, n.Pitch));
                    int vel = Math.Max(1, Math.Min(127, n.Vel));
                    var noteOn = new NoteOnEvent(on, channel, pitch, vel, dur);
                    tr.Add(noteOn);
                    tr.Add(noteOn.OffEvent);
                }
            }
            events.PrepareForExport();
            MidiFile.Export(path, events);
        }

        protected static void ExportMuseScore(List<OutNote> notes, int num, int den, string title, string path,
                                              int tonicLetter, int tonicAccidental, int mode, int totalSlices, ScoreClefKind[] clefs, int[] progs, string[] names, bool[] partIsDrum = null)
        {
            double totalBeats = totalSlices / (double)Spq;
            var parts = new List<MuseScoreExporter.Part>();
            for (int part = 0; part < progs.Length; part++)
            {
                if (partIsDrum != null && part < partIsDrum.Length && partIsDrum[part]) continue; // drums not notated here
                var ts = new TrackScore
                {
                    Clef = clefs[part],
                    Transpose = 0,
                    TotalBeats = totalBeats,
                    Key = new KeySignature { TonicLetter = tonicLetter, Accidental = tonicAccidental, Mode = mode, FullMode = mode == 1 ? 1 : 0 }
                };
                foreach (var n in notes.Where(x => x.Part == part).OrderBy(x => x.Start))
                    ts.Notes.Add(new ScoreNote { Midi = n.Pitch, StartBeat = n.Start / (double)Spq, Beats = n.Len / (double)Spq });
                parts.Add(new MuseScoreExporter.Part { Name = names[part], Program = progs[part], Score = ts });
            }
            MuseScoreExporter.Export(path, parts, num, den, title);
        }
    }

    // ---- neutral in-memory result of a ComposerV2 run (for loading into the app timeline) ----
    public class V2Note { public int Pitch; public int Start; public int Len; public int Vel; }
    public class V2Part { public int Program; public string Name; public bool IsDrum; public List<V2Note> Notes = new List<V2Note>(); }
    public class V2Piece
    {
        public double Bpm;
        public int MeterNum = 4, MeterDen = 4, TonicLetter, TonicAccidental, TotalSlices;
        public bool Minor;
        public string Title;
        public List<V2Part> Parts = new List<V2Part>();
    }
}
