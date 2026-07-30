using System;
using System.Runtime.InteropServices;
using MusicTracker.Engine.Timeline.Effects;
using MusicTracker.Engine.Timeline.Vst3;
using MusicTracker.Engine.Timeline.Vst3.Interop;

namespace MusicTracker.Engine.Timeline
{
    /// <summary>
    /// Hôte d'un plugin VST3 UTILISÉ COMME INSTRUMENT (synthé / sampler). Symétrique de
    /// <see cref="VstInstrument"/> (VST2) au sens de l'API : mêmes NoteOn/NoteOff/ProcessMidiCC/Render,
    /// mêmes IsLoaded/IsFailed/DisplayName, mêmes Save/LoadState, mêmes 4 méthodes d'éditeur natif.
    /// Le pipeline downstream (<c>TimelinePlayer.TrySetupMeltySynth</c> et
    /// <c>TimelinePlayer.RenderTrackSlice</c>) branche l'un ou l'autre au choix selon l'extension du chemin.
    ///
    /// **MIDI VST3 vs VST2** : au lieu du <c>ProcessEvents</c>/<c>VstMidiEvent</c>, VST3 utilise
    /// <see cref="IEventList"/> avec des <see cref="Vst3Event"/> typés (kNoteOnEvent, kNoteOffEvent,
    /// kPolyPressureEvent). Les CC/pitch bend/program change n'ont PAS d'équivalent natif direct — le
    /// SDK les route via <c>IMidiMapping::getMidiControllerAssignment</c> qui rend l'id de paramètre
    /// à modifier via <c>IParameterChanges</c>. Cette v1 ignore CC/PC/PB (TODO Phase 5) — les notes
    /// suffisent à jouer un synthé mélodique.
    ///
    /// **Politique de crash** identique à <see cref="VstInstrument"/> : bypass permanent (piste muette)
    /// sur toute exception, pas de re-load automatique.
    /// </summary>
    public sealed class Vst3Instrument : IVstInstrumentHost
    {
        public string PluginPath { get; }
        public bool IsLoaded => _component != null && !_failed;
        public bool IsFailed => _failed;

        public string DisplayName
        {
            get
            {
                if (!string.IsNullOrEmpty(_effectName)) return _effectName;
                if (!string.IsNullOrEmpty(PluginPath)) return System.IO.Path.GetFileNameWithoutExtension(PluginPath);
                return "VST3i";
            }
        }

        readonly int _sampleRate;
        Vst3ModuleLoader _loader;
        IComponent _component;
        IntPtr _componentPtr;                // FUnknown* du component — gardé pour QI IConnectionPoint
        IntPtr _controllerPtr;               // FUnknown* du controller (si séparé) — gardé pour connect() + QI
        IAudioProcessor _processor;
        IEditController _controller;
        IPlugView _view;
        Vst3PlugFrame _frame;                // CCW IPlugFrame — beaucoup de VST3 refusent de rendre sans
        IntPtr _framePtr;
        Vst3EventList _events;
        IntPtr _eventsPtr;
        bool _failed;
        bool _processingStarted;
        bool _active;
        string _pendingState;
        string _effectName;
        int _allocFrames;

        // Unmanaged buffers — VSTi = 0 in / 1 stereo out, mais on alloue quand même 1 bus in stéréo silencieux
        // (certains plugins refusent le setBusArrangements avec 0 input).
        IntPtr _inBusPtr, _outBusPtr;
        IntPtr _inChanTblPtr, _outChanTblPtr;
        IntPtr _inLPtr, _inRPtr, _outLPtr, _outRPtr;

        readonly object _lock = new object();

        public Vst3Instrument(string pluginPath, int sampleRate)
        {
            PluginPath = pluginPath;
            _sampleRate = sampleRate <= 0 ? 44100 : sampleRate;
        }

        // ================================================================================================
        // Chargement / setup
        // ================================================================================================

        void EnsureLoaded(int blockSize)
        {
            if (_component != null || _failed) return;
            if (string.IsNullOrEmpty(PluginPath))
            {
                _failed = true;
                return;
            }
            try
            {
                _loader = new Vst3ModuleLoader();
                _loader.Load(PluginPath);
                var factory = _loader.Factory;
                var factory2 = _loader.Factory2;

                // Chercher la première classe kAudioEffectClass déclarant subCategory "Instrument".
                // Fallback : première AudioEffectClass tout court (certains VSTi omettent la tag).
                int n = factory.countClasses();
                Guid chosenCid = Guid.Empty;
                Guid firstAny = Guid.Empty;
                string firstAnyName = null;
                for (int i = 0; i < n; i++)
                {
                    if (factory2 != null)
                    {
                        if (factory2.getClassInfo2(i, out var info2) != 0) continue;
                        if (!string.Equals(info2.Category, Vst3Uids.kVstAudioEffectClass, StringComparison.Ordinal)) continue;
                        if (firstAny == Guid.Empty) { firstAny = info2.ClassId; firstAnyName = info2.Name?.TrimEnd('\0'); }
                        var subs = info2.SubCategories ?? "";
                        if (subs.Contains(Vst3Uids.kInstrumentSubCategory, StringComparison.OrdinalIgnoreCase))
                        {
                            chosenCid = info2.ClassId;
                            _effectName = info2.Name?.TrimEnd('\0');
                            break;
                        }
                    }
                    else
                    {
                        if (factory.getClassInfo(i, out var info) != 0) continue;
                        if (!string.Equals(info.Category, Vst3Uids.kVstAudioEffectClass, StringComparison.Ordinal)) continue;
                        if (firstAny == Guid.Empty) { firstAny = info.ClassId; firstAnyName = info.Name?.TrimEnd('\0'); }
                    }
                }
                if (chosenCid == Guid.Empty)
                {
                    chosenCid = firstAny;
                    _effectName = firstAnyName;
                }
                if (chosenCid == Guid.Empty) throw new InvalidOperationException("No audio effect class in factory");

                CreateComponent(factory, chosenCid, out _componentPtr, out _component);
                _processor = (IAudioProcessor)_component;

                TryFetchController(factory);

                int r = _component.initialize(Vst3.Vst3HostApplication.GetPtr());
                if (r != Vst3Enums.kResultOk && r != Vst3Enums.kResultTrue)
                    throw new InvalidOperationException($"IComponent.initialize failed (tresult=0x{r:X8})");

                // Dual-component : connect + sync état initial. Sans ça, plein de plugins renvoient un
                // IPlugView null à createView() (le controller n'a jamais reçu l'état du component et
                // refuse d'instancier son éditeur).
                ConnectAndSyncControllerIfSeparate();

                SetInstrumentBusArrangement();

                var setup = new ProcessSetup
                {
                    ProcessMode = Vst3Enums.kRealtime,
                    SymbolicSampleSize = Vst3Enums.kSample32,
                    MaxSamplesPerBlock = blockSize,
                    SampleRate = _sampleRate,
                };
                _processor.setupProcessing(ref setup);

                // Un VSTi doit activer son bus event (index 0, direction kInput) pour recevoir des notes.
                _component.activateBus(Vst3Enums.kEvent, Vst3Enums.kInput, 0, 1);
                // Bus audio out : index 0. L'input audio est laissé désactivé — la plupart des VSTi n'en ont pas.
                _component.activateBus(Vst3Enums.kAudio, Vst3Enums.kOutput, 0, 1);

                if (!string.IsNullOrEmpty(_pendingState))
                {
                    try
                    {
                        var bytes = Convert.FromBase64String(_pendingState);
                        using (var bs = new Vst3BStream(bytes))
                        {
                            var bsPtr = Marshal.GetComInterfaceForObject(bs, typeof(IBStream));
                            try { _component.setState(bsPtr); }
                            finally { Marshal.Release(bsPtr); }
                        }
                    }
                    catch { }
                    _pendingState = null;
                }

                _component.setActive(1); _active = true;
                _processor.setProcessing(1); _processingStarted = true;

                _events = new Vst3EventList();
                _eventsPtr = Marshal.GetComInterfaceForObject(_events, typeof(IEventList));

                AllocBuffers(blockSize);
            }
            catch
            {
                _failed = true;
                DisposeInternal();
            }
        }

        static void CreateComponent(IPluginFactory factory, Guid cid, out IntPtr rawPtr, out IComponent comp)
        {
            var cidBytes = cid.ToByteArray();
            var iidBytes = new Guid(Vst3Uids.IComponent).ToByteArray();
            var cidH = GCHandle.Alloc(cidBytes, GCHandleType.Pinned);
            var iidH = GCHandle.Alloc(iidBytes, GCHandleType.Pinned);
            try
            {
                int r = factory.createInstance(cidH.AddrOfPinnedObject(), iidH.AddrOfPinnedObject(), out rawPtr);
                if (r != Vst3Enums.kResultOk || rawPtr == IntPtr.Zero)
                    throw new InvalidOperationException($"createInstance(IComponent) failed (tresult=0x{r:X8})");
            }
            finally { cidH.Free(); iidH.Free(); }
            comp = (IComponent)Marshal.GetObjectForIUnknown(rawPtr);
            Marshal.Release(rawPtr);
        }

        void TryFetchController(IPluginFactory factory)
        {
            try
            {
                _controller = _component as IEditController;
                if (_controller != null) { _controllerPtr = IntPtr.Zero; return; }  // même objet → pas de ptr séparé, connect skip
            }
            catch { }
            try
            {
                var cidBytes = new byte[16];
                if (_component.getControllerClassId(cidBytes) != Vst3Enums.kResultOk) return;
                var cid = new Guid(cidBytes);
                if (cid == Guid.Empty) return;
                var iidBytes = new Guid(Vst3Uids.IEditController).ToByteArray();
                var cidH = GCHandle.Alloc(cidBytes, GCHandleType.Pinned);
                var iidH = GCHandle.Alloc(iidBytes, GCHandleType.Pinned);
                try
                {
                    int r = factory.createInstance(cidH.AddrOfPinnedObject(), iidH.AddrOfPinnedObject(), out _controllerPtr);
                    if (r != Vst3Enums.kResultOk || _controllerPtr == IntPtr.Zero) return;
                }
                finally { cidH.Free(); iidH.Free(); }
                _controller = (IEditController)Marshal.GetObjectForIUnknown(_controllerPtr);
                // NB : on NE libère PAS _controllerPtr ici — on le garde pour QI IConnectionPoint (Vst3Effect fait pareil).
                try { _controller.initialize(Vst3.Vst3HostApplication.GetPtr()); } catch { }
            }
            catch { _controller = null; }
        }

        /// <summary>Cf. <c>Vst3Effect.ConnectAndSyncControllerIfSeparate</c> — même problème sur les VSTi
        /// dual-component : sans <c>connect()</c> bidirectionnel entre component et controller, <c>createView</c>
        /// retourne souvent null. No-op si single-component ou pas d'IConnectionPoint.</summary>
        void ConnectAndSyncControllerIfSeparate()
        {
            if (_controller == null) return;
            if (ReferenceEquals(_controller, _component)) return;
            if (_componentPtr == IntPtr.Zero || _controllerPtr == IntPtr.Zero) return;

            var cpIid = new Guid(Vst3Uids.IConnectionPoint);
            IntPtr compCp = IntPtr.Zero, ctrlCp = IntPtr.Zero;
            try
            {
                Marshal.QueryInterface(_componentPtr, ref cpIid, out compCp);
                cpIid = new Guid(Vst3Uids.IConnectionPoint);
                Marshal.QueryInterface(_controllerPtr, ref cpIid, out ctrlCp);
                if (compCp != IntPtr.Zero && ctrlCp != IntPtr.Zero)
                {
                    var compCpRcw = (IConnectionPoint)Marshal.GetObjectForIUnknown(compCp);
                    var ctrlCpRcw = (IConnectionPoint)Marshal.GetObjectForIUnknown(ctrlCp);
                    try { compCpRcw.connect(ctrlCp); } catch { }
                    try { ctrlCpRcw.connect(compCp); } catch { }
                }
            }
            catch { }
            finally
            {
                if (compCp != IntPtr.Zero) Marshal.Release(compCp);
                if (ctrlCp != IntPtr.Zero) Marshal.Release(ctrlCp);
            }

            try
            {
                using (var bs = new Vst3BStream(new byte[0]))
                {
                    var bsPtr = Marshal.GetComInterfaceForObject(bs, typeof(IBStream));
                    try
                    {
                        if (_component.getState(bsPtr) == Vst3Enums.kResultOk)
                        {
                            bs.Rewind();
                            try { _controller.setComponentState(bsPtr); } catch { }
                        }
                    }
                    finally { Marshal.Release(bsPtr); }
                }
            }
            catch { }
        }

        void SetInstrumentBusArrangement()
        {
            // Un VSTi accepte typiquement kEmpty en input (pas d'audio in) et kStereo en output.
            // Certains plugins bugués n'aiment pas kEmpty : on essaie d'abord empty/stereo, puis stereo/stereo.
            var noneIn = new ulong[] { Vst3Enums.kEmpty };
            var stereoIn = new ulong[] { Vst3Enums.kStereo };
            var stereoOut = new ulong[] { Vst3Enums.kStereo };
            var h1 = GCHandle.Alloc(noneIn, GCHandleType.Pinned);
            var h2 = GCHandle.Alloc(stereoOut, GCHandleType.Pinned);
            int r;
            try { r = _processor.setBusArrangements(h1.AddrOfPinnedObject(), 1, h2.AddrOfPinnedObject(), 1); }
            finally { h1.Free(); h2.Free(); }
            if (r != Vst3Enums.kResultOk && r != Vst3Enums.kResultTrue)
            {
                var h3 = GCHandle.Alloc(stereoIn, GCHandleType.Pinned);
                var h4 = GCHandle.Alloc(stereoOut, GCHandleType.Pinned);
                try { _processor.setBusArrangements(h3.AddrOfPinnedObject(), 1, h4.AddrOfPinnedObject(), 1); }
                finally { h3.Free(); h4.Free(); }
            }
        }

        void AllocBuffers(int frames)
        {
            if (frames <= 0) frames = 512;
            if (_allocFrames == frames && _outBusPtr != IntPtr.Zero) return;
            FreeBuffers();

            int busSize = Marshal.SizeOf<AudioBusBuffers>();
            _inBusPtr = Marshal.AllocHGlobal(busSize);
            _outBusPtr = Marshal.AllocHGlobal(busSize);
            _inChanTblPtr = Marshal.AllocHGlobal(IntPtr.Size * 2);
            _outChanTblPtr = Marshal.AllocHGlobal(IntPtr.Size * 2);
            int sampleBytes = sizeof(float) * frames;
            _inLPtr = Marshal.AllocHGlobal(sampleBytes);
            _inRPtr = Marshal.AllocHGlobal(sampleBytes);
            _outLPtr = Marshal.AllocHGlobal(sampleBytes);
            _outRPtr = Marshal.AllocHGlobal(sampleBytes);

            Marshal.WriteIntPtr(_inChanTblPtr, 0, _inLPtr);
            Marshal.WriteIntPtr(_inChanTblPtr, IntPtr.Size, _inRPtr);
            Marshal.WriteIntPtr(_outChanTblPtr, 0, _outLPtr);
            Marshal.WriteIntPtr(_outChanTblPtr, IntPtr.Size, _outRPtr);

            Marshal.StructureToPtr(new AudioBusBuffers { NumChannels = 2, SilenceFlags = 0, ChannelBuffers = _inChanTblPtr }, _inBusPtr, false);
            Marshal.StructureToPtr(new AudioBusBuffers { NumChannels = 2, SilenceFlags = 0, ChannelBuffers = _outChanTblPtr }, _outBusPtr, false);

            _allocFrames = frames;
        }

        void FreeBuffers()
        {
            void F(ref IntPtr p) { if (p != IntPtr.Zero) { Marshal.FreeHGlobal(p); p = IntPtr.Zero; } }
            F(ref _inBusPtr); F(ref _outBusPtr);
            F(ref _inChanTblPtr); F(ref _outChanTblPtr);
            F(ref _inLPtr); F(ref _inRPtr); F(ref _outLPtr); F(ref _outRPtr);
            _allocFrames = 0;
        }

        // ================================================================================================
        // API MIDI (surface identique à VstInstrument)
        // ================================================================================================

        public void NoteOn(int channel, int note, int velocity)
        {
            if (_failed) return;
            var e = new Vst3Event
            {
                BusIndex = 0,
                SampleOffset = 0,
                PpqPosition = 0,
                Flags = 0,
                Type = Vst3Enums.kNoteOnEvent,
                NoteOn_Channel = (short)(channel & 0x0F),
                NoteOn_Pitch = (short)Math.Max(0, Math.Min(127, note)),
                NoteOn_Tuning = 0f,
                NoteOn_Velocity = Math.Max(1, Math.Min(127, velocity)) / 127f,
                NoteOn_Length = 0,
                NoteOn_NoteId = -1,
            };
            _events?.AddSafe(e);
        }

        public void NoteOff(int channel, int note)
        {
            if (_failed) return;
            var e = new Vst3Event
            {
                BusIndex = 0,
                SampleOffset = 0,
                PpqPosition = 0,
                Flags = 0,
                Type = Vst3Enums.kNoteOffEvent,
                NoteOff_Channel = (short)(channel & 0x0F),
                NoteOff_Pitch = (short)Math.Max(0, Math.Min(127, note)),
                NoteOff_Velocity = 0f,
                NoteOff_NoteId = -1,
                NoteOff_Tuning = 0f,
            };
            _events?.AddSafe(e);
        }

        /// <summary>CC ignoré en v1 (VST3 route les CC via IMidiMapping → IParameterChanges — TODO Phase 5).</summary>
        public void ProcessMidiCC(int channel, int cc, int value) { /* TODO */ }

        /// <summary>Program change ignoré en v1 (VST3 utilise IUnitInfo::selectProgram — TODO Phase 5).</summary>
        public void ProcessProgramChange(int channel, int program) { /* TODO */ }

        /// <summary>Pitch bend ignoré en v1 (idem CC — routage IMidiMapping requis). Signature préservée
        /// pour compat avec le pipeline (cf. <see cref="VstInstrument.SetPitchBend"/>).</summary>
        public void SetPitchBend(int channel, int value) { /* TODO */ }

        // ================================================================================================
        // Rendu (surface identique à VstInstrument.Render)
        // ================================================================================================

        public void Render(Span<float> left, Span<float> right)
        {
            int frames = left.Length;
            if (_failed || frames <= 0 || right.Length != frames) { left.Clear(); right.Clear(); return; }
            lock (_lock)
            {
                try
                {
                    EnsureLoaded(frames);
                    if (_processor == null || _failed) { left.Clear(); right.Clear(); return; }
                    if (_allocFrames != frames)
                    {
                        try { _processor.setProcessing(0); } catch { }
                        try { _component.setActive(0); } catch { }
                        var setup = new ProcessSetup
                        {
                            ProcessMode = Vst3Enums.kRealtime,
                            SymbolicSampleSize = Vst3Enums.kSample32,
                            MaxSamplesPerBlock = frames,
                            SampleRate = _sampleRate,
                        };
                        try { _processor.setupProcessing(ref setup); } catch { }
                        try { _component.setActive(1); } catch { }
                        try { _processor.setProcessing(1); } catch { }
                        AllocBuffers(frames);
                    }

                    // Entrée silence (VSTi n'utilise pas l'input audio).
                    for (int i = 0; i < frames; i++) { Marshal.WriteInt32(_inLPtr, i * 4, 0); Marshal.WriteInt32(_inRPtr, i * 4, 0); }

                    var pd = new ProcessData
                    {
                        ProcessMode = Vst3Enums.kRealtime,
                        SymbolicSampleSize = Vst3Enums.kSample32,
                        NumSamples = frames,
                        NumInputs = 1,
                        NumOutputs = 1,
                        Inputs = _inBusPtr,
                        Outputs = _outBusPtr,
                        InputParameterChanges = IntPtr.Zero,
                        OutputParameterChanges = IntPtr.Zero,
                        InputEvents = _eventsPtr,
                        OutputEvents = IntPtr.Zero,
                        ProcessContext = IntPtr.Zero,
                    };
                    _processor.process(ref pd);

                    // Consommer les events pour ne pas les rejouer au prochain buffer
                    _events?.Clear();

                    // Copie output unmanaged → Span, sans allocation intermédiaire
                    CopyUnmanagedToSpan(_outLPtr, left);
                    CopyUnmanagedToSpan(_outRPtr, right);
                }
                catch
                {
                    _failed = true;
                    left.Clear(); right.Clear();
                }
            }
        }

        static unsafe void CopyUnmanagedToSpan(IntPtr src, Span<float> dst)
        {
            var srcSpan = new Span<float>((void*)src, dst.Length);
            srcSpan.CopyTo(dst);
        }

        // ================================================================================================
        // Persistance
        // ================================================================================================

        public string SaveState()
        {
            if (_component == null || _failed) return _pendingState;
            try
            {
                using (var bs = new Vst3BStream())
                {
                    var bsPtr = Marshal.GetComInterfaceForObject(bs, typeof(IBStream));
                    try { _component.getState(bsPtr); }
                    finally { Marshal.Release(bsPtr); }
                    var bytes = bs.ToArray();
                    if (bytes == null || bytes.Length == 0) return null;
                    return Convert.ToBase64String(bytes);
                }
            }
            catch { return null; }
        }

        public void LoadState(string state)
        {
            _pendingState = state;
            if (_component != null && !_failed && !string.IsNullOrEmpty(state))
            {
                try
                {
                    var bytes = Convert.FromBase64String(state);
                    lock (_lock)
                    {
                        using (var bs = new Vst3BStream(bytes))
                        {
                            var bsPtr = Marshal.GetComInterfaceForObject(bs, typeof(IBStream));
                            try { _component.setState(bsPtr); }
                            finally { Marshal.Release(bsPtr); }
                        }
                    }
                    _pendingState = null;
                }
                catch { }
            }
        }

        // ================================================================================================
        // Editor
        // ================================================================================================

        public bool EnsureOpenedSync(int blockSize)
        {
            lock (_lock)
            {
                EnsureLoaded(blockSize > 0 ? blockSize : 512);
                return _component != null && !_failed;
            }
        }

        System.Drawing.Size _lastEditorSize;
        public System.Drawing.Size GetEditorSize()
        {
            EnsureView();
            if (_view == null || _failed) return _lastEditorSize;
            try
            {
                if (_view.getSize(out var rc) == Vst3Enums.kResultOk)
                {
                    _lastEditorSize = new System.Drawing.Size(rc.Width, rc.Height);
                    return _lastEditorSize;
                }
            }
            catch { }
            return _lastEditorSize;
        }

        static IPlugView WrapView(IntPtr p)
        {
            var obj = Marshal.GetObjectForIUnknown(p);
            Marshal.Release(p);
            return (IPlugView)obj;
        }

        void EnsureView()
        {
            if (_view != null || _controller == null || _failed) return;
            try
            {
                var raw = _controller.createView(Vst3Uids.ViewTypeEditor);
                if (raw != IntPtr.Zero) _view = WrapView(raw);
            }
            catch { _view = null; }
        }

        public bool OpenEditor(IntPtr parentHwnd)
        {
            if (_controller == null || _failed) return false;
            try
            {
                EnsureView();
                if (_view == null) return false;
                if (_view.isPlatformTypeSupported(Vst3Uids.kPlatformTypeHWND) != Vst3Enums.kResultOk) return false;
                // IPlugFrame non-null indispensable pour bon nombre de plugins VST3 (sinon GUI noire).
                if (_frame == null) _frame = new Vst3PlugFrame();
                if (_framePtr == IntPtr.Zero) _framePtr = Marshal.GetComInterfaceForObject(_frame, typeof(IPlugFrame));
                try { _view.setFrame(_framePtr); } catch { }
                Vst3.Vst3EditorHelpers.TrySetContentScaleFactor(_view, parentHwnd);
                int r = _view.attached(parentHwnd, Vst3Uids.kPlatformTypeHWND);
                if (r != Vst3Enums.kResultOk) return false;
                try { if (_view.getSize(out var rc0) == Vst3Enums.kResultOk) { _view.onSize(ref rc0); } } catch { }
                return true;
            }
            catch { return false; }
        }

        public void CloseEditor()
        {
            if (_view == null) return;
            try { _view.removed(); } catch { }
            try { _view.setFrame(IntPtr.Zero); } catch { }
            try { Marshal.ReleaseComObject(_view); } catch { }
            _view = null;
            if (_framePtr != IntPtr.Zero) { try { Marshal.Release(_framePtr); } catch { } _framePtr = IntPtr.Zero; }
            _frame = null;
        }

        public void EditorIdle() { /* VST3 : pas d'idle explicite requis */ }

        // ================================================================================================
        // Disposal
        // ================================================================================================

        void DisposeInternal()
        {
            try { if (_view != null) { try { _view.removed(); } catch { } try { Marshal.ReleaseComObject(_view); } catch { } _view = null; } } catch { }
            if (_eventsPtr != IntPtr.Zero) { try { Marshal.Release(_eventsPtr); } catch { } _eventsPtr = IntPtr.Zero; }
            _events = null;
            try
            {
                if (_processor != null && _processingStarted) { try { _processor.setProcessing(0); } catch { } _processingStarted = false; }
                if (_component != null && _active) { try { _component.setActive(0); } catch { } _active = false; }
                if (_controller != null && !ReferenceEquals(_controller, _component))
                {
                    try { _controller.terminate(); } catch { }
                    try { Marshal.ReleaseComObject(_controller); } catch { }
                }
                _controller = null;
                if (_component != null)
                {
                    try { _component.terminate(); } catch { }
                    try { Marshal.ReleaseComObject(_component); } catch { }
                }
                _component = null;
                _processor = null;
            }
            catch { }
            FreeBuffers();
            if (_loader != null) { try { _loader.Dispose(); } catch { } _loader = null; }
        }

        public void Dispose()
        {
            lock (_lock) DisposeInternal();
        }
    }
}
