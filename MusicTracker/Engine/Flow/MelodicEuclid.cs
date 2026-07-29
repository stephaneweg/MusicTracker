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
            if (n == nameof(Hits) || n == nameof(Steps) || n == nameof(CustomMode) || n == nameof(CustomHits) || n == nameof(Rotation))
                foreach (var d in new[] { nameof(SummaryText), nameof(AnalysisText) }) PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(d));
            if (n == nameof(CustomMode))
                foreach (var d in new[] { nameof(HitsRotationVisibility), nameof(AnalysisVisibility), nameof(PersColour) }) PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(d));
            if (n == nameof(Collapsed))
                foreach (var d in new[] { nameof(BodyVisibility), nameof(SummaryVisibility), nameof(AnalysisVisibility), nameof(CollapseGlyph) }) PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(d));
        }

        int voice, hits = 3, steps = 8, rotation, octave;
        bool muted, collapsed, legato, customMode;
        int[] customHits;

        public int Voice { get { return voice; } set { if (voice != value) { voice = value; OnChanged(nameof(Voice)); } } }
        public int Hits { get { return hits; } set { if (hits != value) { hits = value; OnChanged(nameof(Hits)); } } }
        public int Steps { get { return steps; } set { if (steps != value) { steps = value; OnChanged(nameof(Steps)); } } }
        public int Rotation { get { return rotation; } set { if (rotation != value) { rotation = value; OnChanged(nameof(Rotation)); } } }

        /// <summary>Transposition de la voix, en OCTAVES (0 = registre naturel de la voix). Le moteur place chaque
        /// voix dans une bande de registre fixe (aigu / médium / grave) et n'offre qu'un décalage GLOBAL ; ce champ
        /// est donc le seul moyen de poser un anneau en basse et un autre en mélodie dans le même module.
        /// Appliqué APRÈS le choix des hauteurs, pour que le moteur continue de raisonner sur l'harmonie réelle —
        /// transposer avant fausserait sa conduite des voix.</summary>
        public int Octave { get { return octave; } set { int v = Math.Max(-3, Math.Min(3, value)); if (octave != v) { octave = v; OnChanged(nameof(Octave)); } } }
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
            voice = voice, hits = hits, steps = steps, rotation = rotation,
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
        // Résolution : PPCM des Steps / gcd(Beats, PPCM), même formule que PolyDrum.SpqFor.
        const int MaxSpq = 4800;

        public static int SpqFor(MelodicPolyModule m)
        {
            long lcm = 1;
            if (m?.Layers != null)
                foreach (var v in m.Layers)
                {
                    if (v == null || v.Muted) continue;
                    int s = Math.Max(1, v.Steps);
                    lcm = Lcm(lcm, s);
                    if (lcm > MaxSpq) return 24;
                }
            int beats = CycleBeats(m);
            long spq = lcm / Gcd(beats, lcm);
            return (int)Math.Max(1, spq);
        }
        static long Gcd(long a, long b) { while (b != 0) { long t = a % b; a = b; b = t; } return a < 0 ? -a : a; }
        static long Lcm(long a, long b) { long g = Gcd(a, b); return g == 0 ? 0 : a / g * b; }

        /// <summary>Temps par cycle (tour complet où tous les anneaux retombent ensemble). Migration legacy :
        /// on retombe sur BeatsPerBar quand le nouveau champ Beats n'est pas renseigné.</summary>
        public static int CycleBeats(MelodicPolyModule m)
            => Math.Max(1, m == null ? 4 : (m.Beats > 0 ? m.Beats : m.BeatsPerBar));

        /// <summary>Longueur totale du module en slices : Beats × Repeats × spq.</summary>
        public static int TotalSlices(MelodicPolyModule m) => CycleBeats(m) * Math.Max(1, m?.Repeats ?? 1) * SpqFor(m);

        /// <summary>Durée totale du module en TEMPS (noires). Beats × Repeats.</summary>
        public static double TotalBeats(MelodicPolyModule m) => CycleBeats(m) * (double)Math.Max(1, m?.Repeats ?? 1);

        /// <summary>Renumérote Voice = position dans la liste : après un ajout/suppression de calque, ça évite deux
        /// calques sur la même voix (un index réutilisé) ou un "trou" qui gâche le budget de voix (≤3).</summary>
        public static void Renumber(System.Collections.Generic.IList<EuclidVoice> layers)
        {
            if (layers == null) return;
            for (int i = 0; i < layers.Count; i++) if (layers[i] != null) layers[i].Voice = i;
        }

        /// <summary>Construit le squelette rythmique (une voix par calque, PAS de hauteurs) de tout le module.
        /// Nouvelle adresse : cellule k de l'anneau i → slice <c>k × Beats × spq / Steps_i</c>. spq est choisi
        /// pour que le résultat soit toujours entier (voir <see cref="SpqFor"/>).</summary>
        static MelodicLineModule BuildSkeleton(MelodicPolyModule m)
        {
            int spq = SpqFor(m);
            int beats = CycleBeats(m);
            int cycleSlices = beats * spq;
            int repeats = Math.Max(1, m?.Repeats ?? 1);
            int total = cycleSlices * repeats;
            var notes = new List<Engine.RiffNote>();
            int maxVoice = 0;
            if (m?.Layers != null)
                foreach (var v in m.Layers)
                {
                    if (v == null || v.Voice < 0 || v.Voice >= MelodicLineModule.MaxVoices) continue;
                    maxVoice = Math.Max(maxVoice, v.Voice);
                    if (v.Muted) continue;
                    var pat = v.EffectivePattern(); if (pat == null || pat.Length == 0) continue;
                    int steps = pat.Length;
                    // Positions du cycle pour cet anneau (une passe), puis on répète.
                    var onsetsInCycle = new List<int>();
                    for (int i = 0; i < steps; i++)
                        if (pat[i])
                        {
                            long num = (long)i * beats * spq;
                            onsetsInCycle.Add((int)((num + steps / 2) / steps));
                        }
                    if (onsetsInCycle.Count == 0) continue;
                    onsetsInCycle.Sort();
                    for (int rep = 0; rep < repeats; rep++)
                    {
                        int cycleStart = rep * cycleSlices;
                        for (int idx = 0; idx < onsetsInCycle.Count; idx++)
                        {
                            int s = cycleStart + onsetsInCycle[idx];
                            int nextS = idx + 1 < onsetsInCycle.Count
                                ? cycleStart + onsetsInCycle[idx + 1]
                                : cycleStart + cycleSlices;
                            int len = v.Legato ? (nextS - s) : Math.Max(1, cycleSlices / steps);
                            if (s < 0 || s >= total) continue;
                            notes.Add(new Engine.RiffNote(v.Voice, s, len));
                        }
                    }
                }
            notes.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : a.Note.CompareTo(b.Note));
            var skeleton = new MelodicLineModule { BeatsPerBar = Math.Max(1, total / spq), VoiceCount = Math.Max(1, Math.Min(MelodicLineModule.MaxVoices, maxVoice + 1)) };
            skeleton.SetNotes(notes, spq, total);
            return skeleton;
        }

        /// <summary>Déroule le module en Riff avec les VRAIES hauteurs, choisies par MelodicLineEngine sur
        /// l'harmonie active à <paramref name="startBeat"/>. Retourne null si aucun calque n'a de coup (comme
        /// MelodicLineEngine.GenerateLine pour une ligne mélodique classique vide).</summary>
        public static Riff Generate(MelodicPolyModule m, Timeline.TimelineProject project, Func<Guid, Riff> resolve, Score.KeySignature key, double startBeat, int[] carry = null)
            => Octaved(Timeline.MelodicLineEngine.GenerateLine(BuildSkeleton(m), project, resolve, key, startBeat, carry), m);

        /// <summary>Transpose chaque note dans l'octave demandée pour SA voix. Appliqué après coup : le moteur a
        /// choisi les hauteurs sur l'harmonie réelle (note d'accord sur les temps forts, note de passage ailleurs),
        /// et l'octave ne fait que déplacer le résultat — une transposition en amont fausserait sa conduite des voix.
        /// Le riff produit conserve l'indice de voix sur chaque note, ce qui rend le tri possible.</summary>
        static Riff Octaved(Riff riff, MelodicPolyModule m)
        {
            if (riff?.Notes == null || riff.Notes.Count == 0 || m?.Layers == null) return riff;

            var shift = new int[MelodicLineModule.MaxVoices];
            bool any = false;
            foreach (var v in m.Layers)
                if (v != null && v.Voice >= 0 && v.Voice < shift.Length && v.Octave != 0)
                {
                    shift[v.Voice] = v.Octave * 12;
                    any = true;
                }
            if (!any) return riff;

            var outp = new List<Engine.RiffNote>(riff.Notes.Count);
            foreach (var n in riff.Notes)
            {
                int d = (n.Voice >= 0 && n.Voice < shift.Length) ? shift[n.Voice] : 0;
                int row = n.Note + d;
                // Hors de la tessiture jouable : on replie par octaves plutôt que de perdre la note — un silence
                // inexpliqué serait plus déroutant qu'une note à l'octave voisine.
                while (row < 0) row += 12;
                while (row > 95) row -= 12;
                outp.Add(new Engine.RiffNote(row, n.Start, n.Length) { Voice = n.Voice });
            }
            return new Riff { Name = riff.Name, Notes = outp, LengthSlices = riff.LengthSlices, SlicesPerQuarter = riff.SlicesPerQuarter };
        }
    }
}
