using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace MusicTracker.Engine.Flow
{
    /// <summary>
    /// Named drum motifs grouped by category, loaded from <c>Data/drums/catalog.json</c> (seeded from user pieces).
    /// Each motif is a note-list (lane index, start, length at <see cref="Motif.Spq"/>) spanning <see cref="Motif.Beats"/>
    /// beats. Applied to a <see cref="DrumPatternModule"/> as a custom pattern so any drum lane / template can reuse it.
    /// </summary>
    public class DrumCatalog
    {
        public class Motif
        {
            public string Name { get; set; }
            public int Spq { get; set; } = 4;
            public int Beats { get; set; } = 4;
            public int[][] Notes { get; set; }   // [ [lane, start, length], ... ]
            public int Builtin { get; set; } = -1;   // >= 0 = reference to a built-in DrumPattern style (procedural); -1 = note-list

            public int LengthSlices => System.Math.Max(1, Beats) * System.Math.Max(1, Spq);

            public List<RiffNote> ToNotes()
            {
                var list = new List<RiffNote>();
                if (Notes != null)
                    foreach (var n in Notes)
                        if (n != null && n.Length >= 3) list.Add(new RiffNote(n[0], n[1], n[2]));
                return list;
            }
        }

        public class Category
        {
            public string Name { get; set; }
            public List<Motif> Motifs { get; set; } = new List<Motif>();
        }

        public List<Category> Categories { get; set; } = new List<Category>();

        static DrumCatalog _instance;
        public static DrumCatalog Instance => _instance ?? (_instance = Load());

        /// <summary>Category name of the synthetic "Standard" group (the built-in DrumPattern styles).</summary>
        public const string StandardCategory = "Standard";

        static DrumCatalog Load()
        {
            DrumCatalog cat = null;
            try
            {
                string path = AppPaths.Local(Path.Combine("Data", "drums", "catalog.json"));
                if (File.Exists(path))
                    cat = JsonSerializer.Deserialize<DrumCatalog>(File.ReadAllText(path),
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch { /* missing/corrupt catalog → just the built-ins */ }
            cat = cat ?? new DrumCatalog();

            // Migrate the built-in DrumPattern styles into the catalogue as note-list motifs (one canonical bar each,
            // Auto density) so they're uniform with the exotic ones — the catalogue is the single source of truth.
            var std = new Category { Name = StandardCategory };
            for (int i = 0; i < DrumPattern.StyleNames.Length - 1; i++)   // exclude the trailing "Personnalisé…"
            {
                var notes = DrumPattern.LaneNotesForStyle(i, 4);         // one 4/4 bar
                var arr = new int[notes.Count][];
                for (int j = 0; j < notes.Count; j++) arr[j] = new[] { notes[j].Note, notes[j].Start, notes[j].Length };
                std.Motifs.Add(new Motif { Name = DrumPattern.StyleNames[i], Spq = DrumPattern.SlicesPerQuarter, Beats = 4, Notes = arr });
            }
            cat.Categories.Insert(0, std);
            return cat;
        }

        /// <summary>Find a motif by "Category/Name" (case-insensitive). Null if not found.</summary>
        public Motif FindPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            int i = path.IndexOf('/');
            if (i < 0) return null;
            string cat = path.Substring(0, i).Trim(), name = path.Substring(i + 1).Trim();
            foreach (var c in Categories)
                if (string.Equals(c.Name, cat, System.StringComparison.OrdinalIgnoreCase))
                    foreach (var m in c.Motifs)
                        if (string.Equals(m.Name, name, System.StringComparison.OrdinalIgnoreCase)) return m;
            return null;
        }
    }
}
