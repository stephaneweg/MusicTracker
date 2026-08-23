using System;

namespace MusicTracker.Engine.Timeline.Effects
{
    /// <summary>
    /// Surface commune aux VSTi (source sonore MIDI-driven) hébergés par Koton, quel que soit le format
    /// natif (VST2 via <c>VstInstrument</c>, VST3 via <c>Vst3Instrument</c>). Permet à
    /// <c>TimelinePlayer</c> de tenir un champ <c>Vsti</c> unique et de router notes/CC/rendu sans
    /// connaître le format en aval.
    ///
    /// Hérite de <see cref="IVstEditorHost"/> pour partager la fenêtre native <c>VstPluginWindow</c>,
    /// et de <see cref="IDisposable"/> pour la libération explicite (chargement plugin natif =
    /// ressources non-managées).
    /// </summary>
    public interface IVstInstrumentHost : IVstEditorHost, IDisposable
    {
        bool IsLoaded { get; }
        void NoteOn(int channel, int note, int velocity);
        /// <summary>Surcharge glissando : voix qui démarre à glideFromNote et glide vers note en
        /// glideDurationSec. Default = ignore le glide, appelle NoteOn standard. Implémentations
        /// natives (MeltySynth via adapter, KotonInstrumentAdapter → plugin) le supportent.</summary>
        void NoteOn(int channel, int note, int velocity, int glideFromNote, double glideDurationSec)
            => NoteOn(channel, note, velocity);
        void NoteOff(int channel, int note);
        void ProcessMidiCC(int channel, int cc, int value);
        void SetPitchBend(int channel, int value);
        void Render(Span<float> left, Span<float> right);
        string SaveState();
        void LoadState(string state);
    }
}
