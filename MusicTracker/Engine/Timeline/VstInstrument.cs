using System;
using System.Collections.Generic;
using Jacobi.Vst.Core;
using Jacobi.Vst.Host.Interop;
using MusicTracker.Engine.Timeline.Effects;

namespace MusicTracker.Engine.Timeline
{
    /// <summary>
    /// Hôte d'un plugin VST2 UTILISÉ COMME INSTRUMENT (VSTi) : au lieu de traiter un buffer stéréo comme
    /// <see cref="VstEffect"/>, le plugin reçoit du MIDI (note-on / note-off / CC / pitch bend) et PRODUIT
    /// le buffer stéréo qu'on écrit dans la piste. Le pipeline downstream (chaîne d'inserts, mixeur master,
    /// vu-mètres, export) ne voit rien de différent — les samples arrivent d'un synthé, point.
    ///
    /// **Parallèle avec <see cref="VstEffect"/>** : même stub host (<see cref="VstHostCommandStub"/>), même
    /// chargement paresseux au 1er <see cref="Render"/> (le pipeline snapshotte les instruments au démarrage
    /// mais le plugin n'existe pas encore côté audio thread : ça évite de figer l'UI le temps qu'il charge),
    /// même politique de crash (bypass permanent = piste silencieuse plutôt que kill du process). L'API MIDI
    /// s'ajoute par <c>Commands.ProcessEvents</c> AVANT chaque <c>ProcessReplacing</c>.
    ///
    /// **Thread-safety** : les note-on/off arrivent normalement du thread de rendu (via
    /// <c>TimelinePlayer.DispatchSlice</c>), mais aussi potentiellement du thread UI si un jour un mode preview
    /// est ajouté. Le buffer d'events à flusher est protégé par un lock ; le Render entier est aussi sous ce
    /// lock — pas de contention en pratique (audio thread = seul locker chaud).
    /// </summary>
    public sealed class VstInstrument : IVstInstrumentHost
    {
        public string PluginPath { get; }
        public bool IsLoaded => _ctx != null && !_failed;
        public bool IsFailed => _failed;

        /// <summary>Nom du plugin (renseigné après ouverture). Fallback = nom de fichier.</summary>
        public string DisplayName
        {
            get
            {
                if (!string.IsNullOrEmpty(_effectName)) return _effectName;
                if (!string.IsNullOrEmpty(PluginPath)) return System.IO.Path.GetFileNameWithoutExtension(PluginPath);
                return "VSTi";
            }
        }

        readonly int _sampleRate;
        VstPluginContext _ctx;
        VstAudioBufferManager _inMgr, _outMgr;
        VstAudioBuffer[] _inBufs, _outBufs;
        int _currentBlockSize;
        bool _failed;
        string _pendingState;
        string _effectName;
        readonly object _lock = new object();
        // Events MIDI accumulés depuis le dernier Render. Vidés (et posés au plugin) avant chaque
        // ProcessReplacing. Une List simple : les Render sont fréquents (< 100 events par buffer typiquement).
        readonly List<VstEvent> _pendingEvents = new List<VstEvent>();

        public VstInstrument(string pluginPath, int sampleRate)
        {
            PluginPath = pluginPath;
            _sampleRate = sampleRate <= 0 ? 44100 : sampleRate;
        }

        void EnsureLoaded(int blockSize)
        {
            if (_ctx != null || _failed) return;
            if (string.IsNullOrEmpty(PluginPath) || !System.IO.File.Exists(PluginPath))
            {
                _failed = true;
                return;
            }
            try
            {
                var host = new VstHostCommandStub(_sampleRate, blockSize);
                _ctx = VstPluginContext.Create(PluginPath, host);
                var cmd = _ctx.PluginCommandStub.Commands;
                cmd.Open();
                cmd.SetSampleRate(_sampleRate);
                cmd.SetBlockSize(blockSize);
                _currentBlockSize = blockSize;
                cmd.MainsChanged(true);
                cmd.StartProcess();
                try { _effectName = cmd.GetEffectName(); } catch { _effectName = null; }

                if (!string.IsNullOrEmpty(_pendingState))
                {
                    try
                    {
                        var bytes = Convert.FromBase64String(_pendingState);
                        cmd.SetChunk(bytes, false);
                    }
                    catch { }
                    _pendingState = null;
                }
                AllocBuffers(blockSize);
            }
            catch
            {
                _failed = true;
                DisposeContext();
            }
        }

        void AllocBuffers(int blockSize)
        {
            if (_inMgr != null && _outMgr != null && _currentBlockSize == blockSize && _inBufs != null && _outBufs != null)
                return;
            DisposeBuffers();
            // Beaucoup de VSTi n'ont pas d'entrée audio (0 inputs) mais VST.NET tolère un buffer d'entrée fourni.
            // On reste sur 2 canaux comme les effets — c'est ignoré par les instruments purs.
            _inMgr = new VstAudioBufferManager(2, blockSize);
            _outMgr = new VstAudioBufferManager(2, blockSize);
            _inBufs = new VstAudioBuffer[2];
            _outBufs = new VstAudioBuffer[2];
            int i = 0;
            foreach (var b in _inMgr.Buffers) { _inBufs[i++] = b; if (i == 2) break; }
            i = 0;
            foreach (var b in _outMgr.Buffers) { _outBufs[i++] = b; if (i == 2) break; }
            _currentBlockSize = blockSize;
        }

        // ---- API MIDI --------------------------------------------------------------------------------
        //
        // Chaque appel accumule un VstMidiEvent dans _pendingEvents ; ils sont poussés au plugin via
        // Commands.ProcessEvents juste avant le prochain ProcessReplacing (voir Render). Le canal est
        // toujours 0 côté MIDI : un VSTi ne connaît pas la convention "9 = batterie" de MeltySynth ; c'est
        // à l'utilisateur de choisir un plugin de batterie s'il veut du drum sound.
        //
        // Les MidiData sont 4 octets [status|byte1|byte2|reserved], comme spécifié par le SDK VST2.

        public void NoteOn(int channel, int note, int velocity)
        {
            byte st = (byte)(0x90 | (channel & 0x0F));
            byte n = (byte)Math.Max(0, Math.Min(127, note));
            byte v = (byte)Math.Max(1, Math.Min(127, velocity));   // vel 0 = note-off en MIDI
            EnqueueMidi(st, n, v);
        }

        public void NoteOff(int channel, int note)
        {
            byte st = (byte)(0x80 | (channel & 0x0F));
            byte n = (byte)Math.Max(0, Math.Min(127, note));
            EnqueueMidi(st, n, 0);
        }

        public void ProcessMidiCC(int channel, int cc, int value)
        {
            byte st = (byte)(0xB0 | (channel & 0x0F));
            byte c = (byte)Math.Max(0, Math.Min(127, cc));
            byte v = (byte)Math.Max(0, Math.Min(127, value));
            EnqueueMidi(st, c, v);
        }

        public void ProcessProgramChange(int channel, int program)
        {
            byte st = (byte)(0xC0 | (channel & 0x0F));
            byte p = (byte)Math.Max(0, Math.Min(127, program));
            EnqueueMidi(st, p, 0);
        }

        /// <summary>Pitch bend 14-bit : <paramref name="value"/> 0..16383 (8192 = centre). Les valeurs hors plage sont clampées.</summary>
        public void SetPitchBend(int channel, int value)
        {
            int v = Math.Max(0, Math.Min(16383, value));
            byte st = (byte)(0xE0 | (channel & 0x0F));
            byte lsb = (byte)(v & 0x7F);
            byte msb = (byte)((v >> 7) & 0x7F);
            EnqueueMidi(st, lsb, msb);
        }

        void EnqueueMidi(byte status, byte b1, byte b2)
        {
            if (_failed) return;
            // deltaFrames = 0 : les events s'appliquent en tête du prochain buffer. Le renderer découpe déjà par
            // slice temporel (voir TimelinePlayer.ReadMelty) donc la précision buffer suffit — un event tombera
            // au pire un buffer trop tard, soit ~10 ms à 4096 samples/44.1kHz. Comme MeltySynth qui reçoit
            // aussi ses events au début d'un slice.
            var data = new byte[] { status, b1, b2, 0 };
            var ev = new VstMidiEvent(0, 0, 0, data, 0, 0);
            lock (_pendingEvents) _pendingEvents.Add(ev);
        }

        // ---- rendu ------------------------------------------------------------------------------------
        //
        // Écrit <paramref name="left"/>.Length frames dans left/right (stéréo, non-entrelacé). Les buffers
        // d'entrée sont fournis vides — un VSTi les ignore. Politique de crash identique à VstEffect : toute
        // exception passe l'instrument en bypass permanent (piste silencieuse), on ne rejette pas au thread audio.

        public void Render(Span<float> left, Span<float> right)
        {
            int frames = left.Length;
            if (_failed || frames <= 0 || right.Length != frames) { left.Clear(); right.Clear(); return; }
            lock (_lock)
            {
                try
                {
                    // On charge avec un max block size GÉNÉREUX (8192 = ~186ms @ 44.1kHz) une fois pour
                    // toutes. Toggler MainsChanged/SetBlockSize à chaque changement de taille (les slices
                    // varient beaucoup) crashe plein de VST2 natifs (état DSP fragile). Sémantique VST
                    // standard : le plugin peut recevoir moins que le max sans souci.
                    const int MaxBlock = 8192;
                    EnsureLoaded(MaxBlock);
                    if (_ctx == null || _failed) { left.Clear(); right.Clear(); return; }
                    var cmd = _ctx.PluginCommandStub.Commands;
                    if (_currentBlockSize < frames) AllocBuffers(Math.Max(frames, MaxBlock));

                    // Flush des events MIDI accumulés : le plugin reçoit ProcessEvents AVANT ProcessReplacing.
                    // On copie sous _pendingEvents lock pour libérer rapidement l'accumulateur au cas où un
                    // NoteOn arriverait pendant ce Render (rare mais valide).
                    VstEvent[] toSend = null;
                    lock (_pendingEvents)
                    {
                        if (_pendingEvents.Count > 0)
                        {
                            toSend = _pendingEvents.ToArray();
                            _pendingEvents.Clear();
                        }
                    }
                    if (toSend != null)
                    {
                        try { cmd.ProcessEvents(toSend); } catch { }
                    }

                    // Entrée vide (VSTi n'a pas d'input). VST.NET tolère qu'on ne remplisse rien.
                    var iL = _inBufs[0]; var iR = _inBufs[1];
                    for (int i = 0; i < frames; i++) { iL[i] = 0f; iR[i] = 0f; }

                    cmd.ProcessReplacing(_inBufs, _outBufs);

                    // Copie OUT vers les Span cibles.
                    var oL = _outBufs[0]; var oR = _outBufs[1];
                    for (int i = 0; i < frames; i++) { left[i] = oL[i]; right[i] = oR[i]; }
                }
                catch
                {
                    _failed = true;
                    left.Clear(); right.Clear();
                }
            }
        }

        // ---- persistance -----------------------------------------------------------------------------

        public string SaveState()
        {
            if (_ctx == null || _failed) return _pendingState;
            try
            {
                var bytes = _ctx.PluginCommandStub.Commands.GetChunk(false);
                if (bytes == null || bytes.Length == 0) return null;
                return Convert.ToBase64String(bytes);
            }
            catch { return null; }
        }

        public void LoadState(string state)
        {
            _pendingState = state;
            if (_ctx != null && !_failed && !string.IsNullOrEmpty(state))
            {
                try
                {
                    var bytes = Convert.FromBase64String(state);
                    lock (_lock) _ctx.PluginCommandStub.Commands.SetChunk(bytes, false);
                    _pendingState = null;
                }
                catch { }
            }
        }

        // ---- fenêtre native --------------------------------------------------------------------------

        /// <summary>Force le chargement synchrone (utile pour l'UI qui veut ouvrir la GUI sans attendre le 1er buffer audio).</summary>
        public bool EnsureOpenedSync(int blockSize)
        {
            lock (_lock)
            {
                EnsureLoaded(blockSize > 0 ? blockSize : 512);
                return _ctx != null && !_failed;
            }
        }

        public System.Drawing.Size GetEditorSize()
        {
            if (_ctx == null || _failed) return System.Drawing.Size.Empty;
            try
            {
                System.Drawing.Rectangle rc;
                if (_ctx.PluginCommandStub.Commands.EditorGetRect(out rc))
                    return new System.Drawing.Size(rc.Width, rc.Height);
            }
            catch { }
            return System.Drawing.Size.Empty;
        }

        public bool OpenEditor(IntPtr parentHwnd)
        {
            if (_ctx == null || _failed) return false;
            try { return _ctx.PluginCommandStub.Commands.EditorOpen(parentHwnd); }
            catch { return false; }
        }

        public void CloseEditor()
        {
            if (_ctx == null || _failed) return;
            try { _ctx.PluginCommandStub.Commands.EditorClose(); } catch { }
        }

        public void EditorIdle()
        {
            if (_ctx == null || _failed) return;
            try { _ctx.PluginCommandStub.Commands.EditorIdle(); } catch { }
        }

        void DisposeBuffers()
        {
            if (_inMgr != null) { try { _inMgr.Dispose(); } catch { } _inMgr = null; }
            if (_outMgr != null) { try { _outMgr.Dispose(); } catch { } _outMgr = null; }
            _inBufs = null; _outBufs = null;
        }

        void DisposeContext()
        {
            if (_ctx != null)
            {
                try
                {
                    var cmd = _ctx.PluginCommandStub.Commands;
                    try { cmd.StopProcess(); } catch { }
                    try { cmd.MainsChanged(false); } catch { }
                    try { cmd.Close(); } catch { }
                }
                catch { }
                try { _ctx.Dispose(); } catch { }
                _ctx = null;
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                DisposeContext();
                DisposeBuffers();
            }
        }
    }
}
