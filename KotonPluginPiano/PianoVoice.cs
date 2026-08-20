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

        // Filtres dans la boucle
        float _lpPrev;            // LP moyen classique KS
        float _tonePrev;          // LP variable (Brightness)
        float _apPrevIn, _apPrevOut;  // All-pass 1er ordre (Inharmonicity)

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
            _size = Math.Max(4, Math.Min(_buf.Length, (int)Math.Round(_sr / freq)));

            // === EXCITATION "MARTEAU FEUTRE" (pas pluck) =========================================
            // Un vrai piano n'est PAS excite par une impulsion instantanee (comme un pluck) : le
            // marteau reste en CONTACT avec la corde pendant 2-6 ms (feutre dur -> court, mou -> long)
            // et applique une FORCE en cloche pendant ce temps. Consequences audibles :
            //   1. Le contenu spectral de l'excitation est FORTEMENT low-pass (le contact prolonge =
            //      filtre naturel), donc peu d'aigus dans l'attaque = son "mat" pas "clic".
            //   2. L'energie n'est pas repartie uniformement sur la periode fondamentale : elle est
            //      CONCENTREE dans les premiers samples (le contact marteau) puis decroit vite.
            //   3. Pas de comb pluck-position — le marteau distribue l'energie sur une largeur
            //      physique de contact, pas un point comme un mediator.
            //
            // Implementation :
            //  a) LP FORT sur le bruit d'excitation (cutoff 300-1500 Hz selon hardness × velocity).
            //  b) Enveloppe temporelle EXPONENTIELLE decroissante appliquee au buffer (temps de
            //     decroissance principal 15-40 ms) → concentre l'energie dans l'attaque.
            //  c) Petit fade-in de contact 1-3 ms au tout debut (le marteau touche puis appuie) pour
            //     supprimer le "click" numerique du bruit blanc brut.

            float hardnessEff = p.HammerHardness * 0.5f + 0.3f + velocity * 0.2f;  // 0.3..1.0

            // (a) Bruit blanc filtre LP fort : cutoff bas = feutre mou = attaque mate
            float lpCutoff = 300f + hardnessEff * 1500f;    // 300 Hz (mou) .. 1800 Hz (dur)
            float lpAlpha = 1f - (float)Math.Exp(-2.0 * Math.PI * lpCutoff / _sr);
            float lp = 0f;
            for (int i = 0; i < _size; i++)
            {
                float raw = (float)(rng.NextDouble() * 2.0 - 1.0);
                lp += lpAlpha * (raw - lp);
                _buf[i] = lp;
            }

            // (b) Enveloppe temporelle : decay exponentiel principal ~15-40 ms selon hardness
            //     (mou = decay plus lent = plus rond, dur = court = plus percussif)
            float decayMs = 40f - hardnessEff * 25f;        // 15..40 ms
            float decayRate = (float)Math.Exp(-1.0 / (decayMs * _sr / 1000.0));
            float env = 1f;
            for (int i = 0; i < _size; i++)
            {
                _buf[i] *= env;
                env *= decayRate;
            }

            // (c) Fade-in de contact 1-3 ms (evite le "tic" numerique du sample 0)
            int fadeInSamples = Math.Max(4, (int)(2.0 * _sr / 1000.0));
            for (int i = 0; i < Math.Min(fadeInSamples, _size); i++)
                _buf[i] *= (float)Math.Sin(Math.PI * 0.5 * i / fadeInSamples);

            // Normalisation + velocity
            float peak = 0.001f;
            for (int i = 0; i < _size; i++) peak = Math.Max(peak, Math.Abs(_buf[i]));
            float gain = velocity / peak;
            for (int i = 0; i < _size; i++) _buf[i] *= gain;

            _writeIdx = 0;
            _lpPrev = _tonePrev = _apPrevIn = _apPrevOut = 0f;
            _damperGain = 1f;
            _active = true;
        }

        /// <summary>Damper : touche relachee → chute rapide de la corde (multiplicateur de sortie qui
        /// tombe exponentiellement). Sans effet si sustain pedal actif (le plugin gate cet appel).</summary>
        public void EngageDamper(float damperTime)
        {
            if (!_active) return;
            // Rien a faire ici — le decrement du _damperGain se fait au fil du RenderSample via un
            // taux calcule depuis damperTime. On met simplement un flag :
            _damperActive = true;
            _damperRate = (float)Math.Exp(-6.9 / Math.Max(50, damperTime * _sr));  // -60 dB en damperTime * SR samples
        }

        bool _damperActive;
        float _damperRate = 1f;

        public void Kill()
        {
            _active = false;
            _damperActive = false;
            _damperGain = 1f;
            Array.Clear(_buf, 0, _buf.Length);
        }

        public float RenderSample(in PianoParams p)
        {
            if (!_active) return 0f;

            float sample = _buf[_writeIdx];

            // 1) LP moyen KS module par Brightness
            float harmAlpha = 0.5f + p.Brightness * 0.4f;   // 0.5..0.9
            float lp = harmAlpha * sample + (1f - harmAlpha) * _lpPrev;
            _lpPrev = sample;

            // 2) LP variable "tone" : legerement mordant, calme les tres hauts partiels
            float toneHz = 3000f + p.Brightness * 5000f;
            float toneCoef = 1f - (float)Math.Exp(-2.0 * Math.PI * toneHz / _sr);
            _tonePrev += toneCoef * (lp - _tonePrev);
            float toned = _tonePrev;

            // 3) All-pass Inharmonicity — signature du piano (les partiels ne sont pas des multiples entiers)
            float a = p.Inharmonicity * 0.6f;
            float apOut = a * toned + _apPrevIn - a * _apPrevOut;
            _apPrevIn = toned;
            _apPrevOut = apOut;

            // 4) Feedback avec decay naturel (une note piano tient 5-20 s selon note)
            // gBase = 0.998 (tres long) car sans damper le piano soutient longtemps
            // gEff compense pour la longueur du delay (aigus sinon meurent trop vite)
            float gEff = (float)Math.Pow(0.9975f, _size / 1000.0);
            float outValue = apOut * gEff;

            _buf[_writeIdx] = outValue;
            _writeIdx++;
            if (_writeIdx >= _size) _writeIdx = 0;

            // Damper : coupe rapide si touche relachee (sauf sustain pedal)
            if (_damperActive)
            {
                _damperGain *= _damperRate;
                if (_damperGain < 1e-4f) { _active = false; return 0f; }
            }

            return sample * _damperGain;
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

            // Somme des cordes
            float sum = 0f;
            int aliveStrings = 0;
            for (int i = 0; i < _stringCount; i++)
            {
                if (_strings[i].IsActive)
                {
                    sum += _strings[i].RenderSample(p);
                    aliveStrings++;
                }
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
