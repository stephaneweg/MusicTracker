using System;
using System.Collections.Generic;

namespace MusicTracker.Engine.Flow
{
    /// <summary>A pending Repeat on a cursor's path: how many body iterations remain.</summary>
    public class LoopFrame
    {
        public Guid RepeatId;
        public int Remaining;   // body iterations still allowed; -1 = infinite
    }

    /// <summary>
    /// A possibly-ramping playback gain. SetNow jumps instantly; RampTo glides to a target over a
    /// number of samples. Step() returns the current gain and advances one sample (so a ramp can
    /// span several riffs as the cursor carries the envelope forward).
    /// </summary>
    public class VolumeEnvelope
    {
        public double Current = 1.0;
        double target = 1.0;
        double rate = 0; // per-sample increment toward target (0 = steady)

        public void SetNow(double v) { Current = v; target = v; rate = 0; }

        public void RampTo(double tgt, double samples)
        {
            target = tgt;
            rate = samples > 0.5 ? (tgt - Current) / samples : 0;
            if (rate == 0) Current = tgt;
        }

        public double Step()
        {
            double c = Current;
            if (rate != 0)
            {
                Current += rate;
                if ((rate > 0 && Current >= target) || (rate < 0 && Current <= target)) { Current = target; rate = 0; }
            }
            return c;
        }

        public VolumeEnvelope Clone() => new VolumeEnvelope { Current = Current, target = target, rate = rate };
    }

    /// <summary>Context carried by a cursor down a branch (copied at forks). Used by the timeline player
    /// and the riff editor preview to set a riff's tempo / pitch / volume / instrument / reverse.</summary>
    public class FlowContext
    {
        public double Bpm = 120;
        public double Semitones = 0;
        public VolumeEnvelope Vol = new VolumeEnvelope(); // playback gain (set/ramped by Set-volume nodes)
        public bool Reverse = false;      // play downstream riffs backwards (toggled by Reverse nodes)
        public int GmProgram = 0;         // current instrument as a GM program (0-127) — no WaveFunction reference
        public bool Drum = false;         // true => percussion (drum kit) instead of a melodic program
        public List<LoopFrame> Loops = new List<LoopFrame>(); // active Repeat scopes (stack, innermost last)

        public FlowContext Copy()
        {
            var c = new FlowContext { Bpm = Bpm, Semitones = Semitones, Vol = Vol.Clone(), Reverse = Reverse, GmProgram = GmProgram, Drum = Drum };
            foreach (var f in Loops) c.Loops.Add(new LoopFrame { RepeatId = f.RepeatId, Remaining = f.Remaining });
            return c;
        }
    }

   
}
