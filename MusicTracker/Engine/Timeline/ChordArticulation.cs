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

        /// <summary>Durée occupée sur la timeline : la durée totale si elle est fixée, sinon une seule cellule.</summary>
        public static double TotalBeats(ChordArticulationModule m)
            => m == null ? 0 : Math.Max(0.25, m.LengthBeats > 0 ? m.LengthBeats : m.Beats);

        /// <summary>Longueur de la cellule répétée (le motif), toujours &gt; 0.</summary>
        public static double CellBeats(ChordArticulationModule m) => m == null ? 4 : Math.Max(0.25, m.Beats);

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

            // La CELLULE se répète jusqu'à remplir la durée totale, la dernière étant tronquée. Le motif est toujours
            // rendu depuis le DÉBUT de la cellule (le rythme ne redémarre donc pas à chaque accord) ; à l'intérieur
            // d'une cellule, on ne garde d'un rendu que la portion couverte par l'accord qui y règne — un changement
            // d'accord en cours de cellule change les hauteurs sans casser la continuité rythmique.
            double cell = CellBeats(m);
            int cellCount = Math.Max(1, (int)Math.Ceiling(total / cell - 1e-9));
            int genBeats = Math.Max(1, (int)Math.Ceiling(cell - 1e-9));

            // VOICE LEADING : à chaque CHANGEMENT d'accord, on choisit le renversement/registre le plus proche du
            // voicing précédent (au lieu de repartir en position fondamentale). L'état traverse les cellules, sinon
            // la conduite des voix repartirait de zéro à chaque répétition du motif. Tant que l'accord ne change pas,
            // on rejoue le MÊME voicing — sans cela il dériverait d'une cellule à l'autre.
            int[] prevVoicing = null;
            int lastRoot = int.MinValue, lastQuality = int.MinValue;
            int curInv = m.Inversion, curOct = m.Octave;

            for (int c = 0; c < cellCount; c++)
            {
                double cellStart = c * cell;                       // relatif au module
                double cellLen = Math.Min(cell, total - cellStart); // dernière cellule : tronquée
                if (cellLen <= 1e-6) break;

                var segs = Segments(project, resolve, moduleStartBeat + cellStart, cellLen);
                if (segs.Count == 0) continue;                      // aucun accord actif ici → silence

                foreach (var s in segs)
                {
                double segBeats = s.Len;
                if (segBeats <= 1e-6) continue;

                if (s.Root != lastRoot || s.Quality != lastQuality)      // l'accord a changé → (re)choisir le voicing
                {
                    if (m.VoiceLeadMode > 0 && prevVoicing != null)
                    {
                        var v = Engine.Flow.MusicTheory.VoiceLeadStep(prevVoicing, s.Root, s.Quality, m.Octave, m.VoiceLeadMode - 1);
                        curInv = v.inversion; curOct = v.octave;
                    }
                    else { curInv = m.Inversion; curOct = m.Octave; }
                    lastRoot = s.Root; lastQuality = s.Quality;
                    prevVoicing = PatternGenerator.ChordNotes(s.Root, curOct, s.Quality, curInv);
                }

                var pg = new PatternGeneratorModule
                {
                    Root = s.Root, Quality = s.Quality,
                    Inversion = curInv,
                    Octave = curOct,
                    Style = m.Style, Bass = m.Bass, BassPerBeat = m.BassPerBeat,
                    HeldMode = m.HeldMode, ClimbMode = m.ClimbMode, HalveDurations = m.HalveDurations,
                    OpenVoicing = m.OpenVoicing,
                    PatternCellOffset = c,                 // fait tourner le motif « mixte » d'une cellule à l'autre
                    BeatsPerBar = genBeats, Repeats = 1,
                    CustomSlices = m.CustomSlices, CustomSlicesPerQuarter = m.CustomSlicesPerQuarter,
                    CustomNotes = m.CustomNotes,
                };

                // Cellule mélodique optionnelle : 2e voix en DEGRÉS diatoniques, transposée sur chaque accord.
                if (m.HasMelodic)
                {
                    pg.MelodicOctave = m.MelodicOctave;
                    pg.MelodicAnchor = m.MelodicAnchor;
                    pg.MelodicSlicesPerQuarter = m.MelodicSlicesPerQuarter;
                    pg.MelodicNotes = m.MelodicNotes;
                    pg.MelodicSlices = m.MelodicSlices;
                }

                var r = PatternGenerator.Generate(pg);
                // Le riff rendu N'EST PAS forcément à la résolution canonique : un style « Personnalisé » revient à la
                // résolution de SA grille (4 slices/temps par défaut) et les styles arpégés peuvent halver la valeur.
                // Sans ce rééchelonnage, une noire de 4 slices relue comme 4/24 de temps sonnait en double-croche.
                int srcSpq = r.SlicesPerQuarter > 0 ? r.SlicesPerQuarter : spq;
                double scale = (double)spq / srcSpq;

                // Fenêtre de CET accord À L'INTÉRIEUR de la cellule : le motif étant rendu depuis le début de la
                // cellule, on ne retient que les notes qui tombent dans cette fenêtre (les autres appartiennent aux
                // accords voisins et seront produites par leur propre passage).
                int cellOff = (int)Math.Round(cellStart * spq);                              // début de cellule / module
                int winStart = (int)Math.Round((s.Start - (moduleStartBeat + cellStart)) * spq);
                int winEnd = winStart + (int)Math.Round(segBeats * spq);

                void Place(Riff src, double srcScale)
                {
                    if (src?.Notes == null) return;
                    foreach (var n in src.Notes)
                    {
                        int nStart = (int)Math.Round(n.Start * srcScale);
                        int nLen = Math.Max(1, (int)Math.Round(n.Length * srcScale));
                        if (nStart < winStart || nStart >= winEnd) continue;   // hors de l'accord courant
                        int len = Math.Min(nLen, winEnd - nStart);             // rogné à la frontière d'accord
                        int start = cellOff + nStart;
                        if (len <= 0 || start >= totalSlices) continue;
                        len = Math.Min(len, totalSlices - start);              // rogné à la fin du module (troncature)
                        if (len > 0) notes.Add(new RiffNote(n.Note, start, len));
                    }
                }

                Place(r, scale);

                // La cellule mélodique est un riff SÉPARÉ (autre résolution possible) : même fenêtrage.
                if (m.HasMelodic)
                {
                    var mel = PatternGenerator.GenerateMelodic(pg, project?.Key ?? new Engine.Score.KeySignature());
                    if (mel != null) Place(mel, (double)spq / (mel.SlicesPerQuarter > 0 ? mel.SlicesPerQuarter : spq));
                }
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
