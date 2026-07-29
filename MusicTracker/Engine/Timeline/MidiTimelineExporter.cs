using System;
using System.Collections.Generic;
using NAudio.Midi;
using MusicTracker.Engine.Flow;

namespace MusicTracker.Engine.Timeline
{
    /// <summary>
    /// Writes a <see cref="TimelineProject"/> to a Standard MIDI File: one MIDI track per timeline track (drums on
    /// channel 10), notes at their real 24-slices-per-quarter timing, plus a tempo and time-signature event. The
    /// time signature is exported in REAL quarters per bar (a reinterpreted x/8 stays faithful — its triplet timing
    /// is preserved, bars align), so the file round-trips through the importer.
    ///
    /// LE MIXAGE SUIT. Le fichier ne portait auparavant que le tempo INITIAL et aucun réglage de piste : tout ce qui
    /// se règle dans le mixeur (volume + son automation, panoramique, réverbération) et les coupures mute/solo
    /// étaient perdus à l'export. Ils sont désormais écrits en messages de canal (CC7 / CC10 / CC91) et en carte de
    /// tempo, de sorte que le fichier ouvert dans une STA sonne comme la lecture dans l'application.
    /// </summary>
    public static class MidiTimelineExporter
    {
        const int Ppq = 480, Spq = 24;            // MIDI ticks/quarter, timeline slices/quarter
        const double TicksPerSlice = (double)Ppq / Spq; // 20

        /// <summary>Pas d'échantillonnage de l'automation de volume, en temps. Assez fin pour qu'un fondu s'entende
        /// continu, assez grossier pour ne pas noyer le fichier : on n'écrit de toute façon un CC que lorsque la
        /// valeur sur 0..127 change réellement, donc un volume constant ne coûte rien.</summary>
        const double AutomStepBeats = 0.125;

        const int CcVolume = 7, CcPan = 10, CcReverb = 91;

        public static void Export(string path, TimelineProject project, Func<Guid, Riff> resolve)
        {
            TimelineProject.ResolveLoops(project, resolve); // expand looping repeats before flattening
            var events = new MidiEventCollection(1, Ppq);

            // Track 0: tempo map + time signature.
            var meta = events.AddTrack();
            WriteTempoMap(meta, project);
            int barQuarters = project.TimeSigDen == 8 ? Math.Max(1, project.TimeSigNum / 3) : Math.Max(1, project.TimeSigNum);
            meta.Add(new TimeSignatureEvent(0, barQuarters, 2, 24, 8)); // numerator/4

            // Solo : dès qu'une piste est en solo, les autres se taisent — même règle qu'à la lecture
            // (TimelinePlayer.ApplyChannelVolumes), pour que les deux ne puissent pas diverger.
            bool anySolo = false;
            foreach (var t in project.Tracks) if (t != null && t.Solo) { anySolo = true; break; }

            int nextChannel = 1;
            bool drumChannelSet = false;   // le canal 10 est unique : seule la 1re piste de batterie pose ses réglages
            foreach (var t in project.Tracks)
            {
                if (t == null || t.Items == null || t.Items.Count == 0) continue;
                // Une piste inaudible n'est PAS exportée : c'est la convention des séquenceurs (Reaper, Cubase…),
                // et c'est la seule qui vaille pour les deux familles de pistes à la fois — le volume d'une piste de
                // batterie passant par la vélocité (voir plus bas), une vélocité nulle n'existe pas (0 = note off).
                // Le projet, lui, garde tout : seule cette exportation-ci est muette.
                if (t.Mute || (anySolo && !t.Solo)) continue;

                bool drum = t.Type == TimelineTrackType.Drum;
                int channel = drum ? 10 : (nextChannel == 10 ? ++nextChannel : nextChannel); // skip 10 for melodic
                if (!drum) nextChannel = channel + 1;

                var tr = events.AddTrack();
                tr.Add(new TextEvent(string.IsNullOrEmpty(t.Name) ? "Track" : t.Name, MetaEventType.SequenceTrackName, 0));

                var autom = SortedAutomation(t);

                if (drum)
                {
                    // Toutes les pistes de batterie partagent le canal 10 (GM n'en a qu'un pour les percussions) :
                    // un CC7 ou un CC10 posé par l'une s'appliquerait à toutes. Le volume est donc reporté sur la
                    // VÉLOCITÉ note à note — propre à chaque piste, et l'automation suit —, tandis que le pan et la
                    // réverbération, qui ne peuvent pas se dédoubler, sont ceux de la PREMIÈRE piste de batterie.
                    if (!drumChannelSet)
                    {
                        tr.Add(new ControlChangeEvent(0, channel, (MidiController)CcPan, PanCc(t.Pan)));
                        tr.Add(new ControlChangeEvent(0, channel, (MidiController)CcReverb, Clamp(t.ReverbSend, 0, 127)));
                        drumChannelSet = true;
                    }
                }
                else
                {
                    tr.Add(new PatchChangeEvent(0, channel, Clamp(t.Instrument, 0, 127)));
                    tr.Add(new ControlChangeEvent(0, channel, (MidiController)CcPan, PanCc(t.Pan)));
                    tr.Add(new ControlChangeEvent(0, channel, (MidiController)CcReverb, Clamp(t.ReverbSend, 0, 127)));
                    WriteVolumeCurve(tr, channel, t, autom, resolve);
                }

                // `project` est indispensable : les lignes mélodiques (classiques et polyrythmiques) tirent leurs
                // hauteurs de la grille d'accords. Sans lui, ces pistes s'exportaient vides.
                var src = TimelineImporter.FlattenForExport(t, resolve, project); // notes at absolute slices, 24/quarter
                foreach (var n in src.Notes)
                {
                    int pitch = Clamp(n.Pitch, 0, 127);
                    long on = (long)Math.Round(n.StartSlice * TicksPerSlice);
                    int dur = Math.Max(1, (int)Math.Round(n.LengthSlices * TicksPerSlice));
                    // Batterie : le volume de piste (et son automation) est appliqué ici, faute de CC7 utilisable.
                    // Mélodique : la vélocité reste celle de la note, le niveau est porté par la courbe de CC7.
                    int vel = drum
                        ? Clamp((int)Math.Round(n.Velocity * TimelinePlayer.TrackGain(autom, t.Volume, n.StartSlice / (double)Spq)), 1, 127)
                        : Clamp(n.Velocity, 1, 127);
                    var noteOn = new NoteOnEvent(on, channel, pitch, vel, dur);
                    tr.Add(noteOn);
                    tr.Add(noteOn.OffEvent);
                }
            }

            events.PrepareForExport(); // sorts each track and appends the End-of-Track meta events
            MidiFile.Export(path, events);
        }

        /// <summary>La carte de tempo complète, et non plus le seul tempo initial : un morceau qui ralentit ou
        /// change d'allure en cours de route s'exportait à vitesse constante.</summary>
        static void WriteTempoMap(IList<MidiEvent> meta, TimelineProject project)
        {
            var pts = new List<TempoChange>();
            if (project.Tempo != null)
                foreach (var tc in project.Tempo) if (tc != null && tc.Bpm > 0) pts.Add(tc);
            pts.Sort((x, y) => x.Beat.CompareTo(y.Beat));
            if (pts.Count == 0) pts.Add(new TempoChange { Beat = 0, Bpm = 120 });

            double last = -1;
            for (int i = 0; i < pts.Count; i++)
            {
                if (Math.Abs(pts[i].Bpm - last) < 1e-9) continue;         // pas d'événement pour un tempo inchangé
                // Le premier point vaut depuis le début même s'il est posé plus loin — c'est ce que fait
                // TimelinePlayer.BpmAt (avant le 1er point, le tempo est celui du 1er point).
                long tick = i == 0 ? 0 : (long)Math.Round(Math.Max(0, pts[i].Beat) * Ppq);
                meta.Add(new TempoEvent((int)Math.Round(60_000_000.0 / pts[i].Bpm), tick));
                last = pts[i].Bpm;
            }
        }

        /// <summary>Le volume de piste et son automation, tracés en CC7. Un seul événement quand le volume est
        /// constant ; une rampe échantillonnée, écrite seulement là où la valeur change, quand il ne l'est pas.</summary>
        static void WriteVolumeCurve(IList<MidiEvent> tr, int channel, TimelineTrack t, List<VolumePoint> autom, Func<Guid, Riff> resolve)
        {
            int last = VolumeCc(TimelinePlayer.TrackGain(autom, t.Volume, 0));
            tr.Add(new ControlChangeEvent(0, channel, (MidiController)CcVolume, last));
            if (autom == null || autom.Count == 0) return;

            double end = TimelineProject.TrackEnd(t, resolve);
            foreach (var p in autom) if (p.Beat > end) end = p.Beat;   // un point posé après la dernière note compte aussi

            for (double b = AutomStepBeats; b <= end + 1e-9; b += AutomStepBeats)
            {
                int v = VolumeCc(TimelinePlayer.TrackGain(autom, t.Volume, b));
                if (v == last) continue;
                tr.Add(new ControlChangeEvent((long)Math.Round(b * Ppq), channel, (MidiController)CcVolume, v));
                last = v;
            }
        }

        // TimelinePlayer.TrackGain exige une liste triée (le lecteur la trie au démarrage) ; le projet, lui, ne
        // garantit rien sur l'ordre des points.
        static List<VolumePoint> SortedAutomation(TimelineTrack t)
        {
            if (t.VolumeAutomation == null || t.VolumeAutomation.Count == 0) return null;
            var list = new List<VolumePoint>(t.VolumeAutomation);
            list.Sort((x, y) => x.Beat.CompareTo(y.Beat));
            return list;
        }

        static int VolumeCc(double gain) => (int)Math.Round(Math.Max(0.0, Math.Min(1.0, gain)) * 127);
        static int PanCc(double pan) => (int)Math.Round((Math.Max(-1.0, Math.Min(1.0, pan)) + 1.0) * 0.5 * 127);
        static int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);
    }
}
