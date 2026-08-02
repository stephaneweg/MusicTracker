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

            float pressureEff = p.AirPressure * _env * _velocity;

            // Non-linéarité selon le type d'excitation :
            //   - Anche (excitationType < 0.5) : asymmétrique — l'anche ferme sous forte pression
            //     mais reste ouverte sinon. Modélisé par : tanh(x) si x>0, x/(1+|x|) si x<0.
            //   - Jet d'air (excitationType >= 0.5) : symmétrique doux, moins de drive.
            float driveBase = 1f + (1f - p.ReedSoftness) * 2.5f;   // reed dur = drive plus fort (jusqu'à 3.5)
            float driven = tapped * driveBase + pressureEff * 0.4f;
            float reedOut;
            if (p.ExcitationType < 0.5f)
            {
                // Anche : asymmétrique. tanh compresse fort à la fermeture, laisse passer à l'ouverture.
                reedOut = driven >= 0f ? (float)Math.Tanh(driven) : driven / (1f + Math.Abs(driven) * 0.5f);
                reedOut *= pressureEff;
            }
            else
            {
                // Jet d'air : tanh symmétrique + moitié de drive (plus doux qu'une anche)
                reedOut = (float)Math.Tanh(driven * 0.5f) * pressureEff;
            }

            // Souffle : plus important pour un jet d'air (flûte) que pour une anche
            float noise = (float)(_noiseRng.NextDouble() * 2 - 1);
            float noiseAlpha = 1f - (float)Math.Exp(-2.0 * Math.PI * 3000f / _sr);
            _noiseLpState += noiseAlpha * (noise - _noiseLpState);
            float noiseGain = p.BreathNoise * pressureEff * (0.3f + p.ExcitationType * 0.5f);   // ×0.3..×0.8
            float breathNoise = _noiseLpState * noiseGain;

            // LP dans le feedback (Damping)
            float lpCutoff = 800f + (1f - p.Damping) * 2500f;   // 800..3300 Hz (moins que Brass)
            float lpAlpha = 1f - (float)Math.Exp(-2.0 * Math.PI * lpCutoff / _sr);
            _lpState += lpAlpha * (reedOut - _lpState);

            _tube[_writeIdx] = _lpState + breathNoise;
            _writeIdx++;
            if (_writeIdx >= _size) _writeIdx = 0;

            float outSignal = tapped * (0.4f + p.Brightness * 0.9f);

            // Bore size : LP en sortie (petit bore = clair, gros bore = doux)
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
