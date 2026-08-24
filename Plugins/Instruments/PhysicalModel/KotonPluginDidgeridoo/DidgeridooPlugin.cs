using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Windows.Controls;
using KotonStudio.Library;

namespace KotonPluginDidgeridoo
{
    /// <summary>
    /// Didgeridoo (yidaki) — modele source-filtre physique, monophonique.
    ///
    /// Chaine : valve labiale -> anti-formants du conduit vocal -> guide d'onde du tube
    /// (cylindre ferme aux levres / ouvert au pavillon) -> rayonnement.
    ///
    /// Trois points d'acoustique gouvernent le rendu (cf. Tarnopolsky, Fletcher, Hollenberg, Lange,
    /// Smith &amp; Wolfe, "Vocal tract resonances and the sound of the Australian didjeridu", JASA 119(2)
    /// 2006, et Nature 436, 39, 2005) :
    ///
    /// 1. Les levres sont FERMEES environ la moitie du cycle : le debit d'air est une impulsion etroite,
    ///    tres riche en harmoniques (composantes en ~n^-2). D'ou une valve a SEUIL — sinusoide ecretee,
    ///    la duree d'ouverture etant le reglage "Levres" et la pression abaissant le seuil. La formule
    ///    fermee de Fletcher, u = (1-a)^2 / (1 + a.sin)^2, a d'abord ete essayee mais sa decroissance
    ///    mesuree est de ~8 dB par harmonique : plus rien au-dessus de 700 Hz, alors que le didge porte
    ///    jusqu'a 4 kHz. Elle ne ferme jamais la valve, ce qui est justement l'origine des aigus.
    /// 2. Le tube est un cylindre ferme d'un cote : ses resonances tombent sur les harmoniques IMPAIRES
    ///    (f, 3f, 5f...) et sont legerement etirees (perce irreguliere). D'ou un peigne recursif a
    ///    contre-reaction NEGATIVE, avec un passe-tout dans la boucle pour l'inharmonicite.
    /// 3. Le conduit vocal ne cree pas des formants, il creuse des CREUX : les maxima d'impedance du
    ///    conduit (~1.5 / 2.1 / 2.8 kHz langue haute) produisent des minima dans le spectre rayonne, et
    ///    ce qui subsiste entre deux creux s'entend comme un formant. D'ou trois cloches NEGATIVES aux
    ///    maxima d'impedance et deux cloches positives entre elles — et non un passe-bande sur la source
    ///    comme dans la v1, qui sonnait "voyelle synthetique" et pas didgeridoo.
    ///
    /// Clavier : la premiere note lance le bourdon (sa hauteur accorde le tube). Ensuite,
    ///  - re-jouer la meme note = coup de langue (velocite &lt; 96) ou coup de diaphragme (velocite &gt;= 96) ;
    ///  - une note a l'octave ou plus au-dessus = "toot" (2e regime, tenu tant que la touche est tenue) ;
    ///  - toute autre note = glissement legato du bourdon.
    /// CC1 pilote la voyelle, CC2/CC11 la pression, le pitch bend simule la chute de machoire.
    /// </summary>
    [KotonInstrument("Didgeridoo", Id = "koton.didgeridoo", Category = "Physical Model", Version = "2.0", Vendor = "Koton Studio")]
    public sealed class DidgeridooPlugin : IKotonInstrument
    {
        public string Id => "koton.didgeridoo";
        public string DisplayName => "Didgeridoo";

        // --- Tube -------------------------------------------------------------------------------
        readonly KotonParameter _tube      = new KotonParameter("tube",         "Tube (perce & matière)",      0, 1, 0.62);
        readonly KotonParameter _resonance = new KotonParameter("resonance",    "Résonance du tube",           0, 1, 0.60);
        readonly KotonParameter _stretch   = new KotonParameter("stretch",      "Inharmonicité",               0, 1, 0.35);
        readonly KotonParameter _bright    = new KotonParameter("bright",       "Brillance",                   0, 1, 0.50);
        readonly KotonParameter _bassCut   = new KotonParameter("bass_cut",     "Coupe-bas (placement micro)", 0, 1, 0.35);
        readonly KotonParameter _drive     = new KotonParameter("drive",        "Grain",                       0, 1, 0.22);
        readonly KotonParameter _volumeDb  = new KotonParameter("volume",       "Volume",                      -30, 6, -4, "dB");

        // --- Levres & souffle -------------------------------------------------------------------
        readonly KotonParameter _lips      = new KotonParameter("lips",         "Lèvres (ouverture)",          0.10, 0.95, 0.55);
        readonly KotonParameter _pressure  = new KotonParameter("pressure",     "Pression de souffle",         0, 1, 0.70);
        readonly KotonParameter _breath    = new KotonParameter("breath",       "Souffle & salive",            0, 1, 0.16);
        readonly KotonParameter _wobble    = new KotonParameter("wobble",       "Humanisation (dérive)",       0, 1, 0.35);
        readonly KotonParameter _flutter   = new KotonParameter("flutter",      "Flutter de langue",           0, 16, 0, "Hz");
        readonly KotonParameter _tootRatio = new KotonParameter("toot_ratio",   "Toot (x fondamentale)",       1.5, 3.2, 2.0);
        readonly KotonParameter _attack    = new KotonParameter("attack",       "Attaque",                     5, 600, 70, "ms");
        readonly KotonParameter _release   = new KotonParameter("release",      "Relâche",                     50, 3000, 500, "ms");

        // --- Bouche (voyelles) --------------------------------------------------------------------
        readonly KotonParameter _vowel     = new KotonParameter("vowel",        "Voyelle (position de la langue)",   0, 1, 0.45) { Automatable = false };
        readonly KotonParameter _tongue    = new KotonParameter("tongue",       "Langue haute (formant)",      0, 1, 0.72);
        readonly KotonParameter _mouthSize = new KotonParameter("mouth_size",   "Taille de la bouche",         0.60, 1.60, 1.0);
        readonly KotonParameter _modRate   = new KotonParameter("mod_rate",     "Mod. bouche",                 0, 12, 2.6, "Hz");
        readonly KotonParameter _modDepth  = new KotonParameter("mod_depth",    "Amplitude de la mod.",        0, 1, 0.35);
        readonly KotonParameter _modShape  = new KotonParameter("mod_shape",    "Forme de la mod.",            0, 3, 1) { Automatable = false };
        readonly KotonParameter _slew      = new KotonParameter("slew",         "Slew voyelle",                5, 400, 55, "ms");
        readonly KotonParameter _breathCyc = new KotonParameter("breath_cycle", "Respiration circulaire",      0, 12, 5.5, "s");

        readonly List<KotonParameter> _params;
        public IReadOnlyList<KotonParameter> Parameters => _params;

        public DidgeridooPlugin()
        {
            _params = new List<KotonParameter>
            {
                _tube, _resonance, _stretch, _bright, _bassCut, _drive, _volumeDb,
                _lips, _pressure, _breath, _wobble, _flutter, _tootRatio, _attack, _release,
                _vowel, _tongue, _mouthSize, _modRate, _modDepth, _modShape, _slew, _breathCyc
            };
        }

        public bool HasEditor => true;
        public UserControl CreateEditor() => new DidgeridooEditor(this);

        DidgeVoice _voice;
        readonly DidgeParams _p = new DidgeParams();

        public void Prepare(int sampleRate, int maxBlockSize) { _voice = new DidgeVoice(sampleRate); }
        public void Reset() { _voice?.Kill(); }

        public void NoteOn(int note, int velocity, int sampleOffset = 0)
        {
            if (_voice == null || velocity <= 0) return;
            float v = velocity / 127f;
            if (!_voice.IsActive) { _voice.Start(note, v, (float)_attack.Value); return; }

            int delta = note - _voice.Note;
            if (delta == 0)
            {
                // Note repetee sur le bourdon = articulation rythmique. La velocite choisit laquelle :
                // coup de diaphragme (fort) ou coup de langue (le reste).
                if (velocity >= 96) _voice.Overblow(v); else _voice.Plosive(v);
            }
            else if (delta >= 12) _voice.TootOn(note, v, (float)_tootRatio.Value);
            else _voice.Retune(note);
        }

        public void NoteOff(int note, int sampleOffset = 0)
        {
            if (_voice == null || !_voice.IsActive) return;
            if (_voice.IsTootNote(note)) { _voice.TootOff(); return; }
            if (_voice.Note == note) _voice.Release((float)_release.Value);
        }

        public void MidiCC(int cc, int value, int sampleOffset = 0)
        {
            if (_voice == null) return;
            switch (cc)
            {
                case 1:  _voice.CcVowel = value / 127.0 - 0.5; break;                 // molette = balayage de voyelle
                case 2:
                case 11: _voice.CcPressure = 0.35 + 0.65 * (value / 127.0); break;
                case 123: Reset(); break;
            }
        }

        public void SetPitchBend(float value, int sampleOffset = 0)
        {
            // Chute de machoire : un bend descendant assombrit aussi la voyelle, comme sur la note finale.
            _voice?.SetBend(value);
        }

        public void Render(Span<float> left, Span<float> right)
        {
            if (_voice == null || !_voice.IsActive) { left.Clear(); right.Clear(); return; }

            _p.Tube      = _tube.Value;
            _p.Resonance = _resonance.Value;
            _p.Stretch   = _stretch.Value;
            _p.Bright    = _bright.Value;
            _p.BassCut   = _bassCut.Value;
            _p.Drive     = _drive.Value;
            _p.Lips      = _lips.Value;
            _p.Pressure  = _pressure.Value;
            _p.Breath    = _breath.Value;
            _p.Wobble    = _wobble.Value;
            _p.Flutter   = _flutter.Value;
            _p.Vowel     = _vowel.Value;
            _p.Tongue    = _tongue.Value;
            _p.MouthSize = _mouthSize.Value;
            _p.ModRate   = _modRate.Value;
            _p.ModDepth  = _modDepth.Value;
            _p.ModShape  = (int)Math.Round(_modShape.Value);
            _p.SlewMs    = _slew.Value;
            _p.BreathCyc = _breathCyc.Value;

            float gain = (float)Math.Pow(10.0, _volumeDb.Value / 20.0);
            _voice.Render(left, right, _p, gain);
        }

        public byte[] SaveState()
        {
            try
            {
                var d = new Dictionary<string, double>();
                foreach (var kp in _params) d[kp.Id] = kp.Value;
                return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(d));
            }
            catch { return Array.Empty<byte>(); }
        }

        public void LoadState(byte[] state)
        {
            if (state == null || state.Length == 0) return;
            try
            {
                var d = JsonSerializer.Deserialize<Dictionary<string, double>>(Encoding.UTF8.GetString(state));
                if (d == null) return;
                foreach (var kp in _params) if (d.TryGetValue(kp.Id, out var v)) kp.Value = v;
            }
            catch { }
        }

        public void Dispose() { }
        public void SetParam(string id, double value) { foreach (var kp in _params) if (kp.Id == id) { kp.Value = value; return; } }
    }

    /// <summary>Instantane des reglages, recopie une fois par bloc pour eviter de relire (et re-clamper)
    /// les KotonParameter a chaque echantillon.</summary>
    internal sealed class DidgeParams
    {
        public double Tube, Resonance, Stretch, Bright, BassCut, Drive;
        public double Lips, Pressure, Breath, Wobble, Flutter;
        public double Vowel, Tongue, MouthSize, ModRate, ModDepth, SlewMs, BreathCyc;
        public int ModShape;
    }

    /// <summary>Biquad forme directe I, coefficients normalises par a0.</summary>
    internal struct Biquad
    {
        public double B0, B1, B2, A1, A2, X1, X2, Y1, Y2;

        public void Reset() { X1 = X2 = Y1 = Y2 = 0; B0 = 1; B1 = B2 = A1 = A2 = 0; }

        /// <summary>Cloche RBJ. <paramref name="dbGain"/> negatif = creux (maximum d'impedance du
        /// conduit vocal), positif = bosse (minimum d'impedance, donc formant rayonne).</summary>
        public void SetPeaking(double sr, double freq, double q, double dbGain)
        {
            if (freq < 40) freq = 40;
            double nyq = sr * 0.47; if (freq > nyq) freq = nyq;
            if (q < 0.2) q = 0.2;
            double a = Math.Pow(10.0, dbGain / 40.0);
            double w0 = 2.0 * Math.PI * freq / sr;
            double cs = Math.Cos(w0), alpha = Math.Sin(w0) / (2.0 * q);
            double a0 = 1.0 + alpha / a;
            B0 = (1.0 + alpha * a) / a0;
            B1 = (-2.0 * cs) / a0;
            B2 = (1.0 - alpha * a) / a0;
            A1 = (-2.0 * cs) / a0;
            A2 = (1.0 - alpha / a) / a0;
        }

        public void SetBandpass(double sr, double freq, double q)
        {
            if (freq < 40) freq = 40;
            double nyq = sr * 0.47; if (freq > nyq) freq = nyq;
            double w0 = 2.0 * Math.PI * freq / sr;
            double cs = Math.Cos(w0), alpha = Math.Sin(w0) / (2.0 * q);
            double a0 = 1.0 + alpha;
            B0 = alpha / a0; B1 = 0; B2 = -alpha / a0;
            A1 = (-2.0 * cs) / a0; A2 = (1.0 - alpha) / a0;
        }

        public double Process(double x)
        {
            double y = B0 * x + B1 * X1 + B2 * X2 - A1 * Y1 - A2 * Y2;
            X2 = X1; X1 = x; Y2 = Y1; Y1 = y;
            if (double.IsNaN(y) || double.IsInfinity(y)) { X1 = X2 = Y1 = Y2 = 0; return 0; }
            return y;
        }
    }

    internal sealed class DidgeVoice
    {
        const double TwoPi = Math.PI * 2.0;
        const int CtlBlock = 32;   // les coefficients de filtre sont recalcules a cette cadence

        /// <summary>Maxima d'impedance du conduit vocal (Hz) pour 6 positions de langue, de la plus
        /// basse/arriere ("ou") a la plus haute/avant ("i"). Le formant audible ne tombe PAS sur ces
        /// valeurs : il se loge entre deux colonnes voisines (moyenne geometrique), et balaie donc
        /// ~900 Hz -> 2100 Hz sur la course du reglage — la plage du "harmonic sweep" decrit par
        /// Ayers &amp; Horner.</summary>
        static readonly double[,] Tract =
        {
            {  700, 1150, 2100 },   // "ou" — langue basse et arriere,   formant ~900 Hz
            {  850, 1350, 2250 },   // "o"                                formant ~1070 Hz
            { 1000, 1600, 2450 },   // "a"                                formant ~1265 Hz
            { 1200, 1900, 2650 },   // "e"                                formant ~1510 Hz
            { 1450, 2200, 2900 },   // "langue haute" (Tarnopolsky et al.) formant ~1790 Hz
            { 1700, 2600, 3200 },   // "i" — langue haute et avant        formant ~2100 Hz
        };

        readonly int _sr;
        readonly float[] _line;
        readonly int _mask;
        int _wp;

        public bool IsActive { get; private set; }
        public int Note { get; private set; }
        public double CcVowel;              // -0.5..+0.5, molette
        public double CcPressure = 1.0;

        // --- hauteur
        double _noteFreq, _freqSmooth, _bend = 1.0, _bendVowel;
        double _lipPhase;

        // --- toot
        bool _toot; int _tootNote = -1; double _tootRatio = 2.0, _tootMix;

        // --- enveloppe principale
        float _env; float _atkR = 0.001f, _relR = 0.001f; int _stage;   // 0 idle, 1 attaque, 2 tenue, 3 relache
        float _vel = 1f;

        // --- articulations (enveloppes exponentielles, mises a jour par echantillon)
        double _obEnv, _obDec = 0.999, _plEnv, _plDec = 0.999, _plNoise, _plNoiseDec = 0.99, _plChoke;

        // --- modulations lentes
        double _bwSlow, _bwFast, _lfoPhase, _sh, _shPrev, _brown;
        double _breathTimer, _breathPhase = -1, _breathPeriod = 5.5, _breathVowel, _breathSniff, _breathDuck = 1;
        double _flutPhase;

        // --- conduit vocal
        Biquad _n1, _n2, _n3, _p1, _p2;
        double _z1 = 1120, _z2 = 1950, _z3 = 2750;

        // --- tube
        double _loopLp, _apZ, _damp = 0.5, _apC, _fb, _delay = 300;
        Biquad _wall;
        double _wallMix;

        // --- rayonnement
        double _radPrev, _hpX, _hpY, _hpC = 0.99, _dcX, _dcY;
        double _closeBias = 0.585;   // seuil de fermeture des levres, calcule depuis le reglage "Levres"
        readonly double _glide;      // coefficient de glissement de hauteur (~11 ms)
        double _noiseLp;

        uint _rng = 0x9E3779B9;

        public DidgeVoice(int sampleRate)
        {
            _sr = sampleRate <= 0 ? 44100 : sampleRate;
            // Assez long pour la plus basse note utile (MIDI 12 ~ 16.4 Hz) a la frequence d'echantillonnage hote.
            int len = 1;
            while (len < _sr / 8) len <<= 1;
            _line = new float[len];
            _mask = len - 1;
            _glide = 1.0 - Math.Exp(-1.0 / (0.011 * _sr));
            _n1.Reset(); _n2.Reset(); _n3.Reset(); _p1.Reset(); _p2.Reset(); _wall.Reset();
        }

        double Rand() { _rng ^= _rng << 13; _rng ^= _rng >> 17; _rng ^= _rng << 5; return (_rng & 0xFFFFFF) / 16777215.0; }
        double Bipolar() => Rand() * 2.0 - 1.0;
        static double Clamp(double v, double lo, double hi) => v < lo ? lo : (v > hi ? hi : v);

        /// <summary>Approximation rationnelle de tanh. L'ecretage prealable a +-3 est OBLIGATOIRE :
        /// au-dela la fraction diverge (elle tend vers x/9) et le "limiteur" se met a amplifier.</summary>
        static double FastTanh(double x)
        {
            if (x >= 3.0) return 1.0;
            if (x <= -3.0) return -1.0;
            return x * (27.0 + x * x) / (27.0 + 9.0 * x * x);
        }

        public bool IsTootNote(int note) => _toot && note == _tootNote;

        public void Start(int note, float vel, float attackMs)
        {
            Note = note; _vel = vel;
            _noteFreq = 440.0 * Math.Pow(2.0, (note - 69) / 12.0);
            _freqSmooth = _noteFreq;
            _lipPhase = 0; _env = 0; _stage = 1;
            _atkR = 1f / Math.Max(1f, attackMs * _sr / 1000f);
            _toot = false; _tootNote = -1; _tootMix = 0;
            _obEnv = _plEnv = _plNoise = 0; _plChoke = 0;
            _breathTimer = 0; _breathPhase = -1;
            Array.Clear(_line, 0, _line.Length);
            _loopLp = _apZ = _radPrev = _hpX = _hpY = _dcX = _dcY = 0;
            _n1.Reset(); _n2.Reset(); _n3.Reset(); _p1.Reset(); _p2.Reset(); _wall.Reset();
            IsActive = true;
        }

        public void Retune(int note)
        {
            Note = note;
            _noteFreq = 440.0 * Math.Pow(2.0, (note - 69) / 12.0);
            if (_stage == 3) _stage = 2;   // note liee apres un relache = on reprend le souffle
        }

        public void Release(float releaseMs)
        {
            _relR = 1f / Math.Max(1f, releaseMs * _sr / 1000f);
            _stage = 3;
        }

        public void Kill() { IsActive = false; _env = 0; _stage = 0; _toot = false; _tootNote = -1; }

        /// <summary>Coup de diaphragme : bouffee d'air breve et forte.</summary>
        public void Overblow(float amount)
        {
            _obEnv = 0.35 + 0.9 * amount;
            _obDec = Math.Exp(-1.0 / (0.13 * _sr));
        }

        /// <summary>"Ter" / "Ker" : la langue coupe le flux puis le relache, avec un transitoire bruite.</summary>
        public void Plosive(float amount)
        {
            _plEnv = 0.4 + 0.7 * amount;
            _plDec = Math.Exp(-1.0 / (0.05 * _sr));
            _plNoise = 0.6 + 0.5 * amount;
            _plNoiseDec = Math.Exp(-1.0 / (0.006 * _sr));
            _plChoke = 1.0;
        }

        public void TootOn(int note, float vel, float ratio)
        {
            _toot = true; _tootNote = note; _tootRatio = ratio;
            if (_stage == 3) _stage = 2;
        }

        public void TootOff() { _toot = false; _tootNote = -1; }

        public void SetBend(float bend)
        {
            double semis = Clamp(bend, -1, 1) * 2.0;
            _bend = Math.Pow(2.0, semis / 12.0);
            _bendVowel = semis < 0 ? semis * 0.12 : 0;   // machoire qui tombe = timbre plus sombre
        }

        public void Render(Span<float> left, Span<float> right, DidgeParams p, float gain)
        {
            int n = left.Length;
            int i = 0;
            while (i < n)
            {
                int blk = Math.Min(CtlBlock, n - i);
                Control(p, (double)blk / _sr);
                for (int k = 0; k < blk; k++)
                {
                    float s = Tick(p) * gain;
                    if (s > 1f) s = 1f; else if (s < -1f) s = -1f;
                    left[i + k] = s; right[i + k] = s;
                }
                i += blk;
                if (!IsActive) { left.Slice(i).Clear(); right.Slice(i).Clear(); return; }
            }
        }

        void Control(DidgeParams p, double dt)
        {
            // --- derive brownienne : sans elle le bourdon sonne robotique (Hindle & Posnett, NIME 2017).
            _bwSlow += Bipolar() * dt * 3.0;  _bwSlow -= _bwSlow * dt * 0.9;  _bwSlow = Clamp(_bwSlow, -1, 1);
            _bwFast += Bipolar() * dt * 26.0; _bwFast -= _bwFast * dt * 9.0;  _bwFast = Clamp(_bwFast, -1, 1);

            // --- LFO de bouche
            double lfo = 0;
            if (p.ModRate > 0.01)
            {
                _lfoPhase += p.ModRate * dt;
                if (_lfoPhase >= 1.0)
                {
                    _lfoPhase -= Math.Floor(_lfoPhase);
                    _shPrev = _sh; _sh = Bipolar();
                }
                double ph = _lfoPhase;
                switch (p.ModShape)
                {
                    case 0: lfo = Math.Sin(TwoPi * ph); break;                        // cyclique
                    case 1: { double d = 1.0 - ph; lfo = 2.0 * d * d - 1.0; } break;  // "kanga", rebond
                    case 2: lfo = _sh; break;                                         // sample & hold
                    default: lfo = _shPrev + (_sh - _shPrev) * ph; break;             // aleatoire lisse
                }
            }
            _brown += (Bipolar() * 0.5 - _brown) * dt * 2.0;

            // --- geometrie des levres : le reglage fixe la FRACTION du cycle pendant laquelle elles
            //     sont ouvertes (0.46 = levres molles et sourdes, 0.16 = levres serrees et cuivrees).
            //     Le seuil correspondant est cos(pi.duree) : jamais 0, donc jamais le cas degenere de
            //     la sinusoide redressee (qui n'aurait aucune harmonique impaire).
            double duty = 0.46 - 0.30 * Clamp((p.Lips - 0.10) / 0.85, 0, 1);
            _closeBias = Math.Cos(Math.PI * duty);

            // --- respiration circulaire : deplacement de formant puis reniflement, en boucle.
            _breathSniff = 0; _breathVowel = 0; _breathDuck = 1;
            if (p.BreathCyc > 0.5)
            {
                if (_breathPhase < 0)
                {
                    _breathTimer += dt;
                    if (_breathTimer >= _breathPeriod) { _breathPhase = 0; _breathTimer = 0; }
                }
                else
                {
                    _breathPhase += dt / 0.36;
                    if (_breathPhase >= 1.0)
                    {
                        _breathPhase = -1;
                        _breathPeriod = p.BreathCyc * (0.75 + 0.5 * Rand());
                    }
                    else
                    {
                        double b = _breathPhase;
                        double bell = Math.Sin(Math.PI * Math.Min(1.0, b / 0.75));
                        _breathVowel = 0.26 * bell;                    // le timbre change (glotte fermee)
                        _breathDuck = 1.0 - 0.18 * bell;               // la pression retombe un peu
                        if (b > 0.45 && b < 0.8) _breathSniff = Math.Sin(Math.PI * (b - 0.45) / 0.35);
                    }
                }
            }
            else { _breathPhase = -1; _breathTimer = 0; }

            // --- position de la langue, puis frequences cibles des resonances du conduit
            double vw = Clamp(p.Vowel + p.ModDepth * lfo * 0.45 + _breathVowel + CcVowel + _bendVowel
                              + p.Wobble * _brown * 0.10, 0, 1);
            int rows = Tract.GetLength(0);
            double fi = vw * (rows - 1);
            int i0 = (int)fi; if (i0 > rows - 2) i0 = rows - 2;
            double fr = fi - i0;
            double t1 = (Tract[i0, 0] + (Tract[i0 + 1, 0] - Tract[i0, 0]) * fr) * p.MouthSize;
            double t2 = (Tract[i0, 1] + (Tract[i0 + 1, 1] - Tract[i0, 1]) * fr) * p.MouthSize;
            double t3 = (Tract[i0, 2] + (Tract[i0 + 1, 2] - Tract[i0, 2]) * fr) * p.MouthSize;

            double k = 1.0 - Math.Exp(-dt / Math.Max(0.003, p.SlewMs * 0.001));
            _z1 += (t1 - _z1) * k; _z2 += (t2 - _z2) * k; _z3 += (t3 - _z3) * k;

            // Creux aux maxima d'impedance, bosses aux minima (entre deux maxima) : c'est cette paire
            // qui produit le formant caracteristique, pas un passe-bande sur la source.
            // Les creux doivent rester ETROITS : langue haute, les trois maxima ne sont espaces que
            // d'une demi-octave, et avec un Q trop bas ils fusionnent en un seul trou — la voyelle
            // disparait au lieu de monter. Les bosses, elles, sont LARGES : elles portent la bande de
            // formant dans son ensemble et laissent les creux gagner localement.
            // Reponse mesuree de cette cascade seule : ~23 dB de contraste creux/bosse, avec une bosse
            // nette a 895 Hz pour "ou" et a 2020 Hz pour "i". Langue a 0, tout s'aplatit (langue basse
            // = pas de formant marque, exactement ce que decrit le papier).
            double depth = -6.0 - 30.0 * p.Tongue;
            double qNotch = 2.6 + 3.4 * p.Tongue;
            double pkGain = 3.0 + 11.0 * p.Tongue;
            _n1.SetPeaking(_sr, _z1, qNotch, depth);
            _n2.SetPeaking(_sr, _z2, qNotch, depth * 0.85);
            _n3.SetPeaking(_sr, _z3, qNotch, depth * 0.65);
            _p1.SetPeaking(_sr, Math.Sqrt(_z1 * _z2), 2.2, pkGain);
            _p2.SetPeaking(_sr, Math.Sqrt(_z2 * _z3), 2.4, pkGain * 0.5);

            // --- tube : perce large = boucle brillante et resonante ; perce etroite = plus amortie,
            //     avec une resonance de paroi qui ressort.
            _damp = Clamp(0.20 + 0.62 * p.Tube * (0.55 + 0.75 * p.Bright), 0.12, 0.92);
            _apC = -0.34 * p.Stretch;
            _fb = 0.20 + 0.72 * p.Resonance;
            _wallMix = (1.0 - p.Tube) * 0.30;
            _wall.SetBandpass(_sr, 380 + 260 * (1.0 - p.Tube), 1.4);

            // Longueur de ligne : on retranche les retards de groupe du passe-bas et du passe-tout de
            // boucle, sinon le peigne descend sous la fondamentale jouee.
            double f0 = _noteFreq * _bend;
            double total = _sr / (2.0 * Math.Max(12.0, f0));
            double gdLp = (1.0 - _damp) / _damp;
            double gdAp = (1.0 - _apC) / (1.0 + _apC);
            _delay = Clamp(total - gdLp - gdAp, 2.0, _mask - 4.0);

            // --- rayonnement : coupe-bas variable (le fondamental est enorme dans l'axe du tube)
            _hpC = Math.Exp(-TwoPi * (22.0 + 150.0 * p.BassCut) / _sr);

            // --- toot : on y glisse, et le peigne du tube compte moins (regime plus "cor")
            _tootMix += ((_toot ? 1.0 : 0.0) - _tootMix) * Math.Min(1.0, dt * 14.0);
        }

        float Tick(DidgeParams p)
        {
            // --- enveloppe principale
            if (_stage == 1) { _env += _atkR; if (_env >= 1f) { _env = 1f; _stage = 2; } }
            else if (_stage == 3) { _env -= _relR; if (_env <= 0f) { _env = 0f; IsActive = false; return 0f; } }

            // --- enveloppes d'articulation
            _obEnv *= _obDec; if (_obEnv < 1e-5) _obEnv = 0;
            _plEnv *= _plDec; if (_plEnv < 1e-5) _plEnv = 0;
            _plNoise *= _plNoiseDec; if (_plNoise < 1e-5) _plNoise = 0;
            if (_plChoke > 0) { _plChoke -= 1.0 / (0.014 * _sr); if (_plChoke < 0) _plChoke = 0; }

            // --- frequence des levres
            double f0 = _noteFreq * _bend;
            double lipF = f0 * (1.0 + _tootMix * (_tootRatio - 1.0));
            lipF *= 1.0 + p.Wobble * (_bwSlow * 0.0055 + _bwFast * 0.0022);
            _freqSmooth += (lipF - _freqSmooth) * _glide;   // ~11 ms, independant du sample rate

            _lipPhase += _freqSmooth / _sr;
            if (_lipPhase >= 1.0) _lipPhase -= Math.Floor(_lipPhase);

            // --- pression effective au niveau des levres
            double press = p.Pressure * CcPressure * _breathDuck;
            press *= 0.45 + 0.55 * _env;                                 // les levres se lancent progressivement
            press *= 1.0 + 0.45 * _obEnv + 0.15 * _plEnv;
            press *= 1.0 - 0.90 * _plChoke;                              // la langue coupe le flux
            if (p.Flutter > 0.2)
            {
                _flutPhase += p.Flutter / _sr; if (_flutPhase >= 1.0) _flutPhase -= Math.Floor(_flutPhase);
                press *= 1.0 + 0.45 * Math.Sin(TwoPi * _flutPhase);
            }
            press *= 1.0 + p.Wobble * _bwFast * 0.08;
            press = Clamp(press / 0.70, 0.04, 2.2);                      // 1.0 = pression nominale

            // --- valve labiale. La sinusoide est SEUILLEE : les levres se ferment vraiment, environ la
            //     moitie du cycle (Tarnopolsky et al., Fig. 8-9), et c'est cette fermeture — pas une
            //     somme d'oscillateurs — qui produit les harmoniques hautes. Le seuil fixe la duree
            //     d'ouverture ; plus de pression = seuil plus bas = ouverture plus longue et plus forte.
            double bEff = Clamp(_closeBias - 0.55 * (press - 1.0), 0.06, 0.94);
            double raw = Math.Sin(TwoPi * _lipPhase) - bEff;
            double u = raw > 0 ? raw * (0.55 + 0.75 * press) : 0.0;

            // retrait de la composante continue
            _dcY = u - _dcX + 0.9985 * _dcY; _dcX = u;
            u = _dcY * 3.2;

            // --- souffle et salive : bruit "motorboat", module par le debit des levres
            double white = Bipolar();
            _noiseLp += (white - _noiseLp) * 0.22;
            double flow = u > 0 ? u : 0;
            u += _noiseLp * p.Breath * (0.25 + 1.4 * flow) * 0.5;
            // Le "t" est un bruit de bande (~1.7 kHz), pas du blanc plein spectre : sinon le transitoire
            // s'entend comme un clic numerique au lieu d'un coup de langue.
            if (_plNoise > 0) u += (_noiseLp * 0.8 + white * 0.2) * _plNoise * 0.7;
            if (_breathSniff > 0) u += (white - _noiseLp) * _breathSniff * 0.10;  // reniflement nasal

            // --- grain
            if (p.Drive > 0.01)
            {
                u = FastTanh(u * (1.0 + p.Drive * 5.0)) / (1.0 + p.Drive * 1.6);
            }

            // --- conduit vocal
            u = _n1.Process(u); u = _n2.Process(u); u = _n3.Process(u);
            u = _p1.Process(u); u = _p2.Process(u);

            // --- tube : peigne recursif a contre-reaction negative => resonances impaires
            double rp = _wp - _delay;
            while (rp < 0) rp += _line.Length;
            int ia = (int)rp; double frac = rp - ia;
            double dl = _line[ia & _mask] * (1.0 - frac) + _line[(ia + 1) & _mask] * frac;

            _loopLp += _damp * (dl - _loopLp);                               // pertes aux parois
            double ap = _apC * _loopLp + _apZ; _apZ = _loopLp - _apC * ap;   // dispersion => partiels etires

            double g = _fb * (1.0 - 0.55 * _tootMix);                        // le toot s'appuie moins sur le peigne
            double y = u - g * ap;
            if (double.IsNaN(y) || double.IsInfinity(y)) { y = 0; Array.Clear(_line, 0, _line.Length); _loopLp = _apZ = 0; }
            if (y > 8) y = 8; else if (y < -8) y = -8;
            _line[_wp] = (float)y;
            _wp = (_wp + 1) & _mask;

            if (_wallMix > 0.001) y += _wall.Process(y) * _wallMix;

            // --- rayonnement : l'extremite ouverte derive le signal (+6 dB/oct), d'ou le facteur de
            //     compensation — sans lui la sortie tombe 25 dB trop bas. Puis coupe-bas selon le micro.
            double rad = (y - 0.97 * _radPrev) * 16.0; _radPrev = y;
            _hpY = _hpC * (_hpY + rad - _hpX); _hpX = rad;

            double outv = _hpY * (0.55 + 0.9 * _tootMix + 0.5 * _obEnv);
            outv *= _env * (0.35 + 0.65 * _vel);

            // limiteur doux
            return (float)FastTanh(outv * 1.1);
        }
    }
}
