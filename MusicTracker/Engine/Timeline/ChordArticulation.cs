using System;
using System.Collections.Generic;
using MusicTracker.Engine.Flow;

namespace MusicTracker.Engine.Timeline
{
    /// <summary>
    /// Rendu d'un <see cref="ChordArticulationModule"/> : le module ne porte AUCUN accord, il articule
    /// l'accord actif à chaque instant. Sa durée est libre, donc on SEGMENTE son étendue aux frontières
    /// des accords de la piste Accords et on articule chaque segment avec l'accord qui y règne — un bloc
    /// peut ainsi couvrir 4 accords et le suivant 2.
    ///
    /// Chaque segment est rendu en réutilisant le moteur de styles existant (<see cref="PatternGenerator"/>)
    /// via un <see cref="PatternGeneratorModule"/> temporaire = accord du segment + paramètres de réalisation
    /// du bloc. Même approche que <see cref="PatternGenerator.GenerateCadence"/> : on concatène les NOTES
    /// (jamais les slices — deux accords identiques de part et d'autre d'une frontière doivent se ré-attaquer).
    /// </summary>
    public static class ChordArticulation
    {
        /// <summary>Un accord actif sur une portion de la timeline, en temps ABSOLUS.</summary>
        public struct Segment
        {
            public double Start, Len;
            public int Root, Quality, Inversion;
        }

        public static double TotalBeats(ChordArticulationModule m) => m == null ? 0 : Math.Max(0.25, m.Beats);

        /// <summary>
        /// Rendu du bloc posé à <paramref name="moduleStartBeat"/> (temps absolu sur sa piste). Renvoie un riff
        /// dont les notes couvrent <see cref="ChordArticulationModule.Beats"/>. Aucun accord actif → riff vide
        /// (silencieux) : l'harmonie vient de la piste Accords, un bloc sans accord n'a rien à jouer.
        /// </summary>
        public static Riff Generate(ChordArticulationModule m, TimelineProject project, Func<Guid, Riff> resolve, double moduleStartBeat)
        {
            int spq = PatternGenerator.SlicesPerQuarter;
            double total = TotalBeats(m);
            int totalSlices = Math.Max(1, (int)Math.Round(total * spq));
            var notes = new List<RiffNote>();
            if (m == null) return new Riff { Name = "Articulation", Notes = notes, LengthSlices = totalSlices, SlicesPerQuarter = spq };

            var segs = Segments(project, resolve, moduleStartBeat, total);
            for (int i = 0; i < segs.Count; i++)
            {
                var s = segs[i];
                double segBeats = s.Len;
                if (segBeats <= 1e-6) continue;

                // Le moteur de styles raisonne en mesures ENTIÈRES : on génère au moins la durée du segment puis on
                // rogne ce qui déborde, pour qu'un accord de durée fractionnaire ne fasse pas déborder le bloc.
                int genBeats = Math.Max(1, (int)Math.Ceiling(segBeats - 1e-9));
                var pg = new PatternGeneratorModule
                {
                    Root = s.Root, Quality = s.Quality,
                    Inversion = m.VoiceLeadMode > 0 ? s.Inversion : m.Inversion,
                    Octave = m.Octave,
                    Style = m.Style, Bass = m.Bass, BassPerBeat = m.BassPerBeat,
                    HeldMode = m.HeldMode, ClimbMode = m.ClimbMode, HalveDurations = m.HalveDurations,
                    OpenVoicing = m.OpenVoicing,
                    PatternCellOffset = i,                 // fait tourner le motif « mixte » d'un accord à l'autre
                    BeatsPerBar = genBeats, Repeats = 1,
                    CustomSlices = m.CustomSlices, CustomSlicesPerQuarter = m.CustomSlicesPerQuarter,
                    CustomNotes = m.CustomNotes,
                };

                var r = PatternGenerator.Generate(pg);
                // Le riff rendu N'EST PAS forcément à la résolution canonique : un style « Personnalisé » revient à la
                // résolution de SA grille (4 slices/temps par défaut) et les styles arpégés peuvent halver la valeur.
                // Sans ce rééchelonnage, une noire de 4 slices relue comme 4/24 de temps sonnait en double-croche.
                int srcSpq = r.SlicesPerQuarter > 0 ? r.SlicesPerQuarter : spq;
                double scale = (double)spq / srcSpq;

                int off = (int)Math.Round((s.Start - moduleStartBeat) * spq);
                int segSlices = (int)Math.Round(segBeats * spq);
                foreach (var n in r.Notes)
                {
                    int nStart = (int)Math.Round(n.Start * scale);
                    int nLen = Math.Max(1, (int)Math.Round(n.Length * scale));
                    if (nStart >= segSlices) continue;                        // déborde le segment → ignoré
                    int len = Math.Min(nLen, segSlices - nStart);             // rogné à la frontière d'accord
                    int start = off + nStart;
                    if (len <= 0 || start >= totalSlices) continue;
                    len = Math.Min(len, totalSlices - start);                 // rogné à la fin du bloc
                    if (len > 0) notes.Add(new RiffNote(n.Note, start, len));
                }
            }

            return new Riff { Name = "Articulation", Notes = notes, LengthSlices = totalSlices, SlicesPerQuarter = spq };
        }

        /// <summary>
        /// Accords actifs (temps absolus) chevauchant <c>[from, from+length)</c>, découpés à leurs frontières et
        /// rognés à cette fenêtre. Même source d'harmonie que <see cref="Harmony.ChordAt"/> : grille d'arrangement
        /// si présente, sinon les pistes d'accords (accord simple, PolyChord, cadence).
        /// </summary>
        public static List<Segment> Segments(TimelineProject project, Func<Guid, Riff> resolve, double from, double length)
        {
            var list = new List<Segment>();
            double to = from + length;
            if (project == null || length <= 0) return list;

            // Mode STRUCTURE : la grille d'accords de l'arrangement (une cellule = ChordSlices slices).
            var arr = project.Arrangement;
            if (arr?.Chords != null && arr.Chords.Count > 0)
            {
                double pickup = project.PickupBeats > 0 ? project.PickupBeats : 0;
                int aspq = Math.Max(1, arr.SlicesPerQuarter);
                double cell = Math.Max(1e-6, arr.ChordSlices / (double)aspq);
                int first = (int)Math.Floor((from - pickup) / cell + 1e-9);
                for (int i = Math.Max(0, first); i < arr.Chords.Count; i++)
                {
                    double cs = pickup + i * cell, ce = cs + cell;
                    if (ce <= from + 1e-9) continue;
                    if (cs >= to - 1e-9) break;
                    Add(list, cs, ce, from, to, arr.Chords[i].Root, arr.Chords[i].Quality, 0);
                }
                return list;
            }

            if (project.Tracks == null) return list;
            // La piste ACCORDS est LA source d'harmonie : on la consulte d'abord. Sans elle seulement, on retombe sur
            // les autres pistes (anciens projets où les accords vivaient sur une piste instrument). Sans cet ordre, un
            // module polyrythmique posé sur une piste instrument serait pris pour la source de sa propre harmonie.
            bool found = false;
            foreach (var pass in new[] { true, false })
            {
            if (found) break;
            foreach (var tr in project.Tracks)
            {
                if (tr?.Items == null) continue;
                if ((tr.Type == TimelineTrackType.Chord) != pass) continue;
                double cursor = 0;
                bool any = false;
                foreach (var item in tr.Items)
                {
                    cursor += item.SilenceBefore;
                    double len = TimelineProject.ItemLength(item, resolve);
                    double s = cursor, e = cursor + len;
                    cursor = e;
                    if (e <= from + 1e-9 || s >= to - 1e-9) continue;         // hors fenêtre

                    if (item.Module is PatternGeneratorModule pg)
                    { Add(list, s, e, from, to, pg.Root, pg.Quality, pg.Inversion); any = true; }
                    else if (item.Module is CadenceModule cm && cm.Chords != null && cm.Chords.Count > 0)
                    {
                        double cell = Math.Max(1, cm.BeatsPerBar);
                        for (int i = 0; i < cm.Chords.Count; i++)
                        {
                            double cs = s + i * cell, ce = cs + cell;
                            if (ce <= from + 1e-9 || cs >= to - 1e-9) continue;
                            var cc = cm.Chords[i];
                            Add(list, cs, ce, from, to, cc.Root, cc.Quality, cc.Inversion); any = true;
                        }
                    }
                    else if (item.Module is PolyChordModule pc)
                    {
                        // Les accords d'un PolyChord sont internes : on échantillonne à ses propres frontières.
                        double t = Math.Max(s, from);
                        while (t < Math.Min(e, to) - 1e-9)
                        {
                            if (!PolyChord.ChordAt(pc, t - s, out var it, out double itStart)) break;
                            double cs = s + itStart, ce = cs + Math.Max(0.25, it.Beats);
                            if (ce <= t + 1e-9) break;                        // garde-fou anti-boucle
                            Add(list, cs, ce, from, to, it.Root, it.Quality, it.Inversion); any = true;
                            t = ce;
                        }
                    }
                }
                if (any) { found = true; break; }   // première piste porteuse d'harmonie : on s'arrête là
            }
            }

            list.Sort((a, b) => a.Start.CompareTo(b.Start));
            return list;
        }

        static void Add(List<Segment> list, double s, double e, double from, double to, int root, int quality, int inversion)
        {
            double cs = Math.Max(s, from), ce = Math.Min(e, to);
            if (ce - cs <= 1e-6) return;
            list.Add(new Segment { Start = cs, Len = ce - cs, Root = root, Quality = quality, Inversion = inversion });
        }
    }
}
