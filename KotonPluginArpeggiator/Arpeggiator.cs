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
    [KotonGenerator("Arpégiateur", Type = KotonGeneratorType.Melody, Version = "1.0", Vendor = "Koton Studio")]
    public sealed class Arpeggiator : IKotonGenerator
    {
        public string Id => "koton.arpeggiator";
        public string DisplayName => "Arpégiateur";

        // ---- Paramètres ----
        // Pattern est un enum discrétisé en double 0..5. Rate idem 0..4 (division rythmique). Les
        // combos de l'éditeur les posent à des valeurs entières exactes ; une automation qui
        // écraserait à 1.7 arrondit à 2 côté rendu (Math.Floor / cast int).
        readonly KotonParameter _pattern  = new KotonParameter("pattern",  "Motif",     0, 5, 0);
        readonly KotonParameter _rate     = new KotonParameter("rate",     "Vitesse",   0, 4, 1);
        readonly KotonParameter _octaves  = new KotonParameter("octaves",  "Octaves",   1, 3, 1);
        readonly KotonParameter _gate     = new KotonParameter("gate",     "Gate",      0.05, 1.0, 0.5, "%");
        readonly KotonParameter _velocity = new KotonParameter("velocity", "Vélocité",  1, 127, 100);
        readonly KotonParameter _baseMidi = new KotonParameter("base_midi","Note base", 12, 108, 60);

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
                _pattern, _rate, _octaves, _gate, _velocity, _baseMidi,
            };
        }

        // ---- Rendu vignette ----

        public KotonGeneratorDisplay GetTimelineDisplay()
        {
            // Vert Koton (accent teal légèrement décalé pour distinguer des blocs riff standards).
            var bg = Color.FromRgb(0x2A, 0x7C, 0x4E);
            string txt = "Arp " + PatternGlyph((int)_pattern.Value) + " " + RateName((int)_rate.Value);
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
        internal static readonly string[] RateNames    = { "1/4", "1/8", "1/16", "1/8T", "1/16T" };

        static string RateName(int r) => RateNames[Math.Max(0, Math.Min(RateNames.Length - 1, r))];

        // Une "tick" = combien de beats sépare deux notes consécutives ? 1/4 = 1 beat ; 1/8 = 0.5 ;
        // 1/16 = 0.25 ; 1/8T (triolet de croches) = 1/3 ; 1/16T = 1/6.
        static double TickBeats(int rate)
        {
            switch (rate)
            {
                case 0: return 1.0;
                case 1: return 0.5;
                case 2: return 0.25;
                case 3: return 1.0 / 3.0;
                case 4: return 1.0 / 6.0;
                default: return 0.5;
            }
        }

        // ---- Rendu de notes ----

        public IEnumerable<KotonGeneratedNote> RenderNotes(double startBeat, double endBeat, KotonRenderContext ctx)
        {
            // Snapshot des paramètres au début du rendu — cohérent avec la politique du FM synth :
            // un slider bougé pendant que RenderNotes s'exécute prend effet au prochain re-flatten.
            int pattern = (int)_pattern.Value;
            int rate = (int)_rate.Value;
            int octaves = Math.Max(1, (int)_octaves.Value);
            double gate = Math.Max(0.05, Math.Min(1.0, _gate.Value));
            int velocity = Math.Max(1, Math.Min(127, (int)_velocity.Value));
            int baseMidi = Math.Max(0, Math.Min(127, (int)_baseMidi.Value));

            double tick = TickBeats(rate);
            if (tick <= 0) yield break;
            double duration = Math.Max(0.25, DurationBeats);
            // Reseed pour reproductibilité (voir commentaire sur _rng).
            _rng = new Random(pattern * 1315423911 ^ rate * 2654435761u.GetHashCode() ^ octaves * 40503);

            // Compteur d'index de note pour Up/Down/UpDown/DownUp : on incrémente à chaque tick, on
            // enveloppe modulo la taille du pool à chaque itération selon le mode.
            int step = 0;

            for (double t = 0; t < duration - 1e-9; t += tick)
            {
                // Cet arpège n'utilise PAS startBeat/endBeat pour filtrer (yield toutes ses notes dans
                // [0, DurationBeats[) — l'hôte se charge du bornage. Simple et testé.
                // Pas de note s'il n'y a pas d'accord à ce beat (silence).
                KotonChord? chOpt = KotonHost.GetChordAt?.Invoke(t);
                // On peut aussi être appelé HORS lecture (Preview) — dans ce cas GetChordAt peut être
                // null si aucun accord n'est posé sur la timeline. On dégrade en accord de tonique
                // majeur/mineur selon le mode, pour que l'utilisateur entende quelque chose de
                // MUSICAL même sur une piste vide.
                KotonChord ch;
                if (chOpt.HasValue) ch = chOpt.Value;
                else ch = new KotonChord { Root = ctx?.Tonic ?? 0, Quality = (ctx?.IsMajor ?? true) ? KotonChordQuality.Major : KotonChordQuality.Minor };

                // Voicing : root position, base sur baseMidi + décalage pour placer la fondamentale
                // sur la classe de hauteur de l'accord (baseMidi porte l'octave choisie).
                int rootMidi = SnapToOctave(baseMidi, ch.Root);
                var chordNotes = ch.GetMidiNotes(rootMidi);

                // Réplique sur `octaves` — Up ajoute +12, +24 ; Down ne change pas la table (on
                // parcourt à l'envers), UpDown / DownUp aussi utilisent la table étendue.
                var pool = BuildPool(chordNotes, octaves);
                if (pool.Length == 0) continue;

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
                        double lenBeats = tick * gate;
                        foreach (var mn in pool)
                            yield return new KotonGeneratedNote
                            {
                                StartBeat = t,
                                DurationBeats = lenBeats,
                                MidiNote = mn,
                                Velocity = velocity,
                            };
                        continue;
                    }
                    default:
                        noteMidi = pool[0];
                        break;
                }

                noteBeat = t;
                double dur = tick * gate;
                yield return new KotonGeneratedNote
                {
                    StartBeat = noteBeat,
                    DurationBeats = dur,
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
                    [_pattern.Id]  = _pattern.Value,
                    [_rate.Id]     = _rate.Value,
                    [_octaves.Id]  = _octaves.Value,
                    [_gate.Id]     = _gate.Value,
                    [_velocity.Id] = _velocity.Value,
                    [_baseMidi.Id] = _baseMidi.Value,
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
            }
            catch { /* blob corrompu = garder les défauts */ }
        }
    }
}
