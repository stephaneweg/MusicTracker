using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using MusicTracker.Engine.Timeline.Vst3;
using MusicTracker.Engine.Timeline.Vst3.Interop;

namespace MusicTracker.Engine.Timeline.Effects
{
    /// <summary>
    /// Effet d'insert qui héberge un plugin VST3 via P/Invoke direct sur le SDK Steinberg. Symétrique de
    /// <see cref="VstEffect"/> (VST2) au sens de la surface publique : implémente <see cref="IAudioEffect"/>
    /// et <see cref="IVstEditorHost"/> pour que le pipeline audio ET la fenêtre native soient inchangés en aval.
    ///
    /// **Cycle de vie VST3** :
    /// <list type="number">
    ///   <item>chargement paresseux (1er <see cref="Process"/>) — comme le VST2 pour éviter de figer l'UI ;</item>
    ///   <item>Vst3ModuleLoader.Load → factory ;</item>
    ///   <item>createInstance(kAudioEffectClass, IComponent) → IComponent ;</item>
    ///   <item>QI IAudioProcessor + (optionnel) IEditController — si séparé, on createInstance le controller ;</item>
    ///   <item>initialize(null host), setBusArrangements(stereo/stereo), setupProcessing, activateBus, setActive(true), setProcessing(true) ;</item>
    ///   <item>Process : construit <see cref="ProcessData"/> pointant sur nos buffers unmanaged, appelle <see cref="IAudioProcessor.process"/> ;</item>
    ///   <item>Editor : createView("editor") → IPlugView.attached(hwnd, "HWND").</item>
    /// </list>
    ///
    /// **Politique de crash** identique VST2 : tout jet interne passe l'insert en bypass permanent
    /// (<see cref="IsFailed"/> = true) pour cette instance. Un crash natif (AV C++) tue le process — trade-off
    /// assumé pour un hôte in-process.
    ///
    /// **Non couvert dans cette v1** :
    /// <list type="bullet">
    ///   <item>IConnectionPoint entre component séparé et controller (messages) — beaucoup de plugins UI-only l'exigent, on peut l'ajouter ensuite ;</item>
    ///   <item>ParameterChanges live vers/depuis l'hôte (l'UI ne pilote pas encore les paramètres — c'est la GUI native qui a la main) ;</item>
    ///   <item>ProcessContext (transport info : play/stop/tempo/ppq) — l'insert reçoit un pointeur null, la plupart des inserts s'en fichent.</item>
    /// </list>
    /// </summary>
    public sealed class Vst3Effect : IAudioEffect, IVstEditorHost, IDisposable
    {
        public string Kind => "vst3";

        /// <summary>Chemin absolu vers le .vst3 (fichier ou dossier bundle). Doit être posé AVANT le premier Process.</summary>
        public string PluginPath { get; set; }

        public string DisplayName
        {
            get
            {
                if (!string.IsNullOrEmpty(_effectName)) return _effectName;
                if (!string.IsNullOrEmpty(PluginPath)) return System.IO.Path.GetFileNameWithoutExtension(PluginPath);
                return "VST3";
            }
        }

        public bool IsLoaded => _component != null && !_failed;
        public bool IsFailed => _failed;

        readonly int _sampleRate;
        Vst3ModuleLoader _loader;
        IComponent _component;
        IAudioProcessor _processor;
        IEditController _controller;
        IntPtr _componentPtr;    // FUnknown* du component (utile pour QI IEditController)
        IntPtr _controllerPtr;
        IPlugView _view;
        Vst3PlugFrame _frame;   // callback host que le plugin appelle pour demander un resize
        IntPtr _framePtr;        // COM pointer du CCW ; libéré au CloseEditor
        bool _failed;
        bool _processingStarted;
        bool _active;
        string _pendingState;
        string _effectName;
        int _allocFrames;

        // ---- unmanaged process buffers ---------------------------------------------------------------
        // Layout : 1 bus stéréo in + 1 bus stéréo out, sample32.
        // _inBusPtr → AudioBusBuffers { NumChannels=2, Pad=0, SilenceFlags=0, ChannelBuffers=_inChanTblPtr }
        // _inChanTblPtr → float*[2] { _inLPtr, _inRPtr }
        // _inLPtr, _inRPtr → float[frames]  (unmanaged)
        // Idem pour _out*. Réalloués si frames change entre 2 Process.
        IntPtr _inBusPtr;
        IntPtr _outBusPtr;
        IntPtr _inChanTblPtr;
        IntPtr _outChanTblPtr;
        IntPtr _inLPtr, _inRPtr, _outLPtr, _outRPtr;

        readonly object _lock = new object();

        public Vst3Effect(int sampleRate)
        {
            _sampleRate = sampleRate <= 0 ? 44100 : sampleRate;
        }

        // ================================================================================================
        // Loading / setup
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

                // Trouver la première classe kAudioEffectClass qui n'est pas Instrument (pour l'insert).
                // Si aucune n'est explicitement effect, on prend la 1re AudioEffectClass tout court —
                // certains plugins ne remplissent pas la sous-catégorie.
                int n = factory.countClasses();
                Guid chosenCid = Guid.Empty;
                for (int i = 0; i < n; i++)
                {
                    if (factory2 != null)
                    {
                        if (factory2.getClassInfo2(i, out var info2) != 0) continue;
                        if (!string.Equals(info2.Category, Vst3Uids.kVstAudioEffectClass, StringComparison.Ordinal)) continue;
                        var subs = info2.SubCategories ?? "";
                        if (subs.Contains(Vst3Uids.kInstrumentSubCategory, StringComparison.OrdinalIgnoreCase))
                            continue;
                        chosenCid = info2.ClassId;
                        _effectName = info2.Name?.TrimEnd('\0');
                        break;
                    }
                    else
                    {
                        if (factory.getClassInfo(i, out var info) != 0) continue;
                        if (!string.Equals(info.Category, Vst3Uids.kVstAudioEffectClass, StringComparison.Ordinal)) continue;
                        chosenCid = info.ClassId;
                        _effectName = info.Name?.TrimEnd('\0');
                        break;
                    }
                }
                if (chosenCid == Guid.Empty) throw new InvalidOperationException("No suitable audio effect class in factory");

                // createInstance(cid, IComponent::iid) → FUnknown*
                CreateComponent(factory, chosenCid, out _componentPtr, out _component);

                // IAudioProcessor : QI sur le même objet (une classe VST3 implémente les 2)
                _processor = (IAudioProcessor)_component;

                // IEditController : peut être le même objet (single-component effect) OU une classe séparée
                // (getControllerClassId retourne l'id). Best-effort — un plugin sans editor tourne quand même.
                TryFetchController(factory);

                // Initialize
                int r = _component.initialize(Vst3.Vst3HostApplication.GetPtr());
                if (r != Vst3Enums.kResultOk && r != Vst3Enums.kResultTrue)
                    throw new InvalidOperationException($"IComponent.initialize failed (tresult=0x{r:X8})");

                // Dual-component : connect + sync état initial. Sans ça, plein de plugins renvoient un
                // IPlugView null à createView() (controller n'a jamais reçu l'état du component).
                ConnectAndSyncControllerIfSeparate();

                // BusArrangements : 1 stereo in / 1 stereo out
                SetStereoBusArrangement();

                // setupProcessing
                var setup = new ProcessSetup
                {
                    ProcessMode = Vst3Enums.kRealtime,
                    SymbolicSampleSize = Vst3Enums.kSample32,
                    MaxSamplesPerBlock = blockSize,
                    SampleRate = _sampleRate,
                };
                _processor.setupProcessing(ref setup);

                // Activer main audio in + out (bus index 0)
                _component.activateBus(Vst3Enums.kAudio, Vst3Enums.kInput, 0, 1);
                _component.activateBus(Vst3Enums.kAudio, Vst3Enums.kOutput, 0, 1);

                // State pending si LoadState() a été appelé avant l'ouverture réelle
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
                        // Certains plugins veulent aussi setComponentState sur le controller.
                        if (_controller != null)
                        {
                            var bytes2 = Convert.FromBase64String(_pendingState);
                            using (var bs2 = new Vst3BStream(bytes2))
                            {
                                var bsPtr = Marshal.GetComInterfaceForObject(bs2, typeof(IBStream));
                                try { _controller.setComponentState(bsPtr); } catch { }
                                finally { Marshal.Release(bsPtr); }
                            }
                        }
                    }
                    catch { /* setState refusé → état par défaut, on continue */ }
                    _pendingState = null;
                }

                _component.setActive(1); _active = true;
                _processor.setProcessing(1); _processingStarted = true;

                AllocBuffers(blockSize);
            }
            catch
            {
                _failed = true;
                DisposeInternal();
            }
        }

        /// <summary>Instancie une classe VST3 par son cid et retourne FUnknown* + RCW typé IComponent.
        /// GetObjectForIUnknown fait un AddRef ; on relâche l'AddRef initial de createInstance pour rester à ref=1.</summary>
        static void CreateComponent(IPluginFactory factory, Guid cid, out IntPtr rawPtr, out IComponent comp)
        {
            var cidBytes = cid.ToByteArray();
            var iidBytes = new Guid(Vst3Uids.IComponent).ToByteArray();
            var cidHandle = GCHandle.Alloc(cidBytes, GCHandleType.Pinned);
            var iidHandle = GCHandle.Alloc(iidBytes, GCHandleType.Pinned);
            try
            {
                int r = factory.createInstance(cidHandle.AddrOfPinnedObject(), iidHandle.AddrOfPinnedObject(), out rawPtr);
                if (r != Vst3Enums.kResultOk || rawPtr == IntPtr.Zero)
                    throw new InvalidOperationException($"createInstance(IComponent) failed (tresult=0x{r:X8})");
            }
            finally { cidHandle.Free(); iidHandle.Free(); }
            comp = (IComponent)Marshal.GetObjectForIUnknown(rawPtr);
            Marshal.Release(rawPtr); // compensate AddRef done by GetObjectForIUnknown; RCW now owns the sole ref
        }

        /// <summary>Sur un plugin dual-component (component + controller = classes séparées), connecte les
        /// deux points de connexion et copie l'état du component vers le controller. Sans ça, beaucoup de
        /// plugins refusent que <c>createView</c> retourne un IPlugView (le controller est dans un état
        /// « vierge » et sa GUI ne peut pas s'initialiser). No-op si single-component ou pas d'IConnectionPoint.</summary>
        void ConnectAndSyncControllerIfSeparate()
        {
            // Skip : single-component (même objet) ou pas de controller.
            if (_controller == null) return;
            if (ReferenceEquals(_controller, _component)) return;
            if (_controllerPtr == IntPtr.Zero) return;

            // QI IConnectionPoint sur les deux extrémités.
            var cpIid = new Guid(Vst3Uids.IConnectionPoint);
            IntPtr compCp = IntPtr.Zero, ctrlCp = IntPtr.Zero;
            try
            {
                Marshal.QueryInterface(_componentPtr, ref cpIid, out compCp);
                cpIid = new Guid(Vst3Uids.IConnectionPoint); // QueryInterface peut zapper le Guid, on le remet
                Marshal.QueryInterface(_controllerPtr, ref cpIid, out ctrlCp);
                if (compCp != IntPtr.Zero && ctrlCp != IntPtr.Zero)
                {
                    var compCpRcw = (IConnectionPoint)Marshal.GetObjectForIUnknown(compCp);
                    var ctrlCpRcw = (IConnectionPoint)Marshal.GetObjectForIUnknown(ctrlCp);
                    try { compCpRcw.connect(ctrlCp); } catch { }
                    try { ctrlCpRcw.connect(compCp); } catch { }
                }
            }
            catch { /* pas d'IConnectionPoint sur ce plugin — c'est OK, il ne l'implémente juste pas */ }
            finally
            {
                if (compCp != IntPtr.Zero) Marshal.Release(compCp);
                if (ctrlCp != IntPtr.Zero) Marshal.Release(ctrlCp);
            }

            // Sync component → controller : le controller a besoin de l'état pour initialiser sa GUI.
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

        void TryFetchController(IPluginFactory factory)
        {
            // Cas 1 : le component IS le controller (single-component effect, très fréquent)
            try
            {
                _controller = _component as IEditController;
                if (_controller != null) return;
            }
            catch { }
            // Cas 2 : controller class séparée, id renvoyé par getControllerClassId
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
                Marshal.Release(_controllerPtr);
                try { _controller.initialize(Vst3.Vst3HostApplication.GetPtr()); } catch { }
            }
            catch { _controller = null; }
        }

        void SetStereoBusArrangement()
        {
            // SpeakerArrangement stéréo (kSpeakerL | kSpeakerR)
            var stereo = new ulong[] { Vst3Enums.kStereo };
            var h1 = GCHandle.Alloc(stereo, GCHandleType.Pinned);
            var stereoOut = new ulong[] { Vst3Enums.kStereo };
            var h2 = GCHandle.Alloc(stereoOut, GCHandleType.Pinned);
            try
            {
                // Un plugin qui refuse ce layout renverra kResultFalse — on continue quand même
                // (getBusArrangement rendra l'arrangement final choisi par le plugin).
                _processor.setBusArrangements(h1.AddrOfPinnedObject(), 1, h2.AddrOfPinnedObject(), 1);
            }
            finally { h1.Free(); h2.Free(); }
        }

        void AllocBuffers(int frames)
        {
            if (frames <= 0) frames = 512;
            if (_allocFrames == frames && _inBusPtr != IntPtr.Zero) return;
            FreeBuffers();

            int busSize = Marshal.SizeOf<AudioBusBuffers>();
            _inBusPtr = Marshal.AllocHGlobal(busSize);
            _outBusPtr = Marshal.AllocHGlobal(busSize);
            _inChanTblPtr = Marshal.AllocHGlobal(IntPtr.Size * 2);   // float*[2]
            _outChanTblPtr = Marshal.AllocHGlobal(IntPtr.Size * 2);
            int sampleBytes = sizeof(float) * frames;
            _inLPtr = Marshal.AllocHGlobal(sampleBytes);
            _inRPtr = Marshal.AllocHGlobal(sampleBytes);
            _outLPtr = Marshal.AllocHGlobal(sampleBytes);
            _outRPtr = Marshal.AllocHGlobal(sampleBytes);

            // Populate channel tables
            Marshal.WriteIntPtr(_inChanTblPtr, 0 * IntPtr.Size, _inLPtr);
            Marshal.WriteIntPtr(_inChanTblPtr, 1 * IntPtr.Size, _inRPtr);
            Marshal.WriteIntPtr(_outChanTblPtr, 0 * IntPtr.Size, _outLPtr);
            Marshal.WriteIntPtr(_outChanTblPtr, 1 * IntPtr.Size, _outRPtr);

            // Populate AudioBusBuffers structs
            Marshal.StructureToPtr(new AudioBusBuffers
            {
                NumChannels = 2,
                _padding = 0,
                SilenceFlags = 0,
                ChannelBuffers = _inChanTblPtr,
            }, _inBusPtr, false);
            Marshal.StructureToPtr(new AudioBusBuffers
            {
                NumChannels = 2,
                _padding = 0,
                SilenceFlags = 0,
                ChannelBuffers = _outChanTblPtr,
            }, _outBusPtr, false);

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
        // Audio process
        // ================================================================================================

        public void Process(float[] left, float[] right, int frames)
        {
            if (_failed || left == null || right == null || frames <= 0) return;
            lock (_lock)
            {
                try
                {
                    EnsureLoaded(frames);
                    if (_processor == null || _failed) return;
                    if (_allocFrames != frames)
                    {
                        // Changement de taille de bloc : sortir du processing, reconfigurer, ré-entrer.
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

                    // Copie L/R managed → unmanaged
                    Marshal.Copy(left, 0, _inLPtr, frames);
                    Marshal.Copy(right, 0, _inRPtr, frames);

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
                        InputEvents = IntPtr.Zero,
                        OutputEvents = IntPtr.Zero,
                        ProcessContext = IntPtr.Zero,
                    };
                    int r = _processor.process(ref pd);
                    if (r != Vst3Enums.kResultOk && r != Vst3Enums.kResultTrue)
                    {
                        // Certains plugins renvoient kResultFalse quand ils veulent qu'on ignore l'output
                        // (silence). On considère non-OK comme un pass-through, pas un crash.
                        return;
                    }

                    // Copie output unmanaged → L/R
                    Marshal.Copy(_outLPtr, left, 0, frames);
                    Marshal.Copy(_outRPtr, right, 0, frames);
                }
                catch
                {
                    _failed = true;
                }
            }
        }

        public void Reset()
        {
            if (_failed || _processor == null) return;
            lock (_lock)
            {
                try
                {
                    _processor.setProcessing(0);
                    _processor.setProcessing(1);
                }
                catch { _failed = true; }
            }
        }

        // ================================================================================================
        // Persistance (dictionnaire vide + blob binaire)
        // ================================================================================================

        public Dictionary<string, double> Save() => new Dictionary<string, double>();
        public void Load(Dictionary<string, double> data) { /* no-op */ }

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
        // Editor (IVstEditorHost)
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
            if (_controller == null || _failed) return System.Drawing.Size.Empty;
            EnsureView();
            if (_view == null) return _lastEditorSize;
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

        // Helper : conversion IntPtr → IPlugView RCW managed
        static IPlugView WrapView(IntPtr p)
        {
            var obj = Marshal.GetObjectForIUnknown(p);
            Marshal.Release(p); // GetObjectForIUnknown a fait un AddRef, on relâche l'AddRef initial de createView
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
                // Fournir un IPlugFrame AVANT attached() : beaucoup de plugins VST3 refusent de rendre leur GUI
                // (fenêtre noire) si setFrame reçoit null. Le CCW ne fait rien de spécial pour l'instant (le
                // resize plugin-vers-hôte n'est pas encore branché vers la fenêtre WPF hôte — TODO).
                if (_frame == null) _frame = new Vst3PlugFrame();
                if (_framePtr == IntPtr.Zero) _framePtr = Marshal.GetComInterfaceForObject(_frame, typeof(IPlugFrame));
                try { _view.setFrame(_framePtr); } catch { }
                // Négocier le scale DPI AVANT attached : plein de plugins modernes (Surge XT etc.) refusent
                // de peindre leur GUI tant qu'on ne leur a pas dit à quelle échelle rendre.
                Vst3EditorHelpers.TrySetContentScaleFactor(_view, parentHwnd);
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
            try { _view.setFrame(IntPtr.Zero); } catch { } // décoller le frame côté plugin avant de libérer
            try { Marshal.ReleaseComObject(_view); } catch { }
            _view = null;
            if (_framePtr != IntPtr.Zero) { try { Marshal.Release(_framePtr); } catch { } _framePtr = IntPtr.Zero; }
            _frame = null;
        }

        public void EditorIdle() { /* VST3 : pas d'idle explicite — le plugin utilise WM_TIMER natif */ }

        // ================================================================================================
        // Disposal
        // ================================================================================================

        void DisposeInternal()
        {
            try { if (_view != null) { try { _view.removed(); } catch { } try { Marshal.ReleaseComObject(_view); } catch { } _view = null; } } catch { }
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
                _controllerPtr = IntPtr.Zero;
                if (_component != null)
                {
                    try { _component.terminate(); } catch { }
                    try { Marshal.ReleaseComObject(_component); } catch { }
                }
                _component = null;
                _componentPtr = IntPtr.Zero;
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
