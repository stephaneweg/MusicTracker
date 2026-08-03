using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Windows.Controls;
using KotonStudio.Library;

namespace KotonPluginElectricViolin
{
    /// <summary>
    /// Electric Violin — modélisation physique complète (McIntyre-Schumacher-Woodhouse 1983 +
    /// guide d'onde Smith 1992) d'un violon électrique type Zeta jazz fusion / Wood Violin. Le
    /// vrai moteur du son violon = non-linéarité stick-slip de l'archet sur la corde, qui produit
    /// naturellement une onde en dents de scie — c'est CE qui donne le "grain" de crin
    /// caractéristique qu'un bruit filtré (comme Bowed Strings actuel) ne peut pas atteindre.
    ///
    /// **Chaîne DSP** :
    ///   1. Corde = ligne à retard (guide d'onde) avec réflexion négative + LP au chevalet
    ///   2. Archet = friction non-linéaire MSW (stick zone + slip zone) au point d'archet
    ///   3. Position d'archet = filtre comb qui accentue les harmoniques selon la position (près
    ///      chevalet = harmoniques hautes forts / près milieu = fondamentale forte)
    ///   4. 3 formants biquad bandpass série (résonances de caisse : 300/700/2500 Hz)
    ///   5. Saturation tanh douce (Warmth) = simulation pickup piezo (le crunch électrique)
    ///   6. Vibrato (modulation frequence) + tremolo (modulation amplitude)
    ///
    /// **100% procédural**, aucune mesure ni sample. Cohérent avec la lignée Karplus.
    ///
    /// **Différence avec Bowed Strings** : BS utilise un bruit filtré comme excitation → son de
    /// pupitre orchestral "moyen". EV utilise la vraie friction stick-slip → son solo expressif
    /// qui répond à la dynamique (velocity → force → spectre change réellement).
    /// </summary>
    [KotonInstrument("Electric Violin", Id = "koton.electricviolin", Category = "Physical Model", Version = "1.0", Vendor = "Koton Studio")]
    public sealed class ElectricViolinPlugin : IKotonInstrument
    {
        public string Id => "koton.electricviolin";
        public string DisplayName => "Electric Violin";

        readonly KotonParameter _bowForce      = new KotonParameter("bow_force",       "Bow force",       0.0, 1.0, 0.55);
        readonly KotonParameter _bowVelocity   = new KotonParameter("bow_velocity",    "Bow velocity",    0.0, 1.0, 0.65);
        readonly KotonParameter _bowPosition   = new KotonParameter("bow_position",    "Bow position",    0.0, 0.5, 0.12);
        readonly KotonParameter _damping       = new KotonParameter("damping",         "Damping",         0.0, 1.0, 0.30);
        readonly KotonParameter _bodyIntensity = new KotonParameter("body_intensity",  "Body (formants)", 0.0, 1.0, 0.75);
        readonly KotonParameter _warmth        = new KotonParameter("warmth",          "Warmth (piezo)",  0.0, 1.0, 0.45);
        readonly KotonParameter _vibratoRate   = new KotonParameter("vibrato_rate",    "Vibrato rate",    0.0, 8.0, 5.5, "Hz");
        readonly KotonParameter _vibratoDepth  = new KotonParameter("vibrato_depth",   "Vibrato depth",   0.0, 40.0, 12.0, "ct");
        readonly KotonParameter _tremoloRate   = new KotonParameter("tremolo_rate",    "Tremolo rate",    0.0, 8.0, 5.0, "Hz");
        readonly KotonParameter _tremoloDepth  = new KotonParameter("tremolo_depth",   "Tremolo depth",   0.0, 1.0, 0.10);
        readonly KotonParameter _attackTime    = new KotonParameter("attack_time",     "Attack",          0.0, 1.5, 0.10, "s");
        readonly KotonParameter _releaseTime   = new KotonParameter("release_time",    "Release",         0.0, 1.5, 0.25, "s");
        readonly KotonParameter _volumeDb      = new KotonParameter("volume",          "Volume",          -30.0, 6.0, -3.0, "dB");

        readonly List<KotonParameter> _params;
        public IReadOnlyList<KotonParameter> Parameters => _params;

        int _sr;
        ElectricViolinVoice[] _voices;
        int _stealCursor;
        const int Polyphony = 4;   // violon = mono mais 4 voix pour double-cordes / accords occasionnels

        public ElectricViolinPlugin()
        {
            _params = new List<KotonParameter>
            {
                _bowForce, _bowVelocity, _bowPosition, _damping,
                _bodyIntensity, _warmth,
                _vibratoRate, _vibratoDepth, _tremoloRate, _tremoloDepth,
                _attackTime, _releaseTime, _volumeDb,
            };
        }

        public bool HasEditor => true;
        public UserControl CreateEditor() => new ElectricViolinEditor(this);

        public void Prepare(int sampleRate, int maxBlockSize)
        {
            _sr = sampleRate;
            _voices = new ElectricViolinVoice[Polyphony];
            for (int i = 0; i < Polyphony; i++) _voices[i] = new ElectricViolinVoice(sampleRate);
        }
        public void Reset()
        {
            if (_voices != null) foreach (var v in _voices) v.Kill();
        }

        public void NoteOn(int note, int velocity, int sampleOffset = 0)
        {
            if (_voices == null || velocity == 0) return;
            float vel = velocity / 127f;
            var p = ToVoiceParams();

            ElectricViolinVoice target = null;
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
            target.NoteOn(note, vel, p);
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

            var p = ToVoiceParams();
            float volLin = (float)Math.Pow(10.0, _volumeDb.Value / 20.0);

            int n = left.Length;
            for (int i = 0; i < n; i++)
            {
                float sum = 0f;
                for (int v = 0; v < _voices.Length; v++)
                {
                    var voice = _voices[v];
                    if (!voice.IsActive) continue;
                    sum += voice.RenderSample(p);
                }
                float s = sum * volLin;
                left[i] = s;
                right[i] = s;
            }
        }

        EvParams ToVoiceParams() => new EvParams
        {
            BowForce          = (float)_bowForce.Value,
            BowVelocity       = (float)_bowVelocity.Value,
            BowPosition       = (float)_bowPosition.Value,
            Damping           = (float)_damping.Value,
            BodyIntensity     = (float)_bodyIntensity.Value,
            Warmth            = (float)_warmth.Value,
            VibratoRateHz     = (float)_vibratoRate.Value,
            VibratoDepthCents = (float)_vibratoDepth.Value,
            TremoloRateHz     = (float)_tremoloRate.Value,
            TremoloDepth      = (float)_tremoloDepth.Value,
            AttackSec         = (float)_attackTime.Value,
            ReleaseSec        = (float)_releaseTime.Value,
            VolumeDb          = (float)_volumeDb.Value,
        };

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
            "Zeta Jazz Fusion (charnu crémeux)",
            "Wood Violin (métal-jazz mordant)",
            "Studio Clean",
            "Solo Lead (rapide brillant)",
            "Ballade (doux lyrique)",
            "Sultasto (près touche, feutre)"
        };
        static readonly double[,] PresetValues = {
            //          bF   bV   bP    damp body warm vibR vibD trR trD attk rel  vol
            /*Zeta*/    { 0.60, 0.65, 0.15, 0.30, 0.80, 0.55, 5.5, 20.0, 5.0, 0.15, 0.10, 0.30, -3.0 },
            /*Wood*/    { 0.75, 0.75, 0.08, 0.25, 0.70, 0.75, 5.0, 18.0, 5.5, 0.20, 0.06, 0.20, -3.0 },
            /*Clean*/   { 0.50, 0.55, 0.15, 0.35, 0.65, 0.20, 5.0, 15.0, 0.0, 0.00, 0.12, 0.25, -3.0 },
            /*Lead*/    { 0.70, 0.80, 0.10, 0.20, 0.75, 0.60, 6.0, 22.0, 5.5, 0.18, 0.05, 0.20, -3.0 },
            /*Ballade*/ { 0.45, 0.55, 0.20, 0.40, 0.85, 0.35, 4.5, 14.0, 3.5, 0.12, 0.25, 0.50, -3.0 },
            /*Sultasto*/{ 0.50, 0.55, 0.32, 0.45, 0.80, 0.30, 5.0, 15.0, 4.0, 0.10, 0.30, 0.40, -3.0 },
        };
        public void LoadPreset(int idx)
        {
            if (idx < 0 || idx >= PresetValues.GetLength(0)) return;
            _bowForce.Value = PresetValues[idx, 0]; _bowVelocity.Value = PresetValues[idx, 1];
            _bowPosition.Value = PresetValues[idx, 2]; _damping.Value = PresetValues[idx, 3];
            _bodyIntensity.Value = PresetValues[idx, 4]; _warmth.Value = PresetValues[idx, 5];
            _vibratoRate.Value = PresetValues[idx, 6]; _vibratoDepth.Value = PresetValues[idx, 7];
            _tremoloRate.Value = PresetValues[idx, 8]; _tremoloDepth.Value = PresetValues[idx, 9];
            _attackTime.Value = PresetValues[idx, 10]; _releaseTime.Value = PresetValues[idx, 11];
            _volumeDb.Value = PresetValues[idx, 12];
        }
        public void SetParam(string id, double value)
        {
            foreach (var kp in _params) if (kp.Id == id) { kp.Value = value; return; }
        }
    }
}
