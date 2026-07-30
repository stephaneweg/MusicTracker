using System;
using System.Collections.Generic;

namespace MusicTracker.Engine.Timeline.Effects
{
    /// <summary>
    /// Compresseur peak (feed-forward) : détecteur peak (max |L|, |R|) lissé par une enveloppe attack/release
    /// exponentielle, ratio linéaire au-dessus du seuil, makeup en dB. Un seul détecteur pour les deux
    /// canaux — c'est le comportement « stereo linked » usuel, qui empêche le pan mouvant que produirait
    /// un compresseur double mono. V1 pragmatique : pas de knee, pas de look-ahead, pas d'oversampling.
    /// </summary>
    public sealed class CompressorEffect : IAudioEffect
    {
        public string Kind => "comp";

        public double ThresholdDb = -18;
        public double Ratio = 3.0;             // 1 = pas de compression, 4 = /4 au-dessus du seuil
        public double AttackMs = 10;
        public double ReleaseMs = 100;
        public double MakeupDb = 0;

        readonly int sampleRate;
        float envelope;                          // linéaire (0..)

        public CompressorEffect(int sampleRate) { this.sampleRate = sampleRate; }

        public void Process(float[] l, float[] r, int frames)
        {
            double a = Math.Exp(-1.0 / (Math.Max(0.01, AttackMs) * 0.001 * sampleRate));
            double rel = Math.Exp(-1.0 / (Math.Max(0.01, ReleaseMs) * 0.001 * sampleRate));
            double thresh = Math.Pow(10, ThresholdDb / 20.0);
            double makeup = Math.Pow(10, MakeupDb / 20.0);
            double ratio = Math.Max(1.0, Ratio);
            double invRatio = 1.0 / ratio;
            float env = envelope;
            for (int i = 0; i < frames; i++)
            {
                float peak = Math.Max(Math.Abs(l[i]), Math.Abs(r[i]));
                // Enveloppe : attack quand le signal monte, release quand il redescend — coefficients « one-pole ».
                if (peak > env) env = (float)(a * env + (1 - a) * peak);
                else            env = (float)(rel * env + (1 - rel) * peak);
                double gain = 1.0;
                if (env > thresh)
                {
                    // Réduction en dB puis retour linéaire : k(x_dB - t_dB) où k = 1/ratio - 1 (négatif).
                    double envDb = 20.0 * Math.Log10(env);
                    double overDb = envDb - ThresholdDb;
                    double redDb = overDb * (invRatio - 1.0);
                    gain = Math.Pow(10, redDb / 20.0);
                }
                double g = gain * makeup;
                l[i] = (float)(l[i] * g);
                r[i] = (float)(r[i] * g);
            }
            envelope = env;
        }

        public void Reset() { envelope = 0; }

        public Dictionary<string, double> Save() => new Dictionary<string, double>
        {
            ["ThresholdDb"] = ThresholdDb,
            ["Ratio"] = Ratio,
            ["AttackMs"] = AttackMs,
            ["ReleaseMs"] = ReleaseMs,
            ["MakeupDb"] = MakeupDb,
        };

        public void Load(Dictionary<string, double> d)
        {
            if (d == null) return;
            double v;
            if (d.TryGetValue("ThresholdDb", out v)) ThresholdDb = v;
            if (d.TryGetValue("Ratio", out v)) Ratio = v;
            if (d.TryGetValue("AttackMs", out v)) AttackMs = v;
            if (d.TryGetValue("ReleaseMs", out v)) ReleaseMs = v;
            if (d.TryGetValue("MakeupDb", out v)) MakeupDb = v;
        }

        // Effet maison — pas d'état opaque à sérialiser (tout est dans le dict).
        public string SaveState() { return null; }
        public void LoadState(string state) { }
    }
}
