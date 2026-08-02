using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Windows.Controls;
using KotonStudio.Library;

namespace KotonPluginWaterdrops
{
    /// <summary>
    /// Waterdrops — instrument "gouttes qui tombent". Chaque note produit une goutte : chirp initial
    /// bref (l'impact/plop) + oscillateur sinusoïdal qui GLIDE d'une hauteur au-dessus vers la
    /// fondamentale MIDI en ~50 ms, puis résonance amortie.
    ///
    /// **Fondement physique** : dans la nature, une goutte d'eau qui touche une surface produit un
    /// son dont la fréquence AUGMENTE puis se stabilise — c'est la vibration d'une bulle d'air
    /// piégée sous l'eau qui rétrécit (Phillips 1959, Bristol Univ. 2018 pour la démonstration
    /// vidéo virale). Notre synthèse inverse la direction (glide descendant) pour un son plus
    /// musical, mais la structure temporelle (chirp + résonance courte) reste fidèle.
    ///
    /// **Reverb intégrée** : petit espace type "évier / grotte / bassin" — sans elle, une goutte
    /// sèche sonne synthétique. Avec, elle sonne comme dans un environnement réel. Simple all-pass
    /// diffuseur + feedback delay court, mixé selon Wet et Space.
    ///
    /// **Randomness** : détune ±20 cents et jitter de pan par note. Chaque goutte devient unique
    /// (comme dans la nature — jamais deux gouttes ne sonnent identiquement). Un tap répété sur
    /// la même touche produira des variations subtiles au lieu d'un preset copie-collé.
    ///
    /// **Usage typique** : nappes ambient très aériennes, ponctuations ASMR, textures de méditation,
    /// arpèges arrangés en gamme pentatonique = pluie mélodique. Sonne exceptionnellement bien
    /// combiné avec le plugin Forest Ambience en insert.
    /// </summary>
    [KotonInstrument("Waterdrops", Id = "koton.waterdrops", Category = "Synth", Version = "1.0", Vendor = "Koton Studio")]
    public sealed class WaterdropsPlugin : IKotonInstrument
    {
        public string Id => "koton.waterdrops";
        public string DisplayName => "Waterdrops";

        readonly KotonParameter _dropSize     = new KotonParameter("drop_size",    "Drop size",    0.0, 1.0, 0.35);
        readonly KotonParameter _splash       = new KotonParameter("splash",       "Splash",       0.0, 1.0, 0.60);
        readonly KotonParameter _resonance    = new KotonParameter("resonance",    "Resonance",    0.0, 1.0, 0.40);
        readonly KotonParameter _brightness   = new KotonParameter("brightness",   "Brightness",   0.0, 1.0, 0.55);
        readonly KotonParameter _randomness   = new KotonParameter("randomness",   "Randomness",   0.0, 1.0, 0.35);
        readonly KotonParameter _wet          = new KotonParameter("wet",          "Wet",          0.0, 1.0, 0.50);
        readonly KotonParameter _space        = new KotonParameter("space",        "Space",        0.0, 1.0, 0.50);
        readonly KotonParameter _stereoWidth  = new KotonParameter("stereo_width", "Stereo width", 0.0, 1.0, 0.55);
        readonly KotonParameter _volumeDb     = new KotonParameter("volume",       "Volume",       -30.0, 6.0, -3.0, "dB");

        readonly List<KotonParameter> _params;
        public IReadOnlyList<KotonParameter> Parameters => _params;

        int _sampleRate;
        int _maxBlockSize;
        DropVoice[] _voices;
        int _stealCursor;
        const int Polyphony = 24;   // beaucoup de voix car gouttes sont courtes et se chevauchent

        // Reverb intégrée : 4 all-pass diffusion + délai court avec feedback
        AllPassStage[] _reverbDiffusion;
        float[] _reverbBuf;
        int _reverbIdx;
        int _reverbSize;

        public WaterdropsPlugin()
        {
            _params = new List<KotonParameter>
            {
                _dropSize, _splash, _resonance, _brightness, _randomness,
                _wet, _space, _stereoWidth, _volumeDb,
            };
        }

        public bool HasEditor => true;
        public UserControl CreateEditor() => new WaterdropsEditor(this);

        // =============================================================================================
        // Cycle
        // =============================================================================================
        public void Prepare(int sampleRate, int maxBlockSize)
        {
            _sampleRate = sampleRate;
            _maxBlockSize = maxBlockSize;
            _voices = new DropVoice[Polyphony];
            for (int i = 0; i < Polyphony; i++) _voices[i] = new DropVoice(sampleRate);

            // Reverb intégrée
            var apLenMs = new float[] { 5.3f, 8.7f, 13.1f, 21.7f };
            _reverbDiffusion = new AllPassStage[apLenMs.Length];
            for (int i = 0; i < apLenMs.Length; i++)
                _reverbDiffusion[i] = new AllPassStage((int)(apLenMs[i] * sampleRate / 1000f), 0.6f);
            // Buffer principal reverb : max 500 ms
            _reverbSize = sampleRate / 2;
            _reverbBuf = new float[_reverbSize];
        }

        public void Reset()
        {
            if (_voices != null) foreach (var v in _voices) v.Kill();
            if (_reverbDiffusion != null) foreach (var ap in _reverbDiffusion) ap.Reset();
            if (_reverbBuf != null) Array.Clear(_reverbBuf, 0, _reverbBuf.Length);
            _reverbIdx = 0;
        }

        // =============================================================================================
        // MIDI
        // =============================================================================================
        public void NoteOn(int note, int velocity, int sampleOffset = 0)
        {
            if (_voices == null || velocity == 0) return;
            var p = ToVoiceParams();
            float vel = velocity / 127f;

            DropVoice target = null;
            for (int i = 0; i < _voices.Length; i++) if (!_voices[i].IsActive) { target = _voices[i]; break; }
            if (target == null)
            {
                target = _voices[_stealCursor];
                _stealCursor = (_stealCursor + 1) % _voices.Length;
                target.Kill();
            }

            // Pan par note : basses gauche, aigus droite, selon StereoWidth
            float noteNorm = (note - 60) / 24f;
            if (noteNorm < -1f) noteNorm = -1f; else if (noteNorm > 1f) noteNorm = 1f;
            float pan = noteNorm * (float)_stereoWidth.Value;
            target.NoteOn(note, vel, p, pan);
        }

        public void NoteOff(int note, int sampleOffset = 0)
        {
            // Une goutte ne s'interrompt pas — elle décroît naturellement (comme le mallets). No-op.
        }

        public void MidiCC(int cc, int value, int sampleOffset = 0)
        {
            if (cc == 123) Reset();
        }

        public void SetPitchBend(float value, int sampleOffset = 0) { }

        // =============================================================================================
        // Render
        // =============================================================================================
        public void Render(Span<float> left, Span<float> right)
        {
            if (_voices == null) { left.Clear(); right.Clear(); return; }

            float volLin = (float)Math.Pow(10.0, _volumeDb.Value / 20.0);
            float wet = (float)_wet.Value;
            float space = (float)_space.Value;
            // Feedback reverb piloté par Space : 0..1 → 0.3..0.85 (queue courte à moyennement longue)
            float rvFeedback = 0.3f + space * 0.55f;
            // Longueur du délai reverb : plus grand avec Space
            int rvDelay = (int)(_sampleRate * (0.03 + space * 0.15));   // 30..180 ms
            if (rvDelay >= _reverbSize) rvDelay = _reverbSize - 1;

            int n = left.Length;
            for (int i = 0; i < n; i++)
            {
                float sumL = 0f, sumR = 0f;
                for (int v = 0; v < _voices.Length; v++)
                {
                    var voice = _voices[v];
                    if (!voice.IsActive) continue;
                    float s = voice.RenderSample();
                    sumL += s * voice.PanL;
                    sumR += s * voice.PanR;
                }

                // Reverb intégrée : sum mono → diffusion → délai feedback → wet
                float mono = (sumL + sumR) * 0.5f;
                float diffL = mono;
                float diffR = mono;
                for (int a = 0; a < _reverbDiffusion.Length; a++)
                {
                    diffL = _reverbDiffusion[a].ProcessL(diffL);
                    diffR = _reverbDiffusion[a].ProcessR(diffR);
                }
                int readIdx = _reverbIdx - rvDelay;
                if (readIdx < 0) readIdx += _reverbSize;
                float rvOut = _reverbBuf[readIdx];
                float toWrite = (diffL + diffR) * 0.5f + rvOut * rvFeedback;
                _reverbBuf[_reverbIdx] = toWrite;
                _reverbIdx++;
                if (_reverbIdx >= _reverbSize) _reverbIdx = 0;

                // Mix dry (voix directes) + wet (reverb intégrée)
                float outL = sumL * (1f - wet * 0.5f) + rvOut * wet;
                float outR = sumR * (1f - wet * 0.5f) + rvOut * wet;

                left[i] = outL * volLin;
                right[i] = outR * volLin;
            }
        }

        DropParams ToVoiceParams() => new DropParams
        {
            DropSize    = (float)_dropSize.Value,
            Splash      = (float)_splash.Value,
            Resonance   = (float)_resonance.Value,
            Brightness  = (float)_brightness.Value,
            Randomness  = (float)_randomness.Value,
            Wet         = (float)_wet.Value,
            Space       = (float)_space.Value,
            StereoWidth = (float)_stereoWidth.Value,
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
            "Goutte fine", "Goutte lourde", "Cascade", "Grotte humide", "Averse dense", "Cristal (grande queue)",
        };

        static readonly double[,] PresetValues =
        {
            //          drop split reso brgh rand wet  space width vol
            /*Fine*/    { 0.25, 0.50, 0.30, 0.70, 0.30, 0.40, 0.35, 0.55, -3.0 },
            /*Lourde*/  { 0.55, 0.75, 0.45, 0.35, 0.40, 0.55, 0.60, 0.45, -3.0 },
            /*Cascade*/ { 0.45, 0.65, 0.75, 0.55, 0.60, 0.65, 0.70, 0.70, -4.0 },
            /*Grotte*/  { 0.40, 0.45, 0.55, 0.40, 0.35, 0.85, 0.85, 0.65, -3.0 },
            /*Averse*/  { 0.30, 0.80, 0.25, 0.50, 0.75, 0.55, 0.45, 0.85, -2.0 },
            /*Cristal*/ { 0.20, 0.35, 0.90, 0.75, 0.20, 0.50, 0.75, 0.50, -6.0 },
        };

        public void LoadPreset(int index)
        {
            if (index < 0 || index >= PresetValues.GetLength(0)) return;
            _dropSize.Value    = PresetValues[index, 0];
            _splash.Value      = PresetValues[index, 1];
            _resonance.Value   = PresetValues[index, 2];
            _brightness.Value  = PresetValues[index, 3];
            _randomness.Value  = PresetValues[index, 4];
            _wet.Value         = PresetValues[index, 5];
            _space.Value       = PresetValues[index, 6];
            _stereoWidth.Value = PresetValues[index, 7];
            _volumeDb.Value    = PresetValues[index, 8];
        }

        public void SetParam(string id, double value)
        {
            foreach (var kp in _params)
                if (kp.Id == id) { kp.Value = value; return; }
        }
    }

    internal sealed class AllPassStage
    {
        readonly float[] _bufL, _bufR;
        int _idxL, _idxR;
        readonly int _size;
        readonly float _coef;

        public AllPassStage(int size, float coef)
        {
            _size = Math.Max(4, size);
            _coef = coef;
            _bufL = new float[_size];
            _bufR = new float[_size];
        }

        public void Reset()
        {
            Array.Clear(_bufL, 0, _bufL.Length);
            Array.Clear(_bufR, 0, _bufR.Length);
            _idxL = _idxR = 0;
        }

        public float ProcessL(float x)
        {
            float d = _bufL[_idxL];
            float y = -_coef * x + d;
            _bufL[_idxL] = x + _coef * y;
            _idxL++;
            if (_idxL >= _size) _idxL = 0;
            return y;
        }

        public float ProcessR(float x)
        {
            float d = _bufR[_idxR];
            float y = -_coef * x + d;
            _bufR[_idxR] = x + _coef * y;
            _idxR++;
            if (_idxR >= _size) _idxR = 0;
            return y;
        }
    }
}
