using System;
using System.Collections.Generic;
using KotonStudio.Library;
using MusicTracker.Engine.Flow;

namespace MusicTracker.Engine.Timeline
{
    /// <summary>
    /// Résolveur d'accords pour <see cref="KotonHost.GetChordAt"/> : agrège la piste d'accords
    /// built-in du projet (blocs <see cref="PatternGeneratorModule"/>, <see cref="CadenceModule"/>,
    /// <see cref="PolyChordModule"/>) et toutes les instances vivantes de
    /// <see cref="KotonGeneratorModule"/> dont <c>RuntimeInstance</c> implémente
    /// <see cref="IKotonChordSource"/>. Renvoie l'accord dont l'intervalle
    /// [start, start+duration) contient le beat demandé.
    ///
    /// **Portée** : appelé depuis un thread audio (Render d'un plugin qui interroge son harmonie
    /// pendant qu'il produit ses notes) ou depuis un thread UI (bouton Preview de l'éditeur). Le
    /// balayage est O(N) sur les blocs de la piste d'accords — quelques dizaines au pire, pas de
    /// cache nécessaire. Aucun état mutable — thread-safe par construction.
    ///
    /// **Priorité** : si un IKotonChordSource ET un bloc de la piste d'accords couvrent le même beat,
    /// la piste d'accords built-in gagne (comportement le moins surprenant : l'utilisateur qui a
    /// posé un accord classique s'attend à ce qu'il domine). Un futur enrichissement pourra donner
    /// à l'utilisateur le contrôle sur cette priorité via un flag sur le bloc générateur.
    /// </summary>
    public static class KotonChordResolver
    {
        /// <summary>Renvoie l'accord actif au <paramref name="beat"/> ABSOLU (position depuis le
        /// début du morceau, en temps). <c>null</c> = pas d'accord actif à cet instant.</summary>
        public static KotonChord? GetChordAt(TimelineProject project, double beat)
        {
            if (project?.Tracks == null) return null;

            // 1. Piste d'accords built-in — la source primaire, gagne en cas de recouvrement.
            var chordTrack = FindChordTrack(project);
            if (chordTrack != null)
            {
                var fromBuiltin = ScanChordTrack(chordTrack, beat);
                if (fromBuiltin.HasValue) return fromBuiltin;
            }

            // 2. Tous les KotonGeneratorModule qui publient de l'harmonie via IKotonChordSource.
            //    On parcourt TOUTES les pistes (une piste piano peut porter un module chord-source).
            foreach (var t in project.Tracks)
            {
                if (t?.Items == null) continue;
                var fromKoton = ScanKotonSources(project, t, beat);
                if (fromKoton.HasValue) return fromKoton;
            }
            return null;
        }

        static TimelineTrack FindChordTrack(TimelineProject project)
        {
            foreach (var t in project.Tracks)
                if (t != null && t.Type == TimelineTrackType.Chord) return t;
            return null;
        }

        // Balaie une piste d'accords et renvoie le bloc qui contient beat (le PREMIER trouvé — les
        // blocs ne peuvent pas se chevaucher sur une même piste). Renvoie le KotonChord logique
        // correspondant au module.
        static KotonChord? ScanChordTrack(TimelineTrack track, double beat)
        {
            double cursor = 0;
            foreach (var item in track.Items)
            {
                cursor += item.SilenceBefore;
                double len = TimelineProject.ItemLength(item, id => null);
                double endBeat = cursor + len;
                if (beat >= cursor && beat < endBeat && item.Module != null)
                {
                    var ch = ChordFor(item.Module, beat - cursor);
                    if (ch.HasValue) return ch;
                }
                cursor = endBeat;
            }
            return null;
        }

        // Convertit un module d'accord (PatternGenerator / Cadence / PolyChord) en KotonChord. Pour
        // Cadence et PolyChord, choisit l'accord actif au sous-beat donné (relatif au début du module).
        static KotonChord? ChordFor(FlowModule m, double relBeat)
        {
            switch (m)
            {
                case PatternGeneratorModule pg:
                    return new KotonChord
                    {
                        Root = ((pg.Root % 12) + 12) % 12,
                        Quality = MapQuality(pg.Quality),
                        BassNote = null,
                    };
                case CadenceModule cm:
                {
                    if (cm.Chords == null || cm.Chords.Count == 0) return null;
                    int cellIndex = (int)Math.Floor(relBeat / Math.Max(1, cm.BeatsPerBar));
                    if (cellIndex < 0) cellIndex = 0;
                    if (cellIndex >= cm.Chords.Count) cellIndex = cm.Chords.Count - 1;
                    var cc = cm.Chords[cellIndex];
                    if (cc == null) return null;
                    return new KotonChord
                    {
                        Root = ((cc.Root % 12) + 12) % 12,
                        Quality = MapQuality(cc.Quality),
                        BassNote = null,
                    };
                }
                case PolyChordModule pc:
                {
                    if (pc.Chords == null || pc.Chords.Count == 0) return null;
                    double cursor = 0;
                    foreach (var item in pc.Chords)
                    {
                        if (item == null) continue;
                        double next = cursor + Math.Max(1, item.Beats);
                        if (relBeat >= cursor && relBeat < next)
                            return new KotonChord
                            {
                                Root = ((item.Root % 12) + 12) % 12,
                                Quality = MapQuality(item.Quality),
                                BassNote = null,
                            };
                        cursor = next;
                    }
                    return null;
                }
                default: return null;
            }
        }

        // Balaie les blocs KotonGeneratorModule d'une piste et renvoie l'accord publié par l'instance
        // vivante si elle implémente IKotonChordSource. La position est celle du beat DANS le bloc.
        static KotonChord? ScanKotonSources(TimelineProject project, TimelineTrack track, double beat)
        {
            double cursor = 0;
            foreach (var item in track.Items)
            {
                cursor += item.SilenceBefore;
                double len = TimelineProject.ItemLength(item, id => null);
                double endBeat = cursor + len;
                if (beat >= cursor && beat < endBeat && item.Module is KotonGeneratorModule kg)
                {
                    // Ne PAS ré-instancier si l'instance n'existe pas — l'objectif ici est de lire une
                    // source d'accords existante, pas d'en créer une (sinon un simple GetChordAt
                    // provoquerait le chargement de tous les plugins de tous les modules de toutes
                    // les pistes, ce qui n'a pas de sens à cette étape). Un plugin non-instancié
                    // n'a pas encore d'accords à publier.
                    if (kg.RuntimeInstance is IKotonChordSource src)
                    {
                        var ch = src.GetChordAt(beat - cursor);
                        if (ch.HasValue) return ch;
                    }
                }
                cursor = endBeat;
            }
            return null;
        }

        // Mapping index qualité (voir PatternGenerator.QualityNames) → KotonChordQuality. Les tensions
        // (9e, 11e, 13e, sus9…) dégénèrent en leur qualité de base — le contrat KotonChord ne les
        // supporte pas encore. Un plugin qui veut de la finesse harmonique passera par
        // IKotonChordSource où il peut publier des enrichissements.
        static KotonChordQuality MapQuality(int qIndex)
        {
            // Aligné sur PatternGenerator.QualityNames :
            //  0 Majeur, 1 Mineur, 2 Diminué, 3 Augmenté, 4 Sus2, 5 Sus4, 6 Maj7, 7 Min7, 8 7 (dom),
            //  9 m7♭5, 10 dim7, 11 6, 12 m6, 13 add9, 14 m(add9), 15 9 (dom), 16 Maj9, 17 m9,
            //  18 7♭9, 19 7♯9, 20 11 (dom), 21 13 (dom), 22 Maj7♯11, 23 7sus4, 24 7sus2,
            //  25 9sus4, 26 9sus2, 27 6sus4, 28 6sus2, 29 Maj7sus4, 30 Maj7sus2,
            //  31 Maj9sus4, 32 Maj9sus2, 33 add9sus4, 34 7♯5.
            switch (qIndex)
            {
                case 0: return KotonChordQuality.Major;
                case 1: return KotonChordQuality.Minor;
                case 2: return KotonChordQuality.Diminished;
                case 3: return KotonChordQuality.Augmented;
                case 4: return KotonChordQuality.Sus2;
                case 5: return KotonChordQuality.Sus4;
                case 6: return KotonChordQuality.Major7;
                case 7: return KotonChordQuality.Minor7;
                case 8: return KotonChordQuality.Dominant7;
                case 9: return KotonChordQuality.HalfDim7;
                case 10: return KotonChordQuality.Diminished7;
                case 11: return KotonChordQuality.Major;           // 6th → Major (base)
                case 12: return KotonChordQuality.Minor;           // m6 → Minor (base)
                case 13: return KotonChordQuality.Major;           // add9 → Major
                case 14: return KotonChordQuality.Minor;           // m(add9) → Minor
                case 15: return KotonChordQuality.Dominant7;       // 9 (dom) → Dom7
                case 16: return KotonChordQuality.Major7;          // Maj9 → Maj7
                case 17: return KotonChordQuality.Minor7;          // m9 → Min7
                case 18: return KotonChordQuality.Dominant7;       // 7♭9 → Dom7
                case 19: return KotonChordQuality.Dominant7;       // 7♯9 → Dom7
                case 20: return KotonChordQuality.Dominant7;       // 11 (dom) → Dom7
                case 21: return KotonChordQuality.Dominant7;       // 13 (dom) → Dom7
                case 22: return KotonChordQuality.Major7;          // Maj7♯11 → Maj7
                case 23: return KotonChordQuality.Sus4;            // 7sus4 → Sus4
                case 24: return KotonChordQuality.Sus2;            // 7sus2 → Sus2
                case 25: return KotonChordQuality.Sus4;            // 9sus4 → Sus4
                case 26: return KotonChordQuality.Sus2;            // 9sus2 → Sus2
                case 27: return KotonChordQuality.Sus4;            // 6sus4 → Sus4
                case 28: return KotonChordQuality.Sus2;            // 6sus2 → Sus2
                case 29: return KotonChordQuality.Sus4;            // Maj7sus4 → Sus4
                case 30: return KotonChordQuality.Sus2;            // Maj7sus2 → Sus2
                case 31: return KotonChordQuality.Sus4;            // Maj9sus4 → Sus4
                case 32: return KotonChordQuality.Sus2;            // Maj9sus2 → Sus2
                case 33: return KotonChordQuality.Sus4;            // add9sus4 → Sus4
                case 34: return KotonChordQuality.Augmented;       // 7♯5 → Augmented (base)
                default: return KotonChordQuality.Major;
            }
        }
    }
}
