using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using MusicTracker.Engine.Flow;

namespace MusicTracker.Engine.Timeline
{
    /// <summary>
    /// The Orchestrateur's rhythm/articulation CATALOGUE (Data\catalogue\{family}.json). It holds, per
    /// family × mood × meter: reusable one-bar RHYTHM CELLS for melodic lines (the engine fills the pitches from the
    /// chord grid) and one-bar chord/nappe ARTICULATION motifs, plus per-section-role recipes that say which cell/artic
    /// each section uses (+ register, voice count). The generator only composes the HARMONY; everything melodic is a
    /// rhythm skeleton realized by <see cref="MelodicLineEngine"/>. Adding a style/mood = adding JSON, no code.
    /// </summary>
    public class MotifCatalogue
    {
        static readonly JsonSerializerOptions Opts = new JsonSerializerOptions { IncludeFields = true, PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip };

        public static string Dir => AppPaths.Local(Path.Combine("Data", "catalogue"));

        /// <summary>Read a family catalogue fresh from disk (no cache — files are small). Never throws; missing → empty.</summary>
        public static FamilyCatalogue LoadFamily(string family)
        {
            try
            {
                string fam = string.IsNullOrWhiteSpace(family) ? "generic" : family.ToLowerInvariant();
                string path = Path.Combine(Dir, fam + ".json");
                if (File.Exists(path))
                {
                    var c = JsonSerializer.Deserialize<FamilyCatalogue>(File.ReadAllText(path), Opts);
                    if (c != null) { c.Family = fam; return c; }
                }
            }
            catch { }
            return new FamilyCatalogue { Family = string.IsNullOrWhiteSpace(family) ? "generic" : family };
        }
    }

    /// <summary>One bar of a MONOPHONIC melodic-line voice: onset + length in slices (at <see cref="Spq"/>). The pitch is
    /// chosen by the engine from the harmony, so only the rhythm is stored.</summary>
    public class MotifCell
    {
        public int Spq = 4;
        public int[] On = new int[0];
        public int[] Len = new int[0];
    }

    /// <summary>One bar of a CHORD/NAPPE articulation: each note = [row, start, len] in slices (row = custom-voice grid
    /// row: 0 bass, 1 root, 2 3rd, 3 5th, 4 7th, 5 root', 6 9th, …). Portable across chord qualities.</summary>
    public class ArticMotif
    {
        public int Spq = 24;
        public int[][] N = new int[0][];
    }

    /// <summary>Which materials a section role uses. <see cref="Line"/>/<see cref="Counter"/> are cell keys, one per bar
    /// (cycled to the section length).</summary>
    public class RoleMotif
    {
        public string Role = "body";
        public string[] Line = new string[0];
        public string[] Counter = null;   // null = no counter voice available
        public string Chord = null;        // ArticMotif key for the chord track this section
        public int Register = 0;           // melodic-line register shift for this role (dev lifts, etc.)
        public int Voices = 1;             // line voices when same-staff (2 = lead + counter on one staff)
        public int Contour = 0;            // melodic contour mode (0 arc/wave · 1 up · 2 down · 3 static · 4 zigzag · 5 random)
    }

    /// <summary>A concrete (mood, meter) variant of a family: its cell + artic libraries and the per-role recipes.</summary>
    public class CatalogueVariant
    {
        public string Mood = "";
        public int MeterNum = 3, MeterDen = 4;
        public int BassRegister = -24;
        public string BassCell = "bass";
        public string Nappe = "nappe";
        public Dictionary<string, MotifCell> Cells = new Dictionary<string, MotifCell>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, ArticMotif> Artics = new Dictionary<string, ArticMotif>(StringComparer.OrdinalIgnoreCase);
        public List<RoleMotif> Roles = new List<RoleMotif>();

        public RoleMotif RoleFor(string role) => RoleFor(role, 0);

        /// <summary>Pick a role recipe; when several recipes share the role, <paramref name="seed"/> selects one (so each
        /// generation gets different rhythms — less "parrot"). reexpo/recap fall back to theme (same seed → same recipe).</summary>
        public RoleMotif RoleFor(string role, int seed)
        {
            var matches = new List<RoleMotif>();
            foreach (var r in Roles) if (string.Equals(r.Role, role, StringComparison.OrdinalIgnoreCase)) matches.Add(r);
            if (matches.Count == 0 && (role == "reexpo" || role == "recap")) foreach (var r in Roles) if (r.Role == "theme") matches.Add(r);
            if (matches.Count == 0) foreach (var r in Roles) if (r.Role == "body") matches.Add(r);
            if (matches.Count == 0) foreach (var r in Roles) if (r.Role == "dev") matches.Add(r);
            if (matches.Count == 0) return Roles.Count > 0 ? Roles[0] : null;
            return matches[((seed % matches.Count) + matches.Count) % matches.Count];
        }

        MotifCell Cell(string key) => (key != null && Cells.TryGetValue(key, out var c)) ? c : null;
        ArticMotif Artic(string key) => (key != null && Artics.TryGetValue(key, out var a)) ? a : null;

        // ---- builders (called by the Orchestrateur) ----

        /// <summary>Build the melodic-line module for a section: tile the role's per-bar rhythm cells across the section,
        /// optionally adding the counter on voice 1 (same staff). Pitches are filled later by the engine.</summary>
        public MelodicLineModule BuildLine(RoleMotif role, int bars, int beatsPerBar, string lineName, bool counterSameStaff)
        {
            int spq = 4;
            var first = role != null && role.Line != null && role.Line.Length > 0 ? Cell(role.Line[0]) : null;
            if (first != null) spq = Math.Max(1, first.Spq);
            var notes = new List<RiffNote>();
            AppendVoice(notes, role?.Line, 0, bars, beatsPerBar, spq);
            bool addCounter = counterSameStaff && role != null && role.Voices >= 2 && role.Counter != null;
            if (addCounter) AppendVoice(notes, role.Counter, 1, bars, beatsPerBar, spq);
            int totalSlices = bars * beatsPerBar * spq;
            var ml = new MelodicLineModule
            {
                BeatsPerBar = bars * beatsPerBar,
                VoiceCount = addCounter ? 2 : 1,
                LineName = lineName,
                RegisterShift = role != null ? role.Register : 0,
                Contour = role != null ? role.Contour : 0,
            };
            ml.SetNotes(notes, spq, totalSlices);
            return ml;
        }

        /// <summary>Build a separate counter line (voice 0) for the two-staff option.</summary>
        public MelodicLineModule BuildCounterLine(RoleMotif role, int bars, int beatsPerBar, string lineName)
        {
            if (role == null || role.Counter == null) return null;
            int spq = 4; var first = Cell(role.Counter[0]); if (first != null) spq = Math.Max(1, first.Spq);
            var notes = new List<RiffNote>();
            AppendVoice(notes, role.Counter, 0, bars, beatsPerBar, spq);
            var ml = new MelodicLineModule { BeatsPerBar = bars * beatsPerBar, VoiceCount = 1, LineName = lineName, RegisterShift = role.Register - 7, Contour = role.Contour };
            ml.SetNotes(notes, spq, bars * beatsPerBar * spq);
            return ml;
        }

        /// <summary>Build the bass line for a section (mono, low register).</summary>
        public MelodicLineModule BuildBass(int bars, int beatsPerBar, string lineName)
        {
            int spq = 4; var cell = Cell(BassCell); if (cell != null) spq = Math.Max(1, cell.Spq);
            var notes = new List<RiffNote>();
            var bar = new[] { BassCell };
            AppendVoice(notes, bar, 0, bars, beatsPerBar, spq);
            var ml = new MelodicLineModule { BeatsPerBar = bars * beatsPerBar, VoiceCount = 1, LineName = lineName, RegisterShift = BassRegister };
            ml.SetNotes(notes, spq, bars * beatsPerBar * spq);
            return ml;
        }

        void AppendVoice(List<RiffNote> notes, string[] cellKeys, int voice, int bars, int beatsPerBar, int spq)
        {
            if (cellKeys == null || cellKeys.Length == 0) return;
            for (int b = 0; b < bars; b++)
            {
                var cell = Cell(cellKeys[b % cellKeys.Length]);
                if (cell == null) continue;
                int baseSlice = b * beatsPerBar * spq;
                int rescale = spq == cell.Spq ? 1 : 0; // cells share spq by construction; guard anyway
                for (int i = 0; i < cell.On.Length; i++)
                {
                    int st = cell.On[i], ln = i < cell.Len.Length ? cell.Len[i] : 1;
                    if (rescale == 0 && cell.Spq > 0) { st = st * spq / cell.Spq; ln = Math.Max(1, ln * spq / cell.Spq); }
                    notes.Add(new RiffNote(voice, baseSlice + st, Math.Max(1, ln)) { Voice = voice });
                }
            }
        }

        /// <summary>The custom articulation note-list (one bar) for a chord track's Pattern module.</summary>
        public List<RiffNote> ChordArticNotes(string key, out int spq)
        {
            var a = Artic(key); spq = a != null ? Math.Max(1, a.Spq) : 24;
            return ArticToNotes(a);
        }
        public List<RiffNote> NappeArticNotes(out int spq)
        {
            var a = Artic(Nappe); spq = a != null ? Math.Max(1, a.Spq) : 24;
            return ArticToNotes(a);
        }
        static List<RiffNote> ArticToNotes(ArticMotif a)
        {
            var l = new List<RiffNote>();
            if (a != null && a.N != null) foreach (var n in a.N) if (n != null && n.Length >= 3) l.Add(new RiffNote(n[0], n[1], Math.Max(1, n[2])));
            return l;
        }
    }

    public class FamilyCatalogue
    {
        public string Family = "generic";
        public List<CatalogueVariant> Variants = new List<CatalogueVariant>();

        /// <summary>Pick a variant matching the mood + meter (seeded among ties); nearest meter / any-mood fallback.</summary>
        public CatalogueVariant Pick(string mood, int num, int den, int seed)
        {
            if (Variants == null || Variants.Count == 0) return null;
            var pool = new List<CatalogueVariant>();
            foreach (var v in Variants) if (v.MeterNum == num && v.MeterDen == den) pool.Add(v);
            if (pool.Count == 0) return null;   // no motif set for this meter → caller keeps the legacy path
            var moodHits = new List<CatalogueVariant>();
            if (!string.IsNullOrEmpty(mood)) foreach (var v in pool) if (string.Equals(v.Mood, mood, StringComparison.OrdinalIgnoreCase)) moodHits.Add(v);
            var final = moodHits.Count > 0 ? moodHits : pool;
            return final[((seed % final.Count) + final.Count) % final.Count];
        }
    }
}
