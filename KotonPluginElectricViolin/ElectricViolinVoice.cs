using System;

namespace KotonPluginElectricViolin
{
    internal struct EvParams
    {
        public float BowForce;         // 0..1 → vélocité effective de l'archet → cutoff LP dynamique
        public float BowVelocity;      // 0..1 → alimente aussi le LP cutoff (couleur)
        public float BowPosition;      // 0..0.5 → altère le LP base cutoff (couleur du brillant)
        public float Damping;          // 0..1 → contribue au sustain et à la vitesse de release
        public float VibratoRateHz;    // 4.5..6.5 Hz typique
        public float VibratoDepthCents;
        public float TremoloRateHz;
        public float TremoloDepth;
        public float BodyIntensity;    // 0..1 → gain des 2 formants (600Hz + 3kHz)
        public float Warmth;           // 0..1 → saturation tanh (arrondit la dent de scie)
        public float AttackSec;
        public float ReleaseSec;
        public float VolumeDb;
    }

    /// <summary>
    /// Electric Violin v8 — refonte selon le brief DSP Gemini pour un son Zeta jazz fusion "charnu
    /// crémeux". Abandon TOTAL des guides d'ondes (v1-v7) qui donnaient soit du bruit, soit un son
    /// "souffle dans bouteille", soit un effet caverne. La vraie voie pour un violon électrique
    /// (pas acoustique) : sawtooth propre + chaîne DSP de traitement.
    ///
    /// **Chaîne** :
    /// 1. Sawtooth anti-aliasé (PolyBLEP) à la fréquence de la note
    /// 2. LP 24dB dynamique (cutoff = f(velocity, pression)) — harmoniques hautes quand on pousse
    /// 3. Warm saturation tanh — arrondit les angles de la dent de scie ("crémeux")
    /// 4. 2 formants EQ (peak +3dB à 600Hz corps, +2dB à 3kHz présence)
    /// 5. Compressor simple (envelope follower + gain reduction rapide)
    /// 6. LP final serré à 7kHz (élimine l'aspect "chirurgical/froid" du numérique)
    /// 7. Vibrato avec DÉLAI de 200ms + fade-in de 100ms + FM + AM légère
    ///
    /// Justification Gemini : "Pour ce son Fusion bien lisse, un oscillateur de dent de scie filtré
    /// dynamiquement donne un résultat plus propre et maîtrisé qu'un waveguide pur (qui sonne trop
    /// acoustique)." → conclusion validée après 7 versions ratées de waveguide.
    /// </summary>
    internal sealed class ElectricViolinVoice
    {
        readonly int _sr;

        // === Oscillateur sawtooth (PolyBLEP) ===
        double _phase;
        double _phaseInc;
        double _baseFreq;

        // === Envelope ===
        enum EnvStage { Idle, Attack, Sustain, Release }
        EnvStage _stage = EnvStage.Idle;
        float _env, _envAttackRate, _envReleaseRate;

        // === Vibrato avec délai + fade-in ===
        float _vibPhase;
        float _vibInc;
        int _vibDelaySamplesRemaining;   // 200 ms avant activation
        float _vibFadeIn;                // 0..1, fade progressif sur 150 ms après le délai

        // === Tremolo ===
        float _tremPhase, _tremInc;

        // === LP dynamique 24dB (2 biquads LP 12dB en cascade) ===
        BiquadState _lpDyn1, _lpDyn2;

        // === Formants (peak EQ à 600Hz et 3kHz) ===
        BiquadState _formant1, _formant2;

        // === LP final serré à 7 kHz ===
        BiquadState _lpFinal;

        // === Compressor simple ===
        float _compEnv;   // envelope follower level

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
            // Formants avec gains REDUITS et Q ELARGIS (2026-08-03 : v8 avait +4/+3dB Q=2/2.5
            // → sonnait comme une seconde voix aux fréquences boostees). Version douce : +2/+1.5dB
            // Q=1.2/1.4 = bosses larges qui colorent sans creer de pics identifiables.
            SetBiquadPeaking(ref _formant1, sampleRate, 600f, 1.2f, 2.0f);
            SetBiquadPeaking(ref _formant2, sampleRate, 3000f, 1.4f, 1.5f);
            SetBiquadLP(ref _lpFinal, sampleRate, 7000f, 0.707f);
        }

        public void NoteOn(int note, float velocity, in EvParams p)
        {
            _note = note;
            _velocity = velocity;
            _baseFreq = 440.0 * Math.Pow(2.0, (note - 69) / 12.0);
            _phase = 0;
            _phaseInc = _baseFreq / _sr;

            _lpDyn1.ResetState(); _lpDyn2.ResetState();
            _formant1.ResetState(); _formant2.ResetState();
            _lpFinal.ResetState();
            _compEnv = 0f;

            // Vibrato : délai 200ms + fade-in 150ms
            _vibDelaySamplesRemaining = (int)(0.2 * _sr);
            _vibFadeIn = 0f;
            _vibPhase = 0f;
            _vibInc = (float)(2 * Math.PI * p.VibratoRateHz / _sr);
            _tremPhase = 0f;
            _tremInc = (float)(2 * Math.PI * p.TremoloRateHz / _sr);

            // Attack : 15-50 ms selon param (borne inférieure à 15ms pour éviter le "click")
            float attackSec = Math.Max(0.015f, p.AttackSec);
            _envAttackRate = 1f / (attackSec * _sr);
            float releaseSec = Math.Max(0.02f, p.ReleaseSec);
            _envReleaseRate = 1f / (releaseSec * _sr);
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
        }

        public float RenderSample(in EvParams p)
        {
            if (!_active) return 0f;

            // --- Envelope ---
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

            // --- Vibrato dynamique avec délai + fade-in ---
            float vibMod = 0f;
            if (_vibDelaySamplesRemaining > 0)
            {
                _vibDelaySamplesRemaining--;
            }
            else
            {
                // Fade-in sur ~150 ms après le délai
                _vibFadeIn += 1f / (0.15f * _sr);
                if (_vibFadeIn > 1f) _vibFadeIn = 1f;
                _vibPhase += _vibInc;
                if (_vibPhase > 2 * Math.PI) _vibPhase -= (float)(2 * Math.PI);
                vibMod = (float)Math.Sin(_vibPhase) * p.VibratoDepthCents * _vibFadeIn;
            }

            // Fréquence effective (vibrato FM)
            double effFreq = _baseFreq * Math.Pow(2.0, vibMod / 1200.0);
            _phaseInc = effFreq / _sr;

            // --- 1) OSCILLATEUR SAWTOOTH (PolyBLEP anti-aliasé) ---
            float saw = (float)(2.0 * _phase - 1.0);
            // PolyBLEP correction aux points de discontinuité (wrap-around)
            double dt = _phaseInc;
            if (_phase < dt)
            {
                double t = _phase / dt;
                saw -= (float)(t + t - t * t - 1.0);
            }
            else if (_phase > 1.0 - dt)
            {
                double t = (_phase - 1.0) / dt;
                saw -= (float)(t * t + t + t + 1.0);
            }
            _phase += _phaseInc;
            if (_phase >= 1.0) _phase -= 1.0;

            // Amplitude modulation légère (AM du vibrato) — 5% de depth quand vibrato pleine
            float amMod = 1f - 0.05f * _vibFadeIn * (float)Math.Sin(_vibPhase);
            saw *= amMod * _env * _velocity;

            // --- 2) LP DYNAMIQUE 24dB (cascade 2 biquads LP 12dB) ---
            // Cutoff = f(velocity, BowForce, BowPosition)
            //   base 1500 Hz, monte jusqu'à 6000 Hz avec BowForce+Velocity
            //   BowPosition module : chevalet (0) = plus brillant, sultasto (0.5) = plus feutré
            float dynCutoff = 1500f
                            + p.BowForce * _velocity * 4000f
                            + (0.5f - p.BowPosition) * 2000f;
            if (dynCutoff < 400f) dynCutoff = 400f;
            if (dynCutoff > 7000f) dynCutoff = 7000f;
            SetBiquadLP(ref _lpDyn1, _sr, dynCutoff, 0.707f);
            SetBiquadLP(ref _lpDyn2, _sr, dynCutoff, 0.707f);
            float lp1 = BiquadProcess(ref _lpDyn1, saw);
            float lp2 = BiquadProcess(ref _lpDyn2, lp1);
            float filtered = lp2;

            // --- 3) WARM SATURATION (tanh) — arrondit la dent de scie ---
            float driven = filtered * (1f + p.Warmth * 2f);
            float saturated = (float)Math.Tanh(driven) * (1f / (1f + p.Warmth * 0.8f));

            // --- 4) FORMANTS (peak EQ à 600 Hz + 3 kHz) ---
            // Les formants sont EN PARALLÈLE additifs, contrôlés par BodyIntensity
            float form1 = BiquadProcess(ref _formant1, saturated);
            float form2 = BiquadProcess(ref _formant2, saturated);
            float withFormants = saturated + (form1 * 0.3f + form2 * 0.25f) * p.BodyIntensity;
            withFormants *= 1f / (1f + p.BodyIntensity * 0.35f);   // norm légère

            // --- 5) COMPRESSOR DOUX (attack lente pour eviter le pumping) ---
            //   Attack ~30ms, release ~150ms, ratio ~2:1, threshold -9dB (0.35)
            //   Version precedente (attack 5ms ratio 3:1) creait du pumping audible qui donnait
            //   l'impression d'un 2e instrument qui module l'amplitude.
            float absSig = Math.Abs(withFormants);
            float envAttack = 1f - (float)Math.Exp(-1.0 / (0.030 * _sr));
            float envRelease = 1f - (float)Math.Exp(-1.0 / (0.150 * _sr));
            float envRate = absSig > _compEnv ? envAttack : envRelease;
            _compEnv += envRate * (absSig - _compEnv);
            float compGain = 1f;
            if (_compEnv > 0.35f)
            {
                float excess = _compEnv - 0.35f;
                float reduction = excess * 0.5f;   // ratio 2:1
                compGain = (0.35f + excess - reduction) / _compEnv;
                if (compGain > 1f) compGain = 1f;
                if (compGain < 0.5f) compGain = 0.5f;
            }
            float compressed = withFormants * compGain * 1.1f;   // makeup discret

            // --- 6) LP FINAL 7 kHz (élimine "froid numérique") ---
            float final = BiquadProcess(ref _lpFinal, compressed);

            // --- 7) TREMOLO en sortie (subtil) ---
            _tremPhase += _tremInc;
            if (_tremPhase > 2 * Math.PI) _tremPhase -= (float)(2 * Math.PI);
            float trem = 1f - p.TremoloDepth * 0.3f * (1f - (float)Math.Cos(_tremPhase));

            float outSignal = final * trem * 0.6f;   // 0.6 marge anti-clip finale

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

        // === Biquad LP (RBJ cookbook) ===
        internal struct BiquadState
        {
            public float b0, b1, b2, a1, a2;
            public float x1, x2, y1, y2;
            public void ResetState() { x1 = x2 = y1 = y2 = 0f; }
        }
        static void SetBiquadLP(ref BiquadState s, int sr, float freq, float q)
        {
            if (freq < 20f) freq = 20f;
            if (freq > sr * 0.45f) freq = sr * 0.45f;
            double w0 = 2.0 * Math.PI * freq / sr;
            double alpha = Math.Sin(w0) / (2.0 * q);
            double cosw0 = Math.Cos(w0);
            double a0 = 1.0 + alpha;
            s.b0 = (float)((1.0 - cosw0) / 2.0 / a0);
            s.b1 = (float)((1.0 - cosw0) / a0);
            s.b2 = s.b0;
            s.a1 = (float)(-2.0 * cosw0 / a0);
            s.a2 = (float)((1.0 - alpha) / a0);
        }
        static void SetBiquadPeaking(ref BiquadState s, int sr, float freq, float q, float gainDb)
        {
            double A = Math.Pow(10.0, gainDb / 40.0);
            double w0 = 2.0 * Math.PI * freq / sr;
            double alpha = Math.Sin(w0) / (2.0 * q);
            double cosw0 = Math.Cos(w0);
            double a0 = 1.0 + alpha / A;
            s.b0 = (float)((1.0 + alpha * A) / a0);
            s.b1 = (float)(-2.0 * cosw0 / a0);
            s.b2 = (float)((1.0 - alpha * A) / a0);
            s.a1 = (float)(-2.0 * cosw0 / a0);
            s.a2 = (float)((1.0 - alpha / A) / a0);
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
