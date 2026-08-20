using System;

namespace KotonPluginBrass
{
    /// <summary>Snapshot des parametres brass figes par buffer.</summary>
    internal struct BrassParams
    {
        public int   InstrumentIdx;      // 0=Trompette 1=Cor 2=Trombone 3=Tuba (info : le plugin l'utilise pour le formant)
        public float BreathPressure;     // 0..1 : amplitude generale de la voix
        public float BreathNoise;        // 0..1 : noise additif proportionnel au souffle
        public float Overshoot;          // 0..1 : intensite du pic initial d'index (20-40 ms apres attack)
        public float FmMaxIndex;         // 0..8 : index FM max (pilote par vel x envelope)
        public float Brightness;         // 0..1 : offset general de l'index (plus haut = plus brillant en steady state)
        public float Damping;            // 0..1 : rapidite du decay de l'overshoot (0 = decay lent, 1 = rapide)
        public float VibratoRateHz;
        public float VibratoDepthCents;
        public float AttackSec;
        public float ReleaseSec;
        public float VolumeDb;
    }

    /// <summary>
    /// Voix cuivre — synthese FM 2 operateurs ratio 1:1 (approche recommandee pour les cuivres,
    /// popularisee par le Yamaha DX7). La refonte 2026-08-21 remplace le waveguide tube+lip du
    /// modele physique (jamais vraiment realiste) par une synthese FM plus efficace et plus
    /// controlable.
    ///
    /// **Principe** :
    ///   output = sin(carrier_phase + I(t) * sin(modulator_phase))
    ///   avec carrier_freq = modulator_freq = f0 (ratio 1:1)
    ///
    /// La modulation cree des SIDEBANDS a chaque n × f0 : plus l'index I augmente, plus les
    /// harmoniques hautes apparaissent → le son devient "cuivre" (crenele riche). Un cuivre joue
    /// pianissimo (I ~ 0.3) sonne presque sinus doux ; joue fortissimo (I ~ 6) devient franchement
    /// brass avec toute la brillance.
    ///
    /// **Overshoot d'attaque** : quand un cuivriste demarre une note, la pression d'air initiale
    /// depasse la pression de croisiere → l'index FM fait un pic de 20-40 ms puis retombe vers le
    /// steady state. C'est L'INGREDIENT qui donne le "wah" caracteristique d'un cuivre. Modelise
    /// par une enveloppe secondaire (_overshootEnv) qui multiplie l'index pendant la fin d'attaque
    /// et decroit exponentiellement.
    ///
    /// **Velocity tracking** : l'index est pilote par velocity × ampEnvelope × Brightness. Vel
    /// bas = son doux/rond, vel eleve = son incisif/agressif.
    ///
    /// Le plugin ajoute par-dessus le FORMANT DE PAVILLON (bandpass biquad par instrument) qui
    /// donne la signature vocale specifique (trompette = 1500-2000 Hz, cor = 500 Hz, etc.).
    /// </summary>
    internal sealed class BrassVoice
    {
        readonly int _sr;
        // UNE SEULE phase pour les 2 operateurs (ratio 1:1 exact) — evite la derive de phase
        // cumulative qu'auraient 2 phases modulo separement (le timbre changerait au fil de la
        // tenue de note). Merci Gemini pour le catch.
        double _phase, _phaseInc;
        float _prevModOut;              // memoire pour le feedback FM du modulateur

        float _vibPhase, _vibInc;
        Random _rng;
        float _noiseLp;

        enum EnvStage { Idle, Attack, Sustain, Release }
        EnvStage _stage = EnvStage.Idle;
        float _amp;
        float _ampAttackRate, _ampReleaseRate;

        // Overshoot : env secondaire, 1 au NoteOn puis decay exp vers 0 (des le NoteOn, pas apres
        // l'attack — sinon sur un cor a attack lent le pic tomberait trop tard dans la note).
        float _overshootEnv;
        float _overshootRate;
        float _overshootAmount;

        // Pitch envelope d'attaque : la note demarre ~15 cents trop bas puis se stabilise en ~40 ms.
        // C'est le "growl" caracteristique d'un cuivriste qui trouve la note en soufflant. Decay exp
        // du bend vers 0 (semitones = 0).
        float _pitchBendCents;    // fige au NoteOn, decroit vers 0
        float _pitchBendRate;

        bool _active;
        int _note;
        float _velocity;

        public bool IsActive => _active;
        public int Note => _note;

        public BrassVoice(int sampleRate) { _sr = sampleRate; }

        public void NoteOn(int note, float velocity, in BrassParams p)
        {
            _note = note;
            _velocity = velocity;
            double freq = 440.0 * Math.Pow(2.0, (note - 69) / 12.0);
            _phaseInc = 2.0 * Math.PI * freq / _sr;
            _phase = 0;
            _prevModOut = 0f;
            _rng = new Random(note * 7919 + Environment.TickCount);
            _vibPhase = (float)(_rng.NextDouble() * 2 * Math.PI);
            _vibInc = (float)(2 * Math.PI * p.VibratoRateHz / _sr);
            _noiseLp = 0f;

            float attSamples = Math.Max(1f, p.AttackSec * _sr);
            _ampAttackRate = 1f / attSamples;
            float relSamples = Math.Max(1f, p.ReleaseSec * _sr);
            _ampReleaseRate = 1f / relSamples;
            _amp = 0f;
            _stage = EnvStage.Attack;

            // Overshoot decay : 15 ms (dur, damp=1) a 60 ms (long, damp=0)
            float overshootMs = 60f - p.Damping * 45f;
            _overshootRate = (float)Math.Exp(-1.0 / (overshootMs * _sr / 1000.0));
            _overshootEnv = 1f;
            _overshootAmount = p.Overshoot;

            // Pitch envelope d'attaque : proportionnelle a velocity (jouer fort = bend plus marque)
            // et a overshoot (relie a l'intensite de l'attaque). Decay exp ~40 ms.
            _pitchBendCents = -18f * velocity * p.Overshoot;
            _pitchBendRate = (float)Math.Exp(-1.0 / (0.040 * _sr));

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
            _amp = 0f;
            _overshootEnv = 0f;
        }

        public float RenderSample(in BrassParams p)
        {
            if (!_active) return 0f;

            switch (_stage)
            {
                case EnvStage.Attack:
                    _amp += _ampAttackRate;
                    if (_amp >= 1f) { _amp = 1f; _stage = EnvStage.Sustain; }
                    break;
                case EnvStage.Release:
                    _amp -= _ampReleaseRate;
                    if (_amp <= 0f) { _amp = 0f; _active = false; _stage = EnvStage.Idle; return 0f; }
                    break;
            }

            // Overshoot decroit DES le NoteOn (pas apres l'attack) : sinon sur un cor a attack lent
            // (60 ms), le pic d'index tomberait a la fin de l'attaque au lieu d'etre synchrone
            // avec l'impact d'air initial. Le pic doit etre SIMULTANE au "coup de langue" du
            // cuivriste, pas 60 ms plus tard.
            _overshootEnv *= _overshootRate;

            // Vibrato couple pitch + amp : sur un cuivre, le vibrato vient des levres/diaphragme
            // et module SIMULTANEMENT le pitch et l'amplitude (contrairement au violon = pitch pur).
            _vibPhase += _vibInc;
            if (_vibPhase > 2 * Math.PI) _vibPhase -= (float)(2 * Math.PI);
            float vibSin = (float)Math.Sin(_vibPhase);
            float vibCents = vibSin * p.VibratoDepthCents;
            // Pitch bend d'attaque decroit exp vers 0
            _pitchBendCents *= _pitchBendRate;
            double pitchMul = Math.Pow(2.0, (vibCents + _pitchBendCents) / 1200.0);
            // Amp modulation : proportionnelle au vibrato depth, max ~8% de swing
            float vibAmp = 1f + vibSin * (p.VibratoDepthCents / 30f) * 0.08f;

            // FM 2-op ratio 1:1 : phase UNIQUE partagee entre porteuse et modulateur
            double inc = _phaseInc * pitchMul;

            // Index de modulation : pilote par velocity, ampEnvelope, brightness et overshoot.
            // baseIdx : steady-state, brightness rehausse (0.4x..1.0x du max).
            // overshoot : bonus multiplicatif 0..+overshootAmount au tout debut.
            float baseIdx = p.FmMaxIndex * (0.4f + p.Brightness * 0.6f) * _velocity * _amp;
            float overBoost = 1f + _overshootAmount * _overshootEnv;
            float I = baseIdx * overBoost;

            // Feedback FM sur le modulateur : introduit un leger comportement chaotique / bruit de
            // pression caracteristique des fortes pressions d'air (grit fortissimo). Proportionnel
            // a velocity × brightness → nul sur pianissimo doux, marque sur brass fortissimo.
            float fb = 0.15f * _velocity * p.Brightness;
            float modOut = (float)Math.Sin(_phase + _prevModOut * fb);
            _prevModOut = modOut;
            float sample = (float)Math.Sin(_phase + I * modOut);

            _phase += inc;
            if (_phase > 2 * Math.PI) _phase -= 2 * Math.PI;

            float outValue = sample * _amp * p.BreathPressure * _velocity * vibAmp;

            // Breath noise additif proportionnel au volume (le souffle audible du cuivriste)
            if (p.BreathNoise > 0.001f)
            {
                float raw = (float)(_rng.NextDouble() * 2 - 1);
                float alpha = 1f - (float)Math.Exp(-2.0 * Math.PI * 5000f / _sr);
                _noiseLp += alpha * (raw - _noiseLp);
                outValue += _noiseLp * p.BreathNoise * _amp * p.BreathPressure * 0.15f;
            }

            return outValue;
        }
    }
}
