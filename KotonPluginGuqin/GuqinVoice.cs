using System;

namespace KotonPluginGuqin
{
    /// <summary>
    /// Voix Karplus-Strong etendue pour Guqin :
    /// - Delay line de longueur variable (position de lecture fractionnaire pour supporter les
    ///   glissandi fluides — sans ca, changer la longueur cause un click).
    /// - Feedback tres eleve (~0.995 a Sustain=0.85) → sustain 5-15s.
    /// - LP dans le feedback controle par HFDamping.
    /// - GLIDE : quand une nouvelle note arrive en LEGATO (voix deja active), on interpole la
    ///   frequence de lecture (donc la LONGUEUR de la delay-line) de l'ancienne vers la nouvelle
    ///   sur GlideMs. Interpolation en log (semi-tons) donc la direction est naturelle. Pas de
    ///   re-pluck : l'energie deja dans la boucle continue a "sonner" avec la nouvelle hauteur.
    /// </summary>
    internal sealed class GuqinVoice
    {
        readonly int _sr;

        // Delay line + position lecture fractionnaire
        const int DlSize = 8192;   // max ~185 ms a 44.1kHz → couvre ~5.4 Hz (bien en dessous de MIDI 0)
        readonly float[] _dl = new float[DlSize];
        int _writePos;
        double _readOffsetSamples;   // longueur courante du delay (fractionnaire, glide-able)

        double _targetOffsetSamples; // cible du glide (correspond a la note demandee)
        double _glideStepPerSample;  // increment log par sample (en log2(samples))
        bool _gliding;

        // LP dans le feedback (Karplus classic + damping controlable)
        float _lpState;
        float _feedbackGain;
        float _dampingCoef;

        // Envelope ADSR simple
        enum EnvStage { Idle, Attack, Sustain, Release }
        EnvStage _stage;
        float _env, _envAtkRate, _envRelRate;

        // Vibrato
        double _vibPhase;
        double _vibInc;

        int _note;
        float _velocity;
        bool _active;

        // Body resonance biquad (peak grave)
        BiquadState _body;
        float _bodyMix;

        public bool IsActive => _active;
        public int Note => _note;

        const float SilenceThreshold = 5e-5f;
        float _peak;

        public GuqinVoice(int sr) { _sr = sr; }

        double NoteToDelay(float noteMidi)
        {
            double freq = 440.0 * Math.Pow(2.0, (noteMidi - 69) / 12.0);
            return Math.Max(2.0, _sr / freq);
        }

        public void NoteOnPluck(int note, float velocity, in GuqinParams p)
        {
            _note = note;
            _velocity = velocity;
            _readOffsetSamples = NoteToDelay(note);
            _targetOffsetSamples = _readOffsetSamples;
            _gliding = false;

            // Feedback & damping
            _feedbackGain = 0.90f + p.Sustain * 0.099f;   // 0.90..0.999
            _dampingCoef = 0.15f + p.HFDamping * 0.60f;   // LP dans feedback : plus haut = plus mat

            // Corps : peak biquad centre ~180 Hz (guqin = table d'harmonie plate + long resonateur)
            SetBiquadPeaking(ref _body, _sr, 180f, 1.2f, 6.0f);
            _bodyMix = p.BodyResonance;

            // Excitation : burst de bruit blanc de PluckLength ms filtre LP par PluckBrightness
            int pluckSamples = Math.Max(4, (int)(p.PluckLengthMs * _sr / 1000.0));
            var rng = new Random(note * 7919 + Environment.TickCount);
            // Filter bruit blanc par IIR LP simple pour PluckBrightness
            float pluckLpCoef = 0.05f + (1f - p.PluckBrightness) * 0.85f;
            float pluckLp = 0f;
            // Ecrire l'excitation dans la delay-line sur les pluckSamples derniers echantillons.
            Array.Clear(_dl, 0, DlSize);
            for (int i = 0; i < pluckSamples; i++)
            {
                float noise = (float)(rng.NextDouble() * 2 - 1);
                pluckLp = pluckLp * pluckLpCoef + noise * (1f - pluckLpCoef);
                _dl[i] = pluckLp * velocity;
            }
            _writePos = pluckSamples;
            _lpState = 0f;

            // Envelope
            _envAtkRate = 1f / Math.Max(1, p.AttackMs * _sr / 1000.0f);
            _envRelRate = 1f / Math.Max(1, p.ReleaseMs * _sr / 1000.0f);
            _env = 0f;
            _stage = EnvStage.Attack;

            _vibPhase = 0; _vibInc = 2.0 * Math.PI * p.VibratoRate / _sr;
            _peak = 1f;
            _active = true;
        }

        public void NoteOnLegato(int note, float velocity, in GuqinParams p)
        {
            _note = note;
            // NE PAS re-pluck : on garde l'energie dans la delay-line. On glide juste la freq.
            _targetOffsetSamples = NoteToDelay(note);
            _feedbackGain = 0.90f + p.Sustain * 0.099f;
            _dampingCoef = 0.15f + p.HFDamping * 0.60f;

            if (p.GlideMs < 1f)
            {
                // Glide instantane → juste snap
                _readOffsetSamples = _targetOffsetSamples;
                _gliding = false;
            }
            else
            {
                // Interpolation en LOG : on avance de log-samples par sample sur GlideMs.
                // logDist = log2(target/current). Etendre au curve (0 = linear, 1 = exp).
                // Simplification : on interpole en log2(delay) linearement dans le temps, ce qui
                // donne un glide constant en semi-tons/sec — c'est le comportement naturel de
                // pitch glide, direction correcte (monter/descendre selon signe de logDist).
                double logDist = Math.Log(_targetOffsetSamples / _readOffsetSamples, 2);
                double glideSamples = p.GlideMs * _sr / 1000.0;
                _glideStepPerSample = logDist / glideSamples;
                _gliding = true;
            }
            // Envelope : on RESET l'attack, mais legere (moins fort qu'un vrai pluck)
            // → on reprend a env courant (pas de re-attack complete pour eviter le click)
            _velocity = velocity;
        }

        public void NoteOff()
        {
            if (_active && _stage != EnvStage.Release) _stage = EnvStage.Release;
        }

        public void Kill() { _active = false; _env = 0f; _stage = EnvStage.Idle; _peak = 0f; }

        public float RenderSample(in GuqinParams p)
        {
            if (!_active) return 0f;

            // --- Envelope ---
            if (_stage == EnvStage.Attack)
            {
                _env += _envAtkRate;
                if (_env >= 1f) { _env = 1f; _stage = EnvStage.Sustain; }
            }
            else if (_stage == EnvStage.Release)
            {
                _env -= _envRelRate;
                if (_env <= 0f) _env = 0f;
            }

            // --- Glide de la delay-length ---
            if (_gliding)
            {
                // On avance en log-space vers la target.
                double logCur = Math.Log(_readOffsetSamples, 2);
                logCur += _glideStepPerSample;
                _readOffsetSamples = Math.Pow(2, logCur);
                // Detection fin de glide (depasse la target dans le sens du signe)
                bool doneUp = _glideStepPerSample > 0 && _readOffsetSamples >= _targetOffsetSamples;
                bool doneDown = _glideStepPerSample < 0 && _readOffsetSamples <= _targetOffsetSamples;
                if (doneUp || doneDown) { _readOffsetSamples = _targetOffsetSamples; _gliding = false; }
            }

            // --- Vibrato : ajoute une modulation de +/-VibratoDepthCents sur la delay-length ---
            double delayNow = _readOffsetSamples;
            if (p.VibratoDepthCents > 0.01f && p.VibratoRate > 0.01f)
            {
                _vibPhase += _vibInc;
                if (_vibPhase > 2 * Math.PI) _vibPhase -= 2 * Math.PI;
                double centsMod = Math.Sin(_vibPhase) * p.VibratoDepthCents;
                delayNow *= Math.Pow(2, -centsMod / 1200.0);   // signe inverse : cents+ → freq+ → delay-
            }
            if (delayNow < 2.0) delayNow = 2.0;
            if (delayNow > DlSize - 4) delayNow = DlSize - 4;

            // --- Lecture fractionnaire ---
            double readIdx = _writePos - delayNow;
            while (readIdx < 0) readIdx += DlSize;
            int rInt = (int)readIdx;
            double rFrac = readIdx - rInt;
            int r0 = rInt % DlSize;
            int r1 = (r0 + 1) % DlSize;
            float sample = (float)(_dl[r0] * (1 - rFrac) + _dl[r1] * rFrac);

            // --- LP dans le feedback (damping) ---
            _lpState = _lpState * _dampingCoef + sample * (1f - _dampingCoef);
            float fb = _lpState * _feedbackGain;

            // Ecrire dans la delay-line
            _dl[_writePos] = fb;
            _writePos = (_writePos + 1) % DlSize;

            // --- Corps ---
            float bodySig = BiquadProcess(ref _body, sample);
            float mixed = sample + bodySig * _bodyMix * 0.4f;

            // Applique enveloppe
            float outSig = mixed * _env * _velocity;

            _peak = Math.Max(_peak * 0.9998f, Math.Abs(outSig));
            if (_stage == EnvStage.Release && _env <= 0f && _peak < SilenceThreshold)
            {
                _active = false; _stage = EnvStage.Idle;
                return 0f;
            }
            return outSig;
        }

        // ==== Biquad ====
        internal struct BiquadState
        {
            public float b0, b1, b2, a1, a2, x1, x2, y1, y2;
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
