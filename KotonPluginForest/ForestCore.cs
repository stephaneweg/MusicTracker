using System;

namespace KotonPluginForest
{
    internal struct ForestParams
    {
        public float Density;         // 0..1 → nombre + intensité des micro-taps décorrélés (crépitement)
        public float Decay;           // 0..1 → durée de la queue (0.3..0.9s typique — forêt = queue courte)
        public float Absorption;      // 0..1 → LP dans le feedback (le feuillage absorbe les aigus)
        public float Rustle;          // 0..1 → niveau du bruit de feuilles au vent
        public float RustleRate;      // 0..1 → vitesse de modulation du rustle (rafales lentes → rapides)
        public float WindMovement;    // 0..1 → amplitude du LFO de pan stéréo (vent qui passe)
        public float HpFilterHz;      // 20..800 Hz → HP en entrée (la forêt absorbe les basses)
        public float PreDelayMs;      // 0..100 ms
        public float StereoWidth;
        public float Mix;
        public float OutGainDb;
    }

    /// <summary>
    /// Ocean → grand espace ouvert, queue longue, brumeuse. Forest → espace fragmenté, queue courte
    /// et mate, texture organique ajoutée. Deux ambiances complémentaires plutôt qu'une seule reverb
    /// polyvalente.
    ///
    /// **Architecture** :
    /// 1. Pré-delay court (0-100 ms, la forêt est proche)
    /// 2. HP en entrée fort (~200 Hz par défaut, retire les basses que la forêt absorbe)
    /// 3. Diffusion précoce = 6 all-pass en série (double d'une reverb classique — les feuilles/branches
    ///    fragmentent le signal)
    /// 4. FDN 4x4 court (queue ~1 s) avec LP agressif dans le feedback (le feuillage étouffe les aigus)
    /// 5. Micro-taps décorrélés à faible gain : 6 délais courts (30-180 ms) qui simulent des
    ///    réflexions sur troncs distants
    /// 6. Rustle generator : bruit rose filtré BP 500-3000 Hz + amplitude modulée par LFO lent
    ///    (0.05-1 Hz) + panning LFO. Mixé au wet selon Rustle level.
    ///
    /// **Différent d'un simple "reverb sombre"** : c'est la combinaison rustle + micro-taps + FDN
    /// court avec LP fort qui donne l'impression organique. Une reverb sombre standard sonne juste
    /// "mate" ; Forest sonne "vivante et fragmentée".
    /// </summary>
    internal sealed class ForestCore
    {
        readonly int _sr;

        // Pré-delay
        float[] _preL, _preR;
        int _preIdx;
        int _preMaxSamples;
        const int PreMaxMs = 100;

        // Diffusion : 6 all-pass série (plus qu'une reverb classique pour fragmenter)
        AllPassStage[] _diffusion;

        // FDN 4x4 court
        float[][] _fdnLines;
        int[] _fdnIdx;
        int[] _fdnBaseSamples;
        int[] _fdnMaxSamples;

        // LP + HP dans le feedback
        float[] _lpFbState;
        float[] _hpFbState;

        // Micro-taps décorrélés : 6 buffers courts, taps aléatoires réinjectés faiblement
        float[][] _microLines;
        int[] _microIdx;
        int[] _microTapSamples;   // positions de tap dans chaque ligne

        // HP en entrée
        float _hpInStateL, _hpInStateR;

        // Rustle generator : bruit rose (approximé via LP sur bruit blanc) + BP + LFO amplitude + LFO pan
        Random _noiseRng = new Random(42);
        float _pinkLastL, _pinkLastR;
        float _bpStateL_1, _bpStateL_2, _bpStateR_1, _bpStateR_2;
        float _rustleAmpPhase;
        float _rustlePanPhase;
        float _windPhase;

        // Matrice Hadamard 4x4 normalisée (même que Ocean)
        static readonly float[,] Hadamard4x4 = new float[4, 4]
        {
            {  0.5f,  0.5f,  0.5f,  0.5f },
            {  0.5f, -0.5f,  0.5f, -0.5f },
            {  0.5f,  0.5f, -0.5f, -0.5f },
            {  0.5f, -0.5f, -0.5f,  0.5f },
        };

        public ForestCore(int sampleRate)
        {
            _sr = sampleRate;
            _preMaxSamples = PreMaxMs * sampleRate / 1000;
            _preL = new float[_preMaxSamples];
            _preR = new float[_preMaxSamples];

            // Diffusion : 6 all-pass avec longueurs premières entre elles (fragmentation)
            var apLenMs = new float[] { 3.7f, 5.9f, 9.1f, 13.7f, 19.3f, 27.7f };
            _diffusion = new AllPassStage[apLenMs.Length];
            for (int i = 0; i < apLenMs.Length; i++)
                _diffusion[i] = new AllPassStage((int)(apLenMs[i] * sampleRate / 1000f), 0.65f);

            // FDN 4x4 court — longueurs ~15..60 ms (queue rapide)
            var baseMs = new float[] { 17.3f, 27.9f, 41.1f, 59.3f };
            _fdnLines = new float[4][];
            _fdnIdx = new int[4];
            _fdnBaseSamples = new int[4];
            _fdnMaxSamples = new int[4];
            _lpFbState = new float[4];
            _hpFbState = new float[4];
            for (int i = 0; i < 4; i++)
            {
                int maxSamples = (int)(baseMs[i] * 2f * sampleRate / 1000f);
                _fdnMaxSamples[i] = maxSamples;
                _fdnLines[i] = new float[maxSamples];
                _fdnBaseSamples[i] = (int)(baseMs[i] * sampleRate / 1000f);
            }

            // Micro-taps : 6 buffers courts de 30..180 ms avec tap au 2/3 pour un feedback court
            var microMs = new float[] { 31.3f, 47.1f, 67.9f, 89.3f, 121.7f, 173.9f };
            _microLines = new float[6][];
            _microIdx = new int[6];
            _microTapSamples = new int[6];
            for (int i = 0; i < 6; i++)
            {
                int len = (int)(microMs[i] * sampleRate / 1000f);
                _microLines[i] = new float[len + 1];
                _microTapSamples[i] = (int)(len * 0.67f);
            }
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
                _hpFbState[i] = 0f;
            }
            for (int i = 0; i < _microLines.Length; i++)
            {
                Array.Clear(_microLines[i], 0, _microLines[i].Length);
                _microIdx[i] = 0;
            }
            _hpInStateL = _hpInStateR = 0f;
            _pinkLastL = _pinkLastR = 0f;
            _bpStateL_1 = _bpStateL_2 = _bpStateR_1 = _bpStateR_2 = 0f;
            _rustleAmpPhase = 0f;
            _rustlePanPhase = 0f;
            _windPhase = 0f;
        }

        public void Process(Span<float> left, Span<float> right, in ForestParams p)
        {
            int n = left.Length;
            float mix = p.Mix;
            float outLin = (float)Math.Pow(10.0, p.OutGainDb / 20.0);
            float width = p.StereoWidth;

            // Feedback gain court : 0.4..0.75 (queue toujours courte, forêt n'est pas une cathédrale)
            float feedback = 0.4f + p.Decay * 0.35f;

            int preSamples = (int)(p.PreDelayMs * _sr / 1000f);
            if (preSamples < 0) preSamples = 0;
            if (preSamples >= _preMaxSamples) preSamples = _preMaxSamples - 1;

            // LP dans le feedback : plus fort quand Absorption augmente (feuillage étouffe les aigus)
            // 0..1 → 4000..600 Hz (inverse de Ocean : ici l'absorption CROIT avec le param)
            float lpCutoff = 4000f - p.Absorption * 3400f;
            float alphaLpFb = 1f - (float)Math.Exp(-2.0 * Math.PI * lpCutoff / _sr);

            // HP dans le feedback (fixe)
            float hpFbCutoff = 100f;
            float alphaHpFb = 1f - (float)Math.Exp(-2.0 * Math.PI * hpFbCutoff / _sr);

            // HP en entrée (Filter param — plus haut par défaut que Ocean)
            float alphaHpIn = 1f - (float)Math.Exp(-2.0 * Math.PI * p.HpFilterHz / _sr);

            // Rustle LFOs : rate 0..1 → 0.05..1.5 Hz (rafales lentes à rapides)
            float rustleLfoHz = 0.05f + p.RustleRate * 1.45f;
            float rustleAmpInc = (float)(2 * Math.PI * rustleLfoHz / _sr);
            float rustlePanInc = (float)(2 * Math.PI * rustleLfoHz * 0.37f / _sr);   // pan LFO plus lent

            // Wind (mouvement stéréo global) : LFO très lent 0.03..0.3 Hz
            float windInc = (float)(2 * Math.PI * (0.03f + p.WindMovement * 0.27f) / _sr);

            // BP filter pour le rustle (~1500 Hz Q=2) — simple biquad
            float bpFreq = 1500f;
            float bpQ = 2f;
            float bp_w0 = (float)(2 * Math.PI * bpFreq / _sr);
            float bp_alpha = (float)(Math.Sin(bp_w0) / (2 * bpQ));
            float bp_cosw0 = (float)Math.Cos(bp_w0);
            float bp_a0 = 1f + bp_alpha;
            float bp_b0 = bp_alpha / bp_a0;
            float bp_b2 = -bp_alpha / bp_a0;
            float bp_a1 = -2f * bp_cosw0 / bp_a0;
            float bp_a2 = (1f - bp_alpha) / bp_a0;

            // Micro-taps : gain par tap piloté par Density
            float microGain = 0.15f * p.Density;
            float microFeedback = 0.4f + p.Density * 0.2f;

            for (int i = 0; i < n; i++)
            {
                float inL = left[i];
                float inR = right[i];

                // 1) HP en entrée (retire les basses)
                _hpInStateL += alphaHpIn * (inL - _hpInStateL);
                _hpInStateR += alphaHpIn * (inR - _hpInStateR);
                float hpL = inL - _hpInStateL;
                float hpR = inR - _hpInStateR;

                // 2) Pré-delay
                _preL[_preIdx] = hpL;
                _preR[_preIdx] = hpR;
                int preRead = _preIdx - preSamples;
                if (preRead < 0) preRead += _preMaxSamples;
                float pdL = _preL[preRead];
                float pdR = _preR[preRead];
                _preIdx++;
                if (_preIdx >= _preMaxSamples) _preIdx = 0;

                // 3) Diffusion précoce (6 all-pass série pour fragmenter)
                float diffL = pdL;
                float diffR = pdR;
                for (int a = 0; a < _diffusion.Length; a++)
                {
                    diffL = _diffusion[a].ProcessL(diffL);
                    diffR = _diffusion[a].ProcessR(diffR);
                }

                // 4) Micro-taps : réflexions décorrélées sur troncs distants
                float microSum = 0f;
                for (int m = 0; m < _microLines.Length; m++)
                {
                    var line = _microLines[m];
                    int idx = _microIdx[m];
                    int tap = idx - _microTapSamples[m];
                    if (tap < 0) tap += line.Length;
                    float tapVal = line[tap];
                    microSum += tapVal;
                    // Écriture : entrée diffusée + feedback court du tap
                    line[idx] = (m % 2 == 0 ? diffL : diffR) + tapVal * microFeedback;
                    _microIdx[m]++;
                    if (_microIdx[m] >= line.Length) _microIdx[m] = 0;
                }
                microSum *= microGain;
                float microL = microSum * 0.6f + (float)(_noiseRng.NextDouble() * 0.001);   // léger dither
                float microR = microSum * 0.6f - (float)(_noiseRng.NextDouble() * 0.001);

                // 5) FDN : injection décorrélée
                float in0 = diffL + microL * 0.4f;
                float in1 = diffR + microR * 0.4f;
                float in2 = -diffL - microL * 0.4f;
                float in3 = -diffR - microR * 0.4f;

                // Lecture FDN
                float t0 = _fdnLines[0][WrapRead(_fdnIdx[0], _fdnBaseSamples[0], _fdnMaxSamples[0])];
                float t1 = _fdnLines[1][WrapRead(_fdnIdx[1], _fdnBaseSamples[1], _fdnMaxSamples[1])];
                float t2 = _fdnLines[2][WrapRead(_fdnIdx[2], _fdnBaseSamples[2], _fdnMaxSamples[2])];
                float t3 = _fdnLines[3][WrapRead(_fdnIdx[3], _fdnBaseSamples[3], _fdnMaxSamples[3])];

                // LP + HP dans chaque ligne
                _lpFbState[0] += alphaLpFb * (t0 - _lpFbState[0]); _hpFbState[0] += alphaHpFb * (_lpFbState[0] - _hpFbState[0]); t0 = _lpFbState[0] - _hpFbState[0];
                _lpFbState[1] += alphaLpFb * (t1 - _lpFbState[1]); _hpFbState[1] += alphaHpFb * (_lpFbState[1] - _hpFbState[1]); t1 = _lpFbState[1] - _hpFbState[1];
                _lpFbState[2] += alphaLpFb * (t2 - _lpFbState[2]); _hpFbState[2] += alphaHpFb * (_lpFbState[2] - _hpFbState[2]); t2 = _lpFbState[2] - _hpFbState[2];
                _lpFbState[3] += alphaLpFb * (t3 - _lpFbState[3]); _hpFbState[3] += alphaHpFb * (_lpFbState[3] - _hpFbState[3]); t3 = _lpFbState[3] - _hpFbState[3];

                // Matrice Hadamard
                float m0 = Hadamard4x4[0, 0] * t0 + Hadamard4x4[0, 1] * t1 + Hadamard4x4[0, 2] * t2 + Hadamard4x4[0, 3] * t3;
                float m1 = Hadamard4x4[1, 0] * t0 + Hadamard4x4[1, 1] * t1 + Hadamard4x4[1, 2] * t2 + Hadamard4x4[1, 3] * t3;
                float m2 = Hadamard4x4[2, 0] * t0 + Hadamard4x4[2, 1] * t1 + Hadamard4x4[2, 2] * t2 + Hadamard4x4[2, 3] * t3;
                float m3 = Hadamard4x4[3, 0] * t0 + Hadamard4x4[3, 1] * t1 + Hadamard4x4[3, 2] * t2 + Hadamard4x4[3, 3] * t3;

                // Écriture FDN
                _fdnLines[0][_fdnIdx[0]] = in0 + m0 * feedback;
                _fdnLines[1][_fdnIdx[1]] = in1 + m1 * feedback;
                _fdnLines[2][_fdnIdx[2]] = in2 + m2 * feedback;
                _fdnLines[3][_fdnIdx[3]] = in3 + m3 * feedback;
                for (int k = 0; k < 4; k++)
                {
                    _fdnIdx[k]++;
                    if (_fdnIdx[k] >= _fdnMaxSamples[k]) _fdnIdx[k] = 0;
                }

                // 6) Sortie wet : combinaison des taps + micro-taps
                float wetL = (t0 + t2) * 0.5f + microL;
                float wetR = (t1 + t3) * 0.5f + microR;

                // 7) Rustle : bruit rose filtré BP, modulé par LFO amp + LFO pan
                _rustleAmpPhase += rustleAmpInc;
                _rustlePanPhase += rustlePanInc;
                _windPhase += windInc;
                if (_rustleAmpPhase > 2 * Math.PI) _rustleAmpPhase -= (float)(2 * Math.PI);
                if (_rustlePanPhase > 2 * Math.PI) _rustlePanPhase -= (float)(2 * Math.PI);
                if (_windPhase > 2 * Math.PI) _windPhase -= (float)(2 * Math.PI);

                // Bruit rose : intégrateur léger sur bruit blanc (approximation Voss simpliste)
                float whiteL = (float)(_noiseRng.NextDouble() * 2 - 1);
                float whiteR = (float)(_noiseRng.NextDouble() * 2 - 1);
                _pinkLastL = _pinkLastL * 0.95f + whiteL * 0.05f;
                _pinkLastR = _pinkLastR * 0.95f + whiteR * 0.05f;
                float pinkL = whiteL * 0.3f + _pinkLastL * 3f;
                float pinkR = whiteR * 0.3f + _pinkLastR * 3f;

                // BP filter sur pink → contenu ~1.5 kHz caractéristique des feuilles
                float bpOutL = bp_b0 * pinkL + bp_b2 * _bpStateL_2 - bp_a1 * _bpStateL_1 - bp_a2 * _bpStateL_2;
                _bpStateL_2 = _bpStateL_1;
                _bpStateL_1 = bpOutL;
                float bpOutR = bp_b0 * pinkR + bp_b2 * _bpStateR_2 - bp_a1 * _bpStateR_1 - bp_a2 * _bpStateR_2;
                _bpStateR_2 = _bpStateR_1;
                _bpStateR_1 = bpOutR;

                // Amplitude modulée : quand LFO amp est haut = rafale de vent, sinon silence
                float ampMod = 0.5f + 0.5f * (float)Math.Sin(_rustleAmpPhase);
                ampMod = ampMod * ampMod;   // squarer pour un effet plus "gonflé"
                float rustleGain = p.Rustle * ampMod * 0.4f;
                float rustleL = bpOutL * rustleGain;
                float rustleR = bpOutR * rustleGain;

                // Pan LFO stéréo : le vent bouge de gauche à droite
                float panLfo = (float)Math.Sin(_rustlePanPhase) * 0.5f;
                float rL = rustleL * (1f - panLfo);
                float rR = rustleR * (1f + panLfo);

                wetL += rL;
                wetR += rR;

                // 8) Wind movement : LFO pan global sur le wet
                float windPan = (float)Math.Sin(_windPhase) * p.WindMovement * 0.3f;
                float wetMid = (wetL + wetR) * 0.5f;
                float wetSide = wetL - wetR;
                wetL = wetMid + wetSide * width + wetMid * windPan;
                wetR = wetMid - wetSide * width - wetMid * windPan;

                // Mix dry/wet + gain
                left[i]  = (inL * (1f - mix) + wetL * mix) * outLin;
                right[i] = (inR * (1f - mix) + wetR * mix) * outLin;
            }
        }

        static int WrapRead(int idx, int lengthBack, int bufSize)
        {
            int r = idx - lengthBack;
            while (r < 0) r += bufSize;
            return r;
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

        public void Reset()
        {
            Array.Clear(_bufL, 0, _bufL.Length);
            Array.Clear(_bufR, 0, _bufR.Length);
            _idxL = _idxR = 0;
        }

        public float ProcessL(float x)
        {
            float d = _bufL[_idxL];
            float y = -_coef * x + d;
            _bufL[_idxL] = x + _coef * y;
            _idxL++;
            if (_idxL >= _size) _idxL = 0;
            return y;
        }

        public float ProcessR(float x)
        {
            float d = _bufR[_idxR];
            float y = -_coef * x + d;
            _bufR[_idxR] = x + _coef * y;
            _idxR++;
            if (_idxR >= _size) _idxR = 0;
            return y;
        }
    }
}
