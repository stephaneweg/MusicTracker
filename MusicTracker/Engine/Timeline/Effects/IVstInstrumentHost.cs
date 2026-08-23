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
        void NoteOff(int channel, int note);
        void ProcessMidiCC(int channel, int cc, int value);
        void SetPitchBend(int channel, int value);
        /// <summary>Bend PAR VOIX (par note active), en SEMITONS signés. Contrairement à
        /// <see cref="SetPitchBend"/> (canal-wide), celui-ci ne modifie QUE la voix identifiée par
        /// <paramref name="note"/> — indispensable pour un glissando polyphonique où plusieurs
        /// notes jouent en même temps et doivent bender indépendamment. Défaut = no-op : les
        /// backends VST natifs qui n'ont pas d'équivalent l'ignorent (le glissando reste inaudible
        /// sur ces instruments — limitation MIDI standard, pas un bug).</summary>
        void SetNoteBend(int channel, int note, float semis) { }
        void Render(Span<float> left, Span<float> right);
        string SaveState();
        void LoadState(string state);
    }
}
