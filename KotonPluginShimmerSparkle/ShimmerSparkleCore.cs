using System;

namespace KotonPluginShimmerSparkle
{
    internal struct ShimmerSparkleParams
    {
        public float Size, Decay, Damping, PreDelayMs;
        public float Shimmer, ShimmerSemis;
        public float SparkleAmount, SparkleGain;
        public int SparklePitchLo, SparklePitchHi;
        public int SparkleKey, SparkleScale;
        public float SparkleDecayMs;
        public bool SparkleTrigFromInput;
        public float Mix, OutGainDb;
    }

    /// <summary>
    /// DSP de Shimmer + Sparkle : FDN 4x4 (Hadamard) pour la reverb, avec un pitch-shifter grain
    /// dans le feedback (shimmer), + un generateur d'evenements Poisson qui declenchent des bells
    /// modales injectees DANS la reverb (sparkle). Les bells traversent donc le shimmer et se
    /// transforment progressivement en poussiere aigue.
    /// </summary>
    internal sealed class ShimmerSparkleCore
    {
        readonly int _sr;

        // ---- FDN 4x4 ----
        const int Lines = 4;
        readonly float[][] _dl;         // 4 delay lines
        readonly int[] _dlLen;          // longueur de chaque ligne
        readonly int[] _dlPos;          // position ecriture
        readonly float[] _dlLp;         // etat LP dans le feedback (damping)

        // Base delays en samples pour size=1 (prime-based, evite les periodes coincidents)
        static readonly int[] BaseDelays = { 1499, 2039, 2789, 3413 };

        // ---- Pre-delay ----
        float[] _preL, _preR;
        int _prePos;

        // ---- Pitch shifter grain (shimmer) ----
        // 2 grains crossfades, fenetre Hann, taille 4096. On lit dans le buffer d'entree avec un
        // taux different pour pitcher.
        const int PsBufSize = 8192;    // buffer large pour headroom lecture arriere
        readonly float[] _psBuf;
        int _psWritePos;
        double _psReadPos1, _psReadPos2;
        int _psGrainSize;              // taille grain courante (~4096)
        int _psGrainCounter;           // sample counter au sein du grain
        double _psRateCache = 1.0;

        // ---- Bells (sparkle) ----
        // Chaque bell = 3 modes sinus. On garde un pool de bells actives.
        const int MaxBells = 24;
        readonly Bell[] _bells = new Bell[MaxBells];
        Random _rng = new Random(1337);
        double _sparkleAcc;            // accumulateur Poisson (en secondes)

        // ---- Input envelope (pour trigger sparkle from input) ----
        float _inputEnv;
        float _inputEnvAtk, _inputEnvRel;

        public ShimmerSparkleCore(int sr)
        {
            _sr = sr;
            _dl = new float[Lines][];
            _dlLen = new int[Lines];
            _dlPos = new int[Lines];
            _dlLp = new float[Lines];
            int maxLen = 0;
            for (int i = 0; i < Lines; i++)
            {
                int len = BaseDelays[i] * 4;   // headroom pour size=1
                _dl[i] = new float[len];
                _dlLen[i] = len;
                if (len > maxLen) maxLen = len;
            }
            _preL = new float[sr / 2];   // 500 ms max pre-delay
            _preR = new float[sr / 2];
            _psBuf = new float[PsBufSize];
            _psGrainSize = 4096;
            _psReadPos1 = 0;
            _psReadPos2 = PsBufSize / 2.0;
            _psGrainCounter = 0;

            for (int i = 0; i < MaxBells; i++) _bells[i] = new Bell();

            _inputEnvAtk = (float)Math.Exp(-1.0 / (0.005 * sr));   // 5 ms
            _inputEnvRel = (float)Math.Exp(-1.0 / (0.100 * sr));   // 100 ms
        }

        public void Reset()
        {
            for (int i = 0; i < Lines; i++) { Array.Clear(_dl[i], 0, _dlLen[i]); _dlPos[i] = 0; _dlLp[i] = 0; }
            Array.Clear(_preL, 0, _preL.Length); Array.Clear(_preR, 0, _preR.Length); _prePos = 0;
            Array.Clear(_psBuf, 0, _psBuf.Length); _psWritePos = 0; _psReadPos1 = 0; _psReadPos2 = PsBufSize / 2.0; _psGrainCounter = 0;
            for (int i = 0; i < MaxBells; i++) _bells[i].Active = false;
            _inputEnv = 0;
            _sparkleAcc = 0;
        }

        public void Process(Span<float> left, Span<float> right, ShimmerSparkleParams p)
        {
            int n = left.Length;
            int preSamples = Math.Max(0, Math.Min(_preL.Length - 1, (int)(p.PreDelayMs * _sr / 1000.0)));
            // Feedback plafonne a 0.90 (etait 0.999 = quasi-infini + shimmer inject = divergence
            // exponentielle en quelques secondes -> NaN silencieux). Ensuite COMPENSATION : chaque
            // tour de shimmer AJOUTE de l'energie qui n'etait pas dans le signal (le pitch shifter
            // grain n'attenue pas), donc feedback+shimmer_inject > gain unitaire = buildup lent
            // meme apres SoftClip. On soustrait shimmerAmt au feedback pour que l'energie totale
            // reinjectee par cycle reste sous 1.
            float feedback = 0.5f + p.Decay * 0.40f;
            float shimmerAmt = p.Shimmer * 0.35f;   // scale l'injection
            feedback -= shimmerAmt * 0.90f;         // compensation : chaque unite de shimmer inject
                                                    // remplace ~1 unite de feedback pour rester stable
            if (feedback < 0.20f) feedback = 0.20f; // garde une queue minimale meme a shimmer max
            float damping = p.Damping;
            double shimmerRate = Math.Pow(2.0, p.ShimmerSemis / 12.0);
            _psRateCache = shimmerRate;
            float wet = p.Mix;
            float dry = 1f - wet;
            float outGain = (float)Math.Pow(10.0, p.OutGainDb / 20.0);
            float sparkleGain = p.SparkleGain;

            // Sparkle rate : 0..1 → 0..12 events/sec (courbe exp pour finesse aux petites valeurs)
            double sparkleRateHz = p.SparkleAmount * p.SparkleAmount * 12.0;
            double dt = 1.0 / _sr;

            for (int i = 0; i < n; i++)
            {
                float inL = left[i], inR = right[i];
                float inM = (inL + inR) * 0.5f;

                // Env follower input
                float absIn = inM < 0 ? -inM : inM;
                _inputEnv = absIn > _inputEnv
                    ? _inputEnvAtk * _inputEnv + (1 - _inputEnvAtk) * absIn
                    : _inputEnvRel * _inputEnv + (1 - _inputEnvRel) * absIn;

                // Pre-delay
                _preL[_prePos] = inL; _preR[_prePos] = inR;
                int readPre = _prePos - preSamples; if (readPre < 0) readPre += _preL.Length;
                float dryPreL = _preL[readPre];
                float dryPreR = _preR[readPre];
                _prePos = (_prePos + 1) % _preL.Length;

                // Sparkle : evenement Poisson ? Gated by input if enabled.
                float rateEff = (float)sparkleRateHz;
                if (p.SparkleTrigFromInput) rateEff *= Math.Min(1f, _inputEnv * 4f);
                if (rateEff > 0.0001f && _rng.NextDouble() < rateEff * dt) TriggerSparkle(p);

                // Somme des bells (elles injectent dans la reverb, pas dans le dry)
                float bellL = 0f, bellR = 0f;
                for (int b = 0; b < MaxBells; b++)
                {
                    if (!_bells[b].Active) continue;
                    float s = _bells[b].Render(_sr);
                    bellL += s * _bells[b].PanL;
                    bellR += s * _bells[b].PanR;
                }
                bellL *= sparkleGain; bellR *= sparkleGain;

                // Signal qui rentre dans la reverb : dry pre-delayed + bells
                float rvIn = (dryPreL + dryPreR) * 0.5f + (bellL + bellR) * 0.5f;

                // Lire les 4 delay lines
                float a0 = ReadDL(0, p.Size);
                float a1 = ReadDL(1, p.Size);
                float a2 = ReadDL(2, p.Size);
                float a3 = ReadDL(3, p.Size);

                // Hadamard 4x4 mix
                float m0 = a0 + a1 + a2 + a3;
                float m1 = a0 - a1 + a2 - a3;
                float m2 = a0 + a1 - a2 - a3;
                float m3 = a0 - a1 - a2 + a3;
                m0 *= 0.5f; m1 *= 0.5f; m2 *= 0.5f; m3 *= 0.5f;

                // Shimmer : injecter la sortie pitch-shiftee dans le feedback
                float rvSum = (a0 + a1 + a2 + a3) * 0.25f;
                float shifted = ProcessPitchShifter(rvSum);
                float shimmerInject = shifted * shimmerAmt;

                // Feedback : chaque ligne recoit rvIn + mix Hadamard + shimmer, avec LP damping
                float wr0 = rvIn + m0 * feedback + shimmerInject;
                float wr1 = rvIn + m1 * feedback + shimmerInject;
                float wr2 = rvIn + m2 * feedback + shimmerInject;
                float wr3 = rvIn + m3 * feedback + shimmerInject;

                // Damping LP dans le feedback
                _dlLp[0] = _dlLp[0] + (1 - damping) * (wr0 - _dlLp[0]); wr0 = _dlLp[0];
                _dlLp[1] = _dlLp[1] + (1 - damping) * (wr1 - _dlLp[1]); wr1 = _dlLp[1];
                _dlLp[2] = _dlLp[2] + (1 - damping) * (wr2 - _dlLp[2]); wr2 = _dlLp[2];
                _dlLp[3] = _dlLp[3] + (1 - damping) * (wr3 - _dlLp[3]); wr3 = _dlLp[3];

                // Soft-clip tanh dans le feedback + guard NaN/Inf (l'ancien code laissait diverger,
                // au bout de quelques secondes NaN → toute la reverb devient silencieuse et le
                // son "disparaissait"). Un soft-clip a ±1.3 limite l'energie sans casser le sustain.
                wr0 = SoftClip(wr0); wr1 = SoftClip(wr1); wr2 = SoftClip(wr2); wr3 = SoftClip(wr3);

                WriteDL(0, wr0); WriteDL(1, wr1); WriteDL(2, wr2); WriteDL(3, wr3);

                // Sortie stereo : mix des 4 lignes (L = 0+2, R = 1+3)
                float wetL = (a0 + a2) * 0.5f + bellL * 0.6f;
                float wetR = (a1 + a3) * 0.5f + bellR * 0.6f;

                float outL = (inL * dry + wetL * wet) * outGain;
                float outR = (inR * dry + wetR * wet) * outGain;
                if (outL > 1f) outL = 1f; else if (outL < -1f) outL = -1f;
                if (outR > 1f) outR = 1f; else if (outR < -1f) outR = -1f;
                left[i] = outL; right[i] = outR;
            }
        }

        // Soft-clip tanh + guard NaN/Inf : garde le signal borne dans [-1.3, +1.3] (tanh est doux
        // avant clipping dur) et remet a 0 si un NaN/Inf s'infiltre (pitch shifter, dividez-par-zero,
        // denormals x infinity). Sans ce guard le feedback pouvait diverger silencieusement.
        static float SoftClip(float x)
        {
            if (float.IsNaN(x) || float.IsInfinity(x)) return 0f;
            // tanh a ~x=1 vaut 0.76 ; on scale pour que le domaine utile [-1,+1] passe sans distorsion
            // audible, mais que des pointes a ±3 se saturent doucement.
            return (float)Math.Tanh(x * 0.8) * 1.25f;
        }

        float ReadDL(int i, float size)
        {
            int len = (int)Math.Max(64, BaseDelays[i] * (0.3f + size * 0.7f * 4f));
            if (len >= _dlLen[i]) len = _dlLen[i] - 1;
            int r = _dlPos[i] - len;
            if (r < 0) r += _dlLen[i];
            return _dl[i][r];
        }
        void WriteDL(int i, float v) { _dl[i][_dlPos[i]] = v; _dlPos[i] = (_dlPos[i] + 1) % _dlLen[i]; }

        // ==== Pitch shifter grain ====
        // 2 grains 4096 samples, fenetre Hann, crossfade a mi-parcours. On lit dans le buffer
        // d'entree avec un taux different (shimmerRate = 2^(semis/12)).
        float ProcessPitchShifter(float x)
        {
            _psBuf[_psWritePos] = x;
            _psWritePos = (_psWritePos + 1) % PsBufSize;

            // Grain 1
            int r1i = (int)_psReadPos1; float r1f = (float)(_psReadPos1 - r1i);
            int r1a = r1i % PsBufSize; int r1b = (r1i + 1) % PsBufSize;
            float g1 = _psBuf[r1a] + (_psBuf[r1b] - _psBuf[r1a]) * r1f;
            // Grain 2
            int r2i = (int)_psReadPos2; float r2f = (float)(_psReadPos2 - r2i);
            int r2a = r2i % PsBufSize; int r2b = (r2i + 1) % PsBufSize;
            float g2 = _psBuf[r2a] + (_psBuf[r2b] - _psBuf[r2a]) * r2f;

            // Fenetre Hann : t in [0..1] pour chaque grain
            float t1 = _psGrainCounter / (float)_psGrainSize;
            float t2 = ((_psGrainCounter + _psGrainSize / 2) % _psGrainSize) / (float)_psGrainSize;
            float w1 = 0.5f * (1f - (float)Math.Cos(t1 * Math.PI * 2));
            float w2 = 0.5f * (1f - (float)Math.Cos(t2 * Math.PI * 2));

            float out_ = g1 * w1 + g2 * w2;

            // Avance les 2 lectures
            _psReadPos1 += _psRateCache;
            _psReadPos2 += _psRateCache;
            while (_psReadPos1 >= PsBufSize) _psReadPos1 -= PsBufSize;
            while (_psReadPos2 >= PsBufSize) _psReadPos2 -= PsBufSize;
            _psGrainCounter++;
            if (_psGrainCounter >= _psGrainSize) _psGrainCounter = 0;

            return out_;
        }

        // ==== Sparkle bell trigger ====
        static readonly int[] ScaleMajor = { 0, 2, 4, 5, 7, 9, 11 };
        static readonly int[] ScaleMinor = { 0, 2, 3, 5, 7, 8, 10 };
        static readonly int[] ScalePentaMaj = { 0, 2, 4, 7, 9 };
        static readonly int[] ScalePentaMin = { 0, 3, 5, 7, 10 };
        static readonly int[] ScaleChroma = { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11 };

        int[] PickScale(int scaleIdx)
        {
            switch (scaleIdx) {
                case 1: return ScaleMinor;
                case 2: return ScalePentaMaj;
                case 3: return ScalePentaMin;
                case 4: return ScaleChroma;
                default: return ScaleMajor;
            }
        }

        void TriggerSparkle(ShimmerSparkleParams p)
        {
            // Trouver un slot bell libre
            int slot = -1;
            for (int i = 0; i < MaxBells; i++) if (!_bells[i].Active) { slot = i; break; }
            if (slot < 0) slot = _rng.Next(MaxBells);

            var scale = PickScale(p.SparkleScale);
            int lo = Math.Min(p.SparklePitchLo, p.SparklePitchHi);
            int hi = Math.Max(p.SparklePitchLo, p.SparklePitchHi);
            if (hi < lo + 1) hi = lo + 1;
            int octLo = lo / 12, octHi = hi / 12;
            // Pick oct + degree
            int oct = _rng.Next(octLo, octHi + 1);
            int deg = scale[_rng.Next(scale.Length)];
            int midi = oct * 12 + p.SparkleKey + deg;
            if (midi < lo) midi += 12;
            if (midi > hi) midi -= 12;

            float freq = (float)(440.0 * Math.Pow(2.0, (midi - 69) / 12.0));
            float pan = (float)(_rng.NextDouble() * 2 - 1);   // stereo random
            float amp = 0.6f + (float)_rng.NextDouble() * 0.4f;
            _bells[slot].Trigger(freq, p.SparkleDecayMs, amp, pan, _sr);
        }

        // ==== Bell (3 modes sinus, decroissance exp) ====
        internal sealed class Bell
        {
            public bool Active;
            public double Phase1, Phase2, Phase3;
            public double PhaseInc1, PhaseInc2, PhaseInc3;
            public float Amp1, Amp2, Amp3;
            public float Amp, Decay;
            public float PanL, PanR;

            public void Trigger(float freq, float decayMs, float amp, float pan, int sr)
            {
                Active = true;
                Phase1 = Phase2 = Phase3 = 0;
                Amp = amp;
                Amp1 = 1.0f; Amp2 = 0.5f; Amp3 = 0.25f;
                const float p2 = 2.756f;   // partiel inharmonique de cloche
                const float p3 = 5.404f;
                Decay = (float)Math.Exp(-6.907755278982137 / (decayMs * sr / 1000.0));
                float t = (pan + 1) * 0.25f;
                PanL = (float)Math.Cos(t * Math.PI);
                PanR = (float)Math.Sin(t * Math.PI);
                PhaseInc1 = freq / (double)sr;
                PhaseInc2 = freq * p2 / (double)sr;
                PhaseInc3 = freq * p3 / (double)sr;
            }

            public float Render(int sr)
            {
                if (!Active) return 0f;
                Phase1 += PhaseInc1; if (Phase1 >= 1) Phase1 -= 1;
                Phase2 += PhaseInc2; if (Phase2 >= 1) Phase2 -= 1;
                Phase3 += PhaseInc3; if (Phase3 >= 1) Phase3 -= 1;
                float s = (float)(Math.Sin(Phase1 * 2 * Math.PI) * Amp1
                                + Math.Sin(Phase2 * 2 * Math.PI) * Amp2
                                + Math.Sin(Phase3 * 2 * Math.PI) * Amp3);
                s *= Amp * 0.5f;
                Amp *= Decay;
                if (Amp < 1e-4f) Active = false;
                return s;
            }
        }
    }
}
