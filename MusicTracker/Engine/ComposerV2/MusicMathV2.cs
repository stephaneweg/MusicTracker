using System;
using System.Collections.Generic;
using System.Text;

namespace MusicTracker.Engine.ComposerV2
{
    /// <summary>
    /// Composer V2 — self-contained music math for the offline corpus analyzer (and, later, the
    /// generator). Everything here keeps the analysis *key-invariant* and *abstract*: scale degrees
    /// (never absolute pitch), interval classes, duration classes, register classes. No dependency on
    /// the existing composer engine — only plain integers in / classes out.
    /// Grid: 24 slices per quarter note (matches the importers); 96 slices = one whole note.
    /// </summary>
    internal static class MusicMathV2
    {
        public const int SlicesPerQuarter = 24;
        public const int WholeSlices = 96; // 4 quarters

        // ===== configurable-order back-off ladders (shared by the analyzer + the generator so the tier
        //       structure stays identical → Dist's index-aligned back-off works) =====
        public struct Ladder { public string[] Labels; public string[] Ctx; }

        /// <summary>Resolve a per-dimension order from the model's Orders map (clamped ≥1), else the default.</summary>
        public static int Order(Dictionary<string, int> orders, string key, int dflt)
        {
            int v;
            if (orders != null && orders.TryGetValue(key, out v) && v >= 1) return v;
            return dflt;
        }

        /// <summary>Build a back-off ladder = PURE n-gram tiers for orders [order..floor] (token tag + optional
        /// suffix), followed by the FIXED contextual tiers appended verbatim. <paramref name="hist"/>[0] is the
        /// most-recent token x1 … hist[order-1] = x{order}. Used identically on both analysis and generation sides.</summary>
        public static Ladder BuildLadder(string tag, string[] hist, int order, int floor,
                                         string sfxLabel, string sfxVal, string[] fixedLabels, string[] fixedCtx)
        {
            var L = new List<string>(); var C = new List<string>();
            for (int k = order; k >= floor; k--)
            {
                var l = new StringBuilder(); var c = new StringBuilder();
                for (int j = k; j >= 1; j--)
                {
                    if (j < k) { l.Append('|'); c.Append('|'); }
                    l.Append(tag).Append(j);
                    c.Append(j - 1 < hist.Length ? hist[j - 1] : "^");
                }
                if (!string.IsNullOrEmpty(sfxLabel)) { l.Append('|').Append(sfxLabel); c.Append('|').Append(sfxVal); }
                L.Add(l.ToString()); C.Add(c.ToString());
            }
            if (fixedLabels != null) L.AddRange(fixedLabels);
            if (fixedCtx != null) C.AddRange(fixedCtx);
            return new Ladder { Labels = L.ToArray(), Ctx = C.ToArray() };
        }

        /// <summary>The most-recent <paramref name="order"/> history tokens as x1..x{order} (x1 most recent),
        /// "^" past the start. <paramref name="valueAt"/>(d) returns the token d steps back (d=0 = current/x1).</summary>
        public static string[] Hist(int order, Func<int, string> valueAt)
        {
            var h = new string[Math.Max(1, order)];
            for (int j = 0; j < h.Length; j++) h[j] = valueAt(j);
            return h;
        }

        // Diatonic collections relative to the tonic (degree 0).
        public static readonly int[] MajorScale = { 0, 2, 4, 5, 7, 9, 11 }; // Ionian
        public static readonly int[] MinorScale = { 0, 2, 3, 5, 7, 8, 10 }; // natural minor / Aeolian
        public static readonly int[] DorianScale = { 0, 2, 3, 5, 7, 9, 10 };
        public static readonly int[] PhrygianScale = { 0, 1, 3, 5, 7, 8, 10 };
        public static readonly int[] LydianScale = { 0, 2, 4, 6, 7, 9, 11 };
        public static readonly int[] MixolydianScale = { 0, 2, 4, 5, 7, 9, 10 };
        public static readonly int[] LocrianScale = { 0, 1, 3, 5, 6, 8, 10 };

        public static int[] ScaleForMode(string m)
        {
            switch (m)
            {
                case "ionian": return MajorScale; case "dorian": return DorianScale; case "phrygian": return PhrygianScale;
                case "lydian": return LydianScale; case "mixolydian": return MixolydianScale; case "locrian": return LocrianScale;
                default: return MinorScale; // aeolian
            }
        }
        // a mode with a minor 3rd uses the minor-mode model/chains
        public static bool IsMinorMode(string m) { return m == "aeolian" || m == "dorian" || m == "phrygian" || m == "locrian"; }

        // classify the church MODE from a tonic-relative pitch-class histogram (wRel[degree] = weight)
        public static string DetectMode(double[] wRel)
        {
            double W(int d) { return wRel[Mod12(d)]; }
            bool maj3 = W(4) >= W(3);                            // natural 3 vs b3
            if (maj3)
            {
                if (W(6) > 0 && W(6) >= 0.5 * W(5)) return "lydian";        // #4 notably present (loosened: colour, not dominating)
                if (W(10) > 0 && W(10) >= 0.6 * W(11)) return "mixolydian"; // b7 notably present
                return "ionian";
            }
            if (W(1) > W(2)) return "phrygian";  // b2 over natural 2 (strict: b2 is a common borrowed tone in minor)
            if (W(9) > W(8)) return "dorian";    // natural 6 over b6 (strict)
            return "aeolian";
        }

        // classify the melody's overall CHARACTER (a root context, like the mode):
        //   "majestueux" (noble/march) / "enjouée" (lively) / "modérée" (moderate) / "calme" (calm).
        // Signals: notesPerSec (event rate = tempo×density), shortShare (≤eighth = vivacity),
        // longShare (≥dotted-quarter = breadth), bpm, and whether the piece is major.
        //   • MAJESTIC/NOBLE = MAJOR + broad (many long notes, few short) + moderate march tempo, not lively.
        //   • otherwise bucket the "vivacity" composite into calme / modérée / enjouée.
        public static string MelodyCharacter(double notesPerSec, double shortShare, double longShare, double bpm, bool major)
        {
            double speed = Clamp01((notesPerSec - 1.5) / (5.5 - 1.5)); // 1.5 ev/s → 0, 5.5 → 1
            double rhythm = Clamp01(shortShare);                       // already 0..1
            double tempo = Clamp01((bpm - 60.0) / (130.0 - 60.0));     // 60 bpm → 0, 130 → 1
            double vivacity = 0.45 * speed + 0.30 * rhythm + 0.25 * tempo;

            if (major && longShare >= 0.30 && shortShare < 0.40 && bpm >= 66 && bpm <= 120 && vivacity < 0.6)
                return "majestueux";                                   // broad, stately, march-like major
            if (vivacity < 0.34) return "calme";
            if (vivacity < 0.62) return "modérée";
            return "enjouée";
        }
        static double Clamp01(double x) { return x < 0 ? 0 : (x > 1 ? 1 : x); }

        // Chromatic degree names relative to the tonic (Do = 1).
        public static readonly string[] DegName =
            { "1", "b2", "2", "b3", "3", "4", "#4", "5", "b6", "6", "b7", "7" };

        public static int Mod12(int x) { int r = x % 12; return r < 0 ? r + 12 : r; }
        public static int Mod(int x, int m) { if (m <= 0) return 0; int r = x % m; return r < 0 ? r + m : r; }
        public static int Sign(int x) { return x > 0 ? 1 : (x < 0 ? -1 : 0); }

        public static int SlicesPerBar(int num, int den) { return Math.Max(1, num) * WholeSlices / Math.Max(1, den); }
        public static int BeatSlices(int den) { return WholeSlices / Math.Max(1, den); }

        // ---- abstract state: duration classes (note durations, in slices) ----
        static readonly int[] DurSteps = { 6, 12, 18, 24, 36, 48, 72, 96 };
        static readonly string[] DurLabels = { "16", "8", "8.", "q", "q.", "h", "h.", "w" };
        public static string DurBucket(int slices)
        {
            if (slices <= 0) return "0";
            if (slices > 120) return "w+";
            int best = 0, bestd = int.MaxValue;
            for (int i = 0; i < DurSteps.Length; i++)
            {
                int d = Math.Abs(DurSteps[i] - slices);
                if (d < bestd) { bestd = d; best = i; }
            }
            return DurLabels[best];
        }

        // ---- abstract state: harmonic-rhythm class (chord duration, in bars) ----
        public static string DurBucketBars(double bars)
        {
            if (bars < 0.75) return "<1";
            if (bars < 1.5) return "1";
            if (bars < 2.5) return "2";
            if (bars < 4.5) return "3-4";
            return "5+";
        }

        // ---- abstract state: unsigned interval classes ----
        public static string IntervalClass(int semitones)
        {
            int a = Math.Abs(semitones);
            if (a == 0) return "U";
            if (a <= 2) return "step";
            if (a <= 4) return "3rd";
            if (a <= 5) return "P4";
            if (a <= 7) return "P5";
            if (a <= 9) return "6th";
            if (a <= 11) return "7th";
            return "8ve+";
        }
        public static bool IsLeap(int semitones) { return Math.Abs(semitones) > 2; }

        // Signed interval class: direction (+/-) + magnitude class. Keeps the melodic line's contour
        // (the unsigned IntervalClass loses direction). ~15 abstract states.
        public static string SignedIntervalClass(int semitones)
        {
            if (semitones == 0) return "U";
            string sign = semitones > 0 ? "+" : "-";
            int a = Math.Abs(semitones);
            string mag;
            if (a <= 2) mag = "step";
            else if (a <= 4) mag = "3rd";
            else if (a == 5) mag = "P4";
            else if (a <= 7) mag = "P5";
            else if (a <= 9) mag = "6th";
            else if (a <= 11) mag = "7th";
            else mag = "8ve";
            return sign + mag;
        }

        // ---- abstract state: register class (median MIDI pitch -> coarse band) ----
        public static string RegisterBucket(int midi)
        {
            if (midi < 48) return "low";     // < C3
            if (midi < 60) return "midlow";  // C3..B3
            if (midi < 72) return "mid";     // C4..B4
            if (midi < 84) return "high";    // C5..B5
            return "vhigh";
        }

        // ---- chord function abstraction (root scale-degree -> T/S/D/X) ----
        // Keeps the melody's harmonic context to 4 symbols instead of 12.
        public static string ChordFunction(int rootDeg)
        {
            switch (Mod12(rootDeg))
            {
                case 0: case 4: case 9: return "T";   // I, iii, vi
                case 5: case 2: return "S";           // IV, ii
                case 7: case 11: return "D";          // V, vii
                default: return "X";                  // borrowed / chromatic (bIII bVI bVII bII #IV ...)
            }
        }

        // Item 2 (Nierhaus ch.3, Chai & Vercoe VIEWPOINTS): melodic INTERVAL + CONTOUR computed in DEGREE space so the
        // analyzer, greedy generator, and Viterbi resolver all derive them identically from the degree sequence alone
        // (the resolver has no pitches). SignedIv = nearest chromatic interval fromDeg→toDeg in [-6,+5]; Contour = the
        // coarse 5-class direction (Chai & Vercoe): 0 same, +/- step (1-2 st), ++/-- leap (>=3 st).
        public static int SignedIv(int fromDeg, int toDeg) { int d = Mod12(toDeg - fromDeg); return d > 6 ? d - 12 : d; }
        public static string Contour(int iv) { if (iv == 0) return "0"; return System.Math.Abs(iv) <= 2 ? (iv > 0 ? "+" : "-") : (iv > 0 ? "++" : "--"); }

        // =====================================================================
        // Krumhansl-Schmuckler key finding on an EXACT symbolic pitch-class
        // histogram (same KK profiles the app uses in ScoreModel, so V2's tonic
        // matches the timeline). Returns tonic pitch-class + minor flag.
        // =====================================================================
        static readonly double[] KSMaj = { 6.35, 2.23, 3.48, 2.33, 4.38, 4.09, 2.52, 5.19, 2.39, 3.66, 2.29, 2.88 };
        static readonly double[] KSMin = { 6.33, 2.68, 3.52, 5.38, 2.60, 3.53, 2.54, 4.75, 3.98, 2.69, 3.34, 3.17 };

        static double CorrKS(double[] w, int tonic, bool minor)
        {
            var prof = minor ? KSMin : KSMaj;
            double mw = 0, mp = 0;
            for (int i = 0; i < 12; i++) { mw += w[i]; mp += prof[i]; }
            mw /= 12.0; mp /= 12.0;
            double num = 0, dw = 0, dp = 0;
            for (int i = 0; i < 12; i++)
            {
                double a = w[(i + tonic) % 12] - mw;
                double b = prof[i] - mp;
                num += a * b; dw += a * a; dp += b * b;
            }
            return (dw <= 0 || dp <= 0) ? -1 : num / Math.Sqrt(dw * dp);
        }

        public class KeyGuess { public int Tonic; public bool Minor; public double Score; }

        /// <summary>Best of the 24 major/minor keys by K-S correlation against the exact histogram.
        /// <paramref name="finalLowPc"/> (the piece's resting/lowest pitch class, or -1) gently breaks
        /// the relative major/minor tie since they share a collection.</summary>
        public static KeyGuess DetectKey(double[] w12, int finalLowPc)
        {
            var best = new KeyGuess { Tonic = 0, Minor = false, Score = -2 };
            for (int t = 0; t < 12; t++)
            {
                for (int m = 0; m < 2; m++)
                {
                    bool minor = m == 1;
                    double s = CorrKS(w12, t, minor);
                    if (finalLowPc >= 0 && finalLowPc == t) s += 0.05; // rests-on-tonic nudge
                    if (s > best.Score) { best.Score = s; best.Tonic = t; best.Minor = minor; }
                }
            }
            return best;
        }

        // =====================================================================
        // Chord detection: template match over a duration-weighted pitch-class
        // histogram (degrees relative to C after transposition). Coarse quality
        // GROUP is the abstract state; CanonIndex maps to PatternGenerator's
        // 0..22 quality table for later storage/generation.
        // =====================================================================
        public class ChordTemplate
        {
            public string Group; public int[] Iv; public int CanonIndex;
            public ChordTemplate(string g, int[] iv, int idx) { Group = g; Iv = iv; CanonIndex = idx; }
        }

        public static readonly ChordTemplate[] Templates =
        {
            new ChordTemplate("maj",   new[]{0,4,7},        0),
            new ChordTemplate("min",   new[]{0,3,7},        1),
            new ChordTemplate("dim",   new[]{0,3,6},        2),
            new ChordTemplate("aug",   new[]{0,4,8},        3),
            new ChordTemplate("sus",   new[]{0,5,7},        5),
            new ChordTemplate("sus",   new[]{0,2,7},        4),
            new ChordTemplate("maj7",  new[]{0,4,7,11},     6),
            new ChordTemplate("dom7",  new[]{0,4,7,10},     8),
            new ChordTemplate("min7",  new[]{0,3,7,10},     7),
            new ChordTemplate("m7b5",  new[]{0,3,6,10},     9),
            new ChordTemplate("add9",  new[]{0,4,7,2},      13),
            new ChordTemplate("madd9", new[]{0,3,7,2},      14),
            new ChordTemplate("maj7",  new[]{0,4,7,11,2},   16), // maj9 -> group with maj7
            new ChordTemplate("min7",  new[]{0,3,7,10,2},   17), // m9   -> group with min7
            new ChordTemplate("6",     new[]{0,4,7,9},      11),
            new ChordTemplate("m6",    new[]{0,3,7,9},      12),
        };

        public class ChordGuess { public int RootDeg; public string Group; public int CanonIndex; public int[] Iv; public double Score; }

        /// <summary><paramref name="pcw"/> = 12 duration weights for degrees 0..11 (relative to the
        /// tonic). <paramref name="bassPc"/> = the slot's bass degree (or -1). Null if empty.</summary>
        public static ChordGuess DetectChord(double[] pcw, int bassPc)
        {
            double total = 0;
            for (int i = 0; i < 12; i++) total += pcw[i];
            if (total <= 0) return null;

            ChordGuess best = null;
            for (int r = 0; r < 12; r++)
            {
                foreach (var tpl in Templates)
                {
                    double cov = 0; int present = 0;
                    foreach (int iv in tpl.Iv)
                    {
                        double w = pcw[(r + iv) % 12];
                        cov += w;
                        if (w > 0) present++;
                    }
                    double sc = cov * ((double)present / tpl.Iv.Length)
                                - 0.5 * (total - cov)
                                - 0.04 * tpl.Iv.Length; // mild parsimony: prefer simpler chords on ties
                    if (bassPc >= 0 && r == bassPc) sc *= 1.12; // the bass usually carries the root
                    if (best == null || sc > best.Score)
                        best = new ChordGuess { RootDeg = r, Group = tpl.Group, CanonIndex = tpl.CanonIndex, Iv = tpl.Iv, Score = sc };
                }
            }
            return best;
        }
    }
}
