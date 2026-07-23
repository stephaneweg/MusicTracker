using MeltySynth;
using MusicTracker.Engine.Flow;
using NAudio.Wave;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace MusicTracker.Engine.Timeline
{
    /// <summary>
    /// Plays a whole <see cref="TimelineProject"/>: one global cursor advances slice-by-slice using the
    /// tempo map; each track is flattened once (at <see cref="Spb"/> slices/beat — repeats expanded,
    /// silences via SilenceBefore) into a single slice array and rendered by the MeltySynth SF2 synthesizer
    /// (one MIDI channel per track), scaled by the track's base volume × automation. Mono 16-bit
    /// WaveProvider for WaveOutEvent / WaveExporter.
    /// </summary>
    public class TimelinePlayer : WaveProvider16
    {
        const int Spb = 24;        // global flatten resolution (slices per beat)
        const int NoteCount = 96;  // matches RiffPlayer's note range (note -> Utils.Frequencies[note])

        sealed class Track
        {
            public List<int>[] AttackAt, ReleaseAt; // per global slice (Spb): notes to (re)attack / to release
            public List<int>[] AttackVel;           // parallel to AttackAt: the MIDI velocity for each attacked note
            public Preset Template;
            public readonly Preset[] Voices = new Preset[NoteCount];
            public readonly bool[] Active = new bool[NoteCount];
            public readonly bool[] Retrig = new bool[NoteCount];
            public int Applied = -1;                // last slice whose events were applied (persists across Read calls)
            public double BaseVol = 1.0;
            public double Mix = 1.0;                 // mute/solo factor (0 = silent), applied on top of the gain
            public List<VolumePoint> Autom;
        }

        readonly int sampleRate;
        readonly Track[] tracks;
        readonly double[] tempoBeat, tempoBpm; // sorted tempo map
        readonly int totalSlices;
        readonly long[] sliceSampleStart;      // absolute sample offset where each slice begins (tempo map)

        /// <summary>Absolute sample (from beat 0) where Start() began — for mapping consumed samples to beats.</summary>
        public long SampleAtStart { get; private set; }

        readonly Engine.Score.KeySignature melodyKey;   // for chord melodic cells (diatonic degrees → pitches)
        readonly TimelineProject melodyProject;          // for melodic LINE tracks (harmony lookup on other tracks)
        int sliceIndex;
        double t;          // samples elapsed inside the current slice
        double interval;   // samples per slice at the current tempo
        bool playing, endedRaised;


        // ---- MeltySynth backend (optional) : ONE synthesizer renders every track as a MIDI channel,
        //      with true SF2 playback (multi-sample layering + DAHDSR + loop modes). Falls back to the
        //      legacy per-note WaveFunction path (synth == null) if no SoundFont object is available. ----
        // Flat fallback velocity, used when VelocityDynamics is off. (The old vel-80..107 SF2 "hole" that forced
        // this to 110 is fixed at the synth level — the modulator-dedup + filter-modulator fixes in MeltySynth —
        // so any velocity now renders cleanly and monotonically.)
        public const int MeltyVelocity = 110;

        // When true, notes are played with a per-note velocity derived from their METRIC position (the app has no
        // stored per-note velocity), so downbeats sound stronger than offbeats — a live, breathing dynamic instead
        // of a flat level. Set false to revert to the flat MeltyVelocity.
        public static bool VelocityDynamics = true;

        // Metric accent -> MIDI velocity for a note attacking at global slice s (Spb slices/beat). Strong metric
        // positions (downbeat > secondary strong beat > other beats > upbeats > fine offbeats) get more velocity.
        // Pickup (anacrusis) shifts the barline so the true downbeat is accented. Range ~78..118 = a musical, not
        // extreme, dynamic spread; every value renders correctly since the piano hole is fixed.
        int MetricVelocity(int s)
        {
            if (!VelocityDynamics) return MeltyVelocity;
            var proj = melodyProject;
            int num = Math.Max(1, proj != null ? proj.TimeSigNum : 4);
            int den = Math.Max(1, proj != null ? proj.TimeSigDen : 4);
            int spBeat = Math.Max(1, (int)Math.Round(Spb * 4.0 / den)); // slices per NOTATED beat (quarter=24, eighth=12)
            int barSlices = Math.Max(1, spBeat * num);
            int pickup = (int)Math.Round((proj?.PickupBeats ?? 0) * Spb);
            int pos = (((s - pickup) % barSlices) + barSlices) % barSlices;       // slice offset within the bar
            int inBeat = pos % spBeat;                                            // 0 = on a notated beat
            int beatInBar = pos / spBeat;                                         // 0 = downbeat of the bar
            if (inBeat == 0)
            {
                if (beatInBar == 0) return 118;                                   // bar downbeat
                if (num % 2 == 0 && beatInBar == num / 2) return 108;             // secondary strong beat (e.g. beat 3 of 4/4, pulse 2 of 6/8)
                return 100;                                                       // other on-beat
            }
            if (inBeat * 2 == spBeat) return 90;                                  // upbeat (half-beat)
            if (spBeat % 3 == 0 && inBeat % (spBeat / 3) == 0) return 84;         // ternary subdivision
            if (spBeat % 4 == 0 && inBeat % (spBeat / 4) == 0) return 84;         // binary subdivision
            return 78;                                                            // fine offbeat
        }
        MeltySynth.Synthesizer synth;
        int[] trackChannel;                 // MIDI channel per track (drum tracks -> 9)
        int[] trackProgram;                 // GM program per track (drum tracks -> DrumIndex) for the per-instrument boost
        float[] mL, mR;                     // stereo render scratch
        int mDispatched;                    // last slice whose note-on/off were sent to the synth

        public event Action Ended;

        /// <summary>Beat at which <see cref="Start"/> begins playback (0 = from the top). Set before Start().</summary>
        public double StartBeat { get; set; }

        /// <summary>Current playhead position in beats (for a UI cursor).</summary>
        public double CurrentBeat => (sliceIndex + (interval > 0 ? t / interval : 0)) / (double)Spb;

        /// <summary>Total length in samples (sum of per-slice durations under the tempo map) — for export progress.</summary>
        public long EstimatedTotalSamples { get; }

        public TimelinePlayer(TimelineProject project, Func<Guid, Riff> resolveRiff, int sampleRate = 0)
            : base(sampleRate <= 0 ? AudioFormat.SampleRate : sampleRate, 1)
        {
            this.sampleRate = sampleRate <= 0 ? AudioFormat.SampleRate : sampleRate;
            this.melodyKey = project.Key ?? new Engine.Score.KeySignature();
            this.melodyProject = project;

            var temp = (project.Tempo != null && project.Tempo.Count > 0)
                ? project.Tempo.OrderBy(x => x.Beat).ToList()
                : new List<TempoChange> { new TempoChange { Beat = 0, Bpm = 120 } };
            tempoBeat = temp.Select(x => x.Beat).ToArray();
            tempoBpm = temp.Select(x => x.Bpm > 0 ? x.Bpm : 120).ToArray();

            TimelineProject.ResolveLoops(project, resolveRiff); // size looping Repeats to fill up to the end
            double totalBeats = 0;
            foreach (var tr in project.Tracks) totalBeats = Math.Max(totalBeats, TimelineProject.TrackEnd(tr, resolveRiff));
            totalSlices = Math.Max(1, (int)Math.Ceiling(totalBeats * Spb) + 1);

            // Cumulative sample offset at the start of each slice (under the tempo map). Lets us convert a
            // played-sample count back to a beat (for the UI cursor) when a look-ahead buffer is in front.
            sliceSampleStart = new long[totalSlices + 1];
            long acc = 0;
            for (int s = 0; s < totalSlices; s++)
            {
                sliceSampleStart[s] = acc;
                acc += (long)Math.Round((60.0 / BpmAt(s / (double)Spb)) / Spb * this.sampleRate);
            }
            sliceSampleStart[totalSlices] = acc;
            EstimatedTotalSamples = acc;

            var list = new List<Track>();
            bool anySolo = project.Tracks.Any(t => t.Solo);   // solo active anywhere → non-soloed tracks are silent
            foreach (var tr in project.Tracks)
            {
                var T = new Track
                {
                    AttackAt = new List<int>[totalSlices + 1],
                    AttackVel = new List<int>[totalSlices + 1],
                    ReleaseAt = new List<int>[totalSlices + 1],
                    Template = InstrumentCatalog.GetPreset(tr.Instrument),
                    BaseVol = tr.Volume,
                    Mix = (tr.Mute || (anySolo && !tr.Solo)) ? 0.0 : 1.0,
                    Autom = (tr.VolumeAutomation != null ? tr.VolumeAutomation.OrderBy(p => p.Beat).ToList() : new List<VolumePoint>()),
                };
                double cursor = 0;
                var carry = new[] { -1, -1, -1 };   // cross-module continuity: last melodic-line pitch per voice on this track
                foreach (var item in tr.Items)
                {
                    cursor += item.SilenceBefore;
                    PlaceItem(T, item, cursor, resolveRiff, carry);
                    cursor += TimelineProject.ItemLength(item, resolveRiff);
                }
                list.Add(T);
            }
            tracks = list.ToArray();

            TrySetupMeltySynth(project);
        }

        // Build one MeltySynth.Synthesizer sized to the project (one channel per track, drums on ch 9), so a
        // single synth renders the whole arrangement. On any failure we leave synth == null (legacy path).
        void TrySetupMeltySynth(TimelineProject project)
        {
            var sf = InstrumentCatalog.SoundFontObject;
            if (sf == null) return;
            try
            {
                int need = Math.Max(16, Math.Min(1024, tracks.Length + 1));
                var settings = new MeltySynth.SynthesizerSettings(sampleRate)
                {
                    ChannelCount = need,
                    EnableReverbAndChorus = true,
                    MaximumPolyphony = 256,        // dense arrangements
                };
                synth = new MeltySynth.Synthesizer(sf, settings);
                // Headroom for the per-instrument boost: MasterVolume = the max boost factor, and each channel's
                // CC7 is scaled down by sqrt(boost/MaxBoost) so an un-boosted track keeps its exact current level
                // (net = gv² unchanged) while a boosted one can reach up to ×MaxBoost.
                synth.MasterVolume = (float)AppSettings.MaxBoostFactor;
                trackChannel = new int[tracks.Length];
                trackProgram = new int[tracks.Length];
                int nextCh = 0;
                for (int i = 0; i < tracks.Length; i++)
                {
                    var tr = project.Tracks[i];
                    bool isDrum = tr.Type == TimelineTrackType.Drum || tr.Instrument == InstrumentCatalog.DrumIndex;
                    trackProgram[i] = isDrum ? InstrumentCatalog.DrumIndex : Math.Max(0, Math.Min(127, tr.Instrument));
                    if (isDrum) { trackChannel[i] = synth.PercussionChannel; continue; } // ch 9: shared by all drum tracks
                    if (nextCh == synth.PercussionChannel) nextCh++;                      // skip the percussion channel
                    int ch = nextCh < need ? nextCh++ : need - 1;                          // clamp (absurd track counts)
                    trackChannel[i] = ch;
                }
                ApplyPrograms(project);
            }
            catch { synth = null; }
        }

        // Fixed CC2 "single note dynamics" level for the Expr. (bank 17) instruments — the app has no
        // per-note dynamics. Their CC2->attenuation modulators read this value. Restored to the neutral 100
        // now that the piano/sustained balance is correct at the synth level (the modulator-dedup + filter-
        // modulator fixes), so it no longer needs to be held low to keep sustained instruments from dominating.
        public const int MeltyDynamics = 100;

        // (Re)send each melodic channel's GM program (drums keep the percussion bank). Sustained instruments
        // that have an "Expr." variant are routed to bank 17 with a CC2 dynamics level (MuseScore-style).
        // Called at setup and Start.
        void ApplyPrograms(TimelineProject project)
        {
            for (int i = 0; i < tracks.Length; i++)
            {
                var tr = project.Tracks[i];
                bool isDrum = tr.Type == TimelineTrackType.Drum || tr.Instrument == InstrumentCatalog.DrumIndex;
                if (isDrum) continue;
                int program = Math.Max(0, Math.Min(127, tr.Instrument));
                int ch = trackChannel[i];
                bool expr = InstrumentCatalog.ExprPrograms != null && InstrumentCatalog.ExprPrograms.Contains(program);
                synth.ProcessMidiMessage(ch, 0xB0, 0, expr ? InstrumentCatalog.ExprBank : 0); // bank select
                synth.ProcessMidiMessage(ch, 0xC0, program, 0);                        // program change
                if (expr) synth.ProcessMidiMessage(ch, 0xB0, 2, MeltyDynamics);        // CC2 dynamics
            }
        }

        // ---- flattening (riffs -> per-track note attack/release events at Spb) ------------------------------

        void PlaceItem(Track tr, TimelineItem item, double startBeat, Func<Guid, Riff> resolve, int[] carry)
        {
            if (item.Module != null) PlaceLeaf(tr, item.Module, startBeat, resolve, carry);
        }

        void PlaceLeaf(Track tr, FlowModule m, double startBeat, Func<Guid, Riff> resolve, int[] carry)
        {
            PlaceRiffNotes(tr, RiffForModule(m, resolve), startBeat);
            // A chord that carries a MELODIC CELL plays it as a 2nd voice on the same track (same instrument, same time).
            if (m is PatternGeneratorModule pgm && pgm.HasMelodic)
                PlaceRiffNotes(tr, PatternGenerator.GenerateMelodic(pgm, melodyKey), startBeat);
            // A MELODIC LINE: the engine picks pitches from the harmony in effect at each beat (carrying continuity forward).
            else if (m is MelodicLineModule ml)
                PlaceRiffNotes(tr, MelodicLineEngine.GenerateLine(ml, melodyProject, resolve, melodyKey, startBeat, carry), startBeat);
        }

        void PlaceRiffNotes(Track tr, Riff riff, double startBeat)
        {
            if (riff?.Notes == null) return;
            int spq = riff.SlicesPerQuarter > 0 ? riff.SlicesPerQuarter : 4;
            double scale = (double)Spb / spq;             // riff slices -> global slices
            int off = (int)Math.Round(startBeat * Spb);
            foreach (var n in riff.Notes)
            {
                if (n.Note < 0 || n.Note >= NoteCount) continue;
                int s = off + (int)Math.Round(n.Start * scale);
                int e = off + (int)Math.Round(n.End * scale);
                if (e <= s) e = s + 1;
                if (s < 0) s = 0;
                if (s >= totalSlices) continue;
                if (e > totalSlices) e = totalSlices;
                (tr.AttackAt[s] ?? (tr.AttackAt[s] = new List<int>())).Add(n.Note);
                (tr.AttackVel[s] ?? (tr.AttackVel[s] = new List<int>())).Add(MetricVelocity(s));
                (tr.ReleaseAt[e] ?? (tr.ReleaseAt[e] = new List<int>())).Add(n.Note);
            }
        }

        static Riff RiffForModule(FlowModule m, Func<Guid, Riff> resolve)
        {
            switch (m)
            {
                case PlayRiffModule pr: return resolve?.Invoke(pr.RiffId);
                case PatternGeneratorModule pg: return PatternGenerator.Generate(pg);
                case DrumPatternModule d: return DrumPattern.Generate(d);
                case CadenceModule cm: return PatternGenerator.GenerateCadence(cm);
                default: return null;
            }
        }

        // ---- tempo / volume --------------------------------------------------------

        double BpmAt(double beat)
        {
            double bpm = tempoBpm[0];
            for (int i = 0; i < tempoBeat.Length; i++) { if (tempoBeat[i] <= beat + 1e-9) bpm = tempoBpm[i]; else break; }
            return bpm;
        }

        // Absolute volume at a beat: base before the first point, linear between points (override, not a
        // multiply — matches the volume lane), flat after the last point. Lead-in ramps base -> first.
        static double TrackGain(Track tr, double beat)
        {
            var a = tr.Autom;
            if (a == null || a.Count == 0) return tr.BaseVol;
            if (beat <= a[0].Beat)
            {
                if (a[0].Beat <= 1e-9) return a[0].Volume;
                double f = Math.Max(0, beat) / a[0].Beat;
                return tr.BaseVol + (a[0].Volume - tr.BaseVol) * f;
            }
            for (int i = 0; i < a.Count - 1; i++)
                if (beat >= a[i].Beat && beat <= a[i + 1].Beat)
                {
                    double span = a[i + 1].Beat - a[i].Beat;
                    double f = span > 1e-9 ? (beat - a[i].Beat) / span : 0;
                    return a[i].Volume + (a[i + 1].Volume - a[i].Volume) * f;
                }
            return a[a.Count - 1].Volume;
        }

        // Samples-per-slice at the current slice's tempo (gains are computed per track in SynthTrack now).
        void UpdateInterval()
        {
            double beat = sliceIndex / (double)Spb;
            interval = (60.0 / BpmAt(beat)) / Spb * sampleRate;
            if (interval < 1) interval = 1;
        }

        // ---- playback --------------------------------------------------------------

        public void Start()
        {
            sliceIndex = Math.Max(0, Math.Min(totalSlices, (int)Math.Round(StartBeat * Spb)));
            SampleAtStart = sliceSampleStart[sliceIndex];
            t = 0; endedRaised = false; UpdateInterval(); playing = true;
            if (synth != null)
            {
                synth.Reset();                       // kill any ringing notes + restore controllers
                ApplyPrograms(melodyProject);        // Reset() clears program changes -> re-send them
                mDispatched = sliceIndex - 1;        // dispatch from the start slice onward
            }
        }

        /// <summary>Beat at an absolute sample offset (from beat 0), via the tempo map. For a playback cursor.</summary>
        public double BeatAtSample(long abs)
        {
            if (abs <= 0) return 0;
            if (abs >= sliceSampleStart[totalSlices]) return totalSlices / (double)Spb;
            int lo = 0, hi = totalSlices;
            while (lo + 1 < hi) { int mid = (lo + hi) >> 1; if (sliceSampleStart[mid] <= abs) lo = mid; else hi = mid; }
            long s0 = sliceSampleStart[lo], s1 = sliceSampleStart[lo + 1];
            double frac = s1 > s0 ? (abs - s0) / (double)(s1 - s0) : 0;
            return (lo + frac) / Spb;
        }
        public void Stop() { playing = false; }

    

        public override int Read(short[] buffer, int offset, int sampleCount)
        {
            if (!playing) return 0;
            if (synth != null) return ReadMelty(buffer, offset, sampleCount);
            return 0;
        }

        // ---- MeltySynth render path : advance the tempo cursor slice-by-slice, dispatch each slice's
        //      note on/off to the shared synth, render the slice's samples, then downmix stereo -> mono. ----
        int ReadMelty(short[] buffer, int offset, int sampleCount)
        {
            if (mL == null || mL.Length < sampleCount) { mL = new float[sampleCount]; mR = new float[sampleCount]; }

            ApplyChannelVolumes(CurrentBeat); // per-buffer: automation + mute/solo via CC7

            int done = 0;
            while (done < sampleCount)
            {
                if (sliceIndex < totalSlices && sliceIndex > mDispatched)
                {
                    for (int sl = mDispatched + 1; sl <= sliceIndex; sl++) DispatchSlice(sl);
                    mDispatched = sliceIndex;
                }
                // Render at most to the end of the current slice so the next slice's events land on time.
                int remain = sliceIndex < totalSlices ? (int)Math.Ceiling(interval - t) : (sampleCount - done);
                if (remain < 1) remain = 1;
                int n = Math.Min(remain, sampleCount - done);
                synth.Render(mL.AsSpan(done, n), mR.AsSpan(done, n));
                for (int k = 0; k < n; k++)
                {
                    t += 1;
                    if (t >= interval && sliceIndex < totalSlices) { t -= interval; sliceIndex++; UpdateInterval(); }
                }
                done += n;
            }

            double g = AudioFormat.OutputGain;
            for (int s = 0; s < sampleCount; s++)
            {
                double v = (mL[s] + mR[s]) * 0.5 * g;
                v = AudioFormat.SoftClip(v); // musical limiter instead of a hard clamp (no crackle on boost)
                buffer[offset + s] = (short)(v * short.MaxValue);
            }

            if (sliceIndex >= totalSlices && synth.ActiveVoiceCount == 0) RaiseEnded();
            return sampleCount;
        }

        void DispatchSlice(int sl)
        {
            if (sl < 0 || sl > totalSlices) return;
            for (int ti = 0; ti < tracks.Length; ti++)
            {
                int ch = trackChannel[ti];
                var rel = tracks[ti].ReleaseAt[sl];
                if (rel != null) foreach (int note in rel) synth.NoteOff(ch, note + 12); // note index -> MIDI key
                var att = tracks[ti].AttackAt[sl];
                if (att != null)
                {
                    var vel = tracks[ti].AttackVel[sl];
                    for (int k = 0; k < att.Count; k++)
                        synth.NoteOn(ch, att[k] + 12, vel != null && k < vel.Count ? vel[k] : MeltyVelocity);
                }
            }
        }

        void ApplyChannelVolumes(double beat)
        {
            double maxBoost = AppSettings.MaxBoostFactor;
            var settings = AppSettings.Instance;
            for (int ti = 0; ti < tracks.Length; ti++)
            {
                double gv = TrackGain(tracks[ti], beat) * tracks[ti].Mix; // Mix == 0 -> muted / non-soloed
                // Per-instrument boost: CC7 is scaled by sqrt(boost/MaxBoost); combined with MasterVolume=MaxBoost
                // (and MeltySynth squaring CC7) the net gain becomes gv²·boost — un-boosted stays gv².
                double boost = trackProgram != null ? settings.BoostGain(trackProgram[ti]) : 1.0;
                double ccGain = gv * Math.Sqrt(boost / maxBoost);
                int cc = (int)Math.Round(Math.Max(0.0, Math.Min(1.0, ccGain)) * 127);
                synth.ProcessMidiMessage(trackChannel[ti], 0xB0, 7, cc); // CC7 = channel volume
            }
        }

        

        void RaiseEnded() { if (endedRaised) return; endedRaised = true; playing = false; Ended?.Invoke(); }
    }
}
