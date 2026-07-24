using MusicTracker.Dialogs;
using MusicTracker.Engine.Flow;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace MusicTracker.Engine.Timeline
{
    public class TimelineHelper
    {
        // The hand grid is editable only for the "Personnalisé" category (a catalogue motif is applied, not edited).
        public static bool DrumIsCustom(DrumPatternModule dp)
            => dp.CatCategory == "Personnalisé" || (string.IsNullOrEmpty(dp.CatCategory) && dp.Style == DrumPattern.CustomStyle);


        // The section covering a given bar (or null).
        static ArrSection SectionAtBar(ComposedArrangement arr, int bar)
        {
            foreach (var s in arr.Sections) if (bar >= s.StartBar && bar < s.StartBar + s.Bars) return s;
            return null;
        }

        // Total beats occupied by a track (silences + item lengths) — the append cursor for "develop the theme".
        public static double TrackEndBeats(TimelineTrack t)
        {
            if (t?.Items == null) return 0;
            double cur = 0;
            foreach (var it in t.Items) cur += it.SilenceBefore + Engine.Timeline.TimelineProject.ItemLength(it, TimelineHelper.RiffById);
            return cur;
        }

        // Displayed length (beats) = the real length, including a Repeat's full ×Count span. The Repeat's
        // CONTENT is still drawn only once (no tiled ghost copies); only its backdrop spans ×Count.
        public static double DispLen(TimelineItem it) => TimelineProject.ItemLength(it, TimelineHelper.RiffById);

        public static TimelineTrack ChordTrack(TimelineProject project) => Engine.Timeline.ChordModelOps.ChordTrack(project);
        public static PatternGeneratorModule LastChordOn(TimelineTrack t)
        {
            if (t?.Items == null) return null;
            for (int i = t.Items.Count - 1; i >= 0; i--)
            {
                var it = t.Items[i];
                if (it.Module is PatternGeneratorModule pg) return pg;
            }
            return null;
        }

        // Chords on the chords track overlapping [startBeat, startBeat+lenBeats), as AiChords with a measure RELATIVE to the riff.
        public static System.Collections.Generic.List<Engine.AI.AiChord> ChordsUnder(TimelineProject project, double startBeat, double lenBeats, int barTemps)
        {
            var res = new System.Collections.Generic.List<Engine.AI.AiChord>();
            var ct = TimelineHelper.ChordTrack(project);
            if (ct?.Items == null) return res;
            var key = project.Key ?? new Engine.Score.KeySignature();
            double c = 0;
            foreach (var it in ct.Items)
            {
                double s = c + it.SilenceBefore, len = TimelineHelper.DispLen(it);
                c = s + len;
                if (s >= startBeat + lenBeats - 1e-6 || s + len <= startBeat + 1e-6) continue; // no overlap
                if (!(it.Module is PatternGeneratorModule pg)) continue;
                int deg = pg.Degree >= 0 ? pg.Degree : Engine.Flow.MusicTheory.DegreeOf(key, ((pg.Root % 12) + 12) % 12);
                int measure = Math.Max(1, (int)Math.Round((s - startBeat) / Math.Max(1, barTemps)) + 1);
                res.Add(new Engine.AI.AiChord { measure = measure, degree = Math.Max(1, deg + 1), quality = TimelineHelper.Get(PatternGenerator.QualityNames, pg.Quality) });
            }
            return res;
        }

        // Quarter-beats per measure for the ruler/score, from the project's time signature (6/8 → 3, 3/4 → 3).
        // Measure length in the timeline's temps (raw quarter-beats). x/8 is treated as compound (one dotted-
        // quarter temps = one raw quarter), so a bar = num/3 temps (6/8 → 2, 12/8 → 4); x/4 → num. Scale-INDEPENDENT
        // so the ruler is right even if a loaded file's TimeSigScale is stale.
        public static int RulerBeatsPerBar(TimelineProject project) => Engine.Timeline.ChordModelOps.BarTemps(project);

        // Leading anacrusis remainder (in beats) of a motif of `totalBeats`, when a levée is set and the motif isn't
        // bar-aligned (e.g. 7 in 3/4 → 1). 0 when there's no levée (so non-anacrusis projects are untouched). Used to
        // trim the lead-in off DUPLICATED motifs — melodic line, chord rhythm, chord melodic cell.
        public static int CopyLeadRem(TimelineProject project, double totalBeats) => Engine.Timeline.ChordModelOps.CopyLeadRem(project, totalBeats, RulerBeatsPerBar(project));


        public static string Get(string[] a, int i) => (a != null && i >= 0 && i < a.Length) ? a[i] : "?";

        public static readonly string[] MelodicVoiceNames = { "1 voix", "2 voix", "3 voix" };

        // The beat position where an item starts on its track (for the melodic line's harmony lookup / preview).
        public static double ItemStartBeat(TimelineTrack track, TimelineItem item)
        {
            if (track?.Items == null) return 0;
            double cur = 0;
            foreach (var it in track.Items) { cur += it.SilenceBefore; if (ReferenceEquals(it, item)) return cur; cur += Engine.Timeline.TimelineProject.ItemLength(it, TimelineHelper.RiffById); }
            return 0;
        }
        static bool IsChordModule(FlowModule m) => m is PatternGeneratorModule || m is CadenceModule;
        static bool TrackIsAllChords(TimelineTrack t)
        {
            if (t?.Items == null || t.Items.Count == 0) return false;
            foreach (var it in t.Items)
            {
                if (it == null) return false;
                else if (it.Module == null || !IsChordModule(it.Module)) return false;
            }
            return true;
        }

        // Ensure a per-section shared motif (user style named after the section). Seeded once with a CLEAN, QUANTIZED
        // default: the root held in the bass + one chord tone per beat (a light broken chord that fits any meter).
        // Editable after. All chords of the section reference it → one edit updates the section.
        public static string EnsureSectionMotif(System.Collections.Generic.List<UserChordStyle> userStyles, string name, ChordCell cell, int octave, int beats, int spq)
        {
            var existing = userStyles.Find(u => u.Name == name);
            if (existing != null && existing.Beats == beats && existing.Spb == spq && existing.Notes != null && existing.Notes.Count > 0)
                return name;   // reuse only if it MATCHES this meter's length (else re-seed — a stale style would truncate)
            int chordLen = Math.Max(1, PatternGenerator.ChordNotes(cell.Root, octave, cell.Quality, 0).Length);
            int len = beats * spq;
            var notes = new System.Collections.Generic.List<RiffNote> { new RiffNote(0, 0, len) };   // bass (voice 0) held
            for (int b = 0; b < beats; b++) notes.Add(new RiffNote(1 + (b % chordLen), b * spq, spq));  // one chord tone per beat
            var slices = RiffNotes.ToSlices(notes, len);
            if (existing != null) { existing.Slices = slices; existing.Spb = spq; existing.Beats = beats; existing.Notes = notes; }
            else userStyles.Add(new UserChordStyle { Name = name, Slices = slices, Spb = spq, Beats = beats, Notes = notes });
            return name;
        }

        // If rawBeat lies past the end of the edit track's last Riff, extend that riff by whole measures to cover it.
        public static void EnsureRiffCovers(TimelineProject project ,TimelineTrack editScoreTrack, double rawBeat)
        {
            var t = editScoreTrack; if (t?.Items == null) return;
            double cur = 0, lastStart = 0; TimelineItem last = null;
            foreach (var it in t.Items)
            {
                cur += it.SilenceBefore;
                double len = Math.Max(1e-6, DispLen(it));
                if (it.Module is PlayRiffModule) { last = it; lastStart = cur; }
                if (rawBeat >= cur - 1e-6 && rawBeat < cur + len - 1e-6) return; // already covered
                cur += len;
            }
            if (!(last?.Module is PlayRiffModule pr)) return;
            var riff = TimelineHelper.RiffById(pr.RiffId); if (riff == null) return;
            int spq = riff.SlicesPerQuarter > 0 ? riff.SlicesPerQuarter : 24;
            int barSlices = Math.Max(1, TimelineHelper.RulerBeatsPerBar(project)) * spq;
            int need = (int)Math.Ceiling((rawBeat - lastStart) * spq) + 1;      // slices needed from the riff start
            int newLen = ((need / barSlices) + 1) * barSlices;                  // round up to whole measures (+1 bar of room)
            if (newLen > riff.LengthSlices) riff.LengthSlices = newLen;
        }

        // Guarantee EXACTLY ONE chords track, pinned LAST (bottom of the timeline + score), and non-deletable. If none is
        // typed Chord yet, ADOPT the first all-chords track (keeping its instrument + name); else create a new one. Mixed
        // tracks (chords + riffs/lines) are left untouched — only a 100%-chord track is adopted.
        public static void EnsureChordTrack(TimelineProject project)
        {
            if (project?.Tracks == null) return;
            var chord = project.Tracks.Find(t => t.Type == TimelineTrackType.Chord);
            if (chord == null)
            {
                chord = project.Tracks.Find(TrackIsAllChords);
                if (chord != null) chord.Type = TimelineTrackType.Chord;
                else { chord = new TimelineTrack { Name = "Accords", Type = TimelineTrackType.Chord, Instrument = 0 }; project.Tracks.Add(chord); }
            }
            if (project.Tracks[project.Tracks.Count - 1] != chord) { project.Tracks.Remove(chord); project.Tracks.Add(chord); }
        }

        // Per-bar chord degrees from the first timeline track that carries chord objects (PatternGeneratorModule /
        // CadenceModule), looped/truncated to `bars`. null if no chord track exists.
        public static List<(int rootPc, int quality)> FindChordSource(TimelineProject project, out TimelineTrack track, int bars, int barSlices)
        {
            track = null;
            if (project?.Tracks == null) return null;
            foreach (var tr in project.Tracks)
            {
                if (tr?.Items == null) continue;
                var perBar = new List<(int, int)>();
                foreach (var it in tr.Items)
                {
                    if (it == null) continue;
                    var m = it.Module;
                    if (m is PatternGeneratorModule pg)
                        for (int k = 0; k < Math.Max(1, pg.Repeats); k++) perBar.Add((((pg.Root % 12) + 12) % 12, pg.Quality));
                    else if (m is CadenceModule cm && cm.Chords != null)
                        foreach (var c in cm.Chords) perBar.Add((((c.Root % 12) + 12) % 12, c.Quality));
                }
                if (perBar.Count > 0)
                {
                    track = tr;
                    var outl = new List<(int, int)>();
                    for (int b = 0; b < Math.Max(1, bars); b++) outl.Add(perBar[b % perBar.Count]);
                    return outl;
                }
            }
            return null;
        }

        // The first track carrying chord objects, else a track literally named for chords, else a new "Accords" track.
        public static TimelineTrack FindOrCreateChordTrack(TimelineProject project,HashSet<TimelineTrack> scoreTracks)
        {
            TimelineTrack ct;
            if (FindChordSource(project, out ct, 1, 96) != null && ct != null) return ct;
            foreach (var tr in project.Tracks)
                if (tr != null && tr.Name != null && (tr.Name.IndexOf("ccord", StringComparison.OrdinalIgnoreCase) >= 0 || tr.Name.IndexOf("ccompagn", StringComparison.OrdinalIgnoreCase) >= 0))
                    return tr;
            var t = new TimelineTrack { Name = "Accords", Type = TimelineTrackType.Instrument, Instrument = 0 };
            project.Tracks.Add(t);
            if (!scoreTracks.Contains(t)) scoreTracks.Add(t);
            return t;
        }

        // Append a new top-level item. If the track ends with a "loop to the end" Repeat, the new item is
        // inserted JUST BEFORE it (the loop must stay last) — it takes the loop's leading gap and the loop
        // then sits right after it. Otherwise it's appended at the end.
        public static void InsertTopLevel(TimelineTrack track, TimelineItem item)
        {
            track.Items.Add(item);
        }

        // "Appliquer le thème": take the riff currently edited as the canonical theme → copy it into the theme riff and
        // regenerate the DERIVED melody sections (ré-expo / développement / conclusion) + the counter-melody, transposing
        // the theme onto the chord trame of each section's bars (RefitTheme / DevelopTheme). Derived sections are locked.
        // The arrangement section a riff belongs to (by its melody/counter riff id), or null.
        public static Engine.Timeline.ArrSection SectionForRiff(TimelineProject project, Guid riffId)
        {
            var arr = project.Arrangement;
            if (arr == null || arr.Sections == null) return null;
            foreach (var s in arr.Sections) if (s.MelodyRiffId == riffId || s.CounterRiffId == riffId) return s;
            return null;
        }

        // The chord-line BACKING for a section riff = the Accompagnement + Basse notes over that section's bars, shifted
        // to slice 0 (so the riff editor can play the riff WITH its chords, clamped to the riff). Null if not a section.
        public static Riff BackingForRiff(TimelineProject project,Guid riffId)
        {
            var arr = project.Arrangement;
            var sec = SectionForRiff(project, riffId);
            if (arr == null || sec == null) return null;
            int len = sec.Bars * arr.BarSlices;
            var notes = new System.Collections.Generic.List<Engine.RiffNote>();
            foreach (var trackName in new[] { "Accompagnement", "Basse" })
            {
                TimelineTrack tr = null;
                foreach (var t in project.Tracks) if (t.Name == trackName) { tr = t; break; }
                if (tr == null) continue;
                int bar = 0;
                foreach (var item in tr.Items)
                {
                    if (item.Module is PlayRiffModule pr)
                    {
                        if (bar >= sec.StartBar && bar < sec.StartBar + sec.Bars)
                        {
                            var r = RiffById(pr.RiffId);
                            if (r != null && r.Notes != null)
                                foreach (var n in r.Notes) notes.Add(new Engine.RiffNote(n.Note, (bar - sec.StartBar) * arr.BarSlices + n.Start, n.Length));
                        }
                        bar++;
                    }
                }
            }
            return notes.Count > 0 ? new Riff { Notes = notes, LengthSlices = len, SlicesPerQuarter = arr.SlicesPerQuarter } : null;
        }

    
        public static Riff RiffById(Guid id)
        {
            foreach (var r in RiffLibrary.Instance.Riffs) if (r.Id == id) return r;
            return null;
        }

        // Insert a new leaf item right after `item` in the track (no gap).
        public static TimelineItem InsertChordAfter(TimelineTrack track, TimelineItem item, FlowModule module)
        {
            var newItem = new TimelineItem { Module = module };
            int idx = track.Items.IndexOf(item);
            if (idx < 0) track.Items.Add(newItem); else track.Items.Insert(idx + 1, newItem);
            return item;
        }

        // Apply a catalogue selection to the module. Built-in motif → set the procedural Style; note-list motif →
        // set CustomNotes; "Personnalisé" → keep/seed an editable custom pattern.
        public static void ApplyDrumCatalog(TimelineProject project, DrumPatternModule dp, string category, string motifName)
        {
            if (category == null) return;
            dp.CatCategory = category; dp.CatMotif = motifName;

            if (category == "Personnalisé")
            {
                dp.Style = DrumPattern.CustomStyle;
                if (!string.IsNullOrEmpty(motifName) && motifName != "Personnalisé")
                {
                    var u = project.UserDrumStyles.Find(x => x.Name == motifName);   // a saved project motif → load a copy
                    if (u != null)
                    {
                        int spb = u.Spb > 0 ? u.Spb : DrumPattern.SlicesPerQuarter;
                        int beats = Math.Max(1, u.Beats);
                        int oldTotalBeats = Math.Max(1, dp.BeatsPerBar) * Math.Max(1, dp.Repeats);
                        dp.BeatsPerBar = beats;
                        dp.Repeats = Math.Max(1, (int)Math.Round(oldTotalBeats / (double)beats));   // keep the total length
                        var copy = (u.Notes ?? new System.Collections.Generic.List<Engine.RiffNote>()).ConvertAll(n => new Engine.RiffNote(n.Note, n.Start, n.Length));
                        dp.SetCustomNotes(copy, spb, beats * spb);
                    }
                    return;
                }
                if (dp.CustomNotes == null || dp.CustomNotes.Count == 0)
                    dp.SetCustomNotes(new System.Collections.Generic.List<Engine.RiffNote>(), DrumPattern.SlicesPerQuarter, dp.BeatsPerBar * DrumPattern.SlicesPerQuarter);
                return;
            }
            var motif = Engine.Flow.DrumCatalog.Instance.FindPath(category + "/" + motifName);
            if (motif == null) return;
            if (motif.Builtin >= 0) { dp.Style = motif.Builtin; dp.CustomNotes = null; dp.CustomSlices = null; }
            else
            {
                dp.Style = DrumPattern.CustomStyle;
                int beats = Math.Max(1, motif.Beats);
                int oldTotalBeats = Math.Max(1, dp.BeatsPerBar) * Math.Max(1, dp.Repeats);
                dp.BeatsPerBar = beats;
                dp.Repeats = Math.Max(1, (int)Math.Round(oldTotalBeats / (double)beats));   // keep total length when switching motifs
                dp.SetCustomNotes(motif.ToNotes(), motif.Spq, motif.LengthSlices);
            }
        }

        // Apply a suggestion-dialog choice onto a chord module: diatonic → degree-locked (unless the source was absolute),
        // chromatic (secondary dominant / borrowed / Neapolitan) → an absolute chord.
        public static void ApplyChordChoice(PatternGeneratorModule pg, Engine.Score.KeySignature key, bool keepFigured, Dialogs.ChordSuggestionDialog dlg)
        {
            if (dlg.ChosenIsDiatonic)
            {
                var ch = Engine.Flow.MusicTheory.DiatonicChord(key, dlg.ChosenDegree, dlg.ChosenColour, dlg.ChosenSuspension, dlg.ChosenMode);
                pg.Root = ch.root; pg.Quality = ch.quality; pg.DiatonicColour = dlg.ChosenColour; pg.Suspension = dlg.ChosenSuspension; pg.ModeOverride = dlg.ChosenMode;
                pg.Degree = keepFigured ? dlg.ChosenDegree : -1;
            }
            else { pg.Root = dlg.ChosenRoot; pg.Quality = dlg.ChosenQuality; pg.Degree = -1; pg.DiatonicColour = 0; pg.Suspension = 0; pg.ModeOverride = 0; }
        }

        // Insert `item` (length `len` beats) in the first gap >= len AT/after the selected item; else append.
        public static void PlaceInFreeSlot(TimelineItem selectedItem, TimelineTrack track, TimelineItem item, double len)
        {
            var items = track.Items;
            int si = selectedItem != null ? items.IndexOf(selectedItem) : -1;
            if (si < 0) { TimelineHelper.InsertTopLevel(track, item); return; } // no top-level selection -> append at the end
            for (int k = si + 1; k < items.Count; k++)
            {
                double gap = items[k].SilenceBefore;
                if (gap >= len - 1e-6)
                {
                    item.SilenceBefore = 0;             // right after items[k-1] (the selected one when k = si+1)
                    items[k].SilenceBefore = gap - len; // keep the remaining silence before the following item
                    items.Insert(k, item);
                    return;
                }
            }
            TimelineHelper.InsertTopLevel(track, item); // no big-enough gap -> append (handles the loop-Repeat-last invariant)
        }

        // Visit every chord (PatternGeneratorModule) in the project, top-level and inside Repeats.
        public static void ForEachChordModule(TimelineProject project, Action<PatternGeneratorModule> action)
        {
            if (project?.Tracks == null) return;
            foreach (var tr in project.Tracks)
            {
                if (tr?.Items == null) continue;
                foreach (var item in tr.Items)
                {
                    if (item == null) continue;
                    else if (item.Module is PatternGeneratorModule pg) action(pg);
                }
            }
        }

        // "Personnaliser": copy the currently-selected motif into an editable custom pattern (replaces "Copier depuis").
        public static void CustomizeDrum(DrumPatternModule dp)
        {
            var motif = Engine.Flow.DrumCatalog.Instance.FindPath((dp.CatCategory ?? "") + "/" + (dp.CatMotif ?? ""));
            if (motif != null && motif.Builtin < 0 && motif.Notes != null && motif.Notes.Length > 0)
            {
                dp.SetCustomNotes(motif.ToNotes(), motif.Spq, motif.LengthSlices);
            }
            else
            {
                int style = (motif != null && motif.Builtin >= 0) ? motif.Builtin
                          : (dp.Style != DrumPattern.CustomStyle ? dp.Style : 0);
                var notes = DrumPattern.LaneNotesForStyle(style, dp.BeatsPerBar);
                dp.SetCustomNotes(notes, DrumPattern.SlicesPerQuarter, dp.BeatsPerBar * DrumPattern.SlicesPerQuarter);
            }
            dp.Style = DrumPattern.CustomStyle;
            dp.CatCategory = "Personnalisé"; dp.CatMotif = "Personnalisé";
        }


        // Push a user-style grid into a chord: the anchor (anacrusis / first referencing chord) keeps the full grid; every
        // other chord drops the leading anacrusis remainder (7 → 6, bar-aligned). No levée ⇒ no trim (unchanged).
        public static void SetChordStyleGrid(TimelineProject project, PatternGeneratorModule pg, bool anchor, SequencerSlice[] slices, int spb, System.Collections.Generic.List<RiffNote> notes)
        {
            int cut = anchor ? 0 : TimelineHelper.CopyLeadRem(project, slices != null ? slices.Length / (double)Math.Max(1, spb) : 0) * Math.Max(1, spb);
            pg.CustomSlices = Engine.Timeline.MotifCopy.TrimSlices(slices, cut);
            pg.CustomSlicesPerQuarter = spb;
            pg.CustomNotes = cut > 0 ? Engine.Timeline.MotifCopy.TrimNotes(notes, cut)
                                     : (notes != null ? new System.Collections.Generic.List<RiffNote>(notes) : null);
            // Bar-align the module DURATION too (parity with the melodic line): a trimmed copy is shorter by the levée
            // remainder. Idempotent — always derived from the full source grid, so re-syncs on load don't shrink further.
            if (cut > 0 && pg.CustomSlices != null && pg.CustomSlices.Length > 0)
                pg.BeatsPerBar = Math.Max(1, pg.CustomSlices.Length / Math.Max(1, spb));
        }

        // On load, make every referencing chord authoritative from its user style (in case a file saved stale caches).
        public static void SyncUserStyleRefs(TimelineProject project)
        {
            var list = project.UserChordStyles; if (list == null) return;
            var anchored = new System.Collections.Generic.HashSet<string>(); // first referencing chord per style = anacrusis anchor
            ForEachChordModule(project, pg =>
            {
                if (pg.Style != PatternGenerator.CustomStyle || string.IsNullOrEmpty(pg.UserStyleName)) return;
                var us = list.Find(u => u.Name == pg.UserStyleName);
                if (us == null || us.Slices == null) return;
                bool anchor = anchored.Add(pg.UserStyleName);
                TimelineHelper.SetChordStyleGrid(project, pg, anchor, us.Slices, us.Spb, us.Notes);
            });
        }

        // A user style is a shared REFERENCE: push its grid into every chord that points to it (by UserStyleName), so
        // editing one referencing chord updates them all. The per-chord CustomSlices is a synced cache (the renderer
        // reads it without needing the project); the user-style entry stays the source of truth.
        public static void PropagateUserStyle(TimelineProject project,string name, SequencerSlice[] slices, int spb, System.Collections.Generic.List<RiffNote> notes)
        {
            if (string.IsNullOrEmpty(name) || slices == null) return;
            bool anchorTaken = false; // the FIRST referencing chord (document order) is the anacrusis instance → keeps the full grid
            ForEachChordModule(project, pg =>
            {
                if (pg.Style == PatternGenerator.CustomStyle && pg.UserStyleName == name)
                {
                    bool anchor = !anchorTaken; anchorTaken = true;
                    SetChordStyleGrid(project, pg, anchor, slices, spb, notes);
                }
            });
        }

        // Copy a chord's MELODIC CELL (grid + octave/anchor/voicing) to every chord referencing the same user style, so
        // "Appliquer le motif" duplicates the melody across the section (each chord transposes it to its own root/degree).
        public static void PropagateMelodic(TimelineProject project,string name, PatternGeneratorModule src)
        {
            if (string.IsNullOrEmpty(name) || src == null) return;
            // Bar-align the cell copy the same way as the chord rhythm: the anchor (first referencing chord) keeps its own
            // full cell; every other chord gets the source's cell with the leading anacrusis remainder dropped (7 → 6).
            int spq = Math.Max(1, src.MelodicSlicesPerQuarter);
            int cut = CopyLeadRem(project,src.MelodicSlices != null ? src.MelodicSlices.Length / (double)spq : 0) * spq;
            var tNotes = cut > 0 ? Engine.Timeline.MotifCopy.TrimNotes(src.MelodicNotes, cut)
                                 : (src.MelodicNotes != null ? new System.Collections.Generic.List<RiffNote>(src.MelodicNotes) : null);
            var tSlices = Engine.Timeline.MotifCopy.TrimSlices(src.MelodicSlices, cut);
            bool anchorTaken = false;
            ForEachChordModule(project,pg =>
            {
                if (pg.Style != PatternGenerator.CustomStyle || pg.UserStyleName != name) return;
                bool anchor = !anchorTaken; anchorTaken = true;
                if (anchor) return;                            // the anacrusis chord keeps its own (full) cell
                if (ReferenceEquals(pg, src) || pg.MelodicPreserve) return;
                pg.MelodicNotes = tNotes != null ? new System.Collections.Generic.List<RiffNote>(tNotes) : null;
                pg.MelodicSlices = tSlices != null ? (SequencerSlice[])tSlices.Clone() : null;
                pg.MelodicSlicesPerQuarter = src.MelodicSlicesPerQuarter;
                pg.MelodicOctave = src.MelodicOctave;
                pg.MelodicAnchor = src.MelodicAnchor;
                pg.MelodicOpenVoicing = src.MelodicOpenVoicing;
                pg.MelodicVoiceLead = src.MelodicVoiceLead;
            });
        }

        // "Appliquer" a melodic line's rhythm to every MelodicLineModule of the same name, EXCEPT the ones flagged Préserver.
        public static void PropagateMelodicLine(TimelineProject project, string name, MelodicLineModule src)
        {
            if (string.IsNullOrEmpty(name) || src == null || project?.Tracks == null) return;
            // Bar-align the copy: drop the source's leading anacrusis remainder (7 → 6). The source keeps its full length.
            int rem = CopyLeadRem(project,src.BeatsPerBar);
            int cut = rem * src.SlicesPerQuarter;
            var notes = Engine.Timeline.MotifCopy.TrimNotes(src.Notes, cut);
            int lenSlices = Math.Max(1, (src.Slices != null ? src.Slices.Length : 0) - cut);
            int beats = Math.Max(1, src.BeatsPerBar - rem);
            foreach (var tr in project.Tracks)
            {
                if (tr?.Items == null) continue;
                foreach (var it in tr.Items)
                    if (it?.Module is MelodicLineModule ln && !ReferenceEquals(ln, src) && ln.LineName == name && !ln.Preserve)
                    {
                        ln.SetNotes(new System.Collections.Generic.List<RiffNote>(notes), src.SlicesPerQuarter, lenSlices);
                        ln.BeatsPerBar = beats; ln.VoiceCount = src.VoiceCount;
                    }
            }
        }

        // Report a new theme onto the derived sections with a CHOSEN variation technique. The ré-exposition and the recap
        // are the theme restated CONCLUSIVELY (a "conclusion", not a variation) — so they come from the vetted
        // RegenerateFromTheme (theme verbatim + reexpo/recap refit onto their chords). Only the DEVELOPMENT sections get
        // the user-chosen variation technique (tech = index into ArrangementEngine.VariationNames).
        public static List<(Guid riffId, List<RiffNote> notes)> PropagateThemeWithVariation(Engine.Timeline.ComposedArrangement arr, List<RiffNote> theme, int tech)
        {
            var outp = Engine.Timeline.ArrangementEngine.RegenerateFromTheme(arr, theme);
            var themeSec = arr.SectionByRole("theme");
            var themeChords = themeSec != null ? arr.SectionChords(themeSec) : new List<(int, int)>();
            var scale = MusicComposer.ScaleSet(arr.TonicPc, Engine.Score.MusicalMode.Scale(arr.FullMode));
            var rng = new Random(arr.Seed);
            var devOverride = new Dictionary<Guid, List<RiffNote>>();
            int varIdx = 0;
            foreach (var s in arr.Sections)
            {
                if (s == null || s.Protected || s.Role != "dev" || s.MelodyRiffId == Guid.Empty) continue;
                devOverride[s.MelodyRiffId] = Engine.Timeline.ArrangementEngine.ApplyVariation(tech, varIdx++, theme, themeChords,
                    arr.SectionChords(s), arr.ChordSlices, arr.BarSlices, scale, arr.TonicPc, rng);
            }
            for (int i = 0; i < outp.Count; i++)
                if (devOverride.TryGetValue(outp[i].riffId, out var v)) outp[i] = (outp[i].riffId, v);
            return outp;
        }

        // Re-point every section's MelodyRiffId/CounterRiffId at the riff that ACTUALLY carries it, by matching the
        // section's start bar to the per-section riffs on the "Mélodie" / "Contre-chant" tracks and preferring the one
        // with notes (a dialogue ré-exposition lives on the counter). Repairs arrangements generated before the
        // Orchestrateur linking fix, so "Propager" fills the ré-exposition instead of an empty, mis-linked riff.
        public static void RelinkSectionRiffs(TimelineProject project, Engine.Timeline.ComposedArrangement arr)
        {
            if (arr?.Sections == null) return;
            int bs = Math.Max(1, arr.BarSlices);
            foreach (var s in arr.Sections)
            {
                if (s == null) continue;
                var melR = TimelineHelper.SectionRiffOnTrack(project, "Mélodie", s.StartBar, bs);
                var cntR = TimelineHelper.SectionRiffOnTrack(project, "Contre-chant", s.StartBar, bs);
                bool melHas = melR != null && melR.Notes != null && melR.Notes.Count > 0;
                bool cntHas = cntR != null && cntR.Notes != null && cntR.Notes.Count > 0;
                Riff target = s.Role == "reexpo"
                    ? (cntHas ? cntR : (melHas ? melR : (cntR ?? melR)))
                    : (melHas ? melR : (melR ?? cntR));
                if (target != null) s.MelodyRiffId = target.Id;
                if (cntR != null) s.CounterRiffId = cntR.Id;
            }
        }

        // The per-section riff on a named melodic track whose cumulative start bar == startBar (riffs placed
        // consecutively, one per section; each spans LengthSlices/BarSlices bars). Null if none matches.
        public static Riff SectionRiffOnTrack(TimelineProject project, string trackName, int startBar, int barSlices)
        {
            TimelineTrack tr = null;
            foreach (var t in project.Tracks) if (t != null && t.Name == trackName) { tr = t; break; }
            if (tr?.Items == null) return null;
            int bar = 0;
            foreach (var item in tr.Items)
            {
                if (item?.Module is PlayRiffModule pr)
                {
                    var r = TimelineHelper.RiffById(pr.RiffId);
                    if (bar == startBar) return r;
                    bar += (r != null && barSlices > 0) ? Math.Max(1, r.LengthSlices / barSlices) : 1;
                }
            }
            return null;
        }

        // A plain tonic triad per bar — fallback chord context for a standalone variation with no chord track.
        public static List<(int root, int quality)> DefaultChords(int bars, int tonicPc, bool minor)
        {
            var outl = new List<(int, int)>();
            int pc = ((tonicPc % 12) + 12) % 12, q = minor ? 1 : 0;
            for (int b = 0; b < Math.Max(1, bars); b++) outl.Add((pc, q));
            return outl;
        }

        public static void InsertMelodicLine(TimelineProject project,out TimelineTrack selectedTrack,out TimelineItem selectedItem,out MelodicLineModule ml)
        {
            TimelineTrack track = null;
            foreach (var t in project.Tracks)
            {
                if (t?.Items == null) continue;
                foreach (var it in t.Items) if (it?.Module is MelodicLineModule) { track = t; break; }
                if (track != null) break;
            }
            if (track == null)
            {
                track = new TimelineTrack { Name = "Ligne (rythme)", Type = TimelineTrackType.Instrument, Instrument = 73 }; // flute
                project.Tracks.Add(track);
            }
            MelodicLineModule prev = null;
            for (int i = track.Items.Count - 1; i >= 0; i--) if (track.Items[i]?.Module is MelodicLineModule pm) { prev = pm; break; }
            // Copy the previous line's rhythm, but bar-align it: if the previous carries an anacrusis lead-in (its length
            // isn't a whole number of bars, e.g. 7 in 3/4), the NEW one drops that leading remainder (7 → 6).
            int rem = prev != null ? CopyLeadRem(project,prev.BeatsPerBar) : 0;
            int cut = prev != null ? rem * prev.SlicesPerQuarter : 0;
            int beats = prev != null ? Math.Max(1, prev.BeatsPerBar - rem) : Math.Max(1, TimelineHelper.RulerBeatsPerBar(project) * 2); // default: 2 measures
            ml = new MelodicLineModule { BeatsPerBar = beats, VoiceCount = prev?.VoiceCount ?? 1, LineName = prev?.LineName };
            if (prev?.Notes != null)
                ml.SetNotes(Engine.Timeline.MotifCopy.TrimNotes(prev.Notes, cut), prev.SlicesPerQuarter, Math.Max(1, (prev.Slices != null ? prev.Slices.Length : 0) - cut));
            var item = new TimelineItem { Module = ml };
            track.Items.Add(item);
            selectedTrack = track; selectedItem = item;
        }

        public static void ApplyAiDrum(DrumPatternModule dp, string json, int barTemps)
        {
            var opts = new System.Text.Json.JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                AllowTrailingCommas = true,
                NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString,
            };
            var rf = System.Text.Json.JsonSerializer.Deserialize<Engine.AI.AiRiff>(Engine.AI.AiArrangement.CleanJson(json), opts)
                     ?? throw new Exception("JSON vide.");
            const int dspq = 4;
            int barSlices = Math.Max(1, barTemps * dspq);
            int motifBars = rf.motifBars > 0 ? rf.motifBars : 1;
            int unitLen = motifBars * barSlices;
            int reps = rf.repeats > 0 ? rf.repeats : 1;

            var notes = new System.Collections.Generic.List<Engine.RiffNote>();
            if (rf.notes != null)
                foreach (var nn in rf.notes)
                {
                    int start = Math.Max(0, (int)Math.Round(nn.start * dspq));
                    if (start >= unitLen) continue;
                    int len = Math.Max(1, (int)Math.Round(nn.length * dspq));
                    if (start + len > unitLen) len = unitLen - start;
                    if (len < 1) continue;
                    notes.Add(new Engine.RiffNote(DrumPattern.LaneForKey(nn.pitch), start, len));
                }
            if (notes.Count == 0) throw new Exception("Aucune note de batterie dans la réponse.");

            DrumPattern.CompressPeriodic(notes, unitLen, dspq, out var u2, out int u2len, out int u2reps);
            dp.Style = DrumPattern.CustomStyle;
            dp.BeatsPerBar = Math.Max(1, u2len / dspq);
            dp.Repeats = reps * u2reps;
            dp.SetCustomNotes(u2, dspq, u2len);
        }


        // Re-style every chord that shared <paramref name="oldSection"/> (a user-style name) to match <paramref name="src"/>'s
        // new style — builtin (Style index, reference cleared) or another user style (regrouped under it). One dropdown
        // change on a section chord restyles the whole section.
        // Commit this chord's current motif to its user style AND propagate it to every chord referencing that style.
        public static void ApplyMotifToSection(TimelineProject project,PatternGeneratorModule pg)
        {
            if (pg == null || string.IsNullOrEmpty(pg.UserStyleName)) return;
            var us = project.UserChordStyles?.Find(u => u.Name == pg.UserStyleName);
            if (us != null)
            {
                us.Slices = pg.CustomSlices != null ? (SequencerSlice[])pg.CustomSlices.Clone() : us.Slices;
                us.Spb = pg.CustomSlicesPerQuarter; us.Beats = pg.BeatsPerBar;
                us.Notes = pg.CustomNotes != null ? new System.Collections.Generic.List<RiffNote>(pg.CustomNotes) : null;
            }
            TimelineHelper.PropagateUserStyle(project, pg.UserStyleName, pg.CustomSlices, pg.CustomSlicesPerQuarter, pg.CustomNotes);
            TimelineHelper.PropagateMelodic(project, pg.UserStyleName, pg);   // the MELODIC CELL follows the section too (transposes per chord)
        }

        // Load a saved melodic line (UserMelodicLines entry) into a module — its exact note-list (durations preserved),
        // resolution, length, bar-count, name, and inferred voice count.
        public static void ApplyExistingLine(MelodicLineModule ml, UserChordStyle u)
        {
            if (ml == null || u == null) return;
            var notes = u.Notes != null ? new System.Collections.Generic.List<RiffNote>(u.Notes)
                       : (u.Slices != null ? RiffNotes.FromSlices(u.Slices) : new System.Collections.Generic.List<RiffNote>());
            int spq = Math.Max(1, u.Spb);
            int lenSlices = u.Slices != null && u.Slices.Length > 0 ? u.Slices.Length : Math.Max(1, u.Beats) * spq;
            ml.SetNotes(notes, spq, lenSlices);
            ml.BeatsPerBar = Math.Max(1, u.Beats);
            ml.LineName = u.Name;
            int maxV = 0; foreach (var n in notes) maxV = Math.Max(maxV, n.Note);
            ml.VoiceCount = Math.Max(1, Math.Min(MelodicLineModule.MaxVoices, maxV + 1));
        }


        public static void ApplyAiRiff(
            TimelineProject project,
            TimelineTrack track, PlayRiffModule pr, TimelineItem editedItem, Controls.RiffGridControl rg,
            Riff riff, string json, int barTemps, int measures, bool hasChords,
            out TimelineItem riffEditItem,
            out TimelineTrack riffEditTrack,
            out bool riffDirty
            )
        {
            var opts = new System.Text.Json.JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                AllowTrailingCommas = true,
                NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString,
            };
            var res = System.Text.Json.JsonSerializer.Deserialize<Engine.AI.RiffAiResult>(Engine.AI.AiArrangement.CleanJson(json), opts)
                      ?? throw new Exception("JSON vide.");
            if (res.notes == null || res.notes.Count == 0) throw new Exception("Aucune note dans la réponse.");

            const int rspq = 24;
            int totalSlices = Math.Max(1, measures * barTemps * rspq);
            var notes = Engine.AI.AiTranslate.BuildRiffNotes(res.notes, totalSlices, rspq, barTemps * rspq, 0);
            if (notes.Count == 0) throw new Exception("Notes hors plage.");
            riff.Notes = notes; riff.LengthSlices = totalSlices; riff.SlicesPerQuarter = rspq;
            rg.Configure(riff, InstrumentCatalog.GetPreset(track.Instrument), track.Instrument);
            riffEditItem = editedItem; riffEditTrack = track; riffDirty = true;

            // No chords existed under the riff → lay the AI's progression on the chords track, aligned to the riff.
            if (!hasChords && res.chords != null && res.chords.Count > 0)
            {
                TimelineHelper.EnsureChordTrack(project);
                var ct = TimelineHelper.ChordTrack(project);
                double startBeat = TimelineHelper.ItemStartBeat(track, editedItem);
                double chordEndBefore = TimelineHelper.TrackEndBeats(ct);
                int preCount = ct.Items.Count;
                int style = Engine.AI.AiTranslate.StyleIndex(res.articulation);

                var byMeasure = new System.Collections.Generic.Dictionary<int, System.Collections.Generic.List<Engine.AI.AiChord>>();
                int lastMeasure = 1;
                foreach (var c in res.chords) { int m = Math.Max(1, c.measure); if (!byMeasure.TryGetValue(m, out var l)) { l = new System.Collections.Generic.List<Engine.AI.AiChord>(); byMeasure[m] = l; } l.Add(c); if (m > lastMeasure) lastMeasure = m; }
                PatternGeneratorModule prev = null; Engine.AI.AiChord lastSingle = null;
                for (int m = 1; m <= Math.Max(measures, lastMeasure); m++)
                {
                    byMeasure.TryGetValue(m, out var list);
                    if (list != null && list.Count > 0)
                    {
                        int k = list.Count;
                        for (int ci = 0; ci < k; ci++) { int part = Math.Max(1, barTemps / k + (ci < barTemps % k ? 1 : 0)); prev = Engine.Timeline.ChordModelOps.AddAiChord(project, TimelineHelper.RulerBeatsPerBar(project), ct, list[ci], part, style, null, 4, prev, false); lastSingle = list[ci]; }
                    }
                    else if (lastSingle != null) prev = Engine.Timeline.ChordModelOps.AddAiChord(project, TimelineHelper.RulerBeatsPerBar(project), ct, lastSingle, barTemps, style, null, 4, prev, false);
                }
                if (ct.Items.Count > preCount) ct.Items[preCount].SilenceBefore += Math.Max(0, startBeat - chordEndBefore);
                Engine.Flow.ChordDegrees.Revoice(ct);
            }
        }
        public static void GenerateDrumWithAi(Window owner, TimelineProject project,string keySummary,string metterSummary, DrumPatternModule dp, Action onApplied)
        {
            int barTemps = TimelineHelper.RulerBeatsPerBar(project);
            string ctx = $"Contexte : tonalité {keySummary} · mesure {metterSummary}.";
            var dlg = new Dialogs.AiElementDialog("Groove batterie — IA", ctx,
                intention => Engine.AI.AiArrangement.BuildDrumGroovePrompt(keySummary, metterSummary, barTemps, intention))
            { Owner = owner };
            if (dlg.ShowDialog() != true || string.IsNullOrWhiteSpace(dlg.ResultJson)) return;
            try { TimelineHelper.ApplyAiDrum(dp, dlg.ResultJson, barTemps); onApplied?.Invoke(); }
            catch (Exception ex) { MessageBox.Show("Réponse IA invalide : " + ex.Message, "Groove batterie — IA", MessageBoxButton.OK, MessageBoxImage.Warning); }

        }
        public static bool GenerateTheme(Window owner,
            TimelineProject project,
            TimelineItem selectedItem,
            TimelineTrack selectedTrack,
            HashSet<TimelineTrack> scoreTracks)
        {
            bool result = false;
            var arr = project.Arrangement;
            var selPr = selectedItem != null ? selectedItem.Module as PlayRiffModule : null;
            var sec = (arr != null && selPr != null) ? TimelineHelper.SectionForRiff(project, selPr.RiffId) : null;
            bool inStructure = arr != null;
            if (inStructure && sec == null)
            {
                MessageBox.Show("En structure, sélectionne d'abord le riff de la section à remplacer (thème, ré-exposition ou développement).",
                    "Générer un thème", MessageBoxButton.OK, MessageBoxImage.Information);
                return false;
            }
            bool isTheme = sec != null && sec.Role == "theme";

            var dlg = new GenerateThemeDialog(isTheme) { Owner = owner };
            if (dlg.ShowDialog() != true) return false;

            var key = project.Key ?? new Engine.Score.KeySignature();
            int tonicPc = Engine.Flow.MusicTheory.TonicPc(key);
            int mode = Engine.Score.MusicalMode.Effective(key);
            int[] scale = Engine.Score.MusicalMode.Scale(mode);
            bool chromatic = dlg.Technique == Engine.Compose.ProceduralComposer.ProcTechnique.Serial;
            int seed = Environment.TickCount;

            try
            {
                Mouse.OverrideCursor = Cursors.Wait;
                if (sec != null)
                {
                    int barSlices = arr.BarSlices;
                    bool ternary = arr.Ternary || arr.MeterDen == 8;
                    var chords = arr.SectionChords(sec);
                    var res = Engine.Compose.ProceduralComposer.Generate(dlg.Technique, sec.Bars, barSlices, ternary, tonicPc, scale,
                        chromatic, seed, dlg.Register.lo, dlg.Register.hi, chords);
                    var r = TimelineHelper.RiffById(sec.MelodyRiffId);
                    if (r != null) r.Notes = res.Melody;

                    if (dlg.Propagate && sec.Role == "theme")
                    {
                        arr.Theme = new List<RiffNote>(res.Melody);
                        TimelineHelper.RelinkSectionRiffs(project, arr);   // ensure each section points at the riff that actually carries it (fixes older arrangements where reexpo was mis-linked to an empty counter riff)
                        var changes = dlg.VariationTech == 0
                            ? Engine.Timeline.ArrangementEngine.RegenerateFromTheme(arr, res.Melody)
                            : TimelineHelper.PropagateThemeWithVariation(arr, res.Melody, dlg.VariationTech);
                        foreach (var ch in changes) { var rr = TimelineHelper.RiffById(ch.riffId); if (rr != null) rr.Notes = ch.notes; }
                    }
                }
                else
                {
                    int spq = 24, barSlices = TimelineHelper.RulerBeatsPerBar(project) * spq;
                    bool ternary = project.TimeSigDen == 8;
                    TimelineTrack chordTrack;
                    var chordSource = TimelineHelper.FindChordSource(project, out chordTrack, dlg.Bars, barSlices);
                    var res = Engine.Compose.ProceduralComposer.Generate(dlg.Technique, dlg.Bars, barSlices, ternary, tonicPc, scale,
                        chromatic, seed, dlg.Register.lo, dlg.Register.hi, chordSource);
                    var accomp = chordSource == null ? res.ChordAccomp : null;   // reuse existing chords, else verticalize
                    TimelineHelper.PlaceThemeAndAccomp(project, scoreTracks, selectedTrack, res.Melody, accomp, dlg.Bars, barSlices, spq, "Thème");
                }
            }
            catch (Exception ex) { MessageBox.Show("Échec de la génération : " + ex.Message, "Générer un thème", MessageBoxButton.OK, MessageBoxImage.Error); }
            finally { Mouse.OverrideCursor = null; }
            return result;
        }

        // Convert the "Cordes (nappe)" riff track into degree-locked CHORD OBJECTS — one whole-bar PLAQUÉ (tenu) chord per
        // bar of the trame — so the pad is just a chord on the indicated degree and follows the chord grid (like the
        // accompaniment). Keeps the track's instrument + (low) volume. No-op if there is no nappe track.
        public static void BuildNappeChords(TimelineProject project,HashSet<TimelineTrack> scoreTracks)
        {
            var arr = project.Arrangement;
            if (arr == null || arr.Chords == null || arr.Chords.Count == 0) return;
            var key = project.Key ?? new Engine.Score.KeySignature();
            int spq = Math.Max(1, arr.SlicesPerQuarter);
            int beatsPerBar = Math.Max(1, arr.ChordSlices / spq);
            const int octave = 5;

            int replaceIdx = -1, inst = 49; double vol = 0.35;
            for (int t = 0; t < project.Tracks.Count; t++)
            {
                var tr = project.Tracks[t];
                if (tr != null && tr.Name != null && tr.Name.IndexOf("nappe", StringComparison.OrdinalIgnoreCase) >= 0)
                { replaceIdx = t; inst = tr.Instrument; vol = tr.Volume; break; }
            }
            if (replaceIdx < 0) return;

            var track = new TimelineTrack { Name = "Cordes (nappe)", Type = TimelineTrackType.Instrument, Instrument = inst, Volume = vol };
            for (int i = 0; i < arr.Chords.Count; i++)
            {
                var cell = arr.Chords[i];
                var dc = Engine.Flow.ChordDegrees.DegColour(key, cell.Root, cell.Quality);
                track.Items.Add(new TimelineItem
                {
                    Module = new PatternGeneratorModule
                    {
                        Root = cell.Root,
                        Quality = cell.Quality,
                        Degree = dc.degree,
                        DiatonicColour = dc.colour,
                        Suspension = dc.suspension,
                        ModeOverride = dc.mode,
                        Octave = octave,
                        VoiceLeadMode = 1,
                        Style = 0 /* Accords plaqués (tenu) */,
                        BeatsPerBar = beatsPerBar,
                        Repeats = 1,
                        OpenVoicing = true,
                    }
                });
            }
            Engine.Flow.ChordDegrees.Revoice(track);
            scoreTracks.Remove(project.Tracks[replaceIdx]);
            project.Tracks[replaceIdx] = track;
            if (!scoreTracks.Contains(track)) scoreTracks.Add(track);
        }

        // Build the chord-object accompaniment from the arrangement's chord trame and SWAP it in for the riff
        // "Accompagnement" track (same row + instrument). Each chord: by degree, auto colour, auto voice-leading, and a
        // per-section shared "Personnalisé" motif (a user style named after the section, editable — one edit = section).
        public static bool BuildChordAccompaniment(TimelineProject project,HashSet<TimelineTrack> scoreTracks,out TimelineTrack selectedTrack)
        {
            selectedTrack = null;
            var arr = project.Arrangement;
            if (arr == null || arr.Chords == null || arr.Chords.Count == 0) return false;
            var key = project.Key ?? new Engine.Score.KeySignature();
            // Chord length in quarter-beats FROM THE ARRANGEMENT (follows the chosen meter: 6/8 → 3, 3/4 → 3, 4/4 → 4…),
            // so the chord objects line up with the bars instead of a fixed 4.
            int spq = Math.Max(1, arr.SlicesPerQuarter);
            int beatsPerBar = Math.Max(1, arr.ChordSlices / spq);
            const int octave = 4;
            var userStyles = project.UserChordStyles ?? (project.UserChordStyles = new System.Collections.Generic.List<UserChordStyle>());

            int replaceIdx = -1, accompInstr = 0;
            for (int t = 0; t < project.Tracks.Count; t++)
                if (project.Tracks[t] != null && project.Tracks[t].Name == "Accompagnement")
                { replaceIdx = t; accompInstr = project.Tracks[t].Instrument; break; }

            var track = new TimelineTrack { Name = "Accompagnement (accords)", Type = TimelineTrackType.Instrument, Instrument = accompInstr };
            int cpb = Math.Max(1, arr.ChordsPerBar);
            for (int i = 0; i < arr.Chords.Count; i++)
            {
                var cell = arr.Chords[i];
                var sec = SectionAtBar(arr, i / cpb);
                string secName = sec != null && !string.IsNullOrWhiteSpace(sec.Name) ? sec.Name : "Accompagnement";
                string styleName = EnsureSectionMotif(userStyles, secName, cell, octave, beatsPerBar, spq);
                var us = userStyles.Find(u => u.Name == styleName);
                var dc = Engine.Flow.ChordDegrees.DegColour(key, cell.Root, cell.Quality);
                var pg = new PatternGeneratorModule
                {
                    Root = cell.Root,
                    Quality = cell.Quality,
                    Degree = dc.degree,
                    DiatonicColour = dc.colour,
                    Suspension = dc.suspension,
                    ModeOverride = dc.mode,
                    Octave = octave,
                    VoiceLeadMode = 1,   // renversement AUTO (voice-led across the chain)
                    Style = PatternGenerator.CustomStyle,
                    UserStyleName = styleName,
                    BeatsPerBar = beatsPerBar,
                    Repeats = 1,
                };
                if (us != null) { pg.SetCustom(us.Slices, us.Spb); pg.CustomNotes = us.Notes != null ? new System.Collections.Generic.List<RiffNote>(us.Notes) : null; }
                track.Items.Add(new TimelineItem { Module = pg });
            }
            Engine.Flow.ChordDegrees.Revoice(track);   // apply the auto voice-leading now
            if (replaceIdx >= 0)
            {
                scoreTracks.Remove(project.Tracks[replaceIdx]);
                project.Tracks[replaceIdx] = track;   // swap in place (same row + instrument)
            }
            else project.Tracks.Add(track);
            if (!scoreTracks.Contains(track)) scoreTracks.Add(track);
            selectedTrack = track;
            return true;
        }

        // "Varier le thème avec l'IA" — the selected riff is the theme; the AI composes a development appended after it.
        public static bool VaryThemeWithAi(Window owner,TimelineProject project, TimelineTrack track, TimelineItem item)
        {
            bool result = false;
            if (!(item?.Module is PlayRiffModule pr)) { MessageBox.Show("Sélectionne un riff (le thème)."); return false; }
            var riff = TimelineHelper.RiffById(pr.RiffId);
            if (riff == null || riff.Notes == null || riff.Notes.Count == 0) { MessageBox.Show("Ce riff est vide."); return false; }
            var dlg = new Dialogs.AiComposeDialog { Owner = owner, ThemeContext = Engine.AI.AiArrangement.BuildThemeContext(project, track, item, riff) };
            if (dlg.ShowDialog() == true && dlg.Result != null)
            {
                try { 
                    Engine.AI.AiArrangementPlacer.Develop(project, dlg.Result, dlg.FixNotes, dlg.ChordVoice);
                    result = true;

                }
                catch (Exception ex) { MessageBox.Show("Impossible d'appliquer le développement : " + ex.Message); }
            }
            return result;
        }

        public static bool VariateTheme(Window owner,TimelineProject project, Riff src,
            PlayRiffModule pr,
            TimelineTrack selectedTrack,
            HashSet<TimelineTrack> scoreTracks,out TimelineItem timelineItem)
        {
            timelineItem = null;
            bool result = false;
            var dlg = new VariationDialog { Owner = owner };
            if (dlg.ShowDialog() != true) return false;

            var key = project.Key ?? new Engine.Score.KeySignature();
            int tonicPc = Engine.Flow.MusicTheory.TonicPc(key);
            int mode = Engine.Score.MusicalMode.Effective(key);
            bool minor = Engine.Score.MusicalMode.IsMinorish(mode);
            var scaleSet = MusicComposer.ScaleSet(tonicPc, Engine.Score.MusicalMode.Scale(mode));
            int spq = src.SlicesPerQuarter > 0 ? src.SlicesPerQuarter : 24;
            int barSlices = TimelineHelper.RulerBeatsPerBar(project) * spq, chordSlices = barSlices;

            var arr = project.Arrangement;
            var sec = arr != null ? SectionForRiff(project, pr.RiffId) : null;
            int bars = Math.Max(1, (Engine.RiffNotes.LengthOf(src.Notes) + barSlices - 1) / barSlices);

            List<(int root, int quality)> chords;
            if (sec != null) { chords = arr.SectionChords(sec); chordSlices = arr.ChordSlices; barSlices = arr.BarSlices; }
            else { TimelineTrack ct; chords = FindChordSource(project,out ct, bars, barSlices) ?? TimelineHelper.DefaultChords(bars, tonicPc, minor); }

            var theme = new List<RiffNote>(src.Notes);
            var rng = new Random(Environment.TickCount);
            List<RiffNote> varied = dlg.IsDevelop
                ? Engine.Timeline.RecipeRenderer.Develop(dlg.DevelopOp, theme, chords, scaleSet, tonicPc, chordSlices, barSlices, rng)
                : Engine.Timeline.ArrangementEngine.ApplyVariation(dlg.CatalogTech, 0, theme, chords, chords, chordSlices, barSlices, scaleSet, tonicPc, rng);
            if (varied == null || varied.Count == 0) { MessageBox.Show("La variation n'a produit aucune note.", "Variation", MessageBoxButton.OK, MessageBoxImage.Warning); return false; }

            try
            {
                Mouse.OverrideCursor = Cursors.Wait;
                if (sec != null)
                {
                    var r = TimelineHelper.RiffById(sec.MelodyRiffId);
                    if (r != null) { r.Notes = varied; r.LengthSlices = Math.Max(r.LengthSlices, Engine.RiffNotes.LengthOf(varied)); }
                }
                else
                {
                    // Standalone: new riff placed just after the original (the theme is kept). Give it chords: reuse an
                    // existing chord source if there is one, else derive a diatonic accompaniment from the variation.
                    TimelineTrack ct;
                    bool hasChordSrc = FindChordSource(project,out ct, bars, barSlices) != null;
                    var accomp = hasChordSrc ? null
                        : Engine.Compose.ProceduralComposer.DiatonicAccompaniment(varied, bars, barSlices, tonicPc, Engine.Score.MusicalMode.Scale(mode));
                    timelineItem= PlaceThemeAndAccomp(project,scoreTracks,selectedTrack, varied, accomp, bars, barSlices, spq, "Variation");
                }
                result = true;
            }
            catch (Exception ex) { MessageBox.Show("Échec de la variation : " + ex.Message, "Variation", MessageBoxButton.OK, MessageBoxImage.Error); }
            finally { Mouse.OverrideCursor = null; }
            return result;
        }

        // Place a generated melody as a new riff on the active track; if `accomp` is non-null, place it aligned on a
        // found/created chord track (SilenceBefore pads it to the melody's absolute start).
        public static TimelineItem PlaceThemeAndAccomp(
            TimelineProject project, HashSet<TimelineTrack> scoreTracks,
            TimelineTrack selectedTrack,
            List<RiffNote> melody,
            List<RiffNote> accomp,
            int bars, int barSlices, int spq, string label)
        {
            int len = bars * barSlices;
            var riff = new Riff { Name = label + " " + (RiffLibrary.Instance.Riffs.Count + 1), Notes = melody, LengthSlices = len, SlicesPerQuarter = spq };
            RiffLibrary.Instance.Riffs.Add(riff);
            double startBeats = Engine.Timeline.TimelineProject.SequenceLength(selectedTrack.Items, TimelineHelper.RiffById); // active-track fill before append
            var item = new TimelineItem { Module = new PlayRiffModule { RiffId = riff.Id } };
            InsertTopLevel(selectedTrack, item);

            if (accomp != null && accomp.Count > 0)
            {
                var accTrack = FindOrCreateChordTrack(project, scoreTracks);
                var accRiff = new Riff { Name = "Accords " + (RiffLibrary.Instance.Riffs.Count + 1), Notes = accomp, LengthSlices = len, SlicesPerQuarter = spq };
                RiffLibrary.Instance.Riffs.Add(accRiff);
                double accFill = Engine.Timeline.TimelineProject.SequenceLength(accTrack.Items, TimelineHelper.RiffById);
                var accItem = new TimelineItem { Module = new PlayRiffModule { RiffId = accRiff.Id }, SilenceBefore = Math.Max(0, startBeats - accFill) };
                InsertTopLevel(accTrack, accItem);
            }
            return item;
        }

        public static void ConvertRiffToDrums(TimelineTrack track, TimelineItem item, PlayRiffModule prm)
        {
            var riff = TimelineHelper.RiffById(prm.RiffId);
            if (riff?.Notes == null || riff.Notes.Count == 0)
            { MessageBox.Show("Ce riff est vide — rien à convertir.", "Convertir en batterie", MessageBoxButton.OK, MessageBoxImage.Information); return; }

            int spq = riff.SlicesPerQuarter > 0 ? riff.SlicesPerQuarter : 4;
            var dnotes = new System.Collections.Generic.List<Engine.RiffNote>();
            foreach (var n in riff.Notes)
                dnotes.Add(new Engine.RiffNote(DrumPattern.LaneForKey(n.Note + 12), Math.Max(0, n.Start), Math.Max(1, n.Length)));
            int len = riff.LengthSlices > 0 ? riff.LengthSlices : Engine.RiffNotes.LengthOf(riff.Notes);
            // Only keep the USEFUL length: if the phrase repeats every X beats, store one period and loop it.
            DrumPattern.CompressPeriodic(dnotes, len, spq, out var unit, out int unitLen, out int reps);
            int beats = Math.Max(1, (int)Math.Round((double)unitLen / spq));

            var dpm = new DrumPatternModule { Kit = 0, Style = DrumPattern.CustomStyle, BeatsPerBar = beats, Repeats = reps };
            dpm.SetCustomNotes(unit, spq, unitLen);   // one hit per note at its start (percussion one-shot), looped Repeats×
            item.Module = dpm;

        }

        public static void ConvertMelodicLineToRiff(TimelineProject project, TimelineTrack track, TimelineItem item, MelodicLineModule ml)
        {
            double startBeat = ItemStartBeat(track, item);
            var key = project.Key ?? new Engine.Score.KeySignature();
            var riff = Engine.Timeline.MelodicLineEngine.GenerateLine(ml, project, TimelineHelper.RiffById, key, startBeat);
            if (riff?.Notes == null || riff.Notes.Count == 0)
            { MessageBox.Show("Cette ligne est vide, ou aucun accord n'est actif à cette position — les hauteurs ne peuvent pas être figées.", "Convertir en notes éditables", MessageBoxButton.OK, MessageBoxImage.Information); return; }

            riff.Name = !string.IsNullOrWhiteSpace(ml.LineName) ? ml.LineName : ("Ligne " + (RiffLibrary.Instance.Riffs.Count + 1));
            RiffLibrary.Instance.Riffs.Add(riff);
            item.Module = new PlayRiffModule { RiffId = riff.Id };
        }





        // Minimal modal text prompt (a name input). Returns the entered text, or null if cancelled.
        public static string PromptText(string title, string initial)
        {
            var win = new Window
            {
                Title = title,
                Width = 360,
                Height = 140,
                ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = Application.Current?.MainWindow,
                Background = new SolidColorBrush(Color.FromRgb(0x24, 0x24, 0x2C))
            };
            var tb = new TextBox { Text = initial ?? "", Margin = new Thickness(14, 16, 14, 8), FontSize = 13 };
            var ok = new Button { Content = "OK", Width = 84, IsDefault = true, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(8, 3, 8, 3) };
            var cancel = new Button { Content = "Annuler", Width = 84, IsCancel = true, Padding = new Thickness(8, 3, 8, 3) };
            var btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(14, 0, 14, 12) };
            btns.Children.Add(ok); btns.Children.Add(cancel);
            var panel = new StackPanel();
            panel.Children.Add(tb); panel.Children.Add(btns);
            win.Content = panel;
            bool okd = false;
            ok.Click += (s, e) => { okd = true; win.DialogResult = true; };
            win.Loaded += (s, e) => { tb.Focus(); tb.SelectAll(); };
            win.ShowDialog();
            return okd ? tb.Text : null;
        }

    }
}
