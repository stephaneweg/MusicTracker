using System;
using System.Collections.Generic;
using MusicTracker.Engine.Flow;

namespace MusicTracker.Engine.Timeline
{
    /// <summary>A tempo value taking effect at a beat position (the first is the main tempo at beat 0).</summary>
    public class TempoChange
    {
        public double Beat;
        public double Bpm = 120;
    }

    /// <summary>
    /// One item in a track (or in a Repeat's sub-track): a leaf module (Play-riff / Pattern /
    /// Drum-kit) or a Repeat container. Position is RELATIVE: <see cref="SilenceBefore"/> is the rest
    /// (in beats) before it; the absolute start = sum of (SilenceBefore + length) of all preceding
    /// items. Updated by drag. At playback the player first waits SilenceBefore, then plays the item.
    /// </summary>
    public class TimelineItem
    {
        public double SilenceBefore; // hidden rest (beats) before this item
        public FlowModule Module;    // leaf content (null when this is a Repeat)
    }

    /// <summary>
    /// A container = ONE sub-track of LEAF modules (Items[i].Module set, never another Repeat — no
    /// nesting) with gaps between them for silence. Repeated Count times (or looped to the end).
    /// No padding: the group's time span = max(item.StartBeat + length), so it tiles seamlessly and
    /// stays aligned with the outer track timeline.
    /// </summary>
    public class RepeatGroup
    {
        public int Count = 2;
        public bool Loop;
        public List<TimelineItem> Items = new List<TimelineItem>(); // leaves only (no nested Repeat)

        // Transient: when Loop is on, the number of cycles needed to fill up to the end of the piece.
        // Recomputed by TimelineProject.ResolveLoops before each layout / playback (not serialized).
        [System.Text.Json.Serialization.JsonIgnore] public int EffectiveCount;
    }

    /// <summary>A named position marker ("Intro", "Thème A", "Coda") shown in the timeline's marker band, above
    /// the measure ruler. A POINT, not a range: the section it opens runs to the next marker. Purely editorial —
    /// playback, score and exports ignore it. Serialized with the project (.sq).</summary>
    public class SectionMarker
    {
        public double Beat;            // position in raw quarter-beats, same axis as the ruler / PxPerBeat
        public string Name = "";
    }

    public enum TimelineTrackType { Instrument, Drum, Chord }

    /// <summary>A volume value (0..1+) taking effect at a beat — the per-track "volume sub-track".</summary>
    public class VolumePoint
    {
        public double Beat;
        public double Volume = 1.0;
    }

    /// <summary>Paramètre d'automation par canal MIDI, un par lane. Volume et Panoramique restent stockés
    /// respectivement dans <see cref="TimelineTrack.VolumeAutomation"/> (rétro-compatibilité) et sur la valeur
    /// statique de la piste ; les autres sont posés en lanes additionnelles (<see cref="TimelineTrack.AutomationLanes"/>).</summary>
    public enum AutomationParam
    {
        Volume,        // CC7   — géré à part par VolumeAutomation (existant), présent ici pour l'énumération complète.
        Pan,           // CC10  — -1..+1
        Expression,    // CC11  — 0..1
        Modulation,    // CC1   — 0..1  (LFO)
        Sustain,       // CC64  — 0..1  (pédale piano ; le synthé bascule à 64/127)
        ReverbSend,    // CC91  — 0..1
        ChorusSend,    // CC93  — 0..1
        PitchBend,     // 0xE0  — -1..+1 (mappé sur ±8192, plage définie par le RPN 0 côté patch, 2 demi-tons par défaut)
    }

    /// <summary>Un point d'une courbe d'automation générique. <see cref="Value"/> a la plage propre au
    /// <see cref="AutomationParam"/> de la lane (0..1 pour les CC unipolaires, -1..+1 pour Pan/PitchBend).</summary>
    public class AutomationPoint
    {
        public double Beat;
        public double Value;
    }

    /// <summary>Une lane d'automation posée sur une piste : une courbe (points triés par beat) qui pilote un
    /// paramètre MIDI (<see cref="Param"/>). Interpolation linéaire entre les points, valeur par défaut de la
    /// piste avant le premier point, plate après le dernier — même sémantique que la courbe de volume.</summary>
    public class AutomationLane
    {
        public AutomationParam Param;
        public bool Enabled = true;
        public List<AutomationPoint> Points = new List<AutomationPoint>();
    }

    /// <summary>
    /// A horizontal lane. INSTRUMENT type: pick an instrument + base volume, holds riffs / chord
    /// patterns / repeats. DRUM type: base volume only (instrument is implicitly the drum kit), holds
    /// drum-kit generators / repeats. A volume automation (sub-track) overrides the base over time.
    /// </summary>
    // NOTIFIES on the mixer-controlled properties (Volume/Pan/Mute/Solo) so the mixer dialog and the timeline
    // header stay in sync via two-way bindings (edit one → the other updates), and the player reads them live.
    // The private backing fields aren't serialized; the PUBLIC properties keep the same JSON keys → .sq-compatible.
    public class TimelineTrack : System.ComponentModel.INotifyPropertyChanged
    {
        public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;
        void Notify([System.Runtime.CompilerServices.CallerMemberName] string n = null)
            => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(n));

        string name = "Piste";
        public string Name { get { return name; } set { if (name != value) { name = value; Notify(); } } }
        public TimelineTrackType Type = TimelineTrackType.Instrument;
        public int Instrument;                 // InstrumentCatalog index (used by INSTRUMENT type)
        public int DrumKit = 0;                // DRUM type: index into InstrumentCatalog.DrumKits() (0 = Standard); the kit sound
        public Score.ScoreClefKind? Clef;      // explicit notation clef (null = derive from the instrument)

        double volume = 1.0;                   // base volume
        public double Volume { get { return volume; } set { if (volume != value) { volume = value; Notify(); } } }
        double pan = 0.0;                      // stereo pan: -1 = hard left, 0 = centre, +1 = hard right (mixer → CC10)
        public double Pan { get { return pan; } set { if (pan != value) { pan = value; Notify(); } } }
        bool mute = false;                     // silenced
        public bool Mute { get { return mute; } set { if (mute != value) { mute = value; Notify(); } } }
        bool solo = false;                     // when any track is soloed, only soloed tracks are heard
        public bool Solo { get { return solo; } set { if (solo != value) { solo = value; Notify(); } } }

        // REVERB (mixer → CC91). Stored as an OFFSET from the GM default (40), never as an absolute value: a .sq
        // written before this feature has no field, deserializes the offset to 0, and therefore keeps sounding
        // exactly as it did. An absolute field would deserialize to 0 = bone dry and silently change every
        // existing project.
        int reverbOffset = 0;
        public int ReverbOffset { get { return reverbOffset; } set { int v = Math.Max(-DefaultReverb, Math.Min(127 - DefaultReverb, value)); if (reverbOffset != v) { reverbOffset = v; Notify(); Notify(nameof(ReverbSend)); } } }

        /// <summary>The GM default reverb send MeltySynth gives every channel (see Channel.Reset).</summary>
        public const int DefaultReverb = 40;

        /// <summary>The absolute CC91 value, 0..127 — what the mixer edits and the player sends. Not serialized:
        /// only the offset is, so the file stays compatible in both directions.</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public int ReverbSend
        {
            get { return Math.Max(0, Math.Min(127, DefaultReverb + reverbOffset)); }
            set { ReverbOffset = value - DefaultReverb; }
        }

        public bool Collapsed = false;         // header + lane shrunk to a minimal height (title + expand button) to save vertical space
        public List<VolumePoint> VolumeAutomation = new List<VolumePoint>(); // changes at T (CC7 automation, rétro-compat)

        /// <summary>Lanes d'automation MIDI par canal, ajoutées par l'utilisateur : Pan/Expression/Modulation/Sustain/
        /// Réverbe/Chorus/Pitch bend. Volume reste porté par <see cref="VolumeAutomation"/> pour ne pas casser les fichiers
        /// existants ni l'export MIDI. Une piste ouverte avant cette version n'a pas ce champ dans son JSON : le
        /// désérialiseur garde la liste vide (initialiseur) — aucun impact au chargement.</summary>
        public List<AutomationLane> AutomationLanes = new List<AutomationLane>();

        /// <summary>Chaîne d'effets d'insert de la piste (dans l'ordre du signal). Vide par défaut — un .sq
        /// antérieur à cette fonctionnalité n'a pas ce champ dans son JSON, l'initialiseur laisse la liste
        /// vide, donc rétro-compatibilité totale.</summary>
        public List<Effects.TrackEffectData> Inserts = new List<Effects.TrackEffectData>();

        /// <summary>Chemin absolu du plugin VSTi qui rend cette piste au lieu du synthé MeltySynth GM.
        /// <c>null</c> (défaut) = mode MeltySynth (l'ancien pipeline, <see cref="Instrument"/> pilote le patch GM).
        /// Non-null = mode VSTi : le player instancie un <c>VstInstrument</c> et lui envoie les notes de la piste,
        /// <see cref="Instrument"/> est ignoré côté rendu. Un .sq antérieur à cette fonctionnalité n'a pas ce
        /// champ (System.Text.Json ignore les absents proprement) → mode MeltySynth comme avant.</summary>
        public string VstiPath { get; set; }

        /// <summary>Chunk d'état interne du VSTi (base64 du blob binaire retourné par <c>getChunk</c>), pour
        /// restaurer le patch chargé, les paramètres et la banque de presets à la réouverture du projet.
        /// Écrit à chaque sauvegarde par <c>TimelineScreen</c> qui interroge le VstInstrument vivant, si présent.
        /// <c>null</c> = pas d'état sauvé (nouveau VSTi ou plugin qui ne supporte pas <c>getChunk</c>).</summary>
        public string VstiStateBlob { get; set; }

        /// <summary>Id d'un plugin Koton NATIF (framework KotonStudio.Library) qui rend cette piste au lieu du
        /// synthé MeltySynth GM. <c>null</c> = pas de plugin Koton (mode MeltySynth ou VSTi via <see cref="VstiPath"/>).
        /// Non-null = mode Koton natif : le player instancie via <c>KotonPluginRegistry.InstantiateInstrument</c>
        /// et route notes/CC/render à travers un <c>KotonInstrumentAdapter</c>. Un .sq antérieur à cette
        /// fonctionnalité n'a pas ce champ (System.Text.Json ignore les absents) → mode MeltySynth comme avant.
        /// Précédence si les deux sont non-null : VstiPath gagne (les plugins Koton sont ajoutés après le
        /// routage VST existant, un fichier hérité qui aurait les deux garde son comportement historique).</summary>
        public string KotonInstrumentId { get; set; }

        /// <summary>Blob d'état sérialisé (base64) du plugin Koton natif attaché à cette piste. Écrit à
        /// chaque sauvegarde via <c>TimelineScreen</c> qui interroge <c>IKotonInstrument.SaveState()</c>.
        /// <c>null</c> = pas d'état sauvé (plugin fraîchement sélectionné, valeurs par défaut).</summary>
        public string KotonInstrumentStateBlob { get; set; }

        public List<TimelineItem> Items = new List<TimelineItem>();
    }

    /// <summary>The whole arrangement: a global tempo track + one track per instrument.</summary>
    public class TimelineProject
    {
        public List<TempoChange> Tempo = new List<TempoChange> { new TempoChange { Beat = 0, Bpm = 120 } };
        public List<TimelineTrack> Tracks = new List<TimelineTrack>();

        /// <summary>Chaîne d'inserts du bus master (après la sommation des pistes). Vide par défaut ; un .sq
        /// antérieur n'a pas ce champ — l'initialiseur laisse la liste vide.</summary>
        public List<Effects.TrackEffectData> MasterInserts = new List<Effects.TrackEffectData>();

        /// <summary>User-saved custom CHORD accompaniment styles (degree grids), authored in the chord rhythm editor and
        /// reusable across the project: they show up in the editor's "Copier" style dropdown alongside the built-ins.</summary>
        public List<UserChordStyle> UserChordStyles { get; set; } = new List<UserChordStyle>();
        public List<UserChordStyle> UserMelodicLines { get; set; } = new List<UserChordStyle>(); // saved melodic-line rhythms (by name)
        public List<UserChordStyle> UserDrumStyles { get; set; } = new List<UserChordStyle>();   // saved custom drum motifs (by name), Notes = drum lane note-list

        /// <summary>The persistent arrangement "recipe" (chord trame + sections + canonical theme) when the project was
        /// produced by an auto-composer — enables editing the chords/theme afterwards and regenerating the derived
        /// parts. Null for hand-built or imported projects.</summary>
        public ComposedArrangement Arrangement;

        /// <summary>The piece's key signature (concert), set in the toolbar or detected at import. Default Do majeur.</summary>
        public Score.KeySignature Key { get; set; } = new Score.KeySignature();

        /// <summary>Time signature (numerator / denominator), from the imported file or detected. Default 4/4.</summary>
        public int TimeSigNum { get; set; } = 4;
        public int TimeSigDen { get; set; } = 4;

        /// <summary>Anacrusis / pickup ("levée"): length in QUARTER-beats of the incomplete first measure. 0 = none.
        /// Purely metric: the barline grid is shifted by this amount (full barlines fall at PickupBeats + k·bpb) and the
        /// engines' strong-beat / chord-grid alignment follows it; audio playback timing is unchanged.</summary>
        public double PickupBeats { get; set; } = 0;

        /// <summary>Score display time scale: 1.0 normally; 1.5 when a x/4 file in triplets is reinterpreted as a
        /// compound x/8 (triplet-eighths → real eighths, the measure stays the same physical length).</summary>
        public double TimeSigScale { get; set; } = 1.0;

        /// <summary>Minimum timeline length in beats (temps) — set from a starter template's chosen measure count so an
        /// empty project still spans that many bars. The displayed length never goes below this. 0 = no minimum.</summary>
        public double MinBeats { get; set; } = 0;

        /// <summary>Swing: where the OFF-eighth falls inside the beat, in percent. 50 = straight (dead centre), 66.7 =
        /// full triplet feel (the classic jazz 2:1). A performance feel only: playback and audio export are warped,
        /// the written notes / score / MIDI export stay straight eighths, as a DAW does.</summary>
        public double SwingPercent { get; set; } = 50;

        /// <summary>HUMANISATION (0..100) : petit random deterministe sur la velocity (jusqu'a ±8) et le timing
        /// (jusqu'a ±2 slices, JAMAIS sur un downbeat pour preserver le groove). 0 = off (defaut, comportement
        /// mecanique historique). 40-60 = feel naturel. Ne touche que le playback, pas la partition.</summary>
        public int HumanizePercent { get; set; } = 0;

        /// <summary>MICRO-AGOGIQUE (0..100) : allonge legerement la duree des notes sur les temps forts
        /// (downbeat +10% max, temps fort secondaire +5% max, echelle par ce pourcentage). 0 = off (defaut).
        /// Rend une phrase vivante en respirant sur les points d'appui, sans decaler les attaques. Playback seul.</summary>
        public int AgogicPercent { get; set; } = 0;

        /// <summary>Named section markers (see <see cref="SectionMarker"/>), kept sorted by Beat. Empty by default:
        /// a project created, imported or generated before/without this feature simply has none. A PROPERTY with an
        /// initialiser on purpose — opening a .sq written before this feature leaves the list empty, never null.</summary>
        public List<SectionMarker> Markers { get; set; } = new List<SectionMarker>();

        /// <summary>Les riffs que ce projet référence. Ils appartiennent AU PROJET, pas à l'application : chaque
        /// onglet a les siens. Historiquement ils vivaient dans un singleton global (vestige de l'époque où un seul
        /// morceau pouvait être ouvert) ; ouvrir un second morceau vidait alors le premier, et le réenregistrer
        /// écrivait son .sq avec les riffs du voisin. Le format de fichier, lui, n'a pas changé : ils restent
        /// sérialisés au niveau du DOCUMENT (voir TimelineDocument.Riffs).</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public System.Collections.ObjectModel.ObservableCollection<Riff> Riffs { get; }
            = new System.Collections.ObjectModel.ObservableCollection<Riff>();

        /// <summary>Résout un riff par son identifiant, dans CE projet. Remplace l'ancien résolveur statique :
        /// c'est le point qui garantit qu'un onglet ne voit jamais les riffs d'un autre.</summary>
        public Riff RiffById(Guid id)
        {
            foreach (var r in Riffs) if (r != null && r.Id == id) return r;
            return null;
        }

        /// <summary>Le résolveur sous forme de délégué, pour les nombreuses fonctions du moteur qui en attendent un.</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public Func<Guid, Riff> Resolver => RiffById;

        // ---- mesures de géométrie, résolues DANS ce projet -------------------------------------------------
        // Elles vivaient dans TimelineHelper en statique et lisaient le résolveur global. Les porter ici leur
        // donne le bon contexte sans alourdir les appels : `project.DispLen(it)` a la même arité qu'avant.

        /// <summary>Longueur affichée d'un bloc, en temps (un Repeat compte pour toutes ses répétitions).</summary>
        public double DispLen(TimelineItem it) => ItemLength(it, RiffById);

        /// <summary>Fin d'une piste, en temps depuis le début du morceau.</summary>
        public double TrackEndBeats(TimelineTrack t)
        {
            if (t?.Items == null) return 0;
            double cur = 0;
            foreach (var it in t.Items) cur += it.SilenceBefore + ItemLength(it, RiffById);
            return cur;
        }

        /// <summary>Temps auquel un bloc commence sur sa piste.</summary>
        public double ItemStartBeat(TimelineTrack track, TimelineItem item)
        {
            if (track?.Items == null) return 0;
            double cur = 0;
            foreach (var it in track.Items)
            {
                cur += it.SilenceBefore;
                if (ReferenceEquals(it, item)) return cur;
                cur += ItemLength(it, RiffById);
            }
            return 0;
        }

        public double MainBpm => Tempo != null && Tempo.Count > 0 ? Tempo[0].Bpm : 120;

        // ---- duration helpers (beats) ----

        public static double ItemLength(TimelineItem item, Func<Guid, Riff> resolveRiff)
        {
            if (item == null) return 0;
            return item.Module != null ? ModuleDuration.Beats(item.Module, resolveRiff) : 0;
        }

        // Cycles actually played: a finite Repeat uses Count; a looping one uses its resolved EffectiveCount
        // (filled to the end of the piece by ResolveLoops, falling back to 1 before that runs).
        static int RepeatCycles(RepeatGroup g)
            => g.Loop ? Math.Max(1, g.EffectiveCount) : Math.Max(1, g.Count);

        // Same as ItemLength but a looping Repeat counts as ONE cycle — used to measure the "fixed" content
        // length (so a loop can fill up to it without depending on its own filled length).
        static double ItemLengthBase(TimelineItem item, Func<Guid, Riff> resolveRiff)
        {
            if (item == null) return 0;
            return item.Module != null ? ModuleDuration.Beats(item.Module, resolveRiff) : 0;
        }

        /// <summary>
        /// Resolves each looping Repeat's <see cref="RepeatGroup.EffectiveCount"/> so it tiles up to the end
        /// of the longest (non-looping) content. Call before laying out or playing the project.
        /// </summary>
        public static void ResolveLoops(TimelineProject p, Func<Guid, Riff> resolveRiff)
        {
            if (p == null) return;
            var tracks = p.Tracks;
            int nt = tracks.Count;

            // Each track's latest end (last element's absolute start + duration), loops counted as one cycle.
            var baseEnd = new double[nt];
            for (int i = 0; i < nt; i++)
            {
                double c = 0;
                var its = tracks[i].Items;
                if (its != null) foreach (var it in its) c += it.SilenceBefore + ItemLengthBase(it, resolveRiff);
                baseEnd[i] = c;
            }

            for (int i = 0; i < nt; i++)
            {
                var its = tracks[i].Items;
                if (its == null) continue;

                // Fill target = the latest end among the OTHER tracks.
                double target = 0;
                for (int j = 0; j < nt; j++) if (j != i && baseEnd[j] > target) target = baseEnd[j];

                double cursor = 0;
                foreach (var it in its)
                {
                    double startNoPause = cursor;           // the Repeat's start without its own pause-before
                    cursor += it.SilenceBefore;
                    cursor += ItemLength(it, resolveRiff);
                }
            }
        }

        // Total span of a sequence (walk: each item adds SilenceBefore + its length). No padding.
        public static double SequenceLength(System.Collections.Generic.IList<TimelineItem> items, Func<Guid, Riff> resolveRiff)
        {
            double cursor = 0;
            if (items != null)
                foreach (var it in items) cursor += it.SilenceBefore + ItemLength(it, resolveRiff);
            return cursor;
        }

        public static double GroupLength(RepeatGroup g, Func<Guid, Riff> resolveRiff)
            => g == null ? 0 : SequenceLength(g.Items, resolveRiff);

        /// <summary>The track's total length in beats (end of the last item).</summary>
        public static double TrackEnd(TimelineTrack t, Func<Guid, Riff> resolveRiff)
            => t == null ? 0 : SequenceLength(t.Items, resolveRiff);
    }

    /// <summary>A saved timeline (.sq): the arrangement + the riffs it references (so it's self-contained).</summary>
    public class TimelineDocument
    {
        public TimelineProject Project { get; set; } = new TimelineProject();
        public System.Collections.Generic.List<Riff> Riffs { get; set; } = new System.Collections.Generic.List<Riff>();
    }

    /// <summary>A named, project-saved custom chord accompaniment style = the degree grid the user drew in the chord
    /// rhythm editor. <see cref="Slices"/> is the raw cell grid (row = voice: 0 = bass, then chord-tone degrees),
    /// <see cref="Spb"/> its slices-per-beat resolution, <see cref="Beats"/> its beats-per-bar. Serialized with the project.</summary>
    public class UserChordStyle
    {
        public string Name { get; set; } = "";
        public int Spb { get; set; } = 4;
        public int Beats { get; set; } = 4;
        public MusicTracker.Engine.SequencerSlice[] Slices { get; set; } = new MusicTracker.Engine.SequencerSlice[0];
        public System.Collections.Generic.List<MusicTracker.Engine.RiffNote> Notes { get; set; } // note-list form (distinct adjacent notes)
    }
}
