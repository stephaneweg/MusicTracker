using System;
using System.IO;

namespace MeltySynth
{
    internal struct Zone
    {
        private ArraySegment<Generator> generators;
        private ArraySegment<Modulator> modulators;

        private Zone(ArraySegment<Generator> generators, ArraySegment<Modulator> modulators)
        {
            this.generators = generators;
            this.modulators = modulators;
        }

        private Zone(ZoneInfo info, Generator[] generators, Modulator[] modulators)
        {
            this.generators = new ArraySegment<Generator>(generators, info.GeneratorIndex, info.GeneratorCount);
            this.modulators = new ArraySegment<Modulator>(modulators, Math.Min(info.ModulatorIndex, modulators.Length),
                                                          Math.Min(info.ModulatorCount, Math.Max(0, modulators.Length - info.ModulatorIndex)));
        }

        internal static Zone[] Create(ZoneInfo[] infos, Generator[] generators, Modulator[] modulators)
        {
            if (infos.Length <= 1)
            {
                throw new InvalidDataException("No valid zone was found.");
            }

            // The last one is the terminator.
            var zones = new Zone[infos.Length - 1];

            for (var i = 0; i < zones.Length; i++)
            {
                zones[i] = new Zone(infos[i], generators, modulators);
            }

            return zones;
        }

        public static Zone Empty => new Zone(new ArraySegment<Generator>(Array.Empty<Generator>()), new ArraySegment<Modulator>(Array.Empty<Modulator>()));

        public ArraySegment<Generator> Generators => generators;
        public ArraySegment<Modulator> Modulators => modulators;
    }
}
