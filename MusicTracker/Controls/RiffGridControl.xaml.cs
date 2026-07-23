using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using NAudio.Wave;
using MusicTracker.Engine;
using MusicTracker.Engine.Flow;
using MeltySynth;

namespace MusicTracker.Controls
{
    /// <summary>
    /// Piano-roll editor for a single <see cref="Riff"/>. Same idea as the chord/drum grid
    /// (<see cref="RhythmGridControl"/>) but with one row per playable note (0..95, matching the riff
    /// player's note range). Grid lines are drawn once and only active notes get a rectangle, so a long
    /// riff stays light. Raises <see cref="GridChanged"/> so the host persists live.
    /// </summary>
    public partial class RiffGridControl : UserControl
    {
        // Note rows match the riff player (RiffPlayer iterates note 0..95; freq = Utils.Frequencies[note]).
        const int NoteCount = 96;
        const int BeatsPerBar = 4;                 // measure = 4 beats (4/4) -> thicker ruler tick
        const double CellW = 26, CellH = 26, LabelW = 52, RulerH = 18; // pads sized/spaced like RhythmGridControl

        int beats = 4, spb = 4;                    // spb = slices per beat (= riff.SlicesPerQuarter)

        /// <summary>Project time-signature denominator (set by the host): 8 = compound → snap to 1/6 of a beat
        /// (keeps triplets); anything else = simple → snap to the configured fraction (default 1/8).</summary>
        public int MeterDen { get; set; } = 4;
        // Effective keyboard/audio entry-snap fraction of a beat: compound x/8 → 1/6, simple → user setting (1/8).
        double SnapFraction() => MeterDen == 8 ? 1.0 / 6.0 : AppSettings.Instance.RiffSnapFraction;

        bool Ternary => MeterDen == 8;
        /// <summary>The beat count SHOWN in the toolbar (display only — the riff's real <c>beats</c> is unchanged).
        /// Binary = the temps count; ternary = temps×3 (so one 6/8 bar of 2 temps reads as "6" croches).</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public int DisplayTime
        {
            get => Ternary ? beats * 3 : beats;
            set => beats = Math.Max(1, Ternary ? value / 3 : value);
        }
        readonly List<RiffNote> notes = new List<RiffNote>(); // canonical model: 1 entry/note (distinguishes adjacent same-pitch notes)
        GridSurface gridSurface;                   // draws the grid in retained DrawingVisuals (not 1 UIElement/cell)
        int startSlice;

        // Mouse gesture: pressing an OFF cell DRAWS (extend on drag, merge same-pitch on overlap); pressing an
        // ON cell ERASES the touched cells (shrink / split / delete). Drag acts on cell change; pitch row is fixed.
        enum Drag { None, Draw, Erase }
        Drag dragMode = Drag.None;
        int dragRow, drawMinCol, drawMaxCol, lastDragCol, drawIdx = -1; // draw spans [min..max] reached (only grows)

        double editorBpm = 100;                    // tempo (BPM) for the ▶ preview AND the MIDI/audio cursor advance (editable)

        Rectangle cursorLine;
        Polygon startMarker;       // blue down-pointing handle on the ruler: drag to set the play start
        bool draggingMarker;

        Preset previewInstrument;
        Riff backingRiff;                 // optional chord-line backing played UNDER the riff (clamped to it), null = none
        Preset backingInstrument;

        WaveOutEvent waveOut;
        LoopingRiffProvider provider;
        Func<int> currentSliceFn;         // playhead source (riff-only or the mix), set when playback starts
        System.Windows.Threading.DispatcherTimer playTimer;

        // Optional metronome (toggle), ticking at the editor tempo.
        WaveOutEvent metroOut;
        MetronomeProvider metro;
        System.Windows.Threading.DispatcherTimer metroTimer;
        int metroBeat;
        int metroCursorBeat = int.MinValue; // last beat clicked from the cursor during a tempo take
        const double MetroAnticipateSec = 0.05; // fire the click slightly early so it SOUNDS on the beat (audio latency)

        // Audition while editing: notes are sounded through the Windows MIDI synth (MidiOut) — native polyphony
        // and very low latency. (The ▶ preview still renders the REAL instrument; this is just edit feedback.)
        NAudio.Midi.MidiOut midiOut;
        readonly HashSet<int> midiOn = new HashSet<int>();
        int mousePreviewNote = -1;
        int auditionChannel = 1;   // MIDI channel for audition (10 = drums)
        bool auditionEnabled = AppSettings.Instance.RiffAudition; // MIDI echo of played notes (off when monitoring a live instrument)
        int auditionProgram = 0;   // GM program = the track's instrument index (so the audition matches the track)

        // Generic note input: PC keyboard, MIDI, and audio all converge on StartGrowNote/FinishGrowNote via the
        // INoteSourceProvider abstraction. Keyboard is always live; MIDI listens to a chosen device; audio is toggled.
        KeyboardNoteSourceProvider kbSource;
        MidiNoteSourceProvider midiSource;
        WaveNoteSourceProvider waveSource;
        ComboBox cboMidiIn, cboAudioIn;     // device pickers (in the keyboard toolbar), remembered in settings
        bool populatingDevices;

        /// <summary>Raised whenever the grid content/resolution changes (cell paint or regrid).</summary>
        public event Action GridChanged;

        /// <summary>Raised when a live MIDI/audio take is stopped and the length settled — the host can refresh its module/thumbnail now.</summary>
        public event Action RecordingStopped;

        /// <summary>Raised when the riff is cleared (Effacer) — the host refreshes its module box/thumbnail now.</summary>
        public event Action Cleared;

        /// <summary>Slices per beat (= the riff's SlicesPerQuarter).</summary>
        public int Spb => spb;

        // ---- keyboard entry state (toolbar above the grid) ----
        int kbOctave = 4;          // selected octave (1..7)
        int accidental = 0;        // -1 = flat, 0 = natural, +1 = sharp (manual ±1 on the scale note)
        ToggleButton btnSharp, btnFlat;

        // Hold-to-grow keyboard entry: holding a note key grows the note in real time at the tempo; release ends it.
        // Polyphonic hold-to-grow: each held note key is an INDEPENDENT growing voice (drawn as a live fractional
        // overlay, committed on its own key release). A shared timer animates them all. Releasing one key commits
        // that note; the others keep growing.
        // Each held note key is a growing voice. ONE global cursor advances at the input speed while any key is
        // held; every voice's length = cursor − its own start. A voice starts AT the cursor, unless a same-pitch
        // note just ended within < 1 slice of the cursor (then it continues that note from its original start).
        sealed class GrowVoice { public int Note; public double Start; public bool Merge; } // Merge = continue/merge same-pitch (kbd/MIDI); false for audio (keep successive notes separate)
        readonly Dictionary<int, GrowVoice> grows = new Dictionary<int, GrowVoice>();   // keyed by note (one voice per pitch)
        bool growLoopRunning;                     // CompositionTarget.Rendering subscription (the per-frame evaluator)
        System.Diagnostics.Stopwatch growClock;  // advances the global cursor while any voice is held
        double growOrigin;                        // cursor slice when growing began
        bool cursorTempo;                         // this session advances at the TEMPO (MIDI/audio) vs the fixed input speed (keyboard)
        bool tempoRun;                            // MIDI/audio take: cursor keeps running through rests until Stop

        // The global moving cursor (fractional slice) during entry; = startSlice when nothing is held.
        double CursorF() => growClock != null ? growOrigin + growClock.Elapsed.TotalSeconds / CursorSliceSeconds() : startSlice;

        // Hold-Backspace: the cursor recedes smoothly, erasing each column it fully crosses.
        System.Windows.Threading.DispatcherTimer backTimer;
        System.Diagnostics.Stopwatch backWatch;
        bool backActive; int backStartCursor, backErasedTo; double backCursorF;

        // Key/scale: typed letters give the scale's version of that degree by default; the accidental
        // toggles then shift ±1 semitone. Default = Do majeur (all naturals, i.e. no change).
        static readonly int[] NaturalPc = { 0, 2, 4, 5, 7, 9, 11 }; // Do Ré Mi Fa Sol La Si
        int scaleRootLetter = 0;   // index into NaturalPc (0 = Do)
        int scaleRootAcc = 0;      // -1/0/+1 on the scale root
        int scaleMode = 0;         // index into AudioPitch.ScaleNames (0 = Majeur)
        ToggleButton scaleSharp, scaleFlat;

        public RiffGridControl()
        {
            InitializeComponent();
            BuildKbBar();
            canvasGrid.Focusable = true;
            // Note keys are handled at the hosting WINDOW (hooked on Loaded), so they work whenever this editor is
            // visible and no text/combo field is focused — no need to fight for keyboard focus on every click.
            Loaded += RiffGrid_Loaded;
            // The grid's scrollbars narrow its viewport; reserve the same gutters on the header viewports so they
            // scroll the same max (else the ruler/handle lag the cursor at the far right, and the note-name column
            // lags the grid at the very bottom).
            rulerScroll.Margin = new Thickness(0, 0, SystemParameters.VerticalScrollBarWidth, 0);
            labelScroll.Margin = new Thickness(0, 0, 0, SystemParameters.HorizontalScrollBarHeight);

            editorBpm = AppSettings.Instance.RiffEditorTempo > 0 ? AppSettings.Instance.RiffEditorTempo : 100;
            txtTempo.Text = ((int)Math.Round(editorBpm)).ToString();

            // The three note sources, all feeding the same StartGrowNote/FinishGrowNote.
            kbSource = new KeyboardNoteSourceProvider(KeyToNote);
            midiSource = new MidiNoteSourceProvider(a => Dispatcher.BeginInvoke(a));
            waveSource = new WaveNoteSourceProvider(a => Dispatcher.BeginInvoke(a),
                () => AppSettings.Instance.RiffAudioScaleSnap ? EditorScaleMask() : AudioPitch.Chromatic, // chromatic by default
                TempoSliceSeconds, () => AppSettings.Instance.RiffAudioOnsetSensitivity, AudioFormat.SampleRate);
            // Keyboard advances the cursor at the fixed input speed; MIDI / audio at the tempo (real-time play).
            kbSource.NoteOn += n => OnSourceNoteOn(n, false, true);    // keyboard: fixed speed, merge/continue (re-press resume)
            midiSource.NoteOn += n => OnSourceNoteOn(n, true, false); // MIDI: tempo, NO merge (successive notes stay separate)
            waveSource.NoteOn += n => OnSourceNoteOn(n, true, false);  // audio: tempo, NO merge
            kbSource.NoteOff += OnSourceNoteOff;
            midiSource.NoteOff += OnSourceNoteOff;
            waveSource.NoteOff += OnSourceNoteOff;
        }

        // App note for a PC key letter, with the current octave / scale / accidental (read live). -1 if not a note.
        int KeyToNote(Key k)
        {
            int letter = LetterOf(k);
            if (letter < 0) return -1;
            int note = kbOctave * 12 + ScalePitchClass(letter) + accidental;
            return note < 0 ? 0 : (note >= NoteCount ? NoteCount - 1 : note);
        }

        void OnSourceNoteOn(int note, bool tempo, bool merge) { if (IsVisible) StartGrowNote(note, tempo, merge); }
        void OnSourceNoteOff(int note) => FinishGrowNote(note);

        private void TxtTempo_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (double.TryParse(txtTempo.Text, out double b) && b >= 20 && b <= 400)
            {
                editorBpm = b;
                AppSettings.Instance.RiffEditorTempo = b;
                AppSettings.Instance.Save();
                if (metroTimer != null) metroTimer.Interval = TimeSpan.FromSeconds(60.0 / Math.Max(20, editorBpm)); // follow the tempo live
            }
        }

        Window keyHost; // the window we listen to for note keys

        private void RiffGrid_Loaded(object sender, RoutedEventArgs e)
        {
            EnsureMidiOut();        // open the audition synth now so the first note isn't delayed by device init
            PopulateInputDevices(); // fill the MIDI/Audio combos, restore the last-used devices, start MIDI listening
            var w = Window.GetWindow(this);
            if (w != keyHost)
            {
                if (keyHost != null) { keyHost.PreviewKeyDown -= RiffGrid_PreviewKeyDown; keyHost.PreviewKeyUp -= RiffGrid_PreviewKeyUp; }
                keyHost = w;
                if (keyHost != null) { keyHost.PreviewKeyDown += RiffGrid_PreviewKeyDown; keyHost.PreviewKeyUp += RiffGrid_PreviewKeyUp; }
            }
        }

        // Fill the device combos (MIDI in / audio in), select the last-used one (by name), apply to the providers.
        void PopulateInputDevices()
        {
            if (cboMidiIn == null) return;
            populatingDevices = true;

            cboMidiIn.Items.Clear();
            for (int i = 0; i < NAudio.Midi.MidiIn.NumberOfDevices; i++) cboMidiIn.Items.Add(NAudio.Midi.MidiIn.DeviceInfo(i).ProductName);
            cboMidiIn.SelectedIndex = IndexByName(cboMidiIn, AppSettings.Instance.RiffMidiInDevice);

            cboAudioIn.Items.Clear();
            for (int i = 0; i < WaveInEvent.DeviceCount; i++) cboAudioIn.Items.Add(WaveInEvent.GetCapabilities(i).ProductName);
            cboAudioIn.SelectedIndex = IndexByName(cboAudioIn, AppSettings.Instance.RiffAudioInDevice);

            populatingDevices = false;

            midiSource.SetDevice(cboMidiIn.SelectedIndex);
            midiSource.Start();                              // MIDI keyboard is always live
            waveSource.SetDevice(cboAudioIn.SelectedIndex);  // audio starts only when the 🎤 toggle is on
        }

        static int IndexByName(ComboBox cbo, string name)
        {
            if (!string.IsNullOrEmpty(name))
                for (int i = 0; i < cbo.Items.Count; i++)
                    if ((cbo.Items[i] as string) == name) return i;
            return cbo.Items.Count > 0 ? 0 : -1;
        }

        private void CboMidiIn_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (populatingDevices || cboMidiIn.SelectedIndex < 0) return;
            AppSettings.Instance.RiffMidiInDevice = cboMidiIn.SelectedItem as string ?? "";
            AppSettings.Instance.Save();
            midiSource.SetDevice(cboMidiIn.SelectedIndex); // re-opens automatically
        }

        private void CboAudioIn_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (populatingDevices || cboAudioIn.SelectedIndex < 0) return;
            AppSettings.Instance.RiffAudioInDevice = cboAudioIn.SelectedItem as string ?? "";
            AppSettings.Instance.Save();
            waveSource.SetDevice(cboAudioIn.SelectedIndex); // re-opens if currently active
        }

        // A text/combo field anywhere in the app has focus -> let it type; the editor ignores the key.
        static bool TypingElsewhere()
        {
            var fe = Keyboard.FocusedElement;
            return fe is TextBox || fe is ComboBox || fe is ComboBoxItem;
        }

        public void Configure(Riff riff, Preset previewInstrument, int instrumentIndex = -1)
        {
            this.previewInstrument = previewInstrument;
            SetAuditionInstrument(instrumentIndex);
            spb = (riff != null && riff.SlicesPerQuarter > 0) ? riff.SlicesPerQuarter : 4;
            int len = (riff != null && riff.LengthSlices > 0) ? riff.LengthSlices : spb * beats;
            int cols = Math.Max(spb, len);
            beats = Math.Max(1, (int)Math.Ceiling(cols / (double)spb));

            notes.Clear();
            if (riff?.Notes != null) notes.AddRange(riff.Notes); // load the canonical note list (adjacency preserved)

            txtBeats.Text = DisplayTime.ToString();
            cboSpb.Text = spb.ToString();
            Build();
            int fn = FirstOnNote();
            if (fn >= 0) CenterOnRow(fn);              // existing riff: centre on its first notes
            else CenterOnRow(kbOctave * 12, true);     // empty riff: C of the default octave, near the bottom
        }

        // Note row of the earliest-starting notes (centre of their pitches), or -1 if the riff is empty.
        int FirstOnNote()
        {
            int bestStart = int.MaxValue, sum = 0, count = 0;
            foreach (var n in notes)
            {
                if (n.Start < bestStart) { bestStart = n.Start; sum = n.Note; count = 1; }
                else if (n.Start == bestStart) { sum += n.Note; count++; }
            }
            return count > 0 ? sum / count : -1;
        }

        /// <summary>Change the preview instrument (▶ play loop) and the MIDI audition program, without reloading the grid.</summary>
        public void SetPreviewInstrument(Preset instrument, int instrumentIndex = -1)
        {
            previewInstrument = instrument;
            SetAuditionInstrument(instrumentIndex);
        }

        /// <summary>Set an optional chord-line BACKING (its own instrument) played UNDER the riff during the ▶ preview,
        /// clamped to the riff (no notes before/after). Pass null to clear.</summary>
        public void SetBacking(Riff backing, Preset instrument)
        {
            backingRiff = (backing != null && backing.Notes != null && backing.Notes.Count > 0) ? backing : null;
            backingInstrument = instrument;
        }

        // Map the track/preview instrument index (0..127 = GM program, 128 = drum kit) to the audition synth.
        void SetAuditionInstrument(int index)
        {
            if (index >= InstrumentCatalog.DrumIndex) { auditionChannel = 10; auditionProgram = 0; } // GM drum channel
            else if (index >= 0) { auditionChannel = 1; auditionProgram = index; }
            ApplyAuditionProgram();
        }

        void ApplyAuditionProgram()
        {
            if (midiOut == null) return;
            try { midiOut.Send(new NAudio.Midi.PatchChangeEvent(0, auditionChannel, auditionProgram).GetAsShortMessage()); } catch { }
        }

        /// <summary>The edited content as a slice array (length = beats × spb), derived from the note list.</summary>
        public SequencerSlice[] CurrentSlices() => RiffNotes.ToSlices(notes, Cols);

        /// <summary>The edited content as the canonical note list (a copy), and its grid length in slices.</summary>
        public List<RiffNote> CurrentNotes() => new List<RiffNote>(notes);
        public int LengthSlices => Cols;

        int Cols => beats * spb;
        static double RowTop(int note) => (NoteCount - 1 - note) * CellH; // highest note on top (ruler is a separate header now)

        // ---- palette (pads like RhythmGridControl) ----
        static readonly Brush OnBrush = new SolidColorBrush(Color.FromRgb(0x55, 0xCC, 0x88));
        // OFF cells by key type; the start-of-beat column is a lighter variant. C rows are a touch
        // lighter (stand out), black-key (sharp) rows a touch darker.
        static readonly Brush WhiteOff = new SolidColorBrush(Color.FromRgb(0x2C, 0x2C, 0x36));
        static readonly Brush WhiteBeat = new SolidColorBrush(Color.FromRgb(0x39, 0x39, 0x46));
        static readonly Brush BlackOff = new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x2A));
        static readonly Brush BlackBeat = new SolidColorBrush(Color.FromRgb(0x2E, 0x2E, 0x38));
        static readonly Brush COff = new SolidColorBrush(Color.FromRgb(0x34, 0x34, 0x40));
        static readonly Brush CBeat = new SolidColorBrush(Color.FromRgb(0x41, 0x41, 0x4F));
        static readonly Brush RulerBg = new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x28));
        static readonly Brush LabelFg = new SolidColorBrush(Color.FromRgb(0xBB, 0xBB, 0xBB));   // white-key labels
        static readonly Brush LabelDim = new SolidColorBrush(Color.FromRgb(0x80, 0x80, 0x88));  // black-key labels
        static readonly Brush LabelC = new SolidColorBrush(Color.FromRgb(0xF2, 0xF2, 0xFF));    // C labels (bright)
        static readonly Brush BeatTick = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x70));
        static readonly Brush MeasureTick = new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x9A));
        static readonly Brush CursorBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xDD, 0x33));
        static readonly Brush MarkerBrush = new SolidColorBrush(Color.FromRgb(0x33, 0x66, 0xCC));   // blue start-handle (app accent)
        static readonly Brush MarkerStroke = new SolidColorBrush(Color.FromRgb(0xCF, 0xDE, 0xFA));

        static bool IsBlackKey(int pc) => pc == 1 || pc == 3 || pc == 6 || pc == 8 || pc == 10;
        static string NoteName(int note) => Utils.NoteStrings[note % 12] + (note / 12);

        // OFF cell tint by pitch class + whether it's the start-of-beat column.
        Brush OffColorByPc(int pc, bool down)
        {
            if (pc == 0) return down ? CBeat : COff;                  // C rows stand out
            if (IsBlackKey(pc)) return down ? BlackBeat : BlackOff;   // sharps a touch darker
            return down ? WhiteBeat : WhiteOff;
        }

        void Build()
        {
            canvasGrid.Children.Clear();
            int cols = Cols;
            double gridW = cols * CellW, gridH = NoteCount * CellH;
            canvasGrid.Width = gridW;
            canvasGrid.Height = gridH;
            BuildRuler(cols, gridW);
            BuildLabels(gridH);

            // The grid is drawn into ONE element holding two retained DrawingVisuals (OFF grid + ON notes)
            // instead of NoteCount×cols Rectangles. The note-name column is a SEPARATE fixed viewport now,
            // so the grid origin is 0 (no LabelW offset).
            gridSurface = new GridSurface { Width = gridW, Height = gridH };
            Canvas.SetLeft(gridSurface, 0); Canvas.SetTop(gridSurface, 0);
            canvasGrid.Children.Add(gridSurface);
            DrawOffGrid(cols);     // drawn once (cached tile)
            RedrawOnNotesAsync();  // ON notes built off-thread so selection doesn't block

            cursorLine = new Rectangle { Width = 2, Height = gridH, Fill = CursorBrush, IsHitTestVisible = false };
            canvasGrid.Children.Add(cursorLine);
            if (startSlice >= cols) startSlice = 0;
            MoveCursor(startSlice);
        }

        // The fixed note-name column (C bold/bright, sharps dim) — scrolls vertically with the grid only.
        void BuildLabels(double gridH)
        {
            labelCanvas.Children.Clear();
            labelCanvas.Width = LabelW;
            labelCanvas.Height = gridH;
            for (int note = 0; note < NoteCount; note++)
            {
                int pc = note % 12;
                var lbl = new TextBlock
                {
                    Text = NoteName(note),
                    Foreground = pc == 0 ? LabelC : (IsBlackKey(pc) ? LabelDim : LabelFg),
                    FontSize = 10,
                    FontWeight = pc == 0 ? FontWeights.Bold : FontWeights.Normal,
                    IsHitTestVisible = false,
                };
                Canvas.SetLeft(lbl, 6); Canvas.SetTop(lbl, RowTop(note) + (CellH - 14) / 2); labelCanvas.Children.Add(lbl);
            }
        }

        // The OFF grid is a regular pattern (it repeats every beat horizontally and every octave
        // vertically), so instead of drawing NoteCount×cols pads we rasterise ONE beat×octave tile and let
        // a tiled ImageBrush fill the whole grid in a single DrawRectangle — fast even for long riffs.
        void DrawOffGrid(int cols)
        {
            if (gridSurface == null) return;
            double gridW = cols * CellW, gridH = NoteCount * CellH;
            using (var dc = gridSurface.OpenOff())
                dc.DrawRectangle(BuildOffTile(), null, new Rect(0, 0, gridW, gridH));
        }

        // One beat (spb columns) × one octave (12 rows) of OFF pads, rasterised once and tiled. Top row is
        // note 95 (B) to match the grid (highest note at y=0); 96 rows = 8 exact octaves and cols is a whole
        // number of beats, so the tile aligns seamlessly from the origin.
        static readonly System.Collections.Generic.Dictionary<int, Brush> offTileCache = new System.Collections.Generic.Dictionary<int, Brush>();

        Brush BuildOffTile()
        {
            if (offTileCache.TryGetValue(spb, out var cached)) return cached; // the tile only depends on spb
            double uw = spb * CellW, uh = 12 * CellH;
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
                for (int r = 0; r < 12; r++)
                {
                    int pc = (95 - r) % 12;
                    double y = r * CellH + 1;
                    for (int c = 0; c < spb; c++)
                        dc.DrawRoundedRectangle(OffColorByPc(pc, c == 0), null, new Rect(c * CellW + 1, y, CellW - 2, CellH - 2), 3, 3);
                }
            var rtb = new RenderTargetBitmap((int)Math.Ceiling(uw), (int)Math.Ceiling(uh), 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);
            rtb.Freeze();
            var brush = new ImageBrush(rtb)
            {
                TileMode = TileMode.Tile,
                Viewport = new Rect(0, 0, uw, uh),
                ViewportUnits = BrushMappingMode.Absolute,
                Stretch = Stretch.Fill,
            };
            brush.Freeze();
            offTileCache[spb] = brush;
            return brush;
        }

        int onGen; // generation token so a stale async ON build doesn't clobber a newer one

        // Builds the ON-notes geometry: ONE rounded rect per note (spanning its slices), merged into a frozen
        // Geometry. The 1px inset on each end means two adjacent same-pitch notes show a 2px seam automatically.
        // Pure: reads only the passed snapshot + constants, so it can run on a background thread.
        static Geometry BuildOnGeometry(List<RiffNote> notes, int cols)
        {
            // Nonzero (not the default EvenOdd) so an in-progress note overlapping another still renders filled
            // instead of punching a hole in the overlap region.
            var grp = new GeometryGroup { FillRule = FillRule.Nonzero };
            foreach (var n in notes)
            {
                if (n.Note < 0 || n.Note >= NoteCount) continue;
                double y = RowTop(n.Note) + 1;
                double x = n.Start * CellW + 1;
                double w = Math.Max(1, n.Length * CellW - 2);
                grp.Children.Add(new RectangleGeometry(new Rect(x, y, w, CellH - 2), 3, 3));
            }
            grp.Freeze();
            return grp;
        }

        void DrawOnGeometry(Geometry g)
        {
            if (gridSurface == null) return;
            using (var dc = gridSurface.OpenOn())
            {
                dc.DrawGeometry(OnBrush, null, g);
                if (grows.Count > 0) // in-progress keyboard notes: each from its start to the global cursor
                {
                    double cur = CursorF();
                    foreach (var gv in grows.Values)
                    {
                        double x = gv.Start * CellW + 1;
                        double w = Math.Max(2, (cur - gv.Start) * CellW - 2);
                        dc.DrawRoundedRectangle(OnBrush, null, new Rect(x, RowTop(gv.Note) + 1, w, CellH - 2), 3, 3);
                    }
                }
            }
        }

        // Redraw the ON layer synchronously (used by live edits — small, immediate).
        void RedrawOnNotes()
        {
            if (gridSurface == null) return;
            onGen++; // invalidate any pending async build
            DrawOnGeometry(BuildOnGeometry(new List<RiffNote>(notes), Cols));
        }

        // First display: build the ON-notes geometry OFF the UI thread so selecting a charged riff doesn't
        // block — the grid (OFF + labels + cursor) shows immediately and the notes appear a frame later.
        void RedrawOnNotesAsync()
        {
            if (gridSurface == null) return;
            using (gridSurface.OpenOn()) { } // clear the ON layer immediately
            var snap = new List<RiffNote>(notes); int cols = Cols; int gen = ++onGen;
            System.Threading.Tasks.Task.Run(() =>
            {
                var g = BuildOnGeometry(snap, cols);
                Dispatcher.BeginInvoke((Action)(() => { if (gen == onGen) DrawOnGeometry(g); }));
            });
        }

        // The fixed top header: ruler background, beat ticks, measure numbers.
        void BuildRuler(int cols, double gridW)
        {
            rulerCanvas.Children.Clear();
            rulerCanvas.Width = gridW;
            rulerCanvas.Height = RulerH;

            var bg = new Rectangle { Width = gridW, Height = RulerH, Fill = RulerBg };
            Canvas.SetLeft(bg, 0); Canvas.SetTop(bg, 0); rulerCanvas.Children.Add(bg);

            int spm = spb * BeatsPerBar;
            for (int c = 0; c <= cols; c++)
            {
                if (c % spb != 0) continue;
                bool measure = (c % spm == 0);
                double x = c * CellW;
                var tick = new Rectangle { Width = measure ? 2 : 1, Height = measure ? RulerH : RulerH / 2, Fill = measure ? MeasureTick : BeatTick };
                Canvas.SetLeft(tick, x); Canvas.SetTop(tick, measure ? 0 : RulerH / 2); rulerCanvas.Children.Add(tick);
                if (measure)
                {
                    var num = new TextBlock { Text = ((c / spm) + 1).ToString(), Foreground = LabelFg, FontSize = 9 };
                    Canvas.SetLeft(num, x + 3); Canvas.SetTop(num, 2); rulerCanvas.Children.Add(num);
                }
            }

            // Draggable blue start handle (down-pointing triangle), sat at the top of the cursor line.
            startMarker = new Polygon
            {
                Points = new PointCollection { new Point(-7, 0), new Point(7, 0), new Point(0, RulerH) },
                Fill = MarkerBrush,
                Stroke = MarkerStroke,
                StrokeThickness = 1,
                Cursor = Cursors.SizeWE,
                ToolTip = "Glisser pour définir le point de départ de la lecture",
            };
            startMarker.MouseLeftButtonDown += StartMarker_MouseLeftButtonDown;
            startMarker.MouseMove += StartMarker_MouseMove;
            startMarker.MouseLeftButtonUp += StartMarker_MouseLeftButtonUp;
            rulerCanvas.Children.Add(startMarker);
        }

        // Index of the note covering (row, col), or -1.
        int NoteIndexAt(int row, int col)
        {
            for (int i = 0; i < notes.Count; i++)
            {
                var n = notes[i];
                if (n.Note == row && col >= n.Start && col < n.End) return i;
            }
            return -1;
        }

        int ColFromX(double x) => (int)Math.Floor(x / CellW);

        // Erase one cell: removes it from the covering note, splitting / shrinking / deleting as needed.
        void EraseCell(int row, int col)
        {
            int idx = NoteIndexAt(row, col);
            if (idx < 0) return;
            var n = notes[idx];
            notes.RemoveAt(idx);
            if (col > n.Start) notes.Add(new RiffNote(row, n.Start, col - n.Start));          // left part
            if (col + 1 < n.End) notes.Add(new RiffNote(row, col + 1, n.End - (col + 1)));     // right part
            // a 1-cell note (Start==col && col+1==End) re-adds nothing -> deleted from the list
        }

        // Add a note, merging it with any same-pitch notes it OVERLAPS (shares a cell with). Contiguous-but-
        // not-overlapping notes stay separate (that's how two adjacent same-pitch notes are kept distinct).
        void MergeAdd(RiffNote dn)
        {
            int s = dn.Start, e = dn.End;
            for (int i = notes.Count - 1; i >= 0; i--)
            {
                var n = notes[i];
                if (n.Note == dn.Note && n.Start < e && n.End > s) { s = Math.Min(s, n.Start); e = Math.Max(e, n.End); notes.RemoveAt(i); }
            }
            notes.Add(new RiffNote(dn.Note, s, e - s));
        }

        // Drop notes past the current length / clip the ones that cross it (used when the grid is shrunk).
        void TrimToCols()
        {
            int cols = Cols;
            for (int i = notes.Count - 1; i >= 0; i--)
            {
                var n = notes[i];
                if (n.Start >= cols) notes.RemoveAt(i);
                else if (n.End > cols) notes[i] = new RiffNote(n.Note, n.Start, cols - n.Start);
            }
        }

        void MoveCursor(int slice)
        {
            if (slice < 0) slice = 0; else if (slice >= Cols) slice = Cols - 1;
            double left = slice * CellW;
            if (cursorLine != null) Canvas.SetLeft(cursorLine, left);
            if (startMarker != null) Canvas.SetLeft(startMarker, left + 1); // centre on the 2px line
        }

        // Position the cursor at a fractional slice (for smooth keyboard entry); doesn't change startSlice.
        void MoveCursorF(double slice)
        {
            if (slice < 0) slice = 0;
            double left = slice * CellW;
            if (cursorLine != null) Canvas.SetLeft(cursorLine, left);
            if (startMarker != null) Canvas.SetLeft(startMarker, left + 1);
        }

        int centerRow = NoteCount / 2; // note row the view should be vertically aligned on
        bool centerBottom;             // true -> place the row near the BOTTOM of the viewport (octave C), else centre

        // Scroll the view vertically onto a note row. bottom=true puts it near the bottom (so the octave's C sits
        // low and the notes above are visible); else it centres. On first open the viewer may not be measured yet
        // (ViewportHeight == 0) -> we defer to its first SizeChanged.
        void CenterOnRow(int note, bool bottom = false)
        {
            centerRow = note; centerBottom = bottom;
            void DoCenter()
            {
                double vp = scroller.ViewportHeight;
                if (vp <= 0) return;
                double target = centerBottom
                    ? RowTop(centerRow) - vp + 3 * CellH        // C near the bottom (a couple of rows visible below)
                    : RowTop(centerRow) + CellH / 2 - vp / 2;   // centred
                scroller.ScrollToVerticalOffset(Math.Max(0, target));
            }
            if (scroller.ViewportHeight > 0) { DoCenter(); return; }
            void HandleSizeChanged(object sender, SizeChangedEventArgs e)
            {
                if (scroller.ViewportHeight > 0)
                {
                    scroller.SizeChanged -= HandleSizeChanged;
                    DoCenter();
                }
            }
            scroller.SizeChanged += HandleSizeChanged;
            Dispatcher.BeginInvoke((Action)DoCenter, System.Windows.Threading.DispatcherPriority.Loaded);
        }

        bool HitTest(Point p, out int note, out int c)
        {
            note = -1; c = -1;
            double x = p.X, y = p.Y; // canvasGrid is already grid-relative (the note-name column is separate)
            if (x < 0 || y < 0) return false;
            c = (int)(x / CellW);
            note = NoteCount - 1 - (int)(y / CellH);
            return c >= 0 && c < Cols && note >= 0 && note < NoteCount;
        }

        // Keep the fixed ruler aligned with the grid's HORIZONTAL scroll, and the note-name column aligned
        // with its VERTICAL scroll (the two headers each follow only one axis).
        private void Scroller_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            rulerScroll?.ScrollToHorizontalOffset(e.HorizontalOffset);
            labelScroll?.ScrollToVerticalOffset(e.VerticalOffset);
        }

        // Click the ruler (or drag the blue handle) -> set the play start slice.
        private void RulerCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => SetStartFromX(e.GetPosition(rulerCanvas).X);

        void SetStartFromX(double x)
        {
            int c = (int)Math.Round(x / CellW);
            if (c < 0) c = 0; else if (c >= Cols) c = Cols - 1;
            startSlice = c;
            MoveCursor(c);
            if (provider != null) provider.StartSlice = c;
        }

        private void StartMarker_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            draggingMarker = true; startMarker.CaptureMouse();
            SetStartFromX(e.GetPosition(rulerCanvas).X);
            e.Handled = true;
        }

        private void StartMarker_MouseMove(object sender, MouseEventArgs e)
        {
            if (draggingMarker) { SetStartFromX(e.GetPosition(rulerCanvas).X); e.Handled = true; }
        }

        private void StartMarker_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (draggingMarker) { draggingMarker = false; startMarker.ReleaseMouseCapture(); e.Handled = true; }
        }

        private void CanvasGrid_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            canvasGrid.Focus(); // take keyboard focus so note keys work
            EndRun(); FinishAllGrows(); EndBackErase(); // a mouse edit ends any live take / held-key gesture
            var p = e.GetPosition(canvasGrid);
            if (!HitTest(p, out int row, out int c)) return;
            dragRow = row; lastDragCol = c;
            if (NoteIndexAt(row, c) >= 0) { dragMode = Drag.Erase; EraseCell(row, c); }     // ON cell -> erase
            else                                                                            // OFF cell -> draw
            {
                dragMode = Drag.Draw; drawMinCol = drawMaxCol = c;
                drawIdx = notes.Count; notes.Add(new RiffNote(row, c, 1));
                MidiNoteOn(row); mousePreviewNote = row; // audition the note while dragging it
            }
            canvasGrid.CaptureMouse();
            RedrawOnNotes(); GridChanged?.Invoke();
        }

        private void CanvasGrid_MouseMove(object sender, MouseEventArgs e)
        {
            if (dragMode == Drag.None) return;
            int c = ColFromX(e.GetPosition(canvasGrid).X);
            if (c == lastDragCol) return;        // act only when the cell changes
            lastDragCol = c;

            if (dragMode == Drag.Erase)
            {
                if (c >= 0 && c < Cols) EraseCell(dragRow, c);
            }
            else // Draw: the note spans the whole [min..max] range reached (only grows — going back doesn't erase)
            {
                if (c < 0) c = 0;
                if (c < drawMinCol) drawMinCol = c;
                if (c > drawMaxCol) drawMaxCol = c;
                int end = drawMaxCol + 1;
                EnsureCols(end);
                if (drawIdx >= 0 && drawIdx < notes.Count) notes[drawIdx] = new RiffNote(dragRow, drawMinCol, end - drawMinCol);
                // NOTE: no FollowCursor here — a mouse edit must not move the horizontal scroll.
            }
            RedrawOnNotes(); GridChanged?.Invoke();
        }

        private void CanvasGrid_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (dragMode == Drag.Draw && drawIdx >= 0 && drawIdx < notes.Count)
            {
                var dn = notes[drawIdx];
                notes.RemoveAt(drawIdx);
                MergeAdd(dn);     // if it overlaps a same-pitch note, fuse them
            }
            dragMode = Drag.None; drawIdx = -1;
            canvasGrid.ReleaseMouseCapture();
            if (mousePreviewNote >= 0) { MidiNoteOff(mousePreviewNote); mousePreviewNote = -1; }
            RedrawOnNotes(); GridChanged?.Invoke();
        }

        private void BtnApply_Click(object sender, RoutedEventArgs e)
        {
            if (!int.TryParse(txtBeats.Text, out int disp) || disp < 1) disp = DisplayTime; // the field shows DisplayTime
            int ns = ParseSpb();
            int oldSpb = spb;
            if (ns != oldSpb) // rescale every note's position/length to the new slice resolution
            {
                double r = (double)ns / oldSpb;
                for (int i = 0; i < notes.Count; i++)
                {
                    var n = notes[i];
                    notes[i] = new RiffNote(n.Note, (int)Math.Round(n.Start * r), Math.Max(1, (int)Math.Round(n.Length * r)));
                }
            }
            DisplayTime = disp; spb = ns; // DisplayTime setter converts to the real beats (÷3 in ternary)
            txtBeats.Text = DisplayTime.ToString(); cboSpb.Text = spb.ToString();
            TrimToCols();
            Build();
            GridChanged?.Invoke();
        }

        // Clear every note (keeps the riff's length / resolution).
        private void BtnClear_Click(object sender, RoutedEventArgs e)
        {
            EndRun();
            FinishAllGrows();
            notes.Clear();
            startSlice = 0;          // back to the start
            RedrawOnNotes();
            MoveCursor(startSlice);
            scroller?.ScrollToHorizontalOffset(0); // scrollbar back to the left
            GridChanged?.Invoke();
            Cleared?.Invoke();      // host refreshes the module box/thumbnail right away
        }

        // ---- import / crop -----------------------------------------------------------

        // Importer : charge la piste choisie d'un fichier MIDI ou MuseScore DANS ce riff (remplace son contenu).
        // La timeline n'est PAS régénérée — GridChanged ne fait que persister le riff pour qu'on l'édite ici ensuite.
        private void BtnImport_Click(object sender, RoutedEventArgs e)
        {
            EndRun(); FinishAllGrows(); EndBackErase();
            var dlg = new Dialogs.FileBrowserDialog
            {
                Owner = Window.GetWindow(this),
                Title = "Importer un fichier dans le riff",
                Filter = "Fichiers musicaux (*.mid;*.midi;*.mscz;*.mscx)|*.mid;*.midi;*.mscz;*.mscx|"
                       + "MIDI (*.mid;*.midi)|*.mid;*.midi|MuseScore (*.mscz;*.mscx)|*.mscz;*.mscx|Tous les fichiers (*.*)|*.*",
            };
            if (dlg.ShowDialog() != true) return;

            MuseScoreImporter.Score score;
            try
            {
                string ext = System.IO.Path.GetExtension(dlg.FileName).ToLowerInvariant();
                score = (ext == ".mid" || ext == ".midi") ? MidiImporter.Load(dlg.FileName) : MuseScoreImporter.Load(dlg.FileName);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Impossible de lire ce fichier :\n" + ex.Message, "Import", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (score?.Tracks == null || score.Tracks.Count == 0)
            {
                MessageBox.Show("Aucune piste trouvée dans ce fichier.", "Import", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var picker = new MusicTracker.Dialogs.ImportTrackDialog(score, dlg.FileName) { Owner = Window.GetWindow(this) };
            if (picker.ShowDialog() != true || picker.SelectedTrack == null) return;

            // Convert the chosen track's notes to RiffNotes. App convention: riff note 0 = MIDI 12, so Note = Pitch-12
            // (clamped to the grid's 0..95 range). Importers work at 24 slices/quarter → keep spb = 24 to preserve timing.
            var imported = new System.Collections.Generic.List<RiffNote>();
            foreach (var n in picker.SelectedTrack.Notes)
            {
                int note = n.Pitch - 12;
                if (note < 0) note = 0; else if (note >= NoteCount) note = NoteCount - 1;
                imported.Add(new RiffNote(note, Math.Max(0, n.StartSlice), Math.Max(1, n.LengthSlices)) { Bend = n.Bend });
            }
            LoadNotes(imported, 24);
        }

        /// <summary>Replace the riff's content with the given notes at resolution <paramref name="newSpb"/> slices/quarter,
        /// sizing the grid to whole measures around them. Persists via GridChanged (the host does NOT re-render the timeline).</summary>
        public void LoadNotes(System.Collections.Generic.List<RiffNote> newNotes, int newSpb)
        {
            EndRun(); FinishAllGrows();
            if (newSpb >= 1 && newSpb <= 48) spb = newSpb;
            notes.Clear();
            if (newNotes != null) notes.AddRange(newNotes);
            int len = Math.Max(spb, RiffNotes.LengthOf(notes));
            int spm = Math.Max(1, spb * BeatsPerBar);
            beats = Math.Max(BeatsPerBar, (int)Math.Ceiling(len / (double)spm) * BeatsPerBar); // grow to whole measures
            startSlice = 0;
            if (txtBeats != null) txtBeats.Text = DisplayTime.ToString();
            if (cboSpb != null) cboSpb.Text = spb.ToString();
            Build();
            int fn = FirstOnNote();
            if (fn >= 0) CenterOnRow(fn);
            scroller?.ScrollToHorizontalOffset(0);
            GridChanged?.Invoke();        // persist into the riff (no timeline re-render here)
            RecordingStopped?.Invoke();   // length settled → host can refresh the module box/thumbnail
        }

        private void BtnCropBefore_Click(object sender, RoutedEventArgs e) => CropBeforeCursor();
        private void BtnCropAfter_Click(object sender, RoutedEventArgs e) => CropAfterCursor();

        /// <summary>Drop everything before the cursor and shift the remainder left so the cursor becomes slice 0.</summary>
        public void CropBeforeCursor()
        {
            int cut = startSlice;
            if (cut <= 0) return;
            EndRun(); FinishAllGrows();
            var kept = new System.Collections.Generic.List<RiffNote>();
            foreach (var n in notes)
            {
                if (n.End <= cut) continue;                 // entirely before the cut → drop
                int s = Math.Max(n.Start, cut);
                kept.Add(new RiffNote(n.Note, s - cut, n.End - s) { Bend = (s == n.Start) ? n.Bend : null });
            }
            notes.Clear(); notes.AddRange(kept);
            int newCols = Math.Max(spb, Cols - cut);
            beats = Math.Max(1, (int)Math.Ceiling(newCols / (double)spb));
            startSlice = 0;
            if (txtBeats != null) txtBeats.Text = DisplayTime.ToString();
            Build();
            scroller?.ScrollToHorizontalOffset(0);
            GridChanged?.Invoke(); RecordingStopped?.Invoke();
        }

        /// <summary>Drop everything from the cursor onward (the riff ends at the cursor).</summary>
        public void CropAfterCursor()
        {
            int cut = startSlice;
            if (cut >= Cols) return;
            EndRun(); FinishAllGrows();
            var kept = new System.Collections.Generic.List<RiffNote>();
            foreach (var n in notes)
            {
                if (n.Start >= cut) continue;               // entirely after the cut → drop
                kept.Add(new RiffNote(n.Note, n.Start, Math.Min(n.End, cut) - n.Start) { Bend = n.Bend });
            }
            notes.Clear(); notes.AddRange(kept);
            int newCols = Math.Max(spb, cut);
            beats = Math.Max(1, (int)Math.Ceiling(newCols / (double)spb));
            if (startSlice >= Cols) startSlice = Math.Max(0, Cols - 1);
            if (txtBeats != null) txtBeats.Text = DisplayTime.ToString();
            Build();
            GridChanged?.Invoke(); RecordingStopped?.Invoke();
        }

        int ParseSpb()
        {
            string s = (cboSpb.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? cboSpb.Text;
            return (int.TryParse((s ?? "").Trim(), out int v) && v >= 1 && v <= 48) ? v : spb;
        }

        private void BtnPlay_Click(object sender, RoutedEventArgs e)
        {
            EndRun();
            if (waveOut != null) { StopPreview(); return; }
            try
            {
                Func<Riff> melody = () => new Riff { Notes = CurrentNotes(), LengthSlices = Cols, SlicesPerQuarter = spb };
                // Drum kits are identified by the SF2 BANK (128), not the patch number (see RhythmGridControl).
                var melCtx = new FlowContext { GmProgram = previewInstrument?.PatchNumber ?? 0, Drum = previewInstrument?.BankNumber == InstrumentCatalog.DrumIndex, Bpm = editorBpm };
                NAudio.Wave.IWaveProvider wp;
                if (backingRiff != null && backingInstrument != null)
                {
                    // riff + its chord-line backing (own instrument), summed, looped together — clamped to the riff.
                    var layers = new System.Collections.Generic.List<(Func<Riff>, FlowContext)>
                    {
                        (melody, melCtx),
                        ((Func<Riff>)(() => backingRiff), new FlowContext { GmProgram = backingInstrument?.PatchNumber ?? 0, Drum = backingInstrument?.BankNumber == InstrumentCatalog.DrumIndex, Bpm = editorBpm }),
                    };
                    var mix = new LoopingMixProvider(layers) { StartSlice = startSlice };
                    currentSliceFn = () => mix.CurrentSlice; provider = null; wp = mix;
                }
                else
                {
                    var lp = new LoopingRiffProvider(melody, melCtx) { StartSlice = startSlice };
                    currentSliceFn = () => lp.CurrentSlice; provider = lp; wp = lp;
                }
                waveOut = new WaveOutEvent { DesiredLatency = 120 };
                waveOut.Init(wp); waveOut.Play();
                btnPlay.Content = "■ Stop";
                playTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
                playTimer.Tick += (s, ev) => { if (currentSliceFn != null) { int cs = currentSliceFn(); MoveCursor(cs); FollowCursor(cs); } };
                playTimer.Start();
            }
            catch { StopPreview(); }
        }

        public void StopPreview()
        {
            if (playTimer != null) { playTimer.Stop(); playTimer = null; }
            if (waveOut != null) { try { waveOut.Stop(); waveOut.Dispose(); } catch { } waveOut = null; }
            provider = null; currentSliceFn = null;
            if (btnPlay != null) btnPlay.Content = "▶ Écouter";
            MoveCursor(startSlice);
        }

        // ---- keyboard entry ---------------------------------------------------------

        // Builds the toolbar: octave 1..7 (radio), accidental ♯/♭ (toggles), scale, input devices. Note DURATION
        // is no longer chosen here — it's set by how long the cursor advances while a note is held.
        void BuildKbBar()
        {
            kbBar.Children.Add(KbLabel("Octave :"));
            for (int o = 1; o <= 7; o++) OctRadio(o, o == kbOctave);

            kbBar.Children.Add(KbSep());
            btnSharp = AccToggle("♯", +1);
            btnFlat = AccToggle("♭", -1);

            kbBar.Children.Add(KbSep());
            kbBar.Children.Add(KbLabel("Gamme :"));
            var cboRoot = new ComboBox { Width = 56, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(1, 0, 1, 0), ItemsSource = new[] { "Do", "Ré", "Mi", "Fa", "Sol", "La", "Si" }, SelectedIndex = 0 };
            cboRoot.SelectionChanged += (s, e) => { if (cboRoot.SelectedIndex >= 0) scaleRootLetter = cboRoot.SelectedIndex; };
            kbBar.Children.Add(cboRoot);
            scaleSharp = ScaleAccToggle("♯", +1);
            scaleFlat = ScaleAccToggle("♭", -1);
            var cboMode = new ComboBox { Width = 120, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(1, 0, 1, 0), ItemsSource = AudioPitch.ScaleNames, SelectedIndex = 0 };
            cboMode.SelectionChanged += (s, e) => { if (cboMode.SelectedIndex >= 0) scaleMode = cboMode.SelectedIndex; };
            kbBar.Children.Add(cboMode);

            // Live input device pickers (MIDI keyboard / audio). Populated on Loaded, remembered in settings.
            kbBar.Children.Add(KbSep());
            kbBar.Children.Add(KbLabel("MIDI in :"));
            cboMidiIn = new ComboBox { Width = 130, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(1, 0, 1, 0), ToolTip = "Périphérique MIDI joué dans l'éditeur" };
            cboMidiIn.SelectionChanged += CboMidiIn_SelectionChanged;
            kbBar.Children.Add(cboMidiIn);
            kbBar.Children.Add(KbLabel("Audio in :"));
            cboAudioIn = new ComboBox { Width = 150, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(1, 0, 1, 0), ToolTip = "Périphérique audio pour l'entrée 🎤" };
            cboAudioIn.SelectionChanged += CboAudioIn_SelectionChanged;
            kbBar.Children.Add(cboAudioIn);

            kbBar.Children.Add(KbSep());
            var chkEcho = new CheckBox
            {
                Content = "Écho MIDI",
                IsChecked = auditionEnabled,
                Foreground = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA)),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(2, 0, 2, 0),
                Cursor = Cursors.Hand,
                ToolTip = "Rejoue en MIDI les notes détectées. Décoche-le si tu joues déjà l'instrument en entrée audio (🎤).",
            };
            chkEcho.Checked += (s, e) => SetAudition(true);
            chkEcho.Unchecked += (s, e) => SetAudition(false);
            kbBar.Children.Add(chkEcho);
        }

        // Toggle the MIDI echo (audition) of played/detected notes; persisted globally.
        void SetAudition(bool on)
        {
            auditionEnabled = on;
            if (!on) AllNotesOff();                         // release any currently-sounding echo
            AppSettings.Instance.RiffAudition = on;
            AppSettings.Instance.Save();
        }

        // ♯ / ♭ exclusive toggles for the SCALE root (both off = natural).
        ToggleButton ScaleAccToggle(string label, int value)
        {
            var tb = new ToggleButton { Style = (Style)FindResource("TogBtn"), Content = label, FontSize = 13 };
            tb.Checked += (s, e) => { scaleRootAcc = value; var other = value > 0 ? scaleFlat : scaleSharp; if (other != null && other.IsChecked == true) other.IsChecked = false; };
            tb.Unchecked += (s, e) => { if (scaleRootAcc == value) scaleRootAcc = 0; };
            kbBar.Children.Add(tb);
            return tb;
        }

        // The scale's pitch class for a typed letter (0=Do..6=Si): the diatonic degree of the chosen key.
        int ScalePitchClass(int letterIndex)
        {
            int rootPc = (((NaturalPc[scaleRootLetter] + scaleRootAcc) % 12) + 12) % 12;
            var deg = AudioPitch.ScaleDegrees(scaleMode);
            if (deg.Length == 7) // diatonic: one note per letter (proper spelling)
            {
                int d = (((letterIndex - scaleRootLetter) % 7) + 7) % 7;
                return (((rootPc + deg[d]) % 12) + 12) % 12;
            }
            // pentatonic / other: snap the natural letter to the nearest scale pitch class
            int natural = NaturalPc[letterIndex];
            int mask = AudioPitch.ScaleMask(rootPc, scaleMode);
            for (int r = 0; r <= 6; r++)
            {
                int up = (((natural + r) % 12) + 12) % 12; if ((mask & (1 << up)) != 0) return up;
                int dn = (((natural - r) % 12) + 12) % 12; if ((mask & (1 << dn)) != 0) return dn;
            }
            return natural;
        }

        TextBlock KbLabel(string t) => new TextBlock { Text = t, Foreground = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA)), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 4, 0) };
        Border KbSep() => new Border { Width = 1, Background = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44)), Margin = new Thickness(6, 2, 6, 2) };

        RadioButton MakeRadio(string content, string group, bool isChecked)
        {
            // Per-instance group name: RadioButton groups are tree-global, so two grids mustn't share a group.
            return new RadioButton { Style = (Style)FindResource("TogBtn"), Content = content, GroupName = group + GetHashCode(), IsChecked = isChecked };
        }

        void OctRadio(int oct, bool isChecked)
        {
            var rb = MakeRadio(oct.ToString(), "riffOct", isChecked);
            rb.Checked += (s, e) => { kbOctave = oct; CenterOnRow(oct * 12, true); }; // scroll to the C of this octave (near the bottom)
            kbBar.Children.Add(rb);
        }

        // ♯ / ♭ are mutually exclusive but both can be off (= natural).
        ToggleButton AccToggle(string label, int value)
        {
            var tb = new ToggleButton { Style = (Style)FindResource("TogBtn"), Content = label, FontSize = 13 };
            tb.Checked += (s, e) =>
            {
                accidental = value;
                var other = value > 0 ? btnFlat : btnSharp;
                if (other != null && other.IsChecked == true) other.IsChecked = false;
            };
            tb.Unchecked += (s, e) => { if (accidental == value) accidental = 0; };
            kbBar.Children.Add(tb);
            return tb;
        }

        // Space = insert a rest (advance the cursor by the toolbar duration). Holding C D E F G A B = Do..Si
        // starts a note at the cursor that GROWS in real time at the tempo; releasing the key finalises it.
        private void RiffGrid_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (!IsVisible || TypingElsewhere()) return; // only when this editor is shown and no field is focused
            Key k = e.Key == Key.System ? e.SystemKey : e.Key;
            if (k == Key.Escape) { EndRun(); e.Handled = true; return; }
            if (k == Key.Space) { EndRun(); FinishAllGrows(); AdvanceCursor(); e.Handled = true; return; }
            if (k == Key.Back) { e.Handled = true; if (!e.IsRepeat) StartBackErase(); return; } // auto-repeat: timer drives it
            if (LetterOf(k) < 0) return;
            e.Handled = true;
            if (e.IsRepeat) return;     // auto-repeat: the note is already growing
            kbSource.KeyDown(k);
        }

        private void RiffGrid_PreviewKeyUp(object sender, KeyEventArgs e)
        {
            if (!IsVisible) return;
            Key k = e.Key == Key.System ? e.SystemKey : e.Key;
            if (k == Key.Back) { EndBackErase(); e.Handled = true; return; }
            if (LetterOf(k) >= 0) { kbSource.KeyUp(k); e.Handled = true; }
        }

        static int LetterOf(Key k)
        {
            switch (k)
            {
                case Key.C: return 0; case Key.D: return 1; case Key.E: return 2; case Key.F: return 3;
                case Key.G: return 4; case Key.A: return 5; case Key.B: return 6; default: return -1;
            }
        }

        // Selected duration in slices (used by Space for rests): quarters × spb ÷ tuplet, × 1.5 if dotted.
        // Space inserts a rest: advance the cursor by one entry-grid unit (the snap fraction).
        int RestStep() => Math.Max(1, (int)Math.Round(SnapFraction() * spb));

        // Slice durations: the PC keyboard advances at a fixed, deliberate INPUT speed (easy to type precisely);
        // MIDI / audio advance at the TEMPO (real-time performance, so durations match what's played). The cursor
        // uses one or the other depending on which source opened the current session; Backspace stays input-speed.
        double InputSliceSeconds() => 1.0 / Math.Max(0.25, AppSettings.Instance.RiffInputSpeed);
        double TempoSliceSeconds() => (60.0 / Math.Max(20, editorBpm)) / spb;
        double CursorSliceSeconds() => cursorTempo ? TempoSliceSeconds() : InputSliceSeconds();

        // Start a growing voice for a pitch — shared by all input sources. tempo = advance the cursor at the
        // tempo (MIDI/audio) rather than the fixed input speed (keyboard); set when the session's first note begins.
        void StartGrowNote(int note, bool tempo, bool merge)
        {
            if (note < 0 || note >= NoteCount || grows.ContainsKey(note)) return; // one voice per pitch

            if (growClock == null)
            {
                growOrigin = startSlice; cursorTempo = tempo; tempoRun = tempo; growClock = System.Diagnostics.Stopwatch.StartNew(); // start the cursor (tempo take = keep running)
                // Lock the metronome to the cursor: click when it crosses each beat line. If we start ON a beat,
                // click it immediately; otherwise the first click is the next beat.
                if (tempo) { bool onBeat = startSlice % spb < 1e-6; metroCursorBeat = (int)Math.Floor(startSlice / (double)spb) - (onBeat ? 1 : 0); }
            }
            double cur = CursorF();

            double start = cur; // a new note starts at the cursor...
            if (merge) // ...unless a same-pitch note just ended < 1 slice from the cursor: continue it (kbd/MIDI only)
                for (int i = 0; i < notes.Count; i++)
                    if (notes[i].Note == note && Math.Abs(cur - notes[i].End) < 1.0)
                    { start = notes[i].Start; notes.RemoveAt(i); break; }

            grows[note] = new GrowVoice { Note = note, Start = start, Merge = merge };
            MidiNoteOn(note);               // audition while held
            EnsureCols((int)Math.Ceiling(cur) + 2);
            RedrawOnNotes();
            EnsureRowVisible(note);
            StartGrowLoop();
        }

        void StartGrowLoop() { if (!growLoopRunning) { CompositionTarget.Rendering += OnGrowFrame; growLoopRunning = true; } }
        void StopGrowLoop()  { if (growLoopRunning) { CompositionTarget.Rendering -= OnGrowFrame; growLoopRunning = false; } }

        // Per-frame evaluator (UI thread, ~60 fps): let the keyboard source release any key no longer held (MIDI/
        // audio voices end on their own off events), then — if any remain — advance the cursor and grow each note.
        void OnGrowFrame(object sender, EventArgs e)
        {
            kbSource?.Poll();
            if (grows.Count == 0 && !tempoRun) { StopGrowLoop(); return; } // tempo take keeps the cursor running through rests

            double cur = CursorF();
            EnsureCols((int)Math.Ceiling(cur) + 1);
            RedrawOnNotes();
            MoveCursorF(cur);
            FollowCursor(cur);

            // Metronome locked to the cursor: click exactly when it crosses a beat line (so the beats match
            // the displayed time positions). Down-beat = first beat of the measure.
            if (metro != null && tempoRun)
            {
                // Look a little ahead (audio-latency worth of slices) so the click is QUEUED just before the
                // beat and SOUNDS on it.
                double aheadSlices = MetroAnticipateSec / CursorSliceSeconds();
                int beatIdx = (int)Math.Floor((cur + aheadSlices) / spb + 1e-6);
                if (beatIdx > metroCursorBeat) { metroCursorBeat = beatIdx; metro.Tick(((beatIdx % BeatsPerBar) + BeatsPerBar) % BeatsPerBar == 0); }
            }
        }

        // Snap a fractional slice to the keyboard-entry grid (a fraction of a beat, from global settings).
        int SnapToEntryGrid(double sliceValue)
        {
            double unit = SnapFraction() * spb; // snap unit in slices (meter-aware: 1/8 simple, 1/6 compound)
            if (unit < 0.5) return (int)Math.Round(sliceValue);        // off / finer than a slice -> nearest slice
            return (int)Math.Round(Math.Round(sliceValue / unit) * unit);
        }

        // Finalise ONE held note (its key / MIDI note was released): snap its start AND end to the entry grid, commit.
        void FinishGrowNote(int note)
        {
            if (!grows.TryGetValue(note, out var gv)) return;
            grows.Remove(note);
            MidiNoteOff(gv.Note);

            double cur = CursorF();
            int unit = Math.Max(1, (int)Math.Round(SnapFraction() * spb));
            int start = SnapToEntryGrid(gv.Start);
            int end = SnapToEntryGrid(cur);
            if (end < start + unit) end = start + unit;   // a quick tap still lands at least one snap unit
            var rn = new RiffNote(gv.Note, start, Math.Max(1, end - start));
            if (gv.Merge) MergeAdd(rn); else notes.Add(rn); // audio: keep successive notes separate (just snapped)

            RedrawOnNotes();
            if (grows.Count == 0 && !tempoRun) // last note released and not a running take: stop & settle the cursor
            {
                StopGrowLoop();
                startSlice = Math.Max(0, Math.Min(SnapToEntryGrid(cur), Cols - 1));
                growClock = null;
                MoveCursor(startSlice);
                if (scroller != null) scroller.UpdateLayout();
                EnsureVisible(gv.Note, startSlice);
            }
            GridChanged?.Invoke();
        }

        // Finalise every held note (used when a mouse edit / clear / backspace / Space interrupts the keyboard).
        void FinishAllGrows()
        {
            if (grows.Count == 0) return;
            foreach (var n in new List<int>(grows.Keys)) FinishGrowNote(n);
        }

        // Stop a live MIDI/audio take: commit held notes, then trim the riff length UP to the started measure (ceil).
        void StopRun()
        {
            FinishAllGrows();   // commit held notes (tempoRun still true here -> no auto-settle in FinishGrowNote)
            double cur = CursorF(); // take length (clock still running)
            tempoRun = false;
            StopGrowLoop();
            growClock = null;

            double extent = cur; // never clip an actual note
            foreach (var n in notes) if (n.End > extent) extent = n.End;
            int spm = Math.Max(1, spb * BeatsPerBar);
            int measures = Math.Max(1, (int)Math.Ceiling(extent / spm)); // ceil to the started measure
            beats = measures * BeatsPerBar;
            TrimToCols();       // drop/clip anything past the new length
            txtBeats.Text = beats.ToString();
            startSlice = Math.Max(0, Math.Min(SnapToEntryGrid(cur), Cols - 1));
            Build();            // rebuild at the trimmed size (also repositions the cursor)
            GridChanged?.Invoke();
            RecordingStopped?.Invoke(); // length settled -> host refreshes the module box/thumbnail now
        }

        void EndRun() { if (tempoRun) StopRun(); } // end the take before a manual edit / playback

        // Hold-Backspace: recede the cursor SMOOTHLY at the input speed, erasing each column it fully crosses.
        void StartBackErase()
        {
            EndRun();
            EndBackErase();   // finalise any previous gesture
            FinishAllGrows();
            backActive = true;
            backStartCursor = startSlice;
            backErasedTo = startSlice;
            backCursorF = startSlice;
            backWatch = System.Diagnostics.Stopwatch.StartNew();
            backTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
            backTimer.Tick += (s, e) => BackTick();
            backTimer.Start();
            BackTick();
        }

        void BackTick()
        {
            if (!backActive || backWatch == null) return;
            double cursorF = Math.Max(0, backStartCursor - backWatch.Elapsed.TotalSeconds / InputSliceSeconds());
            backCursorF = cursorF;
            int frontier = (int)Math.Ceiling(cursorF);                          // erase columns fully behind the cursor
            while (backErasedTo > frontier) { backErasedTo--; EraseColumn(backErasedTo); }
            RedrawOnNotes();
            MoveCursorF(cursorF);                                                // smooth, fractional cursor
            FollowCursor(cursorF);
            EnsureRowVisible(RowOfNoteBefore(frontier));
            GridChanged?.Invoke();
            if (cursorF <= 0) EndBackErase();   // reached the start
        }

        // Finalise: round the cursor to a slice, erase up to it, set the cursor there.
        void EndBackErase()
        {
            if (backTimer != null) { backTimer.Stop(); backTimer = null; }
            if (!backActive) { backWatch = null; return; }
            backActive = false;
            int fin = (int)Math.Round(backCursorF);
            while (backErasedTo > fin) { backErasedTo--; EraseColumn(backErasedTo); }
            startSlice = Math.Max(0, Math.Min(fin, Cols - 1));
            backWatch = null;
            RedrawOnNotes();
            MoveCursor(startSlice);
            EnsureRowVisible(RowOfNoteBefore(startSlice));
            GridChanged?.Invoke();
        }

        // Erase one whole column (the cell at `col` of every note covering it: shrink / split / delete).
        void EraseColumn(int col)
        {
            if (col < 0) return;
            for (int i = notes.Count - 1; i >= 0; i--)
            {
                var n = notes[i];
                if (col >= n.Start && col < n.End)
                {
                    notes.RemoveAt(i);
                    if (col > n.Start) notes.Add(new RiffNote(n.Note, n.Start, col - n.Start));
                    if (col + 1 < n.End) notes.Add(new RiffNote(n.Note, col + 1, n.End - (col + 1)));
                }
            }
        }

        // Space: advance the cursor by one duration (insert a rest); breaks the détaché chain.
        void AdvanceCursor()
        {
            int pos = startSlice + RestStep();
            bool grew = EnsureCols(pos + 1);
            startSlice = Math.Min(pos, Cols - 1);
            MoveCursor(startSlice);
            if (grew && scroller != null) scroller.UpdateLayout();
            EnsureVisible(-1, startSlice);
        }

        // Grow the riff (more beats) so it holds at least minCols columns. Notes are length-independent of the
        // grid, so this just bumps the beat count and rebuilds. Returns true if it actually grew.
        bool EnsureCols(int minCols)
        {
            if (minCols <= Cols) return false;
            int spm = Math.Max(1, spb * BeatsPerBar);                                  // slices per measure
            beats = (int)Math.Ceiling(minCols / (double)spm) * BeatsPerBar;            // grow by WHOLE measures
            if (txtBeats != null) txtBeats.Text = beats.ToString();
            Build();
            return true;
        }

        // Page-turn (like the score): once the cursor comes within one measure of the right edge (or scrolls
        // off the left), jump so it sits near the left and the upcoming measures are revealed. More readable
        // than a continuous centre-scroll. Used by playback and live keyboard/audio entry.
        void FollowCursor(double slice)
        {
            if (scroller == null) return;
            double vw = scroller.ViewportWidth;
            double x = slice * CellW;
            double measureW = Math.Min(spb * BeatsPerBar * CellW, vw * 0.4); // one measure (capped for small views)
            double left = scroller.HorizontalOffset;
            if (x < left || x > left + vw - measureW)
                scroller.ScrollToHorizontalOffset(Math.Max(0, x - 30)); // bring the cursor near the left
        }

        // Scroll vertically so a note row is on screen (no horizontal change — used by keyboard entry).
        void EnsureRowVisible(int note)
        {
            if (scroller == null || note < 0) return;
            double y = RowTop(note);
            if (y < scroller.VerticalOffset || y > scroller.VerticalOffset + scroller.ViewportHeight - CellH)
                scroller.ScrollToVerticalOffset(Math.Max(0, y - scroller.ViewportHeight * 0.5));
        }

        // Row of the note ending closest before (or at) the given slice, or -1 — for the Backspace scroll-to.
        int RowOfNoteBefore(int slice)
        {
            int bestEnd = -1, row = -1;
            foreach (var n in notes) if (n.End <= slice && n.End > bestEnd) { bestEnd = n.End; row = n.Note; }
            return row;
        }

        // Scroll so the just-entered note (row + cursor column) stays visible.
        void EnsureVisible(int note, int slice)
        {
            if (scroller == null) return;
            double x = slice * CellW;
            if (x < scroller.HorizontalOffset || x > scroller.HorizontalOffset + scroller.ViewportWidth - 40)
                scroller.ScrollToHorizontalOffset(Math.Max(0, x - scroller.ViewportWidth * 0.5));
            if (note < 0) return; // Space: horizontal only
            double y = RowTop(note);
            if (y < scroller.VerticalOffset || y > scroller.VerticalOffset + scroller.ViewportHeight - CellH)
                scroller.ScrollToVerticalOffset(Math.Max(0, y - scroller.ViewportHeight * 0.5));
        }

        private void UserControl_Unloaded(object sender, RoutedEventArgs e)
        {
            if (keyHost != null) { keyHost.PreviewKeyDown -= RiffGrid_PreviewKeyDown; keyHost.PreviewKeyUp -= RiffGrid_PreviewKeyUp; keyHost = null; }
            kbSource?.Dispose();
            midiSource?.Dispose();
            waveSource?.Dispose();
            tempoRun = false;
            StopGrowLoop();
            FinishAllGrows();
            EndBackErase();
            StopMetronome();
            AllNotesOff();
            if (midiOut != null) { try { midiOut.Close(); midiOut.Dispose(); } catch { } midiOut = null; }
            StopPreview();
        }

        // ---- MIDI audition (heard while adding notes; native polyphony, low latency) -----------------------

        void EnsureMidiOut()
        {
            if (midiOut != null) return;
            try
            {
                if (NAudio.Midi.MidiOut.NumberOfDevices > 0)
                {
                    midiOut = new NAudio.Midi.MidiOut(0); // device 0 = Microsoft GS Wavetable Synth
                    ApplyAuditionProgram();               // select the track's instrument (GM program)
                }
            }
            catch { midiOut = null; }
        }

        void MidiNoteOn(int note)
        {
            if (!auditionEnabled) return; // no MIDI echo (e.g. while monitoring a live instrument on audio in)
            EnsureMidiOut();
            if (midiOut == null || note < 0 || note >= NoteCount || !midiOn.Add(note)) return;
            try { midiOut.Send(new NAudio.Midi.NoteOnEvent(0, auditionChannel, note + 12, 100, 0).GetAsShortMessage()); } catch { }
        }

        void MidiNoteOff(int note)
        {
            if (midiOut == null || !midiOn.Remove(note)) return;
            try { midiOut.Send(new NAudio.Midi.NoteEvent(0, auditionChannel, NAudio.Midi.MidiCommandCode.NoteOff, note + 12, 0).GetAsShortMessage()); } catch { }
        }

        void AllNotesOff()
        {
            if (midiOut != null)
                foreach (var n in new List<int>(midiOn))
                    try { midiOut.Send(new NAudio.Midi.NoteEvent(0, auditionChannel, NAudio.Midi.MidiCommandCode.NoteOff, n + 12, 0).GetAsShortMessage()); } catch { }
            midiOn.Clear();
        }

        // Editor scale mask from the toolbar's scale selector (used when "snap audio to scale" is on).
        int EditorScaleMask()
        {
            int root = (((NaturalPc[scaleRootLetter] + scaleRootAcc) % 12) + 12) % 12;
            return AudioPitch.ScaleMask(root, scaleMode);
        }

        private void BtnAudioIn_Checked(object sender, RoutedEventArgs e) => waveSource?.Start();
        private void BtnAudioIn_Unchecked(object sender, RoutedEventArgs e) => waveSource?.Stop();

        private void BtnMetro_Checked(object sender, RoutedEventArgs e) => StartMetronome();
        private void BtnMetro_Unchecked(object sender, RoutedEventArgs e) => StopMetronome();

        private void BtnStopRun_Click(object sender, RoutedEventArgs e) => EndRun();

        void StartMetronome()
        {
            StopMetronome();
            try
            {
                metro = new MetronomeProvider(AudioFormat.SampleRate);
                metroOut = new WaveOutEvent { DesiredLatency = 60 }; // low latency so the click lands with the cursor
                metroOut.Init(metro);
                metroOut.Play();
                metroBeat = 0;
                metroTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(60.0 / Math.Max(20, editorBpm)) };
                // Free-running click only when NO tempo take is active; during a take the cursor drives the
                // metronome (see OnGrowFrame) so the beats line up with the displayed positions.
                metroTimer.Tick += (s, e) => { if (tempoRun && growClock != null) return; metro.Tick(metroBeat % BeatsPerBar == 0); metroBeat++; };
                metroTimer.Start();
                if (!(tempoRun && growClock != null)) { metro.Tick(true); metroBeat = 1; } // first click on the down-beat (idle)
            }
            catch { StopMetronome(); }
        }

        void StopMetronome()
        {
            if (metroTimer != null) { metroTimer.Stop(); metroTimer = null; }
            if (metroOut != null) { try { metroOut.Stop(); metroOut.Dispose(); } catch { } metroOut = null; }
            metro = null;
        }

        // A lightweight surface that draws the whole grid with two retained DrawingVisuals (OFF grid +
        // ON notes) instead of one UIElement per cell — far cheaper to lay out, render and scroll.
        sealed class GridSurface : FrameworkElement
        {
            readonly VisualCollection visuals;
            readonly DrawingVisual off = new DrawingVisual();
            readonly DrawingVisual on = new DrawingVisual();
            public GridSurface() { visuals = new VisualCollection(this) { off, on }; IsHitTestVisible = false; }
            public DrawingContext OpenOff() => off.RenderOpen();
            public DrawingContext OpenOn() => on.RenderOpen();
            protected override int VisualChildrenCount => visuals.Count;
            protected override Visual GetVisualChild(int index) => visuals[index];
        }
    }
}
