using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Windows.Controls;
using KotonStudio.Library;

namespace KotonPluginBlownFlute
{
    /// <summary>
    /// Blown Flute — flûtes à embouchure directe : ney (arabe), shakuhachi (japonais), quena
    /// (andine), kaval (turc/bulgare). Physiquement différent du Woodwind classique — pas d'anche
    /// mais un JET D'AIR non-linéaire qui oscille au bord d'un biseau (jet drive).
    ///
    /// **Signature acoustique** : timbre TRÈS EXPRESSIF, avec du souffle audible important (le
    /// caractère "méditation" du shakuhachi vient en grande partie du bruit du souffle
    /// PROPORTIONNEL à la pression). Attaque progressive typique. Ornements meri/kari
    /// (inclinaison de la tête → shift de pitch de ±50 cents) expressifs.
    ///
    /// **Non-linéarité du jet** : x - x³/3 (soft-cubed, McIntyre-Woodhouse simplifié). Différent
    /// du tanh d'un anche, produit un timbre plus riche en harmoniques paires (flûte est ouverte-
    /// ouverte, spectre complet ; clarinette est ouverte-fermée, harmoniques impaires seulement).
    ///
    /// **Params expressifs uniques** :
    /// - **Jet instability** : chaos du jet, proportionnel à la pression (le "muraiki" du shakuhachi)
    /// - **Embouchure shift** : le meri/kari (-50..+50 cents, pilotable par mod wheel dans une v2)
    /// - **Breath attack** : très progressive typique méditation
    ///
    /// **Presets** : Ney doux, Shakuhachi calme, Shakuhachi muraiki (souffle turbulent), Quena
    /// andine (aigu), Kaval (souffle sec), Flûte celtique.
    /// </summary>
    [KotonInstrument("Blown Flute", Id = "koton.blownflute", Category = "Physical Model", Version = "1.0", Vendor = "Koton Studio")]
    public sealed class BlownFlutePlugin : IKotonInstrument
    {
        public string Id => "koton.blownflute";
        public string DisplayName => "Blown Flute";

        readonly KotonParameter _instrument      = new KotonParameter("instrument",       "Instrument",       0, 5, 0);
        readonly KotonParameter _breathPressure  = new KotonParameter("breath_pressure",  "Breath pressure",  0.0, 1.0, 0.55);
        readonly KotonParameter _breathNoise     = new KotonParameter("breath_noise",     "Breath noise",     0.0, 1.0, 0.45);
        readonly KotonParameter _jetInstability  = new KotonParameter("jet_instability",  "Jet instability",  0.0, 1.0, 0.20);
        readonly KotonParameter _embouchureShift = new KotonParameter("embouchure_shift", "Meri/Kari",        -1.0, 1.0, 0.0);
        readonly KotonParameter _damping         = new KotonParameter("damping",          "Damping",          0.0, 1.0, 0.35);
        readonly KotonParameter _brightness      = new KotonParameter("brightness",       "Brightness",       0.0, 1.0, 0.45);
        readonly KotonParameter _vibratoRate     = new KotonParameter("vibrato_rate",     "Vibrato rate",     0.0, 8.0, 4.5, "Hz");
        readonly KotonParameter _vibratoDepth    = new KotonParameter("vibrato_depth",    "Vibrato depth",    0.0, 40.0, 8.0, "ct");
        readonly KotonParameter _breathAttack    = new KotonParameter("breath_attack",    "Breath attack",    0.0, 1.0, 0.35);
        readonly KotonParameter _releaseTime     = new KotonParameter("release_time",     "Release",          0.0, 2.0, 0.30, "s");
        readonly KotonParameter _stereoWidth     = new KotonParameter("stereo_width",     "Stereo width",     0.0, 1.0, 0.25);
        readonly KotonParameter _volumeDb        = new KotonParameter("volume",           "Volume",           -30.0, 6.0, -6.0, "dB");

        readonly List<KotonParameter> _params;
        public IReadOnlyList<KotonParameter> Parameters => _params;

        int _sampleRate;
        BlownFluteVoice[] _voices;
        int _stealCursor;
        const int Polyphony = 6;   // Flûtes = souvent mono ou peu polyphoniques

        public BlownFlutePlugin()
        {
            _params = new List<KotonParameter>
            {
                _instrument, _breathPressure, _breathNoise, _jetInstability, _embouchureShift,
                _damping, _brightness, _vibratoRate, _vibratoDepth,
                _breathAttack, _releaseTime, _stereoWidth, _volumeDb,
            };
        }

        public bool HasEditor => true;
        public UserControl CreateEditor() => new BlownFluteEditor(this);

        // Presets
        public static readonly string[] InstrumentNames = { "Ney (arabe)", "Shakuhachi (calme)", "Shakuhachi muraiki", "Quena (andine)", "Kaval (turc)", "Flute celtique" };
        static readonly double[,] PresetValues = {
            //          br brNoi jetInst meri damp brgh vibR vibD brAtt rel  wid vol
            /*Ney*/     { 0.50, 0.55, 0.15, 0.0, 0.30, 0.40, 4.0, 7.0, 0.60, 0.40, 0.25, -6.0 },
            /*Shak clm*/{ 0.45, 0.60, 0.20, 0.0, 0.25, 0.35, 3.5, 10.0, 0.80, 0.50, 0.30, -6.0 },
            /*Shak mur*/{ 0.75, 0.75, 0.65, 0.0, 0.20, 0.55, 4.0, 15.0, 0.30, 0.35, 0.30, -5.0 },
            /*Quena*/   { 0.55, 0.35, 0.10, 0.0, 0.40, 0.60, 5.0, 6.0, 0.25, 0.25, 0.20, -6.0 },
            /*Kaval*/   { 0.50, 0.50, 0.15, 0.0, 0.30, 0.50, 4.5, 8.0, 0.35, 0.30, 0.20, -6.0 },
            /*Celtic*/  { 0.55, 0.30, 0.10, 0.0, 0.35, 0.55, 5.5, 5.0, 0.15, 0.25, 0.20, -6.0 },
        };

        public void ApplyInstrumentDefaults(int index)
        {
            if (index < 0 || index >= PresetValues.GetLength(0)) return;
            _breathPressure.Value  = PresetValues[index, 0];
            _breathNoise.Value     = PresetValues[index, 1];
            _jetInstability.Value  = PresetValues[index, 2];
            _embouchureShift.Value = PresetValues[index, 3];
            _damping.Value         = PresetValues[index, 4];
            _brightness.Value      = PresetValues[index, 5];
            _vibratoRate.Value     = PresetValues[index, 6];
            _vibratoDepth.Value    = PresetValues[index, 7];
            _breathAttack.Value    = PresetValues[index, 8];
            _releaseTime.Value     = PresetValues[index, 9];
            _stereoWidth.Value     = PresetValues[index, 10];
            _volumeDb.Value        = PresetValues[index, 11];
        }

        public void Prepare(int sampleRate, int maxBlockSize)
        {
            _sampleRate = sampleRate;
            _voices = new BlownFluteVoice[Polyphony];
            for (int i = 0; i < Polyphony; i++) _voices[i] = new BlownFluteVoice(sampleRate);
        }
        public void Reset()
        {
            if (_voices != null) foreach (var v in _voices) v.Kill();
        }

        public void NoteOn(int note, int velocity, int sampleOffset = 0)
        {
            if (_voices == null || velocity == 0) return;
            var p = ToVoiceParams();
            float vel = velocity / 127f;
            BlownFluteVoice target = null;
            for (int i = 0; i < _voices.Length; i++) if (_voices[i].IsActive && _voices[i].Note == note) { target = _voices[i]; break; }
            if (target == null) for (int i = 0; i < _voices.Length; i++) if (!_voices[i].IsActive) { target = _voices[i]; break; }
            if (target == null) { target = _voices[_stealCursor]; _stealCursor = (_stealCursor + 1) % _voices.Length; target.Kill(); }
            target.NoteOn(note, vel, p);
        }
        public void NoteOff(int note, int sampleOffset = 0)
        {
            if (_voices == null) return;
            for (int i = 0; i < _voices.Length; i++) if (_voices[i].IsActive && _voices[i].Note == note) _voices[i].NoteOff();
        }
        public void MidiCC(int cc, int value, int sampleOffset = 0) { if (cc == 123) Reset(); }
        public void SetPitchBend(float value, int sampleOffset = 0) { }

        public void Render(Span<float> left, Span<float> right)
        {
            if (_voices == null) { left.Clear(); right.Clear(); return; }
            var p = ToVoiceParams();
            float volLin = (float)Math.Pow(10.0, p.VolumeDb / 20.0);
            float width = (float)_stereoWidth.Value;
            int n = left.Length;
            for (int i = 0; i < n; i++)
            {
                float sumL = 0f, sumR = 0f;
                for (int v = 0; v < _voices.Length; v++)
                {
                    var voice = _voices[v];
                    if (!voice.IsActive) continue;
                    float s = voice.RenderSample(p);
                    float noteNorm = (voice.Note - 60) / 24f;
                    if (noteNorm < -1f) noteNorm = -1f; else if (noteNorm > 1f) noteNorm = 1f;
                    float p01 = 0.5f + noteNorm * width * 0.5f;
                    sumL += s * (1f - p01);
                    sumR += s * p01;
                }
                left[i] = sumL * volLin;
                right[i] = sumR * volLin;
            }
        }

        BfParams ToVoiceParams() => new BfParams
        {
            BreathPressure    = (float)_breathPressure.Value,
            BreathNoise       = (float)_breathNoise.Value,
            JetInstability    = (float)_jetInstability.Value,
            EmbouchureShift   = (float)_embouchureShift.Value,
            Damping           = (float)_damping.Value,
            Brightness        = (float)_brightness.Value,
            VibratoRateHz     = (float)_vibratoRate.Value,
            VibratoDepthCents = (float)_vibratoDepth.Value,
            BreathAttack      = (float)_breathAttack.Value,
            ReleaseSec        = (float)_releaseTime.Value,
            VolumeDb          = (float)_volumeDb.Value,
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
    }
}
