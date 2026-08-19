using System;

namespace KotonPluginSitar
{
    internal sealed class SitarVoice
    {
        readonly int _sr;
        const int DlSize = 8192;
        readonly float[] _dl = new float[DlSize];
        int _writePos;
        int _delayLen;

        // 5 cordes sympathiques : delay lines auxiliaires accordees sur Sa/Ma/Pa + octaves
        // (degres I / IV / V + I' + V' du "shruti" — accordage typique du tarab).
        const int SymCount = 5;
        readonly float[][] _sym;
        readonly int[] _symLen;
        readonly int[] _symWr;

        // LP feedback
        float _lpState;
        float _feedback;
        float _brightCoef;

        // Envelope
        enum Stage { Idle, Attack, Sustain, Release }
        Stage _stage;
        float _env, _atkR, _relR;

        // Vibrato
        double _vibPhase, _vibInc;

        int _note;
        float _velocity;
        bool _active;
        float _peak;
        const float Silence = 5e-5f;

        public bool IsActive => _active;
        public int Note => _note;

        public SitarVoice(int sr)
        {
            _sr = sr;
            _sym = new float[SymCount][];
            _symLen = new int[SymCount];
            _symWr = new int[SymCount];
            for (int i = 0; i < SymCount; i++) _sym[i] = new float[DlSize];
        }

        public void NoteOn(int note, float velocity, in SitarParams p)
        {
            _note = note; _velocity = velocity;
            double freq = 440.0 * Math.Pow(2.0, (note - 69) / 12.0);
            _delayLen = Math.Max(2, (int)Math.Round(_sr / freq));
            if (_delayLen > DlSize - 4) _delayLen = DlSize - 4;

            _feedback = 0.92f + p.Sustain * 0.078f;
            _brightCoef = 0.10f + (1f - p.Brightness) * 0.60f;   // LP dans la boucle : bas=brillant

            // Accord des sympathiques : Sa (0), Ma (5), Pa (7), Sa' (12), Pa (7-12)
            int[] intervals = { 0, 5, 7, 12, -5 };
            for (int i = 0; i < SymCount; i++)
            {
                double sf = freq * Math.Pow(2.0, intervals[i] / 12.0);
                int slen = Math.Max(2, (int)Math.Round(_sr / sf));
                if (slen > DlSize - 4) slen = DlSize - 4;
                _symLen[i] = slen;
                Array.Clear(_sym[i], 0, DlSize);
                _symWr[i] = 0;
            }

            // Excitation : bruit blanc filtre LP par Brightness
            int pluckSamples = Math.Max(4, (int)(p.PluckLengthMs * _sr / 1000.0));
            var rng = new Random(note * 7919 + Environment.TickCount);
            float pluckLpCoef = 0.05f + (1f - p.Brightness) * 0.85f;
            float pluckLp = 0f;
            Array.Clear(_dl, 0, DlSize);
            for (int i = 0; i < pluckSamples; i++)
            {
                float noise = (float)(rng.NextDouble() * 2 - 1);
                pluckLp = pluckLp * pluckLpCoef + noise * (1f - pluckLpCoef);
                _dl[i] = pluckLp * velocity;
            }
            _writePos = pluckSamples;
            _lpState = 0;

            _atkR = 1f / Math.Max(1, p.AttackMs * _sr / 1000.0f);
            _relR = 1f / Math.Max(1, p.ReleaseMs * _sr / 1000.0f);
            _env = 0f; _stage = Stage.Attack;
            _vibPhase = 0; _vibInc = 2.0 * Math.PI * p.VibRate / _sr;
            _peak = 1f; _active = true;
        }

        public void NoteOff() { if (_active && _stage != Stage.Release) _stage = Stage.Release; }
        public void Kill() { _active = false; _env = 0; _stage = Stage.Idle; _peak = 0; }

        public float RenderSample(in SitarParams p)
        {
            if (!_active) return 0f;
            if (_stage == Stage.Attack) { _env += _atkR; if (_env >= 1f) { _env = 1f; _stage = Stage.Sustain; } }
            else if (_stage == Stage.Release) { _env -= _relR; if (_env <= 0f) _env = 0f; }

            // Vibrato (module la longueur du delay principal - subtil pour meend/gliss)
            double vibDelta = 0;
            if (p.VibDepthCents > 0.01f && p.VibRate > 0.01f)
            {
                _vibPhase += _vibInc;
                if (_vibPhase > 2 * Math.PI) _vibPhase -= 2 * Math.PI;
                double cents = Math.Sin(_vibPhase) * p.VibDepthCents;
                vibDelta = _delayLen * (Math.Pow(2, -cents / 1200.0) - 1);
            }
            int readDelay = _delayLen + (int)Math.Round(vibDelta);
            if (readDelay < 2) readDelay = 2;
            if (readDelay > DlSize - 2) readDelay = DlSize - 2;

            int r = (_writePos - readDelay + DlSize) % DlSize;
            float s = _dl[r];

            // LP dans le feedback (Karplus classic + brightness)
            _lpState = _lpState * _brightCoef + s * (1f - _brightCoef);

            // JAWARI : soft-clip asymetrique dans le feedback → distorsion des harmoniques =
            // buzz caracteristique du bridge courbe. Plus Jawari haut, plus le clip est agressif.
            float x = _lpState * _feedback;
            if (p.Jawari > 0.01f)
            {
                float drive = 1f + p.Jawari * 3.5f;
                float driven = x * drive;
                // Soft-clip tanh asymetrique (+0.2 offset donne le cote "buzz metallique")
                float clipped = (float)Math.Tanh(driven + p.Jawari * 0.15f) - (float)Math.Tanh(p.Jawari * 0.15f);
                x = clipped / drive;
            }

            _dl[_writePos] = x;
            _writePos = (_writePos + 1) % DlSize;

            // Cordes sympathiques : chaque sympa recoit l'output de la corde principale scale par
            // SympathyLevel, y compris son propre feedback avec SympathyDecay.
            float symSum = 0;
            float symFb = 0.85f + p.SympathyDecay * 0.14f;
            float excite = s * p.SympathyLevel * 0.15f;
            for (int i = 0; i < SymCount; i++)
            {
                int sr2 = (_symWr[i] - _symLen[i] + DlSize) % DlSize;
                float ss = _sym[i][sr2];
                symSum += ss;
                _sym[i][_symWr[i]] = ss * symFb + excite;
                _symWr[i] = (_symWr[i] + 1) % DlSize;
            }
            symSum *= p.SympathyLevel * 0.5f;

            float outSig = (s + symSum) * _env * _velocity;
            _peak = Math.Max(_peak * 0.9998f, Math.Abs(outSig));
            if (_stage == Stage.Release && _env <= 0f && _peak < Silence) { _active = false; _stage = Stage.Idle; return 0f; }
            return outSig;
        }
    }
}
