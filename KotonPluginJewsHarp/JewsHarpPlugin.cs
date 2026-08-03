using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Windows.Controls;
using KotonStudio.Library;

namespace KotonPluginJewsHarp
{
    /// <summary>
    /// Guimbarde (Jew's Harp / Vargan / Morsing / Kubing) — instrument atypique où la lame
    /// métallique vibre TOUJOURS à la même fréquence (fondamentale fixe), et le joueur module
    /// le son en changeant la forme de sa cavité BUCCALE qui agit comme un filtre formant
    /// résonant. Les différentes voyelles ("ooo/aah/eee/iii") mettent en avant différents
    /// harmoniques de la fondamentale.
    ///
    /// **DSP** :
    /// - Sawtooth à la fréquence fixe de la lame (LameFreqHz, ~100-200 Hz typique)
    /// - Excitation impulsive au NoteOn (twang du doigt qui pince la lame)
    /// - Filtre BP peaking Q élevé (Q=8-15) simulant la cavité buccale — sa fréquence est
    ///   pilotée par la NOTE MIDI (contrairement aux instruments normaux) : note grave = bouche
    ///   fermée = formant à ~400 Hz ; note aigüe = bouche ouverte = formant à ~2500 Hz
    /// - Bruit blanc filtré léger (le souffle qui passe autour de la lame)
    /// - LP final pour éliminer les aigus trop numériques
    ///
    /// **Note MIDI → formant** : mapping linéaire par défaut C2 (36) → 400 Hz, C6 (84) → 3500 Hz.
    /// L'user "joue" avec le clavier comme s'il modulait sa bouche.
    ///
    /// **Différence avec autres instruments** : polyphonie 4 mais chaque note re-trigger le twang.
    /// L'effet "boing wow wow" caractéristique vient de la combinaison twang + formant sweep.
    /// </summary>
    [KotonInstrument("Guimbarde (Jew's Harp)", Id = "koton.jewsharp", Category = "Physical Model", Version = "1.0", Vendor = "Koton Studio")]
    public sealed class JewsHarpPlugin : IKotonInstrument
    {
        public string Id => "koton.jewsharp";
        public string DisplayName => "Guimbarde";

        readonly KotonParameter _lameFreq    = new KotonParameter("lame_freq",    "Lame frequency", 60.0, 300.0, 130.0, "Hz");
        readonly KotonParameter _lameDecay   = new KotonParameter("lame_decay",   "Lame decay",     0.0, 1.0, 0.55);
        readonly KotonParameter _twang       = new KotonParameter("twang",        "Twang (impact)", 0.0, 1.0, 0.65);
        readonly KotonParameter _formantQ    = new KotonParameter("formant_q",    "Formant Q",      2.0, 20.0, 10.0);
        readonly KotonParameter _formantMin  = new KotonParameter("formant_min",  "Formant min (C2)", 200.0, 800.0, 400.0, "Hz");
        readonly KotonParameter _formantMax  = new KotonParameter("formant_max",  "Formant max (C6)", 1500.0, 5000.0, 3500.0, "Hz");
        readonly KotonParameter _breath      = new KotonParameter("breath",       "Breath (souffle)", 0.0, 1.0, 0.15);
        readonly KotonParameter _brightness  = new KotonParameter("brightness",   "Brightness",     0.0, 1.0, 0.55);
        readonly KotonParameter _volumeDb    = new KotonParameter("volume",       "Volume",         -30.0, 6.0, -3.0, "dB");

        readonly List<KotonParameter> _params;
        public IReadOnlyList<KotonParameter> Parameters => _params;

        int _sr;
        JewsHarpVoice[] _voices;
        int _stealCursor;
        const int Polyphony = 4;

        public JewsHarpPlugin()
        {
            _params = new List<KotonParameter> {
                _lameFreq, _lameDecay, _twang, _formantQ, _formantMin, _formantMax,
                _breath, _brightness, _volumeDb,
            };
        }

        public bool HasEditor => true;
        public UserControl CreateEditor() => new JewsHarpEditor(this);

        public void Prepare(int sampleRate, int maxBlockSize)
        {
            _sr = sampleRate;
            _voices = new JewsHarpVoice[Polyphony];
            for (int i = 0; i < Polyphony; i++) _voices[i] = new JewsHarpVoice(sampleRate);
        }
        public void Reset()
        {
            if (_voices != null) foreach (var v in _voices) v.Kill();
        }

        public void NoteOn(int note, int velocity, int sampleOffset = 0)
        {
            if (_voices == null || velocity == 0) return;
            float vel = velocity / 127f;

            JewsHarpVoice target = null;
            for (int i = 0; i < _voices.Length; i++) if (!_voices[i].IsActive) { target = _voices[i]; break; }
            if (target == null)
            {
                target = _voices[_stealCursor];
                _stealCursor = (_stealCursor + 1) % _voices.Length;
                target.Kill();
            }

            // Note MIDI → position du formant (mapping linéaire C2=36 → formantMin, C6=84 → formantMax)
            float noteNorm = Math.Max(0f, Math.Min(1f, (note - 36) / 48f));
            float formantFreq = (float)(_formantMin.Value + noteNorm * (_formantMax.Value - _formantMin.Value));

            target.NoteOn(note, vel,
                (float)_lameFreq.Value, (float)_lameDecay.Value, (float)_twang.Value,
                formantFreq, (float)_formantQ.Value,
                (float)_breath.Value, (float)_brightness.Value);
        }

        public void NoteOff(int note, int sampleOffset = 0)
        {
            if (_voices == null) return;
            for (int i = 0; i < _voices.Length; i++)
                if (_voices[i].IsActive && _voices[i].Note == note)
                    _voices[i].NoteOff();
        }
        public void MidiCC(int cc, int value, int sampleOffset = 0)
        {
            if (cc == 123) Reset();
        }
        public void SetPitchBend(float value, int sampleOffset = 0) { }

        public void Render(Span<float> left, Span<float> right)
        {
            if (_voices == null) { left.Clear(); right.Clear(); return; }

            float volLin = (float)Math.Pow(10.0, _volumeDb.Value / 20.0);
            int n = left.Length;
            for (int i = 0; i < n; i++)
            {
                float sum = 0f;
                for (int v = 0; v < _voices.Length; v++)
                {
                    if (_voices[v].IsActive) sum += _voices[v].RenderSample();
                }
                float s = sum * volLin;
                left[i] = s; right[i] = s;
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

        public static readonly string[] PresetNames = {
            "Guimbarde metal (classique)", "Vargan russe (grande, grave)",
            "Morsing indien (petit, aigu)", "Kubing bamboo (doux)", "Doromb (melodique)"
        };
        static readonly double[,] PresetValues = {
            //           lFrq lDec twng fQ   fMin fMax  brth brgh vol
            /*Metal*/    { 130, 0.55, 0.65, 10, 400, 3500, 0.15, 0.55, -3.0 },
            /*Vargan*/   { 85,  0.75, 0.70, 12, 300, 2800, 0.20, 0.45, -3.0 },
            /*Morsing*/  { 180, 0.35, 0.75, 14, 500, 4500, 0.10, 0.75, -4.0 },
            /*Kubing*/   { 110, 0.65, 0.35, 8,  400, 3000, 0.25, 0.40, -3.0 },
            /*Doromb*/   { 140, 0.60, 0.60, 11, 450, 3800, 0.15, 0.60, -3.0 },
        };
        public void LoadPreset(int idx)
        {
            if (idx < 0 || idx >= PresetValues.GetLength(0)) return;
            _lameFreq.Value = PresetValues[idx, 0]; _lameDecay.Value = PresetValues[idx, 1];
            _twang.Value = PresetValues[idx, 2]; _formantQ.Value = PresetValues[idx, 3];
            _formantMin.Value = PresetValues[idx, 4]; _formantMax.Value = PresetValues[idx, 5];
            _breath.Value = PresetValues[idx, 6]; _brightness.Value = PresetValues[idx, 7];
            _volumeDb.Value = PresetValues[idx, 8];
        }
        public void SetParam(string id, double value)
        {
            foreach (var kp in _params) if (kp.Id == id) { kp.Value = value; return; }
        }
    }

    internal sealed class JewsHarpVoice
    {
        readonly int _sr;

        // Sawtooth à la fréquence de la lame (FIXE, indépendante de la note MIDI)
        double _phase;
        double _phaseInc;

        // Enveloppe d'amplitude (twang + decay)
        float _amp;
        float _decayFactor;

        // Twang impact : chirp très court + click au NoteOn
        float _twangAmp;
        float _twangDecay;

        // Bruit d'excitation (souffle qui passe)
        float _noiseState;
        Random _rng;
        float _breathAmount;

        // Formant biquad peak (Q élevé, sweep par note MIDI)
        BiquadState _formant;

        // LP final anti-froid
        BiquadState _lpFinal;

        bool _active;
        int _note;
        public bool IsActive => _active;
        public int Note => _note;

        float _brightness;

        public JewsHarpVoice(int sampleRate)
        {
            _sr = sampleRate;
            SetBiquadLP(ref _lpFinal, sampleRate, 6000f, 0.707f);
        }

        public void NoteOn(int note, float velocity, float lameFreqHz, float lameDecay,
                           float twang, float formantFreq, float formantQ,
                           float breath, float brightness)
        {
            _note = note;
            _rng = new Random(note * 7919 + Environment.TickCount);
            _phaseInc = lameFreqHz / _sr;
            _brightness = brightness;
            _breathAmount = breath;

            // Amplitude : twang initial fort + envelope de décroissance
            _amp = velocity;
            float decayMs = 200f + lameDecay * 3000f;   // 200ms..3.2s
            _decayFactor = (float)Math.Exp(-6.907755278982137 / (decayMs * _sr / 1000.0));

            // Twang chirp initial très court (~15 ms)
            _twangAmp = twang * velocity * 1.5f;
            _twangDecay = (float)Math.Exp(-6.907755278982137 / (15 * _sr / 1000.0));

            // Formant peak avec Q élevé — c'est la CLÉ du son de guimbarde
            SetBiquadPeaking(ref _formant, _sr, formantFreq, formantQ, 15.0f);   // +15 dB boost

            _active = true;
        }

        public void NoteOff()
        {
            // Optionnel : accélère le decay au release
            _decayFactor *= 0.998f;
        }

        public void Kill()
        {
            _active = false;
            _amp = 0f;
            _twangAmp = 0f;
            _phase = 0;
        }

        public float RenderSample()
        {
            if (!_active) return 0f;

            // 1) Sawtooth à la fréquence fixe de la lame
            float saw = (float)(2.0 * _phase - 1.0);
            // PolyBLEP
            double dt = _phaseInc;
            if (_phase < dt) { double t = _phase / dt; saw -= (float)(t + t - t * t - 1.0); }
            else if (_phase > 1.0 - dt) { double t = (_phase - 1.0) / dt; saw -= (float)(t * t + t + t + 1.0); }
            _phase += _phaseInc;
            if (_phase >= 1.0) _phase -= 1.0;

            // 2) Twang impact (chirp très court additif)
            float twang = 0f;
            if (_twangAmp > 1e-4f)
            {
                twang = ((float)_rng.NextDouble() * 2 - 1) * _twangAmp;
                _twangAmp *= _twangDecay;
            }

            // 3) Bruit de souffle
            float noise = (float)(_rng.NextDouble() * 2 - 1);
            _noiseState = _noiseState * 0.9f + noise * 0.1f;   // LP léger sur le noise
            float breath = _noiseState * _breathAmount * _amp * 0.15f;

            // 4) Signal composite : sawtooth × amp + twang + breath
            float source = saw * _amp + twang + breath;

            // 5) Filtre formant (BP peak, Q élevé) — c'est CE qui fait la voix de guimbarde
            float formanted = BiquadProcess(ref _formant, source);

            // 6) LP final anti-froid
            float final = BiquadProcess(ref _lpFinal, formanted);

            // Decay
            _amp *= _decayFactor;
            if (_amp < 1e-4f && _twangAmp < 1e-4f) { _active = false; return 0f; }

            return (float)Math.Tanh(final * 0.6);   // soft-clip anti-explosion
        }

        // === Biquad helpers ===
        internal struct BiquadState
        {
            public float b0, b1, b2, a1, a2;
            public float x1, x2, y1, y2;
        }
        static void SetBiquadLP(ref BiquadState s, int sr, float freq, float q)
        {
            double w0 = 2.0 * Math.PI * freq / sr;
            double alpha = Math.Sin(w0) / (2.0 * q);
            double cosw0 = Math.Cos(w0);
            double a0 = 1.0 + alpha;
            s.b0 = (float)((1.0 - cosw0) / 2.0 / a0);
            s.b1 = (float)((1.0 - cosw0) / a0);
            s.b2 = s.b0;
            s.a1 = (float)(-2.0 * cosw0 / a0);
            s.a2 = (float)((1.0 - alpha) / a0);
        }
        static void SetBiquadPeaking(ref BiquadState s, int sr, float freq, float q, float gainDb)
        {
            if (freq < 20f) freq = 20f;
            if (freq > sr * 0.45f) freq = sr * 0.45f;
            double A = Math.Pow(10.0, gainDb / 40.0);
            double w0 = 2.0 * Math.PI * freq / sr;
            double alpha = Math.Sin(w0) / (2.0 * q);
            double cosw0 = Math.Cos(w0);
            double a0 = 1.0 + alpha / A;
            s.b0 = (float)((1.0 + alpha * A) / a0);
            s.b1 = (float)(-2.0 * cosw0 / a0);
            s.b2 = (float)((1.0 - alpha * A) / a0);
            s.a1 = (float)(-2.0 * cosw0 / a0);
            s.a2 = (float)((1.0 - alpha / A) / a0);
        }
        static float BiquadProcess(ref BiquadState s, float x)
        {
            float y = s.b0 * x + s.b1 * s.x1 + s.b2 * s.x2 - s.a1 * s.y1 - s.a2 * s.y2;
            s.x2 = s.x1; s.x1 = x;
            s.y2 = s.y1; s.y1 = y;
            return y;
        }
    }
}
