using System;

namespace KotonPluginWaveMorph
{
    /// <summary>
    /// Forme d'onde du LFO. Ordre stable pour la persistance JSON (index stocké dans un
    /// KotonParameter 0..N-1). S&amp;H et Random sont générés en interne (pas dans WaveOsc, qui ne
    /// connaît que les formes déterministes basées sur la phase).
    /// </summary>
    public enum LfoShape
    {
        Sine = 0,
        Triangle = 1,
        Saw = 2,
        Square = 3,
        SampleAndHold = 4,
        Random = 5,
    }

    /// <summary>
    /// Oscillateur basse fréquence — moduleur pour la mod matrix. Par voix (une instance par voice)
    /// pour que le NoteOn puisse remettre à zéro la phase quand on veut un LFO synchronisé au trigger
    /// (comportement typique des synths modernes ; un free-run LFO global peut être ajouté plus tard).
    ///
    /// **Sample &amp; Hold** : à chaque cycle de la période choisie, tire un nombre aléatoire et le
    /// garde stable jusqu'au cycle suivant. Idéal pour un effet "bit-crush aléatoire" ou de la
    /// modulation stochastique douce.
    ///
    /// **Random** : bruit blanc filtré (lerp entre valeurs aléatoires successives selon la rate) —
    /// plus doux que S&amp;H, produit un vibrato "vivant".
    /// </summary>
    internal sealed class Lfo
    {
        readonly int _sampleRate;
        readonly Random _rng;
        double _phase;   // 0..1 (pas radians — plus simple pour les formes non-sinus)
        float _shValue;  // valeur courante en mode S&H
        float _rndPrev;  // valeur "ancienne" pour lerp en mode Random
        float _rndNext;  // valeur "cible" pour lerp en mode Random

        public Lfo(int sampleRate, int seed = 12345)
        {
            _sampleRate = sampleRate > 0 ? sampleRate : 44100;
            _rng = new Random(seed);
            _shValue = 0f;
            _rndPrev = 0f;
            _rndNext = (float)(_rng.NextDouble() * 2 - 1);
        }

        /// <summary>Remet la phase à zéro et re-tire les valeurs aléatoires. Appelé sur NoteOn pour
        /// que le LFO démarre "aligné" avec l'attaque de la note.</summary>
        public void Retrigger()
        {
            _phase = 0;
            _shValue = (float)(_rng.NextDouble() * 2 - 1);
            _rndPrev = 0f;
            _rndNext = (float)(_rng.NextDouble() * 2 - 1);
        }

        /// <summary>Avance le LFO d'un sample et retourne sa valeur courante dans [-1, +1].</summary>
        public float Advance(LfoShape shape, float rateHz)
        {
            if (rateHz < 0.001f) rateHz = 0.001f;
            double phaseInc = rateHz / _sampleRate;
            double prevPhase = _phase;
            _phase += phaseInc;
            bool wrapped = false;
            while (_phase >= 1.0) { _phase -= 1.0; wrapped = true; }

            switch (shape)
            {
                case LfoShape.Sine:
                    return (float)Math.Sin(_phase * 2 * Math.PI);

                case LfoShape.Triangle:
                    // -1 → +1 sur [0, 0.5], +1 → -1 sur [0.5, 1]
                    return _phase < 0.5 ? (float)(_phase * 4 - 1) : (float)(3 - _phase * 4);

                case LfoShape.Saw:
                    // -1 → +1 rampe linéaire sur toute la période
                    return (float)(_phase * 2 - 1);

                case LfoShape.Square:
                    return _phase < 0.5 ? 1f : -1f;

                case LfoShape.SampleAndHold:
                    // Tirer une nouvelle valeur à chaque wrap.
                    if (wrapped) _shValue = (float)(_rng.NextDouble() * 2 - 1);
                    return _shValue;

                case LfoShape.Random:
                    // Idem S&H mais on lerp entre la valeur précédente et la nouvelle pour éviter
                    // les sauts abrupts — donne un mouvement "smooth-random".
                    if (wrapped) { _rndPrev = _rndNext; _rndNext = (float)(_rng.NextDouble() * 2 - 1); }
                    return _rndPrev + (_rndNext - _rndPrev) * (float)_phase;

                default:
                    return 0f;
            }
        }

        public static LfoShape ShapeFromDouble(double v)
        {
            int i = (int)Math.Round(v);
            if (i < 0) i = 0;
            else if (i > 5) i = 5;
            return (LfoShape)i;
        }

        public static readonly string[] ShapeNames =
        {
            "Sine", "Triangle", "Saw", "Square", "S&H", "Random",
        };
    }
}
