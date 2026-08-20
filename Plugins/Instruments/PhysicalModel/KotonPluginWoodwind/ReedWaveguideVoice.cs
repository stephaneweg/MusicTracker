using System;

namespace KotonPluginWoodwind
{
    /// <summary>
    /// Guide d'onde numerique pour instruments A ANCHE (clarinette, sax alto/tenor, hautbois,
    /// basson, cor anglais). Contrairement a la synthese additive de <see cref="WoodwindVoice"/>
    /// qui somme des sinusoides parfaitement stables, ce modele SIMULE la physique acoustique :
    ///
    ///   * Table d'anche (fonction non-lineaire) : la reflexion depend de la difference de
    ///     pression bouche/tube via une courbe cubique clippee → generation naturelle
    ///     d'harmoniques dynamiques (plus on souffle fort, plus les aigus apparaissent = saturation
    ///     reelle de l'anche, pas juste une modulation d'amplitude).
    ///   * Tube acoustique : une ligne a retard demi-onde avec reflexion LP au pavillon.
    ///     Clarinette (cylindrique fermee) = reflexion NEGATIVE → suppression naturelle des
    ///     harmoniques paires (signature spectrale de l'instrument). Sax et hautbois (coniques
    ///     equivalents demi-tube ouvert) = reflexion positive → spectre complet.
    ///
    /// **Un seul Write par sample** dans la delay line (le pointer doit avancer d'un sample par
    /// sample rendu, sinon la frequence effective est divisee par 2).
    ///
    /// **Auto-oscillation** : le systeme naît d'une boucle de feedback anche↔tube — pas
    /// d'oscillateur explicite. Souffle doux = quasi-sinusoide, souffle fort = spectre riche.
    /// </summary>
    internal sealed class ReedWaveguideVoice
    {
        readonly int _sr;
        readonly float[] _bore;
        readonly int _boreMask;
        int _writeIdx;

        float _lpState;                 // LP au pavillon (reflexion filtree)
        float _delayLen;                // en samples (fractionnaire)
        float _reflectSign;             // -0.98 clarinette (invers), +0.90 sax/hautbois/basson

        bool _active;
        int _note;
        float _velocity;

        // Enveloppe pression (attack + release)
        float _breathEnv;
        float _attackRate, _releaseRate;
        bool _releasing;

        Random _rng;

        public bool IsActive => _active;
        public int Note => _note;

        public ReedWaveguideVoice(int sampleRate)
        {
            _sr = sampleRate;
            int size = 1;
            int need = Math.Max(sampleRate / 20, 4096);
            while (size < need) size <<= 1;
            _bore = new float[size];
            _boreMask = size - 1;
        }

        public void NoteOn(int note, float velocity, in WwParams p)
        {
            _note = note;
            _velocity = velocity;

            double f0 = 440.0 * Math.Pow(2.0, (note - 69) / 12.0);

            // Half-tube demi-onde : resonance a f = sr / (2 * L). Pour obtenir f0 : L = sr/(2*f0).
            // Clarinette : cylindrique fermee → reflexion NEG → spectre impair.
            // Sax/hautbois/basson/cor : coniques equivalents demi-tube ouvert → reflexion POS → spectre complet.
            _delayLen = (float)(_sr / (2.0 * f0));

            int instr = p.InstrumentIdx;
            bool isClarinette = (instr == 1);
            _reflectSign = isClarinette ? -0.98f : +0.92f;

            Array.Clear(_bore, 0, _bore.Length);
            _writeIdx = 0;
            _lpState = 0f;
            _breathEnv = 0f;
            _releasing = false;

            _attackRate = 1f / Math.Max(1f, p.AttackSec * _sr);
            _releaseRate = 1f / Math.Max(1f, p.ReleaseSec * _sr);

            _rng = new Random(note * 7919 + Environment.TickCount);

            _active = true;
        }

        public void NoteOff()
        {
            _releasing = true;
        }

        public void Kill()
        {
            _active = false;
            _breathEnv = 0f;
            _releasing = false;
        }

        /// <summary>Table d'anche : reflexion non-lineaire. Cubic soft-clip normalise → sinusoide
        /// pure a faible dP, saturation progressive quand la pression augmente (harmoniques hautes
        /// apparaissent naturellement, c'est CE qui differencie du modele additif "orgue").</summary>
        static float ReedTable(float deltaP, float softness)
        {
            float pClamp = 0.6f + softness * 0.6f;   // 0.6..1.2 : anche dure ferme plus vite
            if (deltaP <= -pClamp) return -1f;
            if (deltaP >= +pClamp) return +1f;
            float n = deltaP / pClamp;
            return n - n * n * n / 3f;                // cubic soft-clip
        }

        float ReadDelay(float delaySamples)
        {
            float readPos = _writeIdx - delaySamples;
            while (readPos < 0) readPos += _bore.Length;
            int i1 = ((int)readPos) & _boreMask;
            int i2 = (i1 + 1) & _boreMask;
            float frac = readPos - (int)readPos;
            return _bore[i1] + frac * (_bore[i2] - _bore[i1]);
        }

        public float RenderSample(in WwParams p)
        {
            if (!_active) return 0f;

            // Enveloppe pression
            if (!_releasing)
            {
                _breathEnv += _attackRate;
                if (_breathEnv > 1f) _breathEnv = 1f;
            }
            else
            {
                _breathEnv -= _releaseRate;
                if (_breathEnv <= 0f) { _breathEnv = 0f; _active = false; return 0f; }
            }

            // Pression bouche (pm) : air + petit bruit de souffle
            float noise = (float)(_rng.NextDouble() * 2 - 1) * p.BreathNoise * 0.08f;
            float pm = p.AirPressure * _breathEnv * _velocity + noise;

            // Onde retour depuis le tube (interpolation lineaire pour tuning fractionnaire)
            float waveFromBore = ReadDelay(_delayLen);

            // Filtre LP au pavillon (les aigus s'echappent, les graves reviennent) + reflexion
            float cutoff = 1500f + p.Brightness * 4500f;
            float alpha = 1f - (float)Math.Exp(-2.0 * Math.PI * cutoff / _sr);
            _lpState += alpha * (waveFromBore - _lpState);
            float returned = _lpState * _reflectSign;

            // Table d'anche : delta de pression → reflexion non-lineaire
            float deltaP = pm - returned;
            float reedRefl = ReedTable(deltaP, p.ReedSoftness);
            float waveToBore = pm + deltaP * reedRefl;

            // UNE SEULE ecriture par sample (bug Gemini : le code d'origine faisait 2 writes,
            // faisant avancer le pointer 2x trop vite → frequence divisee par 2).
            _bore[_writeIdx] = waveToBore;
            _writeIdx = (_writeIdx + 1) & _boreMask;

            // Sortie audio = onde qui s'echappe du pavillon (approx : waveToBore filtre LP)
            return waveToBore - _lpState;
        }
    }
}
