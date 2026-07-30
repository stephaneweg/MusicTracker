using System;
using System.Collections.Generic;
using Jacobi.Vst.Core;
using Jacobi.Vst.Interop.Host;

namespace MusicTracker.Engine.Timeline.Effects
{
    /// <summary>
    /// Effet d'insert qui héberge un plugin VST2 (via VST.NET / TruDan-VST.NET, LGPL-2.1). Le plugin est chargé
    /// paresseusement (au premier <see cref="Process"/>) parce que le pipeline audio Koton snapshotte les inserts
    /// AU DÉMARRAGE de la lecture — retarder le vrai chargement au thread audio évite deux gros risques :
    /// (1) figer le thread UI le temps du chargement (certains plugins prennent 1-2 s), (2) crasher au chargement
    /// hors du <c>try/catch</c> qui protège <see cref="Process"/>.
    ///
    /// Le plugin est appelé en <c>processReplacing</c> (float32 non-entrelacé, 2 canaux) — c'est exactement le
    /// contrat qu'utilise déjà le renderer Koton. Les buffers d'entrée/sortie sont alloués par
    /// <see cref="VstAudioBufferManager"/> (mémoire non-managée, gérée par VST.NET) et redimensionnés à la volée
    /// si <c>frames</c> change entre deux appels.
    ///
    /// **Politique de crash** : tout jet depuis le plugin est capturé en silence dans <see cref="Process"/> — un
    /// plugin qui explose passe en bypass permanent pour cette instance (pas de rechargement automatique). Le
    /// projet continue de jouer.
    ///
    /// **Persistance** : le PluginPath vit dans <see cref="TrackEffectData.PluginPath"/> ; le chunk d'état interne
    /// est sérialisé en base64 dans <see cref="TrackEffectData.StateBlob"/> via <see cref="SaveState"/> /
    /// <see cref="LoadState"/>. Load(dict) est un no-op délibéré : le pipeline appelle Load à CHAQUE buffer pour
    /// relire les sliders des effets maison, mais un plugin VST a sa propre GUI native qui mute l'état à l'intérieur
    /// du plugin — le dictionnaire de l'hôte n'a aucune vérité à raconter.
    /// </summary>
    public class VstEffect : IAudioEffect, IDisposable
    {
        public string Kind { get { return EffectFactory.VstKind; } }

        /// <summary>Chemin absolu vers le fichier .dll du plugin. Doit être posé AVANT le premier Process (sinon l'effet reste no-op).</summary>
        public string PluginPath { get; set; }

        /// <summary>Nom affichable (renseigné après chargement — le VST expose son nom via <c>GetEffectName</c>). Fallback = nom de fichier.</summary>
        public string DisplayName
        {
            get
            {
                if (!string.IsNullOrEmpty(_effectName)) return _effectName;
                if (!string.IsNullOrEmpty(PluginPath)) return System.IO.Path.GetFileNameWithoutExtension(PluginPath);
                return "VST";
            }
        }

        /// <summary><c>true</c> une fois que le contexte VST est ouvert (utile pour l'UI qui veut savoir si le HWND peut être demandé).</summary>
        public bool IsLoaded { get { return _ctx != null && !_failed; } }

        /// <summary><c>true</c> si le plugin a jeté à un moment (bypass permanent). L'UI peut afficher un badge d'alerte.</summary>
        public bool IsFailed { get { return _failed; } }

        readonly int _sampleRate;
        VstPluginContext _ctx;
        VstAudioBufferManager _inMgr, _outMgr;
        VstAudioBuffer[] _inBufs, _outBufs;
        int _currentBlockSize;
        bool _failed;
        string _pendingState;   // chunk à appliquer une fois le contexte ouvert
        string _effectName;
        readonly object _lock = new object();

        public VstEffect(int sampleRate)
        {
            _sampleRate = sampleRate <= 0 ? 44100 : sampleRate;
        }

        void EnsureLoaded(int blockSize)
        {
            if (_ctx != null || _failed) return;
            if (string.IsNullOrEmpty(PluginPath) || !System.IO.File.Exists(PluginPath))
            {
                _failed = true; // pas de plugin → bypass permanent (projet ouvert sans le .dll)
                return;
            }
            try
            {
                var host = new VstHostCommandStub(_sampleRate, blockSize);
                _ctx = VstPluginContext.Create(PluginPath, host);
                var cmd = _ctx.PluginCommandStub;
                cmd.Open();
                cmd.SetSampleRate(_sampleRate);
                cmd.SetBlockSize(blockSize);
                _currentBlockSize = blockSize;
                cmd.MainsChanged(true);
                cmd.StartProcess();
                try { _effectName = cmd.GetEffectName(); } catch { _effectName = null; }

                // Restaurer l'état sérialisé s'il en existe un (utilisé au chargement d'un .sq qui contenait déjà ce plugin).
                if (!string.IsNullOrEmpty(_pendingState))
                {
                    try
                    {
                        var bytes = Convert.FromBase64String(_pendingState);
                        cmd.SetChunk(bytes, false); // false = bank chunk (état global) ; les plugins l'ignorent si non supporté.
                    }
                    catch { /* plugin qui ne supporte pas SetChunk : on tolère, l'état retombera aux valeurs par défaut */ }
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
            // 2 canaux (stéréo) : plus vaste que la topologie mono du plugin le cas échéant — VST.NET tolère.
            _inMgr = new VstAudioBufferManager(2, blockSize);
            _outMgr = new VstAudioBufferManager(2, blockSize);
            _inBufs = new VstAudioBuffer[2];
            _outBufs = new VstAudioBuffer[2];
            int i = 0;
            foreach (var b in _inMgr) { _inBufs[i++] = b; if (i == 2) break; }
            i = 0;
            foreach (var b in _outMgr) { _outBufs[i++] = b; if (i == 2) break; }
            _currentBlockSize = blockSize;
        }

        public void Process(float[] left, float[] right, int frames)
        {
            if (_failed || left == null || right == null || frames <= 0) return;
            lock (_lock)
            {
                try
                {
                    EnsureLoaded(frames);
                    if (_ctx == null || _failed) return;
                    if (_currentBlockSize != frames)
                    {
                        // Le plugin doit être averti d'un changement de taille de bloc : MainsChanged(false) → SetBlockSize → MainsChanged(true).
                        try
                        {
                            _ctx.PluginCommandStub.MainsChanged(false);
                            _ctx.PluginCommandStub.SetBlockSize(frames);
                            _ctx.PluginCommandStub.MainsChanged(true);
                        }
                        catch { }
                        AllocBuffers(frames);
                    }

                    // Copie IN — VstAudioBuffer expose un indexeur set : plus propre que fouiller le pointeur brut.
                    var iL = _inBufs[0]; var iR = _inBufs[1];
                    for (int i = 0; i < frames; i++) { iL[i] = left[i]; iR[i] = right[i]; }

                    _ctx.PluginCommandStub.ProcessReplacing(_inBufs, _outBufs);

                    // Copie OUT.
                    var oL = _outBufs[0]; var oR = _outBufs[1];
                    for (int i = 0; i < frames; i++) { left[i] = oL[i]; right[i] = oR[i]; }
                }
                catch
                {
                    // Un plugin qui a jeté (accès violation, out-of-memory) est passé en bypass permanent — on ne
                    // rejette pas le crash au thread audio (ça tuerait la lecture) et on ne rechargera pas non plus
                    // (souvent ça re-jette au même endroit). L'utilisateur peut supprimer/remplacer l'insert manuellement.
                    _failed = true;
                }
            }
        }

        public void Reset()
        {
            if (_failed || _ctx == null) return;
            lock (_lock)
            {
                try
                {
                    _ctx.PluginCommandStub.MainsChanged(false);
                    _ctx.PluginCommandStub.MainsChanged(true);
                    _ctx.PluginCommandStub.StartProcess();
                }
                catch { _failed = true; }
            }
        }

        // Le dict nom→double n'est pas utilisé côté VST — c'est la GUI native du plugin qui pilote son état.
        public Dictionary<string, double> Save() { return new Dictionary<string, double>(); }
        public void Load(Dictionary<string, double> data) { /* no-op : appelé À CHAQUE BUFFER par le renderer */ }

        public string SaveState()
        {
            if (_ctx == null || _failed) return _pendingState; // rien à sérialiser tant que le plugin n'a jamais été chargé.
            try
            {
                var bytes = _ctx.PluginCommandStub.GetChunk(false); // false = bank chunk (voir SetChunk symétrique).
                if (bytes == null || bytes.Length == 0) return null;
                return Convert.ToBase64String(bytes);
            }
            catch { return null; }
        }

        public void LoadState(string state)
        {
            // Appelée UNE fois par la factory au chargement du .sq. Le plugin n'est pas encore ouvert (chargement paresseux
            // au premier Process), donc on stocke le blob et EnsureLoaded l'appliquera après Open()/SetSampleRate().
            _pendingState = state;
            if (_ctx != null && !_failed && !string.IsNullOrEmpty(state))
            {
                // Cas où LoadState est ré-appelé sur un plugin déjà chargé (rare — via UI). On applique tout de suite.
                try
                {
                    var bytes = Convert.FromBase64String(state);
                    lock (_lock)
                    {
                        _ctx.PluginCommandStub.SetChunk(bytes, false);
                    }
                    _pendingState = null;
                }
                catch { }
            }
        }

        /// <summary>
        /// Force le chargement synchrone du plugin (utilisé par l'UI pour ouvrir la fenêtre native SANS attendre la
        /// première frame audio). Retourne <c>true</c> si tout va bien.
        /// </summary>
        public bool EnsureOpenedSync(int blockSize)
        {
            lock (_lock)
            {
                EnsureLoaded(blockSize > 0 ? blockSize : 512);
                return _ctx != null && !_failed;
            }
        }

        /// <summary>Récupère la taille de l'éditeur pour dimensionner le HwndHost. Retourne (0,0) si l'éditeur n'est pas dispo.</summary>
        public System.Drawing.Size GetEditorSize()
        {
            if (_ctx == null || _failed) return System.Drawing.Size.Empty;
            try
            {
                System.Drawing.Rectangle rc;
                if (_ctx.PluginCommandStub.EditorGetRect(out rc))
                    return new System.Drawing.Size(rc.Width, rc.Height);
            }
            catch { }
            return System.Drawing.Size.Empty;
        }

        /// <summary>
        /// Demande au plugin d'ouvrir sa GUI native en enfant du <paramref name="parentHwnd"/> fourni (généralement
        /// le HWND d'un <see cref="System.Windows.Interop.HwndHost"/> WPF). Retourne <c>false</c> si pas d'éditeur.
        /// </summary>
        public bool OpenEditor(IntPtr parentHwnd)
        {
            if (_ctx == null || _failed) return false;
            try
            {
                return _ctx.PluginCommandStub.EditorOpen(parentHwnd);
            }
            catch { return false; }
        }

        /// <summary>Signale au plugin de fermer sa GUI native (à appeler quand le HwndHost part).</summary>
        public void CloseEditor()
        {
            if (_ctx == null || _failed) return;
            try { _ctx.PluginCommandStub.EditorClose(); } catch { }
        }

        /// <summary>À appeler par un timer pendant que l'éditeur est ouvert : beaucoup de plugins ont besoin d'un idle régulier pour redessiner leurs sliders.</summary>
        public void EditorIdle()
        {
            if (_ctx == null || _failed) return;
            try { _ctx.PluginCommandStub.EditorIdle(); } catch { }
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
                    var cmd = _ctx.PluginCommandStub;
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
