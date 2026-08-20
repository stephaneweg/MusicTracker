using System;

namespace KotonPluginWaveMorph
{
    /// <summary>
    /// Type de filtre biquad — LP/HP/BP/Notch. Notation entière parce que stockée dans un
    /// KotonParameter (double 0..3). L'ordre correspond aux 4 boutons de l'éditeur.
    /// </summary>
    public enum FilterType
    {
        LowPass = 0,
        HighPass = 1,
        BandPass = 2,
        Notch = 3,
    }

    /// <summary>
    /// Filtre biquad "RBJ audio EQ cookbook" — Direct Form I, coefficients recalculés à chaque
    /// changement de paramètre (cutoff, Q, type, sample rate). Pour la pente 24 dB/oct, l'éditeur
    /// utilise DEUX Filter chaînés en série (chaque biquad = 12 dB/oct) — la classe elle-même reste
    /// mono-biquad, c'est le voice qui décide combien d'étages instancier.
    ///
    /// **Drive** : le drive est appliqué EN ENTRÉE par un simple gain + tanh soft-clip. Ça saturera
    /// la boucle de résonance et donnera au filtre un caractère "analogue-ish" à haut gain. Pas de
    /// compensation d'output — un utilisateur qui pousse le drive baisse le volume ailleurs.
    ///
    /// **Mix** : cross-fade entrée/sortie du filtre (mix=0 → entrée pure, mix=1 → sortie filtrée pure).
    /// Permet d'utiliser un filtre à faible taux pour ajouter juste une couleur sans écraser le signal.
    ///
    /// **Stabilité** : les coefs sont clampés (Q >= 0.1, cutoff dans [10, sampleRate/2.2] Hz) pour
    /// éviter les biquads instables. Un utilisateur qui pousse cutoff au-delà de Nyquist entend un
    /// comportement dégradé mais pas une explosion.
    /// </summary>
    internal sealed class BiquadFilter
    {
        readonly int _sampleRate;

        // Coefs RBJ normalisés.
        double _b0, _b1, _b2, _a1, _a2;

        // État Direct Form I (2 échantillons en entrée + 2 en sortie).
        double _x1, _x2, _y1, _y2;

        // Cache des paramètres qui ont servi à calculer les coefs, pour éviter un recompute par sample.
        FilterType _cachedType = (FilterType)(-1);
        double _cachedFreq = -1;
        double _cachedQ = -1;

        public BiquadFilter(int sampleRate)
        {
            _sampleRate = sampleRate > 0 ? sampleRate : 44100;
            // Coefs neutres au départ (identity), pour qu'un filtre non-initialisé passe tout.
            _b0 = 1; _b1 = 0; _b2 = 0; _a1 = 0; _a2 = 0;
        }

        public void Reset()
        {
            _x1 = _x2 = _y1 = _y2 = 0;
        }

        /// <summary>Recalcule les coefs si un des trois paramètres a changé. Coût = 1 sin + 1 cos +
        /// une poignée de divisions. Fait 1 fois par buffer (pas par sample) via le check de cache.</summary>
        public void UpdateCoefs(FilterType type, double freq, double q)
        {
            if (freq < 10) freq = 10;
            double nyq = _sampleRate * 0.5;
            double maxF = nyq / 1.1;  // marge de sécurité vs Nyquist (les biquads deviennent bizarres au bord)
            if (freq > maxF) freq = maxF;
            if (q < 0.1) q = 0.1;
            if (q > 20) q = 20;

            if (type == _cachedType && freq == _cachedFreq && q == _cachedQ) return;
            _cachedType = type; _cachedFreq = freq; _cachedQ = q;

            double w0 = 2.0 * Math.PI * freq / _sampleRate;
            double cosw0 = Math.Cos(w0);
            double sinw0 = Math.Sin(w0);
            double alpha = sinw0 / (2.0 * q);

            double b0, b1, b2, a0, a1, a2;

            switch (type)
            {
                case FilterType.LowPass:
                    b0 = (1 - cosw0) * 0.5;
                    b1 = 1 - cosw0;
                    b2 = (1 - cosw0) * 0.5;
                    a0 = 1 + alpha;
                    a1 = -2 * cosw0;
                    a2 = 1 - alpha;
                    break;

                case FilterType.HighPass:
                    b0 = (1 + cosw0) * 0.5;
                    b1 = -(1 + cosw0);
                    b2 = (1 + cosw0) * 0.5;
                    a0 = 1 + alpha;
                    a1 = -2 * cosw0;
                    a2 = 1 - alpha;
                    break;

                case FilterType.BandPass:
                    // Version "constant peak gain" (BPF) de la cookbook — pic à 0 dB.
                    b0 = alpha;
                    b1 = 0;
                    b2 = -alpha;
                    a0 = 1 + alpha;
                    a1 = -2 * cosw0;
                    a2 = 1 - alpha;
                    break;

                case FilterType.Notch:
                    b0 = 1;
                    b1 = -2 * cosw0;
                    b2 = 1;
                    a0 = 1 + alpha;
                    a1 = -2 * cosw0;
                    a2 = 1 - alpha;
                    break;

                default:
                    // Neutralité si un type inconnu débarque via automation.
                    b0 = 1; b1 = 0; b2 = 0; a0 = 1; a1 = 0; a2 = 0;
                    break;
            }

            // Normalisation par a0 pour la forme "canonique" utilisée par Process.
            _b0 = b0 / a0;
            _b1 = b1 / a0;
            _b2 = b2 / a0;
            _a1 = a1 / a0;
            _a2 = a2 / a0;
        }

        /// <summary>Traite un sample via l'équation Direct Form I biquad. Le drive et le mix ne sont
        /// PAS appliqués ici (laissés au voice pour cohérence quand deux Filter sont chaînés — le
        /// drive/mix ne s'appliquent qu'à l'entrée/sortie de la chaîne complète, pas à chaque étage).</summary>
        public float Process(float x)
        {
            double xIn = x;
            double y = _b0 * xIn + _b1 * _x1 + _b2 * _x2 - _a1 * _y1 - _a2 * _y2;
            _x2 = _x1; _x1 = xIn;
            _y2 = _y1; _y1 = y;

            // Denormals : le biquad peut accumuler des valeurs sub-normales qui explosent le CPU.
            // Un flush-to-zero simple par comparaison à un epsilon évite le drame.
            if (y > -1e-20 && y < 1e-20) y = 0;
            return (float)y;
        }
    }
}
