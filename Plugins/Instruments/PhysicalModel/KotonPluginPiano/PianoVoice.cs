using System;

namespace KotonPluginPiano
{
    /// <summary>Snapshot des parametres du plugin figes par buffer, passe a chaque voix.</summary>
    internal struct PianoParams
    {
        public float HammerHardness;    // 0..1 : mou (feutre neuf) → dur (feutre use, plus brillant)
        public float HammerAmount;      // 0..1 : quantite de bruit de marteau (feutre) melange a l'attaque
        public float Brightness;        // 0..1 : preserve les aigus dans la boucle (0 = mat, 1 = brillant)
        public float Inharmonicity;     // 0..1 : dispersion des partiels (all-pass), signature du piano
        public float StringDetune;      // 0..1 : quantite de detonation entre cordes d'une meme note (chorus naturel)
        public float DamperTime;        // 0..1 : vitesse d'etouffement quand la touche remonte (0=instant, 1=long)
        public float SustainPedal;      // 0/1 (ou float continu 0..1) : bloque le damper si actif
        public float Body;              // 0..1 : mix resonateur "corps" (LP + petite reverb naturelle)
        public float Reverb;            // 0..1 : mix reverb natif (Schroeder mini) — resonance de piece
        public float StereoWidth;       // 0..1 : spread stereo des cordes (leur detune L/R)
        public float VolumeDb;
    }

    /// <summary>
    /// Une SEULE corde de piano — Karplus-Strong etendu avec inharmonicite (all-pass), variable tone LP
    /// et damper mode. Plusieurs instances de cette classe sont combinees dans <see cref="PianoVoice"/>
    /// pour simuler les 1-3 cordes accordees a la meme note qu'un vrai piano possede par touche (1
    /// dans le grave, 2 medium, 3 aigu — avec micro-detune pour le chorus "bloomy" caracteristique).
    /// </summary>
    internal sealed class PianoString
    {
        readonly int _sr;
        readonly float[] _buf;
        int _writeIdx, _size;
        // Fraction de délai à lire APRÈS le sample principal (0..1) : la longueur cible du
        // KS = sr/freq n'est presque jamais entière, et les filtres de boucle (LP KS, tone LP,
        // all-pass, DC blocker) ajoutent 1 à 3 échantillons de retard de groupe. Pour garder la
        // note juste on lit à N=_size − 1 avec interpolation linéaire entre _buf[p] (retard _size)
        // et _buf[(p+1) mod _size] (retard _size − 1). Sans ça les aigus sonnent bas de 30-100 cents.
        float _fracDelay;
        // Facteur de mise à l'échelle de l'inharmonicité par la note (raideur de corde) : les
        // graves ont un B mesuré ~1e−4, les aigus ~1e−2 (Fletcher & Rossing). Le multiplicateur
        // exponentiel 2^((note−69)/24) reproduit qualitativement cette variation (×0.25 en A0,
        // ×1 en A4, ×3.1 en C8). Figé au Strike parce que la note ne change pas dans une voix,
        // et évite un Math.Pow par échantillon.
        float _inharmKeyFactor = 1f;

        // Filtres dans la boucle
        float _lpPrev;            // LP moyen classique KS
        float _tonePrev;          // LP variable (Brightness)
        float _apPrevIn, _apPrevOut;  // All-pass 1er ordre (Inharmonicity)
        // DC blocker HP 1er ordre : y[n] = x[n] - x[n-1] + R*y[n-1] (gain DC = 0, plat ailleurs).
        // INDISPENSABLE : la demi-onde sin(pi*t/T) du marteau a une valeur moyenne POSITIVE (le
        // feutre ne peut que POUSSER la corde, pas la tirer) → injecte du DC dans la boucle KS.
        // Le LP moyen KS + l'all-pass Inharmonicity ont GAIN DC = 1 (ne bloquent pas le DC), donc
        // ce DC s'accumule dans la ligne a retard entre les attaques → parasites qui montent.
        float _dcPrevIn, _dcPrevOut;

        // Etats de la voix
        bool _active;
        float _velocity;
        float _damperGain = 1f;   // multiplicateur applique quand damper actif (chute rapide)

        public bool IsActive => _active;

        public PianoString(int sampleRate)
        {
            _sr = sampleRate;
            _buf = new float[Math.Max(sampleRate / 20, 4096)];
        }

        public void Strike(int note, float velocity, float detuneCents, PianoParams p, Random rng)
        {
            _velocity = velocity;
            double freq = 440.0 * Math.Pow(2.0, (note - 69) / 12.0);
            if (detuneCents != 0f) freq *= Math.Pow(2.0, detuneCents / 1200.0);
            // Longueur cible en échantillons (fractionnaire), moins la compensation de retard de
            // groupe cumulé des filtres de boucle. La valeur 1.5 est une approximation raisonnable
            // pour la chaîne (KS-LP + tone-LP + all-pass + DC blocker) sur toute la tessiture ; à
            // affiner note-par-note si le tuning des extrêmes est encore audible. Le reste est
            // absorbé par le _fracDelay via interpolation linéaire au read.
            //
            // Convention d'indexation : _buf[p] a un age = _size échantillons (car la position sera
            // écrasée dans _size cycles), _buf[(p+1) mod _size] a age = _size − 1 (plus récent).
            // Pour lire un délai D avec _size − 1 < D <= _size on prend _size = ceil(D) et
            // _fracDelay = _size − D dans [0, 1) → interpolation entre le sample vieux (p) et le
            // sample plus récent (p+1), pondération _fracDelay sur le récent.
            double targetSamples = _sr / freq - 1.5;
            if (targetSamples < 4.0) targetSamples = 4.0;
            if (targetSamples > _buf.Length - 2) targetSamples = _buf.Length - 2;
            _size = (int)Math.Ceiling(targetSamples);
            _fracDelay = (float)(_size - targetSamples);
            _inharmKeyFactor = (float)Math.Pow(2.0, (note - 69) / 24.0);

            // === EXCITATION "MARTEAU FEUTRE" (modele Hall 1987, Extended Karplus-Strong) =========
            // La force appliquee par un marteau feutre sur la corde est une DEMI-ONDE SINUSOIDALE
            // (raised sine) de duree T = contact time (2-6 ms selon hardness x velocity), pas un
            // buffer plein de bruit. Le buffer initial est :
            //   [0..contactSamples]     : cloche marteau (F(t) = sin(pi*t/T) * amp)
            //   [contactSamples.._size] : petit bruit residuel LP-filtre (0.03..0.08) qui amorce
            //                             les partiels sans "brouiller" l'attaque marteau
            //
            // Justification :
            //  - L'attaque a un spectre naturellement LP (le contact prolonge = filtre naturel des
            //    aigus). Feutre dur = contact court = attaque brillante. Feutre mou = long = mat.
            //  - L'amplitude suit ~velocity^1.5 (loi de puissance mesuree sur vrais pianos).
            //  - Le residuel est necessaire : sans lui, le KS met plusieurs cycles a s'etablir
            //    (attaque "molle"). Avec, on obtient un demarrage franc mais toujours cohérent.

            float hardnessEff = p.HammerHardness * 0.5f + 0.3f + velocity * 0.2f;  // 0.3..1.0

            // (1) Duree de contact : 2-6 ms selon hardness (loi Hall), MISE À L'ÉCHELLE PAR LA NOTE.
            // Sur un vrai piano le marteau reste ~4-8 ms sur la corde dans le grave (A0) et moins
            // de 0.5 ms dans l'aigu (C8) — c'est ce qui donne l'attaque profonde du grave et le
            // claquement clair de l'aigu. Facteur 1 − note/128 ≈ 0.84 en A0, 0.16 en C8.
            float contactMs = 6f - hardnessEff * 4f;                               // 2..6 ms
            contactMs *= 1f - note / 128f;                                         // key scaling
            int contactSamples = Math.Max(4, (int)(contactMs * _sr / 1000f));
            if (contactSamples >= _size) contactSamples = _size - 1;

            // (2) Cloche marteau : F(t) = sin(pi*t/T) — demi-onde
            //     Amplitude ~ velocity^1.5 : loi de puissance des pianos reels
            float ampBell = (float)Math.Pow(velocity, 1.5);
            for (int i = 0; i < contactSamples; i++)
            {
                double phase = Math.PI * i / (double)contactSamples;
                _buf[i] = (float)Math.Sin(phase) * ampBell;
            }

            // (3) Residuel LP-filtre sur le reste : amorce les partiels aigus sans brouiller l'attaque.
            //     Le cutoff suit hardness (attaque brillante = residual plus riche).
            float lpCutoff = 400f + hardnessEff * 2000f;   // 400..2400 Hz
            float lpAlpha = 1f - (float)Math.Exp(-2.0 * Math.PI * lpCutoff / _sr);
            float lp = 0f;
            float residualLevel = 0.03f + velocity * 0.05f;
            for (int i = contactSamples; i < _size; i++)
            {
                float raw = (float)(rng.NextDouble() * 2.0 - 1.0);
                lp += lpAlpha * (raw - lp);
                _buf[i] = lp * residualLevel;
            }

            // (4) Fade-in de contact 1 ms (evite le "tic" numerique sur le sample 0)
            int fadeInSamples = Math.Max(4, (int)(1.0 * _sr / 1000.0));
            for (int i = 0; i < Math.Min(fadeInSamples, contactSamples); i++)
                _buf[i] *= (float)Math.Sin(Math.PI * 0.5 * i / fadeInSamples);

            // Normalisation + velocity (le peak est desormais celui de la cloche marteau)
            float peak = 0.001f;
            for (int i = 0; i < _size; i++) peak = Math.Max(peak, Math.Abs(_buf[i]));
            float gain = velocity / peak;
            for (int i = 0; i < _size; i++) _buf[i] *= gain;

            _writeIdx = 0;
            // Reset INTEGRAL de tous les etats internes — indispensable a chaque nouvelle frappe.
            // Sans ca, une voix reutilisee (voice stealing OU re-strike de la meme note apres NoteOff)
            // hertie des etats damper stale des notes precedentes → l'attaque suivante sonne
            // progressivement plus etouffee. C'est exactement le "de plus en plus etouffe" rapporte
            // par l'utilisateur.
            _lpPrev = _tonePrev = _apPrevIn = _apPrevOut = 0f;
            _dcPrevIn = _dcPrevOut = 0f;
            _damperActive = false;
            _damperMul = 1f;
            _damperLpCurrent = 6000f;
            _damperLpState = 0f;
            _active = true;
        }

        /// <summary>Damper multi-etages : sur un vrai piano, quand le damper touche la corde, les
        /// AIGUS meurent en ~30-80 ms mais la fondamentale garde une resonance residuelle 200-500 ms
        /// avant extinction complete. Modelisation :
        ///   - Un LP progressif dont le cutoff tombe de 6 kHz vers 300 Hz sur damperTime × 0.3
        ///     (etouffe les aigus rapidement)
        ///   - Un multiplicateur d'amplitude qui decroit plus lentement sur damperTime
        ///     (laisse la fondamentale s'eteindre naturellement)</summary>
        public void EngageDamper(float damperTime)
        {
            if (!_active) return;
            _damperActive = true;
            _damperMul = 1f;
            _damperLpCurrent = 6000f;
            _damperLpTarget = 300f;
            float lpRampSec = Math.Max(0.02f, damperTime * 0.3f);
            _damperLpRate = (float)Math.Exp(-1.0 / (lpRampSec * _sr));   // exponential approach to target
            _damperMulRate = (float)Math.Exp(-6.9 / Math.Max(50, damperTime * _sr));
            _damperLpState = 0f;
        }

        bool _damperActive;
        float _damperMul = 1f;
        float _damperMulRate = 1f;
        float _damperLpCurrent = 6000f;
        float _damperLpTarget = 300f;
        float _damperLpRate = 1f;
        float _damperLpState;

        public void Kill()
        {
            _active = false;
            _damperActive = false;
            _damperMul = 1f;
            _damperLpCurrent = 6000f;
            _damperLpState = 0f;
            _dcPrevIn = _dcPrevOut = 0f;
            _lpPrev = _tonePrev = _apPrevIn = _apPrevOut = 0f;
            Array.Clear(_buf, 0, _buf.Length);
        }

        /// <summary>Rend un échantillon. Le couplage inter-cordes est modélisé en CONSERVATION D'ÉNERGIE :
        /// <paramref name="couplingIn"/> = énergie reçue des autres cordes (injection dans le delay), et
        /// <paramref name="couplingDrain"/> = fraction de l'énergie sortante qu'on donne aux autres (retirée
        /// avant écriture). Ensemble ils rendent le couplage strictement neutre en gain — le gain effectif
        /// de la boucle KS reste borné par gEff, donc plus de crachottement même en unisson momentané où
        /// couplingIn peut atteindre 2·outValue en 3 cordes. Passer 0/0 = corde isolée.</summary>
        public float RenderSample(in PianoParams p, float couplingIn, float couplingDrain)
        {
            if (!_active) return 0f;

            // Interpolation linéaire pour retard fractionnaire (voir Strike : _fracDelay = ceil(N) − N).
            int nextIdx = _writeIdx + 1; if (nextIdx >= _size) nextIdx = 0;
            float sample = _buf[_writeIdx] + _fracDelay * (_buf[nextIdx] - _buf[_writeIdx]);

            // 1) LP moyen KS module par Brightness — VERSION IIR 1-pôle (au lieu du FIR 2-taps du
            //    KS classique). Le FIR laisse les aigus vivre longtemps (chaque tour de boucle
            //    n'atténue qu'un peu) → timbre trop métallique/handpan. Le pôle IIR mange les
            //    partiels aigus à chaque cycle, decay des harmoniques plus court, timbre plus
            //    "bois/piano".
            //    Brightness proche de 1 = pôle presque ouvert (LP faible) ; proche de 0 = pôle
            //    fort (LP marqué). harmAlpha = coefficient sur l'entrée : petit = beaucoup de
            //    récursion = plus de LP.
            float harmAlpha = 0.15f + p.Brightness * 0.7f;   // 0.15..0.85
            float lp = harmAlpha * sample + (1f - harmAlpha) * _lpPrev;
            _lpPrev = lp;

            // 2) LP variable "tone" : legerement mordant, calme les tres hauts partiels
            float toneHz = 3000f + p.Brightness * 5000f;
            float toneCoef = 1f - (float)Math.Exp(-2.0 * Math.PI * toneHz / _sr);
            _tonePrev += toneCoef * (lp - _tonePrev);
            float toned = _tonePrev;

            // 3) All-pass Inharmonicity — signature du piano (les partiels ne sont pas des multiples entiers)
            //    Forme standard 1er ordre : y[n] = a·x[n] + x[n-1] − a·y[n-1], pôle en z = −a.
            //    Coefficient négatif = dispersion "vers le haut" (partiels étirés au-dessus des
            //    harmoniques entiers), signature métallique du piano.
            //    Facteur 0.15 × keyFactor (au lieu d'un 0.6 uniforme) : la raideur d'une corde
            //    piano croît avec la fréquence → B ~ 1e−4 en A0, ~1e−2 en C8. keyFactor calé au
            //    Strike absorbe cette variation. Cap à 0.85 pour garder l'all-pass stable.
            float a = -Math.Min(0.85f, p.Inharmonicity * 0.15f * _inharmKeyFactor);
            float apOut = a * toned + _apPrevIn - a * _apPrevOut;
            _apPrevIn = toned;
            _apPrevOut = apOut;

            // 3b) DC BLOCKER (indispensable) : y[n] = x[n] - x[n-1] + R*y[n-1]
            //     R = 0.995 → gain DC = 0, gain plat au-dessus de ~35 Hz (a 44.1 kHz).
            //     Sans ce filtre, le DC injecte par la demi-onde du marteau s'accumule dans la
            //     boucle KS → sature progressivement → parasites qui montent.
            float dcOut = apOut - _dcPrevIn + 0.995f * _dcPrevOut;
            _dcPrevIn = apOut;
            _dcPrevOut = dcOut;

            // 4) Feedback avec decay naturel (une note piano tient 5-20 s selon note)
            // gBase = 0.998 (tres long) car sans damper le piano soutient longtemps
            // gEff compense pour la longueur du delay (aigus sinon meurent trop vite)
            float gEff = (float)Math.Pow(0.9975f, _size / 1000.0);
            float outValue = dcOut * gEff;

            // L'injection croisée arrive DANS le delay (pas dans la sortie) : elle nourrit la
            // résonance de la corde qui la fait ressortir naturellement les cycles suivants. Le
            // drain retire la fraction correspondante de l'énergie sortante pour rester en
            // conservation stricte — sinon feedback en unisson => gain > 1 => crachotements.
            _buf[_writeIdx] = outValue * (1f - couplingDrain) + couplingIn;
            _writeIdx++;
            if (_writeIdx >= _size) _writeIdx = 0;

            // Damper multi-etages : LP progressif + multiplicateur qui decroit plus lentement.
            // Sans damper actif, la sortie est le signal APRÈS toute la chaîne de boucle (outValue),
            // pas le tap brut du delay : c'est le vrai signal en train de mourir dans la corde. Idem
            // pour l'entrée du LP d'étouffement — sinon on filtrerait une version non filtrée par
            // le reste de la chaîne et le damper ferait double emploi de manière incohérente.
            if (_damperActive)
            {
                // Cutoff descend exponentiellement de 6 kHz vers 300 Hz (aigus meurent vite)
                _damperLpCurrent = _damperLpTarget + (_damperLpCurrent - _damperLpTarget) * _damperLpRate;
                float lpAlpha = 1f - (float)Math.Exp(-2.0 * Math.PI * _damperLpCurrent / _sr);
                _damperLpState += lpAlpha * (outValue - _damperLpState);
                _damperMul *= _damperMulRate;
                if (_damperMul < 1e-4f) { _active = false; return 0f; }
                return _damperLpState * _damperMul;
            }
            return outValue;
        }
    }

    /// <summary>
    /// Une voix piano = 1..3 cordes accordees a la meme note avec micro-detonation (chorus naturel).
    /// Le plugin decide combien de cordes selon le registre (comme un vrai piano). Un hammer noise est
    /// ajoute additivement a l'attaque pour recreer le "kap" du feutre qui frappe.
    /// </summary>
    internal sealed class PianoVoice
    {
        readonly int _sr;
        readonly PianoString[] _strings = new PianoString[3];
        int _stringCount;
        // Sortie précédente par corde — sert au calcul du couplage inter-cordes du cycle suivant.
        // On utilise le cycle précédent (délai 1 sample) plutôt qu'un double pass même-cycle :
        // à 1.5 % d'injection, un sample de retard est totalement inaudible et ça évite un refactor
        // "rendu séparé du write" dans PianoString.
        readonly float[] _prevOut = new float[3];
        // Cross-coupling léger (transfert d'énergie via chevalet). 0.8 % est plus prudent que le
        // 1-2 % théorique de Gemini : combiné avec un delay de 1 sample, ça donne le bloom sans
        // approcher la saturation. Le drainage compense l'injection donc c'est stable à tout K,
        // mais on garde K petit pour que l'effet reste subtil.
        const float CouplingCoef = 0.008f;

        // Hammer noise : burst additif court a l'attaque
        Random _rng;
        float _hammerEnv, _hammerRate;
        float _hammerBpLo, _hammerBpHi;   // 2 LP en cascade pour un band-pass simple
        float _hammerClickLo;             // LP separe pour le click ~250 Hz
        float _hammerAmount;              // fige au NoteOn

        bool _active;
        int _note;

        public bool IsActive => _active;
        public int Note => _note;

        public PianoVoice(int sampleRate)
        {
            _sr = sampleRate;
            for (int i = 0; i < 3; i++) _strings[i] = new PianoString(sampleRate);
        }

        static int StringsForNote(int note)
        {
            if (note < 33) return 1;   // < A2 : monocorde grave (basse enroulee)
            if (note < 57) return 2;   // A2 - A4 : deux cordes
            return 3;                  // > A4 : trois cordes (aigu)
        }

        public void NoteOn(int note, float velocity, PianoParams p, float stereoDetune)
        {
            _note = note;
            _stringCount = StringsForNote(note);
            _rng = new Random(note * 7919 + Environment.TickCount);
            float detuneAmt = 0.5f + p.StringDetune * 2.5f;   // ±0.5..±3 cents entre cordes
            // Distribution symetrique autour de 0 avec le stereo detune leger en plus
            for (int i = 0; i < _stringCount; i++)
            {
                float centered = _stringCount == 1 ? 0f
                                : _stringCount == 2 ? (i == 0 ? -detuneAmt : +detuneAmt)
                                : (i == 0 ? -detuneAmt : i == 1 ? 0f : +detuneAmt);
                _strings[i].Strike(note, velocity, centered + stereoDetune, p, _rng);
            }
            for (int i = _stringCount; i < 3; i++) _strings[i].Kill();
            // Reset du buffer de couplage inter-cordes : une voix ré-attaquée ne doit pas
            // hériter du transitoire de couplage de la note précédente.
            _prevOut[0] = _prevOut[1] = _prevOut[2] = 0f;

            // Hammer envelope : decay exponentiel ~30 ms
            _hammerAmount = p.HammerAmount * (0.5f + velocity * 0.5f);
            _hammerEnv = 1f;
            _hammerRate = (float)Math.Exp(-6.9 / (0.03 * _sr));   // -60dB en ~30ms
            _hammerBpLo = _hammerBpHi = _hammerClickLo = 0f;

            _active = true;
        }

        public void NoteOff(PianoParams p)
        {
            // Si sustain pedal active, on n'engage PAS le damper
            if (p.SustainPedal > 0.5f) return;
            // damperTime : mou dans les basses (elles resonnent naturellement plus longtemps meme apres damper)
            float damperTime = 0.08f + p.DamperTime * 0.6f;   // 80ms..680ms
            for (int i = 0; i < _stringCount; i++) _strings[i].EngageDamper(damperTime);
        }

        public void Kill()
        {
            _active = false;
            for (int i = 0; i < 3; i++) _strings[i].Kill();
        }

        public float RenderSample(in PianoParams p)
        {
            if (!_active) return 0f;

            // Somme des cordes, avec CROSS-COUPLING conservatif : chaque corde reçoit K × la somme
            // des AUTRES (via chevalet commun) ET perd K × (n−1) × son propre outValue via drainage.
            // Énergie totale conservée → boucle KS strictement stable, plus de crachotements même
            // dans les instants où deux cordes sont momentanément en phase (juste après la frappe).
            // Note : pré-calcul de prevSum et drain hors boucle (constants sur la voix).
            float prevSum = _prevOut[0] + _prevOut[1] + _prevOut[2];
            float drain = (_stringCount - 1) * CouplingCoef;
            float sum = 0f;
            int aliveStrings = 0;
            for (int i = 0; i < _stringCount; i++)
            {
                if (_strings[i].IsActive)
                {
                    float couplingIn = (prevSum - _prevOut[i]) * CouplingCoef;
                    float s = _strings[i].RenderSample(p, couplingIn, drain);
                    _prevOut[i] = s;
                    sum += s;
                    aliveStrings++;
                }
                else _prevOut[i] = 0f;
            }
            if (aliveStrings > 1) sum /= (float)Math.Sqrt(aliveStrings);   // compensation gain

            // Hammer noise additif (n'a de signal que pendant l'attaque)
            if (_hammerEnv > 1e-4f && _hammerAmount > 0.001f)
            {
                float raw = (float)(_rng.NextDouble() * 2.0 - 1.0);
                // BP par soustraction de deux LP → centre autour de 3-6 kHz selon hardness
                float hiHz = 4000f + p.HammerHardness * 4000f;
                float loHz = 1500f;
                float alphaHi = 1f - (float)Math.Exp(-2.0 * Math.PI * hiHz / _sr);
                float alphaLo = 1f - (float)Math.Exp(-2.0 * Math.PI * loHz / _sr);
                _hammerBpHi += alphaHi * (raw - _hammerBpHi);
                _hammerBpLo += alphaLo * (raw - _hammerBpLo);
                float bp = _hammerBpHi - _hammerBpLo;

                // Click ~250 Hz : LP fort sur bruit → simule le thump du marteau sur la corde
                float clickHz = 250f;
                float alphaC = 1f - (float)Math.Exp(-2.0 * Math.PI * clickHz / _sr);
                _hammerClickLo += alphaC * (raw - _hammerClickLo);

                float hammer = (bp * 0.7f + _hammerClickLo * 0.4f) * _hammerEnv * _hammerAmount * 0.5f;
                sum += hammer;
                _hammerEnv *= _hammerRate;
            }

            // Voix libre quand plus aucune corde n'est active
            if (aliveStrings == 0 && _hammerEnv < 1e-4f) _active = false;

            return sum;
        }
    }
}
