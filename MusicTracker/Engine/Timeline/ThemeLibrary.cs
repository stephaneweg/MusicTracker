using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using MusicTracker.Engine;   // RiffNote

namespace MusicTracker.Engine.Timeline
{
    /// <summary>
    /// A curated library of QUALITY seed themes (hand/LLM-authored), keyed by composer FAMILY + STYLE. It is the
    /// extension point of the "structure-first" generator: instead of sampling a Markov walk (which drifts), the
    /// <see cref="Orchestrateur"/> seeds its theme from here when an entry matches, then the existing form / variation /
    /// harmonization machinery develops it. Adding a style = adding JSON, no code. File: Data\themes\theme_library.json,
    /// resolved next to the assembly via <see cref="AppPaths.Local"/>; deserialized with System.Text.Json.
    /// </summary>
    public class ThemeLibrary
    {
        public int Version { get; set; } = 1;
        public List<ThemeEntry> Themes { get; set; } = new List<ThemeEntry>();
        public List<PoolGesture> Pool { get; set; }   // family-shared, intent-tagged variation gestures (picked via SectionPlan.Want)

        static readonly JsonSerializerOptions Opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        /// <summary>The Data\themes directory (next to the exe), where per-family library files + index.json live.</summary>
        public static string ThemesDir => AppPaths.Local(Path.Combine("Data", "themes"));

        /// <summary>Read a FAMILY's library fresh from disk (Data\themes\{family}.json) — no cache (files are small and
        /// editing them between composes is a normal authoring workflow). Empty library if the file is missing/malformed.
        /// Split per family so we never load every style at once.</summary>
        public static ThemeLibrary LoadFamily(string family)
        {
            try
            {
                string path = Path.Combine(ThemesDir, (string.IsNullOrWhiteSpace(family) ? "generic" : family.ToLowerInvariant()) + ".json");
                if (File.Exists(path))
                {
                    var lib = JsonSerializer.Deserialize<ThemeLibrary>(File.ReadAllText(path), Opts);
                    if (lib != null) return lib;
                }
            }
            catch { /* malformed library must never break composition — fall through to empty */ }
            return new ThemeLibrary();
        }

        /// <summary>Best entry for a (composer family, style), preferring one whose mode (major/minor) matches. Style
        /// is matched case-insensitively; an empty requested style matches the first entry of the family. Null = none.</summary>
        public ThemeEntry Find(string family, string style, bool minor, string mood = null, int seed = 0)
        {
            if (Themes == null) return null;
            // family = HARD filter; style/mood/mode = soft, WEIGHTED (style strongest). Always returns a family entry if
            // any exists (TEMPLATE-FIRST: a style with no authored theme still yields a curated piece, never Markov).
            // Among equally-best matches, pick by SEED → diversity when several themes share style+mood (batch phase).
            int bestScore = int.MinValue;
            var best = new List<ThemeEntry>();
            foreach (var t in Themes)
            {
                if (t == null || !string.Equals(t.Composer, family, StringComparison.OrdinalIgnoreCase)) continue;
                int score = 0;
                if (!string.IsNullOrEmpty(style) && string.Equals(t.Style, style, StringComparison.OrdinalIgnoreCase)) score += 4;
                if (!string.IsNullOrEmpty(mood) && string.Equals(t.Mood, mood, StringComparison.OrdinalIgnoreCase)) score += 2;
                if (t.Key != null && t.Key.Minor == minor) score += 1;
                if (score > bestScore) { bestScore = score; best.Clear(); best.Add(t); }
                else if (score == bestScore) best.Add(t);
            }
            if (best.Count == 0) return null;
            return best[(int)(((uint)seed) % (uint)best.Count)];
        }

        // ---- token melody parsing ("<midi[+midi...] or r>/<dur>", '|' = bar separator) ----

        /// <summary>Parse a token melody string into app-space <see cref="RiffNote"/>s (Note = MIDI − 12). Tokens are
        /// whitespace/'|'-separated; each is "&lt;pitches&gt;/&lt;dur&gt;" where pitches is 'r' (rest) or one or more
        /// MIDI numbers joined by '+', and dur is 1/2/4/8/16/32 (optionally suffixed '.' dotted or 't' triplet).
        /// Durations are musical, so <paramref name="spq"/> is the ENGINE resolution (slices per quarter).</summary>
        public static List<RiffNote> ParseMelody(string mel, int spq)
        {
            var notes = new List<RiffNote>();
            if (string.IsNullOrWhiteSpace(mel)) return notes;
            if (spq <= 0) spq = 24;
            int cursor = 0;
            foreach (var raw in mel.Split(new[] { ' ', '\t', '\r', '\n', '|' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int slash = raw.LastIndexOf('/');
                if (slash <= 0 || slash >= raw.Length - 1) continue;
                int len = DurToSlices(raw.Substring(slash + 1), spq);
                if (len <= 0) continue;
                string pitches = raw.Substring(0, slash);
                if (!pitches.Equals("r", StringComparison.OrdinalIgnoreCase))
                    foreach (var p in pitches.Split('+'))
                        if (int.TryParse(p, out int midi))
                            notes.Add(new RiffNote(Clamp95(midi - 12), cursor, len));
                cursor += len;
            }
            return notes;
        }

        static int DurToSlices(string tok, int spq)
        {
            if (string.IsNullOrEmpty(tok)) return 0;
            bool dotted = tok.EndsWith("."); if (dotted) tok = tok.Substring(0, tok.Length - 1);
            bool trip = tok.EndsWith("t"); if (trip) tok = tok.Substring(0, tok.Length - 1);
            if (!int.TryParse(tok, out int d) || d <= 0) return 0;
            int slices = (4 * spq) / d;     // whole note = 4 quarters; "8" = eighth = spq/2, etc.
            if (dotted) slices = slices * 3 / 2;
            if (trip) slices = slices * 2 / 3;
            return slices;
        }

        /// <summary>Transpose every note by <paramref name="semis"/> semitones (clamped to the 0..95 app range).</summary>
        public static List<RiffNote> TransposeNotes(List<RiffNote> notes, int semis)
        {
            if (semis == 0 || notes == null) return notes;
            var o = new List<RiffNote>(notes.Count);
            foreach (var n in notes) o.Add(new RiffNote(Clamp95(n.Note + semis), n.Start, n.Length) { Bend = n.Bend });
            return o;
        }

        internal static int Clamp95(int n) => n < 0 ? 0 : (n > 95 ? 95 : n);
    }

    /// <summary>One curated theme: its identity (composer family + style + name), key/meter, the token melody, the
    /// home harmonic grid, the form to use, optional accompaniment/bass motifs, and a variation-technique order.</summary>
    public class ThemeEntry
    {
        public string Composer { get; set; }      // "ghibli" | "bach" | "vivaldi" | "generic"
        public string Style { get; set; }         // must match one of the composer's Styles strings
        public string Name { get; set; }
        public string Mood { get; set; }          // frozen-vocab mood tag (Enjoué/Tendre/Calme/Méditatif/Mélancolique/Majestueux) — a selection axis
        public ThemeKey Key { get; set; } = new ThemeKey();
        public ThemeMeter Meter { get; set; }     // null = 4/4 (the engine computes barSlices from this)
        public int Tempo { get; set; }
        public int Spq { get; set; } = 24;
        public int ThemeBars { get; set; }
        public string Melody { get; set; }         // token melody (see ThemeLibrary.ParseMelody)
        public ThemeHarmony Harmony { get; set; }
        public string Form { get; set; }           // a Forms template name valid for this composer (advisory)
        public MotifSpec Accomp { get; set; }      // accompaniment degree-motif (phase 2 wiring)
        public MotifSpec Bass { get; set; }        // bass degree-motif (phase 2 wiring)
        public List<int> Variations { get; set; }  // ApplyVariation technique order for variation/dev sections
        public ArrangementPlan Arrangement { get; set; }  // optional authored development RECIPE — overrides the dialog form
        public bool Auto { get; set; }             // true (or no Arrangement) → AUTO-ASSEMBLE the arc from the family pool (diversity)

        /// <summary>Explicit home chords as (rootPc, qualityIndex), transposed by <paramref name="delta"/> semitones so
        /// the library theme follows the chosen key. Empty when the entry has no explicit grid.</summary>
        public List<(int root, int quality)> HomeChords(int delta)
        {
            var o = new List<(int, int)>();
            if (Harmony?.Chords != null)
                foreach (var c in Harmony.Chords) o.Add((((c.Root + delta) % 12 + 12) % 12, c.Q));
            return o;
        }
    }

    public class ThemeKey { public int TonicPc { get; set; } public bool Minor { get; set; } }

    /// <summary>Time signature of a theme. barSlices = Num × (4 × Spq / Den): 4/4 = 96 · 3/4 = 72 · 6/8 = 72 (at Spq 24).</summary>
    public class ThemeMeter { public int Num { get; set; } = 4; public int Den { get; set; } = 4; }

    public class ThemeHarmony
    {
        public int CadenceStyle { get; set; } = -1;          // MusicTheory.Cadence style index (used when Chords is null)
        public List<ChordSpec> Chords { get; set; }          // explicit per-bar grid (absolute root pc + quality index)
    }

    public class ChordSpec { public int Root { get; set; } public int Q { get; set; } }

    /// <summary>A degree-based accompaniment motif as authored in JSON; <see cref="ToChordMotif"/> maps it onto the
    /// engine's <see cref="ChordMotif"/> for per-chord realization.</summary>
    public class MotifSpec
    {
        public int Bars { get; set; } = 1;
        public int Spq { get; set; } = 24;
        public bool OpenVoicing { get; set; } = true;
        public bool Spread { get; set; }
        public bool SmartVoice { get; set; }
        public List<MotifNoteSpec> Notes { get; set; } = new List<MotifNoteSpec>();

        public ChordMotif ToChordMotif()
        {
            var m = new ChordMotif
            {
                Bars = Math.Max(1, Bars),
                SlicesPerQuarter = Spq > 0 ? Spq : 24,
                OpenVoicing = OpenVoicing,
                Morph = false,
                Spread = Spread,
                SmartVoice = SmartVoice,
                Notes = new List<MotifNote>(),
            };
            if (Notes != null)
                foreach (var n in Notes) m.Notes.Add(new MotifNote(Math.Max(1, n.Deg), n.Start, Math.Max(1, n.Len)));
            return m;
        }
    }

    public class MotifNoteSpec { public int Deg { get; set; } = 1; public int Start { get; set; } public int Len { get; set; } }

    /// <summary>An authored DEVELOPMENT RECIPE: the full section arc of the piece (what ghibli_romance encodes by hand),
    /// moved into data so the engine follows the plan instead of its generic defaults. When present on a ThemeEntry it
    /// REPLACES the dialog's form (authoring the arrangement = authoring the form). Each <see cref="SectionPlan"/> says
    /// how that section's material is produced (derive from the theme + transforms, or authored verbatim).</summary>
    public class ArrangementPlan
    {
        public List<SectionPlan> Sections { get; set; } = new List<SectionPlan>();
    }

    /// <summary>One section of the recipe. Material = an authored <see cref="Melody"/>/<see cref="Counter"/> token string
    /// (home key, verbatim — for intros/outros/descants), OR derived from the theme: <see cref="Source"/> "theme" (whole)
    /// or "fragment" (bars <see cref="FragFrom"/>..<see cref="FragTo"/>) put through the ordered <see cref="Ops"/> chain.
    /// "free"/"rest" = no melodic material (accompaniment carries it).</summary>
    public class SectionPlan
    {
        public string Role { get; set; } = "theme";   // intro | theme | restate | develop | climax | bridge | outro
        public int Bars { get; set; } = 8;
        public int Transpose { get; set; } = 0;        // semitones from home for this section's key
        public int Cadence { get; set; } = 1;          // 0 open/half · 1 resolve to tonic · 2 free
        public string Source { get; set; } = "theme";  // theme | fragment | free | rest
        public int FragFrom { get; set; } = 0;         // theme bar range when Source = "fragment"
        public int FragTo { get; set; } = -1;          // -1 = to the end of the theme
        public List<string> Ops { get; set; }          // transform tokens, applied in order (see RecipeRenderer)
        public string Voice { get; set; } = "lead";    // lead | counter | both (both = octave-stacked "à 2")
        public string Melody { get; set; }             // authored verbatim material (home key) — overrides Source/Ops
        public string Counter { get; set; }            // authored counter-melody / descant (home key)
        public List<ChordSpec> Chords { get; set; }    // explicit section grid (home key); null = the theme grid, transposed
        public Intent Want { get; set; }               // when set, the selector picks a matching POOL gesture instead of fixed material
        public double Fresh { get; set; }              // 0..1 freshness cursor: bounded perturbation intensity on the picked gesture
    }

    /// <summary>Intent tags (controlled vocab in index.json) describing a pool gesture, or what a recipe section WANTS.</summary>
    public class Intent
    {
        public string Function { get; set; }
        public string Energy { get; set; }
        public string Register { get; set; }
        public string Density { get; set; }
        public string Flavor { get; set; }
    }

    /// <summary>A reusable, intent-tagged variation gesture in a family's pool. Material = verbatim <see cref="Melody"/>
    /// (home key) OR theme-derived (<see cref="Source"/> + <see cref="Ops"/>). The selector picks by intent; the engine
    /// adapts it to the section's key/chords. The anti-"parrot" variety comes from picking among matches by seed.</summary>
    public class PoolGesture
    {
        public string Id { get; set; }
        public string Kind { get; set; } = "melody";   // melody | accomp | intro | outro | counter | variation
        public string Style { get; set; }              // optional preferred composer STYLE (e.g. "Jazz"); a style-matched gesture is strongly favoured by Select; null = generic (serves any style)
        public string Mood { get; set; }               // optional preferred MOOD (frozen vocab, e.g. "Mélancolique"); favoured by Select as strongly as Style; null = serves any mood
        public Intent Intent { get; set; }
        public int Bars { get; set; } = 4;
        public bool Minor { get; set; }                 // mode of a VERBATIM gesture (intro/outro/counter) — filtered to match the theme
        public ThemeMeter Meter { get; set; }           // meter of a VERBATIM gesture (null = 4/4) — filtered to match the theme
        public string Melody { get; set; }              // verbatim (home key)
        public string Source { get; set; }              // theme | fragment (derived from the seed theme)
        public int FragFrom { get; set; }
        public int FragTo { get; set; } = -1;
        public List<string> Ops { get; set; }
    }
}
