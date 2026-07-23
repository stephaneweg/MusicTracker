using System;
using System.Collections.Generic;
using MusicTracker.Engine.Score;

namespace MusicTracker.Engine.Timeline
{
    /// <summary>One segment of a chord MOTIF: a span (in slices, within one chord's duration) with an ARTICULATION
    /// type — the engine renders the chord tones accordingly. Artic: 0 arpège montant · 1 arpège descendant · 2 Alberti
    /// · 3 accord plaqué · 4 arpégiato (roulé). Sub = note subdivision (slices) for the arpeggio types (croche=12,
    /// double=6, triolet=8).</summary>
    public class MotifSegment
    {
        public int Start;
        public int Length;
        public int Artic;
        public int Sub = 12;
        public MotifSegment() { }
        public MotifSegment(int start, int length, int artic, int sub) { Start = start; Length = length; Artic = artic; Sub = sub; }
    }

    /// <summary>One note of a degree-based accompaniment MOTIF: a SCALE DEGREE above the chord root (1 = root, 2 = next
    /// scale step, 3 = third, … over 2 octaves) with a position + length (in slices). The engine realizes it per chord:
    /// degree → scale tone above the chord root, chord-tone degrees coloured by the chord quality (7th/9th…). </summary>
    public class MotifNote
    {
        public int Degree = 1;   // 1..15 (two diatonic octaves above the chord root; 1 = root)
        public int Start;        // slices from the pattern start
        public int Length;
        public MotifNote() { }
        public MotifNote(int degree, int start, int length) { Degree = degree; Start = start; Length = length; }
    }

    /// <summary>A reusable accompaniment MOTIF, drawn as a little DEGREE pattern (a 2-octave diatonic "riff") over a
    /// chosen number of bars, then STAMPED on every chord of the trame — re-rooted to each chord, coloured by its
    /// quality, voiced, and optionally spread. Null on the arrangement = use the built-in figure. Serialized with the
    /// project.</summary>
    public class ChordMotif
    {
        public int Bars = 1;              // pattern length in bars — it repeats every `Bars` bars, re-rooted per chord
        public int SlicesPerQuarter = 24;
        public bool OpenVoicing = true;
        public bool Morph = true;
        public bool Spread = false;       // éclatement: spread/arpeggiate the realized notes across octaves
        public bool SmartVoice = false;   // "ajustement modal": voice each chord in its closest INVERSION (common tone held)
                                          // and re-map the degrees onto it — chord tones realign, the shape rides the inversion
        public List<MotifNote> Notes = new List<MotifNote>();   // the drawn degree pattern

        // ---- legacy (old articulation-based motifs) — kept so old projects still deserialize; no longer rendered ----
        public int Chords = 1;
        public int UnitSlices = 48;
        public List<MotifSegment> Segments = new List<MotifSegment>();
    }

    /// <summary>One chord of the harmonic "trame" (skeleton), one per bar: a root pitch-class + a quality index
    /// (the PatternGenerator quality table). Serialized with the project (System.Text.Json, IncludeFields).</summary>
    public class ChordCell
    {
        public int Root;       // pitch-class 0..11
        public int Quality;    // PatternGenerator quality index (Maj0 Min1 Sus2=4 Sus4=5 Maj7=6 … add9=13 m(add9)=14 maj9=16)
        public ChordCell() { }
        public ChordCell(int root, int quality) { Root = root; Quality = quality; }
    }

    /// <summary>One section of the arrangement form (intro / theme / re-exposition / development / recap / outro):
    /// where it starts (bar) and how long (bars), plus its ROLE so regeneration knows how to (re)build it.</summary>
    public class ArrSection
    {
        public string Name;    // human label shown on the riff ("Thème", "Ré-exposition"…)
        public string Role;    // "intro" | "theme" | "reexpo" | "dev" | "recap" | "outro"
        public int StartBar;
        public int Bars;
        public Guid MelodyRiffId;   // the riff carrying this section on the MELODY track (regen target)
        public Guid CounterRiffId;  // the riff carrying this section on the COUNTER-MELODY track
        public bool Protected;      // user-locked: "Appliquer le thème" will NOT overwrite this section's riff
        public ArrSection() { }
        public ArrSection(string name, string role, int startBar, int bars) { Name = name; Role = role; StartBar = startBar; Bars = bars; }
    }

    /// <summary>
    /// The persistent "recipe" of a composed piece, kept WITH the project so the arrangement can be edited and
    /// regenerated AFTER composition. It stores the harmonic TRAME (one <see cref="ChordCell"/> per bar), the
    /// section layout, the canonical THEME (the editable source), and the deterministic parameters needed to
    /// re-derive the mechanical parts (bass / accompaniment / re-exposition / development / recap / counter-melody).
    /// All public fields → auto-serialized by System.Text.Json (IncludeFields = true).
    /// </summary>
    public class ComposedArrangement
    {
        public string Composer;            // which composer produced it (e.g. "Ghibli")
        public string ModelFile;           // the V2 corpus model this was built from (null = V1) — lets chord edits
                                           // regenerate the accompaniment IN THE SAME STYLE instead of the generic figure
        public int Seed;
        // the composer's option values (mode, intro/theme/dev/outro bars…) — so a later edit regenerates with the SAME
        // composer + settings ("la timeline référence les options du compositeur").
        public Dictionary<string, int> Options = new Dictionary<string, int>();

        // tonality / meter / grid
        public int TonicPc;
        public int FullMode;               // MusicalMode index (Lydien 6, Majeur 0, …)
        public int MeterNum = 4, MeterDen = 4;
        public int BarSlices = 96;
        public int SlicesPerQuarter = 24;
        public int ChordsPerBar = 1;       // harmonic rhythm: chords per bar (2 = a chord change every 2 beats)
        public int ChordSlices = 96;       // slices per chord (BarSlices / ChordsPerBar) — the harmonic grid
        public int TotalBars;

        // the TRAME: one chord per bar (Chords.Count == TotalBars)
        public List<ChordCell> Chords = new List<ChordCell>();

        // the form
        public List<ArrSection> Sections = new List<ArrSection>();

        // the canonical THEME (the editable source). Notes are RELATIVE to the theme start (slice 0 = theme bar 0).
        public List<RiffNote> Theme = new List<RiffNote>();
        public int ThemeBars;
        public Guid ThemeRiffId;           // the timeline riff that carries the (editable) theme
        public List<int> DevKeys = new List<int>();   // the development's per-repetition key offsets (semitones) — the modulating build-up
        public ChordMotif Motif;                       // LEGACY single global motif (old files) — used as a fallback when no per-section motif matches
        // Per-SECTION accompaniment motifs, keyed by target: "intro" "theme" "reexpo" "dev" (= all variations) "recap"
        // "outro", plus "dev:0".."dev:N-1" (per-variation OVERRIDES). A chord with no matching key uses the legacy
        // Motif, else the built-in figure. Serialized with the project.
        public Dictionary<string, ChordMotif> Motifs = new Dictionary<string, ChordMotif>();
        // Per-SECTION BASS motifs (same keys/grid as Motifs, realized an octave lower). Empty = the default root pedal
        // (one held root per chord). Lets the user ARTICULATE the bass with the same degree editor. Serialized.
        public Dictionary<string, ChordMotif> BassMotifs = new Dictionary<string, ChordMotif>();

        // deterministic regeneration parameters
        public bool OpenVoicing;
        public int Feel;
        public bool Ternary;
        public int MelodyCenter = 72;
        public int LeadInstrument;
        public int CounterInstrument;

        /// <summary>The chords of a section as a (root,quality) tuple list — what the engine helpers consume. Honours
        /// ChordsPerBar (a section spanning N bars carries N×ChordsPerBar chords).</summary>
        public List<(int root, int quality)> SectionChords(ArrSection sec)
        {
            var list = new List<(int, int)>();
            int cpb = Math.Max(1, ChordsPerBar);
            for (int b = sec.StartBar * cpb; b < (sec.StartBar + sec.Bars) * cpb && b < Chords.Count; b++)
                list.Add((Chords[b].Root, Chords[b].Quality));
            return list;
        }

        public ArrSection SectionByRole(string role)
        {
            foreach (var s in Sections) if (s.Role == role) return s;
            return null;
        }

        /// <summary>Bar where the development begins = the earliest "dev" section's StartBar, or -1 if none. The
        /// development may be ONE multi-bar section (legacy projects) or one section PER variation (each ThemeBars
        /// long); the variation index of a dev section is (sec.StartBar - DevStartBar()) / ThemeBars.</summary>
        public int DevStartBar()
        {
            int min = int.MaxValue;
            foreach (var s in Sections) if (s.Role == "dev" && s.StartBar < min) min = s.StartBar;
            return min == int.MaxValue ? -1 : min;
        }
    }
}
