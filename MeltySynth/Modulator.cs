using System;
using System.IO;

namespace MeltySynth
{
    /// <summary>An SF2 modulator (sfModList / mod): source controller -> destination generator, with amount.</summary>
    internal struct Modulator
    {
        private readonly ushort sourceOper;
        private readonly ushort destinationOper;   // a GeneratorType index (or, with bit15 set, a link — unsupported)
        private readonly short amount;
        private readonly ushort amountSourceOper;
        private readonly ushort transformOper;

        private Modulator(BinaryReader reader)
        {
            sourceOper = reader.ReadUInt16();
            destinationOper = reader.ReadUInt16();
            amount = reader.ReadInt16();
            amountSourceOper = reader.ReadUInt16();
            transformOper = reader.ReadUInt16();
        }

        internal static Modulator[] ReadFromChunk(BinaryReader reader, int size)
        {
            if (size % 10 != 0)
            {
                throw new InvalidDataException("The modulator list is invalid.");
            }

            var count = size / 10 - 1; // the last entry is the terminator
            if (count < 0) count = 0;
            var mods = new Modulator[count];
            for (var i = 0; i < count; i++) mods[i] = new Modulator(reader);
            if (size >= 10) new Modulator(reader); // consume the terminator

            return mods;
        }

        public ushort SourceOper => sourceOper;
        public ushort DestinationOper => destinationOper;
        public short Amount => amount;
        public ushort AmountSourceOper => amountSourceOper;
        public ushort TransformOper => transformOper;

        // ---- source controller decoding (SF2 8.2) ----
        public bool DestinationIsGenerator => (destinationOper & 0x8000) == 0; // bit15 = link (unsupported)
        public GeneratorType DestinationGenerator => (GeneratorType)destinationOper;

        // A "general controller" source (CC flag off): 0 none, 2 velocity, 3 key, 10 poly, 13 chan pressure, 14 pitch wheel...
        public bool SourceIsCC => (sourceOper & 0x0080) != 0;
        public int SourceIndex => sourceOper & 0x007F;
        public int SourceType => (sourceOper >> 10) & 0x3F;  // 0 linear, 1 concave, 2 convex, 3 switch
        public bool SourceBipolar => (sourceOper & 0x0200) != 0;
        public bool SourceDescending => (sourceOper & 0x0100) != 0;

        public bool AmountSourceIsNone => (amountSourceOper & 0x00FF) == 0 && (amountSourceOper & 0x0080) == 0;

        internal float NoteOnAttenuationContribution(int velocity, int key, Channel channel, out bool isVelocitySource)
            => NoteOnContribution(GeneratorType.InitialAttenuation, velocity, key, channel, out isVelocitySource);

        // Contribution (in the destination generator's native units — cB for InitialAttenuation, cents for the
        // filter-cutoff generators) that this modulator adds to generator <paramref name="dest"/> for a NOTE-ON
        // source (velocity / key / CC). Returns 0 for other destinations or unsupported sources. This is what lets
        // MuseScore_General's pianos work like FluidSynth: e.g. a velocity->ModEnvToFilterCutoff modulator (amount
        // +6500) counteracts the layer's baked -2350 cents, so a hard hit opens the low-pass instead of leaving it
        // clamped near ~77 Hz (which muted the note across velocities 80-107). "Amount-source" (linked) modulators
        // are skipped — none of them carry an audible amount in this font.
        internal float NoteOnContribution(GeneratorType dest, int velocity, int key, Channel channel, out bool isVelocitySource)
        {
            isVelocitySource = false;
            if (!DestinationIsGenerator || DestinationGenerator != dest) return 0F;
            if (!AmountSourceIsNone) return 0F;
            int val;
            if (SourceIsCC)
            {
                val = channel.GetController(SourceIndex); // e.g. CC2 (MuseScore single-note dynamics)
            }
            else
            {
                switch (SourceIndex)
                {
                    case 2: val = velocity; isVelocitySource = true; break; // note-on velocity
                    case 3: val = key; break;                               // note key
                    default: return 0F;                                     // other general controllers: ignore
                }
            }
            return amount * SourceCurve(val); // amount source assumed "none" (== 1)
        }

        // The SF2 source transform (linear / concave / convex / switch), with direction + polarity.
        private float SourceCurve(int val)
        {
            float x = val / 127F;
            if (SourceDescending) x = 1F - x;
            float c;
            switch (SourceType)
            {
                case 1: c = Concave(x); break;
                case 2: c = Convex(x); break;
                case 3: c = x >= 0.5F ? 1F : 0F; break;
                default: c = x; break; // linear
            }
            if (SourceBipolar) c = 2F * c - 1F;
            return c;
        }

        // FluidSynth-compatible concave/convex (normalized [0,1]); coefficient = 400/960 (= 200*2/PEAK_ATT).
        private static float Concave(float x) { if (x <= 0F) return 0F; if (x >= 1F) return 1F; float r = -0.4166667F * (float)Math.Log10(1F - x); return r > 1F ? 1F : r; }
        private static float Convex(float x) { if (x <= 0F) return 0F; if (x >= 1F) return 1F; float r = 1F + 0.4166667F * (float)Math.Log10(x); return r < 0F ? 0F : r; }
    }
}
