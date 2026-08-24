using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using MusicTracker.Engine;
using MusicTracker.Engine.Timeline;
using MusicTracker.Engine.Timeline.Effects;

namespace MusicTracker.Dialogs
{
    /// <summary>
    /// Écoute en boucle d'un insert depuis le mixeur : sur une piste, un de ses blocs joué à travers la
    /// chaîne d'effets ; sur le master, le morceau entier. Régler une réverbe ou un compresseur demandait
    /// jusqu'ici de lancer le transport, d'attendre le bon passage, de corriger, de recommencer.
    ///
    /// **Jusqu'à l'insert cliqué, pas au-delà** : chaque ligne de la chaîne fait donc entendre quelque
    /// chose de différent — la première le son presque nu, la dernière la tranche complète. C'est le
    /// comportement d'une console, et c'est ce qui permet d'entendre ce qu'UN effet ajoute plutôt que ce
    /// que la chaîne produit en bloc.
    ///
    /// **Les réglages entendus sont ceux d'à présent** : le projet joué partage les mêmes objets
    /// <see cref="TrackEffectData"/> que le mixeur, et les effets Koton passent par le cache d'instances —
    /// c'est donc littéralement le plugin ouvert à l'écran qui traite le son, curseurs compris.
    ///
    /// **Ce qui est mis de côté** pendant l'écoute d'une piste : les inserts du master, le bus de réverbe,
    /// le mute et le solo, et les courbes d'automation de paramètres. On veut entendre l'effet tel qu'il
    /// est réglé, pas ce qu'une courbe en fait à cet instant du morceau — et surtout pas deux lecteurs qui
    /// se disputeraient les mêmes paramètres si le transport tourne en même temps.
    /// </summary>
    internal static class InsertPreview
    {
        /// <summary>Ce qu'écoute la lecture en cours : la piste (<c>null</c> = master) et l'indice du
        /// dernier insert traversé. Sert au mixeur pour afficher ⏹ sur la bonne ligne.</summary>
        public static (TimelineTrack Track, int Index)? Playing { get; private set; }

        /// <summary>Levé quand l'écoute démarre ou s'arrête (fin de la boucle comprise) — le mixeur
        /// rafraîchit ses boutons. Toujours sur le thread UI.</summary>
        public static event Action StateChanged;

        static TimelinePlayer _player;
        static LookaheadBuffer _buffer;
        static NAudio.Wave.WaveOutEvent _wave;
        static TimelineTrack _clone;      // copie jetable dont il faut libérer les instruments à la fin

        /// <summary>Longueur d'écoute du master, en mesures, à partir du curseur. Assez pour juger un
        /// compresseur ou une réverbe de bus sans avoir à réécouter tout le morceau.</summary>
        const int MasterMeasures = 8;

        /// <summary>Démarre l'écoute demandée, ou l'arrête si c'est déjà elle qui joue.</summary>
        public static void Toggle(TimelineProject project, Screens.TimelineScreen screen, TimelineTrack track, int upToInsert)
        {
            if (Playing.HasValue && ReferenceEquals(Playing.Value.Track, track) && Playing.Value.Index == upToInsert)
            {
                Stop();
                return;
            }
            Stop();
            Start(project, screen, track, upToInsert);
        }

        static void Start(TimelineProject project, Screens.TimelineScreen screen, TimelineTrack track, int upToInsert)
        {
            if (project == null) return;
            // Même garde que le transport : sans SoundFont, une piste MeltySynth ne rendrait rien et le
            // bouton n'aurait l'air que de ne pas marcher.
            if (!SoundFontGuard.EnsureReady(Application.Current?.MainWindow, "Playback")) return;
            var preview = Build(project, screen, track, upToInsert, out double startBeat, out double endBeat);
            if (preview == null) return;

            try
            {
                _clone = track != null ? preview.Tracks.FirstOrDefault() : null;
                _player = new TimelinePlayer(preview, project.RiffById, AudioFormat.SampleRate);
                _player.StartBeat = startBeat;
                _player.Loop = true;
                _player.LoopEndBeat = endBeat;
                _buffer = new LookaheadBuffer(_player, _player.Start, _player.Stop, AudioFormat.SampleRate);
                _buffer.Primed += () => OnUi(BeginDevice);
                _buffer.Ended += () => OnUi(Stop);
                Playing = (track, upToInsert);
                _buffer.Start();
                Raise();
            }
            catch
            {
                Stop();
            }
        }

        static void BeginDevice()
        {
            if (_buffer == null || _wave != null) return;   // arrêté pendant le remplissage
            try
            {
                _wave = new NAudio.Wave.WaveOutEvent { DesiredLatency = 150 };
                _wave.Init(_buffer);
                _wave.Play();
            }
            catch { Stop(); }
        }

        /// <summary>Coupe l'écoute et libère tout. Sans effet si rien ne joue.</summary>
        public static void Stop()
        {
            bool was = Playing.HasValue;
            Playing = null;
            if (_wave != null) { try { _wave.Stop(); _wave.Dispose(); } catch { } _wave = null; }
            if (_buffer != null) { try { _buffer.Stop(); } catch { } _buffer = null; }
            if (_player != null) { try { _player.Stop(); } catch { } try { _player.Dispose(); } catch { } _player = null; }
            if (_clone != null)
            {
                // La copie de piste a sa propre entrée dans les caches d'instruments (les clés sont des
                // références de piste) : sans ça, chaque écoute laisserait un instrument vivant derrière elle.
                try { KotonInstrumentCache.ReleaseTrack(_clone); } catch { }
                try { VstInstrumentCache.ReleaseTrack(_clone); } catch { }
                _clone = null;
            }
            if (was) Raise();
        }

        // ------------------------------------------------------------------------------------------

        /// <summary>Construit le projet d'écoute et la fenêtre de boucle. Retourne <c>null</c> s'il n'y a
        /// rien à jouer (piste vide, morceau vide).</summary>
        static TimelineProject Build(TimelineProject src, Screens.TimelineScreen screen, TimelineTrack track,
                                     int upToInsert, out double startBeat, out double endBeat)
        {
            startBeat = 0; endBeat = 0;
            var p = src.ShallowCopy();
            p.MasterAutomationLanes = new List<PluginAutomationLane>();

            if (track == null)
            {
                // Master : toutes les pistes telles quelles, seule la chaîne du master est tronquée.
                p.MasterInserts = Prefix(src.MasterInserts, upToInsert);
                double bpb = Math.Max(1.0, src.TimeSigNum * (4.0 / Math.Max(1, src.TimeSigDen)));
                double total = src.Tracks != null && src.Tracks.Count > 0 ? src.Tracks.Max(t => src.TrackEndBeats(t)) : 0;
                if (total <= 0) return null;
                startBeat = Math.Max(0, Math.Min(screen?.CursorBeat ?? 0, Math.Max(0, total - bpb)));
                endBeat = Math.Min(total, startBeat + MasterMeasures * bpb);
                if (endBeat - startBeat < 0.5) return null;
                return p;
            }

            var item = PickItem(src, track, screen?.CursorBeat ?? 0, out startBeat, out double len);
            if (item == null) return null;
            endBeat = startBeat + len;

            var clone = track.ShallowCopy();
            clone.Mute = false;
            clone.Solo = false;
            clone.Inserts = Prefix(track.Inserts, upToInsert);
            clone.PluginAutomationLanes = new List<PluginAutomationLane>();
            p.Tracks = new List<TimelineTrack> { clone };
            p.MasterInserts = new List<TrackEffectData>();
            p.ReverbBus = null;
            return p;
        }

        /// <summary>Le bloc à faire entendre : celui qui se trouve sous le curseur si le curseur tombe
        /// dessus, sinon le premier de la piste. Suivre le curseur est ce qui demande le moins
        /// d'explications — on écoute le passage qu'on regarde.</summary>
        static TimelineItem PickItem(TimelineProject project, TimelineTrack track, double cursor,
                                     out double startBeat, out double length)
        {
            startBeat = 0; length = 0;
            if (track?.Items == null || track.Items.Count == 0) return null;

            TimelineItem first = null;
            double firstStart = 0, firstLen = 0;
            double cur = 0;
            foreach (var it in track.Items)
            {
                cur += it.SilenceBefore;
                double len = project.DispLen(it);
                if (len > 0)
                {
                    if (first == null) { first = it; firstStart = cur; firstLen = len; }
                    if (cursor >= cur && cursor < cur + len) { startBeat = cur; length = len; return it; }
                }
                cur += len;
            }
            if (first == null) return null;
            startBeat = firstStart; length = firstLen;
            return first;
        }

        /// <summary>La chaîne jusqu'à <paramref name="upTo"/> inclus, dans une NOUVELLE liste (les
        /// <see cref="TrackEffectData"/> eux-mêmes restent partagés — c'est ce qui fait que l'écoute
        /// entend les réglages en cours d'édition).</summary>
        static List<TrackEffectData> Prefix(List<TrackEffectData> chain, int upTo)
        {
            var outp = new List<TrackEffectData>();
            if (chain == null) return outp;
            for (int i = 0; i < chain.Count && i <= upTo; i++) outp.Add(chain[i]);
            return outp;
        }

        static void OnUi(Action a)
        {
            var d = Application.Current?.Dispatcher;
            if (d == null) { try { a(); } catch { } return; }
            d.BeginInvoke(a);
        }

        static void Raise() { try { StateChanged?.Invoke(); } catch { } }
    }
}
