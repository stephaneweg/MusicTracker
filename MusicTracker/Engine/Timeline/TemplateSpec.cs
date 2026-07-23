using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace MusicTracker.Engine.Timeline
{
    /// <summary>
    /// A GENERATIVE project template (Data/templates/*.json, HomeScreen "Modèles de projet").
    ///
    /// Unlike a finished arrangement, a template is a BANK of reusable material: per section it offers several chord
    /// progressions, several accompaniment articulations, several melodic cells, several riff phrases per track and
    /// several drum grooves. When the template is opened, the app PICKS one option from each bank (at random, but the
    /// same pick for every section sharing a name, so a repeated "refrain" stays coherent) and assembles a full project.
    /// Riff phrases are written over a tonic (degree-1) chord in the key's mode; the app transposes them MODALLY per
    /// chord and voice-leads the joins. A "Régénérer" button re-picks with a new seed.
    ///
    /// Everything is degree/mode-relative, so a template is key-independent. This schema REPLACES the older
    /// (degree + rhythm-motif only) template format — there is no backward compatibility.
    /// </summary>
    public class TemplateSpec
    {
        public string Name { get; set; }
        public string Description { get; set; }
        public string Icon { get; set; }   // optional home-card glyph
        public string Tags { get; set; }   // optional home-card tag line

        /// <summary>True when this template was produced by the AI (not hand-written / imported). Enables the
        /// "Régénérer le template" action, which re-runs the AI with <see cref="Intention"/> and overwrites the file.</summary>
        public bool IsAiGenerated { get; set; }
        /// <summary>The style/intention the user typed when the AI generated this template — reused (and editable) when
        /// regenerating it.</summary>
        public string Intention { get; set; }

        /// <summary>The .json file it was loaded from (for deletion). Not serialized.</summary>
        [System.Text.Json.Serialization.JsonIgnore] public string SourcePath { get; set; }

        public TplMeasure Measure { get; set; } = new TplMeasure();
        public TplTonality Tonality { get; set; } = new TplTonality();
        public List<TplTrack> Tracks { get; set; } = new List<TplTrack>();
        public List<TplSection> Sections { get; set; } = new List<TplSection>();
    }

    /// <summary>Metre, tempo and total length.</summary>
    public class TplMeasure
    {
        public int Num { get; set; } = 4;     // time-signature numerator
        public int Denom { get; set; } = 4;   // time-signature denominator
        public int Bpm { get; set; } = 100;
        public int Count { get; set; } = 32;  // total number of bars to fill
    }

    /// <summary>Key: tonic pitch-class + mode (index into <see cref="Score.MusicalMode"/>, 0..8 — the 9 seven-note modes).</summary>
    public class TplTonality
    {
        public int Note { get; set; } = 0;    // 0..11 (0 = C)
        public int Mode { get; set; } = 0;    // 0 Major, 1 Minor, 2 Harmonic minor, 3 Melodic minor, 4 Dorian … 8 Locrian
    }

    /// <summary>One instrument track (order matters: riffs reference tracks by index).</summary>
    public class TplTrack
    {
        public string Name { get; set; }
        public int Program { get; set; }      // General MIDI program 0..127
    }

    /// <summary>A section = named form block holding the material BANKS the app draws from.</summary>
    public class TplSection
    {
        public string Name { get; set; }
        /// <summary>Bank of chord progressions (each ≈4 bars). The app picks one per section-name.</summary>
        public List<List<TplChord>> ChordProgressions { get; set; } = new List<List<TplChord>>();
        /// <summary>Bank of chord-accompaniment articulations (voiced rhythm motifs).</summary>
        public List<TplArticulation> ChordArticulations { get; set; } = new List<TplArticulation>();
        /// <summary>Bank of melodic cells attached to the chords (2nd voice, diatonic degrees, transposed per chord).</summary>
        public List<TplMelodicCell> ChordMelodicCells { get; set; } = new List<TplMelodicCell>();
        /// <summary>Riff phrase banks, one entry per track (by <see cref="TplRiffTrack.TrackNum"/>).</summary>
        public List<TplRiffTrack> Riffs { get; set; } = new List<TplRiffTrack>();
        /// <summary>Bank of drum grooves (short motifs that repeat).</summary>
        public List<TplDrumGroove> Drums { get; set; } = new List<TplDrumGroove>();
    }

    /// <summary>
    /// One chord, degree-relative. Its quality is described on THREE axes and mapped to the engine's flat chord
    /// vocabulary (<see cref="Flow.PatternGenerator.QualityNames"/>) by <see cref="TemplateChords.QualityIndex"/>.
    /// </summary>
    public class TplChord
    {
        public int Degree { get; set; } = 1;     // 1..7 within the key
        public int Mode { get; set; } = 0;       // triad quality: 0 major, 1 minor, 2 diminished
        public int Quality { get; set; } = 0;    // extension: 0 triad, 1 seventh, 2 ninth, 3 add9
        public int Color { get; set; } = -1;     // suspension: -1 none, 0 sus2, 1 sus4
        public int BeatCount { get; set; } = 4;  // duration in beats
    }

    /// <summary>A voiced accompaniment motif. <see cref="Motif"/> is flat triplets [voice, start, length, …] in slices;
    /// voice 0=bass, 1=root, 2=3rd, 3=5th, 4=7th, 5=root+8ve, … (see PatternGenerator custom grid). Repeated/continued
    /// to cover each chord.</summary>
    public class TplArticulation
    {
        public int BeatCount { get; set; } = 4;
        public int SlicesPerBeat { get; set; } = 4;
        public int[] Motif { get; set; }
    }

    /// <summary>A melodic cell attached to the chords. <see cref="Cell"/> is flat triplets [degree, start, length, …];
    /// degree 1..7 (8..14 = octave up) is diatonic, relative to each chord's anchor → transposes modally per chord.</summary>
    public class TplMelodicCell
    {
        public int BeatCount { get; set; } = 4;
        public int SlicesPerBeat { get; set; } = 4;
        public int[] Cell { get; set; }
    }

    /// <summary>The riff phrase bank for one track.</summary>
    public class TplRiffTrack
    {
        public int TrackNum { get; set; }
        public List<TplPhrase> Phrases { get; set; } = new List<TplPhrase>();
    }

    /// <summary>A riff phrase over 4 bars of degree 1. <see cref="Motif"/> is flat triplets [note, start, length, …]:
    /// note = CHROMATIC semitones above the tonic (0 = tonic, 12 = octave); a NEGATIVE length is a rest; notes may
    /// overlap (polyphony). The app transposes it modally per chord and voice-leads the joins.</summary>
    public class TplPhrase
    {
        public int SlicesPerBeat { get; set; } = 4;
        public int[] Motif { get; set; }
    }

    /// <summary>A drum groove. <see cref="Motif"/> is flat triplets [gmKey, start, length, …]; gmKey = GM drum note
    /// (35..81), start/length in slices; <see cref="Bars"/> = the motif's length so the app can tile it over a section.</summary>
    public class TplDrumGroove
    {
        public int Bars { get; set; } = 1;
        public int SlicesPerBeat { get; set; } = 4;
        public int[] Motif { get; set; }
    }

    /// <summary>Maps a template chord's (mode, quality, color) axes onto the engine's flat <c>QualityNames</c> index.</summary>
    public static class TemplateChords
    {
        // Resolve a name in PatternGenerator.QualityNames (case/diacritics tolerant), −1 if absent.
        static int Name(string n) => Flow.PatternGenerator.IndexOfQuality(n);

        /// <summary>
        /// (mode 0maj/1min/2dim) × (quality 0triad/1seventh/2ninth/3add9) × (color -1none/0sus2/1sus4) → QualityNames index.
        /// The dominant-7 vs major-7 distinction is not expressible on these axes: major+seventh maps to Maj7 (the app's
        /// colour styles favour it). Combinations with no exact entry fall back to the nearest simpler one.
        /// </summary>
        public static int QualityIndex(int mode, int quality, int color)
        {
            mode = Clamp(mode, 0, 2); quality = Clamp(quality, 0, 3);
            bool sus2 = color == 0, sus4 = color == 1;

            // A suspension replaces the 3rd, so the triad "mode" no longer applies.
            if (sus2 || sus4)
            {
                switch (quality)
                {
                    case 0:  return Pick(sus2 ? "Sus2" : "Sus4");
                    case 1:  return Pick(sus2 ? "7sus2" : "7sus4", sus2 ? "Sus2" : "Sus4");
                    case 2:  return Pick(sus2 ? "9sus2" : "9sus4", sus2 ? "7sus2" : "7sus4", sus2 ? "Sus2" : "Sus4");
                    default: return Pick("add9sus4", sus2 ? "Sus2" : "Sus4"); // no add9sus2 entry
                }
            }

            switch (quality)
            {
                case 0:  return Pick(mode == 0 ? "Majeur" : mode == 1 ? "Mineur" : "Diminué");
                case 1:  return Pick(mode == 0 ? "Maj7" : mode == 1 ? "Min7" : "m7♭5", mode == 0 ? "Majeur" : mode == 1 ? "Mineur" : "Diminué");
                case 2:  return Pick(mode == 0 ? "Maj9" : mode == 1 ? "m9" : "m7♭5", mode == 0 ? "Maj7" : "Min7");
                default: return Pick(mode == 1 ? "m(add9)" : "add9", mode == 0 ? "Majeur" : mode == 1 ? "Mineur" : "Diminué"); // add9
            }
        }

        // First name that exists in QualityNames; 0 (Majeur) if none.
        static int Pick(params string[] names)
        {
            foreach (var n in names) { int i = Name(n); if (i >= 0) return i; }
            return 0;
        }
        static int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);
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

    /// <summary>The prompt that asks an LLM for a generative <see cref="TemplateSpec"/> JSON ("Ajouter avec l'IA").</summary>
    public static class TemplatePrompt
    {
        public static string[] Build(string styleIntention)
        {
            var sys = new System.Text.StringBuilder();
            sys.AppendLine("Tu es arrangeur. Tu renvoies UNIQUEMENT un objet JSON STRICT (aucune prose) décrivant un MODÈLE de style musical GÉNÉRATIF et réutilisable.");
            sys.AppendLine("PRINCIPE : le modèle ne décrit pas un morceau figé mais des BANQUES de matière. Pour CHAQUE section tu proposes PLUSIEURS options (progressions d'accords, articulations, cellules mélodiques, phrases de riff par piste, grooves de batterie) ; l'application PIOCHE une option dans chaque banque et assemble le morceau. Deux sections de même 'name' réutilisent la même pioche (forme cohérente). Propose donc 4 à 8 options par banque pour de la variété.");
            sys.AppendLine("Tout est RELATIF au degré et au mode (indépendant de la tonalité absolue).");
            sys.AppendLine();
            sys.AppendLine("Schéma EXACT (respecte les noms de clés, JSON minifié) :");
            sys.AppendLine("{");
            sys.AppendLine(@"  ""name"": string, ""description"": string, ""icon"": un emoji, ""tags"": string court,");
            sys.AppendLine(@"  ""measure"": { ""num"": int, ""denom"": int, ""bpm"": int, ""count"": int(nombre de mesures total, ex. 32) },");
            sys.AppendLine(@"  ""tonality"": { ""note"": int(0-11, 0=Do), ""mode"": int(0 Majeur,1 Mineur,2 Mineur harmonique,3 Mineur mélodique,4 Dorien,5 Phrygien,6 Lydien,7 Mixolydien,8 Locrien) },");
            sys.AppendLine(@"  ""tracks"": [ { ""name"": string, ""program"": int(GM 0-127) } ],   // l'ORDRE compte : les riffs référencent la piste par index (0 = 1re piste)");
            sys.AppendLine(@"  ""sections"": [ {");
            sys.AppendLine(@"     ""name"": string(ex. ""intro"",""theme"",""refrain"",""pont"",""outro""),");
            sys.AppendLine(@"     ""chordProgressions"": [   // BANQUE : plusieurs progressions d'~4 mesures ; chaque accord :");
            sys.AppendLine(@"        [ { ""degree"": 1-7, ""mode"": int(0 majeur,1 mineur,2 diminué), ""quality"": int(0 triade,1 septième,2 neuvième,3 add9), ""color"": int(-1 aucune,0 sus2,1 sus4), ""beatCount"": int(temps) }, ... ] ],");
            sys.AppendLine(@"     ""chordArticulations"": [   // BANQUE : motifs d'accompagnement voisés");
            sys.AppendLine(@"        { ""beatCount"": int, ""slicesPerBeat"": int, ""motif"": [voix,début,durée, voix,début,durée, ...] } ],   // voix 0=basse,1=fond.,2=tierce,3=quinte,4=7e,5=fond.+8,6=9e,7=tierce+8,... ; début/durée en SLICES");
            sys.AppendLine(@"     ""chordMelodicCells"": [   // BANQUE (optionnelle) : 2e voix chantante attachée aux accords");
            sys.AppendLine(@"        { ""beatCount"": int, ""slicesPerBeat"": int(le MÊME que l'articulation), ""cell"": [degré,début,durée, ...] } ],   // degré DIATONIQUE 1-7 (8-14 = octave au-dessus), transposé modalement sur chaque accord");
            sys.AppendLine(@"     ""riffs"": [   // une entrée par piste utilisée");
            sys.AppendLine(@"        { ""trackNum"": int(index de piste), ""phrases"": [   // BANQUE de 4 à 8 phrases de 4 MESURES PAR PISTE");
            sys.AppendLine(@"           { ""slicesPerBeat"": int, ""motif"": [note,début,durée, note,début,durée, ...] } ] } ],");
            sys.AppendLine(@"           // note = demi-tons CHROMATIQUES au-dessus de la TONIQUE (0=tonique, 12=octave), écrite comme si l'accord était le degré 1 ; l'app transpose modalement par accord + voice-leading. Durée NÉGATIVE = silence. Les notes peuvent se chevaucher (polyphonie). début/durée en SLICES.");
            sys.AppendLine(@"     ""drums"": [   // BANQUE de grooves");
            sys.AppendLine(@"        { ""bars"": int(longueur du motif en mesures, souvent 1-2), ""slicesPerBeat"": int, ""motif"": [noteGM,début,durée, ...] } ]   // noteGM = batterie 35-81 (36 grosse caisse, 38 caisse claire, 42 charley fermé, 46 ouvert, 49 crash, 51 ride)");
            sys.AppendLine("  } ] }");
            sys.AppendLine();
            sys.AppendLine("Règles : 3 à 5 sections nommées (intro/theme/refrain/pont/outro selon le style) ; 4 à 8 options par banque ; POUR LES RIFFS, 4 à 8 phrases PAR PISTE et PAR SECTION, avec une DENSITÉ adaptée au rôle de la piste (mélodie/lead = phrases plus denses et actives ; basse, nappes, contrechant = plus aérées et tenues) ; une piste peut être SILENCIEUSE dans une section : soit tu l'OMETS des 'riffs' de la section, soit tu inclus des phrases VIDES (\"motif\": []) dans sa banque — ainsi la pioche laisse parfois l'instrument se taire. SERS-t'en pour la dynamique d'arrangement (intro/pont épurés où seuls 1-2 instruments jouent, montée progressive, respirations) ; accords cohérents avec le style et bouclables ; les phrases de riff pensées sur 4 mesures, aérées (silences via durée négative) ; batterie = motif court qui se répète (renseigne 'bars'). 'slicesPerBeat' typique = 4 (double-croche) ou 6 (ternaire). Réponds UNIQUEMENT par le JSON minifié.");
            string usr = "Style et intention : « " + (styleIntention ?? "").Trim() + " ». Produis le modèle génératif en JSON.";
            return new[] { sys.ToString(), usr };
        }
    }
}
