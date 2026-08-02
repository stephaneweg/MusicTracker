using System;

namespace KotonPluginBrass
{
    /// <summary>Snapshot des paramètres brass, figés par buffer.</summary>
    internal struct BrassParams
    {
        public float BreathPressure;    // 0..1 → force de l'insufflation (excitation)
        public float BreathNoise;       // 0..1 → part de bruit dans l'excitation (souffle audible)
        public float LipTension;        // 0..1 → non-linéarité des lèvres (doux → agressif/screaming)
        public float Damping;           // 0..1 → LP feedback dans le tube (color de la queue)
        public float Brightness;        // 0..1 → gain global sur les hautes (compensation LP)
        public float BellSize;          // 0..1 → filtre passe-bas en sortie (pavillon petit vs gros)
        public float VibratoRateHz;
        public float VibratoDepthCents;
        public float AttackSec;
        public float ReleaseSec;
        public float VolumeDb;
    }

    /// <summary>
    /// Une voix de cuivre — modèle physique par guide d'onde simplifié. Un tube unidimensionnel
    /// (ligne à retard) avec une non-linéarité "lèvres" à l'embouchure : la pression du souffle
    /// module l'ouverture des lèvres qui réinjecte dans le tube, créant une auto-oscillation à
    /// la fréquence de résonance du tube.
    ///
    /// **Implémentation** :
    /// - Ligne à retard de longueur N = SR/f (une période fondamentale)
    /// - Feedback avec LP (mort progressive des aigus, damping)
    /// - Non-linéarité tanh sur la boucle (les lèvres compressent le signal) modulée par
    ///   BreathPressure × envelope
    /// - Injection continue : bruit filtré (souffle) + composante DC (pression stable)
    /// - LipTension amplifie la non-linéarité (dur = attack agressive, cuivré / doux = mellow)
    /// - Bell size = LP en sortie (petit pavillon = mordant, gros = arrondi)
    ///
    /// **Différence avec un vrai modèle de cuivre** (Cook 2002, Adachi-Sato 1996) : ici on utilise
    /// une non-linéarité algorithmique simple (tanh) au lieu de résoudre l'équation de Bernoulli
    /// sur les lèvres. Ça marche moins bien pour les transitoires d'attaque (le "brûle" du démarrage
    /// d'un cuivre naturel) mais reste très musical et facile à contrôler.
    /// </summary>
    internal sealed class BrassVoice
    {
        readonly int _sr;
        readonly float[] _tube;
        int _writeIdx;
        int _size;

        float _lpState;
        float _noiseLpState;
        float _bellLpStateL, _bellLpStateR;

        float _vibPhase, _vibInc;

        enum EnvStage { Idle, Attack, Sustain, Release }
        EnvStage _stage = EnvStage.Idle;
        float _env, _envAttackRate, _envReleaseRate;

        bool _active;
        int _note;
        float _velocity;
        Random _noiseRng;

        const float SilenceThreshold = 1e-5f;
        float _peakEnvelope;

        public bool IsActive => _active;
        public int Note => _note;

        public BrassVoice(int sampleRate)
        {
            _sr = sampleRate;
            _tube = new float[Math.Max(sampleRate / 20, 4096)];
        }

        public void NoteOn(int note, float velocity, in BrassParams p)
        {
            _note = note;
            _velocity = velocity;

            double freq = 440.0 * Math.Pow(2.0, (note - 69) / 12.0);
            _size = Math.Max(4, Math.Min(_tube.Length, (int)Math.Round(_sr / freq)));

            Array.Clear(_tube, 0, _size);
            _writeIdx = 0;
            _lpState = 0f;
            _noiseLpState = 0f;
            _bellLpStateL = _bellLpStateR = 0f;

            _noiseRng = new Random(note * 7919 + Environment.TickCount);
            _vibPhase = (float)(_noiseRng.NextDouble() * 2 * Math.PI);
            _vibInc = (float)(2 * Math.PI * p.VibratoRateHz / _sr);

            float attackSamples = Math.Max(1f, p.AttackSec * _sr);
            _envAttackRate = 1f / attackSamples;
            float releaseSamples = Math.Max(1f, p.ReleaseSec * _sr);
            _envReleaseRate = 1f / releaseSamples;
            _env = 0f;
            _stage = EnvStage.Attack;

            _peakEnvelope = 1f;
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
            _peakEnvelope = 0f;
            Array.Clear(_tube, 0, _tube.Length);
        }

        public float RenderSample(in BrassParams p)
        {
            if (!_active) return 0f;

            switch (_stage)
            {
                case EnvStage.Attack:
                    _env += _envAttackRate;
                    if (_env >= 1f) { _env = 1f; _stage = EnvStage.Sustain; }
                    break;
                case EnvStage.Release:
                    _env -= _envReleaseRate;
                    if (_env <= 0f) _env = 0f;
                    break;
            }

            // Vibrato : longueur de délai modulée
            _vibPhase += _vibInc;
            if (_vibPhase > 2 * Math.PI) _vibPhase -= (float)(2 * Math.PI);
            float vibCents = (float)Math.Sin(_vibPhase) * p.VibratoDepthCents;
            float sizeVib = _size / (float)Math.Pow(2.0, vibCents / 1200.0);
            int sizeI = Math.Max(4, Math.Min(_size, (int)sizeVib));

            // Lecture au bout du tube
            int readIdx = _writeIdx - sizeI;
            while (readIdx < 0) readIdx += _size;
            float tapped = _tube[readIdx];

            // Non-linéarité "lèvres" : compression + drive selon LipTension × Pressure × envelope
            //   pressure_eff = pressure × env × velocity → force effective de l'insufflation
            //   drive_eff = 1 + LipTension × 4 → gain avant le tanh (1..5)
            //   lipsOutput = tanh(driven) × pressure_eff — quand pressure = 0, aucun retour
            float pressureEff = p.BreathPressure * _env * _velocity;
            float driveEff = 1f + p.LipTension * 4f;
            float driven = tapped * driveEff + pressureEff * 0.5f;
            float lipsOut = (float)Math.Tanh(driven) * pressureEff;

            // Souffle : bruit blanc filtré LP à 4000 Hz, ajouté proportionnellement à BreathNoise
            float noise = (float)(_noiseRng.NextDouble() * 2 - 1);
            float noiseAlpha = 1f - (float)Math.Exp(-2.0 * Math.PI * 4000f / _sr);
            _noiseLpState += noiseAlpha * (noise - _noiseLpState);
            float breathNoise = _noiseLpState * p.BreathNoise * pressureEff * 0.3f;

            // Feedback LP (Damping) : le tube absorbe les aigus progressivement
            float lpCutoff = 800f + (1f - p.Damping) * 3000f;   // 800..3800 Hz
            float lpAlpha = 1f - (float)Math.Exp(-2.0 * Math.PI * lpCutoff / _sr);
            _lpState += lpAlpha * (lipsOut - _lpState);

            // Écriture dans le tube = feedback filtré + souffle
            float writeVal = _lpState + breathNoise;
            _tube[_writeIdx] = writeVal;
            _writeIdx++;
            if (_writeIdx >= _size) _writeIdx = 0;

            // Sortie : compensation brightness sur le tap (contrebalance le LP dans la boucle)
            float outSignal = tapped * (0.5f + p.Brightness * 0.8f);

            // Bell size : LP en sortie (petit pavillon = mordant, gros = arrondi)
            float bellCutoff = 1500f + (1f - p.BellSize) * 6500f;
            float bellAlpha = 1f - (float)Math.Exp(-2.0 * Math.PI * bellCutoff / _sr);
            _bellLpStateL += bellAlpha * (outSignal - _bellLpStateL);

            // Détection d'énergie pour libération
            float absOut = Math.Abs(_bellLpStateL);
            _peakEnvelope = Math.Max(_peakEnvelope * 0.9998f, absOut);
            if (_stage == EnvStage.Release && _env <= 0f && _peakEnvelope < SilenceThreshold)
            {
                _active = false;
                _stage = EnvStage.Idle;
                return 0f;
            }

            return _bellLpStateL;
        }
    }
}
