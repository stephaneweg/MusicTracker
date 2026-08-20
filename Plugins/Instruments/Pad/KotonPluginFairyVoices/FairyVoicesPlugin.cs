using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Windows.Controls;
using KotonStudio.Library;

namespace KotonPluginFairyVoices
{
    /// <summary>
    /// Fairy Voices — pad éthéré fantastique : chaque note polyphonique est en réalité un choeur de
    /// N sub-voix legerement desaccordees (chorus ensemble) + formant voyelle (Aah / Ooh / Iih...)
    /// + vibrato lent + air souffle. Attaque tres longue, release long → pad qui monte et
    /// s'evapore, evoquant des voix murmurantes de fees / choeur celeste.
    ///
    /// **DSP par sub-voix** : saw PolyBLEP → 3 formants BP (formant Q eleve) → LP smoothing +
    /// bruit de souffle. Chaque sub-voix a son propre vibrato (phase decalee) et un detune fixe
    /// aleatoire dans ±spread cents.
    ///
    /// **Usage** : super poussé dans le Shimmer + Sparkle → l'effet magique complet.
    /// </summary>
    [KotonInstrument("Fairy Voices", Id = "koton.fairyvoices", Category = "Pad", Version = "1.0", Vendor = "Koton Studio")]
    public sealed class FairyVoicesPlugin : IKotonInstrument
    {
        public string Id => "koton.fairyvoices";
        public string DisplayName => "Fairy Voices";

        readonly KotonParameter _voices     = new KotonParameter("voices",     "Sub-voices",     1, 8, 5);
        readonly KotonParameter _spread     = new KotonParameter("spread",     "Detune spread",  0.0, 40.0, 15.0, "cent");
        readonly KotonParameter _vowel      = new KotonParameter("vowel",      "Voyelle",        0, 5, 0);   // A / E / I / O / U / EH
        readonly KotonParameter _formantQ   = new KotonParameter("formant_q",  "Formant Q",      1.0, 15.0, 5.0);
        readonly KotonParameter _brightness = new KotonParameter("brightness", "Brightness",     0.0, 1.0, 0.55);
        readonly KotonParameter _air        = new KotonParameter("air",        "Air (souffle)",  0.0, 1.0, 0.20);

        readonly KotonParameter _vibRate    = new KotonParameter("vib_rate",   "Vibrato rate",   0.0, 8.0, 2.5, "Hz");
        readonly KotonParameter _vibDepth   = new KotonParameter("vib_depth",  "Vibrato depth",  0.0, 40.0, 8.0, "cent");

        readonly KotonParameter _attack     = new KotonParameter("attack",     "Attack",         50.0, 4000.0, 800.0, "ms");
        readonly KotonParameter _release    = new KotonParameter("release",    "Release",        100.0, 6000.0, 1500.0, "ms");

        readonly KotonParameter _volumeDb   = new KotonParameter("volume",     "Volume",         -30.0, 6.0, -4.0, "dB");

        readonly List<KotonParameter> _params;
        public IReadOnlyList<KotonParameter> Parameters => _params;

        int _sr;
        FairyVoice[] _voicesArr;
        int _stealCursor;
        const int Polyphony = 6;

        public FairyVoicesPlugin()
        {
            _params = new List<KotonParameter> {
                _voices, _spread, _vowel, _formantQ, _brightness, _air,
                _vibRate, _vibDepth, _attack, _release, _volumeDb,
            };
        }
        public bool HasEditor => true;
        public UserControl CreateEditor() => new FairyVoicesEditor(this);
        public void Prepare(int sampleRate, int maxBlockSize) { _sr = sampleRate; _voicesArr = new FairyVoice[Polyphony]; for (int i = 0; i < Polyphony; i++) _voicesArr[i] = new FairyVoice(sampleRate); }
        public void Reset() { if (_voicesArr != null) foreach (var v in _voicesArr) v.Kill(); }
        public void NoteOn(int note, int velocity, int sampleOffset = 0)
        {
            if (_voicesArr == null || velocity == 0) return;
            float vel = velocity / 127f;
            FairyVoice target = null;
            for (int i = 0; i < _voicesArr.Length; i++) if (!_voicesArr[i].IsActive) { target = _voicesArr[i]; break; }
            if (target == null) { target = _voicesArr[_stealCursor]; _stealCursor = (_stealCursor + 1) % _voicesArr.Length; target.Kill(); }
            double noteFreq = 440.0 * Math.Pow(2.0, (note - 69) / 12.0);
            target.NoteOn(note, vel, (float)noteFreq,
                (int)Math.Round(_voices.Value),
                (float)_spread.Value,
                (float)_attack.Value);
        }
        public void NoteOff(int note, int sampleOffset = 0) { if (_voicesArr == null) return; for (int i = 0; i < _voicesArr.Length; i++) if (_voicesArr[i].IsActive && _voicesArr[i].Note == note) _voicesArr[i].NoteOff((float)_release.Value); }
        public void MidiCC(int cc, int value, int sampleOffset = 0) { if (cc == 123) Reset(); }
        public void SetPitchBend(float value, int sampleOffset = 0) { }

        public void Render(Span<float> left, Span<float> right)
        {
            if (_voicesArr == null) { left.Clear(); right.Clear(); return; }
            int vowel = (int)Math.Round(_vowel.Value);
            float f1 = VowelData.F1[vowel];
            float f2 = VowelData.F2[vowel];
            float f3 = VowelData.F3[vowel];
            float fq = (float)_formantQ.Value;
            float bri = (float)_brightness.Value;
            float air = (float)_air.Value;
            float vibRate = (float)_vibRate.Value;
            float vibDepth = (float)_vibDepth.Value;
            float volLin = (float)Math.Pow(10.0, _volumeDb.Value / 20.0);
            int n = left.Length;
            for (int i = 0; i < n; i++)
            {
                float sumL = 0, sumR = 0;
                for (int v = 0; v < _voicesArr.Length; v++)
                {
                    float lS = 0, rS = 0;
                    if (_voicesArr[v].IsActive)
                        _voicesArr[v].RenderSample(_sr, f1, f2, f3, fq, bri, air, vibRate, vibDepth, out lS, out rS);
                    sumL += lS; sumR += rS;
                }
                float sL = sumL * volLin, sR = sumR * volLin;
                if (sL > 1f) sL = 1f; else if (sL < -1f) sL = -1f;
                if (sR > 1f) sR = 1f; else if (sR < -1f) sR = -1f;
                left[i] = sL; right[i] = sR;
            }
        }

        public byte[] SaveState() { try { var d = new Dictionary<string, double>(); foreach (var kp in _params) d[kp.Id] = kp.Value; return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(d)); } catch { return Array.Empty<byte>(); } }
        public void LoadState(byte[] state) { if (state == null || state.Length == 0) return; try { var d = JsonSerializer.Deserialize<Dictionary<string, double>>(Encoding.UTF8.GetString(state)); if (d == null) return; foreach (var kp in _params) if (d.TryGetValue(kp.Id, out var val)) kp.Value = val; } catch { } }
        public void Dispose() { }
        public void SetParam(string id, double value) { foreach (var kp in _params) if (kp.Id == id) { kp.Value = value; return; } }

        // === Presets ===
        public static readonly string[] PresetNames = { "Aah pad (classique)", "Ooh drone (grave)", "Iih halo (aigu)", "Voice choir (spread large)", "Whisper (air fort)", "Angel wide" };
        //                       voi spr vow  fQ   bri  air  vR   vD  atk   rel   vol
        static readonly double[,] PresetValues = {
            /*Aah*/          { 5, 15, 0, 5.0, 0.55, 0.20, 2.5, 8, 800, 1500, -4 },
            /*Ooh drone*/    { 6, 12, 3, 4.5, 0.35, 0.25, 2.0, 6, 1000, 2500, -4 },
            /*Iih halo*/     { 4, 8,  2, 6.0, 0.75, 0.15, 3.0, 10, 600, 1200, -4 },
            /*Choir spread*/ { 7, 30, 0, 5.0, 0.60, 0.20, 2.0, 8, 900, 2000, -4 },
            /*Whisper*/      { 5, 20, 5, 4.0, 0.30, 0.75, 3.5, 12, 500, 1000, -5 },
            /*Angel wide*/   { 8, 25, 0, 5.5, 0.65, 0.30, 2.5, 10, 1200, 3000, -4 },
        };
        public void LoadPreset(int i)
        {
            if (i < 0 || i >= PresetValues.GetLength(0)) return;
            _voices.Value = PresetValues[i, 0]; _spread.Value = PresetValues[i, 1];
            _vowel.Value = PresetValues[i, 2]; _formantQ.Value = PresetValues[i, 3];
            _brightness.Value = PresetValues[i, 4]; _air.Value = PresetValues[i, 5];
            _vibRate.Value = PresetValues[i, 6]; _vibDepth.Value = PresetValues[i, 7];
            _attack.Value = PresetValues[i, 8]; _release.Value = PresetValues[i, 9];
            _volumeDb.Value = PresetValues[i, 10];
        }
    }

    internal static class VowelData
    {
        public static readonly float[] F1 = { 700f, 500f, 300f, 500f, 350f, 550f };
        public static readonly float[] F2 = { 1220f, 1750f, 2200f, 900f, 750f, 1650f };
        public static readonly float[] F3 = { 2600f, 2450f, 3000f, 2400f, 2400f, 2400f };
        public static readonly string[] Names = { "A (aah)", "E (eh)", "I (iih)", "O (oh)", "U (ooh)", "EH (neutre)" };
    }

    internal sealed class FairyVoice
    {
        readonly int _sr;
        const int MaxSubs = 8;
        int _numSubs;
        double[] _phase = new double[MaxSubs];
        double[] _phaseIncBase = new double[MaxSubs];
        double[] _vibPhase = new double[MaxSubs];
        float[] _panL = new float[MaxSubs];
        float[] _panR = new float[MaxSubs];
        float[] _subDetuneMul = new float[MaxSubs];   // detune fixe par sub

        BiquadState[] _bp1 = new BiquadState[MaxSubs];
        BiquadState[] _bp2 = new BiquadState[MaxSubs];
        BiquadState[] _bp3 = new BiquadState[MaxSubs];

        Random _rng;
        float _noiseState;

        // ADSR
        float _amp, _atkFactor, _relFactor;
        int _state;   // 0 idle 1 attack 2 sustain 3 release

        bool _active;
        int _note;
        public bool IsActive => _active;
        public int Note => _note;

        public FairyVoice(int sr) { _sr = sr; }

        public void NoteOn(int note, float vel, float noteFreq, int numSubs, float spreadCents, float atkMs)
        {
            _note = note;
            _numSubs = Math.Max(1, Math.Min(MaxSubs, numSubs));
            _rng = new Random(note * 7919 + Environment.TickCount);
            for (int s = 0; s < _numSubs; s++)
            {
                // Detune reparti [-spread..+spread] avec petit jitter aleatoire
                float pos = (_numSubs == 1) ? 0f : ((s / (float)(_numSubs - 1)) * 2f - 1f);
                float cents = pos * spreadCents + (float)(_rng.NextDouble() - 0.5) * (spreadCents * 0.3f);
                _subDetuneMul[s] = (float)Math.Pow(2.0, cents / 1200.0);
                _phaseIncBase[s] = noteFreq * _subDetuneMul[s] / _sr;
                _phase[s] = _rng.NextDouble();
                _vibPhase[s] = _rng.NextDouble();
                float pan = (_numSubs == 1) ? 0f : (pos * 0.85f);
                float t = (pan + 1) * 0.25f;
                _panL[s] = (float)Math.Cos(t * Math.PI);
                _panR[s] = (float)Math.Sin(t * Math.PI);
            }
            _amp = 0f;
            _atkFactor = atkMs <= 1f ? 1f : (float)(1.0 / (atkMs * _sr / 1000.0));
            _state = 1;
            _active = true;
        }
        public void NoteOff(float relMs) { _relFactor = (float)Math.Exp(-6.907755278982137 / (relMs * _sr / 1000.0)); _state = 3; }
        public void Kill() { _active = false; _amp = 0f; _state = 0; }

        public void RenderSample(int sr, float f1, float f2, float f3, float fq, float bri, float air, float vibRate, float vibDepth, out float outL, out float outR)
        {
            if (!_active) { outL = 0; outR = 0; return; }
            if (_state == 1) { _amp += _atkFactor; if (_amp >= 1f) { _amp = 1f; _state = 2; } }
            else if (_state == 3) { _amp *= _relFactor; if (_amp < 1e-4f) { _active = false; outL = 0; outR = 0; return; } }

            outL = 0; outR = 0;
            float ampScale = _amp / (float)Math.Sqrt(_numSubs);
            for (int s = 0; s < _numSubs; s++)
            {
                // Vibrato pitch par sub
                _vibPhase[s] += vibRate / sr;
                if (_vibPhase[s] >= 1) _vibPhase[s] -= 1;
                double vib = Math.Sin(_vibPhase[s] * 2 * Math.PI) * vibDepth;
                double pitchMul = Math.Pow(2.0, vib / 1200.0);
                double inc = _phaseIncBase[s] * pitchMul;

                // Saw PolyBLEP
                float saw = (float)(2.0 * _phase[s] - 1.0);
                double dt = inc;
                if (_phase[s] < dt) { double t = _phase[s] / dt; saw -= (float)(t + t - t * t - 1.0); }
                else if (_phase[s] > 1.0 - dt) { double t = (_phase[s] - 1.0) / dt; saw -= (float)(t * t + t + t + 1.0); }
                _phase[s] += inc; if (_phase[s] >= 1.0) _phase[s] -= 1.0;

                // Ajout air noise (par sub, avec envelope amp)
                float noise = (float)(_rng.NextDouble() * 2 - 1);
                _noiseState = _noiseState * 0.9f + noise * 0.1f;
                float airSig = _noiseState * air * 0.3f;

                float source = saw + airSig;

                // 3 formants BP
                SetBqBP(ref _bp1[s], sr, f1, fq);
                SetBqBP(ref _bp2[s], sr, f2, fq);
                SetBqBP(ref _bp3[s], sr, f3, fq);
                float b1 = BqProc(ref _bp1[s], source);
                float b2 = BqProc(ref _bp2[s], source);
                float b3 = BqProc(ref _bp3[s], source);
                float voice = (b1 + b2 * (0.6f + bri * 0.5f) + b3 * (0.35f + bri * 0.5f)) * 1.2f;
                voice *= ampScale;
                outL += voice * _panL[s];
                outR += voice * _panR[s];
            }
        }

        internal struct BiquadState { public float b0, b1, b2, a1, a2, x1, x2, y1, y2; public float freq, q; }
        static void SetBqBP(ref BiquadState s, int sr, float freq, float q)
        {
            if (freq < 20f) freq = 20f;
            if (freq > sr * 0.45f) freq = sr * 0.45f;
            if (s.freq == freq && s.q == q) return;
            s.freq = freq; s.q = q;
            double w0 = 2.0 * Math.PI * freq / sr;
            double alpha = Math.Sin(w0) / (2.0 * q);
            double cosw0 = Math.Cos(w0);
            double a0 = 1.0 + alpha;
            s.b0 = (float)(alpha / a0); s.b1 = 0; s.b2 = (float)(-alpha / a0);
            s.a1 = (float)(-2.0 * cosw0 / a0); s.a2 = (float)((1.0 - alpha) / a0);
        }
        static float BqProc(ref BiquadState s, float x)
        {
            float y = s.b0 * x + s.b1 * s.x1 + s.b2 * s.x2 - s.a1 * s.y1 - s.a2 * s.y2;
            s.x2 = s.x1; s.x1 = x; s.y2 = s.y1; s.y1 = y;
            return y;
        }
    }
}
