using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Windows.Controls;
using KotonStudio.Library;

namespace KotonPluginPiano
{
    /// <summary>
    /// Piano acoustique — modele Karplus-Strong multi-cordes avec hammer noise, inharmonicite et
    /// damper mode (note-off). Base sur les principes de la physical modelling piano (Smith 1993,
    /// Bank 2000). Vise ~70-80 % du realisme d'un piano sample, en ~10 KiO de DSP pur.
    ///
    /// **Architecture par voix** (voir <see cref="PianoVoice"/>) :
    /// - 1..3 cordes Karplus-Strong parallelisees (1 grave, 2 medium, 3 aigu — comme un vrai piano)
    /// - Micro-detonation ±0.5-3 cents entre cordes → chorus naturel "bloomy"
    /// - Inharmonicite par all-pass 1er ordre dans la boucle (fk != k*f0, signature acoustique)
    /// - Hammer noise additif a l'attaque : BP 3-6 kHz (feutre) + LP 250 Hz (click) sur ~30 ms
    /// - Damper au note-off : chute rapide 80-680 ms selon param (sauf pedale de sustain)
    ///
    /// **Velocity** : dur = plus de hammer noise + plus brillant, doux = attaque douce + moins d'aigus.
    ///
    /// **Pedale de sustain** : parametre continu OR pilotable via CC64 MIDI standard (>63 = enfoncee).
    ///
    /// **Polyphonie** : 16 voix, voice stealing round-robin. Une meme note re-frappee = re-pluck des
    /// cordes existantes (comportement piano).
    /// </summary>
    [KotonInstrument("Piano", Id = "koton.piano", Category = "Physical Model", Version = "1.0", Vendor = "Koton Studio")]
    public sealed class PianoPlugin : IKotonInstrument
    {
        public string Id => "koton.piano";
        public string DisplayName => "Piano";

        readonly KotonParameter _hammerHardness  = new KotonParameter("hammer_hardness",  "Hammer hardness",  0.0, 1.0, 0.45);
        readonly KotonParameter _hammerAmount    = new KotonParameter("hammer_amount",    "Hammer noise",     0.0, 1.0, 0.35);
        readonly KotonParameter _brightness      = new KotonParameter("brightness",       "Brightness",       0.0, 1.0, 0.60);
        readonly KotonParameter _inharmonicity   = new KotonParameter("inharmonicity",    "Inharmonicity",    0.0, 1.0, 0.15);
        readonly KotonParameter _stringDetune    = new KotonParameter("string_detune",    "String detune",    0.0, 1.0, 0.35);
        readonly KotonParameter _damperTime      = new KotonParameter("damper_time",      "Damper time",      0.0, 1.0, 0.30);
        readonly KotonParameter _sustainPedal    = new KotonParameter("sustain_pedal",    "Sustain pedal",    0.0, 1.0, 0.00);
        readonly KotonParameter _body            = new KotonParameter("body",             "Body",             0.0, 1.0, 0.25);
        readonly KotonParameter _reverb          = new KotonParameter("reverb",           "Reverb",           0.0, 1.0, 0.15);
        readonly KotonParameter _stereoWidth     = new KotonParameter("stereo_width",     "Stereo width",     0.0, 1.0, 0.35);
        readonly KotonParameter _volumeDb        = new KotonParameter("volume",           "Volume",           -30.0, 6.0, -6.0, "dB");
        // Ré-attaque périodique : rejoue la note tenue tous les 1/taux de seconde.
        // 0 Hz = une seule attaque, donc aucun projet existant ne change.
        readonly KotonStudio.Plugins.Shared.KotonReAttack _retrig =
            new KotonStudio.Plugins.Shared.KotonReAttack("Trémolo", 20.0, 0.0);

        readonly List<KotonParameter> _params;
        public IReadOnlyList<KotonParameter> Parameters => _params;

        int _sampleRate;
        int _maxBlockSize;
        PianoVoice[] _voices;
        int _stealCursor;
        const int Polyphony = 16;

        // SOUNDBOARD : 3 peaks en parallele — resonances typiques d'une table d'harmonie de piano.
        // Mesures acoustiques : peaks vers 120 Hz (grande caisse), 500 Hz (mi-cassure) et 1500 Hz
        // (rayonnement aigu). Chaque peak est un bandpass RBJ (Q ~ 3), sommes puis mixe au dry.
        BiquadState _body1L, _body1R;
        BiquadState _body2L, _body2R;
        BiquadState _body3L, _body3R;

        // REVERB SCHROEDER MINI : 4 combs LP-feedback en parallele + 2 all-pass en serie.
        // Temps de comb legerement differents L/R pour decorrelation stereo naturelle. Feedback
        // pilote par le param Reverb (0.5..0.82) → decay de ~200 ms a ~2 s. Mix 0..50% wet max.
        float[] _cL1, _cL2, _cL3, _cL4, _cR1, _cR2, _cR3, _cR4;
        int _iL1, _iL2, _iL3, _iL4, _iR1, _iR2, _iR3, _iR4;
        float _lpL1, _lpL2, _lpL3, _lpL4, _lpR1, _lpR2, _lpR3, _lpR4;
        float[] _apL1, _apL2, _apR1, _apR2;
        int _aL1, _aL2, _aR1, _aR2;

        // Sustain pedal via CC64 (>63 = enfoncee)
        bool _pedalDown;

        public PianoPlugin()
        {
            _params = new List<KotonParameter>
            {
                _hammerHardness, _hammerAmount, _brightness, _inharmonicity, _stringDetune,
                _damperTime, _sustainPedal, _body, _reverb, _stereoWidth, _volumeDb, _retrig.Rate };
        }

        public bool HasEditor => true;
        public UserControl CreateEditor() => new PianoEditor(this);

        // =============================================================================================
        // Cycle
        // =============================================================================================
        public void Prepare(int sampleRate, int maxBlockSize)
        {
            _sampleRate = sampleRate;
            _maxBlockSize = maxBlockSize;
            _voices = new PianoVoice[Polyphony];
            for (int i = 0; i < Polyphony; i++) _voices[i] = new PianoVoice(sampleRate);
            // Q reduit de 3 a 2 : les Q eleves ringent trop sur des attaques repetees et
            // accumulent du "wash" resonant qui vient s'ajouter aux parasites. Q=2 garde le
            // caractere "bois" sans ringing genant.
            SetBiquadBandpass(ref _body1L, sampleRate, 120f,  2f);
            SetBiquadBandpass(ref _body1R, sampleRate, 120f,  2f);
            SetBiquadBandpass(ref _body2L, sampleRate, 500f,  2f);
            SetBiquadBandpass(ref _body2R, sampleRate, 500f,  2f);
            SetBiquadBandpass(ref _body3L, sampleRate, 1500f, 2f);
            SetBiquadBandpass(ref _body3R, sampleRate, 1500f, 2f);

            // Reverb : comb delays choisis coprimes (evite les echoes flutter), L/R legerement decorrelas
            _cL1 = new float[(int)(0.0297 * sampleRate)];  _cR1 = new float[(int)(0.0313 * sampleRate)];
            _cL2 = new float[(int)(0.0371 * sampleRate)];  _cR2 = new float[(int)(0.0389 * sampleRate)];
            _cL3 = new float[(int)(0.0411 * sampleRate)];  _cR3 = new float[(int)(0.0437 * sampleRate)];
            _cL4 = new float[(int)(0.0437 * sampleRate)];  _cR4 = new float[(int)(0.0461 * sampleRate)];
            _apL1 = new float[(int)(0.0053 * sampleRate)]; _apR1 = new float[(int)(0.0061 * sampleRate)];
            _apL2 = new float[(int)(0.0173 * sampleRate)]; _apR2 = new float[(int)(0.0181 * sampleRate)];
            _retrig.Prepare(sampleRate);
        }

        static float ProcessCombLp(float[] buf, ref int idx, ref float lp, float input, float feedback)
        {
            float output = buf[idx];
            lp = 0.2f * output + 0.8f * lp;         // damping HF dans le feedback
            buf[idx] = input + lp * feedback;
            idx++; if (idx >= buf.Length) idx = 0;
            return output;
        }

        static float ProcessAllpass(float[] buf, ref int idx, float input, float coef)
        {
            float delayed = buf[idx];
            float output = -coef * input + delayed;
            buf[idx] = input + coef * output;
            idx++; if (idx >= buf.Length) idx = 0;
            return output;
        }

        public void Reset()
        {
            if (_voices != null) foreach (var v in _voices) v.Kill();
            _body1L.ResetState(); _body1R.ResetState();
            _body2L.ResetState(); _body2R.ResetState();
            _body3L.ResetState(); _body3R.ResetState();
            if (_cL1 != null)
            {
                Array.Clear(_cL1, 0, _cL1.Length); Array.Clear(_cL2, 0, _cL2.Length);
                Array.Clear(_cL3, 0, _cL3.Length); Array.Clear(_cL4, 0, _cL4.Length);
                Array.Clear(_cR1, 0, _cR1.Length); Array.Clear(_cR2, 0, _cR2.Length);
                Array.Clear(_cR3, 0, _cR3.Length); Array.Clear(_cR4, 0, _cR4.Length);
                Array.Clear(_apL1, 0, _apL1.Length); Array.Clear(_apL2, 0, _apL2.Length);
                Array.Clear(_apR1, 0, _apR1.Length); Array.Clear(_apR2, 0, _apR2.Length);
                _iL1 = _iL2 = _iL3 = _iL4 = _iR1 = _iR2 = _iR3 = _iR4 = 0;
                _aL1 = _aL2 = _aR1 = _aR2 = 0;
                _lpL1 = _lpL2 = _lpL3 = _lpL4 = _lpR1 = _lpR2 = _lpR3 = _lpR4 = 0f;
            }
            _pedalDown = false;
            _retrig.Reset();
        }

        // =============================================================================================
        // MIDI
        // =============================================================================================
        public void NoteOn(int note, int velocity, int sampleOffset = 0)
        {
            _retrig.NoteOn(note, velocity);
            if (_voices == null || velocity == 0) return;
            var p = ToVoiceParams();
            float vel = velocity / 127f;

            PianoVoice target = null;
            // Re-strike : meme note deja active → re-pluck (comportement piano)
            for (int i = 0; i < _voices.Length; i++)
                if (_voices[i].IsActive && _voices[i].Note == note) { target = _voices[i]; break; }
            if (target == null)
                for (int i = 0; i < _voices.Length; i++) if (!_voices[i].IsActive) { target = _voices[i]; break; }
            if (target == null)
            {
                target = _voices[_stealCursor];
                _stealCursor = (_stealCursor + 1) % _voices.Length;
                target.Kill();
            }

            float stereoDetune = ((float)(new Random(note).NextDouble() * 2 - 1)) * (float)_stereoWidth.Value * 1.5f;
            target.NoteOn(note, vel, p, stereoDetune);
        }

        public void NoteOff(int note, int sampleOffset = 0)
        {
            _retrig.NoteOff(note);
            if (_voices == null) return;
            var p = ToVoiceParams();
            // Pedale de sustain (via CC64 OU parametre) : bloque le damper
            if (_pedalDown) p.SustainPedal = 1f;
            for (int i = 0; i < _voices.Length; i++)
                if (_voices[i].IsActive && _voices[i].Note == note)
                    _voices[i].NoteOff(p);
        }

        public void MidiCC(int cc, int value, int sampleOffset = 0)
        {
            if (cc == 123) { Reset(); return; }
            if (cc == 64)  // Damper pedal MIDI standard
            {
                bool nowDown = value >= 64;
                bool wasDown = _pedalDown;
                _pedalDown = nowDown;
                // Relachement de la pedale : declencher le damper sur les notes non tenues
                // (comportement piano). Pour la v1, on ne track pas les notes "held by finger",
                // donc on laisse le decay naturel jouer.
                _ = wasDown;
            }
        }

        public void SetPitchBend(float value, int sampleOffset = 0) { /* Karplus ne supporte pas bien le bend continu */ }

        // =============================================================================================
        // Render
        // =============================================================================================
        public void Render(Span<float> left, Span<float> right)
        {
            if (_voices == null) { left.Clear(); right.Clear(); return; }

            var p = ToVoiceParams();
            if (_pedalDown) p.SustainPedal = 1f;
            float volLin = (float)Math.Pow(10.0, p.VolumeDb / 20.0);
            float widthGain = p.StereoWidth;
            float body = p.Body;
            float dry = 1f - body * 0.4f;

            int n = left.Length;
            for (int i = 0; i < n; i++)
            {
                // Ré-attaque : à l'échéance, la note tenue est rejouée (BeginStroke neutralise
                // la notification que NoteOn va renvoyer à l'engin).
                if (_retrig.Tick()) { _retrig.BeginStroke(); for (int rt = 0; rt < _retrig.Count; rt++) NoteOn(_retrig.NoteAt(rt), _retrig.VelocityAt(rt)); _retrig.EndStroke(); }

                float sum = 0f;
                for (int v = 0; v < _voices.Length; v++)
                    if (_voices[v].IsActive) sum += _voices[v].RenderSample(p);

                // Soft-clip tanh doux : evite le clipping brutal quand la pedale de sustain accumule
                // beaucoup de voix (10-16 cordes vibrantes) → transforme la saturation lineaire en
                // "colle" analogique naturelle (comme la table qui compresse en vrai piano).
                sum = (float)Math.Tanh(sum * 0.7);

                // 3 peaks du soundboard (120/500/1500 Hz), sommes puis mixes au dry
                float b1L = BiquadProcess(ref _body1L, sum);
                float b1R = BiquadProcess(ref _body1R, sum);
                float b2L = BiquadProcess(ref _body2L, sum);
                float b2R = BiquadProcess(ref _body2R, sum);
                float b3L = BiquadProcess(ref _body3L, sum);
                float b3R = BiquadProcess(ref _body3R, sum);
                float bodyOutL = (b1L * 0.6f + b2L * 0.5f + b3L * 0.4f);
                float bodyOutR = (b1R * 0.6f + b2R * 0.5f + b3R * 0.4f);
                float wetL = sum * dry + bodyOutL * body;
                float wetR = sum * dry + bodyOutR * body;

                // Soft-clip post-body : les peaks resonants peuvent facilement pousser au-dela
                // de 1.0 sur des attaques accumulees → clipping du buffer WPF = parasites.
                wetL = (float)Math.Tanh(wetL * 0.85);
                wetR = (float)Math.Tanh(wetR * 0.85);

                // Reverb Schroeder mini (natif au plugin) : 4 combs LP-feedback en parallele + 2 AP en serie
                float reverbMix = p.Reverb;
                if (reverbMix > 0.001f)
                {
                    float reverbFb = 0.50f + reverbMix * 0.32f;   // 0.50..0.82 → decay ~200ms..2s
                    float cL = (ProcessCombLp(_cL1, ref _iL1, ref _lpL1, wetL, reverbFb)
                              + ProcessCombLp(_cL2, ref _iL2, ref _lpL2, wetL, reverbFb)
                              + ProcessCombLp(_cL3, ref _iL3, ref _lpL3, wetL, reverbFb)
                              + ProcessCombLp(_cL4, ref _iL4, ref _lpL4, wetL, reverbFb)) * 0.25f;
                    float cR = (ProcessCombLp(_cR1, ref _iR1, ref _lpR1, wetR, reverbFb)
                              + ProcessCombLp(_cR2, ref _iR2, ref _lpR2, wetR, reverbFb)
                              + ProcessCombLp(_cR3, ref _iR3, ref _lpR3, wetR, reverbFb)
                              + ProcessCombLp(_cR4, ref _iR4, ref _lpR4, wetR, reverbFb)) * 0.25f;
                    float rL = ProcessAllpass(_apL2, ref _aL2, ProcessAllpass(_apL1, ref _aL1, cL, 0.5f), 0.5f);
                    float rR = ProcessAllpass(_apR2, ref _aR2, ProcessAllpass(_apR1, ref _aR1, cR, 0.5f), 0.5f);
                    float w = reverbMix * 0.5f;                   // max 50% wet (subtil)
                    wetL = wetL * (1f - w) + rL * w;
                    wetR = wetR * (1f - w) + rR * w;
                }

                float mid = 0.5f * (wetL + wetR);
                float side = wetL - wetR;
                left[i]  = (mid + side * widthGain) * volLin;
                right[i] = (mid - side * widthGain) * volLin;
            }
        }

        PianoParams ToVoiceParams() => new PianoParams
        {
            HammerHardness = (float)_hammerHardness.Value,
            HammerAmount   = (float)_hammerAmount.Value,
            Brightness     = (float)_brightness.Value,
            Inharmonicity  = (float)_inharmonicity.Value,
            StringDetune   = (float)_stringDetune.Value,
            DamperTime     = (float)_damperTime.Value,
            SustainPedal   = (float)_sustainPedal.Value,
            Body           = (float)_body.Value,
            Reverb         = (float)_reverb.Value,
            StereoWidth    = (float)_stereoWidth.Value,
            VolumeDb       = (float)_volumeDb.Value,
        };

        // =============================================================================================
        // Presets
        // =============================================================================================
        public static readonly string[] PresetNames =
        {
            "Piano acoustique", "Piano intime (dolce)", "Piano brillant (forte)", "Piano honky-tonk",
            "Chapman Stick (bass)", "Chapman Stick (melody)", "Chapman Stick (dual)",
        };

        // Chapman Stick : instrument tap a 10-12 cordes (bass + melody). Le tap = attaque percussive
        // proche du hammer piano, mais UNE seule corde par note (pas 2-3), sustain quasi-infini
        // (pas d'etouffoir mecanique), corps electrique (pas de caisse acoustique), inharmonicite
        // faible (corde metal courte, spectre proche harmonique).
        //   - damper_time ~0.85-0.90 : sustain tres long
        //   - string_detune ~0.10   : mono-corde, quasiment pas de chorus
        //   - inharmonicity ~0.05   : proche corde ideale
        //   - body ~0.08            : quasi zero (pas de table d'harmonie)
        //   - brightness varie selon role : bass sombre, melody brillant
        static readonly double[,] PresetValues =
        {
            //                          hardness hamAmt bright inharm detune damper pedal body reverb width volDb
            /*Acoustique*/             { 0.45, 0.35, 0.60, 0.15, 0.35, 0.30, 0.00, 0.25, 0.15, 0.35, -6.0 },
            /*Dolce (intime)*/         { 0.25, 0.25, 0.40, 0.10, 0.25, 0.40, 0.00, 0.30, 0.25, 0.30, -8.0 },
            /*Forte (brillant)*/       { 0.75, 0.55, 0.85, 0.20, 0.45, 0.25, 0.00, 0.20, 0.10, 0.40, -4.0 },
            /*Honky-tonk*/             { 0.60, 0.45, 0.70, 0.35, 0.90, 0.25, 0.00, 0.15, 0.05, 0.45, -6.0 },
            /*Chapman Stick (bass)*/   { 0.55, 0.28, 0.50, 0.04, 0.10, 0.90, 0.00, 0.08, 0.30, 0.35, -4.0 },
            /*Chapman Stick (melody)*/ { 0.65, 0.22, 0.85, 0.05, 0.10, 0.85, 0.00, 0.08, 0.35, 0.40, -5.0 },
            /*Chapman Stick (dual)*/   { 0.60, 0.25, 0.72, 0.05, 0.10, 0.87, 0.00, 0.08, 0.32, 0.38, -4.5 },
        };

        public void LoadPreset(int index)
        {
            if (index < 0 || index >= PresetValues.GetLength(0)) return;
            _hammerHardness.Value = PresetValues[index, 0];
            _hammerAmount.Value   = PresetValues[index, 1];
            _brightness.Value     = PresetValues[index, 2];
            _inharmonicity.Value  = PresetValues[index, 3];
            _stringDetune.Value   = PresetValues[index, 4];
            _damperTime.Value     = PresetValues[index, 5];
            _sustainPedal.Value   = PresetValues[index, 6];
            _body.Value           = PresetValues[index, 7];
            _reverb.Value         = PresetValues[index, 8];
            _stereoWidth.Value    = PresetValues[index, 9];
            _volumeDb.Value       = PresetValues[index, 10];
        }

        public void SetParam(string id, double value)
        {
            foreach (var kp in _params)
                if (kp.Id == id) { kp.Value = value; return; }
        }

        // =============================================================================================
        // Persistance
        // =============================================================================================
        public byte[] SaveState()
        {
            try
            {
                var dict = new Dictionary<string, double>();
                foreach (var kp in _params) dict[kp.Id] = kp.Value;
                return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(dict));
            }
            catch { return Array.Empty<byte>(); }
        }

        public void LoadState(byte[] state)
        {
            if (state == null || state.Length == 0) return;
            try
            {
                var dict = JsonSerializer.Deserialize<Dictionary<string, double>>(Encoding.UTF8.GetString(state));
                if (dict == null) return;
                foreach (var kp in _params)
                    if (dict.TryGetValue(kp.Id, out var v)) kp.Value = v;
            }
            catch { }
        }

        public void Dispose() { }

        // =============================================================================================
        // Biquad bandpass RBJ
        // =============================================================================================
        struct BiquadState
        {
            public float b0, b1, b2, a1, a2;
            public float x1, x2, y1, y2;
            public void ResetState() { x1 = x2 = y1 = y2 = 0f; }
        }
        static void SetBiquadBandpass(ref BiquadState s, int sr, float freq, float q)
        {
            double w0 = 2.0 * Math.PI * freq / sr, alpha = Math.Sin(w0) / (2.0 * q), cosw0 = Math.Cos(w0);
            double a0 = 1.0 + alpha;
            s.b0 = (float)(alpha / a0);
            s.b1 = 0f;
            s.b2 = (float)(-alpha / a0);
            s.a1 = (float)(-2.0 * cosw0 / a0);
            s.a2 = (float)((1.0 - alpha) / a0);
            s.ResetState();
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
