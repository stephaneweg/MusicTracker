using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Windows.Controls;
using System.Windows.Media;
using KotonStudio.Library;

namespace KotonPluginArpeggiator
{
    /// <summary>
    /// Arpégiateur — plugin de RÉFÉRENCE pour le framework IKotonGenerator. Objectif : montrer bout
    /// à bout ce qu'un générateur natif doit fournir (paramètres, harmonie-conscience via KotonHost,
    /// éditeur WPF avec Preview, sauvegarde d'état) sans dépendre du reste de l'app.
    ///
    /// **Fonctionnement** : à chaque tick de la grille (défini par <see cref="_rate"/>), lit l'accord
    /// courant via <see cref="KotonHost.GetChordAt"/>, en extrait les notes MIDI en voicing serré
    /// (via <see cref="KotonChordExtensions.GetMidiNotes"/>) éventuellement dupliquées sur plusieurs
    /// octaves, puis choisit la note à jouer selon le motif :
    /// - Up      : parcourt les notes en ordre montant, boucle
    /// - Down    : parcourt en ordre descendant, boucle
    /// - UpDown  : ping-pong montant/descendant (extrêmes joués UNE fois)
    /// - DownUp  : ping-pong descendant/montant
    /// - Random  : note tirée au hasard à chaque tick (Random déterministe seedé au reset)
    /// - Chord   : toutes les notes en même temps (accord plaqué au tempo choisi)
    ///
    /// **Live-conscience** : les paramètres sont lus À CHAQUE RenderNotes. Bouger un slider pendant
    /// que la lecture tourne = le prochain flatten audio reflète le changement (~200 ms typique
    /// via le LookaheadBuffer).
    ///
    /// **Fallback sans accord** : si <see cref="KotonHost.GetChordAt"/> renvoie null à un tick
    /// (silence dans la piste d'accords), l'arpégiateur SAUTE ce tick (silence). Ce comportement
    /// est le plus prévisible — un utilisateur qui pose un arpège dans un trou d'accords entend un
    /// vide, ce qui l'incite à combler côté harmonie.
    /// </summary>
    [KotonGenerator("Arpégiateur", Id = "koton.arpeggiator", Type = KotonGeneratorType.Melody, Version = "1.0", Vendor = "Koton Studio")]
    public sealed class Arpeggiator : IKotonGenerator
    {
        public string Id => "koton.arpeggiator";
        public string DisplayName => "Arpégiateur";

        // ---- Paramètres ----
        // Pattern est un enum discrétisé en double 0..5. Rate idem 0..4 (division rythmique). Les
        // combos de l'éditeur les posent à des valeurs entières exactes ; une automation qui
        // écraserait à 1.7 arrondit à 2 côté rendu (Math.Floor / cast int).
        readonly KotonParameter _pattern      = new KotonParameter("pattern",       "Motif",        0, 5, 0);
        // Notes par temps. En BINAIRE (4/4, 3/4, 2/4) : 1=noire, 2=croches, 3=triolets, 4=doubles.
        // En TERNAIRE (6/8, 9/8, 12/8) : 1=noire pointée, 3=croches, 6=doubles. Le tick effectif est
        // dérivé de cette valeur ET de la métrique (voir TickBeats). Défaut 2 = croches en binaire.
        readonly KotonParameter _notesPerBeat = new KotonParameter("notes_per_beat","Notes/temps",  1, 8, 2);
        // Prolongation : combien de fois on répète le voicing sur les octaves suivantes.
        //   0 = do mi sol           (juste l'octave de base)
        //   1 = do mi sol do2 mi2 sol2                  (2 octaves)
        //   2 = do mi sol do2 mi2 sol2 do3 mi3 sol3     (3 octaves) — l'exemple du brief
        //   3 = 4 octaves ; 4 = 5 octaves (arpège très large)
        readonly KotonParameter _extend       = new KotonParameter("extend",        "Prolonger",    0, 4, 0);
        // Articulation : 0=Légato (notes tenues, gate 100%), 1=Normal (gate 75%), 2=Détaché (gate 40%),
        // 3=Staccato (gate 15%). Remplace l'ancien slider Gate en % — choix musical direct.
        readonly KotonParameter _articulation = new KotonParameter("articulation",  "Articulation", 0, 3, 1);
        readonly KotonParameter _velocity     = new KotonParameter("velocity",      "Vélocité",     1, 127, 100);
        // Octave 0..8 (0 = grave, 4 = do central C4 = MIDI 60, 8 = très aigu C8). La note MIDI de
        // base est dérivée : 12 + octave*12. Plus lisible qu'un MIDI brut pour un utilisateur musical.
        readonly KotonParameter _octave       = new KotonParameter("octave",        "Octave",       0, 8, 4);
        // Voice leading : 0 = off (chaque tick repart de la position dans le pool selon le motif),
        // 1 = on (quand l'accord change, la 1re note du nouvel accord choisit celle du pool la plus
        // proche de la note précédente en semitons — évite les sauts brusques quand la progression
        // d'accords change).
        readonly KotonParameter _voiceLeading = new KotonParameter("voice_leading", "Voice leading",0, 1, 0);
        // Ouverture (spread) 0..3 : espacement des notes du voicing.
        //  0 = close (root, 3, 5 dans une octave)
        //  1 = drop-2 (la 2e note depuis le haut monte d'une octave)
        //  2 = spread modéré (2 notes montées d'une octave)
        //  3 = wide (chaque note écartée d'une octave — root, 3+12, 5+24)
        readonly KotonParameter _spread       = new KotonParameter("spread",        "Ouverture",    0, 3, 0);
        // Mode rythme : 0 = régulier (subdivision uniforme selon _notesPerBeat), 1 = personnalisé
        // (motif défini par l'utilisateur via RhythmGridEditor, stocké dans _customRhythm).
        readonly KotonParameter _rhythmMode   = new KotonParameter("rhythm_mode",   "Mode rythme",  0, 1, 0);

        // Motif rythmique personnalisé — utilisé UNIQUEMENT quand _rhythmMode.Value >= 0.5. Le motif
        // se loop dans la durée du bloc si sa longueur (Beats × SlicesPerBeat) est < DurationBeats.
        KotonRhythm _customRhythm = new KotonRhythm { Beats = 2, SlicesPerBeat = 4 };
        internal KotonRhythm CustomRhythm
        {
            get => _customRhythm;
            set { _customRhythm = value ?? new KotonRhythm { Beats = 2, SlicesPerBeat = 4 }; }
        }

        readonly List<KotonParameter> _params;
        public IReadOnlyList<KotonParameter> Parameters => _params;

        // Durée du bloc — modifiable par le user via la timeline OU l'éditeur. Persistée dans le blob.
        double _durationBeats = 4.0;
        public double DurationBeats
        {
            get => _durationBeats;
            set { _durationBeats = value < 0.25 ? 0.25 : value; }
        }

        public KotonGeneratorType GeneratorType => KotonGeneratorType.Melody;

        // Random déterministe : reseedé à Reset et à chaque RenderNotes pour que l'aperçu et
        // l'exécution donnent la MÊME séquence tant qu'aucun paramètre ne change. Sinon une double
        // écoute de la même Preview donnerait deux résultats différents — perturbant.
        Random _rng = new Random(1);

        public Arpeggiator()
        {
            _params = new List<KotonParameter>
            {
                _pattern, _notesPerBeat, _extend, _articulation, _velocity, _octave, _voiceLeading, _spread, _rhythmMode,
            };
        }

        // ---- Rendu vignette ----

        public KotonGeneratorDisplay GetTimelineDisplay()
        {
            // Vert Koton (accent teal légèrement décalé pour distinguer des blocs riff standards).
            var bg = Color.FromRgb(0x2A, 0x7C, 0x4E);
            int nb = (int)_notesPerBeat.Value;
            string txt = "Arp " + PatternGlyph((int)_pattern.Value) + " " + nb + "/tps";
            return new KotonGeneratorDisplay { Background = bg, Text = txt };
        }

        static string PatternGlyph(int p)
        {
            switch (p)
            {
                case 0: return "↑";
                case 1: return "↓";
                case 2: return "↕";
                case 3: return "↕";
                case 4: return "?";
                case 5: return "▦";
                default: return "";
            }
        }

        internal static readonly string[] PatternNames = { "Up ↑", "Down ↓", "Up-Down ↕", "Down-Up ↕", "Aléatoire", "Accord plaqué" };
        internal static readonly string[] ArticulationNames = { "Legato", "Normal", "Détaché", "Staccato" };

        // Options "notes par temps" affichées dans l'éditeur — dépendent de la métrique du projet.
        // Binaire (4/4, 3/4, 2/4) : 1 (noire), 2 (croches), 3 (triolets), 4 (doubles).
        // Ternaire (6/8, 9/8, 12/8) : 1 (noire pointée), 3 (croches), 6 (doubles).
        internal static readonly int[] BinaryNotesPerBeat  = { 1, 2, 3, 4 };
        internal static readonly int[] TernaryNotesPerBeat = { 1, 3, 6 };

        /// <summary>Vrai si la signature temporelle est ternaire (compound) : 6/8, 9/8, 12/8...
        /// Détection = dénominateur 8 avec un numérateur multiple de 3.</summary>
        internal static bool IsTernary(int timeSigNum, int timeSigDen)
        {
            return timeSigDen == 8 && timeSigNum > 0 && timeSigNum % 3 == 0;
        }

        // Gate ratio par articulation.
        static double GateFor(int articulation)
        {
            switch (articulation)
            {
                case 0: return 1.0;   // Legato
                case 1: return 0.75;  // Normal
                case 2: return 0.40;  // Détaché
                case 3: return 0.15;  // Staccato
                default: return 0.75;
            }
        }

        // Une "tick" = combien de beats sépare deux notes consécutives ?
        // Binaire : tick = 1 / notesPerBeat (temps = noire, 1 beat).
        // Ternaire : tick = 1.5 / notesPerBeat (temps = noire pointée, 1.5 beats).
        static double TickBeats(int notesPerBeat, int timeSigNum, int timeSigDen)
        {
            if (notesPerBeat < 1) notesPerBeat = 1;
            double beatsPerBeat = IsTernary(timeSigNum, timeSigDen) ? 1.5 : 1.0;
            return beatsPerBeat / notesPerBeat;
        }

        /// <summary>Génère la séquence de ticks (position en beats + durée logique en beats) selon le
        /// mode courant. Mode régulier : subdivision uniforme (position 0, tick, 2·tick, ...). Mode
        /// personnalisé : itère sur les notes du <see cref="KotonRhythm"/> en le loopant dans la durée
        /// du bloc si son motif est plus court. Yield break si les params sont dégénérés.</summary>
        static IEnumerable<(double onset, double tickLen)> EnumerateTicks(bool customMode, double regularTick, double duration, KotonRhythm custom)
        {
            if (customMode && custom != null && custom.TotalSlices > 0 && custom.SlicesPerBeat > 0
                && custom.StartSlices != null && custom.LenSlices != null && custom.StartSlices.Length > 0)
            {
                double motifBeats = (double)custom.TotalSlices / custom.SlicesPerBeat;
                if (motifBeats <= 0) yield break;
                double motifStart = 0;
                while (motifStart < duration - 1e-9)
                {
                    for (int i = 0; i < custom.StartSlices.Length; i++)
                    {
                        double onset = motifStart + (double)custom.StartSlices[i] / custom.SlicesPerBeat;
                        if (onset >= duration - 1e-9) break;
                        double len = Math.Max(0.01, (double)custom.LenSlices[i] / custom.SlicesPerBeat);
                        // Tronque à la fin du bloc — évite une note qui déborde.
                        if (onset + len > duration) len = duration - onset;
                        if (len > 0) yield return (onset, len);
                    }
                    motifStart += motifBeats;
                }
            }
            else
            {
                for (double t = 0; t < duration - 1e-9; t += regularTick)
                    yield return (t, regularTick);
            }
        }

        // ---- Rendu de notes ----

        public IEnumerable<KotonGeneratedNote> RenderNotes(double startBeat, double endBeat, KotonRenderContext ctx)
        {
            // Snapshot des paramètres au début du rendu — cohérent avec la politique du FM synth :
            // un slider bougé pendant que RenderNotes s'exécute prend effet au prochain re-flatten.
            int pattern = (int)_pattern.Value;
            int notesPerBeat = Math.Max(1, (int)_notesPerBeat.Value);
            // extend 0..4 → octaves rendues = 1 + extend (1..5). BuildPool réplique le voicing.
            int octaves = 1 + Math.Max(0, Math.Min(4, (int)_extend.Value));
            int articulation = Math.Max(0, Math.Min(3, (int)_articulation.Value));
            double gate = GateFor(articulation);
            int velocity = Math.Max(1, Math.Min(127, (int)_velocity.Value));
            int octave = Math.Max(0, Math.Min(8, (int)_octave.Value));
            int baseMidi = 12 + octave * 12;  // C0=12, C4=60, C8=108 (convention MIDI standard)
            bool voiceLeading = _voiceLeading.Value >= 0.5;
            int spread = Math.Max(0, Math.Min(3, (int)_spread.Value));

            int tsNum = ctx?.TimeSigNum > 0 ? ctx.TimeSigNum : 4;
            int tsDen = ctx?.TimeSigDen > 0 ? ctx.TimeSigDen : 4;
            double tick = TickBeats(notesPerBeat, tsNum, tsDen);
            if (tick <= 0) yield break;
            double duration = Math.Max(0.25, DurationBeats);
            bool customMode = _rhythmMode.Value >= 0.5;
            // Position ABSOLUE du bloc dans le projet — indispensable pour interroger l'accord courant
            // via KotonHost.GetChordAt (le résolveur cherche dans la piste Accords à un beat absolu,
            // pas relatif). ctx.BlockStartBeat = 0 en preview (pas encore posé) : fallback tonique.
            double blockStart = ctx?.BlockStartBeat ?? 0.0;
            // Reseed pour reproductibilité (voir commentaire sur _rng).
            _rng = new Random(pattern * 1315423911 ^ notesPerBeat * 2654435761u.GetHashCode() ^ octaves * 40503);

            // Compteur d'index de note pour Up/Down/UpDown/DownUp : on incrémente à chaque tick, on
            // enveloppe modulo la taille du pool à chaque itération selon le mode.
            int step = 0;
            // Voice leading : mémorise la dernière note jouée + l'accord précédent pour détecter le
            // changement et re-caler `step` sur la note du nouveau pool la plus proche.
            int? lastPlayedMidi = null;
            KotonChord? lastChord = null;

            foreach (var (t, tickLen) in EnumerateTicks(customMode, tick, duration, _customRhythm))
            {
                // Interrogation harmonique au beat ABSOLU (position du bloc + offset relatif). Sans le
                // décalage BlockStartBeat, on cherche toujours au début du projet → l'arp jouerait la
                // même chose (tonique) quel que soit l'accord réel sous le bloc.
                KotonChord? chOpt = KotonHost.GetChordAt?.Invoke(blockStart + t);
                // Fallback : preview (bloc non posé) OU trou dans la piste d'accords. On dégrade en
                // accord de tonique majeur/mineur selon le mode du projet, pour que l'utilisateur
                // entende quelque chose de MUSICAL même sans harmonie posée.
                KotonChord ch;
                if (chOpt.HasValue) ch = chOpt.Value;
                else ch = new KotonChord { Root = ctx?.Tonic ?? 0, Quality = (ctx?.IsMajor ?? true) ? KotonChordQuality.Major : KotonChordQuality.Minor };

                // Voicing : root position, base sur baseMidi + décalage pour placer la fondamentale
                // sur la classe de hauteur de l'accord (baseMidi porte l'octave choisie). L'ouverture
                // (spread) est appliquée avant la réplication d'octaves — les notes du voicing sont
                // déjà réparties sur plusieurs octaves selon le mode.
                int rootMidi = SnapToOctave(baseMidi, ch.Root);
                var chordNotes = ch.ApplyVoicing(rootMidi, spread);

                // Réplique sur `octaves` — Up ajoute +12, +24 ; Down ne change pas la table (on
                // parcourt à l'envers), UpDown / DownUp aussi utilisent la table étendue.
                var pool = BuildPool(chordNotes, octaves);
                if (pool.Length == 0) continue;

                // Voice leading : si l'accord change et qu'on a une note précédente, on ré-aligne
                // `step` pour que la prochaine note choisie soit celle du nouveau pool la plus proche.
                // Marche pour Up/Down/UpDown/DownUp et pour le mode Random (qui re-tire quand même,
                // mais depuis un point de départ plus musical). Pas pour "Accord plaqué" (toutes les
                // notes sortent en parallèle, pas de séquence).
                bool chordChanged = lastChord.HasValue &&
                                    (lastChord.Value.Root != ch.Root || lastChord.Value.Quality != ch.Quality);
                if (voiceLeading && lastPlayedMidi.HasValue && chordChanged && pattern != 5)
                {
                    step = KotonChordExtensions.NearestNoteIndex(pool, lastPlayedMidi.Value);
                }
                lastChord = ch;

                double noteBeat;
                int noteMidi;
                switch (pattern)
                {
                    case 0: // Up
                        noteMidi = pool[step % pool.Length];
                        step++;
                        break;
                    case 1: // Down
                        noteMidi = pool[(pool.Length - 1 - (step % pool.Length))];
                        step++;
                        break;
                    case 2: // UpDown : ping-pong ; le sommet et le fond joués UNE fois (2*(N-1) période)
                    case 3:
                    {
                        int period = Math.Max(1, 2 * (pool.Length - 1));
                        int phase = ((step % period) + period) % period;
                        int idx = phase < pool.Length ? phase : (period - phase);
                        if (pattern == 3) idx = pool.Length - 1 - idx;   // DownUp = inversion de UpDown
                        noteMidi = pool[Math.Max(0, Math.Min(pool.Length - 1, idx))];
                        step++;
                        break;
                    }
                    case 4: // Random
                        noteMidi = pool[_rng.Next(pool.Length)];
                        step++;
                        break;
                    case 5: // Chord — sortir toutes les notes en même temps et passer au prochain tick
                    {
                        double lenBeats = tickLen * gate;
                        foreach (var mn in pool)
                            yield return new KotonGeneratedNote
                            {
                                StartBeat = t,
                                DurationBeats = lenBeats,
                                // Notation : la valeur rythmique logique = tickLen complet, indépendante
                                // de l'articulation (le staccato joue court mais s'écrit "croche").
                                NotationDurationBeats = tickLen,
                                MidiNote = mn,
                                Velocity = velocity,
                            };
                        // On note la 1re note pour le voice leading du prochain tick (peu importe
                        // laquelle, l'important est d'avoir un point de référence).
                        lastPlayedMidi = pool.Length > 0 ? (int?)pool[0] : null;
                        continue;
                    }
                    default:
                        noteMidi = pool[0];
                        break;
                }

                lastPlayedMidi = noteMidi;
                noteBeat = t;
                double dur = tickLen * gate;
                yield return new KotonGeneratedNote
                {
                    StartBeat = noteBeat,
                    DurationBeats = dur,
                    // Notation : la valeur rythmique logique = tickLen complet, indépendante de
                    // l'articulation (le staccato joue court mais s'écrit "croche").
                    NotationDurationBeats = tickLen,
                    MidiNote = noteMidi,
                    Velocity = velocity,
                };
            }
        }

        // Ajuste `baseMidi` pour que sa pitch class soit `targetPc` — décale au demi-ton près, garde
        // le même registre approximatif.
        static int SnapToOctave(int baseMidi, int targetPc)
        {
            int basePc = ((baseMidi % 12) + 12) % 12;
            int delta = ((targetPc - basePc) + 12) % 12;
            // Prendre l'octave le plus proche : si delta >= 7, redescendre d'un octave (delta - 12)
            // pour ne pas trop monter.
            if (delta >= 7) delta -= 12;
            return Math.Max(0, Math.Min(127, baseMidi + delta));
        }

        // Réplique le voicing sur `octaves` — retourne un tableau trié croissant.
        static int[] BuildPool(int[] chordNotes, int octaves)
        {
            if (chordNotes == null || chordNotes.Length == 0) return Array.Empty<int>();
            octaves = Math.Max(1, Math.Min(3, octaves));
            var list = new List<int>(chordNotes.Length * octaves);
            for (int o = 0; o < octaves; o++)
                foreach (var n in chordNotes)
                {
                    int p = n + o * 12;
                    if (p >= 0 && p <= 127) list.Add(p);
                }
            list.Sort();
            return list.ToArray();
        }

        // ---- Cycle plugin ----

        public bool HasEditor => true;
        public UserControl CreateEditor() => new ArpeggiatorEditor(this);

        public void Dispose()
        {
            // Rien à libérer — pas de buffer, pas de handle natif. Le rng est managé.
        }

        // ---- Persistance ----

        const int SaveFormatVersion = 1;

        public byte[] SaveState()
        {
            var doc = new Dictionary<string, object>
            {
                ["v"] = SaveFormatVersion,
                ["duration"] = _durationBeats,
                ["params"] = new Dictionary<string, double>
                {
                    [_pattern.Id]       = _pattern.Value,
                    [_notesPerBeat.Id]  = _notesPerBeat.Value,
                    [_extend.Id]        = _extend.Value,
                    [_articulation.Id]  = _articulation.Value,
                    [_velocity.Id]      = _velocity.Value,
                    [_octave.Id]        = _octave.Value,
                    [_voiceLeading.Id]  = _voiceLeading.Value,
                    [_spread.Id]        = _spread.Value,
                    [_rhythmMode.Id]    = _rhythmMode.Value,
                },
                // Motif rythmique personnalisé — sérialisé en tableaux d'entiers plutôt que d'imbriquer
                // l'objet, pour rester lisible dans le JSON du projet.
                ["rhythm"] = new Dictionary<string, object>
                {
                    ["beats"] = _customRhythm.Beats,
                    ["spb"]   = _customRhythm.SlicesPerBeat,
                    ["starts"] = _customRhythm.StartSlices ?? Array.Empty<int>(),
                    ["lens"]   = _customRhythm.LenSlices ?? Array.Empty<int>(),
                },
            };
            return System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(doc));
        }

        public void LoadState(byte[] state)
        {
            if (state == null || state.Length == 0) return;
            try
            {
                using var doc = JsonDocument.Parse(state);
                var root = doc.RootElement;
                if (root.TryGetProperty("duration", out var d) && d.TryGetDouble(out double dur))
                    _durationBeats = dur < 0.25 ? 0.25 : dur;
                if (root.TryGetProperty("params", out var pEl) && pEl.ValueKind == JsonValueKind.Object)
                {
                    foreach (var kp in pEl.EnumerateObject())
                    {
                        if (!kp.Value.TryGetDouble(out double v)) continue;
                        for (int i = 0; i < _params.Count; i++)
                            if (string.Equals(_params[i].Id, kp.Name, StringComparison.Ordinal))
                            {
                                _params[i].Value = v;
                                break;
                            }
                    }
                }
                // Motif personnalisé : ignoré silencieusement s'il est absent (projet écrit par une
                // version antérieure à la feature rythme perso) → le rhythmMode reste à 0 (régulier).
                if (root.TryGetProperty("rhythm", out var rEl) && rEl.ValueKind == JsonValueKind.Object)
                {
                    var nr = new KotonRhythm();
                    if (rEl.TryGetProperty("beats", out var beatsEl) && beatsEl.TryGetInt32(out int b)) nr.Beats = Math.Max(1, b);
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
                    _customRhythm = nr;
                }
            }
            catch { /* blob corrompu = garder les défauts */ }
        }
    }
}
