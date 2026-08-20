using System;

namespace KotonPluginWoodwind
{
    /// <summary>Snapshot des paramètres woodwind figés par buffer.</summary>
    internal struct WwParams
    {
        public int InstrumentIdx;
        public float AirPressure;
        public float BreathNoise;
        public float ReedSoftness;
        public float ExcitationType;
        public float Damping;
        public float Brightness;
        public float BoreSize;
        public float VibratoRateHz;
        public float VibratoDepthCents;
        public float AttackSec;
        public float ReleaseSec;
        public float VolumeDb;
    }

    /// <summary>
    /// Woodwind voice — SYNTHÈSE ADDITIVE SPECTRALE (2026-08-20 rewrite).
    ///
    /// La version précédente était un waveguide (delay + reed-table + réflexion) qui donnait un
    /// timbre pauvre et « caisse » — pas assez « bois ». Cette version utilise la même approche
    /// que le plugin Mallets (qui sonne réaliste) : synthèse additive de 12 partiels harmoniques
    /// avec un spectre caractéristique par instrument.
    ///
    /// **Structure par sample** :
    /// <code>
    ///   pour chaque partiel h = 1..12 :
    ///     s += ampNow[h] × sin(phase[h])
    ///     phase[h] += phaseInc[h]
    ///     ampNow[h] += slew × (ampTarget[h] × env × modAM - ampNow[h])
    ///   s += chiffNoise × chiffEnv        [transient d'attaque]
    ///   s += breathNoise × breath         [souffle continu modulé]
    ///   s = boreLP(s)                     [couleur du corps]
    ///   [formants appliqués par le plugin en sortie]
    /// </code>
    ///
    /// **Spectre par instrument** (amplitudes relatives des 12 premiers partiels) : ces valeurs
    /// s'inspirent d'analyses spectrales connues :
    /// - Flute : fondamentale dominante, très peu d'harmoniques (spectre quasi-pur)
    /// - Clarinette : IMPAIRES dominantes (1, 3, 5, 7) — signature du tube cylindrique fermé
    /// - Hautbois : spectre riche et régulier, harmoniques 2-5 fortes (nasal)
    /// - Basson : grave, harmoniques 2-6 fortes (résonance de la boucle)
    /// - Sax alto/ténor : medium riche, harmoniques 1-8 progressives
    /// - Piccolo : comme flûte mais plus de brillance (h=2 renforcée)
    /// - Cor anglais : hautbois plus doux, moins d'aigus
    ///
    /// **Chiff transient** : burst de bruit HP filtré (~4-8 kHz) qui décroît en 30-80 ms —
    /// c'est ce qui donne l'attaque « soufflée » caractéristique des bois. Sans ça les
    /// notes commencent brutalement, on perd tout le côté vivant.
    ///
    /// **Slight detune inharmonique** : chaque partiel a un micro-décalage aléatoire (±1.5 cents)
    /// pour éviter le « sine synthétique » — un vrai instrument n'a jamais des harmoniques
    /// parfaitement alignées.
    ///
    /// **Reed softness** : dure = renforce les harmoniques 5+ (mordant), molle = les atténue.
    /// **Damping** : les aigus décroissent plus vite en release quand damping est haut.
    /// **Brightness** : boost général des harmoniques 4+.
    /// </summary>
    internal sealed class WoodwindVoice
    {
        readonly int _sr;

        const int NumHarmonics = 12;
        readonly double[] _phase = new double[NumHarmonics];
        readonly double[] _phaseInc = new double[NumHarmonics];
        readonly float[] _ampTarget = new float[NumHarmonics];
        readonly float[] _ampNow = new float[NumHarmonics];
        readonly float[] _detune = new float[NumHarmonics];

        float _vibPhase, _vibInc;
        float _chiffEnv, _chiffDecayRate;
        float _breathBpState1, _breathBpState2;
        float _boreLp;
        float _ampModPhase;
        Random _rng;

        enum EnvStage { Idle, Attack, Sustain, Release }
        EnvStage _stage = EnvStage.Idle;
        float _env, _envAttackRate, _envReleaseRate;

        bool _active;
        int _note;
        float _velocity;
        float _f0;

        const float SilenceThreshold = 5e-5f;
        float _peakEnvelope;

        public bool IsActive => _active;
        public int Note => _note;

        // Spectres par instrument (amplitudes relatives des 12 premiers partiels).
        // Index instrument = 0..7. Chaque ligne : [h1, h2, h3, h4, h5, h6, h7, h8, h9, h10, h11, h12].
        static readonly float[][] SpectrumByInstrument =
        {
            /* 0 Flute       */ new float[] { 1.00f, 0.30f, 0.10f, 0.06f, 0.04f, 0.02f, 0.01f, 0.005f, 0f,    0f,    0f,    0f    },
            /* 1 Clarinette  */ new float[] { 1.00f, 0.08f, 0.75f, 0.05f, 0.55f, 0.04f, 0.35f, 0.03f,  0.20f, 0.02f, 0.12f, 0.01f },
            /* 2 Hautbois    */ new float[] { 0.50f, 0.65f, 1.00f, 0.95f, 0.80f, 0.65f, 0.50f, 0.35f,  0.25f, 0.18f, 0.12f, 0.08f },
            /* 3 Basson      */ new float[] { 0.70f, 1.00f, 0.85f, 0.70f, 0.55f, 0.40f, 0.28f, 0.18f,  0.12f, 0.08f, 0.05f, 0.03f },
            /* 4 Sax alto    */ new float[] { 1.00f, 0.70f, 0.60f, 0.55f, 0.45f, 0.35f, 0.28f, 0.20f,  0.15f, 0.10f, 0.07f, 0.05f },
            /* 5 Sax tenor   */ new float[] { 1.00f, 0.75f, 0.65f, 0.50f, 0.40f, 0.32f, 0.25f, 0.18f,  0.12f, 0.08f, 0.05f, 0.03f },
            /* 6 Piccolo     */ new float[] { 1.00f, 0.45f, 0.15f, 0.08f, 0.05f, 0.03f, 0.02f, 0.01f,  0f,    0f,    0f,    0f    },
            /* 7 Cor anglais */ new float[] { 0.90f, 0.85f, 0.70f, 0.75f, 0.60f, 0.45f, 0.32f, 0.22f,  0.15f, 0.10f, 0.06f, 0.04f },
        };

        public WoodwindVoice(int sampleRate) { _sr = sampleRate; }

        public void NoteOn(int note, float velocity, in WwParams p)
        {
            _note = note;
            _velocity = velocity;
            _f0 = (float)(440.0 * Math.Pow(2.0, (note - 69) / 12.0));

            // Nyquist safety
            float nyquistLimit = _sr * 0.45f;

            _rng = new Random(note * 7919 + Environment.TickCount);

            int instr = Math.Max(0, Math.Min(SpectrumByInstrument.Length - 1, p.InstrumentIdx));
            var spectrum = SpectrumByInstrument[instr];

            // Reed softness dur = boost les harmoniques 5+ (mordant), mou = les atténue
            float reedHardness = 1f - p.ReedSoftness;
            // Brightness boost général des harmoniques 4+
            float brightness = p.Brightness;

            // Roll-off spectral naturel : les notes hautes perdent des harmoniques
            // (le tube physique ne résonne pas à Nyquist)
            float rolloffPerNote = Math.Max(0f, (note - 60) / 48f);   // 0 au C4, 1 au C8

            for (int i = 0; i < NumHarmonics; i++)
            {
                int h = i + 1;
                // Micro-détonation inharmonique : ±1.5 cents par partiel pour éviter le « sine synthétique »
                _detune[i] = (float)((_rng.NextDouble() - 0.5) * 3.0);   // -1.5..+1.5 cents
                float detuneMul = (float)Math.Pow(2.0, _detune[i] / 1200.0);
                double freq = _f0 * h * detuneMul;

                if (freq > nyquistLimit)
                {
                    _ampTarget[i] = 0f;
                    _phaseInc[i] = 0;
                    _ampNow[i] = 0f;
                    _phase[i] = 0;
                    continue;
                }
                _phase[i] = _rng.NextDouble() * 2 * Math.PI;   // phase random pour éviter le clic
                _phaseInc[i] = 2.0 * Math.PI * freq / _sr;

                float baseAmp = spectrum[i];
                // Roll-off contre Nyquist et notes hautes
                float rolloff = 1f - rolloffPerNote * (i / 12f);
                if (rolloff < 0f) rolloff = 0f;
                baseAmp *= rolloff;

                // Reed hardness : boost harmoniques 5+ si dure
                if (h >= 5) baseAmp *= (0.6f + reedHardness * 1.2f);

                // Brightness : boost harmoniques 4+
                if (h >= 4) baseAmp *= (0.7f + brightness * 0.9f);

                _ampTarget[i] = baseAmp * velocity;
                _ampNow[i] = 0f;   // démarrage à 0, slew vers target
            }

            _vibPhase = (float)(_rng.NextDouble() * 2 * Math.PI);
            _vibInc = (float)(2 * Math.PI * p.VibratoRateHz / _sr);
            _ampModPhase = (float)(_rng.NextDouble() * 2 * Math.PI);

            // Chiff transient : burst d'attaque, decay ~40-80ms selon souplesse
            float chiffTimeMs = 30f + p.ReedSoftness * 50f;
            _chiffEnv = 0.4f + velocity * 0.5f;
            _chiffDecayRate = (float)Math.Exp(-6.907755278982137 / (chiffTimeMs * _sr / 1000.0));

            float attackSamples = Math.Max(1f, p.AttackSec * _sr);
            _envAttackRate = 1f / attackSamples;
            float releaseSamples = Math.Max(1f, p.ReleaseSec * _sr);
            _envReleaseRate = 1f / releaseSamples;
            _env = 0f;
            _stage = EnvStage.Attack;

            _peakEnvelope = 1f;
            _breathBpState1 = 0f;
            _breathBpState2 = 0f;
            _boreLp = 0f;
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
            for (int i = 0; i < NumHarmonics; i++) { _ampNow[i] = 0f; _ampTarget[i] = 0f; }
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

            // Vibrato pitch + AM
            _vibPhase += _vibInc;
            if (_vibPhase > 2 * Math.PI) _vibPhase -= (float)(2 * Math.PI);
            float vibSin = (float)Math.Sin(_vibPhase);
            float vibCents = vibSin * p.VibratoDepthCents;
            float vibPitchMul = (float)Math.Pow(2.0, vibCents / 1200.0);

            // AM tremolo léger (5% max) couplé au vibrato — naturel des vrais bois
            _ampModPhase += _vibInc * 1.03f;   // pas exactement même freq que le pitch, plus naturel
            if (_ampModPhase > 2 * Math.PI) _ampModPhase -= (float)(2 * Math.PI);
            float amMod = 1f + (float)Math.Sin(_ampModPhase) * 0.04f * (p.VibratoDepthCents / 10f);

            // Slew des amplitudes : approche exponentielle vers target, ~5ms
            float slew = 1f - (float)Math.Exp(-2.0 * Math.PI * 200f / _sr);

            float pressureEff = p.AirPressure * _env * _velocity;
            float envMul = _env * amMod;

            // === Partiels additifs ===
            float sum = 0f;
            for (int i = 0; i < NumHarmonics; i++)
            {
                if (_phaseInc[i] == 0) continue;
                double phaseInc = _phaseInc[i] * vibPitchMul;
                sum += _ampNow[i] * (float)Math.Sin(_phase[i]);
                _phase[i] += phaseInc;
                if (_phase[i] > 2 * Math.PI) _phase[i] -= 2 * Math.PI;
                float target = _ampTarget[i] * envMul;
                _ampNow[i] += slew * (target - _ampNow[i]);
            }

            // === Chiff transient (burst noise HP filtré) ===
            float chiff = 0f;
            if (_chiffEnv > 1e-4f)
            {
                float raw = (float)(_rng.NextDouble() * 2 - 1);
                // BP simple centré vers 5-8 kHz par soustraction de deux LP
                float alphaHi = 1f - (float)Math.Exp(-2.0 * Math.PI * 8000f / _sr);
                _breathBpState1 += alphaHi * (raw - _breathBpState1);
                float alphaLo = 1f - (float)Math.Exp(-2.0 * Math.PI * 3000f / _sr);
                _breathBpState2 += alphaLo * (raw - _breathBpState2);
                float hp = _breathBpState1 - _breathBpState2;
                chiff = hp * _chiffEnv * 0.35f;
                _chiffEnv *= _chiffDecayRate;
            }

            // === Souffle continu ===
            float breathContrib = 0f;
            if (p.BreathNoise > 0.001f)
            {
                float raw = (float)(_rng.NextDouble() * 2 - 1);
                // LP à ~3-5 kHz selon brightness pour un souffle chaleureux
                float breathCutoff = 2500f + p.Brightness * 3500f;
                float breathAlpha = 1f - (float)Math.Exp(-2.0 * Math.PI * breathCutoff / _sr);
                _breathBpState2 += breathAlpha * (raw - _breathBpState2);
                // Souffle proportionnel à la pression appliquée
                breathContrib = _breathBpState2 * p.BreathNoise * pressureEff * 0.6f;
            }

            float raw2 = sum * pressureEff * 0.7f + chiff + breathContrib;

            // === Bore LP (couleur du corps) ===
            float boreCutoff = 1500f + (1f - p.BoreSize) * 6500f;
            float boreAlpha = 1f - (float)Math.Exp(-2.0 * Math.PI * boreCutoff / _sr);
            _boreLp += boreAlpha * (raw2 - _boreLp);

            float outSignal = _boreLp;

            float absOut = Math.Abs(outSignal);
            _peakEnvelope = Math.Max(_peakEnvelope * 0.9998f, absOut);
            if (_stage == EnvStage.Release && _env <= 0f && _peakEnvelope < SilenceThreshold)
            {
                _active = false;
                _stage = EnvStage.Idle;
                return 0f;
            }

            return outSignal;
        }
    }
}
