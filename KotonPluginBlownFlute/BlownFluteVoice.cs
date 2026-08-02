using System;

namespace KotonPluginBlownFlute
{
    internal struct BfParams
    {
        public float BreathPressure;    // 0..1 — force du souffle
        public float BreathNoise;       // 0..1 — quantité de bruit audible dans le souffle (Ney = beaucoup)
        public float JetInstability;    // 0..1 — chaos du jet (Ney = doux, Shakuhachi = très expressif)
        public float EmbouchureShift;   // -1..+1 — décalage de l'angle du souffle (le "meri/kari" du shakuhachi)
        public float Damping;
        public float Brightness;
        public float VibratoRateHz;
        public float VibratoDepthCents;
        public float BreathAttack;      // 0..1 — attaque du souffle (0=direct, 1=très progressive comme méditation)
        public float ReleaseSec;
        public float VolumeDb;
    }

    /// <summary>
    /// Voix de flûte à embouchure (ney, shakuhachi, quena, kaval). Physiquement, différente du
    /// Woodwind avec anche ou de la flûte classique moderne : c'est un JET D'AIR non-linéaire
    /// qui oscille entre les deux côtés du bord de l'embouchure (jet drive).
    ///
    /// **Guide d'onde bidirectionnel simplifié** : tube = 2 lignes à retard, une pour la pression
    /// aller (vers le pied du tube) et une pour le retour. On simplifie ici en une seule ligne
    /// avec réflexion positive au bout (tube ouvert-ouvert = flûte).
    ///
    /// **Non-linéarité du jet** (McIntyre-Woodhouse simplifié) : la vitesse du souffle × l'écart
    /// du jet (par rapport au bord) produit un signal soft-cubed (x - x³/3). Le facteur JetInstability
    /// ajoute du bruit turbulent PROPORTIONNEL à la pression → plus tu souffles fort, plus ça devient
    /// chaotique (comportement observé sur shakuhachi joué "muraiki" = souffle turbulent expressif).
    ///
    /// **Meri/kari** (EmbouchureShift) : sur un shakuhachi, incliner la tête change l'angle du souffle
    /// et donc le pitch de ~50 cents. Notre param le simule via un léger décalage de la fréquence
    /// effective du tube + un tilt sur la nonlinéarité.
    ///
    /// **Breath attack** : l'attaque très progressive et lente typique de la flûte méditative est
    /// modélisée par une enveloppe exponentielle sur la pression (curveable, 0=instantanée type
    /// classique, 1=très progressive type méditation shakuhachi).
    /// </summary>
    internal sealed class BlownFluteVoice
    {
        readonly int _sr;
        readonly float[] _tube;
        int _writeIdx;
        int _size;

        float _lpState;
        float _noiseLpState;
        // Filtre de turbulence : LP variable pour le bruit chaotique du jet
        float _turbState;

        float _vibPhase, _vibInc;

        enum EnvStage { Idle, Attack, Sustain, Release }
        EnvStage _stage = EnvStage.Idle;
        float _env, _envAttackCurve, _envReleaseRate;

        bool _active;
        int _note;
        float _velocity;
        Random _rng;

        const float SilenceThreshold = 1e-5f;
        float _peakEnvelope;

        public bool IsActive => _active;
        public int Note => _note;

        public BlownFluteVoice(int sampleRate)
        {
            _sr = sampleRate;
            _tube = new float[Math.Max(sampleRate / 20, 4096)];
        }

        public void NoteOn(int note, float velocity, in BfParams p)
        {
            _note = note;
            _velocity = velocity;

            double freq = 440.0 * Math.Pow(2.0, (note - 69) / 12.0);
            // Embouchure shift : le meri/kari ajoute ±50 cents
            if (p.EmbouchureShift != 0)
                freq *= Math.Pow(2.0, p.EmbouchureShift * 50.0 / 1200.0);
            _size = Math.Max(4, Math.Min(_tube.Length, (int)Math.Round(_sr / freq)));

            Array.Clear(_tube, 0, _size);
            _writeIdx = 0;
            _lpState = 0f;
            _noiseLpState = 0f;
            _turbState = 0f;

            _rng = new Random(note * 7919 + Environment.TickCount);
            _vibPhase = (float)(_rng.NextDouble() * 2 * Math.PI);
            _vibInc = (float)(2 * Math.PI * p.VibratoRateHz / _sr);

            // Enveloppe : BreathAttack contrôle la courbe (exponentielle vs linéaire)
            // 0..1 → constante de temps 30 ms → 2 s. Attaque très progressive typique méditation.
            double attackSec = 0.03 + p.BreathAttack * 1.97;
            _envAttackCurve = (float)(1.0 - Math.Exp(-1.0 / (attackSec * _sr)));
            _envReleaseRate = 1f / Math.Max(1f, p.ReleaseSec * _sr);
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

        public float RenderSample(in BfParams p)
        {
            if (!_active) return 0f;

            // Enveloppe : attaque exponentielle (montée progressive typique du souffle)
            switch (_stage)
            {
                case EnvStage.Attack:
                    _env += _envAttackCurve * (1f - _env);
                    if (_env >= 0.995f) { _env = 1f; _stage = EnvStage.Sustain; }
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

            int readIdx = _writeIdx - sizeI;
            while (readIdx < 0) readIdx += _size;
            float tapped = _tube[readIdx];

            // LP feedback (Damping) : tube en bois absorbe les aigus
            float lpCutoff = 1500f + (1f - p.Damping) * 4500f;
            float lpAlpha = 1f - (float)Math.Exp(-2.0 * Math.PI * lpCutoff / _sr);
            _lpState += lpAlpha * (tapped - _lpState);
            // Réflexion POSITIVE aux extremités (tube ouvert-ouvert = flûte, spectre complet)
            // + damping global à 0.985 (le tube n'est pas parfaitement conservatif)
            float returnPressure = 0.985f * _lpState;

            // Souffle : bruit blanc filtré à ~3.5kHz (audibilité maximale, comme un vrai souffle sur bois)
            float noise = (float)(_rng.NextDouble() * 2 - 1);
            float noiseAlpha = 1f - (float)Math.Exp(-2.0 * Math.PI * 3500f / _sr);
            _noiseLpState += noiseAlpha * (noise - _noiseLpState);

            // Turbulence variable : plus la pression est forte + JetInstability haute, plus le
            // souffle devient chaotique (bruit haute fréquence modulé par la pression). C'est
            // caractéristique du shakuhachi joué "muraiki" (souffle expressif turbulent).
            float turbCutoff = 500f + (1f - p.JetInstability) * 4000f;
            float turbAlpha = 1f - (float)Math.Exp(-2.0 * Math.PI * turbCutoff / _sr);
            _turbState += turbAlpha * (noise - _turbState);
            float turbulence = _turbState * p.JetInstability;

            float pressureEff = p.BreathPressure * _env * _velocity;
            // Le souffle audible = bruit filtré + turbulence, modulé par pressure
            float breathAudible = (_noiseLpState * p.BreathNoise + turbulence * 0.5f) * pressureEff;
            float breath = pressureEff * (1f + breathAudible * 0.4f);

            // Non-linéarité du JET (McIntyre-Woodhouse simplifié) :
            // Le jet d'air a un comportement soft-cubed : x - x³/3 (compression douce autour de 0).
            // On calcule la différence pression-retour puis on applique cette non-linéarité.
            // GATE par pressureEff pour éviter l'auto-entretien sans souffle (comme Brass/Woodwind).
            float delta = breath - returnPressure;
            // Le jet drive avec un peu d'embouchure shift (tilt) — un léger décalage de la nonlin
            float bias = p.EmbouchureShift * 0.1f;
            float driven = delta * (1f + p.JetInstability * 1.5f) + bias;
            float clipped = driven - (driven * driven * driven) / 3f;   // x - x³/3
            float jetGate = Math.Min(1f, pressureEff * 5f);
            float jetOut = clipped * jetGate * 0.5f;

            // Écriture dans le tube = jet + réflexion, avec damping global 0.995
            _tube[_writeIdx] = (jetOut + returnPressure * 0.5f) * 0.995f;
            _writeIdx++;
            if (_writeIdx >= _size) _writeIdx = 0;

            // Sortie audio = pression au niveau du tube + composante de souffle audible
            // (dans une vraie flûte, on entend le tube ET le souffle qui passe à côté)
            float sig = tapped * (0.4f + p.Brightness * 0.8f) + breathAudible * 0.3f;

            // Détection d'énergie pour libération
            float absOut = Math.Abs(sig);
            _peakEnvelope = Math.Max(_peakEnvelope * 0.9998f, absOut);
            if (_stage == EnvStage.Release && _env <= 0f && _peakEnvelope < SilenceThreshold)
            {
                _active = false;
                _stage = EnvStage.Idle;
                return 0f;
            }

            return sig;
        }
    }
}
