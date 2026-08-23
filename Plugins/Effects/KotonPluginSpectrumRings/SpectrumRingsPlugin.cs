using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Windows.Controls;
using KotonStudio.Library;

namespace KotonPluginSpectrumRings
{
    /// <summary>
    /// Anneaux spectraux — effet visuel pur, pass-through audio. Analyse le signal en 4 bandes
    /// (grave / bas-médium / médium / aigu) via des biquad, calcule le RMS de chaque bande + un
    /// RMS global, et expose ces niveaux en volatile pour que l'éditeur les affiche en temps réel
    /// (anneaux concentriques colorés qui pulsent + cœur central au rythme du niveau global).
    ///
    /// **Audio** : Process copie left/right tels quels (pass-through). Les filtres tournent en
    /// PARALLÈLE — mono downmix pour l'analyse, ne modifie pas le signal stéréo passant.
    ///
    /// **Thread-safety** : Process tourne sur le thread audio ; l'éditeur lit les niveaux depuis
    /// le thread UI. Les niveaux sont écrits via `Volatile.Write` (float atomique 32-bit → pas de
    /// tearing), l'éditeur lit avec `Volatile.Read`. Pas de lock, pas de latence.
    /// </summary>
    [KotonEffect("Anneaux spectraux", Id = "koton.spectrum_rings", Category = "Visualiseur", Version = "1.0", Vendor = "Koton Studio")]
    public sealed class SpectrumRingsPlugin : IKotonEffect
    {
        public string Id => "koton.spectrum_rings";
        public string DisplayName => "Anneaux spectraux";

        readonly KotonParameter _hueSpeed  = new KotonParameter("hue_speed",  "Vitesse teinte", 0.0, 2.0, 0.5, "×");
        readonly KotonParameter _reactivity = new KotonParameter("reactivity", "Réactivité",     0.5, 4.0, 1.5, "×");
        readonly KotonParameter _glow       = new KotonParameter("glow",       "Halo",           0.0, 1.0, 0.6);

        readonly List<KotonParameter> _params;
        public IReadOnlyList<KotonParameter> Parameters => _params;

        // Filtres biquad — instanciés par bande. Coefficients bandpass RBJ pour crossovers cibles.
        // Fréquences centrales : 100 Hz (grave), 500 Hz (bas-medium), 2 kHz (medium), 8 kHz (aigu).
        Biquad _bpBass, _bpLow, _bpMid, _bpHigh;
        int _sr;

        // Niveaux RMS lissés (attack + decay), publiés en volatile pour le rendu UI.
        // Level[0..3] = bandes ; Level[4] = niveau global. Accessibles depuis n'importe quel thread.
        readonly float[] _levels = new float[5];
        public float GetLevel(int idx)
        {
            if (idx < 0 || idx >= _levels.Length) return 0;
            return System.Threading.Volatile.Read(ref _levels[idx]);
        }

        public double Reactivity => _reactivity.Value;
        public double HueSpeed => _hueSpeed.Value;
        public double GlowAmount => _glow.Value;

        public SpectrumRingsPlugin()
        {
            _params = new List<KotonParameter> { _hueSpeed, _reactivity, _glow };
        }

        public bool HasEditor => true;
        public UserControl CreateEditor() => new SpectrumRingsEditor(this);

        public void Prepare(int sampleRate, int maxBlockSize)
        {
            _sr = sampleRate;
            _bpBass = new Biquad(); _bpBass.SetBandpass(sampleRate, 100f, 0.7f);
            _bpLow  = new Biquad(); _bpLow .SetBandpass(sampleRate, 500f, 0.8f);
            _bpMid  = new Biquad(); _bpMid .SetBandpass(sampleRate, 2000f, 0.9f);
            _bpHigh = new Biquad(); _bpHigh.SetBandpass(sampleRate, 8000f, 1.0f);
        }

        public void Reset()
        {
            _bpBass?.Reset(); _bpLow?.Reset(); _bpMid?.Reset(); _bpHigh?.Reset();
            for (int i = 0; i < _levels.Length; i++) System.Threading.Volatile.Write(ref _levels[i], 0);
        }

        // Constantes d'enveloppe : attack rapide (le pic monte vite), decay plus lent (l'anim
        // « tient » un moment sur les percussions) — donne un rendu percussif satisfaisant.
        const float AttackCoef = 0.35f;   // 0..1 : plus haut = plus réactif
        const float DecayCoef  = 0.03f;   // 0..1 : plus haut = décroit plus vite

        public void Process(Span<float> left, Span<float> right)
        {
            if (left.Length == 0 || right.Length == 0) return;
            int n = left.Length;
            float bass = 0, low = 0, mid = 0, high = 0, glob = 0;
            for (int i = 0; i < n; i++)
            {
                float mono = 0.5f * (left[i] + right[i]);
                float b = _bpBass.Process(mono);
                float l = _bpLow .Process(mono);
                float m = _bpMid .Process(mono);
                float h = _bpHigh.Process(mono);
                bass += b * b; low += l * l; mid += m * m; high += h * h;
                glob += mono * mono;
            }
            float rBass = (float)Math.Sqrt(bass / n);
            float rLow  = (float)Math.Sqrt(low  / n);
            float rMid  = (float)Math.Sqrt(mid  / n);
            float rHigh = (float)Math.Sqrt(high / n);
            float rGlob = (float)Math.Sqrt(glob / n);

            // Attack / decay envelope pour lisser l'animation.
            UpdateLevel(0, rBass);
            UpdateLevel(1, rLow);
            UpdateLevel(2, rMid);
            UpdateLevel(3, rHigh);
            UpdateLevel(4, rGlob);
            // NB : left/right restent INCHANGÉS (pass-through complet, l'effet n'est que visuel).
        }

        void UpdateLevel(int idx, float target)
        {
            float cur = System.Threading.Volatile.Read(ref _levels[idx]);
            float next = target > cur ? cur + (target - cur) * AttackCoef
                                      : cur - cur * DecayCoef;
            if (next < 0) next = 0;
            System.Threading.Volatile.Write(ref _levels[idx], next);
        }

        public byte[] SaveState()
        {
            try { var d = new Dictionary<string, double>(); foreach (var kp in _params) d[kp.Id] = kp.Value; return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(d)); }
            catch { return Array.Empty<byte>(); }
        }
        public void LoadState(byte[] state)
        {
            if (state == null || state.Length == 0) return;
            try { var d = JsonSerializer.Deserialize<Dictionary<string, double>>(Encoding.UTF8.GetString(state)); if (d == null) return; foreach (var kp in _params) if (d.TryGetValue(kp.Id, out var v)) kp.Value = v; } catch { }
        }
        public void Dispose() { }
    }

    /// <summary>Biquad bandpass RBJ minimal — un par bande dans SpectrumRingsPlugin.</summary>
    internal sealed class Biquad
    {
        float _b0, _b1, _b2, _a1, _a2;
        float _x1, _x2, _y1, _y2;

        public void SetBandpass(int sr, float freq, float q)
        {
            double w0 = 2.0 * Math.PI * freq / sr;
            double alpha = Math.Sin(w0) / (2.0 * q);
            double cosw0 = Math.Cos(w0);
            double a0 = 1.0 + alpha;
            _b0 = (float)(alpha / a0);
            _b1 = 0f;
            _b2 = (float)(-alpha / a0);
            _a1 = (float)(-2.0 * cosw0 / a0);
            _a2 = (float)((1.0 - alpha) / a0);
            Reset();
        }
        public void Reset() { _x1 = _x2 = _y1 = _y2 = 0; }
        public float Process(float x)
        {
            float y = _b0 * x + _b1 * _x1 + _b2 * _x2 - _a1 * _y1 - _a2 * _y2;
            _x2 = _x1; _x1 = x;
            _y2 = _y1; _y1 = y;
            return y;
        }
    }
}
