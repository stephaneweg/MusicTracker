using System.Collections.Generic;

namespace MusicTracker.Engine.Timeline.Effects
{
    /// <summary>
    /// Égaliseur paramétrique 3 bandes : low-shelf (basses), peaking (médium) et high-shelf (aigus).
    /// Chaque bande a sa fréquence et son gain en dB (paramètre neutre = 0 dB → bande transparente,
    /// l'insert n'ajoute alors aucun coût CPU car <see cref="BiquadStereo"/> se met en bypass).
    /// </summary>
    public sealed class EqEffect : IAudioEffect
    {
        public string Kind => "eq";

        public double LowFreq = 120;
        public double LowGainDb = 0;
        public double MidFreq = 1000;
        public double MidGainDb = 0;
        public double MidQ = 1.0;
        public double HighFreq = 6000;
        public double HighGainDb = 0;

        readonly BiquadStereo low, mid, high;

        public EqEffect(int sampleRate)
        {
            low = new BiquadStereo(sampleRate);
            mid = new BiquadStereo(sampleRate);
            high = new BiquadStereo(sampleRate);
            Update();
        }

        /// <summary>Recalcule les coefficients à partir des paramètres publics — à appeler après édition depuis l'UI.</summary>
        public void Update()
        {
            low.SetLowShelf((float)LowFreq, (float)LowGainDb);
            mid.SetPeaking((float)MidFreq, (float)MidGainDb, (float)MidQ);
            high.SetHighShelf((float)HighFreq, (float)HighGainDb);
        }

        public void Process(float[] l, float[] r, int frames)
        {
            low.Process(l, r, frames);
            mid.Process(l, r, frames);
            high.Process(l, r, frames);
        }

        public void Reset() { low.Reset(); mid.Reset(); high.Reset(); }

        public Dictionary<string, double> Save() => new Dictionary<string, double>
        {
            ["LowFreq"] = LowFreq, ["LowGainDb"] = LowGainDb,
            ["MidFreq"] = MidFreq, ["MidGainDb"] = MidGainDb, ["MidQ"] = MidQ,
            ["HighFreq"] = HighFreq, ["HighGainDb"] = HighGainDb,
        };

        public void Load(Dictionary<string, double> d)
        {
            if (d == null) return;
            double v;
            if (d.TryGetValue("LowFreq", out v)) LowFreq = v;
            if (d.TryGetValue("LowGainDb", out v)) LowGainDb = v;
            if (d.TryGetValue("MidFreq", out v)) MidFreq = v;
            if (d.TryGetValue("MidGainDb", out v)) MidGainDb = v;
            if (d.TryGetValue("MidQ", out v)) MidQ = v;
            if (d.TryGetValue("HighFreq", out v)) HighFreq = v;
            if (d.TryGetValue("HighGainDb", out v)) HighGainDb = v;
            Update();
        }

        public string SaveState() { return null; }
        public void LoadState(string state) { }
    }
}
