using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Windows.Controls;
using KotonStudio.Library;

namespace KotonPluginFreeze
{
    /// <summary>
    /// Freeze / Hold — capture un instantané audio (~1 seconde) et le tient à l'infini par lecture
    /// en boucle avec crossfade. Bouton "Capture" (le param Capture) : posé à 1, l'effet capture
    /// le prochain second de signal et le rejoue en boucle jusqu'à ce que l'user re-capture ou
    /// désactive le freeze. Signal source passe en dry pendant le freeze (option).
    ///
    /// **DSP** : buffer circulaire de ~1s. Deux têtes de lecture décalées (grain size = 1s, fade
    /// 200ms) qui bouclent avec crossfade → aucune coupure audible au wrap-around. Optionnel :
    /// modulation Tone (LP variable pour ajuster la couleur du pad tenu).
    /// </summary>
    [KotonEffect("Freeze", Id = "koton.freeze", Category = "Creative", Version = "1.0", Vendor = "Koton Studio")]
    public sealed class FreezePlugin : IKotonEffect
    {
        public string Id => "koton.freeze";
        public string DisplayName => "Freeze";

        readonly KotonParameter _capture     = new KotonParameter("capture",      "Capture (armer)", 0, 1, 0);
        readonly KotonParameter _freezeMode  = new KotonParameter("freeze_mode",  "Freeze",          0, 1, 0);
        readonly KotonParameter _tone        = new KotonParameter("tone",         "Tone",            0.0, 1.0, 0.60);
        readonly KotonParameter _stereoWidth = new KotonParameter("stereo_width", "Stereo width",    0.0, 1.0, 0.80);
        readonly KotonParameter _mix         = new KotonParameter("mix",          "Mix",             0.0, 1.0, 0.75);
        readonly KotonParameter _outGain     = new KotonParameter("out_gain",     "Output",          -30.0, 6.0, 0.0, "dB");

        readonly List<KotonParameter> _params;
        public IReadOnlyList<KotonParameter> Parameters => _params;

        int _sr;
        float[] _bufL, _bufR;
        int _bufSize;
        int _writeIdx;
        int _captureSamples;
        bool _isCapturing;
        bool _hasFrozen;
        // 2 têtes de lecture avec crossfade
        float _readPos1, _readPos2;
        float _grainSize;
        float _fadeSize;
        float _lpStateL, _lpStateR;

        public FreezePlugin()
        {
            _params = new List<KotonParameter> { _capture, _freezeMode, _tone, _stereoWidth, _mix, _outGain };
        }

        public bool HasEditor => true;
        public UserControl CreateEditor() => new FreezeEditor(this);

        public void Prepare(int sampleRate, int maxBlockSize)
        {
            _sr = sampleRate;
            _bufSize = sampleRate;   // 1 seconde
            _bufL = new float[_bufSize];
            _bufR = new float[_bufSize];
            _grainSize = sampleRate;
            _fadeSize = sampleRate * 0.2f;
            _readPos2 = _grainSize * 0.5f;   // départ crossfade au milieu
        }
        public void Reset()
        {
            if (_bufL != null) { Array.Clear(_bufL, 0, _bufSize); Array.Clear(_bufR, 0, _bufSize); }
            _writeIdx = 0;
            _isCapturing = false;
            _hasFrozen = false;
            _captureSamples = 0;
            _readPos1 = 0f; _readPos2 = _grainSize * 0.5f;
            _lpStateL = _lpStateR = 0f;
        }

        public void Process(Span<float> left, Span<float> right)
        {
            if (_bufL == null) return;

            // Détection edge sur Capture (0 → 1) : arme la capture
            bool captureArmed = _capture.Value >= 0.5;
            if (captureArmed && !_isCapturing)
            {
                // Démarre la capture
                _isCapturing = true;
                _captureSamples = 0;
                _hasFrozen = false;
            }
            if (!captureArmed) _isCapturing = false;

            bool freeze = _freezeMode.Value >= 0.5;
            float tone = (float)_tone.Value;
            float width = (float)_stereoWidth.Value;
            float mix = (float)_mix.Value;
            float outLin = (float)Math.Pow(10.0, _outGain.Value / 20.0);

            float lpCutoff = 300f + tone * 7700f;
            float lpAlpha = 1f - (float)Math.Exp(-2.0 * Math.PI * lpCutoff / _sr);

            int n = left.Length;
            for (int i = 0; i < n; i++)
            {
                float inL = left[i], inR = right[i];

                // Écrit dans le buffer si on capture
                if (_isCapturing && _captureSamples < _bufSize)
                {
                    _bufL[_captureSamples] = inL;
                    _bufR[_captureSamples] = inR;
                    _captureSamples++;
                    if (_captureSamples >= _bufSize)
                    {
                        _isCapturing = false;
                        _hasFrozen = true;
                        _readPos1 = 0f; _readPos2 = _grainSize * 0.5f;
                    }
                }

                float wetL = inL, wetR = inR;
                if (_hasFrozen && freeze)
                {
                    // Lecture 2 grains avec crossfade
                    _readPos1 += 1f;
                    _readPos2 += 1f;
                    if (_readPos1 >= _grainSize) _readPos1 -= _grainSize;
                    if (_readPos2 >= _grainSize) _readPos2 -= _grainSize;

                    float g1 = _readPos1 < _fadeSize ? _readPos1 / _fadeSize :
                               _readPos1 > _grainSize - _fadeSize ? (_grainSize - _readPos1) / _fadeSize : 1f;
                    float g2 = _readPos2 < _fadeSize ? _readPos2 / _fadeSize :
                               _readPos2 > _grainSize - _fadeSize ? (_grainSize - _readPos2) / _fadeSize : 1f;
                    int i1 = (int)_readPos1 % _bufSize;
                    int i2 = (int)_readPos2 % _bufSize;
                    wetL = _bufL[i1] * g1 + _bufL[i2] * g2;
                    wetR = _bufR[i1] * g1 + _bufR[i2] * g2;

                    // Tone LP
                    _lpStateL += lpAlpha * (wetL - _lpStateL);
                    _lpStateR += lpAlpha * (wetR - _lpStateR);
                    wetL = _lpStateL;
                    wetR = _lpStateR;

                    // Width mid/side
                    float mid = (wetL + wetR) * 0.5f;
                    float side = wetL - wetR;
                    wetL = mid + side * width;
                    wetR = mid - side * width;
                }

                left[i]  = (inL * (1f - mix) + wetL * mix) * outLin;
                right[i] = (inR * (1f - mix) + wetR * mix) * outLin;
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

        public void SetParam(string id, double value)
        {
            foreach (var kp in _params) if (kp.Id == id) { kp.Value = value; return; }
        }
    }
}
