using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace MusicTracker.Engine.ComposerV2
{
    /// <summary>
    /// Composer V2 — offline corpus analyzer. Reads corpus/Ghibli via the existing importers
    /// (the ONLY reused code), canonicalizes each piece to scale degrees transposed to C, and trains
    /// the 4-level network of low-order, abstract, backed-off conditional distributions
    /// (<see cref="CorpusModelV2"/>). Run from analyze_ghibli_v2.ps1; the JSON is then bundled and
    /// loaded by the runtime generator (next round). Nothing here depends on the existing composer.
    /// </summary>
    public static class CorpusAnalyzerV2
    {
        // ---------- small note event (transposed degree filled after key detection) ----------
        class Ev { public int Pitch, Start, Len, Vel, Deg; }

        // ---------- accumulation helpers (build the back-off ladder uniformly) ----------
        static void Accum(CondModel m, string[] labels, string[] ctx, string state, double w)
        {
            while (m.Tiers.Count < ctx.Length) m.Tiers.Add(new CondTier());
            for (int i = 0; i < ctx.Length; i++)
            {
                if (string.IsNullOrEmpty(m.Tiers[i].Context)) m.Tiers[i].Context = labels[i];
                Dictionary<string, double> inner;
                if (!m.Tiers[i].Table.TryGetValue(ctx[i], out inner))
                {
                    inner = new Dictionary<string, double>();
                    m.Tiers[i].Table[ctx[i]] = inner;
                }
                double cur; inner.TryGetValue(state, out cur);
                inner[state] = cur + w;
            }
        }
        static void Bump(Dictionary<string, double> d, string key, double w)
        {
            double c; d.TryGetValue(key, out c); d[key] = c + w;
        }

        // =====================================================================
        public static CorpusModelV2 Analyze(string corpusDir)
        {
            var model = new CorpusModelV2();
            var exts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".mid", ".midi", ".mscz", ".mscx" };

            var files = Directory.EnumerateFiles(corpusDir, "*.*", SearchOption.AllDirectories)
                                 .Where(f => exts.Contains(Path.GetExtension(f)))
                                 .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                                 .ToList();

            model.SourceFolders = new List<string> { corpusDir };
            foreach (var f in files) AnalyzeFileInto(f, Rel(corpusDir, f), model);

            model.FilesAnalyzed = model.Pieces.Count;
            model.MajorPieces = model.Pieces.Count(p => !p.Minor);
            model.MinorPieces = model.Pieces.Count(p => p.Minor);
            return model;
        }

        /// <summary>Analyze several corpus directories into ONE model (e.g. bach/solo_cello + solo_violin +
        /// solo_flute → a unified "bach_solo" model). Labels carry the parent folder so the report stays readable.</summary>
        public static CorpusModelV2 AnalyzeMany(string[] dirs) { return AnalyzeManyCore(dirs, null, null); }

        /// <summary>In-app: analyze several folders into a model and write the JSON + report, reporting per-file
        /// progress (done, total, fileName) for a progress dialog. <paramref name="orders"/> = per-dimension Markov
        /// orders chosen in the dialog (null → defaults). Distinct name so the script reflection calls to
        /// <see cref="AnalyzeMany(string[])"/> / <see cref="AnalyzeManyToFile(string[],string,string)"/> stay unambiguous.</summary>
        public static CorpusModelV2 AnalyzeManyWithProgress(string[] dirs, string jsonPath, string reportPath, Action<int, int, string> progress, Dictionary<string, int> orders)
        {
            return WriteModel(AnalyzeManyCore(dirs, progress, orders), jsonPath, reportPath);
        }

        static CorpusModelV2 AnalyzeManyCore(string[] dirs, Action<int, int, string> progress, Dictionary<string, int> orders)
        {
            var model = new CorpusModelV2();
            model.SourceFolders = new List<string>(dirs);
            if (orders != null) model.Orders = new Dictionary<string, int>(orders);
            var exts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".mid", ".midi", ".mscz", ".mscx" };
            var files = new List<string>();
            foreach (var dir in dirs)
                if (Directory.Exists(dir))
                    files.AddRange(Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories)
                                            .Where(f => exts.Contains(Path.GetExtension(f))));
            files.Sort((a, b) => string.Compare(a, b, StringComparison.OrdinalIgnoreCase));
            int total = files.Count, done = 0;
            foreach (var f in files)
            {
                if (progress != null) progress(done, total, Path.GetFileName(f));
                string parent = Path.GetFileName(Path.GetDirectoryName(f));
                AnalyzeFileInto(f, parent + "\\" + Path.GetFileName(f), model);
                done++;
            }
            if (progress != null) progress(total, total, "");
            model.FilesAnalyzed = model.Pieces.Count;
            model.MajorPieces = model.Pieces.Count(p => !p.Minor);
            model.MinorPieces = model.Pieces.Count(p => p.Minor);
            return model;
        }

        static void AnalyzeFileInto(string f, string label, CorpusModelV2 model)
        {
            MuseScoreImporter.Score score = null;
            try
            {
                string ext = Path.GetExtension(f).ToLowerInvariant();
                score = (ext == ".mscz" || ext == ".mscx") ? MuseScoreImporter.Load(f) : MidiImporter.Load(f);
            }
            catch (Exception ex) { model.Skipped.Add(label + " : " + ex.Message); return; }

            try
            {
                if (!AnalyzePiece(score, model, label))
                    model.Skipped.Add(label + " : no usable pitched track");
            }
            catch (Exception ex) { model.Skipped.Add(label + " : analyze error " + ex.Message); }
        }

        /// <summary>Reduced model from a SINGLE file, optionally skipping the first <paramref name="skipSeconds"/>
        /// (the real theme may start later in the file).</summary>
        public static CorpusModelV2 AnalyzeOneFile(string file, double skipSeconds)
        {
            var model = new CorpusModelV2();
            string ext = Path.GetExtension(file).ToLowerInvariant();
            MuseScoreImporter.Score score = (ext == ".mscz" || ext == ".mscx") ? MuseScoreImporter.Load(file) : MidiImporter.Load(file);

            int skip;
            if (skipSeconds < 0) skip = AutoThemeStartSlices(score);  // auto: where the principal instrument's theme enters
            else { double bpm = score.Bpm > 0 ? score.Bpm : 96; skip = (int)Math.Round(skipSeconds * bpm / 60.0 * MusicMathV2.SlicesPerQuarter); }
            if (skip > 0)
            {
                foreach (var t in score.Tracks)
                {
                    if (t.Notes == null) continue;
                    var kept = new List<MuseScoreImporter.Note>();
                    foreach (var n in t.Notes) if (n.StartSlice >= skip) { n.StartSlice -= skip; kept.Add(n); }
                    t.Notes = kept;
                }
                score.SliceCount = Math.Max(0, score.SliceCount - skip);
                if (score.MeasureStartSlices != null) score.MeasureStartSlices.Clear(); // re-grid from the theme start
            }

            try { if (!AnalyzePiece(score, model, Path.GetFileName(file))) model.Skipped.Add(file + " : no usable pitched track"); }
            catch (Exception ex) { model.Skipped.Add(file + " : " + ex.Message); }
            model.FilesAnalyzed = model.Pieces.Count;
            model.MajorPieces = model.Pieces.Count(p => !p.Minor);
            model.MinorPieces = model.Pieces.Count(p => p.Minor);
            return model;
        }

        // Auto-detect where the THEME starts: the first dense phrase of the principal melodic instrument
        // (the non-drum track with the highest duration-weighted mean pitch). Lets a file with a long intro
        // be modeled from its theme onward.
        static int AutoThemeStartSlices(MuseScoreImporter.Score score)
        {
            List<MuseScoreImporter.Note> best = null; double bestMean = double.MinValue;
            foreach (var t in score.Tracks)
            {
                if (t.IsDrum || t.Notes == null || t.Notes.Count < 8) continue;
                double sw = 0, s = 0;
                foreach (var n in t.Notes) { int l = Math.Max(1, n.LengthSlices); sw += l; s += (double)n.Pitch * l; }
                double mean = sw > 0 ? s / sw : 0;
                if (mean > bestMean) { bestMean = mean; best = t.Notes; }
            }
            if (best == null) return 0;
            var onsets = best.Select(n => n.StartSlice).OrderBy(x => x).ToList();
            int win = 4 * 96; // 4 bars
            foreach (int o in onsets)
            {
                int cnt = 0; foreach (var n in best) if (n.StartSlice >= o && n.StartSlice < o + win) cnt++;
                if (cnt >= 6) return o;  // first sustained phrase of the principal instrument
            }
            return onsets.Count > 0 ? onsets[0] : 0;
        }

        public static CorpusModelV2 AnalyzeToFile(string corpusDir, string jsonPath, string reportPath)
        {
            return WriteModel(Analyze(corpusDir), jsonPath, reportPath);
        }

        /// <summary>Analyze several directories into one model and write the JSON + markdown report.</summary>
        public static CorpusModelV2 AnalyzeManyToFile(string[] dirs, string jsonPath, string reportPath)
        {
            return WriteModel(AnalyzeMany(dirs), jsonPath, reportPath);
        }

        static CorpusModelV2 WriteModel(CorpusModelV2 model, string jsonPath, string reportPath)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(jsonPath)));
            File.WriteAllText(jsonPath, MiniJson.Serialize(model));
            if (!string.IsNullOrEmpty(reportPath))
            {
                try { File.WriteAllText(reportPath, BuildReport(model)); }
                catch (Exception ex) { File.WriteAllText(reportPath, "report error: " + ex); }
            }
            return model;
        }

        static string Rel(string root, string f)
        {
            try { return f.Substring(root.TrimEnd('\\', '/').Length).TrimStart('\\', '/'); }
            catch { return Path.GetFileName(f); }
        }

        // The immediate corpus SUBFOLDER can carry a GROUND-TRUTH mood/style category (e.g. Ghibli reorganized into
        // Calme_Nostalgique / Épique_Majestueux / …). When the leading folder of the file label matches a recognized
        // category, it OVERRIDES the heuristic MelodyCharacter, so the melody/rhythm models learn real per-category
        // statistics (and Auto-mood generation samples them straight from CharacterDistribution). Unknown folders
        // (e.g. Bach "wtc", "solo_cello") are NOT categories → fall through to the computed character, leaving every
        // other corpus untouched. The token = the folder name lower-cased (accents kept), byte-identical to the
        // generation side. Extend by adding a folder + its token here.
        static readonly HashSet<string> MoodFolders = new HashSet<string>(StringComparer.Ordinal)
        {
            "calme", "modérée", "enjouée", "majestueux",                       // the 4 computed tokens (if used as folders)
            "calme_nostalgique", "enjoué_léger", "solennel_requiem",           // Ghibli ground-truth categories
            "sombre_dramatique", "valse_dansant", "épique_majestueux",
        };
        static string FolderCharacter(string fileLabel)
        {
            if (string.IsNullOrEmpty(fileLabel)) return null;
            int slash = fileLabel.IndexOfAny(new[] { '\\', '/' });
            if (slash <= 0) return null;
            string f = fileLabel.Substring(0, slash).Trim().ToLowerInvariant();
            return MoodFolders.Contains(f) ? f : null;
        }

        // =====================================================================
        static bool AnalyzePiece(MuseScoreImporter.Score score, CorpusModelV2 model, string fileLabel)
        {
            // ---- tracks (skip drums + empties) ----
            var tracks = new List<List<Ev>>();
            foreach (var t in score.Tracks)
            {
                if (t.IsDrum || t.Notes == null || t.Notes.Count == 0) continue;
                var ev = new List<Ev>();
                foreach (var n in t.Notes)
                    ev.Add(new Ev { Pitch = n.Pitch, Start = n.StartSlice, Len = Math.Max(1, n.LengthSlices), Vel = n.Velocity });
                ev.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : a.Pitch.CompareTo(b.Pitch));
                tracks.Add(ev);
            }
            if (tracks.Count == 0) return false;

            // drum tracks (channel 10) collected separately for the percussion model
            var drumEv = new List<Ev>();
            foreach (var t in score.Tracks)
                if (t.IsDrum && t.Notes != null)
                    foreach (var n in t.Notes)
                        drumEv.Add(new Ev { Pitch = n.Pitch, Start = n.StartSlice, Len = Math.Max(1, n.LengthSlices), Vel = n.Velocity });

            // ---- key detection (exact symbolic histogram + K-S) ----
            var w12 = new double[12];
            foreach (var tr in tracks) foreach (var e in tr) w12[Mod12(e.Pitch)] += e.Len;
            int hiTrack = 0, loTrack = 0; double hiMean = double.MinValue, loMean = double.MaxValue;
            for (int i = 0; i < tracks.Count; i++)
            {
                double mean = WeightedMeanPitch(tracks[i]);
                if (mean > hiMean) { hiMean = mean; hiTrack = i; }
                if (mean < loMean) { loMean = mean; loTrack = i; }
            }
            var melTrackEv = tracks[hiTrack];
            int finalLowPc = melTrackEv.Count > 0 ? Mod12(melTrackEv[melTrackEv.Count - 1].Pitch) : -1;
            var key = MusicMathV2.DetectKey(w12, finalLowPc);
            int tonic = key.Tonic; bool minor = key.Minor;

            // transpose -> degrees relative to C
            foreach (var tr in tracks) foreach (var e in tr) e.Deg = Mod12(e.Pitch - tonic);

            // classify the church MODE (root context) from the tonic-relative histogram
            var wRel = new double[12];
            for (int dd = 0; dd < 12; dd++) wRel[dd] = w12[Mod12(tonic + dd)];
            string modeName = MusicMathV2.DetectMode(wRel);
            Bump(model.ModeDistribution, modeName, 1);

            // ---- meter / bar grid ----
            int num = score.TimeSigN > 0 ? score.TimeSigN : 4;
            int den = score.TimeSigD > 0 ? score.TimeSigD : 4;
            // guard against implausible/guessed time signatures (e.g. a MIDI with num=1) -> 4/4
            if (num < 2 || num > 16 || (den != 2 && den != 4 && den != 8 && den != 16)) { num = 4; den = 4; }
            int barSlices = MusicMathV2.SlicesPerBar(num, den);
            int beatSlices = MusicMathV2.BeatSlices(den);
            double beatsPerBar = barSlices / (double)Math.Max(1, beatSlices);

            var barStarts = new List<int>();
            if (score.MeasureStartSlices != null && score.MeasureStartSlices.Count >= 2)
                barStarts.AddRange(score.MeasureStartSlices);
            int sliceCount = score.SliceCount;
            foreach (var tr in tracks) foreach (var e in tr) if (e.Start + e.Len > sliceCount) sliceCount = e.Start + e.Len;
            if (barStarts.Count < 2)
            {
                barStarts.Clear();
                for (int s = 0; s < Math.Max(sliceCount, barSlices); s += barSlices) barStarts.Add(s);
            }
            barStarts.Sort();
            int totalBars = barStarts.Count;
            Func<int, int> barEnd = b => b < totalBars - 1 ? barStarts[b + 1] : sliceCount;

            // ---- melody skyline + bass line ----
            var mel = Skyline(melTrackEv, true);                 // top note per onset
            var bass = Skyline(tracks[loTrack], false);          // bottom note per onset
            if (mel.Count < 2) return false;

            // ---- root context: melody CHARACTER (enjouée / modérée / calme), like the mode ----
            double charBpm = score.Bpm > 0 ? score.Bpm : 96;
            int melSpan = (mel[mel.Count - 1].Start + mel[mel.Count - 1].Len) - mel[0].Start;
            double spanSeconds = Math.Max(0.5, (melSpan / (double)MusicMathV2.SlicesPerQuarter) * (60.0 / charBpm));
            double notesPerSec = mel.Count / spanSeconds;
            int shortCnt = 0, longCnt = 0;
            foreach (var e in mel) { if (e.Len <= 12) shortCnt++; if (e.Len >= 36) longCnt++; } // ≤eighth / ≥dotted-quarter
            double shortShare = shortCnt / (double)mel.Count;
            double longShare = longCnt / (double)mel.Count;
            // Ground-truth folder category (Ghibli reorg) overrides the heuristic; else compute it from the melody.
            string character = FolderCharacter(fileLabel)
                             ?? MusicMathV2.MelodyCharacter(notesPerSec, shortShare, longShare, charBpm, !minor);
            Bump(model.CharacterDistribution, character, 1);

            // ---- accompaniment = all tracks except the melody track (fallback: everything) ----
            // Combined set is used for CHORD detection (full harmonic texture).
            var accomp = new List<Ev>();
            for (int i = 0; i < tracks.Count; i++) if (i != hiTrack || tracks.Count == 1) accomp.AddRange(tracks[i]);
            accomp.Sort((a, b) => a.Start.CompareTo(b.Start));
            // The single BUSIEST non-melody track is used for TEXTURE/articulation: combining several
            // voices fabricates simultaneous onsets and mislabels everything as "block" chords.
            int accIdx = -1, accBest = -1;
            for (int i = 0; i < tracks.Count; i++)
            {
                if (i == hiTrack && tracks.Count > 1) continue;
                if (tracks[i].Count > accBest) { accBest = tracks[i].Count; accIdx = i; }
            }
            var accompTrack = accIdx >= 0 ? tracks[accIdx] : accomp;

            // ---- per-bar chord detection ----
            var chordRoot = new int[totalBars];
            var chordGroup = new string[totalBars];
            var chordCanon = new int[totalBars];
            var chordTones = new HashSet<int>[totalBars];
            var hasChord = new bool[totalBars];
            for (int b = 0; b < totalBars; b++)
            {
                int bs = barStarts[b], be = barEnd(b);
                var pcw = new double[12];
                foreach (var e in accomp)
                {
                    int ov = Math.Min(be, e.Start + e.Len) - Math.Max(bs, e.Start);
                    if (ov > 0) pcw[e.Deg] += ov;
                }
                int bassPc = -1; int lowest = int.MaxValue;
                foreach (var e in tracks[loTrack])
                    if (e.Start < be && e.Start + e.Len > bs && e.Pitch < lowest) { lowest = e.Pitch; bassPc = e.Deg; }
                var g = MusicMathV2.DetectChord(pcw, bassPc);
                if (g == null) { hasChord[b] = false; chordTones[b] = new HashSet<int>(); continue; }
                hasChord[b] = true; chordRoot[b] = g.RootDeg; chordGroup[b] = g.Group; chordCanon[b] = g.CanonIndex;
                var set = new HashSet<int>();
                foreach (int iv in g.Iv) set.Add(Mod12(g.RootDeg + iv));
                chordTones[b] = set;
                // chord INVERSION (bass vs root) by function -> learned, reinjected at generation
                int relB = bassPc < 0 ? 0 : Mod12(bassPc - g.RootDeg);
                string invc = relB == 0 ? "root" : ((relB == 3 || relB == 4) ? "inv1" : ((relB == 6 || relB == 7 || relB == 8) ? "inv2" : "slash"));
                Accum(model.Inversion, new[] { "func", "" }, new[] { MusicMathV2.ChordFunction(g.RootDeg), "" }, invc, 1);
            }

            // nearest DISTINCT chord roots around a bar — the "accord avant / accord après" of a note in that bar.
            // Skips identical-root bars so a multi-bar chord isn't counted as its own neighbour; -1 when none.
            Func<int, int> prevDistinctRoot = bar =>
            {
                int cur = (bar >= 0 && bar < totalBars && hasChord[bar]) ? chordRoot[bar] : -1;
                for (int b = Math.Min(bar, totalBars - 1) - 1; b >= 0; b--)
                    if (hasChord[b] && chordRoot[b] != cur) return chordRoot[b];
                return -1;
            };
            Func<int, int> nextDistinctRoot = bar =>
            {
                int cur = (bar >= 0 && bar < totalBars && hasChord[bar]) ? chordRoot[bar] : -1;
                for (int b = Math.Max(0, bar) + 1; b < totalBars; b++)
                    if (hasChord[b] && chordRoot[b] != cur) return chordRoot[b];
                return -1;
            };

            // bar index of a slice (binary search on barStarts)
            Func<int, int> barOf = t =>
            {
                int lo = 0, hi = totalBars - 1, ans = 0;
                while (lo <= hi) { int mid = (lo + hi) / 2; if (barStarts[mid] <= t) { ans = mid; lo = mid + 1; } else hi = mid - 1; }
                return ans;
            };
            // Per-bar SECTION roles from real melodic ACTIVITY: a true INTRO (pre-theme bars) + THEME (the opening
            // sustained statement) + BODY + CLIMAX (peak-energy region) + OUTRO (trailing tail). Replaces the old
            // bar-fraction guess so the section-conditioned chains (harmony/melody/dynamics…) learn real intros/themes.
            var barRoles = ComputeBarRoles(mel, totalBars, barSlices, barOf);
            Func<int, string> roleOf = b => barRoles[Math.Max(0, Math.Min(totalBars - 1, b))];

            // CHARACTER (enjouée/modérée/calme/majestueux — the EXISTING mood axis, computed above) prepended onto the
            // section token FOR THE MELODY + RHYTHM only, so those models learn character-distinct statistics (a lively
            // Allegro vs a calm slow movement, etc.). Harmony/texture/etc. keep the plain section role.
            Func<int, string> secOf = b => character + "/" + roleOf(b);

            var modeM = minor ? model.Minor : model.Major;
            modeM.Pieces++;

            // ============ LEVEL 1 — form / phrase ============
            // section-role chain (dedup consecutive)
            string prevRole = null;
            for (int b = 0; b < totalBars; b++)
            {
                string r = roleOf(b);
                if (r != prevRole)
                {
                    Accum(model.SectionRole, new[] { "rolePrev", "" },
                          new[] { prevRole ?? "^", "" }, r, 1);
                    prevRole = r;
                }
            }

            // phrase segmentation of the melody (boundary = >= one beat of silence)
            // boundary on a breath (>= half a beat of silence) OR a held note (>= 2 beats = a cadence)
            var phrases = new List<int[]>(); // [startIdx, endIdx]
            int pStart = 0;
            int breath = Math.Max(1, beatSlices / 2);
            for (int i = 0; i < mel.Count - 1; i++)
            {
                int gap = mel[i + 1].Start - (mel[i].Start + mel[i].Len);
                bool longNote = mel[i].Len >= 2 * beatSlices;
                if (gap >= breath || longNote) { phrases.Add(new[] { pStart, i }); pStart = i + 1; }
            }
            phrases.Add(new[] { pStart, mel.Count - 1 });
            var melPhrasePos = new string[mel.Count];
            foreach (var ph in phrases)
            {
                int a = ph[0], z = ph[1];
                int spanSlices = (mel[z].Start + mel[z].Len) - mel[a].Start;
                double lenBeats = spanSlices / (double)Math.Max(1, beatSlices);
                Accum(model.PhraseLength, new[] { "section", "" },
                      new[] { roleOf(barOf(mel[a].Start)), "" }, PhraseLenBucket(lenBeats), 1);
                for (int i = a; i <= z; i++)
                {
                    if (i == a) melPhrasePos[i] = (mel[i].Start % barSlices == 0) ? "init" : "pickup";
                    else if (i == z) melPhrasePos[i] = "cad";
                    else melPhrasePos[i] = "mid";
                }
            }

            // ============ LEVEL 1 — tonality / modulation (windowed K-S) ============
            AccumTonality(modeM, mel, tonic, barStarts, barSlices, beatSlices, totalBars, roleOf, barOf);

            // ============ LEVEL 2 — harmony (factored) + harmonic rhythm ============
            // merge consecutive identical bars into chord runs
            var runs = new List<int[]>(); // [rootDeg, startBar, lenBars, canon]; group stored separately
            var runGroup = new List<string>();
            for (int b = 0; b < totalBars; b++)
            {
                if (!hasChord[b]) continue;
                if (runs.Count > 0 && runs[runs.Count - 1][0] == chordRoot[b] && runGroup[runGroup.Count - 1] == chordGroup[b])
                    runs[runs.Count - 1][2]++;
                else { runs.Add(new[] { chordRoot[b], b, 1, chordCanon[b] }); runGroup.Add(chordGroup[b]); }
            }
            // (a) root motion — CONFIGURABLE order (default 2) -> backoff -> marginal
            int rootOrder = MusicMathV2.Order(model.Orders, "harmonyRoot", 2);
            for (int i = 0; i < runs.Count; i++)
            {
                int ii = i;
                var rHist = MusicMathV2.Hist(rootOrder, k => ii - 1 - k >= 0 ? runs[ii - 1 - k][0].ToString() : "^");
                string r1 = rHist.Length > 0 ? rHist[0] : "^";
                string secR = roleOf(runs[i][1]);
                // (a) root motion — pure n-grams (order..2) + SECTION-conditioned (sec|root1) + bare order-1 + marginal,
                //     so e.g. an INTRO learns its own characteristic chord moves.
                var rLad = MusicMathV2.BuildLadder("root", rHist, rootOrder, 2, null, null,
                    new[] { "sec|root1", "root1", "" }, new[] { secR + "|" + r1, r1, "" });
                Accum(modeM.HarmonyRoot, rLad.Labels, rLad.Ctx, runs[i][0].ToString(), 1);
                // (b) quality by degree — SECTION-conditioned (intro/theme favour their own colours) + bare degree + marginal
                Accum(modeM.QualityByDegree, new[] { "sec|deg", "deg", "" },
                      new[] { secR + "|" + runs[i][0], runs[i][0].ToString(), "" }, runGroup[i], runs[i][2]);
            }
            // harmonic rhythm chain — CONFIGURABLE order (default 4) + section
            int hrOrder = MusicMathV2.Order(model.Orders, "harmonicRhythm", 4);
            for (int i = 1; i < runs.Count; i++)
            {
                int ii = i;
                string prevDur = MusicMathV2.DurBucketBars(runs[i - 1][2]);
                string curDur = MusicMathV2.DurBucketBars(runs[i][2]);
                string sec = roleOf(runs[i][1]);
                var dHist = MusicMathV2.Hist(hrOrder, k => ii - 1 - k >= 0 ? MusicMathV2.DurBucketBars(runs[ii - 1 - k][2]) : "^");
                var lad = MusicMathV2.BuildLadder("dur", dHist, hrOrder, 2, null, null,
                    new[] { "sec|dur1", "dur1", "" }, new[] { sec + "|" + prevDur, prevDur, "" });
                Accum(model.HarmonicRhythm, lad.Labels, lad.Ctx, curDur, 1);
            }

            // ============ LEVEL 3 + 4 — melody, rhythm, dynamics, articulation ============
            bool velUsable = VelocityVariance(mel) >= 6.0;
            var artSeq = new string[mel.Count];
            for (int i = 0; i < mel.Count; i++)
            {
                var e = mel[i];
                int bar = barOf(e.Start);
                string func = hasChord[bar] ? MusicMathV2.ChordFunction(chordRoot[bar]) : "X";
                bool isCt = hasChord[bar] && chordTones[bar].Contains(e.Deg);
                string rhythm = MusicMathV2.DurBucket(e.Len);
                string sec = secOf(bar);   // movement/role (see secOf)
                string beatPos = (e.Start % beatSlices == 0) ? "on" : "off";
                int beatIdx = (e.Start % barSlices) / Math.Max(1, beatSlices);
                // delta #1: metric position (strong = beats 1 & 3) ; delta #2: chord-relative ROLE
                string metric = ((e.Start % barSlices) % (2 * beatSlices) == 0) ? "S" : "w";
                string role;
                if (hasChord[bar])
                {
                    if (chordTones[bar].Contains(e.Deg)) role = "ct";
                    else { int rel = Mod12(e.Deg - chordRoot[bar]); role = rel == 2 ? "t9" : (rel == 5 ? "t11" : (rel == 9 ? "t13" : "nct")); }
                }
                else role = "nct";

                Bump(modeM.DegreeHistogram, MusicMathV2.DegName[e.Deg], e.Len);
                modeM.MelodyNotes += 1; if (isCt) modeM.ChordTones += 1;

                // non-chord-tone classification (global)
                Bump(model.NctTypes, NctType(mel, i, isCt), 1);

                // delta #3: cadence = phrase-final degree
                if (melPhrasePos[i] == "cad")
                    Accum(modeM.Cadence, new[] { "" }, new[] { "" }, e.Deg.ToString(), Math.Max(1, e.Len));

                if (i < mel.Count - 1)
                {
                    var nxt = mel[i + 1];
                    // delta #4: order-2 (prev2|cur) + metric + chord-role + NEXT-chord function
                    // (anticipation: how the melody moves INTO the upcoming chord). Hard back-off.
                    string d1 = e.Deg.ToString();
                    string d2 = i >= 1 ? mel[i - 1].Deg.ToString() : "^";
                    int barNext = barOf(nxt.Start);
                    int nextRoot = hasChord[barNext] ? chordRoot[barNext] : (hasChord[bar] ? chordRoot[bar] : 0);
                    string nfunc = MusicMathV2.ChordFunction(nextRoot);
                    // delta #5: EXACT chord-degree neighbourhood around the note being predicted (X = nxt) — the chord
                    // degree BEFORE / DURING / AFTER X (previous & next DISTINCT chord roots). Finer than the T/S/D
                    // function tiers; Witten-Bell only lets it dominate where the corpus actually supports it.
                    int xCur = hasChord[barNext] ? chordRoot[barNext] : -1;
                    int xPrev = prevDistinctRoot(barNext), xNext = nextDistinctRoot(barNext);
                    string cdeg = xCur < 0 ? "^" : xCur.ToString();
                    string pdeg = xPrev < 0 ? "^" : xPrev.ToString();
                    string ndeg = xNext < 0 ? "^" : xNext.ToString();
                    // PURE high-order degree tiers up to the CONFIGURED order, then the fixed contextual tiers.
                    // Witten-Bell blends; high order only dominates where the corpus has the idiom.
                    int melOrder = MusicMathV2.Order(model.Orders, "melody", 8);
                    var dHist = MusicMathV2.Hist(melOrder, j => i - j >= 0 ? mel[i - j].Deg.ToString() : "^");
                    // Item 2 VIEWPOINTS: the melodic interval + contour that led INTO the current note (degree-space).
                    int iv = i >= 1 ? MusicMathV2.SignedIv(mel[i - 1].Deg, e.Deg) : 0;
                    string iv1 = i >= 1 ? iv.ToString() : "^";
                    string cont1 = i >= 1 ? MusicMathV2.Contour(iv) : "^";
                    var melLad = MusicMathV2.BuildLadder("d", dHist, melOrder, 3, null, null,
                        new[] { "sec|d1|metric|role", "d1|pdeg|cdeg|ndeg|role", "d1|cdeg|role", "d1|iv1|role", "d1|cont1", "d1|metric|role|nfunc", "d2|d1|metric|role", "d1|func|role|nfunc", "d1|metric|role", "d1|role", "d1", "" },
                        new[] { sec + "|" + d1 + "|" + metric + "|" + role, d1 + "|" + pdeg + "|" + cdeg + "|" + ndeg + "|" + role, d1 + "|" + cdeg + "|" + role, d1 + "|" + iv1 + "|" + role, d1 + "|" + cont1, d1 + "|" + metric + "|" + role + "|" + nfunc, d2 + "|" + d1 + "|" + metric + "|" + role, d1 + "|" + func + "|" + role + "|" + nfunc, d1 + "|" + metric + "|" + role, d1 + "|" + role, d1, "" });
                    Accum(modeM.Melody, melLad.Labels, melLad.Ctx, nxt.Deg.ToString(), 1);

                    int interval = nxt.Pitch - e.Pitch;   // kept only for the dynamics CONTOUR below
                    // (IntervalByRhythm / IntervalByDegree removed — they follow from Melody + RhythmCell)

                    // (note-to-note melodic rhythm chain removed — rhythm is modelled as beat-CELLS, see AccumRhythmCells)

                    // articulation (melody) — length vs inter-onset
                    int ioi = nxt.Start - e.Start;
                    double ratio = ioi > 0 ? e.Len / (double)ioi : 1.0;
                    artSeq[i] = ratio < 0.6 ? "stac" : (ratio < 0.95 ? "norm" : "leg");

                    // dynamics (only when the file actually carries velocity)
                    if (velUsable)
                    {
                        string contour = MusicMathV2.Sign(interval) > 0 ? "up" : (MusicMathV2.Sign(interval) < 0 ? "down" : "flat");
                        Accum(model.Dynamics, new[] { "beat|sec|cont", "beat|sec", "beat", "" },
                              new[] { beatPos + "|" + sec + "|" + contour, beatPos + "|" + sec, beatPos, "" },
                              VelBucket(e.Vel), 1);
                        Accum(model.AccentByPos, new[] { "beatIdx", "" },
                              new[] { beatIdx.ToString(), "" }, VelBucket(e.Vel), 1);
                    }
                }
                else artSeq[i] = "leg";
                // (LeapResolution removed — leaps are already constrained by the generator's leap clamp + the Melody chain)
            }
            // articulation (melody) chain — order 2
            for (int i = 0; i < mel.Count - 1; i++)
            {
                string a1 = artSeq[i] ?? "norm";
                string a2 = i >= 1 ? (artSeq[i - 1] ?? "norm") : "^";
                string a3 = i >= 2 ? (artSeq[i - 2] ?? "norm") : "^";
                string a4 = i >= 3 ? (artSeq[i - 3] ?? "norm") : "^";
                Accum(model.ArtMelody, new[] { "art4|art3|art2|art1", "art3|art2|art1", "art2|art1", "art1", "" },
                      new[] { a4 + "|" + a3 + "|" + a2 + "|" + a1, a3 + "|" + a2 + "|" + a1, a2 + "|" + a1, a1, "" }, artSeq[i + 1] ?? "norm", 1);
            }

            // beat onset patterns (side stat)
            // (BeatOnsetPattern removed — redundant with the per-beat RhythmCell model)

            // per-BEAT rhythm CELLS, chained over several beats + metric position (multi-beat phrasing)
            AccumRhythmCells(model.RhythmCell, MusicMathV2.Order(model.Orders, "rhythmCell", 8), mel, barStarts, barSlices, beatSlices, totalBars, barOf, secOf);

            // ============ LEVEL 3 — texture / accompaniment + LEVEL 4 accomp articulation ============
            AccumTexture(model, accompTrack, barStarts, barEnd, totalBars, beatsPerBar, roleOf);

            // ACCOMPANIMENT modelled "like the melody" but CHORD-RELATIVE: its skyline (one note per onset) drives
            //   • AccompCell — the same per-beat rhythm cells as the melody, and
            //   • AccompTone — a chord-tone-index pattern (re-voiceable onto any chord at generation).
            var accSky = Skyline(accompTrack, true);
            AccumRhythmCells(model.AccompCell, MusicMathV2.Order(model.Orders, "accompCell", 8), accSky, barStarts, barSlices, beatSlices, totalBars, barOf, secOf);
            AccumAccompTone(model, accSky, chordRoot, chordTones, hasChord, barOf, barSlices, beatSlices);

            // ============ LEVEL 4 — voice leading (melody vs bass) ============
            AccumVoiceLeading(modeM, mel, bass, chordRoot, hasChord, barOf);

            // ============ PERCUSSION (rhythm + instruments) ============
            AccumPercussion(model, drumEv, barStarts, barEnd, totalBars, beatSlices, roleOf);

            // ---- meta / aggregates ----
            Bump(model.TempoHistogram, TempoBucket(score.Bpm), 1);
            Bump(model.MeterHistogram, num + "/" + den, 1);
            model.Pieces.Add(new PieceInfoV2
            {
                File = fileLabel,
                TonicPc = tonic,
                Minor = minor,
                Mode = modeName,
                Character = character,
                KeyScore = Math.Round(key.Score, 3),
                Bpm = Math.Round(score.Bpm, 1),
                Meter = num + "/" + den,
                Bars = totalBars,
                MelodyNotes = mel.Count,
                VelocityUsable = velUsable
            });
            return true;
        }

        // ---------- dimension helpers ----------
        static void AccumTonality(ModeModels modeM, List<Ev> mel, int tonic, List<int> barStarts,
                                  int barSlices, int beatSlices, int totalBars, Func<int, string> roleOf, Func<int, int> barOf)
        {
            // window = 4 bars of the melody; local tonic via K-S; offset relative to home (0 after transpose).
            int win = 4 * barSlices;
            if (win <= 0 || mel.Count == 0) return;
            int endSlice = mel[mel.Count - 1].Start + mel[mel.Count - 1].Len;
            string prevOff = null;
            for (int s = 0; s < endSlice; s += barSlices) // step one bar, window 4 bars
            {
                var w = new double[12];
                foreach (var e in mel) if (e.Start >= s && e.Start < s + win) w[Mod12(e.Pitch - tonic)] += e.Len;
                double tot = 0; for (int i = 0; i < 12; i++) tot += w[i];
                if (tot < beatSlices) continue;
                var g = MusicMathV2.DetectKey(w, -1);
                string off = OffsetClass(g.Tonic); // g.Tonic already relative to C (home)
                if (off != prevOff)
                {
                    Accum(modeM.Tonality, new[] { "offPrev", "" }, new[] { prevOff ?? "^", "" }, off, 1);
                    prevOff = off;
                }
            }
        }


        // Per-BEAT rhythm CELL = the note-value figure that fills the beat (durations of the notes whose ONSET is
        // in the beat, e.g. "8+8", "16+16+16+16", "8+16+16", "q", "q.", "8+q."; "-" when no onset starts in the beat
        // = a held note continuing or a rest). The cell is chained over the previous 3 beats + its index in the bar,
        // so the model learns multi-beat / whole-bar rhythmic PHRASES conditioned on the metric position (e.g. a beat
        // of two eighths tends to be followed by …, a dotted quarter by an eighth or two sixteenths, …).
        static void AccumRhythmCells(CondModel cellModel, int order, List<Ev> mel, List<int> barStarts, int barSlices,
                                     int beatSlices, int totalBars, Func<int, int> barOf, Func<int, string> roleOf)
        {
            if (mel.Count == 0 || beatSlices <= 0) return;
            // unified note+REST event stream: a note, and where a gap >= an eighth follows it, a rest "r<value>"
            // (smaller gaps are détaché/articulation, not phrasing rests). Rests are first-class cell tokens.
            var ev = new List<int[]>(); // [start, dur, isRest]
            for (int i = 0; i < mel.Count; i++)
            {
                ev.Add(new[] { mel[i].Start, Math.Max(1, mel[i].Len), 0 });
                if (i + 1 < mel.Count)
                {
                    int gap = mel[i + 1].Start - (mel[i].Start + mel[i].Len);
                    if (gap >= 12) ev.Add(new[] { mel[i].Start + mel[i].Len, gap, 1 });   // >= eighth = a phrasing rest
                }
            }
            int endSlice = mel[mel.Count - 1].Start + mel[mel.Count - 1].Len;
            int totalBeats = (endSlice + beatSlices - 1) / beatSlices;
            var cells = new string[totalBeats];
            var bpos = new string[totalBeats];
            var secArr = new string[totalBeats];
            for (int bt = 0; bt < totalBeats; bt++)
            {
                int bs = bt * beatSlices, be = bs + beatSlices;
                var toks = new List<string>();
                foreach (var e in ev) if (e[0] >= bs && e[0] < be) toks.Add((e[2] == 1 ? "r" : "") + MusicMathV2.DurBucket(e[1]));
                cells[bt] = toks.Count > 0 ? string.Join("+", toks) : "-";   // "-" = a held note OR a long rest continuing
                int bar = Math.Min(totalBars - 1, barOf(bs));
                bpos[bt] = (barSlices > 0 ? (bs - barStarts[bar]) / beatSlices : 0).ToString();
                secArr[bt] = roleOf(bar);
            }
            for (int bt = 0; bt < totalBeats; bt++)
            {
                string c1 = bt >= 1 ? cells[bt - 1] : "^";
                string c2 = bt >= 2 ? cells[bt - 2] : "^";
                string bp = bpos[bt];
                string sec = secArr[bt];
                var hist = MusicMathV2.Hist(order, k => bt - 1 - k >= 0 ? cells[bt - 1 - k] : "^");   // hist[0]=c1, hist[1]=c2…
                var lad = MusicMathV2.BuildLadder("c", hist, order, 1, "bp", bp,
                    new[] { "sec|bp|c1", "c2|c1", "sec|c1", "c1", "sec|bp", "bp", "" },
                    new[] { sec + "|" + bp + "|" + c1, c2 + "|" + c1, sec + "|" + c1, c1, sec + "|" + bp, bp, "" });
                Accum(cellModel, lad.Labels, lad.Ctx, cells[bt], 1);
            }
        }

        static void AccumTexture(CorpusModelV2 model, List<Ev> accomp, List<int> barStarts, Func<int, int> barEnd,
                                 int totalBars, double beatsPerBar, Func<int, string> roleOf)
        {
            string prevMotif = null, prev2Motif = "^", prev3Motif = "^", prev4Motif = "^";
            for (int b = 0; b < totalBars; b++)
            {
                int bs = barStarts[b], be = barEnd(b);
                var onsets = accomp.Where(e => e.Start >= bs && e.Start < be).OrderBy(e => e.Start).ToList();
                string motif = MotifType(onsets);
                int count = onsets.Count;
                double dens = count / Math.Max(1.0, beatsPerBar);
                string densCls = dens < 1.0 ? "sparse" : (dens < 2.5 ? "med" : "dense");
                string regCls = count > 0 ? MusicMathV2.RegisterBucket(Median(onsets.Select(e => e.Pitch))) : "na";

                Bump(model.TextureDensity, densCls, 1);
                Bump(model.TextureRegister, regCls, 1);
                Accum(model.Texture, new[] { "section", "" }, new[] { roleOf(b), "" },
                      motif + "|" + densCls + "|" + regCls, 1);
                string m1 = prevMotif ?? "^";
                Accum(model.ArtAccomp, new[] { "m4|m3|m2|m1", "m3|m2|m1", "m2|m1", "m1", "" },
                      new[] { prev4Motif + "|" + prev3Motif + "|" + prev2Motif + "|" + m1, prev3Motif + "|" + prev2Motif + "|" + m1, prev2Motif + "|" + m1, m1, "" }, motif, 1);
                prev4Motif = prev3Motif; prev3Motif = prev2Motif; prev2Motif = m1; prevMotif = motif;
            }
        }

        static void AccumVoiceLeading(ModeModels modeM, List<Ev> mel, List<Ev> bass, int[] chordRoot, bool[] hasChord, Func<int, int> barOf)
        {
            if (bass.Count == 0) return;
            // bass pitch sounding at a given slice (last bass onset at or before t)
            Func<int, int> bassAt = t =>
            {
                int lo = 0, hi = bass.Count - 1, ans = 0;
                while (lo <= hi) { int mid = (lo + hi) / 2; if (bass[mid].Start <= t) { ans = mid; lo = mid + 1; } else hi = mid - 1; }
                return bass[ans].Pitch;
            };
            string prevMotion = "^";
            for (int i = 0; i < mel.Count - 1; i++)
            {
                int b0 = bassAt(mel[i].Start), b1 = bassAt(mel[i + 1].Start);
                int mD = MusicMathV2.Sign(mel[i + 1].Pitch - mel[i].Pitch);
                int bD = MusicMathV2.Sign(b1 - b0);
                string motion;
                if (mD == 0 || bD == 0) motion = "oblique";
                else if (mD == bD) motion = Math.Abs(mel[i + 1].Pitch - mel[i].Pitch) == Math.Abs(b1 - b0) ? "parallel" : "similar";
                else motion = "contrary";

                int r0 = barOf(mel[i].Start), r1 = barOf(mel[i + 1].Start);
                string rootMove = (hasChord[r0] && hasChord[r1] && chordRoot[r0] == chordRoot[r1]) ? "same" : "move";

                Accum(modeM.VoiceMotion, new[] { "rootMove|prev", "prev", "" },
                      new[] { rootMove + "|" + prevMotion, prevMotion, "" }, motion, 1);

                string ic = MusicMathV2.IntervalClass(mel[i].Pitch - b0);
                Accum(modeM.VoiceInterval, new[] { "" }, new[] { "" }, ic, 1);

                modeM.VoiceSteps += 1;
                if (motion == "parallel" && (ic == "3rd" || ic == "6th" || ic == "8ve+")) modeM.DoublingSteps += 1;
                prevMotion = motion;
            }
        }

        // percussion: instrument-class distribution + per-beat drum-class pattern (rhythm), by section
        static void AccumPercussion(CorpusModelV2 model, List<Ev> drumEv, List<int> barStarts, Func<int, int> barEnd,
                                    int totalBars, int beatSlices, Func<int, string> roleOf)
        {
            if (drumEv == null || drumEv.Count == 0) return;
            model.PiecesWithDrums++;
            foreach (var e in drumEv) Bump(model.PercInstruments, DrumClass(e.Pitch), Math.Max(1, e.Vel));
            for (int b = 0; b < totalBars; b++)
            {
                int bs = barStarts[b], be = barEnd(b);
                string sec = roleOf(b);
                int beat = 0;
                for (int b0 = bs; b0 < be; b0 += Math.Max(1, beatSlices), beat++)
                {
                    int b1 = b0 + beatSlices;
                    var classes = new SortedSet<string>();
                    foreach (var e in drumEv) if (e.Start >= b0 && e.Start < b1) classes.Add(DrumClass(e.Pitch));
                    string pat = classes.Count > 0 ? string.Join("+", classes) : "-";
                    Accum(model.PercOnset, new[] { "sec|beat", "beat", "" }, new[] { sec + "|" + beat, beat.ToString(), "" }, pat, 1);
                }
            }
        }

        // GM percussion key (channel 10) -> coarse class
        static string DrumClass(int note)
        {
            switch (note)
            {
                case 35: case 36: return "kick";
                case 38: case 40: return "snare"; case 37: return "rim";
                case 42: case 44: return "hat"; case 46: return "hatopen";
                case 49: case 52: case 55: case 57: return "crash"; case 51: case 53: case 59: return "ride";
                case 41: case 43: case 45: case 47: case 48: case 50: return "tom";
                case 54: return "tamb"; case 56: return "cowbell"; case 80: case 81: return "triangle";
                default: return "perc";
            }
        }

        // ---------- low-level helpers ----------
        static int Mod12(int x) { int r = x % 12; return r < 0 ? r + 12 : r; }

        static double WeightedMeanPitch(List<Ev> ev)
        {
            double sw = 0, s = 0;
            foreach (var e in ev) { sw += e.Len; s += (double)e.Pitch * e.Len; }
            return sw > 0 ? s / sw : 0;
        }

        // top (or bottom) note per onset, ordered by onset
        static List<Ev> Skyline(List<Ev> ev, bool top)
        {
            var byStart = new Dictionary<int, Ev>();
            foreach (var e in ev)
            {
                Ev cur;
                if (!byStart.TryGetValue(e.Start, out cur)) byStart[e.Start] = e;
                else if (top ? e.Pitch > cur.Pitch : e.Pitch < cur.Pitch) byStart[e.Start] = e;
            }
            return byStart.Values.OrderBy(e => e.Start).ToList();
        }

        // ACCOMPANIMENT pitch RELATIVE to the chord: each skyline onset → "<chord-tone index>@<octave band>"
        // (root=0, 3rd=1, …; "x" = a non-chord/passing tone), chained order 3 on the chord FUNCTION so the learned
        // figure can be re-voiced onto whatever chord is current at generation.
        static void AccumAccompTone(CorpusModelV2 model, List<Ev> acc, int[] chordRoot, HashSet<int>[] chordTones,
                                    bool[] hasChord, Func<int, int> barOf, int barSlices, int beatSlices)
        {
            // Modelled LIKE the melody: configurable order (default 8) + a back-off ladder conditioned on the chord
            // (exact degree + function) and the metric position — not just the chord function at order 3.
            int order = MusicMathV2.Order(model.Orders, "accompTone", 8);
            var ah = new List<string>();   // ah[0] = a1 (most recent), ah[1] = a2, …
            foreach (var e in acc)
            {
                int bar = barOf(e.Start);
                if (bar < 0 || bar >= hasChord.Length || !hasChord[bar]) { ah.Insert(0, "^"); continue; }
                string tok = AccompToneToken(e, chordRoot[bar], chordTones[bar]);
                string func = MusicMathV2.ChordFunction(chordRoot[bar]);
                string cdeg = chordRoot[bar].ToString();
                string metric = (beatSlices > 0 && (e.Start % barSlices) % (2 * beatSlices) == 0) ? "S" : "w";
                string a1 = ah.Count > 0 ? ah[0] : "^";
                string a2 = ah.Count > 1 ? ah[1] : "^";
                var hist = MusicMathV2.Hist(order, k => k < ah.Count ? ah[k] : "^");
                var lad = MusicMathV2.BuildLadder("a", hist, order, 3, null, null,
                    new[] { "a1|cdeg|metric", "a2|a1|metric", "a1|func|metric", "a1|cdeg", "a1|metric", "a1", "" },
                    new[] { a1 + "|" + cdeg + "|" + metric, a2 + "|" + a1 + "|" + metric, a1 + "|" + func + "|" + metric, a1 + "|" + cdeg, a1 + "|" + metric, a1, "" });
                Accum(model.AccompTone, lad.Labels, lad.Ctx, tok, 1);
                ah.Insert(0, tok);
            }
        }

        static string AccompToneToken(Ev e, int root, HashSet<int> tones)
        {
            int oct = Math.Max(0, Math.Min(2, (e.Pitch - 48) / 12));    // octave band over a C3 floor
            if (tones == null || !tones.Contains(e.Deg)) return "x@" + oct;
            var ordered = new List<int>(tones);
            ordered.Sort((x, y) => Mod12(x - root).CompareTo(Mod12(y - root)));   // root, 3rd, 5th, 7th…
            int idx = ordered.IndexOf(e.Deg);
            return (idx < 0 ? 0 : idx) + "@" + oct;
        }

        static string SectionRole(int bar, int total)
        {
            if (total <= 1) return "body";
            double p = bar / (double)total;
            if (p < 0.12) return "intro";
            if (p < 0.50) return "body";
            if (p < 0.78) return "climax";
            return "outro";
        }

        // Per-bar section role from REAL melodic activity (not a bar-fraction guess):
        //   • INTRO  = bars before the theme enters (little/no melody — a lead-in);
        //   • THEME  = the opening SUSTAINED statement (first bar of the first dense 4-bar phrase, a few bars);
        //   • CLIMAX = the highest-energy bar (length × register) in the post-theme region, ±1 bar;
        //   • OUTRO  = trailing bars after the last melody note (cadential tail);
        //   • BODY   = everything else.
        // THEME entry mirrors AutoThemeStartSlices (first onset with ≥6 melody onsets in the next 4 bars).
        static string[] ComputeBarRoles(List<Ev> mel, int totalBars, int barSlices, Func<int, int> barOf)
        {
            var roles = new string[Math.Max(1, totalBars)];
            for (int i = 0; i < roles.Length; i++) roles[i] = "body";
            if (totalBars <= 0 || mel == null || mel.Count == 0) return roles;

            var energy = new double[totalBars];
            var onsets = new int[totalBars];
            var hi = new int[totalBars];
            foreach (var e in mel)
            {
                int b = barOf(e.Start);
                if (b < 0 || b >= totalBars) continue;
                energy[b] += e.Len; onsets[b]++; if (e.Pitch > hi[b]) hi[b] = e.Pitch;
            }

            // THEME start = first bar of the first sustained phrase (≥6 onsets within 4 bars). Pre-theme = INTRO.
            var ord = mel.Select(e => e.Start).OrderBy(x => x).ToList();
            int win = 4 * barSlices, themeOnset = ord[0];
            foreach (int o in ord) { int c = 0; foreach (var e in mel) if (e.Start >= o && e.Start < o + win) c++; if (c >= 6) { themeOnset = o; break; } }
            int themeStart = Math.Max(0, Math.Min(totalBars - 1, barOf(themeOnset)));

            // OUTRO start = just past the last bar carrying a melody onset.
            int lastMel = totalBars - 1; while (lastMel > 0 && onsets[lastMel] == 0) lastMel--;
            int outroStart = Math.Min(totalBars, lastMel + 1);

            // THEME length = the opening statement: 2..8 bars, capped to a third of the active span.
            int activeBars = Math.Max(1, outroStart - themeStart);
            int themeLen = Math.Max(2, Math.Min(8, activeBars / 3));
            int themeEnd = Math.Min(outroStart, themeStart + themeLen);

            // CLIMAX = peak energy (note length × register lift) in the post-theme active region.
            int climaxCenter = -1; double bestE = -1;
            for (int b = themeEnd; b < outroStart; b++)
            {
                double sc = energy[b] * (1.0 + Math.Max(0, hi[b] - 60) / 24.0);
                if (sc > bestE) { bestE = sc; climaxCenter = b; }
            }

            for (int b = 0; b < totalBars; b++)
            {
                if (b < themeStart) roles[b] = "intro";
                else if (b < themeEnd) roles[b] = "theme";
                else if (b >= outroStart) roles[b] = "outro";
                else if (climaxCenter >= 0 && Math.Abs(b - climaxCenter) <= 1) roles[b] = "climax";
                else roles[b] = "body";
            }
            return roles;
        }

        static string PhraseLenBucket(double beats)
        {
            if (beats < 2.5) return "2";
            if (beats < 5) return "4";
            if (beats < 7) return "6";
            if (beats < 10) return "8";
            return "long";
        }

        static string OffsetClass(int off)
        {
            switch (Mod12(off))
            {
                case 0: return "home";
                case 9: return "rel-maj";   // minor -> relative major (up m3 => tonic +9? for minor home rel maj is +3) ; kept coarse
                case 3: return "+m3";
                case 7: return "dom";
                case 5: return "subdom";
                case 2: return "+M2";
                case 10: return "-M2";
                default: return "other";
            }
        }

        static string NctType(List<Ev> mel, int i, bool isCt)
        {
            if (isCt) return "chord";
            if (i == 0 || i == mel.Count - 1) return "edge";
            int prev = mel[i - 1].Pitch, cur = mel[i].Pitch, next = mel[i + 1].Pitch;
            bool stepIn = Math.Abs(cur - prev) <= 2, stepOut = Math.Abs(next - cur) <= 2;
            int sIn = MusicMathV2.Sign(cur - prev), sOut = MusicMathV2.Sign(next - cur);
            if (prev == cur && stepOut && sOut < 0) return "suspension";
            if (stepIn && stepOut && sIn == sOut && sIn != 0) return "passing";
            if (stepIn && stepOut && sIn != sOut) return "neighbor";
            if (!stepIn && stepOut) return "appoggiatura";
            return "other";
        }

        static string MotifType(List<Ev> onsets)
        {
            int count = onsets.Count;
            if (count == 0) return "rest";
            if (count == 1) return "sustain";
            // share of onsets that coincide in time with another (block chords)
            var starts = onsets.Select(e => e.Start).ToList();
            int simul = 0;
            var grp = onsets.GroupBy(e => e.Start);
            foreach (var g in grp) { int c = g.Count(); if (c > 1) simul += c; }
            double simulShare = simul / (double)count;
            if (simulShare > 0.5) return "block";
            // contour of distinct onset pitches
            var pitches = grp.OrderBy(g => g.Key).Select(g => g.First().Pitch).ToList();
            int up = 0, down = 0, rev = 0; int lastDir = 0;
            for (int i = 1; i < pitches.Count; i++)
            {
                int d = MusicMathV2.Sign(pitches[i] - pitches[i - 1]);
                if (d > 0) up++; else if (d < 0) down++;
                if (d != 0 && lastDir != 0 && d != lastDir) rev++;
                if (d != 0) lastDir = d;
            }
            int moves = Math.Max(1, pitches.Count - 1);
            if (rev / (double)moves > 0.6) return "alberti";   // many direction changes => zig-zag
            if (up > 0 && down == 0) return "arp-up";
            if (down > 0 && up == 0) return "arp-down";
            return "broken";
        }

        static int Median(IEnumerable<int> xs)
        {
            var l = xs.OrderBy(x => x).ToList();
            if (l.Count == 0) return 0;
            return l[l.Count / 2];
        }

        static double VelocityVariance(List<Ev> mel)
        {
            if (mel.Count < 2) return 0;
            double mean = mel.Average(e => e.Vel);
            double var = mel.Average(e => (e.Vel - mean) * (e.Vel - mean));
            return Math.Sqrt(var);
        }

        static string VelBucket(int v)
        {
            if (v < 40) return "pp";
            if (v < 56) return "p";
            if (v < 72) return "mp";
            if (v < 88) return "mf";
            if (v < 104) return "f";
            return "ff";
        }

        static string TempoBucket(double bpm)
        {
            if (bpm <= 0) return "na";
            if (bpm < 66) return "<66";
            if (bpm < 76) return "66-76";
            if (bpm < 86) return "76-86";
            if (bpm < 96) return "86-96";
            if (bpm < 112) return "96-112";
            return "112+";
        }

        // =====================================================================
        // human-readable report
        // =====================================================================
        static string BuildReport(CorpusModelV2 m)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# Ghibli Composer V2 — rapport d'analyse du corpus\n");
            sb.AppendLine($"- Fichiers analysés : **{m.FilesAnalyzed}** (majeur {m.MajorPieces} / mineur {m.MinorPieces})");
            sb.AppendLine($"- Ignorés : {m.Skipped.Count}");
            sb.AppendLine($"- Slices/noire : {m.SlicesPerQuarter}\n");

            sb.AppendLine("## Modes (contexte racine)");
            sb.AppendLine(HistLine(m.ModeDistribution));
            sb.AppendLine("\n## Caractère mélodique (contexte racine)");
            sb.AppendLine(HistLine(m.CharacterDistribution));
            sb.AppendLine("\n## Tempo");
            sb.AppendLine(HistLine(m.TempoHistogram));
            sb.AppendLine("\n## Mesure");
            sb.AppendLine(HistLine(m.MeterHistogram));
            sb.AppendLine("\n## Types de notes (accord vs hors-accord)");
            sb.AppendLine(HistLine(m.NctTypes));

            sb.AppendLine("\n## Mélodie — histogramme de degrés (mineur)");
            sb.AppendLine(HistLine(Norm(m.Minor.DegreeHistogram), DegOrder()));
            sb.AppendLine("\n## Mélodie — histogramme de degrés (majeur)");
            sb.AppendLine(HistLine(Norm(m.Major.DegreeHistogram), DegOrder()));

            sb.AppendLine($"\n- % notes d'accord (mineur) : {Pct(m.Minor.ChordTones, m.Minor.MelodyNotes)} | (majeur) : {Pct(m.Major.ChordTones, m.Major.MelodyNotes)}");
            sb.AppendLine($"- Taux de doublure parallèle (mineur) : {Pct(m.Minor.DoublingSteps, m.Minor.VoiceSteps)} | (majeur) : {Pct(m.Major.DoublingSteps, m.Major.VoiceSteps)}");

            sb.AppendLine("\n## Harmonie — transitions de fondamentale les plus fréquentes (mineur, ordre 1)");
            sb.AppendLine(TopTransitions(m.Minor.HarmonyRoot, 1, 14, true));
            sb.AppendLine("\n## Harmonie — transitions de fondamentale les plus fréquentes (majeur, ordre 1)");
            sb.AppendLine(TopTransitions(m.Major.HarmonyRoot, 1, 14, true));
            sb.AppendLine("\n## Qualité par degré (mineur)");
            sb.AppendLine(TopByContext(m.Minor.QualityByDegree, 0, true));
            sb.AppendLine("\n## Qualité par degré (majeur)");
            sb.AppendLine(TopByContext(m.Major.QualityByDegree, 0, true));

            sb.AppendLine("\n## Rythme harmonique (durée d'accord, marginale)");
            sb.AppendLine(MarginalOfLastTier(m.HarmonicRhythm));

            sb.AppendLine("\n## Cellules rythmiques — temps suivant (cellule précédente → cellules les plus fréquentes)");
            sb.AppendLine(CellTransitions(m.RhythmCell, "c1", new[] { "8+8", "q", "q.", "16+16+16+16", "8+16+16", "8", "8.+16", "-" }));
            sb.AppendLine("## Cellules rythmiques — contexte 2 temps (deux temps précédents → suivant)");
            sb.AppendLine(CellTransitions(m.RhythmCell, "c2|c1", new[] { "q.|8", "8+8|8+8", "q|q", "16+16+16+16|16+16+16+16", "q.|16+16" }));
            sb.AppendLine("## Cellules rythmiques — SILENCES (cellule → temps suivant ; r8=silence de croche, rq=silence de noire)");
            sb.AppendLine(CellTransitions(m.RhythmCell, "c1", new[] { "rq", "rh", "r8+8", "8+r8", "rq.", "r8" }));
            sb.AppendLine("Cellules contenant un silence (marginale) : " + TopCellsWithRest(m.RhythmCell));
            sb.AppendLine("\n## Degrés de cadence — fin de phrase (mineur)");
            sb.AppendLine(TopByContext(m.Minor.Cadence, 0, true));
            sb.AppendLine("\n## Degrés de cadence — fin de phrase (majeur)");
            sb.AppendLine(TopByContext(m.Major.Cadence, 0, true));

            sb.AppendLine("\n## Texture / accompagnement — figures (marginale ArtAccomp)");
            sb.AppendLine(MarginalOfLastTier(m.ArtAccomp));
            sb.AppendLine("Densité : " + HistLineInline(m.TextureDensity));
            sb.AppendLine("Registre : " + HistLineInline(m.TextureRegister));
            sb.AppendLine("\n## Renversements par fonction d'accord (root / inv1=3ce basse / inv2=5te basse / slash)");
            sb.AppendLine(TopByContext(m.Inversion, 0, false));
            sb.AppendLine($"\n## Percussion — {m.PiecesWithDrums}/{m.FilesAnalyzed} pièces avec batterie");
            sb.AppendLine("Instruments : " + HistLineInline(m.PercInstruments));

            sb.AppendLine("\n## Conduite des voix — mouvement (mineur, marginale)");
            sb.AppendLine(MarginalOfLastTier(m.Minor.VoiceMotion));
            sb.AppendLine("Intervalle mél/basse (mineur) : " + HistLineInline(LastTierAll(m.Minor.VoiceInterval)));

            sb.AppendLine("\n## Tonalité / modulation (mineur, marginale des offsets)");
            sb.AppendLine(MarginalOfLastTier(m.Minor.Tonality));

            sb.AppendLine("\n## Forme — longueur de phrase (marginale)");
            sb.AppendLine(MarginalOfLastTier(m.PhraseLength));

            if (m.Skipped.Count > 0)
            {
                sb.AppendLine("\n## Fichiers ignorés");
                foreach (var s in m.Skipped) sb.AppendLine("- " + s);
            }

            sb.AppendLine("\n## Détail par fichier");
            sb.AppendLine("| fichier | tonique | mode | caractère | KS | bpm | mesure | barres | notes mél | vélocité |");
            sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|");
            foreach (var p in m.Pieces)
                sb.AppendLine($"| {p.File} | {RootName(p.TonicPc)} | {(p.Minor ? "min" : "maj")} | {p.Character} | {p.KeyScore} | {p.Bpm} | {p.Meter} | {p.Bars} | {p.MelodyNotes} | {(p.VelocityUsable ? "oui" : "non")} |");

            return sb.ToString();
        }

        static readonly string[] RootNames = { "Do", "Do#", "Re", "Re#", "Mi", "Fa", "Fa#", "Sol", "Sol#", "La", "La#", "Si" };
        static string RootName(int pc) { return RootNames[Mod12(pc)]; }
        static string[] DegOrder() { return MusicMathV2.DegName; }

        static Dictionary<string, double> Norm(Dictionary<string, double> d)
        {
            double s = d.Values.Sum(); if (s <= 0) return d;
            var o = new Dictionary<string, double>();
            foreach (var kv in d) o[kv.Key] = kv.Value / s;
            return o;
        }
        static string Pct(double a, double b) { return b > 0 ? Math.Round(100.0 * a / b, 1) + "%" : "n/a"; }

        static string HistLine(Dictionary<string, double> d) { return HistLine(d, null); }
        static string HistLine(Dictionary<string, double> d, string[] order)
        {
            if (d == null || d.Count == 0) return "(vide)";
            double s = d.Values.Sum(); if (s <= 0) s = 1;
            IEnumerable<KeyValuePair<string, double>> items = order != null
                ? order.Where(d.ContainsKey).Select(k => new KeyValuePair<string, double>(k, d[k]))
                : d.OrderByDescending(kv => kv.Value);
            return string.Join("  ", items.Select(kv => $"{kv.Key}={Math.Round(100 * kv.Value / s, 1)}%"));
        }
        static string HistLineInline(Dictionary<string, double> d) { return HistLine(d, null); }

        // marginal = the LAST tier (empty context) of a CondModel
        static Dictionary<string, double> LastTierAll(CondModel m)
        {
            var o = new Dictionary<string, double>();
            if (m.Tiers.Count == 0) return o;
            var last = m.Tiers[m.Tiers.Count - 1].Table;
            foreach (var kv in last) foreach (var st in kv.Value) Bump(o, st.Key, st.Value);
            return o;
        }
        static string MarginalOfLastTier(CondModel m) { return HistLine(LastTierAll(m)); }

        // top next-states for each context of a given tier
        static string TopByContext(CondModel m, int tier, bool degKeys)
        {
            if (m.Tiers.Count <= tier) return "(vide)";
            var sb = new StringBuilder();
            foreach (var kv in m.Tiers[tier].Table.OrderBy(k => SortKey(k.Key)))
            {
                double s = kv.Value.Values.Sum(); if (s <= 0) continue;
                string ctx = degKeys ? DegLabel(kv.Key) : kv.Key;
                var tops = kv.Value.OrderByDescending(x => x.Value).Take(4)
                                   .Select(x => $"{(degKeys && IsInt(x.Key) ? DegLabel(x.Key) : x.Key)} {Math.Round(100 * x.Value / s)}%");
                sb.AppendLine($"- **{ctx}** → " + string.Join(", ", tops));
            }
            return sb.ToString();
        }

        static string TopTransitions(CondModel m, int tier, int n, bool degKeys)
        {
            if (m.Tiers.Count <= tier) return "(vide)";
            var rows = new List<KeyValuePair<string, double>>();
            foreach (var kv in m.Tiers[tier].Table)
                foreach (var st in kv.Value)
                    rows.Add(new KeyValuePair<string, double>(kv.Key + " → " + (degKeys ? DegLabel(st.Key) : st.Key), st.Value));
            double tot = rows.Sum(r => r.Value); if (tot <= 0) tot = 1;
            return string.Join("\n", rows.OrderByDescending(r => r.Value).Take(n)
                .Select(r => $"- {(degKeys ? DegLabel(r.Key.Split(' ')[0]) + r.Key.Substring(r.Key.IndexOf(' ')) : r.Key)} : {Math.Round(100 * r.Value / tot, 1)}%"));
        }

        // top next-cells for a curated set of previous-cell contexts (for the report — matches the user's examples).
        // The tier is found by its CONTEXT LABEL (e.g. "c1", "c2|c1") so it survives back-off-ladder changes.
        static string CellTransitions(CondModel m, string tierLabel, string[] contexts)
        {
            int tier = -1;
            for (int i = 0; i < m.Tiers.Count; i++) if (m.Tiers[i].Context == tierLabel) { tier = i; break; }
            if (tier < 0) return "(vide)";
            var sb = new StringBuilder();
            foreach (var c in contexts)
            {
                Dictionary<string, double> counts;
                if (!m.Tiers[tier].Table.TryGetValue(c, out counts) || counts.Count == 0) continue;
                double s = counts.Values.Sum(); if (s <= 0) continue;
                var tops = counts.OrderByDescending(x => x.Value).Take(6).Select(x => $"{x.Key} {Math.Round(100 * x.Value / s)}%");
                sb.AppendLine($"- **{c}** → " + string.Join(", ", tops));
            }
            return sb.Length == 0 ? "(aucune)" : sb.ToString();
        }

        // marginal cell distribution restricted to cells that contain a rest token (for the report)
        static string TopCellsWithRest(CondModel m)
        {
            var all = LastTierAll(m);
            double tot = 0; foreach (var v in all.Values) tot += v;
            if (tot <= 0) return "(aucune)";
            var rest = all.Where(kv => kv.Key.Contains("r")).OrderByDescending(kv => kv.Value).Take(8).ToList();
            if (rest.Count == 0) return "(aucune)";
            return string.Join("  ", rest.Select(kv => $"{kv.Key}={Math.Round(100 * kv.Value / tot, 1)}%"));
        }

        static bool IsInt(string s) { int x; return int.TryParse(s, out x); }
        static int SortKey(string s) { int x; return int.TryParse(s, out x) ? x : 999; }
        static string DegLabel(string s)
        {
            int x;
            if (int.TryParse(s, out x) && x >= 0 && x < 12) return MusicMathV2.DegName[x];
            return s;
        }
    }

    /// <summary>
    /// Tiny dependency-free JSON writer (System.Text.Json fails to initialize under Windows
    /// PowerShell 5.1 without binding redirects). Handles primitives, strings, IDictionary,
    /// IEnumerable and public readable properties — enough for the model graph (no cycles).
    /// </summary>
    internal static class MiniJson
    {
        public static string Serialize(object o)
        {
            var sb = new StringBuilder();
            Write(sb, o, 0);
            return sb.ToString();
        }

        static void Indent(StringBuilder sb, int d) { sb.Append('\n'); for (int i = 0; i < d; i++) sb.Append("  "); }

        static void Write(StringBuilder sb, object o, int d)
        {
            if (o == null) { sb.Append("null"); return; }
            if (o is string s) { WriteString(sb, s); return; }
            if (o is bool b) { sb.Append(b ? "true" : "false"); return; }
            if (o is int || o is long || o is short || o is byte)
            { sb.Append(Convert.ToInt64(o).ToString(CultureInfo.InvariantCulture)); return; }
            if (o is double || o is float || o is decimal)
            {
                double dv = Convert.ToDouble(o, CultureInfo.InvariantCulture);
                if (double.IsNaN(dv) || double.IsInfinity(dv)) { sb.Append("0"); return; }
                sb.Append(dv.ToString("R", CultureInfo.InvariantCulture));
                return;
            }
            if (o is IDictionary dict)
            {
                sb.Append('{');
                bool first = true;
                foreach (DictionaryEntry kv in dict)
                {
                    if (!first) sb.Append(','); first = false;
                    Indent(sb, d + 1);
                    WriteString(sb, Convert.ToString(kv.Key, CultureInfo.InvariantCulture));
                    sb.Append(": ");
                    Write(sb, kv.Value, d + 1);
                }
                if (!first) Indent(sb, d);
                sb.Append('}');
                return;
            }
            if (o is IEnumerable en)
            {
                sb.Append('[');
                bool first = true;
                foreach (var item in en)
                {
                    if (!first) sb.Append(','); first = false;
                    Indent(sb, d + 1);
                    Write(sb, item, d + 1);
                }
                if (!first) Indent(sb, d);
                sb.Append(']');
                return;
            }
            // plain object -> public readable instance properties
            var props = o.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
                         .Where(p => p.CanRead && p.GetIndexParameters().Length == 0);
            sb.Append('{');
            bool f2 = true;
            foreach (var p in props)
            {
                object val;
                try { val = p.GetValue(o); } catch { continue; }
                if (!f2) sb.Append(','); f2 = false;
                Indent(sb, d + 1);
                WriteString(sb, p.Name);
                sb.Append(": ");
                Write(sb, val, d + 1);
            }
            if (!f2) Indent(sb, d);
            sb.Append('}');
        }

        static void WriteString(StringBuilder sb, string s)
        {
            if (s == null) { sb.Append("null"); return; }
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }
    }
}
