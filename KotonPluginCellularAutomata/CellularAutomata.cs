using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Windows.Controls;
using System.Windows.Media;
using KotonStudio.Library;

namespace KotonPluginCellularAutomata
{
    /// <summary>
    /// Cellular Automata — sequences deterministes issues d'un automate elementaire (Wolfram
    /// 1D). Une ligne de N cellules 0/1 evolue selon une regle (0..255), chaque cellule ACTIVE
    /// a un tick donne = une note (mapping index-cellule → degre de gamme). Ideal pour des
    /// motifs fractals repetitifs mais evolutifs (Rule 30 chaotic, Rule 90 Sierpinski, Rule 110
    /// Turing-complet).
    /// </summary>
    [KotonGenerator("Cellular Automata", Id = "koton.cellular", Type = KotonGeneratorType.Melody, Version = "1.0", Vendor = "Koton Studio")]
    public sealed class CellularAutomata : IKotonGenerator
    {
        public string Id => "koton.cellular";
        public string DisplayName => "Cellular Automata";

        readonly KotonParameter _notesPerBeat = new KotonParameter("notes_per_beat","Notes/temps", 1, 8, 4);
        readonly KotonParameter _rule         = new KotonParameter("rule",          "Rule (Wolfram)", 0, 255, 90);
        readonly KotonParameter _width        = new KotonParameter("width",         "Width (cells)", 8, 32, 16);
        readonly KotonParameter _scale        = new KotonParameter("scale",         "Scale",       0, 4, 3);
        readonly KotonParameter _baseOctave   = new KotonParameter("base_octave",   "Base octave", 0, 8, 4);
        readonly KotonParameter _octRange     = new KotonParameter("oct_range",     "Range (oct)", 1, 3, 2);
        readonly KotonParameter _seed         = new KotonParameter("seed",          "Init seed",   0, 999, 1);
        readonly KotonParameter _seedMode     = new KotonParameter("seed_mode",     "Init mode",   0, 2, 1);   // 0 = single center, 1 = random density, 2 = all-ones
        readonly KotonParameter _density      = new KotonParameter("density",       "Init density",0.0, 1.0, 0.3);
        readonly KotonParameter _velocity     = new KotonParameter("velocity",      "Velocity",    1, 127, 90);
        readonly KotonParameter _articulation = new KotonParameter("articulation",  "Articulation",0, 3, 2);

        readonly List<KotonParameter> _params;
        public IReadOnlyList<KotonParameter> Parameters => _params;
        public CellularAutomata() { _params = new List<KotonParameter> { _notesPerBeat, _rule, _width, _scale, _baseOctave, _octRange, _seed, _seedMode, _density, _velocity, _articulation }; }

        public KotonGeneratorType GeneratorType => KotonGeneratorType.Melody;
        double _durationBeats = 4.0;
        public double DurationBeats { get => _durationBeats; set => _durationBeats = value < 0.25 ? 0.25 : value; }
        public bool HasEditor => true;
        public UserControl CreateEditor() => new CellularEditor(this);
        public KotonGeneratorDisplay GetTimelineDisplay() => new KotonGeneratorDisplay { Background = Color.FromRgb(0x6B, 0x4C, 0x8F), Text = "CA r" + (int)_rule.Value };

        static readonly int[][] ScaleDegrees = {
            new[] { 0,1,2,3,4,5,6,7,8,9,10,11 }, new[] { 0,2,4,5,7,9,11 }, new[] { 0,2,3,5,7,8,10 },
            new[] { 0,2,4,7,9 }, new[] { 0,3,5,7,10 },
        };
        public static readonly string[] ScaleNames = { "Chromatique", "Majeur", "Mineur", "Penta majeur", "Penta mineur" };
        public static readonly string[] SeedModeNames = { "Cellule centrale", "Densité aléatoire", "Toutes actives" };
        static double GateFor(int a) { switch (a) { case 0: return 1.0; case 2: return 0.4; case 3: return 0.15; default: return 0.75; } }

        static byte[] InitRow(int width, int seed, int mode, double density)
        {
            var row = new byte[width];
            var rng = new Random(seed);
            switch (mode)
            {
                case 0: row[width / 2] = 1; break;
                case 2: for (int i = 0; i < width; i++) row[i] = 1; break;
                default: for (int i = 0; i < width; i++) row[i] = rng.NextDouble() < density ? (byte)1 : (byte)0; break;
            }
            // Sinon tout zero — on force au moins la cellule centrale
            bool any = false; for (int i = 0; i < width; i++) if (row[i] != 0) { any = true; break; }
            if (!any) row[width / 2] = 1;
            return row;
        }
        static byte[] StepRule(byte[] row, int rule)
        {
            int n = row.Length;
            var next = new byte[n];
            for (int i = 0; i < n; i++)
            {
                int left = row[(i - 1 + n) % n];
                int center = row[i];
                int right = row[(i + 1) % n];
                int pattern = (left << 2) | (center << 1) | right;   // 0..7
                next[i] = (byte)((rule >> pattern) & 1);
            }
            return next;
        }

        public IEnumerable<KotonGeneratedNote> RenderNotes(double startBeat, double endBeat, KotonRenderContext ctx)
        {
            int notesPerBeat = Math.Max(1, (int)_notesPerBeat.Value);
            int rule = Math.Max(0, Math.Min(255, (int)_rule.Value));
            int width = Math.Max(8, Math.Min(32, (int)_width.Value));
            int scaleIdx = Math.Max(0, Math.Min(4, (int)_scale.Value));
            int baseOct = Math.Max(0, Math.Min(8, (int)_baseOctave.Value));
            int octRange = Math.Max(1, Math.Min(3, (int)_octRange.Value));
            int seed = (int)_seed.Value;
            int mode = Math.Max(0, Math.Min(2, (int)_seedMode.Value));
            double density = _density.Value;
            int velocity = Math.Max(1, Math.Min(127, (int)_velocity.Value));
            int art = Math.Max(0, Math.Min(3, (int)_articulation.Value));
            double gate = GateFor(art);

            int tsNum = ctx?.TimeSigNum > 0 ? ctx.TimeSigNum : 4;
            int tsDen = ctx?.TimeSigDen > 0 ? ctx.TimeSigDen : 4;
            bool ternary = tsDen == 8 && tsNum > 0 && tsNum % 3 == 0;
            double tick = (ternary ? 1.5 : 1.0) / notesPerBeat;
            if (tick <= 0) yield break;
            double duration = Math.Max(0.25, DurationBeats);

            var degrees = ScaleDegrees[scaleIdx];
            int poolSize = degrees.Length * octRange;
            int baseMidi = 12 + baseOct * 12;

            var row = InitRow(width, seed, mode, density);
            for (double t = 0; t < duration - 1e-9; t += tick)
            {
                // Chaque cellule active = une note simultanee
                for (int i = 0; i < width; i++)
                {
                    if (row[i] == 0) continue;
                    int idx = (i * poolSize) / width;   // mapping lineaire cell -> pool
                    if (idx >= poolSize) idx = poolSize - 1;
                    int oct = idx / degrees.Length;
                    int deg = idx % degrees.Length;
                    int midi = baseMidi + oct * 12 + degrees[deg];
                    yield return new KotonGeneratedNote { StartBeat = t, DurationBeats = tick * gate, MidiNote = midi, Velocity = velocity };
                }
                row = StepRule(row, rule);
            }
        }

        public byte[] SaveState() { try { var d = new Dictionary<string, object>(); foreach (var kp in _params) d[kp.Id] = kp.Value; d["_dur"] = _durationBeats; return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(d)); } catch { return Array.Empty<byte>(); } }
        public void LoadState(byte[] state)
        {
            if (state == null || state.Length == 0) return;
            try { using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(state)); var r = doc.RootElement; foreach (var kp in _params) if (r.TryGetProperty(kp.Id, out var v) && v.ValueKind == JsonValueKind.Number) kp.Value = v.GetDouble(); if (r.TryGetProperty("_dur", out var d) && d.ValueKind == JsonValueKind.Number) _durationBeats = d.GetDouble(); } catch { }
        }
        public void Dispose() { }
        public void SetParam(string id, double value) { foreach (var kp in _params) if (kp.Id == id) { kp.Value = value; return; } }
    }
}
