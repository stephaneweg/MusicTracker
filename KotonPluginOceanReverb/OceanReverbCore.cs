using System;

namespace KotonPluginOceanReverb
{
    /// <summary>Snapshot des paramètres figés au début d'un buffer audio.</summary>
    internal struct OceanParams
    {
        public int Mode;              // 0=Abyss, 1=Tide, 2=Foam
        public float Size;            // 0..1 → longueurs des lignes à retard
        public float Decay;           // 0..1 → gain de feedback
        public float Brightness;      // 0..1 → LP cutoff dans le feedback
        public float Movement;        // 0..1 → depth de la modulation des lignes
        public float PreDelayMs;      // 0..500 ms
        public float HpFilterHz;      // 20..1000 Hz → HP en entrée
        public float DuckDepth;       // 0..1 → force du ducker
        public float StereoWidth;     // 0..1
        public bool Freeze;
        public float Mix;             // 0..1 → dry/wet
        public float OutGainDb;       // -30..+6 dB
    }

    /// <summary>
    /// Ocean Reverb — moteur DSP. FDN 4x4 (Feedback Delay Network) avec matrice Hadamard normalisée
    /// pour la diffusion inter-lignes, 4 all-pass en série en entrée pour la diffusion précoce,
    /// modulation LFO des longueurs de délai (Movement), et 3 modes distincts qui recolorent
    /// radicalement la queue :
    /// - <b>Abyss</b> : shimmer pitch-shift +12 semitones dans la boucle de feedback (émergence
    ///   progressive d'octaves supérieures — signature Valhalla Shimmer / Eno).
    /// - <b>Tide</b> : LP variable dans le feedback modulé par un LFO très lent (~0.15 Hz) — la
    ///   brillance de la queue va et vient, comme les vagues sur le rivage.
    /// - <b>Foam</b> : diffusion supplémentaire en entrée (8 all-pass au lieu de 4) + attaque
    ///   adoucie via envelope follower — les transitoires sont dilués, tout flotte.
    ///
    /// **Freeze** = gain feedback exactement 1.0 + input mute : queue infinie, pad ambient qui se
    /// nourrit de ce qui vient d'être joué. Ducking = envelope follower classique sur l'input →
    /// atténuation du wet en présence de signal (garde la voix lisible sous une reverb longue).
    ///
    /// **Pré-delay** : ligne à retard courte en entrée (0..500 ms). L'user perçoit le pré-delay
    /// comme un espace : 0 = son collé au wet ("douche"), 200 ms = grande salle où l'on entend la
    /// première réflexion arriver après la voix.
    /// </summary>
    internal sealed class OceanReverbCore
    {
        readonly int _sr;

        // Pré-delay
        float[] _preL, _preR;
        int _preIdx;
        const int PreMaxMs = 500;
        int _preMaxSamples;

        // 4 all-pass série en entrée (diffusion) + 4 supplémentaires en Foam mode
        AllPassStage[] _diffusion;

        // FDN 4x4 : 4 lignes à retard + matrice de mélange Hadamard 4x4 (orthogonale, préserve
        // l'énergie — condition nécessaire pour éviter que la reverb explose).
        float[][] _fdnLines;
        int[] _fdnIdx;
        int[] _fdnBaseSamples;   // longueur de chaque ligne (samples), pilotée par Size
        int[] _fdnMaxSamples;    // longueur allouée max (pour permettre Size = 1.0)

        // LP + HP dans le feedback (Brightness contrôle le LP)
        float[] _lpFbState;
        float[] _hpFbState;

        // LFOs pour modulation des longueurs (Movement)
        float[] _lfoPhase;
        float[] _lfoInc;

        // LFO lent pour le mode Tide (module le LP cutoff dans le feedback)
        float _tideLfoPhase;
        float _tideLfoInc;

        // HP en entrée (Filter param)
        float _hpInStateL, _hpInStateR;

        // Envelope follower pour le ducker
        float _envFollow;

        // Attack smoother pour le mode Foam
        float _foamSmootherL, _foamSmootherR;

        // Shimmer pitch shifter pour le mode Abyss (deux delay lines lues à vitesse 2x avec
        // crossfade pour produire un pitch +12 semitones injecté dans le feedback)
        ShimmerPitchShifter _shimmer;

        // Matrice Hadamard 4x4 normalisée × 0.5 (préserve la puissance)
        static readonly float[,] Hadamard4x4 = new float[4, 4]
        {
            {  0.5f,  0.5f,  0.5f,  0.5f },
            {  0.5f, -0.5f,  0.5f, -0.5f },
            {  0.5f,  0.5f, -0.5f, -0.5f },
            {  0.5f, -0.5f, -0.5f,  0.5f },
        };

        public OceanReverbCore(int sampleRate)
        {
            _sr = sampleRate;
            _preMaxSamples = PreMaxMs * sampleRate / 1000;
            _preL = new float[_preMaxSamples];
            _preR = new float[_preMaxSamples];

            // 8 all-pass (les 4 premiers toujours actifs, les 4 suivants activés en mode Foam).
            // Longueurs premières entre elles pour éviter des colorations résonantes.
            var apLenMs = new float[] { 4.7f, 7.3f, 11.1f, 17.9f, 23.7f, 31.3f, 41.1f, 53.7f };
            _diffusion = new AllPassStage[apLenMs.Length];
            for (int i = 0; i < apLenMs.Length; i++)
            {
                int len = (int)(apLenMs[i] * sampleRate / 1000f);
                _diffusion[i] = new AllPassStage(len, 0.7f);
            }

            // FDN : 4 lignes de longueurs premières entre elles, ~30..90 ms de base (multipliées
            // par Size 0..1 → range 15..180 ms). Max alloué au double.
            var baseMs = new float[] { 29.7f, 47.1f, 63.3f, 89.7f };
            _fdnLines = new float[4][];
            _fdnIdx = new int[4];
            _fdnBaseSamples = new int[4];
            _fdnMaxSamples = new int[4];
            _lpFbState = new float[4];
            _hpFbState = new float[4];
            _lfoPhase = new float[4];
            _lfoInc = new float[4];
            for (int i = 0; i < 4; i++)
            {
                int maxSamples = (int)(baseMs[i] * 3f * sampleRate / 1000f);   // 3× la base pour Size grand
                _fdnMaxSamples[i] = maxSamples;
                _fdnLines[i] = new float[maxSamples];
                _fdnBaseSamples[i] = (int)(baseMs[i] * sampleRate / 1000f);
                _lfoPhase[i] = i * 0.7f;   // décalés
                _lfoInc[i] = (float)(2 * Math.PI * (0.11f + i * 0.037f) / sampleRate);   // 0.11..0.22 Hz
            }

            _tideLfoInc = (float)(2 * Math.PI * 0.15f / sampleRate);   // 0.15 Hz = ~7 s de période

            _shimmer = new ShimmerPitchShifter(sampleRate);
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
            _hpInStateL = _hpInStateR = 0f;
            _envFollow = 0f;
            _foamSmootherL = _foamSmootherR = 0f;
            _shimmer.Reset();
        }

        public void Process(Span<float> left, Span<float> right, in OceanParams p)
        {
            int n = left.Length;
            float mix = p.Mix;
            float outLin = (float)Math.Pow(10.0, p.OutGainDb / 20.0);
            float width = p.StereoWidth;
            bool freeze = p.Freeze;

            // Feedback gain : Decay 0..1 → 0.5..0.9 en normal, exactement 1.0 en Freeze
            float feedback = freeze ? 1.0f : (0.5f + p.Decay * 0.4f);

            // Pré-delay en samples
            int preSamples = (int)(p.PreDelayMs * _sr / 1000f);
            if (preSamples < 0) preSamples = 0;
            if (preSamples >= _preMaxSamples) preSamples = _preMaxSamples - 1;

            // LP cutoff dans le feedback : Brightness 0..1 → 300..8000 Hz
            float baseCutoff = 300f + p.Brightness * 7700f;
            // HP dans le feedback (fixe, 60 Hz — évite l'accumulation basse fréquence qui rend la queue "boueuse")
            float hpFbCutoff = 60f;
            float alphaHpFb = 1f - (float)Math.Exp(-2.0 * Math.PI * hpFbCutoff / _sr);

            // HP en entrée : Filter 0..1 → 20..1000 Hz
            float alphaHpIn = 1f - (float)Math.Exp(-2.0 * Math.PI * p.HpFilterHz / _sr);

            // Attaque du ducker
            float envAttack = 1f - (float)Math.Exp(-1.0 / (0.010 * _sr));   // 10 ms attaque
            float envRelease = 1f - (float)Math.Exp(-1.0 / (0.200 * _sr));  // 200 ms release

            // Foam smoothing : pôle très bas (~50 Hz)
            float foamAlpha = 1f - (float)Math.Exp(-2.0 * Math.PI * 50f / _sr);

            // Size global : multiplie les longueurs de délai. Range 0.3..1.5 (empêche des taps trop courts)
            float sizeMul = 0.3f + p.Size * 1.2f;

            for (int i = 0; i < n; i++)
            {
                float inL = left[i];
                float inR = right[i];

                // 1) HP en entrée (retire les basses accumulables)
                _hpInStateL += alphaHpIn * (inL - _hpInStateL);
                _hpInStateR += alphaHpIn * (inR - _hpInStateR);
                float hpL = inL - _hpInStateL;
                float hpR = inR - _hpInStateR;

                // 2) Envelope follower pour le ducker (sur mono sum)
                float absMono = Math.Abs((hpL + hpR) * 0.5f);
                if (absMono > _envFollow) _envFollow += envAttack * (absMono - _envFollow);
                else _envFollow += envRelease * (absMono - _envFollow);
                float duckGain = 1f - _envFollow * p.DuckDepth * 3f;
                if (duckGain < 0f) duckGain = 0f;

                // 3) Pré-delay : écrire l'input HPfiltré, lire preSamples derrière
                _preL[_preIdx] = hpL;
                _preR[_preIdx] = hpR;
                int preRead = _preIdx - preSamples;
                if (preRead < 0) preRead += _preMaxSamples;
                float dryDelayedL = _preL[preRead];
                float dryDelayedR = _preR[preRead];
                _preIdx++;
                if (_preIdx >= _preMaxSamples) _preIdx = 0;

                // 4) En Freeze : input muted (la queue vit sur elle-même)
                float toDiffuseL = freeze ? 0f : dryDelayedL;
                float toDiffuseR = freeze ? 0f : dryDelayedR;

                // 5) Diffusion précoce : 4 all-pass série (+ 4 en mode Foam)
                int nAllPass = (p.Mode == 2) ? _diffusion.Length : 4;
                float diffL = toDiffuseL;
                float diffR = toDiffuseR;
                for (int a = 0; a < nAllPass; a++)
                {
                    diffL = _diffusion[a].ProcessL(diffL);
                    diffR = _diffusion[a].ProcessR(diffR);
                }

                // 6) Foam smoothing : lisse l'attaque (transitoires diluées)
                if (p.Mode == 2)
                {
                    _foamSmootherL += foamAlpha * (diffL - _foamSmootherL);
                    _foamSmootherR += foamAlpha * (diffR - _foamSmootherR);
                    diffL = _foamSmootherL;
                    diffR = _foamSmootherR;
                }

                // 7) Injection dans le FDN : les 4 lignes reçoivent des taps décorrélés
                //    (diffL, diffR, -diffL, -diffR) pour l'entrée stéréo
                float in0 = diffL;
                float in1 = diffR;
                float in2 = -diffL;
                float in3 = -diffR;

                // 8) Lecture des lignes FDN avec modulation LFO (Movement)
                float[] taps = new float[4];
                for (int line = 0; line < 4; line++)
                {
                    // Modulation : ±5% de la longueur de base par LFO
                    _lfoPhase[line] += _lfoInc[line];
                    if (_lfoPhase[line] > 2 * Math.PI) _lfoPhase[line] -= (float)(2 * Math.PI);
                    float lenBase = _fdnBaseSamples[line] * sizeMul;
                    float lenMod = lenBase * (1f + 0.05f * p.Movement * (float)Math.Sin(_lfoPhase[line]));
                    int len = (int)lenMod;
                    if (len < 4) len = 4;
                    if (len >= _fdnMaxSamples[line]) len = _fdnMaxSamples[line] - 1;

                    int readIdx = _fdnIdx[line] - len;
                    while (readIdx < 0) readIdx += _fdnMaxSamples[line];
                    taps[line] = _fdnLines[line][readIdx];
                }

                // 9) Tide mode : LFO lent module le LP cutoff dans le feedback
                float cutoffMod = baseCutoff;
                if (p.Mode == 1)
                {
                    _tideLfoPhase += _tideLfoInc;
                    if (_tideLfoPhase > 2 * Math.PI) _tideLfoPhase -= (float)(2 * Math.PI);
                    float mod = 0.5f + 0.5f * (float)Math.Sin(_tideLfoPhase);   // 0..1
                    cutoffMod = baseCutoff * (0.3f + 0.7f * mod);   // 30%..100% du base cutoff
                }
                float alphaLpFb = 1f - (float)Math.Exp(-2.0 * Math.PI * cutoffMod / _sr);

                // 10) LP + HP dans chaque ligne de feedback
                for (int line = 0; line < 4; line++)
                {
                    _lpFbState[line] += alphaLpFb * (taps[line] - _lpFbState[line]);
                    float lp = _lpFbState[line];
                    _hpFbState[line] += alphaHpFb * (lp - _hpFbState[line]);
                    taps[line] = lp - _hpFbState[line];
                }

                // 11) Abyss mode : injecte le shimmer (+12 semitones) sur les taps
                if (p.Mode == 0)
                {
                    float shimIn = (taps[0] + taps[1] + taps[2] + taps[3]) * 0.25f;
                    float shim = _shimmer.Process(shimIn);
                    // Mixe le shimmer avec le tap dans la boucle — le movement contrôle la quantité
                    float shimGain = 0.4f * (0.3f + 0.7f * p.Movement);
                    for (int line = 0; line < 4; line++)
                        taps[line] = taps[line] * 0.85f + shim * shimGain;
                }

                // 12) Matrice Hadamard : mélange les 4 taps
                float m0 = Hadamard4x4[0, 0] * taps[0] + Hadamard4x4[0, 1] * taps[1] + Hadamard4x4[0, 2] * taps[2] + Hadamard4x4[0, 3] * taps[3];
                float m1 = Hadamard4x4[1, 0] * taps[0] + Hadamard4x4[1, 1] * taps[1] + Hadamard4x4[1, 2] * taps[2] + Hadamard4x4[1, 3] * taps[3];
                float m2 = Hadamard4x4[2, 0] * taps[0] + Hadamard4x4[2, 1] * taps[1] + Hadamard4x4[2, 2] * taps[2] + Hadamard4x4[2, 3] * taps[3];
                float m3 = Hadamard4x4[3, 0] * taps[0] + Hadamard4x4[3, 1] * taps[1] + Hadamard4x4[3, 2] * taps[2] + Hadamard4x4[3, 3] * taps[3];

                // 13) Écriture : entrée + feedback * matrice mixée
                _fdnLines[0][_fdnIdx[0]] = in0 + m0 * feedback;
                _fdnLines[1][_fdnIdx[1]] = in1 + m1 * feedback;
                _fdnLines[2][_fdnIdx[2]] = in2 + m2 * feedback;
                _fdnLines[3][_fdnIdx[3]] = in3 + m3 * feedback;
                for (int line = 0; line < 4; line++)
                {
                    _fdnIdx[line]++;
                    if (_fdnIdx[line] >= _fdnMaxSamples[line]) _fdnIdx[line] = 0;
                }

                // 14) Sortie stéréo : combine les taps de manière décorrélée
                float wetL = (taps[0] + taps[2]) * 0.5f;
                float wetR = (taps[1] + taps[3]) * 0.5f;

                // Width : mid/side
                float wetMid = (wetL + wetR) * 0.5f;
                float wetSide = wetL - wetR;
                wetL = wetMid + wetSide * width;
                wetR = wetMid - wetSide * width;

                // Ducker sur wet
                wetL *= duckGain;
                wetR *= duckGain;

                // Mix dry/wet + output gain
                left[i]  = (inL * (1f - mix) + wetL * mix) * outLin;
                right[i] = (inR * (1f - mix) + wetR * mix) * outLin;
            }
        }
    }

    /// <summary>Étage all-pass classique de reverb (Schroeder) : diffuseur qui préserve l'amplitude
    /// mais disperse la phase, ce qui étale les transitoires sans les colorer.</summary>
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

    /// <summary>
    /// Pitch shifter simple pour le shimmer (mode Abyss) : deux delay lines lues à vitesse 2× avec
    /// crossfade au wrap-around. Produit une transposition +12 semitones avec des artefacts modestes
    /// (adaptés à une reverb — les glitches sont noyés dans la queue). Grain size ~50 ms, crossfade
    /// 30% de la fin du grain. Basé sur la technique historique de la SPX-90 Yamaha.
    /// </summary>
    internal sealed class ShimmerPitchShifter
    {
        readonly int _sr;
        readonly float[] _buf;
        int _writeIdx;
        readonly int _bufSize;

        float _readPos1, _readPos2;
        readonly float _grainSize;
        readonly float _fadeSize;

        public ShimmerPitchShifter(int sampleRate)
        {
            _sr = sampleRate;
            _bufSize = sampleRate;   // 1 seconde
            _buf = new float[_bufSize];
            _grainSize = _sr * 0.05f;   // 50 ms
            _fadeSize = _grainSize * 0.3f;
            _readPos1 = 0;
            _readPos2 = _grainSize * 0.5f;   // second grain démarré à mi-course pour crossfade
        }

        public void Reset()
        {
            Array.Clear(_buf, 0, _buf.Length);
            _writeIdx = 0;
            _readPos1 = 0;
            _readPos2 = _grainSize * 0.5f;
        }

        public float Process(float x)
        {
            _buf[_writeIdx] = x;
            _writeIdx++;
            if (_writeIdx >= _bufSize) _writeIdx = 0;

            // Lecture à vitesse 2× (produit octave up = pitch +12)
            _readPos1 += 2f;
            _readPos2 += 2f;

            // Wrap avec crossfade
            if (_readPos1 >= _grainSize) _readPos1 -= _grainSize;
            if (_readPos2 >= _grainSize) _readPos2 -= _grainSize;

            // Lecture interpolée à writeIdx - readPos
            float y1 = ReadFrac(_writeIdx - _readPos1);
            float y2 = ReadFrac(_writeIdx - _readPos2);

            // Crossfade Hann-like : chaque grain a un fade in/out
            float gain1 = Envelope(_readPos1);
            float gain2 = Envelope(_readPos2);

            return y1 * gain1 + y2 * gain2;
        }

        float Envelope(float pos)
        {
            // Fade in de 0 à _fadeSize, plein de _fadeSize à _grainSize-_fadeSize, fade out ensuite
            if (pos < _fadeSize) return pos / _fadeSize;
            if (pos > _grainSize - _fadeSize) return (_grainSize - pos) / _fadeSize;
            return 1f;
        }

        float ReadFrac(float pos)
        {
            while (pos < 0) pos += _bufSize;
            while (pos >= _bufSize) pos -= _bufSize;
            int i0 = (int)pos;
            int i1 = i0 + 1;
            if (i1 >= _bufSize) i1 = 0;
            float frac = pos - i0;
            return _buf[i0] * (1f - frac) + _buf[i1] * frac;
        }
    }
}
