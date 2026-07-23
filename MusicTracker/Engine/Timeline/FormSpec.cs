using System.Collections.Generic;

namespace MusicTracker.Engine.Timeline
{
    /// <summary>How a form SECTION is realized. The FORM dictates the skeleton (which sections, their order, their key
    /// plan + cadence target, their length); the COMPOSER (a V2 style) provides the SOUL (theme melody, accompaniment).</summary>
    public enum FormRole
    {
        Intro,       // lead-in: the style's intro chords + a short intro melody, cadencing into the theme
        ThemeA,      // state the principal theme A (V2 theme melody + its chord grid)
        RestateA,    // restate A (re-fit onto the section's chords; usually resolved)
        ThemeB,      // a SECOND theme B (a fresh V2 theme), typically in another key
        RestateB,    // restate B (re-fit; resolved)
        Develop,     // development: reuse/vary A, OR a third theme over the SAME chords (ThirdTheme); may be a dialogue
        Variation,   // a variation of A (same harmony, fresh figuration)
        Transition,  // free episode (a short progression; sparse or no melody) — e.g. a concerto solo episode
        Recap,       // final conclusive restatement of A (resolved home)
        Outro        // the style's outro chords, decrescendo
    }

    /// <summary>A key AREA relative to the home tonic (resolved to semitones with the home mode at realization).</summary>
    public enum KeyArea { Home, Secondary, Mediant, Subdominant }

    public class FormSection
    {
        public FormRole Role;
        public int Bars;
        public KeyArea Key;
        public int Cad;          // cadence target: 0 = open / half-cadence (V "question"), 1 = resolve to tonic (I "answer"), 2 = free
        public bool Dialogue;    // develop/restate: a question/answer + together exchange between the two voices
        public bool ThirdTheme;  // develop: a fresh 3rd theme over the SAME chords (else reuse/vary theme A)
        public string Label;     // shown as the timeline section name

        public FormSection(FormRole role, int bars, KeyArea key = KeyArea.Home, int cad = 1, bool dialogue = false, bool thirdTheme = false, string label = null)
        { Role = role; Bars = bars; Key = key; Cad = cad; Dialogue = dialogue; ThirdTheme = thirdTheme; Label = label ?? role.ToString(); }
    }

    public class FormSpec
    {
        public string Name;
        public List<FormSection> Sections;
        public FormSpec(string name, List<FormSection> sections) { Name = name; Sections = sections; }

        /// <summary>Semitone shift of a key area from the home tonic (relative major in minor keys, dominant in major, etc.).</summary>
        public static int Semis(KeyArea a, bool minor)
        {
            switch (a)
            {
                case KeyArea.Secondary: return minor ? 3 : 7;   // relative major (minor home) / dominant (major home)
                case KeyArea.Mediant: return minor ? 8 : 9;     // a related upper key for development
                case KeyArea.Subdominant: return 5;
                default: return 0;
            }
        }
    }

    /// <summary>The available FORMS: a set of BASE forms (every style) + STYLE-SPECIFIC forms (Bach/Vivaldi/Ghibli).</summary>
    public static class Forms
    {
        static FormSection S(FormRole r, int bars, KeyArea k, int cad, bool dlg, bool third, string label)
            => new FormSection(r, bars, k, cad, dlg, third, label);

        static List<FormSpec> BaseForms()
        {
            return new List<FormSpec>
            {
                new FormSpec("Posée (intro·thème·dév·reprise)", new List<FormSection> {
                    S(FormRole.Intro,    4, KeyArea.Home,      0, false, false, "Intro"),
                    S(FormRole.ThemeA,   4, KeyArea.Home,      0, false, false, "Thème"),
                    S(FormRole.RestateA, 4, KeyArea.Home,      1, false, false, "Ré-exposition"),
                    S(FormRole.Develop,  8, KeyArea.Secondary, 1, true,  false, "Développement"),
                    S(FormRole.Recap,    4, KeyArea.Home,      1, false, false, "Recap"),
                    S(FormRole.Outro,    4, KeyArea.Home,      1, false, false, "Outro"),
                }),
                new FormSpec("ABAC", new List<FormSection> {
                    S(FormRole.ThemeA,   8, KeyArea.Home,      1, false, false, "A"),
                    S(FormRole.ThemeB,   8, KeyArea.Secondary, 1, false, false, "B"),
                    S(FormRole.RestateA, 8, KeyArea.Home,      1, false, false, "A"),
                    S(FormRole.Develop,  8, KeyArea.Mediant,   1, false, true,  "C"),
                }),
                new FormSpec("AABA (lied)", new List<FormSection> {
                    S(FormRole.ThemeA,   8, KeyArea.Home,        1, false, false, "A"),
                    S(FormRole.RestateA, 8, KeyArea.Home,        1, false, false, "A"),
                    S(FormRole.ThemeB,   8, KeyArea.Subdominant, 1, false, false, "B (pont)"),
                    S(FormRole.RestateA, 8, KeyArea.Home,        1, false, false, "A"),
                }),
                new FormSpec("Sonate", new List<FormSection> {
                    S(FormRole.ThemeA,     4, KeyArea.Home,      0, false, false, "Expo A"),
                    S(FormRole.RestateA,   4, KeyArea.Home,      1, false, false, "Expo A'"),
                    S(FormRole.Transition, 4, KeyArea.Home,      0, false, false, "Transition"),
                    S(FormRole.ThemeB,     8, KeyArea.Secondary, 1, false, false, "Expo B"),
                    S(FormRole.Develop,    8, KeyArea.Mediant,   0, true,  false, "Développement"),
                    S(FormRole.Recap,      4, KeyArea.Home,      0, false, false, "Recap A"),
                    S(FormRole.RestateA,   4, KeyArea.Home,      1, false, false, "Recap A'"),
                    S(FormRole.RestateB,   8, KeyArea.Home,      1, false, false, "Recap B"),
                }),
                new FormSpec("Rondeau (ABACA)", new List<FormSection> {
                    S(FormRole.ThemeA,   8, KeyArea.Home,      1, false, false, "A"),
                    S(FormRole.ThemeB,   8, KeyArea.Secondary, 1, false, false, "B"),
                    S(FormRole.RestateA, 8, KeyArea.Home,      1, false, false, "A"),
                    S(FormRole.Develop,  8, KeyArea.Mediant,   1, false, true,  "C"),
                    S(FormRole.RestateA, 8, KeyArea.Home,      1, false, false, "A"),
                }),
                new FormSpec("Thème et variations", new List<FormSection> {
                    S(FormRole.ThemeA,    8, KeyArea.Home, 1, false, false, "Thème"),
                    S(FormRole.Variation, 8, KeyArea.Home, 1, false, false, "Var. 1"),
                    S(FormRole.Variation, 8, KeyArea.Home, 1, true,  false, "Var. 2"),
                    S(FormRole.Variation, 8, KeyArea.Home, 1, false, false, "Var. 3"),
                }),
            };
        }

        public static string StyleKey(string modelFile)
        {
            string low = (modelFile ?? "").ToLowerInvariant();
            if (low.Contains("vivaldi")) return "vivaldi";
            if (low.Contains("clavier") || low.Contains("bach")) return "bach";
            if (low.Contains("ghibli")) return "ghibli";
            return "other";
        }

        /// <summary>Base forms + the forms specific to the chosen style.</summary>
        public static List<FormSpec> ForStyle(string modelFile)
        {
            var list = BaseForms();
            switch (StyleKey(modelFile))
            {
                case "vivaldi":
                    list.Add(new FormSpec("Ritournelle (concerto)", new List<FormSection> {
                        S(FormRole.ThemeA,     8, KeyArea.Home,      1, false, false, "Ritournelle"),
                        S(FormRole.Transition, 6, KeyArea.Secondary, 2, false, false, "Solo 1"),
                        S(FormRole.RestateA,   4, KeyArea.Secondary, 1, false, false, "Ritournelle"),
                        S(FormRole.Transition, 6, KeyArea.Mediant,   2, false, false, "Solo 2"),
                        S(FormRole.RestateA,   8, KeyArea.Home,      1, false, false, "Ritournelle"),
                    }));
                    break;
                case "bach":
                    list.Add(new FormSpec("Suite (danse AABB)", new List<FormSection> {
                        S(FormRole.ThemeA,   8, KeyArea.Home,      0, false, false, "A"),
                        S(FormRole.RestateA, 8, KeyArea.Home,      1, false, false, "A (reprise)"),
                        S(FormRole.ThemeB,   8, KeyArea.Secondary, 0, false, false, "B"),
                        S(FormRole.RestateB, 8, KeyArea.Home,      1, false, false, "B (reprise)"),
                    }));
                    list.Add(new FormSpec("Invention", new List<FormSection> {
                        S(FormRole.ThemeA,  4, KeyArea.Home,      0, false, false, "Sujet"),
                        S(FormRole.Develop, 12, KeyArea.Secondary, 1, true,  false, "Développement"),
                    }));
                    break;
                case "ghibli":
                    list.Add(new FormSpec("Ballade (ABA)", new List<FormSection> {
                        S(FormRole.Intro,    2, KeyArea.Home,        0, false, false, "Intro"),
                        S(FormRole.ThemeA,   8, KeyArea.Home,        1, false, false, "A"),
                        S(FormRole.ThemeB,   8, KeyArea.Subdominant, 1, false, false, "B"),
                        S(FormRole.RestateA, 8, KeyArea.Home,        1, true,  false, "A"),
                        S(FormRole.Outro,    2, KeyArea.Home,        1, false, false, "Outro"),
                    }));
                    break;
            }
            return list;
        }

        public static string[] Names(string modelFile)
        {
            var l = ForStyle(modelFile);
            var n = new string[l.Count];
            for (int i = 0; i < l.Count; i++) n[i] = l[i].Name;
            return n;
        }

        public static FormSpec Get(string modelFile, int index)
        {
            var l = ForStyle(modelFile);
            return (index >= 0 && index < l.Count) ? l[index] : l[0];
        }

        /// <summary>Override section SIZES from "Créer structure". For each size: &gt;0 sets it, 0 OMITS the intro/outro,
        /// &lt;0 keeps the form's own default. themeBars applies to the theme + its restatements; varCount = how many
        /// Variation sections (or, for a develop form, dev length = themeBars × varCount).</summary>
        public static FormSpec ApplySizes(FormSpec spec, int themeBars, int introBars, int outroBars, int varCount)
        {
            if (spec == null) return spec;
            int origVar = 0; foreach (var s in spec.Sections) if (s.Role == FormRole.Variation) origVar++;
            var secs = new List<FormSection>();
            bool hasVar = false; int varInsert = -1;
            foreach (var s in spec.Sections)
            {
                if (s.Role == FormRole.Variation) { if (!hasVar) { hasVar = true; varInsert = secs.Count; } continue; }   // collected, re-inserted below
                if (s.Role == FormRole.Intro && introBars == 0) continue;   // 0 → omit the intro
                if (s.Role == FormRole.Outro && outroBars == 0) continue;   // 0 → omit the outro
                int bars = s.Bars;
                if (s.Role == FormRole.Intro && introBars > 0) bars = introBars;
                else if (s.Role == FormRole.Outro && outroBars > 0) bars = outroBars;
                else if (themeBars > 0 && (s.Role == FormRole.ThemeA || s.Role == FormRole.RestateA || s.Role == FormRole.ThemeB || s.Role == FormRole.RestateB || s.Role == FormRole.Recap)) bars = themeBars;
                else if (s.Role == FormRole.Develop && themeBars > 0 && varCount > 0) bars = themeBars * varCount;
                secs.Add(new FormSection(s.Role, System.Math.Max(1, bars), s.Key, s.Cad, s.Dialogue, s.ThirdTheme, s.Label));
            }
            if (hasVar)
            {
                int n = varCount > 0 ? varCount : System.Math.Max(1, origVar);
                int tb = themeBars > 0 ? themeBars : 8;
                var vs = new List<FormSection>();
                for (int i = 0; i < n; i++) vs.Add(new FormSection(FormRole.Variation, tb, KeyArea.Home, 1, (i % 2) == 1, false, "Var. " + (i + 1)));
                secs.InsertRange(System.Math.Min(System.Math.Max(0, varInsert), secs.Count), vs);
            }
            return new FormSpec(spec.Name, secs);
        }
    }
}
