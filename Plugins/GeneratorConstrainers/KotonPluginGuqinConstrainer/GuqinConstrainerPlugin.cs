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
        readonly KotonParameter _diapasonCm = new KotonParameter("diapason_cm", "Diapason",    50, 150, 110, "cm");
        readonly KotonParameter _spanCm     = new KotonParameter("span_cm",     "Empan main",  5, 30, 15, "cm");
        readonly KotonParameter _maxFingers = new KotonParameter("max_fingers", "Max doigts",  2, 5, 4);
        readonly KotonParameter _tuning     = new KotonParameter("tuning",      "Accordage",   0, 3, 0);
        // Snap : 0 = rejeter, 1 = snap au plus proche.
        readonly KotonParameter _snapMode   = new KotonParameter("snap_mode",   "Snap hors gamme", 0, 1, 1);

        readonly List<KotonParameter> _params;
        public IReadOnlyList<KotonParameter> Parameters => _params;

        public struct StruckEvent
        {
            public int StringIdx;
            public double Position;
            public int Midi;
            public double StartBeat;
            public double DurationBeats;
            /// <summary>Tempo en BPM au moment du bloc — l'éditeur convertit StartBeat en délai
            /// wall-clock pour animer les doigtés en cadence de lecture.</summary>
            public double Tempo;
            /// <summary>Identifiant incrémental de bloc (une passe Filter = un BlockId). L'éditeur
            /// reset son référentiel temporel quand le BlockId change → chaque relecture repart de
            /// zéro, chaque redraw d'un même bloc ne dédouble pas les événements.</summary>
            public long BlockId;
        }
        public event Action<StruckEvent> NoteStruck;
        public event Action<int> NoteReleased;

        long _blockCounter;

        public GuqinConstrainerPlugin()
        {
            _params = new List<KotonParameter> { _diapasonCm, _spanCm, _maxFingers, _tuning, _snapMode };
        }

        public bool HasEditor => true;
        public UserControl CreateEditor() => new GuqinConstrainerEditor(this);
        public void Dispose() { }

        internal GuqinModel.Tuning ActiveTuning
        {
            get
            {
                int idx = Math.Max(0, Math.Min(GuqinModel.AllTunings.Length - 1, (int)_tuning.Value));
                return GuqinModel.AllTunings[idx];
            }
        }

        public IEnumerable<KotonGeneratedNote> Filter(IEnumerable<KotonGeneratedNote> notes, KotonRenderContext ctx)
        {
            if (notes == null) yield break;
            var tuning = ActiveTuning;
            bool snap = _snapMode.Value >= 0.5;
            bool wantsViz = ctx != null && ctx.WantsViz;
            long blockId = System.Threading.Interlocked.Increment(ref _blockCounter);
            double tempo = ctx?.Tempo > 0 ? ctx.Tempo : 120.0;
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
                    // Résout en tenant compte de la position précédente (préférence : proche de
                    // là où la main était). Fallback = ResolveMidi classique (proche du centre).
                    var f = ResolveMidiWithContext(tuning, srcNote.MidiNote, prevPositionCm, out bool exact);
                    if (!exact && !snap) { resolved[idx] = (false, -1, -1, 0); continue; }

                    var decision = constraint.Consider(f.StringIdx, f.Position, out var toRelease);
                    if (decision == GuqinConstraint.Decision.RejectStringBusy) { resolved[idx] = (false, -1, -1, 0); continue; }
                    if (decision == GuqinConstraint.Decision.StealOldest && toRelease != null)
                    {
                        constraint.Release(toRelease);
                        if (wantsViz) { try { NoteReleased?.Invoke(toRelease.Midi); } catch { } }
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
                                Tempo = tempo,
                                BlockId = blockId,
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
                        if (wantsViz) { try { NoteReleased?.Invoke(h.Midi); } catch { } }
                    }
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
