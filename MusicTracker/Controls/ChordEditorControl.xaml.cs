using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MusicTracker.Engine;
using MusicTracker.Engine.Flow;
using MusicTracker.Engine.Timeline;

namespace MusicTracker.Controls
{
    /// <summary>Side-effects the chord editor delegates to its host (the timeline screen): re-rendering the timeline,
    /// section-wide restyle/motif propagation, and a modal text prompt.</summary>
    public interface IChordEditorHost
    {
        void Rerender();
        void ApplyMotifToSection(PatternGeneratorModule pg);
        string PromptText(string title, string initial);
    }

    /// <summary>
    /// Independent editor for ONE chord. The fields are declared in XAML and bound to <see cref="ChordEditorViewModel"/>;
    /// only the hand-drawn rhythm grid (a custom canvas control) is configured from code.
    /// </summary>
    public partial class ChordEditorControl : UserControl
    {
        ChordEditorViewModel vm;

        public ChordEditorControl() { InitializeComponent(); }

        /// <summary>Point the editor at a chord (called when the user selects a chord object).</summary>
        public void Show(TimelineProject project, TimelineTrack track, PatternGeneratorModule pg, IChordEditorHost host)
        {
            vm = new ChordEditorViewModel(project, track, pg, host);
            vm.GridRefreshNeeded += RefreshGrid;
            vm.MelodicRefreshNeeded += RefreshMelodicGrid;
            DataContext = vm;
            RefreshGrid();
            RefreshMelodicGrid();
        }

        // The optional MELODIC CELL: a 2nd grid whose 14 rows are the diatonic degrees (1..7 over 2 octaves), polyphonic.
        void RefreshMelodicGrid()
        {
            var pg = vm.Pg;
            var labels = new[] { "1", "2", "3", "4", "5", "6", "7", "1'", "2'", "3'", "4'", "5'", "6'", "7'" };
            var rg = new RhythmGridControl();
            Func<SequencerSlice[], int, Riff> mk = (gr, gs) =>
            {
                var t = new PatternGeneratorModule { Root = pg.Root, Quality = pg.Quality, Inversion = pg.Inversion, MelodicOctave = pg.MelodicOctave, MelodicAnchor = pg.MelodicAnchor, BeatsPerBar = pg.BeatsPerBar, Repeats = 1 };
                t.SetMelodicNotes(rg.CurrentNotes(), gs, rg.Beats * gs);
                return PatternGenerator.GenerateMelodic(t, vm.Key);
            };
            rg.Configure(labels, pg.BeatsPerBar, pg.MelodicSlicesPerQuarter > 0 ? pg.MelodicSlicesPerQuarter : 4, pg.MelodicSlices,
                new string[0], (st, b) => null, PatternGenerator.SlicesPerQuarter, mk, InstrumentCatalog.GetPreset(vm.Track.Instrument),
                noteList: true, existingNotes: pg.MelodicNotes);
            bool dirty = false;
            rg.GridChanged += () => { pg.SetMelodicNotes(rg.CurrentNotes(), rg.Spb, rg.Beats * rg.Spb); dirty = true; };
            rg.Unloaded += (s, e) => { if (dirty) { dirty = false; vm.Host.Rerender(); } };
            MelodicHost.Content = rg;
        }

        // The rhythm grid is a canvas-based custom control configured procedurally (not practically bindable).
        void RefreshGrid()
        {
            var pg = vm.Pg;
            if (pg.Style != PatternGenerator.CustomStyle)
            {
                GridHost.Content = new TextBlock { Text = "Choisis le style « Personnalisé… » pour éditer le rythme à la main.", Foreground = Br("#888888"), FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(10) };
                return;
            }
            var chord = PatternGenerator.ChordNotes(pg.Root, pg.Octave, pg.Quality, pg.Inversion);
            var labels = new[] { "Basse", "1", "3", "5", "7", "1'", "9", "3'", "5'", "7'", "9'" }; // rows in PITCH order (9 > 1')
            var builtin = TakeBuiltin(PatternGenerator.StyleNames, PatternGenerator.CustomStyle);
            var userStyles = vm.UserStyles;
            var styleNames = new string[builtin.Length + userStyles.Count];
            Array.Copy(builtin, styleNames, builtin.Length);
            for (int i = 0; i < userStyles.Count; i++) styleNames[builtin.Length + i] = userStyles[i].Name;

            Func<int, int, SequencerSlice[]> seedFunc = (st, b) =>
                st < builtin.Length ? PatternGenerator.VoiceBarForCustom(st, b, chord.Length)
                                    : (st - builtin.Length < userStyles.Count ? userStyles[st - builtin.Length].Slices : null);
            Func<int, int> seedSpbFunc = st =>
                st >= builtin.Length && st - builtin.Length < userStyles.Count ? Math.Max(1, userStyles[st - builtin.Length].Spb)
                                                                              : PatternGenerator.SlicesPerQuarter;
            var rg = new RhythmGridControl();
            Func<SequencerSlice[], int, Riff> mk = (gr, gs) => { var t = new PatternGeneratorModule { Root = pg.Root, Octave = pg.Octave, Quality = pg.Quality, Inversion = pg.Inversion, OpenVoicing = pg.OpenVoicing, Style = PatternGenerator.CustomStyle, BeatsPerBar = pg.BeatsPerBar, Repeats = 1 }; t.SetCustom(gr, gs); t.CustomNotes = rg.CurrentNotes(); return PatternGenerator.Generate(t); };
            Action onSaveStyle = () => vm.SaveStyle(rg.CurrentGrid(), rg.Spb, rg.Beats, rg.CurrentNotes());
            Action onApplyToSection = !string.IsNullOrEmpty(pg.UserStyleName) ? (Action)vm.ApplyMotif : null;
            rg.Configure(labels, pg.BeatsPerBar, pg.CustomSlicesPerQuarter > 0 ? pg.CustomSlicesPerQuarter : 4, pg.CustomSlices, styleNames, seedFunc, PatternGenerator.SlicesPerQuarter, mk, InstrumentCatalog.GetPreset(vm.Track.Instrument), seedSpbFunc, onSaveStyle, noteList: true, existingNotes: pg.CustomNotes, onApplyToSection: onApplyToSection);
            bool chordDirty = false;
            rg.GridChanged += () =>
            {
                pg.SetCustomNotes(rg.CurrentNotes(), rg.Spb, rg.Beats * rg.Spb);
                vm.SetBeatsFromGrid(Math.Max(1, rg.Beats));   // sync the "Nombre de temps" field without a full rebuild
                chordDirty = true;
            };
            rg.Unloaded += (s, e) => { if (chordDirty) { chordDirty = false; vm.Host.Rerender(); } };
            GridHost.Content = rg;
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
            if (pg.Degree < 0) { var d = ChordDegrees.ColourForQuality(pg.Quality); pg.DiatonicColour = d.colour; pg.Suspension = d.suspension; pg.ModeOverride = d.mode; }
            RebuildStyleList();
            ApplyMotifCommand = new RelayCommand(_ => ApplyMotif());
        }

        public PatternGeneratorModule Pg => pg;
        public TimelineTrack Track => track;
        public IChordEditorHost Host => host;
        public Engine.Score.KeySignature Key => project.Key ?? new Engine.Score.KeySignature();
        public List<UserChordStyle> UserStyles => project.UserChordStyles ?? (project.UserChordStyles = new List<UserChordStyle>());

        // ---- item sources ----
        public IReadOnlyList<string> RootNames => PatternGenerator.RootNames;
        public IReadOnlyList<string> DegreeNames { get; } = new[] { "Manuel (accord fixe)", "I", "ii", "iii", "IV", "V", "vi", "vii" };
        public IReadOnlyList<string> ColourNames => MusicTheory.DiatonicColourNames;
        public IReadOnlyList<string> SuspensionNames => MusicTheory.SuspensionNames;
        public IReadOnlyList<string> ModeNames => MusicTheory.ModeOverrideNames;
        public IReadOnlyList<string> VoiceLeadNames { get; } = new[] { "Aucun (position fond.)", "Auto (mouvement mini)", "Basse proche", "Haut proche" };
        public IReadOnlyList<string> BassNames { get; } = new[] { "Aucune", "Par mesure (tenue)", "Par temps" };
        public IReadOnlyList<string> ClimbNames { get; } = new[] { "Arpège montant", "Arpège descendant", "Alberti (1-5-3…)", "Mixte" };
        public IReadOnlyList<string> HeldNames { get; } = new[] { "Note seule", "Accord plaqué", "Fondamentale + quinte", "Fondamentale + tierce" };

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
            get => pg.Degree < 0 ? 0 : pg.Degree + 1;
            set { pg.Degree = value <= 0 ? -1 : value - 1; ApplyDiatonic(); Raise(nameof(RootEnabled)); Changed(); }
        }
        public bool RootEnabled => pg.Degree < 0;
        public int RootIndex { get => pg.Root; set { if (pg.Root == value) return; pg.Root = value; Changed(); } }
        public int ColourIndex { get => pg.DiatonicColour; set { if (pg.DiatonicColour == value) return; pg.DiatonicColour = value; ApplyDiatonic(); Changed(); } }
        public int SuspensionIndex { get => pg.Suspension; set { if (pg.Suspension == value) return; pg.Suspension = value; ApplyDiatonic(); Changed(); } }
        public int ModeIndex { get => pg.ModeOverride; set { if (pg.ModeOverride == value) return; pg.ModeOverride = value; ApplyDiatonic(); Changed(); } }
        public int Octave { get => pg.Octave; set { if (pg.Octave == value) return; pg.Octave = value; Changed(); } }
        public int Inversion { get => pg.Inversion; set { if (pg.Inversion == value) return; pg.Inversion = Math.Max(0, value); Changed(); } }

        // ---- other options ----
        public int VoiceLeadIndex { get => pg.VoiceLeadMode; set { if (pg.VoiceLeadMode == value) return; pg.VoiceLeadMode = value; Changed(); } }
        public bool OpenVoicing { get => pg.OpenVoicing; set { if (pg.OpenVoicing == value) return; pg.OpenVoicing = value; Changed(); } }
        public int BassIndex { get => !pg.Bass ? 0 : (pg.BassPerBeat ? 2 : 1); set { pg.Bass = value > 0; pg.BassPerBeat = value == 2; Changed(); } }
        public int ClimbIndex { get => pg.ClimbMode; set { if (pg.ClimbMode == value) return; pg.ClimbMode = value; Changed(); } }
        public int HeldIndex { get => pg.HeldMode; set { if (pg.HeldMode == value) return; pg.HeldMode = value; Changed(); } }
        public bool Halve { get => pg.HalveDurations; set { if (pg.HalveDurations == value) return; pg.HalveDurations = value; Changed(); } }
        public int Beats { get => pg.BeatsPerBar; set { int v = Math.Max(1, value); if (pg.BeatsPerBar == v) return; pg.BeatsPerBar = v; pg.Repeats = 1; Changed(); } }

        // ---- melodic cell ----
        public IReadOnlyList<string> MelodicAnchorNames { get; } = new[] { "Tonique de l'accord", "Selon le renversement" };
        public int MelodicOctave { get => pg.MelodicOctave; set { if (pg.MelodicOctave == value) return; pg.MelodicOctave = value; MelodicChanged(); } }
        public int MelodicAnchorIndex { get => pg.MelodicAnchor; set { if (pg.MelodicAnchor == value) return; pg.MelodicAnchor = value; MelodicChanged(); } }
        public bool MelodicPreserve { get => pg.MelodicPreserve; set { if (pg.MelodicPreserve == value) return; pg.MelodicPreserve = value; Raise(nameof(MelodicPreserve)); } }

        public Visibility ApplyMotifVisibility => string.IsNullOrEmpty(pg.UserStyleName) ? Visibility.Collapsed : Visibility.Visible;
        public string ApplyMotifText => "Appliquer le motif à\n« " + pg.UserStyleName + " »";
        public ICommand ApplyMotifCommand { get; }

        // Sync the "Nombre de temps" display from a grid edit WITHOUT triggering a rebuild (the grid is mid-edit).
        public void SetBeatsFromGrid(int beats) { pg.BeatsPerBar = Math.Max(1, beats); Raise(nameof(Beats)); }

        public void ApplyMotif() => host.ApplyMotifToSection(pg);

        public void SaveStyle(SequencerSlice[] slices, int spb, int beats, List<RiffNote> notes)
        {
            string name = host.PromptText("Enregistrer le style d'accompagnement", "Mon style");
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
        void ApplyDiatonic()
        {
            if (pg.Degree < 0)
            {
                var tk = new Engine.Score.KeySignature { TonicLetter = 0, Accidental = 0, Mode = 0 };
                pg.Quality = MusicTheory.DiatonicChord(tk, 0, pg.DiatonicColour, pg.Suspension, pg.ModeOverride).quality;
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
        void Changed() { ChordDegrees.Revoice(track); GridRefreshNeeded?.Invoke(); MelodicRefreshNeeded?.Invoke(); host.Rerender(); }
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
