using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Windows.Controls;
using KotonStudio.Library;

namespace KotonPluginStringMachine
{
    /// <summary>
    /// String Machine — synthétiseur "string ensemble" type Solina/ARP String Ensemble. Contrairement
    /// au Bowed Strings (modélisation physique via EKS), c'est un synthé purement SOUSTRACTIF :
    /// oscillateurs saw + sub-octave, filtre LP doux, ADSR, et surtout un CHORUS 3-voix type BBD
    /// qui est le vrai secret du son Solina — sans lui, ça sonne juste "saw poly", avec lui ça sonne
    /// "wall of strings" années 70/80.
    ///
    /// **Paraphonique** (option) : un seul filtre pour toutes les voix, comme dans le Solina original.
    /// L'attaque du filtre est déclenchée par la 1re note et n'est pas ré-armée par les notes suivantes
    /// tant qu'une voix est active — ce comportement caractéristique est ce qui distingue un "string
    /// synth" d'un synthé poly classique. Off = mode poly standard (chaque voix a son enveloppe).
    ///
    /// **Signal path** (par voix) :
    /// <code>
    ///   saw1 ─┐
    ///         ├─ ADSR ─ LP (paraphonic) ─ chorus BBD 3-voix ─ vol/pan
    ///   saw2 ─┘  (sub-octave)
    /// </code>
    ///
    /// **Chorus BBD** : 3 délais courts (~5-25 ms) modulés par 3 LFO à ~0.5, 0.7, 1.1 Hz (rapports
    /// premiers = pas de synchronisation perceptible). Mix wet+dry = 50/50 typiquement — c'est ce qui
    /// crée la nappe "flottante" caractéristique. Implémentation simple : 3 buffers circulaires
    /// avec lecture à position modulée, lerp entre 2 échantillons voisins.
    /// </summary>
    [KotonInstrument("String Machine", Id = "koton.stringmachine", Category = "Ensemble", Version = "1.0", Vendor = "Koton Studio")]
    public sealed class StringMachinePlugin : IKotonInstrument
    {
        public string Id => "koton.stringmachine";
        public string DisplayName => "String Machine";

        // =============================================================================================
        // Paramètres exposés
        // =============================================================================================
        readonly KotonParameter _subLevel      = new KotonParameter("sub_level",       "Sub octave",       0.0, 1.0, 0.50);   // niveau du saw octave-bas
        readonly KotonParameter _cutoff        = new KotonParameter("cutoff",          "Cutoff",           100, 8000, 2500, "Hz");
        readonly KotonParameter _resonance     = new KotonParameter("resonance",       "Resonance",        0.5, 5.0, 0.7);
        readonly KotonParameter _paraphonic    = new KotonParameter("paraphonic",      "Paraphonic",       0, 1, 1);          // 1 = 1 filtre partagé, 0 = par voix
        readonly KotonParameter _attackTime    = new KotonParameter("attack_time",     "Attack",           0.0, 3.0, 0.35, "s");
        readonly KotonParameter _decayTime     = new KotonParameter("decay_time",      "Decay",            0.0, 3.0, 0.8, "s");
        readonly KotonParameter _sustainLvl    = new KotonParameter("sustain_level",   "Sustain",          0.0, 1.0, 0.80);
        readonly KotonParameter _releaseTime   = new KotonParameter("release_time",    "Release",          0.0, 4.0, 1.20, "s");

        // Chorus BBD
        readonly KotonParameter _chorusRate    = new KotonParameter("chorus_rate",     "Chorus rate",      0.1, 3.0, 0.6, "Hz");
        readonly KotonParameter _chorusDepth   = new KotonParameter("chorus_depth",    "Chorus depth",     0.0, 1.0, 0.70);
        readonly KotonParameter _chorusMix     = new KotonParameter("chorus_mix",      "Chorus mix",       0.0, 1.0, 0.55);

        readonly KotonParameter _stereoWidth   = new KotonParameter("stereo_width",    "Stereo width",     0.0, 1.0, 0.70);
        readonly KotonParameter _volumeDb      = new KotonParameter("volume",          "Volume",           -30.0, 6.0, -6.0, "dB");

        readonly List<KotonParameter> _params;
        public IReadOnlyList<KotonParameter> Parameters => _params;

        int _sampleRate;
        int _maxBlockSize;
        StringVoice[] _voices;
        int _stealCursor;
        const int Polyphony = 12;

        // Filtre LP paraphonique (partagé toutes voix)
        BiquadState _paraFilterL, _paraFilterR;

        // Chorus BBD 3-voix
        ChorusBbd _chorus;

        float _bendMul = 1f;

        public StringMachinePlugin()
        {
            _params = new List<KotonParameter>
            {
                _subLevel, _cutoff, _resonance, _paraphonic,
                _attackTime, _decayTime, _sustainLvl, _releaseTime,
                _chorusRate, _chorusDepth, _chorusMix,
                _stereoWidth, _volumeDb,
            };
        }

        public bool HasEditor => true;
        public UserControl CreateEditor() => new StringMachineEditor(this);

        // =============================================================================================
        // Cycle de vie
        // =============================================================================================
        public void Prepare(int sampleRate, int maxBlockSize)
        {
            _sampleRate = sampleRate;
            _maxBlockSize = maxBlockSize;
            _voices = new StringVoice[Polyphony];
            for (int i = 0; i < Polyphony; i++) _voices[i] = new StringVoice(sampleRate);
            SetBiquadLp(ref _paraFilterL, sampleRate, (float)_cutoff.Value, (float)_resonance.Value);
            SetBiquadLp(ref _paraFilterR, sampleRate, (float)_cutoff.Value, (float)_resonance.Value);
            _chorus = new ChorusBbd(sampleRate);
        }

        public void Reset()
        {
            if (_voices != null) foreach (var v in _voices) v.Kill();
            _paraFilterL.ResetState();
            _paraFilterR.ResetState();
            _chorus?.Reset();
            _bendMul = 1f;
        }

        // =============================================================================================
        // MIDI
        // =============================================================================================
        public void NoteOn(int note, int velocity, int sampleOffset = 0)
        {
            if (_voices == null || velocity == 0) return;
            var p = SnapshotParams();
            float vel = velocity / 127f;

            // Voix libre ? Sinon steal.
            StringVoice target = null;
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

        public void SetPitchBend(float value, int sampleOffset = 0)
        {
            _bendMul = (float)Math.Pow(2.0, value * 2.0 / 12.0);
        }

        // =============================================================================================
        // Render
        // =============================================================================================
        public void Render(Span<float> left, Span<float> right)
        {
            if (_voices == null) { left.Clear(); right.Clear(); return; }

            var p = SnapshotParams();
            bool paraphonic = p.Paraphonic_p >= 0.5;
            float cutoff = (float)p.Cutoff_p;
            float reso = (float)p.Resonance_p;
            float subLevel = (float)p.SubLevel_p;
            float volLin = (float)Math.Pow(10.0, p.VolumeDb_p / 20.0);
            float width = (float)p.StereoWidth_p;

            // Filtre paraphonique : mis à jour une fois par buffer (assez à cette fréquence de rafraîchissement)
            if (paraphonic)
            {
                SetBiquadLp(ref _paraFilterL, _sampleRate, cutoff, reso);
                SetBiquadLp(ref _paraFilterR, _sampleRate, cutoff, reso);
            }

            _chorus.UpdateParams((float)p.ChorusRate_p, (float)p.ChorusDepth_p);
            float chorusMix = (float)p.ChorusMix_p;

            int n = left.Length;
            for (int i = 0; i < n; i++)
            {
                float sum = 0f;
                for (int v = 0; v < _voices.Length; v++)
                {
                    if (_voices[v].IsActive)
                        sum += _voices[v].RenderSample(p, subLevel, _bendMul, paraphonic, cutoff, reso);
                }

                float dryL, dryR;
                if (paraphonic)
                {
                    // Filtre paraphonique en post-somme
                    float filtered = BiquadProcess(ref _paraFilterL, sum);
                    dryL = dryR = filtered;
                }
                else
                {
                    dryL = dryR = sum;
                }

                // Chorus BBD
                _chorus.Process(dryL, dryR, out float wetL, out float wetR);
                float outL = dryL * (1f - chorusMix) + wetL * chorusMix;
                float outR = dryR * (1f - chorusMix) + wetR * chorusMix;

                // Width : mid-side
                float mid = 0.5f * (outL + outR);
                float side = outL - outR;
                float fL = (mid + side * width) * volLin;
                float fR = (mid - side * width) * volLin;

                left[i] = fL;
                right[i] = fR;
            }
        }

        internal struct StringParams
        {
            public double SubLevel_p, Cutoff_p, Resonance_p, Paraphonic_p;
            public double AttackTime_p, DecayTime_p, SustainLvl_p, ReleaseTime_p;
            public double ChorusRate_p, ChorusDepth_p, ChorusMix_p;
            public double StereoWidth_p, VolumeDb_p;
        }

        internal StringParams SnapshotParams() => new StringParams
        {
            SubLevel_p     = _subLevel.Value,
            Cutoff_p       = _cutoff.Value,
            Resonance_p    = _resonance.Value,
            Paraphonic_p   = _paraphonic.Value,
            AttackTime_p   = _attackTime.Value,
            DecayTime_p    = _decayTime.Value,
            SustainLvl_p   = _sustainLvl.Value,
            ReleaseTime_p  = _releaseTime.Value,
            ChorusRate_p   = _chorusRate.Value,
            ChorusDepth_p  = _chorusDepth.Value,
            ChorusMix_p    = _chorusMix.Value,
            StereoWidth_p  = _stereoWidth.Value,
            VolumeDb_p     = _volumeDb.Value,
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

        // =============================================================================================
        // Presets
        // =============================================================================================
        public static readonly string[] PresetNames =
        {
            "Solina Strings", "Slow Strings", "Brass Ensemble", "Choir Aah", "Human Voice", "Dark Pad",
        };

        static readonly double[,] PresetValues =
        {
            //           sub  cutoff reso para atk  dec  sus  rel  chR  chD  chM  wid  vol
            /*Solina*/  { 0.5, 2500,  0.7, 1,   0.35, 0.8, 0.85, 1.2, 0.6, 0.7, 0.55, 0.7, -6.0 },
            /*Slow*/    { 0.6, 1800,  0.6, 1,   1.20, 1.5, 0.90, 2.0, 0.5, 0.8, 0.60, 0.8, -6.0 },
            /*Brass*/   { 0.3, 3800,  1.2, 0,   0.10, 0.6, 0.75, 0.5, 0.4, 0.4, 0.30, 0.4, -5.0 },
            /*Choir*/   { 0.4, 2200,  0.5, 1,   0.80, 1.0, 0.85, 1.5, 0.7, 0.8, 0.65, 0.85, -6.0 },
            /*Voice*/   { 0.2, 1600,  0.5, 1,   0.60, 0.9, 0.80, 1.2, 0.5, 0.7, 0.60, 0.75, -6.0 },
            /*DarkPad*/ { 0.7, 1200,  0.6, 1,   1.80, 2.0, 0.95, 3.0, 0.3, 0.9, 0.70, 0.90, -6.0 },
        };

        public void LoadPreset(int index)
        {
            if (index < 0 || index >= PresetValues.GetLength(0)) return;
            _subLevel.Value     = PresetValues[index, 0];
            _cutoff.Value       = PresetValues[index, 1];
            _resonance.Value    = PresetValues[index, 2];
            _paraphonic.Value   = PresetValues[index, 3];
            _attackTime.Value   = PresetValues[index, 4];
            _decayTime.Value    = PresetValues[index, 5];
            _sustainLvl.Value   = PresetValues[index, 6];
            _releaseTime.Value  = PresetValues[index, 7];
            _chorusRate.Value   = PresetValues[index, 8];
            _chorusDepth.Value  = PresetValues[index, 9];
            _chorusMix.Value    = PresetValues[index, 10];
            _stereoWidth.Value  = PresetValues[index, 11];
            _volumeDb.Value     = PresetValues[index, 12];
        }

        public void SetParam(string id, double value)
        {
            foreach (var kp in _params)
                if (kp.Id == id) { kp.Value = value; return; }
        }

        // =============================================================================================
        // Biquad LP RBJ cookbook
        // =============================================================================================
        internal struct BiquadState
        {
            public float b0, b1, b2, a1, a2;
            public float x1, x2, y1, y2;
            public void ResetState() { x1 = x2 = y1 = y2 = 0f; }
        }

        internal static void SetBiquadLp(ref BiquadState s, int sr, float freq, float q)
        {
            if (freq < 20f) freq = 20f;
            if (freq > sr * 0.45f) freq = sr * 0.45f;
            double w0 = 2.0 * Math.PI * freq / sr;
            double alpha = Math.Sin(w0) / (2.0 * q);
            double cosw0 = Math.Cos(w0);

            double b0 = (1.0 - cosw0) / 2.0;
            double b1 = 1.0 - cosw0;
            double b2 = (1.0 - cosw0) / 2.0;
            double a0 = 1.0 + alpha;
            double a1 = -2.0 * cosw0;
            double a2 = 1.0 - alpha;

            s.b0 = (float)(b0 / a0);
            s.b1 = (float)(b1 / a0);
            s.b2 = (float)(b2 / a0);
            s.a1 = (float)(a1 / a0);
            s.a2 = (float)(a2 / a0);
        }

        internal static float BiquadProcess(ref BiquadState s, float x)
        {
            float y = s.b0 * x + s.b1 * s.x1 + s.b2 * s.x2 - s.a1 * s.y1 - s.a2 * s.y2;
            s.x2 = s.x1; s.x1 = x;
            s.y2 = s.y1; s.y1 = y;
            return y;
        }
    }

    // =================================================================================================
    // Voix : saw + saw sub-octave + ADSR + filtre optionnel par voix
    // =================================================================================================
    internal sealed class StringVoice
    {
        readonly int _sr;
        bool _active;
        int _note;
        float _vel;

        double _phase, _subPhase;
        double _freq;

        enum EnvStage { Idle, Attack, Decay, Sustain, Release }
        EnvStage _stage;
        float _env, _envRate;
        float _sustainLevel;
        float _decayRate;
        float _releaseRate;

        StringMachinePlugin.BiquadState _voiceFilter;

        public bool IsActive => _active;
        public int Note => _note;

        public StringVoice(int sr) { _sr = sr; }

        public void NoteOn(int note, float vel, in StringMachinePlugin.StringParams p)
        {
            _note = note;
            _vel = vel;
            _freq = 440.0 * Math.Pow(2.0, (note - 69) / 12.0);
            _phase = 0;
            _subPhase = 0;
            _stage = EnvStage.Attack;
            _env = 0f;

            float attackSamples = Math.Max(1f, (float)p.AttackTime_p * _sr);
            _envRate = 1f / attackSamples;
            _sustainLevel = (float)p.SustainLvl_p;
            _decayRate = 1f / Math.Max(1f, (float)p.DecayTime_p * _sr);
            _releaseRate = 1f / Math.Max(1f, (float)p.ReleaseTime_p * _sr);

            _voiceFilter.ResetState();
            _active = true;
        }

        public void NoteOff()
        {
            if (_active && _stage != EnvStage.Release) _stage = EnvStage.Release;
        }

        public void Kill()
        {
            _active = false;
            _stage = EnvStage.Idle;
            _env = 0f;
        }

        /// <summary>Rend un sample mono. Le filtrage paraphonique est fait par le plugin (post-somme
        /// des voix) ; en mode poly on filtre par voix ici.</summary>
        public float RenderSample(in StringMachinePlugin.StringParams p, float subLevel, float bendMul, bool paraphonic, float cutoff, float reso)
        {
            switch (_stage)
            {
                case EnvStage.Attack:
                    _env += _envRate;
                    if (_env >= 1f) { _env = 1f; _stage = EnvStage.Decay; }
                    break;
                case EnvStage.Decay:
                    _env -= _decayRate * (1f - _sustainLevel);
                    if (_env <= _sustainLevel) { _env = _sustainLevel; _stage = EnvStage.Sustain; }
                    break;
                case EnvStage.Release:
                    _env -= _releaseRate * _env;   // release exponentiel (plus musical qu'un linéaire)
                    if (_env < 1e-4f) { Kill(); return 0f; }
                    break;
            }

            double effFreq = _freq * bendMul;
            double inc = effFreq / _sr;
            _phase += inc; if (_phase >= 1.0) _phase -= 1.0;
            _subPhase += inc * 0.5; if (_subPhase >= 1.0) _subPhase -= 1.0;

            // Saw simple (naïf, aliasing accepté — l'esprit "vintage" Solina). Un anti-aliasing
            // PolyBLEP serait plus propre mais l'aliasing fait partie du son années 70.
            float saw = (float)(_phase * 2.0 - 1.0);
            float sub = (float)(_subPhase * 2.0 - 1.0);
            float mixed = saw + sub * subLevel;

            float out_ = mixed * _env * _vel;

            if (!paraphonic)
            {
                // Filtre par voix — mis à jour à chaque sample serait cher, on l'update juste au NoteOn
                // pour cette v1 (cutoff/reso live modifiés = audibles au prochain NoteOn seulement).
                // TODO v2 : cutoff/reso par voix updatable en temps réel.
                StringMachinePlugin.SetBiquadLp(ref _voiceFilter, _sr, cutoff, reso);
                out_ = StringMachinePlugin.BiquadProcess(ref _voiceFilter, out_);
            }

            return out_;
        }
    }

    // =================================================================================================
    // Chorus BBD 3-voix : 3 délais courts modulés + lecture fractionnaire (lerp)
    // =================================================================================================
    internal sealed class ChorusBbd
    {
        readonly int _sr;
        readonly float[] _bufL, _bufR;
        int _writeIdx;

        float _phase1, _phase2, _phase3;
        float _incRate;   // radian/sample pour la fréquence de base
        float _depth;     // amplitude de modulation (0..1 → 0..8ms de spread)

        const int BufSize = 4096;   // ~85 ms à 48k, largement suffisant pour un chorus BBD

        public ChorusBbd(int sampleRate)
        {
            _sr = sampleRate;
            _bufL = new float[BufSize];
            _bufR = new float[BufSize];
        }

        public void Reset()
        {
            Array.Clear(_bufL, 0, _bufL.Length);
            Array.Clear(_bufR, 0, _bufR.Length);
            _writeIdx = 0;
            _phase1 = 0f; _phase2 = 1.0f; _phase3 = 2.5f;   // décalages initiaux pour éviter la synchro
        }

        public void UpdateParams(float rateHz, float depth)
        {
            _incRate = (float)(2 * Math.PI * rateHz / _sr);
            _depth = depth;
        }

        public void Process(float inL, float inR, out float outL, out float outR)
        {
            // Écrit l'entrée dans les buffers
            _bufL[_writeIdx] = inL;
            _bufR[_writeIdx] = inR;

            // 3 LFO à des rapports non-entiers de la fréquence de base (0.5x, 1.0x, 1.7x) — évite la
            // synchro perceptible, donne l'illusion d'un chorus riche.
            _phase1 += _incRate * 0.5f;
            _phase2 += _incRate * 1.0f;
            _phase3 += _incRate * 1.7f;
            if (_phase1 > 2 * Math.PI) _phase1 -= (float)(2 * Math.PI);
            if (_phase2 > 2 * Math.PI) _phase2 -= (float)(2 * Math.PI);
            if (_phase3 > 2 * Math.PI) _phase3 -= (float)(2 * Math.PI);

            // Chaque délai : base ~10ms + LFO * depth * 8ms → range 2..18ms typique
            float baseSamples = _sr * 0.010f;
            float depthSamples = _sr * 0.008f * _depth;

            float d1 = baseSamples + (float)Math.Sin(_phase1) * depthSamples;
            float d2 = baseSamples + (float)Math.Sin(_phase2) * depthSamples;
            float d3 = baseSamples + (float)Math.Sin(_phase3) * depthSamples;

            // Lecture fractionnaire pour éviter les artefacts de délai entier
            float t1L = ReadFrac(_bufL, _writeIdx - d1);
            float t2L = ReadFrac(_bufL, _writeIdx - d2);
            float t3L = ReadFrac(_bufL, _writeIdx - d3);
            float t1R = ReadFrac(_bufR, _writeIdx - d1);
            float t2R = ReadFrac(_bufR, _writeIdx - d2);
            float t3R = ReadFrac(_bufR, _writeIdx - d3);

            // Mix des 3 voix : L = 1+2, R = 2+3 (croisement stéréo naturel)
            outL = (t1L + t2L) * 0.5f;
            outR = (t2R + t3R) * 0.5f;

            _writeIdx++;
            if (_writeIdx >= BufSize) _writeIdx = 0;
        }

        static float ReadFrac(float[] buf, float pos)
        {
            while (pos < 0) pos += BufSize;
            while (pos >= BufSize) pos -= BufSize;
            int i0 = (int)pos;
            int i1 = i0 + 1;
            if (i1 >= BufSize) i1 = 0;
            float frac = pos - i0;
            return buf[i0] * (1f - frac) + buf[i1] * frac;
        }
    }
}
