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

            // KICK-START du tube : petite impulsion de bruit filtre pour amorcer la resonance
            // en ~3 ms au lieu que la boucle mette 20-200 ms a s'etablir depuis un tube vide (les
            // notes courtes < 200 ms n'avaient pas le temps de sonner). Amplitude modeste
            // (velocity * 0.15) — le jet prendra le relais pour maintenir l'oscillation.
            int kickSamples = Math.Min(_size, (int)(0.003 * _sr));
            float kickAmp = velocity * 0.15f;
            for (int i = 0; i < kickSamples; i++)
            {
                float decay = 1f - (float)i / kickSamples;
                _tube[i] = (float)(_rng.NextDouble() * 2 - 1) * kickAmp * decay;
            }

            // Enveloppe : BreathAttack contrôle la courbe (exponentielle vs linéaire)
            // 0..1 → constante de temps 8 ms → 2 s. Le 8 ms minimum (avant 30 ms) evite que
            // les notes courtes soient etoufees par l'attaque exponentielle qui n'atteignait
            // pas la pression suffisante pour amorcer le jet.
            double attackSec = 0.008 + p.BreathAttack * 1.992;
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
                    // Bascule en Sustain a 0.8 (au lieu de 0.995) : l'attaque exponentielle asymptote
                    // vers 1 et prend ~5 tau pour atteindre 0.995. A 0.8 c'est ~1.6 tau, largement
                    // audible et la note peut atteindre son sustain avant un release rapide.
                    if (_env >= 0.8f) { _env = 1f; _stage = EnvStage.Sustain; }
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
            // Réflexion NEGATIVE (formulation guide d'onde standard qui auto-oscille avec la
            // non-linearite tanh — meme approche que Woodwind mode anche qui marche). Version
            // precedente avait reflexion positive + non-lin x-x³/3 : mathematiquement plausible
            // pour un tube ouvert mais ne s'auto-entretient pas → seul le breath audible passe,
            // le tube reste silencieux. Bug rapporte 2026-08-02 : "que du bruit blanc".
            float returnPressure = -0.985f * _lpState;

            // Souffle : bruit blanc filtré à ~3.5kHz
            float noise = (float)(_rng.NextDouble() * 2 - 1);
            float noiseAlpha = 1f - (float)Math.Exp(-2.0 * Math.PI * 3500f / _sr);
            _noiseLpState += noiseAlpha * (noise - _noiseLpState);

            // Turbulence variable : muraiki
            float turbCutoff = 500f + (1f - p.JetInstability) * 4000f;
            float turbAlpha = 1f - (float)Math.Exp(-2.0 * Math.PI * turbCutoff / _sr);
            _turbState += turbAlpha * (noise - _turbState);
            float turbulence = _turbState * p.JetInstability;

            float pressureEff = p.BreathPressure * _env * _velocity;
            float breathAudible = (_noiseLpState * p.BreathNoise + turbulence * 0.5f) * pressureEff;
            float breath = pressureEff * (1f + breathAudible * 0.4f);

            // Non-linéarité du jet : tanh(delta * drive). La flute a besoin d'un drive plus
            // fort qu'un simple anche (car le jet n'a pas la meme efficacite qu'une anche pour
            // piloter la boucle) — d'ou le 2.0 + jetInstability*2.
            float delta = breath + returnPressure;   // + car returnPressure est deja negatif
            float bias = p.EmbouchureShift * 0.1f;
            float driven = delta * (2f + p.JetInstability * 2f) + bias;
            float jetOut = (float)Math.Tanh(driven);
            // GATE par pressureEff pour eviter l'auto-entretien sans souffle
            float jetGate = Math.Min(1f, pressureEff * 5f);
            jetOut *= jetGate * 0.5f;

            // Écriture dans le tube = jet + réflexion, avec damping global 0.995
            _tube[_writeIdx] = (jetOut + returnPressure * 0.5f) * 0.995f;
            _writeIdx++;
            if (_writeIdx >= _size) _writeIdx = 0;

            // Sortie audio = pression au niveau du tube + composante de souffle audible
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
