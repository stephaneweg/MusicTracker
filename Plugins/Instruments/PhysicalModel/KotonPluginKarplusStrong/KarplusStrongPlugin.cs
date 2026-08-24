using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Windows.Controls;
using KotonStudio.Library;

namespace KotonPluginKarplusStrong
{
    /// <summary>
    /// Karplus-Strong — synthèse par modèle physique de corde pincée. Un des algos de synthèse les plus
    /// efficaces qui existent : une ligne à retard + un filtre passe-bas dans une boucle de feedback.
    /// Publié par Kevin Karplus et Alex Strong en 1983 dans <i>Computer Music Journal</i>.
    ///
    /// **Palette** : guitare, harpe, koto, sitar, basse pincée, luth, mandoline... Tout ce qui est
    /// "corde pincée". Contrairement à un synthé soustractif qui tente d'imiter, ici on RÉSOUT
    /// physiquement une corde vibrante (approximation 1D avec une seule dimension d'onde stationnaire).
    ///
    /// **Paramètres** :
    /// - <b>Damping</b> — amortissement dans la boucle : bas = corde longue (harpe), haut = corde
    ///   courte (percussion pizzicato).
    /// - <b>Tone</b> — brillance générale via un LP variable en série (200 Hz sourd → 8 kHz clair).
    /// - <b>Stiffness</b> — rigidité de la corde via un all-pass dispersif : 0 = corde souple, 1 =
    ///   corde raide (piano, koto).
    /// - <b>Pluck position</b> — où on pince la corde relativement à sa longueur (0.15 = guitare
    ///   typique, 0.5 = pincement au milieu, harmoniques paires supprimées).
    /// - <b>Pluck hardness</b> — dureté du médiator : 0 = doigt (attaque douce), 1 = pointe métal.
    /// - <b>Body mix</b> — mix d'un résonateur simulant le corps de l'instrument (bandpass ~200 Hz
    ///   Q=3, mixé au signal sec).
    /// - <b>Stereo width</b> — détune ±cents L/R + spread pour une image stéréo naturelle.
    /// - <b>Volume</b> — gain de sortie en dB.
    ///
    /// **Presets built-in** : Guitare acoustique, Harpe, Koto, Sitar, Basse pincée, Mandoline.
    /// L'utilisateur peut les charger depuis l'éditeur et modifier ensuite les paramètres à sa guise.
    ///
    /// **Polyphonie** : 16 voix, voice stealing round-robin. Chaque voix a sa propre ligne à retard
    /// (allocation à la construction, pas de GC au NoteOn). NoteOff est un no-op — la corde décroît
    /// naturellement selon <c>Damping</c>, comportement standard d'un instrument à corde pincée.
    /// </summary>
    [KotonInstrument("Karplus-Strong", Id = "koton.karplus", Category = "Physical Model", Version = "1.0", Vendor = "Koton Studio")]
    public sealed class KarplusStrongPlugin : IKotonInstrument
    {
        public string Id => "koton.karplus";
        public string DisplayName => "Karplus-Strong";

        // =============================================================================================
        // Paramètres exposés — 8 sliders, plus une combo preset côté éditeur (pas exposée en param).
        // =============================================================================================
        readonly KotonParameter _damping        = new KotonParameter("damping",         "Damping",         0.0, 1.0, 0.45);
        readonly KotonParameter _sustain        = new KotonParameter("sustain",         "Sustain",         0.0, 1.0, 0.00);
        readonly KotonParameter _harmonics      = new KotonParameter("harmonics",       "Harmonics",       0.0, 1.0, 0.00);
        readonly KotonParameter _tone           = new KotonParameter("tone",            "Tone",            0.0, 1.0, 0.60);
        readonly KotonParameter _stiffness      = new KotonParameter("stiffness",       "Stiffness",       0.0, 1.0, 0.05);
        readonly KotonParameter _pluckPosition  = new KotonParameter("pluck_position",  "Pluck position",  0.02, 0.5, 0.15);
        readonly KotonParameter _pluckHardness  = new KotonParameter("pluck_hardness",  "Pluck hardness",  0.0, 1.0, 0.50);
        readonly KotonParameter _bodyMix        = new KotonParameter("body_mix",        "Body",            0.0, 1.0, 0.30);
        readonly KotonParameter _stereoWidth    = new KotonParameter("stereo_width",    "Stereo width",    0.0, 1.0, 0.30);
        readonly KotonParameter _volumeDb       = new KotonParameter("volume",          "Volume",          -30.0, 6.0, -6.0, "dB");
        // Ré-attaque périodique : rejoue la note tenue tous les 1/taux de seconde.
        // 0 Hz = une seule attaque, donc aucun projet existant ne change.
        readonly KotonStudio.Plugins.Shared.KotonReAttack _retrig =
            new KotonStudio.Plugins.Shared.KotonReAttack("Trémolo", 20.0, 0.0);

        readonly List<KotonParameter> _params;

        int _sampleRate;
        int _maxBlockSize;
        KarplusStrongVoice[] _voices;
        int _stealCursor;
        const int Polyphony = 16;

        // Body resonance biquad (bandpass RBJ) — un seul, sur le mix output (économie CPU + réaliste :
        // c'est LE corps unique de l'instrument, pas un par corde).
        BiquadState _bodyL, _bodyR;

        // Pitch bend global appliqué à toutes les voix (multiplicateur de fréquence)
        float _bendMul = 1f;

        public KarplusStrongPlugin()
        {
            _params = new List<KotonParameter>
            {
                _damping, _sustain, _harmonics, _tone, _stiffness, _pluckPosition, _pluckHardness,
                _bodyMix, _stereoWidth, _volumeDb, _retrig.Rate };
        }

        public IReadOnlyList<KotonParameter> Parameters => _params;

        public bool HasEditor => true;
        public UserControl CreateEditor() => new KarplusStrongEditor(this);

        // =============================================================================================
        // Cycle de vie
        // =============================================================================================
        public void Prepare(int sampleRate, int maxBlockSize)
        {
            _sampleRate = sampleRate;
            _maxBlockSize = maxBlockSize;
            _voices = new KarplusStrongVoice[Polyphony];
            for (int i = 0; i < Polyphony; i++)
                _voices[i] = new KarplusStrongVoice(sampleRate);

            // Body resonator : bandpass ~200 Hz Q=3
            SetBiquadBandpass(ref _bodyL, sampleRate, 200f, 3f);
            SetBiquadBandpass(ref _bodyR, sampleRate, 200f, 3f);
            _retrig.Prepare(sampleRate);
        }

        public void Reset()
        {
            if (_voices != null)
                foreach (var v in _voices) v.Kill();
            _bodyL.ResetState();
            _bodyR.ResetState();
            _bendMul = 1f;
            _retrig.Reset();
        }

        // =============================================================================================
        // MIDI
        // =============================================================================================
        public void NoteOn(int note, int velocity, int sampleOffset = 0)
        {
            _retrig.NoteOn(note, velocity);
            if (_voices == null || velocity == 0) return;

            var p = SnapshotParams();
            float vel = velocity / 127f;

            // 1) Si une voix joue déjà cette note, la re-pincer (comportement corde pincée standard).
            KarplusStrongVoice target = null;
            for (int i = 0; i < _voices.Length; i++)
            {
                if (_voices[i].IsActive && _voices[i].Note == note)
                {
                    target = _voices[i];
                    break;
                }
            }
            // 2) Sinon prendre une voix libre.
            if (target == null)
            {
                for (int i = 0; i < _voices.Length; i++)
                {
                    if (!_voices[i].IsActive)
                    {
                        target = _voices[i];
                        break;
                    }
                }
            }
            // 3) Sinon voice stealing en round-robin.
            if (target == null)
            {
                target = _voices[_stealCursor];
                _stealCursor = (_stealCursor + 1) % _voices.Length;
            }

            // Détune central (aucun) — le stereo width fera le boulot au moment du render L/R
            // On donne 0 en detune à la voix : la largeur stéréo est appliquée au mix, pas à la
            // fréquence de la voix. Note: on POURRAIT créer 2 voix par note (L/R détunées) pour
            // une vraie unison stéréo — v2. Pour l'instant, spread simple = filtrage all-pass léger.
            target.NoteOn(note, vel, (float)p.PluckPosition_p, ToVoiceParams(p), 0f);
        }

        public void NoteOff(int note, int sampleOffset = 0)
        {
            _retrig.NoteOff(note);
            // No-op volontaire : une corde pincée n'a pas de note-off actif. Le sustain est
            // piloté par le paramètre Damping via la décroissance naturelle de la boucle.
        }

        public void MidiCC(int cc, int value, int sampleOffset = 0)
        {
            if (cc == 123)   // All Notes Off
                Reset();
            // CC7 volume, CC10 pan : gérés côté hôte. CC64 sustain : non pertinent pour un pluck.
        }

        public void SetPitchBend(float value, int sampleOffset = 0)
        {
            // Pitch bend ±2 demi-tons par défaut → multiplier de fréquence.
            _bendMul = (float)Math.Pow(2.0, value * 2.0 / 12.0);
            // NOTE: appliqué au sample rate effectif de la ligne à retard serait plus rigoureux,
            // mais Karplus-Strong ne réagit pas facilement au bend continu (il faudrait redimensionner
            // la ligne, ce qui casse le contenu). En v1 on laisse le bend inactif — la note reste
            // sur sa hauteur d'attaque. À documenter dans les limites connues.
            _ = _bendMul;
        }

        // =============================================================================================
        // Render
        // =============================================================================================
        public void Render(Span<float> left, Span<float> right)
        {
            if (_voices == null) { left.Clear(); right.Clear(); return; }

            var p = SnapshotParams();
            var voiceParams = ToVoiceParams(p);

            float volLin = (float)Math.Pow(10.0, p.VolumeDb_p / 20.0);
            float widthGain = (float)p.StereoWidth_p;   // 0 = mono, 1 = stereo max
            float body = (float)p.BodyMix_p;
            float dry = 1f - body * 0.5f;         // le body ajoute, ne remplace pas totalement le sec

            int n = left.Length;
            for (int i = 0; i < n; i++)
            {
                // Ré-attaque : à l'échéance, la note tenue est rejouée (BeginStroke neutralise
                // la notification que NoteOn va renvoyer à l'engin).
                if (_retrig.Tick()) { _retrig.BeginStroke(); for (int rt = 0; rt < _retrig.Count; rt++) NoteOn(_retrig.NoteAt(rt), _retrig.VelocityAt(rt)); _retrig.EndStroke(); }

                // Somme mono des voix
                float sum = 0f;
                for (int v = 0; v < _voices.Length; v++)
                {
                    if (_voices[v].IsActive)
                        sum += _voices[v].RenderSample(voiceParams);
                }

                // Body resonator : bandpass ~200Hz Q=3
                float bodyOutL = BiquadProcess(ref _bodyL, sum);
                float bodyOutR = BiquadProcess(ref _bodyR, sum);

                float wetL = sum * dry + bodyOutL * body;
                float wetR = sum * dry + bodyOutR * body;

                // Spread stéréo par mid/side simplifié (mono si width=0, décorrélé si width=1)
                float mid = 0.5f * (wetL + wetR);
                float side = wetL - wetR;
                float outL = (mid + side * widthGain) * volLin;
                float outR = (mid - side * widthGain) * volLin;

                left[i] = outL;
                right[i] = outR;
            }
        }

        // =============================================================================================
        // Snapshot des paramètres (une seule lecture par buffer)
        // =============================================================================================
        struct PluginParams
        {
            public double Damping_p, Sustain_p, Harmonics_p, Tone_p, Stiffness_p, PluckPosition_p, PluckHardness_p;
            public double BodyMix_p, StereoWidth_p, VolumeDb_p;
        }

        PluginParams SnapshotParams()
        {
            return new PluginParams
            {
                Damping_p        = _damping.Value,
                Sustain_p        = _sustain.Value,
                Harmonics_p      = _harmonics.Value,
                Tone_p           = _tone.Value,
                Stiffness_p      = _stiffness.Value,
                PluckPosition_p  = _pluckPosition.Value,
                PluckHardness_p  = _pluckHardness.Value,
                BodyMix_p        = _bodyMix.Value,
                StereoWidth_p    = _stereoWidth.Value,
                VolumeDb_p       = _volumeDb.Value,
            };
        }

        static KsParams ToVoiceParams(PluginParams p) => new KsParams
        {
            Damping        = (float)p.Damping_p,
            Sustain        = (float)p.Sustain_p,
            Harmonics      = (float)p.Harmonics_p,
            Tone           = (float)p.Tone_p,
            Stiffness      = (float)p.Stiffness_p,
            PluckHardness  = (float)p.PluckHardness_p,
            BodyMix        = (float)p.BodyMix_p,
            StereoWidth    = (float)p.StereoWidth_p,
            VolumeDb       = (float)p.VolumeDb_p,
        };

        // =============================================================================================
        // Persistance
        // =============================================================================================
        public byte[] SaveState()
        {
            try
            {
                var dict = new Dictionary<string, double>();
                foreach (var kp in _params) dict[kp.Id] = kp.Value;
                var json = JsonSerializer.Serialize(dict);
                return Encoding.UTF8.GetBytes(json);
            }
            catch { return Array.Empty<byte>(); }
        }

        public void LoadState(byte[] state)
        {
            if (state == null || state.Length == 0) return;
            try
            {
                var json = Encoding.UTF8.GetString(state);
                var dict = JsonSerializer.Deserialize<Dictionary<string, double>>(json);
                if (dict == null) return;
                foreach (var kp in _params)
                {
                    if (dict.TryGetValue(kp.Id, out var v)) kp.Value = v;
                }
            }
            catch { /* blob corrompu → défauts */ }
        }

        public void Dispose()
        {
            // Rien à libérer (buffers managed).
        }

        // =============================================================================================
        // Presets built-in — chargés par l'éditeur via LoadPreset(index).
        // Ordre : Guitare acoustique, Harpe, Koto, Sitar, Basse pincée, Mandoline.
        // =============================================================================================
        public static readonly string[] PresetNames =
        {
            "Guitare acoustique", "Harpe", "Koto", "Sitar", "Basse pincee", "Mandoline",
        };

        static readonly double[,] PresetValues =
        {
            //          damping sustain harm  tone stiff pluckPos pluckHard body width volDb
            /*Guitare*/ { 0.45, 0.05, 0.10, 0.55, 0.05, 0.15, 0.45, 0.35, 0.30, -6.0 },
            /*Harpe*/   { 0.25, 0.30, 0.20, 0.75, 0.02, 0.50, 0.30, 0.40, 0.45, -6.0 },
            /*Koto*/    { 0.40, 0.15, 0.35, 0.55, 0.20, 0.35, 0.55, 0.35, 0.30, -6.0 },
            /*Sitar*/   { 0.30, 0.10, 0.55, 0.65, 0.55, 0.40, 0.70, 0.15, 0.25, -6.0 },
            /*Basse*/   { 0.35, 0.00, 0.00, 0.30, 0.10, 0.20, 0.65, 0.20, 0.15, -3.0 },
            /*Mando*/   { 0.55, 0.00, 0.45, 0.70, 0.08, 0.20, 0.75, 0.30, 0.30, -6.0 },
        };

        public void LoadPreset(int index)
        {
            if (index < 0 || index >= PresetValues.GetLength(0)) return;
            _damping.Value       = PresetValues[index, 0];
            _sustain.Value       = PresetValues[index, 1];
            _harmonics.Value     = PresetValues[index, 2];
            _tone.Value          = PresetValues[index, 3];
            _stiffness.Value     = PresetValues[index, 4];
            _pluckPosition.Value = PresetValues[index, 5];
            _pluckHardness.Value = PresetValues[index, 6];
            _bodyMix.Value       = PresetValues[index, 7];
            _stereoWidth.Value   = PresetValues[index, 8];
            _volumeDb.Value      = PresetValues[index, 9];
        }

        /// <summary>Setter helper utilisé par l'éditeur — évite de dupliquer le mapping id → param.</summary>
        public void SetParam(string id, double value)
        {
            foreach (var kp in _params)
            {
                if (kp.Id == id) { kp.Value = value; return; }
            }
        }

        // =============================================================================================
        // Biquad bandpass — RBJ cookbook
        // =============================================================================================
        struct BiquadState
        {
            public float b0, b1, b2, a1, a2;
            public float x1, x2, y1, y2;
            public void ResetState() { x1 = x2 = y1 = y2 = 0f; }
        }

        static void SetBiquadBandpass(ref BiquadState s, int sr, float freq, float q)
        {
            double w0 = 2.0 * Math.PI * freq / sr;
            double alpha = Math.Sin(w0) / (2.0 * q);
            double cosw0 = Math.Cos(w0);

            double b0 = alpha;
            double b1 = 0.0;
            double b2 = -alpha;
            double a0 = 1.0 + alpha;
            double a1 = -2.0 * cosw0;
            double a2 = 1.0 - alpha;

            s.b0 = (float)(b0 / a0);
            s.b1 = (float)(b1 / a0);
            s.b2 = (float)(b2 / a0);
            s.a1 = (float)(a1 / a0);
            s.a2 = (float)(a2 / a0);
            s.ResetState();
        }

        static float BiquadProcess(ref BiquadState s, float x)
        {
            float y = s.b0 * x + s.b1 * s.x1 + s.b2 * s.x2 - s.a1 * s.y1 - s.a2 * s.y2;
            s.x2 = s.x1;
            s.x1 = x;
            s.y2 = s.y1;
            s.y1 = y;
            return y;
        }
    }
}
