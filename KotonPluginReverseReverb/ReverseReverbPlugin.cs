using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Windows.Controls;
using KotonStudio.Library;

namespace KotonPluginReverseReverb
{
    /// <summary>
    /// Reverse Reverb — le fameux "swoosh" cinéma qui précède un accent. On accumule le signal
    /// dans un buffer + on le rejoue à l'envers avec une envelope qui monte (au lieu de descendre
    /// comme une reverb normale), puis on laisse le son sec passer à l'accent.
    ///
    /// **DSP** : buffer circulaire de la longueur de reverse (0.5..3s). À chaque sample d'input,
    /// on l'écrit dans le buffer. À chaque sample de sortie, on lit à l'envers dans le buffer
    /// (position = writeIdx - progressCounter). Multiplication par une envelope montante
    /// (triangle inverse : t=0 → gain 0, t=length → gain 1). Diffusion optionnelle via all-pass
    /// pour douceur (Smoothness).
    ///
    /// **Astuce** : l'effet fonctionne mieux en MIX modéré (30-50%) où le dry passe à travers
    /// pendant que le swoosh gonfle et culmine juste avant l'accent original.
    /// </summary>
    [KotonEffect("Reverse Reverb", Id = "koton.reverseverb", Category = "Creative", Version = "1.0", Vendor = "Koton Studio")]
    public sealed class ReverseReverbPlugin : IKotonEffect
    {
        public string Id => "koton.reverseverb";
        public string DisplayName => "Reverse Reverb";

        readonly KotonParameter _lengthSec   = new KotonParameter("length",       "Length",       0.2, 3.0, 1.20, "s");
        readonly KotonParameter _smoothness  = new KotonParameter("smoothness",   "Smoothness",   0.0, 1.0, 0.65);
        readonly KotonParameter _brightness  = new KotonParameter("brightness",   "Brightness",   0.0, 1.0, 0.55);
        readonly KotonParameter _stereoWidth = new KotonParameter("stereo_width", "Stereo width", 0.0, 1.0, 0.85);
        readonly KotonParameter _mix         = new KotonParameter("mix",          "Mix",          0.0, 1.0, 0.45);
        readonly KotonParameter _outGain     = new KotonParameter("out_gain",     "Output",       -30.0, 6.0, 0.0, "dB");

        readonly List<KotonParameter> _params;
        public IReadOnlyList<KotonParameter> Parameters => _params;

        int _sr;
        float[] _bufL, _bufR;
        int _bufSize;
        int _writeIdx;
        // Diffusion : 4 all-pass série pour lisser le reverse
        AllPassStage[] _diff;
        float _lpStateL, _lpStateR;

        public ReverseReverbPlugin()
        {
            _params = new List<KotonParameter> { _lengthSec, _smoothness, _brightness, _stereoWidth, _mix, _outGain };
        }

        public bool HasEditor => true;
        public UserControl CreateEditor() => new ReverseReverbEditor(this);

        public void Prepare(int sampleRate, int maxBlockSize)
        {
            _sr = sampleRate;
            _bufSize = sampleRate * 4;   // 4 sec max
            _bufL = new float[_bufSize];
            _bufR = new float[_bufSize];
            var apLenMs = new float[] { 4.1f, 7.3f, 11.9f, 17.7f };
            _diff = new AllPassStage[apLenMs.Length];
            for (int i = 0; i < apLenMs.Length; i++)
                _diff[i] = new AllPassStage((int)(apLenMs[i] * sampleRate / 1000f), 0.7f);
        }
        public void Reset()
        {
            if (_bufL != null) { Array.Clear(_bufL, 0, _bufSize); Array.Clear(_bufR, 0, _bufSize); }
            _writeIdx = 0;
            if (_diff != null) foreach (var a in _diff) a.Reset();
            _lpStateL = _lpStateR = 0f;
        }

        public void Process(Span<float> left, Span<float> right)
        {
            if (_bufL == null) return;

            int lenSamples = Math.Min(_bufSize - 1, (int)(_lengthSec.Value * _sr));
            float smoothness = (float)_smoothness.Value;
            float brightness = (float)_brightness.Value;
            float width = (float)_stereoWidth.Value;
            float mix = (float)_mix.Value;
            float outLin = (float)Math.Pow(10.0, _outGain.Value / 20.0);

            float lpCutoff = 500f + brightness * 7500f;
            float lpAlpha = 1f - (float)Math.Exp(-2.0 * Math.PI * lpCutoff / _sr);

            int n = left.Length;
            for (int i = 0; i < n; i++)
            {
                float dryL = left[i], dryR = right[i];

                // Écrit l'input dans le buffer
                _bufL[_writeIdx] = dryL;
                _bufR[_writeIdx] = dryR;

                // Lecture REVERSE : lit à writeIdx en arrière, avec un offset qui parcourt lenSamples
                // Chaque sample sortant t correspond à l'input au sample t + lenSamples plus tôt
                // MAIS on lit en direction OPPOSÉE au writeIdx pour l'effet reverse.
                // Simplification : on prend la "queue" du buffer et on la lit dans l'ordre inverse
                // en synchronisation avec l'écriture.

                // Reverse read : lit à index = writeIdx - (writeIdx % lenSamples inverse)
                // Formule : à chaque sample, on regarde ou on est dans le cycle courant [0, lenSamples[,
                // et on lit à writeIdx - (lenSamples - phase - 1)
                int phase = _writeIdx % lenSamples;
                int reverseOffset = lenSamples - phase - 1;
                int readIdx = _writeIdx - reverseOffset;
                while (readIdx < 0) readIdx += _bufSize;
                readIdx %= _bufSize;

                float rL = _bufL[readIdx];
                float rR = _bufR[readIdx];

                // Envelope montante : phase 0 = début du cycle reverse (silencieux), phase lenSamples-1 = pic
                float env = (float)phase / lenSamples;
                // Courbe : x^2 pour un swoosh plus doux au début, punch à la fin
                env = env * env;

                rL *= env;
                rR *= env;

                // Diffusion : 4 all-pass série pour lisser (piloté par Smoothness)
                if (smoothness > 0.01f)
                {
                    float smL = rL, smR = rR;
                    for (int a = 0; a < _diff.Length; a++)
                    {
                        smL = _diff[a].ProcessL(smL);
                        smR = _diff[a].ProcessR(smR);
                    }
                    rL = rL * (1f - smoothness) + smL * smoothness;
                    rR = rR * (1f - smoothness) + smR * smoothness;
                }

                // Brightness LP
                _lpStateL += lpAlpha * (rL - _lpStateL);
                _lpStateR += lpAlpha * (rR - _lpStateR);
                float wetL = _lpStateL;
                float wetR = _lpStateR;

                // Width mid/side
                float mid = (wetL + wetR) * 0.5f;
                float side = wetL - wetR;
                wetL = mid + side * width;
                wetR = mid - side * width;

                _writeIdx++;
                if (_writeIdx >= _bufSize) _writeIdx = 0;

                left[i]  = (dryL * (1f - mix) + wetL * mix) * outLin;
                right[i] = (dryR * (1f - mix) + wetR * mix) * outLin;
            }
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

        public static readonly string[] PresetNames = { "Court (0.5s)", "Cinema (1.2s)", "Long (2.5s)", "Onirique (3s doux)" };
        static readonly double[,] PresetValues = {
            //         len  smth brgh wid  mix  out
            /*Court*/  { 0.50, 0.55, 0.65, 0.75, 0.40, 0.0 },
            /*Cine*/   { 1.20, 0.70, 0.55, 0.85, 0.50, 0.0 },
            /*Long*/   { 2.50, 0.75, 0.45, 0.90, 0.55, 0.0 },
            /*Onir*/   { 3.00, 0.90, 0.35, 1.00, 0.60, -2.0 },
        };
        public void LoadPreset(int idx)
        {
            if (idx < 0 || idx >= PresetValues.GetLength(0)) return;
            _lengthSec.Value = PresetValues[idx, 0]; _smoothness.Value = PresetValues[idx, 1];
            _brightness.Value = PresetValues[idx, 2]; _stereoWidth.Value = PresetValues[idx, 3];
            _mix.Value = PresetValues[idx, 4]; _outGain.Value = PresetValues[idx, 5];
        }
        public void SetParam(string id, double value)
        {
            foreach (var kp in _params) if (kp.Id == id) { kp.Value = value; return; }
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
            _bufL = new float[_size]; _bufR = new float[_size];
        }
        public void Reset() { Array.Clear(_bufL, 0, _size); Array.Clear(_bufR, 0, _size); _idxL = _idxR = 0; }
        public float ProcessL(float x) { float d = _bufL[_idxL]; float y = -_coef * x + d; _bufL[_idxL] = x + _coef * y; _idxL++; if (_idxL >= _size) _idxL = 0; return y; }
        public float ProcessR(float x) { float d = _bufR[_idxR]; float y = -_coef * x + d; _bufR[_idxR] = x + _coef * y; _idxR++; if (_idxR >= _size) _idxR = 0; return y; }
    }
}
