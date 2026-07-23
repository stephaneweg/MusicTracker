using System;
using System.Collections.Generic;
using MusicTracker.Engine.Timeline;

namespace MusicTracker.Engine.Flow
{
    /// <summary>Common surface of a single-riff voice source used by the looping preview providers
    /// (currently implemented by the SoundFont <see cref="MeltyRiffPlayer"/>).</summary>
    public interface IRiffSource
    {
        double ReadNext();
        bool TimelineEnded { get; }
        int CurrentSlice { get; }
    }

    /// <summary>
    /// Plays one riff on one instrument via a small MeltySynth (SoundFont) <see cref="MeltySynth.Synthesizer"/>.
    /// Implements <see cref="IRiffSource"/> (ReadNext / TimelineEnded / CurrentSlice) so it drops into the
    /// looping preview providers. The GM program / drum flag come from the context instrument.
    /// </summary>
    public class MeltyRiffPlayer : IRiffSource
    {
        readonly MeltySynth.Synthesizer synth;
        readonly int channel;
        readonly double interval;      // samples per slice
        readonly int length;           // total slices
        readonly VolumeEnvelope vol;
        readonly List<int>[] attackKeys, releaseKeys; // per slice: MIDI keys to note-on / note-off
        int noteIndex;
        double[] chunk = new double[0];
        int chunkPos, chunkLen;
        float[] rL = new float[0], rR = new float[0];

        public bool TimelineEnded { get; private set; }
        public bool Finished { get; private set; }
        public int CurrentSlice => noteIndex;
        public int Length => length;

        /// <summary>True when the MeltySynth engine is enabled and a SoundFont is loaded.</summary>

        public MeltyRiffPlayer(Riff riff, FlowContext ctx, int sampleRate, int startSlice = 0)
        {
            vol = ctx.Vol ?? new VolumeEnvelope();
            int transpose = (int)Math.Round(ctx.Semitones);
            int program = 0; bool drum = false;
            program = ctx.GmProgram; drum = ctx.Drum;

            double bpm = ctx.Bpm > 0 ? ctx.Bpm : 120;
            int spq = riff.SlicesPerQuarter > 0 ? riff.SlicesPerQuarter : 4;
            interval = (60.0 / bpm) / spq * sampleRate;
            if (interval < 1) interval = 1;
            length = Math.Max(0, riff.LengthSlices);

            var settings = new MeltySynth.SynthesizerSettings(sampleRate) { EnableReverbAndChorus = true, MaximumPolyphony = 64 };
            synth = new MeltySynth.Synthesizer(InstrumentCatalog.SoundFontObject, settings);
            // A single-instrument preview: apply the per-instrument boost straight on the master volume.
            synth.MasterVolume = (float)AppSettings.Instance.BoostGain(drum ? InstrumentCatalog.DrumIndex : Clamp(program, 0, 127));
            channel = drum ? synth.PercussionChannel : 0;
            int prog = Clamp(program, 0, 127);
            if (drum)
            {
                // The percussion channel already defaults to bank 128, so only the KIT has to be selected
                // (patch 0 = Standard, 8 = Room, 16 = Power, …) — without this every kit sounded like Standard.
                synth.ProcessMidiMessage(channel, 0xC0, prog, 0);                                // drum-kit change
            }
            else
            {
                bool expr = InstrumentCatalog.ExprPrograms != null && InstrumentCatalog.ExprPrograms.Contains(prog);
                synth.ProcessMidiMessage(channel, 0xB0, 0, expr ? InstrumentCatalog.ExprBank : 0);     // bank select
                synth.ProcessMidiMessage(channel, 0xC0, prog, 0);                                // program change
                if (expr) synth.ProcessMidiMessage(channel, 0xB0, 2, TimelinePlayer.MeltyDynamics); // CC2 dynam
            }

            attackKeys = new List<int>[length + 1];
            releaseKeys = new List<int>[length + 1];
            foreach (var n in riff.Notes ?? new List<RiffNote>())
            {
                if (n.Note < 0 || n.Note >= 96) continue;
                int s = n.Start, e = n.End;
                if (ctx.Reverse) { int ns = length - e, ne = length - s; s = ns; e = ne; } // mirror in time
                if (s < 0) s = 0;
                if (e > length) e = length;
                if (e <= s) continue;
                int key = Clamp(n.Note + 12 + transpose, 0, 127); // note index -> MIDI key (+ context transpose)
                (attackKeys[s] ?? (attackKeys[s] = new List<int>())).Add(key);
                (releaseKeys[e] ?? (releaseKeys[e] = new List<int>())).Add(key);
            }

            noteIndex = (startSlice > 0 && length > 0) ? Math.Min(startSlice, length - 1) : 0;
            for (int s = 0; s <= noteIndex && s <= length; s++) Dispatch(s); // notes spanning the start are already on
        }

        static int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);

        void Dispatch(int slice)
        {
            if (slice < 0 || slice > length) return;
            var rel = releaseKeys[slice];
            if (rel != null) foreach (int k in rel) synth.NoteOff(channel, k);
            var att = attackKeys[slice];
            if (att != null) foreach (int k in att) synth.NoteOn(channel, k, TimelinePlayer.MeltyVelocity);
        }

        public double ReadNext()
        {
            if (Finished) return 0;
            if (chunkPos >= chunkLen)
            {
                if (noteIndex >= length)
                {
                    TimelineEnded = true;
                    if (synth.ActiveVoiceCount == 0) { Finished = true; return 0; }
                }
                RenderNextChunk();
            }
            return chunk[chunkPos++] * vol.Step();
        }

        void RenderNextChunk()
        {
            // Cumulative flooring keeps the tempo exact despite a fractional samples-per-slice.
            int n = noteIndex < length
                ? (int)Math.Max(1, (long)Math.Floor((noteIndex + 1) * interval) - (long)Math.Floor(noteIndex * interval))
                : 256; // ring-out tail block past the end
            if (rL.Length < n) { rL = new float[n]; rR = new float[n]; }
            if (chunk.Length < n) chunk = new double[n];
            synth.Render(rL.AsSpan(0, n), rR.AsSpan(0, n));
            for (int i = 0; i < n; i++) chunk[i] = (rL[i] + rR[i]) * 0.5;
            chunkLen = n; chunkPos = 0;
            if (noteIndex < length) { noteIndex++; Dispatch(noteIndex); }
        }
    }
}
