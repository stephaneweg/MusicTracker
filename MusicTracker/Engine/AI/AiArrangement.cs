using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MusicTracker.Engine.Flow;
using MusicTracker.Engine.Score;
using MusicTracker.Engine.Timeline;

namespace MusicTracker.Engine.AI
{
    // ---- JSON contract (the object Mistral must return) --------------------------------------------------------------
    public class AiArrangement
    {
        public AiMeter meter { get; set; }
        public AiKey key { get; set; }
        public int bpm { get; set; }
        public int chordInstrument { get; set; } = -1; // GM program for the Accords lane (-1 = piano default)
        public List<AiSection> sections { get; set; }
        public List<AiChord> chords { get; set; }
        public List<AiArticulation> articulation { get; set; }
        public List<AiMelodicLine> melodicLines { get; set; }
        public List<AiRiff> riffs { get; set; }
        public List<AiRiff> drums { get; set; }   // percussion phrases (pitch = GM drum key 35..81); one drum kit track

        static readonly JsonSerializerOptions Opts = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            AllowTrailingCommas = true,
            NumberHandling = JsonNumberHandling.AllowReadingFromString, // a numeric field given as "72" still parses
        };

        /// <summary>Parse the model's reply into an arrangement. Tolerates ```json fences. Throws on invalid JSON/shape.</summary>
        public static AiArrangement Parse(string content)
        {
            string json = StripFences(content);
            var a = JsonSerializer.Deserialize<AiArrangement>(json, Opts)
                    ?? throw new InvalidOperationException("JSON vide.");
            if (a.chords == null || a.chords.Count == 0) throw new InvalidOperationException("Aucun accord dans la réponse.");
            a.sections = a.sections ?? new List<AiSection>();
            a.articulation = a.articulation ?? new List<AiArticulation>();
            a.melodicLines = a.melodicLines ?? new List<AiMelodicLine>();
            a.riffs = a.riffs ?? new List<AiRiff>();
            a.drums = a.drums ?? new List<AiRiff>();
            return a;
        }

        /// <summary>Strip ```fences``` and return the first balanced {…} object — for callers deserializing a single
        /// sub-object (e.g. one drum groove) rather than a whole arrangement.</summary>
        public static string CleanJson(string s) => StripFences(s);

        static string StripFences(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "{}";
            s = s.Trim();
            if (s.StartsWith("```"))
            {
                int nl = s.IndexOf('\n');
                if (nl >= 0) s = s.Substring(nl + 1);
                int fence = s.LastIndexOf("```", StringComparison.Ordinal);
                if (fence >= 0) s = s.Substring(0, fence);
            }
            return ExtractFirstObject(s);
        }

        /// <summary>Returns the first BALANCED {...} object (brace-matched, string-aware), so any junk the model
        /// appends AFTER the valid object (stray tokens like "4}]}]}") or wraps around it is dropped. Falls back to
        /// first-'{'…last-'}' when the object is unbalanced (e.g. a truly truncated reply).</summary>
        static string ExtractFirstObject(string s)
        {
            int start = s.IndexOf('{');
            if (start < 0) return s;
            int depth = 0; bool inStr = false, esc = false;
            for (int i = start; i < s.Length; i++)
            {
                char c = s[i];
                if (inStr)
                {
                    if (esc) esc = false;
                    else if (c == '\\') esc = true;
                    else if (c == '"') inStr = false;
                    continue;
                }
                if (c == '"') inStr = true;
                else if (c == '{') depth++;
                else if (c == '}' && --depth == 0) return s.Substring(start, i - start + 1);
            }
            int b = s.LastIndexOf('}');                              // unbalanced ⇒ best-effort (may be truncated)
            return (b > start) ? s.Substring(start, b - start + 1) : s.Substring(start);
        }
    }

    public class AiMeter { public int num { get; set; } = 4; public int den { get; set; } = 4; }
    public class AiKey { public string tonic { get; set; } public string mode { get; set; } }
    public class AiSection { public string name { get; set; } public int measures { get; set; } }
    // Compact wire form: an ARRAY [measure, degree, quality]; the legacy object form is still accepted (see converter).
    [JsonConverter(typeof(AiChordConverter))]
    public class AiChord { public int measure { get; set; } = 1; public int degree { get; set; } = 1; public string quality { get; set; } }

    /// <summary>Reads a chord as EITHER a compact array [measure, degree, quality] OR the legacy object.</summary>
    public class AiChordConverter : JsonConverter<AiChord>
    {
        public override AiChord Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o)
        {
            var c = new AiChord();
            if (r.TokenType == JsonTokenType.StartArray)
            {
                int i = 0;
                while (r.Read() && r.TokenType != JsonTokenType.EndArray)
                {
                    if (i == 0) c.measure = (int)Math.Round(AiJson.ReadNum(ref r));
                    else if (i == 1) c.degree = (int)Math.Round(AiJson.ReadNum(ref r));
                    else if (i == 2) c.quality = AiJson.ReadStr(ref r);
                    else r.Skip();
                    i++;
                }
            }
            else if (r.TokenType == JsonTokenType.StartObject)
            {
                while (r.Read() && r.TokenType != JsonTokenType.EndObject)
                {
                    if (r.TokenType != JsonTokenType.PropertyName) continue;
                    string name = r.GetString(); r.Read();
                    switch ((name ?? "").ToLowerInvariant())
                    {
                        case "measure": case "mes": c.measure = (int)Math.Round(AiJson.ReadNum(ref r)); break;
                        case "degree": case "deg": c.degree = (int)Math.Round(AiJson.ReadNum(ref r)); break;
                        case "quality": case "qual": c.quality = AiJson.ReadStr(ref r); break;
                        default: r.Skip(); break;
                    }
                }
            }
            else r.Skip();
            return c;
        }
        public override void Write(Utf8JsonWriter w, AiChord v, JsonSerializerOptions o)
        { w.WriteStartArray(); w.WriteNumberValue(v.measure); w.WriteNumberValue(v.degree); w.WriteStringValue(v.quality); w.WriteEndArray(); }
    }

    /// <summary>Reads a JSON string OR number (or bool) as a string — models sometimes give enum-ish fields as indices.</summary>
    public class FlexibleStringConverter : JsonConverter<string>
    {
        public override string Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o)
        {
            switch (r.TokenType)
            {
                case JsonTokenType.String: return r.GetString();
                case JsonTokenType.Number: return r.TryGetInt64(out long l) ? l.ToString() : r.GetDouble().ToString(System.Globalization.CultureInfo.InvariantCulture);
                case JsonTokenType.True: return "true";
                case JsonTokenType.False: return "false";
                case JsonTokenType.Null: return null;
                default: r.Skip(); return null;
            }
        }
        public override void Write(Utf8JsonWriter w, string v, JsonSerializerOptions o) => w.WriteStringValue(v);
    }
    public class AiArticulation
    {
        public string section { get; set; }
        [JsonConverter(typeof(FlexibleStringConverter))] public string style { get; set; } // optional fallback: a named built-in style
        public List<AiArtNote> motif { get; set; }     // preferred: a custom one-bar voiced rhythm (reused over the section)
        [JsonConverter(typeof(FlexibleStringConverter))] public string name { get; set; }  // optional: name for the saved user style
        // Optional MELODIC CELL travelling with the motif: a short diatonic phrase attached to every chord that uses this
        // articulation. Written in DEGREES of the key (not absolute pitches), so each chord transposes it modally.
        public List<AiCellNote> melodicCell { get; set; }
    }

    // One note of a chord's melodic cell. degree = diatonic degree 1..7 (8..14 = same degree one octave up), relative to
    // the chord's anchor; start/length in TEMPS (beats). Compact wire form: [degree, start, length].
    [JsonConverter(typeof(AiCellNoteConverter))]
    public class AiCellNote { public int degree { get; set; } = 1; public double start { get; set; } public double length { get; set; } = 1; }

    /// <summary>Reads a melodic-cell note as EITHER a compact array [degree, start, length] OR the legacy object.</summary>
    public class AiCellNoteConverter : JsonConverter<AiCellNote>
    {
        public override AiCellNote Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o)
        {
            var n = new AiCellNote();
            if (r.TokenType == JsonTokenType.StartArray)
            {
                int i = 0;
                while (r.Read() && r.TokenType != JsonTokenType.EndArray)
                {
                    double v = AiJson.ReadNum(ref r);
                    if (i == 0) n.degree = (int)Math.Round(v); else if (i == 1) n.start = v; else if (i == 2) n.length = v;
                    i++;
                }
            }
            else if (r.TokenType == JsonTokenType.StartObject)
            {
                while (r.Read() && r.TokenType != JsonTokenType.EndObject)
                {
                    if (r.TokenType != JsonTokenType.PropertyName) continue;
                    string name = r.GetString(); r.Read();
                    double v = AiJson.ReadNum(ref r);
                    switch ((name ?? "").ToLowerInvariant())
                    {
                        case "degree": case "deg": case "degre": n.degree = (int)Math.Round(v); break;
                        case "start": n.start = v; break;
                        case "length": case "len": case "dur": n.length = v; break;
                    }
                }
            }
            else r.Skip();
            return n;
        }
        public override void Write(Utf8JsonWriter w, AiCellNote v, JsonSerializerOptions o)
        { w.WriteStartArray(); w.WriteNumberValue(v.degree); w.WriteNumberValue(v.start); w.WriteNumberValue(v.length); w.WriteEndArray(); }
    }
    // One event of a custom chord-articulation motif. voice = grid row (0=bass, 1=root, 2=3rd, 3=5th, 4=7th, 5=root+8,
    // 6=9th, 7=3rd+8, 8=5th+8, 9=7th+8, 10=9th+8). start/length in TEMPS (beats).
    // Compact wire form: an ARRAY [voice, start, length]; the legacy object form is still accepted (see converter).
    [JsonConverter(typeof(AiArtNoteConverter))]
    public class AiArtNote { public int voice { get; set; } public double start { get; set; } public double length { get; set; } = 1; }

    public class AiMelodicLine
    {
        public string section { get; set; }
        public string track { get; set; }          // role/track name (grouped → one instrument track each), e.g. "lead", "basse"
        public int instrument { get; set; } = -1;   // optional GM program for that track (-1 = default)
        public int fromMeasure { get; set; } = 1;
        public int measures { get; set; } = 1;
        public List<double> durations { get; set; } // rhythm motif, in beats (temps); tiled across the part
        [JsonConverter(typeof(FlexibleStringConverter))] public string anchor { get; set; }
        [JsonConverter(typeof(FlexibleStringConverter))] public string contour { get; set; }
        public int register { get; set; }           // RegisterShift, semitones (bass ~ -24, lead 0, high +12)
        public int voice { get; set; } = 1;
    }

    // A riff = an EXPLICIT phrase (the model writes the actual notes + rhythm). Placed as a Riff object on a track.
    public class AiRiff
    {
        public string section { get; set; }
        public string track { get; set; }           // role → one instrument track each (grouped)
        public int instrument { get; set; } = -1;   // GM program (-1 = default)
        public int fromMeasure { get; set; } = 1;
        public int measures { get; set; } = 1;
        public int motifBars { get; set; }           // DRUMS: length of ONE repeating motif, in measures (0 = unset → auto-detect)
        public int repeats { get; set; }             // DRUMS: how many times the motif repeats to fill the section (0 = auto)
        public List<AiRiffNote> notes { get; set; }
    }
    // Compact wire form: an ARRAY [pitch, start, length]; the legacy object form is still accepted (see converter).
    [JsonConverter(typeof(AiRiffNoteConverter))]
    public class AiRiffNote { public int pitch { get; set; } public double start { get; set; } public double length { get; set; } = 1; }

    /// <summary>Reads a note as EITHER a compact array [pitch, start, length] OR the legacy object
    /// {pitch,start,length}. Missing 3rd element ⇒ length keeps its default (1). Numbers may be given as strings.</summary>
    public class AiRiffNoteConverter : JsonConverter<AiRiffNote>
    {
        public override AiRiffNote Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o)
        {
            var n = new AiRiffNote();
            if (r.TokenType == JsonTokenType.StartArray)
            {
                int i = 0;
                while (r.Read() && r.TokenType != JsonTokenType.EndArray)
                {
                    double v = AiJson.ReadNum(ref r);
                    if (i == 0) n.pitch = (int)Math.Round(v); else if (i == 1) n.start = v; else if (i == 2) n.length = v;
                    i++;
                }
            }
            else if (r.TokenType == JsonTokenType.StartObject)
            {
                while (r.Read() && r.TokenType != JsonTokenType.EndObject)
                {
                    if (r.TokenType != JsonTokenType.PropertyName) continue;
                    string name = r.GetString(); r.Read();
                    double v = AiJson.ReadNum(ref r);
                    switch ((name ?? "").ToLowerInvariant())
                    {
                        case "pitch": n.pitch = (int)Math.Round(v); break;
                        case "start": n.start = v; break;
                        case "length": case "len": case "dur": n.length = v; break;
                    }
                }
            }
            else r.Skip();
            return n;
        }
        public override void Write(Utf8JsonWriter w, AiRiffNote v, JsonSerializerOptions o)
        { w.WriteStartArray(); w.WriteNumberValue(v.pitch); w.WriteNumberValue(v.start); w.WriteNumberValue(v.length); w.WriteEndArray(); }
    }

    /// <summary>Reads an articulation event as EITHER a compact array [voice, start, length] OR the legacy object.</summary>
    public class AiArtNoteConverter : JsonConverter<AiArtNote>
    {
        public override AiArtNote Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o)
        {
            var n = new AiArtNote();
            if (r.TokenType == JsonTokenType.StartArray)
            {
                int i = 0;
                while (r.Read() && r.TokenType != JsonTokenType.EndArray)
                {
                    double v = AiJson.ReadNum(ref r);
                    if (i == 0) n.voice = (int)Math.Round(v); else if (i == 1) n.start = v; else if (i == 2) n.length = v;
                    i++;
                }
            }
            else if (r.TokenType == JsonTokenType.StartObject)
            {
                while (r.Read() && r.TokenType != JsonTokenType.EndObject)
                {
                    if (r.TokenType != JsonTokenType.PropertyName) continue;
                    string name = r.GetString(); r.Read();
                    double v = AiJson.ReadNum(ref r);
                    switch ((name ?? "").ToLowerInvariant())
                    {
                        case "voice": case "voix": n.voice = (int)Math.Round(v); break;
                        case "start": n.start = v; break;
                        case "length": case "len": case "dur": n.length = v; break;
                    }
                }
            }
            else r.Skip();
            return n;
        }
        public override void Write(Utf8JsonWriter w, AiArtNote v, JsonSerializerOptions o)
        { w.WriteStartArray(); w.WriteNumberValue(v.voice); w.WriteNumberValue(v.start); w.WriteNumberValue(v.length); w.WriteEndArray(); }
    }

    static class AiJson
    {
        /// <summary>Reads the current scalar token as a double (number, or numeric string). Non-numeric ⇒ 0.</summary>
        public static double ReadNum(ref Utf8JsonReader r)
        {
            if (r.TokenType == JsonTokenType.Number) return r.GetDouble();
            if (r.TokenType == JsonTokenType.String &&
                double.TryParse(r.GetString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double d)) return d;
            if (r.TokenType == JsonTokenType.StartArray || r.TokenType == JsonTokenType.StartObject) r.Skip();
            return 0;
        }

        /// <summary>Reads the current scalar token as a string (string, or number/bool rendered as text). Container ⇒ null.</summary>
        public static string ReadStr(ref Utf8JsonReader r)
        {
            switch (r.TokenType)
            {
                case JsonTokenType.String: return r.GetString();
                case JsonTokenType.Number: return r.TryGetInt64(out long l) ? l.ToString() : r.GetDouble().ToString(System.Globalization.CultureInfo.InvariantCulture);
                case JsonTokenType.True: return "true";
                case JsonTokenType.False: return "false";
                case JsonTokenType.StartArray: case JsonTokenType.StartObject: r.Skip(); return null;
                default: return null;
            }
        }
    }

    /// <summary>Prompt building + name→index/key/rhythm translation for the Mistral arrangement flow.</summary>
    public static class AiArrangementPrompt
    {
        public static string SystemPrompt(bool riffMode, bool wantDrums, bool chordsAsVoice = false)
        {
            var contours = string.Join(", ", MelodicLineEngine.ContourNames.Select((n, i) => $"{i}={First(n)}"));
            var anchors = string.Join(", ", MelodicLineEngine.AnchorNames.Select((n, i) => $"{i}={First(n)}"));
            var qualities = string.Join(", ", PatternGenerator.QualityNames);

            // Explicit-note block (riffs). Reused for the melody in riff mode, and for the dedicated chord voice.
            const string riffsSchema = @"  ""riffs"": [ { ""section"": string, ""track"": string, ""instrument"": int(GM 0..127),
      ""fromMeasure"": int, ""measures"": int,
      ""notes"": [ [hauteur, début, durée], ... ] } ]
      (chaque note = TABLEAU [hauteur MIDI (60=Do central), début en temps RELATIF au début de ce riff (0 = 1re note), durée en temps])";
            const string melodicLinesSchema = @"  ""melodicLines"": [ { ""section"": string, ""track"": string, ""instrument"": int(GM 0..127),
      ""fromMeasure"": int, ""measures"": int, ""durations"": [nombres en TEMPS],
      ""anchor"": string, ""contour"": string, ""register"": int } ]";

            // The melody block differs by mode: explicit riffs (notes) vs melodic lines (rhythm-only, engine picks pitches).
            // In "chords as voice" mode the riffs block is ALSO needed (even in melodic-line mode) for the chord voice.
            string melodyKey = riffMode ? riffsSchema
                : chordsAsVoice ? melodicLinesSchema + ",\n" + riffsSchema
                : melodicLinesSchema;

            var sb = new StringBuilder();
            sb.AppendLine("Tu es un compositeur assistant. Tu renvoies UNIQUEMENT un objet JSON (aucune prose) décrivant un morceau à poser sur une timeline.");
            sb.AppendLine("Schéma EXACT (respecte les noms de clés) :");
            sb.AppendLine("{");
            sb.AppendLine(@"  ""meter"": { ""num"": int, ""den"": int },");
            sb.AppendLine(@"  ""key"": { ""tonic"": ""C|C#|D|...|B (ou Do, Ré...)"", ""mode"": ""major|minor"" },");
            sb.AppendLine(@"  ""bpm"": int,");
            sb.AppendLine(@"  ""chordInstrument"": int(GM 0..127, instrument de la piste d'accords),");
            sb.AppendLine(@"  ""sections"": [ { ""name"": string, ""measures"": int } ],");
            sb.AppendLine(@"  ""chords"": [ [mesure, degré, ""qualité""], ... ],   (chaque accord = TABLEAU [mesure 1-based, degré 1..7 relatif à la tonalité, qualité string])");
            sb.AppendLine(@"  ""articulation"": [ { ""section"": string, ""name"": string,
      ""motif"": [ [voix, début, durée], ... ],   (chaque événement = TABLEAU [voix 0..10, début en temps, durée en temps])
      ""melodicCell"": [ [degré, début, durée], ... ] } ],   (chaque note = TABLEAU [degré 1..14, début en temps, durée en temps])");
            sb.AppendLine(melodyKey + (wantDrums ? "," : ""));
            if (wantDrums)
                sb.AppendLine(@"  ""drums"": [ { ""section"": string, ""fromMeasure"": int, ""measures"": int,
      ""motifBars"": int, ""repeats"": int,
      ""notes"": [ [note GM, début, durée], ... ] } ]   (note batterie = TABLEAU [note GM 35..81, début en temps RELATIF au début du MOTIF, durée en temps])");
            sb.AppendLine("}");
            sb.AppendLine();
            sb.AppendLine("Règles :");
            sb.AppendLine("- FORMAT COMPACT OBLIGATOIRE : réponds en JSON MINIFIÉ (aucun espace ni retour à la ligne superflu). Chaque note et chaque événement de motif est un TABLEAU ORDONNÉ (ex. [64,0,1] ou [64,0.5,1.5]), JAMAIS un objet {\"pitch\":...}. C'est ~2× plus court : tu peux ainsi écrire tout le morceau, jusqu'à la dernière section, sans être tronqué.");
            sb.AppendLine("- Tu choisis la tonalité (key), la métrique (meter) et le tempo (bpm) selon le style et l'intention.");
            sb.AppendLine("- Les accords sont donnés par DEGRÉ (chiffre 1..7 relatif à la tonalité), pas par nom absolu. Un accord par mesure (ou par changement).");
            sb.AppendLine("- COULEUR des accords : dans la qualité (3e élément du tableau accord), précise la couleur quand le style s'y prête (7e, Maj7, m7, 9, add9, 6, m6, sus2, sus4, 7sus4…), pas seulement des triades. Selon l'ambiance (Maj7/add9 = doux/rêveur, 7 dom = tension, sus = suspension).");
            sb.AppendLine("- La somme des 'measures' des sections = la longueur demandée.");
            if (chordsAsVoice)
            {
                sb.AppendLine("- ACCORDS EN VOIX DÉDIÉE : laisse 'articulation' VIDE ([]). Les 'chords' (degrés) servent UNIQUEMENT de repère harmonique — la piste d'accords reste silencieuse ; NE lui donne AUCUN motif.");
                sb.AppendLine("- À la place, écris toi-même le CONTENU des accords, mis en forme À TA GUISE, dans UNE entrée 'riffs' de rôle « Accords » (track=\"Accords\") : les vraies NOTES MIDI (renversements, espacement, arpèges ou rythme d'accompagnement de ton choix), couvrant TOUT le morceau et cohérentes avec les degrés de 'chords'. Donne-lui un 'instrument' adapté (souvent piano 0 ou harpe 46).");
            }
            else
            {
                sb.AppendLine("- Chaque section a UNE 'articulation' = un MOTIF d'accompagnement d'UNE mesure (réutilisé sur toute la section), liste d'événements 'motif'.");
                sb.AppendLine("  Chaque événement joue une VOIX de l'accord : voice = 0 basse, 1 fondamentale, 2 tierce, 3 quinte, 4 septième, 5 fondamentale(8va), 6 neuvième, 7 tierce(8va), 8 quinte(8va), 9 septième(8va), 10 neuvième(8va). 'start'/'length' en TEMPS.");
                sb.AppendLine("  Ex. plaqué 3/4 : [0,0,3],[1,0,3],[2,0,3],[3,0,3]. Valse : [0,0,1],[1,1,1],[2,1,1],[3,1,1],[1,2,1],[2,2,1],[3,2,1]. Arpège : [0,0,1],[1,1,1],[2,2,1].");
                sb.AppendLine("  Donne aussi un 'name' court et parlant à chaque articulation (ex. \"valse_couplet\", \"arpege_refrain\") : elle est enregistrée sous ce nom et réutilisable/modifiable ensuite.");
                sb.AppendLine("- CELLULE MÉLODIQUE ('melodicCell', optionnelle mais RECOMMANDÉE) : une petite phrase chantante d'UNE mesure attachée au motif, jouée en 2e voix PAR-DESSUS chaque accord de la section.");
                sb.AppendLine("  Elle s'écrit en DEGRÉS DIATONIQUES, pas en notes absolues : 1 = note d'ancrage de l'accord, 2..7 = degrés suivants de la gamme, 8..14 = les mêmes une octave au-dessus. Elle est donc TRANSPOSÉE MODALEMENT sur chaque accord (le même dessin sonne juste partout).");
                sb.AppendLine("  'start'/'length' en TEMPS, dans la mesure (comme le motif). Ex. arpège montant : [1,0,1],[3,1,1],[5,2,1]. Ex. broderie : [1,0,0.5],[2,0.5,0.5],[1,1,1]. Garde-la COURTE (3 à 6 notes) et complémentaire du motif d'accompagnement.");
            }
            if (riffMode)
            {
                sb.AppendLine("- MÉLODIE = des RIFFS : écris explicitement les NOTES (pitch MIDI + rythme). Les notes doivent tenir dans la tonalité et coller aux accords.");
                sb.AppendLine("- COUVERTURE OBLIGATOIRE : produis UNE entrée 'riffs' pour CHAQUE section de 'sections' (et pour chaque piste/rôle), du DÉBUT à la FIN du morceau. NE t'arrête PAS après la première section — le tableau 'riffs' doit couvrir toutes les sections.");
                sb.AppendLine("- Dans chaque entrée, 'start' est RELATIF au début de CE riff (0 = première note ; PAS un temps absolu depuis le début du morceau). Les notes couvrent toute la phrase : 'start' va de 0 à measures × (temps par mesure). Écris une mélodie évolutive (un motif court serait juste répété). Sois CONCIS sur le rythme pour ne pas dépasser la limite de sortie (privilégie noires/croches).");
                sb.AppendLine("- SILENCES : pour DYNAMISER la mélodie, laisse des ESPACES entre les notes — il suffit que la note suivante COMMENCE PLUS TARD que la fin de la précédente (l'écart forme le silence). Respirations, fins de phrase, question-réponse ; évite un flux ininterrompu, surtout pour le lead.");
            }
            else
            {
                sb.AppendLine("- MÉLODIE = des LIGNES : 'durations' = le motif rythmique d'une ligne (en TEMPS, ex. [1,1,0.5,0.5,1]), répété sur la partie ; le moteur choisit les hauteurs via 'contour' et 'anchor'.");
                sb.AppendLine("- Choisis 'contour' et 'anchor' de CHAQUE ligne selon l'INTENTION (ex. sombre → contour descendant/statique + ancrage tierce/fondamentale ; joyeux → montante/vague). 'register' décale le registre en demi-tons (basse ≈ -24, mélodie 0, aigu +12).");
            }
            sb.AppendLine("- VARIATION : fais VARIER le motif rythmique d'une section à l'autre (couplet ≠ refrain ≠ pont) — ne réutilise pas le même rythme partout.");
            sb.AppendLine("- CONTRECHANT : ajoute une piste 'contrechant' rythmiquement COMPLÉMENTAIRE du 'lead' — elle joue plutôt dans les silences/tenues du lead (question-réponse), parfois en même temps.");
            sb.AppendLine("- Regroupe par 'track' (rôle) : une piste par rôle. Chaque piste a son 'instrument' (GM 0..127). Ajoute AUTANT de pistes que l'orchestration le demande selon le style (ex. lead, contrechant, harmonies/nappe, arpèges, basse, doublures…) — vise une texture riche et vivante, pas seulement 2-3 voix.");
            sb.AppendLine("- INSTRUMENTS (GM 0..127) selon le style : piano 0, cordes 48/49, violoncelle 42, contrebasse 43, flûte 73, hautbois 68, clarinette 71, harpe 46, guitare 24/25, cor 60, chœur 52.");
            if (wantDrums)
            {
                sb.AppendLine("- BATTERIE ('drums') : un GROOVE = UN MOTIF COURT qui se RÉPÈTE, PAS des notes remplissant toute la section. Écris les 'notes' d'UN SEUL motif ('motifBars' mesures, souvent 1 ou 2), 'start' RELATIF au début du motif (0 à motifBars mesures). Renseigne 'motifBars' (longueur du motif) et 'repeats' (nombre de répétitions pour couvrir la section : motifBars × repeats = 'measures'). Ex. motif d'1 mesure sur une section de 4 → motifBars=1, repeats=4. Un FILL/variation en fin de section : mets-le dans le motif si motifBars couvre la section, sinon garde le motif régulier.");
                sb.AppendLine("  pitch = note batterie GM. KIT : 36 grosse caisse, 38 caisse claire, 42 charley fermé, 46 charley ouvert, 44 charley pied, 49 crash, 51 ride, 39 clap, 37 rimshot, 41/43/45/47/48/50 toms.");
                sb.AppendLine("  PERCUSSION SECONDAIRE (texture — superpose une 2e couche dans le MÊME motif selon le style, ça donne du corps/mouvement) : 54 tambourin, 56 cowbell, 69 cabasa, 70 maracas, 75 claves, 60/61 bongos, 62/63/64 congas, 76/77 wood block, 80/81 triangle. Ex. pop/rock → tambourin sur les temps ; latin/folk/bossa → shaker(cabasa/maracas) en croches + congas ; ballade → shaker léger ; valse → triangle. Dose selon l'intensité de la section.");
            }
            sb.AppendLine();
            sb.AppendLine("Valeurs autorisées :");
            sb.AppendLine("- quality (accords) : " + qualities);
            if (!riffMode)
            {
                sb.AppendLine("- contour : " + contours);
                sb.AppendLine("- anchor : " + anchors);
            }
            return sb.ToString();
        }

        public static string UserPrompt(string style, int measures, string intention, string theme = null)
        {
            style = string.IsNullOrWhiteSpace(style) ? "libre" : style.Trim();
            var sb = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(theme))
            {
                sb.Append($"DÉVELOPPE / VARIE le thème ci-dessous sur environ {Math.Max(1, measures)} mesures (développement : variation motivique, séquences, réharmonisation, modulations passagères — garde l'esprit et des fragments reconnaissables du thème). ");
                sb.Append("CHOISIS les accords du développement. GARDE la même tonalité et la même métrique que le thème (les mesures 'measure'/'fromMeasure' repartent de 1 pour le développement). ");
                sb.Append($"Style : « {style} ».");
                if (!string.IsNullOrWhiteSpace(intention)) sb.Append($" Intention : « {intention.Trim()} ».");
                sb.Append("\n\n").Append(theme.Trim());
                sb.Append("\n\nRenvoie UNIQUEMENT l'objet JSON (mêmes clés que le schéma).");
                return sb.ToString();
            }
            sb.Append($"Compose un morceau dans le style : « {style} », d'environ {Math.Max(1, measures)} mesures.");
            if (!string.IsNullOrWhiteSpace(intention)) sb.Append($" Intention musicale : « {intention.Trim()} ».");
            sb.Append(" Choisis tonalité, métrique et tempo cohérents. Renvoie UNIQUEMENT l'objet JSON.");
            return sb.ToString();
        }

        static string First(string s) { int i = s.IndexOf(' '); return i > 0 ? s.Substring(0, i) : s; }
    }

    /// <summary>Pure name→index / key / rhythm helpers turning an <see cref="AiArrangement"/> into engine values.</summary>
    public static class AiTranslate
    {
        // --- enum name matching (tolerant: a numeric string = direct index; else exact, leading-word, contains) ---
        public static int ContourIndex(string name) => IndexOrMatch(name, MelodicLineEngine.ContourNames);
        public static int AnchorIndex(string name) => IndexOrMatch(name, MelodicLineEngine.AnchorNames);
        public static int StyleIndex(string name) => IndexOrMatch(name, PatternGenerator.StyleNames);

        static int IndexOrMatch(string name, string[] table)
        {
            if (int.TryParse((name ?? "").Trim(), out int idx)) return Math.Max(0, Math.Min(table.Length - 1, idx));
            return MatchIndex(name, table, 0);
        }

        public static int QualityIndex(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return 0;
            int exact = MatchIndex(name, PatternGenerator.QualityNames, -1);
            if (exact >= 0) return exact;
            string n = Norm(name);
            // common aliases the model tends to use
            switch (n)
            {
                case "maj": case "major": return 0;
                case "min": case "minor": case "m": return 1;
                case "dim": return 2;
                case "aug": return 3;
                case "maj7": case "major7": case "M7": return 6;
                case "m7": case "min7": case "minor7": return 7;
                case "7": case "dom7": case "dominant7": return 8;
                case "m7b5": case "halfdim": return 9;
                case "dim7": return 10;
                case "6": return 11;
                case "m6": return 12;
                case "add9": return 13;
                case "9": case "dom9": return 15;
                case "maj9": return 16;
                case "m9": case "min9": return 17;
            }
            return 0;
        }

        // Diatonic root pitch-class of a 1-based degree in the given key.
        public static int RootPc(KeySignature key, int degree1based)
        {
            int tonic = MusicTheory.TonicPc(key ?? new KeySignature());
            int[] scale = MusicalMode.Scale(MusicalMode.Effective(key ?? new KeySignature()));
            int d = (((degree1based - 1) % 7) + 7) % 7;
            return ((tonic + scale[d]) % 12 + 12) % 12;
        }

        // Pitch-classes of the chord at a degree+quality in the key (root + quality intervals).
        public static int[] ChordPcs(KeySignature key, int degree1, string quality)
        {
            int root = RootPc(key, degree1);
            var midis = PatternGenerator.ChordNotes(root, 4, QualityIndex(quality), 0);
            var set = new HashSet<int>();
            foreach (var m in midis) set.Add(((m % 12) + 12) % 12);
            return set.ToArray();
        }

        // Diatonic scale pitch-classes of the key.
        public static int[] ScalePcs(KeySignature key)
        {
            int tonic = MusicTheory.TonicPc(key ?? new KeySignature());
            var set = new HashSet<int>();
            foreach (var off in MusicalMode.Scale(MusicalMode.Effective(key ?? new KeySignature()))) set.Add(((tonic + off) % 12 + 12) % 12);
            return set.ToArray();
        }

        // Nearest MIDI note to `midi` whose pitch-class is in `pcs` (smallest |semitone| move). Returns midi unchanged if empty.
        public static int SnapMidiToPcs(int midi, int[] pcs)
        {
            if (pcs == null || pcs.Length == 0) return midi;
            int best = midi, bestAbs = int.MaxValue;
            foreach (int pc in pcs)
            {
                int delta = (((pc - midi) % 12) + 12) % 12;
                if (delta > 6) delta -= 12;              // choose the closer octave (±6 semitones)
                if (Math.Abs(delta) < bestAbs) { bestAbs = Math.Abs(delta); best = midi + delta; }
            }
            return best;
        }

        public static KeySignature ParseKey(AiKey k)
        {
            var key = new KeySignature();
            if (k == null) return key;
            key.Mode = (k.mode != null && k.mode.Trim().ToLowerInvariant().StartsWith("min")) ? 1 : 0;
            ParseTonic(k.tonic, out int letter, out int acc);
            key.TonicLetter = letter; key.Accidental = acc;
            return key;
        }

        // Custom chord-articulation motif → a note list where Note = grid voice-row (0..CustomVoiceCount-1), start/length
        // converted from temps to slices. One bar of motif; the caller tiles/trims it per chord.
        public static List<RiffNote> BuildArticulationNotes(List<AiArtNote> motif, int spq)
        {
            var notes = new List<RiffNote>();
            if (motif == null) return notes;
            int maxRow = PatternGenerator.CustomVoiceCount - 1;
            foreach (var e in motif)
            {
                int row = Math.Max(0, Math.Min(maxRow, e.voice));
                int start = Math.Max(0, (int)Math.Round(e.start * spq));
                int len = Math.Max(1, (int)Math.Round(e.length * spq));
                notes.Add(new RiffNote(row, start, len));
            }
            return notes;
        }

        // Melodic-cell notes (diatonic degree + start/length in temps) → a note list on the MELODIC grid, whose rows are
        // the key's 7 diatonic degrees over 2 octaves (PatternGenerator.MelodicRowCount = 14). Row = degree−1, so degree 1
        // is the chord's anchor and 8..14 repeat the degrees an octave up. PatternGenerator.GenerateMelodic then resolves
        // each row to a real pitch per chord, which is what makes the cell transpose MODALLY from chord to chord.
        public static List<RiffNote> BuildMelodicCellNotes(List<AiCellNote> cell, int spq)
        {
            var notes = new List<RiffNote>();
            if (cell == null) return notes;
            int maxRow = PatternGenerator.MelodicRowCount - 1;
            foreach (var e in cell)
            {
                int row = Math.Max(0, Math.Min(maxRow, e.degree - 1)); // degrees are 1-based on the wire
                int start = Math.Max(0, (int)Math.Round(e.start * spq));
                int len = Math.Max(1, (int)Math.Round(e.length * spq));
                notes.Add(new RiffNote(row, start, len));
            }
            return notes;
        }

        // Explicit riff notes (pitch MIDI + start/length in temps) → a Riff note list (Note = MIDI−12). `startOffsetTemps`
        // is subtracted first (models sometimes give ABSOLUTE starts from the piece start; the caller passes the riff's
        // fromMeasure offset to make them relative). If the model only wrote the first bar(s), the motif is TILED to fill.
        public static List<RiffNote> BuildRiffNotes(List<AiRiffNote> notes, int totalSlices, int spq, int barSlices, double startOffsetTemps = 0)
        {
            var one = new List<RiffNote>();
            if (notes != null)
                foreach (var n in notes)
                {
                    int start = Math.Max(0, (int)Math.Round((n.start - startOffsetTemps) * spq));
                    if (start >= totalSlices) continue;
                    int len = Math.Max(1, (int)Math.Round(n.length * spq));
                    if (start + len > totalSlices) len = totalSlices - start;
                    if (len >= 1) one.Add(new RiffNote(Math.Max(0, Math.Min(95, n.pitch - 12)), start, len)); // Note 0 == MIDI 12
                }
            if (one.Count == 0 || barSlices < 1) return one;

            int maxEnd = 0; foreach (var n in one) maxEnd = Math.Max(maxEnd, n.Start + n.Length);
            int motif = Math.Max(barSlices, ((maxEnd + barSlices - 1) / barSlices) * barSlices); // bar-aligned motif span
            if (motif >= totalSlices) return one; // already fills the phrase (or a single block)

            var outp = new List<RiffNote>();
            for (int off = 0; off < totalSlices; off += motif)
                foreach (var n in one)
                {
                    int s = off + n.Start;
                    if (s >= totalSlices) continue;
                    int len = Math.Min(n.Length, totalSlices - s);
                    if (len >= 1) outp.Add(new RiffNote(n.Note, s, len));
                }
            return outp;
        }

        // Rhythm motif (durations in temps) → a MelodicLine note list (voice-row 0), tiled to fill totalSlices.
        public static List<RiffNote> BuildRhythmNotes(List<double> durations, int totalSlices, int spq)
        {
            var notes = new List<RiffNote>();
            if (totalSlices <= 0) return notes;
            var durs = (durations != null && durations.Count > 0) ? durations : new List<double> { 1.0 };
            int pos = 0, i = 0, guard = 0;
            while (pos < totalSlices && guard++ < 100000)
            {
                double d = durs[i % durs.Count]; i++;
                if (d < 0)   // a NEGATIVE duration = a REST (silence): advance the cursor, add no note
                {
                    pos += Math.Max(1, (int)Math.Round(-d * spq));
                    continue;
                }
                int len = Math.Max(1, (int)Math.Round(d * spq));
                if (pos + len > totalSlices) len = totalSlices - pos;
                if (len <= 0) break;
                notes.Add(new RiffNote(0, pos, len)); // Note = voice row 0; the engine derives the pitch
                pos += len;
            }
            return notes;
        }

        // --- helpers ---
        static int MatchIndex(string name, string[] table, int fallback)
        {
            if (string.IsNullOrWhiteSpace(name) || table == null) return fallback;
            string n = Norm(name);
            for (int i = 0; i < table.Length; i++) if (Norm(table[i]) == n) return i;                 // exact
            for (int i = 0; i < table.Length; i++) if (Norm(table[i]).StartsWith(n) && n.Length >= 3) return i; // leading word
            for (int i = 0; i < table.Length; i++) if (n.Length >= 4 && Norm(table[i]).Contains(n)) return i;   // contains
            return fallback;
        }

        static string Norm(string s)
        {
            if (s == null) return "";
            var sb = new StringBuilder();
            foreach (char c in s.ToLowerInvariant())
                if (char.IsLetterOrDigit(c)) sb.Append(c);
            return sb.ToString();
        }

        static void ParseTonic(string t, out int letter, out int acc)
        {
            letter = 0; acc = 0;
            if (string.IsNullOrWhiteSpace(t)) return;
            t = t.Trim();
            char c0 = char.ToUpperInvariant(t[0]);
            // English letters A..G → letter index (Do=0..Si=6): C D E F G A B
            int idx = "CDEFGAB".IndexOf(c0);
            if (idx >= 0) letter = idx;
            else
            {
                string low = Norm(t);
                string[] fr = { "do", "re", "mi", "fa", "sol", "la", "si" };
                for (int i = 0; i < fr.Length; i++) if (low.StartsWith(fr[i])) { letter = i; break; }
            }
            if (t.Contains("#") || t.ToLowerInvariant().Contains("dièse") || t.ToLowerInvariant().Contains("diese") || t.ToLowerInvariant().Contains("sharp")) acc = 1;
            else if (t.Contains("b") && t.Length > 1 || t.ToLowerInvariant().Contains("bémol") || t.ToLowerInvariant().Contains("bemol") || t.ToLowerInvariant().Contains("flat")) acc = -1;
        }
    }
}
