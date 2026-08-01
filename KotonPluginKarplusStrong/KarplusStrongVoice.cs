using System;

namespace KotonPluginKarplusStrong
{
    /// <summary>
    /// Snapshot des paramètres du plugin figés au début d'un buffer audio, passé à chaque voix pour
    /// éviter de re-lire les <c>KotonParameter</c> par sample. Bouger un slider s'entend au prochain
    /// buffer.
    /// </summary>
    internal struct KsParams
    {
        public float Damping;           // 0..1 → amortissement dans la boucle
        public float Tone;              // 0..1 → cutoff du LP variable en série avec le filtre KS
        public float Stiffness;         // 0..1 → coefficient de l'all-pass de dispersion (piano feel)
        public float PluckHardness;     // 0..1 → mix bruit blanc / bruit filtré à l'excitation
        public float BodyMix;           // 0..1 → mix résonateur "corps" sur le signal sec
        public float StereoWidth;       // 0..1 → détune ±cents L/R + spread
        public float VolumeDb;          // -30..+6 dB
    }

    /// <summary>
    /// Une voix Karplus-Strong = une corde. Algo : excitation impulsionnelle (courte rafale de bruit
    /// filtré) injectée dans une ligne à retard de N samples (avec N ≈ SR/f), rebouclée à travers un
    /// LP passe-bas moyen (2 pôles zéro : <c>(x[n] + x[n-1])/2</c>) qui simule l'amortissement des
    /// hautes fréquences dans une vraie corde. Le rebouclage est ATTÉNUÉ par un gain de feedback lié
    /// au paramètre <see cref="KsParams.Damping"/> — plus élevé, plus la corde meurt vite.
    ///
    /// **Trois filtres dans la boucle** :
    /// 1. LP "moyen" fixe = filtre naturel KS, indispensable pour la convergence harmonique.
    /// 2. LP variable 1-pôle IIR = "Tone" contrôle la brillance (5000 Hz clair, 500 Hz sombre).
    /// 3. All-pass fractionnaire = "Stiffness" introduit de la dispersion inharmonique (piano/koto).
    ///
    /// **Excitation** : bruit blanc de taille N (une période fondamentale), filtré par un comb
    /// <c>y[n] = x[n] - x[n - Np]</c> où <c>Np = N × PluckPos</c> — supprime les partiels ayant un
    /// nœud à la position de pincement, comme sur une vraie corde. Un mix avec un LP de 1500 Hz
    /// (<see cref="KsParams.PluckHardness"/>) permet une attaque plus douce ou plus piquée.
    ///
    /// **Polyphonie** : plusieurs voix jouées en parallèle par le plugin ; chaque voix a sa propre
    /// ligne à retard. Une voix libre = <see cref="IsActive"/> false ET pas de résidu audible
    /// (silencée par <see cref="Reset"/> ou par décroissance naturelle sous le seuil de coupure).
    ///
    /// **Pas de note-off actif** : dans un vrai instrument à corde pincée, on ne mute pas la corde
    /// quand on lâche la touche — elle décroît naturellement. NoteOff est donc un no-op (le sustain
    /// est piloté par <see cref="KsParams.Damping"/>). Si on veut un "damper" style piano (touche
    /// = doigt qui étouffe), on ajoutera plus tard un mode "damped" qui accélère la fin sur note-off.
    /// </summary>
    internal sealed class KarplusStrongVoice
    {
        readonly int _sampleRate;
        readonly float[] _buffer;   // ligne à retard circulaire
        int _writeIdx;
        int _size;                   // longueur active de la ligne (en samples, entier)

        // LP variable (tone) : 1-pôle IIR
        float _tonePrev;

        // LP fixe (KS classique) : moyenne y[n] = (x[n] + x[n-1]) / 2
        float _lpPrev;

        // All-pass 1er ordre pour la dispersion (stiffness)
        float _apPrevIn, _apPrevOut;

        bool _active;
        int _note;
        float _velocity;
        float _detuneCents;   // décalage stéréo appliqué au moment du NoteOn

        // Amplitude d'énergie détectée pour couper la voix quand la corde est morte
        float _peakEnvelope;
        const float SilenceThreshold = 1e-5f;

        public bool IsActive => _active;
        public int Note => _note;

        public KarplusStrongVoice(int sampleRate)
        {
            _sampleRate = sampleRate;
            // Buffer max = SR / freq_min. On borne à MIDI 21 (A0, 27.5 Hz) → SR/27.5 ≈ 1605 samples à 44.1k.
            // On alloue confortable pour 20 Hz.
            _buffer = new float[Math.Max(sampleRate / 20, 4096)];
        }

        /// <summary>Redémarre la voix sur une nouvelle note. Excitation impulsionnelle injectée
        /// intégralement dans la ligne à retard.</summary>
        public void NoteOn(int note, float velocity, float pluckPosition, KsParams p, float detuneCents)
        {
            _note = note;
            _velocity = velocity;
            _detuneCents = detuneCents;

            double freq = 440.0 * Math.Pow(2.0, (note - 69) / 12.0);
            if (detuneCents != 0f)
                freq *= Math.Pow(2.0, detuneCents / 1200.0);
            _size = Math.Max(4, Math.Min(_buffer.Length, (int)Math.Round(_sampleRate / freq)));

            // Excitation : bruit blanc dans la ligne à retard
            var rng = new Random(note * 7919 + Environment.TickCount);
            for (int i = 0; i < _size; i++)
                _buffer[i] = (float)(rng.NextDouble() * 2.0 - 1.0);

            // Comb de position de pincement : supprime les partiels ayant un nœud à la position
            // relative pluckPosition. Formule : y[n] = x[n] - x[n - Np], avec Np = size × pluckPos.
            int np = Math.Max(1, (int)Math.Round(_size * pluckPosition));
            if (np < _size)
            {
                var scratch = new float[_size];
                for (int i = 0; i < _size; i++)
                {
                    float x = _buffer[i];
                    float xPrev = i - np >= 0 ? _buffer[i - np] : 0f;
                    scratch[i] = x - xPrev;
                }
                Array.Copy(scratch, _buffer, _size);
            }

            // Adoucir l'excitation : mix noise brut (piqué) / noise LP 1500Hz (doux) selon hardness
            if (p.PluckHardness < 0.999f)
            {
                float coef = 1f - p.PluckHardness;   // coef LP = combien on adoucit
                float alpha = 0.15f + 0.6f * coef;    // pôle du LP : plus haut = plus doux
                float lp = 0f;
                for (int i = 0; i < _size; i++)
                {
                    lp += alpha * (_buffer[i] - lp);
                    _buffer[i] = p.PluckHardness * _buffer[i] + coef * lp;
                }
            }

            // Normalisation + application vélocité
            float peak = 0.001f;
            for (int i = 0; i < _size; i++)
                peak = Math.Max(peak, Math.Abs(_buffer[i]));
            float gain = velocity / peak;
            for (int i = 0; i < _size; i++)
                _buffer[i] *= gain;

            _writeIdx = 0;
            _tonePrev = 0f;
            _lpPrev = 0f;
            _apPrevIn = 0f;
            _apPrevOut = 0f;
            _peakEnvelope = 1f;
            _active = true;
        }

        /// <summary>Coupe la voix immédiatement (utilisé par <c>Reset</c> global, pas par NoteOff).</summary>
        public void Kill()
        {
            _active = false;
            _peakEnvelope = 0f;
            Array.Clear(_buffer, 0, _buffer.Length);
        }

        /// <summary>Produit un sample mono. Le mixage stéréo et le pan sont gérés dans le plugin.
        /// La voix passe en inactive automatiquement dès que l'énergie tombe sous le seuil.</summary>
        public float RenderSample(in KsParams p)
        {
            if (!_active) return 0f;

            // Lecture au bout de la ligne (read = write, la structure est en tampon circulaire de
            // longueur _size — on lit la valeur qui va être écrasée par la prochaine écriture).
            float sample = _buffer[_writeIdx];

            // 1) Filtre LP moyen (KS classique, 2 zéros)
            float lp = 0.5f * (sample + _lpPrev);
            _lpPrev = sample;

            // 2) Filtre LP variable 1-pôle (Tone) : cutoff mappé 200..8000 Hz sur 0..1
            float toneHz = 200f + p.Tone * 7800f;
            float toneCoef = 1f - (float)Math.Exp(-2.0 * Math.PI * toneHz / _sampleRate);
            _tonePrev += toneCoef * (lp - _tonePrev);
            float toned = _tonePrev;

            // 3) All-pass 1er ordre (Stiffness) : y[n] = a*x[n] + x[n-1] - a*y[n-1]
            //    a ∈ [0..~0.7] pour rester stable et non-dispersif de plus en plus fort.
            float a = p.Stiffness * 0.7f;
            float apOut = a * toned + _apPrevIn - a * _apPrevOut;
            _apPrevIn = toned;
            _apPrevOut = apOut;

            // 4) Feedback atténué par damping. Formule : g = 0.996 - 0.045*damping.
            //    damping=0 → 0.996 (corde très longue), damping=1 → 0.951 (mort rapide).
            //    Le feedback est PROPORTIONNEL à la durée du délai — sinon un aigu meurt en une
            //    fraction de seconde. Compensation : g_eff = g^(N/1000). N=100 (aigu) → g^0.1 ≈ 0.9955
            //    (long) ; N=500 (grave) → g^0.5 ≈ 0.978 (rapide) — proche du vrai comportement de
            //    corde qui décroît en temps physique constant, pas en # de rebouclages.
            float gBase = 0.996f - p.Damping * 0.045f;
            float gEff = (float)Math.Pow(gBase, _size / 1000.0);
            float outValue = apOut * gEff;

            // Écriture dans la ligne à retard (le buffer devient x[n+_size])
            _buffer[_writeIdx] = outValue;
            _writeIdx++;
            if (_writeIdx >= _size) _writeIdx = 0;

            // Détection d'énergie pour libération auto de la voix
            float absOut = outValue < 0f ? -outValue : outValue;
            _peakEnvelope = Math.Max(_peakEnvelope * 0.9998f, absOut);
            if (_peakEnvelope < SilenceThreshold)
            {
                _active = false;
                return 0f;
            }

            return sample;   // le sample "propre" avant traitement (préserve l'attaque)
        }
    }
}
