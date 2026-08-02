using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Windows.Controls;
using KotonStudio.Library;

namespace KotonPluginNylonGuitar
{
    /// <summary>
    /// Nylon Guitar — guitare classique orientée son rond, riche en harmoniques. Karplus-Strong
    /// avec les optimisations qui font le "beau son classique" :
    ///
    /// - **Excitation nylon** : bruit blanc filtré LP selon PluckSoftness (pulpe molle ≠ ongle dur)
    ///   + comb filter selon PluckPosition (position de pincement, physique réelle de la corde)
    /// - **Body resonance à 2 formants** : ~200 Hz + ~800 Hz, les deux modes principaux d'une
    ///   caisse de guitare classique (mesurés sur guitare Ramírez). C'est CE qui donne le son
    ///   "rond riche" par opposition à un simple KS.
    /// - **Feedback très haut** (~0.998) → sustain long caractéristique nylon
    /// - **All-pass léger** (Stiffness) pour un mordant subtil sans casser le nylon
    ///
    /// **Différence avec KarplusStrong classique** :
    /// - KS classique = corde générique (guitare, harpe, koto — configurable)
    /// - Nylon Guitar = focalisé nylon avec DOUBLE body resonance en sortie qui donne le vrai
    ///   caractère "guitare classique" que KS générique n'atteint pas facilement
    ///
    /// **Presets** : Guitare classique (rond), Flamenca (brillante), Folk steel (brillante+dure),
    /// Bandoneon nylon, Ukulele, Charango.
    /// </summary>
    [KotonInstrument("Nylon Guitar", Id = "koton.nylonguitar", Category = "Physical Model", Version = "1.0", Vendor = "Koton Studio")]
    public sealed class NylonGuitarPlugin : IKotonInstrument
    {
        public string Id => "koton.nylonguitar";
        public string DisplayName => "Nylon Guitar";

        readonly KotonParameter _instrument     = new KotonParameter("instrument",      "Instrument",      0, 5, 0);
        readonly KotonParameter _pluckSoftness  = new KotonParameter("pluck_softness",  "Pluck softness",  0.0, 1.0, 0.55);
        readonly KotonParameter _pluckPosition  = new KotonParameter("pluck_position",  "Pluck position",  0.05, 0.4, 0.20);
        readonly KotonParameter _sustain        = new KotonParameter("sustain",         "Sustain",         0.0, 1.0, 0.70);
        readonly KotonParameter _brightness     = new KotonParameter("brightness",      "Brightness",      0.0, 1.0, 0.55);
        readonly KotonParameter _stiffness      = new KotonParameter("stiffness",       "Stiffness",       0.0, 1.0, 0.10);
        readonly KotonParameter _bodyMix        = new KotonParameter("body_mix",        "Body",            0.0, 1.0, 0.60);
        readonly KotonParameter _stereoSpread   = new KotonParameter("stereo_spread",   "Stereo spread",   0.0, 1.0, 0.30);
        readonly KotonParameter _volumeDb       = new KotonParameter("volume",          "Volume",          -30.0, 6.0, -3.0, "dB");

        readonly List<KotonParameter> _params;
        public IReadOnlyList<KotonParameter> Parameters => _params;

        int _sampleRate;
        NylonGuitarVoice[] _voices;
        int _stealCursor;
        const int Polyphony = 12;

        // Body resonance = 2 peak biquads en série (les 2 modes principaux caisse guitare classique)
        BiquadPeakState _body1L, _body1R;
        BiquadPeakState _body2L, _body2R;
        int _lastInstrument = -1;

        // (f1, q1, g1, f2, q2, g2) par instrument
        static readonly (float f1, float q1, float g1, float f2, float q2, float g2)[] BodyByInstrument = new (float, float, float, float, float, float)[]
        {
            /* Nylon classique */  (200f, 2.5f, 8f,    800f, 2.0f, 6f),   // caisse Ramírez rond
            /* Flamenca         */ (250f, 2.0f, 6f,    1200f, 2.5f, 8f),  // tapa mate + snappy
            /* Folk steel       */ (180f, 2.0f, 7f,    2500f, 2.0f, 5f),  // dreadnought grave + brillance
            /* Bandoneon nylon  */ (300f, 3.0f, 10f,   1000f, 2.5f, 6f),  // très resonant medium
            /* Ukulele          */ (400f, 2.0f, 5f,    1500f, 2.0f, 6f),  // petite caisse aigue
            /* Charango         */ (600f, 2.5f, 4f,    2000f, 2.5f, 6f),  // andine, tres aigu
        };

        public NylonGuitarPlugin()
        {
            _params = new List<KotonParameter>
            {
                _instrument, _pluckSoftness, _pluckPosition, _sustain, _brightness,
                _stiffness, _bodyMix, _stereoSpread, _volumeDb,
            };
        }

        public bool HasEditor => true;
        public UserControl CreateEditor() => new NylonGuitarEditor(this);

        public static readonly string[] InstrumentNames = { "Guitare classique", "Guitare flamenca", "Guitare folk (steel)", "Bandoneon nylon", "Ukulele", "Charango" };

        // Chaque instrument definit aussi ses defaults de string params (attaque, softness, sustain, etc.)
        static readonly double[,] StringDefaults = {
            //          pSoft pPos sust brgh stiff body spread vol
            /*Classique*/ { 0.60, 0.20, 0.75, 0.55, 0.08, 0.65, 0.30, -3.0 },
            /*Flamenca*/  { 0.30, 0.12, 0.55, 0.70, 0.12, 0.55, 0.25, -3.0 },
            /*Folk*/      { 0.35, 0.15, 0.80, 0.75, 0.15, 0.60, 0.25, -3.0 },
            /*Bandoneon*/ { 0.55, 0.18, 0.85, 0.50, 0.10, 0.75, 0.35, -3.0 },
            /*Ukulele*/   { 0.50, 0.22, 0.50, 0.65, 0.05, 0.50, 0.20, -4.0 },
            /*Charango*/  { 0.40, 0.15, 0.55, 0.70, 0.08, 0.55, 0.20, -4.0 },
        };

        public void ApplyInstrumentDefaults(int index)
        {
            if (index < 0 || index >= StringDefaults.GetLength(0)) return;
            _pluckSoftness.Value = StringDefaults[index, 0];
            _pluckPosition.Value = StringDefaults[index, 1];
            _sustain.Value       = StringDefaults[index, 2];
            _brightness.Value    = StringDefaults[index, 3];
            _stiffness.Value     = StringDefaults[index, 4];
            _bodyMix.Value       = StringDefaults[index, 5];
            _stereoSpread.Value  = StringDefaults[index, 6];
            _volumeDb.Value      = StringDefaults[index, 7];
        }

        public void Prepare(int sampleRate, int maxBlockSize)
        {
            _sampleRate = sampleRate;
            _voices = new NylonGuitarVoice[Polyphony];
            for (int i = 0; i < Polyphony; i++) _voices[i] = new NylonGuitarVoice(sampleRate);
        }
        public void Reset()
        {
            if (_voices != null) foreach (var v in _voices) v.Kill();
            _body1L.ResetState(); _body1R.ResetState();
            _body2L.ResetState(); _body2R.ResetState();
        }

        public void NoteOn(int note, int velocity, int sampleOffset = 0)
        {
            if (_voices == null || velocity == 0) return;
            var p = ToVoiceParams();
            float vel = velocity / 127f;

            NylonGuitarVoice target = null;
            for (int i = 0; i < _voices.Length; i++) if (_voices[i].IsActive && _voices[i].Note == note) { target = _voices[i]; break; }
            if (target == null) for (int i = 0; i < _voices.Length; i++) if (!_voices[i].IsActive) { target = _voices[i]; break; }
            if (target == null) { target = _voices[_stealCursor]; _stealCursor = (_stealCursor + 1) % _voices.Length; target.Kill(); }

            float noteNorm = (note - 60) / 24f;
            if (noteNorm < -1f) noteNorm = -1f; else if (noteNorm > 1f) noteNorm = 1f;
            float pan = noteNorm * (float)_stereoSpread.Value;
            target.NoteOn(note, vel, p, pan);
        }
        public void NoteOff(int note, int sampleOffset = 0) { }
        public void MidiCC(int cc, int value, int sampleOffset = 0) { if (cc == 123) Reset(); }
        public void SetPitchBend(float value, int sampleOffset = 0) { }

        public void Render(Span<float> left, Span<float> right)
        {
            if (_voices == null) { left.Clear(); right.Clear(); return; }
            var p = ToVoiceParams();
            float volLin = (float)Math.Pow(10.0, _volumeDb.Value / 20.0);
            float bodyMix = (float)_bodyMix.Value;
            float dry = 1f - bodyMix * 0.5f;

            // Update body resonances si l'instrument a change
            int instrIdx = Math.Max(0, Math.Min(BodyByInstrument.Length - 1, (int)_instrument.Value));
            if (instrIdx != _lastInstrument)
            {
                var (f1, q1, g1, f2, q2, g2) = BodyByInstrument[instrIdx];
                SetBiquadPeak(ref _body1L, _sampleRate, f1, q1, g1);
                SetBiquadPeak(ref _body1R, _sampleRate, f1, q1, g1);
                SetBiquadPeak(ref _body2L, _sampleRate, f2, q2, g2);
                SetBiquadPeak(ref _body2R, _sampleRate, f2, q2, g2);
                _lastInstrument = instrIdx;
            }

            int n = left.Length;
            for (int i = 0; i < n; i++)
            {
                float sumL = 0f, sumR = 0f;
                for (int v = 0; v < _voices.Length; v++)
                {
                    var voice = _voices[v];
                    if (!voice.IsActive) continue;
                    float s = voice.RenderSample(p);
                    sumL += s * voice.PanL;
                    sumR += s * voice.PanR;
                }
                // Body resonances : peak biquads en serie appliques au mix (donne le caractere caisse)
                float b1L = BiquadPeakProcess(ref _body1L, sumL);
                float b1R = BiquadPeakProcess(ref _body1R, sumR);
                float b2L = BiquadPeakProcess(ref _body2L, b1L);
                float b2R = BiquadPeakProcess(ref _body2R, b1R);
                float outL = sumL * dry + b2L * bodyMix;
                float outR = sumR * dry + b2R * bodyMix;
                left[i] = outL * volLin;
                right[i] = outR * volLin;
            }
        }

        NgParams ToVoiceParams() => new NgParams
        {
            PluckSoftness = (float)_pluckSoftness.Value,
            PluckPosition = (float)_pluckPosition.Value,
            Sustain       = (float)_sustain.Value,
            Brightness    = (float)_brightness.Value,
            Stiffness     = (float)_stiffness.Value,
            VolumeDb      = (float)_volumeDb.Value,
        };

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
                foreach (var kp in _params) if (dict.TryGetValue(kp.Id, out var v)) kp.Value = v;
            }
            catch { }
        }
        public void Dispose() { }

        public void SetParam(string id, double value)
        {
            foreach (var kp in _params) if (kp.Id == id) { kp.Value = value; return; }
        }

        // Biquad peak RBJ
        internal struct BiquadPeakState
        {
            public float b0, b1, b2, a1, a2;
            public float x1, x2, y1, y2;
            public void ResetState() { x1 = x2 = y1 = y2 = 0f; }
        }
        static void SetBiquadPeak(ref BiquadPeakState s, int sr, float freq, float q, float dbGain)
        {
            double A = Math.Pow(10.0, dbGain / 40.0);
            double w0 = 2.0 * Math.PI * freq / sr;
            double alpha = Math.Sin(w0) / (2.0 * q);
            double cosw0 = Math.Cos(w0);
            double a0 = 1.0 + alpha / A;
            s.b0 = (float)((1.0 + alpha * A) / a0);
            s.b1 = (float)((-2.0 * cosw0) / a0);
            s.b2 = (float)((1.0 - alpha * A) / a0);
            s.a1 = (float)((-2.0 * cosw0) / a0);
            s.a2 = (float)((1.0 - alpha / A) / a0);
        }
        static float BiquadPeakProcess(ref BiquadPeakState s, float x)
        {
            float y = s.b0 * x + s.b1 * s.x1 + s.b2 * s.x2 - s.a1 * s.y1 - s.a2 * s.y2;
            s.x2 = s.x1; s.x1 = x;
            s.y2 = s.y1; s.y1 = y;
            return y;
        }
    }
}
