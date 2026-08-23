using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Windows.Controls;
using System.Windows.Media;
using KotonStudio.Library;

namespace KotonPluginSplineMelody
{
    /// <summary>
    /// Spline mélodique — générateur multi-voix où l'UTILISATEUR dessine le CONTOUR (courbe Y vs
    /// temps) et un motif rythmique par voix. Le moteur échantillonne la spline à chaque frappe
    /// rythmique, normalise par le point le plus extrême (|Y| max), multiplie par l'ambitus, arrondit
    /// puis snap harmoniquement (chord-tone sur temps fort, scale-tone sinon).
    ///
    /// **Modèle de voix** :
    ///   - liste de <c>ControlPoint</c> (T ∈ [0,1] normalisé sur la durée du bloc, Y en unités arbitraires)
    ///   - motif rythmique <c>KotonRhythm</c> réutilisé (comme l'arpégiateur), loop dans la durée du bloc
    ///   - couleur d'affichage (UI seulement, non lue par le moteur)
    ///
    /// **Ambitus** : NON un clamp. C'est un facteur d'échelle : le point le plus extrême de la
    /// courbe (|Y| max) mappe à ±ambitus semitons, le reste s'interpole linéairement. Une courbe
    /// plate (tous points à 0) → target = 0 (juste la note de départ).
    ///
    /// **Note de départ** :
    ///   - Fixed : MIDI absolu réglé dans les options
    ///   - Auto  : root de l'accord sous le début du bloc (P1 dégradé — l'idée finale « note du module
    ///     précédent » demande accès au flatten inter-module, non disponible dans l'API générateur)
    ///
    /// **Snap harmonique** :
    ///   - beat entier (position tombe sur une pulsation) → nearest chord-tone dans la table étendue
    ///   - autre → nearest scale-tone (majeur ou mineur naturel selon <c>ctx.IsMajor</c>)
    ///
    /// **Interpolation** : linéaire (défaut) ou Catmull-Rom (courbes lisses) — choix par voix
    /// éventuellement, v1 = global.
    /// </summary>
    [KotonGenerator("Spline mélodique", Id = "koton.splinemelody", Type = KotonGeneratorType.Melody, Version = "1.0", Vendor = "Koton Studio")]
    public sealed class SplineMelodyGenerator : IKotonGenerator
    {
        public string Id => "koton.splinemelody";
        public string DisplayName => "Spline mélodique";
        public KotonGeneratorType GeneratorType => KotonGeneratorType.Melody;

        // Cap dur : 4 voix suffisent pour un lead + 3 contre-chants. Plus = UI illisible + confusion
        // rythmique quand tous les motifs sont indépendants.
        public const int MaxVoices = 4;

        // -----------------------------------------------------------------------------------------
        // Paramètres globaux
        // -----------------------------------------------------------------------------------------
        //   voice_count       1..MaxVoices (int) — voix actives
        //   duration_bars     1..16 (double)     — durée du bloc en MESURES (converti en beats à partir de ctx)
        //   start_mode        0=Fixed / 1=Auto
        //   start_midi        0..127 (int)       — utilisé si start_mode=Fixed
        //   ambitus_semis     1..24 (int)        — facteur d'échelle (voir doc de classe)
        //   interpolation     0=Linear / 1=Spline (Catmull-Rom)
        //   velocity          1..127 (int)
        //   articulation      0=Legato / 1=Normal / 2=Détaché / 3=Staccato
        readonly KotonParameter _voiceCount    = new KotonParameter("voice_count",    "Voix",           1, MaxVoices, 1);
        readonly KotonParameter _durationBars  = new KotonParameter("duration_bars",  "Durée",          1, 16, 4, "mes");
        readonly KotonParameter _startMode     = new KotonParameter("start_mode",     "Note départ",    0, 1, 1);
        readonly KotonParameter _startMidi     = new KotonParameter("start_midi",     "Note départ MIDI", 0, 127, 60);
        readonly KotonParameter _ambitusSemis  = new KotonParameter("ambitus_semis",  "Ambitus",        1, 24, 12, "st");
        readonly KotonParameter _interpolation = new KotonParameter("interpolation",  "Interpolation",  0, 1, 0);
        readonly KotonParameter _velocity      = new KotonParameter("velocity",       "Vélocité",       1, 127, 100);
        readonly KotonParameter _articulation  = new KotonParameter("articulation",   "Articulation",   0, 3, 1);

        readonly List<KotonParameter> _params;
        public IReadOnlyList<KotonParameter> Parameters => _params;

        // -----------------------------------------------------------------------------------------
        // État par voix (spline + rythme + couleur). Adressable par l'éditeur pour manipuler
        // directement les points ; le moteur lit en snapshot au début de RenderNotes.
        // -----------------------------------------------------------------------------------------
        public sealed class VoiceSpec
        {
            /// <summary>Points de contrôle. T ∈ [0, 1], Y en unités arbitraires (le max |Y| = 1 en
            /// représentation logique côté moteur — c'est le max qui définit l'ambitus).</summary>
            public List<ControlPoint> Points { get; } = new List<ControlPoint>();

            /// <summary>Motif rythmique loopé sur la durée du bloc (comme l'arpégiateur).</summary>
            public KotonRhythm Rhythm { get; set; } = new KotonRhythm { Beats = 2, SlicesPerBeat = 4 };

            /// <summary>Couleur d'affichage (canvas + tabs). Assignée au chargement.</summary>
            public Color Color { get; set; }
        }

        /// <summary>Un point d'ancrage sur la spline. T = position temporelle normalisée [0,1],
        /// Y = valeur de contour en unités arbitraires (le moteur normalise par |Y| max).</summary>
        public struct ControlPoint
        {
            public double T;
            public double Y;
            public ControlPoint(double t, double y) { T = t; Y = y; }
        }

        readonly VoiceSpec[] _voices = new VoiceSpec[MaxVoices];

        /// <summary>Accès direct pour l'éditeur — instancie à la demande si absent.</summary>
        public VoiceSpec GetVoice(int i)
        {
            if (i < 0 || i >= MaxVoices) return null;
            if (_voices[i] == null)
            {
                _voices[i] = new VoiceSpec { Color = DefaultColorFor(i) };
                // Spline par défaut : ligne horizontale (0 = note de départ).
                _voices[i].Points.Add(new ControlPoint(0.0, 0.0));
                _voices[i].Points.Add(new ControlPoint(1.0, 0.0));
            }
            return _voices[i];
        }

        static readonly Color[] DefaultVoiceColors =
        {
            Color.FromRgb(0x1F, 0xB6, 0xC3),   // teal Koton
            Color.FromRgb(0xE5, 0x9C, 0x4A),   // ambre
            Color.FromRgb(0x9E, 0x6F, 0xE0),   // violet
            Color.FromRgb(0x6E, 0xC7, 0x77),   // vert
        };
        public static Color DefaultColorFor(int i) => DefaultVoiceColors[((i % DefaultVoiceColors.Length) + DefaultVoiceColors.Length) % DefaultVoiceColors.Length];

        // -----------------------------------------------------------------------------------------
        // Cycle plugin
        // -----------------------------------------------------------------------------------------

        public SplineMelodyGenerator()
        {
            _params = new List<KotonParameter>
            {
                _voiceCount, _durationBars, _startMode, _startMidi, _ambitusSemis,
                _interpolation, _velocity, _articulation,
            };
            // Instancie la voix 1 par défaut (comportement raisonnable pour une preview immédiate).
            GetVoice(0);
        }

        public bool HasEditor => true;
        public UserControl CreateEditor() => new SplineMelodyEditor(this);

        public void Dispose() { }

        // -----------------------------------------------------------------------------------------
        // Durée : convertit bars ↔ beats selon la métrique courante (via KotonHost).
        // -----------------------------------------------------------------------------------------

        double _durationBeatsCache = 16.0;
        public double DurationBeats
        {
            get => _durationBeatsCache;
            set
            {
                // Le host propage une durée en beats quand l'utilisateur redimensionne la vignette.
                // On la stocke telle quelle ; l'éditeur affiche "bars" en la reconvertissant via ctx.
                _durationBeatsCache = value < 0.25 ? 0.25 : value;
                // Reflète dans le param bars pour rester cohérent (le combo affichera la nouvelle valeur).
                var ctx = KotonHost.CurrentContext?.Invoke();
                int tsNum = ctx?.TimeSigNum > 0 ? ctx.TimeSigNum : 4;
                int tsDen = ctx?.TimeSigDen > 0 ? ctx.TimeSigDen : 4;
                double beatsPerBar = BeatsPerBar(tsNum, tsDen);
                double bars = _durationBeatsCache / beatsPerBar;
                if (bars < 1) bars = 1;
                _durationBars.Value = Math.Round(bars);
            }
        }

        /// <summary>Beats par mesure : binaire simple = numérateur ; ternaire (6/8, 9/8, 12/8) =
        /// numérateur/3 (car chaque « temps » de compound vaut une noire pointée = 1.5 quarter).
        /// La signature 3/4 reste 3 quarters/bar.</summary>
        static double BeatsPerBar(int tsNum, int tsDen)
        {
            // Ternaire (compound) : dénominateur 8 et numérateur multiple de 3.
            if (tsDen == 8 && tsNum > 0 && tsNum % 3 == 0)
                return (tsNum / 3) * 1.5;
            // Binaire (simple) : numérateur * (4 / dénominateur) — 4/4 = 4, 3/4 = 3, 2/4 = 2, 4/8 = 2.
            return tsNum * (4.0 / Math.Max(1, tsDen));
        }

        internal double DurationBarsValue => _durationBars.Value;
        internal void SetDurationBars(double bars)
        {
            if (bars < 1) bars = 1;
            _durationBars.Value = Math.Round(bars);
            var ctx = KotonHost.CurrentContext?.Invoke();
            int tsNum = ctx?.TimeSigNum > 0 ? ctx.TimeSigNum : 4;
            int tsDen = ctx?.TimeSigDen > 0 ? ctx.TimeSigDen : 4;
            _durationBeatsCache = _durationBars.Value * BeatsPerBar(tsNum, tsDen);
            try { KotonHost.NotifyDurationChanged?.Invoke(_durationBeatsCache); } catch { }
        }

        // -----------------------------------------------------------------------------------------
        // Vignette timeline
        // -----------------------------------------------------------------------------------------

        public KotonGeneratorDisplay GetTimelineDisplay()
        {
            var bg = Color.FromRgb(0x2A, 0x60, 0x7C);   // bleu-teal (distinct du vert arp)
            int nv = Math.Max(1, Math.Min(MaxVoices, (int)_voiceCount.Value));
            string txt = "Spline " + nv + "v";
            return new KotonGeneratorDisplay { Background = bg, Text = txt };
        }

        // -----------------------------------------------------------------------------------------
        // Rendu de notes
        // -----------------------------------------------------------------------------------------

        public IEnumerable<KotonGeneratedNote> RenderNotes(double startBeat, double endBeat, KotonRenderContext ctx)
        {
            int voiceCount = Math.Max(1, Math.Min(MaxVoices, (int)_voiceCount.Value));
            int startMode = (int)_startMode.Value;
            int startMidiFixed = Math.Max(0, Math.Min(127, (int)_startMidi.Value));
            int ambitus = Math.Max(1, Math.Min(24, (int)_ambitusSemis.Value));
            bool spline = _interpolation.Value >= 0.5;
            int velocity = Math.Max(1, Math.Min(127, (int)_velocity.Value));
            int articulation = Math.Max(0, Math.Min(3, (int)_articulation.Value));
            double gate = GateFor(articulation);

            int tsNum = ctx?.TimeSigNum > 0 ? ctx.TimeSigNum : 4;
            int tsDen = ctx?.TimeSigDen > 0 ? ctx.TimeSigDen : 4;
            double duration = _durationBeatsCache;
            if (duration <= 0) yield break;
            double blockStart = ctx?.BlockStartBeat ?? 0.0;

            // Note de départ : Fixed = param ; Auto = root de l'accord sous le début du bloc (fallback
            // tonique si trou). L'idée « choix parmi accord selon dernière note du module précédent »
            // demande un état inter-module non disponible dans l'API — pour P1 on prend le root.
            int startMidi;
            if (startMode == 0) startMidi = startMidiFixed;
            else
            {
                var chOpt = KotonHost.GetChordAt?.Invoke(blockStart);
                int pc = chOpt.HasValue ? chOpt.Value.Root : (ctx?.Tonic ?? 0);
                // Cherche l'octave le plus proche du C4 (60) : évite qu'un projet en E se retrouve
                // à jouer en E1 par défaut.
                startMidi = SnapToNearestOctave(60, pc);
            }

            int[] scale = ScaleFor(ctx?.Tonic ?? 0, ctx?.IsMajor ?? true);

            for (int v = 0; v < voiceCount; v++)
            {
                var spec = GetVoice(v);
                if (spec == null || spec.Points.Count == 0) continue;

                // distMax : point le plus extrême (|Y| max) — sert de dénominateur pour normaliser.
                // Si 0 (courbe plate à zéro), on utilise 1 pour éviter la div par 0 (target = 0 pour
                // tous les points de toutes façons puisque numérateur = 0).
                double distMax = 0;
                for (int i = 0; i < spec.Points.Count; i++)
                {
                    double a = Math.Abs(spec.Points[i].Y);
                    if (a > distMax) distMax = a;
                }
                if (distMax < 1e-9) distMax = 1.0;

                // Points de la spline triés par T croissant (le moteur exige l'ordre — l'éditeur
                // ne le garantit pas forcément après un drag arbitraire).
                var pts = new List<ControlPoint>(spec.Points);
                pts.Sort((a, b) => a.T.CompareTo(b.T));

                foreach (var (onset, tickLen) in EnumerateTicks(spec.Rhythm, duration))
                {
                    double t01 = onset / duration;
                    if (t01 < 0) t01 = 0; else if (t01 > 1) t01 = 1;
                    double yRaw = spline ? SampleCatmullRom(pts, t01) : SampleLinear(pts, t01);
                    int targetSemis = (int)Math.Round(yRaw / distMax * ambitus);
                    int targetMidi = Math.Max(0, Math.Min(127, startMidi + targetSemis));

                    // Snap : chord-tone sur pulsation entière (temps fort), scale-tone ailleurs.
                    // Tolérance de 1/32 de beat pour absorber les frappes très proches de la
                    // pulsation (une croche à 0.9999 doit compter comme un temps fort).
                    double frac = onset - Math.Floor(onset);
                    bool onBeat = frac < 1.0 / 32 || (1 - frac) < 1.0 / 32;

                    int noteMidi;
                    if (onBeat)
                    {
                        KotonChord? chOpt = KotonHost.GetChordAt?.Invoke(blockStart + onset);
                        KotonChord ch = chOpt.HasValue ? chOpt.Value
                            : new KotonChord { Root = ctx?.Tonic ?? 0, Quality = (ctx?.IsMajor ?? true) ? KotonChordQuality.Major : KotonChordQuality.Minor };
                        noteMidi = NearestChordTone(ch, targetMidi);
                    }
                    else
                    {
                        noteMidi = NearestScaleTone(scale, targetMidi);
                    }

                    double dur = tickLen * gate;
                    yield return new KotonGeneratedNote
                    {
                        StartBeat = onset,
                        DurationBeats = dur,
                        NotationDurationBeats = tickLen,
                        MidiNote = noteMidi,
                        Velocity = velocity,
                    };
                }
            }
        }

        // -----------------------------------------------------------------------------------------
        // Sampling
        // -----------------------------------------------------------------------------------------

        /// <summary>Interpolation linéaire entre les points de contrôle à t01 ∈ [0,1]. Extrapole
        /// avec la valeur du premier/dernier point hors de [T_min, T_max].</summary>
        static double SampleLinear(List<ControlPoint> pts, double t)
        {
            int n = pts.Count;
            if (n == 0) return 0;
            if (n == 1) return pts[0].Y;
            if (t <= pts[0].T) return pts[0].Y;
            if (t >= pts[n - 1].T) return pts[n - 1].Y;
            for (int i = 0; i < n - 1; i++)
            {
                var a = pts[i]; var b = pts[i + 1];
                if (t >= a.T && t <= b.T)
                {
                    double dt = b.T - a.T;
                    if (dt < 1e-9) return b.Y;
                    double u = (t - a.T) / dt;
                    return a.Y + u * (b.Y - a.Y);
                }
            }
            return pts[n - 1].Y;
        }

        /// <summary>Catmull-Rom uniforme (tension 0.5) — courbe C1 continue passant par TOUS les
        /// points de contrôle. Aux extrémités on duplique le premier/dernier point pour éviter les
        /// overshoots (formule classique).</summary>
        static double SampleCatmullRom(List<ControlPoint> pts, double t)
        {
            int n = pts.Count;
            if (n == 0) return 0;
            if (n == 1) return pts[0].Y;
            if (t <= pts[0].T) return pts[0].Y;
            if (t >= pts[n - 1].T) return pts[n - 1].Y;

            // Trouve le segment [i, i+1] qui contient t.
            int i = 0;
            for (int k = 0; k < n - 1; k++)
                if (t >= pts[k].T && t <= pts[k + 1].T) { i = k; break; }

            var p0 = i > 0 ? pts[i - 1] : pts[i];
            var p1 = pts[i];
            var p2 = pts[i + 1];
            var p3 = i + 2 < n ? pts[i + 2] : pts[i + 1];

            double dt = p2.T - p1.T;
            if (dt < 1e-9) return p1.Y;
            double u = (t - p1.T) / dt;

            // Formule Catmull-Rom classique (u ∈ [0,1], tension implicite 0.5).
            double u2 = u * u;
            double u3 = u2 * u;
            double y = 0.5 * (
                (2 * p1.Y) +
                (-p0.Y + p2.Y) * u +
                (2 * p0.Y - 5 * p1.Y + 4 * p2.Y - p3.Y) * u2 +
                (-p0.Y + 3 * p1.Y - 3 * p2.Y + p3.Y) * u3
            );
            return y;
        }

        // -----------------------------------------------------------------------------------------
        // Rythme (loop du KotonRhythm sur la durée du bloc — même logique que l'arpégiateur)
        // -----------------------------------------------------------------------------------------

        static IEnumerable<(double onset, double tickLen)> EnumerateTicks(KotonRhythm rhythm, double duration)
        {
            if (rhythm == null || rhythm.TotalSlices <= 0 || rhythm.SlicesPerBeat <= 0
                || rhythm.StartSlices == null || rhythm.LenSlices == null || rhythm.StartSlices.Length == 0)
                yield break;

            double motifBeats = (double)rhythm.TotalSlices / rhythm.SlicesPerBeat;
            if (motifBeats <= 0) yield break;
            double motifStart = 0;
            while (motifStart < duration - 1e-9)
            {
                for (int i = 0; i < rhythm.StartSlices.Length; i++)
                {
                    double onset = motifStart + (double)rhythm.StartSlices[i] / rhythm.SlicesPerBeat;
                    if (onset >= duration - 1e-9) break;
                    double len = Math.Max(0.01, (double)rhythm.LenSlices[i] / rhythm.SlicesPerBeat);
                    if (onset + len > duration) len = duration - onset;
                    if (len > 0) yield return (onset, len);
                }
                motifStart += motifBeats;
            }
        }

        // -----------------------------------------------------------------------------------------
        // Snap harmonique
        // -----------------------------------------------------------------------------------------

        static int[] ScaleFor(int tonicPc, bool isMajor)
        {
            // Majeur : W-W-H-W-W-W-H (0,2,4,5,7,9,11) ; mineur naturel : W-H-W-W-H-W-W (0,2,3,5,7,8,10).
            int[] pattern = isMajor ? new[] { 0, 2, 4, 5, 7, 9, 11 } : new[] { 0, 2, 3, 5, 7, 8, 10 };
            var pcs = new int[pattern.Length];
            for (int i = 0; i < pattern.Length; i++) pcs[i] = ((pattern[i] + tonicPc) % 12 + 12) % 12;
            return pcs;
        }

        static int NearestScaleTone(int[] scalePcs, int targetMidi)
        {
            // Cherche parmi les notes de la gamme dans [target-6, target+6] la plus proche du target.
            int bestMidi = targetMidi;
            int bestDist = int.MaxValue;
            for (int delta = -6; delta <= 6; delta++)
            {
                int m = targetMidi + delta;
                if (m < 0 || m > 127) continue;
                int pc = ((m % 12) + 12) % 12;
                for (int i = 0; i < scalePcs.Length; i++)
                {
                    if (scalePcs[i] == pc)
                    {
                        int d = Math.Abs(delta);
                        if (d < bestDist) { bestDist = d; bestMidi = m; }
                        break;
                    }
                }
            }
            return bestMidi;
        }

        static int NearestChordTone(KotonChord chord, int targetMidi)
        {
            // Notes de l'accord (pitch classes) — GetMidiNotes(0) donne les intervalles.
            var ivals = chord.GetMidiNotes(0);
            var pcs = new HashSet<int>();
            for (int i = 0; i < ivals.Length; i++) pcs.Add(((ivals[i] + chord.Root) % 12 + 12) % 12);
            int bestMidi = targetMidi;
            int bestDist = int.MaxValue;
            for (int delta = -6; delta <= 6; delta++)
            {
                int m = targetMidi + delta;
                if (m < 0 || m > 127) continue;
                int pc = ((m % 12) + 12) % 12;
                if (pcs.Contains(pc))
                {
                    int d = Math.Abs(delta);
                    if (d < bestDist) { bestDist = d; bestMidi = m; }
                }
            }
            return bestMidi;
        }

        static int SnapToNearestOctave(int baseMidi, int targetPc)
        {
            int basePc = ((baseMidi % 12) + 12) % 12;
            int delta = ((targetPc - basePc) + 12) % 12;
            if (delta >= 7) delta -= 12;
            return Math.Max(0, Math.Min(127, baseMidi + delta));
        }

        static double GateFor(int articulation)
        {
            switch (articulation)
            {
                case 0: return 1.0;
                case 1: return 0.75;
                case 2: return 0.40;
                case 3: return 0.15;
                default: return 0.75;
            }
        }

        // -----------------------------------------------------------------------------------------
        // Persistance
        // -----------------------------------------------------------------------------------------

        const int SaveFormatVersion = 1;

        public byte[] SaveState()
        {
            try
            {
                var doc = new Dictionary<string, object>();
                doc["v"] = SaveFormatVersion;
                doc["duration"] = _durationBeatsCache;
                var pd = new Dictionary<string, double>();
                foreach (var kp in _params) pd[kp.Id] = kp.Value;
                doc["params"] = pd;

                var voicesArr = new List<Dictionary<string, object>>();
                for (int v = 0; v < MaxVoices; v++)
                {
                    if (_voices[v] == null) continue;
                    var spec = _voices[v];
                    var pts = new List<double[]>();
                    foreach (var p in spec.Points) pts.Add(new[] { p.T, p.Y });
                    voicesArr.Add(new Dictionary<string, object>
                    {
                        ["i"] = v,
                        ["pts"] = pts,
                        ["r"] = new Dictionary<string, object>
                        {
                            ["beats"] = spec.Rhythm.Beats,
                            ["spb"] = spec.Rhythm.SlicesPerBeat,
                            ["starts"] = spec.Rhythm.StartSlices ?? Array.Empty<int>(),
                            ["lens"] = spec.Rhythm.LenSlices ?? Array.Empty<int>(),
                        },
                        ["col"] = new[] { (int)spec.Color.R, (int)spec.Color.G, (int)spec.Color.B },
                    });
                }
                doc["voices"] = voicesArr;
                return System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(doc));
            }
            catch { return Array.Empty<byte>(); }
        }

        public void LoadState(byte[] state)
        {
            if (state == null || state.Length == 0) return;
            try
            {
                using var doc = JsonDocument.Parse(state);
                var root = doc.RootElement;
                if (root.TryGetProperty("duration", out var d) && d.TryGetDouble(out double dur))
                    _durationBeatsCache = dur < 0.25 ? 0.25 : dur;
                if (root.TryGetProperty("params", out var pEl) && pEl.ValueKind == JsonValueKind.Object)
                {
                    foreach (var kp in pEl.EnumerateObject())
                    {
                        if (!kp.Value.TryGetDouble(out double v)) continue;
                        for (int i = 0; i < _params.Count; i++)
                            if (string.Equals(_params[i].Id, kp.Name, StringComparison.Ordinal)) { _params[i].Value = v; break; }
                    }
                }
                if (root.TryGetProperty("voices", out var vArr) && vArr.ValueKind == JsonValueKind.Array)
                {
                    for (int i = 0; i < MaxVoices; i++) _voices[i] = null;
                    foreach (var vEl in vArr.EnumerateArray())
                    {
                        int idx = 0;
                        if (vEl.TryGetProperty("i", out var iEl) && iEl.TryGetInt32(out int ii)) idx = ii;
                        if (idx < 0 || idx >= MaxVoices) continue;
                        var spec = new VoiceSpec { Color = DefaultColorFor(idx) };
                        if (vEl.TryGetProperty("pts", out var ptsEl) && ptsEl.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var pe in ptsEl.EnumerateArray())
                            {
                                if (pe.ValueKind != JsonValueKind.Array) continue;
                                var arr = pe.EnumerateArray();
                                if (!arr.MoveNext() || !arr.Current.TryGetDouble(out double t)) continue;
                                if (!arr.MoveNext() || !arr.Current.TryGetDouble(out double y)) continue;
                                spec.Points.Add(new ControlPoint(t, y));
                            }
                        }
                        if (vEl.TryGetProperty("r", out var rEl) && rEl.ValueKind == JsonValueKind.Object)
                        {
                            var nr = new KotonRhythm();
                            if (rEl.TryGetProperty("beats", out var bEl) && bEl.TryGetInt32(out int b)) nr.Beats = Math.Max(1, b);
                            if (rEl.TryGetProperty("spb", out var spbEl) && spbEl.TryGetInt32(out int sp)) nr.SlicesPerBeat = Math.Max(1, sp);
                            if (rEl.TryGetProperty("starts", out var stEl) && stEl.ValueKind == JsonValueKind.Array)
                            {
                                var starts = new List<int>();
                                foreach (var el in stEl.EnumerateArray()) if (el.TryGetInt32(out int x)) starts.Add(x);
                                nr.StartSlices = starts.ToArray();
                            }
                            if (rEl.TryGetProperty("lens", out var lnEl) && lnEl.ValueKind == JsonValueKind.Array)
                            {
                                var lens = new List<int>();
                                foreach (var el in lnEl.EnumerateArray()) if (el.TryGetInt32(out int x)) lens.Add(Math.Max(1, x));
                                nr.LenSlices = lens.ToArray();
                            }
                            spec.Rhythm = nr;
                        }
                        if (vEl.TryGetProperty("col", out var cEl) && cEl.ValueKind == JsonValueKind.Array)
                        {
                            var col = cEl.EnumerateArray();
                            byte r = 0, g = 0, bC = 0;
                            if (col.MoveNext() && col.Current.TryGetInt32(out int rr)) r = (byte)Math.Max(0, Math.Min(255, rr));
                            if (col.MoveNext() && col.Current.TryGetInt32(out int gg)) g = (byte)Math.Max(0, Math.Min(255, gg));
                            if (col.MoveNext() && col.Current.TryGetInt32(out int bb)) bC = (byte)Math.Max(0, Math.Min(255, bb));
                            spec.Color = Color.FromRgb(r, g, bC);
                        }
                        _voices[idx] = spec;
                    }
                    // Assure au moins la voix 0.
                    if (_voices[0] == null) GetVoice(0);
                }
            }
            catch { }
        }
    }
}
