using System;
using System.Collections.Generic;
using MusicTracker.Engine.Score;
using MusicTracker.Engine.Timeline;
using MusicTracker.Localization;

namespace MusicTracker.Engine.Flow
{
    /// <summary>
    /// Le VOCABULAIRE de degrés proposé par les éditeurs d'accords pour UNE tonalité : « Manuel (accord fixe) », les
    /// sept degrés diatoniques (chiffre romain dont la CASSE suit la tierce du degré), puis les DOMINANTES SECONDAIRES
    /// « V/x » de chaque degré tonicisable. Source unique de vérité partagée par l'éditeur d'accord ordinaire
    /// (<c>ChordEditorControl</c>) et l'éditeur d'accords polyrythmique (<c>PolyChordEditor</c>) : les deux proposent
    /// donc exactement les mêmes choix pour une même tonalité, PAR CONSTRUCTION et non par duplication.
    ///
    /// La liste DÉPEND DE LA TONALITÉ (casse des romains ; un degré dont la triade diatonique est diminuée ne peut pas
    /// être tonicisé, donc pas de V/ii en mineur) : une instance ne vaut que pour une tonalité, et <see cref="Matches"/>
    /// permet de détecter qu'elle a changé pour en refaire une.
    /// </summary>
    public sealed class ChordDegreeChoices
    {
        /// <summary>Index du premier « V/x » : 0 = Manuel, 1..7 = les degrés, 8.. = les dominantes secondaires.</summary>
        public const int SecondaryBase = 8;

        static readonly string[] RomanU = { "I", "II", "III", "IV", "V", "VI", "VII" };
        static readonly string[] RomanL = { "i", "ii", "iii", "iv", "v", "vi", "vii" };

        readonly KeySignature key;
        readonly string[] names;
        readonly int[] targets;      // degré tonicisé par l'entrée SecondaryBase + i

        ChordDegreeChoices(KeySignature k)
        {
            key = k;
            var n = new List<string> { Loc.T("ManuelAccordFixe") };
            for (int d = 0; d < 7; d++) n.Add(Roman(k, d));
            var t = new List<int>();
            foreach (int target in MusicTheory.SecondaryTargets)
            {
                if (MusicTheory.DiatonicIsDim(k, target)) continue;   // un degré diminué ne se tonicise pas
                n.Add("V/" + Roman(k, target));
                t.Add(target);
            }
            names = n.ToArray(); targets = t.ToArray();
        }

        public static ChordDegreeChoices For(KeySignature k) => new ChordDegreeChoices(k ?? new KeySignature());

        /// <summary>Chiffre romain d'un degré dans la tonalité, en MAJUSCULES si sa tierce diatonique est majeure.</summary>
        public static string Roman(KeySignature k, int degree)
        {
            int d = ((degree % 7) + 7) % 7;
            return MusicTheory.DiatonicThird(k ?? new KeySignature(), d) == 4 ? RomanU[d] : RomanL[d];
        }

        /// <summary>Les libellés du combo, dans l'ordre des index.</summary>
        public IReadOnlyList<string> Names => names;

        /// <summary>Les degrés tonicisables offerts, parallèles aux index &gt;= <see cref="SecondaryBase"/>.</summary>
        public IReadOnlyList<int> SecondaryTargets => targets;

        /// <summary>Vrai si cette liste vaut encore pour cette tonalité (sinon en refaire une).</summary>
        public bool Matches(KeySignature k)
        {
            var o = k ?? new KeySignature();
            return key.TonicLetter == o.TonicLetter && key.Accidental == o.Accidental
                && key.Mode == o.Mode && key.FullMode == o.FullMode;
        }

        /// <summary>Index du combo pour un accord stocké. Un accord « manuel » (degré −1) qui EST en fait une dominante
        /// secondaire se relit comme telle (V/V plutôt que Manuel).</summary>
        public int IndexOf(int degree, int rootPc, int quality)
        {
            if (degree >= 0) return Math.Min(7, degree + 1);
            int t = MusicTheory.SecondaryDominantTarget(key, rootPc, quality);
            int i = t < 0 ? -1 : Array.IndexOf(targets, t);
            return i >= 0 ? SecondaryBase + i : 0;
        }

        /// <summary>Traduit un index de combo en accord CHROMATIQUE à écrire, quand il désigne une dominante secondaire.
        /// Une V/x se stocke avec une fondamentale fixe (degré −1) à la quinte au-dessus du degré tonicisé et une
        /// qualité de DOMINANTE 7. La 7e est indispensable : une simple triade majeure coïnciderait souvent avec un
        /// accord diatonique (V/IV en Do = un Do majeur = I) et ne se relirait donc pas comme une dominante secondaire.
        /// <paramref name="quality"/> vaut −1 si la qualité « 7 (dom) » est introuvable (garder alors l'existante).
        /// Retourne false pour « Manuel » et pour les sept degrés diatoniques (rien de chromatique à poser).</summary>
        public bool TrySecondary(int index, out int rootPc, out int quality)
        {
            rootPc = 0; quality = -1;
            int i = index - SecondaryBase;
            if (i < 0 || i >= targets.Length) return false;
            rootPc = MusicTheory.SecondaryDominantRoot(key, targets[i]);
            quality = PatternGenerator.IndexOfQuality("7 (dom)");
            return true;
        }
    }

    /// <summary>
    /// Shared chord-degree helpers (UI-agnostic) used by the chord editor AND the arrangement builders: map a concrete
    /// chord back to (degree, colour, suspension, mode); recover the colour trio of a fixed chord's quality; and
    /// voice-lead a track's chord chain in place.
    /// </summary>
    public static class ChordDegrees
    {
        /// <summary>Map a generated chord (root pc + quality) to a diatonic (degree, PRIMARY colour, SUSPENSION, MODE) so an
        /// inserted chord is DEGREE-LOCKED (follows the key). Returns degree −1 (absolute) for a chromatic root or a quality
        /// that isn't a diatonic colour of its degree. Prefers AUTO mode / no suspension / lower colour.</summary>
        public static (int degree, int colour, int suspension, int mode) DegColour(KeySignature key, int rootPc, int quality)
        {
            int rpc = ((rootPc % 12) + 12) % 12;
            int deg = MusicTheory.DegreeOf(key, rpc);
            foreach (int mode in new[] { 0, 1, 2, 3, 4, 5 })
                foreach (int susp in new[] { 0, 1, 2 })
                    foreach (int col in new[] { 0, 1, 2, 3, 4 })
                    {
                        var d = MusicTheory.DiatonicChord(key, deg, col, susp, mode);
                        if (d.root == rpc && d.quality == quality) return (deg, col, susp, mode);
                    }
            return (-1, 0, 0, 0);
        }

        /// <summary>A FIXED chord = a root note built as degree I of a C-major reference; recover the (colour, suspension,
        /// mode) that yields a given quality (so the editor combos reflect it). Falls back to (triade, none, auto) for a
        /// quality the colour system can't express (rare exotic tensions).</summary>
        public static (int colour, int suspension, int mode) ColourForQuality(int quality)
        {
            var k = new KeySignature { TonicLetter = 0, Accidental = 0, Mode = 0 };
            foreach (int mode in new[] { 0, 1, 2, 3, 4, 5 })
                foreach (int susp in new[] { 0, 1, 2 })
                    foreach (int col in new[] { 0, 1, 2, 3, 4 })
                        if (MusicTheory.DiatonicChord(k, 0, col, susp, mode).quality == quality)
                            return (col, susp, mode);
            return (0, 0, 0);
        }

        /// <summary>L'INVERSE de <see cref="ColourForQuality"/> : la qualité d'un accord FIXE (degré −1) pour un trio
        /// (couleur, suspension, mode). L'accord se lit comme le degré I d'un Do majeur de référence — sa fondamentale
        /// ne bouge pas, seule la qualité suit les trois combos.</summary>
        public static int QualityForColour(int colour, int suspension, int mode)
            => MusicTheory.DiatonicChord(new KeySignature { TonicLetter = 0, Accidental = 0, Mode = 0 },
                                         0, colour, suspension, mode).quality;

        /// <summary>Voice-lead the track's CHORD CHAIN in place: each PatternGeneratorModule with VoiceLeadMode != 0 gets its
        /// Inversion (+ Octave) chosen GREEDILY from the previous chord's realized voicing. The first chord keeps its manual
        /// inversion as the seed; "off" chords keep their manual voicing but still seed the next.</summary>
        public static void Revoice(TimelineTrack track)
        {
            if (track?.Items == null) return;
            int[] prev = null; int baseOct = 4; bool haveBase = false;
            Action<PatternGeneratorModule> step = pg =>
            {
                if (pg == null) return;
                if (!haveBase) { baseOct = pg.Octave; haveBase = true; }
                if (pg.VoiceLeadMode != 0 && prev != null)
                {
                    var v = MusicTheory.VoiceLeadStep(prev, pg.Root, pg.Quality, baseOct, pg.VoiceLeadMode - 1);
                    pg.Inversion = v.inversion; pg.Octave = v.octave;
                }
                prev = PatternGenerator.ChordNotes(pg.Root, pg.Octave, pg.Quality, pg.Inversion);
            };
            foreach (var item in track.Items)
            {
                if (item == null) continue;
                if (item.Module is PatternGeneratorModule pg) step(pg);
                else if (item.Module is PolyChordModule pc)
                {
                    // Le module PolyChord porte son propre baseOctave ; on l'adopte comme base pour LUI, mais on
                    // continue la chaîne « prev » pour que le module suivant (Pattern ou PolyChord) enchaîne.
                    if (!haveBase) { baseOct = pc.Octave; haveBase = true; }
                    int localBase = pc.Octave;
                    PolyChord.RevoiceChain(pc, ref prev, ref localBase);
                }
            }
        }
    }
}
