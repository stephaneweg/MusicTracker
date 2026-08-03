using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Windows.Controls;
using KotonStudio.Library;

namespace KotonPluginTapeDelay
{
    /// <summary>
    /// Tape Delay — délai avec la chaleur d'une bande magnétique vintage : wow (LFO lent sur la
    /// position de lecture qui simule l'imperfection du moteur), flutter (LFO rapide qui ajoute
    /// des micro-variations irrégulières), saturation tanh sur chaque répétition, et dégradation
    /// HF progressive (chaque écho perd des aigus).
    ///
    /// **DSP** : buffer circulaire + lecture fractionnaire à position = writeIdx - (delayMs + wow_lfo + flutter_lfo).
    /// Feedback avec saturation tanh + LP appliqué sur le tap réinjecté = les échos deviennent de
    /// plus en plus sombres et compressés au fil des répétitions. Optionnel : ping-pong stéréo.
    /// </summary>
    [KotonEffect("Tape Delay", Id = "koton.tapedelay", Category = "Delay", Version = "1.0", Vendor = "Koton Studio")]
    public sealed class TapeDelayPlugin : IKotonEffect
    {
        public string Id => "koton.tapedelay";
        public string DisplayName => "Tape Delay";

        readonly KotonParameter _timeMs      = new KotonParameter("time",         "Time",         30.0, 1500.0, 350.0, "ms");
        readonly KotonParameter _feedback    = new KotonParameter("feedback",     "Feedback",     0.0, 0.95, 0.55);
        readonly KotonParameter _wow         = new KotonParameter("wow",          "Wow",          0.0, 1.0, 0.35);
        readonly KotonParameter _flutter     = new KotonParameter("flutter",      "Flutter",      0.0, 1.0, 0.20);
        readonly KotonParameter _saturation  = new KotonParameter("saturation",   "Saturation",   0.0, 1.0, 0.40);
        readonly KotonParameter _hfDecay     = new KotonParameter("hf_decay",     "HF decay",     0.0, 1.0, 0.60);
        readonly KotonParameter _pingPong    = new KotonParameter("ping_pong",    "Ping-pong",    0, 1, 0);
        readonly KotonParameter _stereoWidth = new KotonParameter("stereo_width", "Stereo width", 0.0, 1.0, 0.80);
        readonly KotonParameter _mix         = new KotonParameter("mix",          "Mix",          0.0, 1.0, 0.40);
        readonly KotonParameter _outGain     = new KotonParameter("out_gain",     "Output",       -30.0, 6.0, 0.0, "dB");

        readonly List<KotonParameter> _params;
        public IReadOnlyList<KotonParameter> Parameters => _params;

        int _sr;
        float[] _bufL, _bufR;
        int _bufSize;
        int _writeIdx;
        // LFOs
        float _wowPhase, _wowInc;
        float _flutterPhase, _flutterInc;
        Random _flutterRng = new Random(41);
        float _flutterState;
        // LP feedback
        float _lpFbL, _lpFbR;

        public TapeDelayPlugin()
        {
            _params = new List<KotonParameter> { _timeMs, _feedback, _wow, _flutter, _saturation, _hfDecay, _pingPong, _stereoWidth, _mix, _outGain };
        }

        public bool HasEditor => true;
        public UserControl CreateEditor() => new TapeDelayEditor(this);

        public void Prepare(int sampleRate, int maxBlockSize)
        {
            _sr = sampleRate;
            _bufSize = (int)(sampleRate * 1.6);   // 1.6s max
            _bufL = new float[_bufSize];
            _bufR = new float[_bufSize];
            _wowInc = (float)(2 * Math.PI * 0.4 / sampleRate);   // 0.4 Hz wow lent
            _flutterInc = (float)(2 * Math.PI * 6.5 / sampleRate); // 6.5 Hz flutter rapide
        }
        public void Reset()
        {
            if (_bufL != null) { Array.Clear(_bufL, 0, _bufSize); Array.Clear(_bufR, 0, _bufSize); }
            _writeIdx = 0;
            _wowPhase = 0f; _flutterPhase = 0f; _flutterState = 0f;
            _lpFbL = _lpFbR = 0f;
        }

        public void Process(Span<float> left, Span<float> right)
        {
            if (_bufL == null) return;

            float delayMs = (float)_timeMs.Value;
            float delaySamples = delayMs * _sr / 1000f;
            float fb = (float)_feedback.Value;
            float wow = (float)_wow.Value;
            float flutter = (float)_flutter.Value;
            float sat = (float)_saturation.Value;
            float hfDecay = (float)_hfDecay.Value;
            bool pingPong = _pingPong.Value >= 0.5;
            float width = (float)_stereoWidth.Value;
            float mix = (float)_mix.Value;
            float outLin = (float)Math.Pow(10.0, _outGain.Value / 20.0);

            // LP feedback : hfDecay 0..1 → 8000..800 Hz
            float lpCutoff = 8000f - hfDecay * 7200f;
            float lpAlpha = 1f - (float)Math.Exp(-2.0 * Math.PI * lpCutoff / _sr);

            int n = left.Length;
            for (int i = 0; i < n; i++)
            {
                float dryL = left[i], dryR = right[i];

                // Wow LFO
                _wowPhase += _wowInc;
                if (_wowPhase > 2 * Math.PI) _wowPhase -= (float)(2 * Math.PI);
                float wowMod = (float)Math.Sin(_wowPhase) * _sr * 0.006f * wow;   // ±6ms wow

                // Flutter : LFO rapide + petites variations random (Vaseline sur la platine)
                _flutterPhase += _flutterInc;
                if (_flutterPhase > 2 * Math.PI) _flutterPhase -= (float)(2 * Math.PI);
                float flutterTarget = (float)Math.Sin(_flutterPhase) * 0.7f + ((float)_flutterRng.NextDouble() * 2 - 1) * 0.3f;
                _flutterState += 0.05f * (flutterTarget - _flutterState);
                float flutterMod = _flutterState * _sr * 0.001f * flutter;   // ±1ms flutter

                float readPos = _writeIdx - delaySamples + wowMod + flutterMod;
                while (readPos < 0) readPos += _bufSize;
                while (readPos >= _bufSize) readPos -= _bufSize;
                int i0 = (int)readPos; int i1 = (i0 + 1) % _bufSize;
                float frac = readPos - i0;

                float tapL = _bufL[i0] * (1f - frac) + _bufL[i1] * frac;
                float tapR = _bufR[i0] * (1f - frac) + _bufR[i1] * frac;

                // Saturation tanh sur le tap (chaleur analogique)
                if (sat > 0.01f)
                {
                    float drive = 1f + sat * 3f;
                    tapL = (float)Math.Tanh(tapL * drive) / drive;
                    tapR = (float)Math.Tanh(tapR * drive) / drive;
                }

                // LP feedback (HF decay)
                _lpFbL += lpAlpha * (tapL - _lpFbL);
                _lpFbR += lpAlpha * (tapR - _lpFbR);

                // Écriture dans le buffer = input + feedback (avec ping-pong optionnel)
                float writeL, writeR;
                if (pingPong)
                {
                    // L input + R feedback ; R input + L feedback (echos alternent stéréo)
                    writeL = dryL + _lpFbR * fb;
                    writeR = dryR + _lpFbL * fb;
                }
                else
                {
                    writeL = dryL + _lpFbL * fb;
                    writeR = dryR + _lpFbR * fb;
                }
                _bufL[_writeIdx] = writeL;
                _bufR[_writeIdx] = writeR;
                _writeIdx++;
                if (_writeIdx >= _bufSize) _writeIdx = 0;

                // Width sur wet
                float wetMid = (tapL + tapR) * 0.5f;
                float wetSide = tapL - tapR;
                float wetL = wetMid + wetSide * width;
                float wetR = wetMid - wetSide * width;

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

        public static readonly string[] PresetNames = { "Slapback (100ms)", "Rockabilly (150ms)", "Roland Space Echo", "Dub echo", "Ambient long", "Wobble tape" };
        static readonly double[,] PresetValues = {
            //           time  fb   wow  flut sat  hf  pp   wid  mix  out
            /*Slap*/    { 100,  0.10, 0.10, 0.15, 0.30, 0.30, 0,   0.60, 0.35, 0.0 },
            /*Rocky*/   { 150,  0.30, 0.25, 0.30, 0.50, 0.45, 0,   0.75, 0.40, 0.0 },
            /*Roland*/  { 250,  0.55, 0.35, 0.25, 0.45, 0.65, 0,   0.85, 0.45, 0.0 },
            /*Dub*/     { 380,  0.75, 0.40, 0.20, 0.60, 0.75, 1,   0.95, 0.55, -1.0 },
            /*Ambient*/ { 600,  0.85, 0.30, 0.15, 0.30, 0.85, 0,   1.00, 0.55, -2.0 },
            /*Wobble*/  { 400,  0.55, 0.85, 0.65, 0.55, 0.60, 1,   0.95, 0.50, -1.0 },
        };
        public void LoadPreset(int idx)
        {
            if (idx < 0 || idx >= PresetValues.GetLength(0)) return;
            _timeMs.Value = PresetValues[idx, 0]; _feedback.Value = PresetValues[idx, 1];
            _wow.Value = PresetValues[idx, 2]; _flutter.Value = PresetValues[idx, 3];
            _saturation.Value = PresetValues[idx, 4]; _hfDecay.Value = PresetValues[idx, 5];
            _pingPong.Value = PresetValues[idx, 6]; _stereoWidth.Value = PresetValues[idx, 7];
            _mix.Value = PresetValues[idx, 8]; _outGain.Value = PresetValues[idx, 9];
        }
        public void SetParam(string id, double value)
        {
            foreach (var kp in _params) if (kp.Id == id) { kp.Value = value; return; }
        }
    }
}
