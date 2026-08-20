using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Windows.Controls;
using KotonStudio.Library;

namespace KotonPluginUnderwater
{
    /// <summary>
    /// Underwater — effet "sous l'eau" : le signal est étouffé progressivement selon Depth
    /// (plus tu descends, plus les aigus disparaissent), modulé subtilement en pitch par un
    /// LFO lent (le tanguage de l'eau qui bouge), et mixé avec un fond de bulles/bruit blanc
    /// filtré qui simule l'ambiance sous-marine. HP haut retire les subgraves anti-naturels.
    ///
    /// **DSP** : LP 1-pole (cutoff piloté par Depth, 200 Hz à 8 kHz), chorus à un tap avec
    /// modulation ±10ms (Movement), bruit blanc filtré BP ~800 Hz (Bubbles), mixed au wet.
    /// Simple et léger, aucune reverb (l'immersion vient du filtre + mod, pas de la queue).
    ///
    /// **Usage** : voix "de dessous", pads immergés, transitions cinématiques
    /// (submerger un mix pendant qq secondes puis le sortir de l'eau).
    /// </summary>
    [KotonEffect("Underwater", Id = "koton.underwater", Category = "Ambience", Version = "1.0", Vendor = "Koton Studio")]
    public sealed class UnderwaterPlugin : IKotonEffect
    {
        public string Id => "koton.underwater";
        public string DisplayName => "Underwater";

        readonly KotonParameter _depth       = new KotonParameter("depth",        "Depth",        0.0, 1.0, 0.60);
        readonly KotonParameter _movement    = new KotonParameter("movement",     "Movement",     0.0, 1.0, 0.40);
        readonly KotonParameter _bubbles     = new KotonParameter("bubbles",      "Bubbles",      0.0, 1.0, 0.25);
        readonly KotonParameter _hpFilter    = new KotonParameter("hp_filter",    "HP filter",    20.0, 500.0, 80.0, "Hz");
        readonly KotonParameter _mix         = new KotonParameter("mix",          "Mix",          0.0, 1.0, 0.80);
        readonly KotonParameter _outGain     = new KotonParameter("out_gain",     "Output",       -30.0, 6.0, 0.0, "dB");

        readonly List<KotonParameter> _params;
        public IReadOnlyList<KotonParameter> Parameters => _params;

        int _sr;
        float _lpStateL, _lpStateR;
        float _hpStateL, _hpStateR;
        float[] _delayL, _delayR;
        int _delayIdx, _delaySize;
        float _lfoPhase;
        float _noiseState;
        float _bpS1, _bpS2;
        Random _rng = new Random(9);

        public UnderwaterPlugin()
        {
            _params = new List<KotonParameter> { _depth, _movement, _bubbles, _hpFilter, _mix, _outGain };
        }

        public bool HasEditor => true;
        public UserControl CreateEditor() => new UnderwaterEditor(this);

        public void Prepare(int sampleRate, int maxBlockSize)
        {
            _sr = sampleRate;
            _delaySize = sampleRate / 30;   // ~33 ms max (assez pour ±10 ms de mod)
            _delayL = new float[_delaySize];
            _delayR = new float[_delaySize];
        }

        public void Reset()
        {
            _lpStateL = _lpStateR = 0f;
            _hpStateL = _hpStateR = 0f;
            if (_delayL != null) { Array.Clear(_delayL, 0, _delayL.Length); Array.Clear(_delayR, 0, _delayR.Length); }
            _delayIdx = 0; _lfoPhase = 0f; _noiseState = 0f; _bpS1 = _bpS2 = 0f;
        }

        public void Process(Span<float> left, Span<float> right)
        {
            if (_delayL == null) return;

            float depth = (float)_depth.Value;
            float movement = (float)_movement.Value;
            float bubbles = (float)_bubbles.Value;
            float mix = (float)_mix.Value;
            float outLin = (float)Math.Pow(10.0, _outGain.Value / 20.0);

            // LP cutoff : Depth 0..1 → 8000..200 Hz (plus profond = plus étouffé)
            float lpCutoff = 8000f - depth * 7800f;
            float lpAlpha = 1f - (float)Math.Exp(-2.0 * Math.PI * lpCutoff / _sr);

            // HP en entrée
            float alphaHp = 1f - (float)Math.Exp(-2.0 * Math.PI * _hpFilter.Value / _sr);

            // LFO chorus : 0.3 Hz (tangage lent de l'eau)
            float lfoInc = (float)(2 * Math.PI * 0.3 / _sr);

            // Bulles : bandpass biquad ~800 Hz Q=1.5
            float bpFreq = 800f, bpQ = 1.5f;
            double w0 = 2.0 * Math.PI * bpFreq / _sr;
            double alpha = Math.Sin(w0) / (2.0 * bpQ);
            double cosw0 = Math.Cos(w0);
            double a0 = 1.0 + alpha;
            float bp_b0 = (float)(alpha / a0), bp_b2 = (float)(-alpha / a0);
            float bp_a1 = (float)(-2.0 * cosw0 / a0), bp_a2 = (float)((1.0 - alpha) / a0);

            int n = left.Length;
            for (int i = 0; i < n; i++)
            {
                float inL = left[i], inR = right[i];

                // HP en entrée (retire subgraves)
                _hpStateL += alphaHp * (inL - _hpStateL);
                _hpStateR += alphaHp * (inR - _hpStateR);
                float hpL = inL - _hpStateL;
                float hpR = inR - _hpStateR;

                // Delay pour modulation pitch (chorus subtil)
                _delayL[_delayIdx] = hpL;
                _delayR[_delayIdx] = hpR;
                _lfoPhase += lfoInc;
                if (_lfoPhase > 2 * Math.PI) _lfoPhase -= (float)(2 * Math.PI);
                float modSamples = _sr * 0.005f + (float)Math.Sin(_lfoPhase) * _sr * 0.005f * movement;   // 5±5ms
                float readPos = _delayIdx - modSamples;
                while (readPos < 0) readPos += _delaySize;
                int i0 = (int)readPos; int i1 = (i0 + 1) % _delaySize;
                float frac = readPos - i0;
                float modL = _delayL[i0] * (1f - frac) + _delayL[i1] * frac;
                float modR = _delayR[i0] * (1f - frac) + _delayR[i1] * frac;
                _delayIdx++;
                if (_delayIdx >= _delaySize) _delayIdx = 0;

                // LP progressif (étouffement)
                _lpStateL += lpAlpha * (modL - _lpStateL);
                _lpStateR += lpAlpha * (modR - _lpStateR);
                float wetL = _lpStateL;
                float wetR = _lpStateR;

                // Bulles : bruit blanc filtré BP mixé au wet
                if (bubbles > 0.01f)
                {
                    float noise = (float)(_rng.NextDouble() * 2 - 1);
                    _noiseState = _noiseState * 0.99f + noise * 0.01f;   // léger LP pour rose
                    float pink = noise * 0.5f + _noiseState * 4f;
                    // Biquad BP
                    float bp = bp_b0 * pink + bp_b2 * _bpS2 - bp_a1 * _bpS1 - bp_a2 * _bpS2;
                    _bpS2 = _bpS1;
                    _bpS1 = bp;
                    wetL += bp * bubbles * 0.15f;
                    wetR += bp * bubbles * 0.15f * ((float)_rng.NextDouble() * 0.5f + 0.5f);   // pan aléatoire
                }

                left[i]  = (inL * (1f - mix) + wetL * mix) * outLin;
                right[i] = (inR * (1f - mix) + wetR * mix) * outLin;
            }
        }

        public byte[] SaveState()
        {
            try
            {
                var d = new Dictionary<string, double>();
                foreach (var kp in _params) d[kp.Id] = kp.Value;
                return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(d));
            } catch { return Array.Empty<byte>(); }
        }
        public void LoadState(byte[] state)
        {
            if (state == null || state.Length == 0) return;
            try
            {
                var d = JsonSerializer.Deserialize<Dictionary<string, double>>(Encoding.UTF8.GetString(state));
                if (d == null) return;
                foreach (var kp in _params) if (d.TryGetValue(kp.Id, out var v)) kp.Value = v;
            } catch { }
        }
        public void Dispose() { }

        public static readonly string[] PresetNames = { "Surface", "Immerge (5m)", "Grand fond", "Sous-marin" };
        static readonly double[,] PresetValues = {
            //           depth mov  bub  hp   mix  out
            /*Surface*/  { 0.35, 0.30, 0.10, 60,  0.60,  0.0 },
            /*Immerge*/  { 0.60, 0.40, 0.25, 80,  0.80,  0.0 },
            /*Grand*/    { 0.85, 0.30, 0.35, 120, 0.90, -1.0 },
            /*Sub*/      { 0.95, 0.20, 0.15, 200, 1.00, -2.0 },
        };
        public void LoadPreset(int idx)
        {
            if (idx < 0 || idx >= PresetValues.GetLength(0)) return;
            _depth.Value = PresetValues[idx, 0];
            _movement.Value = PresetValues[idx, 1];
            _bubbles.Value = PresetValues[idx, 2];
            _hpFilter.Value = PresetValues[idx, 3];
            _mix.Value = PresetValues[idx, 4];
            _outGain.Value = PresetValues[idx, 5];
        }
        public void SetParam(string id, double value)
        {
            foreach (var kp in _params) if (kp.Id == id) { kp.Value = value; return; }
        }
    }
}
