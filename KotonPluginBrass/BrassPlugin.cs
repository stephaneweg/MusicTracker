using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Windows.Controls;
using KotonStudio.Library;

namespace KotonPluginBrass
{
    /// <summary>
    /// Brass — synthèse par modèle physique de cuivres (trompette, trombone, cor, tuba, saxhorn).
    /// Quatrième plugin de modélisation physique après Karplus-Strong (corde pincée), Bowed Strings
    /// (corde frottée) et Mallets (percussion modale), couvrant la famille "tube + auto-oscillation
    /// non-linéaire".
    ///
    /// **Algorithme** : guide d'onde simplifié = tube modélisé par une ligne à retard de longueur
    /// N = SR/f (une période fondamentale), avec une non-linéarité tanh à l'embouchure (les "lèvres")
    /// qui compresse le signal réinjecté. C'est cette compression + le LP dans la boucle qui produit
    /// l'auto-oscillation caractéristique. Excitation continue = pression d'air (souffle) + bruit
    /// blanc filtré (composante audible du souffle).
    ///
    /// **Params physiques** :
    /// - Bow pressure vs. Breath pressure : ici c'est la pression d'AIR qui pilote l'excitation.
    ///   Analogue au Bow pressure des cordes frottées.
    /// - Lip tension = force du pincement des lèvres → tension = mordant/agressif (trompette
    ///   forte, brass fanfare), détendu = doux (bugle, cor tranquille).
    /// - Bell size = taille du pavillon en sortie = filtre passe-bas terminal (petit pavillon
    ///   trompette = mordant, gros pavillon tuba = arrondi).
    ///
    /// **Presets** : Trompette, Trombone, Cor d'harmonie, Tuba, Bugle doux, Cuivres screaming
    /// (fanfare), Section cuivres (unison).
    ///
    /// **Ce qui manque en v1** (pas TODO caché) :
    /// - Vraie non-linéarité Bernoulli sur les lèvres (résolution d'équa diff, coûteux) — le tanh
    ///   simple donne 80% du son avec 5% du CPU. Une v2 pourrait ajouter un mode "physical"
    ///   plus fidèle pour les transitoires d'attaque.
    /// - Ouverture/fermeture des cylindres/pistons (change la longueur de tube pour les cuivres
    ///   à pistons) — pas de bend continu, la fréquence est figée par le note MIDI. Un pitch bend
    ///   MIDI classique s'entend par modulation du délai (comme le vibrato).
    /// </summary>
    [KotonInstrument("Brass", Id = "koton.brass", Category = "Physical Model", Version = "1.0", Vendor = "Koton Studio")]
    public sealed class BrassPlugin : IKotonInstrument
    {
        public string Id => "koton.brass";
        public string DisplayName => "Brass";

        // =============================================================================================
        // Paramètres
        // =============================================================================================
        readonly KotonParameter _instrument     = new KotonParameter("instrument",       "Instrument",      0, 6, 0);
        readonly KotonParameter _breathPressure = new KotonParameter("breath_pressure",  "Breath pressure", 0.0, 1.0, 0.55);
        readonly KotonParameter _breathNoise    = new KotonParameter("breath_noise",     "Breath noise",    0.0, 1.0, 0.20);
        readonly KotonParameter _lipTension     = new KotonParameter("lip_tension",      "Lip tension",     0.0, 1.0, 0.50);
        readonly KotonParameter _damping        = new KotonParameter("damping",          "Damping",         0.0, 1.0, 0.35);
        readonly KotonParameter _brightness     = new KotonParameter("brightness",       "Brightness",      0.0, 1.0, 0.55);
        readonly KotonParameter _bellSize       = new KotonParameter("bell_size",        "Bell size",       0.0, 1.0, 0.40);
        readonly KotonParameter _vibratoRate    = new KotonParameter("vibrato_rate",     "Vibrato rate",    0.0, 8.0, 4.5, "Hz");
        readonly KotonParameter _vibratoDepth   = new KotonParameter("vibrato_depth",    "Vibrato depth",   0.0, 30.0, 6.0, "ct");
        readonly KotonParameter _attackTime     = new KotonParameter("attack_time",      "Attack",          0.0, 1.0, 0.08, "s");
        readonly KotonParameter _releaseTime    = new KotonParameter("release_time",     "Release",         0.0, 1.5, 0.15, "s");
        readonly KotonParameter _stereoWidth    = new KotonParameter("stereo_width",     "Stereo width",    0.0, 1.0, 0.30);
        readonly KotonParameter _volumeDb       = new KotonParameter("volume",           "Volume",          -30.0, 6.0, -6.0, "dB");

        readonly List<KotonParameter> _params;
        public IReadOnlyList<KotonParameter> Parameters => _params;

        int _sampleRate;
        int _maxBlockSize;
        BrassVoice[] _voices;
        int _stealCursor;
        const int Polyphony = 8;

        // Formant filter caractéristique de l'instrument (peak biquad appliqué en sortie).
        // Chaque cuivre a sa propre courbe de réponse due à la conicité du tube + taille du pavillon :
        // Trompette (tube cylindrique + petit pavillon) = pic aigu ~1.5kHz brillant ;
        // Tuba (tube conique large + pavillon énorme) = pic très grave ~200Hz grondement.
        // Sans ce formant, tous les cuivres sonnaient similaires malgré des params différents.
        BiquadPeakState _formantL, _formantR;
        int _lastFormantInstrument = -1;

        // Table par instrument : (freq Hz, Q, gain dB). Approximation des mesures acoustiques
        // réelles des cuivres (Fletcher & Rossing "Physics of Musical Instruments").
        static readonly (float freq, float q, float gainDb)[] FormantByInstrument = new (float, float, float)[]
        {
            /* Trompette      */ (1500f, 2.0f, 7f),
            /* Trombone       */ ( 700f, 2.0f, 5f),
            /* Cor d'harmonie */ ( 450f, 2.2f, 6f),
            /* Tuba           */ ( 200f, 1.8f, 7f),
            /* Bugle doux     */ ( 800f, 1.2f, 3f),
            /* Fanfare        */ (2000f, 3.0f, 9f),
            /* Section unison */ (1200f, 1.0f, 3f),
        };

        public BrassPlugin()
        {
            _params = new List<KotonParameter>
            {
                _instrument, _breathPressure, _breathNoise, _lipTension,
                _damping, _brightness, _bellSize,
                _vibratoRate, _vibratoDepth, _attackTime, _releaseTime,
                _stereoWidth, _volumeDb,
            };
        }

        public bool HasEditor => true;
        public UserControl CreateEditor() => new BrassEditor(this);

        // =============================================================================================
        // Presets — l'index EST le paramètre _instrument
        // =============================================================================================
        public static readonly string[] InstrumentNames =
        {
            "Trompette", "Trombone", "Cor d'harmonie", "Tuba",
            "Bugle doux", "Cuivres fanfare", "Section unison",
        };

        static readonly double[,] PresetValues =
        {
            //          breath brNoi lipT damp brgh bell vibR vibD attk rel  width vol
            /*Trompette*/{ 0.60, 0.18, 0.65, 0.30, 0.65, 0.35, 5.0, 8.0, 0.06, 0.15, 0.20, -6.0 },
            /*Trombone*/ { 0.55, 0.22, 0.55, 0.35, 0.55, 0.45, 4.5, 6.0, 0.09, 0.20, 0.20, -5.0 },
            /*Cor*/      { 0.45, 0.15, 0.35, 0.30, 0.45, 0.60, 4.0, 5.0, 0.12, 0.25, 0.15, -6.0 },
            /*Tuba*/     { 0.65, 0.25, 0.30, 0.45, 0.35, 0.85, 3.5, 4.0, 0.15, 0.30, 0.10, -4.0 },
            /*Bugle*/    { 0.40, 0.20, 0.25, 0.40, 0.50, 0.55, 4.0, 8.0, 0.20, 0.35, 0.15, -6.0 },
            /*Fanfare*/  { 0.85, 0.30, 0.85, 0.25, 0.75, 0.30, 5.5, 12.0, 0.03, 0.10, 0.30, -3.0 },
            /*Section*/  { 0.60, 0.20, 0.55, 0.35, 0.60, 0.45, 4.8, 7.0, 0.10, 0.25, 0.75, -5.0 },
        };

        public void ApplyInstrumentDefaults(int index)
        {
            if (index < 0 || index >= PresetValues.GetLength(0)) return;
            _breathPressure.Value = PresetValues[index, 0];
            _breathNoise.Value    = PresetValues[index, 1];
            _lipTension.Value     = PresetValues[index, 2];
            _damping.Value        = PresetValues[index, 3];
            _brightness.Value     = PresetValues[index, 4];
            _bellSize.Value       = PresetValues[index, 5];
            _vibratoRate.Value    = PresetValues[index, 6];
            _vibratoDepth.Value   = PresetValues[index, 7];
            _attackTime.Value     = PresetValues[index, 8];
            _releaseTime.Value    = PresetValues[index, 9];
            _stereoWidth.Value    = PresetValues[index, 10];
            _volumeDb.Value       = PresetValues[index, 11];
        }

        // =============================================================================================
        // Cycle
        // =============================================================================================
        public void Prepare(int sampleRate, int maxBlockSize)
        {
            _sampleRate = sampleRate;
            _maxBlockSize = maxBlockSize;
            _voices = new BrassVoice[Polyphony];
            for (int i = 0; i < Polyphony; i++) _voices[i] = new BrassVoice(sampleRate);
        }

        public void Reset()
        {
            if (_voices != null) foreach (var v in _voices) v.Kill();
        }

        // =============================================================================================
        // MIDI
        // =============================================================================================
        public void NoteOn(int note, int velocity, int sampleOffset = 0)
        {
            if (_voices == null || velocity == 0) return;
            var p = ToVoiceParams();
            float vel = velocity / 127f;

            BrassVoice target = null;
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

        public void SetPitchBend(float value, int sampleOffset = 0) { /* v1 : ignoré */ }

        // =============================================================================================
        // Render
        // =============================================================================================
        public void Render(Span<float> left, Span<float> right)
        {
            if (_voices == null) { left.Clear(); right.Clear(); return; }

            var p = ToVoiceParams();
            float volLin = (float)Math.Pow(10.0, p.VolumeDb / 20.0);
            float width = (float)_stereoWidth.Value;

            // Met à jour le formant si l'instrument a changé (signature spectrale spécifique)
            int instrIdx = Math.Max(0, Math.Min(FormantByInstrument.Length - 1, (int)_instrument.Value));
            if (instrIdx != _lastFormantInstrument)
            {
                var (fFreq, fQ, fGain) = FormantByInstrument[instrIdx];
                SetBiquadPeak(ref _formantL, _sampleRate, fFreq, fQ, fGain);
                SetBiquadPeak(ref _formantR, _sampleRate, fFreq, fQ, fGain);
                _lastFormantInstrument = instrIdx;
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
                    // Pan par voix selon la note (basses gauche / aigus droite)
                    float noteNorm = (voice.Note - 60) / 24f;
                    if (noteNorm < -1f) noteNorm = -1f; else if (noteNorm > 1f) noteNorm = 1f;
                    float p01 = 0.5f + noteNorm * width * 0.5f;
                    sumL += s * (1f - p01);
                    sumR += s * p01;
                }
                // Formant filter en sortie — donne à l'instrument sa vraie signature spectrale
                sumL = BiquadPeakProcess(ref _formantL, sumL);
                sumR = BiquadPeakProcess(ref _formantR, sumR);
                left[i] = sumL * volLin;
                right[i] = sumR * volLin;
            }
        }

        // Biquad peak filter RBJ cookbook pour le formant caractéristique de l'instrument
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

        BrassParams ToVoiceParams() => new BrassParams
        {
            BreathPressure    = (float)_breathPressure.Value,
            BreathNoise       = (float)_breathNoise.Value,
            LipTension        = (float)_lipTension.Value,
            Damping           = (float)_damping.Value,
            Brightness        = (float)_brightness.Value,
            BellSize          = (float)_bellSize.Value,
            VibratoRateHz     = (float)_vibratoRate.Value,
            VibratoDepthCents = (float)_vibratoDepth.Value,
            AttackSec         = (float)_attackTime.Value,
            ReleaseSec        = (float)_releaseTime.Value,
            VolumeDb          = (float)_volumeDb.Value,
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

        public void SetParam(string id, double value)
        {
            foreach (var kp in _params)
                if (kp.Id == id) { kp.Value = value; return; }
        }
    }
}
