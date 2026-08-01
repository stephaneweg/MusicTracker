using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Windows.Controls;
using System.Windows.Media;
using KotonStudio.Library;

namespace KotonPluginFractal
{
    /// <summary>
    /// Générateur mélodique basé sur des algorithmes fractals / chaotiques. Trois algos disponibles en
    /// v1 (Voss 1/f, Logistic map, Lorenz attractor), extensibles à d'autres familles (fBm, L-systems,
    /// IFS, cellular automata) sans changer l'archi : ajouter une <c>IFractalSource</c> + un cas dans
    /// <see cref="BuildSource"/> + un panneau dans l'éditeur.
    ///
    /// **Chord-aware** : contrairement à Fractmus / ArtSong qui sortent du MIDI brut hors contexte, ce
    /// générateur peut SNAPER chaque valeur produite par la source fractale sur les notes de l'accord
    /// actif (via <see cref="KotonHost.GetChordAt"/>), sur les degrés de la gamme du projet, ou en
    /// chromatique (aucun snap). Choix par le paramètre <c>snap_mode</c>.
    ///
    /// **Reproductibilité** : la source est reconstruite au début de chaque <see cref="RenderNotes"/>
    /// avec la seed courante ; Preview et rendu final donnent la même séquence tant qu'aucun paramètre
    /// ne change. Le bouton "Régénérer" dans l'éditeur incrémente la seed pour explorer.
    ///
    /// **Historique** : les fondamentaux musicaux de la fractale = Voss & Clarke 1975 (spectre 1/f
    /// naturel), Mandelbrot 1977 (auto-similitude), Prusinkiewicz 1986 (L-systems musicaux). Logiciels
    /// contemporains dans le domaine : Fractmus (Díaz-Jerez), ArtSong (Ares), Musinum (Kindermann),
    /// AC Toolbox (Berg). Ce plugin apporte le snap harmonique en live que la plupart n'ont pas.
    /// </summary>
    [KotonGenerator("Fractale", Id = "koton.fractal", Type = KotonGeneratorType.Melody, Version = "1.0", Vendor = "Koton Studio")]
    public sealed class FractalGenerator : IKotonGenerator
    {
        public string Id => "koton.fractal";
        public string DisplayName => "Fractale";
        public KotonGeneratorType GeneratorType => KotonGeneratorType.Melody;

        // =============================================================================================
        // Paramètres — communs à tous les algos + spécifiques par algo. Tous exposés en KotonParameter
        // pour permettre la persistance uniforme ; l'éditeur affiche uniquement ceux pertinents à
        // l'algo courant (visibilité conditionnelle des Border).
        // =============================================================================================

        // COMMUNS
        // Algo : 0=Voss(1/f), 1=Logistic, 2=Lorenz. La borne max grandit quand on ajoute des familles.
        readonly KotonParameter _algo         = new KotonParameter("algo",           "Algo",          0, 2, 0);
        // Notes par temps : mêmes conventions que l'arpégiateur (binaire 1/2/3/4/6/8, ternaire 3/6).
        readonly KotonParameter _notesPerBeat = new KotonParameter("notes_per_beat", "Notes/temps",   1, 8, 4);
        // Octave grave (base MIDI = 12 + octave*12). Défaut 4 = C4=60.
        readonly KotonParameter _baseOctave   = new KotonParameter("base_octave",    "Octave",        0, 8, 4);
        // Range en demi-tons : le scalaire [0,1] mappe sur [baseMidi, baseMidi+range].
        readonly KotonParameter _range        = new KotonParameter("range",          "Range",         12, 48, 24, "st");
        // Snap : 0=Chord tones, 1=Diatonic scale, 2=Chromatic (aucun snap).
        readonly KotonParameter _snapMode     = new KotonParameter("snap_mode",      "Snap",          0, 2, 0);
        // Vélocité et sa variation aléatoire (0=constant, 1=±30 aléatoire).
        readonly KotonParameter _velocity     = new KotonParameter("velocity",       "Vélocité",      1, 127, 90);
        readonly KotonParameter _velVar       = new KotonParameter("vel_var",        "Var vélocité",  0.0, 1.0, 0.2);
        // Seed globale (partagée par tous les algos). L'utilisateur peut Régénérer = seed + 1.
        readonly KotonParameter _seed         = new KotonParameter("seed",           "Seed",          0, 999, 1);
        // Articulation identique à l'arpégiateur (0=Legato/1=Normal/2=Detache/3=Staccato).
        readonly KotonParameter _articulation = new KotonParameter("articulation",   "Articulation",  0, 3, 1);

        // SPÉCIFIQUES — VOSS
        // Nombre de "dés" : plus il y en a, plus la mélodie est lisse (spectre 1/f plus pur). 8 = bon
        // équilibre entre variance et cohérence.
        readonly KotonParameter _vossOctaves  = new KotonParameter("voss_octaves",   "Dés",           2, 12, 8);

        // SPÉCIFIQUES — LOGISTIC
        // r : au-dessus de ~3.57 c'est le chaos. Défaut 3.85 = zone très musicale.
        readonly KotonParameter _logisticR    = new KotonParameter("logistic_r",     "r",             2.5, 4.0, 3.85);

        // SPÉCIFIQUES — LORENZ
        // Speed = dt d'intégration (0.001..0.05). Bas = mouvement lent contemplatif ; haut = agité.
        readonly KotonParameter _lorenzSpeed  = new KotonParameter("lorenz_speed",   "Vitesse",       0.001, 0.05, 0.01);
        // Dimension à lire : 0=X, 1=Y, 2=Z. Y donne le plus de variation, Z fait des cloches d'énergie.
        readonly KotonParameter _lorenzDim    = new KotonParameter("lorenz_dim",     "Axe",           0, 2, 1);

        readonly List<KotonParameter> _params;
        public IReadOnlyList<KotonParameter> Parameters => _params;

        double _durationBeats = 4.0;
        public double DurationBeats
        {
            get => _durationBeats;
            set { _durationBeats = value < 0.25 ? 0.25 : value; }
        }

        public FractalGenerator()
        {
            _params = new List<KotonParameter>
            {
                _algo, _notesPerBeat, _baseOctave, _range, _snapMode, _velocity, _velVar, _seed, _articulation,
                _vossOctaves,
                _logisticR,
                _lorenzSpeed, _lorenzDim,
            };
        }

        // =============================================================================================
        // Rendu vignette
        // =============================================================================================
        public KotonGeneratorDisplay GetTimelineDisplay()
        {
            // Violet fractal — distinguer de l'arpégiateur (vert) et des riffs standards (teal).
            var bg = Color.FromRgb(0x5B, 0x3B, 0x8A);
            int a = (int)_algo.Value;
            string algoName = a == 0 ? "1/f" : (a == 1 ? "Log" : "Lorenz");
            int nb = (int)_notesPerBeat.Value;
            return new KotonGeneratorDisplay
            {
                Background = bg,
                Text = "Fract " + algoName + " " + nb + "/tps",
            };
        }

        // =============================================================================================
        // Construction de la source selon l'algo courant + la seed
        // =============================================================================================
        IFractalSource BuildSource(int algo, int seed)
        {
            switch (algo)
            {
                case 0: return new VossSource((int)_vossOctaves.Value, seed);
                case 1: return new LogisticSource(_logisticR.Value, seed);
                case 2: return new LorenzSource(10.0, 28.0, 8.0 / 3.0, _lorenzSpeed.Value, (int)_lorenzDim.Value, seed);
                default: return new VossSource(8, seed);
            }
        }

        // =============================================================================================
        // Rendu des notes
        // =============================================================================================
        public IEnumerable<KotonGeneratedNote> RenderNotes(double startBeat, double endBeat, KotonRenderContext ctx)
        {
            int algo         = Math.Max(0, Math.Min(2, (int)_algo.Value));
            int notesPerBeat = Math.Max(1, Math.Min(8, (int)_notesPerBeat.Value));
            int baseOctave   = Math.Max(0, Math.Min(8, (int)_baseOctave.Value));
            int baseMidi     = 12 + baseOctave * 12;
            int range        = Math.Max(6, Math.Min(60, (int)_range.Value));
            int snapMode     = Math.Max(0, Math.Min(2, (int)_snapMode.Value));
            int velBase      = Math.Max(1, Math.Min(127, (int)_velocity.Value));
            double velVar    = Math.Max(0.0, Math.Min(1.0, _velVar.Value));
            int seed         = Math.Max(0, Math.Min(999, (int)_seed.Value));
            int articulation = Math.Max(0, Math.Min(3, (int)_articulation.Value));
            double gate      = GateFor(articulation);

            int tsNum = ctx?.TimeSigNum > 0 ? ctx.TimeSigNum : 4;
            int tsDen = ctx?.TimeSigDen > 0 ? ctx.TimeSigDen : 4;
            double tick = TickBeats(notesPerBeat, tsNum, tsDen);
            if (tick <= 0) yield break;

            double duration = Math.Max(0.25, DurationBeats);
            double blockStart = ctx?.BlockStartBeat ?? 0.0;
            int tonicPc = ctx?.Tonic ?? 0;
            bool isMajor = ctx?.IsMajor ?? true;

            var source = BuildSource(algo, seed);
            // RNG dédié aux variations de vélocité — indépendant de la source fractale pour ne pas
            // altérer sa dynamique déterministe.
            var velRng = new Random(seed * 31 + 7);

            for (double t = 0; t < duration - 1e-9; t += tick)
            {
                double x = source.Next();   // ∈ [0,1]

                // Snap au chord tones : construit un pool [baseMidi..baseMidi+range] à partir des notes
                // de l'accord courant répétées sur les octaves nécessaires, puis prend l'index le plus
                // proche du scalaire.
                int midi = SnapToTargetMidi(x, snapMode, baseMidi, range, blockStart + t, tonicPc, isMajor);
                if (midi < 0) continue;   // pas d'accord et snap chord-only : silence

                int vel = velBase;
                if (velVar > 0)
                {
                    double jitter = (velRng.NextDouble() * 2 - 1) * 30.0 * velVar;
                    vel = (int)Math.Round(velBase + jitter);
                    if (vel < 1) vel = 1; else if (vel > 127) vel = 127;
                }

                double dur = tick * gate;
                yield return new KotonGeneratedNote
                {
                    StartBeat = t,
                    DurationBeats = dur,
                    NotationDurationBeats = tick,
                    MidiNote = midi,
                    Velocity = vel,
                };
            }
        }

        /// <summary>Mappe un scalaire fractal [0,1] à une note MIDI en respectant le mode de snap.
        /// - <b>Chord</b> : construit un pool de notes de l'accord courant (via <c>KotonHost.GetChordAt</c>)
        ///   répliqué sur assez d'octaves pour couvrir [baseMidi..baseMidi+range], puis pick l'index le
        ///   plus proche. Fallback = accord tonique si pas d'accord posé.
        /// - <b>Scale</b> : construit une gamme diatonique majeure/mineure sur toute la plage.
        /// - <b>Chromatic</b> : mapping linéaire direct, aucune contrainte.
        /// Retourne -1 = "silence" (utilisé si snap=Chord et vraiment aucun accord ni fallback possible).</summary>
        static int SnapToTargetMidi(double x, int snapMode, int baseMidi, int range, double absBeat, int tonicPc, bool isMajor)
        {
            if (x < 0) x = 0; else if (x > 1) x = 1;

            if (snapMode == 2)   // Chromatic
            {
                int m = baseMidi + (int)Math.Round(x * range);
                return Math.Max(0, Math.Min(127, m));
            }

            // Construction du pool
            int[] pool;
            if (snapMode == 0)   // Chord tones
            {
                KotonChord? chOpt = KotonHost.GetChordAt?.Invoke(absBeat);
                KotonChord ch = chOpt ?? new KotonChord
                {
                    Root = tonicPc,
                    Quality = isMajor ? KotonChordQuality.Major : KotonChordQuality.Minor,
                };
                var notes = ch.GetMidiNotes(SnapRoot(baseMidi, ch.Root));
                pool = ExpandToRange(notes, baseMidi, range);
            }
            else   // Scale
            {
                pool = BuildScale(tonicPc, isMajor, baseMidi, range);
            }

            if (pool == null || pool.Length == 0) return -1;

            // Position dans le pool selon le scalaire.
            int idx = (int)Math.Round(x * (pool.Length - 1));
            if (idx < 0) idx = 0;
            else if (idx >= pool.Length) idx = pool.Length - 1;
            return pool[idx];
        }

        /// <summary>Ajuste baseMidi pour que sa pitch class soit celle de la racine — même idée que dans
        /// l'arpégiateur, garde le registre approximatif.</summary>
        static int SnapRoot(int baseMidi, int targetPc)
        {
            int basePc = ((baseMidi % 12) + 12) % 12;
            int delta = ((targetPc - basePc) + 12) % 12;
            if (delta >= 7) delta -= 12;
            return Math.Max(0, Math.Min(127, baseMidi + delta));
        }

        /// <summary>Réplique un voicing (3-4 notes) sur plusieurs octaves pour couvrir [baseMidi,
        /// baseMidi+range]. Retourne un tableau trié croissant, sans doublons.</summary>
        static int[] ExpandToRange(int[] baseNotes, int baseMidi, int range)
        {
            if (baseNotes == null || baseNotes.Length == 0) return Array.Empty<int>();
            var list = new List<int>();
            int top = baseMidi + range;
            for (int o = -2; o <= 6; o++)   // large gamme d'octaves ; on filtre après
            {
                foreach (var n in baseNotes)
                {
                    int p = n + o * 12;
                    if (p >= baseMidi && p <= top && p >= 0 && p <= 127) list.Add(p);
                }
            }
            list.Sort();
            // Dédupe (rare mais possible si spread + octaves se croisent)
            for (int i = list.Count - 1; i > 0; i--) if (list[i] == list[i - 1]) list.RemoveAt(i);
            return list.ToArray();
        }

        /// <summary>Construit la gamme diatonique (majeure ou mineure naturelle) sur la plage complète
        /// [baseMidi, baseMidi+range]. Utilisé par le mode snap=Scale.</summary>
        static int[] BuildScale(int tonicPc, bool isMajor, int baseMidi, int range)
        {
            int[] intervals = isMajor
                ? new[] { 0, 2, 4, 5, 7, 9, 11 }   // majeur ionien
                : new[] { 0, 2, 3, 5, 7, 8, 10 };  // mineur naturel (aeolien)
            var list = new List<int>();
            int top = baseMidi + range;
            for (int o = -2; o <= 6; o++)
            {
                foreach (var i in intervals)
                {
                    int p = tonicPc + i + o * 12;
                    if (p >= baseMidi && p <= top && p >= 0 && p <= 127) list.Add(p);
                }
            }
            list.Sort();
            for (int i = list.Count - 1; i > 0; i--) if (list[i] == list[i - 1]) list.RemoveAt(i);
            return list.ToArray();
        }

        static double GateFor(int articulation)
        {
            switch (articulation)
            {
                case 0: return 1.00;   // Legato
                case 1: return 0.75;   // Normal
                case 2: return 0.40;   // Detache
                case 3: return 0.15;   // Staccato
                default: return 0.75;
            }
        }

        /// <summary>Durée d'un tick en beats selon la métrique — même logique que l'arpégiateur pour
        /// que l'UX soit cohérente. En binaire (den=4) : 1=noire, 2=croche, 3=triolet, 4=double, etc.
        /// En ternaire (den=8) : 1=noire pointée, 3=croche, 6=double.</summary>
        static double TickBeats(int notesPerBeat, int tsNum, int tsDen)
        {
            if (notesPerBeat <= 0) return 0;
            if (tsDen == 8 && (tsNum == 6 || tsNum == 9 || tsNum == 12))
            {
                // Ternaire composé : 1 = dotted-quarter (3 croches), 3 = croche, 6 = double.
                if (notesPerBeat == 1) return 1.5;
                if (notesPerBeat == 3) return 0.5;
                if (notesPerBeat == 6) return 0.25;
                // Autres valeurs : approximation en fraction de beat de base.
                return 1.5 / notesPerBeat;
            }
            // Binaire : 1 = noire = 1 beat, 2 = croches, 3 = triolet de croches, 4 = doubles, etc.
            return 1.0 / notesPerBeat;
        }

        /// <summary>Bouton "Régénérer" côté éditeur : incrémente la seed (rebouclée sur 999). Effet =
        /// nouvelle séquence fractale avec les mêmes paramètres. Live-audible au prochain re-flatten
        /// via <c>KotonHost.NotifyDurationChanged</c> (invalidation implicite = pas nécessaire ici,
        /// le player re-render quand un param change).</summary>
        public void RerollSeed()
        {
            int next = (int)_seed.Value + 1;
            if (next > 999) next = 0;
            _seed.Value = next;
        }

        // =============================================================================================
        // Cycle plugin
        // =============================================================================================
        public bool HasEditor => true;
        public UserControl CreateEditor() => new FractalEditor(this);

        public byte[] SaveState()
        {
            try
            {
                var state = new PersistedState { DurationBeats = _durationBeats, Params = new Dictionary<string, double>() };
                foreach (var kp in _params) state.Params[kp.Id] = kp.Value;
                return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(state));
            }
            catch { return Array.Empty<byte>(); }
        }

        public void LoadState(byte[] state)
        {
            if (state == null || state.Length == 0) return;
            try
            {
                var s = JsonSerializer.Deserialize<PersistedState>(Encoding.UTF8.GetString(state));
                if (s == null) return;
                if (s.DurationBeats > 0) _durationBeats = s.DurationBeats;
                if (s.Params != null)
                {
                    foreach (var kp in _params)
                        if (s.Params.TryGetValue(kp.Id, out var v)) kp.Value = v;
                }
            }
            catch { /* blob corrompu → défauts */ }
        }

        public void Dispose() { /* rien à libérer */ }

        /// <summary>Helper pour l'éditeur : évite de dupliquer le mapping id → param.</summary>
        public void SetParam(string id, double value)
        {
            foreach (var kp in _params)
            {
                if (kp.Id == id) { kp.Value = value; return; }
            }
        }

        sealed class PersistedState
        {
            public double DurationBeats { get; set; }
            public Dictionary<string, double> Params { get; set; }
        }
    }
}
