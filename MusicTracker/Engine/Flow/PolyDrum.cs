using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;

namespace MusicTracker.Engine.Flow
{
    /// <summary>Un CALQUE d'une batterie polyrythmique : un instrument, et le motif euclidien E(K,N) qui le joue.
    /// Les paramètres restent VIVANTS (contrairement à une génération qui figerait le motif en liste de coups) :
    /// changer K, N ou le décalage se réentend immédiatement.
    ///
    /// Observable (INotifyPropertyChanged) pour que l'éditeur XAML soit un simple ItemsControl bindé sur la liste
    /// des calques : chaque champ notifie sa modification, plus les propriétés DÉRIVÉES visibles à l'écran
    /// (SummaryText, HitsRotationVisibility, AnalysisVisibility, AnalysisText, PersColour) — ainsi la carte du
    /// calque s'auto-rafraîchit sans que le code-behind ait besoin de re-fabriquer l'UI.</summary>
    public class EuclidLayer : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        void OnChanged(string n)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
            // Les propriétés dérivées : quand un champ source change, l'affichage lié doit se rafraîchir aussi.
            if (n == nameof(Hits) || n == nameof(Steps) || n == nameof(StepSlices) || n == nameof(CustomMode) || n == nameof(CustomHits) || n == nameof(Rotation))
                foreach (var d in new[] { nameof(SummaryText), nameof(AnalysisText) }) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(d));
            if (n == nameof(CustomMode))
                foreach (var d in new[] { nameof(HitsRotationVisibility), nameof(AnalysisVisibility), nameof(PersColour) }) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(d));
            if (n == nameof(Collapsed))
                foreach (var d in new[] { nameof(BodyVisibility), nameof(SummaryVisibility), nameof(AnalysisVisibility), nameof(CollapseGlyph) }) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(d));
        }

        int lane, accentLane = -1, hits = 3, steps = 8, rotation, stepSlices = 12;
        bool muted, collapsed, customMode;
        int[] customHits;

        public int Lane { get { return lane; } set { if (lane != value) { lane = value; OnChanged(nameof(Lane)); } } }
        /// <summary>Instrument JOUÉ SUR LE PREMIER COUP de chaque cycle (avant que le motif ne reboucle). -1 = pas
        /// d'accent, tous les coups jouent sur <see cref="Lane"/>. Concrètement, sur un djembé E(3,8), on met un son
        /// de basse ici et le tone/slap sur Lane — l'accent tombe alors sur le 1 de chaque tour de motif.</summary>
        public int AccentLane { get { return accentLane; } set { if (accentLane != value) { accentLane = value; OnChanged(nameof(AccentLane)); PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AccentLaneIndex))); } } }
        /// <summary>Index dans une liste qui préfixe LaneNames par « (aucun) » — 0 = pas d'accent, 1..N = lane 0..N-1.
        /// Sert au binding ComboBox de l'éditeur, sans convertisseur.</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public int AccentLaneIndex
        {
            get { return AccentLane + 1; }
            set { AccentLane = value <= 0 ? -1 : (value - 1); }
        }
        public int Hits { get { return hits; } set { if (hits != value) { hits = value; OnChanged(nameof(Hits)); } } }
        public int Steps { get { return steps; } set { if (steps != value) { steps = value; OnChanged(nameof(Steps)); } } }
        public int Rotation { get { return rotation; } set { if (rotation != value) { rotation = value; OnChanged(nameof(Rotation)); } } }
        public int StepSlices { get { return stepSlices; } set { if (stepSlices != value) { stepSlices = value; OnChanged(nameof(StepSlices)); PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StepSlicesIndex))); } } }
        /// <summary>Index de <see cref="StepSlices"/> dans <see cref="PolyDrum.StepSlicesChoices"/> — pratique pour
        /// binder un ComboBox par SelectedIndex sans passer par un IValueConverter.</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public int StepSlicesIndex
        {
            get { int i = Array.IndexOf(PolyDrum.StepSlicesChoices, StepSlices); return i < 0 ? 0 : i; }
            set { if (value >= 0 && value < PolyDrum.StepSlicesChoices.Length) StepSlices = PolyDrum.StepSlicesChoices[value]; }
        }
        public bool Muted { get { return muted; } set { if (muted != value) { muted = value; OnChanged(nameof(Muted)); } } }
        public bool Collapsed { get { return collapsed; } set { if (collapsed != value) { collapsed = value; OnChanged(nameof(Collapsed)); } } }
        /// <summary>MODE PERSONNALISÉ : le motif n'est plus dérivé de K/N (E(K,N) + rotation) mais dessiné à la main
        /// sur la roue — un clic allume/éteint une cellule. On persiste alors les POSITIONS des coups
        /// (<see cref="CustomHits"/>) et non plus les paramètres.</summary>
        public bool CustomMode { get { return customMode; } set { if (customMode != value) { customMode = value; OnChanged(nameof(CustomMode)); } } }
        public int[] CustomHits { get { return customHits; } set { customHits = value; OnChanged(nameof(CustomHits)); } }

        // ---- propriétés dérivées, à destination du DataTemplate ----------------------------------------------
        // [JsonIgnore] impératif : SolidColorBrush et Visibility sont des types WPF avec transforms internes qui
        // se référencent en boucle ; les inclure dans la sérialisation JSON produit un cycle
        // (Transform.Inverse.Inverse…) et fait planter la sauvegarde du projet.
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

        public EuclidLayer Clone() => new EuclidLayer
        {
            lane = lane, accentLane = accentLane, hits = hits, steps = steps, rotation = rotation, stepSlices = stepSlices,
            muted = muted, collapsed = collapsed,
            customMode = customMode, customHits = customHits == null ? null : (int[])customHits.Clone()
        };

        /// <summary>Motif effectif du calque : dessiné à la main en mode personnalisé, E(K,N) sinon.
        /// Longueur toujours = Steps, pour que la roue et le rendu partagent la même grille.</summary>
        public bool[] EffectivePattern()
            => CustomMode
                ? RhythmAnalysis.FromPositions(CustomHits, Steps)
                : EuclideanRhythm.Rotate(EuclideanRhythm.Pattern(Hits, Steps), Rotation);
    }

    /// <summary>Rend une batterie polyrythmique : chaque calque déroule son propre cycle E(K,N), et les cycles de
    /// longueurs différentes se décalent les uns par rapport aux autres — c'est tout l'intérêt.</summary>
    public static class PolyDrum
    {
        /// <summary>Durées de pas proposées, exactes sur la grille à 24 slices/noire (divisible par 2, 3, 4, 6, 8, 12).</summary>
        public static readonly int[] StepSlicesChoices = { 24, 12, 6, 8 };   // noire · croche · double-croche · triolet de croche

        /// <summary>Longueur totale du module, en slices : dérivée de <see cref="PolyDrumModule.DurationBeats"/>.
        /// Le module peut couper au milieu d'un cycle (motif truncé) ou en boucler plusieurs — c'est le but,
        /// que la longueur soit indépendante du cycle. Fallback (fichiers legacy sans DurationBeats mais avec
        /// Repeats) : max(cycle des couches) × Repeats, l'ancienne formule.</summary>
        public static int TotalSlices(PolyDrumModule m)
        {
            int spq = DrumPattern.SlicesPerQuarter;
            if (m != null && m.DurationBeats > 0) return m.DurationBeats * spq;
            // Legacy : ancien projet chargé avec Repeats seul et DurationBeats non renseigné → on retombe sur la
            // formule d'origine pour que rien ne change à l'écoute.
            int maxCycle = 0;
            if (m?.Layers != null)
                foreach (var l in m.Layers)
                {
                    if (l == null) continue;
                    int c = Math.Max(1, l.Steps) * Math.Max(1, l.StepSlices);
                    if (c > maxCycle) maxCycle = c;
                }
            if (maxCycle <= 0) maxCycle = spq;
            return maxCycle * Math.Max(1, m?.Repeats ?? 1);
        }

        /// <summary>Durée totale du module en temps (beats) — dérivée de <see cref="TotalSlices"/>.</summary>
        public static double TotalBeats(PolyDrumModule m) => TotalSlices(m) / (double)DrumPattern.SlicesPerQuarter;

        /// <summary>Au bout de combien de temps TOUS les calques retombent ensemble : le vrai cycle du polyrythme.
        /// Sert à l'affichage — c'est l'information que l'oreille cherche et que les nombres seuls ne donnent pas.</summary>
        public static double CycleBeats(PolyDrumModule m)
            => EuclideanRhythm.CycleBeats(
                ((System.Collections.Generic.IEnumerable<EuclidLayer>)m?.Layers ?? new List<EuclidLayer>()).Where(l => l != null).Select(l => (l.Steps, l.StepSlices, l.Muted)),
                DrumPattern.SlicesPerQuarter);

        /// <summary>Déroule tous les calques en un riff de batterie. Comme pour les autres motifs de percussion,
        /// chaque note est UN déclenchement à son début (la longueur ne sert qu'à l'édition).
        /// L'ACCENT (<see cref="EuclidLayer.AccentLane"/>) : quand il est défini, le PREMIER coup de chaque cycle
        /// est envoyé sur cette lane à la place de <see cref="EuclidLayer.Lane"/> — pattern typique djembé
        /// (basse sur le 1, tone/slap sur le reste).</summary>
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
                    int accentRow = -1;
                    if (l.AccentLane >= 0 && l.AccentLane < DrumPattern.LaneCount)
                    {
                        int r = DrumPattern.KeyForLane(l.AccentLane) - 12;
                        if (r >= 0 && r < 96) accentRow = r;
                    }
                    int cycleSlices = Math.Max(1, l.Steps) * Math.Max(1, l.StepSlices);
                    var notes = l.CustomMode
                        ? RhythmAnalysis.Build(0, l.EffectivePattern(), l.StepSlices, total)
                        : EuclideanRhythm.Build(0, l.Hits, l.Steps, l.Rotation, l.StepSlices, total);
                    // Le PREMIER coup au sein de chaque tour de cycle prend la lane d'accent (s'il y en a une).
                    // On détecte "premier du cycle" en comparant Start au minimum des Start pour chaque cycle index.
                    int prevCycle = -1;
                    int firstStartInCycle = -1;
                    for (int i = 0; i < notes.Count; i++)
                    {
                        var n = notes[i]; if (n.Start >= total) continue;
                        int cycleIdx = n.Start / cycleSlices;
                        if (cycleIdx != prevCycle) { prevCycle = cycleIdx; firstStartInCycle = n.Start; }
                        int useRow = (accentRow >= 0 && n.Start == firstStartInCycle) ? accentRow : row;
                        slices[n.Start].On(useRow, true);
                    }
                }
            return new Riff { Name = "PolyDrums", Slices = slices, SlicesPerQuarter = DrumPattern.SlicesPerQuarter };
        }

        /// <summary>Convertit les calques en liste de coups (ligne = rangée), pour figer le polyrythme en motif de
        /// batterie ordinaire, éditable coup par coup. L'inverse n'existe pas : on ne remonte pas de coups à des K/N.
        /// L'accent (voir <see cref="Generate"/>) est respecté ici aussi — le premier coup de chaque cycle
        /// atterrit sur la lane d'accent quand elle est définie.</summary>
        public static List<Engine.RiffNote> ToNotes(PolyDrumModule m)
        {
            var notes = new List<Engine.RiffNote>();
            int total = TotalSlices(m);
            if (m.Layers != null)
                foreach (var l in m.Layers)
                {
                    if (l == null || l.Muted || l.Lane < 0) continue;
                    int accent = (l.AccentLane >= 0 && l.AccentLane < DrumPattern.LaneCount) ? l.AccentLane : -1;
                    int cycleSlices = Math.Max(1, l.Steps) * Math.Max(1, l.StepSlices);
                    var built = l.CustomMode
                        ? RhythmAnalysis.Build(l.Lane, l.EffectivePattern(), l.StepSlices, total)
                        : EuclideanRhythm.Build(l.Lane, l.Hits, l.Steps, l.Rotation, l.StepSlices, total);
                    int prevCycle = -1, firstStart = -1;
                    for (int i = 0; i < built.Count; i++)
                    {
                        var n = built[i];
                        int cycleIdx = n.Start / cycleSlices;
                        if (cycleIdx != prevCycle) { prevCycle = cycleIdx; firstStart = n.Start; }
                        int lane = (accent >= 0 && n.Start == firstStart) ? accent : l.Lane;
                        notes.Add(new Engine.RiffNote(lane, n.Start, n.Length));
                    }
                }
            notes.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : a.Note.CompareTo(b.Note));
            return notes;
        }
    }
}
