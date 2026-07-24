using MusicTracker.Dialogs;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using MusicTracker.Engine;
using MusicTracker.Engine.Flow;
using MusicTracker.Engine.Timeline;
using MeltySynth;

namespace MusicTracker.Screens
{
    /// <summary>
    /// Timeline / arrangement editor (replaces the multi-layer sequencer). Horizontal track per
    /// instrument; riff / chord-pattern / drum-kit / repeat modules are placed along time (position &
    /// width proportional to duration). The selected module is edited in the bottom panel.
    /// PHASE 1: data model + display + basic add/select/edit (not yet playable — see Phase 2).
    /// </summary>
    public partial class TimelineScreen : UserControl, IMusicEditor, Controls.IChordEditorHost
    {
        // LaneH is sized so a Repeat's inner children (LaneH-26) keep the full standalone-leaf height
        // while still leaving a clear title strip (top 14) above them — the Repeat box is taller than
        // a plain leaf needs, but uniform lane height keeps the layout simple.
        const double LaneH = 88, TempoH = 40, VolLaneH = 48, HeaderW = 160, ChordH = 26;
        const double CollapsedH = 26; // a collapsed track shrinks header + lane to this minimal height (issue #5)
        const double PxPerBeat = 60; // box width per beat (a 4/4 measure ≈ 240 px); RiffThumbnail must match

        bool autoTransposeChords;        // chord lane: when on, editing a chord also transposes the melody (else only bass+accompaniment)
        readonly TimelineProject project = new TimelineProject();
        TimelineTrack selectedTrack;
        TimelineItem selectedItem;

        // Maps rebuilt by Render so a selection change can update just the affected borders (no full rebuild).
        readonly Dictionary<TimelineItem, Action<bool>> highlighters = new Dictionary<TimelineItem, Action<bool>>();
        readonly Dictionary<TimelineTrack, Border> trackHeaders = new Dictionary<TimelineTrack, Border>();
        static readonly Brush HeaderSelBg = new SolidColorBrush(Color.FromRgb(0x2C, 0x2C, 0x38));
        Controls.RiffGridControl activeRiffGrid; // the inline riff editor currently shown (to stop preview on switch)
        Controls.Score.ScoreView activeScore;    // the score view currently shown (to sweep its cursor on play)
        bool riffDirty;                  // the open riff was edited -> refresh on leave (not per stroke)
        TimelineItem riffEditItem;       // the module whose riff is being edited
        TimelineTrack riffEditTrack;
        double riffOpenLen;              // its displayed length when editing started (to detect a length change)
        readonly Dictionary<TimelineItem, Controls.TimelineEditor.ModuleBoxControl> leafBoxes = new Dictionary<TimelineItem, Controls.TimelineEditor.ModuleBoxControl>();
        readonly Dictionary<TimelineItem, TimelineTrack> boxOwner = new Dictionary<TimelineItem, TimelineTrack>(); // which track drew each box

        // ---- playback (Phase 2) ----
        NAudio.Wave.WaveOutEvent playWaveOut;
        Engine.Timeline.TimelinePlayer player;
        Engine.Timeline.LookaheadBuffer playBuffer; // background pre-render between the player and the device
        System.Windows.Threading.DispatcherTimer playTimer;
        System.Windows.Shapes.Rectangle playCursor;
        System.Windows.Shapes.Polygon startMarker; // blue down-pointing handle on the ruler: drag to set the play start
        bool draggingMarker;
        double startBeat;                           // cursor position = where playback starts/resumes (beats)

        public TimelineScreen()
        {
            InitializeComponent();
            foreach (var t in new[] { "Do", "Ré", "Mi", "Fa", "Sol", "La", "Si" }) cboTonic.Items.Add(t);
            foreach (var m in Engine.Score.MusicalMode.Names) cboMode.Items.Add(m); // all modes (like the Transpose dialog)
            // Start with one instrument track + the permanent chords track (always at the bottom).
            project.Tracks.Add(new TimelineTrack { Name = "Piste 1", Instrument = 0 });
            TimelineHelper.EnsureChordTrack(project);
            selectedTrack = project.Tracks[0];
            txtBpm.Text = ((int)project.MainBpm).ToString();

            // laneScroll shows a vertical scrollbar (always), so its viewport is one scrollbar-width narrower than
            // the ruler's and the docked chords lane (which have none). That makes laneScroll scrollable that much
            // FURTHER, so at the far right the ruler/chords froze a scrollbar-width short of the lanes. Reserve the
            // same right gutter on those two so all three viewports (hence scroll offsets) match 1:1 everywhere.
            const double sbW = 18; // theme's vertical ScrollBar width (Theme/ScrollBar2.xaml)
            rulerScroll.Margin = new Thickness(rulerScroll.Margin.Left, rulerScroll.Margin.Top, sbW, rulerScroll.Margin.Bottom);
            if (chordScroll != null) chordScroll.Margin = new Thickness(chordScroll.Margin.Left, chordScroll.Margin.Top, sbW, chordScroll.Margin.Bottom);

            Loaded += (s, e) => { Render(); EnsureCursor(); };
        }

       
        // ---- key signature (toolbar) ----
        bool syncingKey;
        readonly HashSet<TimelineTrack> scoreTracks = new HashSet<TimelineTrack>(); // tracks INCLUDED in the score (♫)
        bool viewScore;                                    // global toggle: show the score (vs the module editor) in the bottom area
        bool ScoreVisible => viewScore && scoreTracks.Count > 0;

        // ---- score note-input editor state ----
        bool scoreEditMode, scoreKeysHooked;
        int editDurIdx = 2, editOctave = 4;    // note-VALUE index (0=double-croche,1=croche,2=noire,3=blanche,4=ronde) + octave
        bool editDotted;
        // Slices per note value — the beat is ALWAYS 24 slices; only the subdivision changes with the meter.
        // Binary (x/4): the beat splits by 2/4 → {6,12,24,48,96}. Ternary (compound x/8): splits by 3/6 → {4,8,16,32,64}.
        // A dot multiplies by 1.5 at placement (so e.g. ternary croche pointée = 8·1.5 = 12 slices = ½ temps).
        static readonly int[] DurBin = { 6, 12, 24, 48, 96 };
        static readonly int[] DurTern = { 4, 8, 16, 32, 64 };
        int[] DurBases() => (project != null && project.TimeSigDen == 8) ? DurTern : DurBin;
        int EditDur => DurBases()[Math.Max(0, Math.Min(4, editDurIdx))];   // base slices of the selected value at the current meter
        double editRawBeat = -1;               // edit-cursor position (raw beats)
        double selNoteBeat = -1; int selNoteMidi = -1; // selected note (raw beat + concert MIDI), -1 = none
        int editVoice;                          // active notation voice (0..4 = "Voix 1..5")
        readonly int[] lastVoiceMidi = { -1, -1, -1, -1, -1 }; // last concert MIDI entered per voice (octave-nearest entry)
        double lastEnteredBeat = -1; int lastEnteredDur; // last entered note (for Shift+lettre = stack a chord tone, same voice)
        FrameworkElement scoreContainer;       // the toolbar+ScoreView wrapper currently in editorHost
        static readonly int[] LetterPc = { 0, 2, 4, 5, 7, 9, 11 }; // C D E F G A B natural pitch-classes (letter 0..6)

        void SyncKeyToolbar()
        {
            syncingKey = true;
            var k = project.Key ?? (project.Key = new Engine.Score.KeySignature());
            cboTonic.SelectedIndex = Math.Max(0, Math.Min(6, k.TonicLetter));
            tglSharp.IsChecked = k.Accidental > 0;
            tglFlat.IsChecked = k.Accidental < 0;
            cboMode.SelectedIndex = Engine.Score.MusicalMode.Effective(k); // full mode (remembers dorien, etc.)
            if (tglTernary != null) tglTernary.IsChecked = project.TimeSigDen == 8;
            Engine.Flow.PatternGenerator.Ternary = project.TimeSigDen == 8; // generators (harp roll) follow the project meter
            syncingKey = false;
            SyncMeterCombo();
            SyncPickupCombo();
            UpdateKeySummary();
            UpdateMeterSummary();
        }

        // The dropdown buttons show a live summary of the key / meter (so the popups can stay collapsed).
        void UpdateKeySummary()
        {
            if (txtKeySummary == null) return;
            string tonic = cboTonic.SelectedItem as string ?? "";
            string acc = tglSharp.IsChecked == true ? "♯" : tglFlat.IsChecked == true ? "♭" : "";
            string mode = cboMode.SelectedItem as string ?? "";
            txtKeySummary.Text = (tonic + acc + " " + mode).Trim();
        }

        void UpdateMeterSummary()
        {
            if (txtMeterSummary == null) return;
            txtMeterSummary.Text = (cboMeter?.SelectedItem as string) ?? "";
        }

        // Collapse / expand the bottom editor panel. Collapsed: the row shrinks to just its title strip (Auto height,
        // MinHeight 0) and the splitter is hidden (nothing to drag). Expanded: the remembered pixel height is restored.
        double editorRowPx = 340;
        bool editorCollapsed;
        private void btnEditorCollapse_Click(object sender, RoutedEventArgs e)
        {
            editorCollapsed = !editorCollapsed;
            if (editorCollapsed)
            {
                if (editorRow.Height.IsAbsolute && editorRow.Height.Value > 40) editorRowPx = editorRow.Height.Value;
                editorScroll.Visibility = Visibility.Collapsed;
                editorSplitter.Visibility = Visibility.Collapsed;
                editorRow.MinHeight = 0;
                editorRow.Height = GridLength.Auto;
                btnEditorCollapse.Content = "▸";
            }
            else
            {
                editorScroll.Visibility = Visibility.Visible;
                editorSplitter.Visibility = Visibility.Visible;
                editorRow.MinHeight = 120;
                editorRow.Height = new GridLength(Math.Max(120, editorRowPx));
                btnEditorCollapse.Content = "▾";
            }
        }

        bool syncingMeter;
        // Fill the meter combo with BOTH divisions — binary (2/4,3/4,4/4) AND ternary (6/8,9/8,12/8) — so a ternary meter
        // is directly selectable from the list (picking x/8 re-bars to compound, scale 1.5; the "Ternaire" toggle is the
        // no-rebar reinterpretation). Selects the project's current meter.
        static readonly string[] MeterOpts = { "2/4", "3/4", "4/4", "6/8", "9/8", "12/8" };
        void SyncMeterCombo()
        {
            if (cboMeter == null) return;
            syncingMeter = true;
            cboMeter.Items.Clear();
            foreach (var o in MeterOpts) cboMeter.Items.Add(o);
            cboMeter.SelectedItem = project.TimeSigNum + "/" + project.TimeSigDen;
            if (cboMeter.SelectedItem == null) cboMeter.SelectedIndex = 2; // unlisted meter → 4/4
            syncingMeter = false;
        }

        bool syncingPickup;
        // Anacrusis choices, in QUARTER-beats (a croche = 0.5). "Aucune" = 0. The score shifts the barline grid by this;
        // playback is unchanged. Kept below a full bar (the % in ScoreView folds anything larger back into one bar).
        static readonly (string label, double beats)[] PickupOpts =
        {
            ("Aucune", 0), ("Double croche", 0.25), ("Croche", 0.5), ("Croche pointée", 0.75),
            ("Noire", 1.0), ("Noire pointée", 1.5), ("Blanche", 2.0),
        };
        void SyncPickupCombo()
        {
            if (cboPickup == null) return;
            syncingPickup = true;
            cboPickup.Items.Clear();
            foreach (var o in PickupOpts) cboPickup.Items.Add(o.label);
            int sel = 0; double best = double.MaxValue;
            for (int i = 0; i < PickupOpts.Length; i++) { double d = Math.Abs(PickupOpts[i].beats - project.PickupBeats); if (d < best) { best = d; sel = i; } }
            cboPickup.SelectedIndex = sel;
            syncingPickup = false;
        }
        private void Pickup_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (syncingPickup || cboPickup == null) return;
            int i = cboPickup.SelectedIndex;
            if (i < 0 || i >= PickupOpts.Length) return;
            double beats = PickupOpts[i].beats;
            if (Math.Abs(beats - project.PickupBeats) < 1e-9) return;
            project.PickupBeats = beats;
            Render();       // shift the timeline measure ruler by the levée
            RefreshScore(); // and the staff's barline grid
        }

        // Pick a different numerator (same division) → re-bar the riffs/silences to the new measure length.
        private void Meter_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (syncingMeter || !(cboMeter.SelectedItem is string s)) return;
            var p = s.Split('/');
            if (p.Length != 2 || !int.TryParse(p[0], out int num) || !int.TryParse(p[1], out int den)) return;
            if (num == project.TimeSigNum && den == project.TimeSigDen) return;

            int barBeats = den == 8 ? Math.Max(1, num / 3) : num; // new measure length in real beats (= temps count)
            project.TimeSigNum = num; project.TimeSigDen = den;
            project.TimeSigScale = den == 8 ? 1.5 : 1.0;

            var added = Engine.Timeline.TimelineImporter.ReSegment(project, barBeats, TimelineHelper.RiffById);
            foreach (var r in added) RiffLibrary.Instance.Riffs.Add(r);
            if (activeRiffGrid != null) activeRiffGrid.MeterDen = project.TimeSigDen;
            selectedItem = null; // items were rebuilt; drop the stale selection
            Render();
            RefreshScore();
            UpdateMeterSummary();
        }

        // Switch the piece binary ⇄ ternary WITHOUT touching the riffs or their size: x/4 ⇄ x/8 (2/4⇄6/8, 3/4⇄9/8,
        // 4/4⇄12/8). Only the score changes — ternary draws 3 croches (×1.5, beamed by 3), binary draws triolets.
        private void TglTernary_Click(object sender, RoutedEventArgs e)
        {
            if (tglTernary.IsChecked == true) // → ternary
            {
                if (project.TimeSigDen != 8) { project.TimeSigNum = Math.Max(1, project.TimeSigNum * 3); project.TimeSigDen = 8; }
                project.TimeSigScale = 1.5;
            }
            else // → binary
            {
                if (project.TimeSigDen == 8) { project.TimeSigNum = Math.Max(1, project.TimeSigNum / 3); project.TimeSigDen = 4; }
                project.TimeSigScale = 1.0;
            }
            if (activeRiffGrid != null) activeRiffGrid.MeterDen = project.TimeSigDen; // 1/6 ⇄ 1/8 entry snap
            SyncKeyToolbar();
            Render();        // timeline ruler reflects the meter (riffs/sizes unchanged)
            RefreshScore();  // score re-renders: 3 croches ⇄ triolets
        }

        void ApplyKeyFromToolbar()
        {
            if (syncingKey) return;
            int mode = Math.Max(0, cboMode.SelectedIndex);
            var oldKey = project.Key; // capture BEFORE reassigning so degree-locked chords can adapt their quality
            project.Key = new Engine.Score.KeySignature
            {
                TonicLetter = Math.Max(0, cboTonic.SelectedIndex),
                Accidental = tglSharp.IsChecked == true ? 1 : tglFlat.IsChecked == true ? -1 : 0,
                Mode = Engine.Score.MusicalMode.IsMinorish(mode) ? 1 : 0, // nearest major/minor for the armure
                FullMode = mode,                                          // exact mode (transpose source, etc.)
            };
            // Degree-locked chords follow the tonality: re-select them from the new key, then redraw (the placed
            // chords change without being touched). Render so the timeline chord labels reflect them too.
            if (Engine.Timeline.ChordModelOps.ResolveChordDegrees(project, oldKey)) Render();
            if (activeScore != null) RefreshScore(); // rebuild the armure live
            UpdateKeySummary();
        }

        // Re-select every degree-locked chord (Degree >= 0) for the current project key — so changing the
        // tonality (toolbar / transpose) auto-updates placed chords without editing them. The ROOT always
        // follows the degree's scale position. The QUALITY only adapts when the chord was a PLAIN diatonic
        // chord in the old key (so I↔i, ii↔iiø flip with the mode); an explicitly coloured chord (V9, V13,
        // borrowed…) keeps its colour and just moves its root. Returns true if any chord changed.

        static readonly int[] TonicPc = { 0, 2, 4, 5, 7, 9, 11 }; // Do Ré Mi Fa Sol La Si
        static readonly string[] BassModeNames = { "Aucune", "Par mesure (tenue)", "Par temps" };
        static readonly string[] HeldModeNames = { "Note seule", "Accord plaqué", "Fondamentale + quinte", "Fondamentale + tierce" };
        static readonly string[] ClimbModeNames = { "Arpège montant", "Arpège descendant", "Alberti (1-5-3…)", "Mixte" };
        static readonly string[] VoiceLeadModeNames = { "Aucun (position fond.)", "Auto (mouvement mini)", "Basse proche", "Haut proche" };
        static readonly string[] DiatonicColourNames = Engine.Flow.MusicTheory.DiatonicColourNames; // single source of truth (also drives the DiatonicColour clamp)

        static int KeyPc(Engine.Score.KeySignature k)
            => ((TonicPc[Math.Max(0, Math.Min(6, k.TonicLetter))] + k.Accidental) % 12 + 12) % 12;

        // Toolbar "Transposer…": pick a target key, apply the transposition to the whole project (the interval/mode
        // maths + chord re-derivation live in the shared Engine.Timeline.ChordModelOps), then refresh toolbar + score.
        private void btnTranspose_Click(object sender, RoutedEventArgs e)
        {
            var cur = project.Key ?? new Engine.Score.KeySignature();
            var dlg = new Dialogs.TransposeDialog(cur) { Owner = Window.GetWindow(this) };
            if (dlg.ShowDialog() != true) return;
            CommitRiffEditor();
            if (!Engine.Timeline.ChordModelOps.TransposeProject(project, TimelineHelper.RiffById, dlg.Result, dlg.ResultDirection, dlg.ResultMode)) return;
            SyncKeyToolbar();
            Render();
            RefreshScore();
        }



        private void Key_Changed(object sender, SelectionChangedEventArgs e) => ApplyKeyFromToolbar();
        private void TglSharp_Click(object sender, RoutedEventArgs e) { if (tglSharp.IsChecked == true) tglFlat.IsChecked = false; ApplyKeyFromToolbar(); }
        private void TglFlat_Click(object sender, RoutedEventArgs e) { if (tglFlat.IsChecked == true) tglSharp.IsChecked = false; ApplyKeyFromToolbar(); }

        // The timeline model uses public FIELDS, so fields must be (de)serialized.
        static readonly System.Text.Json.JsonSerializerOptions JsonOpts = new System.Text.Json.JsonSerializerOptions { IncludeFields = true };

        // ---- IMusicEditor (playback comes in Phase 2) ----
        public string ModeName => "Séquenceur";
        public string FileExtension => ".sq";
        public string CurrentPath { get; set; }
        public void StopAudio() { StopPlayback(); try { activeRiffGrid?.StopPreview(); } catch { } }

        // ---- playback ----
        // ▶ plays from the cursor; ⏹ stops and leaves the cursor where it stopped (▶ then resumes there).
        // No pause: the player rolls tempo/volume forward to the start beat instantly, so resuming mid-piece
        // lands in the right state.
        private void btnPlay_Click(object sender, RoutedEventArgs e)
        {
            if (player == null) StartPlayback(); // already playing -> ignore
        }

        private void btnStop_Click(object sender, RoutedEventArgs e) => StopPlayback();

        void StartPlayback()
        {
            CommitRiffEditor(); // stop any riff preview first
            if (!SoundFontGuard.EnsureReady(Window.GetWindow(this), "Lecture")) return;
            try
            {
                player = new Engine.Timeline.TimelinePlayer(project, TimelineHelper.RiffById, AudioFormat.SampleRate);
                player.StartBeat = startBeat; // start at the cursor; tempo/volume are set for that beat in Start()
                // A background thread pre-renders ahead so the audio device only copies samples (absorbs the
                // SoundFont synthesis cost / GC spikes). The device is started only once the buffer has a head
                // start (Primed) — otherwise it would drain the buffer as fast as it fills.
                playBuffer = new Engine.Timeline.LookaheadBuffer(player, player.Start, player.Stop, AudioFormat.SampleRate);
                playBuffer.Ended += () => Dispatcher.BeginInvoke((Action)OnPlaybackEnded);
                playBuffer.Primed += () => Dispatcher.BeginInvoke((Action)BeginPlaybackDevice);
                EnsureCursor();
                MoveCursor(startBeat);
                if (btnPlay != null) btnPlay.Content = "⏳"; // filling the buffer before playback
                playBuffer.Start(); // producer fills; the device starts on Primed
            }
            catch (Exception ex) { MessageBox.Show("Lecture : " + ex.Message); StopPlayback(); }
        }

        // Called on the UI thread once the look-ahead buffer has its head start: actually start the device.
        void BeginPlaybackDevice()
        {
            if (playBuffer == null || playWaveOut != null) return; // stopped during prime, or already started
            playWaveOut = new NAudio.Wave.WaveOutEvent { DesiredLatency = 150 };
            playWaveOut.Init(playBuffer);
            playWaveOut.Play();
            if (btnPlay != null) btnPlay.Content = "▶";
            playTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
            playTimer.Tick += (s, ev) => MoveCursor(PlayedBeat());
            playTimer.Start();
        }

        // Stop: freeze the cursor where playback reached (the AUDIBLE position), so ▶ resumes from there.
        // The audible beat = the consumed-sample position mapped back through the tempo map.
        double PlayedBeat() => (player != null && playBuffer != null)
            ? player.BeatAtSample(player.SampleAtStart + playBuffer.ConsumedSamples) : startBeat;

        public void StopPlayback()
        {
            double beat = PlayedBeat();
            TeardownPlayer();
            startBeat = Math.Max(0, Math.Min(TotalBeats(), beat));
            MoveCursor(startBeat);
        }

        // Reached the end on its own -> rewind the cursor to the top.
        void OnPlaybackEnded()
        {
            TeardownPlayer();
            startBeat = 0;
            MoveCursor(0);
        }

        void TeardownPlayer()
        {
            if (playTimer != null) { playTimer.Stop(); playTimer = null; }
            if (playWaveOut != null) { try { playWaveOut.Stop(); playWaveOut.Dispose(); } catch { } playWaveOut = null; }
            if (playBuffer != null) { try { playBuffer.Stop(); } catch { } playBuffer = null; } // stops the producer + inner
            if (player != null) { try { player.Stop(); } catch { } player = null; }
            if (btnPlay != null) btnPlay.Content = "▶";
        }

        // The yellow play head + its blue start handle live permanently (visible even when idle, where
        // they sit at the start position). During playback they sweep together; on stop they return here.
        void EnsureCursor()
        {
            if (playCursor == null)
            {
                playCursor = new System.Windows.Shapes.Rectangle { Width = 2, Fill = new SolidColorBrush(Color.FromRgb(0xFF, 0xDD, 0x33)), IsHitTestVisible = false };
                cursorCanvas.Children.Add(playCursor);
            }
            if (startMarker == null)
            {
                startMarker = new System.Windows.Shapes.Polygon
                {
                    Points = new PointCollection { new Point(-7, 0), new Point(7, 0), new Point(0, 18) },
                    Fill = new SolidColorBrush(Color.FromRgb(0x33, 0x66, 0xCC)),
                    Stroke = new SolidColorBrush(Color.FromRgb(0xCF, 0xDE, 0xFA)),
                    StrokeThickness = 1,
                    Cursor = Cursors.SizeWE,
                    ToolTip = "Glisser pour définir le point de départ de la lecture",
                };
                Panel.SetZIndex(startMarker, 10);
                startMarker.MouseLeftButtonDown += startMarker_MouseLeftButtonDown;
                startMarker.MouseMove += startMarker_MouseMove;
                startMarker.MouseLeftButtonUp += startMarker_MouseLeftButtonUp;
                startCanvas.Children.Add(startMarker);
            }
            playCursor.Visibility = Visibility.Visible;
            MoveCursor(startBeat);
        }

        void MoveCursor(double beat)
        {
            double x = beat * PxPerBeat;
            if (playCursor != null) { playCursor.Height = lanePanel.ActualHeight; Canvas.SetLeft(playCursor, x); }
            if (startMarker != null) Canvas.SetLeft(startMarker, x + 1); // centre the handle on the 2px line
            // Auto-scroll only while actually playing (so setting the start point doesn't yank the scroll).
            // CONTINUOUS follow, like the score view: always aim to centre the cursor, then clamp. The clamping
            // gives the three phases for free — the view holds still until the cursor reaches the middle (target
            // would be negative), then tracks it smoothly, and finally lets it run out to the right edge once the
            // end of the piece can no longer scroll further.
            if (player != null)
            {
                double vw = laneScroll.ViewportWidth;
                double maxOff = laneScroll.ScrollableWidth;
                if (vw > 1 && maxOff > 0.5)
                {
                    double target = Math.Max(0, Math.Min(maxOff, x - vw * 0.5));
                    if (Math.Abs(laneScroll.HorizontalOffset - target) > 0.5) laneScroll.ScrollToHorizontalOffset(target);
                }
            }

            // If a score is shown, sweep its cursor too (same beat position).
            if (activeScore != null && ReferenceEquals(editorHost.Content, scoreContainer)) activeScore.SetCursorBeat(beat);
        }

        int scoreGen; // bumped each RefreshScore; a stale background build (superseded) discards its result

        // Show all CHECKED tracks as a multi-staff score in the bottom editor area (each track = one staff,
        // clef from import/instrument/range; transposing parts at written pitch). The cursor follows playback.
        // The heavy part — flattening each track + generating its riffs/patterns/drums — runs OFF the UI thread
        // and in PARALLEL across tracks, so checking a track never freezes the UI. The layout/draw stays on the
        // UI thread (WPF glyphs). A generation counter drops results from a refresh that's been superseded.
        // The global "🎼 Partition / Éditeur" toggle. Enabled only when ≥1 track is checked (♫); reflects ScoreVisible.
        void SyncViewToggle()
        {
            if (tglViewScore == null) return;
            tglViewScore.IsEnabled = scoreTracks.Count > 0;
            tglViewScore.IsChecked = ScoreVisible;
        }

        void tglViewScore_Click(object sender, RoutedEventArgs e)
        {
            viewScore = tglViewScore.IsChecked == true;
            RefreshScore();  // shows the score or brings back the module editor, per ScoreVisible
        }

        // Global "Arpegiato" toggle: when on, rolled-chord detection collapses staggered/overlapping clusters into one
        // chord + an arpeggio wave; when off (default), every note is notated separately. Re-render the score.
        void chkArpeggio_Click(object sender, RoutedEventArgs e)
        {
            if (ScoreVisible) RefreshScore();
        }

        async void RefreshScore()
        {
            SyncViewToggle();
            if (!ScoreVisible)
            {
                // Not showing the score (toggle off, or no ♫ track): leave score-edit mode + detach the window key hook
                // (so it can't swallow keys in the module editor), then bring back the SELECTED module's editor.
                if (scoreEditMode) { scoreEditMode = false; HookScoreKeys(false); }
                if (activeScore != null) { activeScore = null; OpenModuleEditor(selectedTrack, selectedItem); }
                return;
            }
            CommitRiffEditor(); // stop any inline riff preview first

            Engine.Score.ScoreBuilder.DetectRolls = chkArpeggio?.IsChecked == true; // arpégiato opt-in (default off)
            int myGen = ++scoreGen;
            var toBuild = new List<TimelineTrack>();
            foreach (var t in project.Tracks) if (scoreTracks.Contains(t)) toBuild.Add(t);
            txtEditorTitle.Text = "Partition — calcul…";

            // ResolveLoops mutates the project (sizes looping Repeats) — run it ONCE here on the UI thread, then
            // the per-track builds (parallel, background) only read.
            Engine.Timeline.TimelineProject.ResolveLoops(project, TimelineHelper.RiffById);

            List<Engine.Score.TrackScore> list;
            try
            {
                list = await System.Threading.Tasks.Task.Run(() =>
                {
                    var perTrack = new List<Engine.Score.TrackScore>[toBuild.Count];
                    System.Threading.Tasks.Parallel.For(0, toBuild.Count, k =>
                    {
                        var t = toBuild[k];
                        var l = new List<Engine.Score.TrackScore>();
                        // A chord track that carries melodic cells shows an EXTRA melody staff ABOVE the chord staff.
                        if (Engine.Score.ScoreBuilder.TrackHasMelodic(t)) l.Add(Engine.Score.ScoreBuilder.Build(project, t, TimelineHelper.RiffById, false, melodic: true));
                        l.Add(Engine.Score.ScoreBuilder.Build(project, t, TimelineHelper.RiffById, false));
                        perTrack[k] = l;
                    });
                    var flat = new List<Engine.Score.TrackScore>();
                    foreach (var l in perTrack) if (l != null) flat.AddRange(l);
                    return flat;
                });
            }
            catch (Exception ex) { if (myGen == scoreGen) txtEditorTitle.Text = "Partition — erreur : " + ex.Message; return; }

            if (myGen != scoreGen) return;                 // a newer RefreshScore superseded this one
            if (!ScoreVisible) { editorHost.Content = null; activeScore = null; txtEditorTitle.Text = "Éditeur"; return; }

            var view = new Controls.Score.ScoreView();
            view.EditMode = scoreEditMode;
            view.MeasureClicked += LocateRiffAtBeat; // click a measure → reveal its riff in the timeline (no edit)
            view.EditPositionClicked += ScoreEditClickAt;
            view.NoteEditClicked += ScoreEditSelectNote;
            view.NotePlaceClicked += ScoreMousePlace;
            try { view.Configure(list, project.TimeSigNum, project.TimeSigDen, project.TimeSigScale, project.PickupBeats); }
            catch (Exception ex) { txtEditorTitle.Text = "Partition — erreur rendu : " + ex.Message; return; } // never let a render bug break note-entry
            scoreContainer = ScoreContainer(view);
            editorHost.Content = scoreContainer;
            activeScore = view;
            txtEditorTitle.Text = list.Count > 1 ? "Partition (" + list.Count + " portées)" : "Partition";
            SetEditorScroll(true); // the score manages its own scrolling
            if (scoreEditMode) UpdateEditCursor();
            view.SetCursorBeat(player != null ? PlayedBeat() : startBeat);
        }

        // Click the ruler or drag the blue handle -> set the play start beat (snapped to the beat).
        private void startCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => SetStartFromX(e.GetPosition(startCanvas).X);

        void SetStartFromX(double x)
        {
            double beat = Math.Round(x / PxPerBeat);
            double maxBeat = TotalBeats();
            if (beat < 0) beat = 0; else if (beat > maxBeat) beat = maxBeat;
            startBeat = beat;
            MoveCursor(beat);
        }

        private void startMarker_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            draggingMarker = true; startMarker.CaptureMouse();
            SetStartFromX(e.GetPosition(startCanvas).X);
            e.Handled = true;
        }

        private void startMarker_MouseMove(object sender, MouseEventArgs e)
        {
            if (draggingMarker) { SetStartFromX(e.GetPosition(startCanvas).X); e.Handled = true; }
        }

        private void startMarker_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (draggingMarker) { draggingMarker = false; startMarker.ReleaseMouseCapture(); e.Handled = true; }
        }

        // A .sq file = the arrangement + the riffs it references (same idea as the graph's .graph).
        public bool Save(string path)
        {
            var doc = new TimelineDocument { Project = project };
            doc.Riffs.AddRange(RiffLibrary.Instance.Riffs);
            System.IO.File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(doc, JsonOpts));
            CurrentPath = path;
            return true;
        }

        /// <summary>Gather this project's state as attachable context for a GitHub bug report (see
        /// <see cref="Dialogs.ReportBugDialog"/>). The serialization/formatting lives in the decoupled
        /// <see cref="Engine.BugReport.BugReportContext"/>; this only forwards the screen's state.</summary>
        public Engine.BugReport.BugReportContext BuildBugReportContext()
            => Engine.BugReport.BugReportContext.Build(project, RiffLibrary.Instance.Riffs, templateSpec, CurrentPath, TemplateSeed);

        // Open a .sq (native) or import a .mid/.mscz/.mscx into the timeline.
        public void LoadFile(string path)
        {
            string ext = (System.IO.Path.GetExtension(path) ?? "").ToLowerInvariant();
            if (ext == ".sq") LoadSqFile(path);
            else ImportFile(path); // .mid / .mscz / .mscx
        }

        // ---- section-based AI templates (Data/templates/*.json, see TemplateSpec / the "génère un template" prompt) ----

        // Apply a rich section-based template, expanded to `measures` bars by alternating the dev sections. Builds an
        // audible chord bed on the Accords track, a drum groove per section, and a melodic line per instrument (its
        // per-section motif). Unsaved (CurrentPath = null).
        // ---- generative templates -------------------------------------------------------------------------------
        // A template opened from the home screen is remembered (spec + target length + current seed) so "Régénérer"
        // can re-pick from its banks with a new seed and rebuild the whole project. Not persisted: a saved project is
        // a plain arrangement, no longer tied to its template.
        Engine.Timeline.TemplateSpec templateSpec;
        int templateMeasures;
        public int TemplateSeed { get; private set; }
        public bool FromTemplate => templateSpec != null;


        public void LoadTemplateSpec(Engine.Timeline.TemplateSpec spec, int measures)
        {
            if (spec == null) return;
            templateSpec = spec;
            templateMeasures = measures;
            TemplateSeed = NewSeed();
            RebuildFromTemplate();
        }

        /// <summary>Re-pick from the template's banks with a NEW seed and rebuild (the "Régénérer" button).</summary>
        public void RegenerateFromTemplate()
        {
            if (templateSpec == null) return;
            TemplateSeed = NewSeed();
            RebuildFromTemplate();
        }

        static int NewSeed() { var g = Guid.NewGuid().GetHashCode(); return g == int.MinValue ? 1 : Math.Abs(g) % 1000000 + 1; }

        // Build (or re-pick) the project from the current template spec/seed and load it like any other document. The
        // spec/seed STATE stays on the editor (for the "Régénérer" chip and the bug report); only the construction moved
        // to the shared Engine.Timeline.TemplateProjectBuilder.
        void RebuildFromTemplate()
        {
            var spec = templateSpec; if (spec == null) return;
            int measures = templateMeasures > 0 ? templateMeasures : Math.Max(1, spec.Measure?.Count ?? 32);
            LoadDocument(Engine.Timeline.TemplateProjectBuilder.Build(spec, measures, TemplateSeed));
            UpdateTemplateBar();
            EnsureCursor();
        }


        // Show / refresh the template chip (seed + Régénérer), or hide it when this project isn't from a template.
        void UpdateTemplateBar()
        {
            if (tplBar == null) return;
            tplBar.Visibility = FromTemplate ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
            if (FromTemplate && txtTplSeed != null) txtTplSeed.Text = "#" + TemplateSeed;
        }

        private void btnRegen_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            if (!FromTemplate) return;
            StopPlayback();
            RegenerateFromTemplate();
        }

        // Tonic pitch-class (0..11) + template mode index (0..8) → KeySignature (letter/accidental for the armure,
        // FullMode carrying the exact mode so the scale machinery uses it).

        // Rescale a note list from one slices-per-beat to another (start/length), for mixing banks authored at
        // different resolutions in the same chord (articulation vs melodic cell).

        static int DrumStyleIndex(string name)
        {
            if (!string.IsNullOrWhiteSpace(name))
                for (int i = 0; i < DrumPattern.StyleNames.Length; i++)
                    if (string.Equals(DrumPattern.StyleNames[i], name.Trim(), StringComparison.OrdinalIgnoreCase)) return i;
            return 2;   // Pop fallback
        }

        // Async like the importers: the slow file read + JSON parse runs on a background thread (UI stays
        // responsive, progress dialog), then the project is applied + Render()ed back on the UI thread.
        async void LoadSqFile(string path)
        {
            var prog = new ImportProgressDialog { Owner = Window.GetWindow(this) };
            prog.Show();
            try
            {
                prog.Set(0.1, "Ouverture du fichier…");
                var doc = await System.Threading.Tasks.Task.Run(() =>
                    System.Text.Json.JsonSerializer.Deserialize<TimelineDocument>(System.IO.File.ReadAllText(path), JsonOpts) ?? new TimelineDocument());

                prog.Set(0.7, "Chargement…");
                ApplyDocument(doc, path);
                await RenderBatched(prog); // add the lane controls in batches so the UI stays responsive
                prog.Set(1.0, "Terminé");
            }
            catch (Exception ex) { MessageBox.Show("Erreur d'ouverture : " + ex.Message); }
            finally { prog.Close(); }
        }

        /// <summary>Load a project document into this editor — the single path shared by opening a .sq, importing, and the
        /// generative SOURCES (AI arrangement, structure, template). The editor stays agnostic to where <paramref name="doc"/>
        /// came from. <paramref name="path"/> is the backing file (null for an unsaved, source-generated document).</summary>
        public void LoadDocument(TimelineDocument doc, string path = null)
        {
            ApplyDocument(doc, path);
            Render();
        }

        // Apply a document's project + riffs onto this editor's live state (no render — the caller renders, batched or not).
        void ApplyDocument(TimelineDocument doc, string path)
        {
            if (doc == null) return;
            RiffLibrary.Instance.Riffs.Clear();
            if (doc.Riffs != null) foreach (var r in doc.Riffs) RiffLibrary.Instance.Riffs.Add(r);

            var dp = doc.Project ?? new TimelineProject();
            project.Tempo = (dp.Tempo != null && dp.Tempo.Count > 0)
                ? dp.Tempo : new System.Collections.Generic.List<TempoChange> { new TempoChange() };
            project.Key = dp.Key ?? new Engine.Score.KeySignature();
            project.TimeSigNum = dp.TimeSigNum > 0 ? dp.TimeSigNum : 4;
            project.TimeSigDen = dp.TimeSigDen > 0 ? dp.TimeSigDen : 4;
            project.TimeSigScale = dp.TimeSigScale > 0 ? dp.TimeSigScale : 1.0;
            project.Arrangement = dp.Arrangement;
            project.UserChordStyles = dp.UserChordStyles ?? new System.Collections.Generic.List<UserChordStyle>();
            project.UserMelodicLines = dp.UserMelodicLines ?? new System.Collections.Generic.List<UserChordStyle>();
            project.UserDrumStyles = dp.UserDrumStyles ?? new System.Collections.Generic.List<UserChordStyle>();
            project.PickupBeats = dp.PickupBeats;
            project.MinBeats = dp.MinBeats;
            project.Tracks.Clear();
            if (dp.Tracks != null) foreach (var t in dp.Tracks) project.Tracks.Add(t);
            TimelineHelper.SyncUserStyleRefs(project);   // make chords that reference a user style authoritative from it
            TimelineHelper.EnsureChordTrack(project);    // adopt/create the permanent chords track (bottom-pinned)
            SyncKeyToolbar();
            scoreTracks.Clear(); activeScore = null;
            selectedTrack = project.Tracks.Count > 0 ? project.Tracks[0] : null;
            selectedItem = null;
            editorHost.Content = null;
            CurrentPath = path;
            txtBpm.Text = ((int)project.MainBpm).ToString();
        }

      
       
        double SeqDispLen(System.Collections.Generic.IList<TimelineItem> items)
        {
            double c = 0;
            if (items != null) foreach (var it in items) c += it.SilenceBefore + TimelineHelper.DispLen(it);
            return c;
        }

        double TotalBeats()
        {
            double end = Math.Max(32, project.MinBeats);   // a template's chosen bar count floors an empty project
            foreach (var t in project.Tracks) end = Math.Max(end, SeqDispLen(t.Items));
            return end + 8; // a little room past the end
        }

        // ---- rendering -------------------------------------------------------------

        /// <summary>Re-tint one track's module boxes after its instrument changed, IN PLACE. A full <see cref="Render"/>
        /// would rebuild headerPanel — i.e. destroy the very ComboBox currently raising SelectionChanged — so the boxes
        /// are recoloured directly instead. Chord/cadence boxes are skipped: they are coloured by harmonic function, not
        /// by the instrument family.</summary>
        void RecolorTrackBoxes(TimelineTrack track)
        {
            if (track == null) return;
            var fill = new SolidColorBrush(Controls.InstrumentColors.BoxFill(track.Instrument));
            var border = new SolidColorBrush(Controls.InstrumentColors.BoxBorder(track.Instrument));
            foreach (var kv in leafBoxes)
            {
                if (!boxOwner.TryGetValue(kv.Key, out var owner) || owner != track) continue;
                if (kv.Key.Module is PatternGeneratorModule || kv.Key.Module is CadenceModule) continue;
                kv.Value.SetColors(fill, border);
            }
        }

        void Render()
        {
            TimelineHelper.EnsureChordTrack(project);   // invariant: exactly one chords track, pinned at the bottom (whatever added tracks)
            headerPanel.Children.Clear();
            lanePanel.Children.Clear();
            highlighters.Clear();
            trackHeaders.Clear();
            leafBoxes.Clear();
            boxOwner.Clear();
            TimelineProject.ResolveLoops(project, TimelineHelper.RiffById); // size looping Repeats to fill up to the end
            double laneWidth = TotalBeats() * PxPerBeat;

            measureRuler.Configure(laneWidth, 20, PxPerBeat, TimelineHelper.RulerBeatsPerBar(project), project.PickupBeats); // measure-number ruler on top (4 beats/bar)
            if (startCanvas != null) startCanvas.Width = laneWidth;
            if (startBeat > TotalBeats()) startBeat = 0; // content shrank past the start handle

            // Tempo lane (header + ruler).
            headerPanel.Children.Add(MakeHeader("Tempo", TempoH, null));
            lanePanel.Children.Add(LaneRow(MakeTempoLane(laneWidth), TempoH));

            // Chord trame lane (when this is a composed arrangement).
            if (IsComposedArrangement())
            {
                headerPanel.Children.Add(MakeChordHeader(ChordH));
                lanePanel.Children.Add(LaneRow(MakeChordLane(laneWidth), ChordH));
            }

            for (int i = 0; i < project.Tracks.Count; i++)
            {
                var track = project.Tracks[i];
                if (track.Type == TimelineTrackType.Chord) continue;   // rendered separately in the docked chords lane
                double rh = TrackRowH(track);
                headerPanel.Children.Add(MakeHeader(null, rh, track));
                lanePanel.Children.Add(LaneRow(MakeTrackRow(track, laneWidth), rh));
            }
            RenderChordDock(laneWidth);
            UpdateToolbar();
            SyncKeyToolbar();
            if (player == null) MoveCursor(startBeat); // keep the idle cursor/handle on the start position
        }

        // Render the permanent CHORDS track into its own docked lane (between the main lanes and the splitter), instead of
        // in the scrolling lane list. Its header goes in chordHeaderHost, its lane in chordLanePanel (same width + horizontal
        // scroll as the main lanes, kept in sync by laneScroll_ScrollChanged).
        void RenderChordDock(double laneWidth)
        {
            if (chordHeaderHost == null || chordLanePanel == null) return;
            chordHeaderHost.Content = null;
            chordLanePanel.Children.Clear();
            var chord = TimelineHelper.ChordTrack(project);
            if (chord == null) return;
            double rh = TrackRowH(chord);
            chordHeaderHost.Content = MakeHeader(null, rh, chord);
            chordLanePanel.Children.Add(LaneRow(MakeTrackRow(chord, laneWidth), rh));
        }

        // Like Render but the (heavy) module boxes are added in BATCHES with dispatcher yields, so loading a big
        // piece doesn't freeze the UI: the empty lanes appear at once, then fill in progressively.
        async System.Threading.Tasks.Task RenderBatched(ImportProgressDialog prog)
        {
            headerPanel.Children.Clear();
            lanePanel.Children.Clear();
            highlighters.Clear();
            trackHeaders.Clear();
            leafBoxes.Clear();
            boxOwner.Clear();
            TimelineProject.ResolveLoops(project, TimelineHelper.RiffById);
            double laneWidth = TotalBeats() * PxPerBeat;
            measureRuler.Configure(laneWidth, 20, PxPerBeat, TimelineHelper.RulerBeatsPerBar(project), project.PickupBeats);
            if (startCanvas != null) startCanvas.Width = laneWidth;
            if (startBeat > TotalBeats()) startBeat = 0;

            headerPanel.Children.Add(MakeHeader("Tempo", TempoH, null));
            lanePanel.Children.Add(LaneRow(MakeTempoLane(laneWidth), TempoH));
            if (IsComposedArrangement())
            {
                headerPanel.Children.Add(MakeChordHeader(ChordH));
                lanePanel.Children.Add(LaneRow(MakeChordLane(laneWidth), ChordH));
            }

            var lanes = new List<(Canvas canvas, TimelineTrack track)>(); // empty lanes now, items filled below
            foreach (var track in project.Tracks)
            {
                if (track.Type == TimelineTrackType.Chord) continue;   // rendered separately in the docked chords lane
                double rh = TrackRowH(track);
                headerPanel.Children.Add(MakeHeader(null, rh, track));
                if (track.Collapsed) { lanePanel.Children.Add(LaneRow(MakeTrackRow(track, laneWidth), rh)); continue; } // no items to batch-fill
                var stack = new StackPanel();
                var vol = new Controls.TimelineEditor.VolumeLaneControl();
                vol.Configure(track, PxPerBeat, VolLaneH, laneWidth);
                stack.Children.Add(vol);
                var lane = MakeTrackLane(track, laneWidth, fillItems: false);
                stack.Children.Add(lane);
                lanePanel.Children.Add(LaneRow(stack, rh));
                lanes.Add((lane, track));
            }
            RenderChordDock(laneWidth);
            UpdateToolbar();
            SyncKeyToolbar();

            int total = 0; foreach (var t in project.Tracks) total += t.Items.Count;
            int done = 0;
            foreach (var (canvas, track) in lanes)
            {
                double cursor = 0;
                foreach (var item in track.Items)
                {
                    cursor += item.SilenceBefore;
                    AddItem(canvas, track, item, cursor);
                    cursor += TimelineHelper.DispLen(item);
                    if (++done % 24 == 0)
                    {
                        prog?.Set(0.7 + 0.29 * done / Math.Max(1, total), "Affichage… (" + done + "/" + total + ")");
                        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
                    }
                }
            }
            if (player == null) MoveCursor(startBeat);
        }

        // Show only the "Insérer" items that fit the selected track's type (instrument → Riff/Accords/Cadence,
        // drum → Batterie; Repeat for either). The menu itself is disabled when no track is selected.
        void UpdateToolbar()
        {
            bool instr = selectedTrack != null && selectedTrack.Type == TimelineTrackType.Instrument;
            bool drum = selectedTrack != null && selectedTrack.Type == TimelineTrackType.Drum;
            // Accords/Cadence ALWAYS go to the dedicated chords track (no need to select it), so they are always offered.
            // Riff + ligne mélodique are for INSTRUMENT tracks; Batterie for the DRUM track.
            if (menuInsert != null) menuInsert.IsEnabled = true;
            if (miAddRiff != null) miAddRiff.Visibility = instr ? Visibility.Visible : Visibility.Collapsed;
            if (miInsertMelodicLine != null) miInsertMelodicLine.Visibility = instr ? Visibility.Visible : Visibility.Collapsed;
            if (miAddPattern != null) miAddPattern.Visibility = Visibility.Visible;
            if (miAddCadence != null) miAddCadence.Visibility = Visibility.Visible;
            if (miAddDrum != null) miAddDrum.Visibility = drum ? Visibility.Visible : Visibility.Collapsed;
        }

       

        // A clearly-visible grey divider drawn at the BOTTOM of every track row, in both the header column and
        // the lanes, so tracks read as separated. Shared (UI thread) across all rows.
        static readonly Brush TrackSeparatorBrush = new SolidColorBrush(Color.FromRgb(0x5A, 0x5A, 0x64));

        // Wrap a lane row (tempo lane, or a track's volume+lane stack) so it gets the same bottom divider as its
        // header. Fixed Height = the header's height (border drawn INSIDE it), so header and lane rows stay aligned.
        Border LaneRow(UIElement content, double height)
            => new Border { Height = height, Child = content, BorderBrush = TrackSeparatorBrush, BorderThickness = new Thickness(0, 0, 0, 1) };

        // Height of a track's header + lane row: minimal when collapsed (issue #5), else the full volume + lane stack.
        double TrackRowH(TimelineTrack track) => track != null && track.Collapsed ? CollapsedH : VolLaneH + LaneH;

        // The lane-column content for a track: a thin filler when collapsed, else the volume sub-track + module lane.
        UIElement MakeTrackRow(TimelineTrack track, double laneWidth, bool fillItems = true)
        {
            if (track.Collapsed)
                return MakeCollapsedLane(track, laneWidth);
            var stack = new StackPanel();
            var vol = new Controls.TimelineEditor.VolumeLaneControl();
            vol.Configure(track, PxPerBeat, VolLaneH, laneWidth);
            stack.Children.Add(vol);
            stack.Children.Add(MakeTrackLane(track, laneWidth, fillItems));
            return stack;
        }

        Border MakeHeader(string fixedTitle, double height, TimelineTrack track)
        {
            var border = new Border { Height = height, BorderBrush = TrackSeparatorBrush, BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(6, 4, 6, 4) };
            if (track != null && track == selectedTrack) border.Background = new SolidColorBrush(Color.FromRgb(0x2C, 0x2C, 0x38));

            if (track == null) // tempo header
            {
                border.Child = new TextBlock { Text = fixedTitle, Foreground = Brushes.White, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center };
                return border;
            }

            var panel = new StackPanel();
            var top = new StackPanel { Orientation = Orientation.Horizontal };
            // Collapse / expand toggle (issue #5): shrinks this track's header + lane to a minimal height (title + button)
            // so many tracks fit on screen without scrolling. State lives on the track (persisted with the project).
            var collapseBtn = new Button
            {
                Content = track.Collapsed ? "▸" : "▾", Width = 18, Height = 18, Padding = new Thickness(0), FontSize = 10,
                Margin = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center, Cursor = Cursors.Hand,
                Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = Brushes.White,
                ToolTip = track.Collapsed ? "Déplier la piste" : "Replier la piste (gagner de la hauteur)"
            };
            collapseBtn.Click += (s, e) => { track.Collapsed = !track.Collapsed; Render(); };
            top.Children.Add(collapseBtn);
            // Instrument-family colour dot (fixed in the header, so always visible even when the lane scrolls).
            var famDot = new Ellipse { Width = 9, Height = 9, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0), Fill = new SolidColorBrush(HeaderFamilyColor(track)) };
            top.Children.Add(famDot);
            var name = new TextBox { Text = track.Name, Width = 88, FontSize = 11 };
            name.LostFocus += (s, e) => track.Name = name.Text;
            top.Children.Add(name);

            // Collapsed: minimal header (expand button + colour dot + name), skip instrument/volume/mute controls.
            if (track.Collapsed)
            {
                border.Padding = new Thickness(6, 2, 6, 2);
                top.VerticalAlignment = VerticalAlignment.Center;
                border.Child = top;
                trackHeaders[track] = border;
                border.PreviewMouseLeftButtonDown += (s, e) => SelectTrack(track);
                return border;
            }
            var scoreChk = new CheckBox { Content = "♫", FontFamily = new FontFamily("Segoe UI Symbol"), IsChecked = scoreTracks.Contains(track), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0), Cursor = Cursors.Hand, ToolTip = "Afficher cette piste dans la partition" };
            scoreChk.Checked += (s, e) => { scoreTracks.Add(track); viewScore = true; RefreshScore(); }; // checking ♫ shows the score
            scoreChk.Unchecked += (s, e) => { scoreTracks.Remove(track); RefreshScore(); };
            top.Children.Add(scoreChk);
            if (track.Type != TimelineTrackType.Chord)   // the chords track is permanent → no delete button
            {
                var del = new Button { Content = "✕", Margin = new Thickness(4, 0, 0, 0), Cursor = Cursors.Hand, Style = (Style)FindResource("deleteIconButton"), ToolTip = "Supprimer la piste" };
                del.Click += (s, e) => { project.Tracks.Remove(track); scoreTracks.Remove(track); if (selectedTrack == track) selectedTrack = null; Render(); RefreshScore(); };
                top.Children.Add(del);
            }
            panel.Children.Add(top);

            if (track.Type != TimelineTrackType.Drum)   // instrument + chords tracks pick their instrument (drums = kit)
            {
                var inst = new ComboBox { Margin = new Thickness(0, 3, 0, 0), FontSize = 11, ItemsSource = InstrumentCatalog.Names(), SelectedIndex = track.Instrument };
                inst.SelectionChanged += (s, e) =>
                {
                    if (inst.SelectedIndex < 0) return;
                    track.Instrument = inst.SelectedIndex;
                    famDot.Fill = new SolidColorBrush(HeaderFamilyColor(track)); // reflect the new family colour live
                    RecolorTrackBoxes(track);   // …and re-tint this lane's module boxes to match the new family
                    // If this track's riff editor is open, reflect the new instrument in its preview + MIDI audition.
                    if (activeRiffGrid != null && riffEditTrack == track)
                        activeRiffGrid.SetPreviewInstrument(InstrumentCatalog.GetPreset(track.Instrument), track.Instrument);
                    // If this track's score is shown, the clef/transposition may have changed -> rebuild it.
                    if (activeScore != null && scoreTracks.Contains(track)) RefreshScore();
                };
                panel.Children.Add(inst);
            }
            else
            {
                panel.Children.Add(new TextBlock { Text = "Batterie (kit auto)", Foreground = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99)), FontSize = 11, Margin = new Thickness(0, 4, 0, 0) });
            }

            // base volume
            var volRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 0) };
            volRow.Children.Add(new TextBlock { Text = "Vol", Foreground = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99)), FontSize = 10, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) });
            var vol = new Slider { Minimum = 0, Maximum = 1.5, Value = track.Volume, Width = 80, VerticalAlignment = VerticalAlignment.Center, SmallChange = 0.05 };
            vol.ValueChanged += (s, e) => track.Volume = vol.Value;
            volRow.Children.Add(vol);
            // Mute / Solo (take effect on the next playback)
            var mute = new System.Windows.Controls.Primitives.ToggleButton { Content = "M", IsChecked = track.Mute, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Style = (Style)FindResource("MuteToggle"), ToolTip = "Muet (silence cette piste)" };
            mute.Checked += (s, e) => track.Mute = true; mute.Unchecked += (s, e) => track.Mute = false;
            var solo = new System.Windows.Controls.Primitives.ToggleButton { Content = "S", IsChecked = track.Solo, Margin = new Thickness(3, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Style = (Style)FindResource("SoloToggle"), ToolTip = "Solo (n'entendre que les pistes en solo)" };
            solo.Checked += (s, e) => track.Solo = true; solo.Unchecked += (s, e) => track.Solo = false;
            volRow.Children.Add(mute); volRow.Children.Add(solo);
            panel.Children.Add(volRow);

            border.Child = panel;
            trackHeaders[track] = border; // for incremental selection highlight
            // Preview so the click still selects even though child controls (combo/slider/textbox) handle it.
            // Only re-render when the selection actually changes, so editing the header's own controls works.
            border.PreviewMouseLeftButtonDown += (s, e) => SelectTrack(track);
            return border;
        }

        UIElement MakeTempoLane(double width)
        {
            var lane = new Controls.TimelineEditor.TempoLaneControl();
            lane.Configure(width, TempoH, PxPerBeat, project.Tempo);
            return lane;
        }

        // Header for the chord trame lane: a title + the "auto transpose" toggle.
        Border MakeChordHeader(double height)
        {
            var border = new Border { Height = height, BorderBrush = TrackSeparatorBrush, BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(6, 1, 4, 1) };
            var sp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            sp.Children.Add(new TextBlock { Text = "Accords", Foreground = Brushes.White, FontWeight = FontWeights.Bold, FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
            var at = new CheckBox { Content = "auto transp.", Foreground = Brushes.White, FontSize = 10, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), IsChecked = autoTransposeChords, Cursor = Cursors.Hand, ToolTip = "Coché : changer un accord transpose AUSSI la mélodie. Décoché : seuls la basse et l'accompagnement sont reconstruits (pour caler les accords sous la mélodie)." };
            at.Checked += (s, e) => autoTransposeChords = true;
            at.Unchecked += (s, e) => autoTransposeChords = false;
            sp.Children.Add(at);
            border.Child = sp;
            return border;
        }
        


        void btnAddMelodicLine_Click(object sender, RoutedEventArgs e) => AddMelodicLine();

        // Reconstruct a track's full-piece note line (absolute slice positions). Advances by each riff's LENGTH, so it
        // works for both per-bar tracks (accomp/bass) and per-section tracks (the melody, one riff per section).
        System.Collections.Generic.List<Engine.RiffNote> FullLineOfTrack(string name)
        {
            var outl = new System.Collections.Generic.List<Engine.RiffNote>();
            TimelineTrack tr = null;
            foreach (var t in project.Tracks) if (t.Name == name) { tr = t; break; }
            if (tr == null) return outl;
            int barSlices = project.Arrangement != null ? project.Arrangement.BarSlices : 96;
            int pos = 0;
            foreach (var item in tr.Items)
                if (item.Module is PlayRiffModule pr)
                {
                    var r = TimelineHelper.RiffById(pr.RiffId);
                    if (r != null && r.Notes != null)
                        foreach (var n in r.Notes) outl.Add(new Engine.RiffNote(n.Note, pos + n.Start, n.Length));
                    pos += (r != null && r.LengthSlices > 0) ? r.LengthSlices : barSlices;
                }
            return outl;
        }

        // "Piste → Ajouter une ligne mélodique": compose an EXTRA independent voice over the structured piece's chord
        // trame (respecting harmony + structure) and insert it as a new track BETWEEN the melodic voices and the
        // accompaniment — i.e. add a new instrument that composes itself to fit the existing piece.
        void AddMelodicLine()
        {
            var arr = project.Arrangement;
            if (!IsComposedArrangement())
            {
                System.Windows.MessageBox.Show("Disponible uniquement sur une musique structurée (Piste → Créer structure…).", "Ajouter une ligne mélodique", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            int existing = 0;
            foreach (var t in project.Tracks) if (t.Name != null && t.Name.StartsWith("Ligne mélodique")) existing++;

            var lead = FullLineOfTrack("Mélodie");
            int seed = arr.Seed + 7919 * (existing + 1);
            var line = Engine.Timeline.ArrangementEngine.BuildExtraVoice(arr, lead, seed);
            if (line == null || line.Count == 0)
            {
                System.Windows.MessageBox.Show("Impossible de composer la ligne (arrangement incomplet).", "Ajouter une ligne mélodique", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            // spread successive added lines down an octave each so they don't pile on the same register
            if (existing > 0)
                for (int i = 0; i < line.Count; i++)
                { var n = line[i]; line[i] = new Engine.RiffNote(Math.Max(0, Math.Min(95, n.Note - 12 * existing)), n.Start, n.Length) { Bend = n.Bend }; }

            int spq = arr.SlicesPerQuarter > 0 ? arr.SlicesPerQuarter : 24;
            int inst = arr.CounterInstrument > 0 ? arr.CounterInstrument : arr.LeadInstrument;
            string name = "Ligne mélodique " + (existing + 1);
            var track = new TimelineTrack { Type = TimelineTrackType.Instrument, Instrument = inst, Name = name };
            for (int b = 0; b < arr.TotalBars; b++)
            {
                int lo = b * arr.BarSlices, hi = lo + arr.BarSlices;
                var bn = new System.Collections.Generic.List<Engine.RiffNote>();
                foreach (var n in line) if (n.Start >= lo && n.Start < hi)
                    bn.Add(new Engine.RiffNote(n.Note, n.Start - lo, Math.Max(1, Math.Min(n.Length, hi - n.Start))));
                var br = new Riff { Name = name + " m." + (b + 1), Notes = bn, LengthSlices = arr.BarSlices, SlicesPerQuarter = spq };
                RiffLibrary.Instance.Riffs.Add(br);
                track.Items.Add(new TimelineItem { Module = new PlayRiffModule { RiffId = br.Id } });
            }
            // insert AFTER the last melodic voice (Mélodie / Contre-chant / Ligne mélodique N), before pad/accomp/bass
            int at = 0;
            for (int i = 0; i < project.Tracks.Count; i++)
            {
                var nm = project.Tracks[i].Name ?? "";
                if (nm.StartsWith("Mélodie") || nm.StartsWith("Contre-chant") || nm.StartsWith("Ligne mélodique")) at = i + 1;
            }
            if (at > project.Tracks.Count) at = project.Tracks.Count;
            project.Tracks.Insert(at, track);
            scoreTracks.Add(track); viewScore = true;
            CommitRiffEditor();
            Render();
            RefreshScore();
        }

        // True when the project is a generated arrangement carrying an editable chord trame (the Orchestrateur, or the
        // legacy "Ghibli" composer). Gates the chord lane + chord/theme editing — keyed on the DATA (a chord grid is
        // present), not the composer name, so every generated piece (incl. the template engine) gets the editable trame.
        bool IsComposedArrangement() =>
            project.Arrangement != null && project.Arrangement.Chords != null && project.Arrangement.Chords.Count > 0;

        UIElement MakeChordLane(double width)
        {
            var lane = new Controls.TimelineEditor.ChordLaneControl();
            lane.Configure(width, ChordH, PxPerBeat, project.Arrangement,
                Engine.Flow.MusicTheory.TonicPc(project.Key), Engine.Score.MusicalMode.Effective(project.Key), project.PickupBeats);
            lane.ChordEdited += (idx, deg, color) => ApplyChordEdit(idx, deg, color);
            return lane;
        }

        void SelectTrack(TimelineTrack track)
        {
            if (selectedTrack == track) return; // don't rebuild (keeps header controls usable)
            CommitRiffEditor();
            var oldItem = selectedItem; var oldTrack = selectedTrack;
            selectedTrack = track;
            selectedItem = null;
            // Incremental: drop the item's highlight + editor, move the header highlight. No full Render.
            if (oldItem != null && highlighters.TryGetValue(oldItem, out var off)) off(false);
            SetHeaderSelected(oldTrack, false);
            SetHeaderSelected(track, true);
            txtEditorTitle.Text = "Éditeur";
            editorHost.Content = null;
            UpdateToolbar();
        }

        // Delete an item (leaf or repeat). Whatever follows it stays in place: the freed time (the
        // deleted item's own silence + its displayed length) is transferred onto the next item's
        // SilenceBefore. Deleting the last module of a Repeat shrinks the Repeat and pushes the silence
        // onto the track item that follows the Repeat.
        void DeleteItem(TimelineTrack track, TimelineItem item)
        {
            int idx = track.Items.IndexOf(item);
            if (idx >= 0) RemoveAt(track.Items, idx, null, track);
           

            if (selectedItem == item) selectedItem = null;
            Render();
        }

        void RemoveAt(System.Collections.Generic.IList<TimelineItem> list, int idx, TimelineItem containerRepeat, TimelineTrack track)
        {
            double comp = list[idx].SilenceBefore + TimelineHelper.DispLen(list[idx]); // freed time
            list.RemoveAt(idx);
            if (idx < list.Count)
                list[idx].SilenceBefore += comp; // next item in the same list keeps its position
            else if (containerRepeat != null)
            {
                // last module of a Repeat removed -> the Repeat shrank; keep the item after it in place
                int ri = track.Items.IndexOf(containerRepeat);
                if (ri >= 0 && ri + 1 < track.Items.Count)
                    track.Items[ri + 1].SilenceBefore += comp;
            }
        }

        Canvas MakeTrackLane(TimelineTrack track, double width, bool fillItems = true)
        {
            var canvas = new Canvas { Height = LaneH, Width = width, Background = new SolidColorBrush(LaneBgColor(track)) };
            canvas.MouseLeftButtonDown += (s, e) => SelectTrack(track); // click empty lane area selects the track
            for (int b = 0; b * PxPerBeat < width; b += 4)
            {
                var tick = new Rectangle { Width = 1, Height = LaneH, Fill = new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x29)) };
                Canvas.SetLeft(tick, b * PxPerBeat); canvas.Children.Add(tick);
            }
            if (fillItems) FillLaneItems(canvas, track);
            return canvas;
        }

        // Lanes use the standard dark background; the instrument FAMILY colour is carried by the module BOXES instead.
        static Color LaneBgColor(TimelineTrack track) => Color.FromRgb(0x16, 0x16, 0x1B);

        // The fill colour of a module box (and its collapsed mini-rectangle): chords by harmonic function, cadence blue,
        // everything else tinted by the track's instrument family. Shared by MakeLeafBox and the collapsed strip.
        System.Windows.Media.Brush ItemFillBrush(TimelineTrack track, TimelineItem item)
        {
            if (item.Module is PatternGeneratorModule cpg) return ChordFill(ChordFunction(cpg));
            if (item.Module is CadenceModule) return ChordBlueBase;
            return new SolidColorBrush(Controls.InstrumentColors.BoxFill(track.Instrument));
        }

        // A collapsed track still shows WHERE its modules are: a thin strip of colour rectangles at each module's
        // position + length (same colours as the full boxes), so the lane doesn't read as empty (issue #5 follow-up).
        Canvas MakeCollapsedLane(TimelineTrack track, double width)
        {
            var canvas = new Canvas { Height = CollapsedH, Width = width, Background = new SolidColorBrush(LaneBgColor(track)) };
            canvas.MouseLeftButtonDown += (s, e) => SelectTrack(track);
            for (int b = 0; b * PxPerBeat < width; b += 4)   // faint bar ticks, like the full lane
            {
                var tick = new Rectangle { Width = 1, Height = CollapsedH, Fill = new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x29)) };
                Canvas.SetLeft(tick, b * PxPerBeat); canvas.Children.Add(tick);
            }
            const double h = 18, top = (CollapsedH - h) / 2;   // thicker bars, small top/bottom margin
            double cursor = 0;
            foreach (var item in track.Items)
            {
                cursor += item.SilenceBefore;
                double len = TimelineHelper.DispLen(item);
                var rect = new Rectangle
                {
                    Width = Math.Max(3, len * PxPerBeat - 2), Height = h, RadiusX = 2, RadiusY = 2,
                    Fill = ItemFillBrush(track, item), ToolTip = ItemTitle(item)
                };
                Canvas.SetLeft(rect, cursor * PxPerBeat); Canvas.SetTop(rect, top);
                canvas.Children.Add(rect);
                cursor += len;
            }
            return canvas;
        }

        // The header family dot: the chords track is blue (like its boxes); everything else follows its GM family.
        static Color HeaderFamilyColor(TimelineTrack track)
            => track.Type == TimelineTrackType.Chord ? Color.FromRgb(0x44, 0x88, 0xFF) : Controls.InstrumentColors.FamilyHue(track.Instrument);

        // Add a track's module boxes onto its (already-built) lane canvas. Separate so a batched load can add
        // them incrementally without blocking the UI.
        void FillLaneItems(Canvas canvas, TimelineTrack track)
        {
            double cursor = 0;
            foreach (var item in track.Items)
            {
                cursor += item.SilenceBefore;
                AddItem(canvas, track, item, cursor);
                cursor += TimelineHelper.DispLen(item); // compact: a Repeat advances by one cycle
            }
        }

        // A leaf adds one box; a Repeat adds a translucent backdrop spanning its FULL ×Count span (the
        // real played time) and tiles its inner modules across the cycles: cycle 0 is editable (full
        // opacity), the repeated copies are dimmed ghosts. Title strip stays clear on top.
        void AddItem(Canvas canvas, TimelineTrack track, TimelineItem item, double absStart)
        {
            canvas.Children.Add(MakeLeafBox(track, item, absStart, true, 1.0, 5, LaneH - 10, nl => MoveInList(track, track.Items, item, nl / PxPerBeat)));
        }

        // Chord box blues: base (any chord) · dominant (V) slightly brighter · tonic (I) brightest — three shades of blue.
        // Chord leaf boxes = FLAT combobox blue. Base = combobox normal (#3366CC), tonic = combobox hover (#4488FF,
        // brightest), dominant in between; all share a slightly lighter blue border (ChordBorder).
        static System.Windows.Media.Brush Flat(byte r, byte g, byte b)
        {
            var br = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b));
            br.Freeze();
            return br;
        }
        static readonly System.Windows.Media.Brush ChordBlueBase = Flat(0x33, 0x66, 0xCC);
        static readonly System.Windows.Media.Brush ChordBlueDom = Flat(0x3A, 0x72, 0xDD);
        static readonly System.Windows.Media.Brush ChordBlueTonic = Flat(0x44, 0x88, 0xFF);
        static readonly System.Windows.Media.Brush ChordBorder = Flat(0x6E, 0x9C, 0xEE);
        static System.Windows.Media.Brush ChordFill(int fn) => fn == 0 ? ChordBlueTonic : (fn == 1 ? ChordBlueDom : ChordBlueBase);

        // Harmonic function of a chord relative to the current key: 0 = tonic (I), 1 = dominant (V), 2 = other.
        int ChordFunction(PatternGeneratorModule pg)
        {
            if (pg.Degree == 0) return 0;
            if (pg.Degree == 4) return 1;
            int tonic = Engine.Flow.MusicTheory.TonicPc(project.Key ?? new Engine.Score.KeySignature());
            int r = ((pg.Root % 12) + 12) % 12;
            if (r == tonic) return 0;
            if (r == (tonic + 7) % 12) return 1;
            return 2;
        }

        static readonly string[] RomanU = { "I", "II", "III", "IV", "V", "VI", "VII" };
        static readonly string[] RomanL = { "i", "ii", "iii", "iv", "v", "vi", "vii" };
        // Roman-numeral degree of a chord: its Degree (diatonic) or the nearest scale degree of its root; case + ° by quality.
        /// <summary>
        /// The chord's roman-numeral FUNCTION in the current key. The case reflects the real quality (I / i / vii°),
        /// and a chord that is not simply the diatonic chord of its degree is named by its FUNCTION rather than by its
        /// raw degree: in C major a D MAJOR chord is the dominant of the dominant → "V/V", not "II" (which would be
        /// D minor). Covers every secondary dominant (V/ii, V/iii, V/IV, V/V, V/vi), secondary leading-tone chords
        /// (vii°/x) and borrowed / chromatic roots (♭III, ♭VII, ♯IV…).
        /// </summary>
        string ChordRoman(PatternGeneratorModule pg)
        {
            var key = project.Key ?? new Engine.Score.KeySignature();
            int tonicPc = Engine.Flow.MusicTheory.TonicPc(key);
            int[] scale = Engine.Score.MusicalMode.Scale(Engine.Score.MusicalMode.Effective(key));
            int rootPc = ((pg.Root % 12) + 12) % 12;

            Engine.Flow.MusicTheory.ChordShape(pg.Quality, out bool minThird, out bool dimFifth, out bool augFifth, out _);
            int deg = DiatonicDegree(rootPc, tonicPc, scale);        // 0..6, or −1 when the root is chromatic
            string suffix = dimFifth ? "°" : augFifth ? "+" : "";

            // Secondary functions (shared with the chord editor's degree combo, so the two never disagree).
            int secDom = Engine.Flow.MusicTheory.SecondaryDominantTarget(key, rootPc, pg.Quality);
            if (secDom >= 0) return "V/" + RomanDiatonic(secDom, scale);
            int secLt = Engine.Flow.MusicTheory.SecondaryLeadingToneTarget(key, rootPc, pg.Quality);
            if (secLt >= 0) return "vii°/" + RomanDiatonic(secLt, scale);

            if (deg >= 0) return (minThird ? RomanL[deg] : RomanU[deg]) + suffix;

            // Chromatic root that isn't a secondary function: name it as an altered neighbour degree. Borrowed chords
            // are spelt FLAT by convention (♭III, ♭VI, ♭VII), except the tritone which reads ♯IV — so try the flat
            // neighbour first, and only lead with the sharp one for that tritone.
            bool preferSharp = ((rootPc - tonicPc) % 12 + 12) % 12 == 6;
            for (int pass = 0; pass < 2; pass++)
            {
                bool sharp = preferSharp ? pass == 0 : pass == 1;
                for (int d = 0; d < 7; d++)
                {
                    int dpc = ((tonicPc + scale[d]) % 12 + 12) % 12;
                    int delta = sharp ? ((rootPc - dpc) % 12 + 12) % 12    // root sits a semitone ABOVE that degree
                                      : ((dpc - rootPc) % 12 + 12) % 12;   // root sits a semitone BELOW that degree
                    if (delta == 1) return (sharp ? "♯" : "♭") + (minThird ? RomanL[d] : RomanU[d]) + suffix;
                }
            }
            return (minThird ? RomanL[0] : RomanU[0]) + suffix;
        }

        // Scale index (0..6) of a pitch-class in the key, or −1 when it isn't in the scale.
        static int DiatonicDegree(int pc, int tonicPc, int[] scale)
        {
            for (int d = 0; d < 7; d++) if (((tonicPc + scale[d]) % 12 + 12) % 12 == pc) return d;
            return -1;
        }
        // Semitones of the diatonic third stacked on a degree (4 = major, 3 = minor).
        static int DiatonicThird(int d, int[] scale) => ((scale[(d + 2) % 7] - scale[d]) % 12 + 12) % 12;
        // True when the diatonic triad on that degree is DIMINISHED.
        static bool DiatonicIsDim(int d, int[] scale) => ((scale[(d + 4) % 7] - scale[d]) % 12 + 12) % 12 == 6;
        // The degree's own roman numeral, cased (and marked °) by its DIATONIC quality — used as a secondary target.
        static string RomanDiatonic(int d, int[] scale)
        {
            bool major = DiatonicThird(d, scale) == 4;
            bool dim = ((scale[(d + 4) % 7] - scale[d]) % 12 + 12) % 12 == 6;
            return (major ? RomanU[d] : RomanL[d]) + (dim ? "°" : "");
        }

        FrameworkElement MakeLeafBox(TimelineTrack track, TimelineItem item, double startBeat, bool interactive, double opacity, double top, double height, Action<double> onDrop = null)
        {
            double len = TimelineProject.ItemLength(item, TimelineHelper.RiffById);
            double w = Math.Max(40, len * PxPerBeat - 2);
            bool sel = interactive && item == selectedItem;
            var box = new Controls.TimelineEditor.ModuleBoxControl();
            // CHORDS render in BLUE: a distinct blue for the TONIC (I), a mid blue for the DOMINANT (V), the base blue for
            // the rest (and a whole cadence). Other module types keep the default box colour.
            System.Windows.Media.Brush fill = ItemFillBrush(track, item), border = null;
            string title = ItemTitle(item), info = ItemInfo(item, len), bigLabel = null;
            if (item.Module is PatternGeneratorModule cpg)
            {
                border = ChordBorder;
                // Just the chord NAME as the top title — no "· N temps" (the ruler shows the length visually).
                title = $"{Engine.Score.KeySig.SpellPc(cpg.Root, project.Key)} {TimelineHelper.Get(PatternGenerator.QualityNames, cpg.Quality)}";
                info = "";
                bigLabel = ChordRoman(cpg);       // roman degree shown BIG in the centre, over the thumbnail
            }
            else if (item.Module is CadenceModule) { border = ChordBorder; }
            else   // riff / drum / melodic-line boxes: background + border tinted by the track's INSTRUMENT FAMILY
            {
                border = new System.Windows.Media.SolidColorBrush(Controls.InstrumentColors.BoxBorder(track.Instrument));
            }
            box.Configure(title, info, w, height, sel, interactive, opacity, fill, border);
            box.SetBigLabel(bigLabel);
            switch (item.Module) // cached mini-preview (orange = riff, red = chords, yellow = drums)
            {
                case PlayRiffModule pr: box.SetThumbnail(Controls.RiffThumbnail.Get(TimelineHelper.RiffById(pr.RiffId))); break;
                case PatternGeneratorModule pg:
                    box.SetThumbnail(pg.HasMelodic
                        ? Controls.RiffThumbnail.GetCombined(PatternGenerator.Generate(pg), Controls.RiffThumbnail.Chords, PatternGenerator.GenerateMelodic(pg, project.Key ?? new Engine.Score.KeySignature()), Controls.RiffThumbnail.Melodic)
                        : Controls.RiffThumbnail.Get(PatternGenerator.Generate(pg), Controls.RiffThumbnail.Chords));
                    break;
                case CadenceModule cm: box.SetThumbnail(Controls.RiffThumbnail.Get(PatternGenerator.GenerateCadence(cm), Controls.RiffThumbnail.Chords)); break;
                case DrumPatternModule dp: box.SetThumbnail(Controls.RiffThumbnail.GetDrums(DrumPattern.Generate(dp))); break;
                case MelodicLineModule ml:
                {
                    // Prefer the pitched line the engine derives from the chords; fall back to the raw rhythm skeleton
                    // (so the box still shows something when no chord is in effect). Blue, matching the melodic accent.
                    int spq = ml.SlicesPerQuarter > 0 ? ml.SlicesPerQuarter : 4;
                    var gen = Engine.Timeline.MelodicLineEngine.GenerateLine(ml, project, TimelineHelper.RiffById, project.Key ?? new Engine.Score.KeySignature(), startBeat);
                    if (gen == null && ml.Notes != null && ml.Notes.Count > 0)
                        gen = new Riff { Notes = new System.Collections.Generic.List<RiffNote>(ml.Notes), LengthSlices = Math.Max(1, ml.BeatsPerBar) * spq, SlicesPerQuarter = spq };
                    box.SetThumbnail(Controls.RiffThumbnail.Get(gen, Controls.RiffThumbnail.Melodic));
                    break;
                }
            }
            Canvas.SetLeft(box, startBeat * PxPerBeat);
            Canvas.SetTop(box, top);
            if (interactive)
            {
                box.Selected += () => SelectItem(track, item);
                box.Deleted += () => DeleteItem(track, item);
                box.ContextRequested += () => ShowItemContextMenu(track, item, box);
                if (onDrop != null) { box.Draggable = true; box.Dropped += onDrop; }
                highlighters[item] = box.SetSelected; // incremental selection
                leafBoxes[item] = box;                // for an in-place thumbnail refresh on riff close
                boxOwner[item] = track;               // for an in-place re-tint when the track's instrument changes
            }
            return box;
        }

        // Drag & drop within ANY item list — the track itself, or a Repeat's sub-track. `dropStart` is in
        // beats relative to that list's origin (for a Repeat, the caller subtracts the Repeat's start).
        // Overlap rules: dropped on the 2nd half of another item -> snap right after it; dropped on its 1st
        // half -> keep it at the drop point and push that item (and any following ones that still overlap)
        // to the right, cascading until a gap. The gap freed where it sat stays (next item's SilenceBefore).
        void MoveInList(TimelineTrack track, System.Collections.Generic.IList<TimelineItem> items, TimelineItem dragged, double dropStart)
        {
            int di = items.IndexOf(dragged);
            if (di < 0) return;

            double Ld = TimelineHelper.DispLen(dragged);

            // Absolute start of every current item (including the dragged one).
            var allStart = new double[items.Count];
            double cur = 0;
            for (int i = 0; i < items.Count; i++) { cur += items[i].SilenceBefore; allStart[i] = cur; cur += TimelineHelper.DispLen(items[i]); }

            // The remaining items KEEP their original absolute positions, so removing the dragged module
            // doesn't pull the ones after it leftwards — the freed gap becomes the next item's larger
            // SilenceBefore (it grows by the moved module's footprint).
            int n = items.Count - 1;
            var rest = new TimelineItem[n];
            var s = new double[n];
            var L = new double[n];
            int k = 0;
            for (int i = 0; i < items.Count; i++)
                if (i != di) { rest[k] = items[i]; s[k] = allStart[i]; L[k] = TimelineHelper.DispLen(items[i]); k++; }

            if (dropStart < 0) dropStart = 0;
            dropStart = Math.Round(dropStart); // snap to the nearest beat
            if (dropStart < 0) dropStart = 0;
            

            // The item the drop lands on (if any).
            int a = -1;
            for (int i = 0; i < n; i++) if (dropStart >= s[i] && dropStart < s[i] + L[i]) { a = i; break; }

            int ins; double dStart;
            if (a >= 0)
            {
                double mid = s[a] + L[a] / 2.0;
                if (dropStart >= mid) { ins = a + 1; dStart = s[a] + L[a]; } // 2nd half -> right after it
                else { ins = a; dStart = dropStart; }                        // 1st half -> in front of it
            }
            else
            {
                ins = n;
                for (int i = 0; i < n; i++) if (s[i] >= dropStart) { ins = i; break; }
                dStart = dropStart;
            }

            // New ordered list with absolute starts; cascade-push the items after the drop until a gap.
            int total = n + 1;
            var order = new TimelineItem[total];
            var starts = new double[total];
            int idx = 0;
            for (int i = 0; i < ins; i++) { order[idx] = rest[i]; starts[idx] = s[i]; idx++; }
            order[idx] = dragged; starts[idx] = dStart; idx++;
            double prevEnd = dStart + Ld;
            bool pushing = true;
            for (int i = ins; i < n; i++)
            {
                double st = s[i];
                if (pushing && st + 1e-6 < prevEnd) st = prevEnd; else pushing = false;
                order[idx] = rest[i]; starts[idx] = st; idx++;
                prevEnd = st + L[i];
            }

            // Convert absolute starts back to relative SilenceBefore and rebuild the track list.
            items.Clear();
            double prev = 0;
            for (int i = 0; i < total; i++)
            {
                double sb = (i == 0) ? starts[i] : starts[i] - prev;
                order[i].SilenceBefore = sb < 0 ? 0 : sb;
                items.Add(order[i]);
                prev = starts[i] + TimelineHelper.DispLen(order[i]);
            }

            // Structure changed -> select the dragged item (builds its editor if needed) then rebuild lanes.
            SelectItem(track, dragged);
            Render();
        }

        static string ItemTitle(TimelineItem item)
        {
            return item.Module?.Title ?? "?";
        }

        string ItemInfo(TimelineItem item, double len)
        {
            string beats = Math.Round(len, 2) + " temps";
            switch (item.Module)
            {
                case PatternGeneratorModule pg:
                    return $"{Engine.Score.KeySig.SpellPc(pg.Root, project.Key)} {TimelineHelper.Get(PatternGenerator.QualityNames, pg.Quality)} · {beats}";
                case CadenceModule cm:
                    return $"{TimelineHelper.Get(Engine.Flow.MusicTheory.CadenceStyles, cm.CadenceStyle)} · {cm.Chords?.Count ?? 0} accords · {beats}";
                case DrumPatternModule dp:
                    if (dp.Repeats > 1 && dp.CustomSlices != null && dp.CustomSlices.Length > 0 && dp.CustomSlicesPerQuarter > 0)
                    {
                        double unitT = Math.Round((double)dp.CustomSlices.Length / dp.CustomSlicesPerQuarter, 2);
                        return $"{TimelineHelper.Get(DrumPattern.StyleNames, dp.Style)} · {unitT} temps ×{dp.Repeats}";
                    }
                    return $"{TimelineHelper.Get(DrumPattern.StyleNames, dp.Style)} · {beats}";
                case PlayRiffModule pr:
                    { var r = TimelineHelper.RiffById(pr.RiffId); return (r != null ? r.Name : "(aucun)") + " · " + beats; }
                default:
                    return beats;
            }
        }

      
        // ---- selection + bottom editor (dedicated per type — NOT the graph node control) ----

        // Pure selection change: rebuild only the bottom editor and flip the affected borders — NO full
        // Render (which is O(all modules) and made selecting slow on big pieces). Structural callers
        // (add / delete / drag) still call Render() themselves to create/remove boxes.
        void SelectItem(TimelineTrack track, TimelineItem item)
        {
            // While the SCORE is showing, a click on a module must NOT replace it with the module editor — it only moves
            // the selection/highlight (navigation). Switch to the "Éditeur" view (toggle) to edit the selected module.
            bool scoreShown = ScoreVisible;
            // Already selected -> don't reload the editor (keeps its state).
            if (track == selectedTrack && item == selectedItem && (activeScore == null || scoreShown)) return;
            var oldItem = selectedItem; var oldTrack = selectedTrack;
            selectedTrack = track;
            selectedItem = item;

            if (!scoreShown)
            {
                CommitRiffEditor();       // persist any open inline riff edits before switching
                activeScore = null;       // a module editor will replace any score view
                OpenModuleEditor(track, item);
            }

            // Incremental highlight (the boxes already exist; a structural caller will Render() afterwards
            // if the item is brand-new and has no box yet).
            if (oldItem != null && oldItem != item && highlighters.TryGetValue(oldItem, out var off)) off(false);
            if (item != null && highlighters.TryGetValue(item, out var on)) on(true);
            if (oldTrack != track) { SetHeaderSelected(oldTrack, false); SetHeaderSelected(track, true); }
            UpdateToolbar();
        }

        // ---- IChordEditorHost: the independent chord editor delegates app-level effects back here ----
        void Controls.IChordEditorHost.Rerender() => Render();
        void Controls.IChordEditorHost.ApplyMotifToSection(PatternGeneratorModule pg)
        {
            TimelineHelper.ApplyMotifToSection(project,pg);
            Render();
        }
        string Controls.IChordEditorHost.PromptText(string title, string initial) => TimelineHelper.PromptText(title, initial);

        // Build the bottom editor for an item (per type). Extracted from SelectItem so it can also be re-shown
        // when the score is dismissed (♫ unchecked) without going through SelectItem's "already selected" guard.
        void OpenModuleEditor(TimelineTrack track, TimelineItem item)
        {
            bool selfScroll = false; // editor manages its own scrolling -> disable the outer scroll
            if (item == null)
            {
                txtEditorTitle.Text = "Éditeur";
                editorHost.Content = new TextBlock
                {
                    Text = "Sélectionne un module (riff, accord, batterie, ligne mélodique) sur la timeline pour l'éditer ici.",
                    Foreground = "#777C85".ToBrush(), FontSize = 12, TextWrapping = TextWrapping.Wrap,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(20), MaxWidth = 420, TextAlignment = TextAlignment.Center,
                };
            }
            else if (item.Module is PlayRiffModule pr) { txtEditorTitle.Text = "Éditeur — Riff"; editorHost.Content = BuildRiffEditor(track, pr); selfScroll = true; }
            else if (item.Module is PatternGeneratorModule pg) { txtEditorTitle.Text = "Éditeur — Accords"; var ce = new Controls.ChordEditorControl(); ce.Show(project, track, pg, this); editorHost.Content = ce; selfScroll = true; }
            else if (item.Module is CadenceModule cm) { txtEditorTitle.Text = "Éditeur — Cadence"; editorHost.Content = BuildCadenceEditor(track, cm); }
            else if (item.Module is DrumPatternModule dp) { txtEditorTitle.Text = "Éditeur — Batterie"; editorHost.Content = BuildDrumEditor(track, item, dp); selfScroll = true; }
            else if (item.Module is MelodicLineModule ml) { txtEditorTitle.Text = "Éditeur — Ligne mélodique"; editorHost.Content = BuildMelodicLineEditor(track, item, ml); selfScroll = true; }
            else editorHost.Content = null;

            // The riff / chord / drum editors scroll internally (options panel + grid each have their own
            // scroll viewer, with the grid's toolbar fixed). Disable the outer scroll in those modes.
            SetEditorScroll(selfScroll);
        }

        void SetHeaderSelected(TimelineTrack track, bool sel)
        {
            if (track != null && trackHeaders.TryGetValue(track, out var b)) b.Background = sel ? HeaderSelBg : null;
        }

        // Clicking a measure in the score: highlight + scroll to the riff/module covering that beat in the timeline,
        // WITHOUT opening its editor (the score stays shown). RawBeat is the measure start in real (unscaled) beats.
        void LocateRiffAtBeat(double rawBeat)
        {
            TimelineTrack t = null;
            if (selectedTrack != null && scoreTracks.Contains(selectedTrack)) t = selectedTrack;
            else foreach (var st in scoreTracks) { t = st; break; } // any scored track
            if (t == null) return;

            double cur = 0; TimelineItem found = null; double foundStart = 0;
            foreach (var it in t.Items)
            {
                cur += it.SilenceBefore;
                double len = Math.Max(1e-6, TimelineHelper.DispLen(it));
                if (rawBeat >= cur - 1e-6 && rawBeat < cur + len - 1e-6) { found = it; foundStart = cur; break; }
                cur += len;
            }
            if (found == null) return;

            var oldItem = selectedItem; var oldTrack = selectedTrack;
            selectedTrack = t; selectedItem = found; // remember it (editor not opened; activeScore stays set)
            if (oldItem != null && oldItem != found && highlighters.TryGetValue(oldItem, out var off)) off(false);
            if (highlighters.TryGetValue(found, out var on)) on(true);
            if (oldTrack != t) { SetHeaderSelected(oldTrack, false); SetHeaderSelected(t, true); }
            UpdateToolbar();

            double x = foundStart * PxPerBeat;
            if (x < laneScroll.HorizontalOffset || x > laneScroll.HorizontalOffset + laneScroll.ViewportWidth - 40)
                laneScroll.ScrollToHorizontalOffset(Math.Max(0, x - laneScroll.ViewportWidth * 0.3));
        }

        // ================= SCORE NOTE-INPUT EDITOR =================
        // Wrap the ScoreView with a top toolbar (the ✎ Éditer toggle + octave/duration/dot in edit mode).
        FrameworkElement ScoreContainer(Controls.Score.ScoreView view)
        {
            var dock = new DockPanel { LastChildFill = true };
            var bar = BuildScoreToolbar();
            DockPanel.SetDock(bar, Dock.Top);
            dock.Children.Add(bar);
            dock.Children.Add(view);
            return dock;
        }

        UIElement BuildScoreToolbar()
        {
            var toolStyle = TryFindResource("toolToggleBlue") as Style ?? TryFindResource("toolToggle") as Style;   // themed dark toggle (accent blue when active)
            var bar = new WrapPanel { Margin = new Thickness(2, 0, 2, 4) };
            var tog = new System.Windows.Controls.Primitives.ToggleButton { Style = toolStyle, Content = "✎ Éditer", IsChecked = scoreEditMode, Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(0, 0, 8, 0), Cursor = Cursors.Hand };
            tog.Checked += (s, e) => { scoreEditMode = true; HookScoreKeys(true); RefreshScore(); };
            tog.Unchecked += (s, e) => { scoreEditMode = false; HookScoreKeys(false); selNoteMidi = -1; RefreshScore(); Render(); }; // Render → refresh riff thumbnails edited on the staff
            bar.Children.Add(tog);
            if (scoreEditMode)
            {
                bar.Children.Add(ScoreLbl("Octave"));
                var oct = new TextBox { Width = 38, Text = editOctave.ToString(), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 8, 0) };
                oct.LostFocus += (s, e) => { if (int.TryParse(oct.Text, out int v)) editOctave = Math.Max(0, Math.Min(9, v)); };
                bar.Children.Add(oct);
                bar.Children.Add(ScoreLbl("Durée"));
                var durNames = new[] { "Double-croche", "Croche", "Noire", "Blanche", "Ronde" };
                var bases = DurBases();
                for (int di = 0; di < 5; di++)
                {
                    int ii = di;
                    var tb = new System.Windows.Controls.Primitives.ToggleButton
                    {
                        Style = toolStyle, Content = NoteIcon(di), IsChecked = editDurIdx == di,
                        ToolTip = durNames[di] + $" ({bases[di]} slices" + (project != null && project.TimeSigDen == 8 ? ", ternaire" : "") + ")",
                        Width = 30, Height = 28, Padding = new Thickness(0), Margin = new Thickness(0, 0, 2, 0), Cursor = Cursors.Hand,
                        VerticalContentAlignment = VerticalAlignment.Center, HorizontalContentAlignment = HorizontalAlignment.Center,
                    };
                    tb.Checked += (s, e) => { editDurIdx = ii; RefreshScore(); };   // exclusive (rebuild rechecks only the active one)
                    bar.Children.Add(tb);
                }
                var dot = new System.Windows.Controls.Primitives.ToggleButton { Style = toolStyle, Content = NoteIcon(0, dotOnly: true), IsChecked = editDotted, ToolTip = "Note pointée", Width = 26, Height = 28, Padding = new Thickness(0), Margin = new Thickness(4, 0, 8, 0), Cursor = Cursors.Hand };
                dot.Checked += (s, e) => editDotted = true; dot.Unchecked += (s, e) => editDotted = false;
                bar.Children.Add(dot);
                bar.Children.Add(ScoreLbl("Voix"));
                for (int v = 0; v < 5; v++)
                {
                    int vv = v;
                    var tb = new System.Windows.Controls.Primitives.ToggleButton { Style = toolStyle, Content = (v + 1).ToString(), IsChecked = editVoice == v, Width = 26, Height = 28, Padding = new Thickness(0), Margin = new Thickness(0, 0, 2, 0), Cursor = Cursors.Hand };
                    tb.Checked += (s, e) => { editVoice = vv; RefreshScore(); };   // exclusive: the rebuild rechecks only the active one
                    bar.Children.Add(tb);
                }
                bar.Children.Add(new TextBlock { Text = "C D E F G A B · Espace=silence · Maj+lettre=empiler · ↑↓ ½t · Ctrl+↑↓ 8ve · Suppr · ←→=sélection · 1-5=durée", Foreground = "#888888".ToBrush(), FontSize = 10, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 440, Margin = new Thickness(8, 0, 0, 0) });
            }
            return bar;
        }

        static readonly Brush NoteInk = MakeFrozen(Color.FromRgb(0xDD, 0xDD, 0xDD));
        static Brush MakeFrozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

        /// <summary>A small drawn note icon for the duration toggles (reliable across fonts), keyed by note-VALUE index
        /// (0=double-croche,1=croche,2=noire,3=blanche,4=ronde) so it is meter-independent: a notehead (filled for
        /// ≤ noire, hollow for blanche/ronde), a stem, and 1–2 flags for croche/double-croche. <paramref name="dotOnly"/>
        /// draws just an augmentation dot (for the "pointée" toggle).</summary>
        static UIElement NoteIcon(int idx, bool dotOnly = false)
        {
            var c = new Canvas { Width = 22, Height = 26, Background = Brushes.Transparent };
            if (dotOnly)
            {
                c.Children.Add(new System.Windows.Shapes.Ellipse { Width = 6, Height = 6, Fill = NoteInk });
                Canvas.SetLeft(c.Children[0], 8); Canvas.SetTop(c.Children[0], 10);
                return c;
            }
            bool hollow = idx >= 3;                   // blanche, ronde
            bool hasStem = idx < 4;                   // tout sauf la ronde
            int flags = idx == 1 ? 1 : idx == 0 ? 2 : 0;
            double hx = 6, hy = 18;                   // centre de la tête
            var head = new System.Windows.Shapes.Ellipse { Width = 10, Height = 7.4, Fill = hollow ? Brushes.Transparent : NoteInk, Stroke = NoteInk, StrokeThickness = 1.5, RenderTransform = new RotateTransform(-22, 5, 3.7) };
            Canvas.SetLeft(head, hx - 5); Canvas.SetTop(head, hy - 3.7);
            c.Children.Add(head);
            if (hasStem)
            {
                double sx = hx + 4.4;
                c.Children.Add(new System.Windows.Shapes.Line { X1 = sx, Y1 = hy - 2, X2 = sx, Y2 = 4, Stroke = NoteInk, StrokeThickness = 1.5 });
                for (int f = 0; f < flags; f++)
                {
                    double fy = 4 + f * 5;
                    c.Children.Add(new System.Windows.Shapes.Path { Fill = NoteInk, Stroke = NoteInk, StrokeThickness = 1.0, Data = Geometry.Parse(System.FormattableString.Invariant($"M {sx},{fy} C {sx + 7},{fy + 2} {sx + 7},{fy + 7} {sx + 2},{fy + 9} L {sx},{fy + 6} Z")) });
                }
            }
            return c;
        }

        static TextBlock ScoreLbl(string t) => new TextBlock { Text = t + " :", Foreground = "#AAAAAA".ToBrush(), FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 3, 0) };

        FrameworkElement scoreKeyHost;
        void HookScoreKeys(bool on)
        {
            // Attach to the WINDOW (stable) — each edit replaces editorHost.Content, which would otherwise blow away
            // keyboard focus and stop subsequent keystrokes until the user clicks back in.
            if (on && !scoreKeysHooked) { scoreKeyHost = (FrameworkElement)Window.GetWindow(this) ?? this; scoreKeyHost.PreviewKeyDown += ScoreEdit_KeyDown; scoreKeysHooked = true; }
            else if (!on && scoreKeysHooked) { (scoreKeyHost ?? this).PreviewKeyDown -= ScoreEdit_KeyDown; scoreKeysHooked = false; scoreKeyHost = null; }
        }

        static int LetterOf(Key k)
        {
            switch (k) { case Key.C: return 0; case Key.D: return 1; case Key.E: return 2; case Key.F: return 3; case Key.G: return 4; case Key.A: return 5; case Key.B: return 6; default: return -1; }
        }

        void ScoreEdit_KeyDown(object sender, KeyEventArgs e)
        {
            if (!scoreEditMode || activeScore == null || !ReferenceEquals(editorHost.Content, scoreContainer)) return;
            if (Keyboard.FocusedElement is TextBox) return;   // let the toolbar's octave field receive typing normally
            // CRITICAL: never let an exception escape a window-level PreviewKeyDown handler — WPF would corrupt keyboard
            // input GLOBALLY (keys stop working everywhere, even in other projects, until restart).
            try { ScoreEditKeyCore(e); }
            catch (Exception ex) { txtEditorTitle.Text = "Édition partition — erreur : " + ex.Message; e.Handled = true; }
        }

        void ScoreEditKeyCore(KeyEventArgs e)
        {
            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0, shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
            int letter = LetterOf(e.Key);
            if (letter >= 0) { PlaceScoreNote(letter, false, shift); e.Handled = true; return; }
            switch (e.Key)
            {
                case Key.Space: PlaceScoreNote(-1, true); e.Handled = true; break;
                case Key.OemPeriod: case Key.Decimal: editDotted = !editDotted; RefreshScore(); e.Handled = true; break;
                case Key.Up: TransposeSel(ctrl ? 12 : 1); e.Handled = true; break;
                case Key.Down: TransposeSel(ctrl ? -12 : -1); e.Handled = true; break;
                case Key.Delete: case Key.Back: DeleteSel(); e.Handled = true; break;
                case Key.Left: MoveCursor(-1); e.Handled = true; break;
                case Key.Right: MoveCursor(1); e.Handled = true; break;
                case Key.D1: case Key.NumPad1: editDurIdx = 0; RefreshScore(); e.Handled = true; break;
                case Key.D2: case Key.NumPad2: editDurIdx = 1; RefreshScore(); e.Handled = true; break;
                case Key.D3: case Key.NumPad3: editDurIdx = 2; RefreshScore(); e.Handled = true; break;
                case Key.D4: case Key.NumPad4: editDurIdx = 3; RefreshScore(); e.Handled = true; break;
                case Key.D5: case Key.NumPad5: editDurIdx = 4; RefreshScore(); e.Handled = true; break;
                case Key.OemPlus: case Key.Add: editOctave = Math.Min(9, editOctave + 1); RefreshScore(); e.Handled = true; break;
                case Key.OemMinus: case Key.Subtract: editOctave = Math.Max(0, editOctave - 1); RefreshScore(); e.Handled = true; break;
            }
        }

        // The track being edited = the selected scored track, else the first scored one.
        TimelineTrack EditScoreTrack()
        {
            if (selectedTrack != null && scoreTracks.Contains(selectedTrack)) return selectedTrack;
            foreach (var st in scoreTracks) return st;
            return null;
        }

        // Map a raw beat to the Riff under it (+ slice offset, spq, transpose). False for non-Riff (read-only) measures.
        bool EditableRiffAt(double rawBeat, out Riff riff, out int off, out int spq, out int transpose)
        {
            riff = null; off = 0; spq = 24; transpose = 0;
            var t = EditScoreTrack(); if (t?.Items == null) return false;
            double cur = 0;
            foreach (var it in t.Items)
            {
                cur += it.SilenceBefore;
                double len = Math.Max(1e-6, TimelineHelper.DispLen(it));
                if (rawBeat >= cur - 1e-6 && rawBeat < cur + len - 1e-6)
                {
                    if (!(it.Module is PlayRiffModule pr)) return false;   // Accords/Batterie/Cadence… = read-only
                    riff = TimelineHelper.RiffById(pr.RiffId); if (riff == null) return false;
                    spq = riff.SlicesPerQuarter > 0 ? riff.SlicesPerQuarter : 24;
                    off = Math.Max(0, (int)Math.Round((rawBeat - cur) * spq));
                    Engine.Score.ScoreClef.ForTrack(t.Instrument, t.Type == TimelineTrackType.Drum, out _, out transpose, out _);
                    return true;
                }
                cur += len;
            }
            return false;
        }

        void ScoreEditClickAt(double rawBeat)
        {
            double unit = EditDur / 24.0;                    // beats per current duration (quarter = 1 beat)
            editRawBeat = Math.Max(0, Math.Round(rawBeat / unit) * unit);
            selNoteMidi = -1;
            UpdateEditCursor();
        }

        void ScoreEditSelectNote(double rawBeat, int midi) { selNoteBeat = rawBeat; selNoteMidi = midi; editRawBeat = rawBeat; PlayNotePreview(midi); UpdateEditCursor(); }

        // Mouse note entry: a click on the staff places a note of the CURRENT duration/voice at the clicked beat (snapped
        // to the duration grid) and pitch (computed by ScoreView from the line/space). Per-voice overwrite, then advance.
        void ScoreMousePlace(double rawBeat, int concert)
        {
            double unit = EditDur / 24.0;
            editRawBeat = Math.Max(0, Math.Round(rawBeat / unit) * unit);
            TimelineHelper.EnsureRiffCovers(project,EditScoreTrack(), editRawBeat);
            if (!EditableRiffAt(editRawBeat, out Riff riff, out int off, out int spq, out int transpose)) { UpdateEditCursor(); return; }
            int durQ = editDotted ? EditDur * 3 / 2 : EditDur;
            int dur = Math.Max(1, durQ * spq / 24);
            riff.Notes.RemoveAll(n => n.Voice == editVoice && n.Start >= off && n.Start < off + dur); // per-voice overwrite
            int note = Math.Max(0, Math.Min(95, concert - 12));
            riff.Notes.Add(new RiffNote(note, off, dur) { Voice = editVoice });
            selNoteBeat = editRawBeat; selNoteMidi = concert; lastVoiceMidi[editVoice] = concert;
            lastEnteredBeat = editRawBeat; lastEnteredDur = dur;
            PlayNotePreview(concert);
            editRawBeat += (double)durQ / 24.0;   // advance so successive notes flow (a click still repositions freely)
            RefreshScore();
        }

        void UpdateEditCursor()
        {
            if (activeScore == null) return;
            bool ok = EditableRiffAt(editRawBeat < 0 ? 0 : editRawBeat, out _, out _, out _, out _);
            activeScore.SetEditCursor(editRawBeat < 0 ? 0 : editRawBeat, ok);
            activeScore.SetSelectedNote(selNoteBeat, selNoteMidi);
        }

        // Concert MIDI of a note letter at a given octave, taking the accidental the KEY gives that letter (F→fa♯ in D
        // major): find the key's scale note whose letter matches, and shift the natural to that pitch-class.
        int LetterToKeyMidi(int letter, int octave)
        {
            var key = project.Key ?? new Engine.Score.KeySignature();
            int[] scale = Engine.Score.MusicalMode.Scale(Engine.Score.MusicalMode.Effective(key));
            int tonicPc = Engine.Flow.MusicTheory.TonicPc(key), tonicLetter = key.TonicLetter, naturalPc = LetterPc[letter];
            int keyPc = naturalPc;
            for (int d = 0; d < 7 && d < scale.Length; d++)
                if (((tonicLetter + d) % 7) == letter) { keyPc = (((tonicPc + scale[d]) % 12) + 12) % 12; break; }
            int diff = ((((keyPc - naturalPc) + 6) % 12) + 12) % 12 - 6;   // signed nearest (−1/0/+1, rarely ±2)
            return 12 * (octave + 1) + naturalPc + diff;
        }

        // The letter's pitch, at the octave NEAREST the voice's previous note (the toolbar octave only seeds the 1st note).
        int LetterConcertNearest(int letter)
        {
            int m = LetterToKeyMidi(letter, editOctave);
            int prev = lastVoiceMidi[editVoice];
            if (prev >= 0) { while (m - prev > 6) m -= 12; while (prev - m > 6) m += 12; }
            return Math.Max(0, Math.Min(127, m));
        }

        // Place a note / rest at the edit cursor on the ACTIVE voice (overwrite is per-voice → polyphony), then advance.
        // stack (Shift+lettre) = add at the LAST entered note's position/duration on the current voice, no overwrite/advance.
        void PlaceScoreNote(int letter, bool rest, bool stack = false)
        {
            if (editRawBeat < 0) editRawBeat = 0;
            double atBeat = stack && lastEnteredBeat >= 0 ? lastEnteredBeat : editRawBeat;
            // editing past the end grows the last riff by whole measures
            TimelineHelper.EnsureRiffCovers(project, EditScoreTrack(), atBeat);
            if (!EditableRiffAt(atBeat, out Riff riff, out int off, out int spq, out int transpose)) return;
            int durQ = editDotted ? EditDur * 3 / 2 : EditDur;                      // 24-spq slices
            int dur = stack && lastEnteredDur > 0 ? lastEnteredDur : Math.Max(1, durQ * spq / 24); // riff slices
            if (!stack) riff.Notes.RemoveAll(n => n.Voice == editVoice && n.Start >= off && n.Start < off + dur); // per-voice overwrite
            if (!rest)
            {
                int concert = LetterConcertNearest(letter);
                int note = Math.Max(0, Math.Min(95, concert - 12));
                riff.Notes.Add(new RiffNote(note, off, dur) { Voice = editVoice });
                selNoteBeat = atBeat; selNoteMidi = concert; lastVoiceMidi[editVoice] = concert;
                if (!stack) { lastEnteredBeat = atBeat; lastEnteredDur = dur; }
                PlayNotePreview(concert);
            }
            if (!stack) { editRawBeat += (double)durQ / 24.0; if (rest) selNoteMidi = -1; }
            RefreshScore();
        }

        // Select the note at/nearest the edit cursor on the active voice (used by ←/→). Plays it; clears if none.
        void SelectNoteAtCursor()
        {
            selNoteMidi = -1;
            if (EditableRiffAt(editRawBeat, out Riff riff, out int off, out int spq, out int transpose))
            {
                int best = -1, bestd = spq / 2 + 1;
                for (int i = 0; i < riff.Notes.Count; i++) { var n = riff.Notes[i]; if (n.Voice != editVoice) continue; int d = Math.Abs(n.Start - off); if (d < bestd) { bestd = d; best = i; } }
                if (best >= 0) { selNoteBeat = editRawBeat; selNoteMidi = riff.Notes[best].Note + 12; PlayNotePreview(selNoteMidi); }
            }
            UpdateEditCursor();
        }

        

        // ---- one-shot audio feedback for the score editor (a note auditioned on the track's instrument) ----
        NAudio.Wave.WaveOutEvent scorePreviewOut;
        System.Windows.Threading.DispatcherTimer scorePreviewTimer;
        void PlayNotePreview(int concertMidi)
        {
            try
            {
                StopNotePreview();
                var t = EditScoreTrack(); if (t == null) return;
                int note = Math.Max(0, Math.Min(95, concertMidi - 12));
                // ONE long note so the looping provider doesn't re-attack within the preview window (no "cut & replay");
                // the timer stops it — a single natural attack, like playback.
                var riff = new Riff { LengthSlices = 480, SlicesPerQuarter = 24 };
                riff.Notes.Add(new RiffNote(note, 0, 480));
                var ctx = new Engine.Flow.FlowContext {Bpm = 120 };
                var lp = new Engine.Flow.LoopingRiffProvider(() => riff, ctx);
                scorePreviewOut = new NAudio.Wave.WaveOutEvent { DesiredLatency = 100 };
                scorePreviewOut.Init(lp); scorePreviewOut.Play();
                scorePreviewTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
                scorePreviewTimer.Tick += (s, e) => StopNotePreview();
                scorePreviewTimer.Start();
            }
            catch { StopNotePreview(); }
        }
        void StopNotePreview()
        {
            if (scorePreviewTimer != null) { scorePreviewTimer.Stop(); scorePreviewTimer = null; }
            if (scorePreviewOut != null) { try { scorePreviewOut.Stop(); scorePreviewOut.Dispose(); } catch { } scorePreviewOut = null; }
        }

        void TransposeSel(int delta)
        {
            if (selNoteMidi < 0) return;
            if (!EditableRiffAt(selNoteBeat, out Riff riff, out int off, out int spq, out int transpose)) return;
            int oldNote = selNoteMidi - 12, newNote = Math.Max(0, Math.Min(95, oldNote + delta));
            int best = -1, bestd = int.MaxValue;
            for (int i = 0; i < riff.Notes.Count; i++) if (riff.Notes[i].Note == oldNote) { int d = Math.Abs(riff.Notes[i].Start - off); if (d < bestd) { bestd = d; best = i; } }
            if (best < 0) return;
            var n = riff.Notes[best]; riff.Notes[best] = new RiffNote(newNote, n.Start, n.Length) { Bend = n.Bend };
            selNoteMidi = newNote + 12;
            PlayNotePreview(selNoteMidi);
            RefreshScore();
        }

        void DeleteSel()
        {
            if (!EditableRiffAt(editRawBeat, out Riff riff, out int off, out int spq, out int transpose)) return;
            // A note AT the cursor (active voice) → delete it.
            int at = -1, atd = spq / 2 + 1;
            for (int i = 0; i < riff.Notes.Count; i++) { var n = riff.Notes[i]; if (n.Voice != editVoice) continue; int d = Math.Abs(n.Start - off); if (d < atd) { atd = d; at = i; } }
            if (at >= 0) { riff.Notes.RemoveAt(at); selNoteMidi = -1; RefreshScore(); return; }
            // Cursor on a REST → delete the PREVIOUS note on this voice and move the cursor onto it (backspace).
            int prev = -1;
            for (int i = 0; i < riff.Notes.Count; i++) { var n = riff.Notes[i]; if (n.Voice != editVoice || n.Start >= off) continue; if (prev < 0 || n.Start > riff.Notes[prev].Start) prev = i; }
            if (prev < 0) return;
            editRawBeat = Math.Max(0, editRawBeat - (off - riff.Notes[prev].Start) / (double)spq);
            riff.Notes.RemoveAt(prev); selNoteMidi = -1;
            RefreshScore();
        }

        void MoveCursor(int dir)
        {
            double unit = EditDur / 24.0;
            editRawBeat = Math.Max(0, (editRawBeat < 0 ? 0 : editRawBeat) + dir * unit);
            SelectNoteAtCursor();   // ←/→ selects (and auditions) the note under the new cursor position
        }

        // selfScroll == true -> outer scroll fully disabled (constrains height, no bars) so the editor's
        // own scroll viewers take over; otherwise the small editors (Repeat) scroll normally.
        void SetEditorScroll(bool selfScroll)
        {
            var vis = selfScroll ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
            editorScroll.VerticalScrollBarVisibility = vis;
            editorScroll.HorizontalScrollBarVisibility = vis;
        }

        void CommitRiffEditor()
        {
            // The riff grid persists live (GridChanged), so there's nothing to commit -- just stop preview.
            if (activeRiffGrid != null) { try { activeRiffGrid.StopPreview(); } catch { } activeRiffGrid = null; }
            if (!riffDirty) return;
            riffDirty = false;
            RefreshEditedRiffBox();
        }

        // Refresh the edited riff's timeline box WITHOUT leaving the editor. Length unchanged -> just its thumbnail;
        // length changed -> re-lay-out only this riff's track (its following modules shift) and re-baseline.
        void RefreshEditedRiffBox()
        {
            if (riffEditItem == null) { Render(); return; }
            if (Math.Abs(TimelineHelper.DispLen(riffEditItem) - riffOpenLen) < 1e-6)
            {
                if (leafBoxes.TryGetValue(riffEditItem, out var box))
                {
                    if (riffEditItem.Module is PlayRiffModule pr) box.SetThumbnail(Controls.RiffThumbnail.Get(TimelineHelper.RiffById(pr.RiffId)));
                    else if (riffEditItem.Module is DrumPatternModule dp) box.SetThumbnail(Controls.RiffThumbnail.GetDrums(DrumPattern.Generate(dp)));
                }
            }
            else { RefreshTrackLane(riffEditTrack ?? selectedTrack); riffOpenLen = TimelineHelper.DispLen(riffEditItem); }
        }

        // Rebuild a single track's header + lane stack in place (re-positions its modules) and widen the
        // ruler if the total grew — far cheaper than a full Render for a big piece.
        void RefreshTrackLane(TimelineTrack track)
        {
            // The chord lane shifts the lane indexing (tempo + chord before the tracks) — full Render is simplest & correct.
            if (IsComposedArrangement()) { Render(); return; }
            int ti = track == null ? -1 : project.Tracks.IndexOf(track);
            int idx = ti + 1; // index 0 = the tempo lane/header
            if (ti < 0 || idx >= lanePanel.Children.Count || idx >= headerPanel.Children.Count) { Render(); return; }

            TimelineProject.ResolveLoops(project, TimelineHelper.RiffById);
            double laneWidth = TotalBeats() * PxPerBeat;
            measureRuler.Configure(laneWidth, 20, PxPerBeat, TimelineHelper.RulerBeatsPerBar(project), project.PickupBeats);
            if (startCanvas != null) startCanvas.Width = laneWidth;

            double rh = TrackRowH(track);
            headerPanel.Children.RemoveAt(idx);
            headerPanel.Children.Insert(idx, MakeHeader(null, rh, track));
            lanePanel.Children.RemoveAt(idx);
            lanePanel.Children.Insert(idx, LaneRow(MakeTrackRow(track, laneWidth), rh));

            // The total may have grown (this riff became the longest) -> stretch the OTHER lanes' width too,
            // cheaply (O(tracks)): their modules are unchanged, only the background/volume span widens. Each lane
            // row is wrapped in a divider Border, so unwrap to reach the tempo control / volume+lane stack.
            for (int i = 0; i < lanePanel.Children.Count; i++)
            {
                if (i == idx) continue;
                var inner = (lanePanel.Children[i] as Border)?.Child ?? lanePanel.Children[i];
                if (i == 0) { if (inner is Controls.TimelineEditor.TempoLaneControl tl) tl.Configure(laneWidth, TempoH, PxPerBeat, project.Tempo); continue; }
                if (inner is StackPanel st && st.Children.Count >= 2 && i - 1 < project.Tracks.Count)
                {
                    if (st.Children[0] is Controls.TimelineEditor.VolumeLaneControl vl) vl.Configure(project.Tracks[i - 1], PxPerBeat, VolLaneH, laneWidth);
                    if (st.Children[1] is System.Windows.Controls.Canvas cv) cv.Width = laneWidth;
                }
            }

            if (player == null) MoveCursor(startBeat);
        }

        // ----- small editor builders -----
        TextBlock EdLabel(string t) => new TextBlock { Text = t, Foreground = "#AAAAAA".ToBrush(), FontSize = 11, Margin = new Thickness(0, 4, 0, 1) };

        ComboBox ParamCombo(string[] items, int sel, Action<int> set, Action changed)
        {
            var c = new ComboBox { Width = 180, HorizontalAlignment = HorizontalAlignment.Left, ItemsSource = items, SelectedIndex = sel };
            c.SelectionChanged += (s, e) => { if (c.SelectedIndex >= 0) { set(c.SelectedIndex); changed(); } };
            return c;
        }

        TextBox ParamNum(int val, Action<int> set, Action changed)
        {
            var t = new TextBox { Width = 60, HorizontalAlignment = HorizontalAlignment.Left, Text = val.ToString() };
            t.LostFocus += (s, e) => { if (int.TryParse(t.Text, out int v)) { set(v); changed(); } };
            return t;
        }

        UIElement BuildRepeatEditor(RepeatGroup g)
        {
            var sp = new StackPanel { Margin = new Thickness(2) };
            sp.Children.Add(EdLabel("Répétitions :"));
            sp.Children.Add(ParamNum(g.Count, v => { if (v > 0) g.Count = v; }, Render));
            var loop = new CheckBox { Content = "Boucler jusqu'à la fin", Foreground = Brushes.White, FontSize = 11, Margin = new Thickness(0, 6, 0, 0), IsChecked = g.Loop };
            loop.Checked += (s, e) => { g.Loop = true; Render(); };
            loop.Unchecked += (s, e) => { g.Loop = false; Render(); };
            sp.Children.Add(loop);
            sp.Children.Add(new TextBlock { Text = "Repeat sélectionné → +Riff / +Accords / +Batterie ajoute DEDANS.", Foreground = "#888888".ToBrush(), FontSize = 10, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) });
            return sp;
        }

        // Riff: combo + Nouveau + the inline piano-roll riff editor (RiffGridControl).
        // Opening a module with no riff (or clicking "Nouveau") starts a DRAFT riff: editable right away
        // but NOT added to the library until the user actually paints a note (GridChanged with content).
        // An untouched draft is simply dropped when you leave -> no empty riffs pile up.
        UIElement BuildRiffEditor(TimelineTrack track, PlayRiffModule pr)
        {
            var editedItem = selectedItem; // the TimelineItem wrapping this module (for the on-leave refresh)
            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var rg = new Controls.RiffGridControl { MeterDen = project.TimeSigDen }; // 1/6-beat snap in compound x/8
            activeRiffGrid = rg;
            Grid.SetRow(rg, 1); grid.Children.Add(rg);

            var top = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
            top.Children.Add(new TextBlock { Text = "Riff :", Foreground = "#AAAAAA".ToBrush(), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 5, 0) });
            var combo = new ComboBox { Width = 200, ItemsSource = RiffLibrary.Instance.Riffs, DisplayMemberPath = "Name", SelectedValuePath = "Id" };
            var neu = new Button { Content = "Nouveau", Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(8, 2, 8, 2), Cursor = Cursors.Hand };
            top.Children.Add(combo); top.Children.Add(neu);
            // "Appliquer le thème": treat THIS riff as the theme → copy it into the theme riff and regenerate the derived
            // sections (ré-expo / développement / conclusion) + the counter from the chord trame. Only for composed arrangements.
            var applyTheme = new Button { Content = "Appliquer le thème", Margin = new Thickness(14, 0, 0, 0), Padding = new Thickness(8, 2, 8, 2), Cursor = Cursors.Hand, ToolTip = "Reporter ce riff comme thème : ré-exposition / développement / conclusion + contre-chant régénérés sur les accords de chaque mesure." };
            applyTheme.Visibility = (IsComposedArrangement()) ? Visibility.Visible : Visibility.Collapsed;
            applyTheme.Click += (s, e) => ApplyThemeFromRiff(pr);
            top.Children.Add(applyTheme);
            // "Ne pas écraser": lock THIS section's riff so "Appliquer le thème" leaves it untouched.
            var sec0 = TimelineHelper.SectionForRiff(project, pr.RiffId);
            var protect = new CheckBox { Content = "Ne pas écraser", Foreground = Brushes.White, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0), Cursor = Cursors.Hand, ToolTip = "Protège ce riff de la régénération par « Appliquer le thème »." };
            protect.Visibility = (sec0 != null) ? Visibility.Visible : Visibility.Collapsed;
            protect.IsChecked = sec0 != null && sec0.Protected;
            protect.Checked += (s, e) => { var sc = TimelineHelper.SectionForRiff(project, pr.RiffId); if (sc != null) sc.Protected = true; };
            protect.Unchecked += (s, e) => { var sc = TimelineHelper.SectionForRiff(project, pr.RiffId); if (sc != null) sc.Protected = false; };
            top.Children.Add(protect);
            var aiBtn = new Button { Content = "🤖 Générer (IA)…", Margin = new Thickness(14, 0, 0, 0), Padding = new Thickness(8, 2, 8, 2), Cursor = Cursors.Hand, Style = (Style)FindResource("okButton"), ToolTip = "Décris une intention ; l'IA écrit la mélodie du riff sur les accords présents (ou propose les accords si la zone est vide)." };
            aiBtn.Click += (s, e) => GenerateRiffWithAi(track, pr, editedItem, rg);
            top.Children.Add(aiBtn);
            Grid.SetRow(top, 0); grid.Children.Add(top);

            Riff draft = null; // non-null while the shown riff is an uncommitted draft (not in the library)

            // Show a riff in the editor. asDraft = a brand-new one not yet in the library. rerender = the
            // module's riff actually changed (combo/Nouveau) so its timeline box must refresh; on the initial
            // open nothing changed, so we skip the (O(all modules)) Render to keep selection snappy.
            Action<Riff, bool, bool> show = (r, asDraft, rerender) =>
            {
                draft = asDraft ? r : null;
                pr.RiffId = r.Id;
                rg.Configure(r, InstrumentCatalog.GetPreset(track.Instrument), track.Instrument);
                rg.SetBacking(TimelineHelper.BackingForRiff(project,r.Id), BackingInstrument()); // play the chord line under this riff (clamped), when it's a composed section
                combo.SelectedValue = asDraft ? null : (object)r.Id; // blank while it's a draft
                // Baseline for the on-leave refresh: which item, its track, and its length before editing.
                riffEditItem = editedItem; riffEditTrack = track; riffOpenLen = TimelineHelper.DispLen(editedItem); riffDirty = false;
                if (rerender) Render();
            };

            rg.GridChanged += () =>
            {
                // The draft becomes a real library riff the moment it gets content.
                if (draft != null && rg.CurrentNotes().Count > 0)
                {
                    RiffLibrary.Instance.Riffs.Add(draft);
                    combo.SelectedValue = draft.Id; // now listed -> show it selected
                    draft = null;
                }
                var rr = TimelineHelper.RiffById(pr.RiffId);
                if (rr != null) { rr.Notes = rg.CurrentNotes(); rr.LengthSlices = rg.LengthSlices; rr.SlicesPerQuarter = rg.Spb; }
                riffDirty = true; // persisted live; the timeline box is refreshed when we leave the riff
            };

            // A live MIDI/audio take just finished (length settled) -> refresh the module box/thumbnail right away.
            rg.RecordingStopped += () => { RefreshEditedRiffBox(); riffDirty = false; };

            // Effacer -> refresh the module box/thumbnail immediately (don't wait until we leave the editor).
            rg.Cleared += () => { RefreshEditedRiffBox(); riffDirty = false; };

            combo.SelectionChanged += (s, e) =>
            {
                if (combo.SelectedValue is Guid id && id != pr.RiffId)
                {
                    var r = TimelineHelper.RiffById(id);
                    if (r != null) show(r, false, true);
                }
            };
            neu.Click += (s, e) => show(new Riff { Name = "Riff " + (RiffLibrary.Instance.Riffs.Count + 1) }, true, true);

            // Initial content: the module's existing riff, or auto-"Nouveau" (a draft). No Render on open.
            var cur = TimelineHelper.RiffById(pr.RiffId);
            if (cur != null) show(cur, false, false);
            else show(new Riff { Name = "Riff " + (RiffLibrary.Instance.Riffs.Count + 1) }, true, false);
            return grid;
        }

        

        // The WaveFunction of the accompaniment track (for the riff-editor chord-line backing).
        Preset BackingInstrument()
        {
            foreach (var t in project.Tracks) if (t.Name == "Accompagnement") return InstrumentCatalog.GetPreset(t.Instrument);
            return InstrumentCatalog.GetPreset(0);
        }

        

        void ApplyThemeFromRiff(PlayRiffModule pr)
        {
            var arr = project.Arrangement;
            if (arr == null || arr.Sections == null || arr.Sections.Count == 0)
            {
                System.Windows.MessageBox.Show("Aucun arrangement à régénérer (composez d'abord une structure).", "Appliquer le thème", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var src = TimelineHelper.RiffById(pr.RiffId);
            if (src == null || src.Notes == null || src.Notes.Count == 0)
            {
                System.Windows.MessageBox.Show("Le riff est vide — rien à appliquer.", "Appliquer le thème", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var theme = new System.Collections.Generic.List<Engine.RiffNote>(src.Notes);
            var changes = Engine.Timeline.ArrangementEngine.RegenerateFromTheme(arr, theme);
            int applied = 0;
            foreach (var ch in changes) { var r = TimelineHelper.RiffById(ch.riffId); if (r != null) { r.Notes = ch.notes; applied++; } }
            arr.Theme = theme;       // this riff is now the canonical theme
            CommitRiffEditor();      // close the inline editor; the riff boxes/score are about to be redrawn
            Render();
            RefreshScore();
            System.Windows.MessageBox.Show("Thème reporté sur " + applied + " riff(s) (ré-exposition, développement, conclusion + contre-chant), transposé selon les accords.", "Appliquer le thème", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        // Chord lane: the user picked a new degree for chord #index. The engine chooses the flavour (DiatonicChord),
        // then we ALWAYS rebuild bass + accompaniment from the trame (voice-leading / open). With "auto transpose" on,
        // we also re-fit the existing melody/counter notes onto the new chords (NOT a theme re-derivation).
        void ApplyChordEdit(int index, int degree, int color = 0)
        {
            var arr = project.Arrangement;
            if (arr == null || arr.Chords == null || index < 0 || index >= arr.Chords.Count) return;
            // Degrees are relative to the CURRENT project key (toolbar), not the (possibly stale) compose-time arrangement key.
            int kt = Engine.Flow.MusicTheory.TonicPc(project.Key), km = Engine.Score.MusicalMode.Effective(project.Key);
            var ch = Engine.Timeline.ArrangementEngine.DiatonicChordColored(kt, km, degree, color);
            arr.Chords[index] = new Engine.Timeline.ChordCell(ch.root, ch.quality);
            // CHORD-OBJECT tracks (accompaniment / nappe rendered as PatternGeneratorModule) follow the grid: update the
            // degree of the chord object under this bar so editing the trame changes the plaqué chord in place.
            UpdateChordObjectsAt(index, project.Key ?? new Engine.Score.KeySignature());
            // Regenerate the accompaniment from the edited trame. If a V2 model is known AND the user hasn't drawn a
            // manual motif, rebuild IN THE STYLE (Vivaldi/Bach/Ghibli…); otherwise use the V1 figure/motif renderer.
            // A curated single Motif (the template engine's JSON accomp) counts as a motif too, so editing a chord
            // RE-REALIZES that figure over the new trame (RebuildHarmony honors arr.Motif) instead of re-deriving a
            // generic V2 backing — keeps the accompaniment in the piece's own figuration.
            bool hasMotif = (arr.Motif != null && arr.Motif.Notes != null && arr.Motif.Notes.Count > 0)
                         || (arr.Motifs != null && arr.Motifs.Count > 0) || (arr.BassMotifs != null && arr.BassMotifs.Count > 0);
            bool styleRebuild = !string.IsNullOrEmpty(arr.ModelFile) && !hasMotif;
            if (styleRebuild)
            {
                var b = Engine.Timeline.ArrangementEngine.RebuildBackingV2(arr);
                RedistributeToBars(arr, "Accompagnement", b.accomp);
                RedistributeToBars(arr, "Basse", b.bass);
            }
            else
            {
                var built = Engine.Timeline.ArrangementEngine.RebuildHarmony(arr);
                RedistributeToBars(arr, "Accompagnement", built.accomp);
                RedistributeToBars(arr, "Basse", built.bass);
            }
            if (autoTransposeChords)
                foreach (var rf in Engine.Timeline.ArrangementEngine.RefitMelodyToTrame(arr, id => { var r = TimelineHelper.RiffById(id); return r != null ? r.Notes : null; }))
                { var r = TimelineHelper.RiffById(rf.riffId); if (r != null) r.Notes = rf.notes; }
            CommitRiffEditor();
            Render();
            RefreshScore();
        }

        // Split a freshly rebuilt full-piece line back into the per-bar riffs of a named track (mirrors AddBarRiffs).
        void RedistributeToBars(Engine.Timeline.ComposedArrangement arr, string trackName, System.Collections.Generic.List<Engine.RiffNote> full)
        {
            TimelineTrack track = null;
            foreach (var t in project.Tracks) if (t.Name == trackName) { track = t; break; }
            if (track == null || full == null) return;
            int bar = 0;
            foreach (var item in track.Items)
            {
                if (item.Module is PlayRiffModule pr)
                {
                    var r = TimelineHelper.RiffById(pr.RiffId);
                    if (r != null)
                    {
                        int lo = bar * arr.BarSlices, hi = lo + arr.BarSlices;
                        var barNotes = new System.Collections.Generic.List<Engine.RiffNote>();
                        foreach (var n in full) if (n.Start >= lo && n.Start < hi) barNotes.Add(new Engine.RiffNote(n.Note, n.Start - lo, Math.Max(1, Math.Min(n.Length, hi - n.Start))));
                        r.Notes = barNotes;
                    }
                    bar++;
                }
            }
        }

        // Update the degree-locked CHORD OBJECT (PatternGeneratorModule) at bar `index` on every track that carries
        // chord objects (accompaniment-as-objects, nappe-as-objects). One chord object per bar, in order, so the i-th
        // object corresponds to arr.Chords[i]. Re-voices each affected chain. No-op for riff/drum tracks.
        void UpdateChordObjectsAt(int index, Engine.Score.KeySignature key)
        {
            var arr = project.Arrangement;
            if (arr == null || arr.Chords == null || index < 0 || index >= arr.Chords.Count || project.Tracks == null) return;
            var cell = arr.Chords[index];
            var dc = Engine.Flow.ChordDegrees.DegColour(key, cell.Root, cell.Quality);
            foreach (var tr in project.Tracks)
            {
                if (tr?.Items == null) continue;
                int ci = 0; bool changed = false;
                Action<PatternGeneratorModule> step = pg =>
                {
                    if (ci == index) { pg.Root = cell.Root; pg.Quality = cell.Quality; pg.Degree = dc.degree; pg.DiatonicColour = dc.colour; pg.Suspension = dc.suspension; pg.ModeOverride = dc.mode; changed = true; }
                    ci++;
                };
                foreach (var item in tr.Items)
                {
                    if (item == null) continue;
                    else if (item.Module is PatternGeneratorModule pg) step(pg);
                }
                if (changed) Engine.Flow.ChordDegrees.Revoice(tr);
            }
        }

       

        // True if any slice has at least one note on.
        static bool AnyNote(SequencerSlice[] slices)
        {
            if (slices == null) return false;
            foreach (var s in slices) if (s.NotesLow != 0 || s.NotesHigh != 0) return true;
            return false;
        }

        

        

        

        



        // "cadence_1", "cadence_2", … — the first number not already used by a project user style.
        string NextCadenceStyleName()
        {
            var used = new System.Collections.Generic.HashSet<string>();
            if (project.UserChordStyles != null) foreach (var u in project.UserChordStyles) if (u?.Name != null) used.Add(u.Name);
            for (int n = 1; ; n++) { string nm = "cadence_" + n; if (!used.Contains(nm)) return nm; }
        }

        private void btnAddCadence_Click(object sender, RoutedEventArgs e)
        {
            TimelineHelper.EnsureChordTrack(project);
            var chord = TimelineHelper.ChordTrack(project);                   // cadences ALWAYS go to the chords track
            var key = project.Key ?? new Engine.Score.KeySignature();

            // Continue from the chords track's last chord: propose starting from its degree (and keep its bass + rhythm).
            // rhythm = -1 → the dialog defaults to "Auto" (articulation derived from the chosen cadence style).
            int startDeg = 0; bool bass = false; int rhythm = -1;
            var lastChord = TimelineHelper.LastChordOn(chord);
            if (lastChord != null)
            {
                startDeg = Engine.Flow.MusicTheory.DegreeOf(key, ((lastChord.Root % 12) + 12) % 12);
                bass = lastChord.Bass; rhythm = lastChord.Style;
            }
            var dlg = new Dialogs.CadenceDialog(startDeg, bass, rhythm) { Owner = Window.GetWindow(this) };
            if (dlg.ShowDialog() != true) return;

            // "Auto" → pick an idiomatic articulation for the chosen cadence style (jazz→comping, blues→shuffle…).
            int rhythmStyle = dlg.RhythmStyle < 0 ? Engine.Flow.MusicTheory.AutoRhythmStyle(dlg.StyleIndex) : dlg.RhythmStyle;

            int measureBeats = Math.Max(1, TimelineHelper.RulerBeatsPerBar(project));
            int cpm = Math.Max(1, Math.Min(dlg.ChordsPerMeasure, measureBeats));   // each chord ≥ 1 beat
            int chordBeats = Math.Max(1, (int)Math.Round(measureBeats / (double)cpm));
            int numChords = Math.Max(1, dlg.Measures) * cpm;
            const int octave = 4;

            var chords = BuildCadenceChords(key, dlg.StartDegree, numChords, dlg.StyleIndex, octave);
            if (chords.Count == 0) return;

            // "Personnalisé" articulation → draw the motif NOW (at creation), seeded from the first chord. It's applied
            // to every generated chord. Cancelling the motif aborts the whole insertion.
            SequencerSlice[] motif = null; int motifSpb = 4; System.Collections.Generic.List<RiffNote> motifNotes = null;
            bool custom = rhythmStyle == PatternGenerator.CustomStyle;
            if (custom && !PromptMotifDialog(chord, chords[0].root, chords[0].quality, octave, chordBeats, null, 4, out motif, out motifSpb, out motifNotes))
                return;
            // Each chord spans the MOTIF's full length (a 2-bar motif → each chord lasts 2 bars, so nothing is truncated).
            int chordBeatsEff = (custom && motif != null && motifSpb > 0) ? Math.Max(1, motif.Length / motifSpb) : chordBeats;
            // Register the drawn motif as a SHARED user style "cadence_N": every chord references it, so editing one
            // chord's grid propagates to the whole cadence (and it shows up in the style dropdown for reuse).
            string cadenceStyleName = null;
            if (custom && motif != null)
            {
                cadenceStyleName = NextCadenceStyleName();
                var us = project.UserChordStyles ?? (project.UserChordStyles = new System.Collections.Generic.List<UserChordStyle>());
                us.Add(new UserChordStyle { Name = cadenceStyleName, Slices = (SequencerSlice[])motif.Clone(), Spb = motifSpb, Beats = chordBeatsEff, Notes = motifNotes != null ? new System.Collections.Generic.List<RiffNote>(motifNotes) : null });
            }

            CommitRiffEditor();
            // Insert the cadence as a SEQUENCE of individual, fully-parameterized chord objects (not one Cadence blob):
            // each chord is an editable PatternGeneratorModule with its voice-led inversion/register — so it inherits the
            // whole chord toolset (custom motif, user styles, couleur, voicing ouvert, per-chord voice-leading).
            bool vl = dlg.VoiceLead;
            TimelineItem firstItem = null;
            foreach (var ch in chords)
            {
                var dc = Engine.Flow.ChordDegrees.DegColour(key, ch.root, ch.quality);   // degree-lock (follows key) when the chord is diatonic
                var pg = new PatternGeneratorModule
                {
                    Root = ch.root, Quality = ch.quality,
                    Degree = dc.degree, DiatonicColour = dc.colour, Suspension = dc.suspension, ModeOverride = dc.mode,   // by DEGREE (else absolute for chromatic/secondary chords)
                    Inversion = vl ? ch.inversion : 0,
                    Octave = octave + (vl ? ch.octaveShift : 0),
                    VoiceLeadMode = vl ? 1 : 0,                       // renversement AUTO (chain re-voices on edit)
                    OpenVoicing = dlg.OpenVoicing,
                    Style = rhythmStyle, Bass = dlg.Bass,
                    BeatsPerBar = chordBeatsEff, Repeats = 1,
                };
                if (custom && motif != null) { pg.UserStyleName = cadenceStyleName; pg.SetCustom((SequencerSlice[])motif.Clone(), motifSpb); pg.CustomNotes = motifNotes != null ? new System.Collections.Generic.List<RiffNote>(motifNotes) : null; } // shared "cadence_N" ref
                var it = new TimelineItem { Module = pg };
                TimelineHelper.InsertTopLevel(chord, it);
                if (firstItem == null) firstItem = it;
            }
            if (vl) Engine.Flow.ChordDegrees.Revoice(chord);   // apply auto voice-leading across the inserted chain (from any prior context)
            selectedTrack = chord; selectedItem = firstItem;
            Render();
            RefreshScore();
        }

        // Build a cadence's chords with voice-led inversions + octave placement: each chord picks the voicing that
        // moves the notes the least from the previous chord while staying in register ("fingers barely move").
        System.Collections.Generic.List<(int root, int quality, int inversion, int octaveShift)> BuildCadenceChords(
            Engine.Score.KeySignature key, int startDeg, int numChords, int style, int octave, int anchor = 0)
        {
            var result = new System.Collections.Generic.List<(int, int, int, int)>();
            var chords = Engine.Flow.MusicTheory.Cadence(key, startDeg, numChords, style, Environment.TickCount);
            if (chords.Count == 0) return result;
            var vl = Engine.Flow.MusicTheory.VoiceLead(chords, octave, anchor);
            for (int i = 0; i < chords.Count; i++) result.Add((chords[i].root, chords[i].quality, vl[i].inversion, vl[i].shift));
            return result;
        }

        // Re-voice EXISTING cadence chords (no re-roll) under a voice-lead mode: 0 aucun / 1 auto / 2 basse / 3 haut.
        System.Collections.Generic.List<CadenceChord> RevoiceCadence(Engine.Score.KeySignature key, System.Collections.Generic.List<CadenceChord> chords, int mode, int octave)
        {
            var basics = new System.Collections.Generic.List<(int root, int quality)>();
            foreach (var c in chords) basics.Add((c.Root, c.Quality));
            var tuples = new System.Collections.Generic.List<(int, int, int, int)>();
            if (mode == 0)
                foreach (var b in basics) tuples.Add((b.root, b.quality, 0, 0));
            else
            {
                var vl = Engine.Flow.MusicTheory.VoiceLead(basics, octave, mode - 1);   // mode 1→anchor 0, 2→1, 3→2
                for (int i = 0; i < basics.Count; i++) tuples.Add((basics[i].root, basics[i].quality, vl[i].inversion, vl[i].shift));
            }
            return MakeCadenceChords(key, tuples, mode != 0, octave);
        }

        // Turn voice-led chords into stored CadenceChords. A chord whose root sits exactly on a scale degree is
        // degree-locked (follows transposition/key changes); chromatic roots stay absolute. Also voice-leads the
        // ARP "single held note" (HeldVoice = the chord-tone nearest the previous held note → a smooth top line).
        System.Collections.Generic.List<CadenceChord> MakeCadenceChords(
            Engine.Score.KeySignature key, System.Collections.Generic.List<(int root, int quality, int inversion, int octaveShift)> chords, bool voiceLead, int octave)
        {
            var outl = new System.Collections.Generic.List<CadenceChord>();
            int prevHeld = int.MinValue;
            foreach (var ch in chords)
            {
                int deg = Engine.Flow.MusicTheory.DegreeOf(key, ch.root);
                bool diatonicRoot = Engine.Flow.MusicTheory.DiatonicChord(key, deg).root == ch.root;
                int inv = voiceLead ? ch.inversion : 0, shift = voiceLead ? ch.octaveShift : 0;

                // Held note: the chord tone closest to the previous chord's held note (top for the first chord).
                var notes = PatternGenerator.ChordNotes(ch.root, octave + shift, ch.quality, inv);
                int heldVoice = notes.Length - 1;
                if (prevHeld != int.MinValue && notes.Length > 0)
                {
                    int bd = int.MaxValue;
                    for (int k = 0; k < notes.Length; k++) { int d = Math.Abs(notes[k] - prevHeld); if (d < bd) { bd = d; heldVoice = k; } }
                }
                if (notes.Length > 0) prevHeld = notes[heldVoice];

                outl.Add(new CadenceChord
                {
                    Root = ch.root, Quality = ch.quality, Inversion = inv, OctaveShift = shift,
                    HeldVoice = heldVoice, Degree = diatonicRoot ? deg : -1,
                });
            }
            return outl;
        }

        // Open the cadence's "Personnalisé" motif editor in a DIALOG (same RhythmGridControl as the chord editor):
        // a degree grid (bass + chord-tone degrees over two octaves) applied to every chord of the cadence.
        // Open the degree-grid motif editor (same RhythmGridControl as the chord editor) in a modal DIALOG, seeded from
        // the given chord (voices) + existing slices. Returns true (with the drawn grid) on "Appliquer", false on cancel.
        bool PromptMotifDialog(TimelineTrack track, int firstRoot, int firstQual, int octave, int beatsPerBar,
                               SequencerSlice[] existing, int existingSpb, out SequencerSlice[] outSlices, out int outSpb,
                               out System.Collections.Generic.List<RiffNote> outNotes, System.Collections.Generic.List<RiffNote> existingNotes = null)
        {
            outSlices = existing; outSpb = existingSpb > 0 ? existingSpb : 4; outNotes = existingNotes;
            int chordLen = Math.Max(1, PatternGenerator.ChordNotes(firstRoot, octave, firstQual, 0).Length);
            int voices = chordLen * 2;
            var labels = new string[voices + 1]; labels[0] = "Basse";
            for (int i = 0; i < voices; i++) { int deg = 2 * (i % chordLen) + 1; labels[i + 1] = deg + (i >= chordLen ? "'" : ""); }

            var userStyles = project.UserChordStyles ?? (project.UserChordStyles = new System.Collections.Generic.List<UserChordStyle>());
            int builtinCount = PatternGenerator.StyleNames.Length;
            var styleNames = new string[builtinCount + userStyles.Count];
            Array.Copy(PatternGenerator.StyleNames, styleNames, builtinCount);
            for (int i = 0; i < userStyles.Count; i++) styleNames[builtinCount + i] = userStyles[i].Name;
            Func<int, int, SequencerSlice[]> seedFunc = (st, b) =>
                st < builtinCount ? PatternGenerator.VoiceBarForCustom(st, b, chordLen)
                                  : (st - builtinCount < userStyles.Count ? userStyles[st - builtinCount].Slices : null);
            Func<int, int> seedSpbFunc = st => st >= builtinCount && st - builtinCount < userStyles.Count ? Math.Max(1, userStyles[st - builtinCount].Spb) : PatternGenerator.SlicesPerQuarter;
            var grid = new Controls.RhythmGridControl();
            Func<SequencerSlice[], int, Riff> mk = (gr, gs) => { var t = new PatternGeneratorModule { Root = firstRoot, Octave = octave, Quality = firstQual, Style = PatternGenerator.CustomStyle, BeatsPerBar = beatsPerBar, Repeats = 1 }; t.SetCustom(gr, gs); t.CustomNotes = grid.CurrentNotes(); return PatternGenerator.Generate(t); };

            grid.Configure(labels, beatsPerBar, existingSpb > 0 ? existingSpb : 4, existing,
                           styleNames, seedFunc, PatternGenerator.SlicesPerQuarter, mk, InstrumentCatalog.GetPreset(track.Instrument), seedSpbFunc, null,
                           noteList: true, existingNotes: existingNotes);

            var ok = new Button { Content = "Appliquer", Width = 96, IsDefault = true, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(8, 3, 8, 3) };
            var cancel = new Button { Content = "Annuler", Width = 96, IsCancel = true, Padding = new Thickness(8, 3, 8, 3) };
            var btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(10) };
            btns.Children.Add(ok); btns.Children.Add(cancel);
            var dock = new DockPanel(); DockPanel.SetDock(btns, Dock.Bottom); dock.Children.Add(btns); dock.Children.Add(grid);
            var win = new Window
            {
                Title = "Motif personnalisé", Width = 760, Height = 380, Owner = Window.GetWindow(this),
                WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x20)), Content = dock,
            };
            bool applied = false;
            ok.Click += (s, e) => { applied = true; win.DialogResult = true; };
            win.ShowDialog();
            if (!applied) return false;
            outSlices = grid.CurrentGrid(); outSpb = grid.Spb; outNotes = grid.CurrentNotes();
            return true;
        }

        void EditCadenceMotif(TimelineTrack track, CadenceModule cm, Action refresh)
        {
            int firstRoot = 0, firstQual = 0;
            if (cm.Chords != null && cm.Chords.Count > 0) { firstRoot = cm.Chords[0].Root; firstQual = cm.Chords[0].Quality; }
            if (PromptMotifDialog(track, firstRoot, firstQual, cm.Octave, cm.BeatsPerBar, cm.CustomSlices, cm.CustomSlicesPerQuarter, out _, out var spb, out var notes, cm.CustomNotes))
            { cm.SetCustomNotes(notes, spb, cm.BeatsPerBar * spb); cm.Style = PatternGenerator.CustomStyle; refresh(); }
        }

        // "Mi♭ Maj7 · Si♭ 7 (dom) · …" — the chord sequence of a cadence module (roots spelled for the project key).
        string CadenceChordsLabel(CadenceModule cm)
        {
            var parts = new System.Collections.Generic.List<string>();
            foreach (var c in cm.Chords) parts.Add(Engine.Score.KeySig.SpellPc(c.Root, project.Key) + " " + TimelineHelper.Get(PatternGenerator.QualityNames, c.Quality));
            return parts.Count == 0 ? "(vide)" : string.Join("   ·   ", parts);
        }

        // Cadence module editor: shared rendering settings (rhythm/octave/bass) + the cadence style and a
        // "Régénérer" button to re-roll a variant in the project key. The chord list is shown read-only.
        // Everything renders through PatternGenerator.GenerateCadence — no separate chord bricks.
        UIElement BuildCadenceEditor(TimelineTrack track, CadenceModule cm)
        {
            var sp = new StackPanel { Margin = new Thickness(4) };
            Action refresh = () => { Render(); if (activeScore != null) RefreshScore(); };
            var chordList = new TextBlock { Foreground = "#BBBBBB".ToBrush(), FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0), MaxWidth = 540 };
            Action showChords = () => chordList.Text = CadenceChordsLabel(cm);

            // Generation parameters (apply when you press « Régénérer »).
            sp.Children.Add(EdLabel("Style de cadence"));
            sp.Children.Add(ParamCombo(Engine.Flow.MusicTheory.CadenceStyles, cm.CadenceStyle, v => cm.CadenceStyle = v, () => { }));

            sp.Children.Add(EdLabel("Depuis le degré"));
            var degNames = new[] { "I (tonique)", "ii", "iii", "IV", "V", "vi", "vii" };
            sp.Children.Add(ParamCombo(degNames, Math.Max(0, Math.Min(6, cm.StartDegree)), v => cm.StartDegree = v, () => { }));

            sp.Children.Add(EdLabel("Mesures"));
            sp.Children.Add(ParamNum(cm.Measures, v => cm.Measures = v, () => { }));
            sp.Children.Add(EdLabel("Accords / mesure"));
            sp.Children.Add(ParamNum(cm.ChordsPerMeasure, v => cm.ChordsPerMeasure = v, () => { }));

            // Rendering settings (apply immediately to the stored chords).
            sp.Children.Add(EdLabel("Rythme / articulation"));
            // Full list INCLUDING "Personnalisé…" (CustomStyle) so a hand-drawn motif can drive the whole cadence.
            sp.Children.Add(ParamCombo(PatternGenerator.StyleNames, Math.Max(0, Math.Min(cm.Style, PatternGenerator.StyleNames.Length - 1)), v => cm.Style = v, refresh));
            var editMotif = new Button { Content = "Éditer le motif personnalisé…", Margin = new Thickness(0, 6, 0, 0), Padding = new Thickness(8, 3, 8, 3), HorizontalAlignment = HorizontalAlignment.Left, Cursor = Cursors.Hand };
            editMotif.Click += (s, e) => EditCadenceMotif(track, cm, refresh);
            sp.Children.Add(editMotif);

            sp.Children.Add(EdLabel("Octave"));
            sp.Children.Add(ParamNum(cm.Octave, v => cm.Octave = v, refresh));

            // Voice-leading: re-pick each chord's INVERSION for smooth motion (re-voices the existing chords, no re-roll).
            sp.Children.Add(EdLabel("Renversement (voice-leading)"));
            sp.Children.Add(ParamCombo(VoiceLeadModeNames, Math.Max(0, Math.Min(3, cm.VoiceLeadMode)), v => cm.VoiceLeadMode = v, () =>
            {
                var vlKey = project.Key ?? new Engine.Score.KeySignature();
                cm.Chords = RevoiceCadence(vlKey, cm.Chords, cm.VoiceLeadMode, cm.Octave);
                showChords(); refresh();
            }));
            var cadOpen = new CheckBox { Content = "Voicing ouvert (écarté)", Foreground = Brushes.White, FontSize = 11, Margin = new Thickness(0, 6, 0, 0), IsChecked = cm.OpenVoicing };
            cadOpen.Checked += (s, e) => { cm.OpenVoicing = true; refresh(); };
            cadOpen.Unchecked += (s, e) => { cm.OpenVoicing = false; refresh(); };
            sp.Children.Add(cadOpen);

            sp.Children.Add(EdLabel("Basse (fondamentale)"));
            sp.Children.Add(ParamCombo(BassModeNames, !cm.Bass ? 0 : (cm.BassPerBeat ? 2 : 1), v => { cm.Bass = v > 0; cm.BassPerBeat = v == 2; }, refresh));
            sp.Children.Add(EdLabel("Montée (styles arpège)"));
            sp.Children.Add(ParamCombo(ClimbModeNames, cm.ClimbMode, v => cm.ClimbMode = v, refresh));
            sp.Children.Add(EdLabel("Note tenue (styles arpège)"));
            sp.Children.Add(ParamCombo(HeldModeNames, cm.HeldMode, v => cm.HeldMode = v, refresh));
            var halve = new CheckBox { Content = "Doubles-croches (÷2) — styles arpège", Foreground = Brushes.White, FontSize = 11, Margin = new Thickness(0, 6, 0, 0), IsChecked = cm.HalveDurations };
            halve.Checked += (s, e) => { cm.HalveDurations = true; refresh(); };
            halve.Unchecked += (s, e) => { cm.HalveDurations = false; refresh(); };
            sp.Children.Add(halve);

            var regen = new Button { Content = "Régénérer (variante)", Margin = new Thickness(0, 10, 0, 0), Padding = new Thickness(8, 3, 8, 3), HorizontalAlignment = HorizontalAlignment.Left, Cursor = Cursors.Hand };
            regen.Click += (s, e) =>
            {
                var key = project.Key ?? new Engine.Score.KeySignature();
                int measureBeats = Math.Max(1, TimelineHelper.RulerBeatsPerBar(project));
                int cpm = Math.Max(1, Math.Min(cm.ChordsPerMeasure, measureBeats)); // each chord ≥ 1 beat
                int chordBeats = Math.Max(1, (int)Math.Round(measureBeats / (double)cpm));
                int numChords = Math.Max(1, cm.Measures) * cpm;
                cm.BeatsPerBar = chordBeats;
                int anchor = cm.VoiceLeadMode <= 1 ? 0 : cm.VoiceLeadMode - 1;   // 0/1→auto, 2→basse, 3→haut
                var chords = BuildCadenceChords(key, cm.StartDegree, numChords, cm.CadenceStyle, cm.Octave, anchor);
                if (chords.Count == 0) return;
                cm.Chords = MakeCadenceChords(key, chords, cm.VoiceLeadMode != 0, cm.Octave);
                showChords();
                refresh();
            };
            sp.Children.Add(regen);

            sp.Children.Add(EdLabel("Accords générés"));
            showChords();
            sp.Children.Add(chordList);
            sp.Children.Add(new TextBlock { Text = "Change un paramètre (style, degré, mesures…) puis « Régénérer ». Un seul module : il stocke chaque accord et le générateur d'accords fait le rendu (partition, audio, export).", Foreground = "#888888".ToBrush(), FontSize = 10, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), MaxWidth = 540 });
            return sp;
        }



        

        // Drum: left = the node-like fields, right = the manual grid (when "Personnalisé").
        UIElement BuildDrumEditor(TimelineTrack track, TimelineItem item, DrumPatternModule dp)
        {
            // Baseline for the deferred, on-leave box refresh (the grid persists live via GridChanged, but the
            // timeline thumbnail is only rebuilt when we leave the editor — a full Render per stroke is too slow).
            riffEditItem = item; riffEditTrack = track; riffOpenLen = TimelineHelper.DispLen(item); riffDirty = false;

            var grid = TwoColumns(out StackPanel left, out ContentControl host);
            Action refresh = null;
            refresh = () => { RefreshDrumGrid(host, track, item, dp); Render(); };

            var aiBtn = new Button { Content = "🤖 Générer (IA)…", Margin = new Thickness(0, 0, 0, 8), Cursor = Cursors.Hand, Style = (Style)FindResource("okButton"), ToolTip = "Décris une intention de groove ; l'IA génère un motif de percussions et l'applique à ce module." };
            aiBtn.Click += (s, e) => GenerateDrumWithAi(dp, refresh);
            left.Children.Add(aiBtn);

            left.Children.Add(EdLabel("Kit")); left.Children.Add(ParamCombo(InstrumentCatalog.DrumKitNames().ToArray(), dp.Kit, v => dp.Kit = v, refresh));
            // Catégorie + Motif — the catalogue is the single source (built-in styles + exotic + "Personnalisé").
            const string CUSTOM = "Personnalisé";
            var catalog = Engine.Flow.DrumCatalog.Instance;
            var cboCat = new ComboBox { Margin = new Thickness(0, 0, 0, 6) };
            foreach (var c in catalog.Categories) cboCat.Items.Add(c.Name);
            cboCat.Items.Add(CUSTOM);
            var cboMotif = new ComboBox { Margin = new Thickness(0, 0, 0, 6) };
            bool syncing = false;

            void FillMotifs(string cat, string sel)
            {
                syncing = true;
                cboMotif.Items.Clear();
                if (cat == CUSTOM) { cboMotif.Items.Add(CUSTOM); foreach (var u in project.UserDrumStyles) cboMotif.Items.Add(u.Name); }
                else foreach (var c in catalog.Categories) if (c.Name == cat) foreach (var mo in c.Motifs) cboMotif.Items.Add(mo.Name);
                int idx = sel != null ? cboMotif.Items.IndexOf(sel) : -1;
                cboMotif.SelectedIndex = idx >= 0 ? idx : (cboMotif.Items.Count > 0 ? 0 : -1);
                syncing = false;
            }

            string initCat, initMotif;
            if (!string.IsNullOrEmpty(dp.CatCategory)) { initCat = dp.CatCategory; initMotif = dp.CatMotif; }
            else if (dp.Style == DrumPattern.CustomStyle) { initCat = CUSTOM; initMotif = CUSTOM; }
            else { initCat = Engine.Flow.DrumCatalog.StandardCategory; initMotif = (dp.Style >= 0 && dp.Style < DrumPattern.StyleNames.Length) ? DrumPattern.StyleNames[dp.Style] : null; }
            syncing = true; cboCat.SelectedItem = initCat; if (cboCat.SelectedItem == null) cboCat.SelectedIndex = 0; syncing = false;
            FillMotifs(cboCat.SelectedItem as string, initMotif);

            // Rebuild the whole editor after a catalogue change so the left-panel fields (Répétitions, Temps…) reflect
            // the applied motif (BeatsPerBar + adapted Repeats).
            Action rebuild = () => { editorHost.Content = BuildDrumEditor(track, item, dp); Render(); };
            cboCat.SelectionChanged += (s, e) => { if (syncing) return; FillMotifs(cboCat.SelectedItem as string, null); TimelineHelper.ApplyDrumCatalog(project, dp, cboCat.SelectedItem as string, cboMotif.SelectedItem as string); rebuild(); };
            cboMotif.SelectionChanged += (s, e) => { if (syncing) return; TimelineHelper.ApplyDrumCatalog(project, dp, cboCat.SelectedItem as string, cboMotif.SelectedItem as string); rebuild(); };

            left.Children.Add(EdLabel("Catégorie")); left.Children.Add(cboCat);
            left.Children.Add(EdLabel("Motif")); left.Children.Add(cboMotif);

            var custBtn = new Button { Content = "Personnaliser", Margin = new Thickness(0, 2, 0, 6), Padding = new Thickness(10, 4, 10, 4), Cursor = Cursors.Hand, HorizontalAlignment = HorizontalAlignment.Left, ToolTip = "Copier ce motif dans un motif personnalisé éditable à la main." };
            custBtn.Click += (s, e) => { TimelineHelper.CustomizeDrum(dp); syncing = true; cboCat.SelectedItem = CUSTOM; syncing = false; FillMotifs(CUSTOM, CUSTOM); refresh(); };
            left.Children.Add(custBtn);

            left.Children.Add(EdLabel("Densité")); left.Children.Add(ParamCombo(DrumPattern.DensityNames, dp.Density, v => dp.Density = v, refresh));
            var fill = new CheckBox { Content = "Fill sur la dernière mesure", Foreground = Brushes.White, FontSize = 11, Margin = new Thickness(0, 6, 0, 0), IsChecked = dp.FillLast };
            fill.Checked += (s, e) => { dp.FillLast = true; }; fill.Unchecked += (s, e) => { dp.FillLast = false; };
            left.Children.Add(fill);
            left.Children.Add(EdLabel("Temps / mesure")); left.Children.Add(ParamNum(dp.BeatsPerBar, v => dp.BeatsPerBar = v, refresh));
            left.Children.Add(EdLabel("Répétitions")); left.Children.Add(ParamNum(dp.Repeats, v => dp.Repeats = v, refresh));

            RefreshDrumGrid(host, track, item, dp);
            return grid;
        }

        
        void RefreshDrumGrid(ContentControl host, TimelineTrack track, TimelineItem item, DrumPatternModule dp)
        {
            if (!TimelineHelper.DrumIsCustom(dp))
            {
                host.Content = new TextBlock { Text = "Motif du catalogue appliqué. Clique « Personnaliser » pour l'éditer à la main.", Foreground = "#888888".ToBrush(), FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(10) };
                return;
            }
            // Riff-like drum editor: NOTE mode (note+duration, draw/erase), rows = every GM percussion lane,
            // coloured by family. Preview + persistence go through the note list (one hit per note at its start).
            var rg = new Controls.RhythmGridControl();
            Func<SequencerSlice[], int, Riff> mk = (gr, gs) =>
            {
                var t = new DrumPatternModule { Kit = dp.Kit, Style = DrumPattern.CustomStyle, BeatsPerBar = dp.BeatsPerBar, Repeats = 1 };
                t.SetCustomNotes(rg.CurrentNotes(), gs, gr != null ? gr.Length : 0);
                return DrumPattern.Generate(t);
            };
            // "Enregistrer ce style" (grid toolbar): save the current motif to the project under a name → it appears in
            // the "Personnalisé" category and can be reused on other drum modules.
            Action onSaveStyle = () =>
            {
                string name = TimelineHelper.PromptText("Enregistrer le motif batterie", string.IsNullOrEmpty(dp.CatMotif) || dp.CatMotif == "Personnalisé" ? "Mon motif" : dp.CatMotif);
                if (string.IsNullOrWhiteSpace(name)) return;
                name = name.Trim();
                var entry = new UserChordStyle { Name = name, Slices = rg.CurrentGrid(), Spb = rg.Spb, Beats = rg.Beats, Notes = rg.CurrentNotes() };
                int ex = project.UserDrumStyles.FindIndex(u => u.Name == name);
                if (ex >= 0) project.UserDrumStyles[ex] = entry; else project.UserDrumStyles.Add(entry);
                dp.CatCategory = "Personnalisé"; dp.CatMotif = name;
                editorHost.Content = BuildDrumEditor(track, item, dp);   // rebuild → the motif combo lists + selects the saved name
            };
            // No seed picker here (removed): "Personnaliser" already copies the motif in. Pass null seed styles.
            rg.Configure(DrumPattern.LaneNames, dp.BeatsPerBar, dp.CustomSlicesPerQuarter > 0 ? dp.CustomSlicesPerQuarter : 4, dp.CustomSlices,
                         null, null, DrumPattern.SlicesPerQuarter, mk,
                         InstrumentCatalog.GetDrumKit(dp.Kit),
                         onSaveStyle: onSaveStyle,
                         noteList: true, existingNotes: dp.CustomNotes,
                         rowColor: lane => Controls.DrumColors.ForLane(lane),
                         seedNotesFunc: null);
            rg.GridChanged += () => { dp.SetCustomNotes(rg.CurrentNotes(), rg.Spb, rg.Beats * rg.Spb); riffDirty = true; }; // box refreshed on leave, not per stroke
            host.Content = rg;
        }

        

        

        // "🤖 Générer (IA)…" in the drum editor: describe a groove intention → the AI returns a percussion motif
        // ({motifBars, repeats, notes}) which is applied to this drum module (Style = Personnalisé, looped).
        void GenerateDrumWithAi(DrumPatternModule dp, Action onApplied)
        {
            CommitRiffEditor();
            TimelineHelper.GenerateDrumWithAi(
                Window.GetWindow(this),
                project, 
                txtKeySummary?.Text ?? "", 
                txtMeterSummary?.Text ?? (project.TimeSigNum + "/" + project.TimeSigDen), 
                dp, onApplied);

         }

        // ---- Riff generator (IA) --------------------------------------------------------------------------------
        void GenerateRiffWithAi(TimelineTrack track, PlayRiffModule pr, TimelineItem editedItem, Controls.RiffGridControl rg)
        {
            var riff = TimelineHelper.RiffById(pr.RiffId);
            if (riff == null) return;
            int barTemps = TimelineHelper.RulerBeatsPerBar(project);
            double startBeat = TimelineHelper.ItemStartBeat(track, editedItem);
            double lenBeats = Math.Max(barTemps, TimelineHelper.DispLen(editedItem));
            int measures = Math.Max(1, (int)Math.Round(lenBeats / Math.Max(1, barTemps)));
            string keyStr = txtKeySummary?.Text ?? "";
            string meterStr = txtMeterSummary?.Text ?? (project.TimeSigNum + "/" + project.TimeSigDen);
            var chords = TimelineHelper.ChordsUnder(project,startBeat, lenBeats, barTemps);
            bool hasChords = chords.Count > 0;
            string ctx = hasChords
                ? $"Tonalité {keyStr} · {meterStr} · {measures} mes. · {chords.Count} accord(s) sous le riff"
                : $"Tonalité {keyStr} · {meterStr} · {measures} mes. · aucun accord (l'IA en proposera)";
            var dlg = new Dialogs.AiElementDialog("Riff — IA", ctx,
                intention => Engine.AI.AiArrangement.BuildRiffPrompt(keyStr, meterStr, barTemps, measures, chords, intention)) { Owner = Window.GetWindow(this) };
            if (dlg.ShowDialog() != true || string.IsNullOrWhiteSpace(dlg.ResultJson)) return;
            try { 
                TimelineHelper.ApplyAiRiff(
                    project,
                    track, pr, editedItem, rg, riff, dlg.ResultJson, barTemps, measures, hasChords,
                    out riffEditItem, out riffEditTrack, out riffDirty
                    );
            }
            catch (Exception ex) { MessageBox.Show("Réponse IA invalide : " + ex.Message, "Riff — IA", MessageBoxButton.OK, MessageBoxImage.Warning); }
        }

        
        Grid TwoColumns(out StackPanel left, out ContentControl right)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            // Options panel scrolls on its own if there isn't enough height.
            left = new StackPanel { Margin = new Thickness(2, 0, 12, 0) };
            var leftScroll = new ScrollViewer
            {
                Content = left,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            };
            Grid.SetColumn(leftScroll, 0); grid.Children.Add(leftScroll);
            right = new ContentControl();
            Grid.SetColumn(right, 1); grid.Children.Add(right);
            return grid;
        }

        // ---- toolbar ---------------------------------------------------------------

        private void btnAddInstrTrack_Click(object sender, RoutedEventArgs e)
            => AddTrack(new TimelineTrack { Name = "Instr " + (project.Tracks.Count + 1), Type = TimelineTrackType.Instrument, Instrument = 0 });

        private void btnAddDrumTrack_Click(object sender, RoutedEventArgs e)
            => AddTrack(new TimelineTrack { Name = "Batterie " + (project.Tracks.Count + 1), Type = TimelineTrackType.Drum, Instrument = InstrumentCatalog.DrumIndex });

        // "🏛️ Créer structure…": the dedicated dialog drives the ORCHESTRATEUR (form skeleton + style),
        // producing an EDITABLE arrangement (its chords/theme/per-section motif can then be reworked in place).
        private void btnCreateStructure_Click(object sender, RoutedEventArgs e)
        {
            CommitRiffEditor();
            var key = project.Key ?? new Engine.Score.KeySignature();
            double bpm = (project.Tempo != null && project.Tempo.Count > 0) ? project.Tempo[0].Bpm : 60;
            var dlg = new Dialogs.CreateStructureDialog(key, bpm, project.TimeSigNum, project.TimeSigDen) { Owner = Window.GetWindow(this) };
            if (dlg.ShowDialog() != true || dlg.ChosenComposer == null) return;

            var ctx = new Engine.Timeline.ComposeContext
            {
                Key = dlg.ChosenKey,
                MeterNum = dlg.MeterNum,
                MeterDen = dlg.MeterDen,
                Seed = Environment.TickCount,
                Options = dlg.Options,
                Bpm = dlg.Bpm,
                IntroBars = dlg.IntroBars,
                ThemeBars = dlg.ThemeBars,
                ThemeReps = dlg.ThemeReps,
                OutroBars = dlg.OutroBars,
                GenerateMusic = dlg.GenerateMusic,
                IncludePad = dlg.IncludePad,
                IncludeBass = dlg.IncludeBass,
                IncludeCounter = dlg.IncludeCounter,
                IncludeIntroMelody = dlg.IncludeIntroMelody,
                CounterSameStaff = dlg.CounterSameStaff,
                MelodyInstrument = dlg.MelodyInstrument,
                AccompInstrument = dlg.AccompInstrument,
                PadInstrument = dlg.PadInstrument,
            };
            try
            {
                Mouse.OverrideCursor = Cursors.Wait;
                var res = dlg.ChosenComposer.Compose(ctx);
                ApplyComposeResult(res);
                if (!res.MelodicLineMode)    // melodic-line mode already emits custom chord/pad Patterns
                {
                    TimelineTrack track;
                    // accompaniment as editable CHORD OBJECTS (replaces the riff accomp line)
                    if (TimelineHelper.BuildChordAccompaniment(project,scoreTracks,out track))
                    {
                        selectedTrack = track;
                    }
                    TimelineHelper.BuildNappeChords(project, scoreTracks);          // the string pad too → whole-bar plaqué chord (degree, auto voice-leading, open voicing)
                }
                Render(); RefreshScore();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Échec de la composition : " + ex.Message, "Composition", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally { Mouse.OverrideCursor = null; }
        }

        // "🎼 Accompagnement en accords": from the structured piece's chord trame, build a track of one editable CHORD
        // OBJECT per measure (by DEGREE, so changing a degree edits the object — no transpose), with a shared, editable
        // motif PER SECTION (a user style named after the section: edit one bar → the whole section follows).
        private void btnChordAccomp_Click(object sender, RoutedEventArgs e)
        {
            var arr = project.Arrangement;
            if (arr == null || arr.Chords == null || arr.Chords.Count == 0)
            {
                MessageBox.Show("Disponible sur une musique structurée (Piste → Créer structure…).", "Accompagnement en accords", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            CommitRiffEditor();
            TimelineTrack track;
            if (TimelineHelper.BuildChordAccompaniment(project,scoreTracks,out track))
            {
                selectedTrack = track;
            }
            TimelineHelper.BuildNappeChords(project, scoreTracks);   // the pad is also just a plaqué chord on the degree → make it chord objects too
            Render();
            RefreshScore();
        }

        // Apply a composed result to the timeline: wipe + drop in tracks/riffs, adopt key/meter/tempo, and persist the
        // editable arrangement. Shared by "Composer un morceau" and "Créer structure".
        void ApplyComposeResult(Engine.Timeline.ComposeResult result)
        {
            // Wipe the whole timeline first, then drop in the composed tracks.
            project.Tracks.Clear();
            scoreTracks.Clear();
            activeScore = null;
            selectedItem = null;
            foreach (var r in result.Riffs) RiffLibrary.Instance.Riffs.Add(r);
            foreach (var t in result.Tracks) project.Tracks.Add(t);
            // show all melodic parts (melody voices + chords + bass) in the score; drums are percussion.
            foreach (var t in result.Tracks) if (t.Type != TimelineTrackType.Drum) scoreTracks.Add(t);
            selectedTrack = result.Tracks.Count > 0 ? result.Tracks[0] : null;
            // The timeline ADOPTS whatever the composer's options produced — tonality/mode, time signature and tempo.
            if (result.ResultKey != null) project.Key = result.ResultKey;
            project.Arrangement = result.Arrangement;   // persist the chord trame + sections + theme (null for composers that don't emit one)
            if (result.ResultMeterNum > 0 && result.ResultMeterDen > 0)
            {
                project.TimeSigNum = result.ResultMeterNum;
                project.TimeSigDen = result.ResultMeterDen;
                project.TimeSigScale = result.ResultMeterDen == 8 ? 1.5 : 1.0;
                if (activeRiffGrid != null) activeRiffGrid.MeterDen = project.TimeSigDen;
            }
            if (result.ResultTempo != null && result.ResultTempo.Count > 0)
            {
                // per-section tempo map (e.g. the climax lifts then returns) → a list of TempoChange points.
                project.Tempo = new System.Collections.Generic.List<TempoChange>();
                foreach (var tp in result.ResultTempo) project.Tempo.Add(new TempoChange { Beat = tp.beat, Bpm = tp.bpm });
                txtBpm.Text = ((int)project.Tempo[0].Bpm).ToString();
            }
            else if (result.ResultBpm > 0)
            {
                if (project.Tempo == null || project.Tempo.Count == 0) project.Tempo = new System.Collections.Generic.List<TempoChange> { new TempoChange() };
                project.Tempo[0].Bpm = result.ResultBpm;
                txtBpm.Text = ((int)result.ResultBpm).ToString();
            }
            SyncKeyToolbar(); // key combos + meter combo + ternary toggle follow the project
            Render();
            RefreshScore();
        }

        void AddTrack(TimelineTrack t)
        {
            project.Tracks.Add(t);
            TimelineHelper.EnsureChordTrack(project);     // keep the chords track pinned at the bottom
            selectedTrack = t;
            selectedItem = null;
            Render();
        }

        // Insert a chord module — ALWAYS into the chords track (no need to select it first).
        void AppendChord(FlowModule m)
        {
            TimelineHelper.EnsureChordTrack(project);
            var chord = TimelineHelper.ChordTrack(project);
            var item = new TimelineItem { Module = m };
            TimelineHelper.InsertTopLevel(chord, item);
            selectedTrack = chord; SelectItem(chord, item);
            Render();
        }

        void AppendModule(FlowModule m)
        {
            if (selectedTrack == null) { MessageBox.Show("Sélectionne d'abord une piste."); return; }
            // If a Repeat is selected, add INSIDE it (its sub-track); keep the Repeat selected so you
            
            var item = new TimelineItem { Module = m }; // appended at the end, or inserted before a loop Repeat
            TimelineHelper.InsertTopLevel(selectedTrack, item);
            SelectItem(selectedTrack, item);
            Render(); // new box -> rebuild lanes
        }

        // +Riff: create a 1-measure empty riff and drop it in the first free slot AT/after the selected item
        // (just behind it if there's room, else the first later gap big enough, else at the end). Default = 1 bar.
        private void btnAddRiff_Click(object sender, RoutedEventArgs e)
        {
            if (selectedTrack == null) { MessageBox.Show("Sélectionne d'abord une piste."); return; }
            var track = selectedTrack;

            int temps = TimelineHelper.RulerBeatsPerBar(project);                // one bar in temps: num in /4, num/3 in /8
            const int spq = 24;                            // canonical resolution: 1 temps = 24 slices (like imports)
            var riff = new Riff { Name = "Riff " + (RiffLibrary.Instance.Riffs.Count + 1), LengthSlices = temps * spq, SlicesPerQuarter = spq };
            RiffLibrary.Instance.Riffs.Add(riff);
            var item = new TimelineItem { Module = new PlayRiffModule { RiffId = riff.Id } };

            TimelineHelper.PlaceInFreeSlot(selectedItem, track, item, temps);

            SelectItem(track, item); // open the riff editor on the new 1-measure riff
            Render();
        }

        
        private void btnAddPattern_Click(object sender, RoutedEventArgs e)
        {
            TimelineHelper.EnsureChordTrack(project);
            var chord = TimelineHelper.ChordTrack(project);                                 // chords ALWAYS go to the dedicated chords track
            var lastItem = chord.Items.Count > 0 ? chord.Items[chord.Items.Count - 1] : null;
            var prev = TimelineHelper.LastChordOn(chord);
            var pg = NewChordLike(prev);   // meter-length default + copies the last chord's params (voice-leading auto)
            if (prev != null)
            {
                // CONTEXT-AWARE suggestion (last 2-3 chord degrees + bar position). If the current chord is on a scale
                // degree, propose a functional continuation; the new chord keeps the previous one's nature.
                var key = project.Key ?? new Engine.Score.KeySignature();
                ChordContext(chord, lastItem, out int[] prevDegs, out int barIdx, out int phraseLen);
                if (prevDegs.Length > 0 && prevDegs[prevDegs.Length - 1] >= 0)
                {
                    var dlg = new Dialogs.ChordSuggestionDialog(prevDegs, barIdx, phraseLen, key, InstrumentCatalog.GetPreset(chord.Instrument)) { Owner = Window.GetWindow(this) };
                    if (dlg.ShowDialog() != true) return;
                    TimelineHelper.ApplyChordChoice(pg, key, prev.Degree >= 0, dlg);
                }
            }
            AppendChord(pg);
            Engine.Flow.ChordDegrees.Revoice(selectedTrack);
            Render();
        }

        // A fresh chord module inheriting a source chord's style/voicing params (auto voice-leading on). The logic lives
        // in the shared Engine.Timeline.ChordModelOps; used here by "Insérer ▸ Accords" and the chord context menu.
        PatternGeneratorModule NewChordLike(PatternGeneratorModule prev)
            => Engine.Timeline.ChordModelOps.NewChordLike(project, prev, TimelineHelper.RulerBeatsPerBar(project));

        // ---- AI arrangement (Mistral) ----
        /// <summary>Raised by the "Composer avec l'IA" menu — the shell opens the dialog and lays the result on a NEW tab.</summary>
        public event Action ComposeInNewTabRequested;
        void btnAiCompose_Click(object sender, RoutedEventArgs e) => ComposeInNewTabRequested?.Invoke();

        /// <summary>Raised by the toolbar "Enregistrer" button — the shell handles the save (file dialog + recent + title).</summary>
        public event Action SaveRequested;
        void btnSaveMusic_Click(object sender, RoutedEventArgs e) => SaveRequested?.Invoke();


        // Right-click on a timeline box → a small context menu. Chord boxes get "Proposer la suite…" (the context-aware
        // diagram, inserting the choice right AFTER this chord).
        void ShowItemContextMenu(TimelineTrack track, TimelineItem item, FrameworkElement anchor)
        {
            if (item == null) return;
            var menu = new ContextMenu();
            bool isChord = item.Module is PatternGeneratorModule || item.Module is CadenceModule;
            if (isChord)
            {
                var mi = new MenuItem { Header = "🎼 Proposer la suite…" };
                mi.Click += (s, e) => SuggestNextAfter(track, item);
                menu.Items.Add(mi);
                var chain = new MenuItem { Header = "🎼 Enchaîner 4 mesures" };
                chain.Click += (s, e) => ChainProgression(track, item, 4);
                menu.Items.Add(chain);
                menu.Items.Add(new Separator());
            }
            if (item.Module is PlayRiffModule prm)
            {
                var vary = new MenuItem { Header = "🤖 Varier le thème avec l'IA…" };
                vary.Click += (s, e) =>
                {
                    if (TimelineHelper.VaryThemeWithAi(Window.GetWindow(this), project, track, item))
                    {
                        selectedTrack = null; selectedItem = null; Render();
                    }
                };
                menu.Items.Add(vary);
                var toDrum = new MenuItem { Header = "🥁 Convertir en batterie", ToolTip = "Remplace ce riff par un module batterie au rythme personnalisé : reprend les notes et durées telles quelles (chaque hauteur → sa percussion GM)." };
                toDrum.Click += (s, e) => ConvertRiffToDrums(track, item, prm);
                menu.Items.Add(toDrum);
                menu.Items.Add(new Separator());
            }
            if (item.Module is MelodicLineModule mlm)
            {
                var toRiff = new MenuItem { Header = "🎵 Convertir en notes éditables", ToolTip = "Fige les hauteurs que le moteur a choisies sur les accords en un riff normal : chaque note devient éditable individuellement (la ligne rythme-seule est remplacée à la même position)." };
                toRiff.Click += (s, e) => ConvertMelodicLineToRiff(track, item, mlm);
                menu.Items.Add(toRiff);
                menu.Items.Add(new Separator());
            }
            var del = new MenuItem { Header = "🗑 Supprimer" };
            del.Click += (s, e) => DeleteItem(track, item);
            menu.Items.Add(del);
            menu.PlacementTarget = anchor; menu.IsOpen = true;
        }

        // "Convertir en batterie": swap a Play-riff module for a DRUM module carrying the SAME rhythm — each note's
        // start/length is kept as-is and its pitch is read as a GM percussion key (Note+12 → lane, so a drum-content
        // riff round-trips exactly). The item keeps its position and length. For the drum SOUND the item should sit
        // on a batterie (Drum) track — otherwise it plays the percussion rows on this track's instrument.
        void ConvertRiffToDrums(TimelineTrack track, TimelineItem item, PlayRiffModule prm)
        {
            CommitRiffEditor();
            TimelineHelper.ConvertRiffToDrums(track, item, prm);
            Render();

            if (track.Type != TimelineTrackType.Drum)
                MessageBox.Show("Converti en module batterie. Pour entendre les percussions, place-le sur une piste Batterie (sinon il joue avec l'instrument de cette piste).",
                                "Convertir en batterie", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        // "Convertir en notes éditables": FREEZE a rhythm-only melodic line into a normal, editable riff. The engine
        // resolves the pitches it would play (the same line shown in the box thumbnail and heard at playback — chord
        // tones on the beats, passing tones between, at this item's position in the progression), and we swap the
        // MelodicLineModule for a PlayRiffModule carrying those exact notes. Same track/position; any multiple voices
        // collapse into the one riff (each note on its pitch row). Replacing in place mirrors "Convertir en batterie"
        // and is the least-confusing option here: a track holds items sequentially, so a parallel riff would either
        // shift the timeline or double the audio on a second track.
        void ConvertMelodicLineToRiff(TimelineTrack track, TimelineItem item, MelodicLineModule ml)
        {
            CommitRiffEditor();
            TimelineHelper.ConvertMelodicLineToRiff(project,track, item, ml);
            Render();
            // The module was swapped IN PLACE (same TimelineItem stays selected), so the bottom editor still shows
            // the ligne-mélodique editor. Rebuild it for the new module so the riff editor replaces it (issue #4).
            if (selectedItem == item && !ScoreVisible) OpenModuleEditor(track, item);
        }

        // Open the context-aware diagram for the chord `item` and insert the chosen chord right AFTER it.
        void SuggestNextAfter(TimelineTrack track, TimelineItem item)
        {
            CommitRiffEditor();
            var prev = item.Module as PatternGeneratorModule;
            var key = project.Key ?? new Engine.Score.KeySignature();
            ChordContext(track, item, out int[] prevDegs, out int barIdx, out int phraseLen);
            if (prevDegs.Length == 0 || prevDegs[prevDegs.Length - 1] < 0)
            { MessageBox.Show("Cet accord n'est pas sur un degré de la tonalité — pas de suggestion.", "Suite d'accord", MessageBoxButton.OK, MessageBoxImage.Information); return; }
            var dlg = new Dialogs.ChordSuggestionDialog(prevDegs, barIdx, phraseLen, key, InstrumentCatalog.GetPreset(track.Instrument)) { Owner = Window.GetWindow(this) };
            if (dlg.ShowDialog() != true) return;
            var pg = NewChordLike(prev);
            TimelineHelper.ApplyChordChoice(pg, key, prev == null || prev.Degree >= 0, dlg);
            SelectItem(track, TimelineHelper.InsertChordAfter(track, item, pg));
            Engine.Flow.ChordDegrees.Revoice(track);
            Render();
        }

        // "Enchaîner N mesures": append the top-ranked continuation `bars` times, re-reading the context each step.
        void ChainProgression(TimelineTrack track, TimelineItem item, int bars)
        {
            CommitRiffEditor();
            var key = project.Key ?? new Engine.Score.KeySignature();
            var after = item;
            var prevPg = item.Module as PatternGeneratorModule;
            for (int n = 0; n < Math.Max(1, bars); n++)
            {
                ChordContext(track, after, out int[] prevDegs, out int barIdx, out int phraseLen);
                if (prevDegs.Length == 0 || prevDegs[prevDegs.Length - 1] < 0) break;
                var ranked = Engine.Flow.HarmonySuggest.Rank(prevDegs, barIdx, phraseLen, Engine.Flow.HarmonyMood.Auto, key);
                if (ranked.Count == 0) break;
                var top = ranked[0];
                var pg = NewChordLike(prevPg);
                if (top.Deg >= 0)
                {
                    var ch = Engine.Flow.MusicTheory.DiatonicChord(key, top.Deg, top.SuggestColour);
                    pg.Root = ch.root; pg.Quality = ch.quality; pg.DiatonicColour = top.SuggestColour; pg.Suspension = 0; pg.ModeOverride = 0;
                    pg.Degree = (prevPg == null || prevPg.Degree >= 0) ? top.Deg : -1;
                }
                else { pg.Root = (((MusicTheory.TonicPc(key) + top.RootOff) % 12) + 12) % 12; pg.Quality = top.Quality; pg.Degree = -1; pg.DiatonicColour = 0; pg.Suspension = 0; pg.ModeOverride = 0; }
                SelectItem(track, TimelineHelper.InsertChordAfter(track, after, pg));
                after = track.Items[track.Items.IndexOf(after) + 1];
                prevPg = pg;
            }
            Engine.Flow.ChordDegrees.Revoice(track);
            Render();
        }

        // The recent chord degrees on `track` up to and INCLUDING `upTo`, the bar index where the NEXT chord lands, and a
        // phrase length (default 4-bar hypermetre) for cadence proximity. Degrees = locked Degree, else detected from the
        // root; -1 for a chromatic root. Feeds the context-aware suggestion ranking (last 2-3 kept).
        void ChordContext(TimelineTrack track, TimelineItem upTo, out int[] prevDegrees, out int barIndex, out int phraseLen)
        {
            var key = project.Key ?? new Engine.Score.KeySignature();
            var degs = new System.Collections.Generic.List<int>();
            double beats = 0; int bpb = Math.Max(1, TimelineHelper.RulerBeatsPerBar(project));
            if (track?.Items != null)
                foreach (var it in track.Items)
                {
                    if (it == null) continue;
                    if (it.Module is PatternGeneratorModule pgm)
                        degs.Add(pgm.Degree >= 0 ? pgm.Degree : Engine.Flow.MusicTheory.DegreeOf(key, ((pgm.Root % 12) + 12) % 12));
                    else if (it.Module is CadenceModule cm && cm.Chords != null && cm.Chords.Count > 0)
                    { var lc = cm.Chords[cm.Chords.Count - 1]; degs.Add(lc.Degree >= 0 ? lc.Degree : Engine.Flow.MusicTheory.DegreeOf(key, ((lc.Root % 12) + 12) % 12)); }
                    beats += it.SilenceBefore +  ModuleDuration.Beats(it.Module, TimelineHelper.RiffById);
                    if (ReferenceEquals(it, upTo)) break;
                }
            int take = Math.Min(3, degs.Count);
            prevDegrees = take > 0 ? degs.GetRange(degs.Count - take, take).ToArray() : new int[0];
            barIndex = (int)(beats / bpb + 1e-6);
            phraseLen = 4;
        }
        private void btnAddDrum_Click(object sender, RoutedEventArgs e) => AppendModule(new DrumPatternModule());

        // "Insérer ▸ Ligne mélodique (rythme)" : add a MelodicLineModule on a dedicated "ligne mélodique" track (created
        // once). Re-adding copies the previous line's rhythm right after it (the pitches recompute on the new chords).

        void btnInsertMelodicLine_Click(object sender, RoutedEventArgs e)
        {

            CommitRiffEditor();
            TimelineTrack track; TimelineItem item; MelodicLineModule ml;
            TimelineHelper.InsertMelodicLine(project, out track, out item, out ml);
            selectedTrack = track; selectedItem = item;
            Render();
            editorHost.Content = BuildMelodicLineEditor(track, item, ml);   // open its editor
            txtEditorTitle.Text = "Éditeur — Ligne mélodique";
        }

        UIElement BuildMelodicLineEditor(TimelineTrack track, TimelineItem item, MelodicLineModule ml)
        {
            double startBeat = TimelineHelper.ItemStartBeat(track, item);
            var grid = TwoColumns(out StackPanel left, out ContentControl host);
            Action refresh = null;
            refresh = () => { RefreshMelodicLineGrid(host, track, item, ml, startBeat); Render(); };

            left.Children.Add(EdLabel("Voix"));
            left.Children.Add(ParamCombo(TimelineHelper.MelodicVoiceNames, Math.Max(0, Math.Min(2, ml.VoiceCount - 1)), v => ml.VoiceCount = v + 1, refresh));
            left.Children.Add(EdLabel("Nombre de temps (durée de la ligne)"));
            left.Children.Add(ParamNum(ml.BeatsPerBar, v => ml.BeatsPerBar = Math.Max(1, v), refresh));
            left.Children.Add(EdLabel("Contour (algorithme de choix des notes)"));
            left.Children.Add(ParamCombo(Engine.Timeline.MelodicLineEngine.ContourNames, Math.Max(0, Math.Min(Engine.Timeline.MelodicLineEngine.ContourNames.Length - 1, ml.Contour)), v => ml.Contour = v, refresh));
            left.Children.Add(EdLabel("Ancrage (note de départ de la ligne)"));
            left.Children.Add(ParamCombo(Engine.Timeline.MelodicLineEngine.AnchorNames, Math.Max(0, Math.Min(Engine.Timeline.MelodicLineEngine.AnchorNames.Length - 1, ml.Anchor)), v => ml.Anchor = v, refresh));
            left.Children.Add(EdLabel("Continuité (lissage voice-leading, 0-100)"));
            left.Children.Add(ParamNum(ml.Continuity, v => ml.Continuity = Math.Max(0, Math.Min(100, v)), refresh));
            left.Children.Add(EdLabel("Variation (transformation du motif)"));
            left.Children.Add(ParamCombo(Engine.Timeline.MelodicLineEngine.VariationNames, Math.Max(0, Math.Min(Engine.Timeline.MelodicLineEngine.VariationNames.Length - 1, ml.Variation)), v => ml.Variation = v, refresh));
            left.Children.Add(EdLabel("Tension (pente de registre, ± demi-tons)"));
            left.Children.Add(ParamNum(ml.TensionSlope, v => ml.TensionSlope = v, refresh));
            left.Children.Add(EdLabel("Amplitude (tessiture ± demi-tons, 2-24)"));
            left.Children.Add(ParamNum(ml.Amplitude, v => ml.Amplitude = Math.Max(2, Math.Min(24, v)), refresh));
            left.Children.Add(EdLabel("Ornementation (retards/appoggiatures, 0-100)"));
            left.Children.Add(ParamNum(ml.Ornaments, v => ml.Ornaments = Math.Max(0, Math.Min(100, v)), refresh));
            left.Children.Add(EdLabel("Vague : notes par arc (0 = auto ; petit = ondule vite)"));
            left.Children.Add(ParamNum(ml.WaveLength, v => ml.WaveLength = Math.Max(0, Math.Min(32, v)), refresh));
            var preserve = new CheckBox { Content = "Préserver (non écrasé par « Appliquer »)", Foreground = Brushes.White, FontSize = 11, Margin = new Thickness(0, 8, 0, 0), IsChecked = ml.Preserve };
            preserve.Checked += (s, e) => ml.Preserve = true; preserve.Unchecked += (s, e) => ml.Preserve = false;
            left.Children.Add(preserve);

            // Motif picker (always shown): "Personnalisé…" (custom, no name) OR a saved motif. Picking a saved motif loads it.
            var savedLines = project.UserMelodicLines ?? (project.UserMelodicLines = new System.Collections.Generic.List<UserChordStyle>());
            left.Children.Add(EdLabel("Motif"));
            var cbMotif = new ComboBox { Margin = new Thickness(0, 2, 0, 0), MinWidth = 170, MaxWidth = 260, HorizontalAlignment = HorizontalAlignment.Left };
            foreach (var u in savedLines) cbMotif.Items.Add(u.Name);
            cbMotif.Items.Add("Personnalisé…");
            int customIdx = savedLines.Count;
            int selIdx = savedLines.FindIndex(u => u.Name == ml.LineName);
            cbMotif.SelectedIndex = selIdx >= 0 ? selIdx : customIdx;
            left.Children.Add(cbMotif);
            cbMotif.SelectionChanged += (s, e) =>
            {
                int i = cbMotif.SelectedIndex;
                if (i < 0) return;
                if (i == customIdx) { if (!string.IsNullOrEmpty(ml.LineName)) { ml.LineName = null; editorHost.Content = BuildMelodicLineEditor(track, item, ml); } return; }
                if (i >= savedLines.Count || savedLines[i].Name == ml.LineName) return; // no change
                TimelineHelper.ApplyExistingLine(ml, savedLines[i]);
                editorHost.Content = BuildMelodicLineEditor(track, item, ml); // reload on the chosen motif
                Render(); RefreshScore();
            };
            // Propagate the current motif to every line sharing the same saved-motif selection (disabled for "Personnalisé…").
            var btnApply = new Button
            {
                Content = new TextBlock { Text = "Appliquer le motif à ceux du même nom", TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center },
                Margin = new Thickness(0, 4, 0, 0), Padding = new Thickness(10, 4, 10, 4),
                HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center,
                Cursor = Cursors.Hand, IsEnabled = !string.IsNullOrEmpty(ml.LineName),
            };
            btnApply.Click += (s, e) => { if (!string.IsNullOrEmpty(ml.LineName)) { TimelineHelper.PropagateMelodicLine(project,ml.LineName, ml); Render(); RefreshScore(); } };
            left.Children.Add(btnApply);
            left.Children.Add(new TextBlock { Text = "Dessine seulement le RYTHME (une ligne par voix). Le moteur choisit les notes selon l'accord en cours (grille d'arrangement en structure, sinon la piste d'accords). Notes d'accord sur les temps forts, passages sur les faibles. « Enregistrer » (sous la grille) sauve le motif sous un nom.", Foreground = "#888888".ToBrush(), FontSize = 10, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), MaxWidth = 260 });

            RefreshMelodicLineGrid(host, track, item, ml, startBeat);
            return grid;
        }

        void RefreshMelodicLineGrid(ContentControl host, TimelineTrack track, TimelineItem item, MelodicLineModule ml, double startBeat)
        {
            int voices = Math.Max(1, Math.Min(MelodicLineModule.MaxVoices, ml.VoiceCount));
            var labels = new string[voices];
            for (int i = 0; i < voices; i++) labels[i] = "Voix " + (i + 1);
            var lines = project.UserMelodicLines ?? (project.UserMelodicLines = new System.Collections.Generic.List<UserChordStyle>());
            var rg = new Controls.RhythmGridControl();
            Func<SequencerSlice[], int, Riff> mk = (gr, gs) =>
            {
                var t = new MelodicLineModule { BeatsPerBar = ml.BeatsPerBar, VoiceCount = ml.VoiceCount };
                t.SetNotes(rg.CurrentNotes(), gs, rg.Beats * gs);
                return Engine.Timeline.MelodicLineEngine.GenerateLine(t, project, TimelineHelper.RiffById, project.Key, startBeat);
            };
            // The motif picker + "Appliquer" live in the LEFT panel now, so the grid keeps only "Enregistrer" (save-as).
            Action onSaveStyle = () =>
            {
                string name = TimelineHelper.PromptText("Enregistrer le motif mélodique", string.IsNullOrEmpty(ml.LineName) ? "Ma ligne" : ml.LineName);
                if (string.IsNullOrWhiteSpace(name)) return;
                name = name.Trim();
                var entry = new UserChordStyle { Name = name, Slices = rg.CurrentGrid(), Spb = rg.Spb, Beats = rg.Beats, Notes = rg.CurrentNotes() };
                int ex = lines.FindIndex(u => u.Name == name);
                if (ex >= 0) lines[ex] = entry; else lines.Add(entry);
                ml.LineName = name;
                editorHost.Content = BuildMelodicLineEditor(track, item, ml);   // rebuild (picker + selection)
            };
            rg.Configure(labels, ml.BeatsPerBar, ml.SlicesPerQuarter > 0 ? ml.SlicesPerQuarter : 4, ml.Slices, new string[0], (st, b) => null,
                PatternGenerator.SlicesPerQuarter, mk, InstrumentCatalog.GetPreset(track.Instrument), onSaveStyle: onSaveStyle, noteList: true, existingNotes: ml.Notes);
            bool dirty = false;
            rg.GridChanged += () => { ml.SetNotes(rg.CurrentNotes(), rg.Spb, rg.Beats * rg.Spb); ml.BeatsPerBar = Math.Max(1, rg.Beats); dirty = true; };
            rg.Unloaded += (s, e) => { if (dirty) { dirty = false; Render(); } };
            host.Content = rg;
        }

       

        // ================= Procedural theme / variation (Insérer → Thème / Variation) =================
        // 100 %-procedural (serial + algorithmic, Engine.Compose.ProceduralComposer). In a structure, replaces the
        // section under the selection reusing its chord degrees; otherwise a new riff on the active track (+ a chord
        // source is reused if one exists, else a verticalized accompaniment is created).

        // "🎵 Thème…": generate a procedural theme.
        private void btnGenerateTheme_Click(object sender, RoutedEventArgs e)
        {
            if (selectedTrack == null) { MessageBox.Show("Sélectionne d'abord une piste."); return; }
            CommitRiffEditor();
            if (TimelineHelper.GenerateTheme(Window.GetWindow(this), project, selectedItem,selectedTrack,scoreTracks))
            {
                Render();
                RefreshScore();
            }
        }

        // "🔀 Variation…": apply a variation technique to the selected theme riff.
        private void btnVariation_Click(object sender, RoutedEventArgs e)
        {
            var pr = selectedItem != null ? selectedItem.Module as PlayRiffModule : null;
            var src = pr != null ? TimelineHelper.RiffById(pr.RiffId) : null;
            if (src == null || src.Notes == null || src.Notes.Count == 0)
            {
                MessageBox.Show("Sélectionne d'abord un riff (le thème) à faire varier.", "Variation", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            CommitRiffEditor();
            TimelineItem timelineItem;
            if (TimelineHelper.VariateTheme(Window.GetWindow(this), project, src, pr, selectedTrack,scoreTracks,out timelineItem))
            {
                if (timelineItem != null)
                {
                    SelectItem(selectedTrack, timelineItem);
                }
                Render();
                RefreshScore();
            }
            
        }

        // Export the whole timeline to WAV/MP3 (renders a fresh TimelinePlayer offline via WaveExporter).
        private void btnExport_Click(object sender, RoutedEventArgs e)
        {
            StopPlayback();
            if (project.Tracks.Count == 0) { MessageBox.Show("Aucune piste à exporter."); return; }

            var sfd = new Dialogs.FileBrowserDialog
            {
                SaveMode = true,
                Owner = Window.GetWindow(this),
                Filter = "WAVE (*.wav)|*.wav|MP3 (*.mp3)|*.mp3|Tous les fichiers (*.*)|*.*",
                DefaultExt = ".wav",
            };
            if (!SoundFontGuard.EnsureReady(Window.GetWindow(this), "Export")) return;
            if (sfd.ShowDialog() != true) return;

            string path = sfd.FileName;
            bool mp3 = string.Equals(System.IO.Path.GetExtension(path), ".mp3", StringComparison.OrdinalIgnoreCase);
            var p = new Engine.Timeline.TimelinePlayer(project, TimelineHelper.RiffById, AudioFormat.SampleRate);
            long cap = p.EstimatedTotalSamples + 5L * AudioFormat.SampleRate; // + a few seconds of ring-out tail

            var dlg = new ExportProgressDialog((progress, token) =>
                Engine.WaveExporter.RenderProvider(path, mp3, p, cap, AudioFormat.SampleRate, progress, token, p.Start, p.Stop))
            {
                Owner = Window.GetWindow(this),
            };
            dlg.ShowDialog();

            if (!string.IsNullOrEmpty(dlg.Error)) MessageBox.Show("Export error : " + dlg.Error);
            else if (dlg.Success) MessageBox.Show("Export terminé :\n" + path);
        }

        // Import a MIDI / MuseScore file into the timeline (one track per staff, riffs/drum patterns,
        // repeats for identical drum bars, dynamics -> base volume + automation). Replaces the content.
        private void btnImport_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Dialogs.FileBrowserDialog
            {
                Owner = Window.GetWindow(this),
                Filter = "MIDI / MuseScore (*.mid;*.midi;*.mscz;*.mscx)|*.mid;*.midi;*.mscz;*.mscx|"
                       + "MIDI (*.mid;*.midi)|*.mid;*.midi|MuseScore (*.mscz;*.mscx)|*.mscz;*.mscx|Tous les fichiers (*.*)|*.*",
            };
            if (dlg.ShowDialog() != true) return;
            this.ImportFile(dlg.FileName);
        }

        public async void ImportFile(string path)
        {
            var opt = new ImportGraphOptionsDialog { Owner = Window.GetWindow(this) };
            opt.HideVolumeOption(); // timeline always imports dynamics as automation points (no riff split)
            if (opt.ShowDialog() != true) return;
            int mpr = opt.MeasuresPerRiff, spb = opt.SlicesPerBeat;
            bool importVolume = opt.ImportVolume;

            string ext = (System.IO.Path.GetExtension(path) ?? "").ToLowerInvariant();
            bool midi = ext == ".mid" || ext == ".midi";

            var prog = new ImportProgressDialog { Owner = Window.GetWindow(this) };
            prog.Show();
            try
            {
                prog.Set(0.05, "Lecture du fichier…");
                var result = await System.Threading.Tasks.Task.Run(() =>
                {
                    var score = midi ? MidiImporter.Load(path) : MuseScoreImporter.Load(path);
                    return TimelineImporter.Build(score, mpr, spb, importVolume);
                });

                if (result.Project.Tracks.Count == 0) { MessageBox.Show("Aucune piste trouvée dans le fichier."); return; }

                // Confirm/correct the deduced key + time signature before applying. A declared 4/4 is the MIDI
                // default → ask (suggest the detected meter, e.g. 12/8 for a ternary rhythm).
                var detectedKey = result.Project.Key ?? new Engine.Score.KeySignature();
                string meterHint = result.MeterUncertain
                    ? (result.Ternary ? "4/4 par défaut + rythme ternaire détecté → 6/8 suggéré." : "4/4 par défaut dans le fichier — vérifiez la mesure.")
                    : "";
                prog.Hide();
                var keyDlg = new KeySignatureDialog(detectedKey, result.Project.TimeSigNum, result.Project.TimeSigDen, meterHint) { Owner = Window.GetWindow(this) };
                bool ok = keyDlg.ShowDialog() == true;
                var chosenKey = ok ? keyDlg.Result : detectedKey;
                int chosenNum = ok ? keyDlg.ResultNum : result.Project.TimeSigNum;
                int chosenDen = ok ? keyDlg.ResultDen : result.Project.TimeSigDen;
                prog.Show();

                prog.Set(0.85, "Construction du séquenceur…");
                foreach (var r in result.Riffs) RiffLibrary.Instance.Riffs.Add(r);
                project.Tempo = result.Project.Tempo;
                project.Key = chosenKey; // detected concert key, confirmed/corrected by the user
                project.TimeSigNum = chosenNum; project.TimeSigDen = chosenDen;
                // ×1.5 display scale only for a compound x/8 that came from a ternary (triplet) reinterpretation.
                project.TimeSigScale = (chosenDen == 8 && chosenNum % 3 == 0 && result.Ternary) ? 1.5 : 1.0;
                project.Tracks.Clear();
                foreach (var t in result.Project.Tracks) project.Tracks.Add(t);
                scoreTracks.Clear(); activeScore = null;
                selectedTrack = project.Tracks.Count > 0 ? project.Tracks[0] : null;
                selectedItem = null;
                editorHost.Content = null;
                txtBpm.Text = ((int)project.MainBpm).ToString();
                await RenderBatched(prog); // add the lane controls in batches so the UI stays responsive
                prog.Set(1.0, "Terminé");
            }
            catch (Exception ex) { MessageBox.Show("Erreur d'import : " + ex.Message); }
            finally { prog.Close(); }
        }

        // Export the whole timeline to a Standard MIDI File.
        private void btnExportMidi_Click(object sender, RoutedEventArgs e)
        {
            if (project.Tracks.Count == 0) { MessageBox.Show("Aucune piste à exporter."); return; }
            var sfd = new Dialogs.FileBrowserDialog { SaveMode = true, Owner = Window.GetWindow(this), Filter = "MIDI (*.mid)|*.mid", DefaultExt = ".mid" };
            if (!string.IsNullOrEmpty(CurrentPath)) sfd.FileName = System.IO.Path.GetFileNameWithoutExtension(CurrentPath);
            if (sfd.ShowDialog() != true) return;
            try
            {
                Engine.Timeline.MidiTimelineExporter.Export(sfd.FileName, project, TimelineHelper.RiffById);
                MessageBox.Show("Export MIDI terminé :\n" + sfd.FileName);
            }
            catch (Exception ex) { MessageBox.Show("Erreur d'export MIDI : " + ex.Message); }
        }

        // Export the score to a native MuseScore .mscx file (the checked ♫ tracks, else all instrument tracks;
        // drums skipped). One staff per part, with its clef + key + time signature.
        private void btnExportMuseScore_Click(object sender, RoutedEventArgs e)
        {
            var src = new System.Collections.Generic.List<TimelineTrack>();
            foreach (var t in project.Tracks) if (scoreTracks.Contains(t)) src.Add(t);
            if (src.Count == 0) foreach (var t in project.Tracks) if (t.Type != TimelineTrackType.Drum) src.Add(t);

            var parts = new System.Collections.Generic.List<Engine.Timeline.MuseScoreExporter.Part>();
            foreach (var t in src)
            {
                if (t.Type == TimelineTrackType.Drum) continue; // percussion needs a drum staff — not exported yet
                parts.Add(new Engine.Timeline.MuseScoreExporter.Part { Name = t.Name, Program = t.Instrument, Score = Engine.Score.ScoreBuilder.Build(project, t, TimelineHelper.RiffById) });
            }
            if (parts.Count == 0) { MessageBox.Show("Aucune piste mélodique à exporter (coche une piste ♫ ou ajoute une piste instrument)."); return; }

            string title = string.IsNullOrEmpty(CurrentPath) ? "Partition" : System.IO.Path.GetFileNameWithoutExtension(CurrentPath).Replace('_', ' ');
            var sfd = new Dialogs.FileBrowserDialog { SaveMode = true, Owner = Window.GetWindow(this), Filter = "MuseScore (*.mscx)|*.mscx", DefaultExt = ".mscx", FileName = title };
            if (sfd.ShowDialog() != true) return;
            try
            {
                Engine.Timeline.MuseScoreExporter.Export(sfd.FileName, parts, project.TimeSigNum, project.TimeSigDen, title);
                MessageBox.Show("Export MuseScore terminé :\n" + sfd.FileName);
            }
            catch (Exception ex) { MessageBox.Show("Erreur d'export MuseScore : " + ex.Message); }
        }

        // Export the checked (♫) tracks as an A4 score, broken into lines of 2/4/8/16 measures, printed to PDF.
        private void btnExportPdf_Click(object sender, RoutedEventArgs e)
        {
            var list = new System.Collections.Generic.List<Engine.Score.TrackScore>();
            foreach (var t in project.Tracks) if (scoreTracks.Contains(t)) list.Add(Engine.Score.ScoreBuilder.Build(project, t, TimelineHelper.RiffById));
            if (list.Count == 0) { MessageBox.Show("Coche au moins une piste (♫) pour l'exporter en partition."); return; }

            // Title: the file name (no extension, '_' → space); fallback when unsaved.
            string title = string.IsNullOrEmpty(CurrentPath) ? "Partition" : System.IO.Path.GetFileNameWithoutExtension(CurrentPath).Replace('_', ' ');

            var doc = Controls.Score.ScorePdfExporter.Build(list, project.TimeSigNum, project.TimeSigDen, project.TimeSigScale > 0 ? project.TimeSigScale : 1.0, title);
            // Preview window: a DocumentViewer (zoom + scroll + its own Print button → "Microsoft Print to PDF").
            var viewer = new System.Windows.Controls.DocumentViewer { Document = doc };
            var win = new Window
            {
                Title = "Partition — " + title + "  (zoom + défilement ; bouton Imprimer pour le PDF)",
                Content = viewer,
                Width = 900,
                Height = 1000,
                Owner = Window.GetWindow(this),
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
            };
            win.Show();
        }

        private void txtBpm_LostFocus(object sender, RoutedEventArgs e)
        {
            if (double.TryParse(txtBpm.Text, out double v) && v > 0 && project.Tempo.Count > 0)
            {
                project.Tempo[0].Bpm = v;
                Render();
            }
        }

        // Wheel over the track headers: scroll the LANES instead (they then sync the headers back), so the two
        // halves never drift apart vertically.
        private void laneScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            headerScroll.ScrollToVerticalOffset(laneScroll.VerticalOffset);
            // headerOffset.Y = -laneScroll.VerticalOffset;
            rulerScroll.ScrollToHorizontalOffset(laneScroll.HorizontalOffset);   // keep the measure ruler aligned
            chordScroll?.ScrollToHorizontalOffset(laneScroll.HorizontalOffset);  // keep the docked chords lane aligned
        }
    }

    static class BrushExt
    {
        public static SolidColorBrush ToBrush(this string hex)
            => (SolidColorBrush)new BrushConverter().ConvertFromString(hex);
    }

    public class SyncScrollViewer:ScrollViewer
    {
        protected override void OnMouseWheel(MouseWheelEventArgs e)
        {
            // On ne fait RIEN : pas de scroll, et surtout on ne met PAS e.Handled = true.
            // → l'event continue de remonter (bubbling) normalement.
            // ScrollToVerticalOffset() reste fonctionnel pour la synchro avec laneScroll.
        }
    }
}

