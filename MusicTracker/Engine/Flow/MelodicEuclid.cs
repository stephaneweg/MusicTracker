using System;
using System.Collections.Generic;
using System.Linq;

namespace MusicTracker.Engine.Flow
{
    /// <summary>Un CALQUE d'une LIGNE MÉLODIQUE polyrythmique (MelodicPolyModule) : une VOIX
    /// (0..MelodicLineModule.MaxVoices-1) et son motif euclidien E(K,N). Comme pour la ligne mélodique classique,
    /// seul le RYTHME est produit ici — les hauteurs restent choisies par MelodicLineEngine à partir de l'harmonie
    /// du morceau. Les paramètres restent VIVANTS (cf. EuclidLayer/PolyDrum) : changer K, N ou le décalage
    /// régénère immédiatement le rythme.</summary>
    public class EuclidVoice : System.ComponentModel.INotifyPropertyChanged
    {
        public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;
        void OnChanged(string n)
        {
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(n));
            if (n == nameof(Hits) || n == nameof(Steps) || n == nameof(StepSlices) || n == nameof(CustomMode) || n == nameof(CustomHits) || n == nameof(Rotation))
                foreach (var d in new[] { nameof(SummaryText), nameof(AnalysisText) }) PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(d));
            if (n == nameof(CustomMode))
                foreach (var d in new[] { nameof(HitsRotationVisibility), nameof(AnalysisVisibility), nameof(PersColour) }) PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(d));
            if (n == nameof(Collapsed))
                foreach (var d in new[] { nameof(BodyVisibility), nameof(SummaryVisibility), nameof(AnalysisVisibility), nameof(CollapseGlyph) }) PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(d));
        }

        int voice, hits = 3, steps = 8, rotation, stepSlices = 12;
        bool muted, collapsed, legato, customMode;
        int[] customHits;

        public int Voice { get { return voice; } set { if (voice != value) { voice = value; OnChanged(nameof(Voice)); } } }
        public int Hits { get { return hits; } set { if (hits != value) { hits = value; OnChanged(nameof(Hits)); } } }
        public int Steps { get { return steps; } set { if (steps != value) { steps = value; OnChanged(nameof(Steps)); } } }
        public int Rotation { get { return rotation; } set { if (rotation != value) { rotation = value; OnChanged(nameof(Rotation)); } } }
        public int StepSlices { get { return stepSlices; } set { if (stepSlices != value) { stepSlices = value; OnChanged(nameof(StepSlices)); PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(StepSlicesIndex))); } } }
        [System.Text.Json.Serialization.JsonIgnore]
        public int StepSlicesIndex
        {
            get { int i = System.Array.IndexOf(MelodicEuclid.StepSlicesChoices, StepSlices); return i < 0 ? 0 : i; }
            set { if (value >= 0 && value < MelodicEuclid.StepSlicesChoices.Length) StepSlices = MelodicEuclid.StepSlicesChoices[value]; }
        }
        public bool Muted { get { return muted; } set { if (muted != value) { muted = value; OnChanged(nameof(Muted)); } } }
        public bool Collapsed { get { return collapsed; } set { if (collapsed != value) { collapsed = value; OnChanged(nameof(Collapsed)); } } }
        public bool Legato { get { return legato; } set { if (legato != value) { legato = value; OnChanged(nameof(Legato)); } } }
        /// <summary>Voir <see cref="EuclidLayer.CustomMode"/>. Même contrat côté voix mélodique.</summary>
        public bool CustomMode { get { return customMode; } set { if (customMode != value) { customMode = value; OnChanged(nameof(CustomMode)); } } }
        public int[] CustomHits { get { return customHits; } set { customHits = value; OnChanged(nameof(CustomHits)); } }

        // [JsonIgnore] : ces propriétés dérivées exposent des types WPF (Visibility, SolidColorBrush) qui contiennent
        // des références internes cycliques (Transform.Inverse) — les sérialiser fait exploser System.Text.Json.
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
        [System.Text.Json.Serialization.JsonIgnore] public string VoiceLabel => Localization.Loc.T("Voix2") + (Voice + 1);

        public EuclidVoice Clone() => new EuclidVoice
        {
            voice = voice, hits = hits, steps = steps, rotation = rotation, stepSlices = stepSlices,
            muted = muted, collapsed = collapsed, legato = legato,
            customMode = customMode, customHits = customHits == null ? null : (int[])customHits.Clone()
        };

        public bool[] EffectivePattern()
            => CustomMode
                ? RhythmAnalysis.FromPositions(CustomHits, Steps)
                : EuclideanRhythm.Rotate(EuclideanRhythm.Pattern(Hits, Steps), Rotation);
    }

    /// <summary>Rend une ligne mélodique polyrythmique (MelodicPolyModule) : les calques donnent le RYTHME (une
    /// voix = un cycle euclidien E(K,N), comme PolyDrum pour la batterie), puis c'est
    /// <see cref="Timeline.MelodicLineEngine"/> — le même moteur qu'une ligne mélodique classique — qui choisit
    /// les hauteurs à partir de l'harmonie du morceau à la position où le module est posé.</summary>
    public static class MelodicEuclid
    {
        public static readonly int[] StepSlicesChoices = PolyDrum.StepSlicesChoices;
        const int Spq = 24;   // même grille que la batterie (divisible par 2, 3, 4, 6, 8, 12)

        /// <summary>Longueur totale du module, en slices : dérivée de <see cref="MelodicPolyModule.DurationBeats"/>.
        /// Fallback legacy : max(cycle des voix) × Repeats. Voir <see cref="PolyDrum.TotalSlices"/>.</summary>
        public static int TotalSlices(MelodicPolyModule m)
        {
            if (m != null && m.DurationBeats > 0) return m.DurationBeats * Spq;
            int maxCycle = 0;
            if (m?.Layers != null)
                foreach (var v in m.Layers)
                {
                    if (v == null) continue;
                    int c = Math.Max(1, v.Steps) * Math.Max(1, v.StepSlices);
                    if (c > maxCycle) maxCycle = c;
                }
            if (maxCycle <= 0) maxCycle = Spq;
            return maxCycle * Math.Max(1, m?.Repeats ?? 1);
        }

        /// <summary>Durée totale du module en temps (beats).</summary>
        public static double TotalBeats(MelodicPolyModule m) => TotalSlices(m) / (double)Spq;

        /// <summary>Au bout de combien de temps TOUTES les voix retombent ensemble : le vrai cycle du polyrythme.</summary>
        public static double CycleBeats(MelodicPolyModule m)
            => EuclideanRhythm.CycleBeats(
                ((System.Collections.Generic.IEnumerable<EuclidVoice>)m?.Layers ?? new List<EuclidVoice>()).Where(v => v != null).Select(v => (v.Steps, v.StepSlices, v.Muted)),
                Spq);

        /// <summary>Renumérote Voice = position dans la liste : après un ajout/suppression de calque, ça évite deux
        /// calques sur la même voix (un index réutilisé) ou un "trou" qui gâche le budget de voix (≤3).</summary>
        public static void Renumber(System.Collections.Generic.IList<EuclidVoice> layers)
        {
            if (layers == null) return;
            for (int i = 0; i < layers.Count; i++) if (layers[i] != null) layers[i].Voice = i;
        }

        /// <summary>Construit le squelette rythmique (une voix par calque, PAS de hauteurs) de tout le module.</summary>
        static MelodicLineModule BuildSkeleton(MelodicPolyModule m)
        {
            int total = TotalSlices(m);
            var notes = new List<Engine.RiffNote>();
            int maxVoice = 0;
            if (m.Layers != null)
                foreach (var v in m.Layers)
                {
                    if (v == null || v.Voice < 0 || v.Voice >= MelodicLineModule.MaxVoices) continue;
                    maxVoice = Math.Max(maxVoice, v.Voice);
                    if (v.Muted) continue;
                    notes.AddRange(v.CustomMode
                        ? RhythmAnalysis.Build(v.Voice, v.EffectivePattern(), v.StepSlices, total, v.Legato)
                        : EuclideanRhythm.Build(v.Voice, v.Hits, v.Steps, v.Rotation, v.StepSlices, total, v.Legato));
                }
            notes.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : a.Note.CompareTo(b.Note));
            var skeleton = new MelodicLineModule { BeatsPerBar = Math.Max(1, total / Spq), VoiceCount = Math.Max(1, Math.Min(MelodicLineModule.MaxVoices, maxVoice + 1)) };
            skeleton.SetNotes(notes, Spq, total);
            return skeleton;
        }

        /// <summary>Déroule le module en Riff avec les VRAIES hauteurs, choisies par MelodicLineEngine sur
        /// l'harmonie active à <paramref name="startBeat"/>. Retourne null si aucun calque n'a de coup (comme
        /// MelodicLineEngine.GenerateLine pour une ligne mélodique classique vide).</summary>
        public static Riff Generate(MelodicPolyModule m, Timeline.TimelineProject project, Func<Guid, Riff> resolve, Score.KeySignature key, double startBeat, int[] carry = null)
            => Timeline.MelodicLineEngine.GenerateLine(BuildSkeleton(m), project, resolve, key, startBeat, carry);
    }
}
