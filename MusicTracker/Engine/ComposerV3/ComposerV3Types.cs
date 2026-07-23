using System.Collections.Generic;
using MusicTracker.Engine.ComposerV2;   // CorpusModelV2

namespace MusicTracker.Engine.ComposerV3
{
    /// <summary>The chosen MOOD ("humeur") — a root-context bias on tempo / density / sub-style, shared by every
    /// ComposerV3. Maps to the corpus character tokens. <see cref="Mood.Auto"/> = let the model sample it.</summary>
    public enum Mood { Auto, Calme, Modere, Enjoue, Majestueux }

    /// <summary>Which voice a <see cref="BaseComposerV3.GetNote"/> call should emit.</summary>
    public enum Staff { Lead, Counter, Accompaniment, Bass }

    /// <summary>The composition-wide context handed to a ComposerV3 emitter: the learned model (its internal
    /// resource), the key/meter, the seed, the chosen mood and style. Style-agnostic — the Orchestrateur fills it.</summary>
    public sealed class EmitContext
    {
        public CorpusModelV2 Model;
        public int Seed;
        public bool Minor;
        public int TonicPc;
        public int[] Scale;          // null → the diatonic major/minor scale for the mode
        public int MeterNum = 4, MeterDen = 4;
        public Mood Mood = Mood.Auto;
        public string CharacterTag;  // explicit ground-truth character token (e.g. "calme_nostalgique"); overrides Mood when set
        public string Style;         // one of the composer's Styles (null → its default)
    }

    /// <summary>The per-section context: its form role, key area, cadence intent, and whether it carries NEW material
    /// (sampled from the model) or a VARIATION of a given theme. Supplied by the Orchestrateur for each section.</summary>
    public sealed class SectionContext
    {
        public string Role = "body";   // "intro"|"theme"|"reexpo"|"dev"|"recap"|"outro"|"body"
        public int KeyShiftSemis;      // section key area relative to home tonic
        public int Cad;                // 0 = open (question) / 1 = resolve (answer) / 2 = free
        public bool IsVariation;
        public bool NewMaterial;       // e.g. a fresh theme (rondeau couplet) → sample the model
        public int VarIndex;           // which variation (for the catalogue / cycling)
        public int MeasureCount = 1;
        public int ChordsPerMeasure = 1;
    }
}
