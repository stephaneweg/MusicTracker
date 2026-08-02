using System;

namespace KotonPluginHandpan
{
    /// <summary>Un mode résonant d'un handpan : ratio de fréquence + décay + amplitude actuelle
    /// (mutable — la sympathie peut ré-injecter de l'énergie dedans).</summary>
    internal struct HpMode
    {
        public float Ratio;      // ratio de fréquence par rapport à la fondamentale
        public float DecayMs;    // temps de décroissance -60 dB
    }

    internal struct HandpanParams
    {
        public float MalletHardness;   // 0..1 (dur → main ferme, mou → main de coton)
        public float Resonance;        // 0..1 → multiplicateur global des DecayMs
        public float Brightness;
        public float Sympathy;         // 0..1 → force de la résonance sympathique inter-notes
        public float ShellMix;         // 0..1 → résonance du corps du bol (biquad bandpass ~120 Hz)
        public float StereoSpread;
        public float VolumeDb;
    }

    /// <summary>
    /// Une voix de handpan = synthèse modale avec sympathie. Chaque note = somme de 5-6 sinusoïdes
    /// amorties (comme Mallets) MAIS l'amplitude de chaque mode peut être re-boostée par un
    /// mécanisme de sympathie inter-voix : quand une nouvelle note est frappée, elle "excite" les
    /// modes des voix déjà actives dont la fréquence est un HARMONIQUE proche (ratio 1:2, 2:3, 3:4,
    /// 1:3, 4:5), avec une intensité proportionnelle au paramètre Sympathy.
    ///
    /// **Physique** : un vrai handpan est un bol de métal contigu — quand tu tapes une note, les
    /// autres notes adjacentes vibrent aussi légèrement par couplage acoustique/mécanique. C'est ce
    /// qui donne le caractère "cathédrale de métal en méditation" typique du handpan.
    ///
    /// **Modes typiques** (mesurés sur un vrai Halo pan) :
    ///   Ratio 1.00 (fondamentale), 2.00 (octave — mode toké), 3.00 (compound tone),
    ///   4.00 (2e octave), 5.05 (harmonique légèrement inharmonique)
    /// Le compound tone (ratio ~3) est ce qui distingue le handpan d'une simple triade — il ajoute
    /// une note "fantôme" à la douzième qui donne la profondeur unique.
    /// </summary>
    internal sealed class HandpanVoice
    {
        readonly int _sr;

        const int MaxModes = 8;
        readonly double[] _phase = new double[MaxModes];
        readonly double[] _phaseInc = new double[MaxModes];
        readonly float[] _amp = new float[MaxModes];
        readonly float[] _decayFactor = new float[MaxModes];
        int _numModes;

        bool _active;
        int _note;
        float _velocity;
        float _pan;

        const float SilenceThreshold = 1e-4f;

        public bool IsActive => _active;
        public int Note => _note;
        public float PanL { get; private set; }
        public float PanR { get; private set; }

        // Fréquences des modes actuellement en jeu — utilisées par le plugin pour évaluer la
        // sympathie avec les nouvelles notes.
        public double[] ModeFreqs => _modeFreqs;
        readonly double[] _modeFreqs = new double[MaxModes];

        public HandpanVoice(int sampleRate)
        {
            _sr = sampleRate;
        }

        public void NoteOn(int note, float velocity, HpMode[] modes, in HandpanParams p, float pan)
        {
            _note = note;
            _velocity = velocity;
            _pan = pan;
            _numModes = Math.Min(MaxModes, modes.Length);

            double freq = 440.0 * Math.Pow(2.0, (note - 69) / 12.0);

            // Pan L/R
            float p01 = 0.5f * (1f + pan);
            PanL = 1f - p01;
            PanR = p01;

            // Compensation brightness sur les decays des modes aigus
            float darkness = 1f - p.Brightness;

            for (int i = 0; i < _numModes; i++)
            {
                var m = modes[i];
                double f = freq * m.Ratio;
                _modeFreqs[i] = f;
                if (f > _sr * 0.45)
                {
                    _amp[i] = 0f;
                    _phaseInc[i] = 0;
                    _decayFactor[i] = 0.5f;
                    continue;
                }
                _phase[i] = 0;
                _phaseInc[i] = 2.0 * Math.PI * f / _sr;

                // Amplitude initiale : proportionnelle à velocity, atténuée pour les modes aigus
                // selon MalletHardness (main dure = préserve les aigus, main molle = filtre)
                float ampBase = velocity / (1f + i * 0.6f);   // les modes 2, 3, 4... plus faibles
                float hardnessGain = (float)Math.Pow(m.Ratio, (p.MalletHardness - 0.5) * 0.5);
                _amp[i] = ampBase * hardnessGain;

                // Decay allongé par Resonance (0..1 → ×1..×3)
                float effectiveDecayMs = m.DecayMs * (1f + p.Resonance * 2f) / (1f + m.Ratio * darkness * 0.4f);
                double samples = effectiveDecayMs * _sr / 1000.0;
                if (samples < 1) samples = 1;
                _decayFactor[i] = (float)Math.Exp(-6.907755278982137 / samples);
            }

            _active = true;
        }

        /// <summary>Sympathie : re-injecte une petite quantité d'énergie dans chaque mode dont la
        /// fréquence est proche (à moins de ~2%) d'une des fréquences <paramref name="triggerFreqs"/>.
        /// Appelé par le plugin quand une NOUVELLE note est frappée. L'intensité <paramref name="strength"/>
        /// = Sympathy × velocity de la nouvelle note.</summary>
        public void ApplySympathy(double[] triggerFreqs, int triggerCount, float strength)
        {
            for (int i = 0; i < _numModes; i++)
            {
                if (_amp[i] < 1e-5f) continue;   // mode déjà mort, pas de sympathie
                double f = _modeFreqs[i];
                if (f <= 0) continue;
                for (int t = 0; t < triggerCount; t++)
                {
                    double tf = triggerFreqs[t];
                    // Ratio proche de 1 = fréquences très proches
                    double r = f > tf ? f / tf : tf / f;
                    // Chercher un ratio harmonique proche : 1:1 (unisson), 1:2 (octave), 2:3 (quinte),
                    // 3:4 (quarte), 1:3 (12ème). On teste chaque ratio dans une petite fenêtre.
                    double[] harmonicRatios = { 1.0, 2.0, 3.0, 4.0, 1.5, 4.0 / 3.0, 5.0 / 3.0 };
                    for (int h = 0; h < harmonicRatios.Length; h++)
                    {
                        double target = harmonicRatios[h];
                        if (Math.Abs(r - target) < 0.02)
                        {
                            // Boost proportionnel à l'intensité et inversement proportionnel à
                            // l'écart harmonique (plus l'harmonie est complexe, plus faible le boost)
                            float coupling = 1f / (1f + h * 0.5f);
                            _amp[i] += strength * coupling * 0.15f;
                            if (_amp[i] > 1.5f) _amp[i] = 1.5f;   // éviter le clipping
                            break;
                        }
                    }
                }
            }
        }

        public void Kill()
        {
            _active = false;
            _numModes = 0;
            for (int i = 0; i < MaxModes; i++) _amp[i] = 0f;
        }

        public float RenderSample()
        {
            if (!_active) return 0f;

            float sum = 0f;
            float maxAmp = 0f;
            for (int i = 0; i < _numModes; i++)
            {
                sum += _amp[i] * (float)Math.Sin(_phase[i]);
                _phase[i] += _phaseInc[i];
                if (_phase[i] > 2 * Math.PI) _phase[i] -= 2 * Math.PI;
                _amp[i] *= _decayFactor[i];
                if (_amp[i] > maxAmp) maxAmp = _amp[i];
            }

            if (maxAmp < SilenceThreshold)
            {
                _active = false;
                return 0f;
            }

            return (float)Math.Tanh(sum * 0.5);
        }
    }
}
