using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Windows.Controls;
using KotonStudio.Library;

namespace KotonPluginStorm
{
    /// <summary>
    /// Storm — ambiance orage : mix procédural de trois composants générés au vol :
    /// 1. **Vent** : bruit blanc → BP variable + LFO amplitude lente (rafales)
    /// 2. **Pluie** : bruit rose filtré HP (crépitement de gouttes fines aléatoires)
    /// 3. **Éclairs** : événements Poisson qui déclenchent un peak grave court (~2 kHz noise attaque
    ///    + descente vers 100 Hz) suivi d'un roulement de tonnerre (bruit rose LP + envelope)
    ///
    /// Tout est mixé au signal source selon Mix — utilisation typique : couche d'ambiance en insert
    /// pour un mix ambient/film, ou seul (Mix = 100%) pour créer une ambiance sonore.
    /// </summary>
    [KotonEffect("Storm", Id = "koton.storm", Category = "Ambience", Version = "1.0", Vendor = "Koton Studio")]
    public sealed class StormPlugin : IKotonEffect
    {
        public string Id => "koton.storm";
        public string DisplayName => "Storm";

        readonly KotonParameter _wind        = new KotonParameter("wind",         "Wind",         0.0, 1.0, 0.55);
        readonly KotonParameter _windRate    = new KotonParameter("wind_rate",    "Wind rate",    0.0, 1.0, 0.30);
        readonly KotonParameter _rain        = new KotonParameter("rain",         "Rain",         0.0, 1.0, 0.55);
        readonly KotonParameter _rainDensity = new KotonParameter("rain_density", "Rain density", 0.0, 1.0, 0.60);
        readonly KotonParameter _lightning   = new KotonParameter("lightning",    "Lightning",    0.0, 1.0, 0.20);
        readonly KotonParameter _thunder     = new KotonParameter("thunder",      "Thunder",      0.0, 1.0, 0.35);
        readonly KotonParameter _stereoWidth = new KotonParameter("stereo_width", "Stereo width", 0.0, 1.0, 0.85);
        readonly KotonParameter _mix         = new KotonParameter("mix",          "Mix",          0.0, 1.0, 0.70);
        readonly KotonParameter _outGain     = new KotonParameter("out_gain",     "Output",       -30.0, 6.0, -3.0, "dB");

        readonly List<KotonParameter> _params;
        public IReadOnlyList<KotonParameter> Parameters => _params;

        int _sr;
        Random _rng = new Random(7);

        // Wind : BP filter + LFO amp
        float _windBpS1, _windBpS2;
        float _windPhase, _windCutoffState;

        // Rain : bruit rose filtré HP
        float _rainPinkL, _rainPinkR;

        // Lightning + thunder : événements Poisson
        int _samplesUntilNextLightning;
        // Lightning : peak court (chirp descendant)
        int _lightningSamples;
        float _lightningEnv, _lightningPhase;
        // Thunder : bruit filtré à queue longue
        int _thunderSamples;
        float _thunderEnv;
        float _thunderLpS;

        public StormPlugin()
        {
            _params = new List<KotonParameter> { _wind, _windRate, _rain, _rainDensity, _lightning, _thunder, _stereoWidth, _mix, _outGain };
        }

        public bool HasEditor => true;
        public UserControl CreateEditor() => new StormEditor(this);

        public void Prepare(int sampleRate, int maxBlockSize)
        {
            _sr = sampleRate;
            _samplesUntilNextLightning = sampleRate * 5;   // premier eclair après 5s
        }
        public void Reset()
        {
            _windBpS1 = _windBpS2 = 0f; _windPhase = 0f; _windCutoffState = 0f;
            _rainPinkL = _rainPinkR = 0f;
            _lightningEnv = 0f; _lightningSamples = 0;
            _thunderEnv = 0f; _thunderSamples = 0; _thunderLpS = 0f;
            _samplesUntilNextLightning = _sr * 5;
        }

        public void Process(Span<float> left, Span<float> right)
        {
            float wind = (float)_wind.Value;
            float windRate = (float)_windRate.Value;
            float rain = (float)_rain.Value;
            float rainDensity = (float)_rainDensity.Value;
            float lightning = (float)_lightning.Value;
            float thunder = (float)_thunder.Value;
            float width = (float)_stereoWidth.Value;
            float mix = (float)_mix.Value;
            float outLin = (float)Math.Pow(10.0, _outGain.Value / 20.0);

            // Wind LFO amplitude : 0.05..0.5 Hz selon rate (rafales lentes → rapides)
            float windLfoHz = 0.05f + windRate * 0.45f;
            float windPhInc = (float)(2 * Math.PI * windLfoHz / _sr);

            // Lightning : densité Poisson : 0..1 → 0..0.15 événements/sec
            float lightningHz = lightning * 0.15f;

            int n = left.Length;
            for (int i = 0; i < n; i++)
            {
                float dryL = left[i], dryR = right[i];
                float wetL = 0f, wetR = 0f;

                // WIND : bruit blanc → BP dynamique modulé par LFO amp
                if (wind > 0.01f)
                {
                    float noise = (float)(_rng.NextDouble() * 2 - 1);
                    _windPhase += windPhInc;
                    if (_windPhase > 2 * Math.PI) _windPhase -= (float)(2 * Math.PI);
                    float ampMod = 0.4f + 0.6f * (0.5f + 0.5f * (float)Math.Sin(_windPhase));
                    // BP simplifié : LP → HP
                    _windBpS1 += 0.02f * (noise - _windBpS1);   // LP 300Hz
                    _windBpS2 += 0.15f * (_windBpS1 - _windBpS2); // LP 2kHz du LP → contenu 300-2000
                    float bp = _windBpS1 - _windBpS2;
                    float windSig = bp * wind * ampMod * 0.7f;
                    // Pan LFO large
                    float pan = (float)Math.Sin(_windPhase * 0.37f) * width * 0.5f;
                    wetL += windSig * (1f - pan);
                    wetR += windSig * (1f + pan);
                }

                // RAIN : pluie continue = bruit rose filtré HP + micro-events (Density augmente le nombre)
                if (rain > 0.01f)
                {
                    float wL = (float)(_rng.NextDouble() * 2 - 1);
                    float wR = (float)(_rng.NextDouble() * 2 - 1);
                    // Génération de bruit rose approximatif (accumulation lente)
                    _rainPinkL = _rainPinkL * 0.94f + wL * 0.06f;
                    _rainPinkR = _rainPinkR * 0.94f + wR * 0.06f;
                    // HP : soustraire un LP fort — retire les basses
                    float rL = wL * 0.4f + _rainPinkL * 2.5f;
                    float rR = wR * 0.4f + _rainPinkR * 2.5f;
                    // Density : amplitude à laquelle on ajoute des "gouttes" claires (pics)
                    float dropL = 0f, dropR = 0f;
                    if (_rng.NextDouble() < rainDensity * 0.005)
                    {
                        // Micro-drop : bref sinus haute fréquence
                        float dropSig = (float)((_rng.NextDouble() * 2 - 1) * 0.3);
                        if (_rng.NextDouble() < 0.5) dropL = dropSig; else dropR = dropSig;
                    }
                    wetL += (rL * 0.15f + dropL) * rain;
                    wetR += (rR * 0.15f + dropR) * rain;
                }

                // LIGHTNING event : chirp descendant court
                _samplesUntilNextLightning--;
                if (_samplesUntilNextLightning <= 0 && lightningHz > 0.001f)
                {
                    _lightningSamples = _sr / 20;   // 50 ms d'eclair
                    _lightningEnv = 0.8f + (float)_rng.NextDouble() * 0.2f;
                    _lightningPhase = 0f;
                    // Prochain eclair : distribution exponentielle
                    double avgSamples = _sr / lightningHz;
                    _samplesUntilNextLightning = (int)(-Math.Log(1.0 - _rng.NextDouble()) * avgSamples);
                    // Amorce le thunder ~1s après l'éclair (délai de la vitesse du son sur qq km)
                    _thunderSamples = _sr;   // 1s de délai en attente
                }
                if (_lightningSamples > 0)
                {
                    // Chirp : haute freq → basse freq
                    float phase = 1f - (float)_lightningSamples / (_sr / 20);
                    float freq = 3000f - phase * 2800f;
                    _lightningPhase += (float)(2 * Math.PI * freq / _sr);
                    // Mix sinus + bruit blanc pour l'aspect craquement
                    float noise = (float)(_rng.NextDouble() * 2 - 1);
                    float sig = ((float)Math.Sin(_lightningPhase) * 0.4f + noise * 0.6f) * _lightningEnv;
                    _lightningEnv *= 0.9995f;
                    wetL += sig * lightning * 2f;
                    wetR += sig * lightning * 2f * 0.85f;   // légèrement moins fort droite (asymmétrie naturelle)
                    _lightningSamples--;
                }

                // THUNDER : après le décompte, déclenche le roulement
                if (_thunderSamples > 0)
                {
                    _thunderSamples--;
                    if (_thunderSamples == 0)
                    {
                        // Déclenche le rolling thunder
                        _thunderEnv = 0.6f + (float)_rng.NextDouble() * 0.4f;
                        _thunderLpS = 0f;
                    }
                }
                if (_thunderEnv > 1e-4f)
                {
                    float noise = (float)(_rng.NextDouble() * 2 - 1);
                    _thunderLpS += 0.008f * (noise - _thunderLpS);
                    float thSig = _thunderLpS * _thunderEnv * 4f;
                    _thunderEnv *= 0.99985f;   // decay lent ~5s
                    // Modulation aléatoire pour aspect "roulant"
                    float roll = 0.7f + 0.3f * (float)_rng.NextDouble();
                    thSig *= roll;
                    wetL += thSig * thunder * 0.6f;
                    wetR += thSig * thunder * 0.6f * 0.9f;
                }

                left[i]  = (dryL * (1f - mix) + wetL * mix) * outLin;
                right[i] = (dryR * (1f - mix) + wetR * mix) * outLin;
            }
        }

        public byte[] SaveState()
        {
            try {
                var d = new Dictionary<string, double>();
                foreach (var kp in _params) d[kp.Id] = kp.Value;
                return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(d));
            } catch { return Array.Empty<byte>(); }
        }
        public void LoadState(byte[] state)
        {
            if (state == null || state.Length == 0) return;
            try {
                var d = JsonSerializer.Deserialize<Dictionary<string, double>>(Encoding.UTF8.GetString(state));
                if (d == null) return;
                foreach (var kp in _params) if (d.TryGetValue(kp.Id, out var v)) kp.Value = v;
            } catch { }
        }
        public void Dispose() { }

        public static readonly string[] PresetNames = { "Brise fraiche", "Averse", "Orage lointain", "Tempete", "Ouragan" };
        static readonly double[,] PresetValues = {
            //           wind wR    rain rD    lightn thund width mix   out
            /*Brise*/    { 0.35, 0.25, 0.15, 0.30, 0.00, 0.00, 0.75, 0.60, -3.0 },
            /*Averse*/   { 0.30, 0.20, 0.75, 0.85, 0.00, 0.00, 0.85, 0.75, -3.0 },
            /*Orage*/    { 0.50, 0.30, 0.55, 0.55, 0.15, 0.35, 0.85, 0.75, -3.0 },
            /*Tempete*/  { 0.85, 0.55, 0.75, 0.75, 0.30, 0.55, 0.95, 0.85, -2.0 },
            /*Ouragan*/  { 1.00, 0.75, 0.85, 0.95, 0.55, 0.75, 1.00, 0.95, -1.0 },
        };
        public void LoadPreset(int idx)
        {
            if (idx < 0 || idx >= PresetValues.GetLength(0)) return;
            _wind.Value = PresetValues[idx, 0]; _windRate.Value = PresetValues[idx, 1];
            _rain.Value = PresetValues[idx, 2]; _rainDensity.Value = PresetValues[idx, 3];
            _lightning.Value = PresetValues[idx, 4]; _thunder.Value = PresetValues[idx, 5];
            _stereoWidth.Value = PresetValues[idx, 6]; _mix.Value = PresetValues[idx, 7]; _outGain.Value = PresetValues[idx, 8];
        }
        public void SetParam(string id, double value)
        {
            foreach (var kp in _params) if (kp.Id == id) { kp.Value = value; return; }
        }
    }
}
