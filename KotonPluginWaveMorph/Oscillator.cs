using System;

namespace KotonPluginWaveMorph
{
    /// <summary>
    /// Formes d'onde primitives des deux oscillateurs du Wave Morph. Volontairement limité aux 4
    /// classiques (Sine / Square / Triangle / Sawtooth) — le "morphing" du plugin vient du lerp
    /// linéaire entre les deux oscillateurs (X-Fade), pas d'une palette de wavetables. Un utilisateur
    /// qui veut plus de couleurs assemble ces 4 en combinaison avec l'X-Fade et les filtres.
    ///
    /// **Multiplicateurs de fréquence** : chaque oscillateur peut multiplier / diviser sa fréquence
    /// de la note MIDI (×1, ×2..×8, /2, /3, /4). Combiné avec le détune en cents et le morphing,
    /// ça permet des sons de type sub-oscillator, harmonique isolé, ou dyade (deux notes à la
    /// quinte / octave).
    /// </summary>
    public enum WavePrim
    {
        Sine = 0,
        Square = 1,
        Triangle = 2,
        Sawtooth = 3,
    }

    /// <summary>
    /// Générateur des 4 formes primitives. Comme <c>KotonPluginFmSynth.Osc</c>, prend une phase en
    /// radians potentiellement hors [0, 2π) (la phase de l'oscillateur avance librement) et normalise
    /// en interne. Retourne un signal dans [-1, +1].
    ///
    /// **Coût CPU** : switch sur 4 cas + un sin/cos pour Sine + arithmétique simple pour les autres.
    /// Négligeable vs la boucle de sample-level du plugin.
    /// </summary>
    internal static class WaveOsc
    {
        const double TwoPi = 2 * Math.PI;

        public static float Sample(WavePrim w, double phase)
        {
            phase = phase % TwoPi;
            if (phase < 0) phase += TwoPi;

            switch (w)
            {
                case WavePrim.Sine:
                    return (float)Math.Sin(phase);

                case WavePrim.Square:
                    return phase < Math.PI ? 1f : -1f;

                case WavePrim.Triangle:
                {
                    // Rampe -1 → +1 sur [0, π), puis +1 → -1 sur [π, 2π).
                    double p = phase / Math.PI;
                    return (float)(p < 1.0 ? (2.0 * p - 1.0) : (3.0 - 2.0 * p));
                }

                case WavePrim.Sawtooth:
                    // -1 en 0, +1 juste avant 2π, discontinuité à la fin.
                    return (float)((phase / Math.PI) - 1.0);

                default:
                    return 0f;
            }
        }

        public const int Count = 4;

        /// <summary>Libellés courts pour la ComboBox de l'éditeur.</summary>
        public static readonly string[] Names = { "Sine", "Square", "Triangle", "Sawtooth" };

        /// <summary>Cast d'un double (valeur de KotonParameter) vers l'enum, clampé et arrondi.</summary>
        public static WavePrim FromDouble(double v)
        {
            int i = (int)Math.Round(v);
            if (i < 0) i = 0;
            else if (i >= Count) i = Count - 1;
            return (WavePrim)i;
        }
    }

    /// <summary>
    /// Table des multiplicateurs de fréquence pour un oscillateur. L'index (0..10) est stocké dans le
    /// KotonParameter ; le multiplicateur effectif est lu ici. Choix cohérent avec le mockup :
    /// - 0..7 : ×1, ×2, ×3, ×4, ×5, ×6, ×7, ×8 (harmoniques entiers montants — utile pour un
    ///           2e oscillator qui joue à l'octave, la quinte, la tierce majeure...)
    /// - 8..10 : /2, /3, /4 (sub-octave et harmoniques descendants — utile pour un sub-oscillateur
    ///           qui renforce le bas)
    /// </summary>
    internal static class FreqMult
    {
        public static readonly double[] Values =
        {
            1.0, 2.0, 3.0, 4.0, 5.0, 6.0, 7.0, 8.0,
            0.5, 1.0 / 3.0, 0.25,
        };

        public static readonly string[] Labels =
        {
            "×1", "×2", "×3", "×4", "×5", "×6", "×7", "×8",
            "/2", "/3", "/4",
        };

        public const int Count = 11;

        public static double Get(int idx)
        {
            if (idx < 0) idx = 0;
            else if (idx >= Count) idx = Count - 1;
            return Values[idx];
        }

        public static double GetFromDouble(double v) => Get((int)Math.Round(v));
    }
}
