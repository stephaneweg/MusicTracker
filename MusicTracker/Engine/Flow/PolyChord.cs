using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;

namespace MusicTracker.Engine.Flow
{
    // ===================== énumérations partagées ===============================================================

    /// <summary>Mode de fonctionnement des anneaux d'un module d'accords polyrythmique.
    /// <see cref="OneRingPerTone"/> = un anneau par NOTE de l'accord (index 0 = plus grave après voicing/voice-leading,
    /// puis 2e, 3e...). Chaque anneau ne joue QUE sa voix. <see cref="OneRingSweep"/> = chaque anneau pioche
    /// dynamiquement une note différente à chaque coup, selon un CONTOUR configurable (Vague/Montante/...).</summary>
    public enum PolyChordMode { OneRingPerTone = 0, OneRingSweep = 1 }

    /// <summary>Mode 1-anneau : au CHANGEMENT d'accord, comment choisir la note de départ dans le nouveau voicing.
    /// <see cref="Nearest"/> = note la plus proche (en semi-tons) de la dernière note jouée — parcours le plus lisse.
    /// Les 5 autres = point d'accroche musical fixe : la plus grave/aiguë, la fondamentale, la tierce, la quinte.</summary>
    public enum ChordRestartMode { Nearest = 0, Grave, Aigu, Tonic, Tierce, Quinte }

    /// <summary>Mode "monodie emergente" (MonodicPick) : strategie de selection quand plusieurs anneaux ont un
    /// onset simultane. <see cref="Highest"/>/<see cref="Lowest"/> = toujours la note la plus haute/grave du
    /// groupe (fixe). <see cref="Auto"/> = voice-leading (minimise le saut avec la note precedente + octaviage
    /// automatique) — melodie fluide. <see cref="Random"/> = tirage pseudo-aleatoire (seed).</summary>
    public enum PolyChordMonoStrategy { Highest = 0, Lowest, Auto, Random }

    // ===================== un accord de la liste ================================================================

    /// <summary>Un accord de la liste d'un <see cref="PolyChordModule"/> : mêmes champs que ceux qu'on saisit à
    /// l'ajout d'un accord classique (degré/qualité/couleur/suspension/mode), plus sa DURÉE en temps (variable —
    /// défaut = une mesure). <see cref="Inversion"/> et <see cref="OctaveShift"/> sont CALCULÉS par la passe de
    /// voice-leading (<see cref="PolyChord.RevoiceChain"/>), pas édités à la main.</summary>
    public class PolyChordItem : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        void OnChanged(string n)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
            if (n == nameof(Root) || n == nameof(Quality)) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Label)));
        }

        int root, quality, degree = -1, diatonicColour, suspension, modeOverride;
        int beats = 4;
        int inversion, octaveShift;

        public int Root { get { return root; } set { if (root != value) { root = value; OnChanged(nameof(Root)); } } }
        public int Quality { get { return quality; } set { if (quality != value) { quality = value; OnChanged(nameof(Quality)); } } }
        public int Degree { get { return degree; } set { int v = value < 0 ? -1 : Math.Min(6, value); if (degree != v) { degree = v; OnChanged(nameof(Degree)); } } }
        public int DiatonicColour { get { return diatonicColour; } set { int v = Math.Max(0, Math.Min(MusicTheory.DiatonicColourNames.Length - 1, value)); if (diatonicColour != v) { diatonicColour = v; OnChanged(nameof(DiatonicColour)); } } }
        public int Suspension { get { return suspension; } set { int v = Math.Max(0, Math.Min(MusicTheory.SuspensionNames.Length - 1, value)); if (suspension != v) { suspension = v; OnChanged(nameof(Suspension)); } } }
        public int ModeOverride { get { return modeOverride; } set { int v = Math.Max(0, Math.Min(MusicTheory.ModeOverrideNames.Length - 1, value)); if (modeOverride != v) { modeOverride = v; OnChanged(nameof(ModeOverride)); } } }

        /// <summary>Durée de CET accord, EN TEMPS (noires). Variable par accord — défaut typique = un temps par
        /// beat de la mesure (fixé à l'ajout via <c>TimelineHelper.RulerBeatsPerBar</c>).</summary>
        public int Beats { get { return beats; } set { int v = Math.Max(1, value); if (beats != v) { beats = v; OnChanged(nameof(Beats)); } } }

        /// <summary>Renversement CALCULÉ par la passe de voice-leading (jamais édité à la main).</summary>
        public int Inversion { get { return inversion; } set { int v = Math.Max(0, value); if (inversion != v) { inversion = v; OnChanged(nameof(Inversion)); } } }
        /// <summary>Décalage d'octave relatif au module (−1/0/+1) posé par le voice-leading. Comme
        /// <see cref="CadenceChord.OctaveShift"/> — sert à ce que la conduite des voix reste dans une bande de
        /// registre stable.</summary>
        public int OctaveShift { get { return octaveShift; } set { if (octaveShift != value) { octaveShift = value; OnChanged(nameof(OctaveShift)); } } }

        /// <summary>Libellé lisible « Do Maj7 » utilisé par les combos et l'affichage sur la timeline.</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public string Label
        {
            get
            {
                var qn = (Quality >= 0 && Quality < PatternGenerator.QualityNames.Length) ? PatternGenerator.QualityNames[Quality] : "?";
                var rn = MusicTracker.Engine.Score.KeySig.SpellPc(Root, null);
                return rn + " " + qn;
            }
        }

        public PolyChordItem Clone() => new PolyChordItem
        {
            root = root, quality = quality, degree = degree,
            diatonicColour = diatonicColour, suspension = suspension, modeOverride = modeOverride,
            beats = beats, inversion = inversion, octaveShift = octaveShift
        };
    }

    // ===================== un anneau ============================================================================

    /// <summary>Un anneau (calque) d'un <see cref="PolyChordModule"/>. Mêmes champs de base que
    /// <see cref="EuclidVoice"/>/<see cref="EuclidLayer"/> (Hits/Steps/Rotation, mode personnalisé, muted, legato).
    /// Deux champs spécifiques aux modes du module :
    ///   <see cref="ToneIndex"/> = quelle NOTE de l'accord jouer (mode <see cref="PolyChordMode.OneRingPerTone"/>) ;
    ///   <see cref="Contour"/>   = forme du parcours des notes (mode <see cref="PolyChordMode.OneRingSweep"/>).</summary>
    public class EuclidChordLayer : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        void OnChanged(string n)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
            if (n == nameof(Hits) || n == nameof(Steps) || n == nameof(CustomMode) || n == nameof(CustomHits) || n == nameof(Rotation))
                foreach (var d in new[] { nameof(SummaryText), nameof(AnalysisText) }) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(d));
            if (n == nameof(CustomMode))
                foreach (var d in new[] { nameof(HitsRotationVisibility), nameof(AnalysisVisibility), nameof(PersColour) }) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(d));
            if (n == nameof(Collapsed))
                foreach (var d in new[] { nameof(BodyVisibility), nameof(SummaryVisibility), nameof(AnalysisVisibility), nameof(CollapseGlyph) }) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(d));
        }

        int hits = 3, steps = 8, rotation, octave, toneIndex, contour, randomSeed;
        bool muted, collapsed, customMode, legato;
        int[] customHits;

        public int Hits { get { return hits; } set { if (hits != value) { hits = value; OnChanged(nameof(Hits)); } } }
        public int Steps { get { return steps; } set { if (steps != value) { steps = value; OnChanged(nameof(Steps)); } } }
        public int Rotation { get { return rotation; } set { if (rotation != value) { rotation = value; OnChanged(nameof(Rotation)); } } }

        /// <summary>Transposition post-hoc en OCTAVES (−3..+3). Appliqué APRÈS le voicing (n'affecte pas la
        /// conduite des voix), même contrat que <see cref="EuclidVoice.Octave"/>.</summary>
        public int Octave { get { return octave; } set { int v = Math.Max(-3, Math.Min(3, value)); if (octave != v) { octave = v; OnChanged(nameof(Octave)); } } }

        public bool Muted { get { return muted; } set { if (muted != value) { muted = value; OnChanged(nameof(Muted)); } } }
        public bool Collapsed { get { return collapsed; } set { if (collapsed != value) { collapsed = value; OnChanged(nameof(Collapsed)); } } }
        public bool Legato { get { return legato; } set { if (legato != value) { legato = value; OnChanged(nameof(Legato)); } } }
        public bool CustomMode { get { return customMode; } set { if (customMode != value) { customMode = value; OnChanged(nameof(CustomMode)); } } }
        public int[] CustomHits { get { return customHits; } set { customHits = value; OnChanged(nameof(CustomHits)); } }

        /// <summary>Mode MULTI : index de la note d'accord jouée par cet anneau (0 = la plus grave après voicing).
        /// WRAP À L'OCTAVE dans les deux sens : sur une triade [T,3,5], +3 = T à l'octave supérieure, +6 = T deux
        /// octaves plus haut ; -1 = 5 à l'octave inférieure, -2 = 3 idem, -3 = T idem, -4 = 5 encore une octave plus bas.
        /// Empilement diatonique qui suit la structure de l'accord peu importe sa taille (T-3-5-T'-3'-5'… vers le haut,
        /// …-3-5-T vers le bas). Ignoré en mode 1-anneau.</summary>
        public int ToneIndex { get { return toneIndex; } set { if (toneIndex != value) { toneIndex = value; OnChanged(nameof(ToneIndex)); } } }

        /// <summary>Mode 1-ANNEAU : forme du parcours à travers les notes de l'accord (mêmes noms que
        /// <see cref="MelodicLineModule.Contour"/>). Ignoré en mode multi.</summary>
        public int Contour { get { return contour; } set { int v = Math.Max(0, value); if (contour != v) { contour = v; OnChanged(nameof(Contour)); } } }

        /// <summary>Mode 1-anneau, contour Aléatoire : graine pour rendre le tirage reproductible.</summary>
        public int RandomSeed { get { return randomSeed; } set { if (randomSeed != value) { randomSeed = value; OnChanged(nameof(RandomSeed)); } } }

        // ---- propriétés dérivées pour le DataTemplate ----
        [System.Text.Json.Serialization.JsonIgnore] public System.Windows.Visibility BodyVisibility => Collapsed ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
        [System.Text.Json.Serialization.JsonIgnore] public System.Windows.Visibility SummaryVisibility => Collapsed ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
        [System.Text.Json.Serialization.JsonIgnore] public System.Windows.Visibility HitsRotationVisibility => CustomMode ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
        [System.Text.Json.Serialization.JsonIgnore] public System.Windows.Visibility AnalysisVisibility => (CustomMode && !Collapsed) ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
        [System.Text.Json.Serialization.JsonIgnore] public string CollapseGlyph => Collapsed ? "▸" : "▾";
        [System.Text.Json.Serialization.JsonIgnore] public System.Windows.Media.SolidColorBrush PersColour => CustomMode ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0xC1, 0x07)) : System.Windows.Media.Brushes.White;
        [System.Text.Json.Serialization.JsonIgnore]
        public string SummaryText
        {
            get
            {
                int c = CustomMode ? (CustomHits?.Length ?? 0) : Hits;
                string tag = CustomMode ? " · ✎" : "";
                return $"{c}/{Steps}{tag}";
            }
        }
        [System.Text.Json.Serialization.JsonIgnore] public string AnalysisText => CustomMode ? RhythmAnalysis.Describe(EffectivePattern()) : "";

        public EuclidChordLayer Clone() => new EuclidChordLayer
        {
            hits = hits, steps = steps, rotation = rotation, octave = octave, toneIndex = toneIndex, contour = contour, randomSeed = randomSeed,
            muted = muted, collapsed = collapsed, customMode = customMode, legato = legato,
            customHits = customHits == null ? null : (int[])customHits.Clone()
        };

        public bool[] EffectivePattern()
            => CustomMode
                ? RhythmAnalysis.FromPositions(CustomHits, Steps)
                : EuclideanRhythm.Rotate(EuclideanRhythm.Pattern(Hits, Steps), Rotation);
    }

    // ===================== moteur ===============================================================================

    /// <summary>Rend un module d'accords polyrythmique. Ce moteur combine la structure « liste d'accords »
    /// (comme <see cref="CadenceModule"/>) avec le mécanisme des anneaux euclidiens (comme
    /// <see cref="PolyDrum"/>/<see cref="MelodicEuclid"/>). Chaque accord porte sa propre durée
    /// (<see cref="PolyChordItem.Beats"/>) : les anneaux bouclent DANS l'accord courant (une cellule =
    /// <c>Beats/Steps</c> temps), puis repartent sur le voicing du suivant.
    ///
    /// Deux modes distincts, exclusifs, réglés au niveau du module :
    /// <see cref="PolyChordMode.OneRingPerTone"/> = un anneau = une note fixe du voicing (index <see cref="EuclidChordLayer.ToneIndex"/>) ;
    /// <see cref="PolyChordMode.OneRingSweep"/>   = chaque anneau pioche dynamiquement une note selon
    /// <see cref="EuclidChordLayer.Contour"/> (Vague / Montante / Descendante / Statique / Zigzag / Aléatoire),
    /// avec continuité au changement d'accord réglée par <see cref="PolyChordModule.Restart"/>.
    ///
    /// Voicing/open-voicing/voice-leading sont partagés au niveau du module (comme <see cref="CadenceModule"/>) :
    /// une passe (<see cref="RevoiceChain"/>) écrit Inversion/OctaveShift sur chaque accord de la chaîne. Cette
    /// passe se chaîne avec les voisins <see cref="PatternGeneratorModule"/> ou <see cref="PolyChordModule"/> sur
    /// la même piste, via <see cref="ChordDegrees.Revoice"/>.</summary>
    public static class PolyChord
    {
        // Même garde-fou que PolyDrum/MelodicEuclid : au-delà, on retombe sur spq=24 (arrondi audible < 10 ms).
        const int MaxSpq = 4800;

        // ---- géométrie ---------------------------------------------------------------------------------------

        /// <summary>Durée totale du module en TEMPS = somme des Beats de tous les accords. 0 si vide.</summary>
        public static double TotalBeats(PolyChordModule m)
        {
            if (m == null) return 0;
            if (m.Beats > 0) return m.Beats;              // longueur propre au module (dissociation accord/polyrythme)
            if (m.Chords == null || m.Chords.Count == 0) return 0;
            int sum = 0;                                   // ancien comportement : somme des accords internes
            foreach (var c in m.Chords) if (c != null) sum += Math.Max(1, c.Beats);
            return sum;
        }

        /// <summary>
        /// Accords que le module doit articuler sur son étendue. Le module ne DÉFINIT plus d'accords : la source
        /// normale est l'harmonie active de la piste Accords, découpée à ses frontières et convertie en items
        /// (seule la hauteur compte ici — le voicing est refait par <see cref="VoicedNotes"/>). Repli sur la liste
        /// interne quand aucun accord n'est actif (anciens fichiers, arrangements produits par l'IA), pour que le
        /// module continue de sonner au lieu de devenir muet.
        /// </summary>
        static IList<PolyChordItem> EffectiveChords(PolyChordModule m, Timeline.TimelineProject project, Func<Guid, Riff> resolve, double startBeat)
        {
            double total = TotalBeats(m);
            if (project != null && total > 0)
            {
                var segs = Timeline.ChordArticulation.Segments(project, resolve, startBeat, total);
                if (segs.Count > 0)
                {
                    var list = new List<PolyChordItem>(segs.Count);
                    foreach (var s in segs)
                        list.Add(new PolyChordItem
                        {
                            Root = s.Root, Quality = s.Quality, Inversion = s.Inversion,
                            Beats = Math.Max(1, (int)Math.Round(s.Len)),
                        });
                    return list;
                }
            }
            return m?.Chords;
        }

        /// <summary>Durée du cycle de la roue en TEMPS (défaut migration : 4). C'est le rythme des anneaux ; ne dépend
        /// PAS des durées d'accord. Un accord court fait juste basculer sur le voicing suivant à sa borne, la roue
        /// continue de tourner au même tempo.</summary>
        public static int CycleBeats(PolyChordModule m) => Math.Max(1, m == null ? 4 : m.CycleBeats);

        /// <summary>Résolution de slice : contrainte double — la cellule k d'un anneau doit tomber sur un entier
        /// (<c>k × cycleBeats × spq / Steps</c> ∈ ℤ, comme <see cref="PolyDrum.SpqFor"/>), ET les bornes d'accord
        /// aussi (satisfait dès que spq ∈ ℕ, puisque Beats est entier). Formule : <c>LCM(Steps) / gcd(cycleBeats, LCM)</c>.
        /// Plafonné par <see cref="MaxSpq"/> — au-delà, fallback spq=24 avec arrondi &lt; 10 ms.</summary>
        public static int SpqFor(PolyChordModule m)
        {
            long lcm = 1;
            if (m?.Layers != null)
                foreach (var l in m.Layers)
                {
                    if (l == null || l.Muted) continue;
                    int s = Math.Max(1, l.Steps);
                    lcm = Lcm(lcm, s);
                    if (lcm > MaxSpq) return 24;
                }
            int beats = CycleBeats(m);
            long spq = lcm / Gcd(beats, lcm);
            return (int)Math.Max(1, spq);
        }
        static long Gcd(long a, long b) { while (b != 0) { long t = a % b; a = b; b = t; } return a < 0 ? -a : a; }
        static long Lcm(long a, long b) { long g = Gcd(a, b); return g == 0 ? 0 : a / g * b; }

        /// <summary>Cherche l'accord actif à la position <paramref name="localBeat"/> (relative au début du module),
        /// et renvoie son offset de départ. Retourne <c>false</c> si le module est vide ou la position hors bornes.</summary>
        public static bool ChordAt(PolyChordModule m, double localBeat, out PolyChordItem item, out double itemStart)
        {
            item = null; itemStart = 0;
            if (m?.Chords == null || m.Chords.Count == 0) return false;
            double cur = 0;
            foreach (var c in m.Chords)
            {
                if (c == null) continue;
                double len = Math.Max(1, c.Beats);
                if (localBeat < cur + len - 1e-9)
                {
                    item = c; itemStart = cur; return true;
                }
                cur += len;
            }
            // Après la fin : on prend le dernier — la piste harmonique doit rester définie jusqu'au dernier slice.
            item = m.Chords[m.Chords.Count - 1]; itemStart = cur - Math.Max(1, item.Beats);
            return true;
        }

        // ---- voicing / voice-leading ------------------------------------------------------------------------

        /// <summary>Notes MIDI de cet accord après voicing (renversement + open voicing), triées grave→aigu.
        /// <see cref="PolyChordItem.OctaveShift"/> décale la fondamentale ; le tableau reste trié.</summary>
        public static int[] VoicedNotes(PolyChordItem it, int baseOctave, bool open)
            => PatternGenerator.ChordNotes(it.Root, baseOctave + it.OctaveShift, it.Quality, it.Inversion, open);

        /// <summary>Chaîne un voice-leading GLOUTON sur les accords du module : pour chaque accord, on choisit le
        /// couple (inversion, octaveShift) qui minimise le mouvement depuis le voicing PRÉCÉDENT. La valeur de
        /// <paramref name="prev"/> et <paramref name="baseOct"/> entrent/sortent par ref pour se chaîner avec les
        /// modules voisins sur la même piste (<see cref="ChordDegrees.Revoice"/> — même contrat que pour
        /// <see cref="PatternGeneratorModule"/>). Le premier accord d'une chaîne (prev == null) démarre en position
        /// fondamentale à l'octave de base.</summary>
        public static void RevoiceChain(PolyChordModule m, ref int[] prev, ref int baseOct)
        {
            if (m?.Chords == null || m.Chords.Count == 0) return;
            int anchor = Math.Max(0, Math.Min(2, m.VoiceLeadAnchor));
            foreach (var c in m.Chords)
            {
                if (c == null) continue;
                if (prev == null)
                {
                    // Amorçage : position fondamentale, octave du module. La note posée sert de seed au suivant.
                    c.Inversion = 0; c.OctaveShift = 0;
                }
                else
                {
                    var v = MusicTheory.VoiceLeadStep(prev, c.Root, c.Quality, baseOct, anchor);
                    c.Inversion = v.inversion; c.OctaveShift = v.octave - baseOct;
                }
                prev = VoicedNotes(c, baseOct, m.OpenVoicing);
            }
        }

        // ---- rendu -------------------------------------------------------------------------------------------

        /// <summary>Déroule le module en un riff. NOUVEAU MODÈLE : la roue tourne à VITESSE CONSTANTE, cadencée par
        /// <see cref="PolyChordModule.CycleBeats"/> — INDÉPENDAMMENT de la durée de chaque accord. Les changements
        /// d'accord ne font que basculer sur le voicing suivant à leur borne ; ils n'accélèrent ni ralentissent les
        /// notes. Longueur du module = somme des Beats des accords ; la roue est bouclée autant de fois que nécessaire
        /// pour la couvrir.
        ///
        /// Legato → une note est tenue jusqu'au prochain onset du même anneau, MAIS coupée au changement d'accord
        /// (le voicing suivant réattaque proprement).</summary>
        /// <summary>
        /// Rendu du module. Les accords ne viennent plus du module lui-même mais de l'harmonie active à sa position
        /// (<paramref name="project"/> + <paramref name="startBeat"/>) : le module ne décrit QUE le polyrythme
        /// (anneaux, cycle, durée). Sans projet fourni, on retombe sur ses accords internes (anciens fichiers).
        /// </summary>
        public static Riff Generate(PolyChordModule m, Timeline.TimelineProject project = null, Func<Guid, Riff> resolve = null, double startBeat = 0)
        {
            var outNotes = new List<RiffNote>();
            int spq = SpqFor(m);
            var chords = EffectiveChords(m, project, resolve, startBeat);
            if (chords == null || chords.Count == 0)
                return new Riff { Name = "PolyChords", Notes = outNotes, SlicesPerQuarter = spq, LengthSlices = 1 };

            // Voicing par accord (calculé une fois — Revoice écrit Inversion/OctaveShift).
            var voicedPerChord = new int[chords.Count][];
            for (int i = 0; i < chords.Count; i++)
                voicedPerChord[i] = VoicedNotes(chords[i], m.Octave, m.OpenVoicing);

            // Bornes d'accord en slices (fin exclusive de chaque accord) — cherchées par recherche linéaire dans
            // ChordIndexAt. En pratique on a peu d'accords (< 20), inutile de sortir la binary search.
            int cycleBeats = CycleBeats(m);
            int cycleSlices = cycleBeats * spq;
            var chordEnd = new int[chords.Count];
            int acc = 0;
            for (int i = 0; i < chords.Count; i++)
            {
                acc += Math.Max(1, chords[i].Beats) * spq;
                chordEnd[i] = acc;
            }
            int totalSlices = acc;

            int ChordIndexAt(int sl)
            {
                for (int i = 0; i < chordEnd.Length; i++) if (sl < chordEnd[i]) return i;
                return chordEnd.Length - 1;
            }

            // État PAR ANNEAU (persistant sur toute la durée du module) : dernière note MIDI jouée + dernier index
            // dans le voicing + direction zigzag + dernier chord vu (pour détecter le changement et rejouer Restart).
            int nL = m.Layers?.Count ?? 0;
            var lastMidi = new int[nL];
            var lastIdx = new int[nL];
            var zigDir = new int[nL];
            var lastChordSeen = new int[nL];
            for (int i = 0; i < nL; i++) { lastMidi[i] = -1; lastIdx[i] = -1; lastChordSeen[i] = -1; }

            if (m.Layers != null)
            {
                for (int li = 0; li < m.Layers.Count; li++)
                {
                    var lay = m.Layers[li];
                    if (lay == null || lay.Muted) continue;
                    var pat = lay.EffectivePattern();
                    if (pat == null || pat.Length == 0) continue;
                    int steps = pat.Length;

                    // Positions des onsets DANS UN cycle (mêmes formules que PolyDrum), puis on boucle sur tout le module.
                    var onsetsInCycle = new List<int>();
                    for (int k = 0; k < steps; k++)
                        if (pat[k])
                        {
                            long num = (long)k * cycleBeats * spq;
                            onsetsInCycle.Add((int)((num + steps / 2) / steps));
                        }
                    if (onsetsInCycle.Count == 0) continue;
                    onsetsInCycle.Sort();

                    // Déroulage cycle après cycle jusqu'à couvrir totalSlices. Chaque onset absolu est ensuite
                    // interprété selon l'accord actif à sa position — c'est ce qui découple rythme et harmonie.
                    var absOnsets = new List<int>();
                    int cycCount = (totalSlices + cycleSlices - 1) / cycleSlices;
                    for (int cy = 0; cy < cycCount; cy++)
                    {
                        int cycStart = cy * cycleSlices;
                        for (int i = 0; i < onsetsInCycle.Count; i++)
                        {
                            int s = cycStart + onsetsInCycle[i];
                            if (s >= totalSlices) break;
                            absOnsets.Add(s);
                        }
                    }

                    var rnd = new Random(unchecked(lay.RandomSeed * 1000003 + li * 17));
                    int cellLen = Math.Max(1, cycleSlices / steps);

                    for (int oi = 0; oi < absOnsets.Count; oi++)
                    {
                        int s = absOnsets[oi];
                        int chIdx = ChordIndexAt(s);
                        var voiced = voicedPerChord[chIdx];
                        if (voiced == null || voiced.Length == 0) continue;

                        // Changement d'accord → Restart en mode SWEEP (Multi n'a pas besoin, la voix est fixe).
                        bool chordChanged = lastChordSeen[li] != chIdx;
                        lastChordSeen[li] = chIdx;

                        int idx; int extraOctaves = 0;
                        if (m.Mode == PolyChordMode.OneRingPerTone)
                        {
                            // Wrap à l'octave, SYMÉTRIQUE dans les deux sens :
                            //   +3 sur triade [T,3,5] → T à l'octave supérieure ; +6 → T deux octaves plus haut.
                            //   -1 → 5 à l'octave inférieure ; -2 → 3 idem ; -3 → T idem ; -4 → 5 encore une octave plus bas.
                            // Empilement diatonique qui suit la structure de l'accord peu importe sa taille — un accord
                            // riche (7e/9e) élargit le voicing, une triade replie sur les 3 notes ± octaves.
                            // Formule (modulo mathématique C#) : idx = ((ti % n) + n) % n ; extraOct = (ti - idx) / n.
                            int ti = lay.ToneIndex;
                            int n = voiced.Length;
                            idx = ((ti % n) + n) % n;
                            extraOctaves = (ti - idx) / n;
                        }
                        else
                        {
                            if (chordChanged) idx = ResolveStart(m.Restart, voiced, lay, lastMidi[li], lastIdx[li]);
                            else idx = PickNext(lay.Contour, voiced.Length, oi, absOnsets.Count, ref lastIdx[li], ref zigDir[li], rnd);
                        }
                        int midi = voiced[idx] + (lay.Octave + extraOctaves) * 12;

                        // Longueur : legato → jusqu'au prochain onset du même anneau, MAIS coupé à la borne d'accord
                        // suivante (pour que le voicing ne s'étale pas sur le suivant). Sinon = cellLen constant.
                        int len = cellLen;
                        if (lay.Legato)
                        {
                            int nextS = oi + 1 < absOnsets.Count ? absOnsets[oi + 1] : totalSlices;
                            int boundary = chordEnd[chIdx];
                            len = Math.Max(1, Math.Min(nextS, boundary) - s);
                        }
                        int row = midi - 12;
                        // Aucune note ne doit franchir la fin du module : une note tenue du DERNIER cycle
                        // depassait, donc elle etait coupee au rebouclage de l'ecoute et empietait sur le temps
                        // du module suivant sur la timeline. On la borne a la fin du module.
                        int end = TotalSlices(m, spq);
                        if (s < end && len > end - s) len = end - s;
                        if (row >= 0 && row < 96 && s < end && len >= 1) outNotes.Add(new RiffNote(row, s, len));
                        lastMidi[li] = midi; lastIdx[li] = idx;
                    }
                }
            }

            outNotes.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : a.Note.CompareTo(b.Note));

            // Mode "monodie emergente" : quand plusieurs anneaux ont un onset au meme slice, on n'en
            // garde qu'UN SEUL. Choix par VOICE-LEADING : parmi les candidats du tick, celui qui
            // minimise le saut en semi-tons avec la note precedente, avec option d'octavier (±1, ±2)
            // pour rester proche. Si le meilleur saut brut > quinte (7 st), l'octaviage kick in
            // automatiquement — la ligne melodique glisse au lieu de sauter.
            // Le seed ne sert plus qu'au 1er tick (avant qu'il y ait une "lastNote" de reference).
            if (m.MonodicPick && outNotes.Count > 1)
            {
                var rndMono = new Random(m.MonodicSeed);
                var filtered = new List<RiffNote>(outNotes.Count);
                int lastNote = -1;
                int repeatCount = 0;
                int i0 = 0;
                while (i0 < outNotes.Count)
                {
                    int i1 = i0 + 1;
                    while (i1 < outNotes.Count && outNotes[i1].Start == outNotes[i0].Start) i1++;

                    RiffNote pick;
                    switch (m.MonodicStrategy)
                    {
                        case PolyChordMonoStrategy.Highest:
                        {
                            int bestK = i0;
                            for (int k = i0 + 1; k < i1; k++) if (outNotes[k].Note > outNotes[bestK].Note) bestK = k;
                            pick = OctaveShiftForVoiceLeading(outNotes[bestK], lastNote);
                            break;
                        }
                        case PolyChordMonoStrategy.Lowest:
                        {
                            int bestK = i0;
                            for (int k = i0 + 1; k < i1; k++) if (outNotes[k].Note < outNotes[bestK].Note) bestK = k;
                            pick = OctaveShiftForVoiceLeading(outNotes[bestK], lastNote);
                            break;
                        }
                        case PolyChordMonoStrategy.Random:
                            pick = OctaveShiftForVoiceLeading(outNotes[i0 + rndMono.Next(i1 - i0)], lastNote);
                            break;
                        case PolyChordMonoStrategy.Auto:
                        default:
                            if (lastNote < 0)
                            {
                                // Premier tick : tirage aleatoire (seed reproductible) — pas de reference.
                                pick = outNotes[i0 + rndMono.Next(i1 - i0)];
                            }
                            else
                            {
                                // Voice-leading + anti-repetition : pour chaque candidat, teste ±0 ±12 ±24
                                // semi-tons et garde la combinaison qui minimise le score = distance + penalite
                                // (si AvoidRepeat et shifted == lastNote → +3 + 3*repeatCount).
                                int bestIdx = i0, bestOctShift = 0;
                                double bestScore = double.MaxValue;
                                for (int k = i0; k < i1; k++)
                                {
                                    int cand = outNotes[k].Note;
                                    for (int oct = -4; oct <= 4; oct++)   // ±4 octaves : couvre toute plage MIDI
                                    {
                                        int shifted = cand + oct * 12;
                                        if (shifted < 0 || shifted > 95) continue;
                                        double d = Math.Abs(shifted - lastNote);
                                        if (m.MonodicAvoidRepeat && shifted == lastNote)
                                            d += 3.0 + 3.0 * repeatCount;
                                        if (d < bestScore) { bestScore = d; bestIdx = k; bestOctShift = oct; }
                                    }
                                }
                                var orig = outNotes[bestIdx];
                                pick = new RiffNote(orig.Note + bestOctShift * 12, orig.Start, orig.Length);
                            }
                            break;
                    }

                    filtered.Add(pick);
                    if (pick.Note == lastNote) repeatCount++;
                    else repeatCount = 0;
                    lastNote = pick.Note;
                    i0 = i1;
                }
                outNotes = filtered;
            }

            return new Riff { Name = "PolyChords", Notes = outNotes, SlicesPerQuarter = spq, LengthSlices = TotalSlices(m, spq) };
        }

        /// <summary>Longueur du riff produit, en slices : la durée RÉELLE du module. Sans elle, <see cref="Riff.LengthSlices"/>
        /// garde son défaut de 96, et la boucle d'écoute de l'éditeur — qui reboucle sur la fin du riff — se referme à
        /// 96 slices, soit une durée qui DÉPEND de <see cref="SpqFor"/> et donc des anneaux : « parfois un temps, parfois
        /// deux mesures » (symptôme rapporté par l'utilisateur). On veut boucler sur la durée du module.</summary>
        static int TotalSlices(PolyChordModule m, int spq)
            => Math.Max(1, (int)Math.Round(TotalBeats(m) * spq));

        // Octaviage anti-saut : etant donne UNE note candidate (deja choisie par la strategie), teste
        // ±4 octaves et garde la version qui minimise |shifted - lastNote|. Vise a rester dans la
        // limite d'une 7e mineure (10 st) — si le meilleur trouve est encore > 10, on garde quand
        // meme (impossible de mieux, par ex. note candidate proche d'une extremite MIDI). Si
        // lastNote < 0 (1er tick), renvoie la note originale.
        static RiffNote OctaveShiftForVoiceLeading(RiffNote orig, int lastNote)
        {
            if (lastNote < 0) return orig;
            int bestOct = 0, bestDist = Math.Abs(orig.Note - lastNote);
            for (int oct = -4; oct <= 4; oct++)
            {
                if (oct == 0) continue;
                int shifted = orig.Note + oct * 12;
                if (shifted < 0 || shifted > 95) continue;
                int d = Math.Abs(shifted - lastNote);
                if (d < bestDist) { bestDist = d; bestOct = oct; }
            }
            return bestOct == 0 ? orig : new RiffNote(orig.Note + bestOct * 12, orig.Start, orig.Length);
        }

        // Choix de l'index de départ dans un nouveau voicing (règle Restart appliquée au changement d'accord).
        static int ResolveStart(ChordRestartMode restart, int[] voiced, EuclidChordLayer lay, int lastPlayedMidi, int lastIdx)
        {
            if (voiced == null || voiced.Length == 0) return 0;
            switch (restart)
            {
                case ChordRestartMode.Grave: return 0;
                case ChordRestartMode.Aigu:  return voiced.Length - 1;
                case ChordRestartMode.Tonic: return 0;                              // note[0] = grave (bassiste effectif du voicing)
                case ChordRestartMode.Tierce: return Math.Min(1, voiced.Length - 1);
                case ChordRestartMode.Quinte: return Math.Min(2, voiced.Length - 1);
                case ChordRestartMode.Nearest:
                default:
                    if (lastPlayedMidi < 0) return 0;
                    int best = 0, bestDist = int.MaxValue;
                    int off = lay.Octave * 12;
                    for (int i = 0; i < voiced.Length; i++)
                    {
                        int d = Math.Abs((voiced[i] + off) - lastPlayedMidi);
                        if (d < bestDist) { bestDist = d; best = i; }
                    }
                    return best;
            }
        }

        // Prochaine note SANS reset (à appeler entre deux onsets du même accord). L'onset 0 n'a plus de rôle
        // particulier ici — ResolveStart gère l'amorçage au changement d'accord.
        static int PickNext(int contour, int voicedLen, int oi, int onCount, ref int curIdx, ref int dir, Random rnd)
        {
            if (voicedLen <= 1) { curIdx = 0; return 0; }
            switch (contour)
            {
                case 1: // Montante
                    curIdx++; if (curIdx >= voicedLen) curIdx = 0;
                    return curIdx;
                case 2: // Descendante
                    curIdx--; if (curIdx < 0) curIdx = voicedLen - 1;
                    return curIdx;
                case 3: // Statique
                    return curIdx;
                case 4: // Zigzag
                    if (dir == 0) { curIdx--; if (curIdx <= 0) { curIdx = 0; dir = 1; } }
                    else          { curIdx++; if (curIdx >= voicedLen - 1) { curIdx = voicedLen - 1; dir = 0; } }
                    return curIdx;
                case 5: // Aléatoire
                    curIdx = rnd.Next(voicedLen); return curIdx;
                case 0: // Vague / arcs (défaut)
                default:
                    double phase = (oi / (double)Math.Max(1, onCount)) * Math.PI * 2;
                    double norm = 0.5 * (1 - Math.Cos(phase));
                    curIdx = (int)Math.Round(norm * (voicedLen - 1));
                    return curIdx;
            }
        }
        static int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);

        // ---- "Figer" ------------------------------------------------------------------------------------------

        /// <summary>Explose le module en N listes de RiffNote (une par accord), telles qu'attendues par
        /// <see cref="PatternGeneratorModule.CustomNotes"/> — ligne = index chord-voice (0..10). Utilisé par « Figer »
        /// pour produire N <see cref="PatternGeneratorModule"/> ordinaires, un par accord.</summary>
        public static List<RiffNote>[] ToNotesPerChord(PolyChordModule m)
        {
            if (m?.Chords == null || m.Chords.Count == 0) return new List<RiffNote>[0];
            var res = new List<RiffNote>[m.Chords.Count];
            int spq = SpqFor(m);
            for (int i = 0; i < m.Chords.Count; i++) res[i] = new List<RiffNote>();
            if (m.Layers == null) return res;

            // Grille de cycle constante (nouveau modèle) : on émet les onsets sur toute la durée du module, puis on
            // les répartit dans les buckets par accord. Chaque accord garde ainsi le RYTHME qu'il entendait pendant
            // la lecture, tronqué à ses propres bornes — sans altération de la vitesse.
            int cycleBeats = CycleBeats(m);
            int cycleSlices = cycleBeats * spq;
            var chordStart = new int[m.Chords.Count];
            var chordEnd = new int[m.Chords.Count];
            int acc = 0;
            for (int i = 0; i < m.Chords.Count; i++)
            {
                chordStart[i] = acc;
                acc += Math.Max(1, m.Chords[i].Beats) * spq;
                chordEnd[i] = acc;
            }
            int totalSlices = acc;

            foreach (var lay in m.Layers)
            {
                if (lay == null || lay.Muted) continue;
                var pat = lay.EffectivePattern();
                if (pat == null || pat.Length == 0) continue;
                int steps = pat.Length;
                int voiceRow = Clamp(lay.ToneIndex + 1, 1, PatternGenerator.CustomVoiceCount - 1);
                int cellLen = Math.Max(1, cycleSlices / steps);

                var onsetsInCycle = new List<int>();
                for (int k = 0; k < steps; k++)
                    if (pat[k])
                    {
                        long num = (long)k * cycleBeats * spq;
                        onsetsInCycle.Add((int)((num + steps / 2) / steps));
                    }
                onsetsInCycle.Sort();

                int cycCount = (totalSlices + cycleSlices - 1) / cycleSlices;
                for (int cy = 0; cy < cycCount; cy++)
                {
                    int cyStart = cy * cycleSlices;
                    foreach (int off in onsetsInCycle)
                    {
                        int s = cyStart + off;
                        if (s >= totalSlices) break;
                        // Bucket : quel accord contient cet onset ? Répartit dans res[chIdx] avec Start relatif à l'accord.
                        for (int i = 0; i < m.Chords.Count; i++)
                            if (s < chordEnd[i]) { res[i].Add(new RiffNote(voiceRow, s - chordStart[i], cellLen)); break; }
                    }
                }
            }
            return res;
        }
    }
}
