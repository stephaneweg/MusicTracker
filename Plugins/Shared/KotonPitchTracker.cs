using System;

namespace KotonStudio.Plugins.Shared
{
    /// <summary>
    /// Suiveur de fréquence fondamentale CONTINU, pour les effets qui doivent s'accorder sur le signal
    /// d'entrée (résonateur de corde, violon synthétique…).
    ///
    /// **Ce qu'il n'est pas** : un détecteur de NOTES. Il ne quantifie rien, n'émet aucun événement, ne
    /// décide jamais qu'une note commence ou finit. Il produit une fréquence qui glisse en permanence — donc
    /// le vibrato, les portamentos et les micro-écarts de justesse du jeu sont conservés tels quels, et une
    /// erreur d'analyse se traduit par un frémissement de timbre plutôt que par une fausse note.
    ///
    /// **Méthode** : NSDF (fonction de différence carrée normalisée) façon MPM, avec choix du PREMIER
    /// maximum-clé atteignant <see cref="Cutoff"/> × le plus haut — c'est ce qui évite de verrouiller sur la
    /// période double (l'erreur d'octave basse). Interpolation parabolique du pic pour une précision
    /// bien meilleure que le pas d'échantillon.
    ///
    /// **Coût** : l'analyse complète est en O(fenêtre × plage de retards), trop lourde pour tourner à chaque
    /// bloc sur un insert. Deux garde-fous : elle n'a lieu qu'une fois par <see cref="Hop"/> échantillons, et
    /// une fois accroché on ne cherche plus que dans un voisinage du retard précédent (mode poursuite), ce
    /// qui divise le coût par cinq ou plus. Une perte d'accroche rebascule en recherche complète.
    /// </summary>
    public sealed class KotonPitchTracker
    {
        /// <summary>Borne grave de la recherche (Hz). La monter au plus grave que l'instrument produit rend
        /// les erreurs d'octave basse arithmétiquement impossibles.</summary>
        public double MinFrequency { get; set; } = 80;

        /// <summary>Borne aiguë de la recherche (Hz).</summary>
        public double MaxFrequency { get; set; } = 1500;

        /// <summary>Seuil MPM : on retient le premier maximum-clé atteignant <c>Cutoff</c> × le plus haut.
        /// Plus bas = préférence plus marquée pour la période courte, donc pour l'octave du haut.</summary>
        public double Cutoff { get; set; } = 0.88;

        /// <summary>En dessous de cette clarté NSDF, la fenêtre est jugée non voisée : la fréquence n'est
        /// pas mise à jour (on garde la dernière) au lieu d'inventer une valeur.</summary>
        public double ClarityMin { get; set; } = 0.45;

        /// <summary>Niveau RMS en dessous duquel on ne cherche même pas (silence).</summary>
        public double SilenceThreshold { get; set; } = 0.004;

        /// <summary>Écart maximal, en demi-tons, accepté d'un coup par rapport à la dernière hauteur retenue
        /// (0 = aucune limite). Le mode poursuite interdit déjà les sauts TANT QU'ON EST ACCROCHÉ ; cette
        /// garde couvre le seul trou restant, la ré-acquisition après une perte d'accroche, qui repart en
        /// recherche complète. Un saut réel n'est pas perdu pour autant : s'il se confirme sur
        /// <see cref="LeapConfirmFrames"/> analyses consécutives, il est accepté.</summary>
        public double MaxLeapSemitones { get; set; } = 12;

        /// <summary>Nombre d'analyses consécutives au bout desquelles un grand saut est admis comme réel.
        /// Trop bas = la garde ne sert à rien ; trop haut = un vrai bond de registre traîne.</summary>
        public int LeapConfirmFrames { get; set; } = 3;

        /// <summary>Garde anti-sous-harmonique, 0..1 (0 = désactivée). À l'acquisition, si la période MOITIÉ
        /// (donc l'octave au-dessus) est au moins aussi claire que ce facteur, c'est elle la vraie
        /// fondamentale : verrouiller sur la période double est l'erreur d'octave basse classique, provoquée
        /// par une résonance de corps ou une corde qui vibre par sympathie. 0,80 est prudent, 0,65 agressif.
        /// Sans effet en poursuite, où la période moitié est de toute façon hors de la fenêtre de recherche.</summary>
        public double SubHarmonicGuard { get; set; } = 0.80;

        /// <summary>Constante de glissement vers la nouvelle fréquence, en secondes. Court = suit le vibrato
        /// au plus près ; long = timbre plus stable mais le portamento traîne.</summary>
        public double GlideSeconds { get; set; } = 0.015;

        /// <summary>Fréquence lissée courante, en Hz. 0 tant qu'aucune analyse n'a abouti.</summary>
        public double Frequency => _smoothed;

        /// <summary>Clarté de la dernière analyse (0..1) — utile à un plugin pour doser un effet selon la
        /// confiance, ou pour afficher un témoin.</summary>
        public double Clarity => _clarity;

        /// <summary>Vrai tant que la dernière analyse a abouti (signal voisé et clair).</summary>
        public bool Voiced => _voiced;

        /// <summary>Niveau RMS de la dernière fenêtre analysée.</summary>
        public double Level => _level;

        /// <summary>Taille de la fenêtre d'analyse, en échantillons.</summary>
        public int Window => _win;

        /// <summary>Nombre d'échantillons entre deux analyses.</summary>
        public int Hop => _hop;

        int _sr = 48000, _win = 1024, _hop = 512;
        float[] _ring, _buf;
        double[] _nsdf;
        int _pos, _fill, _since;
        double _raw, _smoothed, _clarity, _level;
        bool _voiced;
        int _lockedLag;          // 0 = pas d'accroche : la prochaine analyse balaie toute la plage
        double _lastGoodHz;      // dernière hauteur retenue, référence de la garde anti-saut
        int _leapRejects;        // analyses consécutives rejetées pour cause de saut trop grand

        /// <summary>Alloue les tampons. <paramref name="window"/> doit valoir au moins deux fois la période
        /// la plus longue recherchée (une période ne se reconnaît pas sur moins de deux périodes).</summary>
        public void Prepare(int sampleRate, int window = 1024, int hop = 512)
        {
            _sr = Math.Max(8000, sampleRate);
            _win = Math.Max(256, window);
            _hop = Math.Max(64, Math.Min(hop, _win));
            _ring = new float[_win];
            _buf = new float[_win];
            _nsdf = new double[_win];
            Reset();
        }

        public void Reset()
        {
            if (_ring != null) Array.Clear(_ring, 0, _ring.Length);
            _pos = _fill = _since = 0;
            _raw = _smoothed = 0;
            _clarity = _level = 0;
            _voiced = false;
            _lockedLag = 0;
            _lastGoodHz = 0;
            _leapRejects = 0;
        }

        /// <summary>Consomme un échantillon (mono) et renvoie la fréquence lissée courante, en Hz.
        /// À appeler pour CHAQUE échantillon : c'est aussi ce qui fait avancer le glissement.</summary>
        public double Push(float mono)
        {
            if (_ring == null) return 0;

            _ring[_pos] = mono;
            _pos++; if (_pos >= _win) _pos = 0;
            if (_fill < _win) _fill++;
            if (++_since >= _hop && _fill >= _win) { _since = 0; Analyze(); }

            // Glissement exponentiel vers la dernière fréquence analysée : c'est lui qui transforme une
            // suite d'analyses discrètes en une hauteur continue, sans marche d'escalier audible.
            if (_raw > 0)
            {
                if (_smoothed <= 0) _smoothed = _raw;   // première accroche : pas de glissando depuis zéro
                else
                {
                    double a = 1.0 - Math.Exp(-1.0 / Math.Max(1e-4, GlideSeconds) / _sr);
                    _smoothed += (_raw - _smoothed) * a;
                }
            }
            return _smoothed;
        }

        /// <summary>NSDF pour UN retard, calculée à la demande sur la fenêtre courante. Sert à la garde
        /// d'octave, qui doit sonder des retards hors de la fenêtre de recherche balayée.</summary>
        double NsdfAt(int tau)
        {
            if (tau < 1 || tau >= _win) return 0;
            double ac = 0, m = 0;
            int lim = _win - tau;
            for (int i = 0; i < lim; i++)
            {
                double a = _buf[i], b = _buf[i + tau];
                ac += a * b;
                m += a * a + b * b;
            }
            return m > 1e-12 ? 2 * ac / m : 0;
        }

        void Analyze()
        {
            // Désentrelace la fenêtre glissante dans un tampon linéaire et mesure son niveau.
            double energy = 0;
            for (int i = 0; i < _win; i++)
            {
                float v = _ring[(_pos + i) % _win];
                _buf[i] = v;
                energy += v * v;
            }
            _level = Math.Sqrt(energy / _win);
            // Le silence remet la garde anti-saut à zéro : après une pause, n'importe quelle hauteur est
            // légitime — c'est ce qui empêche la garde d'enfermer le jeu dans un registre.
            if (_level < SilenceThreshold) { _voiced = false; _lockedLag = 0; _lastGoodHz = 0; _leapRejects = 0; return; }

            int maxLag = Math.Min(_win - 1, (int)(_sr / Math.Max(20.0, MinFrequency)));
            int minLag = Math.Max(2, (int)(_sr / Math.Max(MinFrequency + 1, MaxFrequency)));
            if (minLag >= maxLag) { _voiced = false; return; }

            // Mode poursuite : une fois accroché, la fondamentale ne saute pas d'une fenêtre à l'autre.
            // Chercher dans un voisinage du retard précédent coûte cinq fois moins cher ET écarte d'office
            // les octaves parasites, qui sont loin du retard courant.
            bool fullSearch = _lockedLag <= 0;
            int lo = minLag, hi = maxLag;
            if (_lockedLag > 0)
            {
                lo = Math.Max(minLag, (int)(_lockedLag * 0.78));
                hi = Math.Min(maxLag, (int)(_lockedLag * 1.28) + 1);
                if (hi - lo < 4) { lo = minLag; hi = maxLag; }
            }

            for (int tau = lo; tau <= hi; tau++)
            {
                double ac = 0, m = 0;
                int lim = _win - tau;
                for (int i = 0; i < lim; i++)
                {
                    double a = _buf[i], b = _buf[i + tau];
                    ac += a * b;
                    m += a * a + b * b;
                }
                _nsdf[tau] = m > 1e-12 ? 2 * ac / m : 0;
            }

            // Maxima-clés : le sommet de chaque lobe positif. On retient le PREMIER qui atteint
            // Cutoff × le plus haut — donc la période la plus courte crédible, ce qui écarte la période
            // double (l'erreur d'octave basse).
            // Premier passage : la hauteur du plus haut sommet, qui fixe le seuil d'acceptation.
            int lag = lo, chosen = -1;
            double highest = 0;
            while (lag <= hi)
            {
                if (_nsdf[lag] > 0)
                {
                    double posVal = _nsdf[lag];
                    while (lag <= hi && _nsdf[lag] > 0) { if (_nsdf[lag] > posVal) posVal = _nsdf[lag]; lag++; }
                    if (posVal > highest) highest = posVal;
                }
                else lag++;
            }
            if (highest < ClarityMin) { _voiced = false; _lockedLag = 0; return; }

            // Second passage : le PREMIER sommet qui atteint le seuil — donc la période la plus courte
            // crédible, ce qui écarte la période double.
            double cut = Cutoff * highest;
            lag = lo;
            while (lag <= hi && chosen < 0)
            {
                if (_nsdf[lag] > 0)
                {
                    int posMax = lag; double posVal = _nsdf[lag];
                    while (lag <= hi && _nsdf[lag] > 0) { if (_nsdf[lag] > posVal) { posVal = _nsdf[lag]; posMax = lag; } lag++; }
                    if (posVal >= cut) chosen = posMax;
                }
                else lag++;
            }
            if (chosen < 0) { _voiced = false; _lockedLag = 0; return; }

            // Garde d'octave. Elle vaut dans LES DEUX modes, et c'est essentiel : un signal à 660 Hz est
            // aussi périodique sur la période de 330 Hz, donc la fenêtre de poursuite retrouve un excellent
            // pic à l'ancienne période et ne décrocherait JAMAIS d'un saut d'octave vers le haut. On évalue
            // donc explicitement la période MOITIÉ, même hors fenêtre de recherche (une seule corrélation,
            // négligeable), et on la préfère si elle est presque aussi auto-similaire.
            // Le biais est volontairement à sens unique — vers la période courte : préférer la période
            // longue, ce serait exactement l'erreur d'octave basse qu'on cherche à éviter.
            if (SubHarmonicGuard > 0)
            {
                int half = chosen / 2;
                while (half >= minLag && NsdfAt(half) >= SubHarmonicGuard * NsdfAt(chosen))
                {
                    chosen = half;
                    half = chosen / 2;
                }
            }

            // Interpolation parabolique du sommet : la précision ne dépend plus du pas d'échantillon.
            // Voisins calculés à la demande : après la garde d'octave, `chosen` peut être hors de la
            // fenêtre qui vient d'être balayée.
            double y0 = NsdfAt(chosen - 1), y1 = NsdfAt(chosen), y2 = NsdfAt(chosen + 1);
            double denom = y0 - 2 * y1 + y2;
            double shift = Math.Abs(denom) > 1e-9 ? 0.5 * (y0 - y2) / denom : 0;
            if (shift < -1 || shift > 1) shift = 0;

            double f = _sr / (chosen + shift);

            // Garde anti-saut : un écart énorme d'une analyse à l'autre est presque toujours une erreur.
            // On le refuse... jusqu'à ce qu'il se répète, auquel cas c'est que le jeu a vraiment bondi.
            if (MaxLeapSemitones > 0 && _lastGoodHz > 0
                && Math.Abs(12.0 * Math.Log(f / _lastGoodHz, 2)) > MaxLeapSemitones
                && ++_leapRejects < Math.Max(1, LeapConfirmFrames))
            {
                _voiced = false;    // la hauteur précédente reste en vigueur, aucun saut n'est propagé
                return;
            }

            _leapRejects = 0;
            _clarity = y1;
            _voiced = true;
            _lockedLag = chosen;
            _lastGoodHz = f;
            _raw = f;
        }
    }
}
