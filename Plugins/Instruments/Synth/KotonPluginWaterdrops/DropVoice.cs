using System;

namespace KotonPluginWaterdrops
{
    internal struct DropParams
    {
        public float DropSize;       // 0..1 → ampleur du glide (semitones descendus au demarrage)
        public float Splash;         // 0..1 → intensité du chirp aigu initial (plop)
        public float Resonance;      // 0..1 → durée de la queue sinusoïdale (0=court, 1=très long)
        public float Brightness;     // 0..1 → couleur (fréquence du chirp, richesse harmonique)
        public float Randomness;     // 0..1 → détune aléatoire ±20 cents par note (variations naturelles)
        public float Wet;            // 0..1 → mix de la reverb intégrée
        public float Space;          // 0..1 → longueur de la reverb intégrée (grotte)
        public float StereoWidth;
    }

    /// <summary>
    /// Une "goutte" — l'anatomie sonore d'une vraie goutte d'eau qui tombe :
    /// 1. **Chirp initial très bref** (~5-10 ms) — l'impact / plop / choc mécanique
    /// 2. **Glide sinusoïdal descendant** (~30-80 ms) — le "plink" caractéristique, cause : bulle
    ///    d'air piégée sous l'eau qui vibre à une fréquence qui augmente puis se stabilise (Physics
    ///    of Fluids, Phillips 1959). Notre modèle inverse pour finir sur la fondamentale MIDI.
    /// 3. **Résonance sinusoïdale amortie** — la note se stabilise et décroît.
    ///
    /// Approximation : oscillateur sinus principal dont la fréquence GLIDE de (freq × factorHaut)
    /// vers freq en ~50 ms via un lissage exponentiel, mixé avec un oscillateur haut fréquence
    /// (~8×freq) qui décroît vite (chirp/plop).
    ///
    /// **Randomness** : détune ±20 cents et jitter d'amplitude par voix — donne l'impression que
    /// chaque goutte est unique (comme dans la vraie vie).
    /// </summary>
    internal sealed class DropVoice
    {
        readonly int _sr;
        bool _active;
        int _note;
        float _velocity;

        // Oscillateur principal (glide vers la fréquence cible)
        double _mainPhase;
        double _freqCurrent, _freqTarget;
        float _glideSmoothing;   // coef par sample (0 = pas de glide, 1 = instant)
        float _mainAmp, _mainDecayPerSample;

        // Chirp initial (aigu, décroit très vite)
        double _chirpPhase;
        double _chirpFreq;
        float _chirpAmp, _chirpDecayPerSample;

        // Pan par voix (défini au NoteOn selon la note)
        float _panL, _panR;

        // Random dédié pour randomness
        Random _rng;

        const float SilenceThreshold = 5e-5f;

        public bool IsActive => _active;
        public int Note => _note;
        public float PanL => _panL;
        public float PanR => _panR;

        public DropVoice(int sampleRate)
        {
            _sr = sampleRate;
        }

        public void NoteOn(int note, float velocity, in DropParams p, float pan)
        {
            _note = note;
            _velocity = velocity;
            _rng = new Random(note * 7919 + Environment.TickCount);

            double freq = 440.0 * Math.Pow(2.0, (note - 69) / 12.0);

            // Randomness : détune ±20 cents × Randomness
            if (p.Randomness > 0)
            {
                double detune = (_rng.NextDouble() * 2 - 1) * p.Randomness * 20;
                freq *= Math.Pow(2.0, detune / 1200.0);
            }

            _freqTarget = freq;
            // Glide MONTANT : la bulle d'air piegee sous l'eau retrecit → fréquence augmente au
            // fil du temps (Phillips 1959, physique reelle des gouttes). Start LOW, MONTE vers freq.
            // 2026-08-02 : version precedente avait glide DESCENDANT — musical mais incorrect
            // physiquement. Le user a demande de suivre la vraie physique.
            double glideSemi = p.DropSize * 12.0;
            _freqCurrent = freq / Math.Pow(2.0, glideSemi / 12.0);
            // Constante de temps du glide : plus grande = glide plus long
            // 0..1 → 20..120 ms
            double glideMs = 20 + p.DropSize * 100;
            double glideSamples = glideMs * _sr / 1000.0;
            _glideSmoothing = (float)(1.0 - Math.Exp(-1.0 / glideSamples));

            _mainPhase = 0;
            _mainAmp = velocity * 0.7f;
            // Résonance = durée de décay du sinus principal : 0..1 → 100..3000 ms
            double resMs = 100 + p.Resonance * 2900;
            _mainDecayPerSample = (float)Math.Exp(-6.9078 / (resMs * _sr / 1000.0));   // -60dB en resMs

            // Chirp : fréquence 4x..12x la fondamentale selon Brightness
            _chirpFreq = freq * (4.0 + p.Brightness * 8.0);
            if (_chirpFreq > _sr * 0.45) _chirpFreq = _sr * 0.45;   // anti-alias
            _chirpPhase = 0;
            _chirpAmp = velocity * p.Splash * 0.8f;
            // Chirp très court : décay en 5..15 ms
            double chirpMs = 5 + p.Brightness * 10;
            _chirpDecayPerSample = (float)Math.Exp(-6.9078 / (chirpMs * _sr / 1000.0));

            // Pan : mix entre le pan fourni (par le plugin) et un jitter aléatoire
            float panJitter = (float)((_rng.NextDouble() * 2 - 1) * p.Randomness * 0.3);
            float finalPan = Math.Max(-1f, Math.Min(1f, pan + panJitter));
            float p01 = 0.5f * (1f + finalPan);
            _panL = 1f - p01;
            _panR = p01;

            _active = true;
        }

        public void Kill()
        {
            _active = false;
            _mainAmp = 0f;
            _chirpAmp = 0f;
        }

        public float RenderSample()
        {
            if (!_active) return 0f;

            // Glide de la fréquence principale
            _freqCurrent += (_freqTarget - _freqCurrent) * _glideSmoothing;
            double inc = _freqCurrent / _sr * 2.0 * Math.PI;
            _mainPhase += inc;
            if (_mainPhase > 2 * Math.PI) _mainPhase -= 2 * Math.PI;
            float main = (float)Math.Sin(_mainPhase) * _mainAmp;
            _mainAmp *= _mainDecayPerSample;

            // Chirp initial
            _chirpPhase += _chirpFreq / _sr * 2.0 * Math.PI;
            if (_chirpPhase > 2 * Math.PI) _chirpPhase -= 2 * Math.PI;
            float chirp = (float)Math.Sin(_chirpPhase) * _chirpAmp;
            _chirpAmp *= _chirpDecayPerSample;

            float sum = main + chirp;

            // Libération quand la voix est silencieuse
            if (_mainAmp + _chirpAmp < SilenceThreshold)
            {
                _active = false;
                return 0f;
            }

            return sum;
        }
    }
}
