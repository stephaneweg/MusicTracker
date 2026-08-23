using System.Collections.Generic;

namespace KotonStudio.Library
{
    /// <summary>
    /// Utility pour un plugin Koton polyphonique qui veut supporter le pitch bend PAR VOIX
    /// (<see cref="IKotonInstrument.SetNoteBend"/>). Stocke un semis-offset par MIDI-note active,
    /// expose un multiplicateur de fréquence prêt à appliquer sur la fréquence de base d'une voix.
    ///
    /// **Usage** : le plugin instancie une seule fois <c>readonly KotonNoteBends _bends = new();</c>,
    /// forwarde <c>SetNoteBend</c> à <see cref="Set"/>, appelle <see cref="Factor"/>
    /// (multiplicateur de fréquence, ex. 1.0595 pour +1 semi) à chaque calcul de fréquence de voix
    /// dans <c>Render</c>. Optionnellement, appelle <see cref="Clear"/> depuis <c>NoteOff</c> pour
    /// libérer les entrées expirées (sinon le dict grossit indéfiniment sur les longues sessions).
    ///
    /// **Thread-safety** : `SetNoteBend` peut être appelé depuis le thread UI ET depuis le thread
    /// audio ; les lectures se font depuis Render (thread audio). Un `lock` protège les accès —
    /// les fréquences audibles sont dominées par les samples eux-mêmes (44 kHz), donc quelques µs
    /// de lock par voix par bloc sont négligeables.
    /// </summary>
    public sealed class KotonNoteBends
    {
        readonly Dictionary<int, float> _semis = new Dictionary<int, float>();
        readonly object _lock = new object();

        /// <summary>Set le bend en semitons signés pour cette note MIDI. 0 = pas de bend (efface).</summary>
        public void Set(int midi, float semis)
        {
            lock (_lock)
            {
                if (semis == 0f) _semis.Remove(midi);
                else _semis[midi] = semis;
            }
        }

        /// <summary>Multiplicateur de fréquence à appliquer à la voix de cette note : <c>2^(bend/12)</c>.
        /// Retourne 1.0 si aucun bend actif (fast-path).</summary>
        public float Factor(int midi)
        {
            float s;
            lock (_lock)
            {
                if (!_semis.TryGetValue(midi, out s)) return 1f;
            }
            return s == 0f ? 1f : (float)System.Math.Pow(2.0, s / 12.0);
        }

        /// <summary>Le bend en semitons signés (0 si aucun). Utile si le plugin préfère appliquer
        /// le bend en semis à un modèle pitch-en-semis (autres synths que fréquence directe).</summary>
        public float Semis(int midi)
        {
            lock (_lock)
            {
                return _semis.TryGetValue(midi, out var s) ? s : 0f;
            }
        }

        /// <summary>Efface le bend pour cette note (à appeler depuis NoteOff pour éviter que le
        /// dict grossisse). Aucun effet si l'entrée n'existe pas.</summary>
        public void Clear(int midi) { lock (_lock) _semis.Remove(midi); }

        /// <summary>Efface tous les bends (Reset panic).</summary>
        public void ClearAll() { lock (_lock) _semis.Clear(); }
    }
}
