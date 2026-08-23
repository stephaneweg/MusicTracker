using System;
using System.Collections.Generic;

namespace KotonPluginGuqinConstrainer
{
    /// <summary>
    /// Modèle logique du guqin : 7 cordes + 13 hui + résolveur MIDI ↔ (corde, position).
    /// Copié depuis KotonPluginGuqinVirtuel — chaque plugin .ksl est autonome.
    /// </summary>
    public static class GuqinModel
    {
        public const int StringCount = 7;
        public const int HuiCount = 13;

        public static readonly double[] HuiPositions =
        {
            1.0/8, 1.0/6, 1.0/5, 1.0/4, 1.0/3, 2.0/5, 1.0/2,
            3.0/5, 2.0/3, 3.0/4, 4.0/5, 5.0/6, 7.0/8,
        };

        public sealed class Tuning
        {
            public string Name { get; }
            public int[] OpenMidi { get; }
            public Tuning(string name, int[] openMidi) { Name = name; OpenMidi = openMidi; }
        }

        public static readonly Tuning ZhengdiaoC = new Tuning("Zhengdiao en C (5-6-1-2-3-5-6)", new[] { 43, 45, 48, 50, 52, 55, 57 });
        public static readonly Tuning ZhengdiaoF = new Tuning("Zhengdiao en F (5-6-1-2-3-5-6)", new[] { 41, 43, 46, 48, 50, 53, 55 });
        public static readonly Tuning Manjiao    = new Tuning("Manjiao (baisse corde 3)",       new[] { 43, 45, 47, 50, 52, 55, 57 });
        public static readonly Tuning Ruibin     = new Tuning("Ruibin (monte corde 5)",         new[] { 43, 45, 48, 50, 53, 55, 57 });

        public static readonly Tuning[] AllTunings = { ZhengdiaoC, ZhengdiaoF, Manjiao, Ruibin };

        public struct Fingering
        {
            public int StringIdx;
            public double Position;
            public int Midi;
            public bool IsOpen => Position <= 1e-6;
        }

        public static int MidiFor(Tuning tuning, int stringIdx, double position)
        {
            if (stringIdx < 0 || stringIdx >= StringCount) return -1;
            int openMidi = tuning.OpenMidi[stringIdx];
            if (position <= 1e-6) return openMidi;
            double vibratingRatio = 1.0 - position;
            if (vibratingRatio <= 0.01) vibratingRatio = 0.01;
            double semitones = -12.0 * Math.Log(vibratingRatio, 2);
            return (int)Math.Round(openMidi + semitones);
        }

        public static List<Fingering> AllFingerings(Tuning tuning)
        {
            var list = new List<Fingering>();
            for (int s = 0; s < StringCount; s++)
            {
                list.Add(new Fingering { StringIdx = s, Position = 0, Midi = tuning.OpenMidi[s] });
                for (int h = 0; h < HuiPositions.Length; h++)
                {
                    double p = HuiPositions[h];
                    int m = MidiFor(tuning, s, p);
                    if (m > 0 && m <= 127) list.Add(new Fingering { StringIdx = s, Position = p, Midi = m });
                }
            }
            return list;
        }

        public static Fingering ResolveMidi(Tuning tuning, int targetMidi, out bool exact)
        {
            var all = AllFingerings(tuning);
            Fingering best = default;
            int bestDist = int.MaxValue;
            double bestCenterDist = double.MaxValue;
            foreach (var f in all)
            {
                int d = Math.Abs(f.Midi - targetMidi);
                if (d > bestDist) continue;
                double cd = Math.Abs(f.Position - 0.5);
                if (d < bestDist || cd < bestCenterDist)
                {
                    bestDist = d; bestCenterDist = cd; best = f;
                }
            }
            exact = bestDist == 0;
            return best;
        }
    }
}
