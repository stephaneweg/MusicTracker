using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Windows.Controls;
using KotonStudio.Library;

namespace KotonPluginBowedStrings
{
    /// <summary>
    /// Bowed Strings — synthèse physique de cordes frottées type "ensemble" (violons, alto,
    /// violoncelles), dans la lignée directe du Karplus-Strong pincé de Koton. Utilise l'algorithme
    /// Extended Karplus-Strong (EKS) de Jaffe &amp; Smith 1983 : même topologie que le KS pincé —
    /// ligne à retard + LP feedback — mais l'excitation est CONTINUE (bruit blanc filtré = archet)
    /// au lieu d'impulsionnelle (pluck). Résultat : sustain infini natif, tenue rêveuse, chœur de
    /// cordes qui vibrent tant qu'on maintient la note.
    ///
    /// **Ensemble/unison** : chaque note MIDI déclenche N voix (2..8) légèrement désaccordées et
    /// panées L/R. Le beating entre voix crée l'effet "chœur de cordes" naturel — comme un vrai
    /// pupitre où les instruments n'ont jamais exactement le même accord. Sans effet externe, on
    /// obtient déjà un son ensemble/pad.
    ///
    /// **Vibrato** : LFO sinus modulant la longueur de la ligne à retard (donc la fréquence). Phase
    /// aléatoire par voix pour ajouter du chorus. Défaut ~5 Hz / ~15 cents = vibrato classique cordes.
    ///
    /// **Envelope** : contrairement au KS pincé (pas de note-off actif), la voix a une attaque et
    /// un release contrôlables. Permet des swells (attack lente ~1-2s pour du rêveur) et un release
    /// long pour un legato pad.
    ///
    /// **Différence avec le KS pincé** : deux modes d'excitation différents pour deux gestes musicaux
    /// différents (pluck vs. bow). L'idée de mettre les deux dans le même plugin a été écartée pour
    /// garder chaque preset simple à comprendre — un plugin = un instrument.
    /// </summary>
    [KotonInstrument("Bowed Strings", Id = "koton.bowed", Category = "Physical Model", Version = "1.0", Vendor = "Koton Studio")]
    public sealed class BowedStringsPlugin : IKotonInstrument
    {
        public string Id => "koton.bowed";
        public string DisplayName => "Bowed Strings";

        // =============================================================================================
        // Paramètres exposés
        // =============================================================================================
        readonly KotonParameter _bowPressure    = new KotonParameter("bow_pressure",    "Bow pressure",    0.0, 1.0, 0.30);
        readonly KotonParameter _bowPosition    = new KotonParameter("bow_position",    "Bow position",    0.02, 0.5, 0.15);
        readonly KotonParameter _bowSmoothness  = new KotonParameter("bow_smoothness",  "Bow smoothness",  0.0, 1.0, 0.65);
        readonly KotonParameter _damping        = new KotonParameter("damping",         "Damping",         0.0, 1.0, 0.30);
        readonly KotonParameter _tone           = new KotonParameter("tone",            "Tone",            0.0, 1.0, 0.55);
        readonly KotonParameter _harmonics      = new KotonParameter("harmonics",       "Harmonics",       0.0, 1.0, 0.15);
        // Unison : 1 = mono, 2/4/6/8 = ensemble progressif. Impair non autorisé côté UI (le combo
        // n'expose que les valeurs pertinentes), mais on borne quand même côté rendu.
        readonly KotonParameter _unisonCount    = new KotonParameter("unison_count",    "Unison",          1, 8, 4);
        readonly KotonParameter _detuneSpread   = new KotonParameter("detune_spread",   "Detune spread",   0.0, 30.0, 8.0, "ct");
        readonly KotonParameter _vibratoRate    = new KotonParameter("vibrato_rate",    "Vibrato rate",    0.0, 8.0, 5.0, "Hz");
        readonly KotonParameter _vibratoDepth   = new KotonParameter("vibrato_depth",   "Vibrato depth",   0.0, 50.0, 12.0, "ct");
        readonly KotonParameter _attackTime     = new KotonParameter("attack_time",     "Attack",          0.0, 2.0, 0.20, "s");
        readonly KotonParameter _releaseTime    = new KotonParameter("release_time",    "Release",         0.0, 2.0, 0.30, "s");
        readonly KotonParameter _stereoWidth    = new KotonParameter("stereo_width",    "Stereo width",    0.0, 1.0, 0.60);
        readonly KotonParameter _volumeDb       = new KotonParameter("volume",          "Volume",          -30.0, 6.0, -6.0, "dB");

        readonly List<KotonParameter> _params;
        public IReadOnlyList<KotonParameter> Parameters => _params;

        int _sampleRate;
        int _maxBlockSize;
        // Table de voix : polyphonie 8 notes × jusqu'à 8 voix d'unison = 64 max. Alloué à Prepare.
        BowedStringVoice[] _voices;
        // Note actuelle par slot d'unison — pour NoteOff, on doit killer TOUTES les voix de la note.
        int _stealCursor;
        const int MaxNotes = 8;

        public BowedStringsPlugin()
        {
            _params = new List<KotonParameter>
            {
                _bowPressure, _bowPosition, _bowSmoothness, _damping, _tone, _harmonics,
                _unisonCount, _detuneSpread, _vibratoRate, _vibratoDepth,
                _attackTime, _releaseTime, _stereoWidth, _volumeDb,
            };
        }

        public bool HasEditor => true;
        public UserControl CreateEditor() => new BowedStringsEditor(this);

        // =============================================================================================
        // Cycle de vie
        // =============================================================================================
        public void Prepare(int sampleRate, int maxBlockSize)
        {
            _sampleRate = sampleRate;
            _maxBlockSize = maxBlockSize;
            int total = MaxNotes * 8;   // 8 notes × 8 voix d'unison max
            _voices = new BowedStringVoice[total];
            for (int i = 0; i < total; i++) _voices[i] = new BowedStringVoice(sampleRate);
        }

        public void Reset()
        {
            if (_voices != null)
                foreach (var v in _voices) v.Kill();
        }

        // =============================================================================================
        // MIDI
        // =============================================================================================
        public void NoteOn(int note, int velocity, int sampleOffset = 0)
        {
            if (_voices == null || velocity == 0) return;

            var p = SnapshotParams();
            var vp = ToVoiceParams(p);
            float vel = velocity / 127f;
            int unison = ClampUnison((int)p.UnisonCount_p);
            float spread = (float)p.DetuneSpread_p;
            float widthGain = (float)p.StereoWidth_p;

            // Allouer `unison` voix libres. Si pas assez, voice stealing round-robin.
            for (int u = 0; u < unison; u++)
            {
                // Détune symétrique autour de 0 : voix 0 = -spread, dernière = +spread
                float t = unison == 1 ? 0f : ((float)u / (unison - 1)) * 2f - 1f;   // -1..+1
                float detune = t * spread;
                float pan = t * widthGain;   // -width..+width

                var target = TakeVoice();
                if (target != null) target.NoteOn(note, vel, detune, pan, vp);
            }
        }

        BowedStringVoice TakeVoice()
        {
            // 1) Chercher une voix libre
            for (int i = 0; i < _voices.Length; i++)
                if (!_voices[i].IsActive) return _voices[i];
            // 2) Sinon voler la voix pointée par le curseur
            var v = _voices[_stealCursor];
            _stealCursor = (_stealCursor + 1) % _voices.Length;
            v.Kill();
            return v;
        }

        public void NoteOff(int note, int sampleOffset = 0)
        {
            if (_voices == null) return;
            // Passer TOUTES les voix (unison) qui jouent cette note en release
            for (int i = 0; i < _voices.Length; i++)
                if (_voices[i].IsActive && _voices[i].Note == note)
                    _voices[i].NoteOff();
        }

        public void MidiCC(int cc, int value, int sampleOffset = 0)
        {
            if (cc == 123) Reset();
        }

        public void SetPitchBend(float value, int sampleOffset = 0)
        {
            // Non supporté v1 (comme le KS pincé — redimensionnement de la ligne à retard trop
            // complexe pour un bend continu).
        }

        // =============================================================================================
        // Render
        // =============================================================================================
        public void Render(Span<float> left, Span<float> right)
        {
            if (_voices == null) { left.Clear(); right.Clear(); return; }

            var p = SnapshotParams();
            var vp = ToVoiceParams(p);
            float volLin = (float)Math.Pow(10.0, p.VolumeDb_p / 20.0);
            // Compensation d'unison : plus de voix = plus fort → normaliser
            int unison = ClampUnison((int)p.UnisonCount_p);
            float unisonCompensation = 1f / (float)Math.Sqrt(Math.Max(1, unison));

            int n = left.Length;
            for (int i = 0; i < n; i++)
            {
                float sumL = 0f, sumR = 0f;
                for (int v = 0; v < _voices.Length; v++)
                {
                    var voice = _voices[v];
                    if (!voice.IsActive) continue;
                    float s = voice.RenderSample(vp);
                    sumL += s * voice.PanL;
                    sumR += s * voice.PanR;
                }
                left[i] = sumL * volLin * unisonCompensation;
                right[i] = sumR * volLin * unisonCompensation;
            }
        }

        // =============================================================================================
        // Snapshot des paramètres
        // =============================================================================================
        struct PluginParams
        {
            public double BowPressure_p, BowPosition_p, BowSmoothness_p;
            public double Damping_p, Tone_p, Harmonics_p;
            public double UnisonCount_p, DetuneSpread_p;
            public double VibratoRate_p, VibratoDepth_p;
            public double AttackTime_p, ReleaseTime_p;
            public double StereoWidth_p, VolumeDb_p;
        }

        PluginParams SnapshotParams() => new PluginParams
        {
            BowPressure_p    = _bowPressure.Value,
            BowPosition_p    = _bowPosition.Value,
            BowSmoothness_p  = _bowSmoothness.Value,
            Damping_p        = _damping.Value,
            Tone_p           = _tone.Value,
            Harmonics_p      = _harmonics.Value,
            UnisonCount_p    = _unisonCount.Value,
            DetuneSpread_p   = _detuneSpread.Value,
            VibratoRate_p    = _vibratoRate.Value,
            VibratoDepth_p   = _vibratoDepth.Value,
            AttackTime_p     = _attackTime.Value,
            ReleaseTime_p    = _releaseTime.Value,
            StereoWidth_p    = _stereoWidth.Value,
            VolumeDb_p       = _volumeDb.Value,
        };

        static BsParams ToVoiceParams(PluginParams p) => new BsParams
        {
            BowPressure        = (float)p.BowPressure_p,
            BowPosition        = (float)p.BowPosition_p,
            BowSmoothness      = (float)p.BowSmoothness_p,
            Damping            = (float)p.Damping_p,
            Tone               = (float)p.Tone_p,
            Harmonics          = (float)p.Harmonics_p,
            VibratoRateHz      = (float)p.VibratoRate_p,
            VibratoDepthCents  = (float)p.VibratoDepth_p,
            AttackSec          = (float)p.AttackTime_p,
            ReleaseSec         = (float)p.ReleaseTime_p,
            VolumeDb           = (float)p.VolumeDb_p,
        };

        static int ClampUnison(int u)
        {
            if (u <= 1) return 1;
            if (u <= 2) return 2;
            if (u <= 4) return 4;
            if (u <= 6) return 6;
            return 8;
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
            catch { /* blob corrompu → défauts */ }
        }

        public void Dispose() { /* rien à libérer */ }

        // =============================================================================================
        // Presets built-in
        // =============================================================================================
        public static readonly string[] PresetNames =
        {
            "Violons ensemble", "Alto", "Violoncelle", "Cordes lointaines", "Ensemble complet", "Solo reveur",
        };

        static readonly double[,] PresetValues =
        {
            //          bowP bowPos bowSm damp tone harm  uni det  vibR vibD attk rel width vol
            /*Violons*/ { 0.35, 0.15, 0.65, 0.25, 0.60, 0.20, 6, 8.0, 5.5, 15.0, 0.15, 0.30, 0.70, -6.0 },
            /*Alto*/    { 0.40, 0.18, 0.60, 0.30, 0.50, 0.15, 4, 6.0, 5.0, 12.0, 0.20, 0.35, 0.55, -6.0 },
            /*Cello*/   { 0.45, 0.22, 0.55, 0.35, 0.40, 0.10, 4, 5.0, 4.5, 10.0, 0.30, 0.40, 0.45, -5.0 },
            /*Loin*/    { 0.25, 0.15, 0.80, 0.20, 0.35, 0.05, 8, 12.0, 4.0, 8.0, 0.80, 0.80, 0.85, -8.0 },
            /*Full*/    { 0.40, 0.15, 0.60, 0.28, 0.55, 0.18, 8, 10.0, 5.0, 14.0, 0.25, 0.40, 0.90, -6.0 },
            /*Solo*/    { 0.30, 0.15, 0.70, 0.25, 0.60, 0.20, 1, 0.0, 5.5, 18.0, 0.50, 0.60, 0.00, -6.0 },
        };

        public void LoadPreset(int index)
        {
            if (index < 0 || index >= PresetValues.GetLength(0)) return;
            _bowPressure.Value    = PresetValues[index, 0];
            _bowPosition.Value    = PresetValues[index, 1];
            _bowSmoothness.Value  = PresetValues[index, 2];
            _damping.Value        = PresetValues[index, 3];
            _tone.Value           = PresetValues[index, 4];
            _harmonics.Value      = PresetValues[index, 5];
            _unisonCount.Value    = PresetValues[index, 6];
            _detuneSpread.Value   = PresetValues[index, 7];
            _vibratoRate.Value    = PresetValues[index, 8];
            _vibratoDepth.Value   = PresetValues[index, 9];
            _attackTime.Value     = PresetValues[index, 10];
            _releaseTime.Value    = PresetValues[index, 11];
            _stereoWidth.Value    = PresetValues[index, 12];
            _volumeDb.Value       = PresetValues[index, 13];
        }

        public void SetParam(string id, double value)
        {
            foreach (var kp in _params)
                if (kp.Id == id) { kp.Value = value; return; }
        }
    }
}
