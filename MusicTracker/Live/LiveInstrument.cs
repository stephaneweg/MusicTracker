using System;
using MusicTracker.Engine;
using MusicTracker.Engine.Timeline.Effects;

namespace MusicTracker.Live
{
    /// <summary>Nature de la source sonore choisie dans le mode « Instrument ».</summary>
    public enum LiveInstrumentKind
    {
        /// <summary>Synthé SoundFont interne (MeltySynth) — les 128 programmes GM + le kit de batterie.</summary>
        SoundFont,
        /// <summary>VSTi externe (.dll VST2 ou bundle .vst3), hébergé comme dans la timeline.</summary>
        Vst,
        /// <summary>Plugin instrument Koton natif (.ksl) découvert par <see cref="KotonPluginRegistry"/>.</summary>
        Koton,
    }

    /// <summary>
    /// Source sonore du mode « Instrument » : une façade unique au-dessus des trois familles que Koton sait
    /// jouer (SoundFont MeltySynth, VSTi, plugin Koton natif). Le moteur live ne connaît que cette classe —
    /// il pousse des NoteOn/NoteOff et tire des blocs stéréo, sans savoir ce qu'il y a derrière.
    ///
    /// **Thread-safety** : <see cref="Render"/> et les méthodes de note sont appelées depuis des threads
    /// DIFFÉRENTS (callback audio d'un côté, thread MIDI / détection de hauteur de l'autre). Tout passe donc
    /// par un verrou unique : les note-on sont assez bon marché pour que la contention reste invisible, et
    /// c'est ce qui garantit qu'une note ne se glisse pas au milieu d'un bloc de rendu MeltySynth.
    ///
    /// **Politique d'échec** : un plugin absent / qui refuse de charger ne jette pas — l'instrument devient
    /// silencieux et <see cref="LoadError"/> porte le message affiché par la fenêtre.
    /// </summary>
    public sealed class LiveInstrument : IDisposable
    {
        readonly object _lock = new object();
        readonly int _sampleRate;

        MeltySynth.Synthesizer _synth;      // SoundFont
        IVstInstrumentHost _host;           // VSTi (VST2/VST3) ou plugin Koton via KotonInstrumentAdapter
        int _channel;                       // canal MeltySynth (0 mélodique, 9 = percussions)

        public LiveInstrumentKind Kind { get; }
        /// <summary>Chemin du plugin (VST) ou Id du plugin (Koton) ; vide pour le SoundFont.</summary>
        public string Reference { get; }
        /// <summary>Programme GM 0..127, ou <see cref="InstrumentCatalog.DrumIndex"/> pour le kit de batterie.</summary>
        public int Program { get; }
        public string DisplayName { get; private set; }
        public string LoadError { get; private set; }
        public bool IsUsable => _synth != null || (_host != null && _host.IsLoaded);

        /// <summary>Hôte VSTi / Koton sous-jacent — exposé pour ouvrir la fenêtre d'édition du plugin
        /// (<c>VstPluginWindow</c> / <c>KotonPluginEditorDialog</c>). <c>null</c> en mode SoundFont.</summary>
        public IVstInstrumentHost Host => _host;

        LiveInstrument(LiveInstrumentKind kind, string reference, int program, int sampleRate)
        {
            Kind = kind; Reference = reference; Program = program; _sampleRate = sampleRate;
        }

        /// <summary>Instrument SoundFont : <paramref name="program"/> = programme GM, ou
        /// <see cref="InstrumentCatalog.DrumIndex"/> pour router sur le canal de percussion.</summary>
        public static LiveInstrument CreateSoundFont(int program, int sampleRate)
        {
            var inst = new LiveInstrument(LiveInstrumentKind.SoundFont, "", program, sampleRate);
            inst.DisplayName = InstrumentCatalog.Name(program);
            var sf = InstrumentCatalog.SoundFontObject;
            if (sf == null)
            {
                inst.LoadError = Localization.Loc.T("LiveNoSoundFont");
                return inst;
            }
            try
            {
                // Mêmes réglages que TimelinePlayer pour une piste seule : 16 canaux nominaux dont un seul
                // utilisé, réverb/chorus du SoundFont actives, polyphonie large (un instrument joué à la main
                // ne monte jamais à 64 voix, mais les queues de release comptent).
                var settings = new MeltySynth.SynthesizerSettings(sampleRate)
                {
                    ChannelCount = 16,
                    EnableReverbAndChorus = true,
                    MaximumPolyphony = 64,
                };
                var synth = new MeltySynth.Synthesizer(sf, settings);
                bool drum = program >= InstrumentCatalog.DrumIndex;
                inst._channel = drum ? synth.PercussionChannel : 0;
                if (drum)
                {
                    synth.ProcessMidiMessage(inst._channel, 0xC0, InstrumentCatalog.DrumKitProgram(0), 0);
                }
                else
                {
                    synth.ProcessMidiMessage(inst._channel, 0xB0, 0, 0);                                   // bank select
                    synth.ProcessMidiMessage(inst._channel, 0xC0, Math.Max(0, Math.Min(127, program)), 0); // program change
                }
                inst._synth = synth;
            }
            catch (Exception ex) { inst.LoadError = ex.Message; }
            return inst;
        }

        /// <summary>Instrument VSTi. L'extension décide de l'hôte : bundle <c>.vst3</c> → <c>Vst3Instrument</c>,
        /// sinon <c>VstInstrument</c> (VST2). Le chargement natif est différé au premier rendu par les deux
        /// hôtes, d'où le <c>EnsureOpenedSync</c> ici : on veut savoir TOUT DE SUITE si le plugin refuse de
        /// s'ouvrir, pour l'afficher au lieu de laisser un silence inexpliqué.</summary>
        public static LiveInstrument CreateVst(string path, int sampleRate)
        {
            var inst = new LiveInstrument(LiveInstrumentKind.Vst, path, -1, sampleRate);
            inst.DisplayName = System.IO.Path.GetFileNameWithoutExtension(path ?? "");
            try
            {
                IVstInstrumentHost host = EffectFactory.KindForPluginPath(path) == EffectFactory.Vst3Kind
                    ? (IVstInstrumentHost)new Engine.Timeline.Vst3Instrument(path, sampleRate)
                    : new Engine.Timeline.VstInstrument(path, sampleRate);
                host.EnsureOpenedSync(LiveEngine.MaxBlockSize);
                if (!host.IsLoaded)
                {
                    inst.LoadError = Localization.Loc.T("VstPluginFailedToLoad");
                    try { host.Dispose(); } catch { }
                    return inst;
                }
                inst._host = host;
                inst.DisplayName = host.DisplayName;
            }
            catch (Exception ex) { inst.LoadError = ex.Message; }
            return inst;
        }

        /// <summary>Instrument Koton natif : instance fraîche prise au registre et emballée dans
        /// l'adaptateur qui l'expose comme un VSTi (même surface d'appel pour le moteur).</summary>
        public static LiveInstrument CreateKoton(string id, int sampleRate)
        {
            var inst = new LiveInstrument(LiveInstrumentKind.Koton, id, -1, sampleRate);
            inst.DisplayName = id;
            try
            {
                var plugin = KotonPluginRegistry.InstantiateInstrument(id);
                if (plugin == null)
                {
                    inst.LoadError = string.Format(Localization.Loc.T("KotonPluginMissing"), id);
                    return inst;
                }
                inst.DisplayName = plugin.DisplayName;
                inst._host = new KotonInstrumentAdapter(plugin, sampleRate, plugin.DisplayName);
            }
            catch (Exception ex) { inst.LoadError = ex.Message; }
            return inst;
        }

        // ---- jeu ---------------------------------------------------------------------------------------

        /// <summary><paramref name="midi"/> = note MIDI 0..127, <paramref name="velocity"/> = 1..127.</summary>
        public void NoteOn(int midi, int velocity)
        {
            if (midi < 0 || midi > 127) return;
            velocity = Math.Max(1, Math.Min(127, velocity));
            lock (_lock)
            {
                try
                {
                    if (_synth != null) _synth.NoteOn(_channel, midi, velocity);
                    else _host?.NoteOn(0, midi, velocity);
                }
                catch { }
            }
        }

        public void NoteOff(int midi)
        {
            if (midi < 0 || midi > 127) return;
            lock (_lock)
            {
                try
                {
                    if (_synth != null) _synth.NoteOff(_channel, midi);
                    else _host?.NoteOff(0, midi);
                }
                catch { }
            }
        }

        /// <summary>Coupe tout — bouton « Panic », changement d'instrument, arrêt du moteur.</summary>
        public void AllNotesOff()
        {
            lock (_lock)
            {
                try
                {
                    if (_synth != null) _synth.NoteOffAll(false);
                    else _host?.ProcessMidiCC(0, 123, 0);
                }
                catch { }
            }
        }

        /// <summary>Continuous controller (molette de modulation, pédale de sustain, expression…) relayé
        /// depuis le clavier MIDI.</summary>
        public void MidiCC(int cc, int value)
        {
            lock (_lock)
            {
                try
                {
                    if (_synth != null) _synth.ProcessMidiMessage(_channel, 0xB0, cc, value);
                    else _host?.ProcessMidiCC(0, cc, value);
                }
                catch { }
            }
        }

        /// <summary>Molette de pitch, en unités MIDI 14 bits (0..16383, 8192 = centre).</summary>
        public void PitchBend(int value14)
        {
            value14 = Math.Max(0, Math.Min(16383, value14));
            lock (_lock)
            {
                try
                {
                    if (_synth != null) _synth.ProcessMidiMessage(_channel, 0xE0, value14 & 0x7F, (value14 >> 7) & 0x7F);
                    else _host?.SetPitchBend(0, value14);
                }
                catch { }
            }
        }

        /// <summary>Rend un bloc stéréo (écrase le contenu des deux spans).</summary>
        public void Render(Span<float> left, Span<float> right)
        {
            lock (_lock)
            {
                try
                {
                    if (_synth != null) _synth.Render(left, right);
                    else if (_host != null) _host.Render(left, right);
                    else { left.Clear(); right.Clear(); }
                }
                catch { left.Clear(); right.Clear(); }
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                try { _host?.Dispose(); } catch { }
                _host = null;
                _synth = null;
            }
        }
    }
}
