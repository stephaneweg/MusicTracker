namespace MusicTracker.Engine
{
    /// <summary>
    /// Central audio configuration. The whole engine (live playback AND export) runs at this sample
    /// rate; the SoundFont is resampled to it on load (see <see cref="InstrumentCatalog.TargetSampleRate"/>).
    /// 22050 Hz matches roland.sf2's native rate, so its samples need no resampling.
    /// </summary>
    public static class AudioFormat
    {
        /// <summary>Compile-time fallback rate (used as the default-parameter value where a constant is
        /// required, and as the initial value of <see cref="SampleRate"/>). 22050 Hz matches roland.sf2.</summary>
        public const int DefaultSampleRate = 22050;

        /// <summary>The engine's working sample rate. Settable from the global settings; the SoundFont is
        /// (re)resampled to it on load, so changing it should be followed by a SoundFont reload. Reads
        /// at runtime, so any provider/exporter created afterwards picks up the new rate.</summary>
        public static int SampleRate { get; set; } = DefaultSampleRate;

        /// <summary>Global output gain applied at the final mix stage (before the <see cref="SoftClip"/>
        /// limiter), in playback AND export. Raised from the old conservative 0.5 (-6 dB, needed only for the
        /// former hard clamp) to 0.85 so the overall level sits closer to MuseScore's — the soft limiter now
        /// rounds off dense-passage peaks instead of hard clipping, so no boost is needed for a normal level.</summary>
        public const double OutputGain = 0.85;

        /// <summary>Soft limiter for the final mono mix: linear below <c>Threshold</c>, then a smooth tanh
        /// knee that asymptotes to ±1. Replaces hard clamping so boosting a track (or dense layering)
        /// rounds the peaks off musically instead of crackling.</summary>
        const double Threshold = 0.75;
        public static double SoftClip(double x)
        {
            if (x > Threshold) return Threshold + (1.0 - Threshold) * System.Math.Tanh((x - Threshold) / (1.0 - Threshold));
            if (x < -Threshold) return -Threshold + (1.0 - Threshold) * System.Math.Tanh((x + Threshold) / (1.0 - Threshold));
            return x;
        }
    }
}
