using System;
using System.Collections.Generic;
using NAudio.Wave;

namespace MusicTracker.Engine.Flow
{
    /// <summary>
    /// Plays SEVERAL riff layers (each with its own instrument via its <see cref="FlowContext"/>) summed, on a loop.
    /// Layer 0 is the PRIMARY (drives the loop + the cursor); the others (e.g. an accompaniment backing clamped to the
    /// riff) restart with it. Used by the riff editor to preview a riff together with its chord-line backing.
    /// </summary>
    public class LoopingMixProvider : WaveProvider16
    {
        readonly List<(Func<Riff> next, FlowContext ctx)> layers;
        readonly int sr;
        IRiffSource[] players;

        public int StartSlice { get; set; }
        public int CurrentSlice { get { var p = players; return (p != null && p.Length > 0 && p[0] != null) ? p[0].CurrentSlice : 0; } }

        public LoopingMixProvider(List<(Func<Riff> next, FlowContext ctx)> layers, int sampleRate = 0)
            : base(sampleRate <= 0 ? AudioFormat.SampleRate : sampleRate, 1)
        {
            this.layers = layers ?? new List<(Func<Riff>, FlowContext)>();
            this.sr = sampleRate <= 0 ? AudioFormat.SampleRate : sampleRate;
            players = Make();
        }

        IRiffSource[] Make()
        {
            var ps = new IRiffSource[layers.Count];
            for (int i = 0; i < layers.Count; i++)
            {
                var r = layers[i].next != null ? (layers[i].next() ?? new Riff()) : new Riff();
                ps[i] = (IRiffSource)new MeltyRiffPlayer(r, layers[i].ctx, sr, StartSlice);
            }
            return ps;
        }

        public override int Read(short[] buffer, int offset, int count)
        {
            for (int i = 0; i < count; i++)
            {
                if (players == null || players.Length == 0 || players[0] == null || players[0].TimelineEnded) players = Make();
                double s = 0;
                for (int l = 0; l < players.Length; l++) if (players[l] != null) s += players[l].ReadNext();
                s *= AudioFormat.OutputGain;
                s = AudioFormat.SoftClip(s); // musical limiter instead of a hard clamp
                buffer[offset + i] = (short)(s * short.MaxValue);
            }
            return count;
        }
    }
}
