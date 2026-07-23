using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace MusicTracker.Engine.Timeline
{
    /// <summary>
    /// A rich, section-based project template (as produced by the "génère un template" AI prompt), loaded from
    /// Data/templates/*.json. Describes instruments, and per-section chords (degrees), a drum style and per-instrument
    /// rhythm motifs. TimelineScreen expands it to any bar count by alternating the dev sections.
    /// </summary>
    public class TemplateSpec
    {
        public string Name { get; set; }
        public string Icon { get; set; }
        public string Tags { get; set; }
        /// <summary>The .json file it was loaded from (for deletion). Not serialized.</summary>
        [System.Text.Json.Serialization.JsonIgnore] public string SourcePath { get; set; }
        public int Bpm { get; set; } = 120;
        public int[] Meter { get; set; }                 // [num, den]
        public List<Inst> Instruments { get; set; } = new List<Inst>();
        public Dictionary<string, Section> Sections { get; set; } = new Dictionary<string, Section>();

        public class Inst { public string Name { get; set; } public int Gm { get; set; } public string Role { get; set; } }
        public class Chord { public int Degree { get; set; } = 1; public string Quality { get; set; } }
        public class Part { public string Instrument { get; set; } public double[] Durations { get; set; } public string Contour { get; set; } public string Anchor { get; set; } }
        public class Section
        {
            public int Bars { get; set; } = 8;
            public string Drum { get; set; }
            public List<Chord> Chords { get; set; } = new List<Chord>();
            public List<Part> Parts { get; set; } = new List<Part>();
        }

        // ---- enum string → engine index -------------------------------------------------------
        public static int ContourIndex(string c)
        {
            switch ((c ?? "").Trim().ToLowerInvariant())
            {
                case "up": case "montante": return 1;
                case "down": case "descendante": return 2;
                case "static": case "statique": return 3;
                case "zigzag": return 4;
                case "random": case "aléatoire": case "aleatoire": return 5;
                default: return 0;   // wave / vague
            }
        }
        public static int AnchorIndex(string a)
        {
            switch ((a ?? "").Trim().ToLowerInvariant())
            {
                case "root": case "fondamentale": return 1;
                case "third": case "tierce": return 2;
                case "fifth": case "quinte": return 3;
                case "seventh": case "septième": case "septieme": return 4;
                default: return 0;   // default / nearest
            }
        }
        public static int RegisterForRole(string role)
        {
            switch ((role ?? "").Trim().ToLowerInvariant())
            {
                case "bass": return -24;
                case "pad": return -5;
                case "comp": return -5;
                case "arp": return 7;
                default: return 0;   // lead / counter
            }
        }
    }

    /// <summary>The bundled section-based templates (Data/templates/*.json).</summary>
    public static class TemplateLibrary
    {
        static List<TemplateSpec> _all;
        public static List<TemplateSpec> All => _all ?? (_all = Load());

        static List<TemplateSpec> Load()
        {
            var list = new List<TemplateSpec>();
            try
            {
                string dir = AppPaths.Local(Path.Combine("Data", "templates"));
                if (Directory.Exists(dir))
                {
                    var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, AllowTrailingCommas = true };
                    foreach (var f in Directory.GetFiles(dir, "*.json"))
                        try
                        {
                            var t = JsonSerializer.Deserialize<TemplateSpec>(File.ReadAllText(f), opts);
                            if (t != null && !string.IsNullOrWhiteSpace(t.Name)) { t.SourcePath = f; list.Add(t); }
                        }
                        catch { /* skip a bad file */ }
                }
            }
            catch { }
            return list;
        }

        public static TemplateSpec Find(string name)
        {
            foreach (var t in All) if (t.Name == name) return t;
            return null;
        }

        /// <summary>The writable folder holding the templates (assembly-relative, like the other app data).</summary>
        public static string Dir => AppPaths.Local(Path.Combine("Data", "templates"));

        public static void Reload() => _all = null;

        /// <summary>Delete a template's .json file and reload the library.</summary>
        public static void Delete(string name)
        {
            var t = Find(name);
            if (t != null && !string.IsNullOrEmpty(t.SourcePath))
                try { File.Delete(t.SourcePath); } catch { }
            Reload();
        }

        /// <summary>Save a template JSON to the folder (filename from a slug of its name) and reload the library. Returns the path.</summary>
        public static string Save(string json, string name)
        {
            Directory.CreateDirectory(Dir);
            string slug = Slug(string.IsNullOrWhiteSpace(name) ? "template" : name);
            string path = Path.Combine(Dir, slug + ".json");
            File.WriteAllText(path, json);
            Reload();
            return path;
        }

        static string Slug(string s)
        {
            var sb = new System.Text.StringBuilder();
            foreach (char c in s.Trim().ToLowerInvariant())
                sb.Append(char.IsLetterOrDigit(c) ? c : '_');
            string r = sb.ToString().Trim('_');
            while (r.Contains("__")) r = r.Replace("__", "_");
            return string.IsNullOrEmpty(r) ? "template" : r;
        }
    }

    /// <summary>The prompt that asks an LLM for a section-based <see cref="TemplateSpec"/> JSON (used by "Ajouter avec l'IA").</summary>
    public static class TemplatePrompt
    {
        public static string[] Build(string styleIntention)
        {
            var sys = new System.Text.StringBuilder();
            sys.AppendLine("Tu es arrangeur. Tu renvoies UNIQUEMENT un objet JSON STRICT (aucune prose) décrivant UN template de style musical réutilisable.");
            sys.AppendLine("Le template = 5 sections de 8 mesures (intro, theme, devA, devB, outro), accords en DEGRÉS relatifs (indépendants de la tonalité), instruments (programmes General MIDI 0-127), un motif de batterie par section, et pour chaque instrument un motif de RYTHME (le moteur choisit les hauteurs sur les accords).");
            sys.AppendLine("Schéma EXACT :");
            sys.AppendLine("{ \"name\": string, \"icon\": un emoji, \"tags\": string court, \"bpm\": entier, \"meter\": [num,den],");
            sys.AppendLine("  \"instruments\": [ { \"name\": string, \"gm\": 0-127, \"role\": \"lead|bass|pad|arp|counter|comp\" } ],   // 3 à 5 instruments");
            sys.AppendLine("  \"sections\": { \"intro\": SECTION, \"theme\": SECTION, \"devA\": SECTION, \"devB\": SECTION, \"outro\": SECTION } }");
            sys.AppendLine("SECTION = { \"bars\": 8,");
            sys.AppendLine("  \"drum\": un nom EXACT parmi [\"Rock — basique\",\"Pop\",\"Funk (16th)\",\"Disco (4 au sol)\",\"Jazz swing\",\"Shuffle / Blues\",\"Bossa nova\",\"Half-time\",\"Hip-hop / boom-bap\",\"Marche\",\"Reggae one-drop\",\"Valse\",\"Punk (rapide)\",\"Ballade (cross-stick)\",\"Trap (hats roulés)\"],");
            sys.AppendLine("  \"chords\": [ { \"degree\": 1-7, \"quality\": \"maj|m|dim|maj7|m7|7|m7b5|dim7|6|m6|add9|9|maj9|m9\" } ],   // 2 à 8 accords");
            sys.AppendLine("  \"parts\": [ { \"instrument\": nom d'un instrument déclaré, \"durations\": [durées en TEMPS: 0.5=croche 1=noire 2=blanche 4=ronde ; une valeur NÉGATIVE = un SILENCE de cette durée (ex. -1 = un soupir d'une noire, -2 = un demi-soupir de blanche)], \"contour\": \"wave|up|down|static|zigzag|random\", \"anchor\": \"default|root|third|fifth|seventh\" } ] }");
            sys.AppendLine("Règles : accords cohérents avec le style et bouclables ; devA/devB contrastent avec theme ; 'durations' = motif court adapté au rôle (bass grave simple, pad tenu, lead mélodie, arp rapide). PENSE les motifs (batterie, mélodie, ligne mélodique) par PHRASES de 4 MESURES qui bouclent proprement (1, 2 ou 4 mesures) — ils seront posés en blocs de 4 mesures pour l'édition, donc évite les motifs qui tomberaient mal sur 4 mesures. INSÈRE des SILENCES (durées négatives) là où c'est opportun pour AÉRER et STRUCTURER les phrases (respirations, fins de phrase, appels/réponses) — évite un flux ininterrompu, surtout pour le lead. intro sobre, outro conclusif. Réponds UNIQUEMENT par le JSON.");
            string usr = "Style et intention : « " + (styleIntention ?? "").Trim() + " ». Produis le template en JSON.";
            return new[] { sys.ToString(), usr };
        }
    }
}
