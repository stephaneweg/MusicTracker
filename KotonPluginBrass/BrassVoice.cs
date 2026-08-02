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

        // 2-mass lip model (Adachi-Sato 1996) : deux masses (superieure + inferieure) reliees par
        // ressort+friction, alimentees par la difference de pression bouche-tube. Vraie physique
        // des levres qui vibrent — donne l'attaque "buzz" progressive et la stabilisation
        // harmonique naturelle qu'un simple tanh ne peut pas reproduire.
        float _lipY1, _lipY1v;   // position + vitesse masse superieure
        float _lipY2, _lipY2v;   // position + vitesse masse inferieure
        // Frequence de resonance des levres (~fondamentale de la note, avec un peu de bias
        // selon la tension). Rigidite = m × omega² ; friction ~ 2 × zeta × omega × m.
        float _lipOmega;
        float _lipDamping;
        float _lipMass;   // = 1.0 par convention, absorbe dans les autres coeffs

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

            // Setup 2-mass lip model : la frequence de resonance des levres doit etre proche de
            // la fondamentale du tube pour que l'oscillation s'installe (lock-in acoustique reel).
            // LipTension biaise legerement pour permettre le "pitch bend par levres" que fait un
            // vrai trompettiste (accroche l'harmonique voulu du tube).
            double lipFreq = freq * (0.9 + p.LipTension * 0.2);   // 0.9×..1.1× freq du tube
            _lipOmega = (float)(2.0 * Math.PI * lipFreq / _sr);
            // Damping (zeta) : dur = damping bas = lock-in rapide et agressif ; mou = damping haut =
            // lock-in progressif doux. Typiquement 0.05..0.3 pour des levres.
            _lipDamping = 0.30f - p.LipTension * 0.22f;
            _lipMass = 1f;
            // Kickstart : petite deflection initiale pour amorcer l'oscillation
            _lipY1 = 0.01f;
            _lipY2 = -0.01f;
            _lipY1v = 0f;
            _lipY2v = 0f;

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

            // Lecture au bout du tube = pression réfléchie
            int readIdx = _writeIdx - sizeI;
            while (readIdx < 0) readIdx += _size;
            float tapped = _tube[readIdx];

            // Filtre LP dans la boucle (le tube absorbe les aigus au retour)
            float lpCutoff = 800f + (1f - p.Damping) * 3000f;
            float lpAlpha = 1f - (float)Math.Exp(-2.0 * Math.PI * lpCutoff / _sr);
            _lpState += lpAlpha * (tapped - _lpState);
            // Réflexion négative aux lèvres (tube fermé) avec petit damping global (0.995 = ~-0.04dB par aller-retour)
            float returnPressure = -0.995f * _lpState;

            // Souffle : bruit blanc filtré LP à 4000 Hz
            float noise = (float)(_noiseRng.NextDouble() * 2 - 1);
            float noiseAlpha = 1f - (float)Math.Exp(-2.0 * Math.PI * 4000f / _sr);
            _noiseLpState += noiseAlpha * (noise - _noiseLpState);

            // Pression du souffle (insufflation continue) — modulée par l'enveloppe et la vélocité,
            // bruit ajouté selon BreathNoise (composante audible du souffle).
            float pressureEff = p.BreathPressure * _env * _velocity;
            float breath = pressureEff * (1f + _noiseLpState * p.BreathNoise * 0.5f);

            // 2-MASS LIP MODEL (Adachi-Sato 1996) — vraie physique des levres qui vibrent.
            // Deux masses reliees par ressort+friction, alimentees par la difference de pression :
            //   m·ÿ1 + r·ẏ1 + k·y1 = P_bouche - P_tube - offset1
            //   m·ÿ2 + r·ẏ2 + k·y2 = P_bouche - P_tube + offset2
            // Les deux masses oscillent en opposition (haut = ouvre, bas = ferme), leur difference
            // module l'ouverture des levres. Beaucoup plus riche qu'un tanh — donne l'attaque
            // "buzz" progressif naturel + la possibilite de crack sur souffle excessif.
            float delta = breath + returnPressure;
            // Force appliquee : difference de pression module la pression alveolaire des levres
            // (le vrai driver physique). Positive = pousse ouverture, negative = tire vers fermeture.
            float force = delta * 3f;

            // Integration Euler explicite (dt = 1 sample) sur les 2 masses.
            // Systeme masse-ressort-friction : ẋ = v ; v̇ = -omega²·x - 2·zeta·omega·v + F/m
            float k = _lipOmega * _lipOmega;   // rigidite (omega² × m, m=1)
            float rDamp = 2f * _lipDamping * _lipOmega;   // friction

            // Masse 1 (superieure) : reçoit +force et un petit offset qui la garde legerement fermee au repos
            float acc1 = -k * (_lipY1 - 0.1f) - rDamp * _lipY1v + force / _lipMass;
            _lipY1v += acc1;
            _lipY1  += _lipY1v;

            // Masse 2 (inferieure) : reçoit -force et un offset symmetrique
            float acc2 = -k * (_lipY2 + 0.1f) - rDamp * _lipY2v - force / _lipMass;
            _lipY2v += acc2;
            _lipY2  += _lipY2v;

            // Ouverture = distance entre les 2 masses, clampee a >=0 (si masses se croisent = ferme)
            float lipOpening = Math.Max(0f, (_lipY1 - _lipY2 + 0.3f) * 0.4f);
            if (lipOpening > 2f) lipOpening = 2f;   // saturation physique

            // Flow = opening × sign(delta) × sqrt(|delta|) selon Bernoulli
            float absDelta = Math.Abs(delta);
            float lipsAction = lipOpening * Math.Sign(delta) * (float)Math.Sqrt(absDelta) * 0.4f;
            // GATE par pressureEff (comme avant) pour couper le release
            float lipsGate = Math.Min(1f, pressureEff * 4f);
            lipsAction *= lipsGate;

            // Écriture dans le tube : action des lèvres + réflexion. Damping global 0.995.
            _tube[_writeIdx] = (lipsAction + returnPressure * 0.5f) * 0.995f;
            _writeIdx++;
            if (_writeIdx >= _size) _writeIdx = 0;

            // Sortie audio = pression au niveau du pavillon (= tap, la pression sortant du tube)
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
