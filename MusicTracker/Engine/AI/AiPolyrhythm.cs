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
    /// <summary>
    /// Contrat JSON d'une composition POLYRYTHMIQUE générée par l'IA — pendant polyrythmique de
    /// <see cref="AiArrangement"/>.
    ///
    /// L'IA renvoie UN objet contenant :
    ///  - une ligne de batterie polyrythmique (calques euclidiens, cf. <see cref="PolyDrumModule"/>) ;
    ///  - une ligne mélodique polyrythmique (voix euclidiennes, cf. <see cref="MelodicPolyModule"/>) ;
    ///  - éventuellement UN accord tenu qui ancre l'harmonie (le moteur mélodique s'en sert pour les hauteurs).
    ///
    /// Comme pour <see cref="AiArrangement"/>, la classe est aussi le SCHÉMA de sortie : les noms de champs
    /// correspondent à ce que l'IA doit produire. Pas de contrat par-provider — on passe par
    /// <see cref="AiProviders.CompleteJsonAsync"/> exactement comme les autres surfaces IA.
    /// </summary>
    public class AiPolyrhythm
    {
        public int bpm { get; set; } = 108;
        public AiPolyKey key { get; set; } = new AiPolyKey();
        public int durationBeats { get; set; } = 32;
        public AiPolyDrum drum { get; set; }
        public AiPolyMelodic melodic { get; set; }
        public AiPolyChord chord { get; set; }

        static readonly JsonSerializerOptions Opts = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            AllowTrailingCommas = true,
            NumberHandling = JsonNumberHandling.AllowReadingFromString,
        };

        public static AiPolyrhythm Parse(string json)
            => JsonSerializer.Deserialize<AiPolyrhythm>(StripFences(json), Opts) ?? throw new InvalidOperationException("Réponse IA vide.");

        // Retire les fences ```json ... ``` qu'un modèle bavard ajoute parfois autour du JSON pur.
        static string StripFences(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "{}";
            s = s.Trim();
            if (s.StartsWith("```"))
            {
                int nl = s.IndexOf('\n'); if (nl > 0) s = s.Substring(nl + 1);
                int end = s.LastIndexOf("```"); if (end >= 0) s = s.Substring(0, end);
            }
            return s.Trim();
        }
    }

    public class AiPolyKey { public string tonic { get; set; } = "C"; public string mode { get; set; } = "major"; }

    public class AiPolyDrum
    {
        public int kit { get; set; } = 0;
        public List<AiPolyDrumLayer> layers { get; set; } = new List<AiPolyDrumLayer>();
    }
    public class AiPolyMelodic
    {
        public int instrument { get; set; } = 12;                       // GM program (12 = Marimba)
        public List<AiPolyMelodicLayer> layers { get; set; } = new List<AiPolyMelodicLayer>();
    }
    public class AiPolyDrumLayer
    {
        public int lane { get; set; }                                    // index de lane DrumPattern.LaneNames
        public int hits { get; set; } = 3;
        public int steps { get; set; } = 8;
        public int rotation { get; set; } = 0;
        public int stepSlices { get; set; } = 12;                        // 24 noire, 12 croche, 8 triolet-croche, 6 double-croche
    }
    public class AiPolyMelodicLayer
    {
        public int voice { get; set; }                                   // 0..MelodicLineModule.MaxVoices-1
        public int hits { get; set; } = 3;
        public int steps { get; set; } = 8;
        public int rotation { get; set; } = 0;
        public int stepSlices { get; set; } = 12;
        public bool legato { get; set; }
    }
    public class AiPolyChord
    {
        public string root { get; set; } = "C";
        public string quality { get; set; } = "major";
        public int octave { get; set; } = 3;
    }

    /// <summary>Construit le prompt system+user pour l'IA. C'est le seul endroit où la « théorie »
    /// polyrythmique est expliquée au modèle — le reste (dispatch, appel HTTP) réutilise <see cref="AiProviders"/>.</summary>
    public static class AiPolyrhythmPrompt
    {
        public static string SystemPrompt()
        {
            var sb = new StringBuilder();
            sb.AppendLine("Tu es un compositeur assistant SPÉCIALISÉ en RYTHMES POLYRYTHMIQUES. Tu renvoies UNIQUEMENT un objet JSON (aucune prose autour).");
            sb.AppendLine("MODÈLE : tous les calques partagent le MÊME cycle (durée = `beats` temps). Chaque calque découpe ce cycle en son propre nombre de PAS (steps), et une frappe tombe sur les cases actives d'un pattern euclidien E(hits,steps) éventuellement décalé par rotation. La polyrythmie vient de ce que 3 pas contre 5 pas dans le même cycle produisent des ratios musicalement intéressants (3-contre-5, 7-contre-4…).");
            sb.AppendLine();
            sb.AppendLine("Schéma EXACT :");
            sb.AppendLine("{");
            sb.AppendLine("  \"bpm\": int,                            // tempo (60..200)");
            sb.AppendLine("  \"key\": { \"tonic\": \"C|C#|D|Eb|E|F|F#|G|Ab|A|Bb|B\", \"mode\": \"major|minor\" },");
            sb.AppendLine("  \"durationBeats\": int,                  // durée totale en TEMPS (indicatif ; le module la répartit en cycle × répétitions)");
            sb.AppendLine("  \"drum\": {");
            sb.AppendLine("    \"kit\": 0,                            // toujours 0 (Standard) sauf demande explicite");
            sb.AppendLine("    \"layers\": [ { \"lane\": int, \"hits\": int, \"steps\": int, \"rotation\": int }, ... ]");
            sb.AppendLine("  },");
            sb.AppendLine("  \"melodic\": {");
            sb.AppendLine("    \"instrument\": int,                    // programme GM 0..127 (ex. 12 marimba, 108 kalimba, 73 flûte, 46 harpe)");
            sb.AppendLine("    \"layers\": [ { \"voice\": 0|1|2, \"hits\": int, \"steps\": int, \"rotation\": int, \"legato\": bool }, ... ]");
            sb.AppendLine("  },");
            sb.AppendLine("  \"chord\": { \"root\": \"...\", \"quality\": \"major|minor|sus2|sus4|dim|aug\", \"octave\": int }");
            sb.AppendLine("}");
            sb.AppendLine();
            sb.AppendLine("RÈGLES DES CALQUES :");
            sb.AppendLine(" - hits ∈ [1, steps] ; steps ∈ [2, 32] ; rotation ∈ [0, steps-1].");
            sb.AppendLine(" - Le \"cycle commun\" = ~4 temps (par défaut). Les calques diffèrent par leur NOMBRE DE PAS (steps).");
            sb.AppendLine(" - Choisis des paires de steps mutuellement PREMIÈRES pour un beau polyrythme (3&5, 4&7, 5&8, 7&12…). Éviter des steps identiques sur plusieurs calques.");
            sb.AppendLine(" - Vise 3 à 5 calques de batterie et 2 à 3 voix mélodiques ; c'est ce qui rend le polyrythme LISIBLE.");
            sb.AppendLine();
            sb.AppendLine("LANES DE PERCUSSION (index → nom) :");
            sb.AppendLine(" 0 grosse caisse · 1 caisse claire · 2 charley fermé · 3 charley ouvert · 5 rimshot · 6 clap");
            sb.AppendLine(" 7 tom basse · 8 tom médium · 9 tom aigu · 10 crash · 11 ride");
            sb.AppendLine(" 22 tambourin · 23 cowbell · 24 vibraslap");
            sb.AppendLine(" 25 bongo aigu · 26 bongo grave · 27 conga aigu étouffé · 28 conga aigu · 29 conga grave");
            sb.AppendLine(" 30 timbale aiguë · 31 timbale grave (dundun) · 32 agogo aigu · 33 agogo grave");
            sb.AppendLine(" 34 cabasa · 35 maracas · 40 claves · 45/46 triangle");
            sb.AppendLine();
            sb.AppendLine("VOIX MÉLODIQUES : voice=0 = basse, voice=1 = médium, voice=2 = aigu (le moteur choisit ensuite les hauteurs sur les accords).");
            sb.AppendLine(" - legato = true : la note dure jusqu'à la suivante (utile pour les nappes/soutiens) ; false = coup court.");
            return sb.ToString();
        }

        public static string UserPrompt(string style, int measures, string intention, int meterNum)
        {
            var sb = new StringBuilder();
            int barTemps = Math.Max(1, meterNum);
            sb.Append($"Compose une pièce POLYRYTHMIQUE de {measures} mesure(s) en {meterNum}/4 (soit {measures * barTemps} temps). ");
            sb.Append($"Style : « {(style ?? "").Trim()} ». ");
            if (!string.IsNullOrWhiteSpace(intention)) sb.Append($"Intention : « {intention.Trim()} ». ");
            sb.Append("Renvoie UNIQUEMENT le JSON, minifié.");
            return sb.ToString();
        }
    }

    /// <summary>Traduit un <see cref="AiPolyrhythm"/> parsé en un projet POSABLE dans un nouvel onglet : une piste
    /// percussion (PolyDrumModule), une piste mélodique (MelodicPolyModule) et, si un accord est fourni, une piste
    /// d'accord (PatternGeneratorModule tenu) pour que le moteur mélodique ait de l'harmonie à lire.
    ///
    /// Comme <see cref="AiArrangementPlacer.BuildFresh"/>, on renvoie un <see cref="TimelineDocument"/> autonome :
    /// c'est le shell (MainWindow) qui l'ouvre sur un nouveau tab, sans muter la timeline courante.</summary>
    public static class AiPolyPlacer
    {
        public static TimelineDocument BuildFresh(AiPolyrhythm arr)
        {
            var project = new TimelineProject();
            if (arr == null) return new TimelineDocument { Project = project };

            // Clamp silencieusement — on préfère poser la pièce quitte à corriger un chiffre absurde plutôt qu'échouer.
            int dur = Math.Max(1, arr.durationBeats);

            // MainBpm est calculé (getter) — on l'ajuste via Tempo[0].
            double bpm = Math.Max(30, Math.Min(400, arr.bpm > 0 ? arr.bpm : 108));
            project.Tempo = new List<TempoChange> { new TempoChange { Beat = 0, Bpm = bpm } };
            project.Key = KeyFromAi(arr.key);
            project.TimeSigNum = 4; project.TimeSigDen = 4;

            // ---- batterie -------------------------------------------------------------------------------------
            // Nouveau modèle : Beats (temps par CYCLE) × Repeats. On répartit sagement la durée totale voulue.
            int cycleBeats = Math.Max(2, Math.Min(dur, 8));
            int repeats = Math.Max(1, (int)Math.Round(dur / (double)cycleBeats));
            var pd = new PolyDrumModule
            {
                Kit = Math.Max(0, Math.Min(InstrumentCatalog.DrumKits().Count - 1, arr.drum?.kit ?? 0)),
                Beats = cycleBeats, Repeats = repeats,
            };
            if (arr.drum?.layers != null)
                foreach (var l in arr.drum.layers) pd.Layers.Add(SanitizeDrumLayer(l));
            var drumTrack = new TimelineTrack
            {
                Name = "Percussions poly", Type = TimelineTrackType.Drum, Instrument = 0, DrumKit = pd.Kit, Volume = 1.0,
            };
            drumTrack.Items.Add(new TimelineItem { SilenceBefore = 0, Module = pd });
            project.Tracks.Add(drumTrack);

            // ---- ligne mélodique ------------------------------------------------------------------------------
            var mp = new MelodicPolyModule { Beats = cycleBeats, Repeats = repeats };
            if (arr.melodic?.layers != null)
                foreach (var v in arr.melodic.layers) mp.Layers.Add(SanitizeMelodicLayer(v));
            if (mp.Layers.Count == 0) mp.Layers.Add(new EuclidVoice { Voice = 0, Hits = 3, Steps = 8 });
            MelodicEuclid.Renumber(mp.Layers);

            int instr = arr.melodic?.instrument ?? 12;
            if (instr < 0 || instr >= 128) instr = 12;
            var melTrack = new TimelineTrack
            {
                Name = "Ligne poly", Type = TimelineTrackType.Instrument, Instrument = instr, Volume = 0.9,
            };
            melTrack.Items.Add(new TimelineItem { SilenceBefore = 0, Module = mp });
            project.Tracks.Add(melTrack);

            // ---- accord tenu (optionnel) : sert D'HARMONIE au moteur mélodique. Sans lui, les voix "flottent". -
            if (arr.chord != null)
            {
                var chord = ChordFromAi(arr.chord, dur);
                if (chord != null)
                {
                    var chordTrack = new TimelineTrack
                    {
                        Name = "Accords", Type = TimelineTrackType.Chord, Instrument = 46, Volume = 0.55,   // harpe = 46
                    };
                    chordTrack.Items.Add(new TimelineItem { SilenceBefore = 0, Module = chord });
                    project.Tracks.Add(chordTrack);
                }
            }

            return new TimelineDocument { Project = project };
        }

        static EuclidLayer SanitizeDrumLayer(AiPolyDrumLayer l)
        {
            int steps = Math.Max(2, Math.Min(32, l.steps));
            int hits  = Math.Max(1, Math.Min(steps, l.hits));
            int rot   = ((l.rotation % steps) + steps) % steps;
            return new EuclidLayer
            {
                Lane = Math.Max(0, Math.Min(DrumPattern.LaneCount - 1, l.lane)),
                Hits = hits, Steps = steps, Rotation = rot,
            };
        }
        static EuclidVoice SanitizeMelodicLayer(AiPolyMelodicLayer v)
        {
            int steps = Math.Max(2, Math.Min(32, v.steps));
            int hits  = Math.Max(1, Math.Min(steps, v.hits));
            int rot   = ((v.rotation % steps) + steps) % steps;
            return new EuclidVoice
            {
                Voice = Math.Max(0, Math.Min(MelodicLineModule.MaxVoices - 1, v.voice)),
                Hits = hits, Steps = steps, Rotation = rot,
                Legato = v.legato,
            };
        }

        static KeySignature KeyFromAi(AiPolyKey k)
        {
            if (k == null) return new KeySignature();
            var (letter, acc) = ParseTonic(k.tonic);
            return new KeySignature { TonicLetter = letter, Accidental = acc, Mode = (k.mode ?? "").Trim().ToLowerInvariant().StartsWith("min") ? 1 : 0, FullMode = -1 };
        }
        // Do..Si = 0..6 ; on accepte à la fois les noms français et anglais pour les tolérances de l'IA.
        static (int letter, int acc) ParseTonic(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return (0, 0);
            s = s.Trim(); int acc = 0;
            if (s.Length > 1)
            {
                char last = s[s.Length - 1];
                if (last == '#' || last == '♯') { acc = 1; s = s.Substring(0, s.Length - 1); }
                else if (last == 'b' || last == '♭') { acc = -1; s = s.Substring(0, s.Length - 1); }
            }
            int letter;
            switch (s.Trim().ToUpperInvariant())
            {
                case "C": case "DO": letter = 0; break;
                case "D": case "RE": case "RÉ": letter = 1; break;
                case "E": case "MI": letter = 2; break;
                case "F": case "FA": letter = 3; break;
                case "G": case "SOL": letter = 4; break;
                case "A": case "LA": letter = 5; break;
                case "B": case "SI": case "H": letter = 6; break;
                default: letter = 0; break;
            }
            return (letter, acc);
        }

        static PatternGeneratorModule ChordFromAi(AiPolyChord c, int durationBeats)
        {
            if (c == null) return null;
            int root = TonicPcFromString(c.root);
            int quality = QualityIndex(c.quality);
            return new PatternGeneratorModule
            {
                Root = root, Octave = Math.Max(1, Math.Min(7, c.octave > 0 ? c.octave : 3)),
                Quality = quality, Inversion = 0, Degree = 0,
                Style = 0, BeatsPerBar = Math.Max(1, durationBeats), Repeats = 1,
                Bass = true, BassPerBeat = false,
            };
        }
        // pitch-class (0..11) à partir du nom de note
        static int TonicPcFromString(string s)
        {
            var (letter, acc) = ParseTonic(s);
            int[] letterPc = { 0, 2, 4, 5, 7, 9, 11 };
            return ((letterPc[letter] + acc) % 12 + 12) % 12;
        }
        // Index dans PatternGenerator.QualityNames : 0 Majeur, 1 Mineur, 2 Diminué, 3 Augmenté, 4 Sus2, 5 Sus4
        static int QualityIndex(string q)
        {
            switch ((q ?? "").Trim().ToLowerInvariant())
            {
                case "min": case "minor": case "mineur": case "m": return 1;
                case "dim": case "diminished": case "diminué": case "°": return 2;
                case "aug": case "augmented": case "augmenté": case "+": return 3;
                case "sus2": return 4;
                case "sus4": return 5;
                default: return 0;
            }
        }
    }
}
