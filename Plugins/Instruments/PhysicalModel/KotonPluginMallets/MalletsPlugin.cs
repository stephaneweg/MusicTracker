using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Windows.Controls;
using KotonStudio.Library;

namespace KotonPluginMallets
{
    /// <summary>Une "brique" modale : ratio de fréquence par rapport à la fondamentale, temps de
    /// décroissance en ms, et amplitude initiale relative. La sommation de plusieurs Mode définit
    /// le timbre caractéristique d'un instrument à barres/lames/tubes.</summary>
    internal struct Mode
    {
        public float Ratio;      // multiplicateur de fréquence (1.0 = fondamentale)
        public float DecayMs;    // temps pour tomber sous -60 dB
        public float Amp;        // amplitude relative (typiquement 0..1)

        public Mode(float ratio, float decayMs, float amp) { Ratio = ratio; DecayMs = decayMs; Amp = amp; }
    }

    /// <summary>Preset d'instrument à sons déterminés : jeu de modes + defaults de mallet et de body.
    /// Les ratios inharmoniques sont ce qui distingue un marimba d'un xylophone d'un glockenspiel :
    /// même méthode (sommation de sinusoïdes amorties), palettes de fréquences très différentes.</summary>
    internal sealed class MalletPreset
    {
        public string Name;
        public Mode[] Modes;
        // Excitation LP par défaut (mallet dur = accentue les aigus, mou = filtre les aigus)
        public float DefaultMalletHardness;   // 0..1
        // Long/court : le Damping multiplie tous les DecayMs — permet à l'utilisateur de fondre ou
        // écourter la queue sans re-toucher chaque mode individuellement.
        public float DefaultDamping;          // 0..2
        // Tremolo amplitude (utile pour vibraphone) — 0 par défaut
        public float DefaultTremoloRateHz;
        public float DefaultTremoloDepth;

        public MalletPreset(string name, Mode[] modes, float malletHardness = 0.5f, float damping = 1f, float tremRate = 0f, float tremDepth = 0f)
        {
            Name = name; Modes = modes;
            DefaultMalletHardness = malletHardness;
            DefaultDamping = damping;
            DefaultTremoloRateHz = tremRate;
            DefaultTremoloDepth = tremDepth;
        }
    }

    /// <summary>
    /// Mallets — synthèse modale pour percussion à sons déterminés : xylophone, marimba, vibraphone,
    /// glockenspiel, balafon, cloches tubulaires, steel drum, kalimba. Le troisième plugin de
    /// modélisation physique après Karplus-Strong (corde pincée) et Bowed Strings (corde frottée),
    /// couvrant la famille "objet solide vibrant frappé".
    ///
    /// **Algorithme** : chaque note = somme de N sinusoïdes amorties. Chaque sinusoïde ("mode")
    /// est définie par un ratio de fréquence par rapport à la fondamentale, un temps de décroissance,
    /// et une amplitude initiale. C'est ce que Pigments (Arturia) appelle "Modal Engine" et ce que
    /// le monde académique connaît sous le nom de <i>modal synthesis</i> (Adrien 1991, Cook 2002).
    ///
    /// **Pourquoi ça marche** : l'analyse de Fourier montre qu'une vraie barre de marimba ou une
    /// cloche vibre effectivement dans un petit nombre de modes propres (fréquence + décay). Une
    /// simulation exhaustive des équations d'ondes 2D/3D serait équivalente mais 1000× plus chère.
    /// La sommation directe est le compromis moderne.
    ///
    /// **Ratios inharmoniques** = signature acoustique :
    /// - Marimba : 1, 3.9, 9.5 (bar en bois + résonateur tube en dessous)
    /// - Xylophone : 1, 3, 6 (bar plus courte, spectre pauvre)
    /// - Vibraphone : 1, 4, 9 (métal + tubes accordés + moteur d'amplitude tremolo)
    /// - Glockenspiel : 1, 2.76, 5.4 (métal aigu, très inharmonique)
    /// - Cloches : 0.5 (hum), 1, 1.5, 2, 2.5 (spectre complexe, decay très long)
    /// - Kalimba : 1, 5.4, 12.6 (lame courte de métal, très inharmonique)
    ///
    /// **Mallet hardness** module l'excitation initiale : dur = amplitude proportionnelle au ratio
    /// (accentue les aigus, son "attaque plastique"), mou = amplitude constante (attaque douce,
    /// "feutre"). C'est la même différence qu'entre un marimba joué avec une baguette dure vs. avec
    /// une baguette feutrée.
    /// </summary>
    [KotonInstrument("Mallets", Id = "koton.mallets", Category = "Physical Model", Version = "1.0", Vendor = "Koton Studio")]
    public sealed class MalletsPlugin : IKotonInstrument
    {
        public string Id => "koton.mallets";
        public string DisplayName => "Mallets";

        // =============================================================================================
        // Paramètres
        // =============================================================================================
        // Instrument : discret 0..7 (index dans PresetsData). Un changement ré-arme les modes courants.
        readonly KotonParameter _instrument     = new KotonParameter("instrument",       "Instrument",      0, 7, 0);
        readonly KotonParameter _malletHardness = new KotonParameter("mallet_hardness",  "Mallet hardness", 0.0, 1.0, 0.5);
        // Position sur la barre : 0 = centre (fondamentale forte), 1 = bord (accentue les partiels).
        readonly KotonParameter _position       = new KotonParameter("position",         "Position",        0.0, 1.0, 0.3);
        readonly KotonParameter _damping        = new KotonParameter("damping",          "Damping",         0.1, 3.0, 1.0);
        readonly KotonParameter _brightness     = new KotonParameter("brightness",       "Brightness",      0.0, 1.0, 0.6);
        readonly KotonParameter _tremRate       = new KotonParameter("trem_rate",        "Tremolo rate",    0.0, 12.0, 0.0, "Hz");
        readonly KotonParameter _tremDepth      = new KotonParameter("trem_depth",       "Tremolo depth",   0.0, 1.0, 0.0);
        readonly KotonParameter _stereoSpread   = new KotonParameter("stereo_spread",    "Stereo spread",   0.0, 1.0, 0.4);
        readonly KotonParameter _volumeDb       = new KotonParameter("volume",           "Volume",          -30.0, 6.0, -3.0, "dB");

        readonly List<KotonParameter> _params;
        public IReadOnlyList<KotonParameter> Parameters => _params;

        int _sampleRate;
        int _maxBlockSize;
        ModalVoice[] _voices;
        int _stealCursor;
        const int Polyphony = 16;

        public MalletsPlugin()
        {
            _params = new List<KotonParameter>
            {
                _instrument, _malletHardness, _position, _damping, _brightness,
                _tremRate, _tremDepth, _stereoSpread, _volumeDb,
            };
        }

        public bool HasEditor => true;
        public UserControl CreateEditor() => new MalletsEditor(this);

        // =============================================================================================
        // Presets — l'ordre EST l'index du paramètre _instrument
        // =============================================================================================
        public static readonly string[] InstrumentNames =
        {
            "Marimba", "Xylophone", "Vibraphone", "Glockenspiel",
            "Balafon", "Cloches tubulaires", "Steel drum", "Kalimba",
        };

        static readonly MalletPreset[] Presets = new[]
        {
            // Marimba : bar bois + résonateur tube dessous, chaude, decay moyen
            new MalletPreset("Marimba",
                new[] { new Mode(1.00f, 800f, 1.00f), new Mode(3.93f, 400f, 0.42f), new Mode(9.55f, 200f, 0.15f) },
                malletHardness: 0.35f, damping: 1.0f),

            // Xylophone : bar bois court, dur, decay très court
            new MalletPreset("Xylophone",
                new[] { new Mode(1.00f, 300f, 1.00f), new Mode(3.00f, 150f, 0.55f), new Mode(6.00f, 80f, 0.25f) },
                malletHardness: 0.65f, damping: 1.0f),

            // Vibraphone : métal + tubes accordés + tremolo moteur
            new MalletPreset("Vibraphone",
                new[] { new Mode(1.00f, 3000f, 1.00f), new Mode(4.00f, 1500f, 0.38f), new Mode(9.00f, 800f, 0.15f) },
                malletHardness: 0.30f, damping: 1.0f,
                tremRate: 5.5f, tremDepth: 0.45f),

            // Glockenspiel : petites lames métal, très brillant et inharmonique
            new MalletPreset("Glockenspiel",
                new[] {
                    new Mode(1.00f, 1500f, 1.00f), new Mode(2.76f, 800f, 0.60f),
                    new Mode(5.40f, 400f, 0.35f), new Mode(8.93f, 200f, 0.15f),
                },
                malletHardness: 0.75f, damping: 1.0f),

            // Balafon : bar bois + résonateur (calebasse) — similaire marimba mais plus brut
            new MalletPreset("Balafon",
                new[] { new Mode(1.00f, 600f, 1.00f), new Mode(3.93f, 300f, 0.45f), new Mode(9.55f, 150f, 0.20f) },
                malletHardness: 0.50f, damping: 1.0f),

            // Cloches tubulaires : hum tone très grave (0.5) + spectre complexe, decay long
            new MalletPreset("Cloches tubulaires",
                new[] {
                    new Mode(0.51f, 8000f, 0.30f),
                    new Mode(1.00f, 6000f, 1.00f),
                    new Mode(1.50f, 4000f, 0.50f),
                    new Mode(1.99f, 3000f, 0.70f),
                    new Mode(2.51f, 1500f, 0.30f),
                },
                malletHardness: 0.60f, damping: 1.0f),

            // Steel drum (pan) : métal, spectre riche, attaque marquée
            new MalletPreset("Steel drum",
                new[] { new Mode(1.00f, 500f, 1.00f), new Mode(2.00f, 300f, 0.55f), new Mode(3.00f, 200f, 0.35f), new Mode(4.00f, 150f, 0.20f) },
                malletHardness: 0.65f, damping: 1.0f),

            // Kalimba (thumb piano) : lame métal courte, très inharmonique
            new MalletPreset("Kalimba",
                new[] { new Mode(1.00f, 1500f, 1.00f), new Mode(5.40f, 500f, 0.30f), new Mode(12.60f, 200f, 0.10f) },
                malletHardness: 0.55f, damping: 1.0f),
        };

        internal static MalletPreset GetPreset(int index)
        {
            if (index < 0 || index >= Presets.Length) return Presets[0];
            return Presets[index];
        }

        /// <summary>Ré-applique les défauts d'un preset (tous les params sauf le volume et le
        /// tremolo qu'on garde stables pour ne pas casser la mise). Utilisé quand l'utilisateur
        /// change d'instrument dans le combo.</summary>
        public void ApplyInstrumentDefaults(int index)
        {
            var preset = GetPreset(index);
            _malletHardness.Value = preset.DefaultMalletHardness;
            _damping.Value = preset.DefaultDamping;
            _tremRate.Value = preset.DefaultTremoloRateHz;
            _tremDepth.Value = preset.DefaultTremoloDepth;
        }

        // =============================================================================================
        // Cycle de vie
        // =============================================================================================
        public void Prepare(int sampleRate, int maxBlockSize)
        {
            _sampleRate = sampleRate;
            _maxBlockSize = maxBlockSize;
            _voices = new ModalVoice[Polyphony];
            for (int i = 0; i < Polyphony; i++) _voices[i] = new ModalVoice(sampleRate);
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
            var preset = GetPreset((int)_instrument.Value);
            float vel = velocity / 127f;

            // Voice stealing : voix libre en priorité, sinon round-robin. Un re-strike sur la même
            // note (main gauche qui tape 2× le même do) ré-arme aussi.
            ModalVoice target = null;
            for (int i = 0; i < _voices.Length; i++)
                if (_voices[i].IsActive && _voices[i].Note == note) { target = _voices[i]; break; }
            if (target == null)
                for (int i = 0; i < _voices.Length; i++) if (!_voices[i].IsActive) { target = _voices[i]; break; }
            if (target == null)
            {
                target = _voices[_stealCursor];
                _stealCursor = (_stealCursor + 1) % _voices.Length;
            }

            target.NoteOn(note, vel, preset.Modes,
                          (float)_malletHardness.Value,
                          (float)_position.Value,
                          (float)_damping.Value,
                          (float)_brightness.Value);
        }

        public void NoteOff(int note, int sampleOffset = 0)
        {
            // No-op volontaire : une percussion à son déterminé n'a pas de note-off actif — la barre
            // décroît naturellement selon les DecayMs de ses modes. Un vrai marimba n'a pas de damper
            // (contrairement à un piano). Pour un "damper" manuel style piano, on ajouterait plus
            // tard un CC64 sustain qui multiplierait les decays en release.
        }

        public void MidiCC(int cc, int value, int sampleOffset = 0)
        {
            if (cc == 123) Reset();
        }

        public void SetPitchBend(float value, int sampleOffset = 0)
        {
            // Non supporté v1 (Modal = fréquences fixes par voix)
        }

        // =============================================================================================
        // Render
        // =============================================================================================
        // Phase globale du tremolo — partagée par toutes les voix pour un effet cohérent (comme un
        // vibraphone où le moteur unique fait tourner les ailettes de TOUS les tubes en phase).
        double _tremoloPhase;

        public void Render(Span<float> left, Span<float> right)
        {
            if (_voices == null) { left.Clear(); right.Clear(); return; }

            float volLin = (float)Math.Pow(10.0, _volumeDb.Value / 20.0);
            float tremRate = (float)_tremRate.Value;
            float tremDepth = (float)_tremDepth.Value;
            float stereoSpread = (float)_stereoSpread.Value;
            double tremInc = 2.0 * Math.PI * tremRate / _sampleRate;

            int n = left.Length;
            for (int i = 0; i < n; i++)
            {
                _tremoloPhase += tremInc;
                if (_tremoloPhase > 2 * Math.PI) _tremoloPhase -= 2 * Math.PI;
                float trem = 1f - tremDepth * 0.5f * (1f - (float)Math.Cos(_tremoloPhase));   // 1..1-depth, sinusoïdal

                float sumL = 0f, sumR = 0f;
                for (int v = 0; v < _voices.Length; v++)
                {
                    var voice = _voices[v];
                    if (!voice.IsActive) continue;
                    float s = voice.RenderSample() * trem;
                    // Pan par voix : réparti selon le numéro de note (basses à gauche, aigus à droite,
                    // ampleur pilotée par stereoSpread) — comme un vrai pupitre de mallet devant un
                    // musicien : mains gauche/droite balayent l'espace.
                    float noteNorm = (voice.Note - 60) / 24f;   // -1 (C4-24 st) .. +1 (C4+24 st)
                    if (noteNorm < -1f) noteNorm = -1f; else if (noteNorm > 1f) noteNorm = 1f;
                    float p01 = 0.5f + noteNorm * stereoSpread * 0.5f;   // 0..1
                    float gL = 1f - p01;
                    float gR = p01;
                    sumL += s * gL;
                    sumR += s * gR;
                }
                left[i] = sumL * volLin;
                right[i] = sumR * volLin;
            }
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

        public void SetParam(string id, double value)
        {
            foreach (var kp in _params)
                if (kp.Id == id) { kp.Value = value; return; }
        }
    }
}
