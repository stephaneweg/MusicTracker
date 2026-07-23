using System;
using System.Threading;
using NAudio.Wave;

namespace MusicTracker.Engine.Timeline
{
    /// <summary>
    /// Decouples synthesis from the audio callback: a background thread keeps rendering the inner provider
    /// (a <see cref="WaveProvider16"/> such as the TimelinePlayer) AHEAD into a ring buffer, and
    /// <see cref="Read"/> (called by the audio device) just copies out of it. This absorbs CPU/GC spikes (the
    /// expensive SoundFont synthesis runs ahead of the real-time deadline) so a heavy piece plays without
    /// underruns. Playback only starts once a head start is buffered (<see cref="Primed"/>); <see cref="Ended"/>
    /// fires once the buffer has fully drained. <see cref="ConsumedSamples"/> exposes the AUDIBLE position
    /// (samples handed to the device) so the UI cursor/highlight stays in sync rather than running ahead.
    /// </summary>
    public sealed class LookaheadBuffer : WaveProvider16
    {
        readonly WaveProvider16 inner;
        readonly Action startInner, stopInner;
        readonly short[] ring;
        readonly int capacity;
        readonly object gate = new object();

        readonly int primeSamples; // pre-fill this much before playback starts (a head start)

        int head, tail, count;   // ring state (count = samples available)
        long consumed;           // total samples handed to the audio device
        Thread worker;
        volatile bool running, primed;
        bool innerDone, endedRaised;

        /// <summary>Raised (once) when playback has actually finished — the inner ended AND the buffer drained.</summary>
        public event Action Ended;

        /// <summary>Raised (once) when the buffer has accumulated its head start (or the piece is fully rendered).
        /// The device should only start playing then — otherwise it drains the buffer as fast as it fills.</summary>
        public event Action Primed;

        public LookaheadBuffer(WaveProvider16 inner, Action start, Action stop, int sampleRate, double leadSeconds = 5.0, double primeSeconds = 3.0)
            : base(sampleRate, 1)
        {
            this.inner = inner;
            this.startInner = start;
            this.stopInner = stop;
            capacity = Math.Max(sampleRate, (int)(sampleRate * leadSeconds)) + 1;
            primeSamples = Math.Min(capacity - 8192, Math.Max(1, (int)(sampleRate * primeSeconds)));
            ring = new short[capacity];
        }

        /// <summary>Total samples handed to the audio device so far (the audible playback position).</summary>
        public long ConsumedSamples { get { lock (gate) return consumed; } }

        public void Start()
        {
            startInner?.Invoke();
            running = true;
            worker = new Thread(Produce) { IsBackground = true, Priority = ThreadPriority.AboveNormal, Name = "PlaybackPrebuffer" };
            worker.Start();
        }

        public void Stop()
        {
            running = false;
            lock (gate) Monitor.PulseAll(gate);
            try { worker?.Join(300); } catch { }
            try { stopInner?.Invoke(); } catch { }
        }

        // Background: render ahead until the ring is nearly full, then wait for the consumer to free space.
        void Produce()
        {
            var tmp = new short[8192];
            while (running)
            {
                lock (gate)
                {
                    while (running && count > capacity - tmp.Length) Monitor.Wait(gate);
                    if (!running) return;
                }
                int n = inner.Read(tmp, 0, tmp.Length);
                if (n <= 0) { lock (gate) { innerDone = true; Monitor.PulseAll(gate); } SignalPrimed(); return; } // short piece fully rendered
                bool reached;
                lock (gate)
                {
                    for (int i = 0; i < n; i++) { ring[tail] = tmp[i]; tail = (tail + 1) % capacity; }
                    count += n;
                    reached = count >= primeSamples;
                    Monitor.PulseAll(gate);
                }
                if (reached) SignalPrimed(); // enough head start accumulated -> the device may start
            }
        }

        void SignalPrimed()
        {
            if (primed) return;
            primed = true;
            Primed?.Invoke();
        }

        public override int Read(short[] buffer, int offset, int sampleCount)
        {
            int got = 0;
            bool finished = false;
            lock (gate)
            {
                while (got < sampleCount && count > 0)
                {
                    buffer[offset + got] = ring[head];
                    head = (head + 1) % capacity;
                    count--; got++;
                }
                consumed += got;
                if (got < sampleCount && innerDone && count == 0) finished = true;
                Monitor.PulseAll(gate); // space freed -> let the producer continue
            }
            for (int i = got; i < sampleCount; i++) buffer[offset + i] = 0; // underrun / end -> silence
            if (finished && !endedRaised) { endedRaised = true; Ended?.Invoke(); }
            return sampleCount;
        }
    }
}
