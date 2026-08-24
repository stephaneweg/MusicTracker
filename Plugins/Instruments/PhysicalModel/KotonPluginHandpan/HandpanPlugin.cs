using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Windows.Controls;
using KotonStudio.Library;

namespace KotonPluginHandpan
{
    /// <summary>
    /// Handpan (Hang) — bol de métal accordé type Halo/Hang drum. Deux composantes clés qui le
    /// distinguent d'un simple mallet steel drum :
    ///
    /// **1. Compound tone** (le "3e mode" à ratio ~3.0) — ce mode inharmonique ajoute une note
    /// "fantôme" à la douzième au-dessus de chaque frappe, ce qui donne la profondeur unique
    /// (comme si chaque note était accompagnée en secret de sa quinte-octave). Absent d'un simple
    /// steel drum.
    ///
    /// **2. Résonance sympathique** — un vrai handpan étant un bol continu, quand tu frappes une
    /// note, les autres notes déjà résonantes sont RÉACTIVÉES par couplage acoustique. Effet
    /// audible : quand tu joues une nouvelle note, tu entends les précédentes "gonfler" légèrement
    /// avant de continuer leur décroissance. C'est ce qui donne le caractère "cathédrale de
    /// méditation" typique du handpan qu'un simple synthé modal isolé n'atteint pas.
    ///
    /// **Implémentation sympathie** : au NoteOn d'une nouvelle voix, on parcourt toutes les voix
    /// déjà actives et on cherche les modes dont la fréquence est un HARMONIQUE PROCHE (à ±2%)
    /// de la nouvelle fondamentale : unisson (1:1), octave (1:2), quinte (2:3), quarte (3:4),
    /// double octave (1:4), 12ème (1:3). Pour chaque match on ré-injecte de l'énergie
    /// proportionnellement à Sympathy × velocity.
    /// </summary>
    [KotonInstrument("Handpan", Id = "koton.handpan", Category = "Physical Model", Version = "1.0", Vendor = "Koton Studio")]
    public sealed class HandpanPlugin : IKotonInstrument
    {
        public string Id => "koton.handpan";
        public string DisplayName => "Handpan";

        readonly KotonParameter _malletHardness = new KotonParameter("mallet_hardness", "Touch",          0.0, 1.0, 0.35);
        readonly KotonParameter _resonance      = new KotonParameter("resonance",       "Resonance",      0.0, 1.0, 0.35);
        readonly KotonParameter _brightness     = new KotonParameter("brightness",      "Brightness",     0.0, 1.0, 0.45);
        readonly KotonParameter _sympathy       = new KotonParameter("sympathy",        "Sympathy",       0.0, 1.0, 0.55);
        readonly KotonParameter _shellMix       = new KotonParameter("shell_mix",       "Shell (bol)",    0.0, 1.0, 0.35);
        readonly KotonParameter _stereoSpread   = new KotonParameter("stereo_spread",   "Stereo spread",  0.0, 1.0, 0.35);
        readonly KotonParameter _volumeDb       = new KotonParameter("volume",          "Volume",         -30.0, 6.0, -4.0, "dB");
        // Ré-attaque périodique : rejoue la note tenue tous les 1/taux de seconde.
        // 0 Hz = une seule attaque, donc aucun projet existant ne change.
        readonly KotonStudio.Plugins.Shared.KotonReAttack _retrig =
            new KotonStudio.Plugins.Shared.KotonReAttack("Trémolo", 20.0, 0.0);

        readonly List<KotonParameter> _params;
        public IReadOnlyList<KotonParameter> Parameters => _params;

        int _sampleRate;
        HandpanVoice[] _voices;
        int _stealCursor;
        const int Polyphony = 12;   // moins que Mallets, les notes tiennent plus longtemps

        // Modes standards d'un handpan (mesurés sur Halo pan D). Fondamentale TRÈS long (sustain
        // chantant qui domine), harmoniques BRÈVES (juste pour le transient d'attaque métallique).
        // Fix 2026-08-02 : version precedente avait tous les modes avec long decay → son trop
        // "cluster de metal" ; l'user voulait un sinus quasi-pur avec belle attaque + long sustain,
        // comme un vrai handpan.
        static readonly HpMode[] StandardModes = new[]
        {
            new HpMode { Ratio = 1.00f, DecayMs = 6000f },   // fondamentale : sustain 6s (le "chant" du bol)
            new HpMode { Ratio = 2.00f, DecayMs = 800f },    // octave : bref, attaque seule
            new HpMode { Ratio = 3.00f, DecayMs = 500f },    // compound tone (12ème) — bref
            new HpMode { Ratio = 4.00f, DecayMs = 300f },    // 2e octave : très bref
            new HpMode { Ratio = 5.05f, DecayMs = 200f },    // inharmonique : juste transient
        };

        // Amplitudes relatives : fondamentale DOMINE largement (0.75), harmoniques juste pour
        // colorer l'attaque (0.15, 0.10, 0.05, 0.03). Somme = 1.08 → petite saturation tanh
        // en sortie qui donne le pic d'attaque riche.
        static readonly float[] StandardModeAmps = { 0.75f, 0.15f, 0.10f, 0.05f, 0.03f };

        // Résonance du corps (shell = bol métallique) : biquad bandpass sur le mix final
        BiquadState _shellL, _shellR;

        // Buffer scratch pour ApplySympathy (évite d'allouer à chaque NoteOn)
        readonly double[] _scratchFreqs = new double[8];

        public HandpanPlugin()
        {
            _params = new List<KotonParameter>
            {
                _malletHardness, _resonance, _brightness, _sympathy, _shellMix, _stereoSpread, _volumeDb, _retrig.Rate };
        }

        public bool HasEditor => true;
        public UserControl CreateEditor() => new HandpanEditor(this);

        public void Prepare(int sampleRate, int maxBlockSize)
        {
            _sampleRate = sampleRate;
            _voices = new HandpanVoice[Polyphony];
            for (int i = 0; i < Polyphony; i++) _voices[i] = new HandpanVoice(sampleRate);
            // Shell resonance : bandpass ~120 Hz Q=4 (le "boom" du bol métallique)
            SetBiquadBandpass(ref _shellL, sampleRate, 120f, 4f);
            SetBiquadBandpass(ref _shellR, sampleRate, 120f, 4f);
            _retrig.Prepare(sampleRate);
        }

        public void Reset()
        {
            if (_voices != null) foreach (var v in _voices) v.Kill();
            _shellL.ResetState();
            _shellR.ResetState();
            _retrig.Reset();
        }

        public void NoteOn(int note, int velocity, int sampleOffset = 0)
        {
            _retrig.NoteOn(note, velocity);
            if (_voices == null || velocity == 0) return;
            float vel = velocity / 127f;
            var p = ToVoiceParams();

            // Trouve une voix libre ou stealle
            HandpanVoice target = null;
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
            float pan = noteNorm * (float)_stereoSpread.Value;

            target.NoteOn(note, vel, StandardModes, StandardModeAmps, p, pan);

            // Sympathie : la nouvelle note excite les modes des voix déjà actives dont la fréquence
            // est un harmonique proche. Étape séparée du NoteOn car il faut la faire APRÈS
            // l'insertion de la nouvelle voix (mais avant qu'elle ne rende).
            if (p.Sympathy > 0.01f)
            {
                // Prépare la liste des fréquences de la nouvelle note (sa fondamentale + ses modes)
                double freq = 440.0 * Math.Pow(2.0, (note - 69) / 12.0);
                int nFreqs = 0;
                foreach (var m in StandardModes)
                {
                    if (nFreqs >= _scratchFreqs.Length) break;
                    _scratchFreqs[nFreqs++] = freq * m.Ratio;
                }
                for (int i = 0; i < _voices.Length; i++)
                {
                    if (_voices[i] == target) continue;   // pas la voix qu'on vient de créer
                    if (!_voices[i].IsActive) continue;
                    _voices[i].ApplySympathy(_scratchFreqs, nFreqs, p.Sympathy * vel);
                }
            }
        }

        public void NoteOff(int note, int sampleOffset = 0)
        {
            _retrig.NoteOff(note);
            // Damping "main sur le bol" : accelere la decroissance des modes actifs pour cette note.
            // Sinon le sustain naturel (10s x Resonance) rend un enchainement de notes rapides
            // impossible a ecouter (drone infini). Le geste physique du joueur = pose de la main.
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

            float volLin = (float)Math.Pow(10.0, _volumeDb.Value / 20.0);
            float shellMix = (float)_shellMix.Value;
            float dry = 1f - shellMix * 0.5f;

            int n = left.Length;
            for (int i = 0; i < n; i++)
            {
                // Ré-attaque : à l'échéance, la note tenue est rejouée (BeginStroke neutralise
                // la notification que NoteOn va renvoyer à l'engin).
                if (_retrig.Tick()) { _retrig.BeginStroke(); for (int rt = 0; rt < _retrig.Count; rt++) NoteOn(_retrig.NoteAt(rt), _retrig.VelocityAt(rt)); _retrig.EndStroke(); }

                float sumL = 0f, sumR = 0f;
                for (int v = 0; v < _voices.Length; v++)
                {
                    var voice = _voices[v];
                    if (!voice.IsActive) continue;
                    float s = voice.RenderSample();
                    sumL += s * voice.PanL;
                    sumR += s * voice.PanR;
                }

                // Shell resonance : bandpass sur le mono sum, mixé
                float mono = (sumL + sumR) * 0.5f;
                float shellOutL = BiquadProcess(ref _shellL, mono);
                float shellOutR = BiquadProcess(ref _shellR, mono);
                float outL = sumL * dry + shellOutL * shellMix;
                float outR = sumR * dry + shellOutR * shellMix;

                left[i] = outL * volLin;
                right[i] = outR * volLin;
            }
        }

        HandpanParams ToVoiceParams() => new HandpanParams
        {
            MalletHardness = (float)_malletHardness.Value,
            Resonance      = (float)_resonance.Value,
            Brightness     = (float)_brightness.Value,
            Sympathy       = (float)_sympathy.Value,
            ShellMix       = (float)_shellMix.Value,
            StereoSpread   = (float)_stereoSpread.Value,
            VolumeDb       = (float)_volumeDb.Value,
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

        // Presets
        public static readonly string[] PresetNames = { "Handpan meditatif", "Handpan brillant", "Handpan sombre", "Handpan resonant (grand hall)", "Steel pan (percussif)" };
        static readonly double[,] PresetValues = {
            //          touch reso brgh symp shell spread vol
            /*Med*/     { 0.30, 0.65, 0.40, 0.65, 0.35, 0.35, -4.0 },
            /*Brill*/   { 0.60, 0.55, 0.75, 0.45, 0.25, 0.30, -5.0 },
            /*Sombre*/  { 0.20, 0.75, 0.20, 0.55, 0.55, 0.35, -3.0 },
            /*Hall*/    { 0.35, 0.95, 0.50, 0.80, 0.45, 0.55, -5.0 },
            /*Perc*/    { 0.75, 0.30, 0.60, 0.15, 0.20, 0.25, -4.0 },
        };
        public void LoadPreset(int index)
        {
            if (index < 0 || index >= PresetValues.GetLength(0)) return;
            _malletHardness.Value = PresetValues[index, 0];
            _resonance.Value      = PresetValues[index, 1];
            _brightness.Value     = PresetValues[index, 2];
            _sympathy.Value       = PresetValues[index, 3];
            _shellMix.Value       = PresetValues[index, 4];
            _stereoSpread.Value   = PresetValues[index, 5];
            _volumeDb.Value       = PresetValues[index, 6];
        }

        public void SetParam(string id, double value)
        {
            foreach (var kp in _params) if (kp.Id == id) { kp.Value = value; return; }
        }

        // Biquad bandpass RBJ cookbook
        internal struct BiquadState
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
