using System;
using KotonStudio.Library;

namespace MusicTracker.Engine.Timeline.Effects
{
    /// <summary>
    /// Adapte un <see cref="IKotonInstrument"/> (framework Koton natif) sur l'interface
    /// <see cref="IVstInstrumentHost"/> que consomme déjà le pipeline audio (voir
    /// <c>TimelinePlayer.TrySetupMeltySynth</c>). Objectif : ne PAS toucher le renderer, le mixeur,
    /// l'export MIDI, la capture d'état — tout ce code voit un IVstInstrumentHost et ne sait pas si
    /// derrière il y a un VST2, un VST3 ou un plugin Koton natif.
    ///
    /// **Différences fondamentales VSTi ↔ Koton natif** :
    /// - Le plugin Koton n'a pas de fenêtre native Win32 (le HWND que <see cref="IVstEditorHost"/>
    ///   expose n'a pas de sens ici). L'adaptateur retourne donc <c>HasEditor = false</c> côté
    ///   VstPluginWindow et les méthodes <c>OpenEditor</c>/<c>CloseEditor</c>/<c>EditorIdle</c> sont
    ///   des no-ops. L'éditeur Koton (UserControl WPF) est ouvert par une fenêtre séparée
    ///   (<c>KotonPluginEditorDialog</c>) qui contourne complètement <c>VstPluginWindow</c>.
    /// - Le canal MIDI n'a pas de sens pour un instrument mono-timbral Koton — <see cref="NoteOn"/>
    ///   ignore l'argument channel et route sur la seule voix logique du plugin.
    /// - La persistance passe par un <c>byte[]</c> (base64 encodé pour tenir dans le JSON du projet)
    ///   au lieu du chunk VST natif — l'API reste la même côté hôte (SaveState/LoadState → string).
    ///
    /// **Cycle** : construit avec l'instance IKotonInstrument déjà créée (par
    /// <see cref="KotonPluginRegistry.InstantiateInstrument"/>), Prepare appelé au 1er Render (comme
    /// EnsureLoaded d'un VstInstrument), Dispose délègue au plugin.
    /// </summary>
    public sealed class KotonInstrumentAdapter : IVstInstrumentHost
    {
        readonly IKotonInstrument _plugin;
        readonly int _sampleRate;
        // Chargement paresseux (le pipeline snapshotte les instruments au démarrage — Prepare avant que
        // le renderer ait vraiment démarré n'a pas de sens ; on préfère le faire au premier Render).
        bool _prepared;
        bool _failed;
        readonly object _lock = new object();

        public string DisplayName { get; }
        public bool IsLoaded => _prepared && !_failed;
        public bool IsFailed => _failed;

        // Le framework Koton natif n'expose pas l'équivalent d'IPlugFrame.resizeView (l'éditeur est un
        // UserControl WPF qui gère sa taille tout seul via layout WPF). No-op.
        public event Action<int, int> EditorResizeRequested { add { } remove { } }

        public KotonInstrumentAdapter(IKotonInstrument plugin, int sampleRate, string displayName)
        {
            _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
            _sampleRate = sampleRate <= 0 ? 44100 : sampleRate;
            DisplayName = string.IsNullOrEmpty(displayName) ? plugin.DisplayName : displayName;
        }

        void EnsurePrepared()
        {
            if (_prepared || _failed) return;
            try
            {
                // Max block size généreux (8192 = ~186 ms @ 44.1 kHz) — le renderer peut poser des
                // buffers plus petits, jamais plus grands.
                _plugin.Prepare(_sampleRate, 8192);
                _prepared = true;
            }
            catch { _failed = true; }
        }

        // ---- pilote MIDI --------------------------------------------------------------------------------

        public void NoteOn(int channel, int note, int velocity)
        {
            if (_failed) return;
            EnsurePrepared();
            try { _plugin.NoteOn(note, velocity); }
            catch { _failed = true; }
        }

        public void NoteOff(int channel, int note)
        {
            if (_failed) return;
            EnsurePrepared();
            try { _plugin.NoteOff(note); }
            catch { _failed = true; }
        }

        public void ProcessMidiCC(int channel, int cc, int value)
        {
            if (_failed) return;
            EnsurePrepared();
            try
            {
                _plugin.MidiCC(cc, value);
                // CC 123 = All Notes Off (VSTi panic). Le plugin peut vouloir gérer via MidiCC(123,0),
                // mais un Reset est plus robuste (vide aussi les enveloppes en release infini).
                if (cc == 123) _plugin.Reset();
            }
            catch { _failed = true; }
        }

        public void SetPitchBend(int channel, int value)
        {
            if (_failed) return;
            EnsurePrepared();
            try
            {
                // VST convention : 0..16383 avec 8192 = centre. On mappe vers -1..+1 pour l'interface Koton.
                int v = value < 0 ? 0 : (value > 16383 ? 16383 : value);
                float f = (v - 8192) / 8192f;
                _plugin.SetPitchBend(f);
            }
            catch { _failed = true; }
        }

        public void SetNoteBend(int channel, int note, float semis)
        {
            if (_failed) return;
            EnsurePrepared();
            try { _plugin.SetNoteBend(note, semis); }
            catch { _failed = true; }
        }

        // ---- rendu --------------------------------------------------------------------------------------

        public void Render(Span<float> left, Span<float> right)
        {
            int frames = left.Length;
            if (_failed || frames <= 0 || right.Length != frames) { left.Clear(); right.Clear(); return; }
            lock (_lock)
            {
                EnsurePrepared();
                if (!_prepared || _failed) { left.Clear(); right.Clear(); return; }
                try { _plugin.Render(left, right); }
                catch
                {
                    // Un plugin qui jette au Render passe en bypass permanent (piste silencieuse),
                    // même politique que VstInstrument.Render.
                    _failed = true;
                    left.Clear(); right.Clear();
                }
            }
        }

        // ---- persistance -------------------------------------------------------------------------------

        public string SaveState()
        {
            if (_failed) return null;
            try
            {
                var bytes = _plugin.SaveState();
                if (bytes == null || bytes.Length == 0) return null;
                return Convert.ToBase64String(bytes);
            }
            catch { return null; }
        }

        public void LoadState(string state)
        {
            if (_failed || string.IsNullOrEmpty(state)) return;
            try
            {
                var bytes = Convert.FromBase64String(state);
                _plugin.LoadState(bytes);
            }
            catch { /* blob incompatible / mal formé : plugin garde ses défauts, pas d'exception up */ }
        }

        // ---- interface IVstEditorHost (fenêtre native VST) ---------------------------------------------
        //
        // Un plugin Koton natif N'A PAS de HWND — l'éditeur est un UserControl WPF ouvert par
        // KotonPluginEditorDialog. Ces méthodes retournent des valeurs neutres pour satisfaire
        // l'interface : VstPluginWindow n'est jamais utilisé avec un IKotonInstrument.

        /// <summary>Vrai après Prepare — pas de "HWND ouvert" à distinguer, le plugin est fonctionnel ou pas.</summary>
        public bool EnsureOpenedSync(int blockSize)
        {
            lock (_lock) { EnsurePrepared(); return _prepared && !_failed; }
        }

        public System.Drawing.Size GetEditorSize() => System.Drawing.Size.Empty;
        public bool OpenEditor(IntPtr parentHwnd) => false;
        public void CloseEditor() { }
        public void EditorIdle() { }

        // ---- accès au plugin brut (pour l'éditeur WPF) -----------------------------------------------

        /// <summary>Le plugin sous-jacent — permet au dialogue d'éditeur (WPF) d'appeler
        /// <see cref="IKotonPlugin.CreateEditor"/> et de lier ses paramètres.</summary>
        public IKotonInstrument Plugin => _plugin;

        public void Dispose()
        {
            try { _plugin?.Dispose(); } catch { }
            _prepared = false;
            _failed = true;
        }
    }
}
