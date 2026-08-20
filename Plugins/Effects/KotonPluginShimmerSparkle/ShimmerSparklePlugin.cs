using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Windows.Controls;
using KotonStudio.Library;

namespace KotonPluginShimmerSparkle
{
    /// <summary>
    /// Shimmer + Sparkle — l'effet "magique" par excellence, combine deux astuces qui vont bien
    /// ensemble :
    ///
    /// 1) **Shimmer reverb** = reverb longue dont le feedback est pitch-shifté d'une octave vers le
    ///    haut (ou d'un intervalle configurable). Ce qui rentre grave revient scintillant à l'octave,
    ///    en boucle qui monte vers l'aigu. Signature Eno / Aphex / Sigur Rós / Frozen intro.
    ///
    /// 2) **Sparkle generator** = évènements Poisson qui déclenchent des micro-notes-clochettes
    ///    (bell modales très courtes) accordées à une tonalité + gamme. Les clochettes vont dans la
    ///    reverb → elles brillent et se prolongent. Comme des étincelles / paillettes / poussière
    ///    d'étoile qui suit la musique.
    ///
    /// **Signature commune** : les 2 mécaniques partagent le meme FDN 4x4 (Hadamard) pour un rendu
    /// coherent. Le Sparkle injecte ses bells DANS la reverb (pas en post-reverb), donc elles
    /// participent naturellement au shimmer feedback → elles se pitchent-shift +12 aussi et
    /// deviennent progressivement plus aigues et éthérées.
    ///
    /// **Usage** : super sur pads, voix, guitare, piano — sur tout ce qui a un peu de sustain.
    /// Moins efficace sur percussions (les sparkles se noient dans les transitoires).
    /// </summary>
    [KotonEffect("Shimmer + Sparkle", Id = "koton.shimmersparkle", Category = "Reverb", Version = "1.0", Vendor = "Koton Studio")]
    public sealed class ShimmerSparklePlugin : IKotonEffect
    {
        public string Id => "koton.shimmersparkle";
        public string DisplayName => "Shimmer + Sparkle";

        // === Reverb / Shimmer ===
        readonly KotonParameter _size        = new KotonParameter("size",         "Reverb size",     0.0, 1.0, 0.70);
        readonly KotonParameter _decay       = new KotonParameter("decay",        "Reverb decay",    0.0, 1.0, 0.85);
        readonly KotonParameter _damping     = new KotonParameter("damping",      "HF damping",      0.0, 1.0, 0.30);
        readonly KotonParameter _preDelay    = new KotonParameter("pre_delay",    "Pre-delay",       0.0, 200.0, 20.0, "ms");
        readonly KotonParameter _shimmer     = new KotonParameter("shimmer",      "Shimmer amount",  0.0, 1.0, 0.55);
        readonly KotonParameter _shimmerSemi = new KotonParameter("shimmer_semis","Shimmer interval", -12.0, 24.0, 12.0, "st");

        // === Sparkle ===
        readonly KotonParameter _sparkleAmt  = new KotonParameter("sparkle_amount","Sparkle density", 0.0, 1.0, 0.40);
        readonly KotonParameter _sparkleGain = new KotonParameter("sparkle_gain",  "Sparkle level",   0.0, 1.5, 0.60);
        readonly KotonParameter _sparklePitchLo = new KotonParameter("sparkle_lo", "Sparkle range low",  36, 96, 72);   // MIDI note
        readonly KotonParameter _sparklePitchHi = new KotonParameter("sparkle_hi", "Sparkle range high", 36, 108, 96);
        readonly KotonParameter _sparkleKey   = new KotonParameter("sparkle_key",  "Sparkle key", 0, 11, 0);        // C..B
        readonly KotonParameter _sparkleScale = new KotonParameter("sparkle_scale","Sparkle scale", 0, 4, 0);       // 0=major 1=minor 2=pentaMaj 3=pentaMin 4=chroma
        readonly KotonParameter _sparkleDecay = new KotonParameter("sparkle_decay","Sparkle decay", 50.0, 2000.0, 400.0, "ms");
        readonly KotonParameter _sparkleTrig  = new KotonParameter("sparkle_trigger","Trigger from input",0,1,1);   // 0=free, 1=amp-gated (only when input active)

        // === Mix ===
        readonly KotonParameter _mix         = new KotonParameter("mix",         "Wet mix",         0.0, 1.0, 0.35);
        readonly KotonParameter _outGain     = new KotonParameter("out_gain",    "Output",         -30.0, 6.0, -2.0, "dB");

        readonly List<KotonParameter> _params;
        public IReadOnlyList<KotonParameter> Parameters => _params;

        int _sr;
        ShimmerSparkleCore _core;

        public ShimmerSparklePlugin()
        {
            _params = new List<KotonParameter> {
                _size, _decay, _damping, _preDelay, _shimmer, _shimmerSemi,
                _sparkleAmt, _sparkleGain, _sparklePitchLo, _sparklePitchHi,
                _sparkleKey, _sparkleScale, _sparkleDecay, _sparkleTrig,
                _mix, _outGain,
            };
        }

        public bool HasEditor => true;
        public UserControl CreateEditor() => new ShimmerSparkleEditor(this);

        public void Prepare(int sampleRate, int maxBlockSize)
        {
            _sr = sampleRate;
            _core = new ShimmerSparkleCore(sampleRate);
        }
        public void Reset() => _core?.Reset();
        public void Process(Span<float> left, Span<float> right)
        {
            if (_core == null) return;
            var p = new ShimmerSparkleParams
            {
                Size = (float)_size.Value, Decay = (float)_decay.Value,
                Damping = (float)_damping.Value, PreDelayMs = (float)_preDelay.Value,
                Shimmer = (float)_shimmer.Value, ShimmerSemis = (float)_shimmerSemi.Value,
                SparkleAmount = (float)_sparkleAmt.Value, SparkleGain = (float)_sparkleGain.Value,
                SparklePitchLo = (int)Math.Round(_sparklePitchLo.Value),
                SparklePitchHi = (int)Math.Round(_sparklePitchHi.Value),
                SparkleKey = (int)Math.Round(_sparkleKey.Value),
                SparkleScale = (int)Math.Round(_sparkleScale.Value),
                SparkleDecayMs = (float)_sparkleDecay.Value,
                SparkleTrigFromInput = _sparkleTrig.Value > 0.5,
                Mix = (float)_mix.Value, OutGainDb = (float)_outGain.Value,
            };
            _core.Process(left, right, p);
        }
        public byte[] SaveState() { try { var d = new Dictionary<string, double>(); foreach (var kp in _params) d[kp.Id] = kp.Value; return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(d)); } catch { return Array.Empty<byte>(); } }
        public void LoadState(byte[] state) { if (state == null || state.Length == 0) return; try { var d = JsonSerializer.Deserialize<Dictionary<string, double>>(Encoding.UTF8.GetString(state)); if (d == null) return; foreach (var kp in _params) if (d.TryGetValue(kp.Id, out var v)) kp.Value = v; } catch { } }
        public void Dispose() { }
        public void SetParam(string id, double value) { foreach (var kp in _params) if (kp.Id == id) { kp.Value = value; return; } }

        // === Presets ===
        public static readonly string[] PresetNames = {
            "Frozen (shimmer intense)", "Fairy dust (sparkle fort)", "Ambient pad (doux)",
            "Music box halo (sparkle + peu de shimmer)", "Cathedrale scintillante", "Cosmic (shimmer +24 st)"
        };
        //                    size dec damp preD shim shim_st spAmt spGain spLo spHi spKey spScale spDec spTrig mix out
        static readonly double[,] PresetValues = {
            /*Frozen*/       { 0.85, 0.92, 0.20, 20, 0.75, 12, 0.30, 0.50, 72, 96, 0, 2, 500, 1, 0.45, -2 },
            /*Fairy dust*/   { 0.60, 0.75, 0.35, 15, 0.35, 12, 0.75, 0.80, 72, 100, 0, 2, 350, 1, 0.40, -2 },
            /*Ambient pad*/  { 0.75, 0.85, 0.40, 30, 0.45, 12, 0.15, 0.40, 60, 88, 0, 3, 600, 1, 0.35, -3 },
            /*Music box*/    { 0.50, 0.70, 0.50, 10, 0.20, 12, 0.60, 0.70, 78, 96, 0, 0, 400, 1, 0.45, -3 },
            /*Cathedrale*/   { 0.95, 0.95, 0.15, 40, 0.60, 12, 0.20, 0.45, 68, 92, 0, 0, 800, 1, 0.50, -2 },
            /*Cosmic +24*/   { 0.80, 0.88, 0.30, 25, 0.55, 24, 0.40, 0.55, 84, 108, 0, 3, 300, 0, 0.40, -3 },
        };
        public void LoadPreset(int i, bool keepMix)
        {
            if (i < 0 || i >= PresetValues.GetLength(0)) return;
            double keptMix = _mix.Value;
            _size.Value = PresetValues[i, 0]; _decay.Value = PresetValues[i, 1]; _damping.Value = PresetValues[i, 2];
            _preDelay.Value = PresetValues[i, 3]; _shimmer.Value = PresetValues[i, 4]; _shimmerSemi.Value = PresetValues[i, 5];
            _sparkleAmt.Value = PresetValues[i, 6]; _sparkleGain.Value = PresetValues[i, 7];
            _sparklePitchLo.Value = PresetValues[i, 8]; _sparklePitchHi.Value = PresetValues[i, 9];
            _sparkleKey.Value = PresetValues[i, 10]; _sparkleScale.Value = PresetValues[i, 11];
            _sparkleDecay.Value = PresetValues[i, 12]; _sparkleTrig.Value = PresetValues[i, 13];
            _mix.Value = keepMix ? keptMix : PresetValues[i, 14]; _outGain.Value = PresetValues[i, 15];
        }
    }
}
