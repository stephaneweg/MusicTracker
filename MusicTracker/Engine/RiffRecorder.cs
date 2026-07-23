using System;
using System.Collections.Generic;
using NAudio.Dsp;
using NAudio.Wave;

namespace MusicTracker.Engine
{
    /// <summary>
    /// Monophonic pitch transcription: turns a buffer of mic samples into a riff's slice grid. Per frame it
    /// estimates the fundamental by AUTOCORRELATION, preferring the SHORTEST strong period (so it doesn't
    /// drop an octave). Pitch is kept CONTINUOUS (float MIDI), median-pooled per slice and median-filtered
    /// over time, and only THEN rounded to a semitone — so a held but slightly-off / vibrato'd note resolves
    /// to the note that dominates around it (like a chromatic tuner) instead of flickering between semitones.
    /// Leading silence is trimmed so the first sung/played note lands on slice 0.
    /// </summary>
    public static class AudioPitch
    {
        /// <summary>MPM analysis window in samples (configurable). Smaller = lower latency, but worse on low pitches.</summary>
        public static int FrameSize { get; set; } = 2048;
        const int Hop = 512;      // step between frames (~12 ms at 44.1 kHz)
        const double MinFreq = 70, MaxFreq = 1200;
        const double ClarityMin = 0.50; // NSDF peak below this = unvoiced (voicing gate)
        const double MpmCutoff = 0.90;  // pick the first key maximum >= this × the highest (octave-robust)

        public const int Chromatic = 0xFFF; // all 12 pitch classes (no snapping)

        /// <summary>Scale/mode names — index used by <see cref="ScaleMask"/> (keep in sync with the UI combo).</summary>
        public static readonly string[] ScaleNames =
            { "Majeur", "Mineur", "Dorien", "Phrygien", "Lydien", "Mixolydien", "Locrien", "Penta. majeure", "Penta. mineure" };

        static readonly int[][] ScaleIntervals =
        {
            new[] { 0, 2, 4, 5, 7, 9, 11 }, // Majeur (ionien)
            new[] { 0, 2, 3, 5, 7, 8, 10 }, // Mineur (éolien)
            new[] { 0, 2, 3, 5, 7, 9, 10 }, // Dorien
            new[] { 0, 1, 3, 5, 7, 8, 10 }, // Phrygien
            new[] { 0, 2, 4, 6, 7, 9, 11 }, // Lydien
            new[] { 0, 2, 4, 5, 7, 9, 10 }, // Mixolydien
            new[] { 0, 1, 3, 5, 6, 8, 10 }, // Locrien
            new[] { 0, 2, 4, 7, 9 },        // Pentatonique majeure
            new[] { 0, 3, 5, 7, 10 },       // Pentatonique mineure
        };

        /// <summary>Interval pattern (semitones from the root) of a mode — for diatonic letter spelling.</summary>
        public static int[] ScaleDegrees(int modeIndex)
            => (int[])ScaleIntervals[modeIndex < 0 || modeIndex >= ScaleIntervals.Length ? 0 : modeIndex].Clone();

        /// <summary>12-bit pitch-class mask of a scale (mode) rooted at <paramref name="root"/> (0 = Do).</summary>
        public static int ScaleMask(int root, int modeIndex)
        {
            var iv = ScaleIntervals[modeIndex < 0 || modeIndex >= ScaleIntervals.Length ? 0 : modeIndex];
            int m = 0;
            foreach (int i in iv) m |= 1 << ((((root + i) % 12) + 12) % 12);
            return m;
        }

        // Round a continuous MIDI pitch to a note index (0..95), snapping to the nearest pitch in the scale
        // mask (so a slightly-off sung note lands on the right diatonic degree). -1 if out of range.
        static int NoteFromMidi(double midi, int scaleMask)
        {
            int m = (int)Math.Round(midi);
            if (scaleMask != 0 && scaleMask != Chromatic)
            {
                int best = m; double bestD = double.MaxValue;
                for (int d = -2; d <= 2; d++)
                {
                    int c = m + d, pc = (((c % 12) + 12) % 12);
                    if ((scaleMask & (1 << pc)) != 0)
                    {
                        double dist = Math.Abs(midi - c);
                        if (dist < bestD) { bestD = dist; best = c; }
                    }
                }
                m = best;
            }
            int note = m - 12; // app convention: note 0 = MIDI 12
            return (note < 0 || note >= 96) ? -1 : note;
        }

        // MPM (McLeod) per-frame pitch via the Normalized Square Difference Function (NSDF). Picks the FIRST
        // key maximum >= MpmCutoff × the highest one — that's the fundamental, which sidesteps the octave-down
        // error of a plain autocorrelation max. The NSDF value at that peak is the clarity (0..1, voicing).
        static double DetectPitch(float[] x, int start, int win, int sampleRate, double rmsThreshold, out double clarity)
        {
            clarity = 0;
            double energy = 0;
            for (int i = 0; i < win; i++) { double v = x[start + i]; energy += v * v; }
            if (Math.Sqrt(energy / win) < rmsThreshold) return 0; // gate: silence

            int maxLag = Math.Min(win - 1, (int)(sampleRate / MinFreq));
            int minLag = Math.Max(2, (int)(sampleRate / MaxFreq));
            var nsdf = new double[maxLag + 1];
            for (int tau = 0; tau <= maxLag; tau++)
            {
                double ac = 0, m = 0; int lim = win - tau;
                for (int i = 0; i < lim; i++) { double a = x[start + i], b = x[start + i + tau]; ac += a * b; m += a * a + b * b; }
                nsdf[tau] = m > 1e-12 ? 2 * ac / m : 0;
            }

            // Key maxima = the peak within each positive lobe of the NSDF (past the central lobe at tau ~ 0).
            int lag = 1;
            while (lag <= maxLag && nsdf[lag] > 0) lag++; // skip the lobe around tau = 0
            var keyLag = new List<int>();
            double highest = 0;
            while (lag <= maxLag)
            {
                if (nsdf[lag] > 0)
                {
                    int posMax = lag; double posVal = nsdf[lag];
                    while (lag <= maxLag && nsdf[lag] > 0) { if (nsdf[lag] > posVal) { posVal = nsdf[lag]; posMax = lag; } lag++; }
                    if (posMax >= minLag) { keyLag.Add(posMax); if (posVal > highest) highest = posVal; }
                }
                else lag++;
            }
            if (keyLag.Count == 0 || highest < ClarityMin) return 0;

            double cut = MpmCutoff * highest;
            int chosen = -1;
            foreach (int k in keyLag) if (nsdf[k] >= cut) { chosen = k; break; }
            if (chosen < 0) return 0;

            clarity = nsdf[chosen];
            double a0 = nsdf[Math.Max(0, chosen - 1)], b0 = nsdf[chosen], c0 = nsdf[Math.Min(maxLag, chosen + 1)];
            double denom = a0 - 2 * b0 + c0;
            double shift = Math.Abs(denom) > 1e-9 ? 0.5 * (a0 - c0) / denom : 0;
            if (shift < -1 || shift > 1) shift = 0;
            return sampleRate / (chosen + shift);
        }

        struct NoteEvent { public double Start, End, Midi; }

        /// <summary>Per-frame fundamental (Hz, 0 = unvoiced) for live/real-time use — exposes the internal MPM detector.</summary>
        public static double DetectFramePitch(float[] x, int start, int sampleRate, double rmsThreshold)
            => DetectPitch(x, start, x.Length - start, sampleRate, rmsThreshold, out _); // window = the array the caller filled

        /// <summary>The analysis hop size, so callers can drive a real-time loop at the same resolution.</summary>
        public static int HopSize => Hop;

        /// <summary>Round a continuous MIDI pitch to a note index (0..95), snapped to the scale. -1 if out of range.</summary>
        public static int NoteIndexFromMidi(double midi, int scaleMask) => NoteFromMidi(midi, scaleMask);

        /// <summary>
        /// Transcribe <paramref name="samples"/> (mono, -1..1) into slices. Per-frame MPM pitch -> a smoothed
        /// pitch contour -> note segmentation by HYSTERESIS (a note change is committed only when the pitch
        /// moves > ~0.6 semitone and STAYS there ~60 ms, so vibrato/glides don't split notes) -> snap to the
        /// scale -> quantise to slices, with leading silence trimmed so the first note lands on slice 0.
        /// </summary>
        public static SequencerSlice[] DetectSlices(float[] samples, int sampleRate, double bpm, int spq, double rmsThreshold, int scaleMask = Chromatic)
        {
            int Frame = FrameSize;
            if (samples == null || samples.Length < Frame || bpm <= 0 || spq <= 0) return new SequencerSlice[0];
            double hopSec = (double)Hop / sampleRate;

            // (1) Per-frame pitch contour (NaN = unvoiced), then a light median smooth.
            var midi = new List<double>();
            var time = new List<double>();
            for (int start = 0; start + Frame <= samples.Length; start += Hop)
            {
                double f = DetectPitch(samples, start, Frame, sampleRate, rmsThreshold, out double cl);
                time.Add((start + Frame / 2.0) / sampleRate);
                midi.Add(f > 0 ? 69 + 12 * Math.Log(f / 440.0, 2) : double.NaN);
            }
            var sm = MedianSmoothContour(midi, 2);
            int F = sm.Length;

            // (2)/(3) Hysteresis segmentation into note events (untrained-voice friendly).
            const double Thr = 0.6;                                    // semitone deviation to consider a change
            int hold = Math.Max(2, (int)Math.Round(0.06 / hopSec));    // ...sustained this long -> a new note
            int maxSil = Math.Max(2, (int)Math.Round(0.09 / hopSec));  // silence this long -> end the note
            int minNote = Math.Max(2, (int)Math.Round(0.05 / hopSec)); // drop notes shorter than this

            var notes = new List<NoteEvent>();
            int i = 0;
            while (i < F)
            {
                if (double.IsNaN(sm[i])) { i++; continue; }
                int segStart = i, lastVoiced = i, dev = 0, sil = 0, devStart = -1;
                var vals = new List<double> { sm[i] };
                double center = sm[i];
                int j = i + 1;
                for (; j < F; j++)
                {
                    if (double.IsNaN(sm[j])) { sil++; dev = 0; if (sil >= maxSil) break; continue; }
                    sil = 0;
                    double mv = sm[j];
                    if (Math.Abs(mv - center) > Thr)
                    {
                        if (dev == 0) devStart = j;
                        if (++dev >= hold) { j = devStart; break; } // sustained -> a real note change
                    }
                    else { dev = 0; vals.Add(mv); center = 0.7 * center + 0.3 * mv; lastVoiced = j; } // EMA centre (vibrato-tolerant)
                }
                if (lastVoiced - segStart + 1 >= minNote)
                    notes.Add(new NoteEvent { Start = time[segStart], End = time[lastVoiced] + hopSec, Midi = Median(vals) });
                i = j > segStart ? j : segStart + 1;
            }
            if (notes.Count == 0) return new SequencerSlice[0];

            // (4) Snap each note to the scale + quantise to slices; the first note starts on slice 0.
            double secPerSlice = (60.0 / bpm) / spq;
            double offset = notes[0].Start;
            int total = Math.Max(1, (int)Math.Ceiling((notes[notes.Count - 1].End - offset) / secPerSlice));
            var res = new SequencerSlice[total];
            foreach (var nt in notes)
            {
                int note = NoteFromMidi(nt.Midi, scaleMask);
                if (note < 0) continue;
                int s0 = (int)Math.Round((nt.Start - offset) / secPerSlice);
                int s1 = (int)Math.Round((nt.End - offset) / secPerSlice);
                if (s1 <= s0) s1 = s0 + 1;
                for (int s = Math.Max(0, s0); s < Math.Min(total, s1); s++) res[s].On(note, true);
            }
            return res;
        }

        // ---- 2-note polyphonic detection (homemade DSP) -----------------------------------------------
        const int PolyHarmonics = 8;     // harmonics summed per F0 candidate
        const double PolyRatio = 0.45;   // 2nd note kept only if its residual salience >= this × the 1st's
        const double PolyVoteFrac = 0.40;// a note is ON in a slice if present in >= this fraction of its frames
        const int PolyMidiLow = 37, PolyMidiHigh = 88; // candidate fundamental range (~D1..E6), within MinFreq..MaxFreq

        static double MidiToFreq(int m) => 440.0 * Math.Pow(2, (m - 69) / 12.0);

        // Harmonic-sum salience of an F0 candidate: Σ mag(h·f0)/h over harmonics (1/h weighting is naturally
        // octave-robust — a half/double-octave candidate matches fewer real peaks). Returns the best MIDI note.
        static int BestF0(float[] spec, double binToFreq, out double bestSal)
        {
            bestSal = 0; int best = -1, maxBin = spec.Length - 1;
            for (int m = PolyMidiLow; m <= PolyMidiHigh; m++)
            {
                double f0 = MidiToFreq(m), sal = 0;
                for (int h = 1; h <= PolyHarmonics; h++)
                {
                    int b = (int)Math.Round(h * f0 / binToFreq);
                    if (b < 1 || b >= maxBin) break;
                    float mag = spec[b];                       // local max over ±1 bin (window leakage)
                    if (spec[b - 1] > mag) mag = spec[b - 1];
                    if (spec[b + 1] > mag) mag = spec[b + 1];
                    sal += mag / h;
                }
                if (sal > bestSal) { bestSal = sal; best = m; }
            }
            return best;
        }

        // Zero out the spectral bins around each harmonic of f0 (± ~quarter-tone) so the residual reveals a 2nd note.
        static void CancelHarmonics(float[] spec, double binToFreq, double f0)
        {
            int maxBin = spec.Length - 1;
            for (int h = 1; h <= PolyHarmonics; h++)
            {
                double freq = h * f0, bin = freq / binToFreq;
                if (bin >= maxBin) break;
                int hw = Math.Max(1, (int)Math.Round(freq * (Math.Pow(2, 0.5 / 12) - 1) / binToFreq)); // quarter-tone half-width
                int b = (int)Math.Round(bin);
                for (int k = b - hw - 1; k <= b + hw + 1; k++)
                    if (k >= 0 && k <= maxBin) spec[k] = 0;
            }
        }

        /// <summary>
        /// Transcribe <paramref name="samples"/> into slices detecting up to TWO simultaneous notes per frame
        /// (homemade multi-F0): FFT magnitude -> harmonic-sum salience picks the 1st note -> its harmonics are
        /// cancelled -> the 2nd note is taken from the residual (kept only if strong enough, so a mono passage
        /// stays mono). Notes are snapped to the scale; per slice a note is ON if it dominates its frames. Best
        /// on a CLEAN tone (distortion adds harmonics that confuse it). Octave dyads can't be resolved.
        /// </summary>
        public static SequencerSlice[] DetectSlicesPoly(float[] samples, int sampleRate, double bpm, int spq, double rmsThreshold, int scaleMask = Chromatic, double secondNoteThreshold = PolyRatio)
        {
            int Frame = FrameSize;
            if (samples == null || samples.Length < Frame || bpm <= 0 || spq <= 0) return new SequencerSlice[0];
            double hopSec = (double)Hop / sampleRate;
            int order = (int)Math.Round(Math.Log(Frame, 2));
            double binToFreq = (double)sampleRate / Frame;

            var win = new float[Frame];
            for (int i = 0; i < Frame; i++) win[i] = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (Frame - 1)));

            // (1) Per-frame: detect up to 2 notes (snapped to the scale), collected as a set of note indices.
            var perFrame = new List<HashSet<int>>();
            var time = new List<double>();
            var fft = new Complex[Frame];
            var spec = new float[Frame / 2 + 1];

            for (int start = 0; start + Frame <= samples.Length; start += Hop)
            {
                time.Add((start + Frame / 2.0) / sampleRate);
                var set = new HashSet<int>();
                perFrame.Add(set);

                double energy = 0;
                for (int i = 0; i < Frame; i++) { double v = samples[start + i]; energy += v * v; }
                if (Math.Sqrt(energy / Frame) < rmsThreshold) continue; // silence

                for (int i = 0; i < Frame; i++) { fft[i].X = (float)(samples[start + i] * win[i]); fft[i].Y = 0; }
                FastFourierTransform.FFT(true, order, fft);
                for (int k = 0; k <= Frame / 2; k++) spec[k] = (float)Math.Sqrt(fft[k].X * fft[k].X + fft[k].Y * fft[k].Y);

                int m1 = BestF0(spec, binToFreq, out double sal1);
                if (m1 < 0 || sal1 <= 0) continue;
                int n1 = NoteFromMidi(m1, scaleMask);
                if (n1 >= 0) set.Add(n1);

                CancelHarmonics(spec, binToFreq, MidiToFreq(m1));
                int m2 = BestF0(spec, binToFreq, out double sal2);
                if (m2 >= 0 && Math.Abs(m2 - m1) >= 1 && sal2 >= PolyRatio * sal1)
                {
                    int n2 = NoteFromMidi(m2, scaleMask);
                    if (n2 >= 0) set.Add(n2);
                }
            }

            int F = perFrame.Count;
            if (F == 0) return new SequencerSlice[0];

            // (2) De-flicker: keep a note in a frame only if it also appears in an adjacent frame.
            bool Has(int f, int note) => f >= 0 && f < F && perFrame[f].Contains(note);
            var kept = new List<HashSet<int>>(F);
            for (int f = 0; f < F; f++)
            {
                var s = new HashSet<int>();
                foreach (int note in perFrame[f]) if (Has(f - 1, note) || Has(f + 1, note)) s.Add(note);
                kept.Add(s);
            }

            // (3) Trim leading/trailing silence so the first note lands on slice 0.
            int firstV = -1, lastV = -1;
            for (int f = 0; f < F; f++) if (kept[f].Count > 0) { if (firstV < 0) firstV = f; lastV = f; }
            if (firstV < 0) return new SequencerSlice[0];

            // (4) Quantise: per slice, a note is ON if present in >= PolyVoteFrac of the frames falling in it.
            double secPerSlice = (60.0 / bpm) / spq;
            double offset = time[firstV];
            int total = Math.Max(1, (int)Math.Ceiling((time[lastV] + hopSec - offset) / secPerSlice));
            var counts = new int[total, 96];
            var nFrames = new int[total];
            for (int f = firstV; f <= lastV; f++)
            {
                int s = (int)Math.Floor((time[f] - offset) / secPerSlice);
                if (s < 0 || s >= total) continue;
                nFrames[s]++;
                foreach (int note in kept[f]) counts[s, note]++;
            }

            var res = new SequencerSlice[total];
            for (int s = 0; s < total; s++)
            {
                if (nFrames[s] == 0) continue;
                int need = Math.Max(1, (int)Math.Ceiling(nFrames[s] * PolyVoteFrac));
                for (int note = 0; note < 96; note++) if (counts[s, note] >= need) res[s].On(note, true);
            }
            return res;
        }

        // Round x to the nearest multiple of snap; with strength &lt; 1 only move PART of the way there
        // (so the human feel is kept). strength 1 = full snap, 0 = no move.
        static int SnapRound(int x, int snap, double strength)
        {
            int q = ((x + snap / 2) / snap) * snap;
            return strength >= 1.0 ? q : (int)Math.Round(x + strength * (q - x));
        }

        /// <summary>
        /// Rhythmic quantisation by ONSET: for every note, each contiguous run has its START and END pulled to
        /// the nearest grid line of <paramref name="snap"/> slices (grid anchored on slice 0 = the first note).
        /// A run never shrinks below one slice, so short notes survive; the riff length is rounded up to whole
        /// cells. <paramref name="strength"/> (0..1) lets a note move only part-way to the grid (keep human feel).
        /// Absolute grid (not relative durations) → small timing errors are corrected and nothing drifts.
        /// Works on any slice grid (mono or polyphonic).
        /// </summary>
        public static SequencerSlice[] SnapToGrid(SequencerSlice[] slices, int snap, double strength = 1.0)
        {
            if (slices == null || slices.Length == 0 || snap <= 1 || strength <= 0) return slices;
            if (strength > 1) strength = 1;

            int total = slices.Length, cap = total + snap; // headroom: a final run can round up past the end
            var res = new SequencerSlice[cap];
            for (int note = 0; note < 96; note++)
            {
                int s = 0;
                while (s < total)
                {
                    if (!slices[s].On(note)) { s++; continue; }
                    int s0 = s;
                    while (s < total && slices[s].On(note)) s++;
                    int s1 = s; // exclusive end of the run

                    int q0 = SnapRound(s0, snap, strength);
                    int q1 = SnapRound(s1, snap, strength);
                    if (q1 <= q0) q1 = q0 + 1;        // keep at least one slice (don't lose a short note)
                    if (q1 > cap) q1 = cap;
                    for (int c = Math.Max(0, q0); c < q1; c++) res[c].On(note, true);
                }
            }

            // Trim trailing silence, then round the length up to whole grid cells.
            int last = -1;
            for (int c = 0; c < cap; c++) if (res[c].NotesLow != 0 || res[c].NotesHigh != 0) last = c;
            int len = Math.Max(1, ((last + 1 + snap - 1) / snap) * snap);
            if (len != cap) Array.Resize(ref res, len);
            return res;
        }

        static double Median(List<double> v)
        {
            var a = v.ToArray();
            Array.Sort(a);
            return a[a.Length / 2];
        }

        // Median of the voiced values in [i-w, i+w]; unvoiced (NaN) stays unvoiced (rests preserved).
        static double[] MedianSmoothContour(List<double> p, int w)
        {
            int n = p.Count;
            var o = new double[n];
            var buf = new List<double>();
            for (int i = 0; i < n; i++)
            {
                if (double.IsNaN(p[i])) { o[i] = double.NaN; continue; }
                buf.Clear();
                for (int j = Math.Max(0, i - w); j <= Math.Min(n - 1, i + w); j++)
                    if (!double.IsNaN(p[j])) buf.Add(p[j]);
                buf.Sort();
                o[i] = buf[buf.Count / 2];
            }
            return o;
        }
    }

    /// <summary>
    /// A click track: <see cref="Tick"/> (called on each beat) re-triggers a short click; Read fills
    /// silence in between. The down-beat uses a higher/louder accent click.
    /// </summary>
    public class MetronomeProvider : WaveProvider16
    {
        readonly short[] beat, accent;
        readonly object gate = new object();
        short[] cur;
        int pos;

        public MetronomeProvider(int sampleRate) : base(sampleRate, 1)
        {
            beat = Click(sampleRate, 1400, 0.35);
            accent = Click(sampleRate, 2100, 0.55);
            cur = beat; pos = beat.Length; // start silent
        }

        static short[] Click(int sr, double freq, double amp)
        {
            int n = (int)(sr * 0.05); // 50 ms
            var b = new short[n];
            for (int i = 0; i < n; i++)
            {
                double env = Math.Exp(-6.0 * i / n);          // quick decay
                double v = Math.Sin(2 * Math.PI * freq * i / sr) * env * amp;
                b[i] = (short)(v * short.MaxValue);
            }
            return b;
        }

        public void Tick(bool downbeat)
        {
            lock (gate) { cur = downbeat ? accent : beat; pos = 0; }
        }

        public override int Read(short[] buffer, int offset, int sampleCount)
        {
            lock (gate)
                for (int i = 0; i < sampleCount; i++)
                    buffer[offset + i] = pos < cur.Length ? cur[pos++] : (short)0;
            return sampleCount;
        }
    }
}
