using System;
using System.Collections.Generic;
using MusicTracker.Engine;
using MusicTracker.Engine.Flow;
using MusicTracker.Engine.Score;
using V2 = MusicTracker.Engine.ComposerV2;
using V3 = MusicTracker.Engine.ComposerV3;

namespace MusicTracker.Engine.Timeline
{
    /// <summary>
    /// ORCHESTRATEUR — style-agnostic builder of an editable arrangement. It owns the FORM (which sections, their key
    /// plan + cadence + length) and the HUMEUR (mood); for each section it asks the chosen ComposerV3 EMITTER for the
    /// chords (<see cref="V3.BaseComposerV3.GetChords"/>) and the voice lines (<see cref="V3.BaseComposerV3.GetNote"/>),
    /// applies restatements/variations via <see cref="ArrangementEngine"/>, takes the style's accompaniment + bass +
    /// tempo from the emitter, and assembles a <see cref="ComposedArrangement"/> + timeline tracks. One instance per
    /// corpus model (the model file selects the emitter + carries the learned tables). Replaces the V1 posed-form path.
    /// </summary>
    public sealed class Orchestrateur : MusicComposer
    {
        readonly string modelFile;
        readonly string displayName;
        public override string Name => displayName ?? "Orchestrateur";
        public override string Description =>
            "Structure éditable : une FORME (sonate, ballade, rondeau, ABAC…) bâtit la charpente + le plan harmonique ; le STYLE (émetteur V3) fournit l'âme (thème, accompagnement) selon l'HUMEUR.";

        static readonly string[] V2ToneChoices = { "Majeur", "Mineur", "Mineur harmonique", "Mineur mélodique", "Dorien", "Phrygien", "Lydien", "Mixolydien", "Locrien" };
        static readonly int[] V2ToneModeMap = { 0, 1, 2, 3, 4, 5, 6, 7, 8 };   // combo index → MusicalMode index (Score.MusicalMode) — identity. "Mineur" = naturel (éolien); harmonique/mélodique added. Ionien=Majeur, éolien=Mineur (no duplicate entries).
        static readonly string[] MoodChoices = { "Auto", "Enjoué", "Tendre", "Calme", "Méditatif", "Mélancolique", "Majestueux" };
        // CATALOGUE (théothèque) DÉSACTIVÉ (choix utilisateur 2026-07-05) : le thème est TOUJOURS algorithmique
        // (émetteur GetNote → Viterbi). Plus de graine curée / assemblage auto / accomp curé. Cf. [[algorithmic-composition-nierhaus]].
        // GHIBLI ground-truth mood/style categories (the corpus is reorganized into these folders → the model learns
        // per-category stats). Parallel arrays: dialog label → the exact character TOKEN the analyzer stored (folder
        // name lower-cased, accents kept). Index 0 = Auto (sample the distribution). Other families keep MoodChoices.
        static readonly string[] GhibliMoodChoices = { "Auto", "Calme / nostalgique", "Enjoué / léger", "Solennel / requiem", "Sombre / dramatique", "Valse / dansant", "Épique / majestueux" };
        static readonly string[] GhibliMoodTokens  = { null, "calme_nostalgique", "enjoué_léger", "solennel_requiem", "sombre_dramatique", "valse_dansant", "épique_majestueux" };
        static string FamilyOf(string modelFile)
        {
            try { return V3.ComposerV3Factory.For(modelFile).FamilyKey; } catch { return "generic"; }
        }
        // Theme-development method forced on the DEVELOP section (Auto = the engine's own choice). Parallel arrays: label → op.
        static readonly string[] DevChoices = { "Auto", "Augmentation", "Diminution", "Intervalles élargis", "Rétrograde-inversion", "Fortspinnung (dévidage)", "L-système", "Thue-Morse", "Génétique" };
        static readonly string[] DevOps     = { null,   "augment",      "diminish",   "expand",                "retroinvert",            "spin",                    "grow",       "thuemorse",  "evolve" };

        readonly ComposerOption[] options;
        public override IReadOnlyList<ComposerOption> Options => options;

        /// <summary>The catalogue family this composer resolves to (ghibli / bach / vivaldi / generic).</summary>
        public string Family => FamilyOf(modelFile);

        /// <summary>Indices of the "char"/Humeur choice that HAVE a catalogue variant — so the dialog shows only those
        /// (Auto = 0 always included). Empty = no catalogue for this family → the dialog keeps every choice (legacy path).</summary>
        public System.Collections.Generic.List<int> CatalogueMoodIndices()
        {
            var res = new System.Collections.Generic.List<int>();
            var cat = MotifCatalogue.LoadFamily(FamilyOf(modelFile));
            if (cat?.Variants == null || cat.Variants.Count == 0) return res;   // no catalogue → dialog keeps all
            bool gh = FamilyOf(modelFile) == "ghibli";
            var have = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var v in cat.Variants) if (!string.IsNullOrEmpty(v.Mood)) have.Add(v.Mood);
            int n = gh ? GhibliMoodChoices.Length : MoodChoices.Length;
            res.Add(0);   // Auto always available
            for (int i = 1; i < n; i++)
            {
                string mood = gh ? (i < GhibliMoodTokens.Length ? GhibliMoodTokens[i] : null) : MoodName(i);
                if (!string.IsNullOrEmpty(mood) && have.Contains(mood)) res.Add(i);
            }
            return res;
        }

        public Orchestrateur(string modelFile, string displayName)
        {
            this.modelFile = modelFile;
            this.displayName = displayName;
            var styles = StylesFor(modelFile);
            var moodChoices = FamilyOf(modelFile) == "ghibli" ? GhibliMoodChoices : MoodChoices;
            options = new[]
            {
                new ComposerOption("form", "Forme", Forms.Names(modelFile), 0),
                new ComposerOption("style", "Style", styles, 0),
                new ComposerOption("mode", "Tonalité", V2ToneChoices, 0),    // 0 = Majeur, 1 = Mineur
                new ComposerOption("char", "Humeur", moodChoices, 0),
                new ComposerOption("dev", "Développement", DevChoices, 0),   // 0 = Auto (engine chooses)
            };
        }

        static string[] StylesFor(string modelFile)
        {
            try { var c = V3.ComposerV3Factory.For(modelFile); var s = c.Styles; var a = new string[s.Count]; for (int i = 0; i < s.Count; i++) a[i] = s[i]; return a; }
            catch { return new[] { "Standard" }; }
        }

        // Humeur index (MoodChoices) → emitter CHARACTER (V3.Mood has no Tendre/Méditatif/Mélancolique → nearest).
        static V3.Mood MoodFromOpt(int i)
        {
            switch (i)
            {
                case 1: return V3.Mood.Enjoue;      // Enjoué
                case 2: return V3.Mood.Calme;       // Tendre
                case 3: return V3.Mood.Calme;       // Calme
                case 4: return V3.Mood.Calme;       // Méditatif
                case 5: return V3.Mood.Modere;      // Mélancolique
                case 6: return V3.Mood.Majestueux;  // Majestueux
                default: return V3.Mood.Auto;
            }
        }

        // Humeur index → the frozen-vocab mood STRING for theme selection (0 = Auto = no mood preference).
        static string MoodName(int i) => (i >= 1 && i < MoodChoices.Length) ? MoodChoices[i] : null;

        // PHRASING amount, by style + mood: marked détaché (2) for energetic music (Enjoué/Majestueux) and the
        // inherently-bouncy baroque dances; light, near-legato (1) for lyrical / calm / sad. 0 would be pure legato.
        static int BreathLevelFor(string style, V3.Mood mood)
        {
            if (mood == V3.Mood.Enjoue || mood == V3.Mood.Majestueux) return 2;
            string s = (style ?? "").ToLowerInvariant();
            if (s.Contains("gigue") || s.Contains("bourr") || s.Contains("gavotte") || s.Contains("courante") || s.Contains("vivace")
                || s.Contains("fugue") || s.Contains("toccata")) return 2;
            return 1;   // Berceuse/Ballade/Sarabande/Lent + Calme/Tendre/Méditatif/Mélancolique → light (mostly legato)
        }

        public override ComposeResult Compose(ComposeContext ctx)
        {
            var key = ctx.Key ?? new KeySignature();
            int tonicPc = MusicTheory.TonicPc(key);
            int modeOpt = ctx.Opt("mode", 0);
            int fullMode = (modeOpt >= 0 && modeOpt < V2ToneModeMap.Length) ? V2ToneModeMap[modeOpt] : 0;
            bool minor = MusicalMode.IsMinorish(fullMode);   // modal modes with a ♭3 select minor-tagged themes
            var scale = ScaleSet(tonicPc, MusicalMode.Scale(fullMode));
            var rng = new Random(ctx.Seed);

            // EMITTER + chosen style, and (optionally) a CURATED SEED THEME from the theme library for this composer+style.
            // When found, the form's theme section is sized to the curated theme's bar count.
            var gen = V3.ComposerV3Factory.For(modelFile);
            string style = StyleNameFor(gen, ctx.Opt("style", 0));
            string family = gen != null ? gen.FamilyKey : "generic";
            // GHIBLI: the "char" index selects one of the ground-truth folder CATEGORIES (a token the model learned),
            // passed to the emitter as CharacterTag. For catalog Find, that index does NOT map to the generic mood
            // vocabulary, so Ghibli selects the curated theme mood-agnostically (null). Other families: unchanged.
            bool ghibliMoods = family == "ghibli";
            int charOpt = ctx.Opt("char", 0);
            string charTag = (ghibliMoods && charOpt >= 0 && charOpt < GhibliMoodTokens.Length) ? GhibliMoodTokens[charOpt] : null;
            string findMood = ghibliMoods ? null : MoodName(charOpt);
            var lib = ThemeLibrary.LoadFamily(family);
            var libEntry = lib.Find(family, style, minor, findMood, ctx.Seed);
            // CATALOGUE DÉSACTIVÉ : themeEntry est TOUJOURS null → aucune graine/arrangement/accompagnement curé n'est utilisé,
            // et themeA = gen.GetNote (Viterbi) partout. `libEntry` n'est plus lu que pour un indice NEUTRE (le mètre, ex. valse 3/4).
            bool algoTheme = true;
            var themeEntry = algoTheme ? null : libEntry;
            int wantThemeBars = (themeEntry != null && themeEntry.ThemeBars > 0) ? themeEntry.ThemeBars : (ctx.ThemeBars ?? -1);

            // A curated entry may carry an authored ARRANGEMENT RECIPE — it builds the section arc itself (and overrides
            // the dialog's form). Otherwise the chosen form template drives the sections. `plans` (aligned with the spec)
            // is consulted in the section loop; `delta` (home→chosen key) is set when a seed theme is applied.
            List<SectionPlan> plans = null;
            int delta = 0;
            FormSpec spec;
            // A family with a POOL → AUTO-ASSEMBLE every theme (seeded pick + affinity + recombination + seamless), so the
            // same theme yields a different piece per seed. The inline arrangement, if any, is the fallback for pool-less families.
            bool wantsAuto = themeEntry != null && !string.IsNullOrWhiteSpace(themeEntry.Melody) && lib.Pool != null && lib.Pool.Count > 0;
            if (wantsAuto)
                spec = RecipeRenderer.BuildSpec(RecipeRenderer.AutoPlan(themeEntry, lib.Pool, ctx.Seed, new HashSet<string>(), style, ctx.ThemeReps ?? 2), out plans);
            else if (themeEntry != null && themeEntry.Arrangement != null && themeEntry.Arrangement.Sections != null && themeEntry.Arrangement.Sections.Count > 0)
                spec = RecipeRenderer.BuildSpec(themeEntry.Arrangement, out plans);
            else
                spec = Forms.ApplySizes(Forms.Get(modelFile, ctx.Opt("form", 0)),
                                        wantThemeBars, ctx.IntroBars ?? -1, ctx.OutroBars ?? -1, ctx.ThemeReps ?? -1);
            // DÉVELOPPEMENT forcé (dialog "Développement", Auto = laisser le moteur choisir) → impose la méthode sur la section de développement.
            // DÉVELOPPEMENT forcé (dialog "Développement", Auto = null): the variation SELECTION stays as usual (recombine /
            // pool variations / theme per section), and the chosen method is applied as a POST-transform ON TOP of each
            // section's reconstructed melody (in RenderSection) — so the arc keeps its variety instead of becoming the same
            // transformed theme everywhere. Intro/outro/counter gestures are left untouched.
            int devOpt = ctx.Opt("dev", 0);
            string devOp = (devOpt > 0 && devOpt < DevOps.Length) ? DevOps[devOpt] : null;
            V3.Mood mood = MoodFromOpt(ctx.Opt("char", 0));

            // METER from the caller (dialog) — wins; a seeded theme's meter only applies when none was forced.
            int meterNum = ctx.MeterNum > 0 ? ctx.MeterNum : 4;
            int meterDen = ctx.MeterDen > 0 ? ctx.MeterDen : 4;
            if (ctx.MeterNum <= 0 && libEntry != null && libEntry.Meter != null && libEntry.Meter.Num > 0 && libEntry.Meter.Den > 0)
            { meterNum = libEntry.Meter.Num; meterDen = libEntry.Meter.Den; }
            // Think in FELT BEATS: 24 slices per beat, one measure = beatsPerBar beats. Compound (x/8) = ternary → the
            // felt beat is the dotted-quarter (beatsPerBar = Num/3); simple (x/4) = binary. So the bar aligns with the
            // ruler (RulerBeatsPerBar) instead of an eighth-count length. 6/8=2·24=48 · 9/8=72 · 12/8=96 · 4/4=96 · 3/4=72.
            int beatsPerBar = meterDen == 8 ? Math.Max(1, meterNum / 3) : Math.Max(1, meterNum);
            int barSlices = beatsPerBar * Spq, chordSlices = barSlices;
            double barBeats = barSlices / (double)Spq;
            int total = 0; foreach (var s in spec.Sections) total += Math.Max(1, s.Bars);
            int lim = total * barSlices;
            int tonicQ = ArrangementEngine.DiatonicTonicQuality(fullMode);

            // NEW "melodic-line" model: if this family has a rhythm/articulation CATALOGUE for the chosen mood+meter, the
            // melody/counter/bass become MelodicLineModule skeletons (pitches derived from the chord grid by the engine) and
            // chords/pad become custom-articulated Patterns — the pitch-reconstruction machinery below is skipped. Families
            // without a catalogue keep the legacy riff path.
            var lineVar = MotifCatalogue.LoadFamily(family).Pick(ghibliMoods ? charTag : findMood, meterNum, meterDen, ctx.Seed);
            bool useLines = lineVar != null && ctx.GenerateMusic;

            var result = new ComposeResult
            {
                ResultMeterNum = meterNum,
                ResultMeterDen = meterDen,
                ResultKey = new KeySignature { TonicLetter = key.TonicLetter, Accidental = key.Accidental, Mode = minor ? 1 : 0, FullMode = fullMode },
            };
            result.MelodicLineMode = useLines;   // the emission path below wires the custom chord/pad tracks itself

            // The learned model is the emitter's resource — but a model-FREE composer (Mathématique) has no file:
            // tolerate a missing model with an empty one (its emitter ignores the tables).
            V2.CorpusModelV2 model2; try { model2 = V2.ComposerV2Runtime.LoadModel(modelFile); } catch { model2 = new V2.CorpusModelV2(); }
            bool genOwn = gen != null && gen.GeneratesOwnMelody;

            // a fresh EmitContext for a given seed (the model is the emitter's internal resource)
            Func<int, V3.EmitContext> Emit = seed => new V3.EmitContext
            { Model = model2, Seed = seed, Minor = minor, TonicPc = tonicPc, MeterNum = 4, MeterDen = 4, Mood = mood, CharacterTag = charTag, Style = style };
            Func<string, int, V3.SectionContext> Sec = (role, cad) => new V3.SectionContext { Role = role, Cad = cad };

            Func<List<(int root, int quality)>, int, List<(int root, int quality)>> Transpose =
                (chs, semis) => chs.ConvertAll(c => ((((c.root + semis) % 12) + 12) % 12, c.quality));
            Func<List<(int root, int quality)>, int, List<(int root, int quality)>> Fit = (chs, bars) =>
            {
                var o = new List<(int root, int quality)>();
                if (chs.Count == 0) { for (int i = 0; i < bars; i++) o.Add((((tonicPc % 12) + 12) % 12, tonicQ)); return o; }
                for (int i = 0; i < bars; i++) o.Add(chs[i % chs.Count]);
                return o;
            };
            Action<List<(int root, int quality)>, int, int> Cad = (chs, cad, lt) =>
            {
                if (chs.Count == 0) return;
                if (cad == 0) chs[chs.Count - 1] = (((lt + 7) % 12 + 12) % 12, 5);          // V sus4 = "question"
                else if (cad == 1) chs[chs.Count - 1] = (((lt % 12) + 12) % 12, tonicQ);     // tonic = "answer"
            };

            // STYLE MATERIALS: theme A (its chords + melody), and theme B if the form needs a 2nd theme.
            int themeAbars = 4; foreach (var s in spec.Sections) if (s.Role == FormRole.ThemeA) { themeAbars = Math.Max(2, s.Bars); break; }
            var themeAChords = new List<(int root, int quality)>(gen.GetChords(Emit(ctx.Seed), Sec("theme", 2), themeAbars, 1));
            var themeA = gen.GetNote(Emit(ctx.Seed), Sec("theme", 1), V3.Staff.Lead, themeAChords, themeAbars, null);

            // SEED THEME OVERRIDE — the whole piece derives from themeA + themeAChords (every section transposes/varies/
            // refits them), so replacing them with a curated motif is the single graft point: form, key-plan, variation
            // and accompaniment machinery all run unchanged on the better seed. Transposed to the chosen key.
            if (themeEntry != null && !string.IsNullOrWhiteSpace(themeEntry.Melody))
            {
                delta = ((tonicPc - (themeEntry.Key != null ? themeEntry.Key.TonicPc : tonicPc)) % 12 + 12) % 12;
                var seeded = ThemeLibrary.TransposeNotes(ThemeLibrary.ParseMelody(themeEntry.Melody, Spq), delta);
                if (seeded.Count > 0)
                {
                    themeA = seeded;
                    var libCh = themeEntry.HomeChords(delta);
                    if (libCh.Count == 0 && themeEntry.Harmony != null && themeEntry.Harmony.CadenceStyle >= 0)
                        libCh = MusicTheory.Cadence(key, 1, themeAbars, themeEntry.Harmony.CadenceStyle, ctx.Seed);   // generated grid (no explicit chords)
                    if (libCh.Count > 0) themeAChords = Fit(libCh, themeAbars);
                }
            }
            // A 2nd library theme (same family/style/mood/key) for CROSS-THEME recombination ("un bout de A, un bout de B"):
            // parsed + transposed to the chosen key; null if the library has no distinct alternative for this combo.
            List<RiffNote> recombTheme = null;
            if (themeEntry != null && lib != null)
            {
                var entB = lib.Find(family, style, minor, findMood, ctx.Seed + 137);
                if (entB != null && !ReferenceEquals(entB, themeEntry) && !string.IsNullOrWhiteSpace(entB.Melody))
                {
                    int dB = ((tonicPc - (entB.Key != null ? entB.Key.TonicPc : tonicPc)) % 12 + 12) % 12;
                    var pb = ThemeLibrary.TransposeNotes(ThemeLibrary.ParseMelody(entB.Melody, Spq), dB);
                    if (pb.Count > 0) recombTheme = pb;
                }
            }
            bool needB = spec.Sections.Exists(s => s.Role == FormRole.ThemeB || s.Role == FormRole.RestateB);
            int themeBbars = 8; foreach (var s in spec.Sections) if (s.Role == FormRole.ThemeB) { themeBbars = Math.Max(2, s.Bars); break; }
            List<(int root, int quality)> themeBChords = null; List<RiffNote> themeB = null;
            if (needB)
            {
                themeBChords = new List<(int root, int quality)>(gen.GetChords(Emit(ctx.Seed + 100), Sec("theme", 2), themeBbars, 1));
                themeB = gen.GetNote(Emit(ctx.Seed + 100), Sec("theme", 1), V3.Staff.Lead, themeBChords, themeBbars, null);
            }

            var prog = new List<(int root, int quality)>();
            var mel = new List<RiffNote>();
            var counter = new List<RiffNote>();
            var sections = new List<ArrSection>();
            var segs = new List<KeyValuePair<string, List<(int root, int quality)>>>();
            int barCur = 0, devSeed = 0, varIdx = 0;
            var poolRecent = new HashSet<string>();   // piece-scoped soft anti-repeat for pool picks

            for (int si = 0; si < spec.Sections.Count; si++)
            {
                var sec = spec.Sections[si];
                var plan = (plans != null && si < plans.Count) ? plans[si] : null;
                int bars = Math.Max(1, sec.Bars);
                int shift = plan != null ? plan.Transpose : FormSpec.Semis(sec.Key, minor);
                int lt = ((tonicPc + shift) % 12 + 12) % 12;
                int off = barCur * barSlices;
                string label = V2Label(sec.Role);

                // ---- CHORDS (form key-plan/cadence + emitter colour) ----
                List<(int root, int quality)> ch;
                if (plan != null)
                {
                    // recipe: the section's own grid (authored, home key → chosen key), else the seed theme's grid
                    List<(int root, int quality)> baseCh;
                    if (plan.Chords != null && plan.Chords.Count > 0)
                    {
                        baseCh = new List<(int root, int quality)>();
                        foreach (var c in plan.Chords) baseCh.Add((((c.Root + delta) % 12 + 12) % 12, c.Q));
                    }
                    else baseCh = themeAChords;
                    ch = Fit(Transpose(baseCh, shift), bars);
                }
                else switch (sec.Role)
                {
                    case FormRole.ThemeA:
                    case FormRole.RestateA:
                    case FormRole.Recap:
                    case FormRole.Variation:
                    case FormRole.Develop:
                        ch = Fit(Transpose(themeAChords, shift), bars); break;
                    case FormRole.ThemeB:
                    case FormRole.RestateB:
                        ch = Fit(Transpose(themeBChords ?? themeAChords, shift), bars); break;
                    case FormRole.Intro:
                        ch = Fit(Transpose(new List<(int root, int quality)>(gen.GetChords(Emit(ctx.Seed + 1 + barCur), Sec("intro", 2), bars, 1)), shift), bars); break;
                    case FormRole.Outro:
                        ch = Fit(Transpose(new List<(int root, int quality)>(gen.GetChords(Emit(ctx.Seed + 2 + barCur), Sec("outro", 2), bars, 1)), shift), bars); break;
                    default: // Transition
                        ch = Fit(Transpose(new List<(int root, int quality)>(gen.GetChords(Emit(ctx.Seed + 3 + barCur), Sec("body", 2), bars, 1)), shift), bars); break;
                }
                Cad(ch, plan != null ? plan.Cadence : sec.Cad, lt);
                if (barCur == 0 && ch.Count > 0) ch[0] = (((tonicPc % 12) + 12) % 12, tonicQ);   // open on the home tonic
                prog.AddRange(ch);
                segs.Add(new KeyValuePair<string, List<(int root, int quality)>>(label, ch));

                // ---- MELODY (soul) ---- (melodic-line mode derives the pitches from the chord grid → skip reconstruction)
                if (!useLines)
                if (plan != null)
                {
                    if (plan.Want != null) plan = RecipeRenderer.ResolveWant(plan, lib.Pool, ctx.Seed + si, poolRecent, minor, meterNum, meterDen, style, libEntry != null ? libEntry.Mood : null);
                    RecipeRenderer.RenderSection(plan, themeA, themeAChords, ch, shift, lt, delta, off, lim,
                                                 mel, counter, scale, tonicPc, chordSlices, barSlices, Spq, rng, recombTheme, devOp);
                }
                else if (genOwn && sec.Role != FormRole.Outro && sec.Role != FormRole.Transition)
                {
                    var dest = sec.Dialogue && (sec.Role == FormRole.RestateA || sec.Role == FormRole.Develop) ? counter : mel;
                    foreach (var n in gen.GetNote(Emit(ctx.Seed + 50 + barCur), Sec(label, sec.Cad), V3.Staff.Lead, ch, bars, null))
                        ArrangementEngine.AddAt(dest, n, off, lim);
                }
                else switch (sec.Role)
                {
                    case FormRole.Intro:
                    {
                        var im = gen.GetNote(Emit(ctx.Seed + 11 + barCur), Sec("intro", 1), V3.Staff.Lead, ch, bars, null);
                        if (im.Count > 0 && themeA.Count > 0) { var last = im[im.Count - 1]; int tgt = themeA[0].Note + 12; int lead = ScaleStep(tgt, (last.Note + 12) <= tgt ? -1 : 1, scale) - 12; im[im.Count - 1] = new RiffNote(ArrangementEngine.Clamp95(lead), last.Start, last.Length); }
                        foreach (var n in im) ArrangementEngine.AddAt(mel, n, off, lim);
                        break;
                    }
                    case FormRole.ThemeA:
                    {
                        var t = new List<RiffNote>(themeA); ArrangementEngine.EndOn(t, sec.Cad == 0 ? (lt + 2) % 12 : lt, scale);
                        foreach (var n in t) ArrangementEngine.AddAt(mel, n, off, lim);
                        break;
                    }
                    case FormRole.RestateA:
                    case FormRole.Recap:
                    {
                        var t = ArrangementEngine.TransposeMelLocal(themeA, shift); ArrangementEngine.EndOn(t, sec.Cad == 0 ? (lt + 2) % 12 : lt, scale);
                        var dest = sec.Dialogue ? counter : mel;
                        foreach (var n in t) ArrangementEngine.AddAt(dest, n, off, lim);
                        break;
                    }
                    case FormRole.ThemeB:
                    case FormRole.RestateB:
                    {
                        var t = ArrangementEngine.TransposeMelLocal(themeB ?? themeA, shift); ArrangementEngine.EndOn(t, sec.Cad == 0 ? (lt + 2) % 12 : lt, scale);
                        foreach (var n in t) ArrangementEngine.AddAt(mel, n, off, lim);
                        break;
                    }
                    case FormRole.Variation:
                    {
                        var raw = ArrangementEngine.ApplyVariation(gen.ForcedVariationTech(), varIdx, themeA, themeAChords, themeAChords, chordSlices, barSlices, scale, tonicPc, rng);
                        ArrangementEngine.EndOn(raw, sec.Cad == 0 ? (tonicPc + 2) % 12 : tonicPc, scale);
                        var v = ArrangementEngine.TransposeMelLocal(raw, shift);
                        varIdx++;
                        foreach (var n in v) ArrangementEngine.AddAt(mel, n, off, lim);
                        break;
                    }
                    case FormRole.Develop:
                        DevelopSection(sec, themeA, themeAbars, ch, shift, lt, off, lim, mel, counter, scale, gen, Emit, Sec, minor, tonicPc, rng, ref devSeed);
                        break;
                    default: break;   // Transition / Outro: accompaniment carries it
                }

                sections.Add(new ArrSection(sec.Label, label, barCur, bars));
                barCur += bars;
            }

            // SEAMLESS (Phase C): fold >octave leaps between consecutive melody/counter notes so section joins flow.
            RecipeRenderer.SmoothLeaps(mel);
            RecipeRenderer.SmoothLeaps(counter);
            // PHRASING: the templates are pure pitch+rhythm; add "breathing" (phrase-end breaths + détaché) so the line
            // speaks instead of running on. Level is STYLE/MOOD-dependent (legato for calm/lyrical, marked for energetic).
            int breathLvl = BreathLevelFor(style, mood);
            RecipeRenderer.Breathe(mel, barSlices, Spq, breathLvl, rng);
            RecipeRenderer.Breathe(counter, barSlices, Spq, breathLvl, rng);
            // LEVÉE / anacrusis: a RISING pickup (1 beat on even seeds, 1/2 beat on odd) into the theme's entry, for impulse.
            int themeStartBar = -1;
            foreach (var s in sections) if (s.Role == "theme") { themeStartBar = s.StartBar; break; }
            int pickupSlices = ((ctx.Seed & 1) == 0) ? Spq : Spq / 2;
            if (themeStartBar > 0) RecipeRenderer.AddPickup(mel, themeStartBar * barSlices, scale, pickupSlices, Spq);
            // INTRO TIMBRE: the intro PROPER plays on the ACCOMPANIMENT instrument (it "sets up" before the theme), but the
            // LEVÉE stays on the LEAD so the MELODY itself picks up into the theme. Split at the levée onset.
            var introNotes = new List<RiffNote>();
            if (themeStartBar > 0 && ctx.GenerateMusic)
            {
                int introEnd = themeStartBar * barSlices;
                int pickupStart = Math.Max(0, introEnd - pickupSlices);   // the levée occupies [pickupStart, introEnd)
                foreach (var n in mel) if (n.Start < pickupStart) introNotes.Add(n);
                mel.RemoveAll(n => n.Start < pickupStart);   // keep the levée (+ theme onward) on the lead line
            }

            // DIALOGUE: the 2nd instrument enters after the theme's first bar (question/réponse) and converses with the
            // lead throughout the body — handing the line back and forth and harmonizing — instead of only doubling at the
            // climax. Runs over [theme … outro); additive (leaves any restate/climax counterpoint intact). After SmoothLeaps
            // so handed-off bars already flow; before SmoothLeaps(counter)? No — re-smooth the counter after, the handoff
            // can introduce a seam between an existing counter note and a freshly handed-off bar.
            if (ctx.GenerateMusic && themeStartBar >= 0)
            {
                int dlgEndBar = total; foreach (var s in sections) if (s.Role == "outro") { dlgEndBar = s.StartBar; break; }
                ArrangementEngine.BuildDialogue(mel, counter, themeStartBar, dlgEndBar, barSlices, prog, tonicPc, scale, rng);
                RecipeRenderer.SmoothLeaps(counter);
            }

            // ---- ACCOMPANIMENT + BASS + INSTRUMENTS + TEMPO from the STYLE ----
            string charTok = MoodTok(mood);
            var backing = gen.MakeBacking(model2, ctx.Seed + 5, minor, tonicPc, charTok, segs);
            // CURATED ACCOMP/BASS — a library entry's degree-motifs override the Markov backing, realized on the real trame.
            var accompNotes = backing.Accomp;
            var bassNotes = backing.Bass;
            if (themeEntry != null)   // algo-theme mode keeps the Markov backing (MakeBacking) instead of the curated motifs
            {
                if (themeEntry.Accomp != null && themeEntry.Accomp.Notes != null && themeEntry.Accomp.Notes.Count > 0)
                {
                    var r = ArrangementEngine.RenderMotifOverProgression(themeEntry.Accomp.ToChordMotif(), prog, tonicPc, fullMode, chordSlices, barSlices, 48);
                    if (r.Count > 0) accompNotes = r;
                }
                if (themeEntry.Bass != null && themeEntry.Bass.Notes != null && themeEntry.Bass.Notes.Count > 0)
                {
                    var r = ArrangementEngine.RenderMotifOverProgression(themeEntry.Bass.ToChordMotif(), prog, tonicPc, fullMode, chordSlices, barSlices, 36);
                    if (r.Count > 0) bassNotes = r;
                }
            }
            var acc = new Riff { Name = "Accompagnement", Notes = accompNotes, LengthSlices = lim, SlicesPerQuarter = Spq };
            var bass = new Riff { Name = "Basse", Notes = bassNotes, LengthSlices = lim, SlicesPerQuarter = Spq };
            var pad = HisaishiComposer.PadChords(prog, chordSlices, 0, total, 3);

            double styleBpm = backing.Bpm;
            double baseBpm = (ctx.Bpm.HasValue && ctx.Bpm.Value > 0 && Math.Abs(ctx.Bpm.Value - 60) > 0.5) ? ctx.Bpm.Value : styleBpm;
            result.ResultBpm = baseBpm;
            result.ResultTempo = new List<(double, double)> { (0, baseBpm) };
            int outroStartBar = total; foreach (var s in sections) if (s.Role == "outro") { outroStartBar = s.StartBar; break; }

            // METER-AWARE rhythm cleanup: the emitter generates its rhythm in 4/4 internally, so re-time the model
            // melody + counter onto a clean BINARY/TERNARY rhythm that matches the arrangement's real meter and thins out
            // the (over-frequent) sixteenths. Skipped for styles that figure their own line (e.g. Vivaldi's motor 16ths).
            if (ctx.GenerateMusic && !genOwn)
            {
                mel = global::MusicTracker.Engine.Compose.MeterRhythm.Reflow(mel, barSlices, meterDen == 8, ctx.Seed ^ 0x51EE);
                counter = global::MusicTracker.Engine.Compose.MeterRhythm.Reflow(counter, barSlices, meterDen == 8, ctx.Seed ^ 0x7A2B);
            }
            int leadInst = backing.LeadProgram, counterInst = backing.CounterProgram, accompInst = backing.AccompProgram, bassInst = backing.BassProgram;
            int padInst = 49;
            // Dialog instrument overrides (melodic-line mode): melody / accompaniment / pad.
            if (ctx.MelodyInstrument >= 0) leadInst = ctx.MelodyInstrument;
            if (ctx.AccompInstrument >= 0) accompInst = ctx.AccompInstrument;
            if (ctx.PadInstrument >= 0) padInst = ctx.PadInstrument;
            Guid themeRiffId = Guid.Empty;
            if (useLines)
            {
                // NEW model: melody/counter/bass = MelodicLine skeletons, chords/pad = custom Patterns, all from the catalogue.
                OrchestrateurLines.Emit(result.Tracks, prog, sections, lineVar, beatsPerBar,
                    leadInst, counterInst, accompInst, bassInst, padInst,
                    ctx.CounterSameStaff, ctx.IncludeCounter, ctx.IncludePad, ctx.IncludeBass, ctx.Seed);
            }
            else
            {
                var melRiff = new Riff { Name = "Mélodie", Notes = mel, LengthSlices = lim, SlicesPerQuarter = Spq };
                var counterRiff = new Riff { Name = "Contre-chant", Notes = counter, LengthSlices = lim, SlicesPerQuarter = Spq };
                if (!ctx.GenerateMusic) { melRiff.Notes = new List<RiffNote>(); counterRiff.Notes = new List<RiffNote>(); }
                var melRiffs = AddSectionRiffs(result.Tracks, leadInst, "Mélodie", melRiff, sections, barSlices, result.Riffs);
                var counterRiffs = ctx.IncludeCounter
                    ? AddSectionRiffs(result.Tracks, counterInst, "Contre-chant", counterRiff, sections, barSlices, result.Riffs)
                    : new List<Riff>();
                if (ctx.IncludePad)
                {
                    AddBarRiffs(result.Tracks, 49, "Cordes (nappe)", pad, total, barSlices, result.Riffs);
                    result.Tracks[result.Tracks.Count - 1].Volume = 0.35;
                }
                AddBarRiffs(result.Tracks, accompInst, "Accompagnement", acc, total, barSlices, result.Riffs);
                result.Tracks[result.Tracks.Count - 1].Volume = 0.30;
                if (ctx.IncludeBass)
                    AddBarRiffs(result.Tracks, bassInst, "Basse", bass, total, barSlices, result.Riffs);
                if (ctx.IncludeIntroMelody && introNotes.Count > 0)   // intro played by the ACCOMPANIMENT instrument (set-up before the lead's theme)
                    AddBarRiffs(result.Tracks, accompInst, "Intro", new Riff { Name = "Intro", Notes = introNotes, LengthSlices = lim, SlicesPerQuarter = Spq }, total, barSlices, result.Riffs);
                for (int i = 0; i < sections.Count; i++)
                {
                    Riff melR = i < melRiffs.Count ? melRiffs[i] : null;
                    Riff cntR = i < counterRiffs.Count ? counterRiffs[i] : null;
                    bool melHas = melR != null && melR.Notes != null && melR.Notes.Count > 0;
                    bool cntHas = cntR != null && cntR.Notes != null && cntR.Notes.Count > 0;
                    Riff target;
                    if (sections[i].Role == "reexpo") target = cntHas ? cntR : (melHas ? melR : (cntR ?? melR));
                    else                              target = melHas ? melR : (melR ?? cntR);
                    if (target != null) sections[i].MelodyRiffId = target.Id;
                    if (cntR != null) sections[i].CounterRiffId = cntR.Id;
                }
                int ti = sections.FindIndex(s => s.Role == "theme");
                themeRiffId = (ti >= 0 && ti < melRiffs.Count) ? melRiffs[ti].Id : Guid.Empty;
            }

            double outroStartBeat = outroStartBar * barBeats, endBeat = total * barBeats;
            if (outroStartBar < total)
                foreach (var tr in result.Tracks)
                    tr.VolumeAutomation = new List<VolumePoint>
                    {
                        new VolumePoint { Beat = outroStartBeat, Volume = tr.Volume },
                        new VolumePoint { Beat = endBeat, Volume = tr.Volume * 0.15 },
                    };

            result.Arrangement = new ComposedArrangement
            {
                Composer = "Orchestrateur",
                ModelFile = modelFile,
                Seed = ctx.Seed,
                Options = new Dictionary<string, int> { { "form", ctx.Opt("form", 0) }, { "style", ctx.Opt("style", 0) }, { "mode", ctx.Opt("mode", 0) }, { "char", ctx.Opt("char", 0) } },
                TonicPc = ((tonicPc % 12) + 12) % 12,
                FullMode = fullMode, MeterNum = meterNum, MeterDen = meterDen,
                BarSlices = barSlices, SlicesPerQuarter = Spq, ChordsPerBar = 1, ChordSlices = chordSlices, TotalBars = total,
                Chords = prog.ConvertAll(c => new ChordCell(((c.root % 12) + 12) % 12, c.quality)),
                Sections = sections,
                Theme = new List<RiffNote>(themeA),
                ThemeBars = themeAbars,
                ThemeRiffId = themeRiffId,
                DevKeys = new List<int>(),
                OpenVoicing = true, Feel = 1, Ternary = false, MelodyCenter = 72,
                LeadInstrument = leadInst, CounterInstrument = counterInst,
            };
            if (themeEntry != null && themeEntry.Accomp != null) result.Arrangement.Motif = themeEntry.Accomp.ToChordMotif();   // edit/regen uses the curated accomp too
            return result;
        }

        // Map a form role to the section-texture label (and the arrangement role tag used for riff linking).
        static string V2Label(FormRole r)
        {
            switch (r)
            {
                case FormRole.Intro: return "intro";
                case FormRole.ThemeA: case FormRole.ThemeB: return "theme";
                case FormRole.RestateA: case FormRole.RestateB: return "reexpo";
                case FormRole.Develop: return "dev";
                case FormRole.Recap: return "recap";
                case FormRole.Outro: return "outro";
                default: return "body";   // Transition / Variation → plain body texture
            }
        }

        static string StyleNameFor(V3.BaseComposerV3 gen, int idx)
        {
            try { var s = gen.Styles; return (idx >= 0 && idx < s.Count) ? s[idx] : (s.Count > 0 ? s[0] : null); } catch { return null; }
        }

        static string MoodTok(V3.Mood m)
        {
            switch (m) { case V3.Mood.Calme: return "calme"; case V3.Mood.Enjoue: return "enjouee"; case V3.Mood.Majestueux: return "majestueux"; case V3.Mood.Modere: return "moderee"; default: return null; }
        }

        // DEVELOPMENT: reuse theme A as a question/answer + "together" dialogue, OR a fresh THIRD theme over the SAME chords.
        void DevelopSection(FormSection sec, List<RiffNote> themeA, int themeAbars, List<(int root, int quality)> ch,
                            int shift, int lt, int off, int lim, List<RiffNote> mel, List<RiffNote> counter, HashSet<int> scale,
                            V3.BaseComposerV3 gen, Func<int, V3.EmitContext> Emit, Func<string, int, V3.SectionContext> Sec,
                            bool minor, int tonicPc, Random rng, ref int devSeed)
        {
            const int barSlices = 96;
            int repSlices = themeAbars * barSlices;
            int reps = Math.Max(1, sec.Bars / themeAbars);

            if (sec.ThirdTheme)
            {
                var freshCh = new List<(int root, int quality)>(gen.GetChords(Emit(devSeed + 1000), Sec("theme", 2), themeAbars, 1));
                var fresh = gen.GetNote(Emit(200 + (devSeed++)), Sec("theme", 1), V3.Staff.Lead, freshCh, themeAbars, null);
                for (int r = 0; r < reps; r++)
                {
                    int from = r * themeAbars, len = Math.Min(themeAbars, Math.Max(0, ch.Count - from));
                    var repCh = len > 0 ? ch.GetRange(from, len) : ch;
                    var m = ArrangementEngine.RefitTheme(fresh, repCh, barSlices, scale); ArrangementEngine.EndOn(m, lt, scale);
                    foreach (var n in m) ArrangementEngine.AddAt(mel, n, off + r * repSlices, lim);
                }
                return;
            }

            var themeT = ArrangementEngine.TransposeMelLocal(themeA, shift);
            var devMode = new int[reps];
            for (int r = 0; r < reps; r++)
            {
                bool last = r == reps - 1;
                if (sec.Dialogue && reps >= 3 && last) devMode[r] = 2;
                else if (sec.Dialogue && reps >= 4 && r > 0 && !last && rng.Next(100) < 30) devMode[r] = 2;
                else devMode[r] = sec.Dialogue ? (r % 2) : 0;
            }
            int overlap = barSlices / 2;
            for (int r = 0; r < reps; r++)
            {
                int up = Math.Min(r, 3);
                var rep = themeT.ConvertAll(n => new RiffNote(ArrangementEngine.Clamp95(ShiftScale(n.Note + 12, up, scale) - 12), n.Start, n.Length));
                int o = off + r * repSlices;
                int from = r * themeAbars, len = Math.Min(themeAbars, Math.Max(0, ch.Count - from));
                var repCh = len > 0 ? ch.GetRange(from, len) : ch;
                if (devMode[r] == 2)
                {
                    ArrangementEngine.EndOn(rep, lt, scale);
                    foreach (var n in rep) ArrangementEngine.AddAt(mel, n, o, lim);
                    foreach (var n in ArrangementEngine.BuildTogetherCounter(rep, repCh, themeAbars, barSlices, lt, scale, rng)) ArrangementEngine.AddAt(counter, n, o, lim);
                }
                else
                {
                    bool answer = devMode[r] == 1;
                    ArrangementEngine.EndOn(rep, answer ? lt : (lt + 2) % 12, scale);
                    var dest = answer ? counter : mel;
                    foreach (var n in rep) ArrangementEngine.AddAt(dest, n, o, lim);
                    bool handoff = r + 1 < reps && devMode[r + 1] != 2 && devMode[r + 1] != devMode[r];
                    if (handoff && rep.Count > 0)
                    {
                        var nch = ch[Math.Min((r + 1) * themeAbars, ch.Count - 1)];
                        var npcs = ChordPcs(nch.root, nch.quality);
                        int lp = rep[rep.Count - 1].Note + 12;
                        int cp = npcs.Count > 0 ? NearestChord(lp, npcs) : lp;
                        ArrangementEngine.AddAt(dest, new RiffNote(ArrangementEngine.Clamp95(cp - 12), repSlices, overlap), o, lim);
                    }
                }
            }
        }
    }
}
