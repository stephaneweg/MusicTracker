using System;
using System.Collections.Generic;

namespace MusicTracker.Engine.Timeline.Effects
{
    /// <summary>
    /// Saturation douce (tanh) : le drive amplifie le signal avant le tanh, qui replie les crêtes ; le
    /// gain de sortie compense pour rester à niveau constant à l'oreille. <see cref="Mix"/> = 0 laisse
    /// passer le signal sec, 1 remplace tout par la version saturée. Pas d'oversampling (v1 pragmatique
    /// — repli spectral audible seulement sur les crêtes très saturées).
    /// </summary>
    public sealed class SaturationEffect : IAudioEffect
    {
        public string Kind => "sat";

        public double Drive = 0.5;               // 0 = transparent, 1 = tanh « bien chaud »
        public double Mix = 1.0;

        public SaturationEffect(int sampleRate) { /* sample-rate inutile pour le tanh, gardé par cohérence de l'API. */ }

        public void Process(float[] l, float[] r, int frames)
        {
            double d = Math.Max(0, Math.Min(1.0, Drive));
            double gain = 1.0 + d * 9.0;           // ×10 max en entrée : plage large, mais douce en début de course
            double outGain = 1.0 / (1.0 + d * 4.0); // compensation pour un ressenti de niveau à peu près stable
            double mix = Math.Max(0, Math.Min(1.0, Mix));
            double dry = 1.0 - mix;
            for (int i = 0; i < frames; i++)
            {
                double sl = Math.Tanh(l[i] * gain) * outGain;
                double sr = Math.Tanh(r[i] * gain) * outGain;
                l[i] = (float)(l[i] * dry + sl * mix);
                r[i] = (float)(r[i] * dry + sr * mix);
            }
        }

        public void Reset() { /* pas d'état */ }

        public Dictionary<string, double> Save() => new Dictionary<string, double>
        {
            ["Drive"] = Drive, ["Mix"] = Mix,
        };

        public void Load(Dictionary<string, double> d)
        {
            if (d == null) return;
            double v;
            if (d.TryGetValue("Drive", out v)) Drive = v;
            if (d.TryGetValue("Mix", out v)) Mix = v;
        }
    }
}
