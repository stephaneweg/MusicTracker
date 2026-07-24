using System;
using System.Collections.Generic;
using MusicTracker.Engine;
using MusicTracker.Engine.AI;
using MusicTracker.Engine.Flow;

namespace MusicTracker.Engine.Timeline
{
    /// <summary>
    /// Builds a project document from a generative <see cref="TemplateSpec"/> — a project SOURCE (like
    /// <see cref="MusicTracker.Engine.AI.AiArrangementPlacer"/>). For a given <paramref name="seed"/> it PICKS one option
    /// from each section bank (same pick for sections sharing a name → coherent form), assembles chords (with the
    /// section's articulation + melodic cell), drums, and per-track melody (explicit riff transposed modally, or a
    /// rhythm-only melodic line), and returns a self-contained <see cref="TimelineDocument"/>. Pure model logic (no WPF):
    /// the editor loads the result via <c>LoadDocument</c> exactly like an opened file, and keeps the spec/seed itself so
    /// "Régénérer" can re-call this with a new seed.
    /// </summary>
    public static class TemplateProjectBuilder
    {
        sealed class SecPick
        {
            public List<TplChord> Prog;
            public TplArticulation Art;
            public TplMelodicCell Cell;
            public TplDrumGroove Groove;
            public Dictionary<int, TplPhrase> Phrase = new Dictionary<int, TplPhrase>();
            public Dictionary<int, TplLine> Line = new Dictionary<int, TplLine>();
        }

        /// <summary>Assemble a fresh document from <paramref name="spec"/>, expanded to <paramref name="measures"/> bars,
        /// with the pick RNG seeded by <paramref name="seed"/> (same seed → same piece).</summary>
        public static TimelineDocument Build(TemplateSpec spec, int measures, int seed)
        {
            var p = new TimelineProject();
            var riffs = new List<Riff>();
            if (spec == null) return new TimelineDocument { Project = p, Riffs = riffs };
            if (measures <= 0) measures = Math.Max(1, spec.Measure?.Count ?? 32);

            var rng = new Random(seed);
            const int spq = 4;    // chord/articulation grid
            const int rspq = 24;  // explicit-riff grid (1 temps = 24 slices, matches the app's riffs)

            // metre / key / tempo
            p.TimeSigNum = (spec.Measure != null && spec.Measure.Num > 0) ? spec.Measure.Num : 4;
            p.TimeSigDen = (spec.Measure != null && spec.Measure.Denom > 0) ? spec.Measure.Denom : 4;
            p.TimeSigScale = 1.0; p.PickupBeats = 0; p.Arrangement = null;
            p.Key = KeyFromTonality(spec.Tonality);
            int bpm = (spec.Measure != null && spec.Measure.Bpm > 0) ? spec.Measure.Bpm : 120;
            p.Tempo = new List<TempoChange> { new TempoChange { Beat = 0, Bpm = bpm } };

            // tracks (+ a drum track only if any section carries grooves)
            var instrTracks = new List<TimelineTrack>();
            if (spec.Tracks != null)
                foreach (var t in spec.Tracks)
                {
                    var tr = new TimelineTrack { Name = t.Name, Instrument = Math.Max(0, Math.Min(127, t.Program)), Type = TimelineTrackType.Instrument };
                    p.Tracks.Add(tr); instrTracks.Add(tr);
                }
            bool anyDrums = spec.Sections != null && spec.Sections.Exists(s => s.Drums != null && s.Drums.Count > 0);
            TimelineTrack drumTrack = null;
            if (anyDrums) { drumTrack = new TimelineTrack { Name = "Batterie", Instrument = InstrumentCatalog.DrumIndex, Type = TimelineTrackType.Drum }; p.Tracks.Add(drumTrack); }
            ChordModelOps.EnsureChordTrackSimple(p);
            // The chords lane is created as a piano by default; let the template choose a fitting instrument.
            if (spec.ChordProgram >= 0 && spec.ChordProgram <= 127) ChordModelOps.ChordTrack(p).Instrument = spec.ChordProgram;

            int barTemps = ChordModelOps.BarTemps(p);
            int tonicPc = MusicTheory.TonicPc(p.Key);
            int[] scalePcs = AiTranslate.ScalePcs(p.Key);

            // Per section-NAME pick cache → sections sharing a name reuse the same material (coherent form).
            var picks = new Dictionary<string, SecPick>(StringComparer.OrdinalIgnoreCase);
            SecPick PickFor(TplSection sec)
            {
                string nm = sec.Name ?? "";
                if (!picks.TryGetValue(nm, out var pk))
                {
                    pk = new SecPick
                    {
                        Prog = TemplateComposer.Pick(sec.ChordProgressions, rng) ?? new List<TplChord>(),
                        Art = TemplateComposer.Pick(sec.ChordArticulations, rng),
                        Cell = TemplateComposer.Pick(sec.ChordMelodicCells, rng),
                        Groove = TemplateComposer.Pick(sec.Drums, rng),
                    };
                    if (sec.Riffs != null) foreach (var rt in sec.Riffs) pk.Phrase[rt.TrackNum] = TemplateComposer.Pick(rt.Phrases, rng);
                    if (sec.MelodicLines != null) foreach (var mt in sec.MelodicLines) pk.Line[mt.TrackNum] = TemplateComposer.Pick(mt.Lines, rng);
                    picks[nm] = pk;
                }
                return pk;
            }

            // Build the ordered form: repeat the section list until we reach `measures` bars. Each instance's length
            // is its picked progression's bar span. Remember each instance + its chord slots (for the riff engine).
            var form = new List<(TplSection sec, SecPick pick, int startBar, int bars, List<TemplateComposer.ChordSlot> slots)>();
            int placedBars = 0, guard = 0;
            if (spec.Sections != null && spec.Sections.Count > 0)
                while (placedBars < measures && guard++ < 4000)
                    foreach (var sec in spec.Sections)
                    {
                        if (placedBars >= measures) break;
                        var pick = PickFor(sec);
                        var slots = new List<TemplateComposer.ChordSlot>();
                        double beatAcc = 0;
                        foreach (var ch in pick.Prog)
                        {
                            int beats = Math.Max(1, ch.BeatCount);
                            int qi = TemplateChords.QualityIndex(ch.Mode, ch.Quality, ch.Color);
                            slots.Add(new TemplateComposer.ChordSlot
                            {
                                RootPc = AiTranslate.RootPc(p.Key, ch.Degree),
                                ChordPcs = TemplateComposer.ChordPcs(p.Key, ch.Degree, qi),
                                StartBeat = beatAcc, Beats = beats,
                            });
                            beatAcc += beats;
                        }
                        int bars = Math.Max(1, (int)Math.Ceiling(beatAcc / Math.Max(1, barTemps)));
                        form.Add((sec, pick, placedBars, bars, slots));
                        placedBars += bars;
                    }
            p.MinBeats = Math.Max(measures, placedBars) * barTemps;

            // ---- chords (with the section's articulation + melodic cell) ----
            var ct = ChordModelOps.ChordTrack(p);
            PatternGeneratorModule prevChord = null;
            foreach (var inst in form)
            {
                var art = inst.pick.Art; var cell = inst.pick.Cell;
                int artSpq = art != null && art.SlicesPerBeat > 0 ? art.SlicesPerBeat
                           : cell != null && cell.SlicesPerBeat > 0 ? cell.SlicesPerBeat : spq;
                var artNotes = art != null ? TemplateComposer.ArticulationNotes(art) : null;
                var cellNotes = cell != null ? RescaleNotes(TemplateComposer.CellNotes(cell), cell.SlicesPerBeat > 0 ? cell.SlicesPerBeat : spq, artSpq) : null;
                foreach (var ch in inst.pick.Prog)
                {
                    int beats = Math.Max(1, ch.BeatCount);
                    int qi = TemplateChords.QualityIndex(ch.Mode, ch.Quality, ch.Color);
                    var chord = new AiChord { degree = ch.Degree, quality = Get(PatternGenerator.QualityNames, qi) };
                    prevChord = ChordModelOps.AddAiChord(p, barTemps, ct, chord, beats, -1, artNotes, artSpq, prevChord, false, null, cellNotes);
                }
            }

            // ---- drums: the picked groove tiled over each instance ----
            if (drumTrack != null)
            {
                double dCursor = 0;
                foreach (var inst in form)
                {
                    double startBeat = inst.startBar * barTemps;
                    var g = inst.pick.Groove;
                    if (g != null && g.Motif != null && g.Motif.Length >= 3)
                    {
                        int gSpq = g.SlicesPerBeat > 0 ? g.SlicesPerBeat : spq;
                        var dnotes = TemplateComposer.DrumNotes(g);
                        // Derive the motif's length from its actual CONTENT, rounded UP to a whole bar (a drum note is a
                        // one-shot: use each hit's START, so ring-out into the next bar doesn't inflate the loop).
                        int barSlicesG = Math.Max(1, barTemps * gSpq);
                        int lastStart = 0; foreach (var n in dnotes) lastStart = Math.Max(lastStart, n.Start);
                        int gBars = Math.Max(1, (int)Math.Ceiling((lastStart + 1) / (double)barSlicesG));
                        int reps = Math.Max(1, inst.bars / gBars);
                        var dpm = new DrumPatternModule { Kit = 0, Style = DrumPattern.CustomStyle, BeatsPerBar = gBars * barTemps, Repeats = reps };
                        dpm.SetCustomNotes(dnotes, gSpq, gBars * barTemps * gSpq);
                        drumTrack.Items.Add(new TimelineItem { Module = dpm, SilenceBefore = Math.Max(0, startBeat - dCursor) });
                        dCursor = startBeat + reps * gBars * barTemps;
                    }
                }
            }

            // ---- melody: per track, either an explicit RIFF phrase (transposed modally onto the instance's chords)
            //      or, when the template was generated in "lignes mélodiques" mode, a rhythm-only MELODIC LINE whose
            //      pitches the engine picks from the chords. A track with neither stays silent for that section.
            for (int ti = 0; ti < instrTracks.Count; ti++)
            {
                var track = instrTracks[ti];
                double cursor = 0;
                foreach (var inst in form)
                {
                    double startBeat = inst.startBar * barTemps;
                    double secBeats = inst.bars * barTemps;
                    inst.pick.Phrase.TryGetValue(ti, out var phrase);
                    bool hasPhrase = phrase != null && phrase.Motif != null && phrase.Motif.Length >= 3;

                    if (!hasPhrase)
                    {
                        // Melodic line fallback: rhythm only; MelodicLineEngine resolves the pitches per chord.
                        inst.pick.Line.TryGetValue(ti, out var line);
                        if (line == null || line.Durations == null || line.Durations.Length == 0) continue;
                        int lSlices = (int)Math.Round(secBeats) * spq;
                        var ml = new MelodicLineModule
                        {
                            BeatsPerBar = Math.Max(1, (int)Math.Round(secBeats)), VoiceCount = 1,
                            Contour = line.Contour, Anchor = line.Anchor, RegisterShift = line.Register, Continuity = 30,
                        };
                        ml.SetNotes(AiTranslate.BuildRhythmNotes(new List<double>(line.Durations), lSlices, spq), spq, lSlices);
                        track.Items.Add(new TimelineItem { Module = ml, SilenceBefore = Math.Max(0, startBeat - cursor) });
                        cursor = startBeat + secBeats;
                        continue;
                    }
                    if (inst.slots.Count == 0) continue;

                    double sectionBeats = secBeats;
                    var slots = inst.slots;
                    Func<double, TemplateComposer.ChordSlot> chordAt = (b) =>
                    {
                        var chosen = slots[0];
                        foreach (var s in slots) { if (b + 1e-6 >= s.StartBeat) chosen = s; else break; }
                        return chosen;
                    };
                    var notes = TemplateComposer.RenderRiff(phrase, sectionBeats, chordAt, scalePcs, tonicPc, 0, rspq);
                    if (notes.Count == 0) continue;

                    var riff = new Riff { Name = track.Name, SlicesPerQuarter = rspq, LengthSlices = (int)Math.Round(sectionBeats * rspq), Notes = notes };
                    riffs.Add(riff);
                    track.Items.Add(new TimelineItem { Module = new PlayRiffModule { RiffId = riff.Id }, SilenceBefore = Math.Max(0, startBeat - cursor) });
                    cursor = startBeat + sectionBeats;
                }
            }

            return new TimelineDocument { Project = p, Riffs = riffs };
        }

        static Engine.Score.KeySignature KeyFromTonality(TplTonality t)
        {
            int pc = t != null ? ((t.Note % 12) + 12) % 12 : 0;
            int mode = t != null ? Math.Max(0, Math.Min(8, t.Mode)) : 0;
            // pc → (letter 0..6, accidental) using the usual flat spellings for the black keys.
            int[] letter = { 0, 0, 1, 2, 2, 3, 3, 4, 5, 5, 6, 6 };
            int[] acc = { 0, 1, 0, -1, 0, 0, 1, 0, -1, 0, -1, 0 };
            return new Engine.Score.KeySignature
            {
                TonicLetter = letter[pc], Accidental = acc[pc],
                Mode = Engine.Score.MusicalMode.IsMinorish(mode) ? 1 : 0,
                FullMode = mode,
            };
        }

        // Rescale a note list from one slices-per-beat to another (start/length), for mixing banks authored at
        // different resolutions in the same chord (articulation vs melodic cell).
        static List<RiffNote> RescaleNotes(List<RiffNote> notes, int fromSpq, int toSpq)
        {
            if (notes == null || fromSpq <= 0 || toSpq <= 0 || fromSpq == toSpq) return notes;
            var outp = new List<RiffNote>(notes.Count);
            foreach (var n in notes)
                outp.Add(new RiffNote(n.Note, (int)Math.Round(n.Start * (double)toSpq / fromSpq), Math.Max(1, (int)Math.Round(n.Length * (double)toSpq / fromSpq))));
            return outp;
        }

        static string Get(string[] a, int i) => (a != null && i >= 0 && i < a.Length) ? a[i] : "?";
    }
}
