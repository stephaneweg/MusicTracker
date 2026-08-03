using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Windows.Controls;
using KotonStudio.Library;

namespace KotonPluginNightCrickets
{
    /// <summary>
    /// Night Crickets — ambiance nuit d'été : mix procédural de bruit atmosphérique nocturne +
    /// stridulations d'insectes générées aléatoirement. Chaque grillon = sinus modulé rapidement
    /// (~50-100 Hz de tremolo qui donne le "criii-criii") à une fréquence porteuse ~3.5-6 kHz.
    /// Plusieurs "individus" superposés à des rythmes légèrement décalés (comme dans la nature).
    ///
    /// **DSP** :
    /// - Fond de bruit rose filtré HP à ~600 Hz (ambiance nocturne "sifflement")
    /// - N=12 slots de grillons actifs, chacun avec sa fréquence + rythme + pan aléatoires
    /// - Événements Poisson pour trigger de nouveaux grillons
    /// - Optionnel : hibou (Owl) = hoots occasionnels grave (LP filter sur bruit + envelope)
    /// </summary>
    [KotonEffect("Night Crickets", Id = "koton.nightcrickets", Category = "Ambience", Version = "1.0", Vendor = "Koton Studio")]
    public sealed class NightCricketsPlugin : IKotonEffect
    {
        public string Id => "koton.nightcrickets";
        public string DisplayName => "Night Crickets";

        readonly KotonParameter _density    = new KotonParameter("density",     "Density",     0.0, 1.0, 0.50);
        readonly KotonParameter _pitch      = new KotonParameter("pitch",       "Pitch",       0.0, 1.0, 0.60);
        readonly KotonParameter _tempo      = new KotonParameter("tempo",       "Tempo",       0.0, 1.0, 0.50);
        readonly KotonParameter _ambience   = new KotonParameter("ambience",    "Ambience",    0.0, 1.0, 0.40);
        readonly KotonParameter _owl        = new KotonParameter("owl",         "Owl (hoots)", 0.0, 1.0, 0.10);
        readonly KotonParameter _stereoWidth= new KotonParameter("stereo_width","Stereo width",0.0, 1.0, 0.90);
        readonly KotonParameter _mix        = new KotonParameter("mix",         "Mix",         0.0, 1.0, 0.65);
        readonly KotonParameter _outGain    = new KotonParameter("out_gain",    "Output",      -30.0, 6.0, -3.0, "dB");

        readonly List<KotonParameter> _params;
        public IReadOnlyList<KotonParameter> Parameters => _params;

        int _sr;
        Random _rng = new Random(23);

        // Grillon actif : sinus porteuse × modulation rapide
        const int MaxCrickets = 12;
        double[] _cricketPhase = new double[MaxCrickets];
        double[] _cricketPhaseInc = new double[MaxCrickets];
        double[] _cricketModPhase = new double[MaxCrickets];
        double[] _cricketModInc = new double[MaxCrickets];
        float[] _cricketAmp = new float[MaxCrickets];
        int[] _cricketRemaining = new int[MaxCrickets];   // samples restants
        float[] _cricketPanL = new float[MaxCrickets];
        float[] _cricketPanR = new float[MaxCrickets];
        int _samplesUntilNext;

        // Ambience : bruit rose HP
        float _pinkL, _pinkR;

        // Owl : hoots rares
        int _samplesUntilOwl;
        float _owlEnv, _owlPhase;
        int _owlPhaseSamples;

        public NightCricketsPlugin()
        {
            _params = new List<KotonParameter> { _density, _pitch, _tempo, _ambience, _owl, _stereoWidth, _mix, _outGain };
        }

        public bool HasEditor => true;
        public UserControl CreateEditor() => new NightCricketsEditor(this);

        public void Prepare(int sampleRate, int maxBlockSize)
        {
            _sr = sampleRate;
            _samplesUntilNext = sampleRate / 2;
            _samplesUntilOwl = sampleRate * 8;
        }
        public void Reset()
        {
            for (int i = 0; i < MaxCrickets; i++) { _cricketAmp[i] = 0f; _cricketRemaining[i] = 0; }
            _pinkL = _pinkR = 0f;
            _owlEnv = 0f; _owlPhaseSamples = 0;
            _samplesUntilNext = _sr / 2;
            _samplesUntilOwl = _sr * 8;
        }

        public void Process(Span<float> left, Span<float> right)
        {
            float density = (float)_density.Value;
            float pitch = (float)_pitch.Value;
            float tempo = (float)_tempo.Value;
            float ambience = (float)_ambience.Value;
            float owl = (float)_owl.Value;
            float width = (float)_stereoWidth.Value;
            float mix = (float)_mix.Value;
            float outLin = (float)Math.Pow(10.0, _outGain.Value / 20.0);

            // Densité : 0..1 → 0..3 nouveaux grillons/sec
            float newCricketHz = density * 3f;

            int n = left.Length;
            for (int i = 0; i < n; i++)
            {
                float dryL = left[i], dryR = right[i];
                float wetL = 0f, wetR = 0f;

                // Ambience : bruit rose HP (chuchotement nocturne)
                if (ambience > 0.01f)
                {
                    float wL = (float)(_rng.NextDouble() * 2 - 1);
                    float wR = (float)(_rng.NextDouble() * 2 - 1);
                    _pinkL = _pinkL * 0.94f + wL * 0.06f;
                    _pinkR = _pinkR * 0.94f + wR * 0.06f;
                    // HP : soustraire le LP fort
                    float ambL = (wL * 0.3f + _pinkL * 2f) * ambience * 0.1f;
                    float ambR = (wR * 0.3f + _pinkR * 2f) * ambience * 0.1f;
                    wetL += ambL; wetR += ambR;
                }

                // Trigger new cricket
                _samplesUntilNext--;
                if (_samplesUntilNext <= 0 && newCricketHz > 0.01f)
                {
                    // Trouve un slot libre
                    for (int c = 0; c < MaxCrickets; c++)
                    {
                        if (_cricketRemaining[c] <= 0)
                        {
                            // Pitch : porteuse 3-6 kHz + jitter aléatoire
                            double carrierFreq = 3000 + pitch * 3000 + _rng.NextDouble() * 800 - 400;
                            _cricketPhaseInc[c] = 2 * Math.PI * carrierFreq / _sr;
                            _cricketPhase[c] = 0;
                            // Modulation : 40-120 Hz (le rythme du "criii-criii")
                            double modFreq = 40 + tempo * 80;
                            _cricketModInc[c] = 2 * Math.PI * modFreq / _sr;
                            _cricketModPhase[c] = _rng.NextDouble() * 2 * Math.PI;
                            _cricketAmp[c] = 0.15f + (float)_rng.NextDouble() * 0.15f;
                            _cricketRemaining[c] = (int)(_sr * (0.5 + _rng.NextDouble() * 2.0));   // 0.5-2.5s
                            // Pan aléatoire
                            float pan = ((float)_rng.NextDouble() * 2 - 1) * width;
                            float p01 = 0.5f * (1f + pan);
                            _cricketPanL[c] = 1f - p01;
                            _cricketPanR[c] = p01;
                            break;
                        }
                    }
                    double avgSamples = _sr / newCricketHz;
                    _samplesUntilNext = (int)(-Math.Log(1 - _rng.NextDouble()) * avgSamples);
                }

                // Render active crickets
                for (int c = 0; c < MaxCrickets; c++)
                {
                    if (_cricketRemaining[c] <= 0) continue;
                    _cricketPhase[c] += _cricketPhaseInc[c];
                    if (_cricketPhase[c] > 2 * Math.PI) _cricketPhase[c] -= 2 * Math.PI;
                    _cricketModPhase[c] += _cricketModInc[c];
                    if (_cricketModPhase[c] > 2 * Math.PI) _cricketModPhase[c] -= 2 * Math.PI;
                    // Modulation d'amplitude rapide (square-ish) = le "criii-criii"
                    float modVal = (float)Math.Max(0, Math.Sin(_cricketModPhase[c]));
                    // Squarize pour un "clic" plus net
                    modVal = modVal * modVal * modVal;
                    float sample = (float)Math.Sin(_cricketPhase[c]) * modVal * _cricketAmp[c];
                    wetL += sample * _cricketPanL[c];
                    wetR += sample * _cricketPanR[c];
                    _cricketRemaining[c]--;
                    // Fade out sur les derniers 100ms
                    if (_cricketRemaining[c] < _sr / 10)
                    {
                        _cricketAmp[c] *= 0.9997f;
                    }
                }

                // Owl hoots (rares)
                _samplesUntilOwl--;
                if (_samplesUntilOwl <= 0 && owl > 0.01f)
                {
                    _owlEnv = 0.5f + (float)_rng.NextDouble() * 0.3f;
                    _owlPhaseSamples = _sr / 3;   // 333 ms
                    _owlPhase = 0;
                    // Prochain hoot : 5-25 secondes selon owl
                    double avgOwlSec = 25.0 - owl * 20.0;
                    _samplesUntilOwl = (int)(-Math.Log(1 - _rng.NextDouble()) * _sr * avgOwlSec);
                }
                if (_owlPhaseSamples > 0)
                {
                    // Hoot : sinus grave ~350 Hz + envelope
                    _owlPhase += (float)(2 * Math.PI * 350 / _sr);
                    float hoot = (float)Math.Sin(_owlPhase) * _owlEnv;
                    _owlEnv *= 0.9996f;
                    wetL += hoot * owl * 0.4f * 0.6f;
                    wetR += hoot * owl * 0.4f * 0.4f;
                    _owlPhaseSamples--;
                }

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

        public static readonly string[] PresetNames = { "Nuit d'ete calme", "Prairie chaude", "Foret nocturne", "Etang au clair de lune", "Jungle tropicale" };
        static readonly double[,] PresetValues = {
            //          dens pit  tempo amb  owl  wid  mix  out
            /*Ete*/     { 0.40, 0.55, 0.45, 0.35, 0.05, 0.85, 0.55, -3.0 },
            /*Prairie*/ { 0.75, 0.65, 0.60, 0.20, 0.00, 0.95, 0.75, -3.0 },
            /*Foret*/   { 0.50, 0.50, 0.40, 0.55, 0.30, 0.90, 0.65, -3.0 },
            /*Etang*/   { 0.35, 0.60, 0.50, 0.45, 0.15, 0.85, 0.55, -3.0 },
            /*Jungle*/  { 0.90, 0.75, 0.75, 0.60, 0.35, 1.00, 0.85, -2.0 },
        };
        public void LoadPreset(int idx)
        {
            if (idx < 0 || idx >= PresetValues.GetLength(0)) return;
            _density.Value = PresetValues[idx, 0]; _pitch.Value = PresetValues[idx, 1];
            _tempo.Value = PresetValues[idx, 2]; _ambience.Value = PresetValues[idx, 3];
            _owl.Value = PresetValues[idx, 4]; _stereoWidth.Value = PresetValues[idx, 5];
            _mix.Value = PresetValues[idx, 6]; _outGain.Value = PresetValues[idx, 7];
        }
        public void SetParam(string id, double value)
        {
            foreach (var kp in _params) if (kp.Id == id) { kp.Value = value; return; }
        }
    }
}
