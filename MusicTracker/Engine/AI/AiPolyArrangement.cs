using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using MusicTracker.Engine.Flow;
using MusicTracker.Engine.Timeline;

namespace MusicTracker.Engine.AI
{
    /// <summary>
    /// OPTIONS POLYRYTHMIQUES de « Composer avec l'IA » (<see cref="AiArrangement"/>) — deux TABLEAUX JSON
    /// FACULTATIFS qui ne changent que la FAÇON DE JOUER une piste, jamais la matière musicale :
    ///
    ///  • <c>polyChords</c> : la piste d'accords est rendue par une SUITE de <see cref="PolyChordModule"/> (roues
    ///    d'anneaux euclidiens) au lieu d'un module d'accords par mesure. La PROGRESSION reste celle du champ
    ///    <c>chords</c> déjà existant (degré par mesure) — le reste du placeur (sections, articulations, lignes
    ///    mélodiques, correction d'harmonie des riffs) continue de la lire telle quelle. Chaque entrée ne décrit
    ///    donc QUE sa PLAGE de mesures, ses anneaux et les réglages de sa roue.
    ///  • <c>polyDrums</c> : la batterie est rendue par une suite de <see cref="PolyDrumModule"/>, au lieu des
    ///    phrases de percussion du champ <c>drums</c> (qui n'est alors pas demandé au modèle).
    ///
    /// UNE ENTRÉE = UN MODULE de 1 à 4 mesures (<see cref="AiPolyModules.MaxSpanMeasures"/>) : c'est ce qui permet
    /// PLUSIEURS polyrythmes différents dans un même morceau (au moins un par section) — le but étant la VARIATION —
    /// et ça garde chaque bloc court, donc éditable et léger à afficher.
    ///
    /// Absents du JSON (= option décochée dans le dialogue) ⇒ le placeur garde EXACTEMENT son comportement
    /// historique, et le prompt ne porte aucun fragment de schéma polyrythmique (chaque requête reste aussi
    /// courte qu'avant).
    ///
    /// COMPATIBILITÉ : la forme historique — un OBJET unique au lieu d'un tableau — reste acceptée
    /// (<see cref="SingleOrListConverter{T}"/>) et vaut une entrée unique couvrant TOUT le morceau, à l'identique
    /// d'avant le passage au tableau.
    ///
    /// Ce chemin est INDÉPENDANT de <see cref="AiPolyrhythm"/> / <c>AiPolyDialog</c> (qui compose une pièce
    /// polyrythmique complète depuis zéro) : on ne partage que la façon d'expliquer la polyrythmie au modèle.
    /// </summary>
    public abstract class AiPolyRangeSpec
    {
        /// <summary>Première mesure couverte par cette entrée, 1-based. 0/absent = à la suite de l'entrée précédente.</summary>
        public int fromMeasure { get; set; }
        /// <summary>Nombre de mesures couvertes, borné à <see cref="AiPolyModules.MaxSpanMeasures"/>.
        /// 0/absent = le maximum (sauf entrée UNIQUE sans plage : forme historique = tout le morceau).</summary>
        public int measures { get; set; }
    }

    public class AiPolyChordSpec : AiPolyRangeSpec
    {
        /// <summary>Durée du CYCLE de la roue, en temps — indépendante de la durée des accords.</summary>
        public int cycleBeats { get; set; } = 4;
        /// <summary>Octave de base du voicing (4 = Do central).</summary>
        public int octave { get; set; } = 4;
        /// <summary>"perTone" (un anneau = une note fixe du voicing) ou "sweep" (l'anneau parcourt les notes).</summary>
        [JsonConverter(typeof(FlexibleStringConverter))] public string mode { get; set; }
        /// <summary>Mode sweep : quelle note prendre au changement d'accord (nearest/grave/aigu/tonic/tierce/quinte).</summary>
        [JsonConverter(typeof(FlexibleStringConverter))] public string restart { get; set; }
        public bool openVoicing { get; set; }
        public List<AiPolyChordRing> layers { get; set; }
    }

    /// <summary>Un anneau d'accords : motif euclidien E(hits,steps) tourné de rotation, joué sur une note du
    /// voicing (<see cref="toneIndex"/>, mode perTone) ou en parcourant les notes (<see cref="contour"/>, sweep).</summary>
    public class AiPolyChordRing
    {
        public int toneIndex { get; set; }
        [JsonConverter(typeof(FlexibleStringConverter))] public string contour { get; set; }
        public int hits { get; set; } = 3;
        public int steps { get; set; } = 8;
        public int rotation { get; set; }
        /// <summary>Décalage en OCTAVES de cet anneau (−3..+3), appliqué après le voicing.</summary>
        public int octave { get; set; }
        public bool legato { get; set; }
    }

    public class AiPolyDrumSpec : AiPolyRangeSpec
    {
        public int kit { get; set; }
        /// <summary>Durée du cycle commun à tous les anneaux, en temps.</summary>
        public int cycleBeats { get; set; } = 4;
        public List<AiPolyDrumRing> layers { get; set; }
    }

    /// <summary>Un anneau de percussion : E(hits,steps) tourné de rotation, sur la lane
    /// <see cref="lane"/> (index dans <see cref="DrumPattern.LaneNames"/>, PAS une note MIDI).</summary>
    public class AiPolyDrumRing
    {
        public int lane { get; set; }
        public int hits { get; set; } = 3;
        public int steps { get; set; } = 8;
        public int rotation { get; set; }
        /// <summary>Instrument joué sur le PREMIER coup de chaque tour de motif ; −1 (ou absent) = aucun accent.</summary>
        public int accentLane { get; set; } = -1;
    }

    /// <summary>Lit une liste qui peut arriver SOIT en tableau, SOIT en objet unique (forme historique de
    /// <c>polyChords</c>/<c>polyDrums</c>, qu'une réponse déjà générée peut porter). Un objet seul devient une
    /// liste d'un élément ; tout autre token est ignoré (liste vide) plutôt que de faire échouer le parse entier.</summary>
    public class SingleOrListConverter<T> : JsonConverter<List<T>> where T : class
    {
        public override List<T> Read(ref System.Text.Json.Utf8JsonReader r, Type t, System.Text.Json.JsonSerializerOptions o)
        {
            var list = new List<T>();
            if (r.TokenType == System.Text.Json.JsonTokenType.StartArray)
            {
                while (r.Read() && r.TokenType != System.Text.Json.JsonTokenType.EndArray)
                {
                    if (r.TokenType != System.Text.Json.JsonTokenType.StartObject) { r.Skip(); continue; }
                    var v = System.Text.Json.JsonSerializer.Deserialize<T>(ref r, o);
                    if (v != null) list.Add(v);
                }
            }
            else if (r.TokenType == System.Text.Json.JsonTokenType.StartObject)
            {
                var v = System.Text.Json.JsonSerializer.Deserialize<T>(ref r, o);
                if (v != null) list.Add(v);
            }
            else r.Skip();
            return list;
        }
        public override void Write(System.Text.Json.Utf8JsonWriter w, List<T> v, System.Text.Json.JsonSerializerOptions o)
            => System.Text.Json.JsonSerializer.Serialize(w, v, o);
    }

    /// <summary>Une entrée polyrythmique RÉSOLUE : son spec + la plage de mesures (1-based) qu'elle occupe
    /// réellement, après bornage/dédoublonnage/comblage par <see cref="AiPolyModules.Plan{T}"/>.</summary>
    public class PolySpan<T> where T : AiPolyRangeSpec
    {
        public T Spec;
        public int FromMeasure;   // 1-based
        public int Measures;
        /// <summary>Vrai quand cette plage n'était PAS décrite par le modèle : elle reprend les anneaux de
        /// l'entrée voisine pour ne pas laisser de trou dans la piste d'accords.</summary>
        public bool Filler;
    }

    /// <summary>Fragments de prompt + traduction spec → modules pour les deux options polyrythmiques. Tout est
    /// BORNÉ silencieusement (plage, hits/steps/rotation/lane/octave) : on préfère poser quelque chose de jouable
    /// plutôt que refuser une réponse dont un chiffre est absurde.</summary>
    public static class AiPolyModules
    {
        const int MinSteps = 2, MaxSteps = 32;

        /// <summary>Longueur MAXIMALE d'un module polyrythmique, en mesures. Un module court = un polyrythme par
        /// section (voire par bloc de 4 mesures) au lieu d'une roue unique et statique sur tout le morceau, et un
        /// éditeur qui reste léger (la liste d'accords d'un module de 4 mesures tient en quelques lignes).</summary>
        public const int MaxSpanMeasures = 4;

        /// <summary>Résout les plages des entrées : ordre de déclaration, bornage, recouvrements et trous.
        ///
        /// Règles (arbitrages assumés, tous silencieux — on pose la musique plutôt que d'échouer) :
        ///  • <c>fromMeasure</c> &lt; 1 ou absent ⇒ l'entrée s'enchaîne juste après la précédente ;
        ///  • RECOUVREMENT ⇒ l'entrée qui recouvre est REPOUSSÉE à la fin de la précédente (rien n'est jeté :
        ///    le modèle a décrit N polyrythmes, on en garde N) ;
        ///  • <c>fromMeasure</c> au-delà de la fin du morceau ⇒ entrée ignorée (il n'y a rien à jouer là) ;
        ///  • <c>measures</c> hors [1, <see cref="MaxSpanMeasures"/>] ⇒ borné (0/absent ⇒ le maximum) ;
        ///  • entrée UNIQUE sans plage déclarée ⇒ tout le morceau (forme historique, cf. SingleOrListConverter) ;
        ///  • TROU (mesures que personne ne couvre) : comblé si <paramref name="fillGaps"/> — en RÉPÉTANT les
        ///    anneaux de l'entrée précédente (la première pour un trou initial), découpé en blocs de
        ///    <see cref="MaxSpanMeasures"/> mesures pour rester éditable bloc par bloc. C'est le cas des ACCORDS,
        ///    dont la piste porte l'harmonie que les autres pistes lisent : un trou les laisserait sans harmonie.
        ///    Pour la BATTERIE on ne comble pas : un trou = la batterie se tait, ce qui est un choix d'arrangement
        ///    légitime (et rien ne lit cette piste).</summary>
        public static List<PolySpan<T>> Plan<T>(List<T> specs, int totalMeasures, bool fillGaps) where T : AiPolyRangeSpec
        {
            var res = new List<PolySpan<T>>();
            if (specs == null || specs.Count == 0 || totalMeasures < 1) return res;

            // Forme historique : UN objet unique, sans plage → tout le morceau, exactement comme avant les tableaux.
            bool legacyWhole = specs.Count == 1 && specs[0] != null && specs[0].fromMeasure <= 0 && specs[0].measures <= 0;

            int cursor = 1;
            foreach (var s in specs)
            {
                if (s == null) continue;
                int from = s.fromMeasure >= 1 ? Math.Max(s.fromMeasure, cursor) : cursor;
                if (from > totalMeasures) continue;
                int len = legacyWhole ? totalMeasures
                        : Clamp(s.measures > 0 ? s.measures : MaxSpanMeasures, 1, MaxSpanMeasures);
                len = Math.Min(len, totalMeasures - from + 1);
                if (len < 1) continue;
                res.Add(new PolySpan<T> { Spec = s, FromMeasure = from, Measures = len });
                cursor = from + len;
            }
            if (!fillGaps || res.Count == 0) return res;

            var full = new List<PolySpan<T>>();
            int at = 1;
            T prev = null;                          // dernière entrée VRAIMENT décrite avant le trou
            foreach (var span in res)
            {
                // Trou : on prolonge ce qu'on vient d'entendre (et, pour un trou initial, on anticipe la 1re entrée).
                if (span.FromMeasure > at) AddFiller(full, prev ?? res[0].Spec, at, span.FromMeasure - at);
                full.Add(span);
                at = span.FromMeasure + span.Measures;
                prev = span.Spec;
            }
            if (at <= totalMeasures) AddFiller(full, res[res.Count - 1].Spec, at, totalMeasures - at + 1);
            return full;
        }

        // Comble [from, from+count) avec des blocs de MaxSpanMeasures mesures qui reprennent le MÊME spec.
        static void AddFiller<T>(List<PolySpan<T>> outp, T spec, int from, int count) where T : AiPolyRangeSpec
        {
            for (int m = from; m < from + count; m += MaxSpanMeasures)
                outp.Add(new PolySpan<T>
                {
                    Spec = spec, FromMeasure = m,
                    Measures = Math.Min(MaxSpanMeasures, from + count - m),
                    Filler = true,
                });
        }

        // ---- fragments de SCHÉMA (n'entrent dans le prompt que si l'option est cochée) ---------------------

        public const string ChordSchema =
@"  ""polyChords"": [ { ""fromMeasure"": int, ""measures"": int(1..4),
      ""cycleBeats"": int, ""octave"": int, ""mode"": ""perTone|sweep"",
      ""restart"": ""nearest|grave|aigu|tonic|tierce|quinte"", ""openVoicing"": bool,
      ""layers"": [ { ""toneIndex"": int, ""contour"": int, ""hits"": int, ""steps"": int, ""rotation"": int, ""octave"": int, ""legato"": bool }, ... ] }, ... ]";

        public const string DrumSchema =
@"  ""polyDrums"": [ { ""fromMeasure"": int, ""measures"": int(1..4), ""kit"": 0, ""cycleBeats"": int,
      ""layers"": [ { ""lane"": int, ""hits"": int, ""steps"": int, ""rotation"": int, ""accentLane"": int }, ... ] }, ... ]";

        // ---- fragments de RÈGLES ---------------------------------------------------------------------------

        /// <summary>Le modèle commun aux deux options : un cycle partagé, découpé en Steps parts par anneau.
        /// Émis UNE SEULE FOIS par le prompt, même quand les deux options sont cochées.</summary>
        public static string CycleRules()
            => "- MODÈLE POLYRYTHMIQUE : tous les anneaux partagent le MÊME cycle ('cycleBeats' temps, typiquement 2 à 6). "
             + "Chaque anneau découpe CE cycle en 'steps' parts égales et frappe sur les cases actives du motif euclidien E(hits,steps), décalé de 'rotation'. "
             + "Il n'y a PAS de subdivision propre à un anneau : la polyrythmie vient de ce que les 'steps' DIFFÈRENT d'un anneau à l'autre (3 contre 5, 4 contre 7, 5 contre 8…).\n"
             + "  hits ∈ [1, steps] ; steps ∈ [2, 32] ; rotation ∈ [0, steps−1]. Choisis des 'steps' premiers entre eux et évite deux anneaux au même 'steps' (ils sonneraient à l'unisson).\n"
             + "- UN TABLEAU D'ENTRÉES, PAS UNE SEULE ROUE : chaque entrée de 'polyChords'/'polyDrums' est UN module posé sur la timeline, couvrant 'measures' mesures (1 à "
             + MaxSpanMeasures + " MAXIMUM, jamais plus) à partir de 'fromMeasure' (1-based). Enchaîne les entrées sans trou ni recouvrement pour couvrir le morceau : "
             + "un morceau de 64 mesures = 16 entrées de 4 mesures.\n"
             + "  FAIS VARIER D'UNE ENTRÉE À L'AUTRE — c'est TOUT l'intérêt du découpage : change les 'hits'/'steps'/'rotation', le NOMBRE d'anneaux, les 'toneIndex'/'lane', le 'cycleBeats'. "
             + "Au minimum un polyrythme DIFFÉRENT par section ('sections'), et de préférence une variation à chaque bloc de " + MaxSpanMeasures + " mesures : "
             + "trame qui se densifie vers un refrain, se creuse sur un pont, anneau qui disparaît puis revient déphasé. Deux entrées consécutives identiques = travail non fait.";

        public static string ChordRules()
            => "- ACCORDS EN POLYRYTHME : la piste d'accords n'est PLUS jouée mesure par mesure — elle est jouée par des ROUES d'anneaux euclidiens ('polyChords'). "
             + "Laisse donc 'articulation' VIDE ([]) : elle ne serait pas utilisée.\n"
             + "  La PROGRESSION reste celle de 'chords' (un degré par mesure, comme d'habitude) : ne la réécris PAS dans 'polyChords', qui ne décrit QUE les anneaux et les réglages des roues. Chaque entrée reprend automatiquement les accords de SA plage de mesures.\n"
             + "  COUVRE TOUT LE MORCEAU : la piste d'accords porte l'harmonie que les autres pistes lisent ; une plage sans entrée serait comblée en répétant l'entrée précédente (donc sans variation — à toi de l'écrire).\n"
             + "  'cycleBeats' fixe la VITESSE des anneaux, indépendamment de la durée des accords : un changement d'accord ne fait que basculer sur le voicing suivant, la roue continue au même tempo.\n"
             + "  'mode' = \"perTone\" (à privilégier) : chaque anneau joue UNE note fixe du voicing, désignée par 'toneIndex' — 0 = la plus grave, 1 = la suivante, 2 = celle d'après… ; ça s'ENROULE À L'OCTAVE dans les deux sens, donc le nombre d'anneaux n'est PAS limité au nombre de notes de l'accord : sur une triade, 3 = la fondamentale une octave au-dessus, 4 = la tierce au-dessus, 6 = la fondamentale deux octaves au-dessus, −1 = la quinte une octave en dessous. Un anneau grave lent + des anneaux aigus rapides = ostinato entrelacé.\n"
             + "  'mode' = \"sweep\" : chaque anneau PARCOURT les notes de l'accord selon 'contour' (0 vague, 1 montante, 2 descendante, 3 statique, 4 zigzag, 5 aléatoire) ; 'restart' dit quelle note reprendre au changement d'accord.\n"
             + "  'octave' du module = octave de base (4 = Do central) ; 'octave' d'un anneau = décalage en OCTAVES (−3..+3) ; 'legato' = la note tient jusqu'au coup suivant (nappes) ; 'openVoicing' = voicing écarté.\n"
             + "  Vise 4 à 8 anneaux par entrée (0 à 7 en 'toneIndex' : les voix de l'accord puis leurs octaves), et fais varier ce nombre d'une entrée à l'autre. N'indique AUCUN renversement ni décalage d'octave par accord : la conduite des voix est calculée par l'application.\n"
             + "  TIMBRE — SUGGESTION FORTE : un anneau par note, chaque note attaquée SÉPARÉMENT puis laissée résonner, c'est le geste d'un instrument RÉSONANT PINCÉ OU FRAPPÉ (harpe, handpan, kalimba) — pas celui d'un piano d'accompagnement. "
             + "Choisis 'chordInstrument' dans cet esprit plutôt que le piano par défaut : 46 harpe, 108 kalimba, 12 marimba, 8 célesta, 11 vibraphone, 10 boîte à musique, 114 steel drums (esprit handpan), 15 dulcimer, 107 koto, 24 guitare nylon, 45 cordes pizzicato. "
             + "Ça reste un choix musical : un autre timbre est permis si l'intention le demande.";

        public static string DrumRules()
            => "- BATTERIE EN POLYRYTHME : n'écris AUCUN champ 'drums'. La batterie est une suite de modules d'anneaux euclidiens ('polyDrums').\n"
             + "  La batterie PEUT SE TAIRE sur une plage : il suffit de n'écrire AUCUNE entrée pour ces mesures (intro à nu, pont sans percussions…) — contrairement aux accords, un trou n'est pas comblé.\n"
             + "  Un anneau = UN instrument de percussion, désigné par 'lane' (INDEX de lane, PAS une note MIDI). 'accentLane' (facultatif, −1 = aucun) = instrument joué sur le PREMIER coup de chaque tour de motif (ex. un son de basse de djembé sur le 1, le tone sur le reste).\n"
             + "  Vise 3 à 5 anneaux — c'est ce qui rend le polyrythme LISIBLE.\n"
             + "  LANES : 0 grosse caisse · 1 caisse claire · 2 charley fermé · 3 charley ouvert · 4 charley pied · 5 rimshot · 6 clap · 7 tom basse · 8 tom médium · 9 tom aigu · 10 crash · 11 ride\n"
             + "          22 tambourin · 23 cowbell · 24 vibraslap · 25 bongo aigu · 26 bongo grave · 27 conga aigu étouffé · 28 conga aigu · 29 conga grave\n"
             + "          30 timbale aiguë · 31 timbale grave (dundun) · 32 agogo aigu · 33 agogo grave · 34 cabasa · 35 maracas · 40 claves · 41/42 wood block · 45/46 triangle";

        // ---- spec → modules ---------------------------------------------------------------------------------

        /// <summary>Construit le module de batterie polyrythmique de la plage, longue de <paramref name="totalBeats"/>
        /// temps. Le cycle est borné à la longueur de la plage, et le nombre de répétitions arrondi EN DESSOUS : un
        /// module ne doit jamais DÉPASSER sa plage, sinon il décalerait tous les blocs suivants de la piste (au pire
        /// il reste moins d'un cycle de silence en fin de plage). Aucun anneau exploitable ⇒ un anneau de secours
        /// (grosse caisse E(4,8)).</summary>
        public static PolyDrumModule BuildDrums(AiPolyDrumSpec spec, int totalBeats)
        {
            int total = Math.Max(1, totalBeats);
            int cycle = Clamp(spec != null && spec.cycleBeats > 0 ? spec.cycleBeats : 4, 1, Math.Min(32, total));
            var m = new PolyDrumModule
            {
                Kit = Clamp(spec == null ? 0 : spec.kit, 0, Math.Max(0, InstrumentCatalog.DrumKits().Count - 1)),
                Beats = cycle,
                Repeats = Math.Max(1, total / cycle),
            };
            if (spec != null && spec.layers != null)
                foreach (var l in spec.layers) { if (l != null) m.Layers.Add(DrumLayer(l)); }
            if (m.Layers.Count == 0) m.Layers.Add(new EuclidLayer { Lane = 0, Hits = 4, Steps = 8 });
            return m;
        }

        static EuclidLayer DrumLayer(AiPolyDrumRing l)
        {
            int steps = Clamp(l.steps, MinSteps, MaxSteps);
            int hits = Clamp(l.hits, 1, steps);
            return new EuclidLayer
            {
                Lane = Clamp(l.lane, 0, DrumPattern.LaneCount - 1),
                AccentLane = (l.accentLane >= 0 && l.accentLane < DrumPattern.LaneCount) ? l.accentLane : -1,
                Hits = hits, Steps = steps, Rotation = ((l.rotation % steps) + steps) % steps,
            };
        }

        /// <summary>Construit le module d'accords polyrythmique VIDE d'accords : le placeur remplit ensuite
        /// <see cref="PolyChordModule.Chords"/> depuis le champ <c>chords</c> déjà parsé (via <see cref="ChordItem"/>),
        /// puis appelle <see cref="ChordDegrees.Revoice"/> sur la piste pour résoudre la conduite des voix.
        /// Aucun anneau exploitable ⇒ deux anneaux de secours (E(3,8) grave + E(5,8) au-dessus), comme
        /// « Insérer ▸ Accords polyrythmiques ».</summary>
        public static PolyChordModule BuildChords(AiPolyChordSpec spec)
        {
            var m = new PolyChordModule
            {
                CycleBeats = Clamp(spec != null && spec.cycleBeats > 0 ? spec.cycleBeats : 4, 1, 32),
                Octave = Clamp(spec != null && spec.octave > 0 ? spec.octave : 4, 1, 7),
                OpenVoicing = spec != null && spec.openVoicing,
                Mode = ParseMode(spec == null ? null : spec.mode),
                Restart = ParseRestart(spec == null ? null : spec.restart),
            };
            if (spec != null && spec.layers != null)
                foreach (var l in spec.layers) { if (l != null) m.Layers.Add(ChordLayer(l)); }
            if (m.Layers.Count == 0)
            {
                m.Layers.Add(new EuclidChordLayer { Hits = 3, Steps = 8, ToneIndex = 0 });
                m.Layers.Add(new EuclidChordLayer { Hits = 5, Steps = 8, ToneIndex = 1 });
            }
            return m;
        }

        static EuclidChordLayer ChordLayer(AiPolyChordRing l)
        {
            int steps = Clamp(l.steps, MinSteps, MaxSteps);
            int hits = Clamp(l.hits, 1, steps);
            return new EuclidChordLayer
            {
                Hits = hits, Steps = steps, Rotation = ((l.rotation % steps) + steps) % steps,
                // ToneIndex n'est PAS limité au nombre de notes de l'accord : PolyChord l'enroule à l'octave dans
                // les deux sens (idx = ti mod n, extraOctaves = (ti − idx) / n). 8 anneaux ou plus restent donc
                // musicalement définis — on empile les voix, puis les mêmes une octave plus haut. La borne ci-dessous
                // est celle au-delà de laquelle la note sortirait de la plage jouable du riff (row 0..95, octave de
                // base 4) et serait DROPPÉE en silence : ±3 octaves d'empilement, ce qui laisse largement la place
                // aux 4 à 8 anneaux demandés.
                ToneIndex = Clamp(l.toneIndex, -8, 11),
                // PolyChord.PickNext ne connaît que les 6 premiers contours (les suivants retombent sur « Vague »).
                Contour = Clamp(AiTranslate.ContourIndex(l.contour), 0, 5),
                Octave = Clamp(l.octave, -3, 3),
                Legato = l.legato,
            };
        }

        /// <summary>Un accord de la liste du module, construit depuis le champ <c>chords</c> DÉJÀ parsé — mêmes
        /// règles que <see cref="ChordModelOps.AddAiChord"/> : degré verrouillé quand la qualité demandée est bien
        /// celle du degré dans la tonalité, accord FIXE (Degree = −1) sinon (dominante secondaire, emprunt…).
        /// Inversion/OctaveShift ne sont PAS posés ici : ils sont calculés par la passe de voice-leading.</summary>
        public static PolyChordItem ChordItem(TimelineProject project, AiChord c, int beats)
        {
            int deg = Clamp(c == null ? 1 : c.degree, 1, 7);
            var key = project.Key ?? new Engine.Score.KeySignature();
            var it = new PolyChordItem
            {
                Beats = Math.Max(1, beats),
                Root = AiTranslate.RootPc(key, deg),
                Quality = AiTranslate.QualityIndex(c == null ? null : c.quality),
                Degree = deg - 1,
            };
            MusicTheory.ChordShape(it.Quality, out bool reqMin, out bool reqDim, out _, out _);
            bool diaDim = MusicTheory.DiatonicIsDim(key, it.Degree);
            bool diaMin = !diaDim && MusicTheory.DiatonicThird(key, it.Degree) == 3;
            if (reqMin != diaMin || reqDim != diaDim) it.Degree = -1;
            else
            {
                // Même correctif que ChordModelOps.AddAiChord, et il est encore plus visible ici : sur un accord
                // VERROUILLÉ à son degré, PolyChord.VoicedNotes dérive les notes de DiatonicColour/Suspension et
                // IGNORE Quality. DiatonicColour restant à 0 (triade), toute septième diatonique était rejouée en
                // triade. Le test ci-dessus ne l'attrape pas : il ne compare que la TIERCE.
                var trio = ChordDegrees.ColourForQuality(it.Quality);
                it.DiatonicColour = trio.colour;
                it.Suspension = trio.suspension;
            }
            return it;
        }

        /// <summary>Cale la liste d'accords du module sur la longueur EXACTE de sa plage (<paramref name="targetBeats"/>)
        /// en ajustant la durée du DERNIER accord. Sans ça, deux cas pathologiques décaleraient tous les blocs
        /// suivants de la piste : plus d'accords dans une mesure que la mesure n'a de temps (la somme dépasse), et
        /// mesures situées AVANT le premier accord de 'chords' (la somme manque). Retourne false si le module n'a
        /// aucun accord — la plage est alors à ignorer.</summary>
        public static bool FitChordSpan(PolyChordModule m, int targetBeats)
        {
            if (m == null || m.Chords.Count == 0 || targetBeats < 1) return false;
            int sum = 0;
            foreach (var c in m.Chords) sum += Math.Max(1, c.Beats);
            int delta = targetBeats - sum;
            if (delta != 0)
            {
                var last = m.Chords[m.Chords.Count - 1];
                last.Beats = Math.Max(1, last.Beats + delta);
            }
            return true;
        }

        // ---- helpers ----------------------------------------------------------------------------------------

        static PolyChordMode ParseMode(string s)
        {
            string n = Norm(s);
            if (n.Length == 0) return PolyChordMode.OneRingPerTone;
            if (n == "1" || n.Contains("sweep") || n.Contains("balay") || n.Contains("parcour")) return PolyChordMode.OneRingSweep;
            return PolyChordMode.OneRingPerTone;
        }

        static ChordRestartMode ParseRestart(string s)
        {
            string n = Norm(s);
            switch (n)
            {
                case "1": case "grave": case "low": case "bass": case "basse": return ChordRestartMode.Grave;
                case "2": case "aigu": case "high": case "top": return ChordRestartMode.Aigu;
                case "3": case "tonic": case "tonique": case "fondamentale": case "root": return ChordRestartMode.Tonic;
                case "4": case "tierce": case "third": return ChordRestartMode.Tierce;
                case "5": case "quinte": case "fifth": return ChordRestartMode.Quinte;
                default: return ChordRestartMode.Nearest;   // "0"/"nearest"/inconnu → parcours le plus lisse
            }
        }

        static string Norm(string s)
        {
            if (s == null) return "";
            var sb = new System.Text.StringBuilder();
            foreach (char ch in s.ToLowerInvariant()) if (char.IsLetterOrDigit(ch)) sb.Append(ch);
            return sb.ToString();
        }

        static int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);
    }
}
