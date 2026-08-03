using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Windows.Controls;
using KotonStudio.Library;

namespace KotonPluginElectricGuitar
{
    /// <summary>
    /// Electric Guitar — inspiré de FAUST physmodels.lib/electricGuitar.lib. Karplus-Strong étendu
    /// avec :
    ///
    /// 1. **String bridge filter** : LP dans la boucle qui simule la perte HF au chevalet (KS classique)
    /// 2. **Pluck position** : filtre comb qui atténue les harmoniques dont un nœud est à la position
    ///    de pincement (physique correcte de la corde)
    /// 3. **Pluck attack** : bruit initial filtré selon la dureté du médiator (dur = brillant, feutre = doux)
    /// 4. **Pickup position** : 2e filtre comb qui simule le pickup magnétique placé à une position
    ///    donnée sous la corde (rend le son "bright" vs "dark" comme sur une Strat vs une Les Paul)
    /// 5. **Tone stack** : LP RC simple (comme le potard tone d'une vraie guitare)
    /// 6. **Amp saturation** : tanh drive avec compensation gain, simule un ampli à lampes crunchy
    /// 7. **Body** : petit filtre bandpass ~150 Hz Q=3 (résonance du corps de la guitare électrique,
    ///    beaucoup moins prononcée qu'une acoustique — le solid body absorbe)
    ///
    /// **Différence avec Nylon Guitar** : la nylon utilise un pluck feutré, un body prononcé, pas de
    /// pickup ni d'amp. L'EG utilise médiator dur, pas de body réel (solid body), mais un pickup
    /// magnétique + un ampli qui définit tout le son.
    /// </summary>
    [KotonInstrument("Electric Guitar", Id = "koton.electricguitar", Category = "Physical Model", Version = "1.0", Vendor = "Koton Studio")]
    public sealed class ElectricGuitarPlugin : IKotonInstrument
    {
        public string Id => "koton.electricguitar";
        public string DisplayName => "Electric Guitar";

        readonly KotonParameter _pluckPosition = new KotonParameter("pluck_position", "Pluck position", 0.02, 0.5, 0.15);
        readonly KotonParameter _pluckHardness = new KotonParameter("pluck_hardness", "Pluck hardness", 0.0, 1.0, 0.75);
        readonly KotonParameter _pickupPosition= new KotonParameter("pickup_position","Pickup position",0.05, 0.4, 0.20);
        readonly KotonParameter _damping       = new KotonParameter("damping",        "String damping", 0.0, 1.0, 0.40);
        readonly KotonParameter _sustain       = new KotonParameter("sustain",        "Sustain",        0.0, 1.0, 0.35);
        readonly KotonParameter _tone          = new KotonParameter("tone",           "Tone (LP amp)",  0.0, 1.0, 0.65);
        readonly KotonParameter _drive         = new KotonParameter("drive",          "Drive (amp)",    0.0, 1.0, 0.40);
        readonly KotonParameter _body          = new KotonParameter("body",           "Body",           0.0, 1.0, 0.20);
        readonly KotonParameter _stereoWidth   = new KotonParameter("stereo_width",   "Stereo width",   0.0, 1.0, 0.30);
        readonly KotonParameter _volumeDb      = new KotonParameter("volume",         "Volume",         -30.0, 6.0, -4.0, "dB");

        readonly List<KotonParameter> _params;
        public IReadOnlyList<KotonParameter> Parameters => _params;

        int _sr;
        GuitarVoice[] _voices;
        int _stealCursor;
        const int Polyphony = 8;

        // Body : biquad bandpass partagé (résonance corps solide, faible mais présente)
        BiquadState _bodyL, _bodyR;

        public ElectricGuitarPlugin()
        {
            _params = new List<KotonParameter>
            {
                _pluckPosition, _pluckHardness, _pickupPosition, _damping, _sustain,
                _tone, _drive, _body, _stereoWidth, _volumeDb,
            };
        }

        public bool HasEditor => true;
        public UserControl CreateEditor() => new ElectricGuitarEditor(this);

        public void Prepare(int sampleRate, int maxBlockSize)
        {
            _sr = sampleRate;
            _voices = new GuitarVoice[Polyphony];
            for (int i = 0; i < Polyphony; i++) _voices[i] = new GuitarVoice(sampleRate);
            SetBiquadBandpass(ref _bodyL, sampleRate, 150f, 3f);
            SetBiquadBandpass(ref _bodyR, sampleRate, 150f, 3f);
        }
        public void Reset()
        {
            if (_voices != null) foreach (var v in _voices) v.Kill();
            _bodyL.ResetState(); _bodyR.ResetState();
        }

        public void NoteOn(int note, int velocity, int sampleOffset = 0)
        {
            if (_voices == null || velocity == 0) return;
            float vel = velocity / 127f;

            GuitarVoice target = null;
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
            // Pan par note (basses gauche / aigus droite)
            float noteNorm = (note - 60) / 12f;
            if (noteNorm < -1f) noteNorm = -1f; else if (noteNorm > 1f) noteNorm = 1f;
            float pan = noteNorm * (float)_stereoWidth.Value;

            target.NoteOn(note, vel,
                (float)_pluckPosition.Value, (float)_pluckHardness.Value,
                (float)_pickupPosition.Value, (float)_damping.Value,
                (float)_sustain.Value, pan);
        }
        public void NoteOff(int note, int sampleOffset = 0) { /* la corde décroit naturellement */ }
        public void MidiCC(int cc, int value, int sampleOffset = 0)
        {
            if (cc == 123) Reset();
        }
        public void SetPitchBend(float value, int sampleOffset = 0) { }

        public void Render(Span<float> left, Span<float> right)
        {
            if (_voices == null) { left.Clear(); right.Clear(); return; }

            float tone = (float)_tone.Value;
            float drive = (float)_drive.Value;
            float body = (float)_body.Value;
            float volLin = (float)Math.Pow(10.0, _volumeDb.Value / 20.0);

            // Tone stack : LP simple RC piloté par tone (0=très sombre 500Hz, 1=brillant 6000Hz)
            float toneCutoff = 500f + tone * 5500f;
            float toneAlpha = 1f - (float)Math.Exp(-2.0 * Math.PI * toneCutoff / _sr);

            int n = left.Length;
            for (int i = 0; i < n; i++)
            {
                float sumL = 0f, sumR = 0f;
                for (int v = 0; v < _voices.Length; v++)
                {
                    var voice = _voices[v];
                    if (!voice.IsActive) continue;
                    float s = voice.RenderSample(tone, toneAlpha);
                    sumL += s * voice.PanL;
                    sumR += s * voice.PanR;
                }

                // Amp saturation (drive) : tanh avec compensation gain
                if (drive > 0.01f)
                {
                    float d = 1f + drive * 8f;   // gain avant tanh
                    sumL = (float)Math.Tanh(sumL * d) / (1f + drive * 3f);
                    sumR = (float)Math.Tanh(sumR * d) / (1f + drive * 3f);
                }

                // Body : bandpass mix (petite résonance corps solide)
                if (body > 0.01f)
                {
                    float bpL = BiquadProcess(ref _bodyL, sumL);
                    float bpR = BiquadProcess(ref _bodyR, sumR);
                    sumL += bpL * body * 0.4f;
                    sumR += bpR * body * 0.4f;
                }

                left[i] = sumL * volLin;
                right[i] = sumR * volLin;
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
            "Strat clean (Knopfler)", "Les Paul warm", "Tele twang", "Jazz box (semi-hollow)",
            "Rock crunch", "Blues drive", "Lead solo (saturé)", "Clean pop bright"
        };
        static readonly double[,] PresetValues = {
            //             pluckP pluckH pickup damp sust tone drive body wid  vol
            /*Strat*/      { 0.15, 0.75, 0.25, 0.40, 0.35, 0.75, 0.05, 0.15, 0.30, -4.0 },
            /*LesPaul*/    { 0.18, 0.60, 0.15, 0.45, 0.45, 0.55, 0.15, 0.25, 0.25, -3.0 },
            /*Tele*/       { 0.12, 0.85, 0.30, 0.35, 0.30, 0.85, 0.10, 0.10, 0.30, -4.0 },
            /*Jazzbox*/    { 0.20, 0.55, 0.18, 0.55, 0.45, 0.50, 0.05, 0.40, 0.25, -3.0 },
            /*Crunch*/     { 0.15, 0.75, 0.20, 0.40, 0.40, 0.65, 0.45, 0.15, 0.30, -3.0 },
            /*Blues*/      { 0.18, 0.65, 0.22, 0.45, 0.45, 0.55, 0.55, 0.20, 0.25, -3.0 },
            /*Lead*/       { 0.12, 0.80, 0.15, 0.35, 0.55, 0.60, 0.85, 0.10, 0.35, -3.0 },
            /*CleanPop*/   { 0.15, 0.80, 0.28, 0.40, 0.30, 0.85, 0.00, 0.10, 0.30, -4.0 },
        };
        public void LoadPreset(int idx)
        {
            if (idx < 0 || idx >= PresetValues.GetLength(0)) return;
            _pluckPosition.Value = PresetValues[idx, 0]; _pluckHardness.Value = PresetValues[idx, 1];
            _pickupPosition.Value = PresetValues[idx, 2]; _damping.Value = PresetValues[idx, 3];
            _sustain.Value = PresetValues[idx, 4]; _tone.Value = PresetValues[idx, 5];
            _drive.Value = PresetValues[idx, 6]; _body.Value = PresetValues[idx, 7];
            _stereoWidth.Value = PresetValues[idx, 8]; _volumeDb.Value = PresetValues[idx, 9];
        }
        public void SetParam(string id, double value)
        {
            foreach (var kp in _params) if (kp.Id == id) { kp.Value = value; return; }
        }

        internal struct BiquadState
        {
            public float b0, b1, b2, a1, a2;
            public float x1, x2, y1, y2;
            public void ResetState() { x1 = x2 = y1 = y2 = 0f; }
        }
        internal static void SetBiquadBandpass(ref BiquadState s, int sr, float freq, float q)
        {
            double w0 = 2.0 * Math.PI * freq / sr;
            double alpha = Math.Sin(w0) / (2.0 * q);
            double cosw0 = Math.Cos(w0);
            double a0 = 1.0 + alpha;
            s.b0 = (float)(alpha / a0); s.b1 = 0f; s.b2 = (float)(-alpha / a0);
            s.a1 = (float)(-2.0 * cosw0 / a0); s.a2 = (float)((1.0 - alpha) / a0);
        }
        internal static float BiquadProcess(ref BiquadState s, float x)
        {
            float y = s.b0 * x + s.b1 * s.x1 + s.b2 * s.x2 - s.a1 * s.y1 - s.a2 * s.y2;
            s.x2 = s.x1; s.x1 = x;
            s.y2 = s.y1; s.y1 = y;
            return y;
        }
    }

    internal sealed class GuitarVoice
    {
        readonly int _sr;
        readonly float[] _buffer;
        int _size;
        int _writeIdx;
        float _lpPrev;   // KS LP
        float _toneState;

        bool _active;
        int _note;
        public bool IsActive => _active;
        public int Note => _note;
        public float PanL, PanR;

        // Feedback gain for sustain (0..1 → 0.985..0.9995)
        float _feedbackGain;
        // Pickup comb : offset en samples (filtrage comb en sortie)
        int _pickupOffset;

        Random _rng;
        const float SilenceThreshold = 1e-5f;
        float _peakEnvelope;

        public GuitarVoice(int sampleRate)
        {
            _sr = sampleRate;
            _buffer = new float[Math.Max(sampleRate / 20, 4096)];
        }

        public void NoteOn(int note, float velocity, float pluckPos, float pluckHard, float pickupPos, float damping, float sustain, float pan)
        {
            _note = note;
            _rng = new Random(note * 7919 + Environment.TickCount);
            double freq = 440.0 * Math.Pow(2.0, (note - 69) / 12.0);
            _size = Math.Max(4, Math.Min(_buffer.Length, (int)Math.Round(_sr / freq)));

            float p01 = 0.5f * (1f + Math.Max(-1f, Math.Min(1f, pan)));
            PanL = 1f - p01; PanR = p01;

            // Bruit initial pour l'excitation (impulsion filtrée par pluck hardness)
            for (int i = 0; i < _size; i++)
                _buffer[i] = (float)((_rng.NextDouble() * 2 - 1) * velocity);

            // Filtre LP en 1 pass sur le bruit : simule la dureté du médiator
            // pluckHard 0 = feutre (LP fort, aigus atténués), 1 = plectre dur (bruit blanc préservé)
            if (pluckHard < 1f)
            {
                float coef = 1f - pluckHard;
                float alpha = 0.15f + 0.6f * coef;
                float lp = 0f;
                for (int i = 0; i < _size; i++)
                {
                    lp += alpha * (_buffer[i] - lp);
                    _buffer[i] = pluckHard * _buffer[i] + coef * lp;
                }
            }

            // Filtre comb pluck position : atténue les harmoniques dont le nœud est à cette position
            int combLen = Math.Max(1, (int)(_size * pluckPos));
            if (combLen > 0 && combLen < _size)
            {
                var tmp = new float[_size];
                for (int i = 0; i < _size; i++)
                {
                    int ci = (i - combLen + _size) % _size;
                    tmp[i] = _buffer[i] - _buffer[ci];
                }
                for (int i = 0; i < _size; i++) _buffer[i] = tmp[i] * 0.5f;
            }

            // Normaliser
            float peak = 0.001f;
            for (int i = 0; i < _size; i++) peak = Math.Max(peak, Math.Abs(_buffer[i]));
            float gain = velocity / peak;
            for (int i = 0; i < _size; i++) _buffer[i] *= gain;

            _writeIdx = 0;
            _lpPrev = 0f;
            _toneState = 0f;

            // Feedback gain : sustain 0..1 → 0.985..0.9998 (compense la taille)
            float baseGain = 0.994f - damping * 0.04f;
            _feedbackGain = baseGain + sustain * (0.9998f - baseGain);

            // Pickup position : offset en samples (pour comb en sortie)
            _pickupOffset = Math.Max(1, (int)(_size * pickupPos));

            _peakEnvelope = 1f;
            _active = true;
        }

        public void Kill()
        {
            _active = false;
            Array.Clear(_buffer, 0, _buffer.Length);
        }

        public float RenderSample(float tone, float toneAlpha)
        {
            if (!_active) return 0f;

            // Lecture au bout
            float sample = _buffer[_writeIdx];

            // LP KS classique (perte HF au chevalet)
            float lp = 0.5f * (sample + _lpPrev);
            _lpPrev = sample;

            // Feedback compensé pour la taille de la corde
            float gEff = (float)Math.Pow(_feedbackGain, _size / 1000.0);
            float writeVal = lp * gEff;
            _buffer[_writeIdx] = writeVal;
            _writeIdx++;
            if (_writeIdx >= _size) _writeIdx = 0;

            // Sortie = tap principal + tap pickup (comb filter du pickup magnétique)
            int pickupIdx = _writeIdx - _pickupOffset;
            while (pickupIdx < 0) pickupIdx += _size;
            float pickupTap = _buffer[pickupIdx];
            float outSignal = sample - pickupTap * 0.5f;   // comb filter classique

            // Tone stack (LP RC)
            _toneState += toneAlpha * (outSignal - _toneState);
            outSignal = _toneState;

            // Silence detection
            float absOut = Math.Abs(outSignal);
            _peakEnvelope = Math.Max(_peakEnvelope * 0.9998f, absOut);
            if (_peakEnvelope < SilenceThreshold) { _active = false; return 0f; }

            return outSignal;
        }
    }
}
