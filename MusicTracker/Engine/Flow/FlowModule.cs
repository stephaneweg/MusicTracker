using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Text.Json.Serialization;

namespace MusicTracker.Engine.Flow
{
    /// <summary>
    /// A node in the music flow graph. Modules are connected output->input to control the flow:
    /// a module "completes" then fires all its output connections (multiple = parallel branches).
    /// </summary>
    [JsonDerivedType(typeof(PlayRiffModule), "PlayRiff")]
    [JsonDerivedType(typeof(PatternGeneratorModule), "Pattern")]
    [JsonDerivedType(typeof(DrumPatternModule), "DrumKit")]
    [JsonDerivedType(typeof(CadenceModule), "Cadence")]
    [JsonDerivedType(typeof(MelodicLineModule), "MelodicLine")]
    public abstract class FlowModule : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnChanged(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

        public Guid Id { get; set; } = Guid.NewGuid();

        double x, y;
        public double X { get { return x; } set { if (x != value) { x = value; OnChanged(nameof(X)); } } }
        public double Y { get { return y; } set { if (y != value) { y = value; OnChanged(nameof(Y)); } } }

        // Optional box width in px (0 = auto). Set at import so a node's width is proportional to the
        // number of measures it spans (Play-riff, Pause). Persisted with the graph.
        double widthHint;
        public double WidthHint { get { return widthHint; } set { if (widthHint != value) { widthHint = value; OnChanged(nameof(WidthHint)); } } }

        // Collapsed = show only the title + a short summary (uniform height). New nodes start expanded;
        // imported nodes start collapsed. Persisted with the graph.
        bool collapsed;
        public bool Collapsed { get { return collapsed; } set { if (collapsed != value) { collapsed = value; OnChanged(nameof(Collapsed)); } } }

        [JsonIgnore] public abstract string Title { get; }
        [JsonIgnore] public virtual bool HasInput { get { return true; } }
        /// <summary>Labels of the output ports (most modules have one; Repeat has body + next).</summary>
        [JsonIgnore] public virtual string[] OutputPorts { get { return new[] { "out" }; } }
    }

    public class PlayRiffModule : FlowModule
    {
        Guid riffId;
        public Guid RiffId { get { return riffId; } set { if (riffId != value) { riffId = value; OnChanged(nameof(RiffId)); } } }
        [JsonIgnore] public override string Title { get { return "Play riff"; } }
    }

   

    /// <summary>
    /// Generates a looping accompaniment riff on the fly from a chord (root + octave + quality +
    /// inversion) played in a chosen rhythmic style (block / arpeggio / jazz / rock-pop-blues).
    /// Like Play-riff it's a TIMED node: at play time it builds a <see cref="Riff"/> (see
    /// <see cref="PatternGenerator"/>) and feeds a RiffPlayer, so it reuses the contextual
    /// instrument/tempo/transpose/volume/MIDI-out/export machinery. Looping is built in via Repeats.
    /// All selection fields are int indices into the name tables in <see cref="PatternGenerator"/>.
    /// </summary>
    public class PatternGeneratorModule : FlowModule
    {
        int root = 0;        // pitch class 0..11 (0 = C/Do)
        int octave = 4;      // C4 = MIDI 60
        int quality = 0;     // index into PatternGenerator.QualityNames
        int inversion = 0;   // 0 = root position, 1/2/3 = inversions
        int style = 0;       // index into PatternGenerator.StyleNames
        int beatsPerBar = 4; // time signature numerator (3 = waltz, etc.)
        int repeats = 1;     // how many bars to generate (built-in looping)
        bool bass = false;   // add the chord root in the bass (works with the built-in styles, no "Personnalisé" needed)
        bool bassPerBeat = false; // bass: one note per BEAT (true) vs one held note per MEASURE (false, default)
        int heldMode = 0;    // "arpège + tenue" styles, the held note: 0 = top note, 1 = full chord (plaqué),
                             // 2 = root + fifth, 3 = root + third (lighter voicings)
        int climbMode = 0;   // "arpège + tenue" climb: 0 = ascending arpeggio, 1 = Alberti (1-5-3…), 2 = mix (alternate)
        bool halveDurations = false; // "arpège + tenue": halve every value (croches → doubles-croches, etc.)
        int degree = -1;     // -1 = absolute (use Root/Quality as-is). 0..6 = LOCKED to a diatonic degree of the
                             // project key: Root/Quality are re-selected from the key, so changing/transposing the
                             // tonality auto-updates this chord without editing it.
        int voiceLeadMode = 0; // renversement auto d'après l'accord PRÉCÉDENT sur la piste: 0 off (Inversion manuelle) /
                               // 1 auto (mouvement mini) / 2 basse proche / 3 haut proche. Baked into Inversion by the revoice pass.
        int diatonicColour = 0; // degree-locked PRIMARY colour: 0 triade / 1 sixte / 2 7e / 3 9e(7+9) / 4 9e(add9)
        int suspension = 0;     // degree-locked SECONDARY colour: 0 none / 1 sus2 / 2 sus4 (replaces the 3rd)
        int modeOverride = 0;   // degree-locked mode: 0 auto (diatonic) / 1 force major / 2 force minor
        bool openVoicing = false; // spread the chord (close C-E-G → open C-G-E') — the inversion still sets the bass
        // ---- optional MELODIC CELL (a 2nd voice attached to this chord, on its own staff) ----
        int melodicOctave = 5;    // the melodic cell's octave, INDEPENDENT of the chord's octave
        int melodicAnchor = 0;    // 0 = degree 1 is the chord ROOT (tonic) / 1 = degree 1 follows the INVERSION (root/3rd/5th/7th…)
        bool melodicOpenVoicing = false; // spread stacked melodic notes
        int melodicVoiceLead = 0; // auto voice-leading of the melodic cell across chords (0 = off)
        bool melodicPreserve = false; // "Préserver": exclude this chord's cell when applying the cell to the whole section

        public int Root { get { return root; } set { if (root != value) { root = value; OnChanged(nameof(Root)); } } }
        public int Degree { get { return degree; } set { int v = value < 0 ? -1 : Math.Min(6, value); if (degree != v) { degree = v; OnChanged(nameof(Degree)); } } }
        public bool Bass { get { return bass; } set { if (bass != value) { bass = value; OnChanged(nameof(Bass)); } } }
        public bool BassPerBeat { get { return bassPerBeat; } set { if (bassPerBeat != value) { bassPerBeat = value; OnChanged(nameof(BassPerBeat)); } } }
        public int HeldMode { get { return heldMode; } set { int v = Math.Max(0, Math.Min(3, value)); if (heldMode != v) { heldMode = v; OnChanged(nameof(HeldMode)); } } }
        public int ClimbMode { get { return climbMode; } set { int v = Math.Max(0, Math.Min(3, value)); if (climbMode != v) { climbMode = v; OnChanged(nameof(ClimbMode)); } } }
        public bool HalveDurations { get { return halveDurations; } set { if (halveDurations != value) { halveDurations = value; OnChanged(nameof(HalveDurations)); } } }
        /// <summary>Transient: the chord-voice index for a "single held note" (arp styles), set by the cadence for
        /// voice-leading. -1 = default (top). Not serialized — recomputed per render.</summary>
        [JsonIgnore] public int HeldVoiceOverride { get; set; } = -1;
        /// <summary>Transient: starting cell index for "mixte" climb, so the pattern ROTATES across a cadence's
        /// chords (each chord is generated separately). Set by the cadence = the chord index. Not serialized.</summary>
        [JsonIgnore] public int PatternCellOffset { get; set; }
        public int Octave { get { return octave; } set { if (octave != value) { octave = value; OnChanged(nameof(Octave)); } } }
        public int Quality { get { return quality; } set { if (quality != value) { quality = value; OnChanged(nameof(Quality)); } } }
        public int Inversion { get { return inversion; } set { int v = Math.Max(0, value); if (inversion != v) { inversion = v; OnChanged(nameof(Inversion)); } } }
        public int VoiceLeadMode { get { return voiceLeadMode; } set { int v = Math.Max(0, Math.Min(3, value)); if (voiceLeadMode != v) { voiceLeadMode = v; OnChanged(nameof(VoiceLeadMode)); } } }
        public int DiatonicColour { get { return diatonicColour; } set { int v = Math.Max(0, Math.Min(MusicTheory.DiatonicColourNames.Length - 1, value)); if (diatonicColour != v) { diatonicColour = v; OnChanged(nameof(DiatonicColour)); } } }
        public int Suspension { get { return suspension; } set { int v = Math.Max(0, Math.Min(MusicTheory.SuspensionNames.Length - 1, value)); if (suspension != v) { suspension = v; OnChanged(nameof(Suspension)); } } }
        public int ModeOverride { get { return modeOverride; } set { int v = Math.Max(0, Math.Min(MusicTheory.ModeOverrideNames.Length - 1, value)); if (modeOverride != v) { modeOverride = v; OnChanged(nameof(ModeOverride)); } } }
        public bool OpenVoicing { get { return openVoicing; } set { if (openVoicing != value) { openVoicing = value; OnChanged(nameof(OpenVoicing)); } } }
        public int Style { get { return style; } set { if (style != value) { style = value; OnChanged(nameof(Style)); } } }
        public int BeatsPerBar { get { return beatsPerBar; } set { int v = Math.Max(1, value); if (beatsPerBar != v) { beatsPerBar = v; OnChanged(nameof(BeatsPerBar)); } } }
        public int Repeats { get { return repeats; } set { int v = Math.Max(1, value); if (repeats != v) { repeats = v; OnChanged(nameof(Repeats)); } } }

        // "Personnalisé" style: one-bar voice grid (row = chord-voice index) drawn in the rhythm editor.
        // Persisted with the graph (SequencerSlice exposes NotesLow/NotesHigh).
        public SequencerSlice[] CustomSlices { get; set; }
        public int CustomSlicesPerQuarter { get; set; } = 4;
        /// <summary>NOTE-LIST form of the custom motif (Note = chord-voice ROW index; distinguishes adjacent same-voice
        /// notes, unlike the slice grid). Source of truth for rendering when non-empty; <see cref="CustomSlices"/> stays
        /// as the OR-merged grid (length carrier / thumbnails / compat). Null/empty on old files → render from slices.</summary>
        public List<RiffNote> CustomNotes { get; set; }
        /// <summary>When this chord's Style is CustomStyle because a PROJECT USER STYLE was picked from the main Style
        /// dropdown, the style's name — so the editor re-selects it (and it reads like a first-class style). Null = a
        /// plain hand-drawn "Personnalisé" grid.</summary>
        public string UserStyleName { get; set; }

        // ---- MELODIC CELL grid (row = DIATONIC DEGREE 1..7 over 2 octaves; polyphonic; all scale tones incl. non-chord).
        // Null/empty = this chord has no melodic cell. Rendered on its own staff via PatternGenerator.GenerateMelodic. ----
        public SequencerSlice[] MelodicSlices { get; set; }
        public int MelodicSlicesPerQuarter { get; set; } = 4;
        public List<RiffNote> MelodicNotes { get; set; }
        public int MelodicOctave { get { return melodicOctave; } set { if (melodicOctave != value) { melodicOctave = value; OnChanged(nameof(MelodicOctave)); } } }
        public int MelodicAnchor { get { return melodicAnchor; } set { int v = Math.Max(0, Math.Min(1, value)); if (melodicAnchor != v) { melodicAnchor = v; OnChanged(nameof(MelodicAnchor)); } } }
        public bool MelodicOpenVoicing { get { return melodicOpenVoicing; } set { if (melodicOpenVoicing != value) { melodicOpenVoicing = value; OnChanged(nameof(MelodicOpenVoicing)); } } }
        public int MelodicVoiceLead { get { return melodicVoiceLead; } set { int v = Math.Max(0, Math.Min(3, value)); if (melodicVoiceLead != v) { melodicVoiceLead = v; OnChanged(nameof(MelodicVoiceLead)); } } }
        public bool MelodicPreserve { get { return melodicPreserve; } set { if (melodicPreserve != value) { melodicPreserve = value; OnChanged(nameof(MelodicPreserve)); } } }
        [JsonIgnore] public bool HasMelodic => MelodicNotes != null && MelodicNotes.Count > 0;

        public void SetMelodicNotes(List<RiffNote> notes, int slicesPerQuarter, int lengthSlices)
        {
            MelodicNotes = notes ?? new List<RiffNote>();
            int len = Math.Max(lengthSlices, RiffNotes.LengthOf(MelodicNotes));
            MelodicSlices = RiffNotes.ToSlices(MelodicNotes, len);
            MelodicSlicesPerQuarter = Math.Max(1, slicesPerQuarter);
            OnChanged(nameof(MelodicNotes));
        }

        public void SetCustom(SequencerSlice[] slices, int slicesPerQuarter)
        {
            CustomSlices = slices;
            CustomSlicesPerQuarter = Math.Max(1, slicesPerQuarter);
            OnChanged(nameof(CustomSlices));
        }

        /// <summary>Set the custom motif from a NOTE LIST (voice rows): stores both the notes and their OR-merged slice
        /// grid (so length/thumbnails/compat keep working). <paramref name="lengthSlices"/> = the motif's full bar length.</summary>
        public void SetCustomNotes(List<RiffNote> notes, int slicesPerQuarter, int lengthSlices)
        {
            CustomNotes = notes ?? new List<RiffNote>();
            int len = Math.Max(lengthSlices, RiffNotes.LengthOf(CustomNotes));
            CustomSlices = RiffNotes.ToSlices(CustomNotes, len);
            CustomSlicesPerQuarter = Math.Max(1, slicesPerQuarter);
            OnChanged(nameof(CustomNotes));
        }

        [JsonIgnore] public override string Title { get { return "Pattern"; } }
    }

    /// <summary>
    /// Generates a looping DRUM groove on the fly from a style + density (see <see cref="DrumPattern"/>),
    /// targeting GM percussion keys. Wire a Set-instrument(drum kit) node upstream. Like
    /// <see cref="PatternGeneratorModule"/> it's a TIMED node with built-in looping via Repeats.
    /// Style/Density are int indices into the name tables in <see cref="DrumPattern"/>.
    /// </summary>
    public class DrumPatternModule : FlowModule
    {
        int style = 0;       // index into DrumPattern.StyleNames
        int density = 0;     // index into DrumPattern.DensityNames (mostly the hi-hat subdivision)
        int kit = 0;         // index into InstrumentCatalog.DrumKits() (Standard, Room, Jazz, TR-808...)
        bool fillLast = false;
        int beatsPerBar = 4;
        int repeats = 4;

        public int Kit { get { return kit; } set { if (kit != value) { kit = value; OnChanged(nameof(Kit)); } } }
        public int Style { get { return style; } set { if (style != value) { style = value; OnChanged(nameof(Style)); } } }
        public int Density { get { return density; } set { if (density != value) { density = value; OnChanged(nameof(Density)); } } }
        public bool FillLast { get { return fillLast; } set { if (fillLast != value) { fillLast = value; OnChanged(nameof(FillLast)); } } }
        public int BeatsPerBar { get { return beatsPerBar; } set { int v = Math.Max(1, value); if (beatsPerBar != v) { beatsPerBar = v; OnChanged(nameof(BeatsPerBar)); } } }
        public int Repeats { get { return repeats; } set { int v = Math.Max(1, value); if (repeats != v) { repeats = v; OnChanged(nameof(Repeats)); } } }

        // Which drum-catalogue entry is selected (Category + Motif name), so the editor's two combos restore. Set when
        // a catalogue motif is applied; "Personnalisé" category = hand-edited CustomNotes.
        public string CatCategory { get; set; }
        public string CatMotif { get; set; }

        // "Personnalisé" style: one-bar lane grid (row = drum-lane index) drawn in the rhythm editor.
        public SequencerSlice[] CustomSlices { get; set; }
        public int CustomSlicesPerQuarter { get; set; } = 4;

        /// <summary>NOTE-LIST form of the drum motif (Note = drum LANE index): a multi-bar phrase drawn like a riff
        /// (note+duration, draw/erase) or written by the AI. When non-empty it is the SOURCE OF TRUTH and
        /// <see cref="DrumPattern.Generate"/> triggers each note ONCE at its start (percussion one-shot);
        /// <see cref="CustomSlices"/> stays as the OR-merged grid (length/thumbnails/compat). Null → old one-bar grid.</summary>
        public List<RiffNote> CustomNotes { get; set; }

        public void SetCustom(SequencerSlice[] slices, int slicesPerQuarter)
        {
            CustomSlices = slices;
            CustomSlicesPerQuarter = Math.Max(1, slicesPerQuarter);
            OnChanged(nameof(CustomSlices));
        }

        /// <summary>Set the drum motif from a NOTE LIST (lane rows): stores both the notes and their OR-merged slice
        /// grid (so duration/thumbnails/compat keep working). <paramref name="lengthSlices"/> = the phrase's full length.</summary>
        public void SetCustomNotes(List<RiffNote> notes, int slicesPerQuarter, int lengthSlices)
        {
            CustomNotes = notes ?? new List<RiffNote>();
            int len = Math.Max(lengthSlices, RiffNotes.LengthOf(CustomNotes));
            CustomSlices = RiffNotes.ToSlices(CustomNotes, len);
            CustomSlicesPerQuarter = Math.Max(1, slicesPerQuarter);
            OnChanged(nameof(CustomNotes));
        }

        [JsonIgnore] public override string Title { get { return "Drum kit"; } }
    }

    /// <summary>One chord of a <see cref="CadenceModule"/>: the absolute root/quality/inversion actually played,
    /// plus the scale Degree (-1 = absolute) so it can follow key changes/transposition like a degree-locked chord.</summary>
    public class CadenceChord
    {
        public int Root { get; set; }
        public int Quality { get; set; }
        public int Inversion { get; set; }
        public int OctaveShift { get; set; }  // −1/0/+1 relative to the module octave (voice-leading register)
        public int HeldVoice { get; set; } = -1; // arp "single held note": chord-voice index chosen by voice-leading (-1 = top)
        public int Degree { get; set; } = -1;
    }

    /// <summary>
    /// A whole chord progression (cadence) as ONE timeline brick instead of N separate chord modules. It stores
    /// the parameters of each generated chord (<see cref="Chords"/>) plus shared rendering settings (octave,
    /// articulation style, bass, beats-per-chord). At render time it uses <see cref="PatternGenerator.GenerateCadence"/>
    /// — which drives the chord generator per chord and concatenates the result — so the score, PDF, export and
    /// audio all reuse the same single riff. <see cref="CadenceStyle"/>/<see cref="StartDegree"/> are kept so the
    /// editor can re-roll a variant.
    /// </summary>
    public class CadenceModule : FlowModule
    {
        int octave = 4;      // C4 = MIDI 60
        int style = 0;       // articulation/rhythm — index into PatternGenerator.StyleNames
        int beatsPerBar = 1; // beats (temps) per chord cell
        bool bass = false;   // add the chord root in the bass
        bool bassPerBeat = false; // bass per beat vs per measure (held)
        int heldMode = 0;    // arpège+tenue: 0 top / 1 plaqué / 2 fond+quinte / 3 fond+tierce
        int climbMode = 0;   // arpège+tenue climb: 0 montant / 1 Alberti / 2 mixte
        bool halveDurations = false; // arpège+tenue: halve every value (doubles-croches)
        int cadenceStyle = 0;// index into MusicTheory.CadenceStyles (for re-roll/info)
        int startDegree = 0; // degree the cadence starts from (for re-roll)
        int measures = 4;    // re-roll inputs (mirror the dialog)
        int chordsPerMeasure = 1;
        int voiceLeadMode = 1; // renversement: 0 aucun (fond.) / 1 auto (mouvement mini) / 2 basse proche / 3 haut proche
        bool openVoicing = false; // spread every chord (open position)

        public int Octave { get { return octave; } set { if (octave != value) { octave = value; OnChanged(nameof(Octave)); } } }
        public int Style { get { return style; } set { if (style != value) { style = value; OnChanged(nameof(Style)); } } }
        public int BeatsPerBar { get { return beatsPerBar; } set { int v = Math.Max(1, value); if (beatsPerBar != v) { beatsPerBar = v; OnChanged(nameof(BeatsPerBar)); } } }
        public bool Bass { get { return bass; } set { if (bass != value) { bass = value; OnChanged(nameof(Bass)); } } }
        public bool BassPerBeat { get { return bassPerBeat; } set { if (bassPerBeat != value) { bassPerBeat = value; OnChanged(nameof(BassPerBeat)); } } }
        public int HeldMode { get { return heldMode; } set { int v = Math.Max(0, Math.Min(3, value)); if (heldMode != v) { heldMode = v; OnChanged(nameof(HeldMode)); } } }
        public int ClimbMode { get { return climbMode; } set { int v = Math.Max(0, Math.Min(3, value)); if (climbMode != v) { climbMode = v; OnChanged(nameof(ClimbMode)); } } }
        public bool HalveDurations { get { return halveDurations; } set { if (halveDurations != value) { halveDurations = value; OnChanged(nameof(HalveDurations)); } } }
        public int CadenceStyle { get { return cadenceStyle; } set { if (cadenceStyle != value) { cadenceStyle = value; OnChanged(nameof(CadenceStyle)); } } }
        public int StartDegree { get { return startDegree; } set { int v = Math.Max(0, Math.Min(6, value)); if (startDegree != v) { startDegree = v; OnChanged(nameof(StartDegree)); } } }
        public int Measures { get { return measures; } set { int v = Math.Max(1, value); if (measures != v) { measures = v; OnChanged(nameof(Measures)); } } }
        public int ChordsPerMeasure { get { return chordsPerMeasure; } set { int v = Math.Max(1, value); if (chordsPerMeasure != v) { chordsPerMeasure = v; OnChanged(nameof(ChordsPerMeasure)); } } }
        public int VoiceLeadMode { get { return voiceLeadMode; } set { int v = Math.Max(0, Math.Min(3, value)); if (voiceLeadMode != v) { voiceLeadMode = v; OnChanged(nameof(VoiceLeadMode)); } } }
        public bool OpenVoicing { get { return openVoicing; } set { if (openVoicing != value) { openVoicing = value; OnChanged(nameof(OpenVoicing)); } } }

        public List<CadenceChord> Chords { get; set; } = new List<CadenceChord>();

        // "Personnalisé" rhythm: one-bar voice grid (row = chord-voice index) drawn in the motif dialog, applied to
        // EVERY chord of the cadence (re-rooted/voiced per chord). Persisted with the project.
        public SequencerSlice[] CustomSlices { get; set; }
        public int CustomSlicesPerQuarter { get; set; } = 4;
        public List<RiffNote> CustomNotes { get; set; }   // note-list form (see PatternGeneratorModule.CustomNotes)
        public void SetCustom(SequencerSlice[] slices, int slicesPerQuarter)
        {
            CustomSlices = slices;
            CustomSlicesPerQuarter = Math.Max(1, slicesPerQuarter);
            OnChanged(nameof(CustomSlices));
        }
        public void SetCustomNotes(List<RiffNote> notes, int slicesPerQuarter, int lengthSlices)
        {
            CustomNotes = notes ?? new List<RiffNote>();
            int len = Math.Max(lengthSlices, RiffNotes.LengthOf(CustomNotes));
            CustomSlices = RiffNotes.ToSlices(CustomNotes, len);
            CustomSlicesPerQuarter = Math.Max(1, slicesPerQuarter);
            OnChanged(nameof(CustomNotes));
        }

        [JsonIgnore] public override string Title { get { return "Cadence"; } }
    }

    /// <summary>
    /// A MELODIC LINE over several measures: you draw only the RHYTHM (up to 3 voice rows) and the ENGINE picks the
    /// pitches at render time from the harmony in effect at each instant — the arrangement chord grid in structure mode,
    /// else the chord track's module under the cursor. Voice 0 leads (independent voice-led lines in register bands);
    /// strong beats take chord tones, weak positions chord OR passing tones. Sits on its own "ligne mélodique" track.
    /// Reusable BY NAME like a user chord style (<see cref="LineName"/>): "ajouter ligne mélodique" copies the rhythm.
    /// </summary>
    public class MelodicLineModule : FlowModule
    {
        public const int MaxVoices = 3;

        int beatsPerBar = 4;   // TOTAL beats of the line (= X measures × meter numerator)
        int voiceCount = 1;    // 1..3 voices (rows in the rhythm grid)
        string lineName;       // shared name for reuse/propagation (like PatternGeneratorModule.UserStyleName)
        bool preserve;         // "Préserver": excluded when applying a saved line to every instance of the same name

        public bool Preserve { get { return preserve; } set { if (preserve != value) { preserve = value; OnChanged(nameof(Preserve)); } } }

        public int BeatsPerBar { get { return beatsPerBar; } set { int v = Math.Max(1, value); if (beatsPerBar != v) { beatsPerBar = v; OnChanged(nameof(BeatsPerBar)); } } }
        public int VoiceCount { get { return voiceCount; } set { int v = Math.Max(1, Math.Min(MaxVoices, value)); if (voiceCount != v) { voiceCount = v; OnChanged(nameof(VoiceCount)); } } }
        public string LineName { get { return lineName; } set { if (lineName != value) { lineName = value; OnChanged(nameof(LineName)); } } }

        // RHYTHM grid: row = voice (0..MaxVoices-1). Pitches are NOT stored — the engine derives them from the harmony.
        public SequencerSlice[] Slices { get; set; }
        public int SlicesPerQuarter { get; set; } = 4;
        public List<RiffNote> Notes { get; set; }   // note-list form (Note = voice row)

        /// <summary>Semitone shift of the engine's register bands: 0 = default (melody), negative = lower (e.g. a bass line
        /// ~-24), positive = brighter/higher. Lets one line be a bass and another the lead, and dev sections lift register.</summary>
        public int RegisterShift { get; set; } = 0;

        /// <summary>Melodic CONTOUR mode the engine uses to pick pitches between harmonic anchors: 0 = Vague/arcs (default),
        /// 1 = Montante, 2 = Descendante, 3 = Statique (pivot), 4 = Zigzag, 5 = Aléatoire.</summary>
        public int Contour { get; set; } = 0;

        /// <summary>On STRONG beats, which chord tone the line lands on: 0 = Défaut (nearest, auto), 1 = Fondamentale,
        /// 2 = Tierce, 3 = Quinte. The melodic equivalent of a chord inversion.</summary>
        public int Anchor { get; set; } = 0;

        /// <summary>CONTINUITÉ (voice-leading), 0..100: attraction toward the nearest tone. 0 = free contour (default);
        /// higher caps the leap size between successive notes AND smooths the join with the PREVIOUS module (the line starts
        /// near where the last one ended, favouring common tones at chord changes). 100 = maximally stepwise.</summary>
        public int Continuity { get; set; } = 0;

        /// <summary>Rhythm/pitch VARIATION applied to the motif (for reuse): 0 = Aucune, 1 = Split (couper les longues notes),
        /// 2 = Gate (sauter des notes → silences), 3 = Rétrograde (à l'envers, re-snappé), 4 = Miroir (intervalles inversés,
        /// re-snappé). See <see cref="Timeline.MelodicLineEngine.VariationNames"/>.</summary>
        public int Variation { get; set; } = 0;

        /// <summary>Intra-module TENSION SLOPE: semitone drift of the register band from the first note to the last (e.g. +7
        /// = the line rises across the module, -5 = falls). 0 = flat (default). Layers on top of RegisterShift.</summary>
        public int TensionSlope { get; set; } = 0;

        /// <summary>AMPLITUDE: half-width (in semitones) of the register band the contour is allowed to roam within, around
        /// each voice's centre. Small = the line hovers near its centre (calm), large = wide leaps / broad tessitura.
        /// 0 or unset = the engine default (12 = one octave up and down). Applies to every contour.</summary>
        public int Amplitude { get; set; } = 12;

        /// <summary>ORNEMENTATION (0..100): how often accented ornaments (a suspension/retard or a proper appoggiatura,
        /// resolved by step onto a chord tone on the next note) replace the plain chord tone on a strong/secondary beat.
        /// 0 (default) = none → strong beats are clean chord tones; higher = more ornaments. Off by default so the line
        /// stays predictable.</summary>
        public int Ornaments { get; set; } = 0;

        /// <summary>WAVE LENGTH: for the "Vague" contour, number of notes per arc before the direction flips. Small = tight,
        /// fast ripples; large = broad, slow swells. 0 = the engine's per-voice default. Ignored by the other contours.</summary>
        public int WaveLength { get; set; } = 0;

        public void SetNotes(List<RiffNote> notes, int slicesPerQuarter, int lengthSlices)
        {
            Notes = notes ?? new List<RiffNote>();
            int len = Math.Max(lengthSlices, RiffNotes.LengthOf(Notes));
            Slices = RiffNotes.ToSlices(Notes, len);
            SlicesPerQuarter = Math.Max(1, slicesPerQuarter);
            OnChanged(nameof(Notes));
        }

        [JsonIgnore] public override string Title { get { return "Ligne mélodique"; } }
    }
}
