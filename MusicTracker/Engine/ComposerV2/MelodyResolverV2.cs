using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace MusicTracker.Engine.ComposerV2
{
    /// <summary>
    /// Melody NOTE RESOLVER (Composer V2). Given a chord grid + a melody where some notes are HOLES, fill each
    /// hole with the most probable scale degree using the learned <see cref="CorpusModelV2"/> melody chain. The
    /// chord context (degree BEFORE / DURING / AFTER the note, via the same back-off ladder the analyzer trains —
    /// see <see cref="CorpusAnalyzerV2"/>) is folded into the transition, so there is no separate emission model;
    /// a light chord-fit prior only breaks ties where the corpus is thin.
    ///
    /// A VITERBI pass over the slot sequence makes every hole respect BOTH its left and right neighbours — the
    /// "notes avant ET après" a forward-only sampler cannot use, because reaching the next KNOWN note forces the
    /// path through a hole to pick the degree that best bridges previous → next. State = the degree at a slot;
    /// deeper history for the high-order n-gram tiers is reconstructed from the backpointers.
    ///
    /// Pure analysis utility — no dependency on the runtime composer. The ladder labels are kept byte-identical
    /// to <see cref="CorpusAnalyzerV2"/> / BaseComposerV3 so it queries exactly the buckets that were trained.
    /// </summary>
    public static class MelodyResolverV2
    {
        // ----- inputs / outputs -----
        /// <summary>One melodic position. <see cref="Deg"/> = scale degree relative to tonic (0-11); a HOLE
        /// (<see cref="Known"/> = false) is the note to resolve.</summary>
        public sealed class Slot { public int Bar; public bool Strong; public int Deg; public bool Known; }
        /// <summary>A bar's chord: <see cref="Root"/> degree (0-11, tonic-relative) + the set of chord-tone degrees.
        /// <see cref="Root"/> &lt; 0 (the default) means "no chord this bar".</summary>
        public sealed class Chord { public int Root = -1; public HashSet<int> Tones; }
        public sealed class Options
        {
            public string Section = "body";       // section role used by the top ladder tier (default when unknown)
            public double EmissionWeight = 0.5;   // weight of the chord-fit prior vs the learned model (0 = model only)
            public int TopK = 4;                  // ranked candidates reported per hole
            public int[] Candidates;              // degrees to consider per hole; null => all 12 (or the scale if RestrictToScale)
            public bool UseChordDegreeTiers = true;  // include the exact pdeg|cdeg|ndeg ladder tiers (ablation: false)
            public bool UseIntervalViewpoints = true;// include the interval + contour tiers (item 2; ablation: false)
            public bool RestrictToScale = false;     // limit candidates to the diatonic scale degrees
        }
        public sealed class HoleResult { public int SlotIndex; public int Bar; public int Deg; public List<KeyValuePair<int, double>> Ranked = new List<KeyValuePair<int, double>>(); }
        public sealed class Result { public int[] Degrees; public List<HoleResult> Holes = new List<HoleResult>(); }
        public sealed class EvalReport
        {
            public string File; public bool Minor; public int Tonic;
            public int Holes, Top1, Top3; public double MeanCircErr; public string Summary;
        }

        const double Eps = 1e-9;
        static int Mod12(int x) { int r = x % 12; return r < 0 ? r + 12 : r; }

        // ----- chord-degree neighbourhood over the per-bar grid (the "accord avant / après") -----
        static int PrevDistinct(Chord[] g, int bar)
        {
            int cur = (bar >= 0 && bar < g.Length) ? g[bar].Root : -1;
            for (int b = Math.Min(bar, g.Length - 1) - 1; b >= 0; b--) if (g[b].Root >= 0 && g[b].Root != cur) return g[b].Root;
            return -1;
        }
        static int NextDistinct(Chord[] g, int bar)
        {
            int cur = (bar >= 0 && bar < g.Length) ? g[bar].Root : -1;
            for (int b = Math.Max(0, bar) + 1; b < g.Length; b++) if (g[b].Root >= 0 && g[b].Root != cur) return g[b].Root;
            return -1;
        }

        // =====================================================================
        public static Result Resolve(CorpusModelV2 model, bool minor, Chord[] chordByBar, IList<Slot> slots, Options opts)
        {
            opts = opts ?? new Options();
            var mm = minor ? model.Minor : model.Major;
            int order = MusicMathV2.Order(model.Orders, "melody", 8);
            int n = slots.Count;
            var res = new Result { Degrees = new int[n] };
            if (n == 0) return res;
            var scale = new HashSet<int>(minor ? MusicMathV2.MinorScale : MusicMathV2.MajorScale);
            int[] cands = opts.Candidates
                ?? (opts.RestrictToScale ? (minor ? MusicMathV2.MinorScale : MusicMathV2.MajorScale).ToArray()
                                         : new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11 });
            Func<int, int[]> candAt = i => slots[i].Known ? new[] { slots[i].Deg } : cands;

            // Viterbi: score[i][deg] = best log-prob of a path ending with 'deg' at slot i; bp = backpointer.
            var score = new Dictionary<int, double>[n];
            var bp = new Dictionary<int, int>[n];
            for (int i = 0; i < n; i++) { score[i] = new Dictionary<int, double>(); bp[i] = new Dictionary<int, int>(); }

            // history (most-recent-first) ending at (slot, deg), walked back through the backpointers up to 'order'
            Func<int, int, string[]> histAt = (slot, deg) =>
            {
                var h = new List<string>(); int ci = slot, cd = deg;
                while (h.Count < order && ci >= 0)
                {
                    h.Add(cd.ToString());
                    if (ci <= 0 || !bp[ci].ContainsKey(cd)) break;
                    int pd = bp[ci][cd]; ci--; cd = pd;
                }
                while (h.Count < order) h.Add("^");
                return h.ToArray();
            };

            for (int i = 0; i < n; i++)
            {
                foreach (int d in candAt(i))
                {
                    double best = double.NegativeInfinity; int bestP = -1;
                    if (i == 0)
                        best = StepLogProb(mm, order, opts, chordByBar, slots, i, EmptyHist(order), d, scale);
                    else
                        foreach (int p in candAt(i - 1))
                        {
                            if (!score[i - 1].ContainsKey(p)) continue;
                            double s = score[i - 1][p] + StepLogProb(mm, order, opts, chordByBar, slots, i, histAt(i - 1, p), d, scale);
                            if (s > best) { best = s; bestP = p; }
                        }
                    if (double.IsNegativeInfinity(best)) continue;
                    score[i][d] = best; bp[i][d] = bestP;
                }
            }

            // backtrack the best full path
            int last = -1; double lb = double.NegativeInfinity;
            foreach (var kv in score[n - 1]) if (kv.Value > lb) { lb = kv.Value; last = kv.Key; }
            if (last < 0) last = slots[n - 1].Known ? slots[n - 1].Deg : 0;
            for (int i = n - 1; i >= 0; i--)
            {
                res.Degrees[i] = last;
                last = (i > 0 && bp[i].ContainsKey(res.Degrees[i])) ? bp[i][res.Degrees[i]]
                     : (i > 0 && slots[i - 1].Known ? slots[i - 1].Deg : 0);
            }

            // per-hole ranked candidates given the RESOLVED neighbours (= the before AND after pull, made explicit)
            for (int i = 0; i < n; i++)
            {
                if (slots[i].Known) continue;
                var hr = new HoleResult { SlotIndex = i, Bar = slots[i].Bar, Deg = res.Degrees[i] };
                var histPrev = ShiftHist(res.Degrees, i, order);
                int nextDeg = (i + 1 < n) ? res.Degrees[i + 1] : -1;
                var ranked = new List<KeyValuePair<int, double>>();
                foreach (int d in cands)
                {
                    double lp = StepLogProb(mm, order, opts, chordByBar, slots, i, histPrev, d, scale);
                    if (nextDeg >= 0)   // bridge to the next note: how well does choosing d let us reach it?
                        lp += StepLogProb(mm, order, opts, chordByBar, slots, i + 1, PushHist(histPrev, d, order), nextDeg, scale);
                    ranked.Add(new KeyValuePair<int, double>(d, lp));
                }
                ranked.Sort((a, b) => b.Value.CompareTo(a.Value));
                int k = Math.Min(Math.Max(1, opts.TopK), ranked.Count);
                double max = ranked[0].Value, z = 0; for (int t = 0; t < k; t++) z += Math.Exp(ranked[t].Value - max);
                for (int t = 0; t < k; t++) hr.Ranked.Add(new KeyValuePair<int, double>(ranked[t].Key, Math.Exp(ranked[t].Value - max) / z));
                res.Holes.Add(hr);
            }
            return res;
        }

        static string[] EmptyHist(int order) { var h = new string[Math.Max(1, order)]; for (int i = 0; i < h.Length; i++) h[i] = "^"; return h; }
        static string[] ShiftHist(int[] degs, int i, int order)
        {
            var h = new string[Math.Max(1, order)];
            for (int j = 0; j < h.Length; j++) { int idx = i - 1 - j; h[j] = idx >= 0 ? degs[idx].ToString() : "^"; }
            return h;
        }
        static string[] PushHist(string[] hist, int d, int order)
        {
            var h = new string[Math.Max(1, order)]; h[0] = d.ToString();
            for (int j = 1; j < h.Length; j++) h[j] = j - 1 < hist.Length ? hist[j - 1] : "^";
            return h;
        }

        // one melody step: log( P(d | hist, chord-context @ slot i) ) + emWeight * log( chord-fit(d) ).
        // The context mirrors the analyzer TRAINING convention exactly: func/metric/role describe the PREVIOUS
        // note (d1); nfunc + the exact-degree pdeg|cdeg|ndeg describe the note being resolved (X = slot i).
        static double StepLogProb(ModeModels mm, int order, Options opts, Chord[] g, IList<Slot> slots, int i, string[] hist, int d, HashSet<int> scale)
        {
            int xbar = slots[i].Bar;
            Chord xc = (xbar >= 0 && xbar < g.Length) ? g[xbar] : null;
            int cRoot = xc != null ? xc.Root : -1;
            int pRoot = PrevDistinct(g, xbar), nRoot = NextDistinct(g, xbar);
            string cdeg = cRoot < 0 ? "^" : cRoot.ToString();
            string pdeg = pRoot < 0 ? "^" : pRoot.ToString();
            string ndeg = nRoot < 0 ? "^" : nRoot.ToString();
            string nfunc = MusicMathV2.ChordFunction(cRoot < 0 ? 0 : cRoot);   // chord function DURING X

            // d1 = the previous degree; its bar/chord drive func/metric/role (the "before X" side)
            int d1 = (hist.Length > 0 && hist[0] != "^") ? int.Parse(hist[0]) : -1;
            string d1s = hist.Length > 0 ? hist[0] : "^";
            string d2 = hist.Length > 1 ? hist[1] : "^";
            int pbar = (i > 0) ? slots[i - 1].Bar : xbar;
            Chord pc = (pbar >= 0 && pbar < g.Length) ? g[pbar] : null;
            string func = MusicMathV2.ChordFunction(pc != null && pc.Root >= 0 ? pc.Root : 0);
            string role = RoleOf(d1, pc);
            string metric = (i > 0 ? slots[i - 1].Strong : slots[i].Strong) ? "S" : "w";
            string sec = opts.Section ?? "body";

            var labels = new List<string> { "sec|d1|metric|role" };
            var ctx = new List<string> { sec + "|" + d1s + "|" + metric + "|" + role };
            if (opts.UseChordDegreeTiers)   // the exact chord-degree neighbourhood tiers (ablation toggle)
            {
                labels.Add("d1|pdeg|cdeg|ndeg|role"); ctx.Add(d1s + "|" + pdeg + "|" + cdeg + "|" + ndeg + "|" + role);
                labels.Add("d1|cdeg|role"); ctx.Add(d1s + "|" + cdeg + "|" + role);
            }
            // Item 2 VIEWPOINTS: interval + contour that led INTO d1 (byte-identical to analyzer/generator; degree-space).
            if (opts.UseIntervalViewpoints)   // interval + contour tiers (ablation toggle)
            {
                string iv1 = "^", cont1 = "^";
                if (d1s != "^" && d2 != "^" && int.TryParse(d1s, out int d1v) && int.TryParse(d2, out int d2v))
                { int iv = MusicMathV2.SignedIv(d2v, d1v); iv1 = iv.ToString(); cont1 = MusicMathV2.Contour(iv); }
                labels.Add("d1|iv1|role"); ctx.Add(d1s + "|" + iv1 + "|" + role);
                labels.Add("d1|cont1"); ctx.Add(d1s + "|" + cont1);
            }
            labels.AddRange(new[] { "d1|metric|role|nfunc", "d2|d1|metric|role", "d1|func|role|nfunc", "d1|metric|role", "d1|role", "d1", "" });
            ctx.AddRange(new[] { d1s + "|" + metric + "|" + role + "|" + nfunc, d2 + "|" + d1s + "|" + metric + "|" + role, d1s + "|" + func + "|" + role + "|" + nfunc, d1s + "|" + metric + "|" + role, d1s + "|" + role, d1s, "" });
            var lad = MusicMathV2.BuildLadder("d", hist, order, 3, null, null, labels.ToArray(), ctx.ToArray());
            var dist = Dist(mm.Melody, lad.Labels, lad.Ctx);
            double pt = 0; if (dist != null) dist.TryGetValue(d.ToString(), out pt);

            double em = ChordFit(d, xc, scale);
            return Math.Log(pt + Eps) + opts.EmissionWeight * Math.Log(em + Eps);
        }

        static string RoleOf(int deg, Chord ch)
        {
            if (deg < 0 || ch == null || ch.Root < 0 || ch.Tones == null) return "nct";
            if (ch.Tones.Contains(deg)) return "ct";
            int rel = Mod12(deg - ch.Root);
            return rel == 2 ? "t9" : rel == 5 ? "t11" : rel == 9 ? "t13" : "nct";
        }
        // chord-fit prior: chord tone >> diatonic >> chromatic; neutral when no chord is known
        static double ChordFit(int deg, Chord ch, HashSet<int> scale)
        {
            if (ch == null || ch.Root < 0 || ch.Tones == null) return 1.0;
            if (ch.Tones.Contains(deg)) return 1.0;
            return scale.Contains(deg) ? 0.4 : 0.1;
        }

        // label-matched Witten-Bell back-off — identical semantics to BaseComposerV3.Dist (match each model tier
        // to the runtime context by the tier's stored Context LABEL, blend specific→general).
        static Dictionary<string, double> Dist(CondModel m, string[] labels, string[] ctx)
        {
            if (m == null || m.Tiers.Count == 0) return null;
            Dictionary<string, double> dist = null;
            for (int i = m.Tiers.Count - 1; i >= 0; i--)
            {
                int li = LabelIndex(labels, m.Tiers[i].Context);
                if (li < 0 || li >= ctx.Length) continue;
                Dictionary<string, double> counts;
                if (!m.Tiers[i].Table.TryGetValue(ctx[li], out counts) || counts.Count == 0) continue;
                double nn = 0; foreach (var v in counts.Values) nn += v;
                if (nn <= 0) continue;
                int dd = counts.Count;
                double lambda = nn / (nn + dd);
                var here = new Dictionary<string, double>();
                foreach (var kv in counts) here[kv.Key] = kv.Value / nn;
                if (dist == null) { dist = here; continue; }
                var blend = new Dictionary<string, double>();
                foreach (var kv in here) blend[kv.Key] = lambda * kv.Value;
                foreach (var kv in dist) { double cur; blend.TryGetValue(kv.Key, out cur); blend[kv.Key] = cur + (1 - lambda) * kv.Value; }
                dist = blend;
            }
            return dist;
        }
        static int LabelIndex(string[] labels, string label)
        {
            string want = label ?? "";
            for (int i = 0; i < labels.Length; i++) if (labels[i] == want) return i;
            return -1;
        }

        // =====================================================================
        // EVALUATION: hold out every Nth interior melody note of a real file and measure how well the resolver
        // recovers it from the chords + neighbours. The single most convincing "does it work" test.
        // =====================================================================
        public static EvalReport EvaluateFile(CorpusModelV2 model, string path, int holeEveryN, Options opts)
        {
            opts = opts ?? new Options();
            string ext = Path.GetExtension(path).ToLowerInvariant();
            var score = (ext == ".mscz" || ext == ".mscx") ? MuseScoreImporter.Load(path) : MidiImporter.Load(path);

            var tracks = new List<List<int[]>>();   // each note = [pitch, start, len]
            foreach (var t in score.Tracks)
            {
                if (t.IsDrum || t.Notes == null || t.Notes.Count == 0) continue;
                var ev = new List<int[]>();
                foreach (var nn in t.Notes) ev.Add(new[] { nn.Pitch, nn.StartSlice, Math.Max(1, nn.LengthSlices) });
                ev.Sort((a, b) => a[1] != b[1] ? a[1].CompareTo(b[1]) : a[0].CompareTo(b[0]));
                tracks.Add(ev);
            }
            if (tracks.Count == 0) return new EvalReport { File = Path.GetFileName(path), Summary = "no pitched track" };

            var w12 = new double[12]; foreach (var tr in tracks) foreach (var e in tr) w12[Mod12(e[0])] += e[2];
            int hi = 0, lo = 0; double hiM = double.MinValue, loM = double.MaxValue;
            for (int i = 0; i < tracks.Count; i++) { double m = WMean(tracks[i]); if (m > hiM) { hiM = m; hi = i; } if (m < loM) { loM = m; lo = i; } }
            var melEv = tracks[hi];
            int finalLow = melEv.Count > 0 ? Mod12(melEv[melEv.Count - 1][0]) : -1;
            var key = MusicMathV2.DetectKey(w12, finalLow);
            int tonic = key.Tonic; bool minor = key.Minor;

            int num = score.TimeSigN > 0 ? score.TimeSigN : 4, den = score.TimeSigD > 0 ? score.TimeSigD : 4;
            if (num < 2 || num > 16 || (den != 2 && den != 4 && den != 8 && den != 16)) { num = 4; den = 4; }
            int barSlices = MusicMathV2.SlicesPerBar(num, den), beatSlices = MusicMathV2.BeatSlices(den);
            int sliceCount = score.SliceCount; foreach (var tr in tracks) foreach (var e in tr) if (e[1] + e[2] > sliceCount) sliceCount = e[1] + e[2];
            var barStarts = new List<int>();
            if (score.MeasureStartSlices != null && score.MeasureStartSlices.Count >= 2) barStarts.AddRange(score.MeasureStartSlices);
            else for (int s = 0; s < Math.Max(sliceCount, barSlices); s += barSlices) barStarts.Add(s);
            barStarts.Sort();
            int totalBars = barStarts.Count;
            Func<int, int> barOf = tt =>
            {
                int loo = 0, hii = totalBars - 1, ans = 0;
                while (loo <= hii) { int mid = (loo + hii) / 2; if (barStarts[mid] <= tt) { ans = mid; loo = mid + 1; } else hii = mid - 1; }
                return ans;
            };

            // accompaniment = all non-melody tracks (degrees) for chord detection; bass = lowest track
            var accomp = new List<int[]>(); // [degree, start, len]
            for (int i = 0; i < tracks.Count; i++) { if (i == hi && tracks.Count > 1) continue; foreach (var e in tracks[i]) accomp.Add(new[] { Mod12(e[0] - tonic), e[1], e[2] }); }
            var chords = new Chord[totalBars];
            for (int b = 0; b < totalBars; b++)
            {
                int bs = barStarts[b], be = b < totalBars - 1 ? barStarts[b + 1] : sliceCount;
                var pcw = new double[12];
                foreach (var e in accomp) { int ov = Math.Min(be, e[1] + e[2]) - Math.Max(bs, e[1]); if (ov > 0) pcw[e[0]] += ov; }
                int bassPc = -1, lowest = int.MaxValue;
                foreach (var e in tracks[lo]) if (e[1] < be && e[1] + e[2] > bs && e[0] < lowest) { lowest = e[0]; bassPc = Mod12(e[0] - tonic); }
                var gch = MusicMathV2.DetectChord(pcw, bassPc);
                if (gch == null) { chords[b] = new Chord(); continue; }
                var tones = new HashSet<int>(); foreach (int iv in gch.Iv) tones.Add(Mod12(gch.RootDeg + iv));
                chords[b] = new Chord { Root = gch.RootDeg, Tones = tones };
            }

            // melody skyline -> slots
            var sky = Skyline(melEv);
            var slots = new List<Slot>();
            foreach (var e in sky) slots.Add(new Slot { Bar = barOf(e[1]), Strong = (e[1] % barSlices) % (2 * beatSlices) == 0, Deg = Mod12(e[0] - tonic), Known = true });

            // hold out every Nth interior note
            var truth = new Dictionary<int, int>();
            if (holeEveryN <= 1) holeEveryN = 4;
            for (int i = 1; i < slots.Count - 1; i++) if (i % holeEveryN == 0) { truth[i] = slots[i].Deg; slots[i].Known = false; }
            if (truth.Count == 0) return new EvalReport { File = Path.GetFileName(path), Minor = minor, Tonic = tonic, Summary = "no holes (file too short for N=" + holeEveryN + ")" };

            var rr = Resolve(model, minor, chords, slots, opts);
            var byIdx = new Dictionary<int, HoleResult>(); foreach (var h in rr.Holes) byIdx[h.SlotIndex] = h;
            int top1 = 0, top3 = 0; double cerr = 0;
            foreach (var kv in truth)
            {
                int act = kv.Value;
                HoleResult hr; bool got = byIdx.TryGetValue(kv.Key, out hr);
                int pred = got ? hr.Deg : rr.Degrees[kv.Key];
                if (pred == act) top1++;
                if (got && hr.Ranked.Take(3).Any(p => p.Key == act)) top3++;
                int cd = Math.Abs(act - pred) % 12; cerr += Math.Min(cd, 12 - cd);
            }
            var rep = new EvalReport
            {
                File = Path.GetFileName(path), Minor = minor, Tonic = tonic,
                Holes = truth.Count, Top1 = top1, Top3 = top3, MeanCircErr = cerr / truth.Count
            };
            rep.Summary = string.Format("{0} | {1} tonic={2} bars={3} | holes={4} | top1={5} ({6:F0}%)  top3={7} ({8:F0}%) | meanErr={9:F2} semitones",
                rep.File, minor ? "min" : "maj", tonic, totalBars, rep.Holes, rep.Top1, Pct(rep.Top1, rep.Holes), rep.Top3, Pct(rep.Top3, rep.Holes), rep.MeanCircErr);
            return rep;
        }

        /// <summary>Convenience for the PowerShell harness: load the model JSON, evaluate the file, return a printable
        /// report (summary + a few sample hole predictions). All-string/int args so reflection stays trivial.</summary>
        public static string EvaluateFileReport(string modelPath, string scorePath, int holeEveryN)
        {
            CorpusModelV2 model = ComposerV2Runtime.ReadFromPath(modelPath);
            var rep = EvaluateFile(model, scorePath, holeEveryN, new Options());
            return rep.Summary;
        }

        /// <summary>Harness entry: evaluate a file OR every file in a folder, aggregate, and return the whole printable
        /// report as one string. Keeps ALL object marshalling inside C# (the PowerShell side just prints the string).</summary>
        public static string EvaluateFolderReport(string modelPath, string testPath, int holeEveryN, int max)
        {
            CorpusModelV2 model = ComposerV2Runtime.ReadFromPath(modelPath);
            var exts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".mid", ".midi", ".mscz", ".mscx" };
            List<string> files;
            if (Directory.Exists(testPath))
                files = Directory.EnumerateFiles(testPath, "*.*", SearchOption.AllDirectories)
                                 .Where(f => exts.Contains(Path.GetExtension(f)))
                                 .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                                 .Take(max > 0 ? max : int.MaxValue).ToList();
            else files = new List<string> { testPath };

            var sb = new StringBuilder();
            int sumH = 0, sumT1 = 0, sumT3 = 0; double sumErr = 0;
            foreach (var f in files)
            {
                EvalReport rep;
                try { rep = EvaluateFile(model, f, holeEveryN, new Options()); }
                catch (Exception ex) { sb.AppendLine("  [skip] " + Path.GetFileName(f) + " : " + ex.Message); continue; }
                sb.AppendLine("  " + rep.Summary);
                if (rep.Holes > 0) { sumH += rep.Holes; sumT1 += rep.Top1; sumT3 += rep.Top3; sumErr += rep.MeanCircErr * rep.Holes; }
            }
            sb.AppendLine(new string('-', 96));
            if (sumH > 0)
                sb.AppendLine(string.Format("AGGREGATE | holes={0} | top1={1} ({2:F1}%)  top3={3} ({4:F1}%) | meanErr={5:F2} semitones",
                    sumH, sumT1, 100.0 * sumT1 / sumH, sumT3, 100.0 * sumT3 / sumH, sumErr / sumH));
            else sb.AppendLine("No holes evaluated (files too short for N, or no usable melody).");
            return sb.ToString();
        }

        // Compare several resolver configs over the same test set (tuning + the degree-tier ABLATION) in one call.
        public static string SweepReport(string modelPath, string testPath, int holeEveryN, int max)
        {
            CorpusModelV2 model = ComposerV2Runtime.ReadFromPath(modelPath);
            var files = ListFiles(testPath, max);
            var configs = new List<KeyValuePair<string, Options>>
            {
                new KeyValuePair<string, Options>("model-only   emW=0  degTiers=on",  new Options { EmissionWeight = 0.0 }),
                new KeyValuePair<string, Options>("default      emW=.5 all-tiers-on", new Options { EmissionWeight = 0.5 }),
                new KeyValuePair<string, Options>("ABLATION     emW=.5 degTiers=OFF", new Options { EmissionWeight = 0.5, UseChordDegreeTiers = false }),
                new KeyValuePair<string, Options>("ABLATION     emW=.5 iv/contour=OFF", new Options { EmissionWeight = 0.5, UseIntervalViewpoints = false }),
                new KeyValuePair<string, Options>("ABLATION     emW=.5 deg+iv/cont=OFF", new Options { EmissionWeight = 0.5, UseChordDegreeTiers = false, UseIntervalViewpoints = false }),
                new KeyValuePair<string, Options>("heavy-prior  emW=1  degTiers=on",  new Options { EmissionWeight = 1.0 }),
                new KeyValuePair<string, Options>("diatonic     emW=.5 scale-only",   new Options { EmissionWeight = 0.5, RestrictToScale = true }),
            };
            var sb = new StringBuilder();
            sb.AppendLine(string.Format("{0,-32} {1,7} {2,7} {3,9}", "config", "top1%", "top3%", "meanErr"));
            sb.AppendLine(new string('-', 60));
            int holes = -1;
            foreach (var c in configs)
            {
                var a = RunConfig(model, files, holeEveryN, c.Value);
                if (holes < 0) holes = a.Holes;
                if (a.Holes == 0) { sb.AppendLine(string.Format("{0,-32} (no holes)", c.Key)); continue; }
                sb.AppendLine(string.Format("{0,-32} {1,7:F1} {2,7:F1} {3,9:F2}", c.Key, 100.0 * a.Top1 / a.Holes, 100.0 * a.Top3 / a.Holes, a.ErrSum / a.Holes));
            }
            sb.AppendLine(new string('-', 60));
            sb.AppendLine("holes=" + Math.Max(0, holes) + " over " + files.Count + " file(s)  (hole every " + holeEveryN + " notes)");
            return sb.ToString();
        }

        sealed class Agg { public int Holes, Top1, Top3; public double ErrSum; }
        static Agg RunConfig(CorpusModelV2 model, List<string> files, int n, Options opts)
        {
            var a = new Agg();
            foreach (var f in files)
            {
                EvalReport r; try { r = EvaluateFile(model, f, n, opts); } catch { continue; }
                if (r.Holes <= 0) continue;
                a.Holes += r.Holes; a.Top1 += r.Top1; a.Top3 += r.Top3; a.ErrSum += r.MeanCircErr * r.Holes;
            }
            return a;
        }
        static List<string> ListFiles(string testPath, int max)
        {
            var exts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".mid", ".midi", ".mscz", ".mscx" };
            if (Directory.Exists(testPath))
                return Directory.EnumerateFiles(testPath, "*.*", SearchOption.AllDirectories)
                                .Where(f => exts.Contains(Path.GetExtension(f)))
                                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                                .Take(max > 0 ? max : int.MaxValue).ToList();
            return new List<string> { testPath };
        }

        static double Pct(int a, int b) { return b > 0 ? 100.0 * a / b : 0.0; }
        static double WMean(List<int[]> ev) { double sw = 0, s = 0; foreach (var e in ev) { sw += e[2]; s += (double)e[0] * e[2]; } return sw > 0 ? s / sw : 0; }
        static List<int[]> Skyline(List<int[]> ev)
        {
            var byStart = new Dictionary<int, int[]>();
            foreach (var e in ev) { int[] cur; if (!byStart.TryGetValue(e[1], out cur) || e[0] > cur[0]) byStart[e[1]] = e; }
            return byStart.Values.OrderBy(e => e[1]).ToList();
        }
    }
}
