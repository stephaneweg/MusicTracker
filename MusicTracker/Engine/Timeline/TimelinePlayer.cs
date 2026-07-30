using MeltySynth;
using MusicTracker.Engine.Flow;
using MusicTracker.Engine.Timeline.Effects;
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
            /// <summary>Snapshots triés des lanes d'automation additionnelles (pan/expression/modulation/sustain/reverb/chorus/pitchbend),
            /// prises au démarrage. Ajouter/retirer une lane en cours de lecture n'a pas d'effet audible jusqu'au prochain Start —
            /// même règle qu'avec <see cref="Autom"/>, et raisonnable puisque l'utilisateur relance de toute façon la lecture après édition.</summary>
            public Dictionary<AutomationParam, List<AutomationPoint>> LaneSnaps;
            // La piste de projet dont cette voix a été construite. Les valeurs de mixage (volume / pan / mute /
            // solo / réverbe) y sont lues EN DIRECT à chaque buffer — mais par cette référence, jamais par un
            // index dans project.Tracks : le thread d'interface peut insérer, échanger ou retirer une piste
            // pendant la lecture (dupliquer / monter / descendre / supprimer), et un index désignerait alors une
            // autre piste (mauvais mixage) voire lèverait ArgumentOutOfRangeException sur le thread de
            // pré-rendu — où LookaheadBuffer.Produce n'a pas de try/catch, donc arrêt du processus.
            public TimelineTrack Src;

            // ---- rendu par piste (1 synth par voix — voir TrySetupMeltySynth) --------------------------------
            public MeltySynth.Synthesizer Synth;   // synthé dédié à cette piste : 1 canal utile (0 mélodique, 9 batterie)
            public int Channel;                     // 0 (mélodique) ou 9 (batterie) — sur SON synth
            public int Program;                     // programme GM (drumIndex si batterie) — utilisé pour le boost par instrument
            public float[] BufL, BufR;              // buffer de rendu de la piste — alloué à la volée quand la taille change
            public IAudioEffect[] Effects;          // chaîne d'inserts instanciée au démarrage (snapshot, voir SnapshotInserts)
            /// <summary>Sources parallèles à <see cref="Effects"/> — mêmes objets que dans <c>Src.Inserts</c> au moment du Start,
            /// gardés pour relire EN LIVE les paramètres (sliders du dialogue d'effet) et le toggle Enabled sans avoir à
            /// relancer la lecture. Ajouter/retirer un effet reste snapshot-at-Start (la liste elle-même peut être mutée
            /// par le thread d'interface — on ne pourrait pas la parcourir sans risque).</summary>
            public TrackEffectData[] EffectsSrc;
            public float PeakL, PeakR;              // dernier peak par canal — lu en LIVE par le mixeur pour les vu-mètres

            /// <summary>Instrument VSTi qui remplace <see cref="Synth"/> pour cette piste (mutuellement exclusif :
            /// une piste en mode VSTi n'a pas de Synth alloué). Null = mode MeltySynth (voir <see cref="TimelineTrack.VstiPath"/>).
            /// Instancié dans <see cref="TrySetupMeltySynth"/> et disposé par <see cref="TimelinePlayer.Dispose"/>.
            /// Format-agnostique via <see cref="IVstInstrumentHost"/> — <c>VstInstrument</c> (.dll) et
            /// <c>Vst3Instrument</c> (.vst3) partagent la même surface d'appel.</summary>
            public MusicTracker.Engine.Timeline.Effects.IVstInstrumentHost Vsti;
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

        // DIAGNOSTIC : n'envoyer aucun CC7, donc laisser le volume de canal au défaut MeltySynth (100/127) comme
        // le fait l'aperçu. Sert à isoler l'effet du schéma MasterVolume=boost max + CC7 compensé, soupçonné dans
        // l'écart de réverbération entre la lecture et l'aperçu. Le volume par piste, le mute et le solo sont
        // évidemment inopérants quand c'est faux — à ne pas laisser à false en usage normal.
        public static bool ChannelVolumeCC = true;

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

        // ---- swing (croches inégales) --------------------------------------------------------------------------
        // Slice inside the beat where the OFF-eighth lands: Spb/2 = straight, 16/24 = full triplet. Computed once from
        // the project's SwingPercent; 0 disables the warp entirely (the straight path costs nothing).
        readonly int swingPoint;

        // Piecewise-linear time warp of a position INSIDE one beat: [0, Spb/2] stretches onto [0, swingPoint] and
        // [Spb/2, Spb] compresses onto [swingPoint, Spb]. Monotonic, fixes the beat boundaries, and delays the
        // finer subdivisions along with the eighths (a 16th run keeps its shape) — the usual DAW behaviour.
        int SwingSlice(int s)
        {
            if (swingPoint <= 0 || s <= 0) return s;
            int beat = s / Spb, within = s - beat * Spb, half = Spb / 2;
            double w = within <= half
                ? within * (swingPoint / (double)half)
                : swingPoint + (within - half) * ((Spb - swingPoint) / (double)half);
            return beat * Spb + (int)Math.Round(w);
        }
        // 1 synth PAR PISTE (voir TrySetupMeltySynth) : chaque piste rend son propre buffer stéréo, la chaîne
        // d'inserts s'y applique, puis on somme dans le master (soft-clip + sortie short). Historiquement, un
        // seul synthé était partagé par toutes les pistes via le multiplexage MIDI par canal — mais on ne pouvait
        // alors ni router un traitement DIFFÉRENT par piste, ni mixer des sorties audio séparées, ni éditer les
        // paramètres d'insert (EQ, comp, delay, sat) qui vivent en dehors du synthé.
        int mDispatched;                    // last slice whose note-on/off were sent to the synths
        float[] mL, mR;                     // master mix scratch (stereo, alloué à la volée)
        float[] mIn;                        // scratch mono pour le pré-mix des inserts master (réutilisé)
        // Chaîne d'inserts du bus master (snapshot au démarrage, comme les lanes) + peaks lus par le vu-mètre master.
        IAudioEffect[] masterFx;
        TrackEffectData[] masterFxSrc;      // parallèle à masterFx — pour relecture live des paramètres et Enabled
        public float MasterPeakL { get; private set; }
        public float MasterPeakR { get; private set; }
        readonly TimelineProject srcProject;
        readonly bool meltyReady;
        // Pool de tâches réutilisées pour le rendu parallèle par piste — évite l'alloc à chaque buffer audio.
        readonly Task[] renderTasks;

        public event Action Ended;

        /// <summary>Beat at which <see cref="Start"/> begins playback (0 = from the top). Set before Start().</summary>
        public double StartBeat { get; set; }

        /// <summary>Current playhead position in beats (for a UI cursor).</summary>
        public double CurrentBeat => (sliceIndex + (interval > 0 ? t / interval : 0)) / (double)Spb;

        /// <summary>Total length in samples (sum of per-slice durations under the tempo map) — for export progress.</summary>
        public long EstimatedTotalSamples { get; }

        public TimelinePlayer(TimelineProject project, Func<Guid, Riff> resolveRiff, int sampleRate = 0)
            : base(sampleRate <= 0 ? AudioFormat.SampleRate : sampleRate, 2) // STEREO out (per-track pan via CC10)
        {
            this.sampleRate = sampleRate <= 0 ? AudioFormat.SampleRate : sampleRate;
            this.melodyKey = project.Key ?? new Engine.Score.KeySignature();
            this.melodyProject = project;

            // Straight (or an out-of-range value) leaves swingPoint at 0 = no warp at all.
            double sw = project.SwingPercent;
            swingPoint = (sw > 50.5 && sw < 90) ? (int)Math.Round(Spb * sw / 100.0) : 0;

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
                    LaneSnaps = SnapshotLanes(tr.AutomationLanes),
                    Src = tr,
                };
                double cursor = 0;
                var carry = new[] { -1, -1, -1, -1, -1, -1, -1, -1, -1 };   // cross-module continuity per voice: [0..2] last pitch, [3..5] last downbeat, [6..8] its chord root
                foreach (var item in tr.Items)
                {
                    cursor += item.SilenceBefore;
                    PlaceItem(T, item, cursor, resolveRiff, carry);
                    cursor += TimelineProject.ItemLength(item, resolveRiff);
                }
                list.Add(T);
            }
            tracks = list.ToArray();
            renderTasks = new Task[tracks.Length];

            srcProject = project;
            meltyReady = TrySetupMeltySynth(project);
        }

        /// <summary>Peak instantané d'une piste (pour vu-mètre) — 0..1+, lu par la fenêtre mixeur pendant la lecture.</summary>
        public float TrackPeakL(int index) => (index >= 0 && index < tracks.Length) ? tracks[index].PeakL : 0f;
        public float TrackPeakR(int index) => (index >= 0 && index < tracks.Length) ? tracks[index].PeakR : 0f;

        // Un MeltySynth.Synthesizer PAR PISTE (16 canaux nominaux, on n'en utilise qu'un par piste : 0
        // pour l'instrumental, 9 pour la batterie). Le SoundFont, lui, est PARTAGÉ entre synthés — c'est
        // juste le descripteur des données de patchs, seule la voice-pool doit être indépendante. On garde
        // aussi la logique historique du « boost par instrument » (MasterVolume × CC7) : ici, comme chaque
        // piste a son propre MasterVolume, on peut simplement le fixer au boost et laisser CC7 varier
        // linéairement — le résultat audio est identique à l'ancien schéma partagé mais plus lisible.
        bool TrySetupMeltySynth(TimelineProject project)
        {
            var sf = InstrumentCatalog.SoundFontObject;
            if (sf == null) return false;
            try
            {
                for (int i = 0; i < tracks.Length; i++)
                {
                    var tr = tracks[i].Src;
                    if (tr == null) continue;
                    // Piste en mode VSTi : on instancie un VstInstrument à la place du synthé MeltySynth (le
                    // pipeline est identique en aval — RenderTrackSlice dévie sur Vsti.Render). Aucun Synth
                    // MeltySynth alloué → CPU/RAM économisés. Un plugin introuvable ou qui crash au load passe
                    // silencieusement en bypass (piste muette) sans faire tomber toute la lecture.
                    if (!string.IsNullOrEmpty(tr.VstiPath))
                    {
                        // Routage par extension : .vst3 → hoster P/Invoke Steinberg, sinon .dll VST2 via VST.NET.
                        var ext = System.IO.Path.GetExtension(tr.VstiPath);
                        MusicTracker.Engine.Timeline.Effects.IVstInstrumentHost vsti;
                        if (string.Equals(ext, ".vst3", StringComparison.OrdinalIgnoreCase))
                            vsti = new Vst3Instrument(tr.VstiPath, sampleRate);
                        else
                            vsti = new VstInstrument(tr.VstiPath, sampleRate);
                        if (!string.IsNullOrEmpty(tr.VstiStateBlob)) vsti.LoadState(tr.VstiStateBlob);

                        tracks[i].Vsti = vsti;
                        tracks[i].Channel = 0;   // VSTi = canal 0 par convention (les instruments ne connaissent pas le "9=batterie" de GM)
                        tracks[i].Program = 0;
                        continue;
                    }
                    // Plugin Koton NATIF (framework KotonStudio.Library, chargé depuis plugins/*.ksl). Même
                    // routage que VSTi mais l'instance vient du registre. Un id inconnu (plugin absent /
                    // supprimé) → InstantiateInstrument renvoie null → on retombe silencieusement sur
                    // MeltySynth, comme un VstiPath introuvable. L'adaptateur KotonInstrumentAdapter
                    // implémente IVstInstrumentHost donc le reste du pipeline audio (RenderTrackSlice,
                    // Dispatch, capture d'état) est inchangé.
                    if (!string.IsNullOrEmpty(tr.KotonInstrumentId))
                    {
                        var koton = MusicTracker.Engine.Timeline.Effects.KotonPluginRegistry.InstantiateInstrument(tr.KotonInstrumentId);
                        if (koton != null)
                        {
                            var adapter = new MusicTracker.Engine.Timeline.Effects.KotonInstrumentAdapter(
                                koton, sampleRate, koton.DisplayName);
                            if (!string.IsNullOrEmpty(tr.KotonInstrumentStateBlob))
                                adapter.LoadState(tr.KotonInstrumentStateBlob);
                            tracks[i].Vsti = adapter;
                            tracks[i].Channel = 0;
                            tracks[i].Program = 0;
                            continue;
                        }
                        // Sinon fallthrough → MeltySynth (plugin manquant, l'utilisateur verra un badge dans l'UI).
                    }
                    var settings = new MeltySynth.SynthesizerSettings(sampleRate)
                    {
                        ChannelCount = 16,
                        EnableReverbAndChorus = true,
                        MaximumPolyphony = 64,   // large plage pour un instrument seul — bien assez sans exploser la mémoire quand N pistes
                    };
                    var s = new MeltySynth.Synthesizer(sf, settings);
                    bool isDrum = tr.Type == TimelineTrackType.Drum || tr.Instrument == InstrumentCatalog.DrumIndex;
                    tracks[i].Channel = isDrum ? s.PercussionChannel : 0;
                    tracks[i].Program = isDrum ? InstrumentCatalog.DrumIndex : Math.Max(0, Math.Min(127, tr.Instrument));
                    tracks[i].Synth = s;
                }
                ApplyPrograms(project);
                return true;
            }
            catch
            {
                for (int i = 0; i < tracks.Length; i++) { tracks[i].Synth = null; if (tracks[i].Vsti != null) { try { tracks[i].Vsti.Dispose(); } catch { } tracks[i].Vsti = null; } }
                return false;
            }
        }

        // Fixed CC2 "single note dynamics" level for the Expr. (bank 17) instruments — the app has no
        // per-note dynamics. Their CC2->attenuation modulators read this value. Restored to the neutral 100
        // now that the piano/sustained balance is correct at the synth level (the modulator-dedup + filter-
        // modulator fixes), so it no longer needs to be held low to keep sustained instruments from dominating.
        public const int MeltyDynamics = 100;

        // (Re)send each melodic channel's GM program (drums keep the percussion bank). Sustained instruments
        // that have an "Expr." variant are routed to bank 17 with a CC2 dynamics level (MuseScore-style).
        // Called at setup and Start.
        // Le paramètre `project` n'est plus lu : chaque voix connaît sa piste source (Track.Src). Indexer
        // project.Tracks ici était faux dès qu'une piste avait été insérée / échangée / retirée pendant la
        // lecture, et ApplyPrograms est rappelée à chaque Start().
        void ApplyPrograms(TimelineProject project)
        {
            var settings = AppSettings.Instance;
            for (int i = 0; i < tracks.Length; i++)
            {
                var tr = tracks[i].Src;
                var synth = tracks[i].Synth;
                // Une piste en mode VSTi n'a pas de synth MeltySynth (Vsti != null en substitution) : ni patch
                // GM à envoyer, ni MasterVolume à régler — le programme est choisi dans la GUI du plugin.
                if (tr == null || synth == null) continue;
                // MasterVolume individuel = le boost par instrument. Combiné à CC7 en gain LINÉAIRE (piloté
                // par ApplyChannelAutomation), le net reste identique à l'ancien schéma partagé (net = gv²·boost),
                // mais sans le facteur sqrt() qui plafonnait CC7 en fonction du max de boost global.
                double boost = settings.BoostGain(tracks[i].Program);
                synth.MasterVolume = (float)Math.Max(0.01, boost);
                bool isDrum = tr.Type == TimelineTrackType.Drum || tr.Instrument == InstrumentCatalog.DrumIndex;
                int ch = tracks[i].Channel;
                if (isDrum)
                {
                    // Select the track's drum KIT on THIS synth's percussion channel (bank stays 128; only the patch changes).
                    // Puisque chaque piste batterie a son propre synth, deux kits différents cohabitent sans écrasement.
                    synth.ProcessMidiMessage(ch, 0xC0, InstrumentCatalog.DrumKitProgram(tr.DrumKit), 0);
                    continue;
                }
                int program = Math.Max(0, Math.Min(127, tr.Instrument));
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
            // A MELODIC POLYRHYTHM: same engine, but the rhythm comes from the module's euclidean layers.
            else if (m is MelodicPolyModule mp)
                PlaceRiffNotes(tr, MelodicEuclid.Generate(mp, melodyProject, resolve, melodyKey, startBeat, carry), startBeat);
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
                int straight = off + (int)Math.Round(n.Start * scale); // metric position (accent), before the swing delay
                int s = SwingSlice(straight);
                int e = SwingSlice(off + (int)Math.Round(n.End * scale));
                if (e <= s) e = s + 1;
                if (s < 0) s = 0;
                if (s >= totalSlices) continue;
                if (e > totalSlices) e = totalSlices;
                (tr.AttackAt[s] ?? (tr.AttackAt[s] = new List<int>())).Add(n.Note);
                (tr.AttackVel[s] ?? (tr.AttackVel[s] = new List<int>())).Add(MetricVelocity(straight));
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
                case PolyDrumModule pd: return PolyDrum.Generate(pd);
                case CadenceModule cm: return PatternGenerator.GenerateCadence(cm);
                case PolyChordModule pc: return PolyChord.Generate(pc);
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

        // Snapshot (tri par beat) des lanes actives d'une piste, indexé par paramètre. Une lane vide ou
        // désactivée n'entre PAS dans le dictionnaire : la lecture retombe alors sur la valeur statique de la piste.
        static Dictionary<AutomationParam, List<AutomationPoint>> SnapshotLanes(List<AutomationLane> lanes)
        {
            var d = new Dictionary<AutomationParam, List<AutomationPoint>>();
            if (lanes == null) return d;
            foreach (var ln in lanes)
            {
                if (ln == null || !ln.Enabled || ln.Points == null || ln.Points.Count == 0) continue;
                var sorted = ln.Points.OrderBy(p => p.Beat).ToList();
                d[ln.Param] = sorted;    // une seule lane par paramètre : la dernière définie gagne
            }
            return d;
        }

        // Interpolation générique d'une courbe : valeur par défaut avant le 1er point (rampe base -> premier
        // si le premier est en 0), linéaire entre points, plate après le dernier. Même sémantique que TrackGain.
        static double SampleCurve(List<AutomationPoint> pts, double defaultVal, double beat)
        {
            if (pts == null || pts.Count == 0) return defaultVal;
            if (beat <= pts[0].Beat)
            {
                if (pts[0].Beat <= 1e-9) return pts[0].Value;
                double f = Math.Max(0, beat) / pts[0].Beat;
                return defaultVal + (pts[0].Value - defaultVal) * f;
            }
            for (int i = 0; i < pts.Count - 1; i++)
                if (beat >= pts[i].Beat && beat <= pts[i + 1].Beat)
                {
                    double span = pts[i + 1].Beat - pts[i].Beat;
                    double f = span > 1e-9 ? (beat - pts[i].Beat) / span : 0;
                    return pts[i].Value + (pts[i + 1].Value - pts[i].Value) * f;
                }
            return pts[pts.Count - 1].Value;
        }

        // 0..1 -> 0..127 (clamp).
        static int CcByte(double v)
        {
            int i = (int)Math.Round(v * 127);
            return i < 0 ? 0 : (i > 127 ? 127 : i);
        }

        // Absolute volume at a beat: base before the first point, linear between points (override, not a
        // multiply — matches the volume lane), flat after the last point. Lead-in ramps base -> first.
        static double TrackGain(Track tr, double beat) => TrackGain(tr.Autom, tr.BaseVol, beat);

        /// <summary>Volume absolu d'une piste à un temps donné. PUBLIC parce que l'export MIDI trace la même courbe
        /// en CC7 : les deux doivent lire la rampe d'automation au même endroit, sinon le fichier exporté et la
        /// lecture divergent silencieusement. <paramref name="a"/> doit être TRIÉ par <see cref="VolumePoint.Beat"/>.</summary>
        public static double TrackGain(List<VolumePoint> a, double baseVol, double beat)
        {
            if (a == null || a.Count == 0) return baseVol;
            if (beat <= a[0].Beat)
            {
                if (a[0].Beat <= 1e-9) return a[0].Volume;
                double f = Math.Max(0, beat) / a[0].Beat;
                return baseVol + (a[0].Volume - baseVol) * f;
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

        // ---- A-B loop --------------------------------------------------------------------------------
        // Loop=true → wrap the [StartBeat (A), LoopEndBeat (B)] region seamlessly. Loop=false → B is ignored,
        // playback runs from A straight to the end (normal playback).
        public bool Loop;
        public double LoopEndBeat;          // B in beats (only used while Loop is true)
        int loopStartSlice, loopEndSlice;   // resolved region in slices
        long loopSamples;                   // audible samples per loop cycle (for the loop-aware cursor)

        void RecomputeLoop()
        {
            loopStartSlice = Math.Max(0, Math.Min(totalSlices, (int)Math.Round(StartBeat * Spb)));
            // B only bounds playback while LOOPING; with the loop off we always play through to the end.
            int b = (Loop && LoopEndBeat > StartBeat + 1e-9) ? Math.Min(totalSlices, (int)Math.Round(LoopEndBeat * Spb)) : totalSlices;
            if (b <= loopStartSlice) b = totalSlices;
            loopEndSlice = b;
            loopSamples = sliceSampleStart[loopEndSlice] - sliceSampleStart[loopStartSlice];
        }

        /// <summary>Recompute the loop region live (after Loop / LoopEndBeat changed during playback).</summary>
        public void ApplyLoop() => RecomputeLoop();

        public void Start()
        {
            sliceIndex = Math.Max(0, Math.Min(totalSlices, (int)Math.Round(StartBeat * Spb)));
            SampleAtStart = sliceSampleStart[sliceIndex];
            RecomputeLoop();
            t = 0; endedRaised = false; UpdateInterval(); playing = true;
            if (meltyReady)
            {
                for (int i = 0; i < tracks.Length; i++)
                {
                    var s = tracks[i].Synth;
                    if (s != null) s.Reset();
                }
                ApplyPrograms(melodyProject);        // Reset() clears program changes -> re-send them
                // Snapshot des inserts (par piste + master) au démarrage : instance IAudioEffect créée UNE fois,
                // avec en parallèle la référence à TrackEffectData d'origine. Les PARAMÈTRES et l'état Enabled
                // sont ensuite relus À CHAQUE BUFFER (voir RenderTrackSlice et la boucle master) → les sliders du
                // dialogue d'effet s'entendent immédiatement, plus besoin de relancer la lecture. Ajouter/retirer
                // un effet reste snapshot-at-Start (la liste Src.Inserts peut être mutée par le thread d'UI et on
                // ne veut pas la parcourir depuis l'audio thread).
                for (int i = 0; i < tracks.Length; i++)
                    SnapshotInserts(tracks[i].Src?.Inserts, out tracks[i].Effects, out tracks[i].EffectsSrc);
                SnapshotInserts(srcProject?.MasterInserts, out masterFx, out masterFxSrc);
                mDispatched = sliceIndex - 1;        // dispatch from the start slice onward
                for (int i = 0; i < tracks.Length; i++) { tracks[i].PeakL = 0; tracks[i].PeakR = 0; }
                MasterPeakL = 0; MasterPeakR = 0;
            }
        }

        /// <summary>Renvoie true si au moins une piste a un VSTi (aide TimelineScreen à savoir s'il faut prewarm).</summary>
        public bool HasAnyVsti()
        {
            for (int i = 0; i < tracks.Length; i++) if (tracks[i].Vsti != null) return true;
            return false;
        }

        /// <summary>Warm-up des VSTi : envoie de vraies note-on/off à velo=1 sur chaque VSTi puis rend
        /// <paramref name="frames"/> samples SANS toucher au sliceIndex. Objectif : forcer les plugins
        /// sample-based (Cozy Piano etc.) à CHARGER leurs samples — le chargement est déclenché par le
        /// note-on, pas par le process() seul. Silence audible = zéro (velo=1 + rendu vers buffer jetable).
        /// On sweep les octaves médianes (C2 → C6) pour couvrir la plage utile de la plupart des instruments ;
        /// un plugin qui a des velocity layers profondes ne sera pas 100% chauffé mais assez pour supprimer
        /// le silence initial audible.
        ///
        /// **DOIT être appelée sur le MÊME THREAD que celui qui appellera Render() ensuite** (le thread producer
        /// du LookaheadBuffer) — sinon plusieurs plugins natifs détectent le changement de thread et cassent
        /// leur état. C'est pour ça que ce n'est PAS appelé depuis Start() (thread UI) mais depuis le callback
        /// prewarm de LookaheadBuffer qui s'exécute sur le producer thread.</summary>
        public void PrewarmVstiSilently(int frames)
        {
            if (frames <= 0) return;
            const int Block = 512;
            var l = new float[Block];
            var r = new float[Block];
            // Accord de 5 notes tenu simultanément (5 octaves centrées) — envoyer tous les note-on
            // d'un coup force le plugin à charger tous les samples nécessaires EN PARALLÈLE, plutôt
            // que séquentiellement (ce qui laisse le sample-loader lazy avec des trous). Velo=1 pour
            // être quasi-inaudible même si un plugin buggé leakait vers le buffer (jetable de toute
            // façon). Note : sur les plugins qui gardent du state DLL statique entre instances,
            // le 2e Play du même projet ré-instancie le plugin — les samples doivent se recharger,
            // d'où le besoin de re-prewarmer à chaque Play.
            var notes = new[] { 36, 48, 60, 72, 84 };  // C2, C3, C4, C5, C6
            int holdFrames = frames * 2 / 3;   // ~2s tenues
            int releaseFrames = frames - holdFrames;

            for (int i = 0; i < tracks.Length; i++)
            {
                var v = tracks[i].Vsti;
                if (v == null) continue;

                // Tous les note-on d'un coup
                foreach (var note in notes)
                {
                    try { v.NoteOn(0, note, 1); } catch { }
                }
                RenderSilent(v, l, r, holdFrames);
                // Tous les note-off d'un coup
                foreach (var note in notes)
                {
                    try { v.NoteOff(0, note); } catch { }
                }
                RenderSilent(v, l, r, releaseFrames);
            }
        }

        static void RenderSilent(IVstInstrumentHost v, float[] l, float[] r, int frames)
        {
            int done = 0;
            while (done < frames)
            {
                int n = Math.Min(l.Length, frames - done);
                try { v.Render(l.AsSpan(0, n), r.AsSpan(0, n)); } catch { return; }
                done += n;
            }
        }

        /// <summary>Instancie la chaîne d'inserts + garde la référence à la donnée source EN PARALLÈLE (mêmes index).
        /// Un d.Enabled == false au moment du Start est CONSERVÉ dans le snapshot (rempli quand même) : le toggle
        /// est relu live par le renderer, ce qui permet d'activer/désactiver un effet sans relancer la lecture.
        /// Les d == null (ne devrait pas arriver) sont sautés.</summary>
        void SnapshotInserts(List<TrackEffectData> src, out IAudioEffect[] outFx, out TrackEffectData[] outSrc)
        {
            if (src == null || src.Count == 0) { outFx = Array.Empty<IAudioEffect>(); outSrc = Array.Empty<TrackEffectData>(); return; }
            var fxList = new List<IAudioEffect>(src.Count);
            var dList = new List<TrackEffectData>(src.Count);
            foreach (var d in src)
            {
                if (d == null) continue;
                var fx = EffectFactory.Create(d, sampleRate);
                if (fx != null) { fx.Reset(); fxList.Add(fx); dList.Add(d); }
            }
            outFx = fxList.ToArray();
            outSrc = dList.ToArray();
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

        /// <summary>Audible beat for the playback cursor, given the samples consumed since Start. Loop-aware: while
        /// looping, it folds the consumed count into the [A,B] region so the cursor sweeps A→B and jumps back.</summary>
        public double PlayheadBeat(long consumedSinceStart)
        {
            if (Loop && loopSamples > 0)
                return BeatAtSample(SampleAtStart + (consumedSinceStart % loopSamples));
            return BeatAtSample(SampleAtStart + consumedSinceStart);
        }
        public void Stop() { playing = false; }

    

        public override int Read(short[] buffer, int offset, int sampleCount)
        {
            if (!playing) return 0;
            if (meltyReady) return ReadMelty(buffer, offset, sampleCount);
            return 0;
        }

        // ---- MeltySynth render path : advance the tempo cursor slice-by-slice, dispatch each slice's note on/off
        //      to the shared synth, render the slice's samples, then write INTERLEAVED STEREO (L,R per frame).
        //      `sampleCount` is the number of shorts to fill (= 2 × frames); pan is baked into mL/mR by the synth
        //      (per-track CC10 in ApplyChannelAutomation), so we just emit L and R. ----
        // ---- Rendu (1 synth par piste) : avance le curseur slice-by-slice, dispatche les note-on/off au
        //      synth de chaque piste sur SON canal, rend chaque piste en PARALLÈLE dans son buffer float,
        //      applique la chaîne d'inserts de la piste, somme dans le master, applique les inserts master,
        //      soft-clip, sortie stéréo entrelacée L,R. Le pan est baké dans les buffers via CC10 sur chaque synth.
        int ReadMelty(short[] buffer, int offset, int sampleCount)
        {
            int frames = sampleCount / 2;
            EnsureBuffers(frames);

            ApplyChannelAutomation(CurrentBeat);

            int end = loopEndSlice > 0 ? loopEndSlice : totalSlices;
            Array.Clear(mL, 0, frames);
            Array.Clear(mR, 0, frames);

            int done = 0;
            while (done < frames)
            {
                if (sliceIndex < end && sliceIndex > mDispatched)
                {
                    for (int sl = mDispatched + 1; sl <= sliceIndex; sl++) DispatchSlice(sl);
                    mDispatched = sliceIndex;
                }
                int remain = sliceIndex < end ? (int)Math.Ceiling(interval - t) : (frames - done);
                if (remain < 1) remain = 1;
                int n = Math.Min(remain, frames - done);
                int startOff = done;
                int nn = n;

                // Rendu parallèle par piste : chaque tâche appelle SON synth (aucun conflit d'état). Seuil
                // >= 2 pistes pour éviter l'overhead d'ordonnancement sur un morceau monopiste.
                if (tracks.Length >= 2)
                {
                    for (int i = 0; i < tracks.Length; i++)
                    {
                        int ti = i;
                        renderTasks[ti] = Task.Run(() => RenderTrackSlice(ti, startOff, nn));
                    }
                    Task.WaitAll(renderTasks);
                }
                else if (tracks.Length == 1)
                {
                    RenderTrackSlice(0, startOff, nn);
                }

                // Somme des pistes dans le master.
                for (int i = 0; i < tracks.Length; i++)
                {
                    var tk = tracks[i];
                    if (tk.BufL == null) continue;
                    for (int k = 0; k < nn; k++)
                    {
                        mL[startOff + k] += tk.BufL[startOff + k];
                        mR[startOff + k] += tk.BufR[startOff + k];
                    }
                }

                for (int k = 0; k < n; k++)
                {
                    t += 1;
                    if (t >= interval && sliceIndex < end)
                    {
                        t -= interval; sliceIndex++; UpdateInterval();
                        if (sliceIndex >= end && Loop)
                        {
                            for (int ii = 0; ii < tracks.Length; ii++)
                            {
                                var ss = tracks[ii].Synth; if (ss != null) ss.NoteOffAll(false);
                                var vv = tracks[ii].Vsti; if (vv != null) vv.ProcessMidiCC(tracks[ii].Channel, 123, 0); // CC123 = All Notes Off (VSTi panic)
                            }
                            sliceIndex = loopStartSlice; mDispatched = loopStartSlice - 1; UpdateInterval();
                        }
                    }
                }
                done += n;
            }

            // Inserts master appliqués sur toute la fenêtre déjà sommée (mêmes règles live que les inserts de piste).
            if (masterFx != null)
                for (int i = 0; i < masterFx.Length; i++)
                {
                    var d = (masterFxSrc != null && i < masterFxSrc.Length) ? masterFxSrc[i] : null;
                    if (d != null && !d.Enabled) continue;
                    if (d != null) masterFx[i].Load(d.Params);
                    masterFx[i].Process(mL, mR, frames);
                }

            // Peaks master pour le vu-mètre (décay entre buffers pour rester lisible).
            float mpL = MasterPeakL * 0.85f, mpR = MasterPeakR * 0.85f;
            for (int f = 0; f < frames; f++)
            {
                float al = Math.Abs(mL[f]); if (al > mpL) mpL = al;
                float ar = Math.Abs(mR[f]); if (ar > mpR) mpR = ar;
            }
            MasterPeakL = mpL; MasterPeakR = mpR;

            double g = AudioFormat.OutputGain;
            for (int f = 0; f < frames; f++)
            {
                double l = AudioFormat.SoftClip(mL[f] * g);
                double r = AudioFormat.SoftClip(mR[f] * g);
                buffer[offset + 2 * f]     = (short)(l * short.MaxValue);
                buffer[offset + 2 * f + 1] = (short)(r * short.MaxValue);
            }

            if (!Loop && sliceIndex >= end)
            {
                // On attend que TOUTES les voix MeltySynth soient éteintes avant de signaler la fin (queue de
                // release). Pour les pistes VSTi on n'a pas d'équivalent ActiveVoiceCount fiable : on les
                // considère finies dès la dernière slice — le plugin peut avoir un peu de queue, elle sera
                // simplement coupée à Stop() (acceptable en bêta ; les pistes MeltySynth continuent de finir proprement).
                int active = 0;
                for (int i = 0; i < tracks.Length; i++) { var ss = tracks[i].Synth; if (ss != null) active += ss.ActiveVoiceCount; }
                if (active == 0) RaiseEnded();
            }
            return sampleCount;
        }

        /// <summary>Capture l'état interne (chunk base64) de chaque instrument vivant et le réécrit dans le
        /// bon champ de la piste — <see cref="TimelineTrack.VstiStateBlob"/> pour un VSTi, ou
        /// <see cref="TimelineTrack.KotonInstrumentStateBlob"/> pour un plugin Koton natif (l'adaptateur
        /// reste polymorphe : un <c>KotonInstrumentAdapter</c> est reconnaissable par son type et route
        /// l'état sur le bon slot). Appelée juste avant chaque <c>Save</c>. Un instrument absent (mode
        /// MeltySynth) ou en échec de chargement est ignoré.</summary>
        public void CaptureVstiStates()
        {
            for (int i = 0; i < tracks.Length; i++)
            {
                var v = tracks[i].Vsti;
                var tr = tracks[i].Src;
                if (v == null || tr == null || v.IsFailed) continue;
                try
                {
                    var blob = v.SaveState();
                    // Dispatcher par type : un adaptateur Koton natif écrit sur le champ Koton (l'ancien
                    // champ VstiStateBlob resterait un blob VST inapplicable au prochain Load, on ne veut
                    // pas pop-uper les deux). Un VstInstrument / Vst3Instrument garde le champ VstiStateBlob.
                    if (v is MusicTracker.Engine.Timeline.Effects.KotonInstrumentAdapter)
                        tr.KotonInstrumentStateBlob = blob;
                    else
                        tr.VstiStateBlob = blob;
                }
                catch { }
            }
        }

        /// <summary>Libère les instruments VSTi (ressources natives). À appeler par l'écran hôte quand il jette
        /// le player, en plus de <see cref="Stop"/>. Les synthés MeltySynth n'ont pas besoin de Dispose (GC-only)
        /// mais un VstInstrument tient une DLL native ouverte + des buffers non-managed — la fuite est visible.</summary>
        public void Dispose()
        {
            for (int i = 0; i < tracks.Length; i++)
            {
                var v = tracks[i].Vsti;
                if (v != null) { try { v.Dispose(); } catch { } tracks[i].Vsti = null; }
            }
        }

        void EnsureBuffers(int frames)
        {
            if (mL == null || mL.Length < frames) { mL = new float[frames]; mR = new float[frames]; }
            if (mIn == null || mIn.Length < frames) mIn = new float[frames];
            for (int i = 0; i < tracks.Length; i++)
            {
                var tk = tracks[i];
                if (tk.BufL == null || tk.BufL.Length < frames)
                {
                    tk.BufL = new float[frames];
                    tk.BufR = new float[frames];
                }
            }
        }

        // Rend une piste dans son buffer sur la tranche [startOff, startOff+nn[, puis applique la chaîne
        // d'inserts sur cette tranche et met à jour le peak. La coupure par tranche est indispensable pour
        // que les events des slices suivantes tombent à la frame exacte.
        void RenderTrackSlice(int ti, int startOff, int nn)
        {
            var tk = tracks[ti];
            if (tk.BufL == null) return;
            // Une piste en mode VSTi rend via le plugin, sinon via le synthé MeltySynth. En aval, la chaîne
            // d'inserts, le peak-meter et la sommation dans le master sont IDENTIQUES — le pipeline audio ne
            // sait pas d'où vient le buffer.
            if (tk.Vsti != null)
                tk.Vsti.Render(tk.BufL.AsSpan(startOff, nn), tk.BufR.AsSpan(startOff, nn));
            else if (tk.Synth != null)
                tk.Synth.Render(tk.BufL.AsSpan(startOff, nn), tk.BufR.AsSpan(startOff, nn));
            else return;
            var fx = tk.Effects;
            var fxSrc = tk.EffectsSrc;
            if (fx != null && fx.Length > 0)
            {
                // Les effets prennent (float[] l, float[] r, int frames) sans offset : copie tampon.
                // nn est petit (<= 4096 samples typiquement), l'alloc reste négligeable devant le coût du synth.
                var tmpL = new float[nn];
                var tmpR = new float[nn];
                Array.Copy(tk.BufL, startOff, tmpL, 0, nn);
                Array.Copy(tk.BufR, startOff, tmpR, 0, nn);
                for (int i = 0; i < fx.Length; i++)
                {
                    var d = (fxSrc != null && i < fxSrc.Length) ? fxSrc[i] : null;
                    if (d != null && !d.Enabled) continue;                     // toggle live : effet passé en bypass
                    if (d != null) fx[i].Load(d.Params);                       // params live : slider = audible immédiat
                    fx[i].Process(tmpL, tmpR, nn);
                }
                Array.Copy(tmpL, 0, tk.BufL, startOff, nn);
                Array.Copy(tmpR, 0, tk.BufR, startOff, nn);
            }
            float pl = tk.PeakL * 0.85f, pr = tk.PeakR * 0.85f;
            for (int k = 0; k < nn; k++)
            {
                float al = Math.Abs(tk.BufL[startOff + k]); if (al > pl) pl = al;
                float ar = Math.Abs(tk.BufR[startOff + k]); if (ar > pr) pr = ar;
            }
            tk.PeakL = pl; tk.PeakR = pr;
        }

        void DispatchSlice(int sl)
        {
            if (sl < 0 || sl > totalSlices) return;
            for (int ti = 0; ti < tracks.Length; ti++)
            {
                var synth = tracks[ti].Synth;
                var vsti = tracks[ti].Vsti;
                if (synth == null && vsti == null) continue;
                int ch = tracks[ti].Channel;
                var rel = tracks[ti].ReleaseAt[sl];
                if (rel != null)
                {
                    foreach (int note in rel)
                    {
                        if (vsti != null) vsti.NoteOff(ch, note + 12);
                        else synth.NoteOff(ch, note + 12);
                    }
                }
                var att = tracks[ti].AttackAt[sl];
                if (att != null)
                {
                    var vel = tracks[ti].AttackVel[sl];
                    for (int k = 0; k < att.Count; k++)
                    {
                        int v = vel != null && k < vel.Count ? vel[k] : MeltyVelocity;
                        if (vsti != null) vsti.NoteOn(ch, att[k] + 12, v);
                        else synth.NoteOn(ch, att[k] + 12, v);
                    }
                }
            }
        }

        void ApplyChannelAutomation(double beat)
        {
            // Lit les valeurs de mixage EN DIRECT sur la piste source (volume / pan / mute / solo / réverbe) —
            // par référence Track.Src, JAMAIS via un index dans melodyProject.Tracks : la liste est mutée par
            // l'UI (dupliquer / monter / descendre / supprimer une piste) et un index y désignerait la mauvaise
            // voix (mauvais mixage) voire lèverait sur le thread de pré-rendu.
            bool anySolo = false;
            for (int i = 0; i < tracks.Length; i++) if (tracks[i].Src != null && tracks[i].Src.Solo) { anySolo = true; break; }
            for (int ti = 0; ti < tracks.Length; ti++)
            {
                var synth = tracks[ti].Synth;
                var vsti = tracks[ti].Vsti;
                if (synth == null && vsti == null) continue;
                var p = tracks[ti].Src;
                int ch = tracks[ti].Channel;
                var lanes = tracks[ti].LaneSnaps;
                double baseVol = p != null ? p.Volume : tracks[ti].BaseVol;
                double mix = p != null ? ((p.Mute || (anySolo && !p.Solo)) ? 0.0 : 1.0) : tracks[ti].Mix;
                double gv = TrackGain(tracks[ti].Autom, baseVol, beat) * mix;
                // CC7 direct : chaque synth a MasterVolume = BoostGain(program), donc net = boost * (CC7 modulateur au carré)
                // = boost * gv², identique à l'ancien schéma partagé mais sans le facteur sqrt(). Le VSTi reçoit le
                // même CC7 : la plupart des synthés modernes le mappent sur leur volume de canal, ceux qui l'ignorent
                // laissent le mixeur externe (baseVol × mix appliqués plus loin ? non — pour un VSTi le seul volume
                // MIDI-piloté est CC7 ; le mixeur mute/solo/vol du header agit par CC7, pas par gain analogique).
                int cc = (int)Math.Round(Math.Max(0.0, Math.Min(1.0, gv)) * 127);
                if (ChannelVolumeCC)
                {
                    if (vsti != null) vsti.ProcessMidiCC(ch, 7, cc);
                    else synth.ProcessMidiMessage(ch, 0xB0, 7, cc);
                }
                // Pan: −1..+1 → CC10 0..127 (64 = centre). Drums share ch 9, so multiple kits share one pan.
                // Une lane Pan prend le dessus sur la valeur statique de la piste ; sinon on retombe sur p.Pan.
                double pan;
                if (lanes != null && lanes.TryGetValue(AutomationParam.Pan, out var panPts))
                    pan = SampleCurve(panPts, p != null ? p.Pan : 0.0, beat);
                else
                    pan = p != null ? p.Pan : 0.0;
                if (pan < -1.0) pan = -1.0; else if (pan > 1.0) pan = 1.0;
                int panCc = (int)Math.Round((pan + 1.0) * 0.5 * 127);
                if (vsti != null) vsti.ProcessMidiCC(ch, 10, panCc);
                else synth.ProcessMidiMessage(ch, 0xB0, 10, panCc);
                // CC91 = reverb send. MeltySynth adds it to the SoundFont's own per-instrument send
                // (Voice: reverbSend = channel + instrument, clamped), so this places the track in the room
                // without touching its level. Sending the default (40) is a no-op — a project that never
                // touched the setting sounds exactly as before. Une lane Réverbe (0..1) prend le dessus.
                // Sur un VSTi le CC91 est standard mais optionnel — la plupart des plugins l'ignorent (ils
                // n'ont pas de bus de réverb). On envoie quand même : les effets se posent au niveau du mixeur.
                int reverb;
                if (lanes != null && lanes.TryGetValue(AutomationParam.ReverbSend, out var rvPts))
                    reverb = CcByte(SampleCurve(rvPts, (p != null ? p.ReverbSend : TimelineTrack.DefaultReverb) / 127.0, beat));
                else
                    reverb = p != null ? p.ReverbSend : TimelineTrack.DefaultReverb;
                if (vsti != null) vsti.ProcessMidiCC(ch, 91, reverb);
                else synth.ProcessMidiMessage(ch, 0xB0, 91, reverb);

                if (lanes == null || lanes.Count == 0) continue;

                // Les autres CC : chaque lane -> son numéro de CC. Défaut = valeur neutre du contrôleur MIDI
                // (Expression 127, Modulation/Sustain/Chorus 0). Envoyer même valeur en boucle n'a pas d'effet
                // notable (MeltySynth court-circuite les modulateurs constants) — la simplicité prime ici.
                if (lanes.TryGetValue(AutomationParam.Expression, out var exPts))
                {
                    int v = CcByte(SampleCurve(exPts, 1.0, beat));
                    if (vsti != null) vsti.ProcessMidiCC(ch, 11, v); else synth.ProcessMidiMessage(ch, 0xB0, 11, v);
                }
                if (lanes.TryGetValue(AutomationParam.Modulation, out var modPts))
                {
                    int v = CcByte(SampleCurve(modPts, 0.0, beat));
                    if (vsti != null) vsti.ProcessMidiCC(ch, 1, v); else synth.ProcessMidiMessage(ch, 0xB0, 1, v);
                }
                if (lanes.TryGetValue(AutomationParam.Sustain, out var suPts))
                {
                    int v = CcByte(SampleCurve(suPts, 0.0, beat));
                    if (vsti != null) vsti.ProcessMidiCC(ch, 64, v); else synth.ProcessMidiMessage(ch, 0xB0, 64, v);
                }
                if (lanes.TryGetValue(AutomationParam.ChorusSend, out var chPts))
                {
                    int v = CcByte(SampleCurve(chPts, 0.0, beat));
                    if (vsti != null) vsti.ProcessMidiCC(ch, 93, v); else synth.ProcessMidiMessage(ch, 0xB0, 93, v);
                }
                if (lanes.TryGetValue(AutomationParam.PitchBend, out var pbPts))
                {
                    double v = SampleCurve(pbPts, 0.0, beat);
                    if (v < -1.0) v = -1.0; else if (v > 1.0) v = 1.0;
                    int b14 = 8192 + (int)Math.Round(v * 8191);          // -1..+1 -> 0..16383, 0 -> 8192 (centre)
                    if (b14 < 0) b14 = 0; else if (b14 > 16383) b14 = 16383;
                    if (vsti != null) vsti.SetPitchBend(ch, b14);
                    else synth.ProcessMidiMessage(ch, 0xE0, b14 & 0x7F, (b14 >> 7) & 0x7F); // 0xE0 = pitch wheel (LSB, MSB)
                }
            }
        }

        

        void RaiseEnded() { if (endedRaised) return; endedRaised = true; playing = false; Ended?.Invoke(); }
    }
}
