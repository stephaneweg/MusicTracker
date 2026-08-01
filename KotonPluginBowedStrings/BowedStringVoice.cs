using System;

namespace KotonPluginBowedStrings
{
    /// <summary>Snapshot des paramètres bowed strings figés au début d'un buffer audio. Les voix
    /// d'unison de la MÊME note partagent la plupart des params (Damping/Tone/Bow…) mais chacune a
    /// son propre offset de détune et pan appliqués au moment du NoteOn.</summary>
    internal struct BsParams
    {
        public float BowPressure;       // 0..1 → quantité de bruit blanc injectée en continu
        public float BowPosition;       // 0.02..0.5 → filtrage comb (comme pluck position en KS pincé)
        public float BowSmoothness;     // 0..1 → LP variable sur le bruit d'archet (agressif → doux)
        public float Damping;           // 0..1 → LP feedback de la boucle (mort des aigus)
        public float Tone;              // 0..1 → LP variable en série
        public float Harmonics;         // 0..1 → réduit l'atténuation du LP KS (préserve les aigus)
        public float VibratoRateHz;     // 0..8 Hz
        public float VibratoDepthCents; // 0..50 cents
        public float AttackSec;         // 0..2 s
        public float ReleaseSec;        // 0..2 s
        public float VolumeDb;          // -30..+6 dB
    }

    /// <summary>
    /// Une voix Bowed Karplus-Strong = une corde frottée. Même topologie que le KS pincé — ligne à
    /// retard + LP feedback + all-pass léger — mais l'EXCITATION est CONTINUE et non impulsionnelle :
    /// à chaque sample on injecte une petite quantité de bruit blanc filtré (l'archet) dans la boucle,
    /// modulée par <see cref="BsParams.BowPressure"/>. C'est l'idée de l'Extended Karplus-Strong (EKS)
    /// de Jaffe &amp; Smith 1983.
    ///
    /// **Conséquence** : sustain infini natif tant qu'on injecte du bruit. NoteOff = arrête l'injection
    /// (release actif) et laisse la boucle décroître naturellement selon <see cref="BsParams.Damping"/>.
    ///
    /// **Vibrato** : modulation sinus de la longueur de la ligne à retard (donc de la fréquence). Un
    /// LFO interne par voix (chaque unison a sa phase propre → chorus naturel supplémentaire).
    /// L'implémentation utilise un délai fractionnaire (2 samples voisins + lerp) pour éviter le zippage.
    ///
    /// **Attaque/relâche** : enveloppe simple (attack linéaire → sustain 1.0 → release linéaire) qui
    /// module le BowPressure. Pas d'ADSR complet — les cordes frottées ont un profil très différent
    /// de la synthèse soustractive : attaque douce, sustain plein, release rapide ou long selon le geste.
    /// </summary>
    internal sealed class BowedStringVoice
    {
        readonly int _sampleRate;
        readonly float[] _buffer;
        int _writeIdx;
        int _size;

        float _tonePrev;
        float _lpPrev;
        float _bowNoisePrev;   // LP state du bruit d'archet

        // Vibrato LFO
        float _vibPhase;
        float _vibIncrement;

        // Enveloppe d'archet
        enum EnvStage { Idle, Attack, Sustain, Release }
        EnvStage _envStage = EnvStage.Idle;
        float _envValue;
        float _envAttackRate;
        float _envReleaseRate;

        bool _active;
        int _note;
        float _velocity;
        float _detuneCents;
        float _panL, _panR;   // gains L/R précalculés pour cette voix

        // RNG dédié à cette voix — évite qu'un partage synchronise l'archet entre voix d'unison
        // (perdrait l'effet chorus naturel).
        Random _bowRng;

        const float SilenceThreshold = 1e-5f;
        float _peakEnvelope;

        public bool IsActive => _active;
        public int Note => _note;
        public float PanL => _panL;
        public float PanR => _panR;

        public BowedStringVoice(int sampleRate)
        {
            _sampleRate = sampleRate;
            _buffer = new float[Math.Max(sampleRate / 20, 4096)];
        }

        /// <summary>Démarre la voix. Le pan L/R est passé par le plugin (répartition unison).</summary>
        public void NoteOn(int note, float velocity, float detuneCents, float pan, in BsParams p)
        {
            _note = note;
            _velocity = velocity;
            _detuneCents = detuneCents;

            double freq = 440.0 * Math.Pow(2.0, (note - 69) / 12.0);
            if (detuneCents != 0f)
                freq *= Math.Pow(2.0, detuneCents / 1200.0);
            _size = Math.Max(4, Math.Min(_buffer.Length, (int)Math.Round(_sampleRate / freq)));

            Array.Clear(_buffer, 0, _size);
            _writeIdx = 0;
            _tonePrev = 0f;
            _lpPrev = 0f;
            _bowNoisePrev = 0f;

            // Pan linéaire simple L/R
            float p01 = 0.5f * (1f + pan);
            _panL = 1f - p01;
            _panR = p01;

            // Vibrato : phase aléatoire par voix pour désynchroniser les unisons
            _bowRng = new Random(note * 7919 + (int)(detuneCents * 100) + Environment.TickCount);
            _vibPhase = (float)(_bowRng.NextDouble() * 2 * Math.PI);
            _vibIncrement = (float)(2 * Math.PI * p.VibratoRateHz / _sampleRate);

            // Enveloppe : attack en samples
            float attackSamples = Math.Max(1f, p.AttackSec * _sampleRate);
            _envAttackRate = 1f / attackSamples;
            float releaseSamples = Math.Max(1f, p.ReleaseSec * _sampleRate);
            _envReleaseRate = 1f / releaseSamples;
            _envValue = 0f;
            _envStage = EnvStage.Attack;

            _peakEnvelope = 1f;
            _active = true;
        }

        /// <summary>Note off — passe l'enveloppe en release. La corde décroît d'abord via l'enveloppe
        /// puis via le damping naturel du KS. Le release doit être court (~50-200 ms) pour un jeu
        /// détaché, long (~1-2 s) pour un jeu legato/rêveur.</summary>
        public void NoteOff()
        {
            if (_active && _envStage != EnvStage.Release)
                _envStage = EnvStage.Release;
        }

        /// <summary>Coupe la voix immédiatement (reset global).</summary>
        public void Kill()
        {
            _active = false;
            _envStage = EnvStage.Idle;
            _envValue = 0f;
            _peakEnvelope = 0f;
            Array.Clear(_buffer, 0, _buffer.Length);
        }

        public float RenderSample(in BsParams p)
        {
            if (!_active) return 0f;

            // Enveloppe d'archet
            switch (_envStage)
            {
                case EnvStage.Attack:
                    _envValue += _envAttackRate;
                    if (_envValue >= 1f) { _envValue = 1f; _envStage = EnvStage.Sustain; }
                    break;
                case EnvStage.Release:
                    _envValue -= _envReleaseRate;
                    if (_envValue <= 0f) { _envValue = 0f; }
                    break;
            }

            // Vibrato : longueur de délai effective légèrement modulée
            _vibPhase += _vibIncrement;
            if (_vibPhase > 2 * Math.PI) _vibPhase -= (float)(2 * Math.PI);
            float vibCents = (float)Math.Sin(_vibPhase) * p.VibratoDepthCents;
            // Cents → multiplicateur de fréquence → division sur la longueur du délai
            float sizeVib = _size / (float)Math.Pow(2.0, vibCents / 1200.0);
            int sizeI = (int)sizeVib;
            if (sizeI < 4) sizeI = 4;
            if (sizeI > _size) sizeI = _size;   // ne dépasse pas la longueur allouée

            // Excitation par archet — bruit blanc filtré (LP variable via BowSmoothness) mixé avec un
            // gain proportionnel à BowPressure et à l'enveloppe.
            float noise = (float)(_bowRng.NextDouble() * 2.0 - 1.0);
            float alphaSmooth = 0.05f + (1f - p.BowSmoothness) * 0.5f;   // 0=très LP, 1=noise brut
            _bowNoisePrev += alphaSmooth * (noise - _bowNoisePrev);
            float bowInj = _bowNoisePrev * p.BowPressure * _envValue * _velocity * 0.5f;

            // Lecture au bout de la ligne (avec vibrato = utilise sizeI comme rayon effectif — on lit
            // à writeIdx en respectant sizeI, en circulant modulo _size)
            int readIdx = _writeIdx - sizeI;
            while (readIdx < 0) readIdx += _size;
            float sample = _buffer[readIdx];

            // 1) LP moyen tilt (Harmonics contrôle a comme dans KS pincé)
            float harmAlpha = 0.5f + p.Harmonics * 0.5f;
            float lp = harmAlpha * sample + (1f - harmAlpha) * _lpPrev;
            _lpPrev = sample;

            // 2) LP variable (tone) sur 200..8000 Hz
            float toneHz = 200f + p.Tone * 7800f;
            float toneCoef = 1f - (float)Math.Exp(-2.0 * Math.PI * toneHz / _sampleRate);
            _tonePrev += toneCoef * (lp - _tonePrev);
            float toned = _tonePrev;

            // 3) Feedback atténué (pas de sustain-boost séparé ici : le sustain vient de la re-injection
            //    continue via l'archet ; damping règle juste la couleur/brillance résiduelle).
            float gBase = 0.996f - p.Damping * 0.045f;
            float gEff = (float)Math.Pow(gBase, sizeI / 1000.0);
            float outValue = toned * gEff + bowInj;

            _buffer[_writeIdx] = outValue;
            _writeIdx++;
            if (_writeIdx >= _size) _writeIdx = 0;

            // Détection d'énergie — voix inactive quand elle est vraiment silencieuse
            float absOut = outValue < 0f ? -outValue : outValue;
            _peakEnvelope = Math.Max(_peakEnvelope * 0.9998f, absOut);
            if (_envStage == EnvStage.Release && _envValue <= 0f && _peakEnvelope < SilenceThreshold)
            {
                _active = false;
                _envStage = EnvStage.Idle;
                return 0f;
            }

            return sample;
        }
    }
}
