using System;
using System.Collections.Generic;
using MusicTracker.Engine.Flow;
using MusicTracker.Engine.Score;

namespace MusicTracker.Engine.Timeline
{
    /// <summary>
    /// Auto-composer: builds a small but coherent piece from our existing bricks — a CHORD/CADENCE track (via
    /// <see cref="MusicTheory.Cadence"/> + voice-leading + an articulation), a MELODY track (a diatonic line over
    /// the chords: chord tones on strong beats, scale steps/neighbours on weak beats, small intervals, a repeated
    /// rhythmic motif, resolving to the tonic), and an optional DRUM groove. A STYLE (Mozart, Bach, Vivaldi,
    /// Romantique, Ballade jazz, Pop) picks the cadence palette, chord articulation, instruments and rhythm so the
    /// result evokes that idiom. Pseudo-random (seeded) but rule-based — always in the key/harmony.
    /// </summary>
    public static class Composer
    {
        const int Spq = 24;

        public static readonly string[] StyleNames =
            { "Mozart (classique)", "Bach (baroque)", "Vivaldi (baroque)", "Romantique", "Ballade jazz", "Pop", "Bach — soliste (mélodie composée)", "Ballade / film", "Harpe (arpèges modaux)", "Hisaishi / Ghibli" };

        // Per-style recipe, INSPIRED BY THE WRITING of real pieces (not just instruments):
        //  - Bach (baroque): a short motif developed by SEQUENCE, with IMITATIVE COUNTERPOINT (canon=true) — voice 0
        //    states the subject, the other voices restate it entering a bar later. Cycle-of-fifths harmony.
        //  - Vivaldi (baroque): sequential but MELODIC line (sequence=true) with varied rhythm (not motoric 16ths).
        //  - Mozart (classical): melody-dominated HOMOPHONY over an ALBERTI bass, phrased as balanced
        //    antécédent/conséquent PERIODS (period=true).
        //  - Romantique: lyrical periods over wide arpeggios, richer (7th) chords.
        //  - Ballade jazz: ii-V-I / Coltrane + comping + soft groove.
        //  - Pop: bass+chord + groove, 4+4 period phrasing.
        // Melody rhythm is VARIED per bar (see BarRhythm) with a breath at phrase ends — never a single motif repeated.
        // drum = -1 → no drum track. melEns = melody ensemble (instruments for voices 0,1,2,3). sequence = baroque
        // motif-sequence melody; otherwise the homophonic "walk" melody. fast = 16th-note motion.
        // period = melody built as a balanced antécédent/conséquent PERIOD (the consequent reuses the antecedent's
        // bar-rhythms) — a Classical/songform trait (Mozart, Romantique, Pop). Ignored by the sequence melody.
        // canon = followers IMITATE voice 0 (delayed entry) → Bach-style imitative counterpoint, instead of free
        // independent lines. Only meaningful with >1 melody voice.
        // solo = baroque SOLO + CONTINUO: a soloist (SoloLine, framed in 3rds/6ths over the bass, with suspensions)
        // over a figured bass + harpsichord continuo, at a lively harmonic rhythm. (CompoundMelody — a fully
        // UNACCOMPANIED arpeggiating line — is kept in reserve for a future "soliste seul" toggle.)
        // parallel = extra voices DOUBLE the lead in 3rds/6ths (Vivaldi Italian string writing, parallel motion) rather
        // than being independent. motor = a driving REPEATED-NOTE figured bass (the Vivaldi tremolo/ostinato engine).
        // ballad = slow tonal "ballade / film" texture (analysed from music1.mid): no drums, layered DOUBLED voices over
        // a melodic stepwise bass, gentle wide-arpeggio accompaniment, and a FREE/varied rhythm (profile 4) with no
        // motoric pulse.
        // harp = harp/new-age "arpèges modaux" texture (from the harp piece): a SUSTAINED bass + arpeggiated chord
        // accompaniment over a SLOW modal harmony (1 chord/bar, no modulation, cadence style 27).
        struct Cfg { public int[] cad; public int[] melEns; public int chordArt, heldMode, climb, drum, chordInst, bassInst; public bool halve, bass, fast, sequence, period, canon, solo, parallel, motor, ballad, harp, hisaishi; }

        static Cfg StyleCfg(int s)
        {
            switch (s)
            {
                case 1: return new Cfg { cad = new[] { 26, 10, 9 }, chordArt = 11, heldMode = 0, climb = 0, halve = false, bass = false, drum = -1, melEns = new[] { 40, 41, 42, 6 }, chordInst = 6, bassInst = 42, fast = true, sequence = true, canon = true };  // Bach — baroque cadence (cycle+V-I+picardie)/cycle/Pachelbel, imitative counterpoint, harpsichord
                case 2: return new Cfg { cad = new[] { 26, 25, 9 }, chordArt = 11, heldMode = 0, climb = 0, halve = false, bass = true, drum = -1, melEns = new[] { 40, 41, 42, 43 }, chordInst = 48, bassInst = 43, fast = true, sequence = true, parallel = true, motor = true }; // Vivaldi — string section, parallel 3rds/6ths, repeated-note motor bass
                case 3: return new Cfg { cad = new[] { 29, 5, 17 }, chordArt = 24, heldMode = 1, climb = 2, halve = false, bass = true, drum = -1, melEns = new[] { 40, 41, 42, 73 }, chordInst = 0, bassInst = 42, fast = false, sequence = false, period = true }; // Romantique — rich modal-mixture cadence, lyrical periods, cello bass
                case 4: return new Cfg { cad = new[] { 3, 4, 23 }, chordArt = 6, heldMode = 1, climb = 0, halve = false, bass = true, drum = 14, melEns = new[] { 4, 65, 56, 0 }, chordInst = 0, bassInst = 32, fast = false, sequence = false };   // Ballade jazz — Rhodes, sax, trumpet + drums
                case 5: return new Cfg { cad = new[] { 5, 6, 7 }, chordArt = 8, heldMode = 0, climb = 0, halve = false, bass = true, drum = 0, melEns = new[] { 40, 80, 73, 56 }, chordInst = 0, bassInst = 33, fast = false, sequence = false, period = true };     // Pop — lead + synth + drums, 4+4 phrasing
                case 6: return new Cfg { cad = new[] { 26 }, chordArt = 11, heldMode = 0, climb = 0, halve = false, bass = false, drum = -1, melEns = new[] { 40, 42, 73, 41 }, chordInst = 6, bassInst = 42, fast = true, sequence = false, solo = true }; // Bach soliste — one unaccompanied compound-melody line (violin)
                case 7: return new Cfg { cad = new[] { 29, 28, 4 }, chordArt = 24, heldMode = 1, climb = 2, halve = false, bass = true, drum = -1, melEns = new[] { 40, 48, 24, 73 }, chordInst = 0, bassInst = 32, fast = false, sequence = false, period = true, parallel = true, ballad = true }; // Ballade / film — rich/epic modal-mixture cadences, no drums, doubled voices, melodic bass, free rhythm
                case 8: return new Cfg { cad = new[] { 27 }, chordArt = 27, heldMode = 0, climb = 0, halve = false, bass = false, drum = -1, melEns = new[] { 40, 48, 46, 46 }, chordInst = 46, bassInst = 46, fast = false, sequence = false, period = true, harp = true }; // Harpe — modal mediants, rolled harp arpeggio (art.27) + separate sustained bass; VIOLIN lead entering late
                case 9: return new Cfg { cad = new[] { 27 }, chordArt = 11, heldMode = 0, climb = 0, halve = false, bass = false, drum = -1, melEns = new[] { 73, 48, 49, 0 }, chordInst = 0, bassInst = 0, fast = false, sequence = false, hisaishi = true }; // Hisaishi/Ghibli — handled by ComposeHisaishi (arpeggio ostinato + modal colour + Markov melody, A->B->C->D)
                default: return new Cfg { cad = new[] { 4, 1, 9 }, chordArt = 5, heldMode = 0, climb = 0, halve = false, bass = true, drum = -1, melEns = new[] { 73, 68, 71, 70 }, chordInst = 0, bassInst = 42, fast = false, sequence = false, period = true };   // Mozart — Alberti homophony + antécédent/conséquent periods, woodwinds, cello bass
            }
        }

        public static List<TimelineTrack> Compose(KeySignature key, int meterNum, int meterDen, int measures, int seed, int style, int melodyVoices, List<Riff> newRiffs, int rhythmProfile = -1, int breathLevel = -1, int virtuosity = -1, int form = 0, int mode = 0)
        {
            key = key ?? new KeySignature();
            measures = Math.Max(2, measures);
            var rng = new Random(seed);
            var cfg = StyleCfg(style);
            int beatsPerBar = meterDen == 8 ? Math.Max(1, meterNum / 3) : Math.Max(1, meterNum);
            int barSlices = beatsPerBar * Spq;
            // HISAISHI / GHIBLI — its own A->B->C->D builder (minimalist arpeggio ostinato + modal-colour harmony +
            // pentatonic Markov melody). The dialog's "Mode" drives the scale (major/lydian/minor/aeolian/dorian).
            if (cfg.hisaishi) return ComposeHisaishi(key, beatsPerBar, barSlices, seed, mode, breathLevel >= 0 ? breathLevel : 1, newRiffs);
            // FORM (sonate/rondeau/variations/fugue/contrepoint) — when chosen, the FORM dictates the bar count, the
            // section/key plan and the theme reuse, ignoring the user's measure count. (See ComposeForm.)
            if (form > 0) return ComposeForm(key, beatsPerBar, barSlices, seed, melodyVoices, newRiffs, form,
                                             rhythmProfile >= 0 ? rhythmProfile : 0, breathLevel >= 0 ? breathLevel : 1, virtuosity);
            // rhythmProfile: -1 = Auto (derive from the style: fast baroque → balanced, else calm); 1/2/3 = the user
            // picked a palette (balanced WTC / stately Art-of-Fugue / florid Goldberg) in the composer dialog.
            int profile = rhythmProfile >= 0 ? rhythmProfile : (cfg.ballad ? 4 : cfg.fast ? 1 : 0);
            // breathLevel: -1 = Auto (light), 0 = none (legato), 1 = light détaché + phrase breaths, 2 = marked.
            int breath = breathLevel >= 0 ? breathLevel : 1;
            // virtuosity: 0..3 = how often the baroque sequence bursts into 32nd-note runs (diminution). Auto stays
            // LIGHT (1, ~19% — moderate, "not too many 32nds") except the explicit florid profile (3); set it higher
            // in the dialog for a more brilliant soloist, or 0 for none.
            int virt = virtuosity >= 0 ? virtuosity : (profile == 3 ? 3 : 1);

            // 1) Harmony — a chord progression in the style's cadence palette.
            int cadStyle = cfg.cad[rng.Next(cfg.cad.Length)];

            // SOLO + CONTINUO (baroque solo sonata, cf. BWV 1014-1019): a soloist and bass forming a two-voice
            // contrapuntal frame over a LIVELY harmonic rhythm (~2 chords/bar), realised by a harpsichord continuo +
            // a figured bass. The soloist frames in 3rds/6ths over the bass and uses suspensions.
            if (cfg.solo)
            {
                int scpb = 2;                                              // chords per bar (harmonic rhythm)
                int sslot = Math.Max(1, barSlices / scpb);
                var rawH = BuildModulatingProgression(key, measures * scpb, cadStyle, seed); // finer + modulating progression
                var solo = new List<TimelineTrack>();
                var sr = SoloLine(key, rawH, sslot, beatsPerBar, barSlices, measures, new Random(seed * 131), profile);
                AddBreathing(sr, beatsPerBar, barSlices, new Random(seed * 17 + 1), breath);
                newRiffs?.Add(sr);
                var st = new TimelineTrack { Type = TimelineTrackType.Instrument, Instrument = cfg.melEns[0], Name = sr.Name };
                st.Items.Add(new TimelineItem { Module = new PlayRiffModule { RiffId = sr.Id } });
                solo.Add(st);
                var contChords = BuildCadenceChords(key, rawH, 3);
                var contTrack = new TimelineTrack { Type = TimelineTrackType.Instrument, Instrument = cfg.chordInst, Name = "Continuo" };
                contTrack.Items.Add(new TimelineItem { Module = new CadenceModule { Chords = contChords, Octave = 3, Style = cfg.chordArt, Bass = false, BeatsPerBar = beatsPerBar, CadenceStyle = cadStyle, StartDegree = 0, Measures = measures, ChordsPerMeasure = scpb } });
                solo.Add(contTrack);
                var sbass = GenerateFiguredBass(key, rawH, sslot, new Random(seed * 29 + 5));
                AddBreathing(sbass, beatsPerBar, barSlices, new Random(seed * 23 + 3), breath);
                newRiffs?.Add(sbass);
                var bassTrack = new TimelineTrack { Type = TimelineTrackType.Instrument, Instrument = cfg.bassInst, Name = "Basse" };
                bassTrack.Items.Add(new TimelineItem { Module = new PlayRiffModule { RiffId = sbass.Id } });
                solo.Add(bassTrack);
                return solo;
            }

            // Non-solo: a finer harmonic rhythm (cpb chords/bar) and — for the functional styles — a tonal PLAN that
            // modulates to a related key (the dominant, or the relative major in minor) and returns: I → V → I.
            // Harp = SLOW modal harmony (1 chord/bar, no modulation — it hovers around the tonic).
            int cpb = cfg.harp ? 1 : 2, slot = Math.Max(1, barSlices / cpb);
            bool modulate = !cfg.harp && style <= 3;          // Mozart / Bach / Vivaldi / Romantique
            var raw = modulate ? BuildModulatingProgression(key, measures * cpb, cadStyle, seed)
                               : MusicTheory.Cadence(key, 0, measures * cpb, cadStyle, seed);

            var chordCells = BuildCadenceChords(key, raw, 4);
            var chordTrack = new TimelineTrack { Type = TimelineTrackType.Instrument, Instrument = cfg.chordInst, Name = "Accords" };
            chordTrack.Items.Add(new TimelineItem
            {
                Module = new CadenceModule
                {
                    Chords = chordCells, Octave = 4, Style = cfg.chordArt, Bass = cfg.bass,
                    HeldMode = cfg.heldMode, ClimbMode = cfg.climb, HalveDurations = cfg.halve,
                    BeatsPerBar = beatsPerBar, CadenceStyle = cadStyle, StartDegree = 0, Measures = measures, ChordsPerMeasure = cpb,
                },
            });

            // 2) Melody — N INDEPENDENT diatonic lines over the chords (each its own contour, rhythm and register,
            // via a different seed), not rigid parallel thirds: each lands on a chord tone on strong beats (any tone
            // — 3rds/5ths/roots, and 4th/6th colours as passing notes) so the voices stay consonant but rich.
            int voices = Math.Max(1, Math.Min(4, melodyVoices));
            var tracks = new List<TimelineTrack>();

            // BASS FIRST (so the top voice can be framed against it). The acoustic/classical styles use a figured bass;
            // pop/jazz keep a drum groove (no bass riff here). Vivaldi (motor) drives a repeated-note continuo.
            Riff bassRiff = cfg.drum >= 0 ? null : GenerateFiguredBass(key, raw, slot, new Random(seed * 29 + 5), cfg.motor, cfg.harp);

            var melRiffs = new List<Riff>();
            Riff leader = null;
            for (int v = 0; v < voices; v++)
            {
                var vrng = new Random(seed * 131 + v * 7919); // distinct line per voice
                int center = 67 - 3 * v;                      // ~G4 melodic register; voices only slightly staggered so
                                                              // their ranges OVERLAP and the lines may cross freely
                Riff r;
                if (cfg.canon && v > 0 && leader != null)
                    // imitate voice 0, each successive voice entering ~2 bars later (subject-length staggered fugal entry)
                    r = CanonMelody(leader, key, raw, beatsPerBar, barSlices, measures, Math.Min(v * 2, Math.Max(1, measures - 2)), center);
                else if (cfg.parallel && v > 0 && leader != null)
                    // Vivaldi: double the lead a 3rd/6th below (Italian string doubling, parallel motion — not counterpoint)
                    r = ParallelVoice(leader, key, raw, slot, center, vrng);
                else if (cfg.sequence)
                    r = SequenceMelody(key, raw, beatsPerBar, barSlices, measures, vrng, profile, center, virt);
                else
                    r = GenerateMelody(key, raw, beatsPerBar, barSlices, measures, vrng, profile, center, cfg.period);
                if (v == 0) leader = r;
                r.Name = voices > 1 ? "Mélodie " + (v + 1) : "Mélodie";
                melRiffs.Add(r);
            }
            PolishVoices(melRiffs, key, raw, barSlices);          // phase 2: voice-leading polish between the voices
            if (bassRiff != null && melRiffs.Count > 0) FrameTopVoice(melRiffs[0], bassRiff, raw, slot, key); // outer-voice frame
            if (cfg.harp) for (int v = 0; v < melRiffs.Count; v++) DelayEntry(melRiffs[v], ((measures / 2) + v) * barSlices); // melody (violin) enters late over the harp intro, voices staggered
            for (int v = 0; v < melRiffs.Count; v++)
            {
                var r = melRiffs[v];
                AddBreathing(r, beatsPerBar, barSlices, new Random(seed * 53 + v * 97), breath); // phase 1: breathing (rhythm)
                newRiffs?.Add(r);
                int inst = cfg.melEns[Math.Min(v, cfg.melEns.Length - 1)];
                var t = new TimelineTrack { Type = TimelineTrackType.Instrument, Instrument = inst, Name = r.Name };
                t.Items.Add(new TimelineItem { Module = new PlayRiffModule { RiffId = r.Id } });
                tracks.Add(t);
            }
            tracks.Add(chordTrack);

            // 3) Foundation: a DRUM groove (pop/jazz) or the figured BASS line generated above.
            if (cfg.drum >= 0)
            {
                var drumTrack = new TimelineTrack { Type = TimelineTrackType.Drum, Name = "Batterie" };
                drumTrack.Items.Add(new TimelineItem { Module = new DrumPatternModule { Style = cfg.drum, Density = 1, Kit = 0, BeatsPerBar = beatsPerBar, Repeats = measures } });
                tracks.Add(drumTrack);
            }
            else
            {
                AddBreathing(bassRiff, beatsPerBar, barSlices, new Random(seed * 23 + 3), breath);
                newRiffs?.Add(bassRiff);
                var bassTrack = new TimelineTrack { Type = TimelineTrackType.Instrument, Instrument = cfg.bassInst, Name = "Basse" };
                bassTrack.Items.Add(new TimelineItem { Module = new PlayRiffModule { RiffId = bassRiff.Id } });
                tracks.Add(bassTrack);
            }
            return tracks;
        }

        // ===================== HISAISHI / GHIBLI =====================
        // Built from the 74-piece corpus analysis: a MINIMALIST arpeggio OSTINATO (88% of the corpus accompaniment is
        // arpeggiated broken chords, continuous 16ths, complementary to the tune), an IMPRESSIONIST MODAL harmony
        // (roots I/IV/V/bIII/bVI/bVII, maj7 on the mediants, sus/add9, NO dominant 7th, plagal/mediant motion), and a
        // PENTATONIC FLOATING melody from the canonicalized scale-degree MARKOV chain (78% on 5 degrees; leaps resolve
        // inward 59%) over slow syncopated note values. Assembled as an A->B->C->D intensity automaton.
        public static readonly string[] ModeNames =
            { "Auto (selon la tonalité)", "Majeur (ionien)", "Lydien (majeur #4)", "Mineur", "Éolien (mineur modal)", "Dorien" };

        // mode: 0 Auto · 1 Majeur(Ionien) · 2 Lydien · 3 Mineur(sensible) · 4 Éolien · 5 Dorien
        static int[] HisaishiOffsets(int mode, bool keyMinor)
        {
            switch (mode)
            {
                case 1: return new[] { 0, 2, 4, 5, 7, 9, 11 };   // Ionian
                case 2: return new[] { 0, 2, 4, 6, 7, 9, 11 };   // Lydian (#4 — the "magic")
                case 3: return new[] { 0, 2, 3, 5, 7, 8, 11 };   // minor with leading tone (for the V lift)
                case 4: return new[] { 0, 2, 3, 5, 7, 8, 10 };   // Aeolian
                case 5: return new[] { 0, 2, 3, 5, 7, 9, 10 };   // Dorian (natural 6)
                default: return keyMinor ? new[] { 0, 2, 3, 5, 7, 8, 10 } : new[] { 0, 2, 4, 5, 7, 9, 11 };
            }
        }
        static bool HisaishiMinor(int mode, bool keyMinor) => mode == 3 || mode == 4 || mode == 5 || (mode == 0 && keyMinor);

        // The modal-colour progression (one chord/bar) — chains characteristic Hisaishi CELLS (the chord Markov).
        // Quality indices (see PatternGenerator.QualityNames): Maj=0 Min=1 Sus2=4 Sus4=5 Maj7=6 Min7=7 add9=13 m(add9)=14 Maj9=15.
        static List<(int root, int quality)> HisaishiProgression(int tonicPc, int mode, bool minor, int bars, Random rng)
        {
            int Vq = (mode == 1 || mode == 2 || mode == 3 || (mode == 0 && !minor)) ? 0 : 1; // major V only where a leading tone fits
            List<(int deg, int q)[]> cells;
            if (minor)
                cells = new List<(int deg, int q)[]>
                {
                    new (int, int)[] { (0,14), (10,13), (8,6), (10,13) },  // i(add9) - bVII - bVImaj7 - bVII  (modal descent)
                    new (int, int)[] { (5,7), (3,6), (8,6), (10,13) },     // iv7 - bIIImaj7 - bVImaj7 - bVII
                    new (int, int)[] { (0,14), (3,6), (5,7), (7,Vq) },     // i - bIIImaj7 - iv - V
                    new (int, int)[] { (5,7), (10,13), (0,14), (0,14) },   // iv - bVII - i - i (plagal)
                    new (int, int)[] { (0,14), (5,7), (10,13), (3,6) },    // i - iv - bVII - bIIImaj7
                };
            else
                cells = new List<(int deg, int q)[]>
                {
                    new (int, int)[] { (5,6), (7,0), (4,7), (9,7) },       // IVmaj7 - V - iii7 - vi  (the Ghibli "uplift")
                    new (int, int)[] { (5,6), (7,15), (5,6), (7,15) },     // IVmaj7 <-> Vmaj7 (whole-tone oscillation loop)
                    new (int, int)[] { (0,13), (7,0), (9,7), (5,6) },      // I(add9) - V - vi - IVmaj7
                    new (int, int)[] { (5,13), (0,13) },                   // IV(add9) - I  (plagal)
                    new (int, int)[] { (0,13), (9,7), (5,6), (7,0) },      // I - vi - IVmaj7 - V
                };
            var prog = new List<(int, int)>();
            while (prog.Count < bars)
            {
                var cell = cells[rng.Next(cells.Count)];
                foreach (var pair in cell) { if (prog.Count >= bars) break; prog.Add(((tonicPc + pair.deg) % 12, pair.q)); }
            }
            return prog;
        }

        // The minimalist OSTINATO: a continuous broken-chord arpeggio (low-high-mid-high, the zigzag the corpus shows),
        // `sub` notes per beat (4 = 16ths). Every 4 bars the TOP note shifts to another chord tone (micro-evolution).
        static Riff ArpOstinato(List<(int root, int quality)> prog, int barSlices, int sub, int octave)
        {
            int unit = Math.Max(3, Spq / Math.Max(1, sub));
            var notes = new List<RiffNote>();
            for (int bar = 0; bar < prog.Count; bar++)
            {
                var ch = PatternGenerator.ChordNotes(prog[bar].root, octave, prog[bar].quality, 0);
                if (ch.Length == 0) continue;
                int lo = ch[0], mid = ch[Math.Min(1, ch.Length - 1)], hi = ch[Math.Min(2, ch.Length - 1)];
                int top = ((bar / 4) % 2 == 1 && ch.Length >= 2) ? ch[ch.Length - 1] : hi; // micro-evolution (stays a chord tone)
                int[] pat = { lo, top, mid, top };
                int start = bar * barSlices, pos = 0, k = 0;
                while (pos < barSlices)
                {
                    int p = pat[k % pat.Length] - 12;
                    if (p >= 0 && p < 96) notes.Add(new RiffNote(p, start + pos, Math.Min(unit, barSlices - pos)));
                    pos += unit; k++;
                }
            }
            return new Riff { Name = "Ostinato", Notes = notes, LengthSlices = prog.Count * barSlices, SlicesPerQuarter = Spq };
        }

        // A sustained-root PEDAL bass (one held root per bar, low register).
        static Riff PedalBass(List<(int root, int quality)> prog, int barSlices)
        {
            var notes = new List<RiffNote>();
            for (int bar = 0; bar < prog.Count; bar++)
            {
                int row = (((prog[bar].root % 12) + 12) % 12) + 24; // ~MIDI 36-47 once played
                notes.Add(new RiffNote(row, bar * barSlices, barSlices));
            }
            return new Riff { Name = "Basse", Notes = notes, LengthSlices = prog.Count * barSlices, SlicesPerQuarter = Spq };
        }

        // Sustained string PAD (whole-bar chords) for the climax bars [fromBar, toBar).
        static Riff PadChords(List<(int root, int quality)> prog, int barSlices, int fromBar, int toBar, int octave)
        {
            var notes = new List<RiffNote>();
            for (int bar = fromBar; bar < toBar && bar < prog.Count; bar++)
            {
                var ch = PatternGenerator.ChordNotes(prog[bar].root, octave, prog[bar].quality, 0);
                foreach (var m in ch) { int row = m - 12; if (row >= 0 && row < 96) notes.Add(new RiffNote(row, bar * barSlices, barSlices)); }
            }
            return new Riff { Name = "Cordes", Notes = notes, LengthSlices = prog.Count * barSlices, SlicesPerQuarter = Spq };
        }

        // Melody MARKOV candidates (canonicalized scale-degree transitions learned from the corpus): flattened {pc,weight,...}.
        static int[] MelodyCandidates(int deg, bool minor)
        {
            if (minor)
                switch (deg)
                {
                    case 0: return new[] { 0, 25, 3, 15, 7, 14, 10, 13, 2, 10 };
                    case 2: return new[] { 3, 23, 0, 19, 7, 16, 2, 12 };
                    case 3: return new[] { 2, 19, 5, 19, 3, 17, 10, 11 };
                    case 5: return new[] { 5, 24, 7, 20, 3, 15, 0, 12 };
                    case 7: return new[] { 0, 22, 7, 16, 5, 13, 10, 11 };
                    case 8: return new[] { 10, 20, 8, 17, 7, 17, 3, 13 };
                    case 9: return new[] { 9, 30, 7, 14, 10, 13, 2, 12 };
                    case 10: return new[] { 0, 26, 10, 15, 7, 12, 8, 11 };
                    default: return new[] { 0, 1 };
                }
            switch (deg)
            {
                case 0: return new[] { 7, 32, 2, 13, 0, 12, 5, 11 };
                case 2: return new[] { 4, 22, 0, 19, 2, 17, 5, 10 };
                case 4: return new[] { 2, 24, 7, 17, 11, 17, 5, 11 };
                case 5: return new[] { 7, 21, 5, 19, 0, 14, 9, 12 };
                case 6: return new[] { 7, 45, 4, 15, 11, 10, 6, 10 };
                case 7: return new[] { 0, 33, 7, 16, 9, 15, 6, 8 };
                case 9: return new[] { 7, 23, 0, 18, 9, 16, 2, 13 };
                case 11: return new[] { 4, 28, 9, 18, 11, 15, 0, 12 };
                default: return new[] { 0, 1 };
            }
        }
        static int PickPc(int[] cand, Random rng)
        {
            int tot = 0; for (int i = 1; i < cand.Length; i += 2) tot += cand[i];
            int r = rng.Next(Math.Max(1, tot)), acc = 0;
            for (int i = 0; i < cand.Length; i += 2) { acc += cand[i + 1]; if (r < acc) return cand[i]; }
            return cand[0];
        }

        // Slow, "floating" melodic rhythm: longer note values (eighth..dotted-quarter..held), often a syncopated rest
        // to start (entry off the beat), and a long breath at phrase ends. Returns (duration, isRest) per slot.
        static List<(int dur, bool rest)> HisaishiMelRhythm(int beatsPerBar, Random rng, bool phraseEnd)
        {
            var outl = new List<(int, bool)>();
            int total = beatsPerBar * Spq;
            if (phraseEnd) { outl.Add((total, false)); return outl; }
            int pos = 0;
            if (rng.Next(100) < 35) { outl.Add((Spq / 2, true)); pos += Spq / 2; }        // syncopated eighth-rest start
            int[][] cells = { new[] { Spq }, new[] { Spq, Spq / 2 }, new[] { Spq / 2, Spq }, new[] { Spq, Spq }, new[] { Spq * 3 / 2 }, new[] { Spq / 2, Spq / 2, Spq } };
            while (pos < total)
            {
                var c = cells[rng.Next(cells.Length)];
                foreach (var d in c) { if (pos >= total) break; int dd = Math.Min(d, total - pos); outl.Add((dd, false)); pos += dd; }
            }
            return outl;
        }

        // The nostalgic THEME: a constrained random walk driven by the scale-degree MARKOV chain (style) + register
        // placement (Random Walk: nearest octave to the previous note). Stepwise-dominant; a leap (>=m3) is forced to
        // RESOLVE by a step in the opposite direction next (the corpus rule, 59%). Phrase starts anchor to a chord tone.
        static Riff MarkovMelody(int tonicPc, HashSet<int> scale, bool minor, List<(int root, int quality)> prog, int beatsPerBar, int barSlices, int measures, Random rng, int center)
        {
            int lo = center - 9, hi = center + 11;
            int cur = NearestPc(center, tonicPc), curDeg = 0, lastIv = 1; bool resolve = false;
            var notes = new List<RiffNote>();
            for (int m = 0; m < measures; m++)
            {
                bool phraseStart = (m % 4 == 0), phraseEnd = (m % 4 == 3) || (m == measures - 1);
                var cp = ChordPcs(prog[Math.Min(m, prog.Count - 1)]);
                var rh = HisaishiMelRhythm(beatsPerBar, rng, phraseEnd);
                int pos = 0; bool first = true;
                foreach (var step in rh)
                {
                    if (step.rest) { pos += step.dur; first = false; continue; }
                    int prev = cur;
                    if (resolve) { cur = ScaleStep(prev, -Math.Sign(lastIv), scale); resolve = false; }
                    else if (phraseStart && first) cur = NearestChord(prev, cp);                 // anchor the phrase head to the harmony
                    else cur = NearestPc(prev, PickPc(MelodyCandidates(curDeg, minor), rng));    // Markov degree -> nearest octave
                    while (cur > hi) cur -= 12; while (cur < lo) cur += 12;
                    cur = CapLeap(prev, cur, scale, 12);
                    int iv = cur - prev; if (iv != 0) lastIv = iv;
                    if (Math.Abs(iv) >= 3) resolve = true;                                        // leaps resolve inward next
                    curDeg = (((cur - tonicPc) % 12) + 12) % 12;
                    int row = cur - 12;
                    if (row >= 0 && row < 96) notes.Add(new RiffNote(row, m * barSlices + pos, step.dur));
                    pos += step.dur; first = false;
                }
            }
            if (notes.Count > 0) { var ln = notes[notes.Count - 1]; int t = NearestPc(ln.Note + 12, tonicPc); notes[notes.Count - 1] = new RiffNote(Math.Max(0, Math.Min(95, t - 12)), ln.Start, ln.Length); }
            return new Riff { Name = "Mélodie", Notes = notes, LengthSlices = measures * barSlices, SlicesPerQuarter = Spq };
        }

        static List<TimelineTrack> ComposeHisaishi(KeySignature key, int beatsPerBar, int barSlices, int seed, int mode, int breath, List<Riff> newRiffs)
        {
            var rng = new Random(seed);
            int tonicPc = MusicTheory.TonicPc(key);
            bool keyMinor = key?.Mode == 1;
            bool minor = HisaishiMinor(mode, keyMinor);
            var scale = new HashSet<int>(); foreach (var o in HisaishiOffsets(mode, keyMinor)) scale.Add((tonicPc + o) % 12);

            int aB = 4, bB = 8, cB = 8, dB = 4, total = aB + bB + cB + dB;   // A intro / B theme / C climax / D outro
            var prog = HisaishiProgression(tonicPc, mode, minor, total, rng);

            var ost = ArpOstinato(prog, barSlices, 4, 4);                    // minimalist 16th broken-chord engine (all sections)
            var bass = PedalBass(prog, barSlices);                           // sustained pedal (all sections)

            // THEME over B + C; generated on that sub-progression then offset to bar aB.
            int melBars = bB + cB;
            var melProg = prog.GetRange(aB, Math.Min(melBars, prog.Count - aB));
            var melCore = MarkovMelody(tonicPc, scale, minor, melProg, beatsPerBar, barSlices, melProg.Count, new Random(seed * 131 + 7), 72);
            AddBreathing(melCore, beatsPerBar, barSlices, new Random(seed * 53), breath);
            var melFull = new List<RiffNote>(); foreach (var n in melCore.Notes) melFull.Add(new RiffNote(n.Note, n.Start + aB * barSlices, n.Length));
            var melRiff = new Riff { Name = "Mélodie", Notes = melFull, LengthSlices = total * barSlices, SlicesPerQuarter = Spq };

            // CLIMAX (C): melody doubled an octave up + a string pad.
            var octFull = new List<RiffNote>();
            foreach (var n in melCore.Notes) { if (n.Start / barSlices >= bB) { int p = n.Note + 12; if (p < 96) octFull.Add(new RiffNote(p, n.Start + aB * barSlices, n.Length)); } }
            var octRiff = new Riff { Name = "Mélodie 8ve", Notes = octFull, LengthSlices = total * barSlices, SlicesPerQuarter = Spq };
            var padRiff = PadChords(prog, barSlices, aB + bB, aB + bB + cB, 4);

            var tracks = new List<TimelineTrack>();
            AddBarRiffs(tracks, 0, "Ostinato", ost, total, barSlices, newRiffs);       // piano arpeggio
            AddBarRiffs(tracks, 73, "Mélodie", melRiff, total, barSlices, newRiffs);    // flute theme (B+C)
            AddBarRiffs(tracks, 48, "Mélodie 8ve", octRiff, total, barSlices, newRiffs); // strings doubling (C)
            AddBarRiffs(tracks, 49, "Cordes", padRiff, total, barSlices, newRiffs);     // string pad (C)
            AddBarRiffs(tracks, 0, "Basse", bass, total, barSlices, newRiffs);          // piano pedal bass
            return tracks;
        }

        // ---- FORMS (classical) — the FORM dictates bar count, section/key plan and theme reuse (theory-grounded) ----
        public static readonly string[] FormNames = { "Libre", "Sonate", "Rondeau", "Thème et variations", "Fugue", "Contrepoint" };

        // A section: bars, key shift (semitones from home tonic), local mode (0 maj/1 min), cadence style, role.
        // role: 0 state theme A · 1 restate A + resolve to tonic · 6 restate A verbatim · 2 free (transition/episode) ·
        //       3 develop · 4 state theme B · 5 restate B (transposed) + resolve · 7 variation (fresh figuration)
        static List<(int bars, int shift, int mode, int cad, int role)> FormSections(int form, bool homeMinor)
        {
            int hm = homeMinor ? 1 : 0;
            var L = new List<(int, int, int, int, int)>();
            switch (form)
            {
                case 1: // SONATA: exposition (A→V, A→I, transition, B in S) · development · recap (A, A, B in tonic)
                {
                    int sKey = homeMinor ? 3 : 7;                 // 2nd theme: relative major (minor) / dominant (major)
                    L.Add((4, 0, hm, 20, 0));                     // A antecedent → V (tension)
                    L.Add((4, 0, hm, 1, 1));                      // A consequent → I (conclusion)
                    L.Add((4, 0, hm, 0, 2));                      // transition
                    L.Add((8, sKey, 0, 5, 4));                    // 2nd theme in the S key
                    L.Add((8, homeMinor ? 8 : 9, 0, 0, 3));       // development (a related key)
                    L.Add((4, 0, hm, 20, 6));                     // recap A antecedent (→ V)
                    L.Add((4, 0, hm, 1, 1));                      // recap A consequent (→ I)
                    L.Add((8, 0, hm, 5, 5));                      // recap 2nd theme NOW in the tonic
                    break;
                }
                case 2: // RONDO ABACA: A always tonic; B = V/relative major; C = submediant
                    L.Add((8, 0, hm, 1, 0));
                    L.Add((8, homeMinor ? 3 : 7, 0, 5, 2));
                    L.Add((8, 0, hm, 1, 6));
                    L.Add((8, homeMinor ? 8 : 9, homeMinor ? 0 : 1, 6, 2));
                    L.Add((8, 0, hm, 1, 6));
                    break;
                default: // THEME AND VARIATIONS (form 3): theme + 3 variations (same harmony, varied figuration)
                    L.Add((8, 0, hm, 1, 0));
                    L.Add((8, 0, hm, 1, 7));
                    L.Add((8, 0, hm, 1, 7));
                    L.Add((8, 0, hm, 1, 7));
                    break;
            }
            return L;
        }

        // Home key transposed by `semis` semitones, in the given mode (0 maj / 1 min). Signature = the relative major's.
        static KeySignature ShiftKey(KeySignature home, int semis, int mode)
        {
            int tpc = (((MusicTheory.TonicPc(home) + semis) % 12) + 12) % 12;
            int majPc = mode == 1 ? (tpc + 3) % 12 : tpc;
            int fifths = (7 * majPc) % 12; if (fifths > 6) fifths -= 12;
            return MusicTracker.Engine.Score.KeySig.FromFifths(fifths, mode == 1);
        }

        // Copy a theme's notes transposed by `semis`; if resolvePc ≥ 0, force the last note to that pitch-class (a
        // consequent/recap resolving to the tonic). Preserves the rhythm (the literal restatement of a theme).
        static Riff TransposeCopy(Riff theme, int semis, int resolvePc)
        {
            var notes = new List<RiffNote>();
            if (theme != null) foreach (var n in theme.Notes) { int p = n.Note + semis; if (p >= 0 && p < 96) notes.Add(new RiffNote(p, n.Start, n.Length)); }
            if (resolvePc >= 0 && notes.Count > 0) { var ln = notes[notes.Count - 1]; int t = NearestPc(ln.Note + 12, resolvePc); notes[notes.Count - 1] = new RiffNote(Math.Max(0, Math.Min(95, t - 12)), ln.Start, ln.Length); }
            return new Riff { Name = "Mélodie", Notes = notes, LengthSlices = theme?.LengthSlices ?? 0, SlicesPerQuarter = Spq };
        }

        static List<TimelineTrack> ComposeForm(KeySignature home, int beatsPerBar, int barSlices, int seed, int melodyVoices, List<Riff> newRiffs, int form, int profile, int breath, int virt)
        {
            bool homeMinor = home?.Mode == 1;
            int homeTonic = MusicTheory.TonicPc(home);
            int cpb = 2, slot = Math.Max(1, barSlices / cpb);

            // FUGUE / COUNTERPOINT: a baroque imitative texture (subject + dominant answer / independent voices).
            if (form == 4 || form == 5)
            {
                int measures = 16;
                var raw = MusicTheory.Cadence(home, 0, measures * cpb, 26, seed);   // baroque cadence harmony
                int voices = form == 4 ? 3 : 2;
                var bassF = GenerateFiguredBass(home, raw, slot, new Random(seed * 29 + 5));
                var melF = new List<Riff>(); Riff leader = null;
                for (int v = 0; v < voices; v++)
                {
                    int center = 67 - 4 * v;
                    Riff r = (form == 4 && v > 0 && leader != null)
                        ? CanonMelody(leader, home, raw, beatsPerBar, barSlices, measures, v * 2, center)     // fugue: the subject answered, staggered
                        : SequenceMelody(home, raw, beatsPerBar, barSlices, measures, new Random(seed * 131 + v * 7919), profile, center, virt);
                    if (v == 0) leader = r;
                    r.Name = "Voix " + (v + 1); melF.Add(r);
                }
                PolishVoices(melF, home, raw, barSlices);
                var tracksF = new List<TimelineTrack>(); int[] instF = { 6, 40, 41, 42 };
                for (int v = 0; v < melF.Count; v++) { AddBreathing(melF[v], beatsPerBar, barSlices, new Random(seed * 53 + v * 97), breath); AddBarRiffs(tracksF, instF[Math.Min(v, instF.Length - 1)], melF[v].Name, melF[v], measures, barSlices, newRiffs); }
                AddBreathing(bassF, beatsPerBar, barSlices, new Random(seed * 23 + 3), breath);
                AddBarRiffs(tracksF, 42, "Basse", bassF, measures, barSlices, newRiffs);
                return tracksF;
            }

            // SECTIONAL forms (sonata/rondo/variations): build the section chords + a reused theme.
            var sections = FormSections(form, homeMinor);
            var rawAll = new List<(int root, int quality)>();
            var melodyNotes = new List<RiffNote>();
            Riff themeA = null, themeB = null; int themeAtonic = homeTonic, themeBtonic = homeTonic, varN = 0, barCur = 0, center0 = 67;
            foreach (var sec in sections)
            {
                var lk = ShiftKey(home, sec.shift, sec.mode);
                int lkTonic = MusicTheory.TonicPc(lk);
                var secChords = MusicTheory.Cadence(lk, 0, sec.bars * cpb, sec.cad, seed + barCur * 7 + 1);
                rawAll.AddRange(secChords);
                var srng = new Random(seed + barCur * 131 + 17);
                Riff sm;
                switch (sec.role)
                {
                    // Themes are built MOTIVICALLY (a short motif stated, repeated transposed along the harmony, and
                    // developed — SequenceMelody) so they're "tuneful", not a plain walk. Restatements reuse the motif.
                    case 0: sm = SequenceMelody(lk, secChords, beatsPerBar, barSlices, sec.bars, srng, profile, center0, virt); themeA = sm; themeAtonic = lkTonic; break;
                    case 4: sm = SequenceMelody(lk, secChords, beatsPerBar, barSlices, sec.bars, srng, profile, center0, virt); themeB = sm; themeBtonic = lkTonic; break;
                    case 1: sm = TransposeCopy(themeA, lkTonic - themeAtonic, lkTonic); break;       // restate A, resolve to tonic
                    case 6: sm = TransposeCopy(themeA, lkTonic - themeAtonic, -1); break;            // restate A verbatim
                    case 5: sm = TransposeCopy(themeB ?? themeA, lkTonic - themeBtonic, lkTonic); break; // restate B in tonic
                    case 7: sm = GenerateMelody(lk, secChords, beatsPerBar, barSlices, sec.bars, srng, (++varN) % 2 == 0 ? 3 : 1, center0, true); break; // variation
                    default: sm = GenerateMelody(lk, secChords, beatsPerBar, barSlices, sec.bars, srng, profile, center0, true); break;
                }
                if (sm == null || sm.Notes.Count == 0) sm = GenerateMelody(lk, secChords, beatsPerBar, barSlices, sec.bars, srng, profile, center0, true);
                int off = barCur * barSlices, lim = sec.bars * barSlices;
                foreach (var n in sm.Notes) if (n.Start < lim) melodyNotes.Add(new RiffNote(n.Note, off + n.Start, n.Length));
                barCur += sec.bars;
            }
            int total = Math.Max(1, barCur);
            var melodyRiff = new Riff { Name = "Mélodie", Notes = melodyNotes, LengthSlices = total * barSlices, SlicesPerQuarter = Spq };
            var bassRiff = GenerateFiguredBass(home, rawAll, slot, new Random(seed * 29 + 5));
            var one = new List<Riff> { melodyRiff }; PolishVoices(one, home, rawAll, barSlices);
            AddBreathing(melodyRiff, beatsPerBar, barSlices, new Random(seed * 53), breath);
            AddBreathing(bassRiff, beatsPerBar, barSlices, new Random(seed * 23), breath);
            // Accompaniment articulated like ex_class2 — an ALBERTI broken chord (eighths), bar-aligned. Built as notes
            // (not a CadenceModule) so it, the melody and the bass can each be split into ONE EDITABLE RIFF PER BAR.
            var chordRiff = RenderAlberti(home, rawAll, slot, 4);
            var tracks2 = new List<TimelineTrack>();
            AddBarRiffs(tracks2, 40, "Mélodie", melodyRiff, total, barSlices, newRiffs); // violin
            AddBarRiffs(tracks2, 0, "Accords", chordRiff, total, barSlices, newRiffs);   // piano Alberti
            AddBarRiffs(tracks2, 42, "Basse", bassRiff, total, barSlices, newRiffs);     // cello
            return tracks2;
        }

        // Cadence (root,quality) → stored CadenceChords with voice-led inversions/octave + held-note voice + degree.
        static List<CadenceChord> BuildCadenceChords(KeySignature key, List<(int root, int quality)> raw, int octave)
        {
            var vl = MusicTheory.VoiceLead(raw, octave);
            var outl = new List<CadenceChord>();
            int prevHeld = int.MinValue;
            for (int i = 0; i < raw.Count; i++)
            {
                int inv = vl[i].inversion, shift = vl[i].shift;
                var notes = PatternGenerator.ChordNotes(raw[i].root, octave + shift, raw[i].quality, inv);
                int hv = notes.Length - 1;
                if (prevHeld != int.MinValue && notes.Length > 0)
                {
                    int bd = int.MaxValue;
                    for (int k = 0; k < notes.Length; k++) { int d = Math.Abs(notes[k] - prevHeld); if (d < bd) { bd = d; hv = k; } }
                }
                if (notes.Length > 0) prevHeld = notes[hv];
                int deg = MusicTheory.DegreeOf(key, raw[i].root);
                bool diatonic = MusicTheory.DiatonicChord(key, deg).root == raw[i].root;
                outl.Add(new CadenceChord { Root = raw[i].root, Quality = raw[i].quality, Inversion = inv, OctaveShift = shift, HeldVoice = hv, Degree = diatonic ? deg : -1 });
            }
            return outl;
        }

        static Riff GenerateMelody(KeySignature key, List<(int root, int quality)> chords, int beatsPerBar, int barSlices, int measures, Random rng, int profile, int center, bool period)
        {
            int tonic = MusicTheory.TonicPc(key);
            var scale = ScaleSet(key);

            // VARIED rhythm: a fresh cell-based rhythm per bar (not one motif repeated), with a "breath" (a longer
            // held note) at phrase ends. For a Mozart-style PERIOD, the consequent (2nd half) REUSES the antecedent's
            // bar-rhythms → balanced antecedent/consequent phrasing.
            int half = measures / 2;
            var barR = new List<int>[measures];
            for (int m = 0; m < measures; m++)
            {
                bool breathe = (m == measures - 1) || (period && half > 0 && m == half - 1);
                barR[m] = (period && half > 0 && m >= half) ? barR[m - half] : BarRhythm(beatsPerBar, rng, profile, breathe);
            }

            int lo = Math.Max(48, center - 10), hi = Math.Min(96, center + 10);
            int slot = Math.Max(1, (measures * barSlices) / Math.Max(1, chords.Count)); // slices per chord (harmonic rhythm; ≥1/bar)
            int cur = NearestChord(center, ChordPcs(chords[0]));
            int dir = rng.Next(2) == 0 ? 1 : -1;
            var notes = new List<RiffNote>();

            for (int m = 0; m < measures; m++)
            {
                int pos = 0;
                foreach (int dur in barR[m])
                {
                    var cp = ChordPcs(chords[Math.Min((m * barSlices + pos) / slot, chords.Count - 1)]); // chord at THIS slice
                    bool strong = pos % Spq == 0;
                    bool last = (m == measures - 1) && (pos + dur >= barSlices);
                    int prev = cur;
                    if (last) cur = NearestPc(cur, tonic);
                    else if (strong) cur = rng.Next(100) < 18 ? ChordToneToward(cur, cp, dir) : NearestChord(cur, cp);
                    else if (rng.Next(100) < 15) { /* repeated note — Bach subjects do this (corpus ~5-9%) */ }
                    else
                    {
                        if (cur > center + 7) dir = -1; else if (cur < center - 7) dir = 1; // steer back toward centre…
                        else if (rng.Next(100) < 30) dir = -dir;                              // …else reverse often → up/down waves
                        cur = ScaleStep(cur, dir, scale);
                    }
                    while (cur > hi) { cur -= 12; dir = -1; }   // fold any octave drift back into the band → stays centred
                    while (cur < lo) { cur += 12; dir = 1; }
                    cur = CapLeap(prev, cur, scale);            // FINAL: keep leaps <= a fifth (stepwise melody, no octave jumps)
                    int row = cur - 12;
                    if (row >= 0 && row < 96) notes.Add(new RiffNote(row, m * barSlices + pos, dur));
                    pos += dur;
                }
            }
            return new Riff { Name = "Mélodie", Notes = notes, LengthSlices = measures * barSlices, SlicesPerQuarter = Spq };
        }

        // Develop the subject across the piece — Bach's motivic DEVELOPMENT, not mere transposition. The subject is
        // stated plainly first (unit 0), then later 2-bar units INVERT it (flip the intervals), play it in RETROGRADE,
        // or FRAGMENT it (repeat its head), with occasional rhythmic AUGMENTATION (slower) / DIMINUTION (faster).
        static (int[] deltas, int sub) DevelopMotif(int[] baseMotif, int baseSub, int unit, Random rng, int virtuosity)
        {
            int len = baseMotif.Length; var d = new int[len];
            if (unit == 0) { for (int i = 0; i < len; i++) d[i] = baseMotif[i]; return (d, baseSub); } // state the subject
            int t = rng.Next(100);
            if (t < 30) for (int i = 0; i < len; i++) d[i] = -baseMotif[i];               // inversion
            else if (t < 50) for (int i = 0; i < len; i++) d[i] = baseMotif[len - 1 - i];  // retrograde
            else if (t < 70) for (int i = 0; i < len; i++) d[i] = baseMotif[0];            // fragmentation (head repeated)
            else for (int i = 0; i < len; i++) d[i] = baseMotif[i];                        // restate
            // VIRTUOSITY drives the diminution (32nd-run) rate; low virtuosity prefers augmentation (calmer).
            int vc = Math.Max(0, Math.Min(3, virtuosity));
            int dim = new[] { 0, 10, 25, 45 }[vc], aug = new[] { 34, 22, 12, 6 }[vc];
            int sub = baseSub, r = rng.Next(100);
            if (r < dim) sub = baseSub * 2;                                                // diminution (faster, 32nds)
            else if (r < dim + aug && baseSub > 1) sub = baseSub / 2;                      // augmentation (longer notes)
            return (d, sub);
        }

        // Baroque melody (Bach/Vivaldi) — MOTIVIC SEQUENCE + DEVELOPMENT (Fortspinnung), the trait the corpus flags
        // most: a short MOTIF (balanced scale-step deltas) is repeated, transposed a step at a time along the harmony
        // (a sequence); the sequence DIRECTION reverses periodically + at register edges (waves, no sawtooth). Every
        // 2-bar unit the motif is DEVELOPED (inversion/retrograde/fragmentation + augmentation/diminution — see
        // DevelopMotif), so it isn't mere transposition. DOMINANT-PULSE rhythm; a breath closes each 4-bar phrase.
        static Riff SequenceMelody(KeySignature key, List<(int root, int quality)> chords, int beatsPerBar, int barSlices, int measures, Random rng, int profile, int center, int virtuosity = 1)
        {
            int tonic = MusicTheory.TonicPc(key);
            var scale = ScaleSet(key);
            int lo = Math.Max(48, center - 13), hi = Math.Min(91, center + 11);
            int baseSub = (profile == 1 || profile == 3) ? 4 : 2;   // pulse: 16th for balanced/florid, 8th for calm/stately

            int[][] motifs = { new[] { 1, 1, -1 }, new[] { 1, -1, 1 }, new[] { -1, 1, 1 }, new[] { 1, -1, -1 }, new[] { -1, -1, 1 }, new[] { 2, -1, -1 }, new[] { -1, 2, -1 }, new[] { 1, 1, -2 } };
            int[] baseMotif = motifs[rng.Next(motifs.Length)];  // the SUBJECT (a fixed cell → motivic)
            int motifLen = baseMotif.Length + 1;

            // one beat of durations at `s` notes/beat, biased to a dominant pulse (Fortspinnung) with light variation.
            int[] PulseBeat(int s)
            {
                int u = Math.Max(1, Spq / Math.Max(1, s));
                if (s <= 1) return new[] { Spq };
                if (rng.Next(100) < 70) { var a = new int[s]; for (int i = 0; i < s; i++) a[i] = u; return a; } // uniform pulse
                var bb = new int[s - 1]; bb[0] = 2 * u; for (int i = 1; i < s - 1; i++) bb[i] = u; return bb;    // a slight variation
            }

            int slot = Math.Max(1, (measures * barSlices) / Math.Max(1, chords.Count)); // slices per chord (harmonic rhythm)
            int cur = NearestChord(center, ChordPcs(chords[0]));
            int seqDir = rng.Next(2) == 0 ? 1 : -1, flip = 0, flipEvery = rng.Next(2, 5), gi = 0, prevSlot = -1, unitIdx = -1;
            int[] devM = baseMotif; int devSub = baseSub;
            var notes = new List<RiffNote>();
            for (int m = 0; m < measures; m++)
            {
                int unit = m / 2;                                              // a 2-bar developmental unit
                if (unit != unitIdx) { unitIdx = unit; var dv = DevelopMotif(baseMotif, baseSub, unit, rng, virtuosity); devM = dv.deltas; devSub = dv.sub; gi = 0; }
                bool phraseEnd = (m % 4 == 3) || (m == measures - 1);
                for (int b = 0; b < beatsPerBar; b++)
                {
                    int beatStart = m * barSlices + b * Spq;
                    int slotIdx = Math.Min(beatStart / slot, chords.Count - 1);
                    var cp = ChordPcs(chords[slotIdx]);
                    if (slotIdx != prevSlot) { cur = NearestChord(cur, cp); prevSlot = slotIdx; } // re-ground at each chord change
                    if (phraseEnd && b == beatsPerBar - 1)                     // phrase "breath": one longer note
                    {
                        int t = (m == measures - 1) ? NearestPc(cur, tonic) : NearestChord(cur, cp);
                        while (t > hi) t -= 12; while (t < lo) t += 12;
                        if (t - 12 >= 0 && t - 12 < 96) notes.Add(new RiffNote(t - 12, beatStart, Spq));
                        cur = t; gi = 0; continue;                            // reset the motif phase after a breath
                    }
                    int pos = 0;
                    foreach (int dur in PulseBeat(devSub))
                    {
                        if (gi % motifLen == 0)                               // motif restart → transpose by the sequence step
                        {
                            if (cur >= hi - 1) seqDir = -1; else if (cur <= lo + 1) seqDir = 1;
                            else if (++flip >= flipEvery) { seqDir = -seqDir; flip = 0; flipEvery = rng.Next(2, 5); }
                            cur = ShiftScale(cur, seqDir, scale);
                        }
                        else cur = ShiftScale(cur, devM[(gi % motifLen) - 1], scale); // within-motif delta (developed)
                        while (cur > hi) cur = ScaleStep(cur, -1, scale);     // keep in range by STEPPING (no octave jump)
                        while (cur < lo) cur = ScaleStep(cur, 1, scale);
                        if (cur - 12 >= 0 && cur - 12 < 96) notes.Add(new RiffNote(cur - 12, beatStart + pos, dur));
                        gi++; pos += dur;
                    }
                }
            }
            return new Riff { Name = "Mélodie", Notes = notes, LengthSlices = measures * barSlices, SlicesPerQuarter = Spq };
        }

        // COMPOUND MELODY / implied polyphony — Bach's UNACCOMPANIED-soloist writing (cello suites, flute partita,
        // solo violin). A single line must imply the whole harmony, so unlike an accompanied melody it deliberately
        // LEAPS and arpeggiates. Each beat picks one of three figures over the bar's chord, spanning a WIDE range:
        //   • arpège      — climb the chord tones (root→3rd→5th→7th…), leaping;
        //   • bariolage    — alternate a low implied-BASS pedal with an upper voice (the classic two-voice illusion);
        //   • trait de gamme — a stepwise scale run (the conjunct connective tissue between arpeggios).
        // The mix lands ~half conjunct / half leaping, like the digested solo corpus (flute 53% leaps, cello 42%).
        static Riff CompoundMelody(KeySignature key, List<(int root, int quality)> chords, int beatsPerBar, int barSlices, int measures, Random rng, int profile, int center)
        {
            int tonic = MusicTheory.TonicPc(key);
            var scale = ScaleSet(key);
            int lo = Math.Max(40, center - 14), hi = Math.Min(93, center + 12); // a wide range — the line spans 2+ octaves
            var notes = new List<RiffNote>();
            int cur = NearestChord(center, ChordPcs(chords[0])), fig = 0, sdir = 1;
            for (int m = 0; m < measures; m++)
            {
                var cp = ChordPcs(chords[Math.Min(m, chords.Count - 1)]);
                var tones = new List<int>();
                for (int p = lo; p <= hi; p++) if (cp.Contains(((p % 12) + 12) % 12)) tones.Add(p); // chord tones realised across the range
                if (tones.Count == 0) tones.Add(center);
                int bass = tones[0];
                var rhythm = BarRhythm(beatsPerBar, rng, profile, m == measures - 1);
                int pos = 0, k = 0;
                foreach (int dur in rhythm)
                {
                    if (pos % Spq == 0) { fig = rng.Next(3); k = 0; if (rng.Next(2) == 0) sdir = -sdir; } // a fresh figure each beat
                    int note;
                    switch (fig)
                    {
                        case 0: note = tones[Math.Min(k, tones.Count - 1)]; break;                       // arpège up the chord
                        case 1: note = (k % 2 == 0) ? bass : tones[Math.Min(1 + (k / 2) % Math.Max(1, tones.Count - 1), tones.Count - 1)]; break; // bariolage (pedal bass + upper)
                        default: note = ScaleStep(cur, sdir, scale); break;                              // scale run
                    }
                    while (note > hi) note -= 12; while (note < lo) note += 12;
                    int row = note - 12;
                    if (row >= 0 && row < 96) notes.Add(new RiffNote(row, m * barSlices + pos, dur));
                    cur = note; k++; pos += dur;
                }
            }
            if (notes.Count > 0) { var ln = notes[notes.Count - 1]; int t = NearestPc(cur, tonic); notes[notes.Count - 1] = new RiffNote(Math.Max(0, Math.Min(95, t - 12)), ln.Start, ln.Length); }
            return new Riff { Name = "Mélodie", Notes = notes, LengthSlices = measures * barSlices, SlicesPerQuarter = Spq };
        }

        // A FIGURED BASS (basso continuo): one root per chord SLOT in a low register (octave-continuous), often split
        // into root + a stepwise passing tone toward the next root. The corpus bass is ~40% stepwise / 60% root leaps —
        // this gives that mix and a lively harmonic rhythm (the slot is a half-bar, so ~2 changes/bar).
        static Riff GenerateFiguredBass(KeySignature key, List<(int root, int quality)> chords, int slotSlices, Random rng, bool motor = false, bool sustain = false)
        {
            var scale = ScaleSet(key);
            var notes = new List<RiffNote>();
            int prev = 40;
            for (int ci = 0; ci < chords.Count; ci++)
            {
                int rp = (((chords[ci].root) % 12) + 12) % 12, b = rp + 36;
                while (b > prev + 6 && b - 12 >= 33) b -= 12; while (b < prev - 6 && b + 12 <= 52) b += 12; // nearest octave to prev
                int start = ci * slotSlices;
                int npc = (((chords[(ci + 1) % chords.Count].root) % 12) + 12) % 12, nb = npc + 36;
                while (nb > b + 6) nb -= 12; while (nb < b - 6) nb += 12;
                if (sustain) { notes.Add(new RiffNote(Math.Max(0, b - 12), start, slotSlices)); prev = b; } // harp: one HELD root per slot
                else if (motor)                                              // Vivaldi: a driving REPEATED-NOTE continuo
                {
                    int rep = Math.Max(6, Spq / 2);                          // repeated eighths on the root
                    for (int t = 0; t < slotSlices; t += rep)
                    {
                        bool lastInSlot = t + rep >= slotSlices;
                        int p = (lastInSlot && nb != b) ? ScaleStep(b, Math.Sign(nb - b), scale) : b; // last one steps toward the next root
                        notes.Add(new RiffNote(Math.Max(0, p - 12), start + t, Math.Min(rep, slotSlices - t)));
                        prev = p;
                    }
                }
                else if (slotSlices >= 24 && nb != b && rng.Next(100) < 45)  // split: root then a passing step toward the next root
                {
                    int half = slotSlices / 2, pass = ScaleStep(b, Math.Sign(nb - b), scale);
                    notes.Add(new RiffNote(Math.Max(0, b - 12), start, half));
                    notes.Add(new RiffNote(Math.Max(0, pass - 12), start + half, slotSlices - half));
                    prev = pass;
                }
                else { notes.Add(new RiffNote(Math.Max(0, b - 12), start, slotSlices)); prev = b; }
            }
            return new Riff { Name = "Basse", Notes = notes, LengthSlices = chords.Count * slotSlices, SlicesPerQuarter = Spq };
        }

        // Vivaldi's Italian string DOUBLING: a second voice that shadows the leader a 3rd or 6th BELOW (parallel motion,
        // unlike Bach's contrary counterpoint). Each leader note maps to a chord/scale tone the right interval below, in
        // this voice's register — consonant, parallel, and rhythmically locked to the lead.
        static Riff ParallelVoice(Riff leader, KeySignature key, List<(int root, int quality)> chords, int slot, int center, Random rng)
        {
            var scale = ScaleSet(key);
            int total = leader.LengthSlices, lo = Math.Max(40, center - 12), hi = Math.Min(93, center + 12);
            int below = rng.Next(2) == 0 ? 2 : 5;            // a 3rd (2 scale steps) or a 6th (5 scale steps) below
            var notes = new List<RiffNote>();
            foreach (var n in leader.Notes)
            {
                var cp = ChordPcs(chords[Math.Min(n.Start / Math.Max(1, slot), chords.Count - 1)]);
                int p = ShiftScale(n.Note + 12, -below, scale);             // the parallel interval below, in-scale
                bool strong = (n.Start % Spq) == 0;
                if (strong) p = NearestChord(p, cp);                        // snap to a chord tone on strong beats
                while (p > hi) p -= 12; while (p < lo) p += 12;
                int row = p - 12;
                if (row >= 0 && row < 96) notes.Add(new RiffNote(row, n.Start, n.Length));
            }
            return new Riff { Name = "Mélodie", Notes = notes, LengthSlices = total, SlicesPerQuarter = Spq };
        }

        // Pick a chord tone in [lo,hi] near `cur` that frames CONSONANTLY over the bass — Bach's outer-voice ideal is
        // imperfect consonances (3rds/6ths), avoiding a vertical dissonance on the beat, while staying smooth.
        static int FrameTone(int cur, HashSet<int> cp, int bassPitch, int lo, int hi, HashSet<int> scale)
        {
            int best = cur; double bestS = -1e9;
            for (int p = lo; p <= hi; p++)
            {
                if (!cp.Contains(((p % 12) + 12) % 12)) continue;
                int ivl = (((p - bassPitch) % 12) + 12) % 12;
                double s = (ivl == 3 || ivl == 4 || ivl == 8 || ivl == 9) ? 3 : (ivl == 0 || ivl == 7) ? 1 : -2; // 3rd/6th best
                s -= 0.1 * Math.Abs(p - cur);                                  // stay near the previous soloist note
                if (s > bestS) { bestS = s; best = p; }
            }
            return best;
        }

        // SOLO + CONTINUO line: a soloist coordinated with the bass into a two-voice CONTRAPUNTAL frame. On each beat it
        // lands on a chord tone forming a 3rd/6th/10th over the current bass root (FrameTone), arpeggiates/steps between
        // (compound-melody character), and occasionally SUSPENDS — holds its note across a chord change so it is
        // dissonant on the downbeat, then resolves DOWN by step (~14%). Harmony advances per SLOT (≈2/bar).
        static Riff SoloLine(KeySignature key, List<(int root, int quality)> chords, int slotSlices, int beatsPerBar, int barSlices, int measures, Random rng, int profile)
        {
            var scale = ScaleSet(key); int tonic = MusicTheory.TonicPc(key);
            int center = 69, lo = center - 11, hi = center + 13;
            int sub = (profile == 1 || profile == 3) ? 4 : 2, subDur = Spq / sub;
            int slots = chords.Count; var bass = new int[slots]; int pb = 40;
            for (int ci = 0; ci < slots; ci++) { int rp = (((chords[ci].root) % 12) + 12) % 12, b = rp + 36; while (b > pb + 6 && b - 12 >= 33) b -= 12; while (b < pb - 6 && b + 12 <= 52) b += 12; bass[ci] = b; pb = b; }
            var notes = new List<RiffNote>();
            int cur = center;
            for (int m = 0; m < measures; m++)
            {
                bool phraseEnd = (m % 4 == 3) || (m == measures - 1);
                for (int b = 0; b < beatsPerBar; b++)
                {
                    int beatStart = m * barSlices + b * Spq, slotIdx = Math.Min(slots - 1, beatStart / slotSlices);
                    var cp = ChordPcs(chords[slotIdx]);
                    if (phraseEnd && b == beatsPerBar - 1) { int tt = NearestPc(cur, tonic); while (tt > hi) tt -= 12; while (tt < lo) tt += 12; notes.Add(new RiffNote(Math.Max(0, tt - 12), beatStart, Spq)); cur = tt; continue; }
                    bool suspend = (beatStart % slotSlices) == 0 && slotIdx > 0 && cur > lo + 2 && rng.Next(100) < 14;
                    for (int k = 0; k < sub; k++)
                    {
                        int note;
                        if (k == 0) note = suspend ? cur : FrameTone(cur, cp, bass[slotIdx], lo, hi, scale); // frame over the bass (or hold = suspension)
                        else if (k == 1 && suspend) note = ScaleStep(cur, -1, scale);                        // resolve the suspension down by step
                        else if (rng.Next(100) < 55) note = ScaleStep(cur, rng.Next(2) == 0 ? 1 : -1, scale); // step
                        else note = ChordToneToward(cur, cp, rng.Next(2) == 0 ? 1 : -1);                     // small arpeggio leap
                        while (note > hi) note -= 12; while (note < lo) note += 12;
                        notes.Add(new RiffNote(Math.Max(0, note - 12), beatStart + k * subDur, subDur));
                        cur = note;
                    }
                }
            }
            if (notes.Count > 0) { var ln = notes[notes.Count - 1]; int t = NearestPc(cur, tonic); notes[notes.Count - 1] = new RiffNote(Math.Max(0, Math.Min(95, t - 12)), ln.Start, ln.Length); }
            return new Riff { Name = "Soliste", Notes = notes, LengthSlices = measures * barSlices, SlicesPerQuarter = Spq };
        }

        // A TONAL PLAN: build the progression as sections in different keys — home → a RELATED key (the dominant, or
        // the relative major in minor) → home — so the harmony MODULATES and returns. Each section is a mini-cadence in
        // its local key; the final (home) section resolves to the home tonic. The melody anchors to these chords, so the
        // modulation is heard. (Sectional key change, no pivot chord — a first, audible approximation.)
        static KeySignature RelatedKey(KeySignature key)
        {
            bool minor = key?.Mode == 1;
            int tonic = MusicTheory.TonicPc(key);
            int majPc = minor ? (tonic + 3) % 12 : tonic;          // the major key sharing the signature
            int fifths = (7 * majPc) % 12; if (fifths > 6) fifths -= 12;
            if (minor) return KeySig.FromFifths(fifths, false);    // modulate to the relative MAJOR
            int df = fifths + 1; if (df > 6) df -= 12;             // major → the DOMINANT
            return KeySig.FromFifths(df, false);
        }

        static List<(int root, int quality)> BuildModulatingProgression(KeySignature key, int slots, int style, int seed)
        {
            int s1 = Math.Max(2, slots * 2 / 5), s2 = Math.Max(2, slots / 4);
            if (s1 + s2 >= slots) s2 = Math.Max(1, slots - s1 - 1);
            int s3 = Math.Max(1, slots - s1 - s2);
            var rel = RelatedKey(key);
            var outl = new List<(int, int)>();
            outl.AddRange(MusicTheory.Cadence(key, 0, s1, style, seed));        // home
            outl.AddRange(MusicTheory.Cadence(rel, 0, s2, style, seed + 101));  // related key (tonicised)
            outl.AddRange(MusicTheory.Cadence(key, 0, s3, style, seed + 202));  // back home (ends on the home tonic)
            while (outl.Count > slots) outl.RemoveAt(outl.Count - 1);
            while (outl.Count < slots) outl.Add(outl[outl.Count - 1]);
            return outl;
        }

        static int BassPitchAt(Riff bass, int slice)
        {
            foreach (var n in bass.Notes) if (n.Start <= slice && n.End > slice) return n.Note + 12;
            return -1;
        }

        // OUTER-VOICE FRAME: on each chord change (slot start), if the top voice is vertically DISSONANT with the bass,
        // nudge it (within a small range) to the nearest chord tone forming a 3rd/6th — Bach's soprano-bass framework.
        // Only at chord changes, so the melodic motif elsewhere is preserved.
        static void FrameTopVoice(Riff top, Riff bass, List<(int root, int quality)> chords, int slot, KeySignature key)
        {
            if (top == null || bass == null || chords.Count == 0) return;
            var scale = ScaleSet(key);
            var ns = top.Notes;
            for (int i = 0; i < ns.Count; i++)
            {
                if (ns[i].Start % slot != 0) continue;                 // chord-change points only
                int bp = BassPitchAt(bass, ns[i].Start); if (bp < 0) continue;
                int p = ns[i].Note + 12, ivl = (((p - bp) % 12) + 12) % 12;
                if (ivl == 0 || ivl == 3 || ivl == 4 || ivl == 7 || ivl == 8 || ivl == 9) continue; // already consonant
                var cp = ChordPcs(chords[Math.Min(ns[i].Start / slot, chords.Count - 1)]);
                int framed = FrameTone(p, cp, bp, p - 6, p + 6, scale);  // a consonant chord tone near the current note
                ns[i] = new RiffNote(Math.Max(0, Math.Min(95, framed - 12)), ns[i].Start, ns[i].Length);
            }
        }

        // One bar of note durations, built from per-beat rhythmic CELLS (each summing to a quarter @ Spq=24) chosen at
        // random so each bar differs. The cell POOL is the rhythmic PROFILE — and the pools are weighted (a cell listed
        // N times is N× likelier) to match the duration distributions DIGESTED from real Bach corpora:
        //   0 calm      — quarters/eighths (non-baroque slow styles).
        //   1 balanced  — WTC: ~46% 16th + 28% 8th.
        //   2 stately   — Art of Fugue: ~42% 8th + 20% quarter + 13% 16th (calm counterpoint).
        //   3 florid    — Goldberg: ~46% 16th + 19% 32nd + 22% 8th (virtuosic, with 32nd runs).
        // `breathe` ends the bar on a plain quarter (a phrase "breath") instead of subdivisions.
        static readonly int[][] CellsCalm     = { new[] { 24 }, new[] { 24 }, new[] { 12, 12 }, new[] { 12, 12 }, new[] { 12, 6, 6 }, new[] { 18, 6 } };
        static readonly int[][] CellsBalanced = { new[] { 6, 6, 6, 6 }, new[] { 6, 6, 6, 6 }, new[] { 6, 6, 6, 6 }, new[] { 12, 6, 6 }, new[] { 12, 6, 6 }, new[] { 6, 6, 12 }, new[] { 6, 6, 12 }, new[] { 12, 12 }, new[] { 18, 6 }, new[] { 24 } };
        static readonly int[][] CellsStately  = { new[] { 12, 12 }, new[] { 12, 12 }, new[] { 12, 12 }, new[] { 24 }, new[] { 24 }, new[] { 12, 6, 6 }, new[] { 18, 6 }, new[] { 6, 6, 12 } };
        static readonly int[][] CellsFlorid   = { new[] { 6, 6, 6, 6 }, new[] { 6, 6, 6, 6 }, new[] { 3, 3, 6, 12 }, new[] { 6, 3, 3, 12 }, new[] { 3, 3, 3, 3, 6, 6 }, new[] { 6, 6, 12 }, new[] { 12, 6, 6 }, new[] { 3, 3, 3, 3, 3, 3, 3, 3 } };
        // ballade/film: deliberately MIXED (no dominant value → free, rubato feel) — quarters/eighths + dotted +
        // 16ths + eighth-triplets; long notes come from the phrase breaths/period. Matches music1.mid (rhythm
        // self-similarity ~5%, lots of triplets/dotted).
        static readonly int[][] CellsBallad   = { new[] { 24 }, new[] { 24 }, new[] { 12, 12 }, new[] { 12, 12 }, new[] { 18, 6 }, new[] { 6, 18 }, new[] { 12, 6, 6 }, new[] { 8, 8, 8 }, new[] { 6, 6, 12 } };
        static int[][] CellPool(int profile)
        {
            switch (profile) { case 2: return CellsStately; case 3: return CellsFlorid; case 4: return CellsBallad; case 1: return CellsBalanced; default: return CellsCalm; }
        }
        static List<int> BarRhythm(int beatsPerBar, Random rng, int profile, bool breathe)
        {
            var cells = CellPool(profile);
            var durs = new List<int>();
            for (int b = 0; b < beatsPerBar; b++)
            {
                if (breathe && b == beatsPerBar - 1) { durs.Add(Spq); break; } // last beat = one held quarter
                durs.AddRange(cells[rng.Next(cells.Length)]);
            }
            return durs;
        }

        static HashSet<int> ChordPcs((int root, int quality) c)
        {
            var notes = PatternGenerator.ChordNotes(c.root, 4, c.quality, 0);
            var set = new HashSet<int>();
            foreach (int n in notes) set.Add(((n % 12) + 12) % 12);
            return set;
        }

        static int NearestChord(int cur, HashSet<int> pcs)
        {
            for (int r = 0; r < 12; r++) { if (pcs.Contains(((cur + r) % 12 + 12) % 12)) return cur + r; if (pcs.Contains(((cur - r) % 12 + 12) % 12)) return cur - r; }
            return cur;
        }

        // The next chord tone strictly in direction `dir` from `cur` — a 3rd/4th away — for the occasional tasteful
        // melodic leap (kept rare and balanced so the line stays mostly conjunct, never a one-way ramp).
        static int ChordToneToward(int cur, HashSet<int> pcs, int dir)
        {
            for (int d = 1; d <= 12; d++) { int p = cur + dir * d; if (pcs.Contains(((p % 12) + 12) % 12)) return p; }
            return cur;
        }

        // PHASE 1 — BREATHING: turn a fully legato line into one that articulates and breathes (the corpus shows
        // détaché gaps and phrase rests — Bach's fugues are ~13% silence). Applies to ANY style. level 1 = light,
        // 2 = marked. It (a) detaches some notes (shortens them so a small gap precedes the next), (b) occasionally
        // drops a short weak-beat note (a rest), and (c) clearly shortens phrase-ending notes (a breath). Total
        // length is preserved (a dropped/short note just leaves silence).
        static void AddBreathing(Riff r, int beatsPerBar, int barSlices, Random rng, int level)
        {
            if (level <= 0 || r == null || r.Notes.Count == 0) return;
            int six = Spq / 4;
            double detach = level >= 2 ? 0.33 : 0.18, restP = level >= 2 ? 0.12 : 0.05;
            var outl = new List<RiffNote>(r.Notes.Count);
            for (int i = 0; i < r.Notes.Count; i++)
            {
                var n = r.Notes[i];
                bool weak = (n.Start % Spq) != 0;
                int barIdx = barSlices > 0 ? n.Start / barSlices : 0;
                bool phraseEnd = (barIdx % 4 == 3) && ((n.Start % barSlices) + n.Length >= barSlices - six);
                // a short weak note may become a rest (drop it) — but not right after an existing gap (no double rest)
                if (weak && n.Length <= six * 2 && rng.NextDouble() < restP && (outl.Count == 0 || outl[outl.Count - 1].End >= n.Start)) continue;
                int len = n.Length;
                if (phraseEnd && len > six * 2) len = Math.Max(six * 2, len / 2);              // breath at phrase end
                else if (len > six && rng.NextDouble() < detach) len = Math.Max(six, len - six); // détaché gap
                outl.Add(new RiffNote(n.Note, n.Start, len));
            }
            if (outl.Count > 0) r.Notes = outl; // never empty the line
        }

        // Delay a voice's entry: it RESTS during the intro and enters at `fromSlice` (e.g., a violin coming in over an
        // established harp+bass texture — a progressive build-up). Length is preserved (the trailing silence stays).
        static void DelayEntry(Riff r, int fromSlice)
        {
            if (r == null || fromSlice <= 0) return;
            var outl = new List<RiffNote>();
            foreach (var n in r.Notes) if (n.Start >= fromSlice) outl.Add(n);
            if (outl.Count > 0) r.Notes = outl;
        }

        // Split a full-piece line into ONE RIFF PER BAR and add a track that plays them back-to-back. The result sounds
        // identical to a single long riff, but every bar is an independent, editable Riff in the riff editor (the user
        // can tweak a single measure without touching the rest). Notes are clipped to their bar (held notes across a
        // barline are truncated — fine for the within-bar classical material these forms produce).
        static void AddBarRiffs(List<TimelineTrack> tracks, int instrument, string name, Riff full, int totalBars, int barSlices, List<Riff> newRiffs)
        {
            var tr = new TimelineTrack { Type = TimelineTrackType.Instrument, Instrument = instrument, Name = name };
            for (int b = 0; b < totalBars; b++)
            {
                int lo = b * barSlices, hi = lo + barSlices;
                var barNotes = new List<RiffNote>();
                if (full != null) foreach (var n in full.Notes)
                    if (n.Start >= lo && n.Start < hi)
                        barNotes.Add(new RiffNote(n.Note, n.Start - lo, Math.Max(1, Math.Min(n.Length, hi - n.Start))));
                var br = new Riff { Name = name + " m." + (b + 1), Notes = barNotes, LengthSlices = barSlices, SlicesPerQuarter = Spq };
                newRiffs?.Add(br);
                tr.Items.Add(new TimelineItem { Module = new PlayRiffModule { RiffId = br.Id } });
            }
            tracks.Add(tr);
        }

        // Render a chord progression as a CLASSICAL ALBERTI accompaniment (the ex_class2 left-hand articulation): each
        // chord slot is broken into eighth notes low–high–mid–high (the Alberti figure), repeated to fill the slot.
        // Built bar-aligned (slotSlices = barSlices / chordsPerBar) so it splits cleanly into per-bar riffs — unlike
        // CadenceModule, whose renderer lays one chord per WHOLE bar. Voice-led (reuses VoiceLead) for smooth motion.
        static Riff RenderAlberti(KeySignature key, List<(int root, int quality)> raw, int slotSlices, int octave)
        {
            var vl = MusicTheory.VoiceLead(raw, octave);
            var notes = new List<RiffNote>();
            int eighth = Math.Max(3, Spq / 2);
            for (int i = 0; i < raw.Count; i++)
            {
                var ch = PatternGenerator.ChordNotes(raw[i].root, octave + vl[i].shift, raw[i].quality, vl[i].inversion);
                if (ch.Length == 0) continue;
                int loN = ch[0], hiN = ch[ch.Length - 1], midN = ch[ch.Length >= 3 ? 1 : 0];
                int[] pat = { loN, hiN, midN, hiN };   // the Alberti figure (broken chord)
                int start = i * slotSlices, k = 0;
                for (int t = 0; t < slotSlices; t += eighth)
                {
                    int p = pat[k % pat.Length] - 12;  // RiffNote stores MIDI − 12
                    if (p >= 0 && p < 96) notes.Add(new RiffNote(p, start + t, Math.Min(eighth, slotSlices - t)));
                    k++;
                }
            }
            return new Riff { Name = "Accords", Notes = notes, LengthSlices = raw.Count * slotSlices, SlicesPerQuarter = Spq };
        }

        // PHASE 2 — POLISH (voice-leading): a light pass over the generated voices. (a) within a voice, a leap of a
        // 4th+ must not be followed by another leap the same way → recover by a step in the opposite direction.
        // (b) across voices, break PARALLEL octaves/unisons at aligned attacks by nudging the upper voice to a nearby
        // chord tone. Style-agnostic, conservative (one pass) — the "DeepBach-lite" idea without a neural net.
        static void PolishVoices(List<Riff> voices, KeySignature key, List<(int root, int quality)> raw, int barSlices)
        {
            if (voices == null || voices.Count == 0 || raw.Count == 0) return;
            var scale = ScaleSet(key);
            foreach (var r in voices)
            {
                var ns = r.Notes;
                for (int i = 1; i + 1 < ns.Count; i++)
                {
                    int a = ns[i].Note - ns[i - 1].Note, b = ns[i + 1].Note - ns[i].Note;
                    if (Math.Abs(a) >= 5 && Math.Sign(b) == Math.Sign(a) && Math.Abs(b) >= 3)
                    {
                        int np = ScaleStep(ns[i].Note + 12, -Math.Sign(a), scale) - 12; // a step the other way
                        ns[i + 1] = new RiffNote(Math.Max(0, Math.Min(95, np)), ns[i + 1].Start, ns[i + 1].Length);
                    }
                }
            }
            for (int x = 0; x < voices.Count; x++)
                for (int y = x + 1; y < voices.Count; y++)
                {
                    var A = voices[x].Notes; var B = voices[y].Notes;
                    var bAt = new Dictionary<int, int>();
                    for (int j = 0; j < B.Count; j++) if (!bAt.ContainsKey(B[j].Start)) bAt[B[j].Start] = j;
                    int pAi = -1, pBj = -1;
                    for (int i = 0; i < A.Count; i++)
                    {
                        if (!bAt.TryGetValue(A[i].Start, out int j)) continue; // only aligned attacks
                        int pa = A[i].Note + 12, pb = B[j].Note + 12;
                        int ivl = (((pa - pb) % 12) + 12) % 12;
                        if (pAi >= 0 && (ivl == 0 || ivl == 7))
                        {
                            int qa = A[pAi].Note + 12, qb = B[pBj].Note + 12;
                            int pIvl = (((qa - qb) % 12) + 12) % 12;
                            bool parallel = pIvl == ivl && pa != qa && pb != qb && Math.Sign(pa - qa) == Math.Sign(pb - qb);
                            bool collide = pa == pb;
                            if (parallel || collide)
                            {
                                int m = barSlices > 0 ? A[i].Start / barSlices : 0;
                                var ch = ChordPcs(raw[Math.Min(m, raw.Count - 1)]);
                                if (pa >= pb) A[i] = new RiffNote(Math.Max(0, Math.Min(95, NearestChord(ScaleStep(pa, 1, scale), ch) - 12)), A[i].Start, A[i].Length);
                                else B[j] = new RiffNote(Math.Max(0, Math.Min(95, NearestChord(ScaleStep(pb, 1, scale), ch) - 12)), B[j].Start, B[j].Length);
                            }
                        }
                        pAi = i; pBj = j;
                    }
                }
        }

        static int NearestPc(int cur, int pc)
        {
            for (int r = 0; r < 12; r++) { if ((((cur + r) % 12) + 12) % 12 == pc) return cur + r; if ((((cur - r) % 12) + 12) % 12 == pc) return cur - r; }
            return cur;
        }

        static int ScaleStep(int midi, int dir, HashSet<int> scale)
        {
            int m = midi + dir, guard = 0;
            while (!scale.Contains(((m % 12) + 12) % 12) && guard++ < 12) m += dir;
            return m;
        }

        // Nearest pitch to `from` whose pitch-class is in the scale (for tonal imitation snapping).
        static int NearestScale(int from, HashSet<int> scale)
        {
            for (int d = 0; d < 7; d++)
            {
                if (scale.Contains((((from + d) % 12) + 12) % 12)) return from + d;
                if (scale.Contains((((from - d) % 12) + 12) % 12)) return from - d;
            }
            return from;
        }

        // Keep melodic leaps small. The corpus is ~65-69% stepwise and its leaps are almost always <= a fifth
        // (dominated by 3rds), big leaps rare. If prev->note exceeds maxLeap (default a fifth), fold note within an
        // octave of prev then snap to the nearest scale tone at most a fifth away — preserving the intended direction.
        static int CapLeap(int prev, int note, HashSet<int> scale, int maxLeap = 7)
        {
            while (note - prev > 12) note -= 12;
            while (prev - note > 12) note += 12;
            if (Math.Abs(note - prev) > maxLeap)
                note = NearestScale(prev + Math.Sign(note - prev) * maxLeap, scale);
            return note;
        }

        // Bach-style imitative COUNTERPOINT: the follower restates the leader's subject (same contour + rhythm)
        // entering `delayBars` later — a CANON. To stay consonant against our generated harmony it is a TONAL
        // imitation: each pitch tracks the leader's interval shape but is snapped to a chord tone on strong beats and
        // to a scale tone elsewhere, transposed into this voice's register. The pre-entry bars rest (the answer waits
        // for the subject) — the hallmark staggered fugal/invention entry.
        static Riff CanonMelody(Riff leader, KeySignature key, List<(int root, int quality)> chords, int beatsPerBar, int barSlices, int measures, int delayBars, int center)
        {
            var scale = ScaleSet(key);
            int total = measures * barSlices, delay = Math.Max(1, delayBars) * barSlices;
            int slot = Math.Max(1, total / Math.Max(1, chords.Count)); // slices per chord (harmonic rhythm)
            var notes = new List<RiffNote>();
            int prevP = int.MinValue;
            foreach (var n in leader.Notes)
            {
                int t = n.Start + delay;
                if (t >= total) break;
                var cp = ChordPcs(chords[Math.Min(t / slot, chords.Count - 1)]); // chord at THIS slice
                int pitch = n.Note + 12;                  // leader MIDI
                while (pitch > center + 9) pitch -= 12;    // fold roughly into this voice's register
                while (pitch < center - 9) pitch += 12;
                bool strong = (t % Spq) == 0;
                pitch = strong ? NearestChord(pitch, cp) : NearestScale(pitch, scale);
                if (prevP != int.MinValue) pitch = CapLeap(prevP, pitch, scale); // <= a fifth → no octave jumps
                int row = pitch - 12;
                if (row >= 0 && row < 96) notes.Add(new RiffNote(row, t, n.Length));
                prevP = pitch;
            }
            return new Riff { Name = "Mélodie", Notes = notes, LengthSlices = total, SlicesPerQuarter = Spq };
        }

        static HashSet<int> ScaleSet(KeySignature key)
        {
            int tonic = MusicTheory.TonicPc(key);
            var off = MusicalMode.Scale(MusicalMode.Effective(key));
            var s = new HashSet<int>();
            foreach (var o in off) s.Add(((tonic + o) % 12 + 12) % 12);
            return s;
        }

        // Move a MIDI note by N diatonic scale steps (negative = down).
        static int ShiftScale(int midi, int steps, HashSet<int> scale)
        {
            int dir = steps >= 0 ? 1 : -1, n = Math.Abs(steps), m = midi;
            for (int i = 0; i < n; i++) m = ScaleStep(m, dir, scale);
            return m;
        }
    }
}
