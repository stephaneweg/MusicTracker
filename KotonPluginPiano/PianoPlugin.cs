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
        readonly KotonParameter _stereoWidth     = new KotonParameter("stereo_width",     "Stereo width",     0.0, 1.0, 0.35);
        readonly KotonParameter _volumeDb        = new KotonParameter("volume",           "Volume",           -30.0, 6.0, -6.0, "dB");

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

        // Sustain pedal via CC64 (>63 = enfoncee)
        bool _pedalDown;

        public PianoPlugin()
        {
            _params = new List<KotonParameter>
            {
                _hammerHardness, _hammerAmount, _brightness, _inharmonicity, _stringDetune,
                _damperTime, _sustainPedal, _body, _stereoWidth, _volumeDb,
            };
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
        }

        public void Reset()
        {
            if (_voices != null) foreach (var v in _voices) v.Kill();
            _body1L.ResetState(); _body1R.ResetState();
            _body2L.ResetState(); _body2R.ResetState();
            _body3L.ResetState(); _body3R.ResetState();
            _pedalDown = false;
        }

        // =============================================================================================
        // MIDI
        // =============================================================================================
        public void NoteOn(int note, int velocity, int sampleOffset = 0)
        {
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

                // Soft-clip final post-body : les peaks resonants peuvent facilement pousser au-dela
                // de 1.0 sur des attaques accumulees → clipping du buffer WPF = parasites.
                wetL = (float)Math.Tanh(wetL * 0.85);
                wetR = (float)Math.Tanh(wetR * 0.85);

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
            StereoWidth    = (float)_stereoWidth.Value,
            VolumeDb       = (float)_volumeDb.Value,
        };

        // =============================================================================================
        // Presets
        // =============================================================================================
        public static readonly string[] PresetNames =
        {
            "Piano acoustique", "Piano intime (dolce)", "Piano brillant (forte)", "Piano honky-tonk",
        };

        static readonly double[,] PresetValues =
        {
            //                    hardness hamAmt bright inharm detune damper pedal body width volDb
            /*Acoustique*/       { 0.45, 0.35, 0.60, 0.15, 0.35, 0.30, 0.00, 0.25, 0.35, -6.0 },
            /*Dolce (intime)*/   { 0.25, 0.25, 0.40, 0.10, 0.25, 0.40, 0.00, 0.30, 0.30, -8.0 },
            /*Forte (brillant)*/ { 0.75, 0.55, 0.85, 0.20, 0.45, 0.25, 0.00, 0.20, 0.40, -4.0 },
            /*Honky-tonk*/       { 0.60, 0.45, 0.70, 0.35, 0.90, 0.25, 0.00, 0.15, 0.45, -6.0 },
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
            _stereoWidth.Value    = PresetValues[index, 8];
            _volumeDb.Value       = PresetValues[index, 9];
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
