using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using static System.Net.Mime.MediaTypeNames;

namespace MusicTracker
{
    public static class Extensions
    {
        public static T ReadEnum<T>(this BinaryReader reader)
        {
            return (T)Enum.ToObject(typeof(T), reader.ReadByte());
        }
        public static T ReadEndianEnum<T>(this BinaryReader reader)
        {
            return (T)Enum.ToObject(typeof(T), reader.ReadEndianByte());
        }

        public static void WriteEnum<T>(this BinaryWriter writer, T value)
        {
            writer.Write(Convert.ToByte(value));
        }

        public static void WriteChars_Count(this BinaryWriter writer,string str,int count)
        {
            byte[] data = Encoding.ASCII.GetBytes(str);
            for(int cpt=0;cpt<data.Length && cpt<count;cpt++)
            { 
                writer.Write(data[cpt]);
            }
            for (int cpt = data.Length; cpt < count; cpt++)
            {
                writer.Write((byte)0);
            }
        }


        public static void WriteEndian(this BinaryWriter writer, Int32 value)
        {

            byte[] data = BitConverter.GetBytes(value);
            if (BitConverter.IsLittleEndian)
            {
                Array.Reverse(data);
            }
            writer.Write(data);
        }
        public static void WriteEndian(this BinaryWriter writer, UInt32 value)
        {

            byte[] data = BitConverter.GetBytes(value);
            if (BitConverter.IsLittleEndian)
            {
                Array.Reverse(data);
            }
            writer.Write(data);
        }

        public static Int16 ReadEndianInt16(this BinaryReader reader)
        {
            byte[] data = reader.ReadBytes(2);
            if (BitConverter.IsLittleEndian)
            {
                Array.Reverse(data);
            }
            return BitConverter.ToInt16(data, 0);
        }
        public static Int32 ReadEndianInt32(this BinaryReader reader)
        {
            byte[] data = reader.ReadBytes(4);
            if (BitConverter.IsLittleEndian)
            {
                Array.Reverse(data);
            }
            return BitConverter.ToInt32(data, 0);
        }
        public static UInt16 ReadEndianUInt16(this BinaryReader reader)
        {
            byte[] data = reader.ReadBytes(2);
            if (BitConverter.IsLittleEndian)
            {
                Array.Reverse(data);
            }
            return BitConverter.ToUInt16(data, 0);
        }
        public static UInt32 ReadEndianUInt32(this BinaryReader reader)
        {
            byte[] data = reader.ReadBytes(4);
            if (BitConverter.IsLittleEndian)
            {
                Array.Reverse(data);
            }
            return BitConverter.ToUInt32(data, 0);
        }

        public static Byte ReadEndianByte(this BinaryReader reader)
        {
            byte[] data = reader.ReadBytes(sizeof(Byte));
            if (BitConverter.IsLittleEndian)
            {
                Array.Reverse(data);
            }
            return data[0];
        }
        public static SByte ReadEndianSByte(this BinaryReader reader)
        {
            sbyte[] data = new sbyte[1];
            data[0] = reader.ReadSByte();
            if (BitConverter.IsLittleEndian)
            {
                Array.Reverse(data);
            }
            return data[0];
        }


        public static Double ReadEndianDouble(this BinaryReader reader)
        {
            byte[] data = reader.ReadBytes(sizeof(Double));
            if (BitConverter.IsLittleEndian)
            {
                Array.Reverse(data);
            }
            return BitConverter.ToDouble(data, 0);
        }
        public static Single ReadEndianShort(this BinaryReader reader)
        {
            byte[] data = reader.ReadBytes(sizeof(Single));
            if (BitConverter.IsLittleEndian)
            {
                Array.Reverse(data);
            }
            return BitConverter.ToSingle(data, 0);
        }

        public static void WriteEndian(this BinaryWriter writer, Int16 value)
        {

            byte[] data = BitConverter.GetBytes(value);
            if (BitConverter.IsLittleEndian)
            {
                Array.Reverse(data);
            }
            writer.Write(data);
        }
        public static void WriteEndian(this BinaryWriter writer, UInt16 value)
        {

            byte[] data = BitConverter.GetBytes(value);
            if (BitConverter.IsLittleEndian)
            {
                Array.Reverse(data);
            }
            writer.Write(data);
        }
        public static void WriteEndian(this BinaryWriter writer, Single value)
        {

            byte[] data = BitConverter.GetBytes(value);
            if (BitConverter.IsLittleEndian)
            {
                Array.Reverse(data);
            }
            writer.Write(data);
        }
        public static void WriteEndian(this BinaryWriter writer, Double value)
        {

            byte[] data = BitConverter.GetBytes(value);
            if (BitConverter.IsLittleEndian)
            {
                Array.Reverse(data);
            }
            writer.Write(data);
        }

        public static void WriteUInt32sEndian(this BinaryWriter writer, UInt32[] data)
        {
            for (int cpt = 0; cpt < data.Length; cpt++)
            {
                writer.Write(data[cpt]);
            }
        }
        public static void WriteUInt32s(this BinaryWriter writer, UInt32[] data)
        {
            for (int cpt = 0; cpt < data.Length; cpt++)
            {
                writer.Write(data[cpt]);
            }
        }
        public static void WriteInt32s(this BinaryWriter writer, Int32[] data)
        {
            for (int cpt = 0; cpt < data.Length; cpt++)
            {
                writer.Write(data[cpt]);
            }
        }

        public static void WriteUInt16s(this BinaryWriter writer, UInt16[] data)
        {
            for (int cpt = 0; cpt < data.Length; cpt++)
            {
                writer.Write(data[cpt]);
            }
        }

        public static void WriteInt16s(this BinaryWriter writer, Int16[] data)
        {
            for (int cpt = 0; cpt < data.Length; cpt++)
            {
                writer.Write(data[cpt]);
            }
        }

        public static void ReadUInt32s(this BinaryReader reader, UInt32[] data)
        {
            for (int cpt = 0; cpt < data.Length; cpt++)
            {
                data[cpt] = reader.ReadUInt32();
            }
        }
        public static void ReadUInt16s(this BinaryReader reader, UInt16[] data)
        {
            for (int cpt = 0; cpt < data.Length; cpt++)
            {
                data[cpt] = reader.ReadUInt16();
            }
        }

        public static void ReadInt16s(this BinaryReader reader, Int16[] data)
        {
            for (int cpt = 0; cpt < data.Length; cpt++)
            {
                data[cpt] = reader.ReadInt16();
            }
        }

        public static void ReadEndianInt16s(this BinaryReader reader, Int16[] data)
        {
            for (int cpt = 0; cpt < data.Length; cpt++)
            {
                data[cpt] = reader.ReadEndianInt16();
            }
        }

        public static void ReadInt32s(this BinaryReader reader, Int32[] data)
        {
            for (int cpt = 0; cpt < data.Length; cpt++)
            {
                data[cpt] = reader.ReadInt32();
            }
        }

        public static string ReadString(this BinaryReader reader,int cpt)
        {
            byte[] data = reader.ReadBytes(cpt);
            return Encoding.ASCII.GetString(data);
        }
    }
}
