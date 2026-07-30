using System;

namespace KotonPluginFmSynth
{
    /// <summary>
    /// Formes d'onde disponibles pour chaque opérateur (modulator + carrier) du synthé FM. Palette
    /// inspirée de la puce Yamaha YM3812 (OPL2 / AdLib) qui offrait 4 formes (sinus, demi-sinus,
    /// valeur absolue, pulse) — on y ajoute triangle et sawtooth pour couvrir les demandes basiques
    /// d'un synthé "chip 8-bit".
    ///
    /// **Convention de stockage** : la sélection est stockée dans un <see cref="KotonStudio.Library.KotonParameter"/>
    /// de plage 0..N-1 en tant que double, puis cast en int → enum au moment du Render. Cela évite
    /// d'introduire une API "paramètre discret" dans <see cref="KotonStudio.Library.KotonParameter"/>
    /// tout en gardant la sauvegarde JSON en un simple nombre.
    ///
    /// **Coût CPU** : chaque forme est un switch sur 6 cas → négligeable vs le cos/sin lui-même.
    /// L'oscillator est appelé 2 fois par sample par voix (modulator + carrier), ordre de grandeur ~200 k
    /// évaluations/sec sur 8 voix polyphoniques → invisible sur un CPU moderne.
    /// </summary>
    public enum FmWaveform
    {
        /// <summary>Sinus pur — la forme "FM naturelle" (comme le DX7 / OPL en mode 0).</summary>
        Sine = 0,

        /// <summary>Carré ±1 — riche en harmoniques impaires, son "chip 8-bit" typique.</summary>
        Square = 1,

        /// <summary>Triangle linéaire — moins harmonique que le carré, entre sinus et sawtooth.</summary>
        Triangle = 2,

        /// <summary>Sawtooth (dent-de-scie) descendant — riche en toutes harmoniques, son "brass/pad".</summary>
        Sawtooth = 3,

        /// <summary>Demi-sinus : partie positive du sinus, zéro pour la partie négative (OPL waveform 1).</summary>
        HalfSine = 4,

        /// <summary>Valeur absolue du sinus (OPL waveform 2) — plein d'harmoniques paires, son "cloche".</summary>
        AbsSine = 5,
    }

    /// <summary>
    /// Générateur unique de formes d'onde. Prend une phase en radians (potentiellement hors [0, 2π))
    /// puisqu'en FM la phase du carrier reçoit une modulation qui peut la sortir de sa plage — la
    /// méthode normalise en interne. Retourne un signal dans [-1, +1].
    /// </summary>
    internal static class Osc
    {
        const double TwoPi = 2 * Math.PI;

        public static float Sample(FmWaveform w, double phase)
        {
            // Normalise phase dans [0, 2π). Le carrier peut recevoir _phaseC + index*mod, ce qui sort
            // largement de la plage — modulo indispensable.
            phase = phase % TwoPi;
            if (phase < 0) phase += TwoPi;

            switch (w)
            {
                case FmWaveform.Sine:
                    return (float)Math.Sin(phase);

                case FmWaveform.Square:
                    return phase < Math.PI ? 1f : -1f;

                case FmWaveform.Triangle:
                {
                    // Rampe -1 → +1 sur [0, π), puis +1 → -1 sur [π, 2π).
                    double p = phase / Math.PI;
                    return (float)(p < 1.0 ? (2.0 * p - 1.0) : (3.0 - 2.0 * p));
                }

                case FmWaveform.Sawtooth:
                    // -1 en 0, +1 juste avant 2π, discontinuité à la fin. Descend en tension = -sawtooth
                    // mathématique mais l'oreille ne fait pas la différence sans référence.
                    return (float)((phase / Math.PI) - 1.0);

                case FmWaveform.HalfSine:
                    // Sinus positif clampé à zéro sur la moitié négative. Riche en harmoniques paires.
                    return (float)Math.Max(0.0, Math.Sin(phase));

                case FmWaveform.AbsSine:
                    // |sin(phase)| — double la fréquence perçue, très "cloche" en FM.
                    return (float)Math.Abs(Math.Sin(phase));

                default:
                    return 0f;
            }
        }

        /// <summary>Nombre total de formes définies (0..Count-1 pour un KotonParameter borné).</summary>
        public const int Count = 6;

        /// <summary>Noms affichables pour l'éditeur (ComboBox). Ordre = valeur enum.</summary>
        public static readonly string[] Names =
        {
            "Sine",
            "Square",
            "Triangle",
            "Sawtooth",
            "Half Sine",
            "Abs Sine",
        };

        /// <summary>Cast d'un double (valeur de KotonParameter) vers l'enum, clampé et arrondi.</summary>
        public static FmWaveform FromDouble(double v)
        {
            int i = (int)Math.Round(v);
            if (i < 0) i = 0;
            else if (i >= Count) i = Count - 1;
            return (FmWaveform)i;
        }
    }
}
