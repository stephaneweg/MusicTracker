using System;
using System.Collections.Generic;
using System.Threading;
using KotonStudio.Library;
using MusicTracker.Engine;
using MusicTracker.Engine.Flow;
using MusicTracker.Engine.Timeline;
using NAudio.Wave;

namespace MusicTracker.Screens
{
    /// <summary>
    /// Petit player one-shot utilisé par <see cref="KotonHost.PreviewNotes"/> pour entendre le rendu
    /// d'un générateur SANS lancer la timeline. Utilise la SEULE plomberie MeltySynth existante :
    /// convertit la liste de <see cref="KotonGeneratedNote"/> en <see cref="Riff"/> canonique, puis
    /// laisse <see cref="MeltyRiffPlayer"/> le rendre sur un <see cref="WaveOutEvent"/> jetable —
    /// même pattern que les éditeurs Poly*/RhythmGrid. La piste cible fournit son GM program (pour
    /// que la preview sonne comme la piste ; à l'exception : si la piste a un VSTi/Koton natif, on
    /// tombe silencieusement sur MeltySynth avec le program GM — la preview cross-plugin serait un
    /// enrichissement futur, non demandé v1).
    ///
    /// **Cycle** : <see cref="Start"/> alloue le synth + WaveOut, joue, s'auto-nettoie à la fin de
    /// la note la plus longue ou sur <see cref="CancellationToken"/>. Un nouveau preview annule le
    /// précédent au niveau appelant (KotonHost_PreviewNotes) — ce player ne gère pas le chaînage.
    /// </summary>
    internal sealed class KotonPreviewPlayer
    {
        readonly TimelineTrack _track;
        readonly List<KotonGeneratedNote> _notes;
        readonly double _beatSeconds;
        readonly CancellationToken _cancel;
        WaveOutEvent _wave;
        LoopingRiffProvider _provider;
        Thread _watcher;

        public KotonPreviewPlayer(TimelineTrack track, List<KotonGeneratedNote> notes, double beatSeconds, CancellationToken cancel)
        {
            _track = track;
            _notes = notes ?? new List<KotonGeneratedNote>();
            _beatSeconds = beatSeconds > 0 ? beatSeconds : 0.5;
            _cancel = cancel;
        }

        public void Start()
        {
            // 1. Construire un Riff depuis les notes fournies (le canvas SlicesPerQuarter est celui du
            // pipeline interne). Longueur = la note la plus tardive (start + duration), avec un floor
            // à 0.25 beat.
            var riff = BuildRiff(_notes, out double totalBeats);
            if (riff.Notes.Count == 0) return;

            // 2. Contexte GM pour le canal : le GM program de la piste (0 pour la piste Chord).
            var preset = InstrumentCatalog.GetPreset(_track.Instrument);
            var ctx = new FlowContext
            {
                GmProgram = preset?.PatchNumber ?? 0,
                Drum = _track.Type == TimelineTrackType.Drum,
                Bpm = _beatSeconds > 0 ? 60.0 / _beatSeconds : 120,
            };

            try
            {
                _provider = new LoopingRiffProvider(() => riff, ctx);
                _wave = new WaveOutEvent { DesiredLatency = 120 };
                _wave.Init(_provider);
                _wave.Play();
            }
            catch
            {
                Cleanup();
                return;
            }

            // 3. Thread watcher : arrête la preview à la fin du Riff (durée totale + petite marge de
            // release) ou dès annulation. Un thread léger de courte vie — pas de pool nécessaire.
            _watcher = new Thread(() =>
            {
                double durSeconds = Math.Max(0.5, totalBeats * _beatSeconds + 0.5);
                var deadline = DateTime.UtcNow.AddSeconds(durSeconds);
                while (DateTime.UtcNow < deadline)
                {
                    if (_cancel.IsCancellationRequested) break;
                    Thread.Sleep(50);
                }
                Cleanup();
            })
            { IsBackground = true, Name = "KotonPreview" };
            _watcher.Start();
        }

        static Riff BuildRiff(List<KotonGeneratedNote> notes, out double totalBeats)
        {
            const int spq = 24;   // même résolution que KotonGeneratorRuntime pour éviter les arrondis
            double lastBeat = 0;
            var riff = new Riff { Name = "koton-preview", SlicesPerQuarter = spq, Notes = new List<RiffNote>() };
            foreach (var n in notes)
            {
                int midi = n.MidiNote;
                if (midi < 12 || midi > 107) continue;   // même plage que le pipeline (0..95 en RiffNote)
                double startBeat = Math.Max(0, n.StartBeat);
                double lenBeats = Math.Max(0, n.DurationBeats);
                int startSlice = (int)Math.Round(startBeat * spq);
                int lenSlices = Math.Max(1, (int)Math.Round(lenBeats * spq));
                riff.Notes.Add(new RiffNote(midi - 12, startSlice, lenSlices));
                double endBeat = startBeat + Math.Max(lenBeats, 1.0 / spq);
                if (endBeat > lastBeat) lastBeat = endBeat;
            }
            totalBeats = Math.Max(0.25, lastBeat);
            riff.LengthSlices = (int)Math.Round(totalBeats * spq);
            return riff;
        }

        void Cleanup()
        {
            try { _wave?.Stop(); _wave?.Dispose(); } catch { }
            _wave = null;
            _provider = null;
        }
    }
}
