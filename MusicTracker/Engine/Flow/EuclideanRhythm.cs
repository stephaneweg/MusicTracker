using System;
using System.Collections.Generic;

namespace MusicTracker.Engine.Flow
{
    /// <summary>
    /// Rythmes euclidiens : K coups répartis aussi RÉGULIÈREMENT que possible sur N pas, notés E(K,N).
    /// C'est la structure de quantité de rythmes traditionnels — E(3,8) est le tresillo cubain, E(5,8) le
    /// cinquillo, E(7,16) un motif ouest-africain. Statique pur, sans dépendance : testable isolément.
    /// </summary>
    public static class EuclideanRhythm
    {
        /// <summary>Motif E(k,n) : true = coup. La formule (i·k) mod n &lt; k donne le même collier que la
        /// récursion de Bjorklund, sans la récursion. L'invariant qui définit la régularité maximale — au plus
        /// DEUX longueurs d'intervalle, différant de 1 — est respecté pour tout couple.</summary>
        public static bool[] Pattern(int k, int n)
        {
            n = Math.Max(1, n);
            k = Math.Max(0, Math.Min(k, n));
            var p = new bool[n];
            for (int i = 0; i < n; i++) p[i] = ((i * k) % n) < k;
            return p;
        }

        /// <summary>Fait tourner le motif de r pas (r négatif = sens inverse). Le nombre de coups est invariant.</summary>
        public static bool[] Rotate(bool[] p, int r)
        {
            if (p == null || p.Length == 0) return p;
            int n = p.Length;
            r = ((r % n) + n) % n;
            if (r == 0) return (bool[])p.Clone();
            var q = new bool[n];
            for (int i = 0; i < n; i++) q[(i + r) % n] = p[i];
            return q;
        }

        // Motifs traditionnels, en INTERVALLES cycliques : c'est la seule forme invariante par rotation, donc la
        // seule qui permette de reconnaître un motif quelle que soit sa position de départ. La formule ci-dessus
        // sort E(5,8) à deux pas du cinquillo canonique — comparer les positions brutes ne reconnaîtrait rien.
        static readonly (int k, int n, int[] gaps, string name)[] Known =
        {
            (3, 8,  new[] { 3, 3, 2 },          "tresillo"),
            (5, 8,  new[] { 2, 1, 2, 1, 2 },    "cinquillo"),
            (2, 5,  new[] { 3, 2 },             "tango"),
            (3, 4,  new[] { 1, 1, 2 },          "cumbia"),
            (4, 9,  new[] { 2, 2, 2, 3 },       "aksak"),
            (5, 12, new[] { 2, 3, 2, 2, 3 },    "venda"),
            (7, 16, new[] { 2, 3, 2, 2, 3, 2, 2 }, "samba"),
        };

        /// <summary>Nom traditionnel du motif, ou null. La comparaison se fait sur les intervalles À ROTATION PRÈS :
        /// un motif décalé reste le même rythme.</summary>
        public static string NameFor(bool[] p)
        {
            var gaps = Gaps(p);
            if (gaps == null || gaps.Count == 0) return null;
            foreach (var e in Known)
                if (e.gaps.Length == gaps.Count && IsCyclicRotation(gaps, e.gaps)) return e.name;
            return null;
        }

        /// <summary>Intervalles cycliques entre coups consécutifs (leur somme vaut la longueur du motif).</summary>
        public static List<int> Gaps(bool[] p)
        {
            var at = new List<int>();
            if (p != null) for (int i = 0; i < p.Length; i++) if (p[i]) at.Add(i);
            var g = new List<int>();
            for (int i = 0; i < at.Count; i++)
                g.Add(i < at.Count - 1 ? at[i + 1] - at[i] : at[0] + p.Length - at[i]);
            return g;
        }

        static bool IsCyclicRotation(List<int> a, int[] b)
        {
            int n = a.Count;
            for (int off = 0; off < n; off++)
            {
                bool same = true;
                for (int i = 0; i < n && same; i++) if (a[(i + off) % n] != b[i]) same = false;
                if (same) return true;
            }
            return false;
        }

        /// <summary>Au bout de combien de temps TOUS les calques d'un polyrythme retombent ensemble : le PPCM de
        /// leurs cycles (n × stepSlices), converti en temps. Partagé entre la batterie polyrythmique (PolyDrum) et
        /// la ligne mélodique polyrythmique — c'est le même calcul, seul le sens des "calques" change.</summary>
        public static double CycleBeats(IEnumerable<(int steps, int stepSlices, bool muted)> layers, int slicesPerQuarter)
        {
            long lcm = 1; bool any = false;
            foreach (var l in layers)
            {
                if (l.muted) continue;
                any = true;
                long c = Math.Max(1, l.steps) * (long)Math.Max(1, l.stepSlices);
                lcm = Lcm(lcm, c);
                if (lcm > 1L << 40) return 0;   // garde-fou : cycles absurdes
            }
            return !any || lcm <= 1 ? 0 : lcm / (double)Math.Max(1, slicesPerQuarter);
        }

        static long Gcd(long a, long b) { while (b != 0) { long t = a % b; a = b; b = t; } return a < 0 ? -a : a; }
        static long Lcm(long a, long b) { long g = Gcd(a, b); return g == 0 ? 0 : a / g * b; }

        /// <summary>Déroule le motif en notes, sur la rangée demandée (ligne de percussion, ou voix d'une ligne
        /// mélodique), jusqu'à <paramref name="totalSlices"/>. Le cycle dure n × stepSlices et se répète SANS
        /// recalage sur la mesure : c'est ce qui produit le décalage voulu quand il ne divise pas la mesure.
        /// <paramref name="legato"/> : chaque note dure jusqu'au PROCHAIN coup (au lieu d'un coup court d'une
        /// durée de pas) — la dernière note du module dure jusqu'à la fin. Sans effet sur des percussions
        /// (déclenchements ponctuels), pertinent pour une voix mélodique tenue.</summary>
        public static List<Engine.RiffNote> Build(int row, int k, int n, int rotation, int stepSlices, int totalSlices, bool legato = false)
        {
            var notes = new List<Engine.RiffNote>();
            n = Math.Max(1, n);
            stepSlices = Math.Max(1, stepSlices);
            if (row < 0 || totalSlices <= 0) return notes;

            var pat = Rotate(Pattern(k, n), rotation);
            int cycle = n * stepSlices;
            var onsets = new List<int>();
            for (int at = 0; at < totalSlices; at += cycle)
                for (int i = 0; i < n; i++)
                {
                    if (!pat[i]) continue;
                    int s = at + i * stepSlices;
                    if (s >= totalSlices) break;
                    onsets.Add(s);
                }
            for (int idx = 0; idx < onsets.Count; idx++)
            {
                int s = onsets[idx];
                int len = legato
                    ? (idx + 1 < onsets.Count ? onsets[idx + 1] - s : totalSlices - s)
                    : Math.Min(stepSlices, totalSlices - s);
                notes.Add(new Engine.RiffNote(row, s, len));
            }
            return notes;
        }
    }
}
