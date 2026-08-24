using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Windows.Controls;
using System.Windows.Media;
using KotonStudio.Library;

namespace KotonPluginLifeGrid
{
    /// <summary>
    /// Motif de départ d'un <see cref="LifeGrid"/> — grille 2D de cellules 0/1 avec ses dimensions.
    ///
    /// **Immuable par convention** : l'éditeur (thread UI) ne mute JAMAIS l'instance publiée ; il en
    /// construit une nouvelle et remplace la référence dans le plugin (échange de référence atomique).
    /// <see cref="LifeGrid.RenderNotes"/>, appelée depuis le thread audio, capture la référence une
    /// fois en entrée et travaille dessus — pas de verrou, pas de déchirure entre Cols/Rows/Cells.
    /// </summary>
    public sealed class LifePattern
    {
        public readonly int Cols;
        public readonly int Rows;

        /// <summary>Cellules à plat, index = y * Cols + x. y = 0 est la LIGNE BASSE (grave) : le
        /// mapping ligne → hauteur est croissant, comme un piano-roll.</summary>
        public readonly byte[] Cells;

        public LifePattern(int cols, int rows, byte[] cells) { Cols = cols; Rows = rows; Cells = cells; }

        public static LifePattern Empty(int cols, int rows) => new LifePattern(cols, rows, new byte[cols * rows]);

        public byte At(int x, int y) => Cells[y * Cols + x];

        /// <summary>Copie du motif redimensionnée en <paramref name="cols"/>×<paramref name="rows"/> :
        /// le contenu commun est conservé (ancrage en bas à gauche), le reste est vide. Sert au
        /// changement de taille depuis l'éditeur — l'utilisateur ne perd pas son dessin.</summary>
        public LifePattern Resized(int cols, int rows)
        {
            var cells = new byte[cols * rows];
            int cx = Math.Min(cols, Cols), cy = Math.Min(rows, Rows);
            for (int y = 0; y < cy; y++)
                for (int x = 0; x < cx; x++)
                    cells[y * cols + x] = Cells[y * Cols + x];
            return new LifePattern(cols, rows, cells);
        }

        /// <summary>Copie avec la cellule (<paramref name="x"/>,<paramref name="y"/>) posée à
        /// <paramref name="on"/>. Retourne <c>this</c> si rien ne change (évite un re-render inutile).</summary>
        public LifePattern WithCell(int x, int y, bool on)
        {
            if (x < 0 || y < 0 || x >= Cols || y >= Rows) return this;
            byte v = on ? (byte)1 : (byte)0;
            if (Cells[y * Cols + x] == v) return this;
            var cells = (byte[])Cells.Clone();
            cells[y * Cols + x] = v;
            return new LifePattern(Cols, Rows, cells);
        }

        public bool AnyAlive() { for (int i = 0; i < Cells.Length; i++) if (Cells[i] != 0) return true; return false; }
    }

    /// <summary>
    /// Life Grid — séquenceur à automate cellulaire 2D (jeu de la vie de Conway et sa famille),
    /// dans l'esprit de l'ensemble Newscool de Reaktor.
    ///
    /// **Le principe** : on ne programme pas une mélodie, on AMORCE un système. L'utilisateur dessine
    /// un motif de départ ; à chaque pas d'horloge la grille évolue selon des règles de
    /// naissance/survie par voisinage, et l'état vivant de chaque génération est lu comme de la
    /// musique. Selon l'amorce, ça se stabilise en boucle courte (oscillateur = ostinato), ça meurt,
    /// ou ça part en chaos — les trois régimes sonnent très différemment.
    ///
    /// **Comment naissent les notes** (les trois questions du mapping) :
    /// <list type="bullet">
    /// <item><b>La hauteur</b> vient de la LIGNE : ligne 0 = note la plus grave du pool, et on monte
    /// vers le haut de la grille. Ce pool dépend de <c>harmony</c> — la gamme du projet (transposée sur
    /// sa tonique), les notes de l'accord posé sous le bloc, ou le mode par défaut : contour dessiné
    /// dans la gamme, mais ramené sur l'accord DÈS QUE LA NOTE TOMBE SUR UN TEMPS. Le troisième mode
    /// est le seul à produire des notes de passage : ailleurs qu'aux temps forts, la grille est libre
    /// de sortir de l'harmonie, ce qui évite l'arpège perpétuel du mode « accord ».</item>
    /// <item><b>Le rythme</b> vient de la COLONNE : les colonnes sont réparties en <c>sweep_div</c>
    /// groupes qui découpent la génération en autant de subdivisions, et une ligne sonne dans chaque
    /// subdivision où elle a au moins une cellule vivante. Une génération devient donc un arpège
    /// balayé de gauche à droite sur une VRAIE grille rythmique — c'est ce qui permet aux notes de
    /// tomber sur les temps. À 1, toute la génération sonne d'un coup : lecture en accords.</item>
    /// <item><b>La durée</b> n'est pas choisie, elle ÉMERGE : une cellule qui naît ouvre la note,
    /// une cellule qui meurt la ferme. Un vaisseau qui traverse la grille tient sa note pendant
    /// toute sa traversée, une cellule isolée qui meurt tout de suite fait une croche. C'est le
    /// réglage <c>dur_mode</c> = Durée de vie (les deux autres modes forcent un pas fixe ou du
    /// staccato, pour retrouver un grain régulier).</item>
    /// </list>
    ///
    /// **Anti-mort** : la plupart des amorces finissent en nature morte ou s'éteignent. <c>revive</c>
    /// surveille l'extinction et la stagnation (état identique à la génération précédente ou à
    /// l'avant-précédente = oscillateur de période 2) et ré-injecte soit le motif de départ, soit une
    /// pincée de cellules aléatoires. Réglé sur « Aucune », un motif stable devient un ostinato qui
    /// tourne — ce qui est parfaitement musical aussi.
    ///
    /// **Déterminisme** : <see cref="RenderNotes"/> re-simule depuis la génération 0 à chaque appel et
    /// n'utilise que des <see cref="Random"/> re-semés explicitement. Deux rendus du même bloc donnent
    /// exactement les mêmes notes — indispensable, l'hôte re-flatten en boucle pendant la lecture.
    /// </summary>
    [KotonGenerator("Life Grid", Id = "koton.lifegrid", Type = KotonGeneratorType.Melody, Version = "1.0", Vendor = "Koton Studio")]
    public sealed class LifeGrid : IKotonGenerator
    {
        public string Id => "koton.lifegrid";
        public string DisplayName => "Life Grid";

        // ---------------------------------------------------------------- paramètres
        readonly KotonParameter _gensPerBeat = new KotonParameter("gens_per_beat", "Générations/temps", 1, 8, 2);
        readonly KotonParameter _rulePreset  = new KotonParameter("rule_preset",   "Règle",             0, 9, 0);
        readonly KotonParameter _birthMask   = new KotonParameter("birth_mask",    "Naissances",        0, 511, 8);
        readonly KotonParameter _survMask    = new KotonParameter("surv_mask",     "Survies",           0, 511, 12);
        readonly KotonParameter _sweepDiv    = new KotonParameter("sweep_div",     "Étalement",         1, 8, 2);
        readonly KotonParameter _durMode     = new KotonParameter("dur_mode",      "Durées",            0, 2, 0);
        readonly KotonParameter _gate        = new KotonParameter("gate",          "Gate",              0.05, 1.0, 0.9);
        readonly KotonParameter _scale       = new KotonParameter("scale",         "Gamme",             0, 4, 3);
        readonly KotonParameter _baseOctave  = new KotonParameter("base_octave",   "Octave de base",    0, 8, 3);
        readonly KotonParameter _harmony     = new KotonParameter("harmony",       "Harmonie",          0, 2, 2);
        readonly KotonParameter _velocity    = new KotonParameter("velocity",      "Vélocité",          1, 127, 80);
        readonly KotonParameter _accent      = new KotonParameter("accent",        "Accent (densité)",  0, 1, 0.5);
        readonly KotonParameter _maxVoices   = new KotonParameter("max_voices",    "Voix max",          1, 16, 4);
        readonly KotonParameter _revive      = new KotonParameter("revive",        "Relance",           0, 2, 1);
        readonly KotonParameter _density     = new KotonParameter("density",       "Densité aléatoire", 0.05, 0.9, 0.28);
        readonly KotonParameter _rngSeed     = new KotonParameter("rng_seed",      "Graine",            0, 999, 1);

        readonly List<KotonParameter> _params;
        public IReadOnlyList<KotonParameter> Parameters => _params;

        public LifeGrid()
        {
            // Les réglages STRUCTURELS ne sont pas automatisables : une courbe qui ferait sauter la
            // règle ou le mode de lecture au milieu d'un bloc changerait la simulation sous les notes
            // déjà ouvertes. Restent automatisables les grandeurs continues (gate, vélocité, accent,
            // octave) qui n'altèrent pas la trajectoire de l'automate.
            _rulePreset.Automatable = false;
            _birthMask.Automatable = false;
            _survMask.Automatable = false;
            _sweepDiv.Automatable = false;
            _durMode.Automatable = false;
            _scale.Automatable = false;
            _harmony.Automatable = false;
            _maxVoices.Automatable = false;
            _revive.Automatable = false;
            _density.Automatable = false;
            _rngSeed.Automatable = false;
            _gensPerBeat.Automatable = false;

            _params = new List<KotonParameter>
            {
                _gensPerBeat, _rulePreset, _birthMask, _survMask, _sweepDiv, _durMode, _gate,
                _scale, _baseOctave, _harmony, _velocity, _accent, _maxVoices, _revive,
                _density, _rngSeed,
            };

            _seed = DefaultPattern(16, 12);
        }

        public KotonGeneratorType GeneratorType => KotonGeneratorType.Melody;

        double _durationBeats = 8.0;
        public double DurationBeats { get => _durationBeats; set => _durationBeats = value < 0.25 ? 0.25 : value; }

        public bool HasEditor => true;
        public UserControl CreateEditor() => new LifeGridEditor(this);

        public KotonGeneratorDisplay GetTimelineDisplay()
            => new KotonGeneratorDisplay { Background = Color.FromRgb(0x1E, 0x6E, 0x63), Text = "Life · " + ShortRuleName() };

        // ---------------------------------------------------------------- motif de départ
        // Volatile + échange de référence : voir le commentaire de LifePattern.
        volatile LifePattern _seed;

        /// <summary>Motif de départ courant. L'écriture publie une NOUVELLE instance (jamais de
        /// mutation en place) — c'est ce qui rend la lecture depuis le thread audio sûre.</summary>
        public LifePattern Seed { get => _seed; set { if (value != null) _seed = value; } }

        public const int MinCols = 4, MaxCols = 32, MinRows = 4, MaxRows = 24;

        /// <summary>Amorce par défaut : un planeur (glider) posé au milieu. Motif minimal qui ne meurt
        /// pas et qui traverse la grille — à l'oreille, une figure qui monte et se répète en se
        /// décalant, soit exactement ce qu'on veut entendre au premier essai.</summary>
        public static LifePattern DefaultPattern(int cols, int rows)
            => ApplyStamp(LifePattern.Empty(cols, rows), 0, cols / 4, rows / 2);

        /// <summary>Redimensionne la grille en conservant le dessin (voir <see cref="LifePattern.Resized"/>).</summary>
        public void Resize(int cols, int rows)
        {
            cols = Clamp(cols, MinCols, MaxCols);
            rows = Clamp(rows, MinRows, MaxRows);
            var s = _seed;
            if (s.Cols == cols && s.Rows == rows) return;
            _seed = s.Resized(cols, rows);
        }

        /// <summary>Amorces classiques de la littérature Life. Chacune a un comportement connu : le
        /// planeur voyage indéfiniment, le R-pentomino explose pendant plus de mille générations avant
        /// de se calmer, l'acorn encore plus longtemps. Musicalement, ce sont des durées de
        /// « développement » très différentes avant que ça ne se fige.</summary>
        public static readonly string[] StampNames = { "Planeur", "Clignotant", "R-pentomino", "Acorn", "Diehard", "Vaisseau léger" };

        static readonly int[][][] Stamps =
        {
            // Planeur
            new[] { new[] { 1, 0 }, new[] { 2, 1 }, new[] { 0, 2 }, new[] { 1, 2 }, new[] { 2, 2 } },
            // Clignotant (oscillateur de période 2 = ostinato)
            new[] { new[] { 0, 0 }, new[] { 1, 0 }, new[] { 2, 0 } },
            // R-pentomino
            new[] { new[] { 1, 0 }, new[] { 2, 0 }, new[] { 0, 1 }, new[] { 1, 1 }, new[] { 1, 2 } },
            // Acorn
            new[] { new[] { 1, 0 }, new[] { 3, 1 }, new[] { 0, 2 }, new[] { 1, 2 }, new[] { 4, 2 }, new[] { 5, 2 }, new[] { 6, 2 } },
            // Diehard
            new[] { new[] { 6, 0 }, new[] { 0, 1 }, new[] { 1, 1 }, new[] { 1, 2 }, new[] { 5, 2 }, new[] { 6, 2 }, new[] { 7, 2 } },
            // Vaisseau léger (LWSS)
            new[] { new[] { 0, 0 }, new[] { 3, 0 }, new[] { 4, 1 }, new[] { 0, 2 }, new[] { 4, 2 }, new[] { 1, 3 }, new[] { 2, 3 }, new[] { 3, 3 }, new[] { 4, 3 } },
        };

        /// <summary>Pose l'amorce <paramref name="stampIndex"/> avec son coin bas-gauche en
        /// (<paramref name="ox"/>,<paramref name="oy"/>), par-dessus le motif existant (union).</summary>
        public static LifePattern ApplyStamp(LifePattern p, int stampIndex, int ox, int oy)
        {
            if (stampIndex < 0 || stampIndex >= Stamps.Length) return p;
            var cells = (byte[])p.Cells.Clone();
            foreach (var c in Stamps[stampIndex])
            {
                int x = ox + c[0], y = oy + c[1];
                if (x < 0 || y < 0 || x >= p.Cols || y >= p.Rows) continue;
                cells[y * p.Cols + x] = 1;
            }
            return new LifePattern(p.Cols, p.Rows, cells);
        }

        /// <summary>Remplissage aléatoire déterministe (même graine = même motif).</summary>
        public static LifePattern RandomPattern(int cols, int rows, double density, int rngSeed)
        {
            var rng = new Random(rngSeed);
            var cells = new byte[cols * rows];
            for (int i = 0; i < cells.Length; i++) cells[i] = rng.NextDouble() < density ? (byte)1 : (byte)0;
            if (Array.IndexOf(cells, (byte)1) < 0) cells[cells.Length / 2] = 1;
            return new LifePattern(cols, rows, cells);
        }

        // ---------------------------------------------------------------- règles
        /// <summary>Règles nommées (notation B/S : chiffres = nombres de voisins vivants qui font
        /// naître / qui laissent survivre). « Personnalisé… » en dernier, comme les autres tables de
        /// styles de l'app : au-dessus des choix tout faits, en dernier l'ouverture des masques.</summary>
        public static readonly string[] RuleNames =
        {
            "Life (B3/S23)", "HighLife (B36/S23)", "Labyrinthe (B3/S12345)", "Corail (B3/S45678)",
            "34 Life (B34/S34)", "Graines (B2/S–)", "Réplicateur (B1357/S1357)",
            "Diamoeba (B35678/S5678)", "Jour & Nuit (B3678/S34678)", "Personnalisé…",
        };

        static readonly int[][] RuleMasks =
        {
            new[] { 8, 12 },      // Life        B3     / S23
            new[] { 72, 12 },     // HighLife    B36    / S23
            new[] { 8, 62 },      // Labyrinthe  B3     / S12345
            new[] { 8, 496 },     // Corail      B3     / S45678
            new[] { 24, 24 },     // 34 Life     B34    / S34
            new[] { 4, 0 },       // Graines     B2     / S(rien)
            new[] { 170, 170 },   // Réplicateur B1357  / S1357
            new[] { 488, 480 },   // Diamoeba    B35678 / S5678
            new[] { 456, 472 },   // Jour & Nuit B3678  / S34678
        };

        /// <summary>Applique un preset de règle aux masques. L'index de « Personnalisé… » ne touche à
        /// rien : les masques gardent leur dernière valeur, que l'éditeur laisse alors éditer.</summary>
        public void ApplyRulePreset(int index)
        {
            _rulePreset.Value = index;
            if (index < 0 || index >= RuleMasks.Length) return;
            _birthMask.Value = RuleMasks[index][0];
            _survMask.Value = RuleMasks[index][1];
        }

        string ShortRuleName()
        {
            int i = (int)Math.Round(_rulePreset.Value);
            if (i >= 0 && i < RuleMasks.Length)
            {
                string n = RuleNames[i];
                int p = n.IndexOf(' ');
                return p > 0 ? n.Substring(0, p) : n;
            }
            return MaskToText((int)_birthMask.Value, (int)_survMask.Value);
        }

        /// <summary>Rend une paire de masques en notation B/S lisible (ex. « B3/S23 »).</summary>
        public static string MaskToText(int birth, int surv)
        {
            var sb = new StringBuilder("B");
            for (int i = 0; i <= 8; i++) if ((birth & (1 << i)) != 0) sb.Append(i);
            sb.Append("/S");
            for (int i = 0; i <= 8; i++) if ((surv & (1 << i)) != 0) sb.Append(i);
            return sb.ToString();
        }

        /// <summary>Une génération de l'automate, voisinage de Moore (8 voisins) sur un TORE : les
        /// bords se rejoignent, un vaisseau qui sort à droite rentre à gauche. Sans le tore la grille
        /// se vide par les bords et le bloc devient muet au bout de quelques générations.</summary>
        public static byte[] Step(byte[] cur, int cols, int rows, int birth, int surv)
        {
            var next = new byte[cur.Length];
            for (int y = 0; y < rows; y++)
            {
                int yUp = (y + 1) % rows, yDn = (y - 1 + rows) % rows;
                for (int x = 0; x < cols; x++)
                {
                    int xR = (x + 1) % cols, xL = (x - 1 + cols) % cols;
                    int n = cur[yDn * cols + xL] + cur[yDn * cols + x] + cur[yDn * cols + xR]
                          + cur[y * cols + xL] + cur[y * cols + xR]
                          + cur[yUp * cols + xL] + cur[yUp * cols + x] + cur[yUp * cols + xR];
                    int bit = 1 << n;
                    int i = y * cols + x;
                    next[i] = cur[i] != 0 ? ((surv & bit) != 0 ? (byte)1 : (byte)0)
                                          : ((birth & bit) != 0 ? (byte)1 : (byte)0);
                }
            }
            return next;
        }

        /// <summary>Simule <paramref name="genCount"/> générations depuis le motif courant, en
        /// appliquant la politique de relance. Rend la liste des états (index 0 = motif de départ).
        /// Utilisée telle quelle par l'éditeur pour son animation : la prévisualisation montre donc
        /// EXACTEMENT ce que le rendu jouera, pas une approximation.</summary>
        public List<byte[]> Simulate(int genCount)
        {
            var seed = _seed;
            int cols = seed.Cols, rows = seed.Rows;
            int birth = (int)_birthMask.Value, surv = (int)_survMask.Value;
            int revive = (int)Math.Round(_revive.Value);
            double density = _density.Value;
            int rngSeed = (int)_rngSeed.Value;

            // Un motif vide reste vide (sauf relance aléatoire, qui repeuplera) : « Vider » doit
            // donner du silence, pas une cellule surgie de nulle part.
            var gens = new List<byte[]>(Math.Max(1, genCount));
            var cur = (byte[])seed.Cells.Clone();

            byte[] prev = null;
            for (int g = 0; g < genCount; g++)
            {
                gens.Add(cur);
                var next = Step(cur, cols, rows, birth, surv);

                // Extinction ou stagnation (nature morte / oscillateur de période 2) : sans relance le
                // bloc se fige, ce qui est un choix valable (ostinato) ; sinon on ré-amorce.
                if (revive > 0)
                {
                    bool dead = Array.IndexOf(next, (byte)1) < 0;
                    bool stuck = Same(next, cur) || (prev != null && Same(next, prev));
                    if (dead || stuck)
                    {
                        if (revive == 1)
                        {
                            for (int i = 0; i < next.Length; i++) if (seed.Cells[i] != 0) next[i] = 1;
                        }
                        else
                        {
                            // Graine dérivée du numéro de génération : déterministe d'un rendu à
                            // l'autre, mais différente à chaque relance (sinon on relance toujours
                            // exactement le même nuage).
                            var rng = new Random(rngSeed * 7919 + g);
                            for (int i = 0; i < next.Length; i++)
                                if (rng.NextDouble() < density) next[i] = 1;
                        }
                    }
                }

                prev = cur;
                cur = next;
            }
            if (gens.Count == 0) gens.Add(cur);
            return gens;
        }

        static bool Same(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        // ---------------------------------------------------------------- gammes
        static readonly int[][] ScaleDegrees =
        {
            new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11 }, new[] { 0, 2, 4, 5, 7, 9, 11 }, new[] { 0, 2, 3, 5, 7, 8, 10 },
            new[] { 0, 2, 4, 7, 9 }, new[] { 0, 3, 5, 7, 10 },
        };
        public static readonly string[] ScaleNames = { "Chromatique", "Majeur", "Mineur", "Penta majeur", "Penta mineur" };
        public static readonly string[] DurModeNames = { "Durée de vie", "Un pas", "Staccato" };
        public static readonly string[] ReviveNames = { "Aucune", "Ré-injecter le motif", "Cellules aléatoires" };
        public static readonly string[] HarmonyNames =
        {
            "Gamme seule", "Notes de l'accord", "Accord sur les temps forts",
        };

        /// <summary>Construit un pool de <paramref name="count"/> hauteurs croissantes en partant de
        /// <paramref name="baseMidi"/> et en ne retenant que les notes dont la classe de hauteur est
        /// autorisée. Une ligne de la grille = une entrée du pool.
        ///
        /// **Pourquoi partir d'une hauteur fixe et pas de la fondamentale de l'accord** : sinon la
        /// grille entière saute d'une septième quand on passe de Do à Si, et le dessin qu'on entendait
        /// se retrouve une octave plus haut sans avoir bougé. Ancré ici, le registre reste stable d'un
        /// accord à l'autre — seules les notes disponibles changent sous les lignes, ce qui est
        /// exactement le comportement d'une conduite de voix.</summary>
        static int[] PoolFromPcs(bool[] allowed, int baseMidi, int count)
        {
            var pool = new int[count];
            int k = 0;
            for (int m = Math.Max(0, baseMidi); m <= 127 && k < count; m++)
                if (allowed[m % 12]) pool[k++] = m;
            // Registre saturé (octave de base très haute) : on rabat sur la dernière note trouvée
            // plutôt que de laisser des zéros, qui sonneraient en tout bas du clavier.
            int last = k > 0 ? pool[k - 1] : 60;
            while (k < count) pool[k++] = last;
            return pool;
        }

        /// <summary>Classes de hauteur d'une gamme transposée sur la tonique du projet.</summary>
        static bool[] ScalePcs(int scaleIdx, int tonic)
        {
            var allowed = new bool[12];
            foreach (int d in ScaleDegrees[scaleIdx]) allowed[((tonic + d) % 12 + 12) % 12] = true;
            return allowed;
        }

        /// <summary>Classes de hauteur des notes d'un accord (basse d'inversion comprise : si le
        /// morceau dit Do/Mi, le Mi fait partie des notes disponibles).</summary>
        static bool[] ChordPcs(KotonChord ch)
        {
            var allowed = new bool[12];
            foreach (int m in ch.GetMidiNotes(ch.Root)) allowed[((m % 12) + 12) % 12] = true;
            if (ch.BassNote.HasValue) allowed[((ch.BassNote.Value % 12) + 12) % 12] = true;
            return allowed;
        }

        /// <summary>Ramène <paramref name="midi"/> sur la note autorisée la plus proche (en cherchant
        /// alternativement au-dessus et en dessous). Sert à corriger une note de gamme vers l'accord
        /// sur un temps fort sans la déplacer de plus d'un ton — le contour mélodique dessiné dans la
        /// grille est conservé, seule la couleur est ajustée.</summary>
        static int SnapToPc(int midi, bool[] allowed)
        {
            for (int d = 0; d <= 6; d++)
            {
                int up = midi + d, dn = midi - d;
                if (up <= 127 && allowed[up % 12]) return up;
                if (dn >= 0 && allowed[((dn % 12) + 12) % 12]) return dn;
            }
            return midi;
        }

        /// <summary>Poids métrique de la position absolue <paramref name="absBeat"/> : 1 sur le premier
        /// temps de la mesure, puis par ordre décroissant le temps fort secondaire, les autres temps,
        /// les contretemps et les subdivisions fines.
        ///
        /// Les paliers reprennent volontairement ceux que l'hôte applique aux vélocités
        /// (<c>TimelinePlayer.MetricVelocity</c>) pour que le choix des notes et l'accentuation
        /// désignent les MÊMES temps forts. Le « temps » compté ici est le temps NOTÉ (la croche en
        /// 6/8), et la levée décale la grille de mesures comme il se doit.</summary>
        static double MetricWeight(double absBeat, int tsNum, int tsDen, double pickup)
        {
            double beatLen = 4.0 / Math.Max(1, tsDen);          // durée d'un temps noté, en noires
            double barLen = Math.Max(1e-6, tsNum * beatLen);
            double pos = (absBeat - pickup) % barLen;
            if (pos < 0) pos += barLen;

            const double eps = 1e-6;
            double inBeat = pos % beatLen;
            int beatInBar = (int)Math.Floor(pos / beatLen + eps);
            bool onBeat = inBeat < eps || beatLen - inBeat < eps;

            if (onBeat)
            {
                if (beatInBar == 0 || pos < eps) return 1.0;                    // premier temps
                if (tsNum % 2 == 0 && beatInBar == tsNum / 2) return 0.8;       // temps fort secondaire
                return 0.6;                                                     // autre temps
            }
            if (Math.Abs(inBeat * 2 - beatLen) < eps) return 0.4;               // contretemps (demi-temps)
            if (Math.Abs(inBeat * 3 % beatLen) < eps || Math.Abs(inBeat * 4 % beatLen) < eps) return 0.25;
            return 0.1;                                                         // subdivision fine
        }

        /// <summary>Seuil à partir duquel une position compte comme « temps fort » pour le mode
        /// harmonique 2 : tout ce qui tombe SUR un temps noté. Les contretemps et les subdivisions
        /// restent libres de sortir de l'accord — ce sont les notes de passage.</summary>
        const double StrongBeat = 0.55;

        // ---------------------------------------------------------------- rendu
        public IEnumerable<KotonGeneratedNote> RenderNotes(double startBeat, double endBeat, KotonRenderContext ctx)
        {
            var seed = _seed;
            var notes = new List<KotonGeneratedNote>();
            if (seed == null || seed.Cells == null || seed.Cells.Length == 0) return notes;

            int cols = seed.Cols, rows = seed.Rows;
            int gensPerBeat = Clamp((int)Math.Round(_gensPerBeat.Value), 1, 8);
            int sweepDiv = Clamp((int)Math.Round(_sweepDiv.Value), 1, 8);
            int durMode = Clamp((int)Math.Round(_durMode.Value), 0, 2);
            double gate = _gate.Value;
            int scaleIdx = Clamp((int)Math.Round(_scale.Value), 0, ScaleDegrees.Length - 1);
            int baseMidi = 12 + Clamp((int)Math.Round(_baseOctave.Value), 0, 8) * 12;
            int harmony = Clamp((int)Math.Round(_harmony.Value), 0, 2);
            int baseVel = Clamp((int)Math.Round(_velocity.Value), 1, 127);
            double accent = _accent.Value;
            int maxVoices = Clamp((int)Math.Round(_maxVoices.Value), 1, 16);

            // Même convention ternaire que les autres générateurs : en 6/8 & co, un « temps » vaut une
            // noire pointée, donc une génération dure 1.5 / gens_per_beat.
            int tsNum = ctx != null && ctx.TimeSigNum > 0 ? ctx.TimeSigNum : 4;
            int tsDen = ctx != null && ctx.TimeSigDen > 0 ? ctx.TimeSigDen : 4;
            bool ternary = tsDen == 8 && tsNum % 3 == 0;
            double step = (ternary ? 1.5 : 1.0) / gensPerBeat;
            if (step <= 0) return notes;

            double duration = Math.Max(0.25, DurationBeats);
            double blockStart = ctx != null ? ctx.BlockStartBeat : 0.0;
            double pickup = ctx != null ? ctx.PickupBeats : 0.0;
            int tonic = ctx != null ? ((ctx.Tonic % 12) + 12) % 12 : 0;
            int genCount = (int)Math.Ceiling(duration / step - 1e-9);
            if (genCount <= 0) return notes;

            var gens = Simulate(genCount);

            // Un « slot » = une voix potentielle, suivie de génération en génération : une LIGNE dans
            // une SUBDIVISION de la génération. Les colonnes de la grille sont réparties en sweepDiv
            // groupes, et un groupe qui contient au moins une cellule vivante fait sonner sa ligne au
            // moment correspondant.
            //
            // Pourquoi grouper plutôt que donner sa position à chaque colonne : avec 16 colonnes, une
            // génération se découperait en seizièmes de génération — soit du 1/32 de temps, qui ne
            // s'entend pas comme un rythme mais comme un strum, et qui ne tombe jamais sur un temps.
            // En groupant, les notes atterrissent sur une vraie grille rythmique : elles peuvent tomber
            // SUR les temps, ce dont dépendent l'accentuation de l'hôte et le mode harmonique 2.
            // sweepDiv = 1 rassemble toute la génération sur un seul instant (lecture en accords).
            int slotCount = rows * sweepDiv;
            var groupSize = new int[sweepDiv];
            for (int x = 0; x < cols; x++) groupSize[x * sweepDiv / cols]++;

            var weight = new double[slotCount];      // intensité du slot à la génération courante
            var prevOn = new bool[slotCount];        // slot actif à la génération précédente
            var runStart = new int[slotCount];       // génération d'ouverture de la note en cours
            var runWeight = new double[slotCount];   // intensité figée à l'ouverture
            var runPitch = new int[slotCount];       // hauteur figée à l'ouverture

            // Deux pools ancrés sur la MÊME hauteur de base, donc superposables : celui de la gamme
            // (le contour dessiné dans la grille) et celui de l'accord (la couleur). Le mode harmonique
            // décide lequel gagne, et à quel moment.
            var scaleAllowed = ScalePcs(scaleIdx, tonic);
            int[] scalePool = PoolFromPcs(scaleAllowed, baseMidi, rows);
            int[] chordPool = scalePool;
            bool[] chordAllowed = null;

            for (int g = 0; g <= genCount; g++)
            {
                // Génération fictive au-delà de la fin : tout s'y éteint, ce qui ferme proprement les
                // notes encore ouvertes sans dupliquer le code de fermeture.
                bool past = g >= genCount;
                Array.Clear(weight, 0, weight.Length);

                if (!past)
                {
                    var cur = gens[g];
                    for (int y = 0; y < rows; y++)
                        for (int x = 0; x < cols; x++)
                        {
                            if (cur[y * cols + x] == 0) continue;
                            weight[y * sweepDiv + x * sweepDiv / cols] += 1.0;
                        }
                    // Densité du groupe ramenée sur 0..1 : jamais nulle quand une cellule vit (sinon le
                    // slot passerait pour inactif), et d'autant plus forte que le groupe est rempli.
                    for (int s = 0; s < slotCount; s++)
                        if (weight[s] > 0) weight[s] /= Math.Max(1, groupSize[s % sweepDiv]);

                    LimitVoices(weight, prevOn, maxVoices);

                    // Accord relu à chaque génération : la grille garde son dessin, ce sont les notes
                    // disponibles sous les lignes qui changent avec l'harmonie.
                    if (harmony > 0 && KotonHost.GetChordAt != null)
                    {
                        var ch = KotonHost.GetChordAt(blockStart + g * step);
                        if (ch.HasValue)
                        {
                            chordAllowed = ChordPcs(ch.Value);
                            chordPool = PoolFromPcs(chordAllowed, baseMidi, rows);
                        }
                        else
                        {
                            // Pas d'accord posé sous le bloc : on retombe sur la gamme plutôt que de
                            // garder l'accord précédent, qui deviendrait faux dès la section suivante.
                            chordAllowed = null;
                            chordPool = scalePool;
                        }
                    }
                }

                for (int s = 0; s < slotCount; s++)
                {
                    bool on = weight[s] > 0;
                    if (on && !prevOn[s])
                    {
                        runStart[s] = g;
                        runWeight[s] = weight[s];
                        int row = s / sweepDiv;

                        // Position métrique de la note RÉELLE, décalage de subdivision compris : la
                        // première subdivision d'une génération peut tomber sur un temps, les autres
                        // sont des contretemps — et c'est ce qui les autorise à sortir de l'accord.
                        double metW = MetricWeight(blockStart + SlotStart(s, g, sweepDiv, step), tsNum, tsDen, pickup);

                        int midi;
                        if (harmony == 1 && chordAllowed != null) midi = chordPool[row];
                        else if (harmony == 2 && chordAllowed != null && metW >= StrongBeat)
                            midi = SnapToPc(scalePool[row], chordAllowed);   // temps fort → couleur de l'accord
                        else midi = scalePool[row];                          // ailleurs → note de passage
                        runPitch[s] = midi;
                    }
                    else if (!on && prevOn[s])
                    {
                        var n = MakeNote(s, runStart[s], g, runPitch[s], runWeight[s],
                                         sweepDiv, step, duration, durMode, gate, baseVel, accent);
                        if (n.HasValue) notes.Add(n.Value);
                    }
                    prevOn[s] = on;
                }
            }

            notes.Sort((a, b) => a.StartBeat.CompareTo(b.StartBeat));
            return notes;
        }

        /// <summary>Fabrique la note correspondant à un slot vivant de <paramref name="g0"/> à
        /// <paramref name="g1"/> (exclu). Rend <c>null</c> si elle tombe hors du bloc.</summary>
        KotonGeneratedNote? MakeNote(int slot, int g0, int g1, int midi, double w,
                                     int sweepDiv, double step, double duration, int durMode,
                                     double gate, int baseVel, double accent)
        {
            double t0 = SlotStart(slot, g0, sweepDiv, step);
            if (t0 >= duration - 1e-9) return null;

            double life = (g1 - g0) * step;
            double logical = durMode == 0 ? life : (durMode == 1 ? step : step * 0.25);
            if (t0 + logical > duration) logical = duration - t0;
            if (logical <= 0) return null;

            int vel = baseVel + (int)Math.Round(accent * Math.Min(1.0, w) * (127 - baseVel));
            return new KotonGeneratedNote
            {
                StartBeat = t0,
                DurationBeats = Math.Max(0.01, logical * gate),
                NotationDurationBeats = logical,
                MidiNote = midi,
                Velocity = Clamp(vel, 1, 127),
            };
        }

        /// <summary>Position de départ d'un slot, en beats depuis le début du bloc. Le décalage de
        /// subdivision est ici et pas ailleurs : le choix de la note (position métrique) et sa pose
        /// (<see cref="MakeNote"/>) doivent tomber d'accord au millième de temps près.</summary>
        static double SlotStart(int slot, int gen, int sweepDiv, double step)
            => gen * step + (slot % sweepDiv) * step / sweepDiv;

        /// <summary>Plafonne le nombre de voix simultanées : au-delà de <paramref name="max"/> slots
        /// actifs, on ne garde que les plus intenses — mais un slot DÉJÀ actif passe devant un nouveau,
        /// pour qu'un plafond bas ne hache pas les notes tenues.</summary>
        static void LimitVoices(double[] weight, bool[] prevOn, int max)
        {
            int active = 0;
            for (int i = 0; i < weight.Length; i++) if (weight[i] > 0) active++;
            if (active <= max) return;

            var scores = new double[active];
            int k = 0;
            for (int i = 0; i < weight.Length; i++) if (weight[i] > 0) scores[k++] = Score(weight[i], prevOn[i]);
            Array.Sort(scores);
            double threshold = scores[active - max];

            int kept = 0;
            for (int i = 0; i < weight.Length; i++)
            {
                if (weight[i] <= 0) continue;
                if (kept < max && Score(weight[i], prevOn[i]) >= threshold) kept++;
                else weight[i] = 0;
            }
        }

        static double Score(double w, bool sustaining) => w + (sustaining ? 100.0 : 0.0);
        static int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);

        // ---------------------------------------------------------------- persistance
        public byte[] SaveState()
        {
            try
            {
                var d = new Dictionary<string, object>();
                foreach (var kp in _params) d[kp.Id] = kp.Value;
                d["_dur"] = _durationBeats;
                var s = _seed;
                d["cols"] = s.Cols;
                d["rows"] = s.Rows;
                var sb = new StringBuilder(s.Cells.Length);
                foreach (var c in s.Cells) sb.Append(c != 0 ? '1' : '0');
                d["cells"] = sb.ToString();
                return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(d));
            }
            catch { return Array.Empty<byte>(); }
        }

        public void LoadState(byte[] state)
        {
            if (state == null || state.Length == 0) return;
            try
            {
                using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(state));
                var r = doc.RootElement;
                foreach (var kp in _params)
                    if (r.TryGetProperty(kp.Id, out var v) && v.ValueKind == JsonValueKind.Number) kp.Value = v.GetDouble();
                if (r.TryGetProperty("_dur", out var dv) && dv.ValueKind == JsonValueKind.Number) _durationBeats = dv.GetDouble();

                if (r.TryGetProperty("cols", out var cv) && r.TryGetProperty("rows", out var rv) &&
                    r.TryGetProperty("cells", out var sv) && sv.ValueKind == JsonValueKind.String)
                {
                    int cols = Clamp(cv.GetInt32(), MinCols, MaxCols);
                    int rows = Clamp(rv.GetInt32(), MinRows, MaxRows);
                    string cells = sv.GetString() ?? "";
                    var arr = new byte[cols * rows];
                    for (int i = 0; i < arr.Length && i < cells.Length; i++) arr[i] = cells[i] == '1' ? (byte)1 : (byte)0;
                    _seed = new LifePattern(cols, rows, arr);
                }
            }
            catch { }
        }

        public void Dispose() { }

        public void SetParam(string id, double value) { foreach (var kp in _params) if (kp.Id == id) { kp.Value = value; return; } }

        /// <summary>Lecture d'un paramètre par id — l'éditeur en a besoin pour se resynchroniser (les
        /// valeurs bougent sous lui : chargement de projet, application d'un preset de règle).</summary>
        public double GetParam(string id) { foreach (var kp in _params) if (kp.Id == id) return kp.Value; return 0; }
    }
}
