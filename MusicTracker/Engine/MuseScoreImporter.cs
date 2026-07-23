using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Xml.Linq;

namespace MusicTracker.Engine
{
    /// <summary>
    /// Reads a MuseScore 3 file (.mscz zip or raw .mscx XML) and converts it to the sequencer's
    /// slice grid, the same way the MIDI import does: one layer per staff, notes placed on a
    /// 24-slices-per-quarter grid, instruments taken from the embedded GM program numbers.
    /// </summary>
    public static class MuseScoreImporter
    {
        // Slices per quarter note (matches the MIDI importer's ratio).
        const double SlicesPerQuarter = 24.0;

        public class Note
        {
            public int Pitch;        // MIDI pitch
            public int StartSlice;
            public int LengthSlices;
            public int Velocity = 96; // 0..127 loudness (MIDI velocity / MuseScore dynamic)
            public BendPoint[] Bend;  // optional pitch-bend curve (offsets in SRC slices from StartSlice); null = none
        }

        public class Track
        {
            public string Name = "";
            public int GmProgram;
            public bool IsDrum;      // percussion (MIDI channel 10): use a drum kit, not a pitched instrument
            public MusicTracker.Engine.Score.ScoreClefKind? Clef; // explicit clef from the file (null = derive from the instrument)
            public List<Note> Notes = new List<Note>();
        }

        // Map a MuseScore clef code (G/F/C…) to a score clef. Null = unknown → caller derives from the instrument.
        static MusicTracker.Engine.Score.ScoreClefKind? MapClef(string code)
        {
            if (string.IsNullOrWhiteSpace(code)) return null;
            string c = code.Trim().ToUpperInvariant();
            if (c.StartsWith("F")) return MusicTracker.Engine.Score.ScoreClefKind.Bass;
            if (c.StartsWith("G")) return MusicTracker.Engine.Score.ScoreClefKind.Treble;
            if (c == "C4" || c.Contains("TENOR")) return MusicTracker.Engine.Score.ScoreClefKind.Tenor;
            if (c.StartsWith("C") || c.Contains("ALTO")) return MusicTracker.Engine.Score.ScoreClefKind.Alto;
            return null;
        }

        // The staff's clef: its <defaultClef>, else the first <Clef>/<clef> found in its measures.
        static string StaffClefCode(System.Xml.Linq.XElement staffDef, System.Xml.Linq.XElement staffMeasures)
        {
            string c = (string)staffDef?.Element("defaultClef");
            if (!string.IsNullOrWhiteSpace(c)) return c;
            var clefEl = staffMeasures?.Descendants("Clef").FirstOrDefault();
            if (clefEl != null) return (string)clefEl.Element("concertClefType") ?? (string)clefEl.Element("clefType");
            var lower = staffMeasures?.Descendants("clef").FirstOrDefault();
            return lower != null ? (string)lower : null;
        }

        public class Score
        {
            public int SliceCount;
            public double SpeedFactor = SlicesPerQuarter / 4.0;
            public double Bpm;       // 0 if unspecified
            public int? KeyFifths;   // explicit key signature (circle-of-fifths position) if the file has a <KeySig>
            public bool? KeyIsMinor; // explicit mode if the <KeySig> carries a <mode> (else null = detect)
            public int TimeSigN = 4, TimeSigD = 4; // first time signature
            public bool HasTimeSig;  // true if the file specified a <TimeSig> (else the importer guessed)
            public List<Track> Tracks = new List<Track>();
            public List<int> MeasureStartSlices = new List<int>(); // slice index where each measure begins
        }

        public static Score Load(string path)
        {
            XDocument doc = LoadDocument(path);
            var scoreEl = doc.Root.Element("Score");
            if (scoreEl == null) throw new Exception("Not a MuseScore score (no <Score>).");

            int division = (int?)scoreEl.Element("Division") ?? 480;
            double slicesPerTick = SlicesPerQuarter / division;

            var result = new Score();

            // Explicit key signature (first <KeySig>). Circle-of-fifths position (negative = flats), stored as
            // <concertKey> (MS4), <accidental> (MS2/MS3) or <subtype> (MS1 legacy). The mode (major/minor) is
            // optional via <mode>; when absent we detect it from the notes.
            var firstKey = scoreEl.Descendants("KeySig").FirstOrDefault();
            if (firstKey != null)
            {
                result.KeyFifths = (int?)firstKey.Element("concertKey")
                                   ?? (int?)firstKey.Element("accidental")
                                   ?? (int?)firstKey.Element("subtype");
                string mode = (string)firstKey.Element("mode");
                if (!string.IsNullOrWhiteSpace(mode))
                {
                    string m = mode.Trim().ToLowerInvariant();
                    if (m == "minor" || m == "aeolian") result.KeyIsMinor = true;
                    else if (m == "major" || m == "ionian") result.KeyIsMinor = false;
                }
            }

            // First Tempo (quarter notes per second) -> BPM.
            var tempoEl = scoreEl.Descendants("Tempo").Select(t => t.Element("tempo")).FirstOrDefault(t => t != null);
            if (tempoEl != null && double.TryParse((string)tempoEl, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double qps))
                result.Bpm = Math.Round(qps * 60.0);

            // Map each staff id -> (GM program, part name) from the <Part> definitions.
            var staffInfo = new Dictionary<int, KeyValuePair<int, string>>();
            var staffDefById = new Dictionary<int, System.Xml.Linq.XElement>();
            foreach (var part in scoreEl.Elements("Part"))
            {
                var instrument = part.Element("Instrument");
                int program = 0;
                var channel = instrument?.Elements("Channel").FirstOrDefault();
                var programEl = channel?.Element("program");
                if (programEl != null) program = (int?)programEl.Attribute("value") ?? 0;

                string name = (string)part.Element("trackName")
                              ?? (string)instrument?.Element("trackName")
                              ?? "";

                foreach (var staffDef in part.Elements("Staff"))
                {
                    int id = (int?)staffDef.Attribute("id") ?? 0;
                    if (id > 0) { staffInfo[id] = new KeyValuePair<int, string>(program, name); staffDefById[id] = staffDef; }
                }
            }

            int maxEndSlice = 0;

            // The score-level <Staff> elements carry the actual measures.
            foreach (var staff in scoreEl.Elements("Staff"))
            {
                if (!staff.Elements("Measure").Any()) continue;
                int staffId = (int?)staff.Attribute("id") ?? 0;

                int program = 0; string name = "";
                if (staffInfo.TryGetValue(staffId, out var info)) { program = info.Key; name = info.Value; }
                staffDefById.TryGetValue(staffId, out var sdef);

                var track = new Track { GmProgram = program, Name = name, Clef = MapClef(StaffClefCode(sdef, staff)) };

                long measureStartTick = 0;
                int sigN = 4, sigD = 4;
                int currentVelocity = 96; // updated by <Dynamic> markings (f, p, mf, ...)
                var boundaries = new List<int>();

                foreach (var measure in staff.Elements("Measure"))
                {
                    boundaries.Add((int)Math.Round(measureStartTick * slicesPerTick));
                    var ts = measure.Descendants("TimeSig").FirstOrDefault();
                    if (ts != null)
                    {
                        sigN = (int?)ts.Element("sigN") ?? sigN;
                        sigD = (int?)ts.Element("sigD") ?? sigD;
                        if (!result.HasTimeSig) { result.TimeSigN = sigN; result.TimeSigD = sigD; result.HasTimeSig = true; }
                    }
                    long measureTicks = (long)Math.Round(sigN * (4.0 * division / sigD));

                    // MuseScore 3 wraps each voice in <voice>; MuseScore 1/2 put the chords
                    // directly under <Measure>. Handle both transparently.
                    var voiceContainers = measure.Elements("voice").ToList();
                    if (voiceContainers.Count == 0) voiceContainers = new List<XElement> { measure };

                    foreach (var voice in voiceContainers)
                    {
                        long cursor = measureStartTick;
                        var tupletRatios = new Dictionary<string, double>();

                        foreach (var el in voice.Elements())
                        {
                            string elName = el.Name.LocalName;

                            if (elName == "Dynamic")
                            {
                                // A dynamic marking (f, p, mf, sf, ...) changes the loudness from here on.
                                currentVelocity = DynamicVelocity(el);
                                continue;
                            }

                            if (elName == "Tuplet" && el.Attribute("id") != null)
                            {
                                // Tuplet definition: remember normalNotes/actualNotes by id.
                                double nn = (double?)el.Element("normalNotes") ?? 1.0;
                                double an = (double?)el.Element("actualNotes") ?? 1.0;
                                tupletRatios[(string)el.Attribute("id")] = an != 0 ? nn / an : 1.0;
                                continue;
                            }

                            if (elName == "Chord" || elName == "Rest")
                            {
                                bool grace = elName == "Chord" && IsGrace(el);

                                // A chord/rest inside a tuplet has a <Tuplet>id</Tuplet> reference.
                                double ratio = 1.0;
                                var tupletRef = el.Element("Tuplet");
                                if (tupletRef != null && tupletRatios.TryGetValue(tupletRef.Value.Trim(), out double r))
                                    ratio = r;

                                long dur = grace ? 0 : (long)Math.Round(DurationTicks(el, division, measureTicks) * ratio);

                                if (elName == "Chord")
                                {
                                    int startSlice = (int)Math.Round(cursor * slicesPerTick);
                                    int lenSlices = Math.Max(1, (int)Math.Round((grace ? division / 8 : dur) * slicesPerTick));
                                    foreach (var noteEl in el.Elements("Note"))
                                    {
                                        var pitchEl = noteEl.Element("pitch");
                                        if (pitchEl == null) continue;
                                        track.Notes.Add(new Note
                                        {
                                            Pitch = (int)pitchEl,
                                            StartSlice = startSlice,
                                            LengthSlices = lenSlices,
                                            Velocity = currentVelocity,
                                        });
                                        if (startSlice + lenSlices > maxEndSlice) maxEndSlice = startSlice + lenSlices;
                                    }
                                }
                                cursor += dur;
                            }
                        }
                    }

                    measureStartTick += measureTicks;
                }

                if (boundaries.Count > result.MeasureStartSlices.Count)
                    result.MeasureStartSlices = boundaries; // canonical = the staff with the most measures

                result.Tracks.Add(track);
            }

            result.SliceCount = maxEndSlice;
            return result;
        }

        private static XDocument LoadDocument(string path)
        {
            if (Path.GetExtension(path).Equals(".mscz", StringComparison.OrdinalIgnoreCase))
            {
                using (var zip = ZipFile.OpenRead(path))
                {
                    var entry = zip.Entries.FirstOrDefault(e => e.FullName.EndsWith(".mscx", StringComparison.OrdinalIgnoreCase));
                    if (entry == null) throw new Exception("No .mscx found inside the .mscz archive.");
                    using (var stream = entry.Open())
                        return XDocument.Load(stream);
                }
            }
            return XDocument.Load(path);
        }

        // Standard MuseScore dynamic -> MIDI velocity (0..127).
        static readonly Dictionary<string, int> DynamicVelocities = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            { "pppp", 10 }, { "ppp", 16 }, { "pp", 33 }, { "p", 49 }, { "mp", 64 },
            { "mf", 80 }, { "f", 96 }, { "ff", 112 }, { "fff", 126 }, { "ffff", 127 },
            { "fp", 96 }, { "sf", 112 }, { "sfz", 112 }, { "sff", 112 }, { "sfp", 96 },
            { "rf", 112 }, { "rfz", 112 }, { "fz", 112 },
        };

        private static int DynamicVelocity(XElement dynamic)
        {
            // Prefer an explicit <velocity>, else map the <subtype> (e.g. "f", "mf", "pp").
            var velEl = dynamic.Element("velocity");
            if (velEl != null && int.TryParse((string)velEl, out int v)) return Math.Max(1, Math.Min(127, v));
            string sub = ((string)dynamic.Element("subtype") ?? "").Trim();
            return DynamicVelocities.TryGetValue(sub, out int mapped) ? mapped : 80;
        }

        private static bool IsGrace(XElement chord)
        {
            return chord.Elements().Any(e =>
            {
                string n = e.Name.LocalName;
                return n.StartsWith("grace") || n == "appoggiatura" || n == "acciaccatura";
            });
        }

        private static long DurationTicks(XElement el, int division, long measureTicks)
        {
            string dt = (string)el.Element("durationType") ?? "quarter";
            long baseTicks;
            switch (dt)
            {
                case "measure": return measureTicks;
                case "long": baseTicks = 16 * division; break;
                case "breve": baseTicks = 8 * division; break;
                case "whole": baseTicks = 4 * division; break;
                case "half": baseTicks = 2 * division; break;
                case "quarter": baseTicks = division; break;
                case "eighth": baseTicks = division / 2; break;
                case "16th": baseTicks = division / 4; break;
                case "32nd": baseTicks = division / 8; break;
                case "64th": baseTicks = division / 16; break;
                case "128th": baseTicks = division / 32; break;
                default: baseTicks = division; break;
            }

            int dots = (int?)el.Element("dots") ?? 0;
            long total = baseTicks;
            long add = baseTicks;
            for (int i = 0; i < dots; i++)
            {
                add /= 2;
                total += add;
            }
            return total;
        }
    }
}
