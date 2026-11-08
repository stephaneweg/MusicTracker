using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows.Controls;
using System.Windows.Media;
using KotonStudio.Library;

namespace KotonPluginArabesque
{
    /// <summary>Le rôle harmonique d'une note produite (pour l'éditeur : couleur et légende).</summary>
    public enum NoteRole { Chord, Passing, Neighbor, Appoggiatura, Resolution, Cadence }

    /// <summary>Une note produite et son rôle.</summary>
    public struct RoledNote
    {
        public KotonGeneratedNote Note;
        public NoteRole Role;
    }

    /// <summary>
    /// Générateur mélodique d'après <b>Arabesque</b> (et l'Art Song de 1996) : un chemin fractal (système de fonctions
    /// itérées) parcourt une image ; à chaque point visité, la couleur du pixel propose une note — une composante la
    /// hauteur, une autre la durée (via un rythme écrit, ou librement), une autre la nuance, une autre l'articulation.
    ///
    /// <b>Les règles d'Art Song</b> (lues dans ARTSNG20.EXE) : le rythme écrit se joue dans l'ordre, en boucle, la
    /// couleur ne donnant la durée que sans rythme (ses bits bas) ; la nuance va de 127 - Nuances à 127 ; la hauteur
    /// bouge par degrés depuis la note précédente, la couleur donnant le pas (127 garde la note, plus sombre monte,
    /// plus clair descend) ; trop haut, elle redescend d'autant sous l'aigu ; sous le grave, elle se tait jusqu'à ce
    /// que la couleur la fasse remonter.
    ///
    /// <b>Ce qu'il ajoute à Arabesque</b> : la note proposée passe par l'harmonie de la timeline
    /// (<see cref="KotonHost.GetChordAt"/>) avant d'être jouée :
    /// <list type="bullet">
    /// <item>sur un <b>temps fort</b>, une note d'accord ; ou, avec la probabilité « Appoggiatures », la note étrangère
    /// est gardée en appoggiature (accentuée, sur la première moitié) et se résout par degré conjoint sur l'accord ;</item>
    /// <item>sur un <b>temps faible</b>, une note étrangère n'est gardée que si elle arrive par degré conjoint
    /// (≤ 2 demi-tons) et selon la probabilité « Passages » : <b>note de passage</b> si elle continue dans le même sens
    /// vers une note de l'accord, <b>broderie</b> sinon ; autrement elle glisse sur la note d'accord la plus proche ;</item>
    /// <item>les <b>sauts</b> sont bornés (« Saut max ») : la note est ramenée d'octave en octave vers la précédente ;</item>
    /// <item>les <b>respirations</b> : toutes les « Phrase » temps, la dernière note se pose sur une note stable de
    /// l'accord (fondamentale ou tierce, une <b>cadence</b>), puis un silence de « Respiration » temps ;</item>
    /// <item>les <b>silences</b> : la « Densité » laisse tomber des notes (le tirage suit le chemin, comme Arabesque) ;</item>
    /// <item>un <b>changement d'accord</b> sous une note qui sonne : si elle n'appartient pas au nouvel accord, elle est
    /// coupée et la note d'accord la plus proche est rejouée (conduite des voix).</item>
    /// </list>
    ///
    /// <b>Reproductible</b> : tout est recalculé à chaque <see cref="RenderNotes"/> depuis la graine ; même tracé, même
    /// image, même graine, mêmes accords → mêmes notes.
    /// </summary>
    [KotonGenerator("Arabesque", Id = "koton.arabesque", Type = KotonGeneratorType.Melody, Version = "1.0", Vendor = "Koton Studio")]
    public sealed class ArabesqueGenerator : IKotonGenerator
    {
        public string Id => "koton.arabesque";
        public string DisplayName => "Arabesque";
        public KotonGeneratorType GeneratorType => KotonGeneratorType.Melody;

        // ---- le chemin ----
        // Tracé : 0..5 les tracés livrés, 6 = fichier .ifs (TraceFile).
        readonly KotonParameter _trace     = new KotonParameter("trace",      "Tracé",          0, 6, 0);
        // Image : 0 = mandala calculé, 1 = fichier (ImageFile), 2..7 = Mandelbrot, Julia, Burning Ship, Lyapunov,
        // Newton, Rayons (Fractals) ; la graine d'une image calculée est « mandala » (le nom d'avant, gardé).
        readonly KotonParameter _image     = new KotonParameter("image",      "Image",          0, 7, 0);
        readonly KotonParameter _mandala   = new KotonParameter("mandala",    "Graine d'image", 1, 999, 3);
        readonly KotonParameter _seed      = new KotonParameter("seed",       "Graine",         0, 999, 1);
        readonly KotonParameter _phase     = new KotonParameter("phase",      "Phase",          0, 2000, 100);
        // ---- la couleur ----
        // Composantes : 0 rouge, 1 vert, 2 bleu ; articulation : 0 fixe, 1..3 rouge/vert/bleu.
        readonly KotonParameter _mapPitch  = new KotonParameter("map_pitch",  "Hauteur",        0, 2, 0);
        readonly KotonParameter _mapDur    = new KotonParameter("map_dur",    "Durée",          0, 2, 1);
        readonly KotonParameter _mapVel    = new KotonParameter("map_vel",    "Nuance",         0, 2, 2);
        readonly KotonParameter _mapArt    = new KotonParameter("map_art",    "Articulation",   0, 3, 0);
        // ---- le rythme ----
        // Unité : 0 blanche, 1 noire, 2 croche, 3 triolet, 4 double, 5 sextolet.
        readonly KotonParameter _unit      = new KotonParameter("unit",       "Unité",          0, 5, 2);
        readonly KotonParameter _density   = new KotonParameter("density",    "Densité",        10, 100, 85, "%");
        readonly KotonParameter _gate      = new KotonParameter("gate",       "Tenue",          30, 100, 90, "%");
        // ---- la hauteur ----
        readonly KotonParameter _low       = new KotonParameter("low",        "Grave",          24, 108, 60);
        readonly KotonParameter _high      = new KotonParameter("high",       "Aigu",           24, 108, 84);
        // Notes proposées : 0 gamme du projet, 1 pentatonique du projet, 2 notes de l'accord seules.
        readonly KotonParameter _pool      = new KotonParameter("pool",       "Notes",          0, 2, 0);
        readonly KotonParameter _maxLeap   = new KotonParameter("max_leap",   "Saut max",       3, 19, 9, "st");
        // ---- l'harmonie ----
        readonly KotonParameter _passing   = new KotonParameter("passing",    "Passages",       0, 100, 45, "%");
        readonly KotonParameter _appogg    = new KotonParameter("appogg",     "Appoggiatures",  0, 100, 20, "%");
        readonly KotonParameter _phrase    = new KotonParameter("phrase",     "Phrase",         0, 16, 8, "temps");
        readonly KotonParameter _breath    = new KotonParameter("breath",     "Respiration",    0, 2, 0.5, "temps");
        readonly KotonParameter _range     = new KotonParameter("range",      "Nuances",        10, 80, 50);

        readonly List<KotonParameter> _params;
        public IReadOnlyList<KotonParameter> Parameters => _params;

        double _durationBeats = 16.0;
        public double DurationBeats
        {
            get => _durationBeats;
            set { _durationBeats = value < 0.25 ? 0.25 : value; }
        }

        // Ce qui n'est pas un nombre : le rythme écrit (un chiffre par coefficient de l'unité ; vide = libre) et les
        // fichiers. Écrits sous verrou : le thread audio les lit.
        readonly object _lock = new object();
        string _rhythm = "2112";
        string _traceFile = "", _imageFile = "";
        public string Rhythm { get { lock (_lock) return _rhythm; } set { lock (_lock) _rhythm = value ?? ""; } }
        public string TraceFile { get { lock (_lock) return _traceFile; } set { lock (_lock) { _traceFile = value ?? ""; _ifsKey = null; } } }
        public string ImageFile { get { lock (_lock) return _imageFile; } set { lock (_lock) { _imageFile = value ?? ""; _picKey = null; } } }

        public ArabesqueGenerator()
        {
            _params = new List<KotonParameter>
            {
                _trace, _image, _mandala, _seed, _phase,
                _mapPitch, _mapDur, _mapVel, _mapArt,
                _unit, _density, _gate,
                _low, _high, _pool, _maxLeap,
                _passing, _appogg, _phrase, _breath, _range,
            };
        }

        public KotonGeneratorDisplay GetTimelineDisplay()
        {
            int t = (int)_trace.Value;
            string name = t < Ifs.Builtin.Length ? Ifs.Builtin[t].Name : Path.GetFileNameWithoutExtension(TraceFile);
            return new KotonGeneratorDisplay { Background = Color.FromRgb(0x7A, 0x4F, 0xC8), Text = "Arabesque · " + name };
        }

        // =============================================================================================
        // Le tracé et l'image, gardés tant qu'ils ne changent pas
        // =============================================================================================
        string _ifsKey, _picKey;
        Ifs _ifs;
        Picture _pic;

        /// <summary>Le tracé courant (une copie : chaque rendu a son propre état de tirage).</summary>
        public Ifs CurrentIfs()
        {
            lock (_lock)
            {
                int t = Math.Max(0, Math.Min(6, (int)_trace.Value));
                string key = t + "|" + (t == 6 ? _traceFile : "");
                if (_ifsKey != key || _ifs == null)
                {
                    Ifs f = null;
                    if (t == 6 && File.Exists(_traceFile)) { try { f = Ifs.Parse(File.ReadAllText(_traceFile, Encoding.Latin1)); } catch { } }
                    _ifs = f ?? Ifs.Parse(Ifs.Builtin[t < 6 ? t : 0].Code);
                    _ifsKey = key;
                }
                return _ifs;
            }
        }

        public Picture CurrentPicture()
        {
            lock (_lock)
            {
                int m = Math.Max(0, Math.Min(7, (int)_image.Value));
                uint seed = (uint)Math.Max(1, (int)_mandala.Value);
                string key = m == 1 ? "f|" + _imageFile : m + "|" + seed;
                if (_picKey != key || _pic == null)
                {
                    Picture p = null;
                    if (m == 1 && File.Exists(_imageFile)) p = Picture.Load(_imageFile);
                    // (les fractales en 512 comme dans Arabesque : mêmes pixels ; le mandala reste en 256, comme avant)
                    else if (m >= 2) p = Fractals.Make(m - 2, 512, 512, seed);
                    _pic = p ?? Picture.Mandala(256, 256, seed);
                    _picKey = key;
                }
                return _pic;
            }
        }

        // =============================================================================================
        // Le rendu
        // =============================================================================================
        public IEnumerable<KotonGeneratedNote> RenderNotes(double startBeat, double endBeat, KotonRenderContext ctx)
        {
            foreach (var r in Compose(ctx, null)) yield return r.Note;
        }

        static readonly double[] UnitBeats = { 2.0, 1.0, 0.5, 1.0 / 3, 0.25, 1.0 / 6 };
        static readonly int[] ArtPct = { 50, 75, 90, 100 };

        /// <summary>Toute la mélodie du bloc, avec le rôle de chaque note. <paramref name="path"/> reçoit, si fourni, les
        /// points visités (0..1) — pour l'éditeur.</summary>
        public List<RoledNote> Compose(KotonRenderContext ctx, List<(double U, double V, bool Played)> path)
        {
            var output = new List<RoledNote>();
            var ifsSrc = CurrentIfs();
            var pic = CurrentPicture();
            if (ifsSrc == null || pic == null) return output;

            // les paramètres, lus une fois (cohérents pendant tout le rendu)
            int seed = (int)_seed.Value, phase = (int)_phase.Value;
            int mapP = (int)_mapPitch.Value % 3, mapD = (int)_mapDur.Value % 3, mapV = (int)_mapVel.Value % 3, mapA = (int)_mapArt.Value & 3;
            double unit = UnitBeats[Math.Max(0, Math.Min(5, (int)_unit.Value))];
            int density = (int)_density.Value, gatePct = (int)_gate.Value;
            int low = (int)Math.Min(_low.Value, _high.Value), high = (int)Math.Max(_low.Value, _high.Value);
            int poolMode = (int)_pool.Value, maxLeap = (int)_maxLeap.Value;
            int passing = (int)_passing.Value, appogg = (int)_appogg.Value, vrange = (int)_range.Value;
            double phraseBeats = Math.Round(_phrase.Value), breath = Math.Max(0, _breath.Value);
            var coef = new List<int>();
            foreach (char c in Rhythm) if (c > '0' && c <= '9') coef.Add(c - '0');

            int tonic = ctx?.Tonic ?? 0;
            bool major = ctx?.IsMajor ?? true;
            double blockStart = ctx?.BlockStartBeat ?? 0, pickup = ctx?.PickupBeats ?? 0;
            double duration = Math.Max(0.25, DurationBeats);

            // les notes proposées par la couleur : la gamme (ou la pentatonique) du projet dans la tessiture
            int[] steps = poolMode == 1 ? (major ? new[] { 0, 2, 4, 7, 9 } : new[] { 0, 3, 5, 7, 10 })
                                        : (major ? new[] { 0, 2, 4, 5, 7, 9, 11 } : new[] { 0, 2, 3, 5, 7, 8, 10 });
            var scale = new List<int>();
            for (int k = low; k <= high; k++) if (Array.IndexOf(steps, ((k - tonic) % 12 + 12) % 12) >= 0) scale.Add(k);
            if (scale.Count == 0) scale.Add((low + high) / 2);

            // le chemin, depuis la graine (comme Arabesque : on écarte le début, puis la phase)
            // (les mêmes tirages qu'Arabesque pour sa première partie : Composer.Start puis InitVoice)
            var ifs = ifsSrc;
            var rng = new Rng((uint)seed);
            double x = 0, y = 0;
            for (int i = 0; i < 64 + phase; i++) ifs.Step(ref x, ref y, ref rng);
            rng = new Rng((uint)seed + 1);

            int prev = -1;
            int rpos = 0;                    // le rang dans le rythme écrit
            int pos = 0;                     // le rang de la hauteur dans la gamme (négatif : sous le grave, silence)
            bool fresh = true;               // la première note de la phrase : la couleur la place
            int perOct = steps.Length, top = scale.Count - 1;
            double t = 0;
            int guard = 0;
            while (t < duration - 1e-9 && guard++ < 20000)
            {
                ifs.Step(ref x, ref y, ref rng);
                ifs.ToUnit(x, y, out double u, out double v);
                uint px = pic.At(u, v);
                int[] col = { (int)(px >> 16) & 255, (int)(px >> 8) & 255, (int)px & 255 };

                // la durée : le rythme écrit dans l'ordre ; sans rythme, les bits bas de la couleur (8, sinon 1 à 7, sinon 1)
                int cd = col[mapD], mult;
                if (coef.Count > 0) { mult = coef[rpos]; rpos = (rpos + 1) % coef.Count; }
                else mult = (cd & 8) != 0 ? 8 : (cd & 7) != 0 ? cd & 7 : 1;
                double dur = unit * mult;
                if (t + dur > duration) dur = duration - t;

                // la respiration : la note qui atteint la fin de la phrase devient la cadence, puis un silence
                bool cadence = false;
                if (phraseBeats > 0)
                {
                    double rel = t - pickup + blockStart;
                    double phraseEnd = (Math.Floor(rel / phraseBeats + 1e-9) + 1) * phraseBeats + pickup - blockStart;
                    double rest = phraseEnd - Math.Min(breath, phraseBeats * 0.5);
                    if (t >= rest - 1e-9)
                    {
                        path?.Add((u, v, false));
                        t = phraseEnd;
                        prev = -1; fresh = true; // (une phrase neuve peut repartir d'ailleurs)
                        continue;
                    }
                    if (t + dur > rest + 1e-9) { dur = rest - t; cadence = true; }
                    if (dur < unit * 0.5 - 1e-9) { path?.Add((u, v, false)); t = phraseEnd; prev = -1; fresh = true; continue; }
                }

                // les silences
                if (rng.Below(100) >= density) { path?.Add((u, v, false)); t += dur; continue; }

                // la hauteur proposée : un pas depuis la note jouée (Art Song) ; pas de -n/2 à +n/2 environ, n étant la
                // tessiture (au plus une octave de la gamme). Trop haut : n - pas ; sous le grave : silence
                if (fresh) { pos = Math.Min(top, col[mapP] * scale.Count / 256); fresh = false; }
                else
                {
                    if (pos >= 0 && prev >= 0) pos = scale.IndexOf(Nearest(scale, prev));
                    int move = (int)Math.Round(Math.Min(top, perOct) * (127 - col[mapP]) / 255.0);
                    pos = pos + move > top ? top - move : pos + move < 0 ? move : pos + move;
                    if (pos < 0) { path?.Add((u, v, false)); t += dur; continue; }
                }
                path?.Add((u, v, true));
                // (ramenée près de la précédente si le saut est trop grand)
                int raw = scale[pos];
                if (prev >= 0)
                {
                    while (raw - prev > maxLeap && raw - 12 >= low) raw -= 12;
                    while (prev - raw > maxLeap && raw + 12 <= high) raw += 12;
                    if (Math.Abs(raw - prev) > maxLeap) raw = Nearest(scale, prev + Math.Sign(raw - prev) * maxLeap);
                }

                double abs = blockStart + t;
                var chord = ChordAt(abs, tonic, major);
                int[] pcs = ChordPcs(chord);
                bool strong = IsOnBeat(t + blockStart - pickup);
                int vel = 127 - vrange + col[mapV] * vrange / 255 + (strong ? 6 : 0);
                double pct = (mapA == 0 ? gatePct : ArtPct[col[mapA - 1] * 4 / 256]) / 100.0;

                if (cadence)
                {
                    // une note stable : la fondamentale ou la tierce, la plus proche
                    int target = NearestOf(raw, low, high, new[] { chord.Root, pcs.Length > 1 ? pcs[1] : chord.Root });
                    EmitWithChanges(output, t, dur, 1.0, target, vel - 4, NoteRole.Cadence, abs, tonic, major, low, high, ref prev);
                }
                else if (poolMode == 2 || InChord(raw, pcs))
                {
                    int target = InChord(raw, pcs) ? raw : NearestOf(raw, low, high, pcs);
                    EmitWithChanges(output, t, dur, pct, target, vel, NoteRole.Chord, abs, tonic, major, low, high, ref prev);
                }
                else if (strong)
                {
                    int res = StepResolution(raw, pcs, low, high);
                    if (res >= 0 && dur >= unit * 2 - 1e-9 && rng.Below(100) < appogg)
                    {
                        // l'appoggiature : la dissonance sur le temps, accentuée, liée à sa résolution
                        // (la résolution suit l'accord qui sonne alors : il a pu changer pendant l'appoggiature)
                        double half = dur / 2;
                        int[] later = ChordPcs(ChordAt(abs + half, tonic, major));
                        int res2 = StepResolution(raw, later, low, high);
                        if (res2 < 0) res2 = NearestOf(raw, low, high, later);
                        Emit(output, t, half, 1.0, raw, vel + 8, NoteRole.Appoggiatura, ref prev);
                        EmitWithChanges(output, t + half, half, pct, res2, Math.Max(1, vel - 10), NoteRole.Resolution, abs + half, tonic, major, low, high, ref prev);
                    }
                    else
                    {
                        int target = NearestOf(prev >= 0 ? (raw + prev) / 2 : raw, low, high, pcs);
                        EmitWithChanges(output, t, dur, pct, target, vel, NoteRole.Chord, abs, tonic, major, low, high, ref prev);
                    }
                }
                else
                {
                    // temps faible : un degré de la gamme depuis la note précédente, dans la direction que propose la
                    // couleur ; étranger à l'accord, c'est une note de passage s'il mène à une note de l'accord, une
                    // broderie sinon (la couleur qui ne bouge pas fait une broderie, d'un côté ou de l'autre)
                    int dir = prev >= 0 ? Math.Sign(raw - prev) : 0;
                    if (dir == 0) dir = (col[mapV] & 1) == 0 ? 1 : -1;
                    int cand = prev >= 0 ? ScaleStep(scale, prev, dir) : -1;
                    if (cand >= 0 && !InChord(cand, pcs) && rng.Below(100) < passing)
                    {
                        int after = ScaleStep(scale, cand, dir);
                        bool goesOn = after >= 0 && InChord(after, pcs) && Math.Sign(raw - prev) != 0;
                        Emit(output, t, dur, pct, cand, Math.Max(1, vel - 6), goesOn ? NoteRole.Passing : NoteRole.Neighbor, ref prev);
                    }
                    else
                    {
                        int target = NearestOf(raw, low, high, pcs);
                        EmitWithChanges(output, t, dur, pct, target, vel, NoteRole.Chord, abs, tonic, major, low, high, ref prev);
                    }
                }
                t += dur;
            }
            return output;
        }

        // ---- les notes ----
        static void Emit(List<RoledNote> o, double t, double dur, double pct, int key, int vel, NoteRole role, ref int prev)
        {
            if (dur <= 1e-6) return;
            o.Add(new RoledNote
            {
                Role = role,
                Note = new KotonGeneratedNote
                {
                    StartBeat = t,
                    DurationBeats = Math.Max(0.02, dur * pct),
                    NotationDurationBeats = dur,
                    MidiNote = Math.Max(0, Math.Min(127, key)),
                    Velocity = Math.Max(1, Math.Min(127, vel)),
                },
            });
            prev = key;
        }

        /// <summary>Une note d'accord qui dure : si l'accord change sous elle et qu'elle n'en fait plus partie, elle est
        /// coupée là et la note du nouvel accord la plus proche est rejouée (conduite des voix).</summary>
        void EmitWithChanges(List<RoledNote> o, double t, double dur, double pct, int key, int vel, NoteRole role,
                             double abs, int tonic, bool major, int low, int high, ref int prev)
        {
            double start = t, end = t + dur;
            int cur = key;
            for (double b = Math.Floor(t + 1e-9) + 1; b < end - 1e-9; b += 1)
            {
                var ch = ChordAt(abs + (b - t), tonic, major);
                int[] pcs = ChordPcs(ch);
                if (InChord(cur, pcs)) continue;
                Emit(o, start, b - start, start == t ? pct : 1.0, cur, start == t ? vel : vel - 8, role, ref prev);
                start = b;
                cur = NearestOf(cur, low, high, pcs);
            }
            Emit(o, start, end - start, pct, cur, start == t ? vel : vel - 8, role, ref prev);
        }

        // ---- l'harmonie ----
        static KotonChord ChordAt(double absBeat, int tonic, bool major)
        {
            KotonChord? c = null;
            try { c = KotonHost.GetChordAt?.Invoke(absBeat); } catch { }
            return c ?? new KotonChord { Root = tonic, Quality = major ? KotonChordQuality.Major : KotonChordQuality.Minor };
        }
        static int[] ChordPcs(KotonChord c)
        {
            var n = c.GetMidiNotes(0);
            for (int i = 0; i < n.Length; i++) n[i] = ((c.Root + n[i]) % 12 + 12) % 12;
            return n;
        }
        static bool InChord(int key, int[] pcs) => Array.IndexOf(pcs, ((key % 12) + 12) % 12) >= 0;
        static bool IsOnBeat(double beat) => Math.Abs(beat - Math.Round(beat)) < 1e-6;

        /// <summary>La note de ces classes de hauteur la plus proche de <paramref name="key"/>, dans la tessiture.</summary>
        static int NearestOf(int key, int low, int high, int[] pcs)
        {
            int best = -1, bd = int.MaxValue;
            for (int k = Math.Max(0, low - 12); k <= Math.Min(127, high + 12); k++)
            {
                if (!InChord(k, pcs)) continue;
                int d = Math.Abs(k - key) * 2 + (k < low || k > high ? 25 : 0);
                if (d < bd) { bd = d; best = k; }
            }
            return best >= 0 ? best : key;
        }
        /// <summary>Le degré voisin de <paramref name="key"/> dans la gamme, vers le haut ou le bas (-1 : au bord).</summary>
        static int ScaleStep(List<int> scale, int key, int dir)
        {
            if (dir > 0) { foreach (int k in scale) if (k > key) return k - key <= 2 ? k : -1; return -1; }
            for (int i = scale.Count - 1; i >= 0; i--) if (scale[i] < key) return key - scale[i] <= 2 ? scale[i] : -1;
            return -1;
        }
        static int Nearest(List<int> pool, int key)
        {
            int best = pool[0];
            foreach (int k in pool) if (Math.Abs(k - key) < Math.Abs(best - key)) best = k;
            return best;
        }
        /// <summary>La résolution d'une appoggiature : la note d'accord à un ou deux demi-tons, plutôt en descendant.</summary>
        static int StepResolution(int key, int[] pcs, int low, int high)
        {
            foreach (int d in new[] { -1, -2, 1, 2 })
                if (InChord(key + d, pcs) && key + d >= low - 2 && key + d <= high + 2) return key + d;
            return -1;
        }

        // =============================================================================================
        // Cycle plugin
        // =============================================================================================
        public bool HasEditor => true;
        public UserControl CreateEditor() => new ArabesqueEditor(this);

        public void RerollSeed() { int n = (int)_seed.Value + 1; _seed.Value = n > 999 ? 0 : n; }
        /// <summary>Une autre image de la même sorte (la graine suivante) ; depuis un fichier, un mandala.</summary>
        public void NewMandala() { int n = (int)_mandala.Value + 1; _mandala.Value = n > 999 ? 1 : n; if ((int)_image.Value == 1) _image.Value = 0; }

        public void SetParam(string id, double value)
        {
            foreach (var kp in _params) if (kp.Id == id) { kp.Value = value; return; }
        }
        public double GetParam(string id)
        {
            foreach (var kp in _params) if (kp.Id == id) return kp.Value;
            return 0;
        }

        public byte[] SaveState()
        {
            try
            {
                var s = new PersistedState { DurationBeats = _durationBeats, Rhythm = Rhythm, TraceFile = TraceFile, ImageFile = ImageFile, Params = new Dictionary<string, double>() };
                foreach (var kp in _params) s.Params[kp.Id] = kp.Value;
                return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(s));
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
                Rhythm = s.Rhythm; TraceFile = s.TraceFile; ImageFile = s.ImageFile;
                if (s.Params != null)
                    foreach (var kp in _params)
                        if (s.Params.TryGetValue(kp.Id, out var v)) kp.Value = v;
            }
            catch { /* blob corrompu → défauts */ }
        }

        public void Dispose() { }

        sealed class PersistedState
        {
            public double DurationBeats { get; set; }
            public string Rhythm { get; set; }
            public string TraceFile { get; set; }
            public string ImageFile { get; set; }
            public Dictionary<string, double> Params { get; set; }
        }
    }
}
