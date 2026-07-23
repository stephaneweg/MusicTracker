using System.Collections.Generic;
using System.Windows.Media;
using MusicTracker.Engine.Flow;

namespace MusicTracker.Controls
{
    /// <summary>
    /// Percussion-family colours, à la GarageBand's Beat Sequencer: every <see cref="DrumPattern"/> lane gets a
    /// colour whose HUE identifies the family (kick / snare / hats / toms / cymbals / hands-shakers / latin drums /
    /// wood-metal) and whose SHADE varies within a family (e.g. open vs closed hat, low vs high tom). Shared by the
    /// rhythm grid editor (cell fill + tinted empty-row background) and the timeline thumbnail so the two agree.
    /// </summary>
    public static class DrumColors
    {
        // Family index per lane (indices match DrumPattern.LaneNames / LaneKeys, 0..46).
        static readonly int[] Fam =
        {
            0, 1, 2, 2, 2, 1, 5, 3, 3, 3, 4, 4,   // 0..11  kick, snare, hatC/O/P, rim, clap, toms, crash, ride
            0, 1, 3, 3, 3, 4, 4, 4, 4, 4,         // 12..21 kick2, snareE, toms, china, ridebell, splash, crash2, ride2
            5, 7, 5,                              // 22..24 tambourine, cowbell, vibraslap
            6, 6, 6, 6, 6,                        // 25..29 bongos, congas
            6, 6, 7, 7,                           // 30..33 timbales, agogo
            5, 5, 5, 5, 5, 5,                     // 34..39 cabasa, maracas, whistles, guiros
            7, 7, 7, 5, 5,                        // 40..44 claves, wood blocks, cuica
            7, 7,                                 // 45..46 triangle
        };

        // Family base colours (hue = family). Distinct enough to read at a glance in a dark grid.
        static readonly Color[] FamBase =
        {
            Color.FromRgb(0xE8, 0x65, 0x4E), // 0 kick        — warm red
            Color.FromRgb(0xE8, 0xA2, 0x4B), // 1 snare       — gold
            Color.FromRgb(0x26, 0xC6, 0xD9), // 2 hats        — cyan
            Color.FromRgb(0x57, 0xC7, 0x66), // 3 toms        — green
            Color.FromRgb(0xC6, 0x6C, 0xEA), // 4 cymbals     — purple
            Color.FromRgb(0xDD, 0xB3, 0x6E), // 5 hands/shak. — tan
            Color.FromRgb(0xE8, 0x82, 0x5A), // 6 latin drums — coral
            Color.FromRgb(0xC9, 0xC2, 0x4E), // 7 wood/metal  — olive
        };

        static readonly Color[] Lane;

        static DrumColors()
        {
            // Spread each family's lanes across a lightness range (0.82..1.18) so same-family lanes vary in shade.
            Lane = new Color[Fam.Length];
            var byFam = new Dictionary<int, List<int>>();
            for (int l = 0; l < Fam.Length; l++)
            {
                if (!byFam.TryGetValue(Fam[l], out var list)) { list = new List<int>(); byFam[Fam[l]] = list; }
                list.Add(l);
            }
            foreach (var kv in byFam)
            {
                var list = kv.Value;
                Color b = FamBase[kv.Key >= 0 && kv.Key < FamBase.Length ? kv.Key : 1];
                for (int i = 0; i < list.Count; i++)
                {
                    double f = list.Count <= 1 ? 1.0 : 0.82 + 0.36 * i / (list.Count - 1);
                    Lane[list[i]] = Scale(b, f);
                }
            }
        }

        /// <summary>Family colour for a rhythm-editor lane index.</summary>
        public static Color ForLane(int lane) => (lane >= 0 && lane < Lane.Length) ? Lane[lane] : FamBase[1];

        /// <summary>Family colour for a GM percussion key (folded to a lane via <see cref="DrumPattern.LaneForKey"/>).</summary>
        public static Color ForKey(int key) => ForLane(DrumPattern.LaneForKey(key));

        /// <summary>A dark, desaturated shade of a lane colour, for the empty-cell row background
        /// (beats slightly brighter than off-beats, echoing the family hue behind the grid).</summary>
        public static Color Dim(Color c, bool beat) => Blend(c, Color.FromRgb(0x23, 0x24, 0x29), beat ? 0.22 : 0.11);

        static Color Scale(Color c, double f) => Color.FromRgb(Clamp(c.R * f), Clamp(c.G * f), Clamp(c.B * f));
        static byte Clamp(double v) => (byte)(v < 0 ? 0 : v > 255 ? 255 : v);

        static Color Blend(Color fg, Color bg, double a) => Color.FromRgb(
            (byte)(fg.R * a + bg.R * (1 - a)),
            (byte)(fg.G * a + bg.G * (1 - a)),
            (byte)(fg.B * a + bg.B * (1 - a)));
    }
}
