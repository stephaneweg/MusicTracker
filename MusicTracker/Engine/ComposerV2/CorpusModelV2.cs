using System.Collections.Generic;

namespace MusicTracker.Engine.ComposerV2
{
    /// <summary>
    /// Composer V2 — the serialized corpus model. A *network of low-order, abstract, backed-off*
    /// conditional distributions, organized by the 4-level generative DAG (form → harmony →
    /// melody/texture → expression). Counts are duration-weighted doubles so the runtime can
    /// renormalize and interpolate (Katz/Witten-Bell) across back-off tiers.
    /// JSON-friendly: public auto-properties only, string keys everywhere.
    /// </summary>
    public class CondModel
    {
        public string Name { get; set; }
        public string StateDesc { get; set; }
        /// <summary>Back-off tiers, MOST specific first, LAST = marginal (empty context "").</summary>
        public List<CondTier> Tiers { get; set; } = new List<CondTier>();
    }

    public class CondTier
    {
        /// <summary>Human label of the variables in this tier's context key (e.g. "prevDeg|func|isCt").</summary>
        public string Context { get; set; }
        /// <summary>contextKey -> (nextState -> weight).</summary>
        public Dictionary<string, Dictionary<string, double>> Table { get; set; }
            = new Dictionary<string, Dictionary<string, double>>();
    }

    /// <summary>Dimensions whose distribution genuinely differs by mode (separate major / minor).</summary>
    public class ModeModels
    {
        public int Pieces { get; set; }

        // Level 1 — tonality / modulation (key offsets relative to home).
        public CondModel Tonality { get; set; } = new CondModel();

        // Level 2 — harmony, FACTORED: (a) root-degree motion (order 2), (b) quality-by-degree (order 0).
        public CondModel HarmonyRoot { get; set; } = new CondModel();
        public CondModel QualityByDegree { get; set; } = new CondModel();

        // Level 3 — melody pitch (high-order, chord/metric-conditioned) + small side tables.
        public CondModel Melody { get; set; } = new CondModel();
        // (IntervalByRhythm / IntervalByDegree / LeapResolution removed — redundant with Melody + RhythmCell)
        public CondModel Cadence { get; set; } = new CondModel();            // phrase-final melody degree

        // Level 4 — voice leading (melody vs bass).
        public CondModel VoiceMotion { get; set; } = new CondModel();
        public CondModel VoiceInterval { get; set; } = new CondModel();

        // Aggregates.
        public Dictionary<string, double> DegreeHistogram { get; set; } = new Dictionary<string, double>();
        public double ChordTones { get; set; }
        public double MelodyNotes { get; set; }
        public double DoublingSteps { get; set; }
        public double VoiceSteps { get; set; }
    }

    /// <summary>Per-file detected metadata (for the report / sanity checks).</summary>
    public class PieceInfoV2
    {
        public string File { get; set; }
        public int TonicPc { get; set; }
        public bool Minor { get; set; }
        public string Mode { get; set; }
        public string Character { get; set; }   // melody character: enjouée / modérée / calme
        public double KeyScore { get; set; }
        public double Bpm { get; set; }
        public string Meter { get; set; }
        public int Bars { get; set; }
        public int MelodyNotes { get; set; }
        public bool VelocityUsable { get; set; }
    }

    public class CorpusModelV2
    {
        public int Version { get; set; } = 2;   // v2: per-bar section roles incl. real intro/theme; section in harmony chains
        public int SlicesPerQuarter { get; set; } = MusicMathV2.SlicesPerQuarter;
        public int FilesAnalyzed { get; set; }
        public int MajorPieces { get; set; }
        public int MinorPieces { get; set; }
        public List<string> Skipped { get; set; } = new List<string>();
        public List<PieceInfoV2> Pieces { get; set; } = new List<PieceInfoV2>();

        // ---- import settings (remembered so a model can be re-opened in the dialog and re-analyzed) ----
        public List<string> SourceFolders { get; set; } = new List<string>();
        // per-dimension Markov order chosen at import (harmony + melody dims). Keys: melody, rhythmCell,
        // harmonyRoot, harmonicRhythm, accompCell, accompTone. Missing key → the dimension's default.
        public Dictionary<string, int> Orders { get; set; } = new Dictionary<string, int>();

        // ---- Level 1 root context: distribution of detected MODES (ionian/aeolian/dorian/lydian/...) ----
        public Dictionary<string, double> ModeDistribution { get; set; } = new Dictionary<string, double>();

        // ---- Level 1 root context: distribution of melody CHARACTER (enjouée / modérée / calme) ----
        public Dictionary<string, double> CharacterDistribution { get; set; } = new Dictionary<string, double>();

        // ---- Level 1 — form / phrase (mode-independent) ----
        public CondModel SectionRole { get; set; } = new CondModel();   // order-1 chain over section roles
        public CondModel PhraseLength { get; set; } = new CondModel();  // length class conditioned on section

        // ---- Level 2 — harmonic rhythm (mode-independent) ----
        public CondModel HarmonicRhythm { get; set; } = new CondModel();

        // ---- Level 3 — melodic rhythm + texture (mode-independent) ----
        // (note-to-note MelodyRhythm + BeatOnsetPattern removed — rhythm is modelled as per-beat CELLS, see RhythmCell)
        // per-BEAT rhythm CELL (the note-value figure filling a beat, e.g. "8+8", "16+16+16+16", "q.", "8+q.", "-"),
        // chained over up to 3 previous beats + the beat's index in the bar → multi-beat/bar rhythmic phrasing.
        public CondModel RhythmCell { get; set; } = new CondModel();
        public CondModel Texture { get; set; } = new CondModel();
        public Dictionary<string, double> TextureDensity { get; set; } = new Dictionary<string, double>();
        public Dictionary<string, double> TextureRegister { get; set; } = new Dictionary<string, double>();
        // ACCOMPANIMENT modelled "like the melody" but CHORD-CONSTRAINED:
        //  • AccompCell = per-beat rhythm cells of the accompaniment voice (when notes attack), like RhythmCell;
        //  • AccompTone = the pitch RELATIVE to the current chord — "<chord-tone index>@<octave band>" (e.g. "0@1"
        //    = root, mid octave; "x@1" = a non-chord/passing tone) — chained (order 3) on the chord FUNCTION, so
        //    generation can re-voice the learned figure onto whatever chord is current.
        public CondModel AccompCell { get; set; } = new CondModel();
        public CondModel AccompTone { get; set; } = new CondModel();

        // ---- Level 4 — dynamics + articulation (mode-independent) ----
        public CondModel Dynamics { get; set; } = new CondModel();
        public CondModel AccentByPos { get; set; } = new CondModel();
        public CondModel ArtMelody { get; set; } = new CondModel();
        public CondModel ArtAccomp { get; set; } = new CondModel();
        public CondModel Inversion { get; set; } = new CondModel();   // chord inversion (bass vs root) by function

        // ---- percussion (drum channel 10): instruments + rhythm, by section ----
        public Dictionary<string, double> PercInstruments { get; set; } = new Dictionary<string, double>(); // class -> weight
        public CondModel PercOnset { get; set; } = new CondModel();   // per-beat drum-class pattern, by section
        public int PiecesWithDrums { get; set; }

        // ---- mode-split bundles ----
        public ModeModels Major { get; set; } = new ModeModels();
        public ModeModels Minor { get; set; } = new ModeModels();

        // ---- aggregate stats ----
        public Dictionary<string, double> TempoHistogram { get; set; } = new Dictionary<string, double>();
        public Dictionary<string, double> MeterHistogram { get; set; } = new Dictionary<string, double>();
        public Dictionary<string, double> NctTypes { get; set; } = new Dictionary<string, double>();
    }
}
