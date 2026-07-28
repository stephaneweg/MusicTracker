using System;
using System.Collections.Generic;

namespace MusicTracker.Engine.Flow
{
    /// <summary>
    /// Analyse d'un motif rythmique QUELCONQUE — typiquement dessiné à la main sur le cercle.
    ///
    /// Générer, c'est facile ; reconnaître, c'est ce qui manque. Quand l'utilisateur allume et éteint des points
    /// librement, il ne sait pas où il atterrit. Ces fonctions lui répondent : ce que tu viens de dessiner est-il
    /// euclidien ? équilibré ? périodique ? porte-t-il un nom ? C'est la boucle de retour qui transforme un
    /// éditeur de cercle en instrument d'apprentissage.
    ///
    /// Statique pur : chaque réponse est un calcul exact, vérifiable sans oreille.
    /// </summary>
    public static class RhythmAnalysis
    {
        /// <summary>Le motif est-il EUCLIDIEN, c'est-à-dire à répartition maximale ? Critère intrinsèque, sans
        /// régénérer E(k,n) : les intervalles entre coups consécutifs ne prennent qu'au plus DEUX valeurs, et
        /// elles diffèrent de 1. C'est la définition, et elle est invariante par rotation — un motif décalé reste
        /// euclidien, ce qu'une comparaison position par position manquerait.</summary>
        public static bool IsEuclidean(bool[] p)
        {
            var gaps = EuclideanRhythm.Gaps(p);
            if (gaps == null || gaps.Count == 0) return false;
            int min = int.MaxValue, max = int.MinValue;
            foreach (int g in gaps) { if (g < min) min = g; if (g > max) max = g; }
            return max - min <= 1;
        }

        /// <summary>Nombre de coups.</summary>
        public static int Count(bool[] p)
        {
            int k = 0;
            if (p != null) foreach (var b in p) if (b) k++;
            return k;
        }

        /// <summary>Ce qu'on peut dire d'un motif, en une passe.</summary>
        public struct Report
        {
            public int Hits, Steps;
            public bool Euclidean;      // répartition maximale
            public bool Balanced;       // centre de masse au centre du cercle
            public bool Periodic;       // se répète à l'intérieur du cycle
            public double CentroidDist; // 0 = équilibré ; sert à montrer « à quel point » c'est déséquilibré
            public string Name;         // nom traditionnel (tresillo, cinquillo…) ou null
        }

        public static Report Analyse(bool[] p)
        {
            var r = new Report
            {
                Steps = p?.Length ?? 0,
                Hits = Count(p),
                CentroidDist = BalancedRhythm.Centroid(p),
            };
            r.Balanced = r.CentroidDist <= 1e-9;
            r.Euclidean = IsEuclidean(p);
            r.Periodic = BalancedRhythm.IsPeriodic(p);
            r.Name = EuclideanRhythm.NameFor(p);
            return r;
        }

        /// <summary>Décrit le motif en une ligne, pour l'afficher sous le cercle. Volontairement en langage de
        /// musicien : on ne dit « euclidien » qu'en second, après ce que ça veut dire.</summary>
        public static string Describe(bool[] p)
        {
            var r = Analyse(p);
            if (r.Hits == 0) return Localization.Loc.T("CercleVide");
            var bits = new List<string>();
            bits.Add(r.Hits + "/" + r.Steps);
            if (r.Name != null) bits.Add("« " + r.Name + " »");
            if (r.Euclidean) bits.Add(Localization.Loc.T("RepartitionReguliere"));
            if (r.Balanced) bits.Add(Localization.Loc.T("RythmeEquilibre"));
            if (r.Periodic) bits.Add(Localization.Loc.T("SeRepete"));
            return string.Join(" · ", bits);
        }

        // ---- conversions entre les trois modes ---------------------------------------------------------------
        // Le mode libre doit pouvoir RECEVOIR ce que les deux autres produisent : on génère, puis on retouche.
        // L'inverse n'existe pas — un motif dessiné à la main n'a en général ni K/N ni décomposition en polygones.

        /// <summary>Positions des coups, forme compacte pour la persistance d'un motif libre.</summary>
        public static int[] ToPositions(bool[] p)
        {
            var l = new List<int>();
            if (p != null) for (int i = 0; i < p.Length; i++) if (p[i]) l.Add(i);
            return l.ToArray();
        }

        /// <summary>Reconstruit le motif depuis les positions (les hors-bornes sont ignorées).</summary>
        public static bool[] FromPositions(int[] pos, int steps)
        {
            var p = new bool[Math.Max(1, steps)];
            if (pos != null) foreach (int i in pos) if (i >= 0 && i < p.Length) p[i] = true;
            return p;
        }

        /// <summary>Allume / éteint un pas — le geste du mode libre. Renvoie le nouvel état du pas.</summary>
        public static bool Toggle(bool[] p, int step)
        {
            if (p == null || step < 0 || step >= p.Length) return false;
            return p[step] = !p[step];
        }

        /// <summary>Déroule un motif libre en notes, sur la rangée demandée — même contrat que
        /// <see cref="EuclideanRhythm.Build"/>, pour que les trois modes alimentent le même moteur.</summary>
        public static List<Engine.RiffNote> Build(int row, bool[] p, int stepSlices, int totalSlices, bool legato = false)
        {
            var notes = new List<Engine.RiffNote>();
            if (p == null || p.Length == 0 || row < 0 || totalSlices <= 0) return notes;
            stepSlices = Math.Max(1, stepSlices);
            int cycle = p.Length * stepSlices;

            var onsets = new List<int>();
            for (int at = 0; at < totalSlices; at += cycle)
                for (int i = 0; i < p.Length; i++)
                {
                    if (!p[i]) continue;
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
