using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Windows.Controls;
using KotonStudio.Library;

namespace KotonPluginGuqinConstrainer
{
    /// <summary>
    /// Constrainer Guqin — filtre chaque NoteOn incoming vers un fingering (corde × position)
    /// jouable sur un guqin réel : max N doigts stoppés dans un empan physique ≤ MaxSpanCm sur
    /// l'axe corde. À poser sur une piste instrument (n'importe laquelle — le DSP du son reste
    /// libre : peut être le KotonPluginGuqin réel, mais aussi un piano, une harpe, un synth,
    /// selon ce qu'on veut entendre avec des contraintes de guqin).
    ///
    /// **Filtrage temporel** : Filter() reçoit toutes les notes d'un bloc. On simule le déroulé
    /// événementiel (NoteOn + NoteOff en ordre chronologique) pour appliquer la règle de
    /// polyphonie correctement — c'est ce qui permet de « voler » un doigt encore tenu quand une
    /// nouvelle note arrive.
    ///
    /// **Événements viz** : NoteEmitted / NoteReleased sont émis pendant Filter() pour que
    /// l'éditeur (qui possède l'instance vivante du constrainer) puisse afficher les cordes qui
    /// vibrent en temps réel. Ces événements sont purement observationnels et ne modifient pas
    /// la sortie.
    /// </summary>
    [KotonGeneratorConstrainer("Guqin", Id = "koton.guqin_constrainer", Version = "1.0", Vendor = "Koton Studio")]
    public sealed class GuqinConstrainerPlugin : IKotonGeneratorConstrainer
    {
        public string Id => "koton.guqin_constrainer";
        public string DisplayName => "Guqin";

        // Params du modèle guqin.
        readonly KotonParameter _diapasonCm  = new KotonParameter("diapason_cm",  "Diapason",         50, 150, 110, "cm");
        readonly KotonParameter _spanCm      = new KotonParameter("span_cm",      "Empan main",       5, 30, 15, "cm");
        readonly KotonParameter _maxFingers  = new KotonParameter("max_fingers",  "Max doigts",       2, 5, 4);
        readonly KotonParameter _stringCount = new KotonParameter("string_count", "Nombre de cordes", 2, GuqinModel.MaxStrings, 7);
        // Snap : 0 = rejeter, 1 = snap au plus proche, 2 = transpose à l'octave (dans la
        // tessiture) puis snap. Le mode 2 est utile quand la source contient des notes trop
        // graves/aiguës pour le guqin (basse à 30, mélodie à 90) et qu'on veut quand même les
        // jouer, ramenées dans l'ambitus par octaves entières (préserve la classe de hauteur).
        readonly KotonParameter _snapMode    = new KotonParameter("snap_mode",    "Snap hors gamme",  0, 2, 2);
        // Glissando : 0 = off, 1 = on. Ajoute un pitch bend automatique entre 2 notes consécutives
        // qui se résolvent sur la MÊME corde stoppée (aucune corde à vide) — geste caractéristique
        // du guqin (yin/nao/chuo). Le bend monte/descend depuis la note source vers la note cible
        // pendant la durée de la note source, puis la note cible est attaquée normalement.
        readonly KotonParameter _glissando   = new KotonParameter("glissando",    "Glissando même corde", 0, 1, 1);
        // MIDI de chaque corde à vide, pré-alloués (MaxStrings entrées). Seules les _stringCount
        // premières sont utilisées ; les autres restent dans SaveState pour préserver l'état de
        // l'user si le nombre de cordes est diminué puis réaugmenté.
        readonly KotonParameter[] _stringMidi;

        readonly List<KotonParameter> _params;
        public IReadOnlyList<KotonParameter> Parameters => _params;

        public struct StruckEvent
        {
            public int StringIdx;
            public double Position;
            public int Midi;
            /// <summary>Beat RELATIF au début du bloc (info de contexte).</summary>
            public double StartBeat;
            public double DurationBeats;
            /// <summary>Beat ABSOLU sur la timeline (BlockStartBeat + StartBeat). C'est cette
            /// position qui permet à l'éditeur de scheduler l'animation à son moment réel de
            /// lecture, quelle que soit la position du bloc sur la piste — sinon l'anim de tous
            /// les blocs joue en même temps au flatten et rien ne suit les modules suivants.</summary>
            public double AbsoluteStartBeat;
            public double Tempo;
        }
        public struct ReleaseEvent
        {
            public int Midi;
            /// <summary>Beat ABSOLU sur la timeline (position de release).</summary>
            public double AbsoluteAtBeat;
            public double Tempo;
        }
        public event Action<StruckEvent> NoteStruck;
        public event Action<ReleaseEvent> NoteReleased;

        public GuqinConstrainerPlugin()
        {
            // Défauts : les 7 premiers = ZhengdiaoC (accordage standard le plus courant). Au-delà,
            // on prolonge par gamme pentatonique montante pour rester musicalement cohérent si
            // l'user pousse à 8+ cordes (koto/guzheng-style).
            int[] defaults = { 43, 45, 48, 50, 52, 55, 57, 60, 62, 64, 67, 69, 72, 74, 76 };
            _stringMidi = new KotonParameter[GuqinModel.MaxStrings];
            for (int i = 0; i < GuqinModel.MaxStrings; i++)
                _stringMidi[i] = new KotonParameter("string_midi_" + (i + 1), "Corde " + (i + 1), 0, 127, defaults[i]);

            _params = new List<KotonParameter> { _diapasonCm, _spanCm, _maxFingers, _stringCount, _snapMode, _glissando };
            _params.AddRange(_stringMidi);
        }

        public bool HasEditor => true;
        public UserControl CreateEditor() => new GuqinConstrainerEditor(this);
        public void Dispose() { }

        /// <summary>Compose le tuning courant depuis _stringCount + les N premiers _stringMidi.</summary>
        internal GuqinModel.Tuning ActiveTuning
        {
            get
            {
                int count = Math.Max(2, Math.Min(GuqinModel.MaxStrings, (int)_stringCount.Value));
                var midis = new int[count];
                for (int i = 0; i < count; i++)
                    midis[i] = Math.Max(0, Math.Min(127, (int)_stringMidi[i].Value));
                return new GuqinModel.Tuning("Custom", midis);
            }
        }

        /// <summary>Applique un preset historique aux N premières cordes + met StringCount à N.</summary>
        internal void ApplyPreset(GuqinModel.Tuning preset)
        {
            if (preset == null || preset.OpenMidi == null || preset.OpenMidi.Length == 0) return;
            int n = Math.Min(GuqinModel.MaxStrings, preset.OpenMidi.Length);
            _stringCount.Value = n;
            for (int i = 0; i < n; i++)
                _stringMidi[i].Value = preset.OpenMidi[i];
        }

        public IEnumerable<KotonGeneratedNote> Filter(IEnumerable<KotonGeneratedNote> notes, KotonRenderContext ctx)
        {
            if (notes == null) yield break;
            var tuning = ActiveTuning;
            int snapMode = (int)Math.Round(_snapMode.Value);
            bool snap = snapMode >= 1;
            bool octaveTranspose = snapMode >= 2;
            // Tessiture accessible du guqin : de la corde à vide la plus grave (min OpenMidi) au
            // fingering le plus aigu de la corde la plus aiguë (position 7/8 → +36 semitons). Sert
            // au mode « transpose octave » : on ajoute/retire des ×12 jusqu'à rentrer dedans.
            int rangeMin = int.MaxValue, rangeMax = int.MinValue;
            for (int i = 0; i < tuning.OpenMidi.Length; i++)
            {
                if (tuning.OpenMidi[i] < rangeMin) rangeMin = tuning.OpenMidi[i];
                int top = GuqinModel.MidiFor(tuning, i, GuqinModel.HuiPositions[GuqinModel.HuiPositions.Length - 1]);
                if (top > rangeMax) rangeMax = top;
            }
            bool wantsViz = ctx != null && ctx.WantsViz;
            double tempo = ctx?.Tempo > 0 ? ctx.Tempo : 120.0;
            double blockStartAbs = ctx?.BlockStartBeat ?? 0.0;   // position absolue du bloc sur la timeline
            var constraint = new GuqinConstraint
            {
                DiapasonCm = _diapasonCm.Value,
                MaxSpanCm = _spanCm.Value,
                MaxStoppedFingers = Math.Max(1, Math.Min(5, (int)_maxFingers.Value)),
            };

            var input = new List<KotonGeneratedNote>(notes);
            input.Sort((a, b) => a.StartBeat.CompareTo(b.StartBeat));

            // Événements : NoteOn (kind=0) + NoteOff (kind=1) triés par temps. En cas d'égalité,
            // NoteOff avant NoteOn (une note qui finit exactement où une autre commence doit
            // d'abord libérer sa corde avant que la nouvelle n'y prétende).
            var events = new List<(double t, int kind, int idx)>(input.Count * 2);
            for (int i = 0; i < input.Count; i++)
            {
                events.Add((input[i].StartBeat, 0, i));
                events.Add((input[i].StartBeat + Math.Max(0.001, input[i].DurationBeats), 1, i));
            }
            events.Sort((a, b) =>
            {
                int c = a.t.CompareTo(b.t);
                if (c != 0) return c;
                return a.kind.CompareTo(b.kind);
            });

            var resolved = new (bool emitted, int midi, int stringIdx, double position)[input.Count];
            var heldByIdx = new GuqinConstraint.Held[input.Count];
            // Contexte pour la voice-leading : dernière position stoppée jouée (pour préférer un
            // fingering proche au suivant, comme un musicien qui minimise le déplacement de main).
            double? prevPositionCm = null;

            foreach (var (t, kind, idx) in events)
            {
                if (kind == 0)
                {
                    var srcNote = input[idx];
                    int target = srcNote.MidiNote;
                    // Mode « transpose octave » : ramène target dans [rangeMin..rangeMax] par
                    // sauts d'octave AVANT le résolveur. Préserve la classe de hauteur (do reste
                    // do, la reste la), juste dans le registre du guqin.
                    if (octaveTranspose && rangeMax >= rangeMin)
                    {
                        while (target < rangeMin) target += 12;
                        while (target > rangeMax) target -= 12;
                    }
                    // Résout en tenant compte de la position précédente (préférence : proche de
                    // là où la main était). Fallback = ResolveMidi classique (proche du centre).
                    var f = ResolveMidiWithContext(tuning, target, prevPositionCm, out bool exact);
                    if (!exact && !snap) { resolved[idx] = (false, -1, -1, 0); continue; }

                    var decision = constraint.Consider(f.StringIdx, f.Position, out var toRelease);
                    if (decision == GuqinConstraint.Decision.RejectStringBusy) { resolved[idx] = (false, -1, -1, 0); continue; }
                    if (decision == GuqinConstraint.Decision.StealOldest && toRelease != null)
                    {
                        constraint.Release(toRelease);
                        // Vol de doigt à l'instant t (StartBeat de la nouvelle note) → release
                        // scheduled à cet instant précis, PAS à la fin de la duration originale
                        // (la corde est reprise par la nouvelle note).
                        if (wantsViz) { try { NoteReleased?.Invoke(new ReleaseEvent { Midi = toRelease.Midi, AbsoluteAtBeat = blockStartAbs + srcNote.StartBeat, Tempo = tempo }); } catch { } }
                    }
                    var held = constraint.Register(f.Midi, f.StringIdx, f.Position);
                    heldByIdx[idx] = held;
                    resolved[idx] = (true, f.Midi, f.StringIdx, f.Position);
                    if (!f.IsOpen) prevPositionCm = f.Position * _diapasonCm.Value;
                    if (wantsViz)
                    {
                        try
                        {
                            NoteStruck?.Invoke(new StruckEvent
                            {
                                StringIdx = f.StringIdx,
                                Position = f.Position,
                                Midi = f.Midi,
                                StartBeat = srcNote.StartBeat,
                                DurationBeats = srcNote.DurationBeats,
                                AbsoluteStartBeat = blockStartAbs + srcNote.StartBeat,
                                Tempo = tempo,
                            });
                        } catch { }
                    }
                }
                else
                {
                    var h = heldByIdx[idx];
                    if (h != null && constraint.Active.Contains(h))
                    {
                        constraint.Release(h);
                        // Release scheduled à la fin naturelle de la note (t = event t).
                        if (wantsViz) { try { NoteReleased?.Invoke(new ReleaseEvent { Midi = h.Midi, AbsoluteAtBeat = blockStartAbs + t, Tempo = tempo }); } catch { } }
                    }
                }
            }

            // --- passe glissando : pour chaque note émise, cherche la note suivante émise sur la
            // MÊME corde ; si ni l'une ni l'autre n'est à vide, attache un pitch bend qui monte de
            // 0 à (target.midi - src.midi) semitons pendant la durée de la source. Le player
            // interpole linéairement → glissement audible entre les 2 hui. La note cible reste
            // attaquée normalement à son midi. ---
            bool doGliss = _glissando.Value >= 0.5;
            var bendCurves = new KotonBendPoint[input.Count][];
            if (doGliss)
            {
                for (int i = 0; i < input.Count; i++)
                {
                    if (!resolved[i].emitted) continue;
                    if (resolved[i].position <= 1e-6) continue;   // source à vide → pas de glissando
                    // Cherche la 1re note ultérieure sur la même corde, émise, non ouverte, midi différent.
                    int target = -1;
                    for (int j = i + 1; j < input.Count; j++)
                    {
                        if (!resolved[j].emitted) continue;
                        if (resolved[j].stringIdx != resolved[i].stringIdx) continue;
                        if (resolved[j].position <= 1e-6) break;   // suivante à vide → pas de glissando
                        if (resolved[j].midi == resolved[i].midi) break;   // même hui, rien à glisser
                        target = j; break;
                    }
                    if (target < 0) continue;
                    var s = input[i];
                    var t = input[target];
                    // Le bend atteint sa valeur cible juste avant la ré-attaque de la note cible
                    // (t.StartBeat - s.StartBeat). Si les 2 notes sont adjacentes (durée = gap),
                    // ça correspond à la durée sonore de la source. Si elles ne le sont pas, on
                    // borne à s.DurationBeats pour éviter un bend qui déborde après la fin audio.
                    double reachBeat = Math.Min(t.StartBeat - s.StartBeat, s.DurationBeats);
                    if (reachBeat < 0.01) continue;   // notes quasi-simultanées : pas de glissando
                    float semis = (float)(resolved[target].midi - resolved[i].midi);
                    bendCurves[i] = new[]
                    {
                        new KotonBendPoint(0.0, 0f),
                        new KotonBendPoint(reachBeat, semis),
                    };
                }
            }

            for (int i = 0; i < input.Count; i++)
            {
                if (!resolved[i].emitted) continue;
                var src = input[i];
                yield return new KotonGeneratedNote
                {
                    StartBeat = src.StartBeat,
                    DurationBeats = src.DurationBeats,
                    NotationDurationBeats = src.NotationDurationBeats,
                    MidiNote = resolved[i].midi,
                    Velocity = src.Velocity,
                    Bends = bendCurves[i],
                };
            }
        }

        /// <summary>Résout un MIDI en fingering avec voice-leading : parmi les candidats à distance
        /// minimale en semitons, on préfère celui dont la position (cm) est LA PLUS PROCHE de
        /// la précédente. Fallback (pas de précédente) = position proche du centre (hui 7),
        /// confortable pour la main.</summary>
        GuqinModel.Fingering ResolveMidiWithContext(GuqinModel.Tuning tuning, int targetMidi, double? prevPositionCm, out bool exact)
        {
            var all = GuqinModel.AllFingerings(tuning);
            double diapason = Math.Max(1, _diapasonCm.Value);
            GuqinModel.Fingering best = default;
            int bestDist = int.MaxValue;
            double bestScore = double.MaxValue;
            foreach (var f in all)
            {
                int d = Math.Abs(f.Midi - targetMidi);
                if (d > bestDist) continue;
                double score;
                if (prevPositionCm.HasValue)
                {
                    // Priorité : proche de la main précédente. Une corde à vide = pas de main
                    // engagée → score neutre au centre pour éviter un attrait pour la position 0.
                    double posCm = f.IsOpen ? diapason * 0.5 : f.Position * diapason;
                    score = Math.Abs(posCm - prevPositionCm.Value);
                }
                else
                {
                    score = Math.Abs(f.Position - 0.5) * diapason;   // fallback : proche du centre
                }
                if (d < bestDist || score < bestScore)
                {
                    bestDist = d; bestScore = score; best = f;
                }
            }
            exact = bestDist == 0;
            return best;
        }

        public byte[] SaveState()
        {
            try { var d = new Dictionary<string, double>(); foreach (var kp in _params) d[kp.Id] = kp.Value; return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(d)); }
            catch { return Array.Empty<byte>(); }
        }
        public void LoadState(byte[] state)
        {
            if (state == null || state.Length == 0) return;
            try { var d = JsonSerializer.Deserialize<Dictionary<string, double>>(Encoding.UTF8.GetString(state)); if (d == null) return; foreach (var kp in _params) if (d.TryGetValue(kp.Id, out var v)) kp.Value = v; } catch { }
        }
    }
}
