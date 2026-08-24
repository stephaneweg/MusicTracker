using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Windows.Controls;
using KotonStudio.Library;

namespace KotonPluginAuroraShimmer
{
    /// <summary>
    /// Aurora Shimmer — LE reverb magique signature. Pitch shift +12 (octave) et/ou +7 (quinte)
    /// injecté DANS le feedback du reverb → nappes qui montent indéfiniment vers l'aigu,
    /// texture "aurore boréale" / cathédrale enchantée / film-score fantasy.
    ///
    /// Ingrédients :
    /// - Long reverb tail (FDN 4 lignes)
    /// - Pitch shifter granulaire (crossfaded windows) +12 sur le signal recyclé
    /// - Optionnel : 2ème pitch shifter +7 mixé
    /// - Low cut sur le shimmer (les basses transposées deviennent boueuses)
    /// - Mix pitch↔dry dans le feedback = pilote la vitesse de "montée" du son
    /// </summary>
    [KotonEffect("Aurora Shimmer", Id = "koton.aurorashimmer", Category = "Reverb", Version = "1.0", Vendor = "Koton Studio")]
    public sealed class AuroraPlugin : IKotonEffect
    {
        public string Id => "koton.aurorashimmer";
        public string DisplayName => "Aurora Shimmer";

        readonly KotonParameter _size       = new KotonParameter("size",       "Size",       0.0, 1.0, 0.75);
        readonly KotonParameter _feedback   = new KotonParameter("feedback",   "Feedback",   0.0, 1.0, 0.72);
        readonly KotonParameter _shimmer    = new KotonParameter("shimmer",    "Shimmer +12", 0.0, 1.0, 0.60);
        readonly KotonParameter _shimmer5   = new KotonParameter("shimmer5",   "Shimmer +7",  0.0, 1.0, 0.20);
        readonly KotonParameter _shimmerLow = new KotonParameter("shimmer_low","Shimmer low cut", 60, 1000, 220, "Hz");
        readonly KotonParameter _highCut    = new KotonParameter("high_cut",   "High cut",   500, 20000, 6500, "Hz");
        readonly KotonParameter _modDepth   = new KotonParameter("mod_depth",  "Mod",        0.0, 1.0, 0.35);
        readonly KotonParameter _width      = new KotonParameter("width",      "Width",      0.0, 1.0, 0.95);
        readonly KotonParameter _mix        = new KotonParameter("mix",        "Mix",        0.0, 1.0, 0.42);
        readonly KotonParameter _outGain    = new KotonParameter("out_gain",   "Output",     -30.0, 6.0, -1.0, "dB");

        readonly List<KotonParameter> _params;
        public IReadOnlyList<KotonParameter> Parameters => _params;
        int _sr;
        AuroraCore _core;

        public AuroraPlugin()
        {
            _params = new List<KotonParameter> { _size, _feedback, _shimmer, _shimmer5, _shimmerLow, _highCut, _modDepth, _width, _mix, _outGain };
        }
        public bool HasEditor => true;
        public UserControl CreateEditor() => new AuroraEditor(this);
        public void Prepare(int sr, int max) { _sr = sr; _core = new AuroraCore(sr); }
        public void Reset() => _core?.Reset();
        public void Process(Span<float> l, Span<float> r)
        {
            if (_core == null) return;
            _core.Process(l, r, new AuroraParams
            {
                Size = (float)_size.Value, Feedback = (float)_feedback.Value,
                Shimmer = (float)_shimmer.Value, Shimmer5 = (float)_shimmer5.Value,
                ShimmerLowHz = (float)_shimmerLow.Value, HighCutHz = (float)_highCut.Value,
                ModDepth = (float)_modDepth.Value, Width = (float)_width.Value,
                Mix = (float)_mix.Value, OutGainDb = (float)_outGain.Value,
            });
        }
        public byte[] SaveState() { try { var d = new Dictionary<string, double>(); foreach (var k in _params) d[k.Id] = k.Value; return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(d)); } catch { return Array.Empty<byte>(); } }
        public void LoadState(byte[] s) { if (s == null || s.Length == 0) return; try { var d = JsonSerializer.Deserialize<Dictionary<string, double>>(Encoding.UTF8.GetString(s)); if (d == null) return; foreach (var k in _params) if (d.TryGetValue(k.Id, out var v)) k.Value = v; } catch { } }
        public void Dispose() { }
        public void SetParam(string id, double v) { foreach (var k in _params) if (k.Id == id) { k.Value = v; return; } }
    }

    internal struct AuroraParams
    {
        public float Size, Feedback, Shimmer, Shimmer5, ShimmerLowHz, HighCutHz, ModDepth, Width, Mix, OutGainDb;
    }

    /// <summary>FDN 4 lignes + pitch shifter granulaire dans le feedback loop.
    /// Le pitch shift octave se fait par granular resampling : buffer circulaire relu 2× plus vite,
    /// cross-fadé pour éviter les clicks. C'est la technique standard des shimmer reverbs (Eno, Valhalla).</summary>
    internal sealed class AuroraCore
    {
        const int N = 4;
        const float MaxSec = 8f;
        const int GrainSamples = 4096;   // ~85 ms @ 48k — assez court pour rester "temps réel", assez long pour ne pas modulariser

        readonly int _sr;
        readonly float[][] _delays;
        readonly int[] _writeIdx = new int[N];
        readonly float[] _lp = new float[N];
        float _lpOutL, _lpOutR, _hpLoop;

        // Pitch shifter octave : buffer accumulé, relu à vitesse ×2 (donc pitch +12), 2 grains cross-fadés.
        readonly float[] _shBuf;
        int _shWrite;
        double _shReadA, _shReadB;
        double _shPhaseAB;   // 0..1 = fondu entre A et B

        // Pitch shifter quinte (facteur 3/2 = pitch +7 semitones ~= 2^(7/12) = 1.4983).
        readonly float[] _sh5Buf;
        int _sh5Write;
        double _sh5ReadA, _sh5ReadB;
        double _sh5PhaseAB;

        double _modPhase;

        static readonly float[] Ratios = { 1.0f, 1.325f, 1.618f, 2.000f };

        public AuroraCore(int sr)
        {
            _sr = sr;
            int maxS = (int)(MaxSec * sr);
            _delays = new float[N][];
            for (int i = 0; i < N; i++) _delays[i] = new float[maxS];
            _shBuf = new float[GrainSamples * 4];
            _sh5Buf = new float[GrainSamples * 4];
        }
        public void Reset()
        {
            for (int i = 0; i < N; i++) { Array.Clear(_delays[i], 0, _delays[i].Length); _writeIdx[i] = 0; _lp[i] = 0; }
            Array.Clear(_shBuf, 0, _shBuf.Length);
            Array.Clear(_sh5Buf, 0, _sh5Buf.Length);
            _shWrite = 0; _shReadA = 0; _shReadB = GrainSamples;
            _sh5Write = 0; _sh5ReadA = 0; _sh5ReadB = GrainSamples;
            _shPhaseAB = 0; _sh5PhaseAB = 0.5;
            _lpOutL = _lpOutR = _hpLoop = 0;
        }
        public void Process(Span<float> left, Span<float> right, AuroraParams p)
        {
            float baseMs = 40f + 400f * p.Size;
            float baseS = baseMs * _sr / 1000f;
            var lens = new float[N];
            for (int i = 0; i < N; i++) lens[i] = baseS * Ratios[i];
            float g = 0.5f + 0.495f * p.Feedback;
            float lpCoef = 1f - (float)Math.Exp(-2.0 * Math.PI * p.HighCutHz / _sr);
            float lpLoop = lpCoef * 0.9f;
            float hpCoef = 1f - (float)Math.Exp(-2.0 * Math.PI * p.ShimmerLowHz / _sr);
            float modAmp = p.ModDepth * 30f;   // ±30 samples max
            double modInc = 2.0 * Math.PI * 0.3 / _sr;   // 0.3 Hz
            float mix = p.Mix, dryG = 1f - mix, wetG = mix;
            float outLin = (float)Math.Pow(10.0, p.OutGainDb / 20.0);

            for (int n = 0; n < left.Length; n++)
            {
                float inL = left[n], inR = right[n];
                float inMono = 0.5f * (inL + inR);

                // Modulation LFO commune
                double lfo = Math.Sin(_modPhase);
                _modPhase += modInc; if (_modPhase > 2 * Math.PI) _modPhase -= 2 * Math.PI;

                // Read 4 delays avec petit modulation
                var read = new float[N];
                for (int i = 0; i < N; i++)
                {
                    float len = lens[i] + (float)lfo * modAmp * (0.7f + 0.3f * i);
                    if (len < 8) len = 8; if (len > _delays[i].Length - 4) len = _delays[i].Length - 4;
                    int li = (int)len; float f = len - li;
                    int r0 = _writeIdx[i] - li; if (r0 < 0) r0 += _delays[i].Length;
                    int r1 = r0 - 1; if (r1 < 0) r1 += _delays[i].Length;
                    read[i] = _delays[i][r0] * (1f - f) + _delays[i][r1] * f;
                    _lp[i] += lpLoop * (read[i] - _lp[i]);
                    read[i] = _lp[i];
                }

                // Somme du feedback (mix Householder)
                float sum = 0f; for (int i = 0; i < N; i++) sum += read[i];
                float hMix = 2f / N;

                // Extrait le "wet" pour le pitch shift : moyenne des lignes
                float wet = sum * 0.25f;

                // High-pass sur ce qui va être pitché (les basses gigotées deviennent boueuses)
                float hpDelta = wet - _hpLoop;
                _hpLoop += hpCoef * hpDelta;
                float hpForShift = wet - _hpLoop;

                // ---- PITCH SHIFT +12 (granular resampling 2×) ----
                _shBuf[_shWrite] = hpForShift;
                _shWrite++; if (_shWrite >= _shBuf.Length) _shWrite = 0;
                _shReadA += 2.0; _shReadB += 2.0;
                if (_shReadA >= _shBuf.Length) _shReadA -= _shBuf.Length;
                if (_shReadB >= _shBuf.Length) _shReadB -= _shBuf.Length;
                int rA = (int)_shReadA; int rB = (int)_shReadB;
                float sA = _shBuf[rA]; float sB = _shBuf[rB];
                // Crossfade : phase augmente en fonction de la position dans le grain
                double distA = ((_shReadA - _shWrite + _shBuf.Length) % _shBuf.Length) / GrainSamples;
                if (distA > 1) distA = 1;
                float w = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * distA));   // Hann window
                float shimmerOut = sA * w + sB * (1f - w);

                // ---- PITCH SHIFT +7 (facteur 1.498) ----
                _sh5Buf[_sh5Write] = hpForShift;
                _sh5Write++; if (_sh5Write >= _sh5Buf.Length) _sh5Write = 0;
                _sh5ReadA += 1.4983; _sh5ReadB += 1.4983;
                if (_sh5ReadA >= _sh5Buf.Length) _sh5ReadA -= _sh5Buf.Length;
                if (_sh5ReadB >= _sh5Buf.Length) _sh5ReadB -= _sh5Buf.Length;
                int r5A = (int)_sh5ReadA; int r5B = (int)_sh5ReadB;
                double dist5 = ((_sh5ReadA - _sh5Write + _sh5Buf.Length) % _sh5Buf.Length) / GrainSamples;
                if (dist5 > 1) dist5 = 1;
                float w5 = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * dist5));
                float shimmer5Out = _sh5Buf[r5A] * w5 + _sh5Buf[r5B] * (1f - w5);

                // Injection dans le feedback : le "wet" mixé aux 2 pitch shifts recycle dans le FDN
                float injected = wet * (1f - p.Shimmer - p.Shimmer5) + shimmerOut * p.Shimmer + shimmer5Out * p.Shimmer5;

                // Write back dans les delays avec matrice Householder (mixage entre lignes)
                for (int i = 0; i < N; i++)
                {
                    float house = read[i] - hMix * sum;
                    float mixed = house;   // pas de density paramétrable ici, on reste dense
                    float newIn = inMono + (mixed + injected * 0.5f) * g;
                    _delays[i][_writeIdx[i]] = newIn;
                    _writeIdx[i]++; if (_writeIdx[i] >= _delays[i].Length) _writeIdx[i] = 0;
                }

                // Sortie mid/side stereo
                float mid = 0f, side = 0f;
                for (int i = 0; i < N; i++) if ((i & 1) == 0) mid += read[i]; else side += read[i];
                mid *= (2f / N); side *= (2f / N);
                float wL = mid + side * p.Width;
                float wR = mid - side * p.Width;
                _lpOutL += lpCoef * (wL - _lpOutL);
                _lpOutR += lpCoef * (wR - _lpOutR);
                float outL = (dryG * inL + wetG * _lpOutL) * outLin;
                float outR = (dryG * inR + wetG * _lpOutR) * outLin;
                if (outL > 1) outL = 1; else if (outL < -1) outL = -1;
                if (outR > 1) outR = 1; else if (outR < -1) outR = -1;
                left[n] = outL; right[n] = outR;
            }
        }
    }
}
