using System;
using System.Collections.Generic;

namespace MusicTracker.Engine.Flow
{
    /// <summary>
    /// Rythmes ÉQUILIBRÉS — à ne pas confondre avec les rythmes euclidiens, ce sont deux familles voisines mais
    /// distinctes, et un motif peut appartenir à l'une sans l'autre :
    ///
    ///   • euclidien  = les coups sont aussi ÉGALEMENT ESPACÉS que possible (régularité) ;
    ///   • équilibré  = le polygone formé par les coups a son CENTRE DE MASSE au centre du cercle (symétrie).
    ///
    /// Le tresillo E(3,8) est euclidien et NON équilibré (son centre est à 0,414). À l'inverse, un triangle plus
    /// un bipoint décalé est équilibré sans être euclidien.
    ///
    /// Résultat central (Milne &amp; Bulger, J. of Mathematics and Music, 2018) : tout rythme équilibré est une somme
    /// SIGNÉE de polygones réguliers — on en additionne, et on peut aussi en SOUSTRAIRE. Soustraire signifie
    /// musicalement qu'on entend un polygone régulier à travers les silences plutôt que dans les coups.
    ///
    /// Statique pur, sans dépendance : testable isolément, et son critère de succès est NUMÉRIQUE (centre nul),
    /// donc vérifiable sans oreille — rare pour une fonction rythmique.
    /// </summary>
    public static class BalancedRhythm
    {
        /// <summary>Distance entre le centre de masse des coups et le centre du cercle. 0 = équilibré.
        /// Un motif vide vaut 0 par convention (rien à déséquilibrer).</summary>
        public static double Centroid(bool[] p)
        {
            if (p == null || p.Length == 0) return 0;
            double x = 0, y = 0; int k = 0;
            for (int i = 0; i < p.Length; i++)
            {
                if (!p[i]) continue;
                double a = 2 * Math.PI * i / p.Length;
                x += Math.Cos(a); y += Math.Sin(a); k++;
            }
            return k == 0 ? 0 : Math.Sqrt(x * x + y * y);
        }

        /// <summary>Le motif est-il équilibré ? La tolérance absorbe l'erreur de virgule flottante des cosinus.</summary>
        public static bool IsBalanced(bool[] p, double eps = 1e-9) => Centroid(p) <= eps;

        /// <summary>Polygone régulier à k sommets sur un cercle divisé en n, tourné de <paramref name="rotation"/> pas.
        /// Exige que k divise n — sinon les sommets ne tombent pas sur la grille et le polygone n'est pas régulier.
        /// Renvoie null si k ne divise pas n.</summary>
        public static bool[] Polygon(int k, int n, int rotation = 0)
        {
            n = Math.Max(1, n);
            if (k <= 0 || k > n || n % k != 0) return null;
            var p = new bool[n];
            int stepv = n / k;
            for (int i = 0; i < k; i++) p[(((rotation + i * stepv) % n) + n) % n] = true;
            return p;
        }

        /// <summary>Les k possibles pour un cercle divisé en n : les diviseurs de n. C'est ce que l'interface doit
        /// proposer — un k qui ne divise pas n ne donne pas de polygone régulier.</summary>
        public static List<int> AllowedSides(int n)
        {
            var r = new List<int>();
            n = Math.Max(1, n);
            for (int k = 2; k <= n; k++) if (n % k == 0) r.Add(k);
            return r;
        }

        /// <summary>Un calque : un polygone régulier, tourné, ajouté (Positive) ou retiré (négatif).</summary>
        public struct SignedPolygon
        {
            public int Sides, Rotation;
            public bool Positive;
            public SignedPolygon(int sides, int rotation, bool positive) { Sides = sides; Rotation = rotation; Positive = positive; }
        }

        /// <summary>Assemble les polygones signés en un motif : les positifs posent des coups, les négatifs en
        /// retirent. Le résultat est équilibré par construction — c'est le sens du théorème — ce que
        /// <see cref="IsBalanced"/> permet de vérifier plutôt que de croire.</summary>
        public static bool[] Combine(IEnumerable<SignedPolygon> polys, int n)
        {
            n = Math.Max(1, n);
            var on = new bool[n];
            if (polys == null) return on;
            var neg = new List<bool[]>();
            foreach (var sp in polys)
            {
                var g = Polygon(sp.Sides, n, sp.Rotation);
                if (g == null) continue;                       // k ne divise pas n : calque ignoré
                if (sp.Positive) { for (int i = 0; i < n; i++) if (g[i]) on[i] = true; }
                else neg.Add(g);
            }
            foreach (var g in neg) for (int i = 0; i < n; i++) if (g[i]) on[i] = false;
            return on;
        }

        /// <summary>Un calque négatif ne peut retirer que des coups RÉELLEMENT posés. Renvoie le nombre de sommets
        /// du polygone négatif qui ne correspondent à aucun coup — 0 = soustraction propre. L'interface s'en sert
        /// pour signaler un calque négatif qui ne sert à rien.</summary>
        public static int DanglingSubtraction(IEnumerable<SignedPolygon> polys, int n, SignedPolygon negative)
        {
            var on = new bool[Math.Max(1, n)];
            if (polys != null)
                foreach (var sp in polys)
                {
                    if (!sp.Positive) continue;
                    var g = Polygon(sp.Sides, n, sp.Rotation);
                    if (g != null) for (int i = 0; i < g.Length; i++) if (g[i]) on[i] = true;
                }
            var q = Polygon(negative.Sides, n, negative.Rotation);
            if (q == null) return 0;
            int miss = 0;
            for (int i = 0; i < q.Length; i++) if (q[i] && !on[i]) miss++;
            return miss;
        }

        /// <summary>Le motif se répète-t-il à l'intérieur du cycle ? Un rythme périodique est équilibré trivialement
        /// et musicalement pauvre : c'est le même motif court joué plusieurs fois.</summary>
        public static bool IsPeriodic(bool[] p)
        {
            if (p == null || p.Length < 2) return false;
            int n = p.Length;
            for (int r = 1; r < n; r++)
            {
                if (n % r != 0) continue;                      // seules les rotations divisant n peuvent être des périodes
                bool same = true;
                for (int i = 0; i < n && same; i++) if (p[i] != p[(i + r) % n]) same = false;
                if (same) return true;
            }
            return false;
        }

        // ---- quels n valent la peine -------------------------------------------------------------------------
        // Contre-intuitif, et c'est ce qui doit être dit à l'utilisateur plutôt que caché : tous les n ne se
        // valent pas. Une puissance de 2 (16, 32, 64) ne donne QUE des rythmes périodiques.

        /// <summary>Facteurs premiers de n, avec multiplicité.</summary>
        public static List<int> PrimeFactors(int n)
        {
            var f = new List<int>();
            n = Math.Max(1, n);
            for (int d = 2; (long)d * d <= n; d++) while (n % d == 0) { f.Add(d); n /= d; }
            if (n > 1) f.Add(n);
            return f;
        }

        /// <summary>Existe-t-il des rythmes équilibrés NON PÉRIODIQUES pour cette subdivision ? Il faut au moins
        /// 3 facteurs premiers dont 2 distincts : le premier n qui convient est 12, puis 18, 20, 24, 28, 30…</summary>
        public static bool AllowsNonPeriodic(int n)
        {
            var f = PrimeFactors(n);
            if (f.Count < 3) return false;
            var distinct = new HashSet<int>(f);
            return distinct.Count >= 2;
        }

        /// <summary>Existe-t-il des rythmes équilibrés à SOMME NÉGATIVE ? Il faut 3 facteurs premiers DISTINCTS :
        /// le premier n est 30 = 2×3×5, puis 42 = 2×3×7.</summary>
        public static bool AllowsNegativeSum(int n) => new HashSet<int>(PrimeFactors(n)).Count >= 3;

        /// <summary>Ce qu'on peut espérer d'une subdivision, pour guider le choix dans l'interface.
        /// 0 = seulement des rythmes périodiques · 1 = non périodiques à somme positive · 2 = somme négative aussi.</summary>
        public static int Richness(int n) => AllowsNegativeSum(n) ? 2 : (AllowsNonPeriodic(n) ? 1 : 0);
    }
}
