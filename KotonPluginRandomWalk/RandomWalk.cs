using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Windows.Controls;
using System.Windows.Media;
using KotonStudio.Library;

namespace KotonPluginRandomWalk
{
    /// <summary>
    /// Random Walk melodique — brownian motion sur une gamme. A chaque tick, ajoute ±step au
    /// dernier index de gamme, clamp a la range. Genere des lignes melodiques cohesives (petits
    /// mouvements) mais imprevisibles, tres musicales pour un pad, une nappe, un lead ambiant.
    /// </summary>
    [KotonGenerator("Random Walk", Id = "koton.randomwalk", Type = KotonGeneratorType.Melody, Version = "1.0", Vendor = "Koton Studio")]
    public sealed class RandomWalk : IKotonGenerator
    {
        public string Id => "koton.randomwalk";
        public string DisplayName => "Random Walk";

        readonly KotonParameter _notesPerBeat = new KotonParameter("notes_per_beat","Notes/temps",  1, 8, 2);
        readonly KotonParameter _stepMax      = new KotonParameter("step_max",      "Step max",     1, 7, 2);   // degres de gamme
        readonly KotonParameter _scale        = new KotonParameter("scale",         "Scale",        0, 4, 2);   // 0 chromatic 1 major 2 minor 3 pentaMaj 4 pentaMin
        readonly KotonParameter _octRange     = new KotonParameter("oct_range",     "Range (oct)",  1, 4, 2);
        readonly KotonParameter _baseOctave   = new KotonParameter("base_octave",   "Base octave",  0, 8, 4);
        readonly KotonParameter _seed         = new KotonParameter("seed",          "Seed",         0, 999, 42);
        readonly KotonParameter _velocity     = new KotonParameter("velocity",      "Velocity",     1, 127, 100);
        readonly KotonParameter _articulation = new KotonParameter("articulation",  "Articulation", 0, 3, 1);
        readonly KotonParameter _chordAware   = new KotonParameter("chord_aware",   "Chord-aware",  0, 1, 1);   // 0=gamme fixe, 1=notes de l'accord courant

        readonly List<KotonParameter> _params;
        public IReadOnlyList<KotonParameter> Parameters => _params;
        public RandomWalk() { _params = new List<KotonParameter> { _notesPerBeat, _stepMax, _scale, _octRange, _baseOctave, _seed, _velocity, _articulation, _chordAware }; }

        public KotonGeneratorType GeneratorType => KotonGeneratorType.Melody;
        double _durationBeats = 4.0;
        public double DurationBeats { get => _durationBeats; set => _durationBeats = value < 0.25 ? 0.25 : value; }

        public bool HasEditor => true;
        public UserControl CreateEditor() => new RandomWalkEditor(this);
        public KotonGeneratorDisplay GetTimelineDisplay() => new KotonGeneratorDisplay { Background = Color.FromRgb(0x38, 0x7A, 0x8C), Text = "RW " + (int)_notesPerBeat.Value + "/tps" };

        static readonly int[][] ScaleDegrees = {
            new[] { 0,1,2,3,4,5,6,7,8,9,10,11 },   // chromatic
            new[] { 0,2,4,5,7,9,11 },                // major
            new[] { 0,2,3,5,7,8,10 },                // minor
            new[] { 0,2,4,7,9 },                     // penta maj
            new[] { 0,3,5,7,10 },                    // penta min
        };
        static readonly string[] ScaleNames = { "Chromatique", "Majeur", "Mineur", "Penta majeur", "Penta mineur" };
        public static string[] GetScaleNames() => ScaleNames;
        static double GateFor(int art) { switch (art) { case 0: return 1.0; case 2: return 0.4; case 3: return 0.15; default: return 0.75; } }

        public IEnumerable<KotonGeneratedNote> RenderNotes(double startBeat, double endBeat, KotonRenderContext ctx)
        {
            int notesPerBeat = Math.Max(1, (int)_notesPerBeat.Value);
            int stepMax = Math.Max(1, (int)_stepMax.Value);
            int scaleIdx = Math.Max(0, Math.Min(4, (int)_scale.Value));
            int octRange = Math.Max(1, (int)_octRange.Value);
            int baseOct = Math.Max(0, Math.Min(8, (int)_baseOctave.Value));
            int seed = (int)_seed.Value;
            int velocity = Math.Max(1, Math.Min(127, (int)_velocity.Value));
            int art = Math.Max(0, Math.Min(3, (int)_articulation.Value));
            bool chordAware = _chordAware.Value >= 0.5;
            double gate = GateFor(art);
            int tsNum = ctx?.TimeSigNum > 0 ? ctx.TimeSigNum : 4;
            int tsDen = ctx?.TimeSigDen > 0 ? ctx.TimeSigDen : 4;
            bool ternary = tsDen == 8 && tsNum > 0 && tsNum % 3 == 0;
            double tick = (ternary ? 1.5 : 1.0) / notesPerBeat;
            if (tick <= 0) yield break;
            double duration = Math.Max(0.25, DurationBeats);
            double blockStart = ctx?.BlockStartBeat ?? 0.0;

            var scaleDegs = ScaleDegrees[scaleIdx];
            int baseMidi = 12 + baseOct * 12;
            var rnd = new Random(seed);
            int idx = 0;   // sera recalcule dynamiquement selon le pool

            // Pool initial (fallback : gamme)
            int[] pool = BuildScalePool(scaleDegs, octRange, baseMidi);
            idx = pool.Length / 2;

            for (double t = 0; t < duration - 1e-9; t += tick)
            {
                // Chord-aware : recuperer l'accord courant, construire le pool a partir de ses notes
                if (chordAware && KotonHost.GetChordAt != null)
                {
                    var ch = KotonHost.GetChordAt(blockStart + t);
                    if (ch.HasValue)
                    {
                        // Notes de l'accord (root position) → repliquer sur octRange octaves
                        int rootBase = baseMidi + ch.Value.Root;
                        var chordNotes = ch.Value.GetMidiNotes(rootBase);
                        pool = BuildOctaveReplicated(chordNotes, octRange);
                        if (idx >= pool.Length) idx = pool.Length - 1;
                    }
                    // Sinon on garde le pool existant (gamme fallback)
                }

                int step = rnd.Next(-stepMax, stepMax + 1);
                idx += step;
                if (idx < 0) idx = 0;
                if (idx >= pool.Length) idx = pool.Length - 1;
                double len = tick * gate;
                yield return new KotonGeneratedNote { StartBeat = t, DurationBeats = len, MidiNote = pool[idx], Velocity = velocity };
            }
        }

        static int[] BuildScalePool(int[] degrees, int octRange, int baseMidi)
        {
            var pool = new int[degrees.Length * octRange];
            int k = 0;
            for (int oct = 0; oct < octRange; oct++)
                for (int d = 0; d < degrees.Length; d++)
                    pool[k++] = baseMidi + oct * 12 + degrees[d];
            return pool;
        }
        static int[] BuildOctaveReplicated(int[] chordNotes, int octRange)
        {
            if (chordNotes == null || chordNotes.Length == 0) return new int[] { 60 };
            var pool = new int[chordNotes.Length * octRange];
            int k = 0;
            for (int oct = 0; oct < octRange; oct++)
                for (int i = 0; i < chordNotes.Length; i++)
                    pool[k++] = chordNotes[i] + oct * 12;
            return pool;
        }

        public byte[] SaveState()
        {
            try { var d = new Dictionary<string, object>(); foreach (var kp in _params) d[kp.Id] = kp.Value; d["_dur"] = _durationBeats; return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(d)); } catch { return Array.Empty<byte>(); }
        }
        public void LoadState(byte[] state)
        {
            if (state == null || state.Length == 0) return;
            try
            {
                using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(state));
                var r = doc.RootElement;
                foreach (var kp in _params) if (r.TryGetProperty(kp.Id, out var v) && v.ValueKind == JsonValueKind.Number) kp.Value = v.GetDouble();
                if (r.TryGetProperty("_dur", out var d) && d.ValueKind == JsonValueKind.Number) _durationBeats = d.GetDouble();
            }
            catch { }
        }
        public void Dispose() { }
        public void SetParam(string id, double value) { foreach (var kp in _params) if (kp.Id == id) { kp.Value = value; return; } }
    }
}
