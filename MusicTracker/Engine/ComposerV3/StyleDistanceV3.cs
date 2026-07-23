using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using MusicTracker.Engine.ComposerV2;

namespace MusicTracker.Engine.ComposerV3
{
    /// <summary>
    /// STYLE DISTANCE — an objective measure of how closely GENERATED melodies match the trained corpus's
    /// statistics. Generates K pieces (ComposeInMemory), extracts the melody, and compares three histograms to
    /// the corpus distributions encoded in the model, via HISTOGRAM INTERSECTION (overlap %: 100 = identical):
    ///   • degree distribution        (vs mode.DegreeHistogram)
    ///   • degree-bigram distribution (vs mode.Melody "d1" transition tier)
    ///   • per-beat rhythm-cell distr. (vs model.RhythmCell marginal) — the target of the rhythm-chain change.
    /// Each generated piece is compared to the corpus of its OWN mode (major/minor), mixed by prevalence.
    /// Run via reflection from style_distance.ps1; lets generator changes be judged by a number, not the ear.
    /// </summary>
    public static class StyleDistanceV3
    {
        static int Mod12(int x) { int r = x % 12; return r < 0 ? r + 12 : r; }
        static void Bump(Dictionary<string, double> d, string k, double w) { double c; d.TryGetValue(k, out c); d[k] = c + w; }

        sealed class Acc
        {
            public double[] GenDeg = new double[12], CorpDeg = new double[12];
            public double[] GenBg = new double[144], CorpBg = new double[144];
            public Dictionary<string, double> GenCells = new Dictionary<string, double>();
            public int Pieces, MelNotes;
        }

        public static string Report(string modelFileName, string modelFullPath, int seeds, bool compareMotif)
        {
            var model = ComposerV2Runtime.ReadFromPath(modelFullPath);
            var corpusCells = CorpusCells(model);
            if (seeds < 1) seeds = 8;

            var chain = Run(model, modelFileName, seeds, false);
            var motif = compareMotif ? Run(model, modelFileName, seeds, true) : null;

            var sb = new StringBuilder();
            sb.AppendLine("=== Style distance: " + modelFileName + ", " + seeds + " seeds ===");
            sb.AppendLine("(overlap % = histogram intersection with the corpus; 100 = identical, higher = closer to style)");
            sb.AppendLine();
            if (compareMotif)
            {
                sb.AppendLine(string.Format("{0,-16} {1,8} {2,8}", "feature", "chain", "motif"));
                sb.AppendLine(new string('-', 36));
                sb.AppendLine(Row("degree", Overlap(chain.GenDeg, chain.CorpDeg), Overlap(motif.GenDeg, motif.CorpDeg)));
                sb.AppendLine(Row("degree bigram", Overlap(chain.GenBg, chain.CorpBg), Overlap(motif.GenBg, motif.CorpBg)));
                sb.AppendLine(Row("rhythm cells", Overlap(chain.GenCells, corpusCells), Overlap(motif.GenCells, corpusCells)) + "   <-- #1");
            }
            else
            {
                sb.AppendLine(string.Format("{0,-16} {1,8}", "feature", "overlap"));
                sb.AppendLine(new string('-', 27));
                sb.AppendLine(string.Format("{0,-16} {1,7:F1}%", "degree", 100 * Overlap(chain.GenDeg, chain.CorpDeg)));
                sb.AppendLine(string.Format("{0,-16} {1,7:F1}%", "degree bigram", 100 * Overlap(chain.GenBg, chain.CorpBg)));
                sb.AppendLine(string.Format("{0,-16} {1,7:F1}%", "rhythm cells", 100 * Overlap(chain.GenCells, corpusCells)));
            }
            sb.AppendLine();
            sb.AppendLine("top corpus cells : " + TopCells(corpusCells, 8));
            sb.AppendLine("top chain  cells : " + TopCells(chain.GenCells, 8));
            if (compareMotif) sb.AppendLine("top motif  cells : " + TopCells(motif.GenCells, 8));
            sb.AppendLine();
            sb.AppendLine(string.Format("(generated {0} melody notes over {1} pieces; chain rhythm)", chain.MelNotes, chain.Pieces));
            return sb.ToString();
        }

        static string Row(string name, double a, double b) { return string.Format("{0,-16} {1,7:F1}% {2,7:F1}%", name, 100 * a, 100 * b); }

        static Acc Run(CorpusModelV2 model, string modelFileName, int seeds, bool motif)
        {
            var acc = new Acc();
            for (int seed = 1; seed <= seeds; seed++)
            {
                V2Piece piece;
                try
                {
                    var composer = ComposerV3Factory.For(modelFileName);
                    composer.UseMotifRhythm = motif;
                    piece = composer.ComposeInMemory(model, seed);
                }
                catch { continue; }
                var mel = MelodyPart(piece);
                if (mel.Count == 0) continue;
                acc.Pieces++; acc.MelNotes += mel.Count;

                int tonicPc = TonicPc(piece.TonicLetter, piece.TonicAccidental);
                var mm = piece.Minor ? model.Minor : model.Major;
                var cDeg = Normalize(CorpusDeg(mm));
                var cBg = Normalize(CorpusBigram(mm));

                int prev = -1; double pieceW = 0; int bgCount = 0;
                foreach (var n in mel)
                {
                    int deg = Mod12((n[0] % 12) - tonicPc);
                    double w = Math.Max(1, n[2]);
                    acc.GenDeg[deg] += w; pieceW += w;
                    if (prev >= 0) { acc.GenBg[prev * 12 + deg] += 1; bgCount++; }
                    prev = deg;
                }
                // mix the corpus reference for THIS piece by its mode + size, so modes are weighted as generated
                for (int i = 0; i < 12; i++) acc.CorpDeg[i] += cDeg[i] * pieceW;
                for (int i = 0; i < 144; i++) acc.CorpBg[i] += cBg[i] * bgCount;
                AccumCells(acc.GenCells, mel, MusicMathV2.BeatSlices(piece.MeterDen));
            }
            return acc;
        }

        // melody part = the non-drum part with the highest duration-weighted mean pitch; returned as its skyline
        // (top note per onset), each note = [pitch, start, len], ordered by onset.
        static List<int[]> MelodyPart(V2Piece piece)
        {
            // The melody is the highest-register DENSE part. Gate out sparse decorations (e.g. a 6-note glockenspiel
            // sitting above everything) — otherwise "highest mean pitch" picks the ornament, not the tune.
            int maxNotes = 0;
            foreach (var p in piece.Parts) if (!p.IsDrum && p.Notes != null && p.Notes.Count > maxNotes) maxNotes = p.Notes.Count;
            int gate = Math.Max(16, maxNotes / 5);
            V2Part best = null; double bestMean = double.MinValue;
            foreach (var p in piece.Parts)
            {
                if (p.IsDrum || p.Notes == null || p.Notes.Count < gate) continue;
                double sw = 0, s = 0; foreach (var n in p.Notes) { int l = Math.Max(1, n.Len); sw += l; s += (double)n.Pitch * l; }
                double mean = sw > 0 ? s / sw : 0;
                if (mean > bestMean) { bestMean = mean; best = p; }
            }
            var res = new List<int[]>();
            if (best == null) return res;
            var byStart = new Dictionary<int, int[]>();
            foreach (var n in best.Notes) { int[] cur; if (!byStart.TryGetValue(n.Start, out cur) || n.Pitch > cur[0]) byStart[n.Start] = new[] { n.Pitch, n.Start, Math.Max(1, n.Len) }; }
            res.AddRange(byStart.Values);
            res.Sort((a, b) => a[1].CompareTo(b[1]));
            return res;
        }

        // per-beat rhythm cells — identical tokenization to CorpusAnalyzerV2.AccumRhythmCells (rest tokens on gaps
        // ≥ an eighth; "-" for a beat with no onset), so the cells compare to the trained model.RhythmCell.
        static void AccumCells(Dictionary<string, double> cells, List<int[]> mel, int beatSlices)
        {
            if (mel.Count == 0 || beatSlices <= 0) return;
            var ev = new List<int[]>();
            for (int i = 0; i < mel.Count; i++)
            {
                ev.Add(new[] { mel[i][1], Math.Max(1, mel[i][2]), 0 });
                if (i + 1 < mel.Count) { int gap = mel[i + 1][1] - (mel[i][1] + mel[i][2]); if (gap >= 12) ev.Add(new[] { mel[i][1] + mel[i][2], gap, 1 }); }
            }
            int endSlice = mel[mel.Count - 1][1] + mel[mel.Count - 1][2];
            int totalBeats = (endSlice + beatSlices - 1) / beatSlices;
            for (int bt = 0; bt < totalBeats; bt++)
            {
                int bs = bt * beatSlices, be = bs + beatSlices;
                var toks = new List<string>();
                foreach (var e in ev) if (e[0] >= bs && e[0] < be) toks.Add((e[2] == 1 ? "r" : "") + MusicMathV2.DurBucket(e[1]));
                Bump(cells, toks.Count > 0 ? string.Join("+", toks) : "-", 1);
            }
        }

        // ----- corpus references read from the model -----
        static double[] CorpusDeg(ModeModels mm)
        {
            var d = new double[12];
            if (mm != null && mm.DegreeHistogram != null)
                foreach (var kv in mm.DegreeHistogram) { int idx = Array.IndexOf(MusicMathV2.DegName, kv.Key); if (idx >= 0) d[idx] += kv.Value; }
            return d;
        }
        static double[] CorpusBigram(ModeModels mm)
        {
            var b = new double[144];
            var t = FindTier(mm?.Melody, "d1");
            if (t != null)
                foreach (var kv in t.Table)
                {
                    int prev; if (!int.TryParse(kv.Key, out prev) || prev < 0 || prev > 11) continue;
                    foreach (var nx in kv.Value) { int next; if (int.TryParse(nx.Key, out next) && next >= 0 && next < 12) b[prev * 12 + next] += nx.Value; }
                }
            return b;
        }
        static Dictionary<string, double> CorpusCells(CorpusModelV2 model)
        {
            var d = new Dictionary<string, double>();
            var t = FindTier(model.RhythmCell, "");
            Dictionary<string, double> inner;
            if (t != null && t.Table.TryGetValue("", out inner)) foreach (var kv in inner) d[kv.Key] = kv.Value;
            return d;
        }
        static CondTier FindTier(CondModel m, string label)
        {
            if (m == null) return null;
            foreach (var t in m.Tiers) if ((t.Context ?? "") == label) return t;
            return null;
        }

        // ----- helpers -----
        static int TonicPc(int letter, int accidental)
        {
            int[] letterPc = { 0, 2, 4, 5, 7, 9, 11 };
            int l = Math.Max(0, Math.Min(6, letter));
            return Mod12(letterPc[l] + accidental);
        }
        static double[] Normalize(double[] a) { double s = a.Sum(); var r = new double[a.Length]; if (s > 0) for (int i = 0; i < a.Length; i++) r[i] = a[i] / s; return r; }
        static double Overlap(double[] a, double[] b)
        {
            double sa = a.Sum(), sb = b.Sum(); if (sa <= 0 || sb <= 0) return 0;
            double o = 0; for (int i = 0; i < a.Length; i++) o += Math.Min(a[i] / sa, b[i] / sb); return o;
        }
        static double Overlap(Dictionary<string, double> a, Dictionary<string, double> b)
        {
            double sa = a.Values.Sum(), sb = b.Values.Sum(); if (sa <= 0 || sb <= 0) return 0;
            var keys = new HashSet<string>(a.Keys); keys.UnionWith(b.Keys);
            double o = 0; foreach (var k in keys) { double pa = a.ContainsKey(k) ? a[k] / sa : 0, pb = b.ContainsKey(k) ? b[k] / sb : 0; o += Math.Min(pa, pb); }
            return o;
        }
        static string TopCells(Dictionary<string, double> d, int n)
        {
            double s = d.Values.Sum(); if (s <= 0) return "(none)";
            return string.Join("  ", d.OrderByDescending(kv => kv.Value).Take(n).Select(kv => kv.Key + " " + (100 * kv.Value / s).ToString("F0") + "%"));
        }
    }
}
