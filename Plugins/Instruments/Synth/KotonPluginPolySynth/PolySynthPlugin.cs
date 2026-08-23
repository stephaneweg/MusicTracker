using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Windows.Controls;
using KotonStudio.Library;

namespace KotonPluginPolySynth
{
    /// <summary>
    /// PolySynth (style Prophet-5 / OB-6) — 2 VCO saw detune + filtre LP 24dB avec envelope
    /// dedie, envelope amplitude ADSR, LFO destinable au pitch/cutoff. Polyphonique 6 voix.
    /// </summary>
    [KotonInstrument("PolySynth", Id = "koton.polysynth", Category = "Synth", Version = "1.0", Vendor = "Koton Studio")]
    public sealed class PolySynthPlugin : IKotonInstrument
    {
        public string Id => "koton.polysynth";
        public string DisplayName => "PolySynth";

        readonly KotonParameter _detune = new KotonParameter("detune", "OSC detune", 0.0, 20.0, 6.0, "cent");
        readonly KotonParameter _mix2   = new KotonParameter("mix_osc2","OSC 2 mix", 0.0, 1.0, 0.7);
        readonly KotonParameter _cutoff = new KotonParameter("cutoff", "Filter cutoff", 100, 8000, 3000, "Hz");
        readonly KotonParameter _res    = new KotonParameter("resonance", "Resonance", 0.0, 1.0, 0.3);
        readonly KotonParameter _envMod = new KotonParameter("env_mod", "Env → cutoff", 0.0, 1.0, 0.4);
        readonly KotonParameter _fAtk   = new KotonParameter("f_attack","Filt attack", 1, 500, 5, "ms");
        readonly KotonParameter _fDec   = new KotonParameter("f_decay", "Filt decay", 20, 3000, 400, "ms");
        readonly KotonParameter _aAtk   = new KotonParameter("a_attack","Amp attack", 1, 2000, 15, "ms");
        readonly KotonParameter _aRel   = new KotonParameter("a_release","Amp release", 20, 3000, 300, "ms");
        readonly KotonParameter _lfoRate= new KotonParameter("lfo_rate","LFO rate", 0.0, 12.0, 4.0, "Hz");
        readonly KotonParameter _lfoPitch = new KotonParameter("lfo_pitch","LFO → pitch", 0.0, 50.0, 0.0, "cent");
        readonly KotonParameter _lfoCut = new KotonParameter("lfo_cut", "LFO → cutoff", 0.0, 2000.0, 0.0, "Hz");
        readonly KotonParameter _volumeDb = new KotonParameter("volume", "Volume", -30, 6, -3, "dB");

        readonly List<KotonParameter> _params;
        public IReadOnlyList<KotonParameter> Parameters => _params;
        public PolySynthPlugin() { _params = new List<KotonParameter> { _detune, _mix2, _cutoff, _res, _envMod, _fAtk, _fDec, _aAtk, _aRel, _lfoRate, _lfoPitch, _lfoCut, _volumeDb }; }
        public bool HasEditor => true;
        public UserControl CreateEditor() => new PolySynthEditor(this);

        int _sr;
        PsVoice[] _voices;
        int _stealCursor;
        const int MaxPoly = 6;
        double _lfoPhase;

        public void Prepare(int sampleRate, int maxBlockSize) { _sr = sampleRate; _voices = new PsVoice[MaxPoly]; for (int i = 0; i < MaxPoly; i++) _voices[i] = new PsVoice(sampleRate); }
        public void Reset() { if (_voices != null) foreach (var v in _voices) v.Kill(); }
        public void NoteOn(int note, int velocity, int sampleOffset = 0)
        {
            if (_voices == null || velocity == 0) return;
            PsVoice t = null;
            for (int i = 0; i < MaxPoly; i++) if (!_voices[i].IsActive) { t = _voices[i]; break; }
            if (t == null) { t = _voices[_stealCursor]; _stealCursor = (_stealCursor + 1) % MaxPoly; t.Kill(); }
            t.NoteOn(note, velocity / 127f, (float)_detune.Value, (float)_aAtk.Value, (float)_fAtk.Value, (float)_fDec.Value);
        }
        public void NoteOff(int note, int sampleOffset = 0) { if (_voices == null) return; for (int i = 0; i < _voices.Length; i++) if (_voices[i].IsActive && _voices[i].Note == note) _voices[i].NoteOff((float)_aRel.Value); _bends.Clear(note); }
        public void MidiCC(int cc, int value, int sampleOffset = 0) { if (cc == 123) Reset(); }
        public void SetPitchBend(float value, int sampleOffset = 0) { }
        readonly KotonNoteBends _bends = new KotonNoteBends();
        public void SetNoteBend(int note, float semis, int sampleOffset = 0) => _bends.Set(note, semis);

        public void Render(Span<float> left, Span<float> right)
        {
            if (_voices == null) { left.Clear(); right.Clear(); return; }
            float volLin = (float)Math.Pow(10.0, _volumeDb.Value / 20.0);
            float mix2 = (float)_mix2.Value;
            float baseCutoff = (float)_cutoff.Value;
            float res = (float)_res.Value;
            float envMod = (float)_envMod.Value;
            float lfoRate = (float)_lfoRate.Value, lfoPitch = (float)_lfoPitch.Value, lfoCut = (float)_lfoCut.Value;
            int n = left.Length;
            for (int i = 0; i < n; i++)
            {
                _lfoPhase += lfoRate / _sr;
                if (_lfoPhase >= 1) _lfoPhase -= 1;
                float lfo = (float)Math.Sin(_lfoPhase * 2 * Math.PI);
                float sum = 0;
                for (int v = 0; v < _voices.Length; v++) if (_voices[v].IsActive) sum += _voices[v].RenderSample(mix2, baseCutoff, res, envMod, lfo * lfoCut, lfo * lfoPitch, _bends.Factor(_voices[v].Note));
                float s = sum * volLin;
                if (s > 1f) s = 1f; else if (s < -1f) s = -1f;
                left[i] = s; right[i] = s;
            }
        }
        public byte[] SaveState() { try { var d = new Dictionary<string, double>(); foreach (var kp in _params) d[kp.Id] = kp.Value; return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(d)); } catch { return Array.Empty<byte>(); } }
        public void LoadState(byte[] state) { if (state == null || state.Length == 0) return; try { var d = JsonSerializer.Deserialize<Dictionary<string, double>>(Encoding.UTF8.GetString(state)); if (d == null) return; foreach (var kp in _params) if (d.TryGetValue(kp.Id, out var v)) kp.Value = v; } catch { } }
        public void Dispose() { }
        public void SetParam(string id, double value) { foreach (var kp in _params) if (kp.Id == id) { kp.Value = value; return; } }
    }

    internal sealed class PsVoice
    {
        readonly int _sr;
        double _p1, _p2, _inc1, _inc2;
        double _baseFreq;
        float _amp, _atkR, _relR;
        int _stage;
        float _fEnv, _fEnvAtk, _fEnvDec;
        BiquadState _lp1, _lp2;
        int _note; float _vel; bool _active;
        public bool IsActive => _active;
        public int Note => _note;
        public PsVoice(int sr) { _sr = sr; }

        public void NoteOn(int note, float vel, float detCents, float aAtkMs, float fAtkMs, float fDecMs)
        {
            _note = note; _vel = vel;
            _baseFreq = 440.0 * Math.Pow(2.0, (note - 69) / 12.0);
            _inc1 = _baseFreq / _sr;
            _inc2 = _baseFreq * Math.Pow(2.0, detCents / 1200.0) / _sr;
            _p1 = 0; _p2 = 0.5;
            _atkR = 1f / Math.Max(1, aAtkMs * _sr / 1000f);
            _amp = 0; _stage = 1;
            _fEnvAtk = 1f / Math.Max(1, fAtkMs * _sr / 1000f);
            _fEnvDec = (float)Math.Exp(-1.0 / (fDecMs * _sr / 1000.0));
            _fEnv = 0;
            _active = true;
            _lp1 = default; _lp2 = default;
        }
        public void NoteOff(float relMs) { _relR = 1f / Math.Max(1, relMs * _sr / 1000f); _stage = 3; }
        public void Kill() { _active = false; _amp = 0; _stage = 0; }

        public float RenderSample(float mix2, float baseCutoff, float res, float envMod, float lfoCut, float lfoPitchCents, float bendMul = 1f)
        {
            if (!_active) return 0f;
            if (_stage == 1) { _amp += _atkR; if (_amp >= 1f) { _amp = 1f; _stage = 2; } _fEnv += _fEnvAtk; if (_fEnv > 1f) _fEnv = 1f; }
            else if (_stage == 2) _fEnv *= _fEnvDec;
            else if (_stage == 3) { _amp -= _relR; _fEnv *= _fEnvDec; if (_amp <= 0f) { _amp = 0f; _active = false; return 0f; } }

            // bendMul (per-voice) et LFO pitch se multiplient — les deux modulent la freq de lecture.
            double pitchMul = Math.Pow(2.0, lfoPitchCents / 1200.0) * bendMul;
            double inc1e = _inc1 * pitchMul;
            double inc2e = _inc2 * pitchMul;
            _p1 += inc1e; if (_p1 >= 1) _p1 -= 1;
            _p2 += inc2e; if (_p2 >= 1) _p2 -= 1;
            float saw1 = (float)(2.0 * _p1 - 1.0);
            float saw2 = (float)(2.0 * _p2 - 1.0);
            float src = saw1 + saw2 * mix2;

            float cutoff = baseCutoff + envMod * _fEnv * 5000f + lfoCut;
            if (cutoff < 80) cutoff = 80;
            if (cutoff > _sr * 0.4f) cutoff = _sr * 0.4f;
            float q = 0.5f + res * 6f;
            SetBQ(ref _lp1, _sr, cutoff, q);
            SetBQ(ref _lp2, _sr, cutoff, q);
            float o1 = BQ(ref _lp1, src);
            float o2 = BQ(ref _lp2, o1);
            return o2 * _amp * _vel * 0.4f;
        }

        internal struct BiquadState { public float b0, b1, b2, a1, a2, x1, x2, y1, y2; public float freq, q; }
        static void SetBQ(ref BiquadState s, int sr, float freq, float q) { if (s.freq == freq && s.q == q) return; s.freq = freq; s.q = q; double w0 = 2.0 * Math.PI * freq / sr, alpha = Math.Sin(w0) / (2.0 * q), cosw0 = Math.Cos(w0), a0 = 1.0 + alpha; s.b0 = (float)((1.0 - cosw0) / 2.0 / a0); s.b1 = (float)((1.0 - cosw0) / a0); s.b2 = s.b0; s.a1 = (float)(-2.0 * cosw0 / a0); s.a2 = (float)((1.0 - alpha) / a0); }
        static float BQ(ref BiquadState s, float x) { float y = s.b0 * x + s.b1 * s.x1 + s.b2 * s.x2 - s.a1 * s.y1 - s.a2 * s.y2; s.x2 = s.x1; s.x1 = x; s.y2 = s.y1; s.y1 = y; return y; }
    }
}
