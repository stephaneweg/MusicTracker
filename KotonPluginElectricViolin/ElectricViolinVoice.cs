using System;

namespace KotonPluginElectricViolin
{
    internal struct EvParams
    {
        public float BowForce;
        public float BowVelocity;
        public float BowPosition;      // 0..0.5 → position sur la corde (0=milieu, 0.5=chevalet)
        public float Damping;
        public float VibratoRateHz;
        public float VibratoDepthCents;
        public float TremoloRateHz;
        public float TremoloDepth;
        public float BodyIntensity;
        public float Warmth;
        public float AttackSec;
        public float ReleaseSec;
        public float VolumeDb;
    }

    /// <summary>
    /// Electric Violin — refonte v2 basée sur la structure de FAUST physmodels.lib/violin.lib :
    /// guide d'onde bidirectionnel (2 lignes à retard, une pour l'onde ascendante et une pour la
    /// descendante) avec un point d'archet qui échange l'énergie entre les 2 via une **bowTable
    /// Friedlander** pré-calculée. C'est le vrai modèle académique (Rocchesso 1999, Serafin 2001).
    ///
    /// **Différence avec v1** (qui sonnait comme du bruit blanc) :
    ///   v1 : formulation MSW ad-hoc avec calculs conditionnels stick/slip par-sample, gain
    ///        empirique à 1.8×, comb filter réinjecté — la boucle explosait en bruit
    ///   v2 : bowTable pré-calculée selon la formule de Friedlander (asymétrique, physique correcte),
    ///        2 lignes de délai couplées au bow point, coupling coefficient γ contrôlant l'échange
    ///        d'énergie. Formulation numériquement stable et musicalement propre.
    ///
    /// **BowTable Friedlander** : friction(vRel) = vRel × exp(-0.5 × (vRel/scale)²) — courbe
    /// asymétrique qui pique à |vRel|=scale puis décroît exponentiellement. C'est ce qui produit
    /// la commutation stick↔slip qui donne l'onde en dents de scie caractéristique du violon,
    /// SANS les instabilités des formulations conditionnelles.
    /// </summary>
    internal sealed class ElectricViolinVoice
    {
        readonly int _sr;

        // Guide d'onde BIDIRECTIONNEL : 2 lignes à retard.
        // Ligne upper = onde qui va du chevalet vers le sillet.
        // Ligne lower = onde qui va du sillet vers le chevalet.
        // Le point d'archet est à la position "bowPos" (0..1) le long de la corde.
        readonly float[] _upperString;
        readonly float[] _lowerString;
        int _upperWrite, _lowerWrite;
        int _stringLen;

        // BowTable pré-calculée (Friedlander) — 512 points, symétrique autour de 0
        static readonly float[] BowTable = BuildBowTable();
        const int BowTableSize = 512;
        const float BowTableRange = 1.0f;   // vRel range [-1, +1] → indices [0, 511]

        static float[] BuildBowTable()
        {
            var t = new float[BowTableSize];
            for (int i = 0; i < BowTableSize; i++)
            {
                float vRel = (i - BowTableSize / 2f) / (BowTableSize / 2f) * BowTableRange;
                // Friedlander : friction pique à |vRel|~0.15 puis décroit rapidement
                float scale = 0.15f;
                float x = vRel / scale;
                t[i] = vRel * (float)Math.Exp(-0.5 * x * x) / scale;
            }
            return t;
        }

        static float LookupBowTable(float vRel)
        {
            float pos = (vRel / BowTableRange + 1f) * (BowTableSize / 2f);
            if (pos < 0) pos = 0;
            if (pos >= BowTableSize - 1) pos = BowTableSize - 2;
            int i0 = (int)pos;
            float f = pos - i0;
            return BowTable[i0] * (1f - f) + BowTable[i0 + 1] * f;
        }

        // Filtres au chevalet et au sillet
        float _bridgeLpState;   // LP au chevalet (perte HF à la radiation)
        float _nutHpState;      // HP au sillet (petit high-pass au bout de la corde)

        // Body (3 formants biquad série)
        BiquadState _f1, _f2, _f3;

        // Vibrato + tremolo
        float _vibPhase, _vibInc;
        float _tremPhase, _tremInc;

        // Enveloppe
        enum EnvStage { Idle, Attack, Sustain, Release }
        EnvStage _stage = EnvStage.Idle;
        float _env, _envAttackRate, _envReleaseRate;

        bool _active;
        int _note;
        float _velocity;

        const float SilenceThreshold = 1e-5f;
        float _peakEnvelope;

        public bool IsActive => _active;
        public int Note => _note;

        public ElectricViolinVoice(int sampleRate)
        {
            _sr = sampleRate;
            // Buffer max = 2 secondes (largement suffisant pour toutes les notes)
            int maxLen = sampleRate * 2 / 10;
            _upperString = new float[maxLen];
            _lowerString = new float[maxLen];
            // Formants classiques violon (basés sur Stradivarius mesuré, cf. Meyer 1978) :
            // F1 = 300 Hz Q=8 (grave chaud "chest"), F2 = 700 Hz Q=6 (corps),
            // F3 = 2500 Hz Q=5 (le "singing" caractéristique)
            SetBiquadBandpass(ref _f1, sampleRate, 300f, 8f);
            SetBiquadBandpass(ref _f2, sampleRate, 700f, 6f);
            SetBiquadBandpass(ref _f3, sampleRate, 2500f, 5f);
        }

        public void NoteOn(int note, float velocity, in EvParams p)
        {
            _note = note;
            _velocity = velocity;

            double freq = 440.0 * Math.Pow(2.0, (note - 69) / 12.0);
            // Longueur totale de la corde = SR/f/2 pour chaque moitié (bidirectionnel) ×2 = SR/f
            _stringLen = Math.Max(8, Math.Min(_upperString.Length, (int)(_sr / freq / 2)));

            Array.Clear(_upperString, 0, _upperString.Length);
            Array.Clear(_lowerString, 0, _lowerString.Length);
            _upperWrite = 0;
            _lowerWrite = 0;
            _bridgeLpState = 0f;
            _nutHpState = 0f;
            _f1.ResetState(); _f2.ResetState(); _f3.ResetState();

            _vibPhase = 0f;
            _vibInc = (float)(2 * Math.PI * p.VibratoRateHz / _sr);
            _tremPhase = 0f;
            _tremInc = (float)(2 * Math.PI * p.TremoloRateHz / _sr);

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
            Array.Clear(_upperString, 0, _upperString.Length);
            Array.Clear(_lowerString, 0, _lowerString.Length);
        }

        public float RenderSample(in EvParams p)
        {
            if (!_active) return 0f;

            // Envelope
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

            // BOW POSITION : divise la corde en 2 segments.
            // bowPos = 0.15 typique = archet à 15% du chevalet.
            float bowPos = 0.05f + p.BowPosition;   // range 0.05..0.55
            int upperLen = Math.Max(2, (int)(_stringLen * bowPos));            // segment chevalet → archet
            int lowerLen = Math.Max(2, _stringLen - upperLen);                  // segment archet → sillet

            // Vibrato : moduler légèrement la longueur totale
            _vibPhase += _vibInc;
            if (_vibPhase > 2 * Math.PI) _vibPhase -= (float)(2 * Math.PI);
            float vibCents = (float)Math.Sin(_vibPhase) * p.VibratoDepthCents;
            float vibFactor = (float)Math.Pow(2.0, -vibCents / 1200.0);
            int upperLenV = Math.Max(2, (int)(upperLen * vibFactor));
            int lowerLenV = Math.Max(2, (int)(lowerLen * vibFactor));
            if (upperLenV >= _upperString.Length) upperLenV = _upperString.Length - 1;
            if (lowerLenV >= _lowerString.Length) lowerLenV = _lowerString.Length - 1;

            // 1) LECTURE au point d'archet : les 2 ondes qui arrivent
            //    - onde upper au bout (chevalet)
            //    - onde lower au bout (sillet)
            int upReadIdx = _upperWrite - upperLenV;
            while (upReadIdx < 0) upReadIdx += _upperString.Length;
            float upperIn = _upperString[upReadIdx];

            int loReadIdx = _lowerWrite - lowerLenV;
            while (loReadIdx < 0) loReadIdx += _lowerString.Length;
            float lowerIn = _lowerString[loReadIdx];

            // 2) POINT D'ARCHET (interaction non-linéaire via bowTable)
            //    La vitesse de la corde au point d'archet = somme des 2 ondes qui arrivent.
            float stringVel = upperIn + lowerIn;
            float bowVel = p.BowVelocity * 0.4f * _env * _velocity;   // range 0..0.4
            float bowForce = p.BowForce * _env * _velocity;

            float vRel = bowVel - stringVel;
            // Lookup dans la bowTable (Friedlander) : friction non-linéaire
            float friction = LookupBowTable(vRel) * bowForce * 3.0f;
            // Gate : sans mouvement d'archet, pas d'excitation (release meurt)
            float gate = Math.Min(1f, bowForce * 3f);
            friction *= gate;

            // 3) ÉCHANGE D'ÉNERGIE au bow point : chaque onde qui repart est
            //    la somme des 2 ondes qui arrivent (Kirchhoff) - la moitié de la friction
            //    (l'archet ajoute de l'énergie à la corde)
            float upperOut = lowerIn + friction * 0.5f;
            float lowerOut = upperIn + friction * 0.5f;

            // 4) RÉFLEXION au chevalet (fin de la ligne upper)
            //    LP + réflexion négative : la corde perd de l'énergie et inverse la phase.
            //    Damping contrôle le LP cutoff.
            float lpCutoff = 2000f + (1f - p.Damping) * 5000f;
            float lpAlpha = 1f - (float)Math.Exp(-2.0 * Math.PI * lpCutoff / _sr);
            _bridgeLpState += lpAlpha * (upperOut - _bridgeLpState);
            float bridgeReflection = -0.985f * _bridgeLpState;

            // 5) RÉFLEXION au sillet (fin de la ligne lower) : réflexion presque parfaite,
            //    petit HP pour éviter l'accumulation DC
            float alphaHp = 1f - (float)Math.Exp(-2.0 * Math.PI * 30f / _sr);
            _nutHpState += alphaHp * (lowerOut - _nutHpState);
            float nutReflection = -0.995f * (lowerOut - _nutHpState);

            // 6) ÉCRITURE dans les lignes
            _upperString[_upperWrite] = nutReflection;   // repart du sillet vers chevalet dans upperString
            _lowerString[_lowerWrite] = bridgeReflection;   // repart du chevalet vers sillet dans lowerString
            _upperWrite++; if (_upperWrite >= _upperString.Length) _upperWrite = 0;
            _lowerWrite++; if (_lowerWrite >= _lowerString.Length) _lowerWrite = 0;

            // 7) SORTIE audio = pression au chevalet (bridgeLpState = ce qui rayonne)
            float bridgeOut = _bridgeLpState;

            // 8) Body : 3 formants biquad série (résonance de la caisse)
            float f1Out = BiquadProcess(ref _f1, bridgeOut);
            float f2Out = BiquadProcess(ref _f2, bridgeOut);
            float f3Out = BiquadProcess(ref _f3, bridgeOut);
            // Mix parallèle des formants (mode plus classique que série pour body violon)
            float bodyOut = bridgeOut * (1f - p.BodyIntensity * 0.4f)
                          + (f1Out * 0.5f + f2Out * 0.4f + f3Out * 0.3f) * p.BodyIntensity * 1.2f;

            // 9) Warmth : saturation piezo tanh douce
            float saturated = (float)Math.Tanh(bodyOut * (1f + p.Warmth * 2f)) * (1f - p.Warmth * 0.3f);

            // 10) Tremolo LFO en sortie
            _tremPhase += _tremInc;
            if (_tremPhase > 2 * Math.PI) _tremPhase -= (float)(2 * Math.PI);
            float trem = 1f - p.TremoloDepth * 0.4f * (1f - (float)Math.Cos(_tremPhase));

            float outSignal = saturated * trem * 0.8f;   // 0.8 marge anti-clip

            // Silence detection
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

        // === Biquad bandpass RBJ ===
        internal struct BiquadState
        {
            public float b0, b1, b2, a1, a2;
            public float x1, x2, y1, y2;
            public void ResetState() { x1 = x2 = y1 = y2 = 0f; }
        }
        static void SetBiquadBandpass(ref BiquadState s, int sr, float freq, float q)
        {
            double w0 = 2.0 * Math.PI * freq / sr;
            double alpha = Math.Sin(w0) / (2.0 * q);
            double cosw0 = Math.Cos(w0);
            double a0 = 1.0 + alpha;
            s.b0 = (float)(alpha / a0);
            s.b1 = 0f;
            s.b2 = (float)(-alpha / a0);
            s.a1 = (float)(-2.0 * cosw0 / a0);
            s.a2 = (float)((1.0 - alpha) / a0);
        }
        static float BiquadProcess(ref BiquadState s, float x)
        {
            float y = s.b0 * x + s.b1 * s.x1 + s.b2 * s.x2 - s.a1 * s.y1 - s.a2 * s.y2;
            s.x2 = s.x1; s.x1 = x;
            s.y2 = s.y1; s.y1 = y;
            return y;
        }
    }
}
