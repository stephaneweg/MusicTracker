using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Windows.Controls;
using KotonStudio.Library;

namespace KotonPluginSpace
{
    /// <summary>
    /// Space Cosmic — l'inverse d'Ocean Abyss : reverb longue avec pitch-shifter OCTAVE DOWN
    /// dans la boucle de feedback (au lieu de +12 shimmer). Le signal s'enfonce progressivement
    /// dans les graves, spectre qui se remplit vers le sub, aspect trou noir / infini cosmique.
    ///
    /// **DSP** : FDN 4x4 très long + pitch shifter à vitesse 0.5x (octave down) sur le tap injecté
    /// dans la boucle + LP variable (Coldness) + très large stéréo décorrélé. Optionnel : star
    /// twinkle = petits pics aléatoires sinusoïdaux aigus (les étoiles qui scintillent) mixés
    /// selon Twinkle.
    /// </summary>
    [KotonEffect("Space Cosmic", Id = "koton.space", Category = "Reverb", Version = "1.0", Vendor = "Koton Studio")]
    public sealed class SpacePlugin : IKotonEffect
    {
        public string Id => "koton.space";
        public string DisplayName => "Space Cosmic";

        readonly KotonParameter _size       = new KotonParameter("size",         "Size",         0.0, 1.0, 0.85);
        readonly KotonParameter _decay      = new KotonParameter("decay",        "Decay",        0.0, 1.0, 0.88);
        readonly KotonParameter _octaveDown = new KotonParameter("octave_down",  "Octave down",  0.0, 1.0, 0.55);
        readonly KotonParameter _coldness   = new KotonParameter("coldness",     "Coldness",     0.0, 1.0, 0.65);
        readonly KotonParameter _twinkle    = new KotonParameter("twinkle",      "Twinkle",      0.0, 1.0, 0.25);
        readonly KotonParameter _preDelay   = new KotonParameter("pre_delay",    "Pre-delay",    0.0, 300.0, 50.0, "ms");
        readonly KotonParameter _stereoWidth= new KotonParameter("stereo_width", "Stereo width", 0.0, 1.0, 1.00);
        readonly KotonParameter _mix        = new KotonParameter("mix",          "Mix",          0.0, 1.0, 0.60);
        readonly KotonParameter _outGain    = new KotonParameter("out_gain",     "Output",       -30.0, 6.0, -3.0, "dB");

        readonly List<KotonParameter> _params;
        public IReadOnlyList<KotonParameter> Parameters => _params;

        int _sr;
        // FDN 4x4 long
        float[][] _fdnLines;
        int[] _fdnIdx;
        int[] _fdnBase;
        int[] _fdnMax;
        float[] _lpState;
        // Pré-delay
        float[] _preL, _preR;
        int _preIdx, _preMax;
        // Pitch shifter octave down : lit à 0.5× vitesse
        float[] _pitchBufL, _pitchBufR;
        int _pitchWrite;
        float _pitchRead1, _pitchRead2;   // 2 grains avec crossfade
        // Twinkle : événements aléatoires
        Random _rng = new Random(31);
        int _twinklePeriod;
        float _twinkleAmp, _twinklePhase, _twinklePhaseInc;

        static readonly float[,] Hadamard4x4 = new float[4, 4] {
            {  0.5f,  0.5f,  0.5f,  0.5f },
            {  0.5f, -0.5f,  0.5f, -0.5f },
            {  0.5f,  0.5f, -0.5f, -0.5f },
            {  0.5f, -0.5f, -0.5f,  0.5f }
        };

        public SpacePlugin()
        {
            _params = new List<KotonParameter> { _size, _decay, _octaveDown, _coldness, _twinkle, _preDelay, _stereoWidth, _mix, _outGain };
        }

        public bool HasEditor => true;
        public UserControl CreateEditor() => new SpaceEditor(this);

        public void Prepare(int sampleRate, int maxBlockSize)
        {
            _sr = sampleRate;
            _preMax = (int)(sampleRate * 0.3);
            _preL = new float[_preMax]; _preR = new float[_preMax];

            var baseMs = new float[] { 173f, 251f, 349f, 479f };
            _fdnLines = new float[4][]; _fdnIdx = new int[4]; _fdnBase = new int[4]; _fdnMax = new int[4];
            _lpState = new float[4];
            for (int i = 0; i < 4; i++)
            {
                _fdnMax[i] = (int)(baseMs[i] * 2f * sampleRate / 1000f);
                _fdnLines[i] = new float[_fdnMax[i]];
                _fdnBase[i] = (int)(baseMs[i] * sampleRate / 1000f);
            }
            // Pitch shifter buffer
            _pitchBufL = new float[sampleRate];
            _pitchBufR = new float[sampleRate];
        }
        public void Reset()
        {
            for (int i = 0; i < 4; i++) { Array.Clear(_fdnLines[i], 0, _fdnLines[i].Length); _fdnIdx[i] = 0; _lpState[i] = 0f; }
            Array.Clear(_preL, 0, _preMax); Array.Clear(_preR, 0, _preMax); _preIdx = 0;
            Array.Clear(_pitchBufL, 0, _pitchBufL.Length); Array.Clear(_pitchBufR, 0, _pitchBufR.Length);
            _pitchWrite = 0; _pitchRead1 = 0f; _pitchRead2 = _sr * 0.05f;
        }

        public void Process(Span<float> left, Span<float> right)
        {
            if (_fdnLines == null) return;
            float mix = (float)_mix.Value;
            float outLin = (float)Math.Pow(10.0, _outGain.Value / 20.0);
            float width = (float)_stereoWidth.Value;
            float feedback = 0.75f + (float)_decay.Value * 0.23f;
            float sizeMul = 0.5f + (float)_size.Value * 1.4f;
            float octaveMix = (float)_octaveDown.Value;
            float coldness = (float)_coldness.Value;
            float twinkle = (float)_twinkle.Value;
            int preSamples = Math.Min(_preMax - 1, (int)(_preDelay.Value * _sr / 1000f));
            float lpCutoff = 8000f - coldness * 7500f;
            float lpAlpha = 1f - (float)Math.Exp(-2.0 * Math.PI * lpCutoff / _sr);

            int n = left.Length;
            for (int i = 0; i < n; i++)
            {
                float inL = left[i], inR = right[i];

                // Pré-delay
                _preL[_preIdx] = inL; _preR[_preIdx] = inR;
                int preRead = _preIdx - preSamples;
                if (preRead < 0) preRead += _preMax;
                float pdL = _preL[preRead]; float pdR = _preR[preRead];
                _preIdx++; if (_preIdx >= _preMax) _preIdx = 0;

                // FDN taps
                float[] taps = new float[4];
                for (int line = 0; line < 4; line++)
                {
                    int len = (int)(_fdnBase[line] * sizeMul);
                    if (len < 4) len = 4;
                    if (len >= _fdnMax[line]) len = _fdnMax[line] - 1;
                    int r = _fdnIdx[line] - len;
                    while (r < 0) r += _fdnMax[line];
                    taps[line] = _fdnLines[line][r];
                }
                // LP feedback (coldness)
                for (int line = 0; line < 4; line++) { _lpState[line] += lpAlpha * (taps[line] - _lpState[line]); taps[line] = _lpState[line]; }

                // Pitch shifter octave DOWN sur la somme des taps
                float tapSum = (taps[0] + taps[1] + taps[2] + taps[3]) * 0.25f;
                _pitchBufL[_pitchWrite] = tapSum;
                _pitchBufR[_pitchWrite] = tapSum;
                _pitchWrite++; if (_pitchWrite >= _pitchBufL.Length) _pitchWrite = 0;
                // 2 grains lus à vitesse 0.5x (octave down) avec crossfade
                _pitchRead1 += 0.5f; _pitchRead2 += 0.5f;
                float grainSize = _sr * 0.08f;
                float fadeSize = grainSize * 0.3f;
                if (_pitchRead1 >= grainSize) _pitchRead1 -= grainSize;
                if (_pitchRead2 >= grainSize) _pitchRead2 -= grainSize;
                float pos1 = _pitchWrite - _pitchRead1;
                float pos2 = _pitchWrite - _pitchRead2;
                while (pos1 < 0) pos1 += _pitchBufL.Length;
                while (pos2 < 0) pos2 += _pitchBufL.Length;
                float p1L = ReadFrac(_pitchBufL, pos1); float p2L = ReadFrac(_pitchBufL, pos2);
                float g1 = _pitchRead1 < fadeSize ? _pitchRead1 / fadeSize : _pitchRead1 > grainSize - fadeSize ? (grainSize - _pitchRead1) / fadeSize : 1f;
                float g2 = _pitchRead2 < fadeSize ? _pitchRead2 / fadeSize : _pitchRead2 > grainSize - fadeSize ? (grainSize - _pitchRead2) / fadeSize : 1f;
                float pitchOut = (p1L * g1 + p2L * g2);
                // Injection dans FDN : diffusion + pitch down feedback
                float pitchInj = pitchOut * octaveMix * 0.6f;
                float in0 = pdL + pitchInj;
                float in1 = pdR + pitchInj;
                float in2 = -pdL - pitchInj;
                float in3 = -pdR - pitchInj;

                // Hadamard mix
                float m0 = Hadamard4x4[0,0]*taps[0] + Hadamard4x4[0,1]*taps[1] + Hadamard4x4[0,2]*taps[2] + Hadamard4x4[0,3]*taps[3];
                float m1 = Hadamard4x4[1,0]*taps[0] + Hadamard4x4[1,1]*taps[1] + Hadamard4x4[1,2]*taps[2] + Hadamard4x4[1,3]*taps[3];
                float m2 = Hadamard4x4[2,0]*taps[0] + Hadamard4x4[2,1]*taps[1] + Hadamard4x4[2,2]*taps[2] + Hadamard4x4[2,3]*taps[3];
                float m3 = Hadamard4x4[3,0]*taps[0] + Hadamard4x4[3,1]*taps[1] + Hadamard4x4[3,2]*taps[2] + Hadamard4x4[3,3]*taps[3];

                _fdnLines[0][_fdnIdx[0]] = in0 + m0 * feedback;
                _fdnLines[1][_fdnIdx[1]] = in1 + m1 * feedback;
                _fdnLines[2][_fdnIdx[2]] = in2 + m2 * feedback;
                _fdnLines[3][_fdnIdx[3]] = in3 + m3 * feedback;
                for (int k = 0; k < 4; k++) { _fdnIdx[k]++; if (_fdnIdx[k] >= _fdnMax[k]) _fdnIdx[k] = 0; }

                float wetL = (taps[0] + taps[2]) * 0.5f;
                float wetR = (taps[1] + taps[3]) * 0.5f;

                // Twinkle : événements sinus aléatoires haut fréquence
                _twinklePeriod--;
                if (_twinklePeriod <= 0 && twinkle > 0.01f)
                {
                    _twinklePeriod = (int)(_sr * (0.5 + _rng.NextDouble() * 3.0) / (twinkle + 0.1));
                    _twinkleAmp = (float)_rng.NextDouble() * 0.15f * twinkle;
                    float twinkleFreq = 3000f + (float)_rng.NextDouble() * 4000f;
                    _twinklePhaseInc = (float)(2 * Math.PI * twinkleFreq / _sr);
                    _twinklePhase = 0f;
                }
                if (_twinkleAmp > 1e-4f)
                {
                    _twinklePhase += _twinklePhaseInc;
                    float t = (float)Math.Sin(_twinklePhase) * _twinkleAmp;
                    _twinkleAmp *= 0.9995f;   // fade court
                    wetL += t; wetR += t * 0.7f;
                }

                // Width mid-side
                float mid = (wetL + wetR) * 0.5f; float side = wetL - wetR;
                wetL = mid + side * width; wetR = mid - side * width;

                left[i]  = (inL * (1f - mix) + wetL * mix) * outLin;
                right[i] = (inR * (1f - mix) + wetR * mix) * outLin;
            }
        }

        static float ReadFrac(float[] buf, float pos)
        {
            while (pos < 0) pos += buf.Length;
            while (pos >= buf.Length) pos -= buf.Length;
            int i0 = (int)pos; int i1 = (i0 + 1) % buf.Length;
            float f = pos - i0;
            return buf[i0] * (1 - f) + buf[i1] * f;
        }

        public byte[] SaveState()
        {
            try { var d = new Dictionary<string, double>(); foreach (var kp in _params) d[kp.Id] = kp.Value; return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(d)); } catch { return Array.Empty<byte>(); }
        }
        public void LoadState(byte[] state)
        {
            if (state == null || state.Length == 0) return;
            try { var d = JsonSerializer.Deserialize<Dictionary<string, double>>(Encoding.UTF8.GetString(state)); if (d == null) return; foreach (var kp in _params) if (d.TryGetValue(kp.Id, out var v)) kp.Value = v; } catch { }
        }
        public void Dispose() { }

        public static readonly string[] PresetNames = { "Vaisseau spatial", "Nebuleuse", "Trou noir (drone)", "Space station humming", "Etoiles lointaines" };
        static readonly double[,] PresetValues = {
            //          size decay oct  cold twk  pre  wid mix  out
            /*Vaiss*/   { 0.60, 0.75, 0.35, 0.55, 0.30, 40,  1.00, 0.55, -3.0 },
            /*Nebul*/   { 0.85, 0.90, 0.55, 0.65, 0.55, 80,  1.00, 0.70, -3.0 },
            /*Trou n*/  { 1.00, 0.98, 0.85, 0.85, 0.10, 20,  1.00, 0.85, -4.0 },
            /*Statio*/  { 0.65, 0.80, 0.25, 0.75, 0.20, 60,  0.85, 0.60, -2.0 },
            /*Etoiles*/ { 0.80, 0.88, 0.40, 0.50, 0.80, 100, 1.00, 0.65, -3.0 },
        };
        public void LoadPreset(int idx, bool keepMix)
        {
            if (idx < 0 || idx >= PresetValues.GetLength(0)) return;
            double km = _mix.Value;
            _size.Value = PresetValues[idx, 0]; _decay.Value = PresetValues[idx, 1]; _octaveDown.Value = PresetValues[idx, 2];
            _coldness.Value = PresetValues[idx, 3]; _twinkle.Value = PresetValues[idx, 4]; _preDelay.Value = PresetValues[idx, 5];
            _stereoWidth.Value = PresetValues[idx, 6]; _mix.Value = keepMix ? km : PresetValues[idx, 7]; _outGain.Value = PresetValues[idx, 8];
        }
        public void SetParam(string id, double value)
        {
            foreach (var kp in _params) if (kp.Id == id) { kp.Value = value; return; }
        }
    }
}
