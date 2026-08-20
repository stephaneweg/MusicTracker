using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Windows.Controls;
using KotonStudio.Library;

namespace KotonPluginChoir
{
    /// <summary>
    /// Chœur — 8 sub-voix par note polyphonique (large ensemble). Chaque sub-voix = saw + 3
    /// formants voyelle avec detune + pan + vibrato individuel. Attaque tres longue (pad
    /// celeste). Vowel morph lent en LFO (Ah → Oh → Ee) qui evoque la respiration du choeur.
    /// </summary>
    [KotonInstrument("Choir", Id = "koton.choir", Category = "Pad", Version = "1.0", Vendor = "Koton Studio")]
    public sealed class ChoirPlugin : IKotonInstrument
    {
        public string Id => "koton.choir";
        public string DisplayName => "Choir";

        readonly KotonParameter _subVoices     = new KotonParameter("sub_voices",   "Sub-voices",   3, 12, 8);
        readonly KotonParameter _detune        = new KotonParameter("detune",       "Detune spread", 5, 40, 18, "cent");
        readonly KotonParameter _panSpread     = new KotonParameter("pan_spread",   "Pan spread",   0.0, 1.0, 0.75);
        readonly KotonParameter _vowelMorphRate= new KotonParameter("morph_rate",   "Vowel morph rate", 0.0, 0.5, 0.08, "Hz");
        readonly KotonParameter _vowelMorphAmt = new KotonParameter("morph_amount", "Vowel morph amount", 0.0, 1.0, 0.45);
        readonly KotonParameter _formantQ      = new KotonParameter("formant_q",    "Formant Q",    2, 10, 4);
        readonly KotonParameter _air           = new KotonParameter("air",          "Air (souffle)",0.0, 1.0, 0.20);
        readonly KotonParameter _attack        = new KotonParameter("attack",       "Attack",       100.0, 3000.0, 800.0, "ms");
        readonly KotonParameter _release       = new KotonParameter("release",      "Release",      200.0, 5000.0, 1800.0, "ms");
        readonly KotonParameter _volumeDb      = new KotonParameter("volume",       "Volume",       -30.0, 6.0, -6.0, "dB");

        readonly List<KotonParameter> _params;
        public IReadOnlyList<KotonParameter> Parameters => _params;
        public ChoirPlugin() { _params = new List<KotonParameter> { _subVoices, _detune, _panSpread, _vowelMorphRate, _vowelMorphAmt, _formantQ, _air, _attack, _release, _volumeDb }; }
        public bool HasEditor => true;
        public UserControl CreateEditor() => new ChoirEditor(this);

        int _sr;
        ChoirVoice[] _voices;
        int _stealCursor;
        const int MaxPoly = 5;   // 5 notes × 12 sub = 60 oscillos max — raisonnable
        double _morphPhase;

        public void Prepare(int sampleRate, int maxBlockSize)
        {
            _sr = sampleRate;
            _voices = new ChoirVoice[MaxPoly];
            for (int i = 0; i < MaxPoly; i++) _voices[i] = new ChoirVoice(sampleRate);
        }
        public void Reset() { if (_voices != null) foreach (var v in _voices) v.Kill(); }
        public void NoteOn(int note, int velocity, int sampleOffset = 0)
        {
            if (_voices == null || velocity == 0) return;
            ChoirVoice t = null;
            for (int i = 0; i < MaxPoly; i++) if (!_voices[i].IsActive) { t = _voices[i]; break; }
            if (t == null) { t = _voices[_stealCursor]; _stealCursor = (_stealCursor + 1) % MaxPoly; t.Kill(); }
            t.NoteOn(note, velocity / 127f, (int)_subVoices.Value, (float)_detune.Value, (float)_panSpread.Value, (float)_attack.Value);
        }
        public void NoteOff(int note, int sampleOffset = 0) { if (_voices == null) return; for (int i = 0; i < _voices.Length; i++) if (_voices[i].IsActive && _voices[i].Note == note) _voices[i].NoteOff((float)_release.Value); }
        public void MidiCC(int cc, int value, int sampleOffset = 0) { if (cc == 123) Reset(); }
        public void SetPitchBend(float value, int sampleOffset = 0) { }

        // Voyelles (F1, F2, F3) : Ah, Eh, Ih, Oh, Uh
        static readonly float[] F1 = { 700, 500, 300, 500, 350 };
        static readonly float[] F2 = { 1220, 1750, 2200, 900, 750 };
        static readonly float[] F3 = { 2600, 2450, 3000, 2400, 2400 };

        public void Render(Span<float> left, Span<float> right)
        {
            if (_voices == null) { left.Clear(); right.Clear(); return; }
            float volLin = (float)Math.Pow(10.0, _volumeDb.Value / 20.0);
            float rate = (float)_vowelMorphRate.Value, amt = (float)_vowelMorphAmt.Value;
            float fq = (float)_formantQ.Value, air = (float)_air.Value;
            int n = left.Length;
            for (int i = 0; i < n; i++)
            {
                // LFO morph : cycle Ah(0) → Oh(3) → Ih(2) → Ah(0) (indices)
                _morphPhase += rate / _sr;
                if (_morphPhase >= 1) _morphPhase -= 1;
                double phase3 = _morphPhase * 3;   // 0..3 sur la boucle
                int va = (int)Math.Floor(phase3);
                float t = (float)(phase3 - va);
                int[] pattern = { 0, 3, 2, 0 };   // Ah → Oh → Ih → Ah
                int vidx1 = pattern[va % 4]; int vidx2 = pattern[(va + 1) % 4];
                float f1 = F1[vidx1] * (1 - t) + F1[vidx2] * t;
                float f2 = F2[vidx1] * (1 - t) + F2[vidx2] * t;
                float f3 = F3[vidx1] * (1 - t) + F3[vidx2] * t;
                // Base voyelle = Ah, morph vers cible avec amount
                float f1Base = F1[0], f2Base = F2[0], f3Base = F3[0];
                f1 = f1Base * (1 - amt) + f1 * amt;
                f2 = f2Base * (1 - amt) + f2 * amt;
                f3 = f3Base * (1 - amt) + f3 * amt;

                float sumL = 0, sumR = 0;
                for (int v = 0; v < _voices.Length; v++)
                {
                    if (!_voices[v].IsActive) continue;
                    _voices[v].RenderSample(f1, f2, f3, fq, air, out float l, out float r);
                    sumL += l; sumR += r;
                }
                float sL = sumL * volLin, sR = sumR * volLin;
                if (sL > 1f) sL = 1f; else if (sL < -1f) sL = -1f;
                if (sR > 1f) sR = 1f; else if (sR < -1f) sR = -1f;
                left[i] = sL; right[i] = sR;
            }
        }

        public byte[] SaveState() { try { var d = new Dictionary<string, double>(); foreach (var kp in _params) d[kp.Id] = kp.Value; return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(d)); } catch { return Array.Empty<byte>(); } }
        public void LoadState(byte[] state) { if (state == null || state.Length == 0) return; try { var d = JsonSerializer.Deserialize<Dictionary<string, double>>(Encoding.UTF8.GetString(state)); if (d == null) return; foreach (var kp in _params) if (d.TryGetValue(kp.Id, out var v)) kp.Value = v; } catch { } }
        public void Dispose() { }
        public void SetParam(string id, double value) { foreach (var kp in _params) if (kp.Id == id) { kp.Value = value; return; } }
    }

    internal sealed class ChoirVoice
    {
        readonly int _sr;
        const int MaxSubs = 12;
        int _numSubs;
        double[] _phase = new double[MaxSubs];
        double[] _phaseIncBase = new double[MaxSubs];
        double[] _vibPhase = new double[MaxSubs];
        float[] _panL = new float[MaxSubs];
        float[] _panR = new float[MaxSubs];
        BiquadState[] _bp1 = new BiquadState[MaxSubs];
        BiquadState[] _bp2 = new BiquadState[MaxSubs];
        BiquadState[] _bp3 = new BiquadState[MaxSubs];
        Random _rng;
        float _noiseSt;
        float _env, _atkR, _relR;
        int _stage;
        int _note; float _vel; bool _active;
        public bool IsActive => _active;
        public int Note => _note;

        public ChoirVoice(int sr) { _sr = sr; }

        public void NoteOn(int note, float vel, int numSubs, float spreadCents, float panSpread, float atkMs)
        {
            _note = note; _vel = vel;
            _numSubs = Math.Max(1, Math.Min(MaxSubs, numSubs));
            double f = 440.0 * Math.Pow(2.0, (note - 69) / 12.0);
            _rng = new Random(note * 7919 + Environment.TickCount);
            for (int s = 0; s < _numSubs; s++)
            {
                float pos = _numSubs == 1 ? 0f : ((s / (float)(_numSubs - 1)) * 2f - 1f);
                float cents = pos * spreadCents + (float)(_rng.NextDouble() - 0.5) * spreadCents * 0.3f;
                _phaseIncBase[s] = f * Math.Pow(2.0, cents / 1200.0) / _sr;
                _phase[s] = _rng.NextDouble();
                _vibPhase[s] = _rng.NextDouble();
                float pan = pos * panSpread;
                float pnt = (pan + 1) * 0.25f;
                _panL[s] = (float)Math.Cos(pnt * Math.PI);
                _panR[s] = (float)Math.Sin(pnt * Math.PI);
            }
            _atkR = 1f / Math.Max(1, atkMs * _sr / 1000f);
            _env = 0; _stage = 1;
            _active = true;
        }
        public void NoteOff(float relMs) { _relR = 1f / Math.Max(1, relMs * _sr / 1000f); _stage = 3; }
        public void Kill() { _active = false; _env = 0; _stage = 0; }

        public void RenderSample(float f1, float f2, float f3, float fq, float air, out float outL, out float outR)
        {
            outL = 0; outR = 0;
            if (!_active) return;
            if (_stage == 1) { _env += _atkR; if (_env >= 1f) { _env = 1f; _stage = 2; } }
            else if (_stage == 3) { _env -= _relR; if (_env <= 0f) { _env = 0f; _active = false; return; } }
            float ampScale = _env / (float)Math.Sqrt(_numSubs);
            for (int s = 0; s < _numSubs; s++)
            {
                // Small vibrato per sub
                _vibPhase[s] += 3.5 / _sr;
                if (_vibPhase[s] >= 1) _vibPhase[s] -= 1;
                double vib = Math.Sin(_vibPhase[s] * 2 * Math.PI) * 4;   // ±4 cents
                double inc = _phaseIncBase[s] * Math.Pow(2, vib / 1200.0);
                float saw = (float)(2.0 * _phase[s] - 1.0);
                double dt = inc;
                if (_phase[s] < dt) { double t = _phase[s] / dt; saw -= (float)(t + t - t * t - 1.0); }
                else if (_phase[s] > 1.0 - dt) { double t = (_phase[s] - 1.0) / dt; saw -= (float)(t * t + t + t + 1.0); }
                _phase[s] += inc; if (_phase[s] >= 1) _phase[s] -= 1;
                float noise = (float)(_rng.NextDouble() * 2 - 1);
                _noiseSt = _noiseSt * 0.9f + noise * 0.1f;
                float src = saw + _noiseSt * air * 0.2f;
                SetBP(ref _bp1[s], _sr, f1, fq);
                SetBP(ref _bp2[s], _sr, f2, fq);
                SetBP(ref _bp3[s], _sr, f3, fq);
                float b1 = BiquadProcess(ref _bp1[s], src);
                float b2 = BiquadProcess(ref _bp2[s], src);
                float b3 = BiquadProcess(ref _bp3[s], src);
                float voice = (b1 + b2 * 0.6f + b3 * 0.35f) * ampScale * _vel;
                outL += voice * _panL[s]; outR += voice * _panR[s];
            }
        }

        internal struct BiquadState { public float b0, b1, b2, a1, a2, x1, x2, y1, y2; public float freq, q; }
        static void SetBP(ref BiquadState s, int sr, float freq, float q) { if (freq < 20f) freq = 20f; if (freq > sr * 0.45f) freq = sr * 0.45f; if (s.freq == freq && s.q == q) return; s.freq = freq; s.q = q; double w0 = 2.0 * Math.PI * freq / sr, alpha = Math.Sin(w0) / (2.0 * q), cosw0 = Math.Cos(w0), a0 = 1.0 + alpha; s.b0 = (float)(alpha / a0); s.b1 = 0; s.b2 = (float)(-alpha / a0); s.a1 = (float)(-2.0 * cosw0 / a0); s.a2 = (float)((1.0 - alpha) / a0); }
        static float BiquadProcess(ref BiquadState s, float x) { float y = s.b0 * x + s.b1 * s.x1 + s.b2 * s.x2 - s.a1 * s.y1 - s.a2 * s.y2; s.x2 = s.x1; s.x1 = x; s.y2 = s.y1; s.y1 = y; return y; }
    }
}
