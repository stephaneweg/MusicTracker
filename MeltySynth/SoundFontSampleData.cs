using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace MeltySynth
{
    internal sealed class SoundFontSampleData
    {
        private readonly int bitsPerSample;
        private readonly short[] samples;

        internal SoundFontSampleData(BinaryReader reader)
        {
            var chunkId = reader.ReadFourCC();
            if (chunkId != "LIST")
            {
                throw new InvalidDataException("The LIST chunk was not found.");
            }

            var end = (long)reader.ReadInt32();
            end += reader.BaseStream.Position;

            var listType = reader.ReadFourCC();
            if (listType != "sdta")
            {
                throw new InvalidDataException($"The type of the LIST chunk must be 'sdta', but was '{listType}'.");
            }

            while (reader.BaseStream.Position < end)
            {
                var id = reader.ReadFourCC();
                var size = reader.ReadInt32();

                switch (id)
                {
                    case "smpl":
                        bitsPerSample = 16;
                        samples = new short[size / 2];
                        // net48: BinaryReader has no Read(Span<byte>); read raw bytes then blit into the short[].
                        var raw = reader.ReadBytes(size);
                        Buffer.BlockCopy(raw, 0, samples, 0, raw.Length);
                        break;
                    case "sm24":
                        // 24 bit audio is not supported.
                        reader.BaseStream.Position += size;
                        break;
                    default:
                        throw new InvalidDataException($"The INFO list contains an unknown ID '{id}'.");
                }
            }

            if (samples == null)
            {
                throw new InvalidDataException("No valid sample data was found.");
            }

            if (samples.Length >= 2)
            {
                // net48: no Encoding.GetString(ReadOnlySpan<byte>); check the "OggS" magic from the first 2 shorts.
                var magic = new byte[4];
                Buffer.BlockCopy(samples, 0, magic, 0, 4);
                if (Encoding.ASCII.GetString(magic, 0, 4) == "OggS")
                {
                    throw new NotSupportedException("SoundFont3 is not yet supported.");
                }
            }

            if (!BitConverter.IsLittleEndian)
            {
                // TODO: Insert the byte swapping code here.
                throw new NotSupportedException("Big endian architectures are not yet supported.");
            }
        }

        public int BitsPerSample => bitsPerSample;
        public short[] Samples => samples;
    }
}
