using System;

namespace KotonPluginDuduk
{
    /// <summary>Réglages d'une voix. Volontairement RESTREINT : on part d'un modèle simple et on
    /// enrichira au fur et à mesure de ce que l'oreille réclame.</summary>
    internal struct DkParams
    {
        public float Brightness;      // 0..1 — inclinaison du spectre source (anche molle -> sombre)
        public float Harmonics;       // rang du dernier partiel entendu (« jusqu'où monte le timbre »)
        public float OddBias;         // 0..1 — dominance des harmoniques impaires (perce cylindrique)
        public float Voice;           // 0..1 — force du formant vocal
        public float Breath;          // 0..1 — souffle continu audible dans le son
        public float PortamentoSec;   // glissé vers la note suivante, sur les enchaînements liés
        public float VibratoRateHz;
        public float VibratoDepthCents;
        public float VibratoRiseSec;  // temps d'installation du vibrato après l'attaque
        public float AttackSec;
        public float ReleaseSec;
    }

    /// <summary>
    /// Une voix de duduk : une source ADDITIVE (série d'harmoniques construite explicitement) façonnée
    /// par trois résonances FIXES en fréquence absolue.
    ///
    /// **Pourquoi pas le guide d'onde.** Une première version modélisait la perce en Kelly-Lochbaum, ce
    /// qui est acoustiquement la bonne façon de faire et laisse le portamento glisser réellement dans le
    /// tube. Mais elle n'a jamais tenu l'accord : la longueur effective de la boucle dépend du retard de
    /// groupe de l'anche et des filtres, si bien que chaque retouche du modèle désaccordait l'instrument
    /// de plus d'un demi-ton, et deux notes de l'ambitus n'avaient aucune hauteur stable. Ici la hauteur
    /// est un simple incrément de phase : elle est juste par construction, sur tout l'ambitus et quels
    /// que soient les réglages.
    ///
    /// **Pourquoi additif plutôt qu'une dent de scie filtrée.** La version précédente mêlait une dent de
    /// scie et un carré, puis coupait le tout par un passe-bas ; le banc de formants ne laissait ensuite
    /// passer que les partiels qui tombaient par hasard près de ses résonances. Mesuré, cela donnait un
    /// son à peu près SINUSOÏDAL — l'ensemble des partiels 11 à 27 dB sous la fondamentale selon la note —
    /// et un équilibre impair/pair qui changeait de signe d'une note à l'autre, jusqu'à un la4 dont
    /// l'octave sortait plus fort que la fondamentale. En construisant la série terme à terme, chaque
    /// harmonique a le niveau qu'on lui donne, et ce niveau ne dépend plus de la note jouée.
    ///
    /// **Trois traits du duduk, et comment ils sont obtenus :**
    ///
    /// 1. **Des formants FIXES.** On décrit le duduk comme l'instrument le plus proche de la voix
    ///    humaine, et une voix se reconnaît à des résonances qui ne bougent pas quand la hauteur change —
    ///    c'est ce qui fait qu'un « a » reste un « a », grave ou aigu.
    ///
    /// 2. **Harmoniques impaires dominantes.** La perce est cylindrique et fermée côté anche, comme une
    ///    clarinette : les partiels pairs y sont bien plus faibles que les impairs. Sans être absents pour
    ///    autant — l'anche double et la perce réelle ne sont pas celles d'une clarinette, et un spectre
    ///    strictement impair sonnerait comme un chalumeau.
    ///
    /// 3. **Peu de partiels, aucun éclat.** L'anche (le ghamish) est énorme et molle : elle étouffe les
    ///    aigus. C'est ce qui sépare le duduk du hautbois bien plus que sa perce, et c'est ce que règle
    ///    <see cref="DkParams.Harmonics"/> — le rang où la série s'arrête.
    /// </summary>
    internal sealed class DudukVoice
    {
        /// <summary>Plafond du banc additif. Au-delà, un duduk n'a plus rien à dire : même à pleine
        /// brillance les partiels y sont 60 dB sous la fondamentale.</summary>
        const int MaxHarm = 32;

        readonly int _sr;

        double _phase;                       // 0..1
        double _hz, _hzTarget, _portaCoef;

        float _vibPhase, _vibInc;
        float _vibAmt, _vibRise;             // installation progressive après l'attaque

        // Banc additif : amplitudes déjà projetées sur sin/cos, ce qui incorpore le décalage de phase
        // par harmonique sans coûter un appel trigonométrique de plus par partiel.
        readonly float[] _aSin = new float[MaxHarm + 2];
        readonly float[] _aCos = new float[MaxHarm + 2];
        int _hMax;
        float _cachedBright = -1f, _cachedHarm = -1f, _cachedOdd = -1f;
        double _cachedHz = -1;
        int _recalcCountdown;

        readonly Formant _f1 = new Formant(), _f2 = new Formant(), _f3 = new Formant();
        float _noiseLp, _noiseLp2, _chiffEnv, _breathSlow;
        float _pinkA, _pinkB, _pinkC;      // états du filtre de bruit rose

        float _env, _envAttack, _envRelease;
        enum Stage { Idle, Attack, Sustain, Release }
        Stage _stage = Stage.Idle;

        int _note;
        float _velocity;
        bool _active;
        Random _rng = new Random(12345);

        public bool IsActive => _active;
        public int Note => _note;

        public DudukVoice(int sampleRate) { _sr = sampleRate <= 0 ? 44100 : sampleRate; }

        static double NoteHz(int note) => 440.0 * Math.Pow(2.0, (note - 69) / 12.0);

        public void NoteOn(int note, float velocity, in DkParams p)
        {
            _note = note;
            _velocity = velocity;
            _hzTarget = NoteHz(note);

            // Portamento : la hauteur ne saute pas d'une note à l'autre, elle y GLISSE. Sur un duduk
            // le joueur découvre progressivement un trou au lieu de le libérer d'un coup, et les
            // mélodies arméniennes sont pleines de ces liaisons glissées — c'est presque plus
            // caractéristique que le vibrato.
            //
            // Il ne s'applique qu'aux enchaînements LIÉS : une note qui démarre alors que rien ne
            // sonne se pose directement sur sa hauteur, sans quoi chaque attaque partirait de la note
            // précédente et l'instrument aurait l'air de chercher sa note.
            bool legato = _active && p.PortamentoSec > 0.001f;
            if (!legato) _hz = _hzTarget;
            _portaCoef = legato
                ? 1.0 - Math.Exp(-1.0 / Math.Max(1.0, p.PortamentoSec * _sr * 0.35))
                : 1.0;

            if (!_active)
            {
                _phase = 0; _noiseLp = 0f; _noiseLp2 = 0f; _breathSlow = 0f;
                _pinkA = _pinkB = _pinkC = 0f;
                _f1.Clear(); _f2.Clear(); _f3.Clear();
                _rng = new Random(note * 7919 + Environment.TickCount);
            }
            _cachedHz = -1;   // force le recalcul du banc pour la nouvelle hauteur
            // Le vibrato REPART DE ZÉRO à chaque attaque et se réinstalle progressivement. C'est le
            // geste réel : un joueur de duduk attaque droit puis fait vivre la note tenue. Poser le
            // vibrato dès la première milliseconde s'entend tout de suite comme une machine.
            //
            // Mais sur une note LIÉE il ne repart pas : un joueur ne recommence pas son vibrato à
            // chaque note d'une phrase liée, il le porte d'une note à l'autre.
            if (!legato) { _vibPhase = 0f; _vibAmt = 0f; }
            _vibRise = 1f / Math.Max(1f, p.VibratoRiseSec * _sr);
            _vibInc = (float)(2 * Math.PI * p.VibratoRateHz / _sr);

            // Bouffée d'air à l'attaque, brève et douce — le duduk ne claque pas.
            _chiffEnv = 0.22f + velocity * 0.28f;

            _envAttack = 1f / Math.Max(1f, p.AttackSec * _sr);
            _envRelease = 1f / Math.Max(1f, p.ReleaseSec * _sr);
            _stage = Stage.Attack;
            _active = true;
        }

        public void NoteOff() { if (_active && _stage != Stage.Release) _stage = Stage.Release; }

        public void Kill()
        {
            _active = false; _stage = Stage.Idle; _env = 0f;
            _f1.Clear(); _f2.Clear(); _f3.Clear();
        }

        /// <summary>
        /// Recalcule le niveau de chaque harmonique. Appelé quand un réglage ou la hauteur ont bougé,
        /// pas à chaque échantillon : c'est une trentaine de puissances et de racines.
        ///
        /// Trois lois se composent :
        /// <list type="number">
        /// <item>une décroissance en 1/hᵅ — la pente que règle la brillance, l'anche molle du duduk
        /// donnant une pente raide ;</item>
        /// <item>l'atténuation des rangs PAIRS, qui est la signature d'une perce cylindrique fermée ;</item>
        /// <item>un adoucissement du haut de la série, pour que déplacer le curseur « Harmoniques »
        /// ouvre et referme le timbre au lieu de faire apparaître et disparaître des partiels d'un coup.</item>
        /// </list>
        ///
        /// Les partiels au-delà de Nyquist sont simplement absents : la série est donc à bande limitée
        /// par construction, sans le repliement qu'il fallait corriger sur une dent de scie.
        /// </summary>
        void RebuildHarmonics(in DkParams p, double hz)
        {
            _cachedBright = p.Brightness; _cachedHarm = p.Harmonics; _cachedOdd = p.OddBias; _cachedHz = hz;

            float hf = Math.Max(1f, Math.Min(MaxHarm, p.Harmonics));
            double slope = 1.95 - 1.15 * Math.Max(0f, Math.Min(1f, p.Brightness));
            double odd = Math.Max(0f, Math.Min(1f, p.OddBias));
            double nyq = _sr * 0.45;

            var a = new double[MaxHarm + 2];
            double sumSq = 0;
            int top = 0;
            for (int h = 1; h <= MaxHarm; h++)
            {
                if (h > hf + 1 || hz * h > nyq) break;
                double v = Math.Pow(h, -slope);
                // Atténuation des rangs pairs, EXPONENTIELLE en décibels plutôt que linéaire en amplitude.
                // Une loi linéaire ne descendait qu'à −6 dB en position médiane, soit exactement ce que la
                // pente 1/hᵅ retire déjà d'un rang au suivant : les pairs et les impairs finissaient au
                // même niveau et le caractère cylindrique ne s'entendait pas. Ici la moitié de la course
                // vaut −13 dB et le bout −26 dB, franchement clarinette.
                if ((h & 1) == 0) v *= Math.Pow(0.05, odd);

                // Bord haut de la série : cosinus surélevé sur le dernier quart, puis extinction linéaire
                // sur le dernier rang (fractionnaire) pour que le réglage soit continu.
                double t = hf > 1.0 ? (h - 1.0) / (hf - 1.0) : 1.0;
                if (t > 0.75) v *= 0.5 * (1.0 + Math.Cos(Math.PI * Math.Min(1.0, (t - 0.75) / 0.25)));
                if (h > hf) v *= Math.Max(0.0, 1.0 - (h - hf));

                if (v <= 1e-5) continue;
                a[h] = v; sumSq += v * v; top = h;
            }
            _hMax = top;

            // Normalisation en ÉNERGIE, pas en somme d'amplitudes : ouvrir le timbre doit l'enrichir,
            // pas le faire baisser. Le facteur de crête reste tenu par la dispersion de phase ci-dessous.
            double norm = sumSq > 1e-12 ? 0.62 / Math.Sqrt(sumSq) : 0.0;

            for (int h = 1; h <= _hMax; h++)
            {
                // Phases de Schroeder : sans elles, tous les partiels s'alignent une fois par période et
                // la forme d'onde devient une impulsion — beaucoup de crête pour peu de son. Les décaler
                // en h² étale l'énergie sur toute la période, ce qui donne un signal nettement plus nourri
                // à niveau crête égal, sans rien changer au spectre (l'oreille est sourde à ces phases).
                double phi = Math.PI * h * (h - 1) / Math.Max(1, _hMax);
                double amp = a[h] * norm;
                _aSin[h] = (float)(amp * Math.Cos(phi));
                _aCos[h] = (float)(amp * Math.Sin(phi));
            }
        }

        public float RenderSample(in DkParams p)
        {
            if (!_active) return 0f;

            if (_stage == Stage.Attack)
            {
                _env += _envAttack;
                if (_env >= 1f) { _env = 1f; _stage = Stage.Sustain; }
            }
            else if (_stage == Stage.Release)
            {
                _env -= _envRelease;
                if (_env <= 0f) { _env = 0f; _active = false; _stage = Stage.Idle; return 0f; }
            }

            // ---- hauteur : portamento, puis vibrato qui s'installe -----------------------------------
            _hz += (_hzTarget - _hz) * _portaCoef;
            if (_vibAmt < 1f) { _vibAmt += _vibRise; if (_vibAmt > 1f) _vibAmt = 1f; }
            _vibPhase += _vibInc;
            if (_vibPhase > 2 * Math.PI) _vibPhase -= (float)(2 * Math.PI);
            float vib = (float)Math.Sin(_vibPhase) * _vibAmt;
            double hz = _hz * Math.Pow(2.0, vib * p.VibratoDepthCents / 1200.0);

            // Le banc ne se reconstruit que quand quelque chose a bougé, et au plus une fois par
            // milliseconde : un portamento fait dériver la hauteur en continu, le suivre à l'échantillon
            // coûterait plus que tout le reste de la voix réunie.
            if (--_recalcCountdown <= 0)
            {
                _recalcCountdown = _sr / 1000;
                if (p.Brightness != _cachedBright || p.Harmonics != _cachedHarm || p.OddBias != _cachedOdd
                    || _cachedHz <= 0 || Math.Abs(hz - _cachedHz) > _cachedHz * 0.01)
                    RebuildHarmonics(p, hz);
            }

            // ---- source additive ---------------------------------------------------------------------
            double dt = hz / _sr;
            _phase += dt;
            if (_phase >= 1.0) _phase -= 1.0;

            // Récurrences de Tchebychev : sin(hθ) et cos(hθ) à partir de sin θ et cos θ, deux
            // multiplications par harmonique. Repartir de la phase à chaque échantillon plutôt que
            // d'entretenir un oscillateur par partiel évite toute dérive numérique.
            double th = 2.0 * Math.PI * _phase;
            double cs = Math.Cos(th);
            double sPrev = 0.0, sCur = Math.Sin(th);
            double cPrev = 1.0, cCur = cs;
            double src = 0.0;
            for (int h = 1; h <= _hMax; h++)
            {
                src += _aSin[h] * sCur + _aCos[h] * cCur;
                double sNext = 2.0 * cs * sCur - sPrev; sPrev = sCur; sCur = sNext;
                double cNext = 2.0 * cs * cCur - cPrev; cPrev = cCur; cCur = cNext;
            }
            float voiced = (float)src;

            // ---- souffle ----------------------------------------------------------------------------
            // Deux composantes distinctes : la bouffée d'air de l'ATTAQUE, qui s'éteint en une centaine
            // de millisecondes, et un souffle CONTINU présent tout au long de la note. Sans ce second, le
            // son est propre au point d'en devenir synthétique — un duduk laisse toujours entendre l'air
            // passer.
            //
            // Le bruit est ROSE, pas blanc. Une turbulence réelle — l'air qui force un passage étroit —
            // a un spectre qui décroît avec la fréquence ; un bruit blanc, plat par définition, met
            // autant d'énergie entre 10 et 11 kHz qu'entre 100 et 1100 Hz, et c'est ce trop-plein d'aigu
            // qui s'entend comme un souffle de bande plutôt que comme un souffle humain. Deux pôles de
            // plus rabotent ensuite ce qui reste au-dessus du kilohertz, là où le ghamish n'émet rien.
            //
            // Le tout traverse les formants comme le reste du signal, et c'est voulu : le bruit naît à
            // l'anche puis parcourt la perce, donc il est coloré par les mêmes résonances que le son.
            float white = (float)(_rng.NextDouble() * 2 - 1);
            _pinkA = 0.99765f * _pinkA + white * 0.0990460f;
            _pinkB = 0.96300f * _pinkB + white * 0.2965164f;
            _pinkC = 0.57000f * _pinkC + white * 1.0526913f;
            float pink = (_pinkA + _pinkB + _pinkC + white * 0.1848f) * 0.16f;

            float ka = 1f - (float)Math.Exp(-2.0 * Math.PI * 900.0 / _sr);
            _noiseLp += ka * (pink - _noiseLp);
            _noiseLp2 += ka * (_noiseLp - _noiseLp2);

            // Le débit d'un souffle humain n'est jamais parfaitement égal : une ondulation très lente
            // suffit à faire la différence entre un souffle et un générateur de bruit. Bornée, sinon les
            // rares excursions de la marche aléatoire s'entendent comme des rafales.
            _breathSlow += 0.00012f * (white - _breathSlow);
            float wob = _breathSlow * 9f;
            if (wob > 0.6f) wob = 0.6f; else if (wob < -0.6f) wob = -0.6f;
            float breathGain = p.Breath * (1f + wob);
            if (breathGain < 0f) breathGain = 0f;

            // La bouffée d'attaque garde un pôle de moins : un coup d'air est plus clair qu'un souffle
            // installé, et l'entendre légèrement plus haut est ce qui le fait lire comme un départ.
            float air = _noiseLp2 * breathGain * 0.50f + _noiseLp * _chiffEnv * 0.26f;
            if (_chiffEnv > 1e-4f) _chiffEnv *= 0.99965f; else _chiffEnv = 0f;

            // ---- formants fixes ---------------------------------------------------------------------
            // Ils COLORENT une source déjà complète, ils ne la fabriquent plus. D'où des gains bien plus
            // sages qu'avant : quand la source ne contenait presque rien, il fallait que les résonateurs
            // fassent tout le son, et c'est ce qui rendait le timbre dépendant de la note jouée — selon
            // qu'un partiel tombait ou non dans une résonance.
            float exc = voiced + air;
            float body = _f1.Process(exc) * 1.05f
                       + _f2.Process(exc) * (0.22f + p.Voice * 0.55f)
                       + _f3.Process(exc) * 0.09f;

            // Aucun bruit DIRECT dans le mélange final : le passer à côté des formants revenait à poser un
            // souffle non coloré par-dessus l'instrument, ce qui s'entend comme deux sources distinctes.
            float sig = voiced * 0.68f + body * 0.52f;

            // Le vibrato module aussi légèrement le niveau : une note de duduk n'est jamais plate.
            return sig * _env * _velocity * (1f + vib * 0.06f);
        }

        /// <summary>Prépare les trois formants. Appelé une fois au Prepare du plugin : leurs fréquences
        /// sont FIXES, c'est tout l'intérêt du modèle.</summary>
        public void SetupFormants()
        {
            // Valeurs de départ pour un duduk : un corps grave marqué, la résonance « vocale » autour du
            // kilohertz — c'est elle qu'on entend comme une voyelle — et un aigu volontairement discret,
            // parce que l'instrument n'a pas d'éclat.
            _f1.Set(320f, 2.6f, _sr);
            _f2.Set(1020f, 2.4f, _sr);   // large A DESSEIN : trop fin, il tombait sur l'OCTAVE des notes A4-D5 et la rendait plus forte que la fondamentale
            _f3.Set(2600f, 3.0f, _sr);
        }

        /// <summary>Résonance à deux pôles, forme bande passante. Les coefficients ne sont recalculés que
        /// si la fréquence ou la finesse changent : un cosinus par échantillon coûterait plus cher que
        /// tout le reste de la voix.</summary>
        internal sealed class Formant
        {
            float _c, _r2, _g, _y1, _y2;
            float _lastHz = -1f, _lastQ = -1f;

            public void Set(float hz, float q, int sr)
            {
                if (hz == _lastHz && q == _lastQ) return;
                _lastHz = hz; _lastQ = q;
                double w = 2.0 * Math.PI * Math.Max(20f, Math.Min(hz, sr * 0.45f)) / sr;
                double r = Math.Exp(-w / (2.0 * Math.Max(0.5, q)));
                _c = (float)(2.0 * r * Math.Cos(w));
                _r2 = (float)(r * r);
                _g = (float)((1.0 - r * r) * Math.Sin(w));   // niveau constant quelle que soit la finesse
            }

            public float Process(float x)
            {
                float y = _c * _y1 - _r2 * _y2 + _g * x;
                _y2 = _y1; _y1 = y;
                return y;
            }

            public void Clear() { _y1 = _y2 = 0f; }
        }
    }
}
