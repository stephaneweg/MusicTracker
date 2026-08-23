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
    /// dont l'éditeur affiche un clavier 88 touches (A0 → C8) où les touches s'illuminent au
    /// NoteOn et s'éteignent au NoteOff. Utilisable sur n'importe quelle piste instrument pour
    /// voir en direct ce qui joue.
    ///
    /// Réutilise exactement la même infra event que le Guqin constrainer :
    /// StruckEvent/ReleaseEvent avec AbsoluteStartBeat + Tempo + PlaybackStarted/Stopped pour
    /// synchroniser l'animation à la lecture réelle.
    /// </summary>
    [KotonGeneratorConstrainer("Piano (viz clavier)", Id = "koton.piano_visualizer", Version = "1.0", Vendor = "Koton Studio")]
    public sealed class PianoVisualizerPlugin : IKotonGeneratorConstrainer
    {
        public string Id => "koton.piano_visualizer";
        public string DisplayName => "Piano (viz clavier)";

        readonly List<KotonParameter> _params = new List<KotonParameter>();
        public IReadOnlyList<KotonParameter> Parameters => _params;

        public struct StruckEvent { public int Midi; public double AbsoluteStartBeat; public double Tempo; public int Velocity; }
        public struct ReleaseEvent { public int Midi; public double AbsoluteAtBeat; public double Tempo; }
        public event Action<StruckEvent> NoteStruck;
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
            foreach (var n in notes)
            {
                if (wantsViz)
                {
                    try
                    {
                        NoteStruck?.Invoke(new StruckEvent
                        {
                            Midi = n.MidiNote,
                            AbsoluteStartBeat = blockStart + n.StartBeat,
                            Tempo = tempo,
                            Velocity = n.Velocity,
                        });
                        NoteReleased?.Invoke(new ReleaseEvent
                        {
                            Midi = n.MidiNote,
                            AbsoluteAtBeat = blockStart + n.StartBeat + Math.Max(0.001, n.DurationBeats),
                            Tempo = tempo,
                        });
                    }
                    catch { }
                }
                // PASS-THROUGH complet — la note ressort telle quelle.
                yield return n;
            }
        }

        public byte[] SaveState() => Array.Empty<byte>();
        public void LoadState(byte[] state) { }
    }
}
