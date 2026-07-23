using System;

namespace MusicTracker.Engine.Flow
{
    /// <summary>
    /// Generates a looping drum groove as a <see cref="Riff"/>. Internally it builds a one-bar LANE
    /// grid (row = drum-lane index, see <see cref="LaneNames"/>) and maps each lane to its General
    /// MIDI percussion key, the same way <see cref="PatternGenerator"/> uses a voice grid. Each hit is
    /// a SINGLE slice (a crisp trigger, so the rhythm is clear — drums are one-shots). The "Personnalisé"
    /// style uses the user's hand-drawn lane grid. Grid: 24 slices/quarter; slice row = MIDI key - 12.
    /// </summary>
    public static class DrumPattern
    {
        public const int SlicesPerQuarter = 24;

        // Editor rows: a drum lane -> its GM percussion key. Order defines the row order in the editor.
        // Lanes 0..11 are the common kit (their INDICES are fixed — the built-in style writers and any saved
        // custom grids rely on them). Lanes 12.. add the rest of the General MIDI percussion set (35..81) so
        // every GM sound — bongos, congas, timbales, agogo, cabasa, maracas, guiro, claves, wood block, cuica,
        // triangle, tambourine, cowbell, whistle… — is available. Audio already works: a drum note plays its GM
        // key directly on the SoundFont bank-128 kit.
        public static readonly string[] LaneNames =
        {
            "Grosse caisse", "Caisse claire", "Charley fermé", "Charley ouvert", "Charley pied",     // 0..4
            "Rim (side stick)", "Clap", "Tom basse", "Tom médium", "Tom aigu", "Crash", "Ride",       // 5..11
            "Grosse caisse acous.", "Caisse claire électro", "Tom très grave", "Tom grave (floor)",   // 12..15
            "Tom médium-aigu", "Cymbale chinoise", "Cloche de ride", "Splash", "Crash 2", "Ride 2",    // 16..21
            "Tambourin", "Cowbell", "Vibraslap",                                                       // 22..24
            "Bongo aigu", "Bongo grave", "Conga aigu étouffé", "Conga aigu", "Conga grave",            // 25..29
            "Timbale aiguë", "Timbale grave", "Agogo aigu", "Agogo grave",                             // 30..33
            "Cabasa", "Maracas", "Sifflet court", "Sifflet long", "Guiro court", "Guiro long",         // 34..39
            "Claves", "Wood block aigu", "Wood block grave", "Cuica étouffé", "Cuica ouvert",          // 40..44
            "Triangle étouffé", "Triangle ouvert",                                                     // 45..46
        };
        static readonly int[] LaneKeys =
        {
            36, 38, 42, 46, 44, 37, 39, 45, 47, 50, 49, 51,   // 0..11
            35, 40, 41, 43, 48, 52, 53, 55, 57, 59,           // 12..21
            54, 56, 58,                                       // 22..24
            60, 61, 62, 63, 64,                               // 25..29
            65, 66, 67, 68,                                   // 30..33
            69, 70, 71, 72, 73, 74,                           // 34..39
            75, 76, 77, 78, 79,                               // 40..44
            80, 81,                                           // 45..46
        };

        public static int LaneCount => LaneKeys.Length;

        // GM key -> lane, exact where the lane exists (now a near-bijection over 35..81), nearest otherwise.
        static readonly System.Collections.Generic.Dictionary<int, int> KeyToLane = BuildKeyToLane();
        static System.Collections.Generic.Dictionary<int, int> BuildKeyToLane()
        {
            var d = new System.Collections.Generic.Dictionary<int, int>();
            for (int l = 0; l < LaneKeys.Length; l++) if (!d.ContainsKey(LaneKeys[l])) d[LaneKeys[l]] = l;
            return d;
        }

        /// <summary>The canonical GM percussion key for a lane (inverse of <see cref="LaneForKey"/>).</summary>
        public static int KeyForLane(int lane) => (lane >= 0 && lane < LaneKeys.Length) ? LaneKeys[lane] : 38;

        /// <summary>Maps any GM percussion key to its lane (exact where possible, else the nearest lane by key).</summary>
        public static int LaneForKey(int key)
        {
            if (KeyToLane.TryGetValue(key, out int lane)) return lane;
            int best = 0, bd = int.MaxValue;           // out-of-range key -> nearest lane by key
            for (int l = 0; l < LaneKeys.Length; l++) { int d = Math.Abs(LaneKeys[l] - key); if (d < bd) { bd = d; best = l; } }
            return best;
        }

        // Lane indices (NOT MIDI) used by the built-in style writers (fixed — see LaneNames).
        const int KICK = 0, SNARE = 1, CLOSED = 2, OPEN = 3, PEDAL = 4, RIM = 5, CLAP = 6,
                  LOWTOM = 7, MIDTOM = 8, HITOM = 9, CRASH = 10, RIDE = 11;

        public static readonly string[] StyleNames =
        {
            "Rock — basique",        // 0
            "Rock — appuyé",         // 1
            "Pop",                   // 2
            "Funk (16th)",           // 3
            "Disco (4 au sol)",      // 4
            "Jazz swing",            // 5
            "Shuffle / Blues",       // 6
            "Bossa nova",            // 7
            "Half-time",             // 8
            "Hip-hop / boom-bap",    // 9
            "Marche",                // 10
            "Reggae one-drop",       // 11
            "Valse",                 // 12
            "Punk (rapide)",         // 13
            "Ballade (cross-stick)", // 14
            "Trap (hats roulés)",    // 15
            "Personnalisé…",         // last
        };

        public static readonly int CustomStyle = StyleNames.Length - 1;

        public static readonly string[] DensityNames = { "Auto", "Léger", "Normal", "Dense" };

        public static Riff Generate(DrumPatternModule m)
        {
            // Explicit NOTE-LIST phrase (drawn like a riff, or written by the AI): each note is ONE hit at its
            // start (percussion one-shot — the note LENGTH is only for editing). The stored notes are ONE unit;
            // it is tiled Repeats times (so the "Répétitions" field loops the motif, like the style paths). Takes
            // priority over the style / one-bar-grid paths. Note = drum LANE index.
            if (m.CustomNotes != null && m.CustomNotes.Count > 0)
            {
                int spqN = m.CustomSlicesPerQuarter > 0 ? m.CustomSlicesPerQuarter : SlicesPerQuarter;
                // Unit length = the stored grid length (so it matches ModuleDuration, which uses CustomSlices).
                int unit = (m.CustomSlices != null && m.CustomSlices.Length > 0)
                         ? m.CustomSlices.Length
                         : Math.Max(1, MusicTracker.Engine.RiffNotes.LengthOf(m.CustomNotes));
                int reps = Math.Max(1, m.Repeats);
                var outN = new SequencerSlice[unit * reps];
                for (int r = 0; r < reps; r++)
                    foreach (var n in m.CustomNotes)
                    {
                        if (n.Note < 0 || n.Note >= LaneKeys.Length || n.Start < 0 || n.Start >= unit) continue;
                        int row = LaneKeys[n.Note] - 12;                 // GM key -> Riff row (Note 0 == MIDI 12)
                        int at = r * unit + n.Start;
                        if (row >= 0 && row < 96 && at < outN.Length) outN[at].On(row, true); // single trigger at the start
                    }
                return new Riff { Name = "Drums", Slices = outN, SlicesPerQuarter = spqN };
            }

            int repeats = Math.Max(1, m.Repeats);
            SequencerSlice[] outp;
            int spq;

            if (m.Style == CustomStyle && m.CustomSlices != null && m.CustomSlices.Length > 0)
            {
                var bar = m.CustomSlices; // one-bar lane grid drawn by the user
                spq = m.CustomSlicesPerQuarter > 0 ? m.CustomSlicesPerQuarter : SlicesPerQuarter;
                outp = new SequencerSlice[bar.Length * repeats];
                for (int r = 0; r < repeats; r++) MapBar(bar, outp, r * bar.Length);
            }
            else
            {
                int beats = Math.Max(1, m.BeatsPerBar);
                int barSlices = beats * SlicesPerQuarter;
                spq = SlicesPerQuarter;
                outp = new SequencerSlice[barSlices * repeats];
                for (int bar = 0; bar < repeats; bar++)
                {
                    var g = new SequencerSlice[barSlices];
                    if (m.FillLast && bar == repeats - 1 && repeats > 1) RenderFill(g, beats);
                    else RenderStyle(g, beats, Clamp(m.Style, 0, StyleNames.Length - 1), m.Density, bar);
                    MapBar(g, outp, bar * barSlices);
                }
            }

            return new Riff { Name = "Drums — " + Get(StyleNames, m.Style), Slices = outp, SlicesPerQuarter = spq };
        }

        /// <summary>A built-in style's one-bar lane grid (to seed/illustrate the custom editor).</summary>
        public static SequencerSlice[] LaneBarForStyle(int style, int beats)
        {
            int b = Math.Max(1, beats);
            var g = new SequencerSlice[b * SlicesPerQuarter];
            RenderStyle(g, b, Clamp(style, 0, StyleNames.Length - 1), 0, 0);
            return g;
        }

        /// <summary>A built-in style's one bar as a NOTE LIST (Note = lane index), each hit a SEPARATE length-1 note
        /// so back-to-back same-lane hits (e.g. 16th hats) stay distinct — used to seed the riff-like drum editor.</summary>
        public static System.Collections.Generic.List<MusicTracker.Engine.RiffNote> LaneNotesForStyle(int style, int beats)
        {
            var g = LaneBarForStyle(style, beats);
            var notes = new System.Collections.Generic.List<MusicTracker.Engine.RiffNote>();
            for (int s = 0; s < g.Length; s++)
                for (int lane = 0; lane < LaneKeys.Length; lane++)
                    if (g[s].On(lane)) notes.Add(new MusicTracker.Engine.RiffNote(lane, s, 1));
            return notes;
        }

        /// <summary>Detects whether a note-list phrase repeats every X BEATS and, if so, returns just one period as
        /// the <paramref name="unit"/> plus how many times it repeats — so the module can store only the useful
        /// length and loop it via Repeats. If no shorter period is found, returns the whole phrase (repeats = 1).</summary>
        public static void CompressPeriodic(System.Collections.Generic.List<MusicTracker.Engine.RiffNote> notes,
                                            int totalLen, int spq,
                                            out System.Collections.Generic.List<MusicTracker.Engine.RiffNote> unit,
                                            out int unitLen, out int repeats)
        {
            unit = notes; unitLen = Math.Max(1, totalLen); repeats = 1;
            if (notes == null || notes.Count == 0 || totalLen <= 0 || spq <= 0) return;

            var set = new System.Collections.Generic.HashSet<long>();
            foreach (var n in notes) set.Add(NoteKey(n.Note, n.Start, n.Length));

            for (int P = spq; P < totalLen; P += spq)               // whole-beat periods, smallest first
            {
                if (totalLen % P != 0) continue;
                if (IsPeriodic(set, notes, P, totalLen))
                {
                    var u = new System.Collections.Generic.List<MusicTracker.Engine.RiffNote>();
                    foreach (var n in notes) if (n.Start < P) u.Add(n);
                    unit = u; unitLen = P; repeats = totalLen / P;
                    return;
                }
            }
        }

        static bool IsPeriodic(System.Collections.Generic.HashSet<long> set,
                               System.Collections.Generic.List<MusicTracker.Engine.RiffNote> notes, int P, int total)
        {
            int reps = total / P;
            foreach (var n in notes)                                 // every note reduces to a period-0 copy
                if (!set.Contains(NoteKey(n.Note, n.Start % P, n.Length))) return false;
            foreach (var n in notes)                                 // every period-0 note appears in every period
            {
                if (n.Start >= P) continue;
                for (int k = 1; k < reps; k++)
                    if (!set.Contains(NoteKey(n.Note, n.Start + k * P, n.Length))) return false;
            }
            return true;
        }

        static long NoteKey(int lane, int start, int len)
            => ((long)(lane & 0xFF) << 48) | ((long)(start & 0xFFFFF) << 20) | (long)(len & 0xFFFFF);

        // ---- rasterization (lane grid: row = drum-lane index) ----------------------

        // Map a one-bar lane grid to GM drum keys in the output (each ON cell = a 1-slice hit).
        static void MapBar(SequencerSlice[] laneBar, SequencerSlice[] outp, int offset)
        {
            for (int s = 0; s < laneBar.Length; s++)
                for (int lane = 0; lane < LaneKeys.Length; lane++)
                    if (laneBar[s].On(lane))
                    {
                        int row = LaneKeys[lane] - 12;
                        if (row >= 0 && row < 96 && offset + s < outp.Length) outp[offset + s].On(row, true);
                    }
        }

        static void Hit(SequencerSlice[] g, int slice, int lane)
        {
            if (lane < 0 || lane >= 96) return;
            if (slice >= 0 && slice < g.Length) g[slice].On(lane, true); // a single slice
        }

        // Place a hit at beat b (0-based) + a fraction f of a quarter (0 = on the beat, 0.5 = the "and").
        static void At(SequencerSlice[] g, double beatPlusFrac, int lane)
            => Hit(g, (int)Math.Round(beatPlusFrac * SlicesPerQuarter), lane);

        // Repeat a hit across the bar at a fixed step (in quarters), from an offset (in quarters).
        static void Layer(SequencerSlice[] g, int beats, int lane, double stepQuarters, double offsetQuarters = 0)
        {
            if (stepQuarters <= 0) return;
            for (double t = offsetQuarters; t < beats - 1e-9; t += stepQuarters) At(g, t, lane);
        }

        static double HatStep(int density)
        {
            switch (density)
            {
                case 1: return 1.0;   // Léger : noires
                case 3: return 0.25;  // Dense : doubles-croches
                default: return 0.5;  // Auto / Normal : croches
            }
        }

        static void RenderStyle(SequencerSlice[] g, int beats, int style, int density, int bar)
        {
            switch (style)
            {
                case 0: // Rock basique
                    Layer(g, beats, CLOSED, HatStep(density));
                    for (int b = 0; b < beats; b++) At(g, b, (b % 2 == 0) ? KICK : SNARE);
                    break;

                case 1: // Rock appuyé
                    Layer(g, beats, CLOSED, HatStep(density));
                    At(g, 0, KICK);
                    if (beats >= 3) At(g, 2.5, KICK);
                    for (int b = 1; b < beats; b += 2) At(g, b, SNARE);
                    if (bar == 0) At(g, 0, CRASH);
                    break;

                case 2: // Pop
                    Layer(g, beats, CLOSED, HatStep(density));
                    At(g, 0, KICK);
                    if (beats >= 3) { At(g, 2, KICK); At(g, 2.5, KICK); }
                    for (int b = 1; b < beats; b += 2) At(g, b, SNARE);
                    break;

                case 3: // Funk (16th)
                    Layer(g, beats, CLOSED, density == 1 ? 0.5 : 0.25);
                    At(g, 0, KICK);
                    At(g, 0.75, KICK);
                    if (beats >= 3) At(g, 2.5, KICK);
                    for (int b = 1; b < beats; b += 2) At(g, b, SNARE);
                    if (density == 3 && beats >= 3) At(g, 2.25, SNARE);
                    break;

                case 4: // Disco — four on the floor
                    for (int b = 0; b < beats; b++) At(g, b, KICK);
                    Layer(g, beats, OPEN, 1.0, 0.5);
                    Layer(g, beats, CLOSED, 1.0);
                    for (int b = 1; b < beats; b += 2) { At(g, b, SNARE); At(g, b, CLAP); }
                    break;

                case 5: // Jazz swing — ride + foot hat on 2 & 4
                    for (int b = 0; b < beats; b++)
                    {
                        At(g, b, RIDE);
                        if (b % 2 == 1) { At(g, b + 2.0 / 3.0, RIDE); At(g, b, PEDAL); }
                    }
                    At(g, 0, KICK);
                    break;

                case 6: // Shuffle / Blues — triplet hat (long-short)
                    for (int b = 0; b < beats; b++)
                    {
                        At(g, b, CLOSED);
                        At(g, b + 2.0 / 3.0, CLOSED);
                        At(g, b, (b % 2 == 0) ? KICK : SNARE);
                    }
                    break;

                case 7: // Bossa nova — side-stick + soft kick
                    Layer(g, beats, CLOSED, 0.5);
                    foreach (var p in new[] { 0.0, 1.5, 2.0, 3.5 }) if (p < beats) At(g, p, RIM);
                    At(g, 0, KICK);
                    if (beats >= 3) At(g, 2.5, KICK);
                    break;

                case 8: // Half-time — snare on beat 3 only
                    Layer(g, beats, CLOSED, HatStep(density));
                    At(g, 0, KICK);
                    At(g, beats >= 3 ? 2 : beats / 2.0, SNARE);
                    break;

                case 9: // Hip-hop / boom-bap
                    Layer(g, beats, CLOSED, HatStep(density));
                    At(g, 0, KICK);
                    if (beats >= 2) At(g, 1.5, KICK);
                    for (int b = 1; b < beats; b += 2) At(g, b, SNARE);
                    break;

                case 10: // Marche — kick on strong beats, steady snare eighths
                    for (int e = 0; e < beats * 2; e++) At(g, e * 0.5, SNARE);
                    for (int b = 0; b < beats; b += 2) At(g, b, KICK);
                    if (bar == 0) At(g, 0, CRASH);
                    break;

                case 11: // Reggae one-drop — kick + snare together on beat 3 (the "drop"), hat on the ands
                    Layer(g, beats, CLOSED, 1.0, 0.5);
                    { double drop = beats >= 3 ? 2 : beats / 2.0; At(g, drop, KICK); At(g, drop, SNARE); }
                    break;

                case 12: // Valse (3 temps) — kick on 1, side-stick on 2 & 3, hat on each beat
                    At(g, 0, KICK);
                    for (int b = 1; b < beats; b++) At(g, b, RIM);
                    Layer(g, beats, CLOSED, 1.0);
                    break;

                case 13: // Punk (rapide) — kick on every eighth, driving hat, backbeat snare
                    Layer(g, beats, CLOSED, 0.5);
                    for (int e = 0; e < beats * 2; e++) At(g, e * 0.5, KICK);
                    for (int b = 1; b < beats; b += 2) At(g, b, SNARE);
                    if (bar == 0) At(g, 0, CRASH);
                    break;

                case 14: // Ballade (cross-stick) — soft ride, kick on 1 (& 3), rim on the backbeats
                    Layer(g, beats, RIDE, 1.0);
                    At(g, 0, KICK);
                    if (beats >= 3) At(g, 2, KICK);
                    for (int b = 1; b < beats; b += 2) At(g, b, RIM);
                    break;

                case 15: // Trap — 16th hats with a couple of rolls, sparse syncopated kick, snare on 3
                    Layer(g, beats, CLOSED, 0.25);
                    At(g, 0, KICK);
                    if (beats >= 2) At(g, 1.5, KICK);
                    if (beats >= 3) At(g, 2.75, KICK);
                    At(g, beats >= 3 ? 2 : beats / 2.0, SNARE);
                    break;

                default:
                    Layer(g, beats, CLOSED, 0.5);
                    for (int b = 0; b < beats; b++) At(g, b, (b % 2 == 0) ? KICK : SNARE);
                    break;
            }
        }

        // A descending tom/snare fill across the bar, ending with a crash + kick on the downbeat.
        static void RenderFill(SequencerSlice[] g, int beats)
        {
            int eighths = beats * 2;
            int[] ladder = { SNARE, SNARE, SNARE, HITOM, HITOM, MIDTOM, MIDTOM, LOWTOM };
            for (int e = 0; e < eighths; e++)
            {
                int lane = ladder[Math.Min(ladder.Length - 1, e * ladder.Length / Math.Max(1, eighths))];
                At(g, e * 0.5, lane);
            }
            At(g, 0, CRASH);
            At(g, 0, KICK);
        }

        static string Get(string[] arr, int i) => (arr != null && i >= 0 && i < arr.Length) ? arr[i] : "?";
        static int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);
    }
}
