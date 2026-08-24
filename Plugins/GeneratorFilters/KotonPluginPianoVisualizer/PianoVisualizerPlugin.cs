using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Windows.Controls;
using KotonStudio.Library;

namespace KotonPluginPianoVisualizer
{
    /// <summary>
    /// Visualiseur clavier — filtre de notes PASS-THROUGH (aucune contrainte, aucune modification)
    /// dont l'éditeur affiche un clavier 88 touches (A0 → C8) surmonté d'une piste de CHUTE :
    /// chaque note tombe d'en haut sous forme de bâtonnet vertical (hauteur = durée de la note) et
    /// touche le clavier exactement à l'instant où elle sonne. L'anticipation (par défaut 4 temps)
    /// est réglable. Utilisable sur n'importe quelle piste instrument pour lire ce qui arrive.
    ///
    /// Chaque note est émise UNE fois via <see cref="NoteStruck"/> avec sa position ABSOLUE sur la
    /// timeline et sa durée ; l'éditeur ne planifie rien sur une horloge murale, il POLLE la tête
    /// de lecture réelle (<see cref="KotonHost.PlayheadBeat"/>) à chaque frame — donc l'animation
    /// reste collée à l'audio malgré la latence du device, la pause, le départ au curseur ou la boucle.
    /// </summary>
    [KotonGeneratorConstrainer("Piano (viz clavier)", Id = "koton.piano_visualizer", Version = "1.1", Vendor = "Koton Studio")]
    public sealed class PianoVisualizerPlugin : IKotonGeneratorConstrainer
    {
        public string Id => "koton.piano_visualizer";
        public string DisplayName => "Piano (viz clavier)";

        /// <summary>Anticipation : combien de temps (beats) de musique la piste de chute montre
        /// AU-DESSUS du clavier. 4 = on voit une note arriver 4 temps avant qu'elle sonne.</summary>
        public KotonParameter Lead { get; } = new KotonParameter("lead", "Anticipation", 1, 16, 4, "temps");

        readonly List<KotonParameter> _params;
        public IReadOnlyList<KotonParameter> Parameters => _params;

        public PianoVisualizerPlugin()
        {
            _params = new List<KotonParameter> { Lead };
        }

        /// <summary>Note à visualiser, en beats ABSOLUS timeline (mêmes unités que
        /// <see cref="KotonHost.PlayheadBeat"/>) — l'éditeur en déduit la position ET la longueur
        /// du bâtonnet qui tombe.</summary>
        public struct StruckEvent
        {
            public int Midi;
            public double AbsoluteStartBeat;
            public double DurationBeats;
            public double Tempo;
            public int Velocity;
            /// <summary>Longueur d'une mesure en beats (noires), pour la grille rythmique de fond.</summary>
            public double BarBeats;
        }
        public struct ReleaseEvent { public int Midi; public double AbsoluteAtBeat; public double Tempo; }

        public event Action<StruckEvent> NoteStruck;
        /// <summary>Conservé pour les consommateurs qui ne raisonnent qu'en note-on / note-off.
        /// L'éditeur intégré ne s'en sert pas : la durée voyage désormais dans <see cref="StruckEvent"/>.</summary>
        public event Action<ReleaseEvent> NoteReleased;

        public bool HasEditor => true;
        public UserControl CreateEditor() => new PianoVisualizerEditor(this);
        public void Dispose() { }

        public IEnumerable<KotonGeneratedNote> Filter(IEnumerable<KotonGeneratedNote> notes, KotonRenderContext ctx)
        {
            if (notes == null) yield break;
            bool wantsViz = ctx != null && ctx.WantsViz;
            double tempo = ctx?.Tempo > 0 ? ctx.Tempo : 120.0;
            double blockStart = ctx?.BlockStartBeat ?? 0.0;
            // Mesure en NOIRES : 4/4 → 4, 3/4 → 3, 6/8 → 3 (6 croches = 3 noires).
            double barBeats = 4.0;
            if (ctx != null && ctx.TimeSigNum > 0 && ctx.TimeSigDen > 0)
                barBeats = ctx.TimeSigNum * 4.0 / ctx.TimeSigDen;
            foreach (var n in notes)
            {
                if (wantsViz)
                {
                    try
                    {
                        double dur = Math.Max(0.03, n.DurationBeats);
                        NoteStruck?.Invoke(new StruckEvent
                        {
                            Midi = n.MidiNote,
                            AbsoluteStartBeat = blockStart + n.StartBeat,
                            DurationBeats = dur,
                            Tempo = tempo,
                            Velocity = n.Velocity,
                            BarBeats = barBeats,
                        });
                        NoteReleased?.Invoke(new ReleaseEvent
                        {
                            Midi = n.MidiNote,
                            AbsoluteAtBeat = blockStart + n.StartBeat + dur,
                            Tempo = tempo,
                        });
                    }
                    catch { }
                }
                // PASS-THROUGH complet — la note ressort telle quelle.
                yield return n;
            }
        }

        public byte[] SaveState()
        {
            try { return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new State { lead = Lead.Value })); }
            catch { return Array.Empty<byte>(); }
        }

        public void LoadState(byte[] state)
        {
            if (state == null || state.Length == 0) return;
            try
            {
                var st = JsonSerializer.Deserialize<State>(Encoding.UTF8.GetString(state));
                if (st != null && st.lead > 0) Lead.Value = st.lead;
            }
            catch { }
        }

        sealed class State { public double lead { get; set; } }
    }
}
