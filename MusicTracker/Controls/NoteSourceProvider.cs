using System;
using System.Collections.Generic;
using System.Windows.Input;
using NAudio.Midi;
using NAudio.Wave;
using MusicTracker.Engine;

namespace MusicTracker.Controls
{
    /// <summary>
    /// A source of note on/off events feeding the riff editor — PC keyboard, MIDI, or audio. Each implementation
    /// decides WHEN a pitch is on/off; the editor turns the events into growing notes (cursor / snap stay there).
    /// Note numbers are app note indices (0..95, note 0 = MIDI 12 = C0).
    /// </summary>
    public interface INoteSourceProvider : IDisposable
    {
        event Action<int> NoteOn;
        event Action<int> NoteOff;
        void Start();
        void Stop();
    }

    /// <summary>
    /// PC keyboard: letters → notes via an editor-supplied mapping (octave / scale / accidental, read live). The
    /// editor forwards KeyDown/KeyUp and calls <see cref="Poll"/> each frame to catch a missed KeyUp (focus loss).
    /// </summary>
    public sealed class KeyboardNoteSourceProvider : INoteSourceProvider
    {
        readonly Func<Key, int> keyToNote;            // -1 if the key isn't a note
        readonly Dictionary<Key, int> held = new Dictionary<Key, int>(); // key -> note

        public event Action<int> NoteOn;
        public event Action<int> NoteOff;

        public KeyboardNoteSourceProvider(Func<Key, int> keyToNote) { this.keyToNote = keyToNote; }

        public void Start() { }
        public void Stop() { ReleaseAll(); }

        public void KeyDown(Key k)
        {
            if (held.ContainsKey(k)) return;          // already held (auto-repeat)
            int note = keyToNote(k);
            if (note < 0) return;
            held[k] = note;
            NoteOn?.Invoke(note);
        }

        public void KeyUp(Key k)
        {
            if (held.TryGetValue(k, out int note)) { held.Remove(k); NoteOff?.Invoke(note); }
        }

        // Finalise keys that are no longer physically down (a KeyUp can be lost on focus change).
        public void Poll()
        {
            if (held.Count == 0) return;
            foreach (var k in new List<Key>(held.Keys))
                if (!Keyboard.IsKeyDown(k)) { int note = held[k]; held.Remove(k); NoteOff?.Invoke(note); }
        }

        void ReleaseAll() { foreach (var n in new List<int>(held.Values)) NoteOff?.Invoke(n); held.Clear(); }
        public void Dispose() => Stop();
    }

    /// <summary>
    /// MIDI input device. <see cref="SetDevice"/> (re)opens the chosen device on the fly. NoteOn/NoteOff messages
    /// are marshalled to the UI thread via the supplied delegate.
    /// </summary>
    public sealed class MidiNoteSourceProvider : INoteSourceProvider
    {
        readonly Action<Action> toUi;
        MidiIn midiIn;
        int deviceIndex = -1;
        bool started;

        public event Action<int> NoteOn;
        public event Action<int> NoteOff;

        public MidiNoteSourceProvider(Action<Action> marshalToUi) { toUi = marshalToUi; }

        public void SetDevice(int index) { deviceIndex = index; if (started) Reopen(); }
        public void Start() { started = true; Reopen(); }
        public void Stop() { started = false; Close(); }

        void Reopen()
        {
            Close();
            if (deviceIndex < 0 || deviceIndex >= MidiIn.NumberOfDevices) return;
            try
            {
                midiIn = new MidiIn(deviceIndex);
                midiIn.MessageReceived += OnMsg;
                midiIn.ErrorReceived += (s, e) => { };
                midiIn.Start();
            }
            catch { midiIn = null; }
        }

        void Close() { if (midiIn != null) { try { midiIn.Stop(); midiIn.Dispose(); } catch { } midiIn = null; } }

        void OnMsg(object sender, MidiInMessageEventArgs e)
        {
            var ne = e.MidiEvent as NoteEvent;
            if (ne == null) return;
            bool on = ne.CommandCode == MidiCommandCode.NoteOn && (!(ne is NoteOnEvent noe) || noe.Velocity > 0);
            bool off = ne.CommandCode == MidiCommandCode.NoteOff || (ne is NoteOnEvent noe2 && noe2.Velocity == 0);
            int note = ne.NoteNumber - 12; // MIDI -> app note
            if (on) toUi(() => NoteOn?.Invoke(note));
            else if (off) toUi(() => NoteOff?.Invoke(note));
        }

        public void Dispose() => Stop();
    }

    /// <summary>
    /// Audio input: live MONOPHONIC pitch detection (MPM). A background WaveIn callback fills a rolling window and
    /// detects the pitch each hop; a new pitch (or silence) must persist for <c>holdSeconds()</c> before it replaces
    /// the current note (NoteOff old + NoteOn new), snapped to <c>scaleMask()</c>. Events marshalled to the UI thread.
    /// </summary>
    public sealed class WaveNoteSourceProvider : INoteSourceProvider
    {
        readonly Action<Action> toUi;
        readonly Func<int> scaleMask;
        readonly Func<double> holdSeconds;
        readonly Func<double> onsetSensitivity; // 0..1: re-attack sensitivity (détaché on the same pitch)
        readonly int sampleRate;
        const double Rms = 0.012;
        const double EnvDecay = 0.92;     // amplitude-envelope decay per hop (~150 ms) for onset detection
        const double MaxHoldSeconds = 0.03; // cap on the pitch-change debounce (decoupled from tempo → low latency)

        WaveInEvent waveIn;
        int deviceIndex = -1;
        bool started, prioritySet;

        float[] ring, frame;              // sized to AudioPitch.FrameSize at Reopen (picks up a settings change)
        int ringPos, ringFill, sinceHop;
        int curNote = -1, cand = -1, candCount; // hysteresis (callback thread)
        int raised = -1;                         // currently-raised NoteOn
        double env;                              // amplitude envelope (for onset/re-attack detection)
        int framesSinceOnset;
        int onsetRefractory;                     // min frames between onsets (debounce)
        int hopLen;                              // analysis hop = frame/4 — smaller = analysed more often → lower latency

        public event Action<int> NoteOn;
        public event Action<int> NoteOff;

        public WaveNoteSourceProvider(Action<Action> marshalToUi, Func<int> scaleMask, Func<double> holdSeconds, Func<double> onsetSensitivity, int sampleRate)
        {
            toUi = marshalToUi; this.scaleMask = scaleMask; this.holdSeconds = holdSeconds; this.onsetSensitivity = onsetSensitivity; this.sampleRate = sampleRate;
        }

        public void SetDevice(int index) { deviceIndex = index; if (started) Reopen(); }
        public void Start() { started = true; Reopen(); }

        public void Stop()
        {
            started = false; Close();
            if (raised >= 0) { int n = raised; raised = -1; toUi(() => NoteOff?.Invoke(n)); }
        }

        void Reopen()
        {
            Close();
            int fr = Math.Max(256, AudioPitch.FrameSize);
            if (ring == null || ring.Length != fr) { ring = new float[fr]; frame = new float[fr]; }
            hopLen = Math.Max(64, fr / 4); // analyse 4×/window → fast confirmation; CPU stays bounded (scales with fr)
            onsetRefractory = Math.Max(2, (int)Math.Round(0.06 / ((double)hopLen / sampleRate))); // ~60 ms debounce
            ringPos = ringFill = sinceHop = 0; curNote = cand = -1; candCount = 0; env = 0; framesSinceOnset = 0; prioritySet = false;
            if (WaveInEvent.DeviceCount == 0) return;
            try
            {
                // Small input buffer = low latency (3 buffers keep it stable). Detection latency is still bounded
                // below by the MPM analysis window (FrameSize samples) — use a smaller window for low latency.
                waveIn = new WaveInEvent { WaveFormat = new WaveFormat(sampleRate, 16, 1), BufferMilliseconds = 10, NumberOfBuffers = 3 };
                if (deviceIndex >= 0 && deviceIndex < WaveInEvent.DeviceCount) waveIn.DeviceNumber = deviceIndex;
                waveIn.DataAvailable += OnData;
                waveIn.StartRecording();
            }
            catch { waveIn = null; }
        }

        void Close() { if (waveIn != null) { try { waveIn.StopRecording(); waveIn.Dispose(); } catch { } waveIn = null; } }

        void OnData(object sender, WaveInEventArgs e)
        {
            // Bump the capture/detection thread above normal so analysis isn't preempted (lower jitter/latency).
            // NOT process-level realtime — that would starve the rest of the app.
            if (!prioritySet) { prioritySet = true; try { System.Threading.Thread.CurrentThread.Priority = System.Threading.ThreadPriority.Highest; } catch { } }

            int fr = ring.Length, hop = hopLen;
            for (int i = 0; i + 1 < e.BytesRecorded; i += 2)
            {
                short s16 = (short)(e.Buffer[i] | (e.Buffer[i + 1] << 8));
                ring[ringPos] = s16 / 32768f; ringPos = (ringPos + 1) % fr; if (ringFill < fr) ringFill++;
                if (++sinceHop >= hop && ringFill >= fr) { sinceHop = 0; Analyze(); }
            }
        }

        void Analyze()
        {
            int fr = frame.Length;
            double energy = 0;
            for (int i = 0; i < fr; i++) { float v = ring[(ringPos + i) % fr]; frame[i] = v; energy += v * v; } // unroll + RMS
            double rms = Math.Sqrt(energy / fr);

            // Onset = a re-attack louder than the running envelope (a fresh bow stroke / pluck on the SAME pitch).
            framesSinceOnset++;
            double sens = onsetSensitivity != null ? Math.Max(0, Math.Min(1, onsetSensitivity())) : 0;
            double rise = 2.2 - 1.05 * sens;   // sens 0 -> needs a 2.2× spike; sens 1 -> 1.15× (very sensitive)
            bool onset = rms > Rms && rms > env * rise && framesSinceOnset >= onsetRefractory;
            env = Math.Max(rms, env * EnvDecay);

            double f = AudioPitch.DetectFramePitch(frame, 0, sampleRate, Rms);
            int note = f > 0 ? AudioPitch.NoteIndexFromMidi(69 + 12 * Math.Log(f / 440.0, 2), scaleMask()) : -1;

            if (note == curNote)
            {
                cand = -1; candCount = 0;
                if (onset && curNote >= 0 && raised == curNote) // détaché: re-attack on the same note -> re-articulate
                {
                    framesSinceOnset = 0;
                    int nn = curNote;
                    toUi(() => { NoteOff?.Invoke(nn); NoteOn?.Invoke(nn); });
                }
                return;
            }

            if (note == cand) candCount++; else { cand = note; candCount = 1; }
            // Debounce a pitch change by a FIXED short window (~2-3 hops, max 30 ms), NOT the musical slice
            // duration — otherwise a slow tempo / coarse grid would add up to hundreds of ms of latency.
            double hopTime = (double)hopLen / sampleRate;
            int hold = Math.Max(2, (int)Math.Round(Math.Min(holdSeconds(), MaxHoldSeconds) / hopTime));
            int need = curNote < 0 ? 1 : hold; // attack from silence: accept on the FIRST solid frame (lowest latency)
            if (candCount >= need)
            {
                curNote = note; cand = -1; candCount = 0; framesSinceOnset = 0;
                int nn = note;
                toUi(() =>
                {
                    if (raised >= 0) { int old = raised; raised = -1; NoteOff?.Invoke(old); }
                    if (nn >= 0) { raised = nn; NoteOn?.Invoke(nn); }
                });
            }
        }

        public void Dispose() => Stop();
    }
}
