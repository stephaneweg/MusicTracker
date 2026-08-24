using System;
using NAudio.Midi;

namespace MusicTracker.Live
{
    /// <summary>
    /// Entrée MIDI temps réel du mode « Instrument ». Différence avec le
    /// <c>MidiNoteSourceProvider</c> de l'éditeur de riff : celui-ci ne remonte que des hauteurs (l'éditeur
    /// dessine des cases, la vélocité n'y a pas de sens) alors qu'ici on JOUE l'instrument — la vélocité, la
    /// pédale de sustain, la molette de modulation et le pitch bend comptent autant que les notes.
    ///
    /// Les événements sont levés depuis le thread MIDI, SANS marshalling vers l'UI : ils vont droit à
    /// <see cref="LiveInstrument"/> (qui se verrouille lui-même), ce qui économise un aller-retour par le
    /// Dispatcher — soit quelques millisecondes de latence en moins sur le chemin le plus sensible.
    /// </summary>
    public sealed class LiveMidiInput : IDisposable
    {
        readonly object _lock = new object();
        MidiIn _midi;
        int _device = -1;

        /// <summary>(note MIDI 0..127, vélocité 1..127)</summary>
        public event Action<int, int> NoteOn;
        /// <summary>(note MIDI 0..127)</summary>
        public event Action<int> NoteOff;
        /// <summary>(numéro de CC, valeur 0..127)</summary>
        public event Action<int, int> ControlChange;
        /// <summary>Molette de pitch en unités MIDI 14 bits (0..16383, 8192 = centre).</summary>
        public event Action<int> PitchBend;
        /// <summary>Message d'erreur d'ouverture, ou <c>null</c> si tout va bien.</summary>
        public string Error { get; private set; }

        /// <summary>Ouvre (ou ré-ouvre) le périphérique d'index <paramref name="deviceIndex"/>.
        /// Un index négatif ou hors bornes ferme simplement l'entrée.</summary>
        public void Open(int deviceIndex)
        {
            lock (_lock)
            {
                Close();
                _device = deviceIndex;
                Error = null;
                if (deviceIndex < 0 || deviceIndex >= MidiIn.NumberOfDevices) return;
                try
                {
                    _midi = new MidiIn(deviceIndex);
                    _midi.MessageReceived += OnMessage;
                    _midi.ErrorReceived += (s, e) => { };
                    _midi.Start();
                }
                catch (Exception ex) { Error = ex.Message; _midi = null; }
            }
        }

        public void Close()
        {
            lock (_lock)
            {
                if (_midi == null) return;
                try { _midi.Stop(); _midi.Dispose(); } catch { }
                _midi = null;
            }
        }

        void OnMessage(object sender, MidiInMessageEventArgs e)
        {
            var ev = e.MidiEvent;
            if (ev == null) return;
            switch (ev.CommandCode)
            {
                case MidiCommandCode.NoteOn:
                {
                    var n = (NoteOnEvent)ev;
                    // Vélocité 0 = note-off déguisé (convention MIDI que tous les claviers utilisent).
                    if (n.Velocity > 0) NoteOn?.Invoke(n.NoteNumber, n.Velocity);
                    else NoteOff?.Invoke(n.NoteNumber);
                    break;
                }
                case MidiCommandCode.NoteOff:
                    NoteOff?.Invoke(((NoteEvent)ev).NoteNumber);
                    break;
                case MidiCommandCode.ControlChange:
                {
                    var c = (ControlChangeEvent)ev;
                    ControlChange?.Invoke((int)c.Controller, c.ControllerValue);
                    break;
                }
                case MidiCommandCode.PitchWheelChange:
                    PitchBend?.Invoke(((PitchWheelChangeEvent)ev).Pitch);
                    break;
            }
        }

        public void Dispose() => Close();
    }
}
