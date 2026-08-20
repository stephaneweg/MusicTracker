using System;

namespace KotonPluginFractal
{
    /// <summary>Contrat commun à toutes les sources fractales : réinitialisation avec seed +
    /// paramètres spécifiques, puis production itérative d'un scalaire normalisé dans [0,1].
    /// Le générateur amont mappe ce scalaire en note MIDI selon le mode de snap (chord/scale/chroma).
    ///
    /// **Deterministic** : Reset avec le même seed + mêmes paramètres = mêmes valeurs. Indispensable
    /// pour que la Preview et le rendu final donnent la même séquence.</summary>
    internal interface IFractalSource
    {
        double Next();
    }

    // =================================================================================================
    // Voss-Clarke 1/f — LA fractale musicale par excellence. Publié 1975 par Voss & Clarke (Bell Labs)
    // après avoir montré que musique naturelle, taux de crue de rivières, battements cardiaques ont un
    // spectre en 1/f. Musicalement : entre bruit blanc (trop aléatoire) et brownien (trop lisse).
    //
    // Algo dice-based (Martin Gardner, Sci. Am. 1978) : N dés à taux de rafraîchissement différents
    // (dé 0 = chaque note, dé 1 = tous les 2, dé 2 = tous les 4, dé k = tous les 2^k). Somme = pitch.
    // Plus il y a de dés, plus le spectre approche 1/f exact.
    // =================================================================================================
    internal sealed class VossSource : IFractalSource
    {
        readonly int _octaves;
        readonly int[] _dice;
        readonly Random _rng;
        int _sample;

        public VossSource(int octaves, int seed)
        {
            _octaves = Math.Max(2, Math.Min(12, octaves));
            _dice = new int[_octaves];
            _rng = new Random(seed);
            for (int i = 0; i < _octaves; i++) _dice[i] = _rng.Next(1000);
            _sample = 0;
        }

        public double Next()
        {
            // Chaque dé k change tous les 2^k samples. Le dé 0 change à chaque sample (bruit blanc
            // rapide) ; le dé k=8 change tous les 256 samples (dérive lente). La somme donne le 1/f.
            for (int k = 0; k < _octaves; k++)
            {
                int period = 1 << k;
                if ((_sample % period) == 0) _dice[k] = _rng.Next(1000);
            }
            _sample++;
            int sum = 0;
            for (int k = 0; k < _octaves; k++) sum += _dice[k];
            return sum / (1000.0 * _octaves);
        }
    }

    // =================================================================================================
    // Logistic map — Robert May 1976, un des exemples les plus simples de chaos déterministe :
    //   x_{n+1} = r * x_n * (1 - x_n)   avec x, r ∈ [0,1] et r ∈ [0,4]
    // - r < 3 : converge vers un point fixe
    // - 3 < r < 3.45 : oscillation 2 périodes
    // - 3.57 < r < 4 : chaos (avec fenêtres d'ordre)
    // - r = 3.6786... : bifurcation classique du triple = début du chaos "pur"
    // Musicalement : à r=3.7-3.9 produit une séquence à la fois inattendue et structurée.
    // =================================================================================================
    internal sealed class LogisticSource : IFractalSource
    {
        double _x;
        readonly double _r;

        public LogisticSource(double r, int seed)
        {
            _r = Math.Max(2.5, Math.Min(4.0, r));
            // Éviter 0 et 1 (points fixes triviaux) : borner la seed dans [0.1, 0.9].
            var rng = new Random(seed);
            _x = 0.1 + rng.NextDouble() * 0.8;
        }

        public double Next()
        {
            _x = _r * _x * (1.0 - _x);
            if (_x < 0.0) _x = 0.0;
            else if (_x > 1.0) _x = 1.0;
            return _x;
        }
    }

    // =================================================================================================
    // Lorenz attractor — Edward Lorenz 1963, l'attracteur étrange le plus iconique (papillon).
    //   dx/dt = σ (y - x)
    //   dy/dt = x (ρ - z) - y
    //   dz/dt = x y - β z
    // Paramètres canoniques : σ=10, ρ=28, β=8/3.
    // On intègre par Euler explicite (suffisant à cette échelle, pas besoin de RK4). Chaque axe (X/Y/Z)
    // donne une composante mappable — le user choisit laquelle drive la mélodie.
    // Range typique : X ∈ [-20, 20], Y ∈ [-25, 25], Z ∈ [0, 50].
    // =================================================================================================
    internal sealed class LorenzSource : IFractalSource
    {
        double _x, _y, _z;
        readonly double _sigma, _rho, _beta, _dt;
        readonly int _dim;

        public LorenzSource(double sigma, double rho, double beta, double dt, int dim, int seed)
        {
            _sigma = sigma;
            _rho = rho;
            _beta = beta;
            _dt = dt;
            _dim = Math.Max(0, Math.Min(2, dim));
            var rng = new Random(seed);
            _x = rng.NextDouble() * 20 - 10;
            _y = rng.NextDouble() * 20 - 10;
            _z = 5 + rng.NextDouble() * 20;
        }

        public double Next()
        {
            // Sub-stepping : on avance de plusieurs micro-pas pour rester stable au dt du user, l'attrait
            // du Lorenz étant sensible aux grands dt (Euler diverge). 10 sub-steps = compromis raisonnable.
            const int SubSteps = 10;
            double subDt = _dt / SubSteps;
            for (int i = 0; i < SubSteps; i++)
            {
                double dx = _sigma * (_y - _x);
                double dy = _x * (_rho - _z) - _y;
                double dz = _x * _y - _beta * _z;
                _x += dx * subDt;
                _y += dy * subDt;
                _z += dz * subDt;
            }
            double raw = _dim == 0 ? _x : (_dim == 1 ? _y : _z);
            // Normalisation vers [0,1] — bornes empiriques par axe. Un dépassement (attracteur qui
            // s'étend au-delà) est clampé, pas d'exception.
            double norm;
            if (_dim == 0) norm = (raw + 20.0) / 40.0;
            else if (_dim == 1) norm = (raw + 25.0) / 50.0;
            else norm = raw / 50.0;
            if (norm < 0.0) return 0.0;
            if (norm > 1.0) return 1.0;
            return norm;
        }
    }
}
