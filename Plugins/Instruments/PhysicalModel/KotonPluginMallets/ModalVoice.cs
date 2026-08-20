using System;

namespace KotonPluginMallets
{
    /// <summary>
    /// Une voix de synthèse modale — l'implémentation directe de la somme de sinusoïdes amorties
    /// qui définit le timbre d'un objet solide vibrant frappé (bar de xylo, cloche, kalimba…).
    ///
    /// **Algorithme par sample** :
    /// <code>
    ///   sum = 0
    ///   pour chaque mode i :
    ///     sum += amp[i] * sin(phase[i])
    ///     phase[i] += phaseInc[i]
    ///     amp[i] *= decayFactor[i]
    ///   return sum
    /// </code>
    ///
    /// C'est du **sinus additif amorti** : chaque mode est une sinusoïde pure (freq = f0 × ratio)
    /// dont l'amplitude décroît exponentiellement au taux fixé par DecayMs. Le résultat est un
    /// mélange qui, avec les bons ratios, sonne exactement comme une barre de marimba (ou une
    /// cloche, un vibraphone, une kalimba…).
    ///
    /// **Amortissement** : <c>decayFactor = exp(-6.91 / (DecayMs × SR / 1000))</c>.
    /// Justification : 6.91 = ln(1000) → l'amp tombe à 1/1000 (soit -60 dB) au bout de DecayMs.
    /// C'est la convention audio standard pour "temps de décroissance".
    ///
    /// **Pas d'oscillateur wave-table** : on utilise <see cref="Math.Sin"/> direct par sample. Coût
    /// négligeable en managed sur PC moderne (~50 sin/sample × 16 voix ≈ 40 M sin/s, tenu large sans
    /// SIMD). Une table sinusoïdale accélérerait mais introduirait des artefacts d'aliasing à cause
    /// des ratios inharmoniques (7.5x etc.) qui ne s'alignent pas sur une table entière.
    ///
    /// **Position** module l'amplitude initiale de chaque mode : centre = fondamentale forte,
    /// bord = sommation où les harmoniques dominent (comme frapper le bord d'une bar de marimba).
    /// L'implémentation : atténuation cosine du mode i selon sa "distance" à la fondamentale.
    ///
    /// **Mallet hardness** (dur = plastique, mou = feutre) module également : dur = préserve les
    /// aigus (le mode k a son amp × ratio_k^0.5), mou = filtre (amp × ratio_k^-0.5).
    /// </summary>
    internal sealed class ModalVoice
    {
        readonly int _sampleRate;

        // Modes actifs — jusqu'à 16 (au cas où un preset ambitieux en demande beaucoup)
        const int MaxModes = 16;
        readonly double[] _phase = new double[MaxModes];
        readonly double[] _phaseInc = new double[MaxModes];
        readonly float[] _amp = new float[MaxModes];
        readonly float[] _decayFactor = new float[MaxModes];
        int _numModes;

        bool _active;
        int _note;
        float _velocity;

        // Seuil sous lequel la voix est considérée éteinte — le max des _amp doit être < ce seuil
        // pour que la voix libère son slot.
        const float SilenceThreshold = 1e-4f;

        public bool IsActive => _active;
        public int Note => _note;
        public float Velocity => _velocity;

        public ModalVoice(int sampleRate)
        {
            _sampleRate = sampleRate;
        }

        /// <summary>Frappe la barre. Configure les N modes selon le preset, la vélocité, la dureté
        /// du mallet et la position de frappe.</summary>
        public void NoteOn(int note, float velocity, Mode[] modes, float malletHardness, float position, float damping, float brightness)
        {
            _note = note;
            _velocity = velocity;
            _numModes = Math.Min(MaxModes, modes.Length);

            double freq = 440.0 * Math.Pow(2.0, (note - 69) / 12.0);

            // Facteur d'amortissement supplémentaire piloté par Brightness : brightness haut = préserve
            // les hautes fréquences (les modes aigus décroissent plus lentement) ; brightness bas =
            // filtre les aigus (les modes aigus décroissent plus vite). Ce n'est pas un LP au sens
            // classique mais un contrôle direct sur le decay par mode — plus élégant pour du modal.
            // Range effectif : brightness 0.0 → mode k a decay divisé par (1 + ratio_k/2),
            //                  brightness 1.0 → decay identique pour tous les modes (aigus préservés).
            float darkness = 1f - brightness;

            for (int i = 0; i < _numModes; i++)
            {
                var m = modes[i];
                double f = freq * m.Ratio;
                // Éviter les fréquences au-dessus de Nyquist — sinon aliasing.
                if (f > _sampleRate * 0.45) { _amp[i] = 0f; _phaseInc[i] = 0; _decayFactor[i] = 0.5f; continue; }
                _phase[i] = 0;
                _phaseInc[i] = 2.0 * Math.PI * f / _sampleRate;

                // Amplitude initiale : base = m.Amp × velocity
                float ampBase = m.Amp * velocity;

                // Mallet hardness : dur = accentue les aigus proportionnellement (^0.5 par ratio),
                // mou = atténue (^-0.5). Le ratio 1 est inchangé, seuls les modes aigus varient.
                float hardnessGain = (float)Math.Pow(m.Ratio, (malletHardness - 0.5) * 0.6);

                // Position : 0.0 = centre = tous les modes plein volume ; 1.0 = bord = fondamentale
                // atténuée, aigus dominants. Modélisé simplement : cos(π × position × 1/ratio_norm).
                // Effet : au bord d'une bar, le premier mode (mode 0) est fortement diminué, les
                // suivants moins — comme dans la vraie physique.
                float positionGain;
                if (i == 0) positionGain = 1f - position * 0.7f;   // fondamentale : diminue jusqu'à 30%
                else positionGain = 1f + position * 0.4f;           // aigus : boost léger jusqu'à +40%

                _amp[i] = ampBase * hardnessGain * positionGain;

                // Facteur de décroissance : amp *= factor à chaque sample, tel que après (DecayMs × SR/1000)
                // samples on ait amp × (1/1000) = -60 dB.
                float effectiveDecayMs = m.DecayMs * Math.Max(0.01f, damping) / (1f + m.Ratio * darkness * 0.5f);
                double samples = effectiveDecayMs * _sampleRate / 1000.0;
                if (samples < 1) samples = 1;
                _decayFactor[i] = (float)Math.Exp(-6.907755278982137 / samples);   // ln(1000) = 6.907...
            }

            _active = true;
        }

        /// <summary>Coupe la voix immédiatement (reset global de l'instrument).</summary>
        public void Kill()
        {
            _active = false;
            _numModes = 0;
            for (int i = 0; i < MaxModes; i++) _amp[i] = 0f;
        }

        /// <summary>Produit un sample mono. La libération de voix se fait quand le mode le plus fort
        /// tombe sous le seuil de silence.</summary>
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

            // Soft-clip par tanh atténué : sur une frappe forte les modes s'additionnent en phase
            // à t=0 et peuvent dépasser ±1.0. Le tanh borne naturellement à ±1, le facteur 0.7
            // préserve la dynamique dans la zone utile.
            return (float)Math.Tanh(sum * 0.7);
        }
    }
}
