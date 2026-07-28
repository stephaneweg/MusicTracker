using System;
using System.Collections.Generic;

namespace MusicTracker.Engine.Flow
{
    /// <summary>Un CALQUE d'une batterie polyrythmique : un instrument, et le motif euclidien E(K,N) qui le joue.
    /// Les paramètres restent VIVANTS (contrairement à une génération qui figerait le motif en liste de coups) :
    /// changer K, N ou le décalage se réentend immédiatement.</summary>
    public class EuclidLayer
    {
        public int Lane { get; set; }              // index dans DrumPattern.LaneNames (grosse caisse, caisse claire…)
        public int Hits { get; set; } = 3;         // K — combien de frappes
        public int Steps { get; set; } = 8;        // N — sur combien de positions elles se répartissent
        public int Rotation { get; set; } = 0;     // décalage du motif, en pas
        public int StepSlices { get; set; } = 12;  // durée d'un pas, sur la grille à 24 slices/noire (12 = croche)
        public bool Muted { get; set; } = false;

        public EuclidLayer Clone() => new EuclidLayer
        {
            Lane = Lane, Hits = Hits, Steps = Steps, Rotation = Rotation, StepSlices = StepSlices, Muted = Muted
        };
    }

    /// <summary>Rend une batterie polyrythmique : chaque calque déroule son propre cycle E(K,N), et les cycles de
    /// longueurs différentes se décalent les uns par rapport aux autres — c'est tout l'intérêt.</summary>
    public static class PolyDrum
    {
        /// <summary>Durées de pas proposées, exactes sur la grille à 24 slices/noire (divisible par 2, 3, 4, 6, 8, 12).</summary>
        public static readonly int[] StepSlicesChoices = { 12, 6, 8 };   // croche · double-croche · triolet de croche

        /// <summary>Longueur totale du module, en slices.</summary>
        public static int TotalSlices(PolyDrumModule m)
            => Math.Max(1, m.BeatsPerBar) * DrumPattern.SlicesPerQuarter * Math.Max(1, m.Repeats);

        /// <summary>Au bout de combien de temps TOUS les calques retombent ensemble : le vrai cycle du polyrythme.
        /// Sert à l'affichage — c'est l'information que l'oreille cherche et que les nombres seuls ne donnent pas.</summary>
        public static double CycleBeats(PolyDrumModule m)
        {
            long lcm = 1;
            if (m.Layers != null)
                foreach (var l in m.Layers)
                {
                    if (l == null || l.Muted) continue;
                    long c = Math.Max(1, l.Steps) * (long)Math.Max(1, l.StepSlices);
                    lcm = Lcm(lcm, c);
                    if (lcm > 1L << 40) return 0;               // garde-fou : cycles absurdes
                }
            return lcm <= 1 ? 0 : lcm / (double)DrumPattern.SlicesPerQuarter;
        }

        static long Gcd(long a, long b) { while (b != 0) { long t = a % b; a = b; b = t; } return a < 0 ? -a : a; }
        static long Lcm(long a, long b) { long g = Gcd(a, b); return g == 0 ? 0 : a / g * b; }

        /// <summary>Déroule tous les calques en un riff de batterie. Comme pour les autres motifs de percussion,
        /// chaque note est UN déclenchement à son début (la longueur ne sert qu'à l'édition).</summary>
        public static Riff Generate(PolyDrumModule m)
        {
            int total = TotalSlices(m);
            var slices = new SequencerSlice[total];
            if (m.Layers != null)
                foreach (var l in m.Layers)
                {
                    if (l == null || l.Muted || l.Lane < 0 || l.Lane >= DrumPattern.LaneCount) continue;
                    int row = DrumPattern.KeyForLane(l.Lane) - 12;        // ligne → note du riff (note 0 == MIDI 12)
                    if (row < 0 || row >= 96) continue;
                    foreach (var n in EuclideanRhythm.Build(0, l.Hits, l.Steps, l.Rotation, l.StepSlices, total))
                        if (n.Start < total) slices[n.Start].On(row, true);
                }
            return new Riff { Name = "PolyDrums", Slices = slices, SlicesPerQuarter = DrumPattern.SlicesPerQuarter };
        }

        /// <summary>Convertit les calques en liste de coups (ligne = rangée), pour figer le polyrythme en motif de
        /// batterie ordinaire, éditable coup par coup. L'inverse n'existe pas : on ne remonte pas de coups à des K/N.</summary>
        public static List<Engine.RiffNote> ToNotes(PolyDrumModule m)
        {
            var notes = new List<Engine.RiffNote>();
            int total = TotalSlices(m);
            if (m.Layers != null)
                foreach (var l in m.Layers)
                {
                    if (l == null || l.Muted || l.Lane < 0) continue;
                    notes.AddRange(EuclideanRhythm.Build(l.Lane, l.Hits, l.Steps, l.Rotation, l.StepSlices, total));
                }
            notes.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : a.Note.CompareTo(b.Note));
            return notes;
        }
    }
}
