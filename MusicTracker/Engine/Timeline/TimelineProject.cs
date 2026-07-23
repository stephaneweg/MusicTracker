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

    public enum TimelineTrackType { Instrument, Drum, Chord }

    /// <summary>A volume value (0..1+) taking effect at a beat — the per-track "volume sub-track".</summary>
    public class VolumePoint
    {
        public double Beat;
        public double Volume = 1.0;
    }

    /// <summary>
    /// A horizontal lane. INSTRUMENT type: pick an instrument + base volume, holds riffs / chord
    /// patterns / repeats. DRUM type: base volume only (instrument is implicitly the drum kit), holds
    /// drum-kit generators / repeats. A volume automation (sub-track) overrides the base over time.
    /// </summary>
    public class TimelineTrack
    {
        public string Name = "Piste";
        public TimelineTrackType Type = TimelineTrackType.Instrument;
        public int Instrument;                 // InstrumentCatalog index (used by INSTRUMENT type)
        public Score.ScoreClefKind? Clef;      // explicit notation clef (null = derive from the instrument)
        public double Volume = 1.0;            // base volume
        public bool Mute = false;              // silenced
        public bool Solo = false;              // when any track is soloed, only soloed tracks are heard
        public List<VolumePoint> VolumeAutomation = new List<VolumePoint>(); // changes at T
        public List<TimelineItem> Items = new List<TimelineItem>();
    }

    /// <summary>The whole arrangement: a global tempo track + one track per instrument.</summary>
    public class TimelineProject
    {
        public List<TempoChange> Tempo = new List<TempoChange> { new TempoChange { Beat = 0, Bpm = 120 } };
        public List<TimelineTrack> Tracks = new List<TimelineTrack>();

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
