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

        // LP state pour le filtre passe-bas au pavillon (declare en bas de la classe).
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

        // Reed table STK : rt = offset + slope * pDiff, clippee. Offset piloté par softness.
        static float ReedTable(float pDiff, float softness)
        {
            float offset = 0.60f + softness * 0.20f;   // 0.60..0.80
            float rt = offset + 0.30f * pDiff;
            if (rt > 1f) return 1f;
            if (rt < -1f) return -1f;
            return rt;
        }

        float _lpState;

        // Lecture interpolee lineaire avec Math.Floor explicite pour eviter la troncation vers
        // zero (bug quand readPos peut passer par valeurs proches de 0 apres le wrap while).
        float ReadDelay(float delaySamples)
        {
            float readPos = _writeIdx - delaySamples;
            while (readPos < 0) readPos += _bore.Length;
            int i1 = (int)Math.Floor(readPos);
            float frac = readPos - i1;
            i1 &= _boreMask;
            int i2 = (i1 + 1) & _boreMask;
            return _bore[i1] + frac * (_bore[i2] - _bore[i1]);
        }

        public float RenderSample(in WwParams p)
        {
            if (!_active) return 0f;

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

            // Drive reduit x0.8 (au lieu de x2.5) : au-dessus de la pression critique la reed
            // sature en permanence a ±1, la table d'anche perd sa dynamique → boucle etouffee.
            // Zone d'oscillation utile ~ pm entre 0.15 et 0.75.
            float noise = (float)(_rng.NextDouble() * 2 - 1) * p.BreathNoise * 0.02f;
            float pm = Math.Max(0.05f, p.AirPressure * _breathEnv * _velocity * 0.8f) + noise;

            // 1) Lire onde retour du tube
            float waveFromBore = ReadDelay(_delayLen);

            // 2) LP doux au pavillon (cutoff 2000-8000 Hz : ne pas tuer les hautes freq
            //    necessaires a l'accrochage de l'anche)
            float cutoff = 2000f + p.Brightness * 6000f;
            float alpha = 1f - (float)Math.Exp(-2.0 * Math.PI * cutoff / _sr);
            _lpState += alpha * (waveFromBore - _lpState);

            // 3) Onde reflechie signee (- clarinette, + sax/hautbois/basson/cor)
            float reflected = _lpState * _reflectSign;

            // 4) Difference de pression a l'anche (convention pm - reflected)
            float pDiff = pm - reflected;
            float rt = ReedTable(pDiff, p.ReedSoftness);

            // 5) Injection tube : formule symetrique 0.5*pm + rt*pDiff
            float waveToBore = 0.5f * pm + rt * pDiff;

            // 6) UNE SEULE ecriture par sample
            _bore[_writeIdx] = waveToBore;
            _writeIdx = (_writeIdx + 1) & _boreMask;

            // 7) Sortie = pression rayonnee au pavillon = waveToBore - reflected
            return waveToBore - reflected;
        }
    }
}
