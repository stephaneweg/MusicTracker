using System;
using System.Collections.Generic;
using System.Linq;
using MusicTracker.Engine.Score;

namespace MusicTracker.Engine.Flow
{
    /// <summary>Mood presets that bias the harmonic suggestions (candidate ranking + colours/borrowings).</summary>
    public enum HarmonyMood { Auto, Joyeux, Serein, Melancolique, Nostalgique, Epique, Lumineux, Jazzy }

    /// <summary>One ranked next-chord candidate: a DIATONIC degree (Deg 0..6, colour-aware) or a CHROMATIC chord
    /// (Deg = -1, absolute RootOff/Quality). Carries its display label, functional effect, weight and a "recommended" flag.</summary>
    public struct SuggestCand
    {
        public int Deg;            // 0..6 diatonic, or -1 = chromatic (use RootOff/Quality)
        public int RootOff;        // semitones from the tonic (chromatic)
        public int Quality;        // PatternGenerator quality index (chromatic)
        public int SuggestColour;  // suggested DiatonicColour 0..5 (diatonic candidates)
        public string Label, Effect;
        public double Weight;
        public bool Recommended;
    }

    /// <summary>
    /// CONTEXT-AWARE harmonic co-pilot: ranks idiomatic next-chord candidates from the last 2-3 chords, the bar
    /// position in the phrase (cadence proximity) and a mood. Shared by the suggestion diagram and the "chain" helper.
    /// Candidates are diatonic degrees + a few pertinent chromatics (secondary dominant V/V, borrowed ♭VII/♭VI/iv,
    /// Neapolitan ♭II). Effects are the felt RESULT (Tension / Résolution / Repos / Pré-dominante / …).
    /// </summary>
    public static class HarmonySuggest
    {
        struct Base { public int Deg, RootOff, Quality; public string Label, Effect; }
        static Base D(int deg, string effect) => new Base { Deg = deg, Effect = effect };
        static Base C(int rootOff, int quality, string label, string effect) => new Base { Deg = -1, RootOff = rootOff, Quality = quality, Label = label, Effect = effect };

        // Functional continuations (major context; the most idiomatic first). Diatonic + a few pertinent chromatics.
        static readonly Base[][] Table =
        {
            new[] { D(3,"Ouverture"), D(4,"Tension"), D(5,"Repos"), D(1,"Pré-dominante"), C(2,8,"V/V","Tension lumineuse"), C(10,0,"♭VII","Couleur planante") }, // I
            new[] { D(4,"Tension"), D(3,"Pré-dominante"), D(6,"Tension"), C(1,0,"♭II","Tension dramatique") },                                                    // ii
            new[] { D(5,"Repos"), D(3,"Ouverture"), C(8,0,"♭VI","Couleur lointaine") },                                                                           // iii
            new[] { D(4,"Tension"), D(0,"Cadence plagale"), D(1,"Pré-dominante"), C(5,1,"iv","Mélancolie"), C(1,0,"♭II","Tension dramatique") },                  // IV
            new[] { D(0,"Résolution"), D(5,"Cadence rompue"), D(3,"Couleur"), D(2,"Couleur"), C(8,0,"♭VI","Surprise sombre") },                                   // V
            new[] { D(1,"Pré-dominante"), D(3,"Ouverture"), D(4,"Tension"), C(2,8,"V/V","Tension lumineuse"), C(10,0,"♭VII","Couleur planante") },                // vi
            new[] { D(0,"Résolution"), D(5,"Couleur"), D(2,"Couleur") },                                                                                          // vii°
        };

        static readonly string[] RomanU = { "I", "II", "III", "IV", "V", "VI", "VII" };
        static readonly string[] RomanL = { "i", "ii", "iii", "iv", "v", "vi", "vii" };

        static int Clamp6(int d) => Math.Max(0, Math.Min(6, d));

        /// <summary>Rank the next-chord candidates. <paramref name="prevDegrees"/> = recent chord degrees (last = current,
        /// 0..6; -1 entries ignored). <paramref name="barIndex"/> = 0-based bar of the chord being added; <paramref name="phraseLen"/>
        /// = hypermetre length (4/8) for cadence proximity. Sorted best-first; the top 1-2 are flagged Recommended.</summary>
        public static List<SuggestCand> Rank(int[] prevDegrees, int barIndex, int phraseLen, HarmonyMood mood, KeySignature key)
        {
            key = key ?? new KeySignature();
            int tonicPc = MusicTheory.TonicPc(key);
            int cur = (prevDegrees != null && prevDegrees.Length > 0 && prevDegrees[prevDegrees.Length - 1] >= 0) ? Clamp6(prevDegrees[prevDegrees.Length - 1]) : 0;
            int prev2 = (prevDegrees != null && prevDegrees.Length > 1) ? prevDegrees[prevDegrees.Length - 2] : -1;
            if (phraseLen < 1) phraseLen = 4;
            bool nearCadence = ((barIndex + 1) % phraseLen) == 0;   // last bar of the hypermetre → a phrase ending
            bool phraseStart = (barIndex % phraseLen) == 0;

            var baseList = Table[cur];
            var outl = new List<SuggestCand>();
            for (int i = 0; i < baseList.Length; i++)
            {
                var b = baseList[i];
                double weight = 1.0 - i * 0.12;                       // base by idiomatic rank
                weight *= ContextMul(b, cur, prev2, nearCadence, phraseStart);
                weight *= MoodMul(b, mood);
                outl.Add(new SuggestCand
                {
                    Deg = b.Deg, RootOff = b.RootOff, Quality = b.Quality, Effect = b.Effect, Weight = Math.Max(0.01, weight),
                    Label = b.Deg >= 0 ? RomanLabel(key, b.Deg) : b.Label,
                    SuggestColour = b.Deg >= 0 ? SuggestColour(cur, b.Deg) : 0,
                });
            }
            outl = outl.OrderByDescending(c => c.Weight).ToList();
            if (outl.Count > 0) { var t = outl[0]; t.Recommended = true; outl[0] = t; }
            if (outl.Count > 1 && outl[1].Weight >= outl[0].Weight * 0.85) { var t = outl[1]; t.Recommended = true; outl[1] = t; }
            return outl;
        }

        // Context weighting from the functional arc + cadence position.
        static double ContextMul(Base b, int cur, int prev2, bool nearCadence, bool phraseStart)
        {
            double m = 1.0;
            if (b.Deg == cur && b.Deg >= 0) m *= 0.30;                        // anti-repeat (same chord)
            // dominant/pre-dominant → resolution
            bool preDomOrDom = cur == 1 || cur == 3 || cur == 4;
            if (preDomOrDom && b.Deg == 0) m *= 1.6;                          // …→ I (resolution)
            if (cur == 4 && b.Deg == 5) m *= 1.25;                            // V → vi (deceptive)
            if (prev2 == 1 && cur == 4 && b.Deg == 0) m *= 1.8;              // a ii-V in progress → I strongly
            if (cur == 1 && b.Deg == 4) m *= 1.3;                            // ii → V (set up the ii-V)
            // cadence proximity
            if (nearCadence) { if (b.Deg == 0) m *= 1.6; else if (b.Deg == 4) m *= 1.2; else if (b.Effect == "Ouverture") m *= 0.7; }
            if (phraseStart && (b.Effect == "Ouverture" || b.Deg == 3 || b.Deg == 4)) m *= 1.15;
            return m;
        }

        // Mood biasing: nudge families of chords up/down.
        static double MoodMul(Base b, HarmonyMood mood)
        {
            bool chromatic = b.Deg < 0;
            bool seventhy = b.Quality == 8 || b.Deg == 1 || b.Deg == 4;       // dom7 / ii / V (jazz-leaning)
            switch (mood)
            {
                case HarmonyMood.Jazzy: return seventhy ? 1.5 : (chromatic ? 1.2 : 0.9);
                case HarmonyMood.Epique: return chromatic ? 1.7 : (b.Effect == "Cadence rompue" || b.Effect == "Surprise sombre" ? 1.4 : 0.9);
                case HarmonyMood.Nostalgique: return (b.Deg == 5 || b.Deg == 3 || b.Deg == 2) ? 1.4 : (chromatic ? 0.8 : 1.0);
                case HarmonyMood.Melancolique: return (b.Deg == 5 || b.Deg == 1 || (chromatic && b.RootOff == 5)) ? 1.4 : (b.Deg == 0 ? 0.85 : 1.0);
                case HarmonyMood.Lumineux:
                case HarmonyMood.Joyeux: return (b.Deg == 0 || b.Deg == 3 || b.Deg == 4 || b.Deg == 5) ? 1.25 : (chromatic ? 0.5 : 0.9);
                case HarmonyMood.Serein: return (b.Deg == 3 || b.Deg == 0 || b.Effect == "Cadence plagale") ? 1.35 : (b.Effect == "Tension" ? 0.75 : (chromatic ? 0.8 : 1.0));
                default: return 1.0;
            }
        }

        // Idiomatic PRIMARY colour for a DIATONIC target given where we come from (dominants & ii → 7e=2; a non-resolved
        // tonic → add9=4; resolved tonic/other → triade=0). Indices follow MusicTheory.DiatonicColourNames.
        public static int SuggestColour(int prevDeg, int nextDeg)
        {
            if (nextDeg == 4 || nextDeg == 6) return 2;                        // V / vii° → 7e
            if (nextDeg == 1) return 2;                                        // ii → 7e (ii7)
            if (nextDeg == 0) return (prevDeg == 4 || prevDeg == 6) ? 0 : 4;   // I: resolved → triade, else add9
            return 0;
        }

        static string RomanLabel(KeySignature key, int degree)
        {
            var ch = MusicTheory.DiatonicChord(key, degree);
            int q = ch.quality;
            bool minorish = q == 1 || q == 7 || q == 14 || q == 17 || q == 2 || q == 9 || q == 10;
            bool dim = q == 2 || q == 9 || q == 10;
            string r = minorish ? RomanL[Clamp6(degree)] : RomanU[Clamp6(degree)];
            return dim ? r + "°" : r;
        }
    }
}
