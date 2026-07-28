using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using MusicTracker.Engine.Score;
using MusicTracker.Localization;

namespace MusicTracker.Engine.Timeline
{
    /// <summary>
    /// Writes the score (one or more <see cref="TrackScore"/> parts) to a MusicXML 4.0 <c>score-partwise</c> file —
    /// the interchange format every notation program reads. Works in DISPLAY-beat space (quarter units, ×TimeSigScale
    /// already applied by <see cref="ScoreBuilder"/>) so a compound 6/8 exports as 6/8; one part = one staff, with its
    /// own clef, its WRITTEN key signature and the instrument transposition declared.
    ///
    /// Unlike <see cref="MuseScoreExporter"/>, a note running past the bar line is TIED over it instead of being
    /// truncated, and a length that is not a standard figure (five eighths…) becomes a chain of TIED figures instead
    /// of several re-attacks. The other limits are the same as the .mscx, on purpose: durations snap to the 1/32
    /// (3-unit) grid so triplets are approximated (no tuplet brackets), simultaneous notes of one track merge into
    /// chords (a single voice per staff), the pickup (<c>PickupBeats</c>) is not applied so bar lines start at beat 0,
    /// and the extra "melodic" staff the score view adds for a chord track is not exported.
    ///
    /// The pitch spelling (<see cref="Tpc"/>) and the figure table are duplicated from <see cref="MuseScoreExporter"/>
    /// rather than shared: touching that file would risk a regression on an existing export. The day the .mscx export
    /// gains ties, the two are to be merged.
    /// </summary>
    public static class MusicXmlExporter
    {
        public struct Part { public string Name; public int Program; public TrackScore Score; }

        const int U = 24; // integer units per quarter note = <divisions> (LCM of the 1/8 and 1/6 display grids)

        // Standard note values in units, largest first, with dot count. 3 (=1/32) is the floor so any multiple of 3
        // decomposes exactly. The names are literally the MusicXML <type> vocabulary.
        static readonly (int u, string type, int dots)[] Vals =
        {
            (96, "whole", 0), (72, "half", 1), (48, "half", 0), (36, "quarter", 1), (24, "quarter", 0),
            (18, "eighth", 1), (12, "eighth", 0), (9, "16th", 1), (6, "16th", 0), (3, "32nd", 0),
        };

        /// <summary>One sounding event of a staff: an onset, its written pitches (a chord) and where it stops —
        /// either its own length or the next onset, whichever comes first (the staff is monophonic).</summary>
        struct Ev { public int Start, End; public List<int> Pitches; public bool Arp; }

        /// <param name="timeSigScale">display/real beat ratio (the ×1.5 of a ternary 4/4 shown as 12/8)</param>
        /// <param name="bpm">the piece's initial tempo, in REAL quarters per minute</param>
        public static void Export(string path, List<Part> parts, int num, int den, double timeSigScale, double bpm, string title)
        {
            if (parts == null) parts = new List<Part>();
            int sigN = Math.Max(1, num), sigD = den == 8 ? 8 : 4;
            int barUnits = sigN * 96 / sigD;                              // 4/4 = 96, 3/4 = 72, 6/8 = 72, 12/8 = 144
            int beatUnits = (sigD == 8 && sigN % 3 == 0) ? 36 : 24;       // the felt beat: dotted quarter when compound

            // Flatten every part first: the measure count is the longest of them all, so short staves get padded with
            // whole-bar rests and all systems stay aligned.
            var events = new List<List<Ev>>();
            int measures = 1;
            foreach (var p in parts)
            {
                var ts = p.Score ?? new TrackScore();
                var evs = BuildEvents(ts);
                events.Add(evs);
                int endU = (int)Math.Round(ts.TotalBeats * U);
                foreach (var ev in evs) endU = Math.Max(endU, ev.End);    // a note may run past TotalBeats: keep it
                measures = Math.Max(measures, Math.Max(1, (endU + barUnits - 1) / barUnits));
            }
            measures = Math.Min(measures, 20000);                         // an aberrant length must not hang the app

            var sb = new StringBuilder();
            sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
            sb.AppendLine("<!DOCTYPE score-partwise PUBLIC \"-//Recordare//DTD MusicXML 4.0 Partwise//EN\" \"http://www.musicxml.org/dtds/partwise.dtd\">");
            sb.AppendLine("<score-partwise version=\"4.0\">");
            if (!string.IsNullOrWhiteSpace(title))
                sb.AppendLine($"  <work><work-title>{Esc(title)}</work-title></work>");
            sb.AppendLine("  <identification>");
            sb.AppendLine("    <encoding>");
            sb.AppendLine("      <software>Koton Studio</software>");
            sb.AppendLine($"      <encoding-date>{DateTime.Now:yyyy-MM-dd}</encoding-date>");
            sb.AppendLine("    </encoding>");
            sb.AppendLine("  </identification>");

            sb.AppendLine("  <part-list>");
            for (int i = 0; i < parts.Count; i++)
            {
                string id = "P" + (i + 1);
                string name = string.IsNullOrEmpty(parts[i].Name) ? Loc.T("Voix2") + (i + 1) : parts[i].Name;
                int channel = 1 + (i % 15);
                if (channel >= 10) channel++;                             // channel 10 is GM percussion: never used here
                sb.AppendLine($"    <score-part id=\"{id}\">");
                sb.AppendLine($"      <part-name>{Esc(name)}</part-name>");
                sb.AppendLine($"      <score-instrument id=\"{id}-I1\"><instrument-name>{Esc(name)}</instrument-name></score-instrument>");
                sb.AppendLine($"      <midi-instrument id=\"{id}-I1\">");
                sb.AppendLine($"        <midi-channel>{channel}</midi-channel>");
                sb.AppendLine($"        <midi-program>{Math.Max(1, Math.Min(128, parts[i].Program + 1))}</midi-program>"); // MusicXML programs are 1-based
                sb.AppendLine("      </midi-instrument>");
                sb.AppendLine("    </score-part>");
            }
            sb.AppendLine("  </part-list>");

            for (int i = 0; i < parts.Count; i++)
            {
                sb.AppendLine($"  <part id=\"P{i + 1}\">");
                WritePart(sb, parts[i].Score ?? new TrackScore(), events[i], measures, barUnits, beatUnits, sigN, sigD,
                          i == 0 ? (int)Math.Round(bpm * (timeSigScale > 0 ? timeSigScale : 1.0)) : 0);
                sb.AppendLine("  </part>");
            }

            sb.AppendLine("</score-partwise>");
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        }

        /// <summary>Merge the notes into onset-grouped chords (written pitch, 1/32 grid) then close each one at the
        /// next onset, which makes the staff strictly monophonic — measures then fill exactly.</summary>
        static List<Ev> BuildEvents(TrackScore ts)
        {
            var byStart = new SortedDictionary<int, (int len, List<int> pitches, bool arp)>();
            foreach (var n in ts.Notes)
            {
                int start = R3(n.StartBeat);
                int len = Math.Max(3, R3(n.Beats));
                int written = n.Midi + ts.Transpose;
                if (written < 0 || written > 127) continue;
                if (byStart.TryGetValue(start, out var e)) { e.len = Math.Max(e.len, len); e.pitches.Add(written); e.arp |= n.Arpeggio; byStart[start] = e; }
                else byStart[start] = (len, new List<int> { written }, n.Arpeggio);
            }

            var starts = new List<int>(byStart.Keys);
            var evs = new List<Ev>();
            for (int i = 0; i < starts.Count; i++)
            {
                var e = byStart[starts[i]];
                int end = starts[i] + e.len;
                if (i + 1 < starts.Count) end = Math.Min(end, starts[i + 1]);   // onsets are ≥ 3 units apart: end > start
                evs.Add(new Ev { Start = starts[i], End = end, Pitches = e.pitches, Arp = e.arp });
            }
            return evs;
        }

        static void WritePart(StringBuilder sb, TrackScore ts, List<Ev> evs, int measures, int barUnits, int beatUnits,
                              int sigN, int sigD, int tempo)
        {
            var dk = KeySig.Derive(ts.Key ?? new KeySignature(), ts.Transpose);
            int fifths = dk.Flats ? -dk.Count : dk.Count;
            // Line-of-fifths position of the actual TONIC (a minor tonic sits 3 fifths above its key signature), so a
            // minor leading tone is spelled as a sharp (C# in d-minor) and not as an enharmonic flat.
            int spellCenter = fifths + ((ts.Key != null && ts.Key.Mode == 1) ? 3 : 0);

            int ei = 0;
            for (int m = 0; m < measures; m++)
            {
                int ms = m * barUnits, me = ms + barUnits;
                sb.AppendLine($"    <measure number=\"{m + 1}\">");
                if (m == 0)
                {
                    sb.AppendLine("      <attributes>");
                    sb.AppendLine($"        <divisions>{U}</divisions>");
                    sb.AppendLine($"        <key><fifths>{fifths}</fifths><mode>{((ts.Key != null && ts.Key.Mode == 1) ? "minor" : "major")}</mode></key>");
                    sb.AppendLine($"        <time><beats>{sigN}</beats><beat-type>{sigD}</beat-type></time>");
                    string sign, line;
                    ClefCode(ts.Clef, out sign, out line);
                    sb.AppendLine($"        <clef><sign>{sign}</sign><line>{line}</line></clef>");
                    if (ts.Transpose != 0)
                    {
                        // TrackScore.Transpose goes sounding → written; MusicXML <transpose> goes the other way.
                        int chromatic = -ts.Transpose;
                        int diatonic = -(int)Math.Round(ts.Transpose * 7.0 / 12.0);
                        sb.AppendLine($"        <transpose><diatonic>{diatonic}</diatonic><chromatic>{chromatic}</chromatic></transpose>");
                    }
                    sb.AppendLine("      </attributes>");
                    if (tempo > 0)
                    {
                        sb.AppendLine("      <direction placement=\"above\">");
                        sb.AppendLine("        <direction-type>");
                        sb.AppendLine($"          <metronome><beat-unit>quarter</beat-unit><per-minute>{tempo}</per-minute></metronome>");
                        sb.AppendLine("        </direction-type>");
                        sb.AppendLine($"        <sound tempo=\"{tempo}\"/>");
                        sb.AppendLine("      </direction>");
                    }
                }

                int pos = ms;
                while (ei < evs.Count && evs[ei].Start < me)
                {
                    var ev = evs[ei];
                    if (ev.End <= ms) { ei++; continue; }                 // fully consumed by a previous measure
                    if (ev.Start > pos) EmitRests(sb, pos - ms, ev.Start - pos, beatUnits);
                    int segStart = Math.Max(ev.Start, ms), segEnd = Math.Min(ev.End, me);
                    EmitChordSegment(sb, ev.Pitches, segStart - ms, segEnd - segStart, beatUnits,
                                     ev.Start < ms,          // continues a segment started in an earlier measure
                                     ev.End > me,             // will continue in the next measure
                                     ev.Arp && ev.Start >= ms, // the roll mark belongs to the attack only
                                     spellCenter, dk);
                    pos = segEnd;
                    if (ev.End > me) break;                              // resumes at the next measure, same event
                    ei++;
                }
                if (pos == ms) EmitMeasureRest(sb, barUnits);
                else if (pos < me) EmitRests(sb, pos - ms, me - pos, beatUnits);

                sb.AppendLine("    </measure>");
            }
        }

        /// <summary>Write one chord segment: its length is split into standard figures, each figure tied to the next
        /// (plus the ties inherited from / handed to the neighbouring measures).</summary>
        static void EmitChordSegment(StringBuilder sb, List<int> pitches, int pos, int len, int beatUnits,
                                     bool tieStop, bool tieStart, bool arp, int spellCenter, DerivedKey dk)
        {
            var figs = Split(pos, len, beatUnits);
            for (int i = 0; i < figs.Count; i++)
            {
                bool stop = i > 0 || tieStop;
                bool start = i < figs.Count - 1 || tieStart;
                for (int p = 0; p < pitches.Count; p++)
                    EmitNote(sb, pitches[p], p > 0, figs[i], stop, start, arp && i == 0, spellCenter, dk);
            }
        }

        static void EmitNote(StringBuilder sb, int midi, bool chord, (int u, string type, int dots) fig,
                             bool tieStop, bool tieStart, bool arp, int spellCenter, DerivedKey dk)
        {
            string step; int alter, octave;
            SpellPitch(midi, spellCenter, out step, out alter, out octave);
            sb.AppendLine("      <note>");
            if (chord) sb.AppendLine("        <chord/>");
            sb.AppendLine("        <pitch>");
            sb.AppendLine($"          <step>{step}</step>");
            if (alter != 0) sb.AppendLine($"          <alter>{alter}</alter>");
            sb.AppendLine($"          <octave>{octave}</octave>");
            sb.AppendLine("        </pitch>");
            sb.AppendLine($"        <duration>{fig.u}</duration>");
            // <tie> is the sounding tie (before <voice>), <tied> below is the drawn slur: MusicXML wants both.
            if (tieStop) sb.AppendLine("        <tie type=\"stop\"/>");
            if (tieStart) sb.AppendLine("        <tie type=\"start\"/>");
            sb.AppendLine("        <voice>1</voice>");
            sb.AppendLine($"        <type>{fig.type}</type>");
            for (int d = 0; d < fig.dots; d++) sb.AppendLine("        <dot/>");
            int letter = "CDEFGAB".IndexOf(step, StringComparison.Ordinal);
            // Only where it differs from the armure — and never on the continuation of a tie, which is not a new
            // attack (a note tied over a bar line does not repeat its accidental).
            if (!tieStop && letter >= 0 && alter != dk.Acc[letter])
                sb.AppendLine($"        <accidental>{(alter > 0 ? "sharp" : alter < 0 ? "flat" : "natural")}</accidental>");
            if (tieStop || tieStart || arp)
            {
                sb.AppendLine("        <notations>");
                if (tieStop) sb.AppendLine("          <tied type=\"stop\"/>");
                if (tieStart) sb.AppendLine("          <tied type=\"start\"/>");
                if (arp) sb.AppendLine("          <arpeggiate/>");
                sb.AppendLine("        </notations>");
            }
            sb.AppendLine("      </note>");
        }

        static void EmitRests(StringBuilder sb, int pos, int len, int beatUnits)
        {
            foreach (var fig in Split(pos, len, beatUnits))
            {
                sb.AppendLine("      <note>");
                sb.AppendLine("        <rest/>");
                sb.AppendLine($"        <duration>{fig.u}</duration>");
                sb.AppendLine("        <voice>1</voice>");
                sb.AppendLine($"        <type>{fig.type}</type>");
                for (int d = 0; d < fig.dots; d++) sb.AppendLine("        <dot/>");
                sb.AppendLine("      </note>");
            }
        }

        // A whole empty bar is one measure rest: no figure has to exist for the bar length (12/8 = 144 units has none).
        static void EmitMeasureRest(StringBuilder sb, int barUnits)
        {
            sb.AppendLine("      <note>");
            sb.AppendLine("        <rest measure=\"yes\"/>");
            sb.AppendLine($"        <duration>{barUnits}</duration>");
            sb.AppendLine("        <voice>1</voice>");
            sb.AppendLine("      </note>");
        }

        /// <summary>Split a length into standard figures, aware of WHERE in the bar it starts: a figure beginning off
        /// the beat first finishes the beat it started in, and one starting on a beat takes only whole beats aligned on
        /// their own length. This is what keeps "quarter + half" from being written "dotted quarter + eighth + quarter".
        /// </summary>
        static List<(int u, string type, int dots)> Split(int pos, int len, int beatUnits)
        {
            var outp = new List<(int, string, int)>();
            len = Math.Max(0, len / 3 * 3);
            int guard = 0;
            while (len >= 3 && guard++ < 64)
            {
                int off = pos % beatUnits;
                bool onBeat = off == 0 && len >= beatUnits;
                int take = onBeat ? len : Math.Min(len, beatUnits - off);
                var pick = Vals[Vals.Length - 1];
                foreach (var v in Vals)
                {
                    if (v.u > take) continue;
                    // On a beat: whole beats only, and placed where a musician would place them. beatUnits (24 or 36)
                    // is always in the table, so the loop always finds something and always advances.
                    if (onBeat && (v.u % beatUnits != 0 || (v.u != beatUnits && pos % v.u != 0))) continue;
                    pick = v; break;
                }
                outp.Add(pick);
                pos += pick.u; len -= pick.u;
            }
            if (outp.Count == 0) outp.Add(Vals[Vals.Length - 1]);
            return outp;
        }

        // Round display-beats (quarter units) to the 1/32 (3-unit) grid.
        static int R3(double beats) { int u = (int)Math.Round(beats * U); return Math.Max(0, (int)Math.Round(u / 3.0) * 3); }

        /// <summary>Spell a MIDI pitch as MusicXML step/alter/octave, going through the same line-of-fifths choice as
        /// the .mscx export (see <see cref="Tpc"/>): tpc 13..19 are the naturals F C G D A E B, ±7 gives the flats and
        /// the sharps — the table never reaches a double accidental, so alter stays in -1..+1.</summary>
        static void SpellPitch(int midi, int spellCenter, out string step, out int alter, out int octave)
        {
            int tpc = Tpc(midi, spellCenter);
            step = "FCGDAEB"[(tpc - 6) % 7].ToString();
            alter = (tpc - 6) / 7 - 1;
            int c = midi - alter;                                          // the natural's pitch, i.e. the letter's own
            octave = (c >= 0 ? c / 12 : (c - 11) / 12) - 1;                // floor division: B#-2 (midi 0) stays legal
        }

        // Spell a MIDI pitch as a TPC (position on the line of fifths). For the 5 black keys there are two candidates
        // (sharp vs flat, 12 apart); pick the one closest to the key's TONIC center (14 = C, +spellCenter fifths). This
        // keeps diatonic notes spelled by the key signature and chromatic ones by function.
        static int Tpc(int midi, int spellCenter)
        {
            int pc = ((midi % 12) + 12) % 12;
            int[] sharp = { 14, 21, 16, 23, 18, 13, 20, 15, 22, 17, 24, 19 };
            int[] flat = { 14, 9, 16, 11, 18, 13, 8, 15, 10, 17, 12, 19 };
            int s = sharp[pc], f = flat[pc];
            if (s == f) return s;                                  // natural (white key)
            int center = 14 + spellCenter;
            int ds = Math.Abs(s - center), df = Math.Abs(f - center);
            if (ds != df) return ds < df ? s : f;
            return spellCenter < 0 ? f : s;                        // tie → follow the key's flat/sharp bias
        }

        static void ClefCode(ScoreClefKind c, out string sign, out string line)
        {
            switch (c)
            {
                case ScoreClefKind.Bass: sign = "F"; line = "4"; break;
                case ScoreClefKind.Alto: sign = "C"; line = "3"; break;
                case ScoreClefKind.Tenor: sign = "C"; line = "4"; break;
                default: sign = "G"; line = "2"; break;
            }
        }

        static string Esc(string s) => string.IsNullOrEmpty(s) ? "" :
            s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
    }
}
