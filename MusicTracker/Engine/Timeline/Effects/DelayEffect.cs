using System;
using System.Collections.Generic;

namespace MusicTracker.Engine.Timeline.Effects
{
    /// <summary>
    /// Delay stéréo : deux lignes à retard indépendantes (L et R), avec optionnel ping-pong (l'écho L
    /// nourrit la ligne R et réciproquement). Ring buffers séparés, taille = 4 s au sample-rate courant
    /// (couvre <see cref="TimeMs"/> jusqu'à 4000 ms). Le paramètre <see cref="Mix"/> mixe le signal sec
    /// avec les retards ; <see cref="Feedback"/> plafonné à 0.95 pour éviter la divergence.
    /// </summary>
    public sealed class DelayEffect : IAudioEffect
    {
        public string Kind => "delay";

        public double TimeMs = 300;
        public double Feedback = 0.35;
        public double Mix = 0.30;
        public double PingPong = 0;              // 0 = stéréo standard, 1 = ping-pong

        readonly int sampleRate;
        readonly float[] bufL, bufR;
        int idx;

        public DelayEffect(int sampleRate)
        {
            this.sampleRate = sampleRate;
            int cap = sampleRate * 4 + 16;       // 4 s de retard maximum, largement au-dessus de la valeur exposée
            bufL = new float[cap];
            bufR = new float[cap];
        }

        public void Process(float[] l, float[] r, int frames)
        {
            int cap = bufL.Length;
            int d = Math.Max(1, Math.Min(cap - 1, (int)Math.Round(TimeMs * 0.001 * sampleRate)));
            float fb = (float)Math.Max(0, Math.Min(0.95, Feedback));
            float mix = (float)Math.Max(0, Math.Min(1.0, Mix));
            float dry = 1f - mix;                  // couplage équi-puissance simplifié : sec + wet somment linéairement
            bool ping = PingPong >= 0.5;
            for (int i = 0; i < frames; i++)
            {
                int rIdx = idx - d; if (rIdx < 0) rIdx += cap;
                float dL = bufL[rIdx];
                float dR = bufR[rIdx];
                float inL = l[i], inR = r[i];
                // Ping-pong : le retour L vient de la ligne R et inversement — donne l'écho qui « saute » de canal en canal.
                if (ping)
                {
                    bufL[idx] = inL + dR * fb;
                    bufR[idx] = inR + dL * fb;
                }
                else
                {
                    bufL[idx] = inL + dL * fb;
                    bufR[idx] = inR + dR * fb;
                }
                l[i] = dry * inL + mix * dL;
                r[i] = dry * inR + mix * dR;
                idx++; if (idx >= cap) idx = 0;
            }
        }

        public void Reset()
        {
            Array.Clear(bufL, 0, bufL.Length);
            Array.Clear(bufR, 0, bufR.Length);
            idx = 0;
        }

        public Dictionary<string, double> Save() => new Dictionary<string, double>
        {
            ["TimeMs"] = TimeMs,
            ["Feedback"] = Feedback,
            ["Mix"] = Mix,
            ["PingPong"] = PingPong,
        };

        public void Load(Dictionary<string, double> d)
        {
            if (d == null) return;
            double v;
            if (d.TryGetValue("TimeMs", out v)) TimeMs = v;
            if (d.TryGetValue("Feedback", out v)) Feedback = v;
            if (d.TryGetValue("Mix", out v)) Mix = v;
            if (d.TryGetValue("PingPong", out v)) PingPong = v;
        }

        // Aucun état interne à sérialiser (les buffers de retard se remplissent en jouant).
        public string SaveState() => null;
        public void LoadState(string state) { }
    }
}
