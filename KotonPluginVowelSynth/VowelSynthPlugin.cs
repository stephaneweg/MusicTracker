using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Windows.Controls;
using KotonStudio.Library;

namespace KotonPluginVowelSynth
{
    /// <summary>
    /// Vowel Synth — lead synthetique a formants voyelle. Rebuild du son "Wah Lead" analyse
    /// depuis le .sf2 de l'utilisateur : saw PolyBLEP → 3 filtres BP en parallele accordes sur
    /// les formants d'une voyelle (A/E/I/O/U/AE), enveloppe ADSR, LFO optionnel pour un vrai
    /// wah dynamique.
    ///
    /// **Formants (Sundberg, homme adulte)** :
    /// - A  : 700 / 1220 / 2600 Hz  (bouche ouverte)
    /// - E  : 500 / 1750 / 2450 Hz  (mi-ouverte)
    /// - I  : 300 / 2200 / 3000 Hz  (fermee avant)
    /// - O  : 500 /  900 / 2400 Hz  (arrondie)
    /// - U  : 350 /  750 / 2400 Hz  (fermee arriere)
    /// - EH : 550 / 1650 / 2400 Hz  (voyelle neutre)
    ///
    /// **Vowel morph** : slider Voyelle A → Vowel B fait un lerp entre 2 vecteurs de formants,
    /// ce qui simule un vrai mouvement de bouche (le classique wah "aou-aou-aou" c'est morph
    /// entre A et O).
    ///
    /// **Wah rate/depth** : LFO qui module la position du morph au fil du temps. rate=0 → statique
    /// (le Wah Lead analyse), rate=2..8 Hz → wah classique pedal.
    ///
    /// **Sources** : saw PolyBLEP (anti-alias basique). Le "grain" du signal Vital source est
    /// approxime en ajoutant Detune (2 saws un peu desaccordes) + un petit sub sine.
    /// </summary>
    [KotonInstrument("Vowel Synth", Id = "koton.vowelsynth", Category = "Synth", Version = "1.0", Vendor = "Koton Studio")]
    public sealed class VowelSynthPlugin : IKotonInstrument
    {
        public string Id => "koton.vowelsynth";
        public string DisplayName => "Vowel Synth";

        // Vowel index (0..5 = A/E/I/O/U/EH). Combo dans l'editeur.
        readonly KotonParameter _vowelA     = new KotonParameter("vowel_a",   "Vowel A",         0, 5, 0);   // A par defaut
        readonly KotonParameter _vowelB     = new KotonParameter("vowel_b",   "Vowel B",         0, 5, 3);   // O par defaut
        readonly KotonParameter _morph      = new KotonParameter("morph",     "Morph A→B",       0.0, 1.0, 0.0);
        readonly KotonParameter _formantQ   = new KotonParameter("formant_q", "Formant Q",       1.0, 20.0, 6.0);
        readonly KotonParameter _formantGain= new KotonParameter("formant_gain","Formant gain",  0.0, 4.0, 1.5);

        readonly KotonParameter _wahRate    = new KotonParameter("wah_rate",  "Wah rate",        0.0, 12.0, 0.0, "Hz");
        readonly KotonParameter _wahDepth   = new KotonParameter("wah_depth", "Wah depth",       0.0, 1.0, 0.0);

        readonly KotonParameter _detune     = new KotonParameter("detune",    "Detune",          0.0, 30.0, 8.0, "cent");
        readonly KotonParameter _sub        = new KotonParameter("sub",       "Sub sine (-1 oct)", 0.0, 1.0, 0.15);
        readonly KotonParameter _drive      = new KotonParameter("drive",     "Drive",           0.0, 1.0, 0.20);

        readonly KotonParameter _attack     = new KotonParameter("attack",    "Attack",          1.0, 500.0, 15.0, "ms");
        readonly KotonParameter _release    = new KotonParameter("release",   "Release",         10.0, 3000.0, 200.0, "ms");

        readonly KotonParameter _vibratoDepth = new KotonParameter("vib_depth","Vibrato depth",  0.0, 50.0, 4.0, "cent");
        readonly KotonParameter _vibratoRate  = new KotonParameter("vib_rate", "Vibrato rate",   0.0, 12.0, 5.0, "Hz");

        readonly KotonParameter _volumeDb   = new KotonParameter("volume",    "Volume",          -30.0, 6.0, -3.0, "dB");

        readonly List<KotonParameter> _params;
        public IReadOnlyList<KotonParameter> Parameters => _params;

        int _sr;
        VowelVoice[] _voices;
        int _stealCursor;
        const int Polyphony = 8;

        public VowelSynthPlugin()
        {
            _params = new List<KotonParameter> {
                _vowelA, _vowelB, _morph, _formantQ, _formantGain,
                _wahRate, _wahDepth,
                _detune, _sub, _drive,
                _attack, _release,
                _vibratoDepth, _vibratoRate,
                _volumeDb,
            };
        }

        public bool HasEditor => true;
        public UserControl CreateEditor() => new VowelSynthEditor(this);

        public void Prepare(int sampleRate, int maxBlockSize)
        {
            _sr = sampleRate;
            _voices = new VowelVoice[Polyphony];
            for (int i = 0; i < Polyphony; i++) _voices[i] = new VowelVoice(sampleRate);
        }
        public void Reset() { if (_voices != null) foreach (var v in _voices) v.Kill(); }

        public void NoteOn(int note, int velocity, int sampleOffset = 0)
        {
            if (_voices == null || velocity == 0) return;
            float vel = velocity / 127f;
            double noteFreq = 440.0 * Math.Pow(2.0, (note - 69) / 12.0);

            VowelVoice target = null;
            for (int i = 0; i < _voices.Length; i++) if (!_voices[i].IsActive) { target = _voices[i]; break; }
            if (target == null)
            {
                target = _voices[_stealCursor];
                _stealCursor = (_stealCursor + 1) % _voices.Length;
                target.Kill();
            }
            target.NoteOn(note, vel, (float)noteFreq,
                (float)_detune.Value, (float)_sub.Value,
                (float)_attack.Value);
        }
        public void NoteOff(int note, int sampleOffset = 0)
        {
            if (_voices == null) return;
            for (int i = 0; i < _voices.Length; i++)
                if (_voices[i].IsActive && _voices[i].Note == note)
                    _voices[i].NoteOff((float)_release.Value);
        }
        public void MidiCC(int cc, int value, int sampleOffset = 0) { if (cc == 123) Reset(); }
        public void SetPitchBend(float value, int sampleOffset = 0) { }

        // LFO global (wah)
        double _wahPhase;

        public void Render(Span<float> left, Span<float> right)
        {
            if (_voices == null) { left.Clear(); right.Clear(); return; }

            float volLin = (float)Math.Pow(10.0, _volumeDb.Value / 20.0);
            float q = (float)_formantQ.Value;
            float formantGain = (float)_formantGain.Value;
            float wahRate = (float)_wahRate.Value;
            float wahDepth = (float)_wahDepth.Value;
            int va = (int)Math.Round(_vowelA.Value);
            int vb = (int)Math.Round(_vowelB.Value);
            float baseMorph = (float)_morph.Value;
            float drive = (float)_drive.Value;
            float vibDepthCents = (float)_vibratoDepth.Value;
            float vibRate = (float)_vibratoRate.Value;

            int n = left.Length;
            for (int i = 0; i < n; i++)
            {
                // LFO wah global
                float wahLfo = 0f;
                if (wahRate > 0.001f)
                {
                    _wahPhase += wahRate / _sr;
                    if (_wahPhase >= 1.0) _wahPhase -= 1.0;
                    wahLfo = (float)Math.Sin(_wahPhase * 2.0 * Math.PI);
                }
                float morph = baseMorph + wahLfo * wahDepth * 0.5f;
                if (morph < 0f) morph = 0f;
                if (morph > 1f) morph = 1f;

                // 3 formants morphed
                float f1 = Lerp(VowelData.F1[va], VowelData.F1[vb], morph);
                float f2 = Lerp(VowelData.F2[va], VowelData.F2[vb], morph);
                float f3 = Lerp(VowelData.F3[va], VowelData.F3[vb], morph);

                float sum = 0f;
                for (int v = 0; v < _voices.Length; v++)
                {
                    if (_voices[v].IsActive)
                        sum += _voices[v].RenderSample(_sr, f1, f2, f3, q, formantGain, vibDepthCents, vibRate, drive);
                }
                float s = sum * volLin;
                if (s > 1f) s = 1f; else if (s < -1f) s = -1f;
                left[i] = s; right[i] = s;
            }
        }

        static float Lerp(float a, float b, float t) => a + (b - a) * t;

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
        public void SetParam(string id, double value) { foreach (var kp in _params) if (kp.Id == id) { kp.Value = value; return; } }

        // Presets
        public static readonly string[] PresetNames = {
            "Wah Lead (statique O)", "Wah pedal (A-U)", "Vocal lead (I-EH)",
            "Talkbox (A-E rapide)", "Vox drone (O sub)", "Bright lead (I aigue)"
        };
        //                                   vA vB morph fQ  fG  wR   wD   det  sub  drv  atk   rel   vib  vibR vol
        static readonly double[,] PresetValues = {
            /*Wah Lead statique*/          { 3, 3, 0.0, 4.0, 1.5, 0.0, 0.0, 8,  0.15, 0.20, 15,   300,  4,   5,   -3 },
            /*Wah pedal*/                  { 0, 4, 0.5, 8.0, 2.0, 3.0, 0.8, 6,  0.10, 0.25, 15,   400,  4,   5,   -3 },
            /*Vocal lead*/                 { 2, 5, 0.5, 6.0, 1.8, 0.0, 0.0, 5,  0.05, 0.10, 20,   500,  6,   5.5, -3 },
            /*Talkbox rapide*/             { 0, 1, 0.5, 10, 2.5, 5.5, 0.9, 4,  0.05, 0.30, 8,    200,  3,   6,   -3 },
            /*Vox drone*/                  { 3, 3, 0.0, 5.0, 1.5, 0.3, 0.3, 12, 0.60, 0.15, 200,  1200, 8,   4,   -4 },
            /*Bright lead*/                { 2, 2, 0.0, 5.0, 1.8, 0.0, 0.0, 10, 0.05, 0.35, 15,   250,  4,   5,   -3 },
        };
        public void LoadPreset(int i)
        {
            if (i < 0 || i >= PresetValues.GetLength(0)) return;
            _vowelA.Value = PresetValues[i, 0]; _vowelB.Value = PresetValues[i, 1];
            _morph.Value = PresetValues[i, 2]; _formantQ.Value = PresetValues[i, 3];
            _formantGain.Value = PresetValues[i, 4];
            _wahRate.Value = PresetValues[i, 5]; _wahDepth.Value = PresetValues[i, 6];
            _detune.Value = PresetValues[i, 7]; _sub.Value = PresetValues[i, 8];
            _drive.Value = PresetValues[i, 9];
            _attack.Value = PresetValues[i, 10]; _release.Value = PresetValues[i, 11];
            _vibratoDepth.Value = PresetValues[i, 12]; _vibratoRate.Value = PresetValues[i, 13];
            _volumeDb.Value = PresetValues[i, 14];
        }
    }

    internal static class VowelData
    {
        // A E I O U EH (Sundberg, male adult formants)
        public static readonly float[] F1 = { 700f, 500f, 300f, 500f, 350f, 550f };
        public static readonly float[] F2 = { 1220f, 1750f, 2200f, 900f, 750f, 1650f };
        public static readonly float[] F3 = { 2600f, 2450f, 3000f, 2400f, 2400f, 2400f };
        public static readonly string[] Names = { "A", "E", "I", "O", "U", "EH" };
    }

    internal sealed class VowelVoice
    {
        readonly int _sr;
        double _phase1, _phase2;   // 2 saws detunees
        double _phaseInc1, _phaseInc2;
        double _phaseSub;
        double _phaseIncSub;
        float _subAmount;
        double _noteFreq;
        double _vibratoPhase;

        // ADSR
        float _amp;
        float _atkFactor;
        float _relFactor;
        int _state;      // 0 idle, 1 attack, 2 sustain, 3 release

        // 3 formants BP biquad
        BqState _bp1, _bp2, _bp3;

        bool _active;
        int _note;
        public bool IsActive => _active;
        public int Note => _note;

        public VowelVoice(int sampleRate) { _sr = sampleRate; }

        public void NoteOn(int note, float vel, float noteFreq, float detuneCents, float subAmount, float attackMs)
        {
            _note = note;
            _noteFreq = noteFreq;
            double detuneMul = Math.Pow(2.0, detuneCents / 1200.0);
            _phaseInc1 = noteFreq / _sr;
            _phaseInc2 = (noteFreq * detuneMul) / _sr;
            _phaseIncSub = (noteFreq * 0.5) / _sr;   // sub -1 oct
            _subAmount = subAmount;

            _amp = 0f;
            _atkFactor = attackMs <= 1f ? 1f : (float)(1.0 / (attackMs * _sr / 1000.0));   // atteint 1.0 en attackMs
            _state = 1;
            _active = true;
        }
        public void NoteOff(float releaseMs)
        {
            _relFactor = (float)Math.Exp(-6.907755278982137 / (releaseMs * _sr / 1000.0));
            _state = 3;
        }
        public void Kill() { _active = false; _amp = 0f; _state = 0; }

        public float RenderSample(int sr, float f1, float f2, float f3, float q, float formantGain,
                                  float vibDepthCents, float vibRate, float drive)
        {
            if (!_active) return 0f;

            // Vibrato pitch
            double pitchMul = 1.0;
            if (vibDepthCents > 0.01f && vibRate > 0.01f)
            {
                _vibratoPhase += vibRate / _sr;
                if (_vibratoPhase >= 1.0) _vibratoPhase -= 1.0;
                double cents = Math.Sin(_vibratoPhase * 2.0 * Math.PI) * vibDepthCents;
                pitchMul = Math.Pow(2.0, cents / 1200.0);
            }

            // Envelope
            if (_state == 1)
            {
                _amp += _atkFactor;
                if (_amp >= 1f) { _amp = 1f; _state = 2; }
            }
            else if (_state == 3)
            {
                _amp *= _relFactor;
                if (_amp < 1e-4f) { _active = false; return 0f; }
            }

            // Saws PolyBLEP
            double inc1 = _phaseInc1 * pitchMul;
            double inc2 = _phaseInc2 * pitchMul;
            float saw1 = PolyBlepSaw(ref _phase1, inc1);
            float saw2 = PolyBlepSaw(ref _phase2, inc2);

            // Sub sine
            _phaseSub += _phaseIncSub * pitchMul;
            if (_phaseSub >= 1.0) _phaseSub -= 1.0;
            float sub = (float)Math.Sin(_phaseSub * 2.0 * Math.PI) * _subAmount;

            float source = (saw1 + saw2) * 0.5f + sub;
            source *= _amp;

            // 3 formants BP en parallele
            SetBqBP(ref _bp1, sr, f1, q);
            SetBqBP(ref _bp2, sr, f2, q);
            SetBqBP(ref _bp3, sr, f3, q);
            float b1 = BqProc(ref _bp1, source);
            float b2 = BqProc(ref _bp2, source);
            float b3 = BqProc(ref _bp3, source);
            float voice = (b1 + b2 * 0.7f + b3 * 0.5f) * formantGain;

            // Drive doux
            if (drive > 0.001f) voice = (float)Math.Tanh(voice * (1.0 + drive * 3.0)) * (1.0f - drive * 0.4f);

            return voice;
        }

        // PolyBLEP saw
        float PolyBlepSaw(ref double phase, double dt)
        {
            float saw = (float)(2.0 * phase - 1.0);
            if (phase < dt) { double t = phase / dt; saw -= (float)(t + t - t * t - 1.0); }
            else if (phase > 1.0 - dt) { double t = (phase - 1.0) / dt; saw -= (float)(t * t + t + t + 1.0); }
            phase += dt;
            if (phase >= 1.0) phase -= 1.0;
            return saw;
        }

        // BP biquad (RBJ cookbook - bandpass constant peak gain form)
        internal struct BqState { public float b0, b1, b2, a1, a2, x1, x2, y1, y2; public float freq, q; }
        static void SetBqBP(ref BqState s, int sr, float freq, float q)
        {
            if (freq < 20f) freq = 20f;
            if (freq > sr * 0.45f) freq = sr * 0.45f;
            if (s.freq == freq && s.q == q) return;
            s.freq = freq; s.q = q;
            double w0 = 2.0 * Math.PI * freq / sr;
            double alpha = Math.Sin(w0) / (2.0 * q);
            double cosw0 = Math.Cos(w0);
            double a0 = 1.0 + alpha;
            s.b0 = (float)(alpha / a0);
            s.b1 = 0;
            s.b2 = (float)(-alpha / a0);
            s.a1 = (float)(-2.0 * cosw0 / a0);
            s.a2 = (float)((1.0 - alpha) / a0);
        }
        static float BqProc(ref BqState s, float x)
        {
            float y = s.b0 * x + s.b1 * s.x1 + s.b2 * s.x2 - s.a1 * s.y1 - s.a2 * s.y2;
            s.x2 = s.x1; s.x1 = x;
            s.y2 = s.y1; s.y1 = y;
            return y;
        }
    }
}
