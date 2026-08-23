using System;

namespace KotonPluginCosmicVerb
{
    /// <summary>
    /// Snapshot des paramètres au début d'un buffer audio, passé au moteur pour éviter les lectures
    /// par sample. Bouger un slider s'entend au prochain buffer.
    /// </summary>
    internal struct CosmicParams
    {
        public int    ModeIndex;      // 0..CosmicModes.Count-1
        public float  DelayMs;        // 10..3000 ms — longueur de base de toutes les delays
        public float  Warp;           // 0..1 — décorrèle les longueurs des N lignes
        public float  Feedback;       // 0..1 — 1 = sustain quasi-infini
        public float  Density;        // 0..1 — sparse (identité) → dense (Householder)
        public float  ModDepth;       // 0..1 — profondeur de la modulation (± samples)
        public float  ModRateHz;      // 0..2 Hz — vitesse LFO
        public float  Width;          // 0..1 — image stéréo (M/S)
        public float  HighCutHz;      // 500..20000 — LP one-pole en sortie
        public float  Mix;            // 0..1 — dry ↔ wet
        public float  OutGainDb;      // -30..+6
    }

    /// <summary>
    /// Preset de mode : configure la topologie du FDN. Un mode = un jeu de valeurs qui rend un
    /// COMPORTEMENT (single echoes, dense reverb, long predelay, etc.) sans changer le moteur.
    /// </summary>
    internal readonly struct CosmicMode
    {
        public readonly string Name;
        public readonly int    ActiveDelays;     // 2..8 lignes actives dans le FDN
        public readonly float  PredelayMs;       // ms avant l'entrée du FDN
        public readonly float  AttackMs;         // ramp de l'entrée dans le FDN (slow attack = long)
        public readonly float  BaseScale;        // multiplicateur appliqué au DELAY (mode long → >1)
        public readonly float  DensityBias;      // ajouté à Density : mode "dense" par nature → +
        public readonly float  DarkBias;         // ajouté à HighCut : mode "filtered decay" = LP tight
        public readonly float  RatioSpread;      // ampleur des ratios entre lignes (Warp interne)
        public readonly float  ModBias;          // module de plus ou moins fort (Sirius = balanced)
        public CosmicMode(string n, int ad, float pd, float atk, float bs, float dens, float dark, float rs, float mb)
        { Name = n; ActiveDelays = ad; PredelayMs = pd; AttackMs = atk; BaseScale = bs; DensityBias = dens; DarkBias = dark; RatioSpread = rs; ModBias = mb; }
    }

    /// <summary>Table des 21 modes cosmologiques (déduits des descriptions publiques du plugin
    /// de référence). Chaque ligne configure la topologie sans toucher au moteur.</summary>
    internal static class CosmicModes
    {
        public static readonly CosmicMode[] All =
        {
            //           name                    N  pdMs atkMs base dens dark spread modBias
            new CosmicMode("Gemini",              2,   0,   1,  0.8f, +0.20f, -0.10f, 0.30f, 0.30f),
            new CosmicMode("Hydra",               4,   0,   1,  1.0f,  0.00f, -0.05f, 0.40f, 0.35f),
            new CosmicMode("Capricorn",           3,   0,   1,  0.9f, -0.15f,  0.00f, 0.35f, 0.30f),
            new CosmicMode("Scorpio",             3,   0,   1,  1.0f, -0.20f,  0.20f, 0.40f, 0.30f),
            new CosmicMode("Virgo",               4,   0,   1,  1.1f, -0.35f,  0.30f, 0.50f, 0.30f),
            new CosmicMode("Aquarius",            4,   0,   1,  1.0f,  0.00f,  0.00f, 0.55f, 0.40f),
            new CosmicMode("Pisces",              6,   0,   1,  1.0f, +0.20f, -0.05f, 0.55f, 0.40f),
            new CosmicMode("Cassiopeia",          6,   0,   1,  1.0f, +0.10f,  0.00f, 0.65f, 0.45f),
            new CosmicMode("Cirrus Minor",        4,  80,   5,  1.1f, -0.20f,  0.10f, 0.60f, 0.35f),
            new CosmicMode("Cirrus Major",        4, 140,   8,  1.2f, -0.10f,  0.15f, 0.65f, 0.40f),
            new CosmicMode("Orion",               6,   0,   3,  1.1f, +0.10f,  0.05f, 0.60f, 0.45f),
            new CosmicMode("Centaurus",           6,   0,   1,  1.2f, +0.15f,  0.05f, 0.65f, 0.45f),
            new CosmicMode("Andromeda",           8,   0,  40,  1.4f, +0.25f,  0.10f, 0.70f, 0.50f),
            new CosmicMode("Sagittarius",         8,   0,  60,  1.5f, +0.25f,  0.10f, 0.75f, 0.55f),
            new CosmicMode("Triangulum",          8,   0,  80,  1.7f, +0.30f,  0.15f, 0.75f, 0.55f),
            new CosmicMode("Large Magellanic",    8,   0,  30,  2.0f, +0.30f,  0.10f, 0.80f, 0.55f),
            new CosmicMode("Leo",                 8,   0, 120,  2.4f, +0.35f,  0.30f, 0.80f, 0.55f),
            new CosmicMode("Libra",               8,   0,  90,  2.2f, +0.30f,  0.25f, 0.75f, 0.50f),
            new CosmicMode("Pleiades",            8,   0,   2,  0.7f, +0.40f,  0.20f, 0.55f, 0.60f),
            new CosmicMode("Sirius",              8,   0,   2,  0.6f, +0.45f,  0.10f, 0.60f, 0.60f),
            new CosmicMode("Great Annihilator",   8,   0,   1,  1.6f, +0.40f,  0.10f, 0.75f, 0.55f),
        };
    }

    /// <summary>
    /// Moteur FDN (Feedback Delay Network) modulé — l'architecture standard des reverbs modernes
    /// (Jot 1991), avec long delays, matrice de feedback unitaire, LFO de modulation, filtre in-
    /// loop. Chaque MODE (cf. <see cref="CosmicModes"/>) est un preset de topologie ; le moteur
    /// est identique pour tous.
    ///
    /// **Chaîne** :
    ///   in → predelay → attack envelope → [8 delays // parallèle, longueurs warpées + modulées]
    ///     → matrice Householder mixée à identité (Density) → LP+HP boucle → sum → mid/side
    ///     → HighCut → out
    ///
    /// **Householder** : y = x - (2/N) × sum(x). Unitaire (préserve l'énergie), stable, très bon
    /// mélange. Un mix Identity (chaque ligne renvoie sur elle-même) → Householder (mélange plein)
    /// piloté par Density module la texture entre echos discrets et reverb dense.
    ///
    /// **Écrit from scratch** à partir de la littérature publique (papers CCRMA de Jot & Chaigne,
    /// Dattorro 1997). Aucun code binaire décompilé n'a été réutilisé — l'analyse du plugin de
    /// référence a servi uniquement à cataloguer les 21 modes cosmologiques et leurs
    /// comportements musicaux (nombre de delays, predelay, attack, densité de matrice).
    /// </summary>
    internal sealed class CosmicCore
    {
        const int N = 8;                  // 8 lignes FDN (max)
        const float MaxDelaySec = 15f;    // 15s max par ligne → 720k samples @ 48k = 2.88 MB × 8 = 23 MB

        readonly int _sr;
        // Delay lines circulaires. Longueur fixée à Prepare (max) ; la longueur EFFECTIVE varie
        // par sample selon DELAY × ratio × (1 + modulation).
        readonly float[][] _delays;
        readonly int[] _writeIdx = new int[N];

        // Predelay commun avant le FDN.
        readonly float[] _preL, _preR;
        int _preIdxL, _preIdxR, _preLen;

        // LFO indépendants (phase distincte) pour décorréler les modulations entre lignes.
        readonly double[] _lfoPhase = new double[N];

        // Filtres one-pole in-loop (assombrit / éclaircit le decay).
        readonly float[] _lpLoop = new float[N];
        // Filtre HighCut en sortie (LP one-pole).
        float _lpOutL, _lpOutR;

        // Envelope d'attaque : ramp linéaire de 0 à 1 en AttackMs sur l'ENTRÉE du FDN.
        float _atkEnv;

        // Ratios de warp entre lignes (fig au calcul, ratios irrationnels/premier-based).
        // Prime ratios de Jot : bien décorrélés, pas de pic modal.
        static readonly float[] BaseRatios = { 1.0f, 1.3247f, 1.6180f, 2.0000f, 2.4142f, 2.7321f, 3.1416f, 3.6180f };

        int _lastMode = -1;

        public CosmicCore(int sampleRate)
        {
            _sr = sampleRate;
            int maxSamples = (int)(MaxDelaySec * sampleRate);
            _delays = new float[N][];
            for (int i = 0; i < N; i++) _delays[i] = new float[maxSamples];
            int preMax = (int)(0.5 * sampleRate);   // 500 ms max de predelay
            _preL = new float[preMax];
            _preR = new float[preMax];
            for (int i = 0; i < N; i++) _lfoPhase[i] = i * (2.0 * Math.PI / N);   // phases réparties
        }

        public void Reset()
        {
            for (int i = 0; i < N; i++)
            {
                Array.Clear(_delays[i], 0, _delays[i].Length);
                _writeIdx[i] = 0;
                _lpLoop[i] = 0f;
            }
            Array.Clear(_preL, 0, _preL.Length);
            Array.Clear(_preR, 0, _preR.Length);
            _preIdxL = _preIdxR = 0;
            _lpOutL = _lpOutR = 0f;
            _atkEnv = 0f;
        }

        public void Process(Span<float> left, Span<float> right, CosmicParams p)
        {
            int modeIdx = Math.Max(0, Math.Min(CosmicModes.All.Length - 1, p.ModeIndex));
            var mode = CosmicModes.All[modeIdx];
            if (modeIdx != _lastMode) { _atkEnv = 0f; _lastMode = modeIdx; }

            int active = Math.Max(1, Math.Min(N, mode.ActiveDelays));
            float baseSamples = p.DelayMs * mode.BaseScale * _sr / 1000f;
            if (baseSamples < 32) baseSamples = 32;
            float maxBufSamples = _delays[0].Length - 4;
            // Longueurs par ligne : base × (1 + Warp × (ratio-1) × spread). Warp 0 = toutes = base.
            var lenSamples = new float[N];
            for (int i = 0; i < N; i++)
            {
                float ratio = BaseRatios[i];
                float warp = p.Warp * mode.RatioSpread;
                float len = baseSamples * (1f + warp * (ratio - 1f));
                if (len > maxBufSamples) len = maxBufSamples;
                if (len < 8) len = 8;
                lenSamples[i] = len;
            }

            _preLen = (int)Math.Min(_preL.Length - 4, mode.PredelayMs * _sr / 1000f);
            if (_preLen < 1) _preLen = 1;

            float fb = MathClamp(p.Feedback, 0f, 1f);
            // Feedback effectif : reste stable même à 100% grâce à Householder unitaire ; on
            // limite à 0.995 pour éviter le drift numérique.
            float g = 0.5f + 0.495f * fb;   // 0.5..0.995
            float density = MathClamp(p.Density + mode.DensityBias, 0f, 1f);
            // Density → mix Identity (α) et Householder (1-α). α=1 = échos discrets ; α=0 = dense.
            float identityMix = 1f - density;

            float modAmp = p.ModDepth * (0.5f + mode.ModBias * 0.5f) * (_sr / 1000f) * 8f;   // ±X samples max
            double modPhaseInc = 2.0 * Math.PI * p.ModRateHz / _sr;

            float hcHz = MathClamp(p.HighCutHz - mode.DarkBias * 3000f, 200f, 20000f);
            float lpCoef = 1f - (float)Math.Exp(-2.0 * Math.PI * hcHz / _sr);
            // Filtre in-loop : le même LP one-pole que HighCut mais un peu plus tight → decay filtré.
            float lpLoopCoef = lpCoef * 0.85f;

            float mix = MathClamp(p.Mix, 0f, 1f);
            float dryG = 1f - mix, wetG = mix;
            float outLin = (float)Math.Pow(10.0, p.OutGainDb / 20.0);
            float atkR = 1f / Math.Max(1, mode.AttackMs * _sr / 1000f);

            int nFrames = left.Length;
            for (int n = 0; n < nFrames; n++)
            {
                float inL = left[n], inR = right[n];
                float inMono = 0.5f * (inL + inR);

                // Predelay stéréo (L/R indépendants pour préserver la stéréo d'entrée).
                _preL[_preIdxL] = inL;
                _preR[_preIdxR] = inR;
                int rdL = _preIdxL - _preLen; if (rdL < 0) rdL += _preL.Length;
                int rdR = _preIdxR - _preLen; if (rdR < 0) rdR += _preR.Length;
                float preL = _preL[rdL], preR = _preR[rdR];
                _preIdxL = (_preIdxL + 1) % _preL.Length;
                _preIdxR = (_preIdxR + 1) % _preR.Length;

                // Envelope d'attaque : ramp 0→1 sur le mono qui rentre dans le FDN. Slow attack
                // = fade-in graduel du wet (Andromeda, Leo, Triangulum...).
                if (_atkEnv < 1f) { _atkEnv += atkR; if (_atkEnv > 1f) _atkEnv = 1f; }
                float fdnInMono = 0.5f * (preL + preR) * _atkEnv;

                // Lecture des 8 lignes avec longueur modulée par LFO. Interpolation linéaire pour
                // le pitch continu (indispensable avec la modulation, sinon zipper noise).
                var read = new float[N];
                for (int i = 0; i < active; i++)
                {
                    float mod = (float)Math.Sin(_lfoPhase[i]) * modAmp;
                    _lfoPhase[i] += modPhaseInc; if (_lfoPhase[i] > 2 * Math.PI) _lfoPhase[i] -= 2 * Math.PI;
                    float len = lenSamples[i] + mod;
                    if (len < 4) len = 4;
                    if (len > maxBufSamples) len = maxBufSamples;
                    int lenInt = (int)len;
                    float frac = len - lenInt;
                    int r0 = _writeIdx[i] - lenInt; if (r0 < 0) r0 += _delays[i].Length;
                    int r1 = r0 - 1; if (r1 < 0) r1 += _delays[i].Length;
                    read[i] = _delays[i][r0] * (1f - frac) + _delays[i][r1] * frac;
                    // LP one-pole dans la boucle : assombrit progressivement le decay.
                    _lpLoop[i] += lpLoopCoef * (read[i] - _lpLoop[i]);
                    read[i] = _lpLoop[i];
                }

                // Matrice de feedback : Identity (α) + Householder (1-α) sur les lignes actives.
                // Householder y[i] = x[i] - (2/N) × sum(x). Coefficient 2/active pour rester unitaire.
                float sum = 0f;
                for (int i = 0; i < active; i++) sum += read[i];
                float hMix = 2f / active;
                var newInput = new float[N];
                for (int i = 0; i < active; i++)
                {
                    float house = read[i] - hMix * sum;
                    float mixed = identityMix * read[i] + (1f - identityMix) * house;
                    // Feedback ×g + injection de l'entrée sur la ligne (répartie sur toutes).
                    newInput[i] = fdnInMono + mixed * g;
                }
                // Écriture séquentielle.
                for (int i = 0; i < active; i++)
                {
                    _delays[i][_writeIdx[i]] = newInput[i];
                    _writeIdx[i]++;
                    if (_writeIdx[i] >= _delays[i].Length) _writeIdx[i] = 0;
                }

                // Sortie : mid = sum lignes paires, side = sum lignes impaires (pour naturellement
                // décorréler L/R). Width = mix M/S final.
                float mid = 0f, side = 0f;
                for (int i = 0; i < active; i++)
                {
                    if ((i & 1) == 0) mid += read[i]; else side += read[i];
                }
                mid *= (2f / active);
                side *= (2f / active);
                float wL = mid + side * p.Width;
                float wR = mid - side * p.Width;

                // HighCut en sortie (LP one-pole).
                _lpOutL += lpCoef * (wL - _lpOutL);
                _lpOutR += lpCoef * (wR - _lpOutR);
                float outL = (dryG * inL + wetG * _lpOutL) * outLin;
                float outR = (dryG * inR + wetG * _lpOutR) * outLin;
                if (outL > 1f) outL = 1f; else if (outL < -1f) outL = -1f;
                if (outR > 1f) outR = 1f; else if (outR < -1f) outR = -1f;
                left[n] = outL;
                right[n] = outR;
            }
        }

        static float MathClamp(float x, float lo, float hi) { if (x < lo) return lo; if (x > hi) return hi; return x; }
    }
}
