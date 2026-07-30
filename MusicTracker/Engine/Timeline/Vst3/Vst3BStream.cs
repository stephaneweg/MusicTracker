using System;
using System.IO;
using System.Runtime.InteropServices;
using MusicTracker.Engine.Timeline.Vst3.Interop;

namespace MusicTracker.Engine.Timeline.Vst3
{
    /// <summary>
    /// Implémentation managée d'<see cref="IBStream"/> sur un <see cref="MemoryStream"/>. Sert à
    /// passer un blob de state au plugin (setState / setComponentState) OU à recueillir le state
    /// produit par le plugin (getState). Le CLR génère automatiquement un CCW (COM Callable Wrapper)
    /// dont on récupère le pointeur via <see cref="Marshal.GetComInterfaceForObject"/>.
    ///
    /// Les 4 opérations sont mappées naïvement — pas de buffer pool, pas de zero-copy : c'est appelé
    /// à l'ouverture/sauvegarde du projet, pas dans le thread audio. La correctness prime sur la perf.
    /// </summary>
    [ClassInterface(ClassInterfaceType.None)]
    public sealed class Vst3BStream : IBStream, IDisposable
    {
        readonly MemoryStream _ms;
        readonly bool _ownsStream;

        public Vst3BStream() { _ms = new MemoryStream(); _ownsStream = true; }

        /// <summary>Construit un stream initialisé avec <paramref name="data"/> et prêt en lecture (position 0).</summary>
        public Vst3BStream(byte[] data)
        {
            _ms = new MemoryStream();
            if (data != null && data.Length > 0) _ms.Write(data, 0, data.Length);
            _ms.Position = 0;
            _ownsStream = true;
        }

        public byte[] ToArray() => _ms.ToArray();
        public long Length => _ms.Length;
        /// <summary>Remet le curseur au début — utile entre un getState (écriture) et setComponentState (relecture).</summary>
        public void Rewind() => _ms.Position = 0;

        public int read(IntPtr buffer, int numBytes, IntPtr numBytesRead)
        {
            if (numBytes <= 0) { if (numBytesRead != IntPtr.Zero) Marshal.WriteInt32(numBytesRead, 0); return Vst3Enums.kResultOk; }
            var tmp = new byte[numBytes];
            int n = _ms.Read(tmp, 0, numBytes);
            if (n > 0) Marshal.Copy(tmp, 0, buffer, n);
            if (numBytesRead != IntPtr.Zero) Marshal.WriteInt32(numBytesRead, n);
            return Vst3Enums.kResultOk;
        }

        public int write(IntPtr buffer, int numBytes, IntPtr numBytesWritten)
        {
            if (numBytes <= 0) { if (numBytesWritten != IntPtr.Zero) Marshal.WriteInt32(numBytesWritten, 0); return Vst3Enums.kResultOk; }
            var tmp = new byte[numBytes];
            Marshal.Copy(buffer, tmp, 0, numBytes);
            _ms.Write(tmp, 0, numBytes);
            if (numBytesWritten != IntPtr.Zero) Marshal.WriteInt32(numBytesWritten, numBytes);
            return Vst3Enums.kResultOk;
        }

        public int seek(long pos, int mode, IntPtr result)
        {
            long newPos = mode switch
            {
                Vst3Enums.kIBSeekSet => pos,
                Vst3Enums.kIBSeekCur => _ms.Position + pos,
                Vst3Enums.kIBSeekEnd => _ms.Length + pos,
                _ => pos,
            };
            if (newPos < 0) newPos = 0;
            _ms.Position = newPos;
            if (result != IntPtr.Zero) Marshal.WriteInt64(result, newPos);
            return Vst3Enums.kResultOk;
        }

        public int tell(out long pos) { pos = _ms.Position; return Vst3Enums.kResultOk; }

        public void Dispose() { if (_ownsStream) _ms.Dispose(); }
    }
}
