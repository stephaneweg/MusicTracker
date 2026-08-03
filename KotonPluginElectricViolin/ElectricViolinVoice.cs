using System;

namespace KotonPluginElectricViolin
{
    internal struct EvParams
    {
        public float BowForce;
        public float BowVelocity;
        public float BowPosition;      // 0..0.5 → position sur la corde (0.05..0.55 en pratique)
        public float Damping;          // 0..1 → module l'absorption au bridge (moins de perte HF)
        public float VibratoRateHz;
        public float VibratoDepthCents;
        public float TremoloRateHz;
        public float TremoloDepth;
        public float BodyIntensity;    // 0..1 → gain du body resonbp + formants extra Zeta
        public float Warmth;           // 0..1 → saturation tanh piezo
        public float AttackSec;
        public float ReleaseSec;
        public float VolumeDb;
    }

    /// <summary>
    /// Electric Violin — portage LITTÉRAL de FAUST physmodels.lib/violin.lib (GRAME, LGPL+audio
    /// exception), après 3 tentatives ratées où j'improvisais. Code source de référence :
    /// grame-cncm/faustlibraries/physmodels.lib (fonctions violin, violinBowedString, violinBow,
    /// violinBowTable, bowTable, violinBridge, violinNuts, bridgeFilter).
    ///
    /// **BowTable de FAUST** (le vrai algo, très simple) :
    ///   slope = 5 - 4 × bowPressure
    ///   x = vRel × slope
    ///   friction = min(1, (|x| + 0.75)^-4)
    ///
    /// Puis **bowForce = vRel × friction** (ce que j'oubliais dans les versions précédentes).
    ///
    /// **Scattering au bow point** (Kelly-Lochbaum) :
    ///   upperOut = lowerIn + bowForce
    ///   lowerOut = upperIn + bowForce
    ///
    /// **Bridge/Nut filters** = FIR 2-zeros avec réflexion négative :
    ///   filter(x) = rho × (h0 × x[n-1] + h1 × (x + x[n-2]))
    ///   h0 = (1+brightness)/2, h1 = (1-brightness)/4
    ///   rho = 0.001^(1/(320 × t60))  avec t60 = (1-absorption) × 20
    ///   Puis reflectance = -filter(x)
    ///
    /// Bridge : brightness=0.2, absorption=0.9 → t60=2s, rho=0.98931
    /// Nut :    brightness=0.6, absorption=0.1 → t60=18s, rho=0.99880
    ///
    /// **Body** : resonbp(500Hz, Q=2, gain=1) = bandpass biquad résonant
    /// </summary>
    internal sealed class ElectricViolinVoice
    {
        readonly int _sr;

        // Guide d'onde bidirectionnel : 2 lignes à retard découpant la corde au bow point.
        // Upper = du bridge vers le bow ; Lower = du bow vers le nut (sillet).
        // À chaque sample : lit à la position "délai" de chaque ligne, échange au bow (scattering),
        // écrit le résultat filtré aux extrémités.
        readonly float[] _upperString;
        readonly float[] _lowerString;
        int _upperWrite, _lowerWrite;
        int _upperLen, _lowerLen;

        // Bridge FIR filter state (2 zeros) — brightness=0.2, absorption=0.9
        float _bridgeX1, _bridgeX2;
        const float BridgeBrightness = 0.2f;
        const float BridgeH0 = (1f + BridgeBrightness) / 2f;    // = 0.6
        const float BridgeH1 = (1f - BridgeBrightness) / 4f;    // = 0.2
        float _bridgeRho;   // dépend du t60, donc de p.Damping

        // Nut FIR filter state
        float _nutX1, _nutX2;
        const float NutBrightness = 0.6f;
        const float NutH0 = (1f + NutBrightness) / 2f;   // = 0.8
        const float NutH1 = (1f - NutBrightness) / 4f;   // = 0.1
        float _nutRho;   // ~0.9988 (t60 = 18s)

        // Body : biquad resonbp(500, 2, 1) — bandpass résonant 500Hz Q=2
        BiquadState _body;
        // Extras Zeta : 2 formants biquad additionnels (700 Hz + 2500 Hz) pour le "singing"
        BiquadState _fZeta1, _fZeta2;

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
        float _bowPositionCurrent;   // stocké pour ajustement dynamique lors du vibrato

        const float SilenceThreshold = 1e-5f;
        float _peakEnvelope;

        public bool IsActive => _active;
        public int Note => _note;

        public ElectricViolinVoice(int sampleRate)
        {
            _sr = sampleRate;
            int maxLen = sampleRate / 20;   // ~50 ms max, largement suffisant
            _upperString = new float[maxLen];
            _lowerString = new float[maxLen];
            SetBiquadResonBp(ref _body, sampleRate, 500f, 2f);
            // Formants Zeta additionnels sur la sortie (non-critiques, controlés par BodyIntensity)
            SetBiquadResonBp(ref _fZeta1, sampleRate, 700f, 4f);
            SetBiquadResonBp(ref _fZeta2, sampleRate, 2500f, 5f);
        }

        public void NoteOn(int note, float velocity, in EvParams p)
        {
            _note = note;
            _velocity = velocity;

            double freq = 440.0 * Math.Pow(2.0, (note - 69) / 12.0);
            // Longueur totale de la corde en samples = 1 période acoustique.
            // La corde vibre en aller-retour : la longueur "acoustique" est SR/(f*2) pour chaque
            // demi-cycle (l'onde va d'un bout à l'autre en 1/2 période).
            int totalLen = Math.Max(8, Math.Min(_upperString.Length * 2, (int)Math.Round(_sr / freq / 2)));
            // Position d'archet : partage totalLen entre upper et lower
            _bowPositionCurrent = Math.Max(0.05f, Math.Min(0.5f, p.BowPosition));
            _upperLen = Math.Max(2, (int)(totalLen * _bowPositionCurrent));
            _lowerLen = Math.Max(2, totalLen - _upperLen);
            if (_upperLen >= _upperString.Length) _upperLen = _upperString.Length - 1;
            if (_lowerLen >= _lowerString.Length) _lowerLen = _lowerString.Length - 1;

            Array.Clear(_upperString, 0, _upperString.Length);
            Array.Clear(_lowerString, 0, _lowerString.Length);
            _upperWrite = 0; _lowerWrite = 0;
            _bridgeX1 = _bridgeX2 = 0f;
            _nutX1 = _nutX2 = 0f;
            _body.ResetState(); _fZeta1.ResetState(); _fZeta2.ResetState();

            // rho au bridge et nut selon p.Damping.
            // absorption = 0.9 - Damping * 0.5 → range [0.4, 0.9]
            // p.Damping haut = plus d'absorption = t60 plus court = son plus court
            float absorption = 0.9f - p.Damping * 0.5f;
            float t60Bridge = (1f - absorption) * 20f;
            _bridgeRho = (float)Math.Pow(0.001, 1.0 / (320.0 * t60Bridge));
            float absorbNut = 0.1f + p.Damping * 0.3f;
            float t60Nut = (1f - absorbNut) * 20f;
            _nutRho = (float)Math.Pow(0.001, 1.0 / (320.0 * t60Nut));

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

            // Vibrato : modulation de la longueur totale (donc de la fréquence)
            _vibPhase += _vibInc;
            if (_vibPhase > 2 * Math.PI) _vibPhase -= (float)(2 * Math.PI);
            float vibCents = (float)Math.Sin(_vibPhase) * p.VibratoDepthCents;
            float vibFactor = (float)Math.Pow(2.0, -vibCents / 1200.0);
            int upperLenV = Math.Max(2, Math.Min(_upperString.Length - 1, (int)(_upperLen * vibFactor)));
            int lowerLenV = Math.Max(2, Math.Min(_lowerString.Length - 1, (int)(_lowerLen * vibFactor)));

            // 1) LECTURE des 2 signaux qui arrivent au bow (fin de chaque segment)
            int upReadIdx = _upperWrite - upperLenV;
            while (upReadIdx < 0) upReadIdx += _upperString.Length;
            float upperIn = _upperString[upReadIdx];

            int loReadIdx = _lowerWrite - lowerLenV;
            while (loReadIdx < 0) loReadIdx += _lowerString.Length;
            float lowerIn = _lowerString[loReadIdx];

            // 2) BOWTABLE (FAUST bowTable/violinBowTable) :
            //    stringVel = upperIn + lowerIn
            //    vRel = bowVel - stringVel
            //    slope = 5 - 4*bowPressure
            //    x = vRel * slope
            //    friction = min(1, (|x| + 0.75)^-4)
            //    bowForce = vRel * friction
            float bowVel = p.BowVelocity * 0.4f * _env * _velocity;   // vitesse effective
            float bowPressure = p.BowForce;

            float stringVel = upperIn + lowerIn;
            float vRel = bowVel - stringVel;
            float slope = 5f - 4f * bowPressure;
            float x = vRel * slope;
            float ax = Math.Abs(x) + 0.75f;
            float ax2 = ax * ax;
            float friction = 1f / (ax2 * ax2);   // = ax^-4
            if (friction > 1f) friction = 1f;
            float bowForce = vRel * friction;

            // Gate : sans mouvement d'archet, pas d'excitation (release meurt)
            float gate = Math.Min(1f, bowVel * 5f);
            bowForce *= gate;

            // 3) SCATTERING (Kelly-Lochbaum) au bow point :
            //    upperOut = lowerIn + bowForce   (repart vers le bridge)
            //    lowerOut = upperIn + bowForce   (repart vers le nut)
            float upperOut = lowerIn + bowForce;
            float lowerOut = upperIn + bowForce;

            // 4) RÉFLEXION AU BRIDGE (FIR 2-zeros × -rho) :
            //    filter(x) = rho * (h0 * x[n-1] + h1 * (x + x[n-2]))
            //    reflectance = -filter(upperOut)
            float bridgeFilterOut = _bridgeRho * (BridgeH0 * _bridgeX1 + BridgeH1 * (upperOut + _bridgeX2));
            _bridgeX2 = _bridgeX1;
            _bridgeX1 = upperOut;
            float bridgeReflection = -bridgeFilterOut;

            // 5) RÉFLEXION AU NUT (FIR 2-zeros × -rho, plus léger)
            float nutFilterOut = _nutRho * (NutH0 * _nutX1 + NutH1 * (lowerOut + _nutX2));
            _nutX2 = _nutX1;
            _nutX1 = lowerOut;
            float nutReflection = -nutFilterOut;

            // 6) ÉCRITURE dans les lignes à retard
            //    upperString reçoit ce qui repart du sillet et voyage vers le bow
            //    lowerString reçoit ce qui repart du bridge et voyage vers le bow
            _upperString[_upperWrite] = nutReflection;
            _lowerString[_lowerWrite] = bridgeReflection;
            _upperWrite++; if (_upperWrite >= _upperString.Length) _upperWrite = 0;
            _lowerWrite++; if (_lowerWrite >= _lowerString.Length) _lowerWrite = 0;

            // 7) SORTIE : la transmittance au bridge (upperOut) passe par le body resonbp
            //    (bandpass à 500Hz Q=2 — le "corps" du violon selon FAUST)
            float bodyOut = BiquadProcess(ref _body, upperOut);

            // 8) Extras Zeta : 2 formants additionnels contrôlés par BodyIntensity
            float zeta1 = BiquadProcess(ref _fZeta1, upperOut);
            float zeta2 = BiquadProcess(ref _fZeta2, upperOut);
            float body = bodyOut + (zeta1 * 0.5f + zeta2 * 0.4f) * p.BodyIntensity;

            // 9) Warmth : saturation piezo tanh
            float saturated = (float)Math.Tanh(body * (1f + p.Warmth * 2f));

            // 10) Tremolo LFO en sortie
            _tremPhase += _tremInc;
            if (_tremPhase > 2 * Math.PI) _tremPhase -= (float)(2 * Math.PI);
            float trem = 1f - p.TremoloDepth * 0.4f * (1f - (float)Math.Cos(_tremPhase));

            float finalOut = saturated * trem;

            // Silence detection
            float absOut = Math.Abs(finalOut);
            _peakEnvelope = Math.Max(_peakEnvelope * 0.9998f, absOut);
            if (_stage == EnvStage.Release && _env <= 0f && _peakEnvelope < SilenceThreshold)
            {
                _active = false;
                _stage = EnvStage.Idle;
                return 0f;
            }

            return finalOut;
        }

        // === Biquad resonbp (bandpass résonant, comme fi.resonbp de FAUST) ===
        internal struct BiquadState
        {
            public float b0, b1, b2, a1, a2;
            public float x1, x2, y1, y2;
            public void ResetState() { x1 = x2 = y1 = y2 = 0f; }
        }
        static void SetBiquadResonBp(ref BiquadState s, int sr, float freq, float q)
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
