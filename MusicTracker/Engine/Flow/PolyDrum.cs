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
            if (n == nameof(Hits) || n == nameof(Steps) || n == nameof(CustomMode) || n == nameof(CustomHits) || n == nameof(Rotation))
                foreach (var d in new[] { nameof(SummaryText), nameof(AnalysisText) }) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(d));
            if (n == nameof(CustomMode))
                foreach (var d in new[] { nameof(HitsRotationVisibility), nameof(AnalysisVisibility), nameof(PersColour) }) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(d));
            if (n == nameof(Collapsed))
                foreach (var d in new[] { nameof(BodyVisibility), nameof(SummaryVisibility), nameof(AnalysisVisibility), nameof(CollapseGlyph) }) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(d));
        }

        int lane, accentLane = -1, hits = 3, steps = 8, rotation;
        // Ancien champ StepSlices : la subdivision par calque a DISPARU — chaque anneau vaut désormais tout le cycle
        // du module (module.Beats temps), divisé en <see cref="Steps"/> parts égales. Une cellule dure Beats/Steps.
        // Le champ reste ignoré côté rendu ; on le garde en lecture JSON pour ne pas casser les fichiers existants.
        [System.Text.Json.Serialization.JsonInclude] int stepSlices = 12;
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
            lane = lane, accentLane = accentLane, hits = hits, steps = steps, rotation = rotation,
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

    /// <summary>Rend une batterie polyrythmique. NOUVEAU MODÈLE — tous les calques partagent le MÊME cycle
    /// (durée = <see cref="PolyDrumModule.Beats"/> temps), simplement découpé en un nombre de pas différent par
    /// anneau. Il n'y a donc plus de PPCM des cycles euclidiens à calculer : la « polyrythmie » vient de ce que
    /// 3 pas contre 5 pas dans le même cycle produisent des ratios musicalement intéressants.
    ///
    /// Pour que chaque cellule tombe EXACTEMENT sur un slice entier — pas de dérive rythmique — on prend
    /// <c>spq = PPCM(Steps de tous les anneaux)</c> comme résolution du riff produit. Le cycle en slices vaut
    /// alors <c>Beats × spq</c>, et la cellule k de l'anneau i vaut <c>k × Beats × spq / Steps_i</c>, toujours
    /// entier (par définition du PPCM). Garde-fou : si le PPCM devient absurdement grand (Steps premiers
    /// entre eux à 4 chiffres), on retombe sur spq = 24 avec un simple arrondi (erreur &lt; 10 ms).</summary>
    public static class PolyDrum
    {
        // Garde-fou : au-delà, on préfère l'arrondi (perte imperceptible) à un tableau de slices monstrueux.
        const int MaxSpq = 4800;

        /// <summary>Résolution de slice à utiliser pour ce module. On veut que la cellule k de l'anneau i
        /// (position slice = <c>k × Beats × spq / Steps_i</c>) tombe TOUJOURS sur un entier. C'est vrai dès que
        /// <c>Steps_i</c> divise <c>Beats × spq</c> pour tout i. Le plus petit spq qui garantit ça est
        /// <c>LCM(Steps) / gcd(Beats, LCM(Steps))</c> — la formule proposée « LCM/Beats » n'est correcte que
        /// lorsque Beats divise le PPCM ; on divise donc plutôt par leur pgcd, ce qui reste minimal ET toujours
        /// entier. Plafond : au-delà, on retombe sur spq = 24 (arrondi audible &lt; 10 ms).</summary>
        public static int SpqFor(PolyDrumModule m)
        {
            long lcm = 1;
            if (m?.Layers != null)
                foreach (var l in m.Layers)
                {
                    if (l == null || l.Muted) continue;
                    int s = Math.Max(1, l.Steps);
                    lcm = Lcm(lcm, s);
                    if (lcm > MaxSpq) return 24;    // trop grand : fallback snap
                }
            int beats = CycleBeats(m);
            long spq = lcm / Gcd(beats, lcm);       // ≥ 1 par construction ; parfois beaucoup plus petit que lcm
            return (int)Math.Max(1, spq);
        }
        static long Gcd(long a, long b) { while (b != 0) { long t = a % b; a = b; b = t; } return a < 0 ? -a : a; }
        static long Lcm(long a, long b) { long g = Gcd(a, b); return g == 0 ? 0 : a / g * b; }

        /// <summary>Nombre de temps par CYCLE — utilisé par le rendu et l'éditeur pour dire « au bout de N temps
        /// tous les anneaux retombent ensemble ». Migration transparente pour les fichiers legacy où le champ
        /// Beats n'existait pas encore : on retombe sur BeatsPerBar (ancien nom).</summary>
        public static int CycleBeats(PolyDrumModule m)
            => Math.Max(1, m == null ? 4 : (m.Beats > 0 ? m.Beats : m.BeatsPerBar));

        /// <summary>Longueur totale du module en slices : Beats × Repeats × spq (où spq dépend du PPCM des Steps).</summary>
        public static int TotalSlices(PolyDrumModule m) => CycleBeats(m) * Math.Max(1, m?.Repeats ?? 1) * SpqFor(m);

        /// <summary>Durée totale du module en TEMPS (noires). Ne dépend PAS de spq — juste Beats × Repeats.</summary>
        public static double TotalBeats(PolyDrumModule m) => CycleBeats(m) * (double)Math.Max(1, m?.Repeats ?? 1);

        /// <summary>Position en slices de la cellule <paramref name="cellIndex"/> (0-based) d'un anneau à
        /// <paramref name="steps"/> pas, dans un cycle qui vaut Beats × spq slices. Formule :
        /// <c>cellIndex × Beats × spq / Steps</c>. Grâce à <see cref="SpqFor"/> (spq = LCM/gcd), le résultat est
        /// TOUJOURS entier — pas de dérive. En mode fallback (spq=24 par garde-fou), on arrondit au plus proche.</summary>
        static int CellStart(int cellIndex, int steps, int beats, int spq)
        {
            long num = (long)cellIndex * beats * spq;
            return (int)((num + steps / 2) / steps);           // arrondi (entier si tout est exact)
        }

        /// <summary>Déroule tous les calques en un riff de batterie. NOUVEAU MODÈLE : tous les anneaux partagent
        /// le même cycle (<see cref="PolyDrumModule.Beats"/> temps), découpé en Steps parts par calque. La cellule
        /// k de l'anneau est déclenchée au slice <c>k × Beats × spq / Steps</c>.
        /// L'ACCENT (<see cref="EuclidLayer.AccentLane"/>) est envoyé sur le PREMIER coup de chaque tour de cycle.</summary>
        public static Riff Generate(PolyDrumModule m)
        {
            int spq = SpqFor(m);
            int beats = CycleBeats(m);
            int cycleSlices = beats * spq;
            int repeats = Math.Max(1, m?.Repeats ?? 1);
            int total = cycleSlices * repeats;
            var slices = new SequencerSlice[total];
            if (m?.Layers != null)
                foreach (var l in m.Layers)
                {
                    if (l == null || l.Muted || l.Lane < 0 || l.Lane >= DrumPattern.LaneCount) continue;
                    int row = DrumPattern.KeyForLane(l.Lane) - 12;
                    if (row < 0 || row >= 96) continue;
                    int accentRow = -1;
                    if (l.AccentLane >= 0 && l.AccentLane < DrumPattern.LaneCount)
                    {
                        int r = DrumPattern.KeyForLane(l.AccentLane) - 12;
                        if (r >= 0 && r < 96) accentRow = r;
                    }
                    var pat = l.EffectivePattern(); if (pat == null || pat.Length == 0) continue;
                    int steps = pat.Length;
                    int firstOn = -1; for (int i = 0; i < steps; i++) if (pat[i]) { firstOn = i; break; }
                    if (firstOn < 0) continue;
                    for (int rep = 0; rep < repeats; rep++)
                    {
                        int cycleStart = rep * cycleSlices;
                        for (int i = 0; i < steps; i++)
                        {
                            if (!pat[i]) continue;
                            int s = cycleStart + CellStart(i, steps, beats, spq);
                            if (s < 0 || s >= total) continue;
                            int useRow = (accentRow >= 0 && i == firstOn) ? accentRow : row;
                            slices[s].On(useRow, true);
                        }
                    }
                }
            return new Riff { Name = "PolyDrums", Slices = slices, SlicesPerQuarter = spq };
        }

        /// <summary>Convertit les calques en liste de coups (ligne = rangée), pour figer le polyrythme en motif
        /// ordinaire. L'accent est respecté ici aussi.</summary>
        public static List<Engine.RiffNote> ToNotes(PolyDrumModule m)
        {
            var notes = new List<Engine.RiffNote>();
            int spq = SpqFor(m);
            int beats = CycleBeats(m);
            int cycleSlices = beats * spq;
            int repeats = Math.Max(1, m?.Repeats ?? 1);
            int total = cycleSlices * repeats;
            if (m?.Layers != null)
                foreach (var l in m.Layers)
                {
                    if (l == null || l.Muted || l.Lane < 0) continue;
                    int accent = (l.AccentLane >= 0 && l.AccentLane < DrumPattern.LaneCount) ? l.AccentLane : -1;
                    var pat = l.EffectivePattern(); if (pat == null || pat.Length == 0) continue;
                    int steps = pat.Length;
                    int firstOn = -1; for (int i = 0; i < steps; i++) if (pat[i]) { firstOn = i; break; }
                    if (firstOn < 0) continue;
                    int cellLen = Math.Max(1, cycleSlices / steps);
                    for (int rep = 0; rep < repeats; rep++)
                    {
                        int cycleStart = rep * cycleSlices;
                        for (int i = 0; i < steps; i++)
                        {
                            if (!pat[i]) continue;
                            int s = cycleStart + CellStart(i, steps, beats, spq);
                            if (s < 0 || s >= total) continue;
                            int lane = (accent >= 0 && i == firstOn) ? accent : l.Lane;
                            notes.Add(new Engine.RiffNote(lane, s, cellLen));
                        }
                    }
                }
            notes.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : a.Note.CompareTo(b.Note));
            return notes;
        }
    }
}
