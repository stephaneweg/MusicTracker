using System;
using NAudio.Wave;

namespace MusicTracker.Engine.Flow
{
    /// <summary>
    /// Plays a riff on a loop for previewing (e.g. the rhythm editor). The riff is rebuilt from
    /// <c>nextRiff</c> at each loop, so edits made while playing are heard on the next cycle.
    /// </summary>
    public class LoopingRiffProvider : WaveProvider16
    {
        readonly Func<Riff> nextRiff;
        readonly FlowContext ctx;
        readonly int sr;
        IRiffSource player;

        /// <summary>Slice the loop starts (and restarts) from — the editor's cursor position.</summary>
        public int StartSlice { get; set; }

        /// <summary>Current playhead slice (for a UI cursor), or 0.</summary>
        public int CurrentSlice { get { var p = player; return p != null ? p.CurrentSlice : 0; } }

        public LoopingRiffProvider(Func<Riff> nextRiff, FlowContext ctx, int sampleRate = 0)
            : base(sampleRate <= 0 ? AudioFormat.SampleRate : sampleRate, 1) // 0 = use the current engine rate
        {
            this.nextRiff = nextRiff;
            this.ctx = ctx;
            this.sr = sampleRate <= 0 ? AudioFormat.SampleRate : sampleRate;
            player = Make();
        }

        IRiffSource Make()
        {
            var r = nextRiff?.Invoke() ?? new Riff();
            return (IRiffSource)new MeltyRiffPlayer(r, ctx, sr, StartSlice);
        }

        public override int Read(short[] buffer, int offset, int count)
        {
            for (int i = 0; i < count; i++)
            {
                if (player == null || player.TimelineEnded) player = Make(); // loop (picks up edits)
                double s = player.ReadNext() * AudioFormat.OutputGain;
                s = AudioFormat.SoftClip(s); // musical limiter instead of a hard clamp
                buffer[offset + i] = (short)(s * short.MaxValue);
            }
            return count;
        }
    }
}
