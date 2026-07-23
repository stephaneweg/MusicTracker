using System.Windows.Media;

namespace MusicTracker.Controls
{
    /// <summary>
    /// Instrument-family colours for the timeline: each General MIDI program (0..127, or the drum kit) maps to a
    /// family — keys, organ, guitar, bass, strings, brass, winds, synth, other, drums — with a bright HUE (a small
    /// header dot) and a DARK TINT (a subtle lane background wash, like the drum grid's family-tinted rows) so the
    /// arrangement reads by section at a glance: strings vs winds vs brass vs organs vs keys vs guitars…
    /// </summary>
    public static class InstrumentColors
    {
        // family 0 keys, 1 organ, 2 guitar, 3 bass, 4 strings, 5 brass, 6 winds, 7 synth, 8 other, 9 drums
        static readonly Color[] Hue =
        {
            Color.FromRgb(0xD2, 0xA2, 0x4B), // keys    — amber
            Color.FromRgb(0xA5, 0x74, 0xDA), // organ   — purple
            Color.FromRgb(0xE0, 0x7A, 0x42), // guitar  — orange
            Color.FromRgb(0x4C, 0x79, 0xD6), // bass    — blue
            Color.FromRgb(0xCE, 0x5E, 0x6E), // strings — rose
            Color.FromRgb(0xE3, 0xC6, 0x3E), // brass   — gold
            Color.FromRgb(0x5C, 0xB8, 0x6A), // winds   — green
            Color.FromRgb(0xC8, 0x5F, 0xC0), // synth   — magenta
            Color.FromRgb(0x5F, 0xA9, 0xB8), // other   — teal-grey
            Color.FromRgb(0xE0, 0x6A, 0x55), // drums   — warm red
        };

        /// <summary>General-MIDI-program (or drum-kit index &gt;=128) → family index.</summary>
        public static int Family(int program)
        {
            if (program >= 128) return 9;      // drum kit
            if (program < 0) return 8;
            if (program <= 15) return 0;       // 0-7 piano, 8-15 chromatic perc → keys
            if (program <= 23) return 1;       // organ
            if (program <= 31) return 2;       // guitar
            if (program <= 39) return 3;       // bass
            if (program <= 55) return 4;       // strings + ensemble/choir
            if (program <= 63) return 5;       // brass
            if (program <= 79) return 6;       // reed + pipe → winds
            if (program <= 103) return 7;      // synth lead/pad/fx
            return 8;                          // ethnic / percussive / sfx
        }

        /// <summary>Bright family hue — a small always-visible marker (a header dot) or a module-box border.</summary>
        public static Color FamilyHue(int program) => Hue[Family(program)];

        /// <summary>Module-box BACKGROUND: a dark family shade that keeps white title text readable for every family.</summary>
        public static Color BoxFill(int program) => Blend(Hue[Family(program)], Color.FromRgb(0x20, 0x21, 0x27), 0.45);

        /// <summary>Module-box BORDER: nearly the bright family hue — a coloured edge around the box.</summary>
        public static Color BoxBorder(int program) => Blend(Hue[Family(program)], Color.FromRgb(0x20, 0x21, 0x27), 0.85);

        static Color Blend(Color fg, Color bg, double a) => Color.FromRgb(
            (byte)(fg.R * a + bg.R * (1 - a)),
            (byte)(fg.G * a + bg.G * (1 - a)),
            (byte)(fg.B * a + bg.B * (1 - a)));
    }
}
