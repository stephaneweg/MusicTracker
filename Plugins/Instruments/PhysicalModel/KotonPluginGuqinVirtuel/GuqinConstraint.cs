using System;
using System.Collections.Generic;

namespace KotonPluginGuqinVirtuel
{
    /// <summary>
    /// État de « ce qui est joué en ce moment » sur le guqin virtuel : quelles cordes sont tenues,
    /// à quelles positions, par quels doigts.  Vérifie chaque `NoteOn` contre les règles :
    ///   1. Une note par corde (physiquement une corde ne peut vibrer qu'à une hauteur)
    ///   2. Max N doigts stoppés simultanément (par défaut 4, jusqu'à 5)
    ///   3. Empan max : tous les doigts stoppés dans un intervalle ≤ MaxSpanCm sur l'axe corde
    ///   4. Les cordes à vide (position 0) ne consomment PAS de budget doigts et ne comptent pas
    ///      dans l'empan.
    ///
    /// Décision : `Allow` = OK, joue la note ; `StealOldest` = trop de doigts, on libère la plus
    /// vieille note stoppée ; `Reject` = la contrainte physique refuse, note ignorée (rare —
    /// arrive quand la nouvelle note serait sur une corde déjà occupée sans possibilité de vol).
    /// </summary>
    public sealed class GuqinConstraint
    {
        public enum Decision
        {
            Allow,
            StealOldest,
            RejectStringBusy,   // corde déjà tenue par une autre note et pas de vol permis
        }

        /// <summary>Une note active sur le guqin (une corde donnée à une position donnée).</summary>
        public sealed class Held
        {
            public int Midi;
            public int StringIdx;
            public double Position;    // 0 = corde à vide
            public long StruckOrder;   // pour identifier la plus vieille au voice-stealing
            public bool IsOpen => Position <= 1e-6;
        }

        readonly List<Held> _held = new List<Held>();
        long _counter;

        /// <summary>Longueur du diapason en cm — sert à convertir position (0..1) en cm réels.</summary>
        public double DiapasonCm { get; set; } = 110.0;

        /// <summary>Empan max des doigts stoppés en cm.</summary>
        public double MaxSpanCm { get; set; } = 15.0;

        /// <summary>Max doigts stoppés (4 = pouce + 3 doigts, standard ; jusqu'à 5).</summary>
        public int MaxStoppedFingers { get; set; } = 4;

        public IReadOnlyList<Held> Active => _held;

        public double PositionCm(double p) => p * DiapasonCm;

        /// <summary>Décide si l'on peut jouer un `NoteOn` à `(stringIdx, position)`. Retourne aussi
        /// éventuellement la note à voler (`toRelease`). L'appelant applique la décision (kill de
        /// la note volée + play de la nouvelle).</summary>
        public Decision Consider(int stringIdx, double position, out Held toRelease)
        {
            toRelease = null;

            // Règle 1 : corde unique. Si la corde a déjà une note active, on vole (release + replay
            // sur la même corde). C'est le comportement guqin naturel : jouer une nouvelle note
            // sur une corde stoppe la précédente sur la même corde.
            for (int i = 0; i < _held.Count; i++)
            {
                if (_held[i].StringIdx == stringIdx)
                {
                    toRelease = _held[i];
                    return Decision.StealOldest;
                }
            }

            bool newIsOpen = position <= 1e-6;

            // Corde à vide : pas de contrainte doigts / empan, toujours accepter.
            if (newIsOpen) return Decision.Allow;

            // Compte les doigts stoppés actuellement + regarde l'empan si on ajoute celui-ci.
            int stopped = 0;
            double minCm = PositionCm(position);
            double maxCm = minCm;
            foreach (var h in _held)
            {
                if (h.IsOpen) continue;
                stopped++;
                double c = PositionCm(h.Position);
                if (c < minCm) minCm = c;
                if (c > maxCm) maxCm = c;
            }
            bool spanOk = (maxCm - minCm) <= MaxSpanCm;

            // Trop de doigts OU empan violé → vole le doigt stoppé le PLUS VIEUX (comportement
            // "le musicien lève un doigt pour poser un nouveau, en priorité celui qu'il tient depuis
            // le plus longtemps").
            if (stopped + 1 > MaxStoppedFingers || !spanOk)
            {
                Held oldest = null;
                foreach (var h in _held)
                    if (!h.IsOpen && (oldest == null || h.StruckOrder < oldest.StruckOrder))
                        oldest = h;
                if (oldest == null) return Decision.Allow;   // parade défensive : rien à voler → accepte
                toRelease = oldest;
                return Decision.StealOldest;
            }

            return Decision.Allow;
        }

        /// <summary>À appeler après avoir validé la décision : enregistre la nouvelle note tenue.</summary>
        public Held Register(int midi, int stringIdx, double position)
        {
            var h = new Held { Midi = midi, StringIdx = stringIdx, Position = position, StruckOrder = ++_counter };
            _held.Add(h);
            return h;
        }

        /// <summary>À appeler au NoteOff pour libérer la note.</summary>
        public void Release(int midi)
        {
            for (int i = _held.Count - 1; i >= 0; i--)
                if (_held[i].Midi == midi) { _held.RemoveAt(i); return; }
        }

        /// <summary>Libère une note précise (identifiée par le Held retourné par Register).</summary>
        public void Release(Held h)
        {
            _held.Remove(h);
        }

        public void Clear() => _held.Clear();
    }
}
