using System;
using System.Runtime.InteropServices;

namespace MusicTracker.Engine.Timeline.Vst3.Interop
{
    /// <summary>
    /// Structs C++ Steinberg VST3 mappés en C# pour l'appel natif. Chaque layout suit textuellement les
    /// headers de <c>pluginterfaces/vst/</c> (SDK 3.7.x) avec un <see cref="StructLayoutAttribute"/> explicite.
    ///
    /// Attention aux alignements 8-octets sur x64 : les unions contenant un <see cref="double"/> forcent la
    /// jointure d'un pad avant elles. Les tailles calculées à la main (Event = 48 octets total) ont été
    /// validées à partir des définitions dans <c>ivstevents.h</c>.
    /// </summary>
    // ------------------------------------------------------------------------------------------------
    // Énumérations Steinberg::Vst — utilisées dans ProcessData / Event
    // ------------------------------------------------------------------------------------------------

    public static class Vst3Enums
    {
        // ProcessModes
        public const int kRealtime = 0;
        public const int kPrefetch = 1;
        public const int kOffline = 2;

        // SymbolicSampleSizes
        public const int kSample32 = 0;
        public const int kSample64 = 1;

        // BusDirection / BusType
        public const int kInput = 0;
        public const int kOutput = 1;
        public const int kAudio = 0;
        public const int kEvent = 1;

        // MediaTypes
        public const int kMain = 0;
        public const int kAux = 1;

        // Event types (Event.type)
        public const ushort kNoteOnEvent = 0;
        public const ushort kNoteOffEvent = 1;
        public const ushort kDataEvent = 2;
        public const ushort kPolyPressureEvent = 3;
        public const ushort kNoteExpressionValueEvent = 4;
        public const ushort kNoteExpressionTextEvent = 5;
        public const ushort kChordEvent = 6;
        public const ushort kScaleEvent = 7;
        public const ushort kLegacyMIDICCOutEvent = 65535;

        // Steinberg tresult (int32) return codes — cf. pluginterfaces/base/ftypes.h
        public const int kNoInterface = unchecked((int)0x80004002); // E_NOINTERFACE
        public const int kResultOk = 0;                              // S_OK
        public const int kResultTrue = 0;
        public const int kResultFalse = 1;                           // S_FALSE
        public const int kInvalidArgument = unchecked((int)0x80070057); // E_INVALIDARG
        public const int kNotImplemented = unchecked((int)0x80004001); // E_NOTIMPL
        public const int kInternalError = unchecked((int)0x80004005); // E_FAIL
        public const int kNotInitialized = unchecked((int)0x8000FFFF);
        public const int kOutOfMemory = unchecked((int)0x8007000E);

        // IBStream::seekMode
        public const int kIBSeekSet = 0;
        public const int kIBSeekCur = 1;
        public const int kIBSeekEnd = 2;

        // ComponentFlags for busarrangements — Speaker arrangements (SpeakerArrangement is a uint64 bitmap)
        public const ulong kEmpty = 0;
        public const ulong kSpeakerL = 1UL << 0;
        public const ulong kSpeakerR = 1UL << 1;
        public const ulong kStereo = kSpeakerL | kSpeakerR;
        public const ulong kMono = 1UL << 19; // kSpeakerM
    }

    // ------------------------------------------------------------------------------------------------
    // PClassInfo / PClassInfo2 : retournés par IPluginFactory.getClassInfo(index, out info)
    // ------------------------------------------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    public struct PClassInfo
    {
        // TUID (16-byte identifier) — matched to a Guid by value; on Windows COM_COMPATIBLE the byte layout aligns.
        public Guid ClassId;
        public int Cardinality;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string Category;      // ANSI, 32 chars
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string Name;          // ANSI, 64 chars
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    public struct PClassInfo2
    {
        public Guid ClassId;
        public int Cardinality;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string Category;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string Name;
        public uint ClassFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string SubCategories; // ANSI, 128 chars — includes "Fx" / "Instrument" / musical genre tags
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string Vendor;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string SdkVersion;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    public struct PFactoryInfo
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string Vendor;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string Url;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Email;
        public int Flags;
    }

    // ------------------------------------------------------------------------------------------------
    // ProcessData / AudioBusBuffers : passés à IAudioProcessor.process
    // ------------------------------------------------------------------------------------------------

    // AudioBusBuffers: sizeof = 24 bytes on x64 (int32 + 4 pad + uint64 + pointer).
    [StructLayout(LayoutKind.Sequential)]
    public struct AudioBusBuffers
    {
        public int NumChannels;         // 0..3
        public int _padding;            // 4..7 — natural padding before the following uint64
        public ulong SilenceFlags;      // 8..15 — bit i = 1 → channel i is silent
        public IntPtr ChannelBuffers;   // 16..23 — float**  (32-bit samples) or double** (64-bit samples)
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ProcessData
    {
        public int ProcessMode;            // Vst3Enums.kRealtime
        public int SymbolicSampleSize;     // Vst3Enums.kSample32
        public int NumSamples;
        public int NumInputs;              // number of AudioBusBuffers in Inputs
        public int NumOutputs;
        public IntPtr Inputs;              // AudioBusBuffers*
        public IntPtr Outputs;             // AudioBusBuffers*
        public IntPtr InputParameterChanges;   // IParameterChanges* (may be null)
        public IntPtr OutputParameterChanges;  // may be null
        public IntPtr InputEvents;             // IEventList* (may be null)
        public IntPtr OutputEvents;            // may be null
        public IntPtr ProcessContext;          // ProcessContext* (may be null)
    }

    // ------------------------------------------------------------------------------------------------
    // Event : union of note-on/note-off/etc — LayoutKind.Explicit
    // ------------------------------------------------------------------------------------------------
    //
    // C header layout (ivstevents.h, on x64):
    //   int32   busIndex;        // 0
    //   int32   sampleOffset;    // 4
    //   double  ppqPosition;     // 8
    //   uint16  flags;           // 16
    //   uint16  type;            // 18
    //   [4 bytes pad → align union to 8]
    //   union { ... };           // starts at 24, size 24 (max member incl. 8-alignment)
    // Total: 48 bytes.

    [StructLayout(LayoutKind.Explicit, Size = 48)]
    public struct Vst3Event
    {
        [FieldOffset(0)] public int BusIndex;
        [FieldOffset(4)] public int SampleOffset;
        [FieldOffset(8)] public double PpqPosition;
        [FieldOffset(16)] public ushort Flags;
        [FieldOffset(18)] public ushort Type;

        // Union payload — only the members we actually emit today (NoteOn, NoteOff, PolyPressure). All start
        // at offset 24 (post 8-byte alignment).

        // NoteOnEvent: int16 channel, int16 pitch, float tuning, float velocity, int32 length, int32 noteId
        [FieldOffset(24)] public short NoteOn_Channel;
        [FieldOffset(26)] public short NoteOn_Pitch;
        [FieldOffset(28)] public float NoteOn_Tuning;
        [FieldOffset(32)] public float NoteOn_Velocity;
        [FieldOffset(36)] public int NoteOn_Length;
        [FieldOffset(40)] public int NoteOn_NoteId;

        // NoteOffEvent: int16 channel, int16 pitch, float velocity, int32 noteId, float tuning
        [FieldOffset(24)] public short NoteOff_Channel;
        [FieldOffset(26)] public short NoteOff_Pitch;
        [FieldOffset(28)] public float NoteOff_Velocity;
        [FieldOffset(32)] public int NoteOff_NoteId;
        [FieldOffset(36)] public float NoteOff_Tuning;

        // PolyPressureEvent: int16 channel, int16 pitch, float pressure, int32 noteId
        [FieldOffset(24)] public short PolyPressure_Channel;
        [FieldOffset(26)] public short PolyPressure_Pitch;
        [FieldOffset(28)] public float PolyPressure_Pressure;
        [FieldOffset(32)] public int PolyPressure_NoteId;
    }

    // ------------------------------------------------------------------------------------------------
    // ParameterInfo : IEditController.getParameterInfo returns this
    // ------------------------------------------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct ParameterInfo
    {
        public uint Id;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Title;          // UTF-16, 128 chars
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string ShortTitle;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Units;
        public int StepCount;
        public double DefaultNormalizedValue;
        public int UnitId;
        public int Flags;
    }

    // ------------------------------------------------------------------------------------------------
    // ViewRect : IPlugView.getSize + onSize
    // ------------------------------------------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    public struct ViewRect
    {
        public int Left, Top, Right, Bottom;
        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }
}
