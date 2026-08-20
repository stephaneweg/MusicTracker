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

        // (LP filter state est _lpPrevIn en bas du fichier — filtre averaged STK simple)
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
            _lpPrevIn = 0f;
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

        // Reed table STK : rt = offset + slope * pDiff, clippee. Offset piloté par softness :
        // hard reed = offset plus bas (ferme plus vite), soft reed = offset plus haut.
        static float ReedTable(float pDiff, float softness)
        {
            float offset = 0.60f + softness * 0.20f;   // 0.60..0.80
            float rt = offset + 0.30f * pDiff;
            if (rt > 1f) return 1f;
            if (rt < -1f) return -1f;
            return rt;
        }

        // LP passe-bas 1er ordre averaged (STK utilise `y = 0.5*(x + prevX)` — filtre simple
        // sans coefs à calculer, tres stable, cutoff nyquist/2).
        float _lpPrevIn;

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

            // Pression bouche STK-style : AirPressure ~ 1.0 en steady state, ~1.4 max avec drive
            // × velocity + petit bruit multiplicatif (le noise MOD la pression, pas ajoute)
            float breathPressure = _breathEnv * p.AirPressure * _velocity * 2.5f;
            float noise = (float)(_rng.NextDouble() * 2 - 1) * p.BreathNoise * breathPressure * 0.08f;
            breathPressure += noise;

            // === Formulation STK Cook Clarinet A LA LETTRE ===============================
            // 1) Lire ce qui sort de la delay (onde qui retourne vers l'anche)
            float delayed = ReadDelay(_delayLen);

            // 2) LP simple : y = 0.5*(x + x_prev) — STK "OnePole" filter, coupure Nyquist/2
            float filtered = 0.5f * (delayed + _lpPrevIn);
            _lpPrevIn = delayed;

            // 3) Reflexion filtree * gain de reflexion signe (-clarinette / +sax coniques)
            float reflectedPressure = _reflectSign * filtered;

            // 4) Difference de pression : reflected - breath/2 (formule STK)
            float pressureDiff = reflectedPressure - breathPressure * 0.5f;

            // 5) Reed table -> coefficient de reflexion (adimensionnel [-1, +1])
            float rt = ReedTable(pressureDiff, p.ReedSoftness);

            // 6) Input dans le tube = breath/2 + pressureDiff * rt (formule STK Cook exacte)
            float boreInput = breathPressure * 0.5f + pressureDiff * rt;

            // 7) UNE SEULE ecriture par sample
            _bore[_writeIdx] = boreInput;
            _writeIdx = (_writeIdx + 1) & _boreMask;

            // 8) Sortie audio = l'onde qu'on lit (ce qui s'echappe du pavillon vers l'auditeur).
            //    En STK c'est delayLine.tick(...) qui retourne la valeur lue apres l'ecriture.
            return delayed;
        }
    }
}
