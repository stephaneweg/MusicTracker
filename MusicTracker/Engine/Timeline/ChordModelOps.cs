using System;
using System.Collections.Generic;
using MusicTracker.Engine.AI;
using MusicTracker.Engine.Flow;

namespace MusicTracker.Engine.Timeline
{
    /// <summary>
    /// Pure model helpers for building CHORD modules on a timeline, shared by the AI composer
    /// (<see cref="AiArrangementPlacer"/>) and the structure/template code in the editor. No UI: they take the
    /// <see cref="TimelineProject"/> and its bar length explicitly, so both a fresh source and the live editor can use
    /// the exact same logic instead of duplicating it.
    /// </summary>
    public static class ChordModelOps
    {
        /// <summary>Beats (temps) per bar for the project's meter: num in x/4, num/3 in x/8.</summary>
        public static int BarTemps(TimelineProject project)
            => project.TimeSigDen == 8 ? Math.Max(1, project.TimeSigNum / 3) : Math.Max(1, project.TimeSigNum);

        /// <summary>The permanent "Accords" chord track, or null if not present.</summary>
        public static TimelineTrack ChordTrack(TimelineProject project)
            => project.Tracks?.Find(t => t.Type == TimelineTrackType.Chord);

        /// <summary>Ensure a bottom-pinned "Accords" chord track exists on a FRESH project (create if absent, pin last).
        /// The load-time ADOPTION of a legacy all-chords track lives in the editor's own EnsureChordTrack.</summary>
        public static void EnsureChordTrackSimple(TimelineProject project)
        {
            if (project?.Tracks == null) return;
            var chord = project.Tracks.Find(t => t.Type == TimelineTrackType.Chord);
            if (chord == null) { chord = new TimelineTrack { Name = "Accords", Type = TimelineTrackType.Chord, Instrument = 0 }; project.Tracks.Add(chord); }
            if (project.Tracks[project.Tracks.Count - 1] != chord) { project.Tracks.Remove(chord); project.Tracks.Add(chord); }
        }

        /// <summary>Add a chord module (degree/quality + optional voiced articulation and melodic cell) to a chords track,
        /// inheriting the previous chord's params. Returns the created module (pass it as <paramref name="prev"/> next time).</summary>
        public static PatternGeneratorModule AddAiChord(TimelineProject project, int barTemps, TimelineTrack chordTrack,
            AiChord c, int beats, int style, List<RiffNote> motifNotes, int artSpq, PatternGeneratorModule prev,
            bool silent = false, string userStyleName = null, List<RiffNote> melodicCell = null)
        {
            var pg = NewChordLike(project, prev, barTemps);
            pg.BeatsPerBar = System.Math.Max(1, beats); pg.Repeats = 1;
            int deg = System.Math.Max(1, System.Math.Min(7, c.degree));
            pg.Degree = deg - 1;
            pg.Root = AiTranslate.RootPc(project.Key, deg);
            pg.Quality = AiTranslate.QualityIndex(c.quality);
            // When the requested chord ISN'T the diatonic chord of its degree (a secondary dominant like V/V = a MAJOR
            // chord on degree ii, a borrowed chord, a modal mixture…), store it as a FIXED chord (Degree = −1). Otherwise
            // its major/dim quality would be silently re-derived back to the diatonic one on the next edit or key change,
            // and the editor combo would mislabel it. As a fixed chord, its FUNCTION (V/V…) is read from root+quality.
            {
                var k = project.Key ?? new Engine.Score.KeySignature();
                MusicTheory.ChordShape(pg.Quality, out bool reqMin, out bool reqDim, out _, out _);
                bool diaDim = MusicTheory.DiatonicIsDim(k, pg.Degree);
                bool diaMin = !diaDim && MusicTheory.DiatonicThird(k, pg.Degree) == 3;
                if (reqMin != diaMin || reqDim != diaDim) pg.Degree = -1;
                else
                {
                    // Le degré reste VERROUILLÉ : ses notes sont alors dérivées du degré + DiatonicColour + Suspension,
                    // et Quality est IGNORÉE. Sans la ligne ci-dessous, DiatonicColour restait à 0 = TRIADE, et toute
                    // septième diatonique demandée par l'IA était rejouée en triade — silencieusement, puisque le test
                    // ci-dessus ne compare que la TIERCE (une Maj7 sur le degré I a la même tierce que sa triade).
                    // Constaté sur une réponse réelle : 63 des 64 accords étaient des septièmes ou plus riches
                    // (26 Maj7, 25 Min7, 7 dom7, 2 neuvièmes, 2 7sus4, 1 Maj9) et AUCUNE ne s'entendait.
                    // ColourForQuality rend le trio (couleur, suspension, mode) qui REPRODUIT la qualité demandée sur un
                    // degré ; quand la qualité n'est pas exprimable ainsi, il rend (0,0,0) et on retombe sur la triade,
                    // c'est-à-dire l'ancien comportement. Le mode n'est pas repris : il est déjà porté par la tonalité.
                    var trio = Engine.Flow.ChordDegrees.ColourForQuality(pg.Quality);
                    pg.DiatonicColour = trio.colour;
                    pg.Suspension = trio.suspension;
                }
            }
            if (silent)
            {
                // "Accords en voix dédiée" : custom style with an EMPTY motif → the chord plays nothing but still carries
                // its degree/quality so MelodicLineModule (and the riff harmony fix) know the harmony under each bar.
                int chordSlices = System.Math.Max(1, pg.BeatsPerBar * artSpq);
                pg.Style = PatternGenerator.CustomStyle;
                pg.CustomSlicesPerQuarter = artSpq;
                pg.CustomNotes = new List<RiffNote>();
                pg.CustomSlices = RiffNotes.ToSlices(pg.CustomNotes, chordSlices);
            }
            else if (motifNotes != null && motifNotes.Count > 0)
            {
                // Custom voiced articulation: trim the one-bar motif to this chord's length, set the "Personnalisé" style.
                int chordSlices = System.Math.Max(1, pg.BeatsPerBar * artSpq);
                var mnotes = new List<RiffNote>();
                foreach (var n in motifNotes)
                {
                    if (n.Start >= chordSlices) continue;
                    int len = System.Math.Min(n.Length, chordSlices - n.Start);
                    if (len >= 1) mnotes.Add(new RiffNote(n.Note, n.Start, len));
                }
                pg.Style = PatternGenerator.CustomStyle;
                pg.CustomSlicesPerQuarter = artSpq;
                pg.CustomNotes = mnotes;
                pg.CustomSlices = RiffNotes.ToSlices(mnotes, chordSlices);
                // Reference the shared, project-saved articulation: the grid above stays per-chord (each chord may be
                // trimmed to its own length), but the name links them so a later edit propagates over the whole section.
                pg.UserStyleName = userStyleName;
            }
            else if (style >= 0) pg.Style = style;

            // Melodic cell: the SAME diatonic-degree phrase on every chord of the section. It is stored as grid rows
            // (degrees), so GenerateMelodic resolves it against THIS chord's anchor — i.e. it transposes modally.
            if (melodicCell != null && melodicCell.Count > 0 && !silent)
            {
                int cellSlices = System.Math.Max(1, pg.BeatsPerBar * artSpq);
                var cnotes = new List<RiffNote>();
                foreach (var n in melodicCell)
                {
                    if (n.Start >= cellSlices) continue;
                    int len = System.Math.Min(n.Length, cellSlices - n.Start);
                    if (len >= 1) cnotes.Add(new RiffNote(n.Note, n.Start, len));
                }
                if (cnotes.Count > 0) pg.SetMelodicNotes(cnotes, artSpq, cellSlices);
            }

            chordTrack.Items.Add(new TimelineItem { Module = pg });
            return pg;
        }

        /// <summary>A fresh chord module inheriting the previous chord's params (voice-leading defaults, bar-aligned).</summary>
        public static PatternGeneratorModule NewChordLike(TimelineProject project, PatternGeneratorModule prev, int barTemps)
        {
            var pg = new PatternGeneratorModule { BeatsPerBar = barTemps, Repeats = 1 };
            if (prev != null)
            {
                pg.Style = prev.Style; pg.UserStyleName = prev.UserStyleName;
                // Bar-align: if the previous chord carries an anacrusis lead-in (7 in 3/4), the new one drops it (7 → 6).
                int spq = System.Math.Max(1, prev.CustomSlicesPerQuarter);
                int gridCut = CopyLeadRem(project, prev.CustomSlices != null ? prev.CustomSlices.Length / (double)spq : 0, barTemps) * spq;
                pg.CustomSlices = prev.CustomSlices != null ? MotifCopy.TrimSlices(prev.CustomSlices, gridCut) : null;
                pg.CustomNotes = gridCut > 0 ? MotifCopy.TrimNotes(prev.CustomNotes, gridCut)
                                             : (prev.CustomNotes != null ? new List<RiffNote>(prev.CustomNotes) : null);
                pg.CustomSlicesPerQuarter = prev.CustomSlicesPerQuarter;
                pg.OpenVoicing = prev.OpenVoicing;
                pg.VoiceLeadMode = prev.VoiceLeadMode != 0 ? prev.VoiceLeadMode : 1;
                pg.Bass = prev.Bass; pg.BassPerBeat = prev.BassPerBeat;
                pg.ClimbMode = prev.ClimbMode; pg.HeldMode = prev.HeldMode; pg.HalveDurations = prev.HalveDurations;
                pg.DiatonicColour = prev.DiatonicColour; pg.Suspension = prev.Suspension; pg.ModeOverride = prev.ModeOverride; pg.Octave = prev.Octave;
                int prevTotal = System.Math.Max(1, prev.BeatsPerBar * System.Math.Max(1, prev.Repeats));
                pg.BeatsPerBar = System.Math.Max(1, prevTotal - CopyLeadRem(project, prevTotal, barTemps)); // default-style duration follows the same trim
            }
            return pg;
        }

        /// <summary>The leading anacrusis remainder (in beats) to drop when copying a phrase of <paramref name="totalBeats"/>,
        /// so the copy is bar-aligned; 0 when the project has no pickup.</summary>
        public static int CopyLeadRem(TimelineProject project, double totalBeats, int barTemps)
            => project.PickupBeats > 1e-6 ? MotifCopy.LeadRemainder(totalBeats, barTemps) : 0;

        /// <summary>Re-derive every degree-locked chord (and cadence chord) for the project's CURRENT key after a key/mode
        /// change from <paramref name="oldKey"/>: diatonic chords flip major↔minor with the mode; fixed-root chords keep
        /// their quality and transpose by the tonic interval (preserving their function). Returns true if anything moved.</summary>
        public static bool ResolveChordDegrees(TimelineProject project, Engine.Score.KeySignature oldKey)
        {
            var newKey = project.Key ?? new Engine.Score.KeySignature();
            oldKey = oldKey ?? newKey;
            bool any = false;

            // Cadence chords carry no colour, so infer it: find the primary colour that reproduced the chord's quality
            // in the OLD key, then re-derive with that colour in the NEW key. Non-diatonic (borrowed/secondary) qualities
            // aren't matched → the chord keeps its quality and only its root follows the degree.
            int NewCadenceQuality(int degree, int curQuality)
            {
                for (int col = 0; col < MusicTheory.DiatonicColourNames.Length; col++)
                    if (MusicTheory.DiatonicChord(oldKey, degree, col).quality == curQuality)
                        return MusicTheory.DiatonicChord(newKey, degree, col).quality;
                return curQuality;
            }

            // A FIXED-root chord (Degree < 0: secondary dominants, borrowed chords, hand-set chords) keeps its quality;
            // its root is TRANSPOSED by the tonic interval so its FUNCTION is preserved (a V/V = D major in Do stays a
            // V/V = Mi major in Ré). A mode-only change keeps the tonic, so the root doesn't move (D major is still V/V
            // in Do minor). Diatonic (Degree ≥ 0) chords are instead re-derived so they flip major↔minor with the mode.
            int tonicShift = ((MusicTheory.TonicPc(newKey) - MusicTheory.TonicPc(oldKey)) % 12 + 12) % 12;

            foreach (var t in project.Tracks)
            {
                if (t.Type == TimelineTrackType.Drum) continue;   // chord + instrument tracks carry pitched/degree chords
                foreach (var m in EnumModules(t.Items))
                {
                    if (m is PatternGeneratorModule pg)
                    {
                        if (pg.Degree >= 0)
                        {
                            var nd = MusicTheory.DiatonicChord(newKey, pg.Degree, pg.DiatonicColour, pg.Suspension, pg.ModeOverride);
                            if (pg.Root != nd.root || pg.Quality != nd.quality) { pg.Root = nd.root; pg.Quality = nd.quality; any = true; }
                        }
                        else if (tonicShift != 0)
                        {
                            pg.Root = ((pg.Root + tonicShift) % 12 + 12) % 12; any = true;   // transpose the fixed chord
                        }
                    }
                    else if (m is CadenceModule cm && cm.Chords != null)
                    {
                        foreach (var c in cm.Chords)
                            if (c.Degree >= 0)
                            {
                                var nd = MusicTheory.DiatonicChord(newKey, c.Degree);
                                int nq = NewCadenceQuality(c.Degree, c.Quality);
                                if (c.Root != nd.root || c.Quality != nq) { c.Root = nd.root; c.Quality = nq; any = true; }
                            }
                            else if (tonicShift != 0) { c.Root = ((c.Root + tonicShift) % 12 + 12) % 12; any = true; }
                    }
                    else if (m is Engine.Flow.PolyChordModule pcm && pcm.Chords != null)
                    {
                        // Même logique que PatternGeneratorModule (degré-verrouillé re-dérive avec couleur/susp/mode),
                        // appliquée à chaque accord de la liste.
                        foreach (var c in pcm.Chords)
                            if (c.Degree >= 0)
                            {
                                var nd = MusicTheory.DiatonicChord(newKey, c.Degree, c.DiatonicColour, c.Suspension, c.ModeOverride);
                                if (c.Root != nd.root || c.Quality != nd.quality) { c.Root = nd.root; c.Quality = nd.quality; any = true; }
                            }
                            else if (tonicShift != 0) { c.Root = ((c.Root + tonicShift) % 12 + 12) % 12; any = true; }
                    }
                }
            }
            return any;
        }

        /// <summary>Enumerate the top-level modules of a track's items (skips empty slots).</summary>
        public static IEnumerable<FlowModule> EnumModules(IList<TimelineItem> items)
        {
            if (items == null) yield break;
            foreach (var it in items) if (it.Module != null) yield return it.Module;
        }

        /// <summary>Transpose the whole project to key <paramref name="to"/> (direction 0=nearest, 1=up, 2=down;
        /// <paramref name="targetMode"/> = destination FullMode): shift every melodic riff note by the nearest interval,
        /// remap degrees on a mode flip, shift absolute chord roots, set the new key and re-derive degree-locked chords.
        /// Riffs are transposed once each (deduped via <paramref name="riffById"/>). Returns false if nothing to do.
        /// The caller commits pending edits beforehand and refreshes the UI afterwards.</summary>
        public static bool TransposeProject(TimelineProject project, Func<Guid, Riff> riffById,
            Engine.Score.KeySignature to, int direction, int targetMode)
        {
            var cur = project.Key ?? new Engine.Score.KeySignature();
            // All the interval/mode-remap maths lives in MusicTheory, so transposition behaves consistently with the rest
            // of the harmony (cadences, degree-locked chords).
            int interval = MusicTheory.NearestInterval(MusicTheory.TonicPc(cur), MusicTheory.TonicPc(to), direction);
            int srcMode = Engine.Score.MusicalMode.Effective(cur);
            int[] degDelta = MusicTheory.ModeDelta(srcMode, targetMode);
            if (interval == 0 && degDelta == null) return false; // nothing to do

            int toPc = MusicTheory.TonicPc(to);
            var seen = new HashSet<Riff>();
            foreach (var t in project.Tracks)
            {
                if (t.Type == TimelineTrackType.Drum) continue; // drums keep their pitches; chords transpose
                foreach (var m in EnumModules(t.Items))
                {
                    if (m is PlayRiffModule pr) { var r = riffById(pr.RiffId); if (r != null && seen.Add(r)) TransposeRiff(r, interval, degDelta, toPc); }
                    else if (m is PatternGeneratorModule pg && pg.Degree < 0) pg.Root = ((pg.Root + interval) % 12 + 12) % 12; // absolute chords (degree-locked follow the key below)
                    else if (m is CadenceModule cm && cm.Chords != null)
                        foreach (var c in cm.Chords) if (c.Degree < 0) c.Root = ((c.Root + interval) % 12 + 12) % 12; // absolute cadence chords
                    else if (m is Engine.Flow.PolyChordModule pcm && pcm.Chords != null)
                        foreach (var pci in pcm.Chords) if (pci.Degree < 0) pci.Root = ((pci.Root + interval) % 12 + 12) % 12; // absolute polychord chords
                }
            }

            project.Key = new Engine.Score.KeySignature { TonicLetter = to.TonicLetter, Accidental = to.Accidental, Mode = to.Mode, FullMode = targetMode };
            ResolveChordDegrees(project, cur); // degree-locked chords re-select themselves in the target key
            return true;
        }

        // Shift a riff's notes by the nearest interval; on a mode flip, raise/lower the differing degrees (3rd/6th/7th).
        static void TransposeRiff(Riff r, int interval, int[] degDelta, int toPc)
        {
            if (r?.Notes == null) return;
            var outn = new List<RiffNote>(r.Notes.Count);
            foreach (var n in r.Notes)
            {
                int p = n.Note + interval;
                if (degDelta != null) { int deg = (((p % 12) + 12) % 12 - toPc + 12) % 12; p += degDelta[deg]; }
                while (p < 0) p += 12; while (p > 95) p -= 12; // keep within the 0..95 note range (octave-wrap)
                outn.Add(new RiffNote(p, n.Start, n.Length));
            }
            r.Notes = outn;
        }
    }
}
