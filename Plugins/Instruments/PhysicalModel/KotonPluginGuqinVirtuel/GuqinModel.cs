using System;
using System.Collections.Generic;

namespace KotonPluginGuqinVirtuel
{
    /// <summary>
    /// Modèle logique du guqin : 7 cordes + 13 hui + résolveur MIDI ↔ (corde, position).
    ///
    /// **Convention position** : `p ∈ [0, 1]`, mesuré depuis le yueshan (chevalet supérieur où les
    /// cordes sont tendues). Le musicien plaque au bord opposé (bridge). Longueur vibrante d'une
    /// note stoppée à `p` = `(1-p) × diapason`. Facteur de fréquence par rapport à la corde à vide
    /// = `1 / (1-p)`. Donc `hui 7` (p=1/2) → octave, `hui 5` (p=1/3) → quinte, etc.
    ///
    /// **Hui** (13 marqueurs nacre correspondant à des nœuds harmoniques). Positions :
    ///   1: 1/8, 2: 1/6, 3: 1/5, 4: 1/4, 5: 1/3, 6: 2/5, 7: 1/2, 8: 3/5, 9: 2/3, 10: 3/4,
    ///   11: 4/5, 12: 5/6, 13: 7/8.
    /// Plus proches des extrémités = espacement physique plus petit. C'est pourquoi on raisonne en
    /// distance physique (cm) et pas en index de hui pour la contrainte d'empan.
    ///
    /// **Accordages** (tuning en pitch class ou en MIDI pour la corde à vide) : commence par
    /// « zhengdiao » 5-6-1-2-3-5-6 (le plus courant, base F/D/G majeur selon la fondamentale).
    /// </summary>
    public static class GuqinModel
    {
        public const int StringCount = 7;
        public const int HuiCount = 13;

        /// <summary>Ratios des 13 hui depuis le yueshan (fractions de la longueur totale).</summary>
        public static readonly double[] HuiPositions =
        {
            1.0/8, 1.0/6, 1.0/5, 1.0/4, 1.0/3, 2.0/5, 1.0/2,
            3.0/5, 2.0/3, 3.0/4, 4.0/5, 5.0/6, 7.0/8,
        };

        /// <summary>Accordages préréglés — MIDI des 7 cordes à vide, corde 1 = grave, corde 7 = aigu.</summary>
        public sealed class Tuning
        {
            public string Name { get; }
            public int[] OpenMidi { get; }
            public Tuning(string name, int[] openMidi) { Name = name; OpenMidi = openMidi; }
        }

        public static readonly Tuning ZhengdiaoC = new Tuning(
            "Zhengdiao en C (5-6-1-2-3-5-6)",
            // Sol1 La1 Do2 Re2 Mi2 Sol2 La2 dans une base F/C — pentatonique majeure classique
            new[] { 43, 45, 48, 50, 52, 55, 57 });

        public static readonly Tuning ZhengdiaoF = new Tuning(
            "Zhengdiao en F (5-6-1-2-3-5-6)",
            // Décale d'un ton : le guqin "en F" est aussi très courant
            new[] { 41, 43, 46, 48, 50, 53, 55 });

        public static readonly Tuning Manjiao = new Tuning(
            "Manjiao (baisse corde 3)",
            new[] { 43, 45, 47, 50, 52, 55, 57 });

        public static readonly Tuning Ruibin = new Tuning(
            "Ruibin (monte corde 5)",
            new[] { 43, 45, 48, 50, 53, 55, 57 });

        public static readonly Tuning[] AllTunings = { ZhengdiaoC, ZhengdiaoF, Manjiao, Ruibin };

        // -----------------------------------------------------------------------------------------
        // Résolveur pitch ↔ position
        // -----------------------------------------------------------------------------------------

        /// <summary>Un fingering candidat : (corde, position sur la corde, note MIDI produite).</summary>
        public struct Fingering
        {
            public int StringIdx;      // 0..6
            public double Position;    // 0 = open ; sinon = ratio dans (0, 7/8]
            public int Midi;
            public bool IsOpen => Position <= 1e-6;
        }

        /// <summary>MIDI produit en stoppant `stringIdx` à `position` (p=0 = corde à vide).</summary>
        public static int MidiFor(Tuning tuning, int stringIdx, double position)
        {
            if (stringIdx < 0 || stringIdx >= StringCount) return -1;
            int openMidi = tuning.OpenMidi[stringIdx];
            if (position <= 1e-6) return openMidi;
            // Facteur de fréquence = 1 / (1 - position). Semitons = 12 × log2(facteur).
            double vibratingRatio = 1.0 - position;
            if (vibratingRatio <= 0.01) vibratingRatio = 0.01;   // évite div/0 aux extrémités
            double semitones = -12.0 * Math.Log(vibratingRatio, 2);
            return (int)Math.Round(openMidi + semitones);
        }

        /// <summary>Génère TOUS les fingerings possibles (7 open + 91 hui = 98 candidats) et filtre
        /// les doublons de MIDI (garde le premier). L'appelant peut ensuite chercher le fingering
        /// pour un MIDI donné.</summary>
        public static List<Fingering> AllFingerings(Tuning tuning)
        {
            var list = new List<Fingering>();
            for (int s = 0; s < StringCount; s++)
            {
                list.Add(new Fingering { StringIdx = s, Position = 0, Midi = tuning.OpenMidi[s] });
                for (int h = 0; h < HuiPositions.Length; h++)
                {
                    double p = HuiPositions[h];
                    int m = MidiFor(tuning, s, p);
                    if (m > 0 && m <= 127) list.Add(new Fingering { StringIdx = s, Position = p, Midi = m });
                }
            }
            return list;
        }

        /// <summary>Cherche le meilleur fingering pour un MIDI cible :
        ///   1) match exact préféré (même MIDI)
        ///   2) parmi les matches exacts, préfère la position PROCHE DU CENTRE (hui 7) — position
        ///      confortable pour la main. Fallback : le plus proche en MIDI (peut être off).
        /// Retourne aussi si le match est exact.</summary>
        public static Fingering ResolveMidi(Tuning tuning, int targetMidi, out bool exact)
        {
            var all = AllFingerings(tuning);
            Fingering best = default;
            int bestDist = int.MaxValue;
            double bestCenterDist = double.MaxValue;
            foreach (var f in all)
            {
                int d = Math.Abs(f.Midi - targetMidi);
                if (d > bestDist) continue;
                double cd = Math.Abs(f.Position - 0.5);
                if (d < bestDist || cd < bestCenterDist)
                {
                    bestDist = d; bestCenterDist = cd; best = f;
                }
            }
            exact = bestDist == 0;
            return best;
        }
    }
}
