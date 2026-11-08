using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MusicTracker.Engine;
using MusicTracker.Engine.Flow;
using MusicTracker.Engine.Timeline;
using MusicTracker.Localization;

namespace MusicTracker.Controls
{
    /// <summary>Side-effects the chord editor delegates to its host (the timeline screen): re-rendering the timeline,
    /// section-wide restyle/motif propagation, and a modal text prompt.</summary>
    public interface IChordEditorHost
    {
        void Rerender();
        void ApplyMotifToSection(PatternGeneratorModule pg);
        string PromptText(string title, string initial);
        /// <summary>The chord's roman numeral in the song's key, as written on its box ("iv", "V/V", "♭VII").</summary>
        string ChordRoman(PatternGeneratorModule pg);
        /// <summary>The ranked continuations of chord <paramref name="pg"/> where it sits on <paramref name="track"/>
        /// (empty when it is not on a degree of the key).</summary>
        IList<SuggestCand> RankNextChords(TimelineTrack track, PatternGeneratorModule pg, HarmonyMood mood);
        /// <summary>Insert the candidate as a new chord right AFTER <paramref name="pg"/> and select it.</summary>
        void InsertSuggestedChord(TimelineTrack track, PatternGeneratorModule pg, SuggestCand c);
        /// <summary>Append the top-ranked continuation <paramref name="bars"/> times after <paramref name="pg"/>.</summary>
        void ChainAfter(TimelineTrack track, PatternGeneratorModule pg, int bars);
    }

    /// <summary>One next-chord card of the chord editor: the candidate + what the card shows.</summary>
    public sealed class ChordSuggestionCard
    {
        static readonly System.Windows.Media.Brush Tonic = Frozen(0x3F, 0x7F, 0xD9), Subdominant = Frozen(0x3F, 0xA3, 0x7F),
            Dominant = Frozen(0xD9, 0x84, 0x3F), Chromatic = Frozen(0x8E, 0x6B, 0xC9),
            Accent = Frozen(0x1F, 0xB6, 0xC3), Line = Frozen(0x55, 0x55, 0x60);
        static System.Windows.Media.Brush Frozen(byte r, byte g, byte b)
        {
            var br = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b));
            br.Freeze();
            return br;
        }

        /// <summary>Colour of a harmonic function given as a scale degree (0..6), −1 = a chord outside the key.</summary>
        public static System.Windows.Media.Brush FunctionBrush(int degree)
        {
            switch (degree)
            {
                case 0: case 2: case 5: return Tonic;
                case 1: case 3: return Subdominant;
                case 4: case 6: return Dominant;
                default: return Chromatic;
            }
        }

        public ChordSuggestionCard(SuggestCand cand, string name) { Cand = cand; Name = name; }

        public SuggestCand Cand { get; }
        public string Roman => Cand.Label;
        public string Name { get; }
        public string Effect => Cand.Effect;
        /// <summary>Colour of the degree square = the harmonic function (tonic / subdominant / dominant / chromatic).</summary>
        public System.Windows.Media.Brush Fill
            => Cand.Deg < 0 && Cand.Quality == 8 ? Dominant : FunctionBrush(Cand.Deg);   // V/V is a dominant, the rest is borrowed colour
        public System.Windows.Media.Brush Border => Cand.Recommended ? Accent : Line;
        public Thickness BorderThickness => new Thickness(Cand.Recommended ? 2 : 1);
    }

    /// <summary>
    /// Independent editor for ONE chord — HARMONIE SEULE (degré / fondamentale / couleur / suspension / mode forcé
    /// + durée). Tout le « comment on le joue » a migré vers le module « Articulation d'accord » : plus de grille
    /// d'accompagnement ni de cellule mélodique ici, donc plus rien à configurer depuis le code (tout est en XAML).
    /// </summary>
    public partial class ChordEditorControl : UserControl
    {
        ChordEditorViewModel vm;

        public ChordEditorControl() { InitializeComponent(); Unloaded += (s, e) => StopPreview(); }

        /// <summary>Point the editor at a chord (called when the user selects a chord object).</summary>
        public void Show(TimelineProject project, TimelineTrack track, PatternGeneratorModule pg, IChordEditorHost host)
        {
            vm = new ChordEditorViewModel(project, track, pg, host);
            DataContext = vm;
        }

        // ---- the next-chord cards ----
        NAudio.Wave.WaveOutEvent waveOut;

        static bool CardOf(object sender, out ChordSuggestionCard card)
        {
            card = (sender as FrameworkElement)?.DataContext as ChordSuggestionCard;
            return card != null;
        }

        void Card_Pick(object sender, MouseButtonEventArgs e)
        {
            if (vm == null || !CardOf(sender, out var card)) return;
            e.Handled = true;
            StopPreview();
            vm.InsertAfter(card.Cand);   // the host selects the new chord → this editor is replaced by the new chord's
        }

        void Card_Replace(object sender, MouseButtonEventArgs e)
        {
            if (vm == null || !CardOf(sender, out var card)) return;
            e.Handled = true;
            StopPreview();
            vm.ReplaceWith(card.Cand);
        }

        void Card_Enter(object sender, MouseEventArgs e) { if (CardOf(sender, out var card)) Preview(card.Cand); }
        void Card_Leave(object sender, MouseEventArgs e) => StopPreview();

        // Hovering a card PLAYS the chord (as the former suggestion dialog did). Silent when no SoundFont is loaded:
        // a hover must never pop the "missing SoundFont" prompt.
        void Preview(SuggestCand c)
        {
            StopPreview();
            if (vm == null || !SoundFontGuard.IsReady) return;
            var preset = InstrumentCatalog.GetPreset(vm.Track.Instrument);
            if (preset == null) return;
            try
            {
                HarmonySuggest.ChordOf(vm.Key, c, out int rootPc, out int quality);
                var riff = PatternGenerator.Generate(new PatternGeneratorModule { Root = rootPc, Quality = quality, Octave = 4, Style = 0, BeatsPerBar = 2, Repeats = 1 });
                var ctx = new FlowContext { GmProgram = preset.PatchNumber, Drum = preset.BankNumber == InstrumentCatalog.DrumIndex, Bpm = 88 };
                waveOut = new NAudio.Wave.WaveOutEvent { DesiredLatency = 120 };
                waveOut.Init(new LoopingRiffProvider(() => riff, ctx)); waveOut.Play();
            }
            catch { StopPreview(); }
        }

        void StopPreview()
        {
            if (waveOut != null) { try { waveOut.Stop(); waveOut.Dispose(); } catch { } waveOut = null; }
        }

        static System.Windows.Media.Brush Br(string hex) => (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString(hex);
        static string[] TakeBuiltin(string[] all, int count)
        {
            var r = new string[Math.Max(0, count)];
            for (int i = 0; i < r.Length && i < all.Length; i++) r[i] = all[i];
            return r;
        }
    }

    /// <summary>Bindable state for the chord editor. Mutates the <see cref="PatternGeneratorModule"/> in place, runs the
    /// colour→quality derivation, voice-leads the chain, and asks the view to rebuild the rhythm grid / re-render.</summary>
    public sealed class ChordEditorViewModel : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        public event Action GridRefreshNeeded;   // the chord rhythm grid must be rebuilt
        public event Action MelodicRefreshNeeded; // the melodic-cell grid must be rebuilt (chord pitches / octave / anchor changed)

        readonly TimelineProject project;
        readonly TimelineTrack track;
        readonly PatternGeneratorModule pg;
        readonly IChordEditorHost host;

        public ChordEditorViewModel(TimelineProject project, TimelineTrack track, PatternGeneratorModule pg, IChordEditorHost host)
        {
            this.project = project; this.track = track; this.pg = pg; this.host = host;
            pg.BeatsPerBar = Math.Max(1, pg.BeatsPerBar * Math.Max(1, pg.Repeats));   // normalize legacy chords once
            pg.Repeats = 1;
            if (pg.Degree < 0) SyncColourTrio();
            RebuildStyleList();
            ApplyMotifCommand = new RelayCommand(_ => ApplyMotif());
            ChainCommand = new RelayCommand(_ => host.ChainAfter(track, pg, 4));
            RefreshSuggestions();
        }

        // ---- the chord on the keyboard + its name card ----
        /// <summary>The chord AS VOICED: the inversion and octave the voice leading chose for it (see
        /// <see cref="ChordDegrees.Revoice"/>), spread when the voicing is open.</summary>
        public int[] KeyboardNotes => PatternGenerator.ChordNotes(pg.Root, pg.Octave, pg.Quality, pg.Inversion, pg.OpenVoicing);
        public string ChordTitle => Engine.Score.KeySig.SpellPc(pg.Root, Key) + " " + TimelineHelper.Get(PatternGenerator.QualityNames, pg.Quality);
        public string ChordRoman => host.ChordRoman(pg);
        // Its function as a scale degree: the locked degree, a secondary dominant (→ dominant), the degree its root
        // sits on, else −1 (a root outside the key).
        int FunctionDegree
        {
            get
            {
                if (pg.Degree >= 0) return pg.Degree;
                int rootPc = ((pg.Root % 12) + 12) % 12;
                if (MusicTheory.SecondaryDominantTarget(Key, rootPc, pg.Quality) >= 0) return 4;
                int d = MusicTheory.DegreeOf(Key, rootPc);
                return ((MusicTheory.DiatonicChord(Key, d).root % 12) + 12) % 12 == rootPc ? d : -1;
            }
        }
        public System.Windows.Media.Brush FunctionBrush => ChordSuggestionCard.FunctionBrush(FunctionDegree);
        public string FunctionName
        {
            get
            {
                switch (FunctionDegree)
                {
                    case 0: case 2: case 5: return Loc.T("FonctionTonique");
                    case 1: case 3: return Loc.T("FonctionSousDominante");
                    case 4: case 6: return Loc.T("FonctionDominante");
                    default: return Loc.T("FonctionHorsTonalite");
                }
            }
        }
        void RefreshChordView()
        {
            Raise(nameof(KeyboardNotes)); Raise(nameof(ChordTitle)); Raise(nameof(ChordRoman));
            Raise(nameof(FunctionBrush)); Raise(nameof(FunctionName));
        }

        // ---- next-chord co-pilot (the cards) ----
        public IReadOnlyList<string> MoodNames { get; } = new[] { "Auto", Loc.T("Joyeux"), Loc.T("Serein"), Loc.T("Melancolique"), Loc.T("Nostalgique"), Loc.T("Epique"), Loc.T("Lumineux"), "Jazzy" };
        static int mood;   // kept from one chord to the next: the mood is a choice for the song, not for a chord
        public int MoodIndex { get => mood; set { if (value < 0 || value == mood) return; mood = value; RefreshSuggestions(); } }
        const int MaxCards = 6;
        List<ChordSuggestionCard> suggestions = new List<ChordSuggestionCard>();
        public IReadOnlyList<ChordSuggestionCard> Suggestions => suggestions;
        public Visibility NoSuggestionVisibility => suggestions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        public ICommand ChainCommand { get; }

        void RefreshSuggestions()
        {
            var key = Key;
            var list = new List<ChordSuggestionCard>();
            foreach (var c in host.RankNextChords(track, pg, (HarmonyMood)mood))
            {
                if (list.Count == MaxCards) break;
                HarmonySuggest.ChordOf(key, c, out int root, out int quality);
                list.Add(new ChordSuggestionCard(c, Dialogs.ChordSuggestionDialog.ChordName(key, c, root, quality)));
            }
            suggestions = list;
            Raise(nameof(Suggestions)); Raise(nameof(NoSuggestionVisibility));
        }

        /// <summary>Click on a card: that chord is added right after this one.</summary>
        public void InsertAfter(SuggestCand c) => host.InsertSuggestedChord(track, pg, c);

        /// <summary>Right-click on a card: THIS chord becomes it (an edit of the chord, like its combos).</summary>
        public void ReplaceWith(SuggestCand c)
        {
            TimelineHelper.ApplyChordChoice(pg, Key, pg.Degree >= 0, c);
            Raise(string.Empty);   // every combo re-reads the chord
            Changed();
        }

        public PatternGeneratorModule Pg => pg;
        public TimelineTrack Track => track;
        public IChordEditorHost Host => host;
        public Engine.Score.KeySignature Key => project.Key ?? new Engine.Score.KeySignature();
        public List<UserChordStyle> UserStyles => project.UserChordStyles ?? (project.UserChordStyles = new List<UserChordStyle>());

        // ---- item sources ----
        public IReadOnlyList<string> RootNames => PatternGenerator.RootNames;
        // Diatonic degrees, then the SECONDARY DOMINANTS (V/x) so they can be picked directly instead of being
        // hand-built as a "manual" chord. The V/x list is KEY-DEPENDENT: a degree whose diatonic triad is diminished
        // cannot be tonicised (no V/ii in a minor key, where ii° is diminished), so it isn't offered.
        // The list itself lives in ChordDegreeChoices, SHARED with the polyrhythmic chord editor so both offer exactly
        // the same vocabulary for a given key.
        ChordDegreeChoices choices;
        ChordDegreeChoices Choices => choices ?? (choices = ChordDegreeChoices.For(project?.Key));
        public IReadOnlyList<string> DegreeNames => Choices.Names;
        public IReadOnlyList<string> ColourNames => MusicTheory.DiatonicColourNames;
        public IReadOnlyList<string> SuspensionNames => MusicTheory.SuspensionNames;
        public IReadOnlyList<string> ModeNames => MusicTheory.ModeOverrideNames;
        // Ordre d'AFFICHAGE demandé : aucun / auto / haut proche / bas proche / fixe. La numérotation du MODÈLE reste
        // celle d'origine (0 aucun, 1 auto, 2 basse, 3 haut, 4 fixe) pour ne pas réinterpréter les projets existants —
        // d'où la table de correspondance ci-dessous plutôt qu'un simple index.
        public IReadOnlyList<string> VoiceLeadNames { get; } = new[] { Loc.T("AucunPositionFond"), Loc.T("AutoMouvementMini"), Loc.T("HautProche"), Loc.T("BasseProche"), Loc.T("Fixe") };
        static readonly int[] VlUiToModel = { 0, 1, 3, 2, 4 };
        public IReadOnlyList<string> BassNames { get; } = new[] { Loc.T("Aucune"), Loc.T("ParMesureTenue"), Loc.T("ParTemps") };
        public IReadOnlyList<string> ClimbNames { get; } = new[] { Loc.T("ArpegeMontant"), Loc.T("ArpegeDescendant"), Loc.T("Alberti153"), Loc.T("Mixte") };
        public IReadOnlyList<string> HeldNames { get; } = new[] { Loc.T("NoteSeule"), Loc.T("AccordPlaque"), Loc.T("FondamentaleQuinte"), Loc.T("FondamentaleTierce") };

        string[] styleNames; int styleIndex;
        public IReadOnlyList<string> StyleNames => styleNames;
        public int StyleIndex
        {
            get => styleIndex;
            set
            {
                if (value < 0 || value == styleIndex) return;
                int builtinCount = PatternGenerator.StyleNames.Length;
                if (value < builtinCount) { pg.Style = value; pg.UserStyleName = null; }
                else
                {
                    int u = value - builtinCount;
                    if (u >= 0 && u < UserStyles.Count)
                    {
                        pg.Style = PatternGenerator.CustomStyle; pg.UserStyleName = UserStyles[u].Name;
                        pg.SetCustom(UserStyles[u].Slices, UserStyles[u].Spb);
                        pg.CustomNotes = UserStyles[u].Notes != null ? new List<RiffNote>(UserStyles[u].Notes) : null;
                        if (UserStyles[u].Beats > 0) pg.BeatsPerBar = UserStyles[u].Beats;
                    }
                }
                // Selecting a style (builtin OR user) affects ONLY the current chord. Propagation to every chord sharing
                // a user style is done EXPLICITLY via the "Appliquer le motif à « … »" button (ApplyMotifToSection).
                styleIndex = value;
                Raise(nameof(StyleIndex)); Raise(nameof(Beats)); Raise(nameof(ApplyMotifVisibility)); Raise(nameof(ApplyMotifText));
                Changed();
            }
        }

        // ---- degree / note / quality colours ----
        public int DegreeIndex
        {
            // A "manual" chord that actually IS a secondary dominant shows up as such (V/V rather than Manuel).
            get => Choices.IndexOf(pg.Degree, pg.Root, pg.Quality);
            set
            {
                // Secondary dominant: a chromatic chord, so it is stored with a fixed root (Degree = −1) placed a
                // fifth above the degree it tonicises, with a DOMINANT 7th quality (see ChordDegreeChoices).
                if (Choices.TrySecondary(value, out int secRoot, out int secQuality))
                {
                    pg.Degree = -1;
                    pg.Root = secRoot;
                    ApplyDiatonic();
                    if (secQuality >= 0) pg.Quality = secQuality;
                    // Align the colour trio with the dominant 7th we just wrote, so the colour/suspension/mode combos
                    // reflect the chord instead of showing a stale triad the next edit would silently apply.
                    SyncColourTrio();
                    Raise(nameof(RootEnabled)); Raise(nameof(RootIndex)); Changed();
                    return;
                }
                pg.Degree = value <= 0 ? -1 : value - 1; ApplyDiatonic(); Raise(nameof(RootEnabled)); Changed();
            }
        }
        public bool RootEnabled => pg.Degree < 0;
        public int RootIndex { get => pg.Root; set { if (pg.Root == value) return; pg.Root = value; Changed(); } }
        public int ColourIndex { get => pg.DiatonicColour; set { if (pg.DiatonicColour == value) return; pg.DiatonicColour = value; ApplyDiatonic(); Changed(); } }
        public int SuspensionIndex { get => pg.Suspension; set { if (pg.Suspension == value) return; pg.Suspension = value; ApplyDiatonic(); Changed(); } }
        public int ModeIndex { get => pg.ModeOverride; set { if (pg.ModeOverride == value) return; pg.ModeOverride = value; ApplyDiatonic(); Changed(); } }
        public int Octave { get => pg.Octave; set { if (pg.Octave == value) return; pg.Octave = value; Changed(); } }
        public int Inversion { get => pg.Inversion; set { if (pg.Inversion == value) return; pg.Inversion = Math.Max(0, value); Changed(); } }

        // ---- other options ----
        public int VoiceLeadIndex
        {
            get { int i = Array.IndexOf(VlUiToModel, pg.VoiceLeadMode); return i < 0 ? 0 : i; }
            set
            {
                if (value < 0 || value >= VlUiToModel.Length) return;
                int m = VlUiToModel[value];
                if (pg.VoiceLeadMode == m) return;
                pg.VoiceLeadMode = m;
                Raise(nameof(FixedInversionVisibility));
                Changed();
            }
        }

        /// <summary>Le renversement manuel n'est proposé que dans le mode « fixe ».</summary>
        public Visibility FixedInversionVisibility => pg.VoiceLeadMode == 4 ? Visibility.Visible : Visibility.Collapsed;
        public bool OpenVoicing { get => pg.OpenVoicing; set { if (pg.OpenVoicing == value) return; pg.OpenVoicing = value; Changed(); } }
        public int BassIndex { get => !pg.Bass ? 0 : (pg.BassPerBeat ? 2 : 1); set { pg.Bass = value > 0; pg.BassPerBeat = value == 2; Changed(); } }
        public int ClimbIndex { get => pg.ClimbMode; set { if (pg.ClimbMode == value) return; pg.ClimbMode = value; Changed(); } }
        public int HeldIndex { get => pg.HeldMode; set { if (pg.HeldMode == value) return; pg.HeldMode = value; Changed(); } }
        public bool Halve { get => pg.HalveDurations; set { if (pg.HalveDurations == value) return; pg.HalveDurations = value; Changed(); } }
        public int Beats { get => pg.BeatsPerBar; set { int v = Math.Max(1, value); if (pg.BeatsPerBar == v) return; pg.BeatsPerBar = v; pg.Repeats = 1; Changed(); } }

        // ---- melodic cell ----
        public IReadOnlyList<string> MelodicAnchorNames { get; } = new[] { Loc.T("ToniqueDeLAccord"), Loc.T("SelonLeRenversement") };
        public int MelodicOctave { get => pg.MelodicOctave; set { if (pg.MelodicOctave == value) return; pg.MelodicOctave = value; MelodicChanged(); } }
        public int MelodicAnchorIndex { get => pg.MelodicAnchor; set { if (pg.MelodicAnchor == value) return; pg.MelodicAnchor = value; MelodicChanged(); } }
        public bool MelodicPreserve { get => pg.MelodicPreserve; set { if (pg.MelodicPreserve == value) return; pg.MelodicPreserve = value; Raise(nameof(MelodicPreserve)); } }

        public Visibility ApplyMotifVisibility => string.IsNullOrEmpty(pg.UserStyleName) ? Visibility.Collapsed : Visibility.Visible;
        public string ApplyMotifText => Loc.T("AppliquerLeMotifA") + pg.UserStyleName + " »";
        public ICommand ApplyMotifCommand { get; }

        // Sync the "Nombre de temps" display from a grid edit WITHOUT triggering a rebuild (the grid is mid-edit).
        public void SetBeatsFromGrid(int beats) { pg.BeatsPerBar = Math.Max(1, beats); Raise(nameof(Beats)); }

        public void ApplyMotif() => host.ApplyMotifToSection(pg);

        public void SaveStyle(SequencerSlice[] slices, int spb, int beats, List<RiffNote> notes)
        {
            string name = host.PromptText(Loc.T("EnregistrerLeStyleDAccompagnement"), Loc.T("MonStyle"));
            if (string.IsNullOrWhiteSpace(name)) return;
            name = name.Trim();
            var existing = UserStyles.FindIndex(u => u.Name == name);
            var entry = new UserChordStyle { Name = name, Slices = slices, Spb = spb, Beats = beats, Notes = notes };
            if (existing >= 0) UserStyles[existing] = entry; else UserStyles.Add(entry);
            pg.UserStyleName = name;
            RebuildStyleList();
            Raise(nameof(ApplyMotifVisibility)); Raise(nameof(ApplyMotifText));
            GridRefreshNeeded?.Invoke();
        }

        // ---- internals ----
        // Read the (colour, suspension, mode) trio back from a FIXED chord's actual quality, so the three combos show
        // what the chord really is. Skipped when the colour system can't express that quality (exotic tensions): the
        // trio is then left alone rather than reset to a triad it doesn't describe.
        void SyncColourTrio()
        {
            var d = ChordDegrees.ColourForQuality(pg.Quality);
            if (ChordDegrees.QualityForColour(d.colour, d.suspension, d.mode) != pg.Quality) return;
            pg.DiatonicColour = d.colour; pg.Suspension = d.suspension; pg.ModeOverride = d.mode;
            Raise(nameof(ColourIndex)); Raise(nameof(SuspensionIndex)); Raise(nameof(ModeIndex));
        }

        void ApplyDiatonic()
        {
            if (pg.Degree < 0)
            {
                pg.Quality = ChordDegrees.QualityForColour(pg.DiatonicColour, pg.Suspension, pg.ModeOverride);
                return;
            }
            var ch = MusicTheory.DiatonicChord(project.Key ?? new Engine.Score.KeySignature(), pg.Degree, pg.DiatonicColour, pg.Suspension, pg.ModeOverride);
            pg.Root = ch.root; pg.Quality = ch.quality;
            Raise(nameof(RootIndex));
        }

        void RebuildStyleList()
        {
            int builtinCount = PatternGenerator.StyleNames.Length;
            var us = UserStyles;
            styleNames = new string[builtinCount + us.Count];
            Array.Copy(PatternGenerator.StyleNames, styleNames, builtinCount);
            for (int i = 0; i < us.Count; i++) styleNames[builtinCount + i] = us[i].Name;
            styleIndex = pg.Style;
            if (!string.IsNullOrEmpty(pg.UserStyleName)) { int p = us.FindIndex(u => u.Name == pg.UserStyleName); if (p >= 0) styleIndex = builtinCount + p; }
            Raise(nameof(StyleNames)); Raise(nameof(StyleIndex));
        }

        // A field changed: voice-lead the chain, rebuild both grids (melodic pitches depend on the chord), re-render.
        void Changed() { ChordDegrees.Revoice(track); GridRefreshNeeded?.Invoke(); MelodicRefreshNeeded?.Invoke(); host.Rerender(); RefreshChordView(); RefreshSuggestions(); }
        // A melodic-only field changed: just rebuild the melodic grid + re-render (no need to touch the chord chain).
        void MelodicChanged() { MelodicRefreshNeeded?.Invoke(); host.Rerender(); }

        void Raise(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

        sealed class RelayCommand : ICommand
        {
            readonly Action<object> exec;
            public RelayCommand(Action<object> exec) { this.exec = exec; }
            public bool CanExecute(object parameter) => true;
            public void Execute(object parameter) => exec(parameter);
            public event EventHandler CanExecuteChanged { add { } remove { } }
        }
    }
}
