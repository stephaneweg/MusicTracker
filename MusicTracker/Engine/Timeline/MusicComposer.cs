using System;
using System.Collections.Generic;
using System.Linq;
using MusicTracker.Engine;
using MusicTracker.Engine.Flow;
using MusicTracker.Engine.Score;

namespace MusicTracker.Engine.Timeline
{
    /// <summary>One declared option of a composer — the generic compose dialog builds a labelled combo from it.</summary>
    public class ComposerOption
    {
        public string Key;        // identifier read back in ComposeContext (e.g. "mode")
        public string Label;      // shown in the dialog (e.g. "Mode")
        public string[] Choices;  // combo entries
        public int Default;       // default selected index
        public ComposerOption(string key, string label, string[] choices, int def = 0)
        { Key = key; Label = label; Choices = choices; Default = def; }
    }

    /// <summary>Everything a composer needs to write a piece: the current key/meter, a seed, and the chosen options.</summary>
    public class ComposeContext
    {
        public KeySignature Key;
        public int MeterNum = 4, MeterDen = 4;
        public int Seed;
        public IReadOnlyDictionary<string, int> Options;
        public int Opt(string key, int def = 0) => (Options != null && Options.TryGetValue(key, out int v)) ? v : def;

        // Explicit STRUCTURE overrides (set by the dedicated "Créer structure" dialog; null = fall back to the
        // option-index values). Let the structure be specified as plain numbers rather than {2,4,8,16} combos.
        public double? Bpm;        // chosen base tempo (the composer's tempo map is scaled to it)
        public int? IntroBars;
        public int? ThemeBars;
        public int? ThemeReps;     // how many times the theme is restated in the development (the "variations")
        public int? OutroBars;
        public bool GenerateMusic = true;   // false = only the harmonic bed (accompaniment + bass + pad), no melody/counter
        // Optional tracks (true = include). Let the "Créer structure" dialog trim the arrangement to taste.
        public bool IncludePad = true;          // string pad ("Cordes (nappe)")
        public bool IncludeBass = true;         // bass line
        public bool IncludeCounter = true;      // counter-melody ("Contre-chant")
        public bool IncludeIntroMelody = true;  // melodic intro (played by the accompaniment before the theme)
        public bool CounterSameStaff = false;   // melodic-line mode: counter as voice 1 of the melody (same staff) vs its own line track
        public int MelodyInstrument = -1;       // InstrumentCatalog index override (-1 = the style's default lead)
        public int AccompInstrument = -1;       // -1 = style default accompaniment
        public int PadInstrument = -1;          // -1 = style default pad (strings)
    }

    /// <summary>The composer's output: the tracks + their riffs, and OPTIONALLY a key the timeline should adopt
    /// (when the chosen options changed the tonality or mode — e.g. a Dorian/Lydian Hisaishi piece).</summary>
    public class ComposeResult
    {
        public List<TimelineTrack> Tracks = new List<TimelineTrack>();
        public List<Riff> Riffs = new List<Riff>();
        public bool MelodicLineMode;   // true = tracks already carry the custom chord/pad Patterns (skip BuildChordAccompaniment/BuildNappeChords)
        public KeySignature ResultKey;        // null = leave the project key unchanged
        public double ResultBpm;              // 0 = leave the tempo unchanged
        public int ResultMeterNum, ResultMeterDen; // 0 = leave the time signature unchanged (the timeline adopts it otherwise)
        public List<(double beat, double bpm)> ResultTempo; // optional per-section tempo map (beat → bpm); overrides ResultBpm if set
        public ComposedArrangement Arrangement; // optional persistent "recipe" (chord trame + sections + theme) for later editing/regeneration
    }

    /// <summary>
    /// Base class for the auto-composers. A concrete subclass models ONE composer/style: it DECLARES its options
    /// (<see cref="Options"/>) and implements <see cref="Compose"/> with its own formulas. The shared musical
    /// utilities (scales, intervals, chord tones, weighted/Markov sampling, per-bar riff assembly, breathing,
    /// phrasing) live here so every composer reuses them. Register concrete composers in <see cref="MusicComposers"/>.
    /// </summary>
    public abstract class MusicComposer
    {
        public abstract string Name { get; }
        public virtual string Description => "";
        public abstract IReadOnlyList<ComposerOption> Options { get; }
        public abstract ComposeResult Compose(ComposeContext ctx);

        internal const int Spq = 24; // slices per quarter (canonical grid)

        // ---------- scales / intervals ----------
        internal static HashSet<int> ScaleSet(int tonicPc, int[] offsets)
        { var s = new HashSet<int>(); foreach (var o in offsets) s.Add((((tonicPc + o) % 12) + 12) % 12); return s; }

        internal static int ScaleStep(int midi, int dir, HashSet<int> scale)
        { int m = midi + dir, g = 0; while (!scale.Contains(((m % 12) + 12) % 12) && g++ < 12) m += dir; return m; }

        internal static int ShiftScale(int midi, int steps, HashSet<int> scale)
        { int dir = steps >= 0 ? 1 : -1, n = Math.Abs(steps), m = midi; for (int i = 0; i < n; i++) m = ScaleStep(m, dir, scale); return m; }

        internal static int NearestPc(int cur, int pc)
        { for (int r = 0; r < 12; r++) { if ((((cur + r) % 12) + 12) % 12 == pc) return cur + r; if ((((cur - r) % 12) + 12) % 12 == pc) return cur - r; } return cur; }

        internal static int NearestScale(int from, HashSet<int> scale)
        { for (int d = 0; d < 7; d++) { if (scale.Contains((((from + d) % 12) + 12) % 12)) return from + d; if (scale.Contains((((from - d) % 12) + 12) % 12)) return from - d; } return from; }

        // Keep a leap within maxLeap semitones, folding within an octave then snapping to a nearby scale tone.
        internal static int CapLeap(int prev, int note, HashSet<int> scale, int maxLeap)
        {
            while (note - prev > 12) note -= 12; while (prev - note > 12) note += 12;
            if (Math.Abs(note - prev) > maxLeap) note = NearestScale(prev + Math.Sign(note - prev) * maxLeap, scale);
            return note;
        }

        // ---------- chords ----------
        protected static int[] ChordTones(int rootPc, int octave, int quality) => PatternGenerator.ChordNotes(rootPc, octave, quality, 0);

        internal static HashSet<int> ChordPcs(int rootPc, int quality)
        { var set = new HashSet<int>(); foreach (var n in PatternGenerator.ChordNotes(rootPc, 4, quality, 0)) set.Add(((n % 12) + 12) % 12); return set; }

        internal static int NearestChord(int cur, HashSet<int> pcs)
        { for (int r = 0; r < 12; r++) { if (pcs.Contains(((cur + r) % 12 + 12) % 12)) return cur + r; if (pcs.Contains(((cur - r) % 12 + 12) % 12)) return cur - r; } return cur; }

        // The next pitch strictly above `p` whose pitch-class is in the chord (the next chord tone up).
        protected static int NextChordToneUp(int p, HashSet<int> pcs)
        { for (int d = 1; d <= 12; d++) if (pcs.Contains((((p + d) % 12) + 12) % 12)) return p + d; return p + 12; }

        // The next chord tone at least a MINOR THIRD above `p` (skips 2nds) — for SIMULTANEOUS stacks, so two stacked
        // notes are never a harsh major/minor 2nd apart (a 9th/2nd colour tone is opened up instead of voiced closed).
        internal static int NextChordToneUpOpen(int p, HashSet<int> pcs)
        { for (int d = 3; d <= 14; d++) if (pcs.Contains((((p + d) % 12) + 12) % 12)) return p + d; return p + 12; }

        // The next chord tone at least a MINOR THIRD BELOW `p` (open spacing downward) — mirror of NextChordToneUpOpen.
        internal static int NextChordToneDownOpen(int p, HashSet<int> pcs)
        { for (int d = 3; d <= 14; d++) if (pcs.Contains((((p - d) % 12) + 12) % 12)) return p - d; return p - 12; }

        // A COLOUR-TONE pitch-class of the chord (its 7th/9th — beyond the root/3rd/5th triad), or -1 if a plain triad.
        protected static int ColorTone(int rootPc, int quality, Random rng)
        { var ch = ChordTones(rootPc, 4, quality); return ch.Length <= 3 ? -1 : ch[3 + rng.Next(ch.Length - 3)] % 12; }

        // The TOP (held) note of an `n`-note rising chord-tone figure starting at `start`.
        protected static int FigureTop(int start, HashSet<int> pcs, int n)
        { int c = start; for (int k = 1; k < n; k++) c = NextChordToneUp(c, pcs); return c; }

        // ---------- voice-leading table (shared) ----------
        // VL[origin degree 0-11, origin inversion 0-2, next degree 0-11] = the inversion (0=root, 1=3rd, 2=5th
        // position) to START the next chord's broken-chord figure on, so its bass moves the LEAST from the origin's
        // bass; on a tie, the inversion whose TOP note moves least. Built once from generic triads on each chromatic
        // degree (root & 5th are quality-independent; the 3rd is ±1 — barely changes the choice — and callers play the
        // ACTUAL chord tones at the chosen position). 12 chromatic degrees cover diatonic AND modal (bIII/bVI/bVII).
        protected static readonly int[,,] VoiceLeadTable = BuildVoiceLeadTable();
        static int[,,] BuildVoiceLeadTable()
        {
            int[] tri = { 0, 4, 7 };
            var t = new int[12, 3, 12];
            for (int od = 0; od < 12; od++)
                for (int oi = 0; oi < 3; oi++)
                {
                    int origStart = 60 + ((od + tri[oi]) % 12);
                    var odPcs = new HashSet<int> { od % 12, (od + 4) % 12, (od + 7) % 12 };
                    int origTop = FigureTop(origStart, odPcs, 3);
                    for (int nd = 0; nd < 12; nd++)
                    {
                        var ndPcs = new HashSet<int> { nd % 12, (nd + 4) % 12, (nd + 7) % 12 };
                        int bestNi = 0, bestDist = int.MaxValue, bestTopDist = int.MaxValue;
                        for (int ni = 0; ni < 3; ni++)
                        {
                            int cand = NearestPc(origStart, (nd + tri[ni]) % 12), candTop = FigureTop(cand, ndPcs, 3);
                            int dist = Math.Abs(cand - origStart), td = Math.Abs(candTop - origTop);
                            if (dist < bestDist || (dist == bestDist && td < bestTopDist)) { bestDist = dist; bestTopDist = td; bestNi = ni; }
                        }
                        t[od, oi, nd] = bestNi;
                    }
                }
            return t;
        }

        // ---------- weighted / Markov sampling ----------
        // `flat` is a flattened {value, weight, value, weight, ...} table; returns a value with probability ∝ weight.
        internal static int PickWeighted(int[] flat, Random rng)
        {
            int tot = 0; for (int i = 1; i < flat.Length; i += 2) tot += flat[i];
            int r = rng.Next(Math.Max(1, tot)), acc = 0;
            for (int i = 0; i < flat.Length; i += 2) { acc += flat[i + 1]; if (r < acc) return flat[i]; }
            return flat.Length > 0 ? flat[0] : 0;
        }

        // ---------- assembly ----------
        // Split a full-piece line into ONE editable Riff PER BAR and add a track that plays them back-to-back (sounds
        // identical to one long riff, but each bar is independently editable). Empty bars stay full-bar rests.
        protected static void AddBarRiffs(List<TimelineTrack> tracks, int instrument, string name, Riff full, int totalBars, int barSlices, List<Riff> newRiffs)
        {
            var tr = new TimelineTrack { Type = TimelineTrackType.Instrument, Instrument = instrument, Name = name };
            for (int b = 0; b < totalBars; b++)
            {
                int lo = b * barSlices, hi = lo + barSlices;
                var barNotes = new List<RiffNote>();
                if (full != null) foreach (var n in full.Notes)
                    if (n.Start >= lo && n.Start < hi)
                        barNotes.Add(new RiffNote(n.Note, n.Start - lo, Math.Max(1, Math.Min(n.Length, hi - n.Start))));
                var br = new Riff { Name = name + " m." + (b + 1), Notes = barNotes, LengthSlices = barSlices, SlicesPerQuarter = Spq };
                newRiffs?.Add(br);
                tr.Items.Add(new TimelineItem { Module = new PlayRiffModule { RiffId = br.Id } });
            }
            tracks.Add(tr);
        }

        // Split a full-piece line into ONE editable Riff PER SECTION (intro / theme / re-expo / dev / recap / outro)
        // instead of per bar — so the THEME is a single multi-bar riff one can edit by hand, and each derived section
        // is its own riff (regenerated on propagation). Returns the created riffs in section order (so the caller can
        // grab e.g. the theme riff's Id). Empty sections become a full rest of that length.
        protected static List<Riff> AddSectionRiffs(List<TimelineTrack> tracks, int instrument, string name, Riff full, List<ArrSection> sections, int barSlices, List<Riff> newRiffs)
        {
            var tr = new TimelineTrack { Type = TimelineTrackType.Instrument, Instrument = instrument, Name = name };
            var made = new List<Riff>();
            foreach (var sec in sections)
            {
                int lo = sec.StartBar * barSlices, hi = lo + sec.Bars * barSlices, len = sec.Bars * barSlices;
                var secNotes = new List<RiffNote>();
                if (full != null) foreach (var n in full.Notes)
                    if (n.Start >= lo && n.Start < hi)
                        secNotes.Add(new RiffNote(n.Note, n.Start - lo, Math.Max(1, Math.Min(n.Length, hi - n.Start))));
                var rr = new Riff { Name = name + " - " + sec.Name, Notes = secNotes, LengthSlices = len, SlicesPerQuarter = Spq };
                newRiffs?.Add(rr); made.Add(rr);
                tr.Items.Add(new TimelineItem { Module = new PlayRiffModule { RiffId = rr.Id } });
            }
            tracks.Add(tr);
            return made;
        }

        // Harmonic rhythm: how many chords per bar. A chord CHANGE every 2 beats (= 2/bar) for 4/4 and 12/8; ONE chord
        // per bar otherwise (2/4, 3/4, 6/8, 9/8…).
        internal static int ChordsPerBar(int meterNum, int meterDen)
        {
            if (meterDen == 4) return meterNum == 4 ? 2 : 1;     // 4/4 → 2 · 2/4, 3/4 → 1
            if (meterDen == 8) return meterNum == 12 ? 2 : 1;    // 12/8 → 2 · 6/8, 9/8 → 1
            return 1;
        }

        // BREATHING: turn a legato line into one that articulates and breathes. level 1 = light, 2 = marked. (a) détaché
        // (shorten some notes so a small gap precedes the next), (b) drop the occasional short weak-beat note (a rest),
        // (c) clearly shorten phrase-ending notes. Total length is preserved (gaps become silence).
        protected static void AddBreathing(Riff r, int beatsPerBar, int barSlices, Random rng, int level)
        {
            if (level <= 0 || r == null || r.Notes.Count == 0) return;
            int six = Spq / 4;
            double detach = level >= 2 ? 0.33 : 0.18, restP = level >= 2 ? 0.12 : 0.05;
            var outl = new List<RiffNote>(r.Notes.Count);
            for (int i = 0; i < r.Notes.Count; i++)
            {
                var n = r.Notes[i];
                bool weak = (n.Start % Spq) != 0;
                int barIdx = barSlices > 0 ? n.Start / barSlices : 0;
                bool phraseEnd = (barIdx % 4 == 3) && ((n.Start % barSlices) + n.Length >= barSlices - six);
                if (weak && n.Length <= six * 2 && rng.NextDouble() < restP && (outl.Count == 0 || outl[outl.Count - 1].End >= n.Start)) continue;
                int len = n.Length;
                if (phraseEnd && len > six * 2) len = Math.Max(six * 2, len / 2);
                else if (len > six && rng.NextDouble() < detach) len = Math.Max(six, len - six);
                outl.Add(new RiffNote(n.Note, n.Start, len));
            }
            if (outl.Count > 0) r.Notes = outl;
        }
    }

    /// <summary>Registry of the available composers shown in the compose dialog. DYNAMIC: one entry per corpus
    /// model file found in Data\models\ (rescanned on each access, so a freshly-analyzed model appears at once).
    /// The generator KIND is inferred from the file name (vivaldi / clavier / bach / else Ghibli); the display
    /// name is the prettified file name. (The old hand-written V1 Ghibli/Hisaishi composers are no longer listed.)</summary>
    public static class MusicComposers
    {
        public static IReadOnlyList<MusicComposer> All => Build();

        // SPIKE — expose ONLY the template-backed generators (a family that has a Data\themes\{family}.json curated
        // library). This hides the raw V3/Markov models + Mathématique while the data-driven theme-library path is the
        // focus. Set TemplateOnly = false to restore the full generator list.
       

        static List<MusicComposer> Build()
        {
            var list = new List<MusicComposer>();
            string dir = MusicTracker.Engine.ComposerV2.ComposerV2Runtime.ModelsDir;
            if (!System.IO.Directory.Exists(dir)) return list;
            var files = System.IO.Directory.GetFiles(dir, "*.json").OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();


            // TEMPLATE-ONLY: one entry per FAMILY that has a curated theme library; pick the model whose emitter exposes
            // the MOST styles (the richest — e.g. Bach SOLO's 9 dances over the Clavier's Prélude/Fugue, so authored
            // dance styles are reachable in the dialog).
            var pickFile = new Dictionary<string, string>();
            var pickStyles = new Dictionary<string, int>();
            foreach (var f in files)
            {
                string file = System.IO.Path.GetFileName(f);
                string family = MusicTracker.Engine.ComposerV3.ComposerV3Factory.For(file).FamilyKey;
                if (!System.IO.File.Exists(System.IO.Path.Combine(ThemeLibrary.ThemesDir, family + ".json"))) continue;
                int sc = 0; try { sc = MusicTracker.Engine.ComposerV3.ComposerV3Factory.For(file).Styles.Count; } catch { }
                if (!pickFile.ContainsKey(family) || sc > pickStyles[family]) { pickFile[family] = file; pickStyles[family] = sc; }
            }
            foreach (var kv in pickFile) list.Add(new Orchestrateur(kv.Value, kv.Key == "generic" ? "Générique" : PrettyName(kv.Value)));
            list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            return list;
        }

        // file name → a readable dialog label, e.g. "bach_solo_model_v2.json" → "Bach solo"
        static string PrettyName(string file)
        {
            string n = System.IO.Path.GetFileNameWithoutExtension(file);
            n = n.Replace("_model_v2", "").Replace("_model", "").Replace("_v2", "").Replace('_', ' ').Trim();
            return n.Length == 0 ? System.IO.Path.GetFileNameWithoutExtension(file) : char.ToUpper(n[0]) + n.Substring(1);
        }
    }
}
