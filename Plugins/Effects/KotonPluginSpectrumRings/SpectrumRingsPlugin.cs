using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Windows.Controls;
using KotonStudio.Library;

namespace KotonPluginSpectrumRings
{
    /// <summary>
    /// Anneaux spectraux — effet audio pass-through avec viz temps réel EVENT-DRIVEN. À chaque
    /// ATTAQUE détectée dans le signal (transient dépassant un seuil relatif au niveau précédent),
    /// on émet un « ring birth » que l'éditeur affiche comme un anneau qui naît au bord, contracte
    /// vers le centre, et se fond en ~1.5s. Beaucoup de notes courtes → beaucoup d'anneaux qui se
    /// chevauchent → anim très agitée. Rythme lent → anneaux plus espacés.
    ///
    /// **Detection transient** : envelope follower rapide (attack 15ms, release 200ms) par bande.
    /// Quand le niveau instantané dépasse 1.6× l'enveloppe suivie + un plancher (évite le bruit),
    /// on trigger un anneau. Blocage 60ms après trigger pour éviter les redéclenchements sur une
    /// même note.
    ///
    /// **Bandes → couleurs/tailles** : bass = anneau large + rouge/orange, low-mid = jaune,
    /// medium = vert/teal, high = bleu/violet + anneau petit. Mapping intuitif : plus la note est
    /// aigue, plus le point de départ est proche du centre.
    ///
    /// **Thread-safety** : ConcurrentQueue MPSC — audio thread pousse les naissances, UI thread
    /// draine et gère la vie des rings. Audio path 100% pass-through, ne modifie jamais le signal.
    /// </summary>
    [KotonEffect("Anneaux spectraux", Id = "koton.spectrum_rings", Category = "Visualiseur", Version = "2.0", Vendor = "Koton Studio")]
    public sealed class SpectrumRingsPlugin : IKotonEffect
    {
        public string Id => "koton.spectrum_rings";
        public string DisplayName => "Anneaux spectraux";

        readonly KotonParameter _hueSpeed    = new KotonParameter("hue_speed",    "Vitesse teinte", 0.0, 2.0, 0.5, "×");
        readonly KotonParameter _sensitivity = new KotonParameter("sensitivity", "Sensibilité",     0.5, 3.0, 1.5, "×");
        readonly KotonParameter _ringLife    = new KotonParameter("ring_life",    "Durée anneau",   0.4, 3.0, 1.5, "s");
        readonly KotonParameter _glow        = new KotonParameter("glow",         "Halo",           0.0, 1.0, 0.6);

        readonly List<KotonParameter> _params;
        public IReadOnlyList<KotonParameter> Parameters => _params;

        Biquad _bpBass, _bpLow, _bpMid, _bpHigh;
        int _sr;

        // Enveloppes de suivi + blocages anti-rebond par bande.
        readonly float[] _env = new float[4];         // niveau lissé pour la comparaison
        readonly float[] _lastTriggerSec = new float[4];
        double _sampleClockSec;                        // temps interne en secondes (par blocs)

        // File de naissances d'anneaux (audio → UI).
        public struct RingBirth
        {
            public int BandIdx;    // 0=bass, 1=low, 2=mid, 3=high
            public float Intensity;
        }
        readonly ConcurrentQueue<RingBirth> _births = new ConcurrentQueue<RingBirth>();
        public bool TryDequeueBirth(out RingBirth b) => _births.TryDequeue(out b);

        // ---- Instrumentation debug (lisible depuis l'UI) ----
        // Niveau max vu depuis le dernier "peak" reset (approximation de ce que la VU voit).
        readonly float[] _debugEnv = new float[4];
        readonly int[] _debugTriggers = new int[4];
        int _debugProcessCalls;
        public float GetDebugEnv(int bandIdx) => bandIdx >= 0 && bandIdx < 4 ? System.Threading.Volatile.Read(ref _debugEnv[bandIdx]) : 0f;
        public int GetDebugTriggers(int bandIdx) => bandIdx >= 0 && bandIdx < 4 ? System.Threading.Volatile.Read(ref _debugTriggers[bandIdx]) : 0;
        public int GetDebugProcessCalls() => System.Threading.Volatile.Read(ref _debugProcessCalls);

        public double HueSpeed => _hueSpeed.Value;
        public double Sensitivity => _sensitivity.Value;
        public double RingLifeSec => _ringLife.Value;
        public double GlowAmount => _glow.Value;

        public SpectrumRingsPlugin()
        {
            _params = new List<KotonParameter> { _hueSpeed, _sensitivity, _ringLife, _glow };
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
            for (int i = 0; i < 4; i++) { _env[i] = 0; _lastTriggerSec[i] = -10f; }
            _sampleClockSec = 0;
            while (_births.TryDequeue(out _)) { }
        }

        public void Reset()
        {
            _bpBass?.Reset(); _bpLow?.Reset(); _bpMid?.Reset(); _bpHigh?.Reset();
            for (int i = 0; i < 4; i++) { _env[i] = 0; _lastTriggerSec[i] = -10f; }
            while (_births.TryDequeue(out _)) { }
        }

        // Seuil transient : l'instant dépasse SIGNIFICATIVEMENT l'env suivi + un plancher.
        // TriggerRatio abaissé à 1.25 et MinTriggerLevel drastiquement descendu pour être sûr de
        // choper les attaques d'instruments doux (guqin, harpe, choir…). Si trop de faux triggers,
        // remonter la sensibilité côté UI.
        const float TriggerRatio = 1.25f;
        const float MinTriggerLevel = 0.001f;
        const float ReTriggerBlockSec = 0.05f;

        const float EnvAttackCoef = 0.30f;
        const float EnvReleaseCoef = 0.005f;

        public void Process(Span<float> left, Span<float> right)
        {
            if (left.Length == 0 || right.Length == 0) return;
            System.Threading.Interlocked.Increment(ref _debugProcessCalls);
            int n = left.Length;
            // RMS par bande sur ce bloc.
            float sBass = 0, sLow = 0, sMid = 0, sHigh = 0;
            for (int i = 0; i < n; i++)
            {
                float mono = 0.5f * (left[i] + right[i]);
                float b = _bpBass.Process(mono);
                float l = _bpLow .Process(mono);
                float m = _bpMid .Process(mono);
                float h = _bpHigh.Process(mono);
                sBass += b * b; sLow += l * l; sMid += m * m; sHigh += h * h;
            }
            float invN = 1f / n;
            float[] rms = { (float)Math.Sqrt(sBass * invN), (float)Math.Sqrt(sLow * invN),
                            (float)Math.Sqrt(sMid * invN),  (float)Math.Sqrt(sHigh * invN) };

            _sampleClockSec += n / (double)_sr;
            float nowSec = (float)_sampleClockSec;
            float sens = (float)_sensitivity.Value;

            for (int i = 0; i < 4; i++)
            {
                float inst = rms[i];
                float env = _env[i];
                // Detection transient : instant > seuil × env ET > plancher ET pas re-trigger récent.
                bool blocked = (nowSec - _lastTriggerSec[i]) < ReTriggerBlockSec;
                float triggerThreshold = Math.Max(MinTriggerLevel, env * TriggerRatio / sens);
                if (!blocked && inst > triggerThreshold)
                {
                    _lastTriggerSec[i] = nowSec;
                    float intensity = Math.Min(1.5f, inst * sens * 4);
                    _births.Enqueue(new RingBirth { BandIdx = i, Intensity = intensity });
                    System.Threading.Interlocked.Increment(ref _debugTriggers[i]);
                }
                // Update envelope (attack rapide / release lent).
                _env[i] = inst > env
                    ? env + (inst - env) * EnvAttackCoef
                    : env - env * EnvReleaseCoef;
                System.Threading.Volatile.Write(ref _debugEnv[i], _env[i]);
            }
            // Audio pass-through : left/right INCHANGÉS.
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

    /// <summary>Biquad bandpass RBJ.</summary>
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
