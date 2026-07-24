using System;
using System.Collections.Generic;
using MusicTracker.Engine;
using MusicTracker.Engine.Flow;
using MusicTracker.Engine.Timeline;

namespace MusicTracker.Engine.AI
{
    /// <summary>
    /// Lays a parsed <see cref="AiArrangement"/> (Mistral) onto a <see cref="TimelineProject"/>: one chord module per bar
    /// on the Accords track (articulation style per section), melodic lines / explicit-note riffs grouped by role into
    /// instrument tracks, and drums as looped percussion modules.
    ///
    /// This is a project SOURCE: <see cref="BuildFresh"/> returns a self-contained <see cref="TimelineDocument"/> that the
    /// editor loads exactly like an opened/imported file (no in-place UI mutation). <see cref="Develop"/> is the one
    /// exception — the "Varier le thème" case — which appends onto the CURRENT project in place. Both share <see cref="Place"/>,
    /// which is pure model logic (project tracks + a riff sink); no WPF.
    /// </summary>
    public static class AiArrangementPlacer
    {
        /// <summary>Build a fresh project document from the arrangement (meter/key/bpm from the arrangement itself).</summary>
        public static TimelineDocument BuildFresh(AiArrangement a, bool fixRiffNotes, bool silentChords)
        {
            var p = new TimelineProject();
            int num = a.meter?.num ?? 4; if (num < 1) num = 4;
            int den = a.meter?.den ?? 4; if (den != 2 && den != 4 && den != 8 && den != 16) den = 4;
            p.TimeSigNum = num; p.TimeSigDen = den;
            p.Key = AiTranslate.ParseKey(a.key);
            if (a.bpm >= 20 && a.bpm <= 400 && p.Tempo != null && p.Tempo.Count > 0) p.Tempo[0].Bpm = a.bpm;

            var riffs = new List<Riff>();
            Place(p, riffs, a, fixRiffNotes, append: false, silentChords: silentChords);
            return new TimelineDocument { Project = p, Riffs = riffs };
        }

        /// <summary>"Develop the theme": append the arrangement AFTER the current project's content, in place. The new riffs
        /// go into the shared <see cref="RiffLibrary"/> (the live project keeps its existing riffs). The caller re-renders.</summary>
        public static void Develop(TimelineProject project, AiArrangement a, bool fixRiffNotes, bool silentChords)
        {
            Place(project, RiffLibrary.Instance.Riffs, a, fixRiffNotes, append: true, silentChords: silentChords);
        }

        // ---- core placement (pure model) ---------------------------------------------------------------------------

        static void Place(TimelineProject project, IList<Riff> riffSink, AiArrangement a, bool fixRiffNotes, bool append, bool silentChords)
        {
            int barTemps = ChordModelOps.BarTemps(project);
            Func<Guid, Riff> riffById = id => { foreach (var r in riffSink) if (r.Id == id) return r; return null; };

            // Develop mode: place the result AFTER the existing content, aligned to the next bar.
            double baseBeats = 0;
            if (append)
            {
                double maxEnd = 0;
                foreach (var t in project.Tracks) maxEnd = Math.Max(maxEnd, TrackEndBeats(t, riffById));
                baseBeats = (int)Math.Ceiling(maxEnd / Math.Max(1, barTemps) - 1e-6) * barTemps;
            }

            // sections → measure→section map + section→articulation style
            var secOfMeasure = new Dictionary<int, string>();
            int m = 1;
            foreach (var s in a.sections)
            {
                int len = Math.Max(1, s.measures);
                for (int k = 0; k < len; k++) secOfMeasure[m + k] = s.name;
                m += len;
            }
            // Articulation per section: a CUSTOM voiced motif (preferred) built to note-rows, else a named built-in Style.
            const int artSpq = 4; // slices per temps for the custom chord grid
            var styleOfSection = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var motifOfSection = new Dictionary<string, List<RiffNote>>(StringComparer.OrdinalIgnoreCase);
            // Each AI articulation is ALSO registered as a NAMED user style, and every chord of the section references it
            // (UserStyleName). The per-chord grid stays duplicated (as before), but the shared name makes the motif
            // editable/reusable afterwards: "Appliquer le motif" then propagates one edit — and its MELODIC CELL — to the
            // whole section, exactly like a hand-drawn cadence style.
            var nameOfSection = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var cellOfSection = new Dictionary<string, List<RiffNote>>(StringComparer.OrdinalIgnoreCase);
            var userStylesAi = project.UserChordStyles ?? (project.UserChordStyles = new List<UserChordStyle>());
            foreach (var art in a.articulation)
            {
                if (string.IsNullOrWhiteSpace(art.section)) continue;
                if (art.motif != null && art.motif.Count > 0)
                {
                    var mnotes = AiTranslate.BuildArticulationNotes(art.motif, artSpq);
                    motifOfSection[art.section] = mnotes;
                    string nm = UniqueUserStyleName(project, art.name, art.section);
                    nameOfSection[art.section] = nm;
                    int beats = Math.Max(1, barTemps);
                    userStylesAi.Add(new UserChordStyle
                    {
                        Name = nm,
                        Spb = artSpq,
                        Beats = beats,
                        Notes = new List<RiffNote>(mnotes),
                        Slices = RiffNotes.ToSlices(mnotes, Math.Max(1, beats * artSpq)),
                    });
                }
                else if (!string.IsNullOrWhiteSpace(art.style))
                    styleOfSection[art.section] = AiTranslate.StyleIndex(art.style);

                if (art.melodicCell != null && art.melodicCell.Count > 0)
                    cellOfSection[art.section] = AiTranslate.BuildMelodicCellNotes(art.melodicCell, artSpq);
            }

            // fresh timeline (replace mode); develop mode keeps existing tracks and appends
            if (!append) project.Tracks.Clear();
            ChordModelOps.EnsureChordTrackSimple(project);
            var chordTrack = ChordModelOps.ChordTrack(project);
            if (!append && a.chordInstrument >= 0 && a.chordInstrument <= 127) chordTrack.Instrument = a.chordInstrument;
            double chordEndBefore = TrackEndBeats(chordTrack, riffById);
            int chordPreCount = chordTrack.Items.Count;

            // chords → one module per bar (holding the last degree across bars with no new chord; a bar with several
            // chords is split evenly among them).
            var byMeasure = new Dictionary<int, List<AiChord>>();
            int lastMeasure = 0;
            foreach (var c in a.chords)
            {
                int meas = Math.Max(1, c.measure);
                if (!byMeasure.TryGetValue(meas, out var list)) { list = new List<AiChord>(); byMeasure[meas] = list; }
                list.Add(c);
                if (meas > lastMeasure) lastMeasure = meas;
            }
            int totalMeasures = Math.Max(lastMeasure, secOfMeasure.Count > 0 ? Max(secOfMeasure.Keys) : 0);
            PatternGeneratorModule prevPg = null; AiChord lastSingle = null;
            var chordAtMeasure = new AiChord[totalMeasures + 1]; // effective chord per bar (for riff harmony fix)
            for (int meas = 1; meas <= totalMeasures; meas++)
            {
                byMeasure.TryGetValue(meas, out var list);
                chordAtMeasure[meas] = (list != null && list.Count > 0) ? list[0] : lastSingle;
                string sec = secOfMeasure.TryGetValue(meas, out var sn) ? sn : null;
                int style = (sec != null && styleOfSection.TryGetValue(sec, out int st)) ? st : -1;
                List<RiffNote> motif = (sec != null && motifOfSection.TryGetValue(sec, out var mo)) ? mo : null;
                string artName = (sec != null && nameOfSection.TryGetValue(sec, out var an)) ? an : null;
                List<RiffNote> cell = (sec != null && cellOfSection.TryGetValue(sec, out var ce)) ? ce : null;
                if (list != null && list.Count > 0)
                {
                    int k = list.Count;
                    for (int ci = 0; ci < k; ci++)
                    {
                        int part = Math.Max(1, barTemps / k + (ci < barTemps % k ? 1 : 0));
                        prevPg = ChordModelOps.AddAiChord(project, barTemps, chordTrack, list[ci], part, style, motif, artSpq, prevPg, silentChords, artName, cell);
                        lastSingle = list[ci];
                    }
                }
                else if (lastSingle != null)
                    prevPg = ChordModelOps.AddAiChord(project, barTemps, chordTrack, lastSingle, barTemps, style, motif, artSpq, prevPg, silentChords, artName, cell);
            }
            // Develop mode: shift the newly-appended chords so they start at baseBeats (a gap on the first new chord).
            if (append && chordTrack.Items.Count > chordPreCount)
                chordTrack.Items[chordPreCount].SilenceBefore += Math.Max(0, baseBeats - chordEndBefore);

            // melodic lines → grouped by role into one instrument track each
            const int spq = 4;
            var groups = new Dictionary<string, List<AiMelodicLine>>(StringComparer.OrdinalIgnoreCase);
            var order = new List<string>();
            foreach (var line in a.melodicLines)
            {
                string role = string.IsNullOrWhiteSpace(line.track) ? "Mélodie" : line.track.Trim();
                if (!groups.TryGetValue(role, out var list)) { list = new List<AiMelodicLine>(); groups[role] = list; order.Add(role); }
                list.Add(line);
            }
            foreach (var role in order)
            {
                var lines = groups[role];
                lines.Sort((p, q) => p.fromMeasure.CompareTo(q.fromMeasure));
                int instr = 73; foreach (var l in lines) if (l.instrument >= 0 && l.instrument <= 127) { instr = l.instrument; break; }
                var track = GetOrCreateInstrTrack(project, role, instr, append);
                double cursor = TrackEndBeats(track, riffById);
                foreach (var line in lines)
                {
                    double lineStart = baseBeats + (Math.Max(1, line.fromMeasure) - 1) * barTemps;
                    int lineBars = Math.Max(1, line.measures);
                    int contour = AiTranslate.ContourIndex(line.contour), anchor = AiTranslate.AnchorIndex(line.anchor);
                    for (int b = 0; b < lineBars; b += 4)   // lay in 4-bar blocks (easier to edit than one per section)
                    {
                        int cb = Math.Min(4, lineBars - b);
                        int cBeats = cb * barTemps, cSlices = cBeats * spq;
                        double startBeat = lineStart + b * barTemps;
                        var ml = new MelodicLineModule { BeatsPerBar = cBeats, VoiceCount = 1, Contour = contour, Anchor = anchor, RegisterShift = line.register };
                        ml.SetNotes(AiTranslate.BuildRhythmNotes(line.durations, cSlices, spq), spq, cSlices);
                        track.Items.Add(new TimelineItem { Module = ml, SilenceBefore = Math.Max(0, startBeat - cursor) });
                        cursor = startBeat + cBeats;
                    }
                }
            }

            // riffs (explicit-note mode) → grouped by role into one instrument track each, as Riff objects.
            if (a.riffs != null && a.riffs.Count > 0)
            {
                const int rspq = 24; // matches the app's timeline riffs (1 temps = SlicesPerQuarter slices)
                int[] scalePcs = AiTranslate.ScalePcs(project.Key);
                var rgroups = new Dictionary<string, List<AiRiff>>(StringComparer.OrdinalIgnoreCase);
                var rorder = new List<string>();
                foreach (var rf in a.riffs)
                {
                    string role = string.IsNullOrWhiteSpace(rf.track) ? "Mélodie" : rf.track.Trim();
                    if (!rgroups.TryGetValue(role, out var list)) { list = new List<AiRiff>(); rgroups[role] = list; rorder.Add(role); }
                    list.Add(rf);
                }
                foreach (var role in rorder)
                {
                    var list = rgroups[role];
                    list.Sort((p, q) => p.fromMeasure.CompareTo(q.fromMeasure));
                    int instr = 73; foreach (var l in list) if (l.instrument >= 0 && l.instrument <= 127) { instr = l.instrument; break; }
                    var track = GetOrCreateInstrTrack(project, role, instr, append);
                    double cursor = TrackEndBeats(track, riffById);
                    foreach (var rf in list)
                    {
                        int relBeat = (Math.Max(1, rf.fromMeasure) - 1) * barTemps;   // dev-relative (for harmony lookup)
                        double rfStart = baseBeats + relBeat;                          // absolute (for placement)
                        int rfBars = Math.Max(1, rf.measures);
                        int lenSlices = rfBars * barTemps * rspq;
                        // Some models give ABSOLUTE note starts (from the piece start) instead of relative to the riff.
                        // Detect: if the earliest note is at/after this riff's bar offset, subtract it to make them relative.
                        double sub = 0;
                        if (relBeat > 0 && rf.notes != null && rf.notes.Count > 0)
                        {
                            double minStart = double.MaxValue;
                            foreach (var nn in rf.notes) if (nn.start < minStart) minStart = nn.start;
                            if (minStart >= relBeat - 0.5) sub = relBeat;
                        }
                        // Build the whole riff once, correct harmony, then cut it into 4-bar blocks (easier to edit).
                        var full = AiTranslate.BuildRiffNotes(rf.notes, lenSlices, rspq, barTemps * rspq, sub);
                        if (fixRiffNotes) CorrectRiffHarmony(project, full, relBeat, barTemps, rspq, chordAtMeasure, scalePcs);
                        int barSlices = barTemps * rspq;
                        for (int b = 0; b < rfBars; b += 4)
                        {
                            int cb = Math.Min(4, rfBars - b);
                            int blockStart = b * barSlices, blockLen = cb * barSlices;
                            var bn = new List<RiffNote>();
                            foreach (var n in full)
                            {
                                if (n.Start < blockStart || n.Start >= blockStart + blockLen) continue;
                                int ns = n.Start - blockStart;
                                int nl = Math.Min(n.Length, blockLen - ns);
                                if (nl >= 1) bn.Add(new RiffNote(n.Note, ns, nl));
                            }
                            var riff = new Riff
                            {
                                Name = role + " " + (rf.section ?? ""),
                                SlicesPerQuarter = rspq,
                                LengthSlices = blockLen,
                                Notes = bn,
                            };
                            riffSink.Add(riff);
                            double startBeat = rfStart + b * barTemps;
                            track.Items.Add(new TimelineItem { Module = new PlayRiffModule { RiffId = riff.Id }, SilenceBefore = Math.Max(0, startBeat - cursor) });
                            cursor = startBeat + cb * barTemps;
                        }
                    }
                }
            }

            // drums → one dedicated drum-kit track, each section a DRUM MODULE holding an explicit percussion
            // phrase (Note = drum lane; edited later in the riff-like rhythm editor). 16th-note grid keeps it light.
            if (a.drums != null && a.drums.Count > 0)
            {
                const int dspq = 4;
                var dtrack = GetOrCreateDrumTrack(project, append);
                var dlist = new List<AiRiff>(a.drums);
                dlist.Sort((p, q) => p.fromMeasure.CompareTo(q.fromMeasure));
                double cursor = TrackEndBeats(dtrack, riffById);
                foreach (var rf in dlist)
                {
                    int relBeat = (Math.Max(1, rf.fromMeasure) - 1) * barTemps;
                    double startBeat = baseBeats + relBeat;
                    int totalBeats = Math.Max(1, rf.measures) * barTemps;
                    int lenSlices = totalBeats * dspq;
                    double sub = 0;
                    if (relBeat > 0 && rf.notes != null && rf.notes.Count > 0)
                    {
                        double minStart = double.MaxValue;
                        foreach (var nn in rf.notes) if (nn.start < minStart) minStart = nn.start;
                        if (minStart >= relBeat - 0.5) sub = relBeat;
                    }
                    var dnotes = new List<RiffNote>();
                    if (rf.notes != null)
                        foreach (var nn in rf.notes)
                        {
                            int start = Math.Max(0, (int)Math.Round((nn.start - sub) * dspq));
                            if (start >= lenSlices) continue;
                            int len = Math.Max(1, (int)Math.Round(nn.length * dspq));
                            if (start + len > lenSlices) len = lenSlices - start;
                            if (len < 1) continue;
                            dnotes.Add(new RiffNote(DrumPattern.LaneForKey(nn.pitch), start, len)); // GM key → lane
                        }
                    // A drum groove is ONE short motif looped, not notes filling the whole section. Prefer the model's
                    // declared motif length + repeat count ('motifBars'/'repeats'); else detect the motif's real length
                    // (rounded up to whole bars) and loop it to fill the section. Store the UNIT + Repeats (no dup).
                    int barSlices = Math.Max(1, barTemps * dspq);
                    int sectionBars = Math.Max(1, rf.measures);
                    int unitLen, reps;
                    if (rf.motifBars > 0)
                    {
                        unitLen = rf.motifBars * barSlices;
                        reps = rf.repeats > 0 ? rf.repeats : Math.Max(1, sectionBars / Math.Max(1, rf.motifBars));
                    }
                    else
                    {
                        int maxEnd = 0; foreach (var n in dnotes) maxEnd = Math.Max(maxEnd, n.Start + n.Length);
                        int contentBars = Math.Max(1, (int)Math.Ceiling((double)maxEnd / barSlices));
                        unitLen = contentBars * barSlices; reps = 1;
                        if (unitLen < lenSlices && lenSlices % unitLen == 0) reps = lenSlices / unitLen;
                        else unitLen = lenSlices;   // content fills the section (or doesn't divide evenly) — one unit
                    }
                    var unitNotes = new List<RiffNote>();
                    foreach (var n in dnotes) if (n.Start < unitLen) unitNotes.Add(new RiffNote(n.Note, n.Start, Math.Min(n.Length, unitLen - n.Start)));
                    DrumPattern.CompressPeriodic(unitNotes, unitLen, dspq, out var u2, out int u2len, out int u2reps);

                    // Lay the groove in 4-bar blocks (like the melody/riffs) when the unit aligns on a bar; else one module.
                    bool canSplit = u2len > 0 && barSlices % u2len == 0 && sectionBars > 4;
                    if (!canSplit)
                    {
                        var dpm = new DrumPatternModule { Kit = 0, Style = DrumPattern.CustomStyle, BeatsPerBar = Math.Max(1, u2len / dspq), Repeats = reps * u2reps };
                        dpm.SetCustomNotes(u2, dspq, u2len);
                        dtrack.Items.Add(new TimelineItem { Module = dpm, SilenceBefore = Math.Max(0, startBeat - cursor) });
                        cursor = startBeat + totalBeats;
                    }
                    else
                    {
                        int perBar = barSlices / u2len;   // unit repeats per bar
                        for (int b = 0; b < sectionBars; b += 4)
                        {
                            int cb = Math.Min(4, sectionBars - b);
                            var dpm = new DrumPatternModule { Kit = 0, Style = DrumPattern.CustomStyle, BeatsPerBar = Math.Max(1, u2len / dspq), Repeats = perBar * cb };
                            dpm.SetCustomNotes(u2, dspq, u2len);
                            double bStart = startBeat + b * barTemps;
                            dtrack.Items.Add(new TimelineItem { Module = dpm, SilenceBefore = Math.Max(0, bStart - cursor) });
                            cursor = bStart + cb * barTemps;
                        }
                    }
                }
            }

            ChordModelOps.EnsureChordTrackSimple(project);  // re-pin the chords track at the bottom
        }

        // ---- model helpers (self-contained copies of the pure ones TimelineScreen also uses) -------------------------


        // Reuse the existing drum track (develop/append), else create the dedicated GM-kit drum track.
        static TimelineTrack GetOrCreateDrumTrack(TimelineProject project, bool reuse)
        {
            if (reuse)
            {
                var ex = project.Tracks.Find(x => x != null && x.Type == TimelineTrackType.Drum);
                if (ex != null) return ex;
            }
            var t = new TimelineTrack { Name = "Batterie", Type = TimelineTrackType.Drum, Instrument = InstrumentCatalog.DrumIndex };
            project.Tracks.Add(t);
            return t;
        }

        // Reuse an existing instrument track with this role name (develop/append mode), else create a new one.
        static TimelineTrack GetOrCreateInstrTrack(TimelineProject project, string role, int instr, bool reuse)
        {
            if (reuse)
            {
                var ex = project.Tracks.Find(x => x != null && x.Type == TimelineTrackType.Instrument && string.Equals(x.Name, role, StringComparison.OrdinalIgnoreCase));
                if (ex != null) return ex;
            }
            var t = new TimelineTrack { Name = role, Type = TimelineTrackType.Instrument, Instrument = instr };
            project.Tracks.Add(t);
            return t;
        }

        // Total beats occupied by a track (silences + item lengths) — the append cursor for "develop the theme".
        static double TrackEndBeats(TimelineTrack t, Func<Guid, Riff> riffById)
        {
            if (t?.Items == null) return 0;
            double cur = 0;
            foreach (var it in t.Items) cur += it.SilenceBefore + TimelineProject.ItemLength(it, riffById);
            return cur;
        }

        // Snap out-of-harmony riff notes: a note ON a beat must be a CHORD tone; off-beat notes must be in the SCALE. Only
        // notes already outside the target set move (to the nearest tone), so in-key material is left intact.
        static void CorrectRiffHarmony(TimelineProject project, List<RiffNote> notes, int riffStartBeat, int barTemps, int rspq,
            AiChord[] chordAtMeasure, int[] scalePcs)
        {
            if (notes == null) return;
            for (int i = 0; i < notes.Count; i++)
            {
                var n = notes[i];
                double absBeat = (riffStartBeat * rspq + n.Start) / (double)rspq;
                int meas = (int)Math.Floor(absBeat / barTemps) + 1;                 // 1-based bar
                if (meas < 1 || meas >= chordAtMeasure.Length || chordAtMeasure[meas] == null) continue;
                double beatInBar = absBeat - (meas - 1) * barTemps;
                bool onBeat = Math.Abs(beatInBar - Math.Round(beatInBar)) < 0.06;
                int[] target = onBeat ? AiTranslate.ChordPcs(project.Key, chordAtMeasure[meas].degree, chordAtMeasure[meas].quality) : scalePcs;
                int midi = n.Note + 12;
                int pc = ((midi % 12) + 12) % 12;
                bool inTarget = false; foreach (int t in target) if (t == pc) { inTarget = true; break; }
                if (inTarget) continue;
                int fixedMidi = AiTranslate.SnapMidiToPcs(midi, target);
                n.Note = Math.Max(0, Math.Min(95, fixedMidi - 12));
                notes[i] = n;
            }
        }

        // A unique, file-safe name for an AI articulation registered as a user chord style (deduped against existing ones).
        static string UniqueUserStyleName(TimelineProject project, string preferred, string section)
        {
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (project.UserChordStyles != null) foreach (var u in project.UserChordStyles) if (u?.Name != null) used.Add(u.Name);

            string b = !string.IsNullOrWhiteSpace(preferred) ? preferred : section;
            var sb = new System.Text.StringBuilder();
            foreach (char c in (b ?? "").Trim()) sb.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '_');
            string base_ = sb.ToString().Trim('_');
            if (base_.Length == 0) base_ = "ia";
            if (base_.Length > 24) base_ = base_.Substring(0, 24);

            if (!used.Contains(base_)) return base_;
            for (int n = 2; ; n++) { string nm = base_ + "_" + n; if (!used.Contains(nm)) return nm; }
        }

        static int Max(IEnumerable<int> xs) { int mx = 0; foreach (var x in xs) if (x > mx) mx = x; return mx; }
    }
}
