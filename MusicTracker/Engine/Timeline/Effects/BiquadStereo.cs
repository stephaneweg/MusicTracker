using System;

namespace MusicTracker.Engine.Timeline.Effects
{
    /// <summary>
    /// Biquad stéréo pour la chaîne d'effets d'insert : trois topologies (low-shelf, peaking, high-shelf)
    /// destinées à un égaliseur 3 bandes. Les équations sont celles de la « Audio EQ Cookbook » de Robert
    /// Bristow-Johnson — les mêmes qui sont utilisées dans <see cref="MeltySynth.BiQuadFilter"/>, ré-écrites
    /// ici pour être indépendantes du synthé (deux canaux + gain en dB au lieu d'une seule voix mono).
    /// </summary>
    internal sealed class BiquadStereo
    {
        readonly int sampleRate;
        // Coefficients normalisés (a0 = 1) — un seul jeu partagé par les deux canaux, les états x/y sont distincts.
        float b0, b1, b2, a1, a2;
        float xl1, xl2, yl1, yl2;
        float xr1, xr2, yr1, yr2;
        bool active;

        public BiquadStereo(int sampleRate) { this.sampleRate = sampleRate; }

        public void Reset()
        {
            xl1 = xl2 = yl1 = yl2 = 0;
            xr1 = xr2 = yr1 = yr2 = 0;
        }

        /// <summary>Low-shelf (basses) : gain <paramref name="gainDb"/> en dB sous <paramref name="freq"/>.</summary>
        public void SetLowShelf(float freq, float gainDb)
        {
            float A = (float)Math.Pow(10, gainDb / 40.0);
            float w0 = 2 * (float)Math.PI * Clamp(freq, 20, sampleRate * 0.45f) / sampleRate;
            float cos = (float)Math.Cos(w0);
            float sin = (float)Math.Sin(w0);
            float S = 1f;                                   // pente shelf « proche de 1 » : pas de bosse indésirable
            float alpha = sin / 2f * (float)Math.Sqrt((A + 1 / A) * (1 / S - 1) + 2);
            float sqrtA = (float)Math.Sqrt(A);
            float b0i = A * ((A + 1) - (A - 1) * cos + 2 * sqrtA * alpha);
            float b1i = 2 * A * ((A - 1) - (A + 1) * cos);
            float b2i = A * ((A + 1) - (A - 1) * cos - 2 * sqrtA * alpha);
            float a0i = (A + 1) + (A - 1) * cos + 2 * sqrtA * alpha;
            float a1i = -2 * ((A - 1) + (A + 1) * cos);
            float a2i = (A + 1) + (A - 1) * cos - 2 * sqrtA * alpha;
            Normalize(b0i, b1i, b2i, a0i, a1i, a2i);
            active = Math.Abs(gainDb) > 0.05f;
        }

        /// <summary>Peaking (médium) autour de <paramref name="freq"/>, largeur définie par Q.</summary>
        public void SetPeaking(float freq, float gainDb, float q)
        {
            float A = (float)Math.Pow(10, gainDb / 40.0);
            float w0 = 2 * (float)Math.PI * Clamp(freq, 20, sampleRate * 0.45f) / sampleRate;
            float cos = (float)Math.Cos(w0);
            float sin = (float)Math.Sin(w0);
            float alpha = sin / (2 * Math.Max(0.1f, q));
            float b0i = 1 + alpha * A;
            float b1i = -2 * cos;
            float b2i = 1 - alpha * A;
            float a0i = 1 + alpha / A;
            float a1i = -2 * cos;
            float a2i = 1 - alpha / A;
            Normalize(b0i, b1i, b2i, a0i, a1i, a2i);
            active = Math.Abs(gainDb) > 0.05f;
        }

        /// <summary>High-shelf (aigus) : gain <paramref name="gainDb"/> en dB au-dessus de <paramref name="freq"/>.</summary>
        public void SetHighShelf(float freq, float gainDb)
        {
            float A = (float)Math.Pow(10, gainDb / 40.0);
            float w0 = 2 * (float)Math.PI * Clamp(freq, 20, sampleRate * 0.45f) / sampleRate;
            float cos = (float)Math.Cos(w0);
            float sin = (float)Math.Sin(w0);
            float S = 1f;
            float alpha = sin / 2f * (float)Math.Sqrt((A + 1 / A) * (1 / S - 1) + 2);
            float sqrtA = (float)Math.Sqrt(A);
            float b0i = A * ((A + 1) + (A - 1) * cos + 2 * sqrtA * alpha);
            float b1i = -2 * A * ((A - 1) + (A + 1) * cos);
            float b2i = A * ((A + 1) + (A - 1) * cos - 2 * sqrtA * alpha);
            float a0i = (A + 1) - (A - 1) * cos + 2 * sqrtA * alpha;
            float a1i = 2 * ((A - 1) - (A + 1) * cos);
            float a2i = (A + 1) - (A - 1) * cos - 2 * sqrtA * alpha;
            Normalize(b0i, b1i, b2i, a0i, a1i, a2i);
            active = Math.Abs(gainDb) > 0.05f;
        }

        void Normalize(float b0i, float b1i, float b2i, float a0i, float a1i, float a2i)
        {
            b0 = b0i / a0i; b1 = b1i / a0i; b2 = b2i / a0i;
            a1 = a1i / a0i; a2 = a2i / a0i;
        }

        public void Process(float[] l, float[] r, int frames)
        {
            if (!active) return;
            for (int i = 0; i < frames; i++)
            {
                float xl = l[i];
                float yl = b0 * xl + b1 * xl1 + b2 * xl2 - a1 * yl1 - a2 * yl2;
                xl2 = xl1; xl1 = xl; yl2 = yl1; yl1 = yl;
                l[i] = yl;

                float xr = r[i];
                float yr = b0 * xr + b1 * xr1 + b2 * xr2 - a1 * yr1 - a2 * yr2;
                xr2 = xr1; xr1 = xr; yr2 = yr1; yr1 = yr;
                r[i] = yr;
            }
        }

        static float Clamp(float v, float lo, float hi) => v < lo ? lo : (v > hi ? hi : v);
    }
}
