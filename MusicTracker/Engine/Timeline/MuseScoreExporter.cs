using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using MusicTracker.Engine.Score;

namespace MusicTracker.Engine.Timeline
{
    /// <summary>
    /// Writes the score (one or more <see cref="TrackScore"/> parts) to a native MuseScore <c>.mscx</c> file
    /// (uncompressed MuseScore 3 XML, opens directly in MuseScore 3/4). Works in DISPLAY-beat space (quarter
    /// units, ×TimeSigScale already applied by <see cref="ScoreBuilder"/>) so a compound 6/8 exports as 6/8 and
    /// a simple meter keeps its real durations. Each part becomes a staff with its own clef + key signature.
    ///
    /// v1 scope: standard note values (whole…32nd, single dot) with rests; chords = notes sharing an onset.
    /// Durations are rounded to the 1/32 (3-unit) grid, so triplets are APPROXIMATED (no tuplet brackets) and
    /// notes are re-articulated rather than tied (incl. across bar lines). Refine the rest in MuseScore.
    /// </summary>
    public static class MuseScoreExporter
    {
        public struct Part { public string Name; public int Program; public TrackScore Score; }

        const int U = 24; // integer units per quarter note (LCM of the 1/8 and 1/6 display grids)

        // Standard note values in units, largest first, with dot flag. 3 (=1/32) is the floor so any multiple
        // of 3 decomposes exactly by greedy.
        static readonly (int u, string name, int dots)[] Vals =
        {
            (96, "whole", 0), (72, "half", 1), (48, "half", 0), (36, "quarter", 1), (24, "quarter", 0),
            (18, "eighth", 1), (12, "eighth", 0), (9, "16th", 1), (6, "16th", 0), (3, "32nd", 0),
        };

        public static void Export(string path, List<Part> parts, int num, int den, string title)
        {
            if (parts == null) parts = new List<Part>();
            int sigN = Math.Max(1, num), sigD = den == 8 ? 8 : 4;
            int barUnits = sigN * 96 / sigD;           // 4/4 = 96, 6/8 = 72, 3/4 = 72 (always a multiple of 3)

            // Measure count = the longest part (others get padded with whole-bar rests so staves stay aligned).
            int measures = 1;
            foreach (var p in parts)
            {
                int totalU = (int)Math.Round((p.Score?.TotalBeats ?? 0) * U);
                measures = Math.Max(measures, Math.Max(1, (totalU + barUnits - 1) / barUnits));
            }

            var sb = new StringBuilder();
            sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
            sb.AppendLine("<museScore version=\"3.02\">");
            sb.AppendLine("  <Score>");
            sb.AppendLine("    <Division>480</Division>");

            // Parts (instrument declarations), in staff order.
            for (int i = 0; i < parts.Count; i++)
            {
                int id = i + 1;
                sb.AppendLine("    <Part>");
                sb.AppendLine($"      <Staff id=\"{id}\">");
                sb.AppendLine("        <StaffType group=\"pitched\"><name>stdNormal</name></StaffType>");
                sb.AppendLine("      </Staff>");
                sb.AppendLine($"      <trackName>{Esc(string.IsNullOrEmpty(parts[i].Name) ? "Voix " + id : parts[i].Name)}</trackName>");
                sb.AppendLine("      <Instrument>");
                sb.AppendLine($"        <longName>{Esc(string.IsNullOrEmpty(parts[i].Name) ? "Voix " + id : parts[i].Name)}</longName>");
                sb.AppendLine("        <Channel>");
                sb.AppendLine($"          <program value=\"{Math.Max(0, Math.Min(127, parts[i].Program))}\"/>");
                sb.AppendLine("        </Channel>");
                sb.AppendLine("      </Instrument>");
                sb.AppendLine("    </Part>");
            }

            // Staves (the music). Staff 1 carries the title VBox.
            for (int i = 0; i < parts.Count; i++)
            {
                int id = i + 1;
                var ts = parts[i].Score ?? new TrackScore();
                sb.AppendLine($"    <Staff id=\"{id}\">");
                if (i == 0 && !string.IsNullOrWhiteSpace(title))
                {
                    sb.AppendLine("      <VBox>");
                    sb.AppendLine("        <height>10</height>");
                    sb.AppendLine($"        <Text><style>Title</style><text>{Esc(title)}</text></Text>");
                    sb.AppendLine("      </VBox>");
                }
                WriteStaff(sb, ts, measures, barUnits, sigN, sigD);
                sb.AppendLine("    </Staff>");
            }

            sb.AppendLine("  </Score>");
            sb.AppendLine("</museScore>");
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        }

        static void WriteStaff(StringBuilder sb, TrackScore ts, int measures, int barUnits, int sigN, int sigD)
        {
            var dk = KeySig.Derive(ts.Key ?? new KeySignature(), ts.Transpose);
            int fifths = dk.Flats ? -dk.Count : dk.Count;
            // line-of-fifths position of the actual TONIC (a minor tonic sits 3 fifths above its key signature):
            // chromatic notes are then spelled toward the tonic, so a minor leading tone reads as a sharp
            // (C# in d-minor) not an enharmonic flat (Db), and the Picardy third reads as a sharp too.
            int spellCenter = fifths + ((ts.Key != null && ts.Key.Mode == 1) ? 3 : 0);
            string clef = ClefCode(ts.Clef);

            // Notes → onset-grouped chords (written pitch), snapped to the 3-unit (1/32) grid. A note flagged
            // Arpeggio (a fast roll snapped to one onset) makes its chord carry an arpeggio mark.
            var chords = new List<(int start, int len, List<int> pitches, bool arp)>();
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
            foreach (var kv in byStart) chords.Add((kv.Key, kv.Value.len, kv.Value.pitches, kv.Value.arp));

            int ci = 0;
            for (int m = 0; m < measures; m++)
            {
                int ms = m * barUnits, me = ms + barUnits;
                sb.AppendLine("      <Measure>");
                sb.AppendLine("        <voice>");
                if (m == 0)
                {
                    sb.AppendLine($"          <Clef><concertClefType>{clef}</concertClefType><transposingClefType>{clef}</transposingClefType></Clef>");
                    sb.AppendLine($"          <KeySig><accidental>{fifths}</accidental></KeySig>");
                    sb.AppendLine($"          <TimeSig><sigN>{sigN}</sigN><sigD>{sigD}</sigD></TimeSig>");
                }

                int pos = ms;
                while (ci < chords.Count && chords[ci].start < me)
                {
                    var c = chords[ci];
                    int cs = Math.Max(c.start, pos);
                    int nextStart = (ci + 1 < chords.Count) ? chords[ci + 1].start : me;
                    if (cs > pos) EmitRest(sb, cs - pos);
                    int cend = Math.Min(Math.Min(c.start + c.len, me), Math.Max(nextStart, cs + 3));
                    if (cend > cs) { EmitChord(sb, c.pitches, cend - cs, spellCenter, c.arp); pos = cend; }
                    if (c.start + c.len > me) break;     // chord runs past the bar line: truncate here (no tie)
                    ci++;
                }
                if (pos < me) EmitRest(sb, me - pos);

                sb.AppendLine("        </voice>");
                sb.AppendLine("      </Measure>");
            }
        }

        static void EmitChord(StringBuilder sb, List<int> pitches, int len, int spellCenter, bool arp = false)
        {
            bool first = true;
            foreach (var (name, dots) in Decompose(len))
            {
                sb.AppendLine("          <Chord>");
                if (first && arp) sb.AppendLine("            <Arpeggio><subtype>0</subtype></Arpeggio>"); // rolled chord mark
                first = false;
                if (dots > 0) sb.AppendLine($"            <dots>{dots}</dots>");
                sb.AppendLine($"            <durationType>{name}</durationType>");
                foreach (int p in pitches)
                {
                    sb.AppendLine("            <Note>");
                    sb.AppendLine($"              <pitch>{p}</pitch>");
                    sb.AppendLine($"              <tpc>{Tpc(p, spellCenter)}</tpc>");
                    sb.AppendLine("            </Note>");
                }
                sb.AppendLine("          </Chord>");
            }
        }

        static void EmitRest(StringBuilder sb, int len)
        {
            foreach (var (name, dots) in Decompose(len))
            {
                sb.AppendLine("          <Rest>");
                if (dots > 0) sb.AppendLine($"            <dots>{dots}</dots>");
                sb.AppendLine($"            <durationType>{name}</durationType>");
                sb.AppendLine("          </Rest>");
            }
        }

        // Greedy decomposition of a length (multiple of 3 units) into standard note values, largest first.
        static List<(string name, int dots)> Decompose(int len)
        {
            var outp = new List<(string, int)>();
            int r = Math.Max(0, len / 3 * 3);
            int guard = 0;
            while (r >= 3 && guard++ < 64)
                foreach (var v in Vals)
                    if (v.u <= r) { outp.Add((v.name, v.dots)); r -= v.u; break; }
            if (outp.Count == 0) outp.Add(("32nd", 0));
            return outp;
        }

        // Round display-beats (quarter units) to the 1/32 (3-unit) grid.
        static int R3(double beats) { int u = (int)Math.Round(beats * U); return Math.Max(0, (int)Math.Round(u / 3.0) * 3); }

        // Spell a MIDI pitch as a MuseScore TPC (position on the line of fifths). For the 5 black keys there
        // are two candidates (sharp vs flat, 12 apart); pick the one closest to the key's TONIC center
        // (14 = C on the line of fifths, +spellCenter fifths). This keeps diatonic notes spelled by the key
        // signature and spells chromatic notes by function — e.g. a minor leading tone as a sharp, not a flat.
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

        static string ClefCode(ScoreClefKind c)
        {
            switch (c)
            {
                case ScoreClefKind.Bass: return "F";
                case ScoreClefKind.Alto: return "C3";
                case ScoreClefKind.Tenor: return "C4";
                default: return "G";
            }
        }

        static string Esc(string s) => string.IsNullOrEmpty(s) ? "" :
            s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
    }
}
