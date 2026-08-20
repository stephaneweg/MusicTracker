using System;

namespace KotonPluginCaveReverb
{
    internal struct CaveParams
    {
        public float Size;            // 0..1 → longueur des lignes (grotte petite → cathédrale)
        public float Decay;            // 0..1 → durée de la queue (5..30 s dans le cave)
        public float LowBoom;          // 0..1 → résonance sous-basse ~80 Hz (le "boom" du cave)
        public float Darkness;         // 0..1 → LP dans le feedback (les grottes absorbent les aigus)
        public float PreDelayMs;       // 0..300 ms (grande cave = pré-delay long)
        public float DripAmount;       // 0..1 → densité des drips métalliques aléatoires
        public float DripPitch;        // 0..1 → fréquence des drips (bas..haut)
        public float DripVolume;       // 0..2 → multiplicateur du volume de chaque drip
        public float StereoWidth;
        public float Mix;
        public float OutGainDb;
    }

    /// <summary>
    /// Cave Reverb — la "cathédrale de pierre humide" de Koton. Un pendant monumental d'Ocean :
    /// - Ocean : espace ouvert, brumeux, mouvement (Movement/Modulation)
    /// - Forest : espace fragmenté, court, texture organique (Rustle)
    /// - Cave : espace ENFERMÉ énorme, queue TRÈS LONGUE (10-30 s), résonance sous-basse
    ///   caractéristique (le "boom" qui persiste à ~80 Hz), avec des drips aléatoires optionnels
    ///   (les gouttes d'eau sur les parois métalliques)
    ///
    /// **Deux ingrédients uniques** :
    ///
    /// 1. **Low boom** : biquad peak à ~80 Hz avec gain +12 dB en parallèle du feedback normal
    ///    → simule la résonance sous-basse d'une cavité de pierre. C'est ce qui donne l'aspect
    ///    "monumental" que ni Ocean ni Forest n'ont.
    ///
    /// 2. **Drips generator** : événements aléatoires (Poisson process, densité pilotée par
    ///    DripAmount) qui déclenchent une petite goutte métallique (sinus court à fréquence pilotée
    ///    par DripPitch) injectée dans la reverb. La goutte tombe QUELQUE PART dans le space, on
    ///    l'entend résonner. Effet cinématique typique des scènes de grotte / caverne.
    ///
    /// **Usage typique** : dark ambient, sound design cinéma/jeu (donjon/temple/mine), méditation
    /// gothique, drone metal. Sonne particulièrement bien avec Waterdrops (les drips synchros
    /// avec les vraies gouttes) et Handpan (l'espace amplifie la résonance sympathique).
    /// </summary>
    internal sealed class CaveCore
    {
        readonly int _sr;

        // Pré-delay
        float[] _preL, _preR;
        int _preIdx;
        int _preMaxSamples;
        const int PreMaxMs = 300;

        // Diffusion : 4 all-pass
        AllPassStage[] _diffusion;

        // FDN 4x4 LONG (queue monumentale)
        float[][] _fdnLines;
        int[] _fdnIdx;
        int[] _fdnBaseSamples;
        int[] _fdnMaxSamples;

        // LP dans feedback (Darkness)
        float[] _lpFbState;

        // Low boom : peak filter ~80 Hz en série dans le feedback (résonance basse)
        BiquadState[] _boomBiquad = new BiquadState[4];

        // HP fixe en entrée (retire les subgraves qui accumulent)
        float _hpInStateL, _hpInStateR;

        // Drips generator
        Random _dripRng = new Random(1337);
        int _samplesUntilNextDrip;
        // Drips actifs : jusqu'à 8 drips simultanés (sinus amortis)
        const int MaxActiveDrips = 8;
        double[] _dripPhase = new double[MaxActiveDrips];
        double[] _dripPhaseInc = new double[MaxActiveDrips];
        float[] _dripAmp = new float[MaxActiveDrips];
        float[] _dripDecay = new float[MaxActiveDrips];
        float[] _dripPanL = new float[MaxActiveDrips];
        float[] _dripPanR = new float[MaxActiveDrips];

        // Matrice Hadamard 4x4
        static readonly float[,] Hadamard4x4 = new float[4, 4]
        {
            {  0.5f,  0.5f,  0.5f,  0.5f },
            {  0.5f, -0.5f,  0.5f, -0.5f },
            {  0.5f,  0.5f, -0.5f, -0.5f },
            {  0.5f, -0.5f, -0.5f,  0.5f },
        };

        public CaveCore(int sampleRate)
        {
            _sr = sampleRate;
            _preMaxSamples = PreMaxMs * sampleRate / 1000;
            _preL = new float[_preMaxSamples];
            _preR = new float[_preMaxSamples];

            // 4 all-pass diffusion
            var apLenMs = new float[] { 5.3f, 8.1f, 13.9f, 21.3f };
            _diffusion = new AllPassStage[apLenMs.Length];
            for (int i = 0; i < apLenMs.Length; i++)
                _diffusion[i] = new AllPassStage((int)(apLenMs[i] * sampleRate / 1000f), 0.7f);

            // FDN 4x4 avec lignes LONGUES : ~150..500 ms de base (queue monumentale)
            var baseMs = new float[] { 137.3f, 197.1f, 283.7f, 397.9f };
            _fdnLines = new float[4][];
            _fdnIdx = new int[4];
            _fdnBaseSamples = new int[4];
            _fdnMaxSamples = new int[4];
            _lpFbState = new float[4];
            for (int i = 0; i < 4; i++)
            {
                int maxSamples = (int)(baseMs[i] * 2f * sampleRate / 1000f);
                _fdnMaxSamples[i] = maxSamples;
                _fdnLines[i] = new float[maxSamples];
                _fdnBaseSamples[i] = (int)(baseMs[i] * sampleRate / 1000f);
                // Boom biquad : peak à 80 Hz avec Q=4 et gain (mis à jour à chaque buffer)
                SetBiquadPeak(ref _boomBiquad[i], sampleRate, 80f, 4f, 0f);
            }

            _samplesUntilNextDrip = sampleRate * 2;   // premier drip après 2s
        }

        public void Reset()
        {
            Array.Clear(_preL, 0, _preL.Length);
            Array.Clear(_preR, 0, _preR.Length);
            _preIdx = 0;
            foreach (var ap in _diffusion) ap.Reset();
            for (int i = 0; i < 4; i++)
            {
                Array.Clear(_fdnLines[i], 0, _fdnLines[i].Length);
                _fdnIdx[i] = 0;
                _lpFbState[i] = 0f;
                _boomBiquad[i].ResetState();
            }
            _hpInStateL = _hpInStateR = 0f;
            for (int i = 0; i < MaxActiveDrips; i++) _dripAmp[i] = 0f;
            _samplesUntilNextDrip = _sr * 2;
        }

        public void Process(Span<float> left, Span<float> right, in CaveParams p)
        {
            int n = left.Length;
            float mix = p.Mix;
            float outLin = (float)Math.Pow(10.0, p.OutGainDb / 20.0);
            float width = p.StereoWidth;

            // Feedback gain LONG : 0..1 → 0.75..0.98 (queue 5..30s selon Size)
            float feedback = 0.75f + p.Decay * 0.23f;

            int preSamples = (int)(p.PreDelayMs * _sr / 1000f);
            if (preSamples < 0) preSamples = 0;
            if (preSamples >= _preMaxSamples) preSamples = _preMaxSamples - 1;

            // LP feedback : Darkness 0..1 → cutoff 3000..300 Hz (plus sombre = filtre plus fort)
            float lpCutoff = 3000f - p.Darkness * 2700f;
            float alphaLpFb = 1f - (float)Math.Exp(-2.0 * Math.PI * lpCutoff / _sr);

            // HP en entrée fixe pour éviter accumulation subgraves
            float alphaHpIn = 1f - (float)Math.Exp(-2.0 * Math.PI * 40f / _sr);

            // Low boom : peak gain 0..12 dB selon LowBoom param
            float boomGainDb = p.LowBoom * 12f;
            for (int i = 0; i < 4; i++)
                SetBiquadPeak(ref _boomBiquad[i], _sr, 80f, 4f, boomGainDb);

            // Size global : multiplie les longueurs des lignes 0.5..1.8
            float sizeMul = 0.5f + p.Size * 1.3f;

            // Drips : probabilité par sample d'un nouveau drip
            // DripAmount 0 → jamais, 1 → environ un drip toutes les 300ms
            float dripHz = p.DripAmount * 3.3f;
            // Fréquence du drip : DripPitch 0..1 → 400..2500 Hz
            float dripFreqHz = 400f + p.DripPitch * 2100f;

            for (int i = 0; i < n; i++)
            {
                float inL = left[i];
                float inR = right[i];

                // HP en entrée
                _hpInStateL += alphaHpIn * (inL - _hpInStateL);
                _hpInStateR += alphaHpIn * (inR - _hpInStateR);
                float hpL = inL - _hpInStateL;
                float hpR = inR - _hpInStateR;

                // Pré-delay
                _preL[_preIdx] = hpL;
                _preR[_preIdx] = hpR;
                int preRead = _preIdx - preSamples;
                if (preRead < 0) preRead += _preMaxSamples;
                float pdL = _preL[preRead];
                float pdR = _preR[preRead];
                _preIdx++;
                if (_preIdx >= _preMaxSamples) _preIdx = 0;

                // Diffusion
                float diffL = pdL;
                float diffR = pdR;
                for (int a = 0; a < _diffusion.Length; a++)
                {
                    diffL = _diffusion[a].ProcessL(diffL);
                    diffR = _diffusion[a].ProcessR(diffR);
                }

                // Drips generation (Poisson-like)
                _samplesUntilNextDrip--;
                if (_samplesUntilNextDrip <= 0 && dripHz > 0.01f)
                {
                    // Trouve un slot libre
                    for (int d = 0; d < MaxActiveDrips; d++)
                    {
                        if (_dripAmp[d] < 1e-5f)
                        {
                            // Détune aléatoire ±30% (drips ne sonnent jamais pareil)
                            double detune = _dripRng.NextDouble() * 0.6 - 0.3;
                            double df = dripFreqHz * (1.0 + detune);
                            _dripPhase[d] = 0;
                            _dripPhaseInc[d] = 2 * Math.PI * df / _sr;
                            _dripAmp[d] = (0.3f + (float)_dripRng.NextDouble() * 0.4f) * p.DripVolume;
                            // Decay court : ~50-200 ms
                            double decayMs = 50 + _dripRng.NextDouble() * 150;
                            _dripDecay[d] = (float)Math.Exp(-6.9078 / (decayMs * _sr / 1000.0));
                            // Pan aléatoire
                            float dp = (float)(_dripRng.NextDouble() * 2 - 1);
                            float dp01 = 0.5f * (1f + dp);
                            _dripPanL[d] = 1f - dp01;
                            _dripPanR[d] = dp01;
                            break;
                        }
                    }
                    // Prochain drip : distribution exponentielle inverse
                    double avgSamples = _sr / dripHz;
                    _samplesUntilNextDrip = (int)(-Math.Log(1.0 - _dripRng.NextDouble()) * avgSamples);
                    if (_samplesUntilNextDrip < 100) _samplesUntilNextDrip = 100;
                }

                float dripSumL = 0f, dripSumR = 0f;
                for (int d = 0; d < MaxActiveDrips; d++)
                {
                    if (_dripAmp[d] < 1e-5f) continue;
                    float dripSample = (float)Math.Sin(_dripPhase[d]) * _dripAmp[d];
                    _dripPhase[d] += _dripPhaseInc[d];
                    if (_dripPhase[d] > 2 * Math.PI) _dripPhase[d] -= 2 * Math.PI;
                    _dripAmp[d] *= _dripDecay[d];
                    dripSumL += dripSample * _dripPanL[d];
                    dripSumR += dripSample * _dripPanR[d];
                }

                // Injection FDN : diffusion + drips (les drips sont réinjectés dans la reverb pour
                // résonner comme dans une vraie grotte)
                float in0 = diffL + dripSumL * 0.5f;
                float in1 = diffR + dripSumR * 0.5f;
                float in2 = -diffL - dripSumL * 0.5f;
                float in3 = -diffR - dripSumR * 0.5f;

                // Lecture FDN
                float[] taps = new float[4];
                for (int line = 0; line < 4; line++)
                {
                    int len = (int)(_fdnBaseSamples[line] * sizeMul);
                    if (len < 4) len = 4;
                    if (len >= _fdnMaxSamples[line]) len = _fdnMaxSamples[line] - 1;
                    int readIdx = _fdnIdx[line] - len;
                    while (readIdx < 0) readIdx += _fdnMaxSamples[line];
                    taps[line] = _fdnLines[line][readIdx];
                }

                // LP feedback (Darkness) SEULEMENT — le boom est desormais applique en SORTIE
                // (post-FDN, pas dans la boucle) pour eviter le larsen a 80Hz : un peak filter
                // avec +12dB dans une boucle de feedback accumule de l'energie a la resonance
                // et fait exploser la reverb apres quelques secondes (bug rapporte 2026-08-02).
                for (int line = 0; line < 4; line++)
                {
                    _lpFbState[line] += alphaLpFb * (taps[line] - _lpFbState[line]);
                    taps[line] = _lpFbState[line];
                }

                // Matrice Hadamard
                float m0 = Hadamard4x4[0, 0] * taps[0] + Hadamard4x4[0, 1] * taps[1] + Hadamard4x4[0, 2] * taps[2] + Hadamard4x4[0, 3] * taps[3];
                float m1 = Hadamard4x4[1, 0] * taps[0] + Hadamard4x4[1, 1] * taps[1] + Hadamard4x4[1, 2] * taps[2] + Hadamard4x4[1, 3] * taps[3];
                float m2 = Hadamard4x4[2, 0] * taps[0] + Hadamard4x4[2, 1] * taps[1] + Hadamard4x4[2, 2] * taps[2] + Hadamard4x4[2, 3] * taps[3];
                float m3 = Hadamard4x4[3, 0] * taps[0] + Hadamard4x4[3, 1] * taps[1] + Hadamard4x4[3, 2] * taps[2] + Hadamard4x4[3, 3] * taps[3];

                _fdnLines[0][_fdnIdx[0]] = in0 + m0 * feedback;
                _fdnLines[1][_fdnIdx[1]] = in1 + m1 * feedback;
                _fdnLines[2][_fdnIdx[2]] = in2 + m2 * feedback;
                _fdnLines[3][_fdnIdx[3]] = in3 + m3 * feedback;
                for (int k = 0; k < 4; k++)
                {
                    _fdnIdx[k]++;
                    if (_fdnIdx[k] >= _fdnMaxSamples[k]) _fdnIdx[k] = 0;
                }

                float wetL = (taps[0] + taps[2]) * 0.5f + dripSumL * 0.4f;
                float wetR = (taps[1] + taps[3]) * 0.5f + dripSumR * 0.4f;

                // Boom EN SORTIE (post-FDN, hors boucle) : peak biquad accentue le grave a 80Hz
                // sans creer de feedback. Caractere "grotte" preserve, sans larsen.
                wetL = BiquadProcess(ref _boomBiquad[0], wetL);
                wetR = BiquadProcess(ref _boomBiquad[1], wetR);

                // Width mid/side
                float wetMid = (wetL + wetR) * 0.5f;
                float wetSide = wetL - wetR;
                wetL = wetMid + wetSide * width;
                wetR = wetMid - wetSide * width;

                left[i]  = (inL * (1f - mix) + wetL * mix) * outLin;
                right[i] = (inR * (1f - mix) + wetR * mix) * outLin;
            }
        }

        // Biquad peak filter RBJ cookbook (pour le low boom)
        internal struct BiquadState
        {
            public float b0, b1, b2, a1, a2;
            public float x1, x2, y1, y2;
            public void ResetState() { x1 = x2 = y1 = y2 = 0f; }
        }
        static void SetBiquadPeak(ref BiquadState s, int sr, float freq, float q, float dbGain)
        {
            double A = Math.Pow(10.0, dbGain / 40.0);
            double w0 = 2.0 * Math.PI * freq / sr;
            double alpha = Math.Sin(w0) / (2.0 * q);
            double cosw0 = Math.Cos(w0);
            double a0 = 1.0 + alpha / A;
            s.b0 = (float)((1.0 + alpha * A) / a0);
            s.b1 = (float)((-2.0 * cosw0) / a0);
            s.b2 = (float)((1.0 - alpha * A) / a0);
            s.a1 = (float)((-2.0 * cosw0) / a0);
            s.a2 = (float)((1.0 - alpha / A) / a0);
        }
        static float BiquadProcess(ref BiquadState s, float x)
        {
            float y = s.b0 * x + s.b1 * s.x1 + s.b2 * s.x2 - s.a1 * s.y1 - s.a2 * s.y2;
            s.x2 = s.x1; s.x1 = x;
            s.y2 = s.y1; s.y1 = y;
            return y;
        }
    }

    internal sealed class AllPassStage
    {
        readonly float[] _bufL, _bufR;
        int _idxL, _idxR;
        readonly int _size;
        readonly float _coef;
        public AllPassStage(int size, float coef)
        {
            _size = Math.Max(4, size);
            _coef = coef;
            _bufL = new float[_size];
            _bufR = new float[_size];
        }
        public void Reset() { Array.Clear(_bufL, 0, _bufL.Length); Array.Clear(_bufR, 0, _bufR.Length); _idxL = _idxR = 0; }
        public float ProcessL(float x) { float d = _bufL[_idxL]; float y = -_coef * x + d; _bufL[_idxL] = x + _coef * y; _idxL++; if (_idxL >= _size) _idxL = 0; return y; }
        public float ProcessR(float x) { float d = _bufR[_idxR]; float y = -_coef * x + d; _bufR[_idxR] = x + _coef * y; _idxR++; if (_idxR >= _size) _idxR = 0; return y; }
    }
}
