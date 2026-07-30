using System;
using System.Runtime.InteropServices;

namespace MusicTracker.Engine.Timeline.Vst3.Interop
{
    // ================================================================================================
    // COM interfaces Steinberg VST3
    //
    // **Approche** : chaque interface est déclarée en <see cref="ComImportAttribute"/> +
    // <see cref="InterfaceTypeAttribute"/>(<see cref="ComInterfaceType.InterfaceIsIUnknown"/>). Le
    // marshaller CLR ajoute automatiquement les 3 slots FUnknown (QueryInterface/AddRef/Release en
    // tête du vtable) — on ne déclare donc que les méthodes VST3 dans l'ORDRE EXACT du header C++.
    //
    // Toute interface héritée doit LISTER DE NOUVEAU les méthodes du parent, dans l'ordre, car le
    // COM interop CLR n'hérite que d'IUnknown. Exemple : IComponent hérite de IPluginBase qui hérite
    // de FUnknown → on liste d'abord initialize/terminate (IPluginBase), puis les 9 propres à
    // IComponent, sinon les slots sont décalés.
    //
    // **Return code** : les VST3 renvoient <c>tresult</c> = <see cref="int"/>. 0 = OK ; codes négatifs =
    // erreurs (voir Vst3Enums). Certaines méthodes renvoient un pointeur (<see cref="IntPtr"/>) ou un
    // <see cref="int"/>/<see cref="uint"/> pur (count, latency…) au lieu de tresult.
    //
    // **Pointeurs opaques** : les méthodes qui renvoient un objet COM (createInstance, createView,
    // getParameterData…) sont typées <see cref="IntPtr"/> plutôt que par une interface .NET — ça permet
    // de gérer les null (returnal légal) sans AV, et de faire un <see cref="Marshal.GetObjectForIUnknown"/>
    // suivi d'un cast contrôlé côté appelant.
    //
    // Signatures reprises de <c>pluginterfaces/</c> du SDK VST3 3.7.x (dépôt Steinberg, GPL-3.0).
    // ================================================================================================

    // ------------------------------------------------------------------------------------------------
    // IPluginBase - initialize / terminate
    // ------------------------------------------------------------------------------------------------

    [ComImport, Guid(Vst3Uids.IPluginBase), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IPluginBase
    {
        [PreserveSig] int initialize(IntPtr context);        // context = FUnknown* implementing IHostApplication (optional)
        [PreserveSig] int terminate();
    }

    // ------------------------------------------------------------------------------------------------
    // IPluginFactory
    // ------------------------------------------------------------------------------------------------
    // NOTE : cid/iid sont passés comme des tableaux de 16 octets. Signature C++ :
    //   createInstance(FIDString cid, FIDString _iid, void** obj)
    // avec TUID = char[16]. On passe donc des IntPtr pointant sur des 16 octets. En pratique on alloue
    // un buffer via GCHandle sur un byte[16] issu de Guid.ToByteArray() (dont on doit CORRIGER l'endian
    // pour matcher COM_COMPATIBLE — cf. Vst3ModuleLoader).

    [ComImport, Guid(Vst3Uids.IPluginFactory), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IPluginFactory
    {
        [PreserveSig] int getFactoryInfo(out PFactoryInfo info);
        [PreserveSig] int countClasses();                     // returns int, NOT tresult
        [PreserveSig] int getClassInfo(int index, out PClassInfo info);
        [PreserveSig] int createInstance(IntPtr cid, IntPtr iid, out IntPtr obj);
    }

    [ComImport, Guid(Vst3Uids.IPluginFactory2), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IPluginFactory2
    {
        // IPluginFactory inherited (order must match)
        [PreserveSig] int getFactoryInfo(out PFactoryInfo info);
        [PreserveSig] int countClasses();
        [PreserveSig] int getClassInfo(int index, out PClassInfo info);
        [PreserveSig] int createInstance(IntPtr cid, IntPtr iid, out IntPtr obj);
        // IPluginFactory2 additions
        [PreserveSig] int getClassInfo2(int index, out PClassInfo2 info);
    }

    // ------------------------------------------------------------------------------------------------
    // BusInfo - passed to IComponent.getBusInfo (out struct)
    // ------------------------------------------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct BusInfo
    {
        public int MediaType;         // kAudio / kEvent
        public int Direction;         // kInput / kOutput
        public int ChannelCount;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Name;           // UTF-16, 128 chars
        public int BusType;           // kMain / kAux
        public uint Flags;            // kDefaultActive, ...
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RoutingInfo
    {
        public int MediaType;
        public int BusIndex;
        public int Channel;
    }

    // ------------------------------------------------------------------------------------------------
    // IComponent  (extends IPluginBase)
    // ------------------------------------------------------------------------------------------------

    [ComImport, Guid(Vst3Uids.IComponent), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IComponent
    {
        // --- IPluginBase inherited ---
        [PreserveSig] int initialize(IntPtr context);
        [PreserveSig] int terminate();
        // --- IComponent methods ---
        [PreserveSig] int getControllerClassId([Out, MarshalAs(UnmanagedType.LPArray, SizeConst = 16)] byte[] classId);
        [PreserveSig] int setIoMode(int mode);
        [PreserveSig] int getBusCount(int mediaType, int direction);   // NOTE: returns int (count), NOT tresult
        [PreserveSig] int getBusInfo(int mediaType, int direction, int index, out BusInfo bus);
        [PreserveSig] int getRoutingInfo(ref RoutingInfo inInfo, ref RoutingInfo outInfo);
        [PreserveSig] int activateBus(int mediaType, int direction, int index, byte state); // TBool = uint8
        [PreserveSig] int setActive(byte state);
        [PreserveSig] int setState(IntPtr state);   // IBStream*
        [PreserveSig] int getState(IntPtr state);   // IBStream*
    }

    // ------------------------------------------------------------------------------------------------
    // ProcessSetup - IAudioProcessor.setupProcessing arg
    // ------------------------------------------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    public struct ProcessSetup
    {
        public int ProcessMode;         // kRealtime
        public int SymbolicSampleSize;  // kSample32
        public int MaxSamplesPerBlock;
        public int _padding;            // sampleRate is double, needs 8-align
        public double SampleRate;
    }

    // ------------------------------------------------------------------------------------------------
    // IAudioProcessor  (extends FUnknown directly)
    // ------------------------------------------------------------------------------------------------

    [ComImport, Guid(Vst3Uids.IAudioProcessor), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioProcessor
    {
        // inputs / outputs = SpeakerArrangement* (uint64 array), numIns / numOuts = element count.
        [PreserveSig] int setBusArrangements(IntPtr inputs, int numIns, IntPtr outputs, int numOuts);
        [PreserveSig] int getBusArrangement(int direction, int index, ref ulong arr);
        [PreserveSig] int canProcessSampleSize(int symbolicSampleSize);
        [PreserveSig] uint getLatencySamples();
        [PreserveSig] int setupProcessing(ref ProcessSetup setup);
        [PreserveSig] int setProcessing(byte state);
        [PreserveSig] int process(ref ProcessData data);
        [PreserveSig] uint getTailSamples();
    }

    // ------------------------------------------------------------------------------------------------
    // IEditController  (extends IPluginBase)
    // ------------------------------------------------------------------------------------------------

    [ComImport, Guid(Vst3Uids.IEditController), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IEditController
    {
        // --- IPluginBase inherited ---
        [PreserveSig] int initialize(IntPtr context);
        [PreserveSig] int terminate();
        // --- IEditController methods ---
        [PreserveSig] int setComponentState(IntPtr state);   // IBStream*
        [PreserveSig] int setState(IntPtr state);
        [PreserveSig] int getState(IntPtr state);
        [PreserveSig] int getParameterCount();
        [PreserveSig] int getParameterInfo(int paramIndex, out ParameterInfo info);
        [PreserveSig] int getParamStringByValue(uint id, double valueNormalized, IntPtr stringBuf128);
        [PreserveSig] int getParamValueByString(uint id, IntPtr stringBuf, out double valueNormalized);
        [PreserveSig] double normalizedParamToPlain(uint id, double valueNormalized);
        [PreserveSig] double plainParamToNormalized(uint id, double plainValue);
        [PreserveSig] double getParamNormalized(uint id);
        [PreserveSig] int setParamNormalized(uint id, double value);
        [PreserveSig] int setComponentHandler(IntPtr handler);
        [PreserveSig] IntPtr createView([MarshalAs(UnmanagedType.LPStr)] string name);  // IPlugView* (nullable → IntPtr)
    }

    // ------------------------------------------------------------------------------------------------
    // IPlugView (extends FUnknown)
    // ------------------------------------------------------------------------------------------------

    [ComImport, Guid(Vst3Uids.IPlugView), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IPlugView
    {
        [PreserveSig] int isPlatformTypeSupported([MarshalAs(UnmanagedType.LPStr)] string type);
        [PreserveSig] int attached(IntPtr parent, [MarshalAs(UnmanagedType.LPStr)] string type);
        [PreserveSig] int removed();
        [PreserveSig] int onWheel(float distance);
        [PreserveSig] int onKeyDown(ushort key, short keyCode, short modifiers);
        [PreserveSig] int onKeyUp(ushort key, short keyCode, short modifiers);
        [PreserveSig] int getSize(out ViewRect size);
        [PreserveSig] int onSize(ref ViewRect newSize);
        [PreserveSig] int onFocus(byte state);
        [PreserveSig] int setFrame(IntPtr frame);           // IPlugFrame* — passer un CCW non-null (voir Vst3PlugFrame)
        [PreserveSig] int canResize();
        [PreserveSig] int checkSizeConstraint(ref ViewRect rect);
    }

    // ------------------------------------------------------------------------------------------------
    // IPlugViewContentScaleSupport (extends FUnknown) — négociation HiDPI. Host appelle
    // setContentScaleFactor(scale) AVANT ou juste après attached() ; plein de plugins modernes refusent
    // de peindre sans ça.
    // ------------------------------------------------------------------------------------------------

    [ComImport, Guid(Vst3Uids.IPlugViewContentScaleSupport), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IPlugViewContentScaleSupport
    {
        [PreserveSig] int setContentScaleFactor(float factor);
    }

    // ------------------------------------------------------------------------------------------------
    // IPlugFrame (extends FUnknown) — callback host que le plugin appelle pour demander un resize.
    // Doit être fourni via IPlugView.setFrame() AVANT attached() sinon plein de plugins refusent de rendre.
    // ------------------------------------------------------------------------------------------------

    [ComImport, Guid(Vst3Uids.IPlugFrame), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IPlugFrame
    {
        [PreserveSig] int resizeView(IntPtr view, ref ViewRect newSize);
    }

    // ------------------------------------------------------------------------------------------------
    // IConnectionPoint (extends FUnknown) — messages bidirectionnels component ↔ controller (dual-comp)
    // ------------------------------------------------------------------------------------------------

    [ComImport, Guid(Vst3Uids.IConnectionPoint), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IConnectionPoint
    {
        [PreserveSig] int connect(IntPtr other);            // IConnectionPoint* — l'autre extrémité
        [PreserveSig] int disconnect(IntPtr other);
        [PreserveSig] int notify(IntPtr message);           // IMessage* — non implémenté côté host, on ignore
    }

    // ------------------------------------------------------------------------------------------------
    // IBStream (extends FUnknown) — plugin state serialization
    // ------------------------------------------------------------------------------------------------

    [ComImport, Guid(Vst3Uids.IBStream), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IBStream
    {
        [PreserveSig] int read(IntPtr buffer, int numBytes, IntPtr numBytesRead);          // numBytesRead: int32* (may be null)
        [PreserveSig] int write(IntPtr buffer, int numBytes, IntPtr numBytesWritten);
        [PreserveSig] int seek(long pos, int mode, IntPtr result);                          // result: int64* (may be null)
        [PreserveSig] int tell(out long pos);
    }

    // ------------------------------------------------------------------------------------------------
    // IEventList (extends FUnknown)
    // ------------------------------------------------------------------------------------------------

    [ComImport, Guid(Vst3Uids.IEventList), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IEventList
    {
        [PreserveSig] int getEventCount();
        [PreserveSig] int getEvent(int index, out Vst3Event e);
        [PreserveSig] int addEvent(ref Vst3Event e);
    }

    // ------------------------------------------------------------------------------------------------
    // IParamValueQueue + IParameterChanges
    // ------------------------------------------------------------------------------------------------

    [ComImport, Guid(Vst3Uids.IParamValueQueue), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IParamValueQueue
    {
        [PreserveSig] uint getParameterId();
        [PreserveSig] int getPointCount();
        [PreserveSig] int getPoint(int index, out int sampleOffset, out double value);
        [PreserveSig] int addPoint(int sampleOffset, double value, out int index);
    }

    [ComImport, Guid(Vst3Uids.IParameterChanges), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IParameterChanges
    {
        [PreserveSig] int getParameterCount();
        [PreserveSig] IntPtr getParameterData(int index);                        // IParamValueQueue*
        [PreserveSig] IntPtr addParameterData(ref uint id, out int index);       // IParamValueQueue*
    }

    // ------------------------------------------------------------------------------------------------
    // IHostApplication (extends FUnknown) — we implement this ourselves and pass to IPluginBase.initialize
    // ------------------------------------------------------------------------------------------------

    [ComImport, Guid(Vst3Uids.IHostApplication), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IHostApplication
    {
        // name: TChar[128] out buffer (UTF-16). We pass a caller-allocated IntPtr.
        [PreserveSig] int getName(IntPtr name128);
        [PreserveSig] int createInstance(IntPtr cid, IntPtr iid, out IntPtr obj);
    }
}
