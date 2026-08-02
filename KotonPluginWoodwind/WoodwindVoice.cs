using System;

namespace KotonPluginWoodwind
{
    /// <summary>Snapshot des paramètres woodwind figés par buffer.</summary>
    internal struct WwParams
    {
        public float AirPressure;
        public float BreathNoise;
        public float ReedSoftness;      // 0 = anche dure/agressive (hautbois, sax), 1 = anche molle / jet flûte
        public float ExcitationType;    // 0 = anche (asymmétrique), 1 = jet d'air (symmétrique doux)
        public float Damping;
        public float Brightness;
        public float BoreSize;          // taille du corps de tube (LP en sortie)
        public float VibratoRateHz;
        public float VibratoDepthCents;
        public float AttackSec;
        public float ReleaseSec;
        public float VolumeDb;
    }

    /// <summary>
    /// Une voix de bois — guide d'onde simplifié. Même topologie que Brass (tube + non-linéarité +
    /// LP feedback + injection de bruit) mais avec deux différences clés :
    ///
    /// 1. **Excitation** : deux modes possibles au choix — <b>anche</b> (clarinette/hautbois/sax) =
    ///    non-linéarité ASYMMÉTRIQUE (l'anche ferme d'un côté mais s'ouvre pas de l'autre), ou <b>jet
    ///    d'air</b> (flûte/piccolo) = non-linéarité SYMMÉTRIQUE plus douce mais avec beaucoup de
    ///    bruit d'excitation (le souffle audible caractéristique de la flûte).
    /// 2. **Non-linéarité globalement PLUS DOUCE** que les cuivres : les bois ont moins d'harmoniques
    ///    (surtout les impaires prédominent pour un tube fermé style clarinette), donc le drive avant
    ///    tanh est plus modeste.
    ///
    /// **Reed softness** module la dureté de l'anche : anche dure (sax alto avec baguette 3) =
    ///     réponse plus mordante, transitoires nets. Anche molle (clarinette Vandoren 1.5) =
    ///     attaque douce, timbre chaleureux.
    ///
    /// **Bore size** : diamètre relatif du tube → LP en sortie (petit bore = clair et perçant
    /// comme un hautbois, gros bore = arrondi comme un basson).
    /// </summary>
    internal sealed class WoodwindVoice
    {
        readonly int _sr;
        readonly float[] _tube;
        int _writeIdx;
        int _size;

        float _lpState;
        float _noiseLpState;
        float _boreLpState;

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

        public WoodwindVoice(int sampleRate)
        {
            _sr = sampleRate;
            _tube = new float[Math.Max(sampleRate / 20, 4096)];
        }

        public void NoteOn(int note, float velocity, in WwParams p)
        {
            _note = note;
            _velocity = velocity;

            double freq = 440.0 * Math.Pow(2.0, (note - 69) / 12.0);
            _size = Math.Max(4, Math.Min(_tube.Length, (int)Math.Round(_sr / freq)));

            Array.Clear(_tube, 0, _size);
            _writeIdx = 0;
            _lpState = 0f;
            _noiseLpState = 0f;
            _boreLpState = 0f;

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

        public float RenderSample(in WwParams p)
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

            // Vibrato
            _vibPhase += _vibInc;
            if (_vibPhase > 2 * Math.PI) _vibPhase -= (float)(2 * Math.PI);
            float vibCents = (float)Math.Sin(_vibPhase) * p.VibratoDepthCents;
            float sizeVib = _size / (float)Math.Pow(2.0, vibCents / 1200.0);
            int sizeI = Math.Max(4, Math.Min(_size, (int)sizeVib));

            int readIdx = _writeIdx - sizeI;
            while (readIdx < 0) readIdx += _size;
            float tapped = _tube[readIdx];

            // LP dans le feedback (Damping)
            float lpCutoff = 800f + (1f - p.Damping) * 2500f;
            float lpAlpha = 1f - (float)Math.Exp(-2.0 * Math.PI * lpCutoff / _sr);
            _lpState += lpAlpha * (tapped - _lpState);
            // Réflexion NEGATIVE pour les deux modes — la formulation guide d'onde avec tanh
            // s'auto-entretient uniquement avec reflexion negative. Version precedente avait
            // reflexion positive pour le jet (theoriquement correct pour tube ouvert) mais la
            // boucle ne s'entretient pas → que du bruit blanc audible (bug rapporte 2026-08-02).
            // Le caractere "tube ouvert" du jet est compense par le drive plus fort applique plus bas.
            float returnPressure = -0.98f * _lpState;

            // Souffle bruité
            float noise = (float)(_noiseRng.NextDouble() * 2 - 1);
            float noiseAlpha = 1f - (float)Math.Exp(-2.0 * Math.PI * 3000f / _sr);
            _noiseLpState += noiseAlpha * (noise - _noiseLpState);

            float pressureEff = p.AirPressure * _env * _velocity;
            // Bruit d'excitation plus fort pour un jet d'air (souffle audible caractéristique flûte)
            float noiseGain = p.BreathNoise * (0.3f + p.ExcitationType * 0.5f);
            float breath = pressureEff * (1f + _noiseLpState * noiseGain);

            // Non-linéarité selon le type d'excitation, opérant sur la différence de pression :
            //   - Anche : soft clip asymmétrique (l'anche ferme d'un côté). Approximation reed table.
            //   - Jet : tanh symmétrique plus doux (le jet oscille des deux côtés du tube).
            float delta = breath + returnPressure;   // + car returnPressure porte déjà son signe
            float drive = 1f + (1f - p.ReedSoftness) * 2.5f;
            float driven = delta * drive;
            float excitation;
            if (p.ExcitationType < 0.5f)
            {
                // Anche : asymmétrique. Plus dur à l'ouverture (delta > 0), plus souple à la fermeture.
                excitation = driven >= 0f ? (float)Math.Tanh(driven) : driven / (1f + Math.Abs(driven) * 0.7f);
            }
            else
            {
                // Jet : tanh symmétrique doux
                excitation = (float)Math.Tanh(driven * 0.7f);
            }
            // GATE de l'excitation par la pression courante : sans souffle, l'anche/le jet ne
            // s'entretiennent pas — essentiel pour que la boucle meure au release. Sans gate,
            // tanh(delta * drive) amplifie le retour du tube meme avec pressureEff=0 (bug 2026-08-02).
            float exGate = Math.Min(1f, pressureEff * 4f);
            excitation *= 0.5f * exGate;

            // Écriture dans le tube = excitation + réflexion, damping global 0.99 pour un decay
            // naturel des harmoniques quand l'excitation est coupée.
            _tube[_writeIdx] = (excitation + returnPressure * 0.5f) * 0.99f;
            _writeIdx++;
            if (_writeIdx >= _size) _writeIdx = 0;

            float outSignal = tapped * (0.4f + p.Brightness * 0.9f);

            // Bore size : LP en sortie
            float boreCutoff = 1500f + (1f - p.BoreSize) * 6000f;
            float boreAlpha = 1f - (float)Math.Exp(-2.0 * Math.PI * boreCutoff / _sr);
            _boreLpState += boreAlpha * (outSignal - _boreLpState);

            float absOut = Math.Abs(_boreLpState);
            _peakEnvelope = Math.Max(_peakEnvelope * 0.9998f, absOut);
            if (_stage == EnvStage.Release && _env <= 0f && _peakEnvelope < SilenceThreshold)
            {
                _active = false;
                _stage = EnvStage.Idle;
                return 0f;
            }

            return _boreLpState;
        }
    }
}
