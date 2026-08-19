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
using MusicTracker.Localization;
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
        const double MarkerLaneH = 18; // the section-marker band, above the 20px ruler (both inside rulerScroll)
        const double BasePxPerBeat = 60; // REFERENCE scale = 100 % zoom (a 4/4 measure ≈ 240 px); RiffThumbnail renders at this value

        // ---- horizontal zoom (display only: never written to the .sq, never in the undo history) ----
        /// <summary>Zoom steps, as factors of <see cref="BasePxPerBeat"/>. 100 % = index 6 = the historical display.</summary>
        static readonly double[] ZoomLevels = { 0.10, 0.15, 0.25, 0.35, 0.50, 0.75, 1.00, 1.50, 2.00, 3.00, 4.00 };
        const int ZoomDefaultIdx = 6;   // 100 %
        const double MaxLaneWidth = 1_000_000; // WPF canvas-width guard: past this the "+" button disables instead
        int zoomIdx = ZoomDefaultIdx;   // UI state: per tab, NOT serialized and NOT undoable
        int pendingZoomIdx = -1;        // step awaiting the debounced re-render (-1 = none)
        double zoomAnchorBeat;          // musical time to keep under the anchor point
        double zoomAnchorViewX;         // its position, in px, inside laneScroll's viewport
        System.Windows.Threading.DispatcherTimer zoomTimer; // 50 ms debounce: one Render per wheel burst
        double Zoom => ZoomLevels[Math.Max(0, Math.Min(ZoomLevels.Length - 1, zoomIdx))];

        /// <summary>Pixels per beat AT THE CURRENT ZOOM. Single point of the feature: every timeline geometry
        /// (ruler, marker band, boxes, tempo/volume lanes, chord trame, cursor & handles, px→beat conversions)
        /// already goes through this symbol, so the whole editor scales without touching those call sites.</summary>
        double PxPerBeat => BasePxPerBeat * Zoom;

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
        /// <summary>Le player en cours de lecture — null si arrêté. Exposé pour que le mixeur puisse
        /// interroger les peaks des vu-mètres. Lecture seule côté externe : la vie du player est gérée
        /// exclusivement par Start/Pause/Stop de cet écran.</summary>
        public Engine.Timeline.TimelinePlayer CurrentPlayer => player;

        /// <summary>Capture un snapshot d'annulation — appelé par le mixeur après un ajout/retrait d'effet
        /// d'insert. Édition des paramètres d'un effet non captée en v1 (cohérent avec le reste des
        /// dialogues qui n'ajoutent pas de snapshot par changement de slider).</summary>
        public void CaptureUndoFromMixer() { try { PushUndo("mixer:inserts"); } catch { } }
        Engine.Timeline.LookaheadBuffer playBuffer; // background pre-render between the player and the device
        System.Windows.Threading.DispatcherTimer playTimer;
        System.Windows.Shapes.Rectangle playCursor;
        System.Windows.Shapes.Polygon startMarker; // blue down-pointing handle on the ruler: drag to set the play start
        bool draggingMarker;
        double startBeat;                           // cursor position = where playback starts/resumes (beats) = loop point A
        System.Windows.Shapes.Polygon loopMarker;  // orange handle: the A-B loop END (B); shown only when looping
        bool draggingLoop;
        bool loopEnabled;                           // ⟳ toggle: loop the [startBeat, loopEndBeat] region seamlessly
        double loopEndBeat;                         // B in beats (0 = unset → defaults to A + 4 bars when enabled)
        Dialogs.MixerDialog mixerWindow;            // non-modal per-track mixer (volume/pan/mute/solo), live during playback

        // ---- undo/redo (snapshot-based; see Engine.Timeline.UndoManager) ----
        readonly Engine.Timeline.UndoManager undoMgr = new Engine.Timeline.UndoManager(50);
        string pendingUndo;      // pre-edit snapshot captured when an editor opened; flushed on leave IF the state changed
        string pendingUndoKey;   // its op key ("edit:<id>")
        bool restoringUndo;      // guard: don't snapshot/clear history while applying an undo/redo restore
        // Stable per-object identity for op keys (so insert:X + delete:X can neutralize, and moves of X coalesce).
        static string Id(object o) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o).ToString();

        string markerDragPre;    // undo pre-state captured when a marker drag passes the threshold

        public TimelineScreen()
        {
            InitializeComponent();
            foreach (var t in new[] { Loc.T("Do"), Loc.T("Re"), Loc.T("Mi"), Loc.T("Fa"), Loc.T("Sol"), Loc.T("La"), Loc.T("Si") }) cboTonic.Items.Add(t);
            foreach (var m in Engine.Score.MusicalMode.Names) cboMode.Items.Add(m); // all modes (like the Transpose dialog)
            // Start with one instrument track + the permanent chords track (always at the bottom).
            project.Tracks.Add(new TimelineTrack { Name = "Piste 1", Instrument = 0 });
            TimelineHelper.EnsureChordTrack(project);
            selectedTrack = project.Tracks[0];
            SetBpmText();

            // laneScroll shows a vertical scrollbar (always), so its viewport is one scrollbar-width narrower than
            // the ruler's and the docked chords lane (which have none). That makes laneScroll scrollable that much
            // FURTHER, so at the far right the ruler/chords froze a scrollbar-width short of the lanes. Reserve the
            // same right gutter on those two so all three viewports (hence scroll offsets) match 1:1 everywhere.
            const double sbW = 18; // theme's vertical ScrollBar width (Theme/ScrollBar2.xaml)
            rulerScroll.Margin = new Thickness(rulerScroll.Margin.Left, rulerScroll.Margin.Top, sbW, rulerScroll.Margin.Bottom);
            if (chordScroll != null) chordScroll.Margin = new Thickness(chordScroll.Margin.Left, chordScroll.Margin.Top, sbW, chordScroll.Margin.Bottom);

            // Section markers: wire the band's gestures ONCE (markerLane is a single instance created by
            // InitializeComponent). Wiring this from Render() would stack one handler per render — ten renders
            // later a double-click would open ten dialogs.
            if (markerLane != null)
            {
                markerLane.CreateRequested += MarkerCreateAt;
                markerLane.MarkerClicked += MarkerGoTo;
                markerLane.MarkerDoubleClicked += MarkerRename;
                markerLane.DragStarted += m => markerDragPre = BeginUndo();
                markerLane.MarkerDropped += MarkerDrop;
                markerLane.ContextRequested += ShowMarkerContextMenu;
            }

            // Horizontal zoom: start at the level the APP remembers (per-tab from then on). An unknown/corrupt
            // settings value falls back to 100 %, i.e. the historical display. Ctrl+wheel is wired on exactly the
            // three time viewports (never on headerScroll, the bottom editor or the toolbar).
            zoomIdx = ClampZoomIdx(NearestZoomIdx(AppSettings.Instance.TimelineZoom));
            rulerScroll.PreviewMouseWheel += Timeline_PreviewMouseWheel;
            laneScroll.PreviewMouseWheel += Timeline_PreviewMouseWheel;
            if (chordScroll != null) chordScroll.PreviewMouseWheel += Timeline_PreviewMouseWheel;
            UpdateZoomUi();

            Loaded += (s, e) => { Render(); EnsureCursor(); HookKotonHost(); RefreshKotonGeneratorMenu(); };
            // Unhook au démontage : KotonHost est statique, un onglet fermé ne doit plus répondre.
            // (Une navigation d'un onglet à l'autre : le nouvel onglet ré-hookera au Loaded et écrasera,
            // pas de collision.)
            Unloaded += (s, e) => { try { UnhookKotonHost(); } catch { } };

            undoMgr.Changed += UpdateUndoButtons;
            undoMgr.Changed += RaiseDirtyChanged;
            UpdateUndoButtons();
            // Référence de départ : un morceau neuf, auquel personne n'a touché, n'est pas « modifié ».
            savedState = DocumentJson();
            PreviewKeyDown += TimelineKeyDown; // Ctrl+Z / Ctrl+Y / Ctrl+Shift+Z (unless a text field has focus)
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
            SyncSwingCombo();
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
            string meter = (cboMeter?.SelectedItem as string) ?? "";
            // Swing leaves the written notes untouched, so the collapsed chip is the only place it shows.
            txtMeterSummary.Text = project.SwingPercent > 50.5 ? meter + " ♪⁀" : meter;
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
            (Loc.T("Aucune"), 0), (Loc.T("DoubleCroche"), 0.25), (Loc.T("Croche"), 0.5), (Loc.T("CrochePointee"), 0.75),
            (Loc.T("Noire"), 1.0), (Loc.T("NoirePointee"), 1.5), (Loc.T("Blanche"), 2.0),
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
        bool syncingSwing;
        // Swing presets: where the off-eighth lands inside the beat. 50 = straight, 66.7 = full triplet; the values in
        // between are the usual "half swing" degrees. Playback only — the score keeps its straight eighths.
        static readonly (string label, double pct)[] SwingOpts =
        {
            (Loc.T("SwingStraight"), 50), (Loc.T("SwingLight") + " (54%)", 54), (Loc.T("SwingMedium") + " (58%)", 58),
            ("Swing (62%)", 62), (Loc.T("SwingTriplet") + " (67%)", 200.0 / 3),
        };
        void SyncSwingCombo()
        {
            if (cboSwing == null) return;
            syncingSwing = true;
            cboSwing.Items.Clear();
            foreach (var o in SwingOpts) cboSwing.Items.Add(o.label);
            int sel = 0; double best = double.MaxValue;
            for (int i = 0; i < SwingOpts.Length; i++) { double d = Math.Abs(SwingOpts[i].pct - project.SwingPercent); if (d < best) { best = d; sel = i; } }
            cboSwing.SelectedIndex = sel;
            syncingSwing = false;
        }
        private void Swing_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (syncingSwing || cboSwing == null) return;
            int i = cboSwing.SelectedIndex;
            if (i < 0 || i >= SwingOpts.Length) return;
            double pct = SwingOpts[i].pct;
            if (Math.Abs(pct - project.SwingPercent) < 1e-9) return;
            PushUndo("swing"); // stable op key (coalescing) — not a localized label
            project.SwingPercent = pct;
            UpdateMeterSummary(); // the collapsed chip shows the swing, since the notes look unchanged
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

            var added = Engine.Timeline.TimelineImporter.ReSegment(project, barBeats, project.RiffById);
            foreach (var r in added) project.Riffs.Add(r);
            if (activeRiffGrid != null) activeRiffGrid.MeterDen = project.TimeSigDen;
            selectedItem = null; // items were rebuilt; drop the stale selection
            Render();
            RefreshScore();
            UpdateMeterSummary();
            NotifyKotonEditorContextChanged();  // arpégiateurs, etc. peuvent adapter leur UI (binaire ↔ ternaire)
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
            NotifyKotonEditorContextChanged();  // arpégiateurs, etc. peuvent adapter leur UI (binaire ↔ ternaire)
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
        static readonly string[] BassModeNames = { Loc.T("Aucune"), Loc.T("ParMesureTenue"), Loc.T("ParTemps") };
        static readonly string[] HeldModeNames = { Loc.T("NoteSeule"), Loc.T("AccordPlaque"), Loc.T("FondamentaleQuinte"), Loc.T("FondamentaleTierce") };
        static readonly string[] ClimbModeNames = { Loc.T("ArpegeMontant"), Loc.T("ArpegeDescendant"), Loc.T("Alberti153"), Loc.T("Mixte") };
        static readonly string[] VoiceLeadModeNames = { Loc.T("AucunPositionFond"), Loc.T("AutoMouvementMini"), Loc.T("BasseProche"), Loc.T("HautProche") };
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
            PushUndo("transpose");
            if (!Engine.Timeline.ChordModelOps.TransposeProject(project, project.RiffById, dlg.Result, dlg.ResultDirection, dlg.ResultMode)) return;
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
        public string ModeName => Loc.T("Sequenceur");
        public string FileExtension => ".sq";
        public string CurrentPath { get; set; }
        public void StopAudio() { PausePlayback(); try { activeRiffGrid?.StopPreview(); } catch { } StopEditorPreviews(); }

        // The bottom editor hosts SEVERAL preview-capable controls (riff grid, drum grid, poly melodic/drum editors),
        // each owning its own WaveOut. Stopping only the riff grid left the others audible after the tab was closed
        // (issue #12) — walking the host's tree silences every one, including any added later.
        void StopEditorPreviews()
        {
            if (editorHost == null) return;
            foreach (var d in Descendants(editorHost))
            {
                try
                {
                    if (d is Controls.RiffGridControl rg) rg.StopPreview();
                    else if (d is Controls.RhythmGridControl dg) dg.StopPreview();
                    else if (d is Controls.TimelineEditor.MelodicPolyEditor mp) mp.Stop();
                    else if (d is Controls.TimelineEditor.PolyDrumEditor pd) pd.Stop();
                }
                catch { /* a preview that can't stop must not block the others */ }
            }
        }

        // Depth-first walk of the visual tree, plus the ContentControl's Content (which may not be realised yet).
        static IEnumerable<DependencyObject> Descendants(DependencyObject root)
        {
            if (root == null) yield break;
            yield return root;
            if (root is ContentControl cc && cc.Content is DependencyObject content && !ReferenceEquals(content, root))
                foreach (var d in Descendants(content)) yield return d;
            int n = 0;
            try { n = VisualTreeHelper.GetChildrenCount(root); } catch { n = 0; }
            for (int i = 0; i < n; i++)
            {
                DependencyObject child = null;
                try { child = VisualTreeHelper.GetChild(root, i); } catch { }
                if (child == null) continue;
                foreach (var d in Descendants(child)) yield return d;
            }
        }

        // ---- playback ----
        // ▶/⏸ is a toggle: play from the cursor, or pause (freeze the cursor where it is; ▶ resumes there — the
        // player rolls tempo/volume forward to the start beat instantly, so resuming mid-piece lands right).
        // ⏹ is a real STOP: it also rewinds the cursor to the beginning.
        private void btnPlay_Click(object sender, RoutedEventArgs e)
        {
            if (player == null) StartPlayback(); // idle or paused -> play from the cursor
            else PausePlayback();                // playing -> pause (cursor freezes; ▶ resumes here)
        }

        private void btnStop_Click(object sender, RoutedEventArgs e) => StopPlayback();

        // 🎚 Mixer: non-modal so the transport stays usable; the player reads the project's volume/pan/mute/solo
        // live each buffer, so moving a fader while playing is heard immediately.
        private void btnMixer_Click(object sender, RoutedEventArgs e)
        {
            if (mixerWindow != null) { try { mixerWindow.Activate(); return; } catch { mixerWindow = null; } }
            mixerWindow = new Dialogs.MixerDialog(project, this);
            mixerWindow.Closed += (s, ev) => mixerWindow = null;
            mixerWindow.Show();
        }

        void StartPlayback()
        {
            CommitRiffEditor(); // stop any riff preview first
            // Une lecture réelle qui démarre coupe la preview d'un générateur Koton (deux sources
            // audio parallèles = confusion à l'oreille + éventuel bug d'ordre sur le device audio).
            try { KotonHost_StopPreview(); } catch { }
            if (!SoundFontGuard.EnsureReady(Window.GetWindow(this), "Playback")) return;
            try
            {
                player = new Engine.Timeline.TimelinePlayer(project, project.RiffById, AudioFormat.SampleRate);
                player.StartBeat = startBeat; // start at the cursor; tempo/volume are set for that beat in Start()
                player.Loop = loopEnabled; player.LoopEndBeat = loopEndBeat; // A-B loop region ([startBeat, loopEndBeat])
                // A background thread pre-renders ahead so the audio device only copies samples (absorbs the
                // SoundFont synthesis cost / GC spikes). The device is started only once the buffer has a head
                // start (Primed) — otherwise it would drain the buffer as fast as it fills.
                playBuffer = new Engine.Timeline.LookaheadBuffer(player, player.Start, player.Stop, AudioFormat.SampleRate);
                playBuffer.Ended += () => Dispatcher.BeginInvoke((Action)OnPlaybackEnded);
                playBuffer.Primed += () => Dispatcher.BeginInvoke((Action)BeginPlaybackDevice);
                // Note : le prewarm de 3s (rendu silencieux pour reveiller les VSTi) a ete retire — desormais
                // les instances VSTi sont partagees via VstInstrumentCache et gardent leurs samples charges
                // + leur DSP chaud entre deux Play. La 1re creation du plugin coute toujours (LoadLibrary +
                // COM init + samples), mais elle est absorbee par le prime buffer du LookaheadBuffer.
                // PrewarmVstiSilently reste dans TimelinePlayer au cas ou un futur cas d'usage le rappelle.
                EnsureCursor();
                MoveCursor(startBeat);
                SetPlayGlyph("⏳"); // filling the buffer before playback
                playBuffer.Start(); // producer fills; the device starts on Primed
            }
            catch (Exception ex) { MessageBox.Show(Loc.T("Lecture") + ex.Message); StopPlayback(); }
        }

        // Called on the UI thread once the look-ahead buffer has its head start: actually start the device.
        void BeginPlaybackDevice()
        {
            if (playBuffer == null || playWaveOut != null) return; // stopped during prime, or already started
            playWaveOut = new NAudio.Wave.WaveOutEvent { DesiredLatency = 150 };
            playWaveOut.Init(playBuffer);
            playWaveOut.Play();
            SetPlayGlyph("⏸"); // now playing -> the toggle shows Pause
            playTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
            playTimer.Tick += (s, ev) => MoveCursor(PlayedBeat());
            playTimer.Start();
        }

        // The audible beat = the consumed-sample position mapped back through the tempo map.
        double PlayedBeat() => (player != null && playBuffer != null)
            ? player.PlayheadBeat(playBuffer.ConsumedSamples / Math.Max(1, player.WaveFormat.Channels)) : startBeat; // shorts → frames

        // Pause: stop the audio but freeze the cursor where playback reached (the AUDIBLE position), so ▶ resumes
        // from there. Also used when switching away from this editor's tab (keep the position).
        public void PausePlayback()
        {
            double beat = PlayedBeat();
            TeardownPlayer();
            startBeat = Math.Max(0, Math.Min(TotalBeats(), beat));
            MoveCursor(startBeat);
        }

        // Stop: stop the audio AND rewind the cursor to the beginning (the ⏹ button).
        public void StopPlayback()
        {
            TeardownPlayer();
            startBeat = 0;
            MoveCursor(0);
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
            if (player != null) { try { player.Stop(); } catch { } try { player.Dispose(); } catch { } player = null; }
            // Note : le GC.Collect() force qui existait ici visait a liberer les objets COM des VSTi
            // disposes entre deux Play (ancien schema : nouveau plugin instancie a chaque Play). Depuis
            // VstInstrumentCache, les instances SURVIVENT au Stop — plus rien a collecter en urgence, le
            // GC habituel suffit. Le vrai Dispose des VSTi est declenche par ReleaseTrack / ClearAll.
            SetPlayGlyph("▶");
        }

        // Keep both play buttons (top toolbar + bottom transport bar) showing the same ▶ / ⏸ / ⏳ glyph.
        void SetPlayGlyph(string glyph)
        {
            if (btnPlay != null) btnPlay.Content = glyph;
            if (btnPlayBottom != null) btnPlayBottom.Content = glyph;
        }

        // Set the toolbar BPM box AND the bottom transport tempo readout from the project's main tempo.
        void SetBpmText()
        {
            if (txtBpm != null) txtBpm.Text = ((int)project.MainBpm).ToString();
            SyncTempoReadout();
        }

        // Mirror the current BPM box value into the bottom transport bar's "♩ = N" readout.
        void SyncTempoReadout()
        {
            if (txtTransportTempo != null && txtBpm != null)
                txtTransportTempo.Text = "♩ = " + txtBpm.Text;
        }

        // Bottom transport: ⏮ rewinds to the start (same as ⏹); ⏭ jumps the cursor to the end.
        private void btnPrevBottom_Click(object sender, RoutedEventArgs e) => StopPlayback();
        private void btnNextBottom_Click(object sender, RoutedEventArgs e) => SeekTo(TotalBeats());

        // Move the cursor and the resume point to a beat; if playing, stop first so the next ▶ starts there.
        void SeekTo(double beat)
        {
            if (player != null) TeardownPlayer();
            startBeat = Math.Max(0, Math.Min(TotalBeats(), beat));
            MoveCursor(startBeat);
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
                    ToolTip = Loc.T("GlisserPourDefinirLePointDe"),
                };
                Panel.SetZIndex(startMarker, 10);
                startMarker.MouseLeftButtonDown += startMarker_MouseLeftButtonDown;
                startMarker.MouseMove += startMarker_MouseMove;
                startMarker.MouseLeftButtonUp += startMarker_MouseLeftButtonUp;
                startCanvas.Children.Add(startMarker);
            }
            if (loopMarker == null)
            {
                loopMarker = new System.Windows.Shapes.Polygon
                {
                    Points = new PointCollection { new Point(-7, 0), new Point(7, 0), new Point(0, 18) },
                    Fill = new SolidColorBrush(Color.FromRgb(0xE8, 0x89, 0x4A)),   // orange = loop END (B)
                    Stroke = new SolidColorBrush(Color.FromRgb(0xFA, 0xDE, 0xCF)),
                    StrokeThickness = 1,
                    Cursor = Cursors.SizeWE,
                    Visibility = Visibility.Collapsed,                              // shown only when looping
                    ToolTip = Loc.T("GlisserPourDefinirLaFinDeBoucle"),
                };
                Panel.SetZIndex(loopMarker, 10);
                loopMarker.MouseLeftButtonDown += loopMarker_MouseLeftButtonDown;
                loopMarker.MouseMove += loopMarker_MouseMove;
                loopMarker.MouseLeftButtonUp += loopMarker_MouseLeftButtonUp;
                startCanvas.Children.Add(loopMarker);
            }
            playCursor.Visibility = Visibility.Visible;
            MoveCursor(startBeat);
        }

        void MoveCursor(double beat)
        {
            double x = beat * PxPerBeat;
            if (playCursor != null) { playCursor.Height = lanePanel.ActualHeight; Canvas.SetLeft(playCursor, x); }
            if (startMarker != null) Canvas.SetLeft(startMarker, x + 1); // centre the handle on the 2px line
            if (loopMarker != null) { loopMarker.Visibility = loopEnabled ? Visibility.Visible : Visibility.Collapsed; Canvas.SetLeft(loopMarker, loopEndBeat * PxPerBeat + 1); }
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

            // Bottom transport progress bar: fraction of the piece played.
            if (transportProgress != null)
            {
                double tot = TotalBeats();
                transportProgress.Value = tot > 0 ? Math.Max(0, Math.Min(1, beat / tot)) : 0;
            }
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
            txtEditorTitle.Text = Loc.T("PartitionCalcul");

            // ResolveLoops mutates the project (sizes looping Repeats) — run it ONCE here on the UI thread, then
            // the per-track builds (parallel, background) only read.
            Engine.Timeline.TimelineProject.ResolveLoops(project, project.RiffById);

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
                        if (Engine.Score.ScoreBuilder.TrackHasMelodic(t)) l.Add(Engine.Score.ScoreBuilder.Build(project, t, project.RiffById, false, melodic: true));
                        l.Add(Engine.Score.ScoreBuilder.Build(project, t, project.RiffById, false));
                        perTrack[k] = l;
                    });
                    var flat = new List<Engine.Score.TrackScore>();
                    foreach (var l in perTrack) if (l != null) flat.AddRange(l);
                    return flat;
                });
            }
            catch (Exception ex) { if (myGen == scoreGen) txtEditorTitle.Text = Loc.T("PartitionErreur") + ex.Message; return; }

            if (myGen != scoreGen) return;                 // a newer RefreshScore superseded this one
            if (!ScoreVisible) { editorHost.Content = null; activeScore = null; txtEditorTitle.Text = Loc.T("Editeur"); return; }

            var view = new Controls.Score.ScoreView();
            view.EditMode = scoreEditMode;
            view.MeasureClicked += LocateRiffAtBeat; // click a measure → reveal its riff in the timeline (no edit)
            view.EditPositionClicked += ScoreEditClickAt;
            view.NoteEditClicked += ScoreEditSelectNote;
            view.NotePlaceClicked += ScoreMousePlace;
            try { view.Configure(list, project.TimeSigNum, project.TimeSigDen, project.TimeSigScale, project.PickupBeats); }
            catch (Exception ex) { txtEditorTitle.Text = Loc.T("PartitionErreurRendu") + ex.Message; return; } // never let a render bug break note-entry
            scoreContainer = ScoreContainer(view);
            editorHost.Content = scoreContainer;
            activeScore = view;
            txtEditorTitle.Text = list.Count > 1 ? Loc.T("Partition2") + list.Count + Loc.T("Portees") : Loc.T("Partition");
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

        // ---- A-B loop end (B) marker ----
        void SetLoopEndFromX(double x)
        {
            double beat = Math.Max(0, Math.Min(TotalBeats(), x / PxPerBeat));
            if (beat <= startBeat + 1e-6) beat = Math.Min(TotalBeats(), startBeat + 1); // B must stay after A
            loopEndBeat = beat;
            if (loopMarker != null) Canvas.SetLeft(loopMarker, loopEndBeat * PxPerBeat + 1);
            if (player != null) { player.LoopEndBeat = loopEndBeat; player.ApplyLoop(); } // live update while playing
        }

        private void loopMarker_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            draggingLoop = true; loopMarker.CaptureMouse();
            SetLoopEndFromX(e.GetPosition(startCanvas).X);
            e.Handled = true;
        }

        private void loopMarker_MouseMove(object sender, MouseEventArgs e)
        {
            if (draggingLoop) { SetLoopEndFromX(e.GetPosition(startCanvas).X); e.Handled = true; }
        }

        private void loopMarker_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (draggingLoop) { draggingLoop = false; loopMarker.ReleaseMouseCapture(); e.Handled = true; }
        }

        // ⟳ "Boucle" toggle: loop the [A, B] region seamlessly (A = the blue start handle, B = the orange handle).
        private void btnLoop_Click(object sender, RoutedEventArgs e)
        {
            loopEnabled = btnLoop != null && btnLoop.IsChecked == true;
            if (loopEnabled)
            {
                double total = TotalBeats();
                // Default B to A + 4 bars (clamped) the first time, so there's a sensible region to drag.
                if (loopEndBeat <= startBeat + 1e-6)
                    loopEndBeat = Math.Min(total, startBeat + 4 * Math.Max(1, TimelineHelper.RulerBeatsPerBar(project)));
                if (loopEndBeat <= startBeat + 1e-6) loopEndBeat = total; // whole piece if nothing else fits
            }
            EnsureCursor();
            if (player != null) { player.Loop = loopEnabled; player.LoopEndBeat = loopEndBeat; player.ApplyLoop(); } // live
            MoveCursor(player != null ? PlayedBeat() : startBeat);
        }

        // ---- section markers (the band above the ruler) ------------------------------------------------------
        // The band itself (MarkerLaneControl) only draws and detects gestures; the model (project.Markers), the
        // undo keys, the localized strings and the A-B loop all live here, next to startBeat/loopEndBeat.

        // Re-push the CURRENT list (ApplyDocument replaces the list instance on load and on every undo/redo, so
        // the control must never cache it) + the current width. Called wherever the ruler width is recomputed.
        void RefreshMarkers()
        {
            if (markerLane == null) return;
            markerLane.Configure(TotalBeats() * PxPerBeat, MarkerLaneH, PxPerBeat, project.Markers,
                                 b => TimelineHelper.SnapToBarline(project, ClampBeat(b)),
                                 MarkerTooltip, Loc.T("MarkersLaneHint"));
        }

        // Clamp to the drawable timeline, then let SnapToBarline round to a real barline (§3.5: dropped out of
        // bounds -> nearest valid bar).
        double ClampBeat(double b) => Math.Max(0, Math.Min(TotalBeats(), b));

        string MarkerTooltip(SectionMarker m)
        {
            int i = TimelineHelper.BarIndexAt(project, m.Beat);
            string bar = i < 0 ? Loc.T("MarkerPickupBar") : Loc.T("MarkerBar") + " " + (i + 1);
            return (m.Name ?? "") + " — " + bar;
        }

        void MarkerCreateAt(double beat)
        {
            var existing = MarkerAt(beat);
            if (existing != null) { MarkerRename(existing); return; }   // §3.2: no duplicate, rename instead
            string name = TimelineHelper.PromptText(Loc.T("MarkerNewTitle"), NextMarkerName());
            if (string.IsNullOrWhiteSpace(name)) return;                // Cancel or blank = no marker at all
            PushUndo("marker:add");
            project.Markers.Add(new SectionMarker { Beat = beat, Name = name.Trim() });
            SortMarkers();
            RefreshMarkers();
        }

        void MarkerRename(SectionMarker m)
        {
            string name = TimelineHelper.PromptText(Loc.T("MarkerRenameTitle"), m.Name);
            if (string.IsNullOrWhiteSpace(name)) return;
            if (name.Trim() == m.Name) return;                          // no-op: don't pollute the history
            PushUndo("marker:rename");
            m.Name = name.Trim();
            RefreshMarkers();
        }

        SectionMarker MarkerAt(double beat)
        {
            foreach (var m in project.Markers) if (Math.Abs(m.Beat - beat) < 1e-6) return m;
            return null;
        }

        void SortMarkers() { project.Markers.Sort((a, b) => a.Beat.CompareTo(b.Beat)); }

        // "Repère N" with the smallest free N (localized prefix).
        string NextMarkerName()
        {
            string prefix = Loc.T("MarkerDefaultName");
            var used = new System.Collections.Generic.HashSet<int>();
            foreach (var m in project.Markers)
            {
                string s = (m.Name ?? "").Trim();
                if (s.StartsWith(prefix + " ", StringComparison.Ordinal)
                    && int.TryParse(s.Substring(prefix.Length + 1).Trim(), out int n)) used.Add(n);
            }
            int k = 1; while (used.Contains(k)) k++;
            return prefix + " " + k;
        }

        // Click a marker = set the play START point there (exactly what clicking the ruler does). Playback, if
        // any, is deliberately NOT interrupted: the new start applies to the next ▶ (never SeekTo here).
        void MarkerGoTo(SectionMarker m)
        {
            startBeat = ClampBeat(m.Beat);
            MoveCursor(startBeat);
        }

        // Drop: exactly ONE undo entry per drag (pre-state captured at threshold crossing, pushed here).
        void MarkerDrop(SectionMarker m, double beat)
        {
            string pre = markerDragPre; markerDragPre = null;
            var occupant = MarkerAt(beat);
            if (Math.Abs(beat - m.Beat) < 1e-6 || (occupant != null && occupant != m))
            {
                RefreshMarkers();   // §3.5: unchanged, or target bar taken -> redraw from the model = snap back
                return;
            }
            CommitUndo(pre, "marker:move"); // key deliberately NOT prefixed "move:" -> never coalesced with another drag
            m.Beat = beat;
            SortMarkers();
            RefreshMarkers();
        }

        void ShowMarkerContextMenu(SectionMarker m, FrameworkElement anchor)
        {
            var menu = new ContextMenu();
            var ren = new MenuItem { Header = Loc.T("MarkerMenuRename") }; ren.Click += (s, e) => MarkerRename(m);
            var loop = new MenuItem { Header = Loc.T("MarkerMenuLoop") }; loop.Click += (s, e) => MarkerLoopSection(m);
            var del = new MenuItem { Header = Loc.T("MarkerMenuDelete") }; del.Click += (s, e) => MarkerDelete(m);
            menu.Items.Add(ren); menu.Items.Add(loop); menu.Items.Add(new Separator()); menu.Items.Add(del);
            menu.PlacementTarget = anchor; menu.IsOpen = true;
        }

        void MarkerDelete(SectionMarker m)
        {
            PushUndo("marker:del");  // NOT "delete:" -> no accidental neutralization against a module insert
            project.Markers.Remove(m);
            RefreshMarkers();
        }

        // "Boucler cette section": A = this marker, B = the next one (or the end of the piece). Pure UI state
        // (startBeat / loopEndBeat / loopEnabled are neither serialized nor undoable) -> NO undo entry.
        void MarkerLoopSection(SectionMarker m)
        {
            int bpb = Math.Max(1, TimelineHelper.RulerBeatsPerBar(project));
            double a = ClampBeat(m.Beat);
            double b = double.NaN;
            foreach (var o in project.Markers) if (o.Beat > a + 1e-6 && (double.IsNaN(b) || o.Beat < b)) b = o.Beat;
            if (double.IsNaN(b)) b = PieceEndBeats();                 // last marker -> end of the piece (§4.8)
            if (b <= a + 1e-6) b = a + bpb;                           // never empty nor inverted: at least one bar
            startBeat = a; loopEndBeat = b; loopEnabled = true;
            if (btnLoop != null) btnLoop.IsChecked = true;
            EnsureCursor();
            if (player != null) { player.Loop = true; player.LoopEndBeat = loopEndBeat; player.ApplyLoop(); }
            MoveCursor(player != null ? PlayedBeat() : startBeat);
        }

        // Musical end of the piece = the latest track end, floored by MinBeats — WITHOUT the +8 beats of display
        // slack TotalBeats() adds (a loop must not run past the music into empty bars).
        double PieceEndBeats()
        {
            double end = project.MinBeats;
            foreach (var t in project.Tracks) end = Math.Max(end, SeqDispLen(t.Items));
            return end;
        }

        // ---- horizontal zoom (display only) ------------------------------------------------------------------
        // Everything drawn against time reads PxPerBeat, so a Render() at a new zoomIdx rescales the WHOLE
        // timeline (ruler, marker band, boxes, tempo/volume lanes, chord trame, cursor, handles) at once. The
        // zoom is UI state: no undo entry, nothing written to the .sq, and each tab has its own level.

        /// <summary>Highest step reachable without exceeding <see cref="MaxLaneWidth"/> for THIS piece's length
        /// (a several-hundred-bar piece at 400 % must degrade by disabling "+", never by building a monstrous canvas).</summary>
        int MaxZoomIdx()
        {
            double beats = Math.Max(1, TotalBeats());
            for (int i = ZoomLevels.Length - 1; i > 0; i--)
                if (beats * BasePxPerBeat * ZoomLevels[i] <= MaxLaneWidth) return i;
            return 0;
        }

        int ClampZoomIdx(int i) => Math.Max(0, Math.Min(MaxZoomIdx(), i));

        /// <summary>Nearest step to a stored FACTOR (settings.json keeps the factor, not the index, so an older or
        /// corrupt file stays interpretable). Unknown / ≤ 0 / NaN → 100 %.</summary>
        static int NearestZoomIdx(double factor)
        {
            if (double.IsNaN(factor) || double.IsInfinity(factor) || factor <= 0) return ZoomDefaultIdx;
            int best = ZoomDefaultIdx; double bestD = double.MaxValue;
            for (int i = 0; i < ZoomLevels.Length; i++)
            {
                double d = Math.Abs(ZoomLevels[i] - factor);
                if (d < bestD) { bestD = d; best = i; }
            }
            return best;
        }

        /// <summary>Contract: after application, musical time <paramref name="anchorBeat"/> sits
        /// <paramref name="anchorViewX"/> px from the left edge of the visible area. The level (hence the label and
        /// the button states) changes at once; the re-render is debounced so a wheel burst costs ONE Render.</summary>
        void RequestZoom(int newIdx, double anchorBeat, double anchorViewX)
        {
            newIdx = ClampZoomIdx(newIdx);
            if (newIdx == zoomIdx && pendingZoomIdx < 0) return;
            // FIRST step of a burst only: at later steps the caller computed anchorBeat with an already-changed
            // (but not yet rendered) PxPerBeat, so that value is wrong and must be ignored.
            if (pendingZoomIdx < 0) { zoomAnchorBeat = anchorBeat; zoomAnchorViewX = anchorViewX; }
            pendingZoomIdx = newIdx;
            zoomIdx = newIdx;
            UpdateZoomUi();
            if (zoomTimer == null)
            {
                zoomTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
                zoomTimer.Tick += (s, e) => ApplyPendingZoom();
            }
            zoomTimer.Stop(); zoomTimer.Start();
        }

        void ApplyPendingZoom()
        {
            zoomTimer?.Stop();
            if (pendingZoomIdx < 0) return;
            pendingZoomIdx = -1;

            Render();                    // redraws everything at the new scale
            laneScroll.UpdateLayout();   // MANDATORY: Render() emptied lanePanel, so ScrollableWidth is still stale
                                         // and ScrollToHorizontalOffset would be silently clamped to 0.
            // WPF clamps the offset to [0, ScrollableWidth] itself -> "never before the start nor past the end".
            laneScroll.ScrollToHorizontalOffset(zoomAnchorBeat * PxPerBeat - zoomAnchorViewX);
            // rulerScroll (ruler + marker band) and chordScroll follow through laneScroll_ScrollChanged: the zoom
            // drives ONE viewport, never a fourth synchronisation point.

            AppSettings.Instance.TimelineZoom = Zoom;   // app-level memory: the next tab opens at this level
            AppSettings.Instance.Save();
        }

        // Musical time at the CENTRE of the visible area — the anchor for the toolbar commands (§3.3).
        double CentreBeat()
        {
            double px = PxPerBeat;
            if (px <= 0 || laneScroll == null) return 0;
            return (laneScroll.HorizontalOffset + laneScroll.ViewportWidth / 2) / px;
        }

        void btnZoomOut_Click(object sender, RoutedEventArgs e) => RequestZoom(zoomIdx - 1, CentreBeat(), laneScroll.ViewportWidth / 2);
        void btnZoomIn_Click(object sender, RoutedEventArgs e) => RequestZoom(zoomIdx + 1, CentreBeat(), laneScroll.ViewportWidth / 2);
        void btnZoomLevel_Click(object sender, RoutedEventArgs e) => RequestZoom(ZoomDefaultIdx, CentreBeat(), laneScroll.ViewportWidth / 2);

        // "Ajuster": the LARGEST predefined step at which the whole piece fits the visible lane width — never
        // above 100 % (§3.4), and 10 % without any error message when even that is not enough.
        void btnZoomFit_Click(object sender, RoutedEventArgs e)
        {
            bool empty = true;
            foreach (var t in project.Tracks) if (t.Items.Count > 0) { empty = false; break; }
            if (empty) return;                            // empty project: nothing visible to fit

            double avail = laneScroll.ViewportWidth;
            if (avail < 50) return;                       // not laid out yet
            double beats = Math.Max(1, TotalBeats());     // already includes TotalBeats()'s +8 beats of slack

            int idx = 0;
            for (int i = ZoomDefaultIdx; i >= 0; i--)
                if (beats * BasePxPerBeat * ZoomLevels[i] <= avail) { idx = i; break; }

            // Already at that step (e.g. a very long piece pinned at 10 %): RequestZoom would no-op, so honour the
            // "the whole piece becomes visible" part by going back to the start ourselves.
            if (idx == zoomIdx && pendingZoomIdx < 0) { laneScroll.ScrollToHorizontalOffset(0); return; }
            RequestZoom(idx, 0, 0);                       // anchor = start of the piece
        }

        /// <summary>Refresh the zoom chip (level label + border states). Called by UpdateToolbar (hence by every
        /// Render), so the upper bound follows the piece's length.</summary>
        void UpdateZoomUi()
        {
            if (txtZoomLevel == null) return;
            txtZoomLevel.Content = (int)Math.Round(Zoom * 100) + " %";  // same format in all 7 languages
            if (btnZoomOut != null) btnZoomOut.IsEnabled = zoomIdx > 0;
            if (btnZoomIn != null) btnZoomIn.IsEnabled = zoomIdx < MaxZoomIdx();
        }

        // Ctrl + wheel over the ruler / the lanes / the docked chords lane: one step per notch, anchored on the
        // musical position UNDER THE POINTER. Wheel WITHOUT Ctrl is left completely alone (we return before
        // touching e.Handled), and the track headers are deliberately not subscribed.
        void Timeline_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
            var sv = sender as ScrollViewer; if (sv == null) return;
            e.Handled = true;                                  // don't let the ScrollViewer scroll as well
            double vx = e.GetPosition(sv).X;                   // px from the viewport's left edge
            double beat = PxPerBeat > 0 ? (sv.HorizontalOffset + vx) / PxPerBeat : 0;
            RequestZoom(zoomIdx + (e.Delta > 0 ? 1 : -1), beat, vx);
        }

        // A .sq file = the arrangement + the riffs it references (same idea as the graph's .graph).
        public bool Save(string path)
        {
            // Si un player est en cours de lecture (ou récemment stoppé), lui demander de capturer l'état
            // courant de chaque VSTi (patch chargé, sliders, banque de presets) avant la sérialisation.
            // Le SaveState() par piste coûte l'appel getChunk du plugin (rapide en général) ; on ne le fait
            // pas dans DocumentJson (appelé aussi par IsDirty) — uniquement à la vraie sauvegarde.
            if (player != null) { try { player.CaptureVstiStates(); } catch { } }
            // Capture des états des générateurs Koton vivants : on parcourt les modules et on
            // rafraîchit GeneratorState depuis l'instance vivante avant sérialisation. Sinon un
            // changement de slider dans l'éditeur (qui mute UNIQUEMENT l'instance vivante) serait
            // perdu au save. Fait ici et pas dans DocumentJson (appelé aussi par IsDirty).
            try { CaptureKotonGeneratorStates(); } catch { }
            string json = DocumentJson();
            Engine.SafeFile.WriteAllText(path, json);   // atomique : ne détruit jamais le .sq existant
            CurrentPath = path;
            savedState = json;                          // référence pour « modifié depuis l'enregistrement »
            return true;
        }

        // L'état sérialisé au dernier enregistrement (ou à l'ouverture). Comparer la sérialisation courante à
        // celle-ci donne une réponse EXACTE : pas de faux « modifié » après une action annulée puis rétablie,
        // contrairement à un simple drapeau posé à chaque mutation.
        string savedState;

        /// <inheritdoc/>
        public bool IsDirty
        {
            get
            {
                if (savedState == null) return false;   // référence pas encore posée (construction en cours)
                // DocumentJson et non SnapshotState : la sélection ♫ fait partie de l'unité d'ANNULATION, pas du
                // fichier — la cocher ne doit pas faire apparaître l'astérisque « modifié ».
                try { return DocumentJson() != savedState; }
                catch { return true; }   // dans le doute, on protège le travail plutôt que de fermer en silence
            }
        }

        /// <summary>Gather this project's state as attachable context for a GitHub bug report (see
        /// <see cref="Dialogs.ReportBugDialog"/>). The serialization/formatting lives in the decoupled
        /// <see cref="Engine.BugReport.BugReportContext"/>; this only forwards the screen's state.</summary>
        public Engine.BugReport.BugReportContext BuildBugReportContext()
            => Engine.BugReport.BugReportContext.Build(project, project.Riffs, templateSpec, CurrentPath, TemplateSeed);

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
                prog.Set(0.1, Loc.T("OuvertureDuFichier"));
                var doc = await System.Threading.Tasks.Task.Run(() =>
                    System.Text.Json.JsonSerializer.Deserialize<TimelineDocument>(System.IO.File.ReadAllText(path), JsonOpts) ?? new TimelineDocument());

                prog.Set(0.7, Loc.T("Chargement"));
                ApplyDocument(doc, path);
                await RenderBatched(prog); // add the lane controls in batches so the UI stays responsive
                prog.Set(1.0, Loc.T("Termine"));
            }
            catch (Exception ex) { MessageBox.Show(Loc.T("ErreurDOuverture") + ex.Message); }
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
            project.Riffs.Clear();
            if (doc.Riffs != null) foreach (var r in doc.Riffs) project.Riffs.Add(r);

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
            project.SwingPercent = dp.SwingPercent > 0 ? dp.SwingPercent : 50; // pre-swing files have no value -> straight
            // Section markers. MANDATORY: this is the only field-by-field copy from a document to the live project,
            // and it runs on open, on import AND on every undo/redo (RestoreState) — omit it and the markers vanish
            // at each Ctrl+Z. `?? new List<>` covers a hand-edited file with "Markers": null.
            project.Markers = dp.Markers ?? new System.Collections.Generic.List<SectionMarker>();
            // Meme raison pour les inserts du bus master : ce sont un CHAMP (pas une propriete initialisee),
            // et sans cette ligne ils disparaissaient a chaque ouverture / undo — les params etaient
            // bien serialises dans le .sq, juste jamais recopies dans l'instance vivante.
            project.MasterInserts = dp.MasterInserts ?? new System.Collections.Generic.List<MusicTracker.Engine.Timeline.Effects.TrackEffectData>();
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
            SetBpmText();

            // A freshly-loaded document starts a new history (unless we're restoring an undo/redo state).
            if (!restoringUndo)
            {
                undoMgr.Clear(); pendingUndo = null; pendingUndoKey = null;
                // Un document qui vient d'être chargé n'est pas « modifié » : on fige ici la référence.
                // Pendant un undo/redo on ne la touche PAS — revenir à l'état enregistré doit bien effacer
                // l'astérisque, et s'en éloigner doit le rendre.
                savedState = DocumentJson();
            }
        }

        // ===== Undo / redo =====================================================================================

        // L'unité d'annulation = le document .sq PLUS la sélection ♫ de l'éditeur. Cette sélection vit dans
        // `scoreTracks`, un ensemble de RÉFÉRENCES d'objets qu'ApplyDocument vide et que la désérialisation
        // invalide (les pistes restaurées sont de NOUVEAUX objets) — elle doit donc voyager DANS l'instantané,
        // par index de piste. Sans ça, annuler une suppression de piste rend la piste mais pas sa case ♫ (et,
        // défaut préexistant, chaque Ctrl+Z décochait TOUTES les cases).
        sealed class UndoSnapshot
        {
            public TimelineDocument Doc { get; set; }
            public System.Collections.Generic.List<int> Score { get; set; } // index dans Doc.Project.Tracks
        }

        // Le DOCUMENT seul, sérialisé exactement comme un enregistrement .sq. C'est ce que Save écrit et la
        // référence du drapeau « modifié » : la sélection ♫ n'appartient pas au fichier, la cocher ne doit donc
        // pas rendre le morceau « modifié ».
        string DocumentJson()
        {
            var doc = new TimelineDocument { Project = project };
            doc.Riffs.AddRange(project.Riffs);
            return System.Text.Json.JsonSerializer.Serialize(doc, JsonOpts);
        }

        // Serialize the whole editor state (project + referenced riffs + the ♫ selection) — the snapshot unit.
        string SnapshotState()
        {
            var doc = new TimelineDocument { Project = project };
            doc.Riffs.AddRange(project.Riffs);
            var score = new System.Collections.Generic.List<int>();
            for (int i = 0; i < project.Tracks.Count; i++)
                if (scoreTracks.Contains(project.Tracks[i])) score.Add(i);
            return System.Text.Json.JsonSerializer.Serialize(new UndoSnapshot { Doc = doc, Score = score }, JsonOpts);
        }

        // Record the state BEFORE a structural mutation, keyed by op (call this JUST before mutating).
        void PushUndo(string opKey)
        {
            if (restoringUndo) return;
            FlushPending();
            undoMgr.Push(SnapshotState(), opKey);
        }

        // For inserts, the new object's id is only known after the mutation: capture the pre-state, mutate, then commit.
        string BeginUndo() { if (restoringUndo) return null; FlushPending(); return SnapshotState(); }
        void CommitUndo(string preState, string opKey) { if (preState != null && !restoringUndo) undoMgr.Push(preState, opKey); }

        // An editor opened: remember the pre-edit state; FlushPending records it later only if editing changed something.
        void BeginEditSessionFor(TimelineItem item)
        {
            if (restoringUndo) return;
            FlushPending();
            if (item?.Module != null) { pendingUndo = SnapshotState(); pendingUndoKey = "edit:" + Id(item); }
        }

        // Commit a pending edit session (if the state actually changed) into the undo stack.
        void FlushPending()
        {
            if (pendingUndo == null) return;
            string pre = pendingUndo, key = pendingUndoKey;
            pendingUndo = null; pendingUndoKey = null;
            if (SnapshotState() != pre) undoMgr.Push(pre, key);
        }

        void DoUndo()
        {
            FlushPending();
            string s = undoMgr.Undo(SnapshotState());
            if (s != null) RestoreState(s);
        }

        void DoRedo()
        {
            FlushPending();
            string s = undoMgr.Redo(SnapshotState());
            if (s != null) RestoreState(s);
        }

        // Deserialize a snapshot back onto the live editor (same path as loading a .sq), guarding against re-snapshotting.
        void RestoreState(string json)
        {
            restoringUndo = true;
            try
            {
                StopPlayback();
                var snap = System.Text.Json.JsonSerializer.Deserialize<UndoSnapshot>(json, JsonOpts) ?? new UndoSnapshot();
                ApplyDocument(snap.Doc ?? new TimelineDocument(), CurrentPath);   // vide scoreTracks
                // Ré-cocher les ♫ par index : ApplyDocument vient de reconstruire project.Tracks dans l'ordre du
                // document, et EnsureChordTrack n'a rien à repositionner (l'instantané venait d'un état où
                // l'invariant « accords en dernier » était déjà vrai). Double garde-fou sur les bornes malgré tout.
                if (snap.Score != null)
                    foreach (int i in snap.Score)
                        if (i >= 0 && i < project.Tracks.Count) scoreTracks.Add(project.Tracks[i]);
                Render();
                if (ScoreVisible) RefreshScore();
                RefreshMixer();   // le mixeur est non modal : l'ordre/les pistes peuvent avoir changé
            }
            finally { restoringUndo = false; }
            UpdateUndoButtons();
        }

        void UpdateUndoButtons()
        {
            if (btnUndo != null) btnUndo.IsEnabled = undoMgr.CanUndo;
            if (btnRedo != null) btnRedo.IsEnabled = undoMgr.CanRedo;
        }

        void btnUndo_Click(object sender, RoutedEventArgs e) => DoUndo();
        void btnRedo_Click(object sender, RoutedEventArgs e) => DoRedo();

        // ===== Guided tour (issue #11) =========================================================================

        /// <summary>Run the interactive coach-mark tour over this editor. Builds a small demo project (a chord, a riff
        /// and a drum module) so the tour can also open each module's editor and spotlight its options.</summary>
        public void StartTutorial()
        {
            var win = Window.GetWindow(this);
            if (win == null) return;

            BuildTutorialDemo(out var instrTrack, out var riffItem, out var chordTrack, out var chordItem, out var drumTrack, out var drumItem);

            var steps = new System.Collections.Generic.List<Controls.TourStep>
            {
                new Controls.TourStep(() => null,          Loc.T("TourWelcomeTitle"), Loc.T("TourWelcomeText")),
                new Controls.TourStep(() => menuTracks,    Loc.T("TourTracksTitle"),  Loc.T("TourTracksText")),
                new Controls.TourStep(() => menuInsert,    Loc.T("TourInsertTitle"),  Loc.T("TourInsertText")),
                new Controls.TourStep(() => tglKeyMenu,    Loc.T("TourKeyTitle"),     Loc.T("TourKeyText")),
                new Controls.TourStep(() => tglMeterMenu,  Loc.T("TourMeterTitle"),   Loc.T("TourMeterText")),
                // Open each module's editor (via its demo block) and spotlight the bottom editor panel + its options.
                new Controls.TourStep(() => editorHost,    Loc.T("TourChordEdTitle"), Loc.T("TourChordEdText"), () => { if (chordItem != null) SelectItem(chordTrack, chordItem); }),
                new Controls.TourStep(() => editorHost,    Loc.T("TourRiffEdTitle"),  Loc.T("TourRiffEdText"),  () => { if (riffItem != null) SelectItem(instrTrack, riffItem); }),
                new Controls.TourStep(() => editorHost,    Loc.T("TourDrumEdTitle"),  Loc.T("TourDrumEdText"),  () => { if (drumItem != null) SelectItem(drumTrack, drumItem); }),
                new Controls.TourStep(() => btnPlay,       Loc.T("TourPlayTitle"),    Loc.T("TourPlayText")),
                new Controls.TourStep(() => menuMixer,     Loc.T("TourMixerTitle"),   Loc.T("TourMixerText")),
                new Controls.TourStep(() => btnUndo,       Loc.T("TourUndoTitle"),    Loc.T("TourUndoText")),
                new Controls.TourStep(() => btnImport,     Loc.T("TourImportTitle"),  Loc.T("TourImportText")),
                new Controls.TourStep(() => null,          Loc.T("TourEndTitle"),     Loc.T("TourEndText")),
            };
            Controls.GuidedTour.Run(win, steps);
        }

        // Populate this (fresh) editor with a minimal demo: one chord (chords track), one riff (instrument track) and
        // one drum module (a new drum track) — just enough content for the tour to open each editor. Not undoable.
        void BuildTutorialDemo(out TimelineTrack instrTrack, out TimelineItem riffItem,
                               out TimelineTrack chordTrack, out TimelineItem chordItem,
                               out TimelineTrack drumTrack, out TimelineItem drumItem)
        {
            int temps = TimelineHelper.RulerBeatsPerBar(project);

            instrTrack = null;
            foreach (var t in project.Tracks) if (t.Type == TimelineTrackType.Instrument) { instrTrack = t; break; }
            if (instrTrack == null) { instrTrack = new TimelineTrack { Name = "Piste 1", Instrument = 0 }; project.Tracks.Insert(0, instrTrack); }

            // Chord on the dedicated chords track.
            TimelineHelper.EnsureChordTrack(project);
            chordTrack = TimelineHelper.ChordTrack(project);
            chordItem = new TimelineItem { Module = NewChordLike(null) };
            TimelineHelper.InsertTopLevel(chordTrack, chordItem);

            // Empty 1-bar riff on the instrument track.
            var riff = new Riff { Name = "Riff", LengthSlices = temps * 24, SlicesPerQuarter = 24 };
            project.Riffs.Add(riff);
            riffItem = new TimelineItem { Module = new PlayRiffModule { RiffId = riff.Id } };
            TimelineHelper.PlaceAtCursor(instrTrack, riffItem, temps, 0, project.RiffById);

            // Drum module on a new drum track.
            drumTrack = new TimelineTrack { Name = "Batterie", Type = TimelineTrackType.Drum, Instrument = InstrumentCatalog.DrumIndex };
            project.Tracks.Add(drumTrack);
            TimelineHelper.EnsureChordTrack(project); // keep the chords track pinned at the bottom
            drumItem = new TimelineItem { Module = new DrumPatternModule() };
            TimelineHelper.PlaceAtCursor(drumTrack, drumItem, temps, 0, project.RiffById);

            undoMgr.Clear(); pendingUndo = null; pendingUndoKey = null; // the demo isn't a user action
            Render();
        }

        void TimelineKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            var mods = System.Windows.Input.Keyboard.Modifiers;
            if ((mods & System.Windows.Input.ModifierKeys.Control) == 0) return;
            bool shift = (mods & System.Windows.Input.ModifierKeys.Shift) != 0;

            // Enregistrer et exporter AVANT la garde sur les champs de texte : le réflexe Ctrl+S doit marcher même
            // quand le curseur est dans un champ — c'est justement là qu'on vient de saisir quelque chose.
            if (e.Key == System.Windows.Input.Key.S)
            {
                // Le champ garde le focus : sans cela, la valeur en cours de saisie ne serait pas validée et
                // l'enregistrement écrirait l'ancienne.
                CommitFocusedField();
                if (shift) SaveAsRequested?.Invoke(); else SaveRequested?.Invoke();
                e.Handled = true; return;
            }
            if (e.Key == System.Windows.Input.Key.E)
            {
                CommitFocusedField();
                btnExportAny_Click(this, null);
                e.Handled = true; return;
            }

            // Laisser Ctrl+Z/Y au champ de texte en cours d'édition (il a sa propre annulation).
            if (System.Windows.Input.Keyboard.FocusedElement is System.Windows.Controls.TextBox) return;
            if (e.Key == System.Windows.Input.Key.Z && shift) { DoRedo(); e.Handled = true; }
            else if (e.Key == System.Windows.Input.Key.Z) { DoUndo(); e.Handled = true; }
            else if (e.Key == System.Windows.Input.Key.Y) { DoRedo(); e.Handled = true; }
        }

        // Valide le champ qui a le focus en déplaçant le focus : les champs de l'éditeur appliquent leur valeur
        // sur LostFocus, donc enregistrer sans cela perdrait la dernière saisie.
        void CommitFocusedField()
        {
            if (System.Windows.Input.Keyboard.FocusedElement is System.Windows.Controls.TextBox tb)
                tb.MoveFocus(new System.Windows.Input.TraversalRequest(System.Windows.Input.FocusNavigationDirection.Next));
        }

      
       
        double SeqDispLen(System.Collections.Generic.IList<TimelineItem> items)
        {
            double c = 0;
            if (items != null) foreach (var it in items) c += it.SilenceBefore + project.DispLen(it);
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
            TimelineProject.ResolveLoops(project, project.RiffById); // size looping Repeats to fill up to the end
            double laneWidth = TotalBeats() * PxPerBeat;

            measureRuler.Configure(laneWidth, 20, PxPerBeat, TimelineHelper.RulerBeatsPerBar(project), project.PickupBeats); // measure-number ruler on top (4 beats/bar)
            if (startCanvas != null) startCanvas.Width = laneWidth;
            RefreshMarkers();                            // the marker band spans the same width as the ruler
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
            TimelineProject.ResolveLoops(project, project.RiffById);
            double laneWidth = TotalBeats() * PxPerBeat;
            measureRuler.Configure(laneWidth, 20, PxPerBeat, TimelineHelper.RulerBeatsPerBar(project), project.PickupBeats);
            if (startCanvas != null) startCanvas.Width = laneWidth;
            RefreshMarkers();
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
                var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Left };
                var vol = new Controls.TimelineEditor.VolumeLaneControl { HorizontalAlignment = HorizontalAlignment.Left };
                vol.Configure(track, PxPerBeat, VolLaneH, laneWidth);
                stack.Children.Add(vol);
                if (track.AutomationLanes != null)
                {
                    foreach (var ln in track.AutomationLanes)
                    {
                        var auto = new Controls.TimelineEditor.AutomationLaneControl { HorizontalAlignment = HorizontalAlignment.Left };
                        auto.Configure(track, ln, PxPerBeat, AutomLaneH, laneWidth);
                        stack.Children.Add(auto);
                    }
                }
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
                    cursor += project.DispLen(item);
                    if (++done % 24 == 0)
                    {
                        prog?.Set(0.7 + 0.29 * done / Math.Max(1, total), Loc.T("Affichage") + done + "/" + total + ")");
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
            if (miInsertMelodicPoly != null) miInsertMelodicPoly.Visibility = instr ? Visibility.Visible : Visibility.Collapsed;
            if (miAddPattern != null) miAddPattern.Visibility = Visibility.Visible;
            if (miAddCadence != null) miAddCadence.Visibility = Visibility.Visible;
            // Accords polyrythmiques : idem accord classique — toujours proposé (ils vont sur la piste Accords dédiée).
            if (miAddPolyChord != null) miAddPolyChord.Visibility = Visibility.Visible;
            if (miAddDrum != null) miAddDrum.Visibility = drum ? Visibility.Visible : Visibility.Collapsed;
            if (miAddPolyDrum != null) miAddPolyDrum.Visibility = drum ? Visibility.Visible : Visibility.Collapsed;
            // Le sous-menu « Générateur Koton » est toujours visible — filtrage à l'ouverture (SubmenuOpened
            // rebuild dynamiquement selon selectedTrack). Une piste sans type approprié n'affiche que
            // « aucun générateur trouvé », ce qui est plus informatif que masquer l'entrée entière.
            if (miKotonGenerator != null) miKotonGenerator.Visibility = Visibility.Visible;
            UpdateZoomUi();   // the "+" bound depends on the piece's length, which a render may have changed
        }

       

        // A clearly-visible grey divider drawn at the BOTTOM of every track row, in both the header column and
        // the lanes, so tracks read as separated. Shared (UI thread) across all rows.
        static readonly Brush TrackSeparatorBrush = new SolidColorBrush(Color.FromRgb(0x5A, 0x5A, 0x64));

        // Wrap a lane row (tempo lane, or a track's volume+lane stack) so it gets the same bottom divider as its
        // header. Fixed Height = the header's height (border drawn INSIDE it), so header and lane rows stay aligned.
        // HorizontalAlignment=Left is LOAD-BEARING here (and on every explicitly-sized element below): WPF CENTRES an
        // element whose HorizontalAlignment is Stretch (the default) but which has an explicit Width. When the piece is
        // narrower than the viewport — routine once you can zoom out — the lanes would drift right by
        // (viewport - laneWidth)/2 while the ruler (already Left) stayed put, so a module no longer faced its measure.
        Border LaneRow(UIElement content, double height)
            => new Border { Height = height, Child = content, BorderBrush = TrackSeparatorBrush, BorderThickness = new Thickness(0, 0, 0, 1), HorizontalAlignment = HorizontalAlignment.Left };

        // Hauteur d'une lane d'automation additionnelle (Pan/Expression/Modulation/Sustain/Réverbe/Chorus/PitchBend) :
        // plus mince que la lane de volume pour empiler plusieurs paramètres sans dévorer la hauteur verticale.
        const double AutomLaneH = 36;

        // Height of a track's header + lane row: minimal when collapsed (issue #5), else the full volume + extra lanes + module lane stack.
        double TrackRowH(TimelineTrack track)
        {
            if (track == null || track.Collapsed) return track != null && track.Collapsed ? CollapsedH : VolLaneH + LaneH;
            int extras = track.AutomationLanes != null ? track.AutomationLanes.Count : 0;
            return VolLaneH + extras * AutomLaneH + LaneH;
        }

        // The lane-column content for a track: a thin filler when collapsed, else the volume sub-track + additional
        // automation lanes (one per AutomationLane) + module lane.
        UIElement MakeTrackRow(TimelineTrack track, double laneWidth, bool fillItems = true)
        {
            if (track.Collapsed)
                return MakeCollapsedLane(track, laneWidth);
            var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Left };
            var vol = new Controls.TimelineEditor.VolumeLaneControl { HorizontalAlignment = HorizontalAlignment.Left };
            vol.Configure(track, PxPerBeat, VolLaneH, laneWidth);
            stack.Children.Add(vol);
            if (track.AutomationLanes != null)
            {
                foreach (var ln in track.AutomationLanes)
                {
                    var auto = new Controls.TimelineEditor.AutomationLaneControl { HorizontalAlignment = HorizontalAlignment.Left };
                    auto.Configure(track, ln, PxPerBeat, AutomLaneH, laneWidth);
                    stack.Children.Add(auto);
                }
            }
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

            // Clic droit n'importe où sur un en-tête de piste : sélectionner cette piste, puis ouvrir le menu
            // d'organisation. Posé ICI, avant les DEUX return de la méthode, donc valable pour l'en-tête replié,
            // l'en-tête déplié ET l'en-tête de la piste d'accords dockée (RenderChordDock passe par la même
            // méthode). PREVIEW + Handled pour qu'un enfant (zone de nom, combo, curseur) ne l'avale pas ni
            // n'affiche son propre menu système ; sur le bouton RELÂCHÉ, comme ModuleBoxControl, sinon le menu se
            // refermerait aussitôt.
            border.PreviewMouseRightButtonUp += (s, e) => { e.Handled = true; SelectTrack(track); ShowTrackContextMenu(track, border); };

            var panel = new StackPanel();
            var top = new StackPanel { Orientation = Orientation.Horizontal };
            // Collapse / expand toggle (issue #5): shrinks this track's header + lane to a minimal height (title + button)
            // so many tracks fit on screen without scrolling. State lives on the track (persisted with the project).
            var collapseBtn = new Button
            {
                Content = track.Collapsed ? "▸" : "▾", Width = 18, Height = 18, Padding = new Thickness(0), FontSize = 10,
                Margin = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center, Cursor = Cursors.Hand,
                Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = Brushes.White,
                ToolTip = track.Collapsed ? Loc.T("DeplierLaPiste") : Loc.T("ReplierLaPisteGagnerDeLa")
            };
            collapseBtn.Click += (s, e) => { track.Collapsed = !track.Collapsed; Render(); };
            top.Children.Add(collapseBtn);
            // Instrument-family colour dot (fixed in the header, so always visible even when the lane scrolls).
            var famDot = new Ellipse { Width = 9, Height = 9, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0), Fill = new SolidColorBrush(HeaderFamilyColor(track)) };
            top.Children.Add(famDot);
            var name = new TextBox { Text = track.Name, Width = 88, FontSize = 11 };
            name.LostFocus += (s, e) => track.Name = name.Text;
            // Sur la zone de nom, c'est le menu système de la TextBox (Couper/Copier/Coller) qui s'ouvrirait :
            // le neutraliser pour que le clic droit y donne le MÊME menu de piste qu'ailleurs sur l'en-tête.
            name.ContextMenuOpening += (s, e) => e.Handled = true;
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
            var scoreChk = new CheckBox { Content = "♫", FontFamily = new FontFamily("Segoe UI Symbol"), IsChecked = scoreTracks.Contains(track), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0), Cursor = Cursors.Hand, ToolTip = Loc.T("AfficherCettePisteDansLaPartition") };
            scoreChk.Checked += (s, e) => { scoreTracks.Add(track); viewScore = true; RefreshScore(); }; // checking ♫ shows the score
            scoreChk.Unchecked += (s, e) => { scoreTracks.Remove(track); RefreshScore(); };
            top.Children.Add(scoreChk);
            if (track.Type != TimelineTrackType.Chord)   // the chords track is permanent → no delete button
            {
                var del = new Button { Content = "✕", Margin = new Thickness(4, 0, 0, 0), Cursor = Cursors.Hand, Style = (Style)FindResource("deleteIconButton"), ToolTip = Loc.T("SupprimerLaPiste") };
                del.Click += (s, e) => DeleteTrack(track);   // même chemin que « Supprimer la piste » du menu contextuel (annulable)
                top.Children.Add(del);
            }
            panel.Children.Add(top);

            // La piste ACCORDS n'a plus d'instrument : elle est silencieuse et ne sert qu'à fournir l'accord courant
            // au contexte (ce sont les modules « Articulation d'accord », sur les pistes instrument, qui sonnent).
            if (track.Type == TimelineTrackType.Chord)
            {
                panel.Children.Add(new TextBlock
                {
                    Text = Loc.T("PisteAccordsSilencieuse"),
                    Foreground = "#777C85".ToBrush(), FontSize = 10, TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 3, 0, 0),
                });
            }
            else if (track.Type != TimelineTrackType.Drum)   // instrument tracks pick their instrument (drums = kit)
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
                // Le combo GM est grisé quand un VSTi OU un plugin Koton natif est actif : le patch GM n'est
                // plus rendu (le plugin le remplace, exclusion mutuelle appliquée à la sélection).
                if (!string.IsNullOrEmpty(track.VstiPath) || !string.IsNullOrEmpty(track.KotonInstrumentId))
                    inst.IsEnabled = false;
                panel.Children.Add(inst);
            }
            else
            {
                // Drum track: pick the SoundFont drum kit (Standard / Room / Power / Jazz / TR-808…) — applied at playback.
                var kitNames = InstrumentCatalog.DrumKitNames();
                var kit = new ComboBox { Margin = new Thickness(0, 3, 0, 0), FontSize = 11, ItemsSource = kitNames, SelectedIndex = Math.Max(0, Math.Min(kitNames.Count - 1, track.DrumKit)) };
                kit.SelectionChanged += (s, e) =>
                {
                    if (kit.SelectedIndex < 0) return;
                    track.DrumKit = kit.SelectedIndex;
                    // If this drum track's editor is open, rebuild it so the preview uses the new kit.
                    if (riffEditTrack == track && selectedItem?.Module is DrumPatternModule dpm)
                        editorHost.Content = BuildDrumEditor(track, selectedItem, dpm);
                };
                if (!string.IsNullOrEmpty(track.VstiPath) || !string.IsNullOrEmpty(track.KotonInstrumentId))
                    kit.IsEnabled = false;
                panel.Children.Add(kit);
            }

            // La piste ACCORDS ne produit aucun son : ni VSTi, ni volume, ni muet/solo (un « solo » sur elle
            // rendrait d'ailleurs tout le reste silencieux). Elle n'expose que ses accords.
            if (track.Type == TimelineTrackType.Chord)
            {
                border.Child = panel;
                trackHeaders[track] = border;
                border.PreviewMouseLeftButtonDown += (s, e) => SelectTrack(track);
                return border;
            }

            // Bouton VSTi : ouvre un menu (choix, éditer, retirer). L'étiquette montre le nom du plugin actif
            // ou "VSTi…" quand aucun n'est chargé. Un ⚠ apparaît si le plugin a été référencé mais est
            // introuvable (fichier déplacé/désinstallé). Disponible sur les pistes sonores — un VSTi peut
            // être une drum machine, un synthé mélodique ou un pad d'accompagnement.
            panel.Children.Add(BuildVstiRow(track));

            // base volume
            var volRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 0) };
            volRow.Children.Add(new TextBlock { Text = Loc.T("Vol"), Foreground = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99)), FontSize = 10, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) });
            var vol = new Slider { Minimum = 0, Maximum = 1.5, Width = 80, VerticalAlignment = VerticalAlignment.Center, SmallChange = 0.05 };
            vol.SetBinding(Slider.ValueProperty, new System.Windows.Data.Binding("Volume") { Source = track, Mode = System.Windows.Data.BindingMode.TwoWay }); // sync with the mixer
            volRow.Children.Add(vol);
            // Mute / Solo (take effect on the next playback)
            var mute = new System.Windows.Controls.Primitives.ToggleButton { Content = "M", Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Style = (Style)FindResource("MuteToggle"), ToolTip = Loc.T("MuetSilenceCettePiste") };
            mute.SetBinding(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, new System.Windows.Data.Binding("Mute") { Source = track, Mode = System.Windows.Data.BindingMode.TwoWay });
            var solo = new System.Windows.Controls.Primitives.ToggleButton { Content = "S", Margin = new Thickness(3, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Style = (Style)FindResource("SoloToggle"), ToolTip = Loc.T("SoloNEntendreQueLesPistes") };
            solo.SetBinding(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, new System.Windows.Data.Binding("Solo") { Source = track, Mode = System.Windows.Data.BindingMode.TwoWay });
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
            var lane = new Controls.TimelineEditor.TempoLaneControl { HorizontalAlignment = HorizontalAlignment.Left };
            lane.Configure(width, TempoH, PxPerBeat, project.Tempo);
            return lane;
        }

        // Header for the chord trame lane: a title + the "auto transpose" toggle.
        Border MakeChordHeader(double height)
        {
            var border = new Border { Height = height, BorderBrush = TrackSeparatorBrush, BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(6, 1, 4, 1) };
            var sp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            sp.Children.Add(new TextBlock { Text = Loc.T("Accords"), Foreground = Brushes.White, FontWeight = FontWeights.Bold, FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
            var at = new CheckBox { Content = Loc.T("AutoTransp"), Foreground = Brushes.White, FontSize = 10, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), IsChecked = autoTransposeChords, Cursor = Cursors.Hand, ToolTip = Loc.T("CocheChangerUnAccordTransposeAUSSI") };
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
                    var r = project.RiffById(pr.RiffId);
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
                System.Windows.MessageBox.Show(Loc.T("DisponibleUniquementSurUneMusiqueStructu"), Loc.T("AjouterUneLigneMelodique"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            int existing = 0;
            foreach (var t in project.Tracks) if (t.Name != null && t.Name.StartsWith("Ligne mélodique")) existing++;

            var lead = FullLineOfTrack("Mélodie");
            int seed = arr.Seed + 7919 * (existing + 1);
            var line = Engine.Timeline.ArrangementEngine.BuildExtraVoice(arr, lead, seed);
            if (line == null || line.Count == 0)
            {
                System.Windows.MessageBox.Show(Loc.T("ImpossibleDeComposerLaLigneArrangement"), Loc.T("AjouterUneLigneMelodique"), MessageBoxButton.OK, MessageBoxImage.Information);
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
                project.Riffs.Add(br);
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
            var lane = new Controls.TimelineEditor.ChordLaneControl { HorizontalAlignment = HorizontalAlignment.Left };
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
            txtEditorTitle.Text = Loc.T("Editeur");
            editorHost.Content = null;
            UpdateToolbar();
            // Le sous-menu Générateur Koton filtre selon le type de la piste sélectionnée — refresh
            // ItemsSource pour refléter le nouveau contexte (batterie n'affiche pas Melody, etc.).
            RefreshKotonGeneratorMenu();
        }

        // Delete an item (leaf or repeat). Whatever follows it stays in place: the freed time (the
        // deleted item's own silence + its displayed length) is transferred onto the next item's
        // SilenceBefore. Deleting the last module of a Repeat shrinks the Repeat and pushes the silence
        // onto the track item that follows the Repeat.
        void DeleteItem(TimelineTrack track, TimelineItem item)
        {
            PushUndo("delete:" + Id(item)); // (neutralizes a just-inserted item) — capture BEFORE the removal
            int idx = track.Items.IndexOf(item);
            if (idx >= 0) RemoveAt(track.Items, idx, null, track);
           

            if (selectedItem == item) selectedItem = null;
            Render();
        }

        void RemoveAt(System.Collections.Generic.IList<TimelineItem> list, int idx, TimelineItem containerRepeat, TimelineTrack track)
        {
            double comp = list[idx].SilenceBefore + project.DispLen(list[idx]); // freed time
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
            var canvas = new Canvas { Height = LaneH, Width = width, Background = new SolidColorBrush(LaneBgColor(track)), HorizontalAlignment = HorizontalAlignment.Left };
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
            if (item.Module is Engine.Flow.PolyChordModule) return PolyChordFill;
            if (item.Module is KotonGeneratorModule kgm)
            {
                // Couleur fournie par le plugin via GetTimelineDisplay(). Si le plugin est absent ou
                // GetTimelineDisplay jette, on tombe sur la couleur d'instrument standard — bloc lisible
                // même en cas de plugin cassé.
                var inst = Engine.Flow.KotonGeneratorRuntime.EnsureInstance(kgm);
                if (inst != null)
                {
                    try
                    {
                        var disp = inst.GetTimelineDisplay();
                        return new SolidColorBrush(disp.Background);
                    }
                    catch { }
                }
                return new SolidColorBrush(Controls.InstrumentColors.BoxFill(track.Instrument));
            }
            return new SolidColorBrush(Controls.InstrumentColors.BoxFill(track.Instrument));
        }

        // A collapsed track still shows WHERE its modules are: a thin strip of colour rectangles at each module's
        // position + length (same colours as the full boxes), so the lane doesn't read as empty (issue #5 follow-up).
        Canvas MakeCollapsedLane(TimelineTrack track, double width)
        {
            var canvas = new Canvas { Height = CollapsedH, Width = width, Background = new SolidColorBrush(LaneBgColor(track)), HorizontalAlignment = HorizontalAlignment.Left };
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
                double len = project.DispLen(item);
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
                cursor += project.DispLen(item); // compact: a Repeat advances by one cycle
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
        // Accords polyrythmiques : fuchsia, pour se distinguer immédiatement des accords classiques (bleu) sur la piste.
        static readonly System.Windows.Media.Brush PolyChordFill = Flat(0xA8, 0x2A, 0x6A);
        static readonly System.Windows.Media.Brush PolyChordBorder = Flat(0xE1, 0x44, 0x96);
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
            double len = TimelineProject.ItemLength(item, project.RiffById);
            // NO readability floor: a box must start and end exactly where its module does on the ruler (§3.6 of the
            // functional spec). The old Math.Max(40, …) widened short modules, which shifted a whole lane out of
            // alignment — invisible at 60 px/beat, glaring at 6.
            double w = Math.Max(2, len * PxPerBeat - 2);
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
            else if (item.Module is Engine.Flow.PolyChordModule) { border = PolyChordBorder; title = Loc.T("AccordsPolyrythmiques"); info = ""; }
            else if (item.Module is KotonGeneratorModule kg)
            {
                // Titre + border = ce que le plugin publie via GetTimelineDisplay(). Le texte affiché
                // dans la vignette EST celui du plugin ; on garde info = "" (le plugin est libre de
                // mettre la durée dans son texte s'il veut). Bord = variante légèrement claire du
                // background pour rester cohérent avec le thème pro sombre.
                var inst = Engine.Flow.KotonGeneratorRuntime.EnsureInstance(kg);
                if (inst != null)
                {
                    try
                    {
                        var disp = inst.GetTimelineDisplay();
                        title = disp.Text ?? inst.DisplayName ?? "?";
                        // Bord ~25% plus clair que le fond, pour distinguer sans clash de couleurs.
                        var bg = disp.Background;
                        byte lighten(byte v) => (byte)Math.Min(255, v + 40);
                        border = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(bg.A, lighten(bg.R), lighten(bg.G), lighten(bg.B)));
                    }
                    catch { border = new System.Windows.Media.SolidColorBrush(Controls.InstrumentColors.BoxBorder(track.Instrument)); }
                }
                else
                {
                    // Plugin absent : bord de piste + titre grisé pour l'attention.
                    border = new System.Windows.Media.SolidColorBrush(Controls.InstrumentColors.BoxBorder(track.Instrument));
                    title = "⚠ " + (kg.GeneratorId ?? "?");
                }
                info = "";
            }
            else   // riff / drum / melodic-line boxes: background + border tinted by the track's INSTRUMENT FAMILY
            {
                border = new System.Windows.Media.SolidColorBrush(Controls.InstrumentColors.BoxBorder(track.Instrument));
            }
            box.Configure(title, info, w, height, sel, interactive, opacity, fill, border);
            box.SetBigLabel(bigLabel);
            switch (item.Module) // cached mini-preview (orange = riff, red = chords, yellow = drums)
            {
                case PlayRiffModule pr: box.SetThumbnail(Controls.RiffThumbnail.Get(project.RiffById(pr.RiffId))); break;
                // ACCORD : aucune vignette de notes. Le module ne produit plus de son (il ne décrit que l'harmonie),
                // donc dessiner un rythme laisserait croire qu'il joue quelque chose. La case ne montre que son nom
                // et son degré en gros — l'articulation, elle, garde sa vignette puisque c'est elle qui sonne.
                case PatternGeneratorModule pg: break;
                // ARTICULATION : c'est elle qui sonne → sa vignette montre le rythme réellement joué, accords
                // de la piste Accords compris (cellule répétée sur toute la durée du bloc).
                case Engine.Flow.ChordArticulationModule cam:
                    box.SetThumbnail(Controls.RiffThumbnail.Get(
                        Engine.Timeline.ChordArticulation.Generate(cam, project, project.RiffById, startBeat),
                        Controls.RiffThumbnail.Chords));
                    break;
                case CadenceModule cm: box.SetThumbnail(Controls.RiffThumbnail.Get(PatternGenerator.GenerateCadence(cm), Controls.RiffThumbnail.Chords)); break;
                case DrumPatternModule dp: box.SetThumbnail(Controls.RiffThumbnail.GetDrums(DrumPattern.Generate(dp))); break;
                case Engine.Flow.PolyDrumModule pdm: box.SetThumbnail(Controls.RiffThumbnail.GetDrums(Engine.Flow.PolyDrum.Generate(pdm))); break;
                case MelodicLineModule ml:
                {
                    // Prefer the pitched line the engine derives from the chords; fall back to the raw rhythm skeleton
                    // (so the box still shows something when no chord is in effect). Blue, matching the melodic accent.
                    int spq = ml.SlicesPerQuarter > 0 ? ml.SlicesPerQuarter : 4;
                    var gen = Engine.Timeline.MelodicLineEngine.GenerateLine(ml, project, project.RiffById, project.Key ?? new Engine.Score.KeySignature(), startBeat);
                    if (gen == null && ml.Notes != null && ml.Notes.Count > 0)
                        gen = new Riff { Notes = new System.Collections.Generic.List<RiffNote>(ml.Notes), LengthSlices = Math.Max(1, ml.BeatsPerBar) * spq, SlicesPerQuarter = spq };
                    box.SetThumbnail(Controls.RiffThumbnail.Get(gen, Controls.RiffThumbnail.Melodic));
                    break;
                }
                case Engine.Flow.MelodicPolyModule mp:
                    box.SetThumbnail(Controls.RiffThumbnail.Get(Engine.Flow.MelodicEuclid.Generate(mp, project, project.RiffById, project.Key ?? new Engine.Score.KeySignature(), startBeat), Controls.RiffThumbnail.Melodic));
                    break;
                case Engine.Flow.PolyChordModule pcm:
                    // Panneau custom : une zone par accord (largeur ∝ Beats), séparateurs 1px + label roman/qualité.
                    // Reflète le vrai découpage temporel du module — on ne peut pas exprimer ça avec la mini-thumbnail.
                    box.SetContentPanel(BuildPolyChordPanel(pcm));
                    break;
                case KotonGeneratorModule kgm2:
                {
                    // Aperçu mini : le riff produit par le générateur — même chemin que le player,
                    // donc ce qu'on voit est exactement ce qui va sonner. Nul possible = plugin
                    // absent ou RenderNotes qui jette (le fond du bloc reste la couleur du plugin).
                    var previewRiff = Engine.Flow.KotonGeneratorRuntime.RenderRiff(kgm2, project);
                    if (previewRiff != null)
                        box.SetThumbnail(Controls.RiffThumbnail.Get(previewRiff, Controls.RiffThumbnail.Melodic));
                    break;
                }
            }
            // Thumbnails are bitmaps rendered ONCE at the reference scale (60 px/beat): scale them for DISPLAY
            // instead of re-rendering (and re-caching) one image per zoom level. Horizontal only.
            box.SetThumbnailScale(Zoom);
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
            PushUndo("move:" + Id(dragged)); // a drag emits several — coalesced into one undo entry

            double Ld = project.DispLen(dragged);

            // Absolute start of every current item (including the dragged one).
            var allStart = new double[items.Count];
            double cur = 0;
            for (int i = 0; i < items.Count; i++) { cur += items[i].SilenceBefore; allStart[i] = cur; cur += project.DispLen(items[i]); }

            // The remaining items KEEP their original absolute positions, so removing the dragged module
            // doesn't pull the ones after it leftwards — the freed gap becomes the next item's larger
            // SilenceBefore (it grows by the moved module's footprint).
            int n = items.Count - 1;
            var rest = new TimelineItem[n];
            var s = new double[n];
            var L = new double[n];
            int k = 0;
            for (int i = 0; i < items.Count; i++)
                if (i != di) { rest[k] = items[i]; s[k] = allStart[i]; L[k] = project.DispLen(items[i]); k++; }

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
                prev = starts[i] + project.DispLen(order[i]);
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
            string beats = Math.Round(len, 2) + Loc.T("Temps");
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
                case Engine.Flow.PolyDrumModule pdm:
                {
                    int nl = 0; if (pdm.Layers != null) foreach (var l in pdm.Layers) if (l != null && !l.Muted) nl++;
                    double cyc = Engine.Flow.PolyDrum.CycleBeats(pdm);
                    // Le cycle commun est l'information que l'oreille cherche : au bout de combien de temps tout
                    // retombe ensemble. On ne l'affiche que s'il diffère de la longueur du module.
                    string cs = (cyc > 0 && Math.Abs(cyc - Engine.Flow.PolyDrum.TotalBeats(pdm)) > 1e-6)
                              ? $" · {Loc.T("Cycle")} {Math.Round(cyc, 2)}" : "";
                    return $"{Loc.T("Polyrythmique")} · {nl} {Loc.T("Calques")}{cs} · {beats}";
                }
                case Engine.Flow.MelodicPolyModule mp:
                {
                    int nv = 0; if (mp.Layers != null) foreach (var v in mp.Layers) if (v != null && !v.Muted) nv++;
                    double cyc = Engine.Flow.MelodicEuclid.CycleBeats(mp);
                    string cs = (cyc > 0 && Math.Abs(cyc - Engine.Flow.MelodicEuclid.TotalBeats(mp)) > 1e-6)
                              ? $" · {Loc.T("Cycle")} {Math.Round(cyc, 2)}" : "";
                    return $"{Loc.T("Polyrythmique")} · {nv} {Loc.T("Calques")}{cs} · {beats}";
                }
                case Engine.Flow.PolyChordModule pcm2:
                {
                    int nc = pcm2.Chords?.Count ?? 0;
                    int nl = 0; if (pcm2.Layers != null) foreach (var l in pcm2.Layers) if (l != null && !l.Muted) nl++;
                    return $"{nc} {Loc.T("Accords")} · {nl} {Loc.T("Calques")} · {beats}";
                }
                case PlayRiffModule pr:
                    { var r = project.RiffById(pr.RiffId); return (r != null ? r.Name : Loc.T("Aucun")) + " · " + beats; }
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
            BeginEditSessionFor(item); // start an undo "edit session" for this module (recorded on leave if it changes)
            bool selfScroll = false; // editor manages its own scrolling -> disable the outer scroll
            if (item == null)
            {
                txtEditorTitle.Text = Loc.T("Editeur");
                editorHost.Content = new TextBlock
                {
                    Text = Loc.T("SelectionneUnModuleRiffAccordBatterie"),
                    Foreground = "#777C85".ToBrush(), FontSize = 12, TextWrapping = TextWrapping.Wrap,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(20), MaxWidth = 420, TextAlignment = TextAlignment.Center,
                };
            }
            else if (item.Module is PlayRiffModule pr) { txtEditorTitle.Text = Loc.T("EditeurRiff"); editorHost.Content = BuildRiffEditor(track, pr); selfScroll = true; }
            else if (item.Module is PatternGeneratorModule pg) { txtEditorTitle.Text = Loc.T("EditeurAccords"); var ce = new Controls.ChordEditorControl(); ce.Show(project, track, pg, this); editorHost.Content = ce; selfScroll = true; }
            else if (item.Module is CadenceModule cm) { txtEditorTitle.Text = Loc.T("EditeurCadence"); editorHost.Content = BuildCadenceEditor(track, cm); }
            else if (item.Module is DrumPatternModule dp) { txtEditorTitle.Text = Loc.T("EditeurBatterie"); editorHost.Content = BuildDrumEditor(track, item, dp); selfScroll = true; }
            else if (item.Module is Engine.Flow.PolyDrumModule pdm2) { txtEditorTitle.Text = Loc.T("EditeurBatteriePolyrythmique"); editorHost.Content = BuildPolyDrumEditor(track, item, pdm2); selfScroll = true; }
            else if (item.Module is Engine.Flow.MelodicPolyModule mpm) { txtEditorTitle.Text = Loc.T("EditeurLigneMelodiquePolyrythmique"); editorHost.Content = BuildMelodicPolyEditor(track, item, mpm); selfScroll = true; }
            else if (item.Module is Engine.Flow.PolyChordModule pcmm) { txtEditorTitle.Text = Loc.T("EditeurAccordsPolyrythmiques"); editorHost.Content = BuildPolyChordEditor(track, item, pcmm); selfScroll = true; }
            else if (item.Module is Engine.Flow.ChordArticulationModule cam) { txtEditorTitle.Text = Loc.T("EditeurArticulationAccord"); editorHost.Content = BuildChordArticulationEditor(track, item, cam); selfScroll = true; }
            else if (item.Module is MelodicLineModule ml) { txtEditorTitle.Text = Loc.T("EditeurLigneMelodique"); editorHost.Content = BuildMelodicLineEditor(track, item, ml); selfScroll = true; }
            // Un module générateur Koton natif : le plugin fournit son UserControl WPF, on l'affiche
            // dans le panneau du bas avec une barre Preview/Stop (voir TimelineScreen.KotonGenerator.cs).
            // Le UserControl vit tant que ce panneau reste actif — bouger un slider dedans affecte le
            // prochain flatten audio via l'instance vivante partagée (KotonGeneratorRuntime.EnsureInstance).
            else if (item.Module is KotonGeneratorModule kgm) { txtEditorTitle.Text = Loc.T("KotonEditorTitle"); editorHost.Content = BuildKotonGeneratorEditor(track, item, kgm); selfScroll = true; }
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
                double len = Math.Max(1e-6, project.DispLen(it));
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
            var tog = new System.Windows.Controls.Primitives.ToggleButton { Style = toolStyle, Content = Loc.T("Editer"), IsChecked = scoreEditMode, Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(0, 0, 8, 0), Cursor = Cursors.Hand };
            tog.Checked += (s, e) => { scoreEditMode = true; HookScoreKeys(true); RefreshScore(); };
            tog.Unchecked += (s, e) => { scoreEditMode = false; HookScoreKeys(false); selNoteMidi = -1; RefreshScore(); Render(); }; // Render → refresh riff thumbnails edited on the staff
            bar.Children.Add(tog);
            if (scoreEditMode)
            {
                bar.Children.Add(ScoreLbl("Octave"));
                var oct = new TextBox { Width = 38, Text = editOctave.ToString(), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 8, 0) };
                oct.LostFocus += (s, e) => { if (int.TryParse(oct.Text, out int v)) editOctave = Math.Max(0, Math.Min(9, v)); };
                bar.Children.Add(oct);
                bar.Children.Add(ScoreLbl(Loc.T("Duree")));
                var durNames = new[] { Loc.T("DoubleCroche2"), Loc.T("Croche"), Loc.T("Noire"), Loc.T("Blanche"), Loc.T("Ronde") };
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
                var dot = new System.Windows.Controls.Primitives.ToggleButton { Style = toolStyle, Content = NoteIcon(0, dotOnly: true), IsChecked = editDotted, ToolTip = Loc.T("NotePointee"), Width = 26, Height = 28, Padding = new Thickness(0), Margin = new Thickness(4, 0, 8, 0), Cursor = Cursors.Hand };
                dot.Checked += (s, e) => editDotted = true; dot.Unchecked += (s, e) => editDotted = false;
                bar.Children.Add(dot);
                bar.Children.Add(ScoreLbl(Loc.T("Voix")));
                for (int v = 0; v < 5; v++)
                {
                    int vv = v;
                    var tb = new System.Windows.Controls.Primitives.ToggleButton { Style = toolStyle, Content = (v + 1).ToString(), IsChecked = editVoice == v, Width = 26, Height = 28, Padding = new Thickness(0), Margin = new Thickness(0, 0, 2, 0), Cursor = Cursors.Hand };
                    tb.Checked += (s, e) => { editVoice = vv; RefreshScore(); };   // exclusive: the rebuild rechecks only the active one
                    bar.Children.Add(tb);
                }
                bar.Children.Add(new TextBlock { Text = Loc.T("CDEFGA"), Foreground = "#888888".ToBrush(), FontSize = 10, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 440, Margin = new Thickness(8, 0, 0, 0) });
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
            catch (Exception ex) { txtEditorTitle.Text = Loc.T("EditionPartitionErreur") + ex.Message; e.Handled = true; }
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
                double len = Math.Max(1e-6, project.DispLen(it));
                if (rawBeat >= cur - 1e-6 && rawBeat < cur + len - 1e-6)
                {
                    if (!(it.Module is PlayRiffModule pr)) return false;   // Accords/Batterie/Cadence… = read-only
                    riff = project.RiffById(pr.RiffId); if (riff == null) return false;
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
            if (Math.Abs(project.DispLen(riffEditItem) - riffOpenLen) < 1e-6)
            {
                if (leafBoxes.TryGetValue(riffEditItem, out var box))
                {
                    if (riffEditItem.Module is PlayRiffModule pr) box.SetThumbnail(Controls.RiffThumbnail.Get(project.RiffById(pr.RiffId)));
                    else if (riffEditItem.Module is DrumPatternModule dp) box.SetThumbnail(Controls.RiffThumbnail.GetDrums(DrumPattern.Generate(dp)));
                }
            }
            else { RefreshTrackLane(riffEditTrack ?? selectedTrack); riffOpenLen = project.DispLen(riffEditItem); }
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

            TimelineProject.ResolveLoops(project, project.RiffById);
            double laneWidth = TotalBeats() * PxPerBeat;
            measureRuler.Configure(laneWidth, 20, PxPerBeat, TimelineHelper.RulerBeatsPerBar(project), project.PickupBeats);
            if (startCanvas != null) startCanvas.Width = laneWidth;
            RefreshMarkers();   // in-place path (a riff edit changed the length): the band must widen with the ruler

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
                    var tk = project.Tracks[i - 1];
                    if (st.Children[0] is Controls.TimelineEditor.VolumeLaneControl vl) vl.Configure(tk, PxPerBeat, VolLaneH, laneWidth);
                    // Extra automation lanes vivent entre le volume et la lane de modules ; les élargir aussi
                    // pour rester alignés avec la règle de mesure et le lecteur.
                    for (int c = 1; c < st.Children.Count - 1; c++)
                        if (st.Children[c] is Controls.TimelineEditor.AutomationLaneControl al && tk.AutomationLanes != null && (c - 1) < tk.AutomationLanes.Count)
                            al.Configure(tk, tk.AutomationLanes[c - 1], PxPerBeat, AutomLaneH, laneWidth);
                    var last = st.Children[st.Children.Count - 1];
                    if (last is System.Windows.Controls.Canvas cv) cv.Width = laneWidth;
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
            sp.Children.Add(EdLabel(Loc.T("Repetitions2")));
            sp.Children.Add(ParamNum(g.Count, v => { if (v > 0) g.Count = v; }, Render));
            var loop = new CheckBox { Content = Loc.T("BouclerJusquALaFin"), Foreground = Brushes.White, FontSize = 11, Margin = new Thickness(0, 6, 0, 0), IsChecked = g.Loop };
            loop.Checked += (s, e) => { g.Loop = true; Render(); };
            loop.Unchecked += (s, e) => { g.Loop = false; Render(); };
            sp.Children.Add(loop);
            sp.Children.Add(new TextBlock { Text = Loc.T("RepeatSelectionneRiffAccordsBatterieAjou"), Foreground = "#888888".ToBrush(), FontSize = 10, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) });
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
            var combo = new ComboBox { Width = 200, ItemsSource = project.Riffs, DisplayMemberPath = "Name", SelectedValuePath = "Id" };
            var neu = new Button { Content = Loc.T("Nouveau"), Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(8, 2, 8, 2), Cursor = Cursors.Hand };
            top.Children.Add(combo); top.Children.Add(neu);
            // "Appliquer le thème": treat THIS riff as the theme → copy it into the theme riff and regenerate the derived
            // sections (ré-expo / développement / conclusion) + the counter from the chord trame. Only for composed arrangements.
            var applyTheme = new Button { Content = Loc.T("AppliquerLeTheme"), Margin = new Thickness(14, 0, 0, 0), Padding = new Thickness(8, 2, 8, 2), Cursor = Cursors.Hand, ToolTip = Loc.T("ReporterCeRiffCommeThemeRe") };
            applyTheme.Visibility = (IsComposedArrangement()) ? Visibility.Visible : Visibility.Collapsed;
            applyTheme.Click += (s, e) => ApplyThemeFromRiff(pr);
            top.Children.Add(applyTheme);
            // "Ne pas écraser": lock THIS section's riff so "Appliquer le thème" leaves it untouched.
            var sec0 = TimelineHelper.SectionForRiff(project, pr.RiffId);
            var protect = new CheckBox { Content = Loc.T("NePasEcraser"), Foreground = Brushes.White, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0), Cursor = Cursors.Hand, ToolTip = Loc.T("ProtegeCeRiffDeLaRegeneration") };
            protect.Visibility = (sec0 != null) ? Visibility.Visible : Visibility.Collapsed;
            protect.IsChecked = sec0 != null && sec0.Protected;
            protect.Checked += (s, e) => { var sc = TimelineHelper.SectionForRiff(project, pr.RiffId); if (sc != null) sc.Protected = true; };
            protect.Unchecked += (s, e) => { var sc = TimelineHelper.SectionForRiff(project, pr.RiffId); if (sc != null) sc.Protected = false; };
            top.Children.Add(protect);
            var aiBtn = new Button { Content = Loc.T("GenererIA"), Margin = new Thickness(14, 0, 0, 0), Padding = new Thickness(8, 2, 8, 2), Cursor = Cursors.Hand, Style = (Style)FindResource("okButton"), ToolTip = Loc.T("DecrisUneIntentionLIAEcrit") };
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
                riffEditItem = editedItem; riffEditTrack = track; riffOpenLen = project.DispLen(editedItem); riffDirty = false;
                if (rerender) Render();
            };

            rg.GridChanged += () =>
            {
                // The draft becomes a real library riff the moment it gets content.
                if (draft != null && rg.CurrentNotes().Count > 0)
                {
                    project.Riffs.Add(draft);
                    combo.SelectedValue = draft.Id; // now listed -> show it selected
                    draft = null;
                }
                var rr = project.RiffById(pr.RiffId);
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
                    var r = project.RiffById(id);
                    if (r != null) show(r, false, true);
                }
            };
            neu.Click += (s, e) => show(new Riff { Name = "Riff " + (project.Riffs.Count + 1) }, true, true);

            // Initial content: the module's existing riff, or auto-"Nouveau" (a draft). No Render on open.
            var cur = project.RiffById(pr.RiffId);
            if (cur != null) show(cur, false, false);
            else show(new Riff { Name = "Riff " + (project.Riffs.Count + 1) }, true, false);
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
                System.Windows.MessageBox.Show(Loc.T("AucunArrangementARegenererComposezD"), Loc.T("AppliquerLeTheme"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var src = project.RiffById(pr.RiffId);
            if (src == null || src.Notes == null || src.Notes.Count == 0)
            {
                System.Windows.MessageBox.Show(Loc.T("LeRiffEstVideRienA"), Loc.T("AppliquerLeTheme"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var theme = new System.Collections.Generic.List<Engine.RiffNote>(src.Notes);
            var changes = Engine.Timeline.ArrangementEngine.RegenerateFromTheme(arr, theme);
            int applied = 0;
            foreach (var ch in changes) { var r = project.RiffById(ch.riffId); if (r != null) { r.Notes = ch.notes; applied++; } }
            arr.Theme = theme;       // this riff is now the canonical theme
            CommitRiffEditor();      // close the inline editor; the riff boxes/score are about to be redrawn
            Render();
            RefreshScore();
            System.Windows.MessageBox.Show(Loc.T("ThemeReporteSur") + applied + Loc.T("RiffSReExpositionDeveloppementConclusion"), Loc.T("AppliquerLeTheme"), MessageBoxButton.OK, MessageBoxImage.Information);
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
                foreach (var rf in Engine.Timeline.ArrangementEngine.RefitMelodyToTrame(arr, id => { var r = project.RiffById(id); return r != null ? r.Notes : null; }))
                { var r = project.RiffById(rf.riffId); if (r != null) r.Notes = rf.notes; }
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
                    var r = project.RiffById(pr.RiffId);
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

            // Continue from the chords track's last chord: propose starting from its degree.
            int startDeg = 0;
            var lastChord = TimelineHelper.LastChordOn(chord);
            if (lastChord != null)
                startDeg = Engine.Flow.MusicTheory.DegreeOf(key, ((lastChord.Root % 12) + 12) % 12);

            var dlg = new Dialogs.CadenceDialog(startDeg, false, -1) { Owner = Window.GetWindow(this) };
            if (dlg.ShowDialog() != true) return;

            int measureBeats = Math.Max(1, TimelineHelper.RulerBeatsPerBar(project));
            int cpm = Math.Max(1, Math.Min(dlg.ChordsPerMeasure, measureBeats));   // each chord ≥ 1 beat
            int chordBeats = Math.Max(1, (int)Math.Round(measureBeats / (double)cpm));
            int numChords = Math.Max(1, dlg.Measures) * cpm;
            const int octave = 4;

            var chords = BuildCadenceChords(key, dlg.StartDegree, numChords, dlg.StyleIndex, octave);
            if (chords.Count == 0) return;

            CommitRiffEditor();
            // Une cadence pose UNIQUEMENT des accords : degré/qualité/couleur/suspension/mode + durée. Aucun style
            // d'articulation, aucune basse, aucun voicing, aucun motif personnalisé — c'est le module « Articulation
            // d'accord », posé sur une piste instrument, qui décide comment ces accords sont joués.
            TimelineItem firstItem = null;
            foreach (var ch in chords)
            {
                var dc = Engine.Flow.ChordDegrees.DegColour(key, ch.root, ch.quality);   // degree-lock (follows key) when the chord is diatonic
                var pg = new PatternGeneratorModule
                {
                    Root = ch.root, Quality = ch.quality,
                    Degree = dc.degree, DiatonicColour = dc.colour, Suspension = dc.suspension, ModeOverride = dc.mode,   // by DEGREE (else absolute for chromatic/secondary chords)
                    Octave = octave,
                    BeatsPerBar = chordBeats, Repeats = 1,
                };
                var it = new TimelineItem { Module = pg };
                TimelineHelper.InsertTopLevel(chord, it);
                if (firstItem == null) firstItem = it;
            }
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
            var labels = new string[voices + 1]; labels[0] = Loc.T("Basse");
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

            var ok = new Button { Content = Loc.T("Appliquer"), Width = 96, IsDefault = true, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(8, 3, 8, 3) };
            var cancel = new Button { Content = Loc.T("Annuler"), Width = 96, IsCancel = true, Padding = new Thickness(8, 3, 8, 3) };
            var btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(10) };
            btns.Children.Add(ok); btns.Children.Add(cancel);
            var dock = new DockPanel(); DockPanel.SetDock(btns, Dock.Bottom); dock.Children.Add(btns); dock.Children.Add(grid);
            var win = new Window
            {
                Title = Loc.T("MotifPersonnalise"), Width = 760, Height = 380, Owner = Window.GetWindow(this),
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
            return parts.Count == 0 ? Loc.T("Vide") : string.Join("   ·   ", parts);
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
            sp.Children.Add(EdLabel(Loc.T("StyleDeCadence")));
            sp.Children.Add(ParamCombo(Engine.Flow.MusicTheory.CadenceStyles, cm.CadenceStyle, v => cm.CadenceStyle = v, () => { }));

            sp.Children.Add(EdLabel(Loc.T("DepuisLeDegre")));
            var degNames = new[] { Loc.T("ITonique"), "ii", "iii", "IV", "V", "vi", "vii" };
            sp.Children.Add(ParamCombo(degNames, Math.Max(0, Math.Min(6, cm.StartDegree)), v => cm.StartDegree = v, () => { }));

            sp.Children.Add(EdLabel(Loc.T("Mesures")));
            sp.Children.Add(ParamNum(cm.Measures, v => cm.Measures = v, () => { }));
            sp.Children.Add(EdLabel(Loc.T("AccordsMesure")));
            sp.Children.Add(ParamNum(cm.ChordsPerMeasure, v => cm.ChordsPerMeasure = v, () => { }));

            // Rendering settings (apply immediately to the stored chords).
            sp.Children.Add(EdLabel(Loc.T("RythmeArticulation")));
            // Full list INCLUDING "Personnalisé…" (CustomStyle) so a hand-drawn motif can drive the whole cadence.
            sp.Children.Add(ParamCombo(PatternGenerator.StyleNames, Math.Max(0, Math.Min(cm.Style, PatternGenerator.StyleNames.Length - 1)), v => cm.Style = v, refresh));
            var editMotif = new Button { Content = Loc.T("EditerLeMotifPersonnalise"), Margin = new Thickness(0, 6, 0, 0), Padding = new Thickness(8, 3, 8, 3), HorizontalAlignment = HorizontalAlignment.Left, Cursor = Cursors.Hand };
            editMotif.Click += (s, e) => EditCadenceMotif(track, cm, refresh);
            sp.Children.Add(editMotif);

            sp.Children.Add(EdLabel(Loc.T("Octave")));
            sp.Children.Add(ParamNum(cm.Octave, v => cm.Octave = v, refresh));

            // Voice-leading: re-pick each chord's INVERSION for smooth motion (re-voices the existing chords, no re-roll).
            sp.Children.Add(EdLabel(Loc.T("RenversementVoiceLeading")));
            sp.Children.Add(ParamCombo(VoiceLeadModeNames, Math.Max(0, Math.Min(3, cm.VoiceLeadMode)), v => cm.VoiceLeadMode = v, () =>
            {
                var vlKey = project.Key ?? new Engine.Score.KeySignature();
                cm.Chords = RevoiceCadence(vlKey, cm.Chords, cm.VoiceLeadMode, cm.Octave);
                showChords(); refresh();
            }));
            var cadOpen = new CheckBox { Content = Loc.T("VoicingOuvertEcarte"), Foreground = Brushes.White, FontSize = 11, Margin = new Thickness(0, 6, 0, 0), IsChecked = cm.OpenVoicing };
            cadOpen.Checked += (s, e) => { cm.OpenVoicing = true; refresh(); };
            cadOpen.Unchecked += (s, e) => { cm.OpenVoicing = false; refresh(); };
            sp.Children.Add(cadOpen);

            sp.Children.Add(EdLabel(Loc.T("BasseFondamentale")));
            sp.Children.Add(ParamCombo(BassModeNames, !cm.Bass ? 0 : (cm.BassPerBeat ? 2 : 1), v => { cm.Bass = v > 0; cm.BassPerBeat = v == 2; }, refresh));
            sp.Children.Add(EdLabel(Loc.T("MonteeStylesArpege")));
            sp.Children.Add(ParamCombo(ClimbModeNames, cm.ClimbMode, v => cm.ClimbMode = v, refresh));
            sp.Children.Add(EdLabel(Loc.T("NoteTenueStylesArpege")));
            sp.Children.Add(ParamCombo(HeldModeNames, cm.HeldMode, v => cm.HeldMode = v, refresh));
            var halve = new CheckBox { Content = Loc.T("DoublesCroches2StylesArpege"), Foreground = Brushes.White, FontSize = 11, Margin = new Thickness(0, 6, 0, 0), IsChecked = cm.HalveDurations };
            halve.Checked += (s, e) => { cm.HalveDurations = true; refresh(); };
            halve.Unchecked += (s, e) => { cm.HalveDurations = false; refresh(); };
            sp.Children.Add(halve);

            var regen = new Button { Content = Loc.T("RegenererVariante"), Margin = new Thickness(0, 10, 0, 0), Padding = new Thickness(8, 3, 8, 3), HorizontalAlignment = HorizontalAlignment.Left, Cursor = Cursors.Hand };
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

            sp.Children.Add(EdLabel(Loc.T("AccordsGeneres")));
            showChords();
            sp.Children.Add(chordList);
            sp.Children.Add(new TextBlock { Text = Loc.T("ChangeUnParametreStyleDegreMesures"), Foreground = "#888888".ToBrush(), FontSize = 10, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), MaxWidth = 540 });
            return sp;
        }



        

        // Drum: left = the node-like fields, right = the manual grid (when "Personnalisé").
        UIElement BuildDrumEditor(TimelineTrack track, TimelineItem item, DrumPatternModule dp)
        {
            // Baseline for the deferred, on-leave box refresh (the grid persists live via GridChanged, but the
            // timeline thumbnail is only rebuilt when we leave the editor — a full Render per stroke is too slow).
            riffEditItem = item; riffEditTrack = track; riffOpenLen = project.DispLen(item); riffDirty = false;

            var grid = TwoColumns(out StackPanel left, out ContentControl host);
            Action refresh = null;
            refresh = () => { RefreshDrumGrid(host, track, item, dp); Render(); };

            var aiBtn = new Button { Content = Loc.T("GenererIA"), Margin = new Thickness(0, 0, 0, 8), Cursor = Cursors.Hand, Style = (Style)FindResource("okButton"), ToolTip = Loc.T("DecrisUneIntentionDeGrooveL") };
            aiBtn.Click += (s, e) => GenerateDrumWithAi(dp, refresh);
            left.Children.Add(aiBtn);

            // The drum KIT is chosen per TRACK (in the track header), not per module → applied both here (preview) and at playback.
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

            left.Children.Add(EdLabel(Loc.T("Categorie"))); left.Children.Add(cboCat);
            left.Children.Add(EdLabel(Loc.T("Motif"))); left.Children.Add(cboMotif);

            var custBtn = new Button { Content = Loc.T("Personnaliser"), Margin = new Thickness(0, 2, 0, 6), Padding = new Thickness(10, 4, 10, 4), Cursor = Cursors.Hand, HorizontalAlignment = HorizontalAlignment.Left, ToolTip = Loc.T("CopierCeMotifDansUnMotif") };
            custBtn.Click += (s, e) => { TimelineHelper.CustomizeDrum(dp); syncing = true; cboCat.SelectedItem = CUSTOM; syncing = false; FillMotifs(CUSTOM, CUSTOM); refresh(); };
            left.Children.Add(custBtn);

            // ---- Décalage + génération euclidienne -------------------------------------------------------------
            // Les deux agissent sur UNE ligne de percussion à la fois : superposer = répéter l'opération. Le motif
            // produit reste un motif personnalisé ordinaire, donc modifiable à la main dans la grille.
            var laneNames = DrumPattern.LaneNames;
            int euLane = 0, euK = 3, euN = 8, euRot = 0, euUnit = 0;
            var stepNames = new[] { Loc.T("Croche"), Loc.T("DoubleCroche"), Loc.T("TrioletDeCroche") };
            var stepSlices = new[] { 12, 6, 8 };   // sur la grille à 24 slices/noire : tous exacts

            left.Children.Add(EdLabel(Loc.T("EuclidLigne")));
            var cboLane = new ComboBox { Width = 180, HorizontalAlignment = HorizontalAlignment.Left, ItemsSource = laneNames, SelectedIndex = 0 };
            cboLane.SelectionChanged += (s, e) => { if (cboLane.SelectedIndex >= 0) euLane = cboLane.SelectedIndex; };
            left.Children.Add(cboLane);

            // Décalage : des boutons plutôt qu'un champ, parce qu'un décalage se cherche à l'oreille par essais
            // successifs et non en saisissant une valeur connue d'avance.
            left.Children.Add(EdLabel(Loc.T("Decalage")));
            var shiftRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
            Action<int> shift = dir =>
            {
                PushUndo("euclid:rot");
                TimelineHelper.RotateDrumLane(dp, euLane, dir * stepSlices[Math.Max(0, euUnit)]);
                editorHost.Content = BuildDrumEditor(track, item, dp); Render();
            };
            foreach (var (glyph, dir) in new[] { ("◀", -1), ("▶", +1) })
            {
                var b = new Button { Content = glyph, Width = 34, Margin = new Thickness(0, 0, 6, 0), Padding = new Thickness(0, 2, 0, 2), Cursor = Cursors.Hand, ToolTip = Loc.T("DecalerCetteLigneDUnPas") };
                int d = dir; b.Click += (s, e) => shift(d);
                shiftRow.Children.Add(b);
            }
            left.Children.Add(shiftRow);

            // Génération euclidienne, repliée par défaut.
            var euPanel = new StackPanel();
            var euExp = new Expander { Header = Loc.T("RepartirRegulierement"), Foreground = Brushes.White, FontSize = 11, Margin = new Thickness(0, 4, 0, 6), Content = euPanel };
            var preview = new TextBlock { FontFamily = new FontFamily("Consolas"), FontSize = 13, Foreground = "#1FB6C3".ToBrush(), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 4) };
            Action redraw = () =>
            {
                var pat = Engine.Flow.EuclideanRhythm.Rotate(Engine.Flow.EuclideanRhythm.Pattern(euK, euN), euRot);
                var sb = new System.Text.StringBuilder();
                foreach (var on in pat) sb.Append(on ? '●' : '·');
                string nm = Engine.Flow.EuclideanRhythm.NameFor(pat);
                if (nm != null) sb.Append("   « ").Append(nm).Append(" »");
                // Combien de coups tombent sur un temps : ce qui ancre l'harmonie d'une ligne mélodique, et ce que
                // le décalage fait varier sans changer le rythme perçu.
                int spb = DrumPattern.SlicesPerQuarter, st = stepSlices[Math.Max(0, euUnit)], onBeat = 0;
                for (int i = 0; i < pat.Length; i++) if (pat[i] && (i * st) % spb == 0) onBeat++;
                sb.Append('\n').Append(Loc.T("SurLesTemps")).Append(' ').Append(onBeat);
                if ((euN * st) % (Math.Max(1, dp.BeatsPerBar) * spb) != 0) sb.Append("   ").Append(Loc.T("SeDecaleDUneMesureALAutre"));
                preview.Text = sb.ToString();
            };
            euPanel.Children.Add(EdLabel(Loc.T("Coups"))); euPanel.Children.Add(ParamNum(euK, v => euK = Math.Max(0, v), redraw));
            euPanel.Children.Add(EdLabel(Loc.T("Pas"))); euPanel.Children.Add(ParamNum(euN, v => euN = Math.Max(1, v), redraw));
            euPanel.Children.Add(EdLabel(Loc.T("Decalage"))); euPanel.Children.Add(ParamNum(euRot, v => euRot = v, redraw));
            euPanel.Children.Add(EdLabel(Loc.T("Unite"))); euPanel.Children.Add(ParamCombo(stepNames, 0, v => euUnit = v, redraw));
            euPanel.Children.Add(preview);
            var euApply = new Button { Content = Loc.T("Appliquer"), Margin = new Thickness(0, 2, 0, 2), Padding = new Thickness(10, 4, 10, 4), Cursor = Cursors.Hand, HorizontalAlignment = HorizontalAlignment.Left };
            euApply.Click += (s, e) =>
            {
                PushUndo("euclid:gen");
                TimelineHelper.ApplyEuclideanDrum(dp, euLane, euK, euN, euRot, stepSlices[Math.Max(0, euUnit)]);
                editorHost.Content = BuildDrumEditor(track, item, dp); Render();
            };
            euPanel.Children.Add(euApply);
            redraw();
            left.Children.Add(euExp);

            left.Children.Add(EdLabel(Loc.T("Densite"))); left.Children.Add(ParamCombo(DrumPattern.DensityNames, dp.Density, v => dp.Density = v, refresh));
            var fill = new CheckBox { Content = Loc.T("FillSurLaDerniereMesure"), Foreground = Brushes.White, FontSize = 11, Margin = new Thickness(0, 6, 0, 0), IsChecked = dp.FillLast };
            fill.Checked += (s, e) => { dp.FillLast = true; }; fill.Unchecked += (s, e) => { dp.FillLast = false; };
            left.Children.Add(fill);
            left.Children.Add(EdLabel(Loc.T("TempsMesure"))); left.Children.Add(ParamNum(dp.BeatsPerBar, v => dp.BeatsPerBar = v, refresh));
            left.Children.Add(EdLabel(Loc.T("Repetitions"))); left.Children.Add(ParamNum(dp.Repeats, v => dp.Repeats = v, refresh));

            RefreshDrumGrid(host, track, item, dp);
            return grid;
        }

        
        void RefreshDrumGrid(ContentControl host, TimelineTrack track, TimelineItem item, DrumPatternModule dp)
        {
            if (!TimelineHelper.DrumIsCustom(dp))
            {
                host.Content = new TextBlock { Text = Loc.T("MotifDuCatalogueAppliqueCliquePersonnali"), Foreground = "#888888".ToBrush(), FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(10) };
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
                string name = TimelineHelper.PromptText(Loc.T("EnregistrerLeMotifBatterie"), string.IsNullOrEmpty(dp.CatMotif) || dp.CatMotif == "Personnalisé" ? Loc.T("MonMotif") : dp.CatMotif);
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
                         InstrumentCatalog.GetDrumKit(track.DrumKit),
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
            var riff = project.RiffById(pr.RiffId);
            if (riff == null) return;
            int barTemps = TimelineHelper.RulerBeatsPerBar(project);
            double startBeat = project.ItemStartBeat(track, editedItem);
            double lenBeats = Math.Max(barTemps, project.DispLen(editedItem));
            int measures = Math.Max(1, (int)Math.Round(lenBeats / Math.Max(1, barTemps)));
            string keyStr = txtKeySummary?.Text ?? "";
            string meterStr = txtMeterSummary?.Text ?? (project.TimeSigNum + "/" + project.TimeSigDen);
            var chords = TimelineHelper.ChordsUnder(project,startBeat, lenBeats, barTemps);
            bool hasChords = chords.Count > 0;
            string ctx = hasChords
                ? $"Tonalité {keyStr} · {meterStr} · {measures} mes. · {chords.Count} accord(s) sous le riff"
                : $"Tonalité {keyStr} · {meterStr} · {measures} mes. · aucun accord (l'IA en proposera)";
            var dlg = new Dialogs.AiElementDialog(Loc.T("RiffIA"), ctx,
                intention => Engine.AI.AiArrangement.BuildRiffPrompt(keyStr, meterStr, barTemps, measures, chords, intention)) { Owner = Window.GetWindow(this) };
            if (dlg.ShowDialog() != true || string.IsNullOrWhiteSpace(dlg.ResultJson)) return;
            try { 
                TimelineHelper.ApplyAiRiff(
                    project,
                    track, pr, editedItem, rg, riff, dlg.ResultJson, barTemps, measures, hasChords,
                    out riffEditItem, out riffEditTrack, out riffDirty
                    );
            }
            catch (Exception ex) { MessageBox.Show(Loc.T("ReponseIAInvalide") + ex.Message, Loc.T("RiffIA"), MessageBoxButton.OK, MessageBoxImage.Warning); }
        }

        
        // "Track ▸ Ajouter un instrument (IA)…" / "…une batterie (IA)…": describe an intention and the AI — given the
        // WHOLE current piece (every track's notes + the chords) — writes ONE new track (rhythm-only melodic line or
        // full melody, or a drum groove) laid OVER the same bars as a fresh track. Reuses the shared AI element dialog.
        void btnAddInstrumentAi_Click(object sender, RoutedEventArgs e) => AddTrackWithAi(drums: false);
        void btnAddDrumsAi_Click(object sender, RoutedEventArgs e) => AddTrackWithAi(drums: true);

        void AddTrackWithAi(bool drums)
        {
            CommitRiffEditor();
            int barTemps = TimelineHelper.RulerBeatsPerBar(project);
            string fullCtx = Engine.AI.AiArrangement.BuildFullPieceContext(project, out int measures);
            string keyStr = txtKeySummary?.Text ?? "";
            string meterStr = txtMeterSummary?.Text ?? (project.TimeSigNum + "/" + project.TimeSigDen);
            string shortCtx = keyStr + " · " + meterStr + " · " + measures + " mes.";

            string title = drums ? Loc.T("AddDrumsAI") : Loc.T("AddAnInstrumentAI");
            string optionLabel = drums ? null : Loc.T("FullMelody");   // checked = full melody, unchecked = melodic line

            Dialogs.AiElementDialog dlg = null;
            dlg = new Dialogs.AiElementDialog(
                title, shortCtx,
                intention => drums
                    ? Engine.AI.AiArrangement.BuildAddDrumsPrompt(fullCtx, barTemps, measures, intention)
                    : Engine.AI.AiArrangement.BuildAddTrackPrompt(fullCtx, barTemps, measures, dlg.OptionChecked, intention),
                optionLabel: optionLabel)
            { Owner = Window.GetWindow(this) };

            if (dlg.ShowDialog() != true || string.IsNullOrWhiteSpace(dlg.ResultJson)) return;
            try
            {
                var a = Engine.AI.AiArrangement.ParseTrack(dlg.ResultJson);
                Engine.AI.AiArrangementPlacer.AddTrack(project, a, fixRiffNotes: false);
                Render();
                RefreshScore();
            }
            catch (Exception ex)
            {
                MessageBox.Show(Loc.T("ReponseIAInvalide") + ex.Message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        // ===== Batterie polyrythmique et ligne mélodique polyrythmique ============================================
        // Les deux éditeurs ont été déportés dans des UserControls (Controls/TimelineEditor/PolyDrumEditor.xaml et
        // MelodicPolyEditor.xaml) : le squelette (colonnes + roue + boutons figés) est en XAML, chaque carte de calque
        // vient d'un DataTemplate bindé à un EuclidLayer/EuclidVoice observable. Ici on ne fait que les instancier
        // avec un PolyEditorHost qui leur passe les hooks vers l'écran (Undo/Render/SelectItem/SoundFontGuard).

        Controls.TimelineEditor.PolyEditorHost MakePolyHost()
            => new Controls.TimelineEditor.PolyEditorHost
            {
                Project = project,
                PushUndo = PushUndo,
                Render = Render,
                SelectItem = SelectItem,
                EditorHost = editorHost,
                EnsureSoundFont = (win, action) => SoundFontGuard.EnsureReady(win, action),
            };

        UIElement BuildPolyDrumEditor(TimelineTrack track, TimelineItem item, Engine.Flow.PolyDrumModule pd)
            => new Controls.TimelineEditor.PolyDrumEditor(track, item, pd, MakePolyHost());

        UIElement BuildMelodicPolyEditor(TimelineTrack track, TimelineItem item, Engine.Flow.MelodicPolyModule mp)
            => new Controls.TimelineEditor.MelodicPolyEditor(track, item, mp, MakePolyHost());

        UIElement BuildPolyChordEditor(TimelineTrack track, TimelineItem item, Engine.Flow.PolyChordModule pc)
            => new Controls.TimelineEditor.PolyChordEditor(track, item, pc, MakePolyHost());

        // Panneau custom dessiné dans la box timeline d'un module PolyChord : une zone par accord (largeur ∝ Beats),
        // séparateurs verticaux 1px et label (roman + qualité). Reflète la structure temporelle du module — la
        // mini-thumbnail piano-roll utilisée par les autres modules ne peut pas exprimer ce découpage variable.
        FrameworkElement BuildPolyChordPanel(Engine.Flow.PolyChordModule pc)
        {
            var grid = new Grid();
            if (pc?.Chords == null || pc.Chords.Count == 0)
            {
                grid.Children.Add(new TextBlock
                {
                    Text = Loc.T("PolyChordVide"),
                    Foreground = "#DDDDDD".ToBrush(), FontSize = 10, HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                });
                return grid;
            }
            // Une colonne par accord, largeur = GridLength en étoile pondérée par Beats. Le layout WPF divise
            // l'espace disponible proportionnellement — pas besoin de connaître la largeur de la box en px.
            for (int i = 0; i < pc.Chords.Count; i++)
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(1, pc.Chords[i].Beats), GridUnitType.Star) });

            var key = project.Key ?? new Engine.Score.KeySignature();
            for (int i = 0; i < pc.Chords.Count; i++)
            {
                var c = pc.Chords[i];
                var cell = new Grid();
                // Séparateur gauche (sauf pour le premier accord) — 1px, moitié transparent, tracé DANS la cellule.
                if (i > 0)
                {
                    var sep = new System.Windows.Shapes.Rectangle { Width = 1, Fill = "#66FFFFFF".ToBrush(), HorizontalAlignment = HorizontalAlignment.Left };
                    cell.Children.Add(sep);
                }
                var label = new TextBlock
                {
                    Text = ChordFunctionLabel(c, key),
                    Foreground = "#FFFFFF".ToBrush(), FontSize = 24, FontWeight = FontWeights.Bold,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    // Même halo noir que le gros label roman des accords classiques (txtBig) — reste lisible sur
                    // les cellules aux limites de la boîte fuchsia.
                    Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = Colors.Black, ShadowDepth = 0, BlurRadius = 5, Opacity = 0.7 }
                };
                cell.Children.Add(label);
                Grid.SetColumn(cell, i);
                grid.Children.Add(cell);
            }
            return grid;
        }

        // « I », « V7 », « ♭III » … pour un PolyChordItem. Réutilise la logique ChordRoman en la ré-exprimant sur
        // les champs de PolyChordItem (pas de PatternGeneratorModule sous la main).
        string ChordFunctionLabel(Engine.Flow.PolyChordItem c, Engine.Score.KeySignature key)
        {
            // Cas simple : si l'accord est en degré, on affiche le romain avec le suffixe de qualité (° / +).
            string q = TimelineHelper.Get(PatternGenerator.QualityNames, c.Quality);
            if (c.Degree >= 0)
            {
                string[] rU = { "I", "II", "III", "IV", "V", "VI", "VII" };
                string[] rL = { "i", "ii", "iii", "iv", "v", "vi", "vii" };
                Engine.Flow.MusicTheory.ChordShape(c.Quality, out bool minor, out bool dim, out bool aug, out _);
                string suffix = dim ? "°" : (aug ? "+" : "");
                return (minor ? rL[c.Degree] : rU[c.Degree]) + suffix;
            }
            // Accord fixe : d'abord les FONCTIONS secondaires (une V/V posée dans l'éditeur doit se lire « V/V » sur la
            // boîte, comme pour un accord ordinaire — même source de vérité MusicTheory que ChordRoman).
            int secDom = Engine.Flow.MusicTheory.SecondaryDominantTarget(key, c.Root, c.Quality);
            if (secDom >= 0) return "V/" + Engine.Flow.ChordDegreeChoices.Roman(key, secDom);
            int secLt = Engine.Flow.MusicTheory.SecondaryLeadingToneTarget(key, c.Root, c.Quality);
            if (secLt >= 0) return "vii°/" + Engine.Flow.ChordDegreeChoices.Roman(key, secLt);
            // Sinon le nom réel (« Do Maj7 ») — plus lisible qu'un romain calculé qui pourrait paraître arbitraire.
            return Engine.Score.KeySig.SpellPc(c.Root, key) + " " + q;
        }

        // « Insérer ▸ Accords polyrythmiques » : crée un nouveau module PolyChord AVEC un premier accord (I) et
        // deux anneaux de longueurs différentes, comme les autres modules polyrythmiques (un seul anneau n'a rien
        // à déphaser). Va TOUJOURS sur la piste Accords (via AppendChord).
        // Onglet actif de l'éditeur d'articulation (accompagnement / cellule mélodique), conservé entre deux
        // reconstructions de l'éditeur pour ne pas éjecter l'utilisateur de l'onglet où il travaille.
        int articulationTabIndex;

        // Éditeur du bloc d'articulation : uniquement des paramètres de RÉALISATION (aucun paramètre d'accord —
        // l'harmonie vient de la piste Accords). Durée libre, donc « Durée du bloc » est un réglage de premier plan.
        UIElement BuildChordArticulationEditor(TimelineTrack track, TimelineItem item, Engine.Flow.ChordArticulationModule ca)
        {
            riffEditItem = item; riffEditTrack = track; riffOpenLen = project.DispLen(item); riffDirty = false;

            var grid = TwoColumns(out StackPanel left, out ContentControl host);
            // Changer de style doit faire apparaître (ou disparaître) la grille de motif → on reconstruit l'éditeur.
            Action rebuild = () => { editorHost.Content = BuildChordArticulationEditor(track, item, ca); Render(); };
            Action refresh = () => Render();

            left.Children.Add(new TextBlock
            {
                Text = Loc.T("ArticulationSuitAccords"),
                Foreground = "#8A8F98".ToBrush(), FontSize = 11, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8),
            });

            left.Children.Add(EdLabel(Loc.T("CelluleTemps")));
            left.Children.Add(ParamNum((int)Math.Round(ca.Beats), v => { if (v > 0) ca.Beats = v; }, rebuild));

            left.Children.Add(EdLabel(Loc.T("DureeTotaleTemps")));
            left.Children.Add(ParamNum((int)Math.Round(Engine.Timeline.ChordArticulation.TotalBeats(ca)),
                                       v => { if (v > 0) ca.LengthBeats = v; }, refresh));

            // Styles intégrés PUIS styles utilisateur enregistrés dans le projet : choisir un style utilisateur
            // recharge son motif (et sa longueur) dans le bloc, exactement comme sur l'ancien éditeur d'accord.
            var userStyles = project.UserChordStyles ?? (project.UserChordStyles = new System.Collections.Generic.List<UserChordStyle>());
            var styleNames = new System.Collections.Generic.List<string>(PatternGenerator.StyleNames);
            foreach (var us in userStyles) styleNames.Add(us.Name);
            int styleSel = ca.Style;
            if (ca.Style == PatternGenerator.CustomStyle && !string.IsNullOrEmpty(ca.UserStyleName))
            {
                int u = userStyles.FindIndex(s => s.Name == ca.UserStyleName);
                if (u >= 0) styleSel = PatternGenerator.StyleNames.Length + u;
            }
            left.Children.Add(EdLabel(Loc.T("Style")));
            left.Children.Add(ParamCombo(styleNames.ToArray(), styleSel, v =>
            {
                if (v < PatternGenerator.StyleNames.Length) { ca.Style = v; ca.UserStyleName = null; }
                else
                {
                    int u = v - PatternGenerator.StyleNames.Length;
                    if (u >= 0 && u < userStyles.Count)
                    {
                        var us = userStyles[u];
                        ca.Style = PatternGenerator.CustomStyle;
                        ca.UserStyleName = us.Name;
                        ca.SetCustomNotes(us.Notes != null ? new System.Collections.Generic.List<RiffNote>(us.Notes) : null, us.Spb, us.Beats * us.Spb);
                        if (us.Slices != null && us.Slices.Length > 0) ca.CustomSlices = (SequencerSlice[])us.Slices.Clone();
                        if (us.Beats > 0) ca.Beats = us.Beats;
                    }
                }
            }, rebuild));

            left.Children.Add(EdLabel(Loc.T("Octave")));
            left.Children.Add(ParamNum(ca.Octave, v => ca.Octave = v, refresh));

            var bass = new CheckBox { Content = Loc.T("Basse"), Foreground = Brushes.White, FontSize = 11, Margin = new Thickness(0, 8, 0, 0), IsChecked = ca.Bass };
            bass.Checked += (s, e) => { ca.Bass = true; refresh(); };
            bass.Unchecked += (s, e) => { ca.Bass = false; refresh(); };
            left.Children.Add(bass);

            var bassBeat = new CheckBox { Content = Loc.T("BasseParTemps"), Foreground = Brushes.White, FontSize = 11, Margin = new Thickness(18, 2, 0, 0), IsChecked = ca.BassPerBeat };
            bassBeat.Checked += (s, e) => { ca.BassPerBeat = true; refresh(); };
            bassBeat.Unchecked += (s, e) => { ca.BassPerBeat = false; refresh(); };
            left.Children.Add(bassBeat);

            // Voicing ouvert : oui / non / hérité de l'intention portée par l'accord.
            left.Children.Add(EdLabel(Loc.T("VoicingOuvert")));
            int openSel = ca.OpenVoicingMode == 0 && ca.OpenVoicing ? 1 : ca.OpenVoicingMode;   // ancien booléen respecté
            left.Children.Add(ParamCombo(
                new[] { Loc.T("Non"), Loc.T("Oui"), Loc.T("SelonLAccord") },
                openSel, v => { ca.OpenVoicingMode = v; ca.OpenVoicing = v == 1; }, refresh));

            var halve = new CheckBox { Content = Loc.T("HalveDurations"), Foreground = Brushes.White, FontSize = 11, Margin = new Thickness(0, 6, 0, 0), IsChecked = ca.HalveDurations };
            halve.Checked += (s, e) => { ca.HalveDurations = true; refresh(); };
            halve.Unchecked += (s, e) => { ca.HalveDurations = false; refresh(); };
            left.Children.Add(halve);

            // Conduite des voix : appliquée à chaque CHANGEMENT d'accord sous le bloc. « Aucun » garde le
            // renversement fixe ci-dessous ; les autres modes choisissent le voicing le plus proche du précédent.
            // Ordre d'affichage : aucun / auto / haut proche / bas proche / selon l'accord. La numérotation du modèle
            // (0 aucun, 1 auto, 2 basse, 3 haut, 4 selon l'accord) est conservée — d'où la table de correspondance.
            var vlUiToModel = new[] { 0, 1, 3, 2, 4 };
            int vlSel = Array.IndexOf(vlUiToModel, ca.VoiceLeadMode); if (vlSel < 0) vlSel = 0;
            left.Children.Add(EdLabel(Loc.T("RenversementAutoVoiceLeading")));
            left.Children.Add(ParamCombo(
                new[] { Loc.T("AucunPositionFond"), Loc.T("AutoMouvementMini"), Loc.T("HautProche"), Loc.T("BasseProche"), Loc.T("SelonLAccord") },
                vlSel, v => { if (v >= 0 && v < vlUiToModel.Length) ca.VoiceLeadMode = vlUiToModel[v]; }, rebuild));

            if (ca.VoiceLeadMode == 0)   // le renversement manuel n'a de sens que sans conduite automatique
            {
                left.Children.Add(EdLabel(Loc.T("Renversement")));
                left.Children.Add(ParamNum(ca.Inversion, v => ca.Inversion = v, refresh));
            }
            else
            {
                // Tendance : départage deux voicings aussi proches l'un que l'autre.
                left.Children.Add(EdLabel(Loc.T("TendanceDirection")));
                left.Children.Add(ParamCombo(
                    new[] { Loc.T("Auto"), Loc.T("Monter"), Loc.T("Descendre") },
                    ca.VoiceLeadDirection, v => ca.VoiceLeadDirection = v, refresh));
            }

            // Panneau droit : accompagnement (grille du style « Personnalisé ») + cellule mélodique, comme l'éditeur
            // d'accord d'origine — ces deux grilles décrivent COMMENT on joue, elles appartiennent donc à l'articulation.
            var tabs = new TabControl { Margin = new Thickness(4, 0, 0, 0) };
            var accompHost = new ContentControl();
            var melodicHost = new ContentControl();
            tabs.Items.Add(new TabItem { Header = Loc.T("Accompagnement"), Content = accompHost });
            // Les réglages de l'onglet mélodique utilisent `refresh` (et non `rebuild`) : reconstruire l'éditeur
            // recréerait le TabControl et ramènerait l'utilisateur sur « Accompagnement » au moindre clic.
            tabs.Items.Add(new TabItem { Header = Loc.T("CelluleMelodique"), Content = MelodicTab(melodicHost, track, item, ca, refresh) });
            // L'onglet actif SURVIT aux reconstructions de l'éditeur (changement de style, Render…).
            tabs.SelectedIndex = Math.Max(0, Math.Min(1, articulationTabIndex));
            tabs.SelectionChanged += (s, e) => { if (ReferenceEquals(e.OriginalSource, tabs)) articulationTabIndex = tabs.SelectedIndex; };
            host.Content = tabs;

            RefreshArticulationGrid(accompHost, track, item, ca);
            RefreshArticulationMelodicGrid(melodicHost, track, item, ca);
            return grid;
        }

        // En-tête de l'onglet mélodique (octave + ancrage du degré 1) au-dessus de sa grille.
        UIElement MelodicTab(ContentControl gridHost, TimelineTrack track, TimelineItem item, Engine.Flow.ChordArticulationModule ca, Action rebuild)
        {
            var dock = new DockPanel();
            var bar = new WrapPanel { Margin = new Thickness(4, 4, 4, 6) };
            DockPanel.SetDock(bar, Dock.Top);

            bar.Children.Add(new TextBlock { Text = Loc.T("OctaveMelodie"), Foreground = "#AAAAAA".ToBrush(), FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) });
            bar.Children.Add(ParamNum(ca.MelodicOctave, v => ca.MelodicOctave = v, rebuild));

            bar.Children.Add(new TextBlock { Text = Loc.T("AncrageDegre1"), Foreground = "#AAAAAA".ToBrush(), FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 4, 0) });
            bar.Children.Add(ParamCombo(new[] { Loc.T("Tonique"), Loc.T("Renversement") }, ca.MelodicAnchor, v => ca.MelodicAnchor = v, rebuild));

            dock.Children.Add(bar);
            dock.Children.Add(gridHost);
            return dock;
        }

        // Grille de la CELLULE MÉLODIQUE : 14 rangées = degrés diatoniques 1..7 sur deux octaves, polyphonique.
        // Elle ne dépend pas de l'accord (les degrés se transposent sur chaque accord traversé), d'où des rangées fixes.
        void RefreshArticulationMelodicGrid(ContentControl host, TimelineTrack track, TimelineItem item, Engine.Flow.ChordArticulationModule ca)
        {
            var labels = new[] { "1", "2", "3", "4", "5", "6", "7", "1'", "2'", "3'", "4'", "5'", "6'", "7'" };
            int beats = Math.Max(1, (int)Math.Round(Engine.Timeline.ChordArticulation.TotalBeats(ca)));   // la PHRASE mélodique couvre TOUT le module
            var key = project.Key ?? new Engine.Score.KeySignature();

            double startBeat = project.ItemStartBeat(track, item);
            var segs = Engine.Timeline.ChordArticulation.Segments(project, project.RiffById, startBeat, Engine.Timeline.ChordArticulation.TotalBeats(ca));
            int root = segs.Count > 0 ? segs[0].Root : Engine.Flow.MusicTheory.DiatonicChord(key, 0).root;
            int quality = segs.Count > 0 ? segs[0].Quality : Engine.Flow.MusicTheory.DiatonicChord(key, 0).quality;

            var rg = new Controls.RhythmGridControl();
            Func<SequencerSlice[], int, Riff> mk = (gr, gs) =>
            {
                var t = new PatternGeneratorModule
                {
                    Root = root, Quality = quality, Inversion = ca.Inversion,
                    MelodicOctave = ca.MelodicOctave, MelodicAnchor = ca.MelodicAnchor,
                    BeatsPerBar = beats, Repeats = 1,
                };
                t.SetMelodicNotes(rg.CurrentNotes(), gs, rg.Beats * gs);
                return PatternGenerator.GenerateMelodic(t, key);
            };
            rg.Configure(labels, beats, ca.MelodicSlicesPerQuarter > 0 ? ca.MelodicSlicesPerQuarter : 4, ca.MelodicSlices,
                         new string[0], (st, b) => null, PatternGenerator.SlicesPerQuarter, mk,
                         InstrumentCatalog.GetPreset(track.Instrument),
                         noteList: true, existingNotes: ca.MelodicNotes,
                         showBeats: false);   // longueur = « Durée totale » du panneau de gauche

            bool dirty = false;
            rg.GridChanged += () => { ca.SetMelodicNotes(rg.CurrentNotes(), rg.Spb, rg.Beats * rg.Spb); dirty = true; };
            rg.Unloaded += (s, e) => { if (dirty) { dirty = false; Render(); } };
            host.Content = rg;
        }

        // Panneau droit de l'articulation : la grille de motif du style « Personnalisé » (rangées = voix de l'accord),
        // identique à celle qui vivait dans l'éditeur d'accord. Comme l'articulation ne porte pas d'accord, on dessine
        // sur l'accord ACTIF sous le bloc (à défaut la tonique du morceau) — le motif reste portable d'un accord à l'autre.
        void RefreshArticulationGrid(ContentControl host, TimelineTrack track, TimelineItem item, Engine.Flow.ChordArticulationModule ca)
        {
            if (ca.Style != PatternGenerator.CustomStyle)
            {
                host.Content = new TextBlock
                {
                    Text = Loc.T("ChoisisLeStylePersonnalisePourEditer"),
                    Foreground = "#888888".ToBrush(), FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(10),
                };
                return;
            }

            // Accord de référence pour l'aperçu : le premier accord actif sous le bloc, sinon la tonique.
            double startBeat = project.ItemStartBeat(track, item);
            int root, quality;
            var segs = Engine.Timeline.ChordArticulation.Segments(project, project.RiffById, startBeat, Engine.Timeline.ChordArticulation.TotalBeats(ca));
            if (segs.Count > 0) { root = segs[0].Root; quality = segs[0].Quality; }
            else { var d = Engine.Flow.MusicTheory.DiatonicChord(project.Key ?? new Engine.Score.KeySignature(), 0); root = d.root; quality = d.quality; }

            int beats = Math.Max(1, (int)Math.Round(Engine.Timeline.ChordArticulation.CellBeats(ca)));   // l'accompagnement dessine LA CELLULE qui boucle
            var chord = PatternGenerator.ChordNotes(root, ca.Octave, quality, ca.Inversion);
            var labels = new[] { Loc.T("Basse"), "1", "3", "5", "7", "1'", "9", "3'", "5'", "7'", "9'" };  // rangées en ordre de HAUTEUR

            var rg = new Controls.RhythmGridControl();
            Func<SequencerSlice[], int, Riff> mk = (gr, gs) =>
            {
                var t = new PatternGeneratorModule
                {
                    Root = root, Octave = ca.Octave, Quality = quality, Inversion = ca.Inversion,
                    OpenVoicing = ca.OpenVoicing, Style = PatternGenerator.CustomStyle,
                    BeatsPerBar = beats, Repeats = 1,
                };
                t.SetCustom(gr, gs);
                t.CustomNotes = rg.CurrentNotes();
                return PatternGenerator.Generate(t);
            };
            // Amorces = les motifs des styles intégrés, pour partir d'un rythme existant puis le retoucher.
            var builtin = new string[PatternGenerator.CustomStyle];
            for (int i = 0; i < builtin.Length && i < PatternGenerator.StyleNames.Length; i++) builtin[i] = PatternGenerator.StyleNames[i];
            Func<int, int, SequencerSlice[]> seedFunc = (st, b) => PatternGenerator.VoiceBarForCustom(st, b, chord.Length);

            // « Enregistrer ce style » : mémorise le motif dans le projet sous un nom, réutilisable sur d'autres blocs
            // (il apparaît alors en fin de liste des styles).
            Action onSaveStyle = () =>
            {
                string name = TimelineHelper.PromptText(Loc.T("EnregistrerLeStyleDAccompagnement"), Loc.T("MonStyle"));
                if (string.IsNullOrWhiteSpace(name)) return;
                name = name.Trim();
                var us = project.UserChordStyles ?? (project.UserChordStyles = new System.Collections.Generic.List<UserChordStyle>());
                var entry = new UserChordStyle
                {
                    Name = name, Slices = rg.CurrentGrid(), Spb = rg.Spb, Beats = rg.Beats,
                    Notes = new System.Collections.Generic.List<RiffNote>(rg.CurrentNotes()),
                };
                int existing = us.FindIndex(u => u.Name == name);
                if (existing >= 0) us[existing] = entry; else us.Add(entry);
                ca.UserStyleName = name;
                editorHost.Content = BuildChordArticulationEditor(track, item, ca);   // la liste des styles se rafraîchit
                Render();
            };

            rg.Configure(labels, beats, ca.CustomSlicesPerQuarter > 0 ? ca.CustomSlicesPerQuarter : 4, ca.CustomSlices,
                         builtin, seedFunc, PatternGenerator.SlicesPerQuarter, mk,
                         InstrumentCatalog.GetPreset(track.Instrument),
                         seedSpbFunc: null, onSaveStyle: onSaveStyle,
                         noteList: true, existingNotes: ca.CustomNotes,
                         showBeats: false);   // longueur = « Longueur de la cellule » du panneau de gauche

            bool dirty = false;
            rg.GridChanged += () =>
            {
                // La longueur de cellule vient du panneau de gauche, plus de la grille : ne pas la réécrire ici
                // (c'est cette réécriture qui pouvait écraser la valeur saisie par l'utilisateur).
                ca.SetCustomNotes(rg.CurrentNotes(), rg.Spb, rg.Beats * rg.Spb);
                dirty = true;
            };
            rg.Unloaded += (s, e) => { if (dirty) { dirty = false; Render(); } };
            host.Content = rg;
        }

        // « Articulation d'accord » : un bloc de RÉALISATION posé sur une piste INSTRUMENT. Il ne porte aucun
        // accord — à la lecture il articule l'accord actif de la piste Accords à chaque instant. Sa longueur est
        // libre : par défaut une mesure, à étirer ensuite pour couvrir autant d'accords que voulu.
        private void btnAddChordArticulation_Click(object sender, RoutedEventArgs e)
        {
            if (selectedTrack == null || selectedTrack.Type == TimelineTrackType.Chord)
            { MessageBox.Show(Loc.T("SelectionneDAbordUnePisteInstrument")); return; }
            int bpb = Math.Max(1, TimelineHelper.RulerBeatsPerBar(project));
            AppendModule(new Engine.Flow.ChordArticulationModule { Beats = bpb });
        }

        private void btnAddPolyChord_Click(object sender, RoutedEventArgs e)
        {
            // Le module ne décrit QUE le polyrythme : il lit l'accord actif de la piste Accords. Il se pose donc sur
            // une piste INSTRUMENT (c'est lui qui sonne), avec sa propre durée — plus d'accord interne à créer.
            if (selectedTrack == null || selectedTrack.Type == TimelineTrackType.Chord)
            { MessageBox.Show(Loc.T("SelectionneDAbordUnePisteInstrument")); return; }

            var m = new Engine.Flow.PolyChordModule { Mode = Engine.Flow.PolyChordMode.OneRingPerTone };
            // Deux anneaux par défaut (E(3,8) grave + E(5,8) plus haut) — donne quelque chose d'audible tout de suite.
            m.Layers.Add(new Engine.Flow.EuclidChordLayer { Hits = 3, Steps = 8, ToneIndex = 0 });
            m.Layers.Add(new Engine.Flow.EuclidChordLayer { Hits = 5, Steps = 8, ToneIndex = 1 });
            m.Beats = Math.Max(1, TimelineHelper.RulerBeatsPerBar(project));   // une mesure par défaut, à étirer ensuite
            AppendModule(m);
        }

        Grid TwoColumns(out StackPanel left, out ContentControl right)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(320) });
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
                MessageBox.Show(Loc.T("EchecDeLaComposition") + ex.Message, Loc.T("Composition"), MessageBoxButton.OK, MessageBoxImage.Error);
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
                MessageBox.Show(Loc.T("DisponibleSurUneMusiqueStructureePiste"), Loc.T("AccompagnementEnAccords"), MessageBoxButton.OK, MessageBoxImage.Information);
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
            PushUndo("generate"); // capture the pre-generation state so a big compose can be undone in one step
            // Wipe the whole timeline first, then drop in the composed tracks.
            project.Tracks.Clear();
            scoreTracks.Clear();
            activeScore = null;
            selectedItem = null;
            foreach (var r in result.Riffs) project.Riffs.Add(r);
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
                SyncTempoReadout();
            }
            else if (result.ResultBpm > 0)
            {
                if (project.Tempo == null || project.Tempo.Count == 0) project.Tempo = new System.Collections.Generic.List<TempoChange> { new TempoChange() };
                project.Tempo[0].Bpm = result.ResultBpm;
                txtBpm.Text = ((int)result.ResultBpm).ToString();
                SyncTempoReadout();
            }
            // Les sections nommées de la recette deviennent des REPÈRES sur le bandeau (« Intro », « Thème »,
            // « Ré-exposition »…). APRÈS l'adoption de la mesure : la position d'une barre dépend de TimeSigNum/Den,
            // qui viennent d'être écrasés juste au-dessus. Rien n'est posé si le compositeur n'émet pas de recette.
            TimelineHelper.AddSectionMarkersFromArrangement(project);
            SyncKeyToolbar(); // key combos + meter combo + ternary toggle follow the project
            Render();
            RefreshScore();
        }

        void AddTrack(TimelineTrack t)
        {
            string pre = BeginUndo();
            project.Tracks.Add(t);
            TimelineHelper.EnsureChordTrack(project);     // keep the chords track pinned at the bottom
            selectedTrack = t;
            selectedItem = null;
            CommitUndo(pre, "insert:" + Id(t));
            Render();
            RefreshMixer();                              // le mixeur ouvert doit voir la nouvelle piste
        }

        // ===== Organisation des pistes (dupliquer / monter / descendre / supprimer) =============================
        // Toute la logique de MODÈLE est dans TimelineHelper (CloneTrack / CanMoveTrack / MoveTrack / CopyName) ;
        // ici on n'orchestre que l'annulation, la sélection, le rendu, le mixeur et la partition.

        /// <summary>Clic droit sur un en-tête de piste → les quatre commandes d'organisation. La piste d'accords a le
        /// menu elle aussi, avec les quatre entrées GRISÉES : les montrer indisponibles est plus clair qu'un menu vide.</summary>
        void ShowTrackContextMenu(TimelineTrack track, FrameworkElement anchor)
        {
            if (track == null) return;
            bool organisable = track.Type != TimelineTrackType.Chord;   // la piste d'accords est permanente et épinglée
            var menu = new ContextMenu();

            var dup = new MenuItem { Header = Loc.T("DupliquerLaPiste"), IsEnabled = organisable };
            dup.Click += (s, e) => DuplicateTrack(track);
            var up = new MenuItem { Header = Loc.T("MonterLaPiste"), IsEnabled = TimelineHelper.CanMoveTrack(project, track, -1) };
            up.Click += (s, e) => MoveTrackBy(track, -1);
            var down = new MenuItem { Header = Loc.T("DescendreLaPiste"), IsEnabled = TimelineHelper.CanMoveTrack(project, track, +1) };
            down.Click += (s, e) => MoveTrackBy(track, +1);
            var del = new MenuItem { Header = Loc.T("SupprimerLaPiste"), IsEnabled = organisable };
            del.Click += (s, e) => DeleteTrack(track);

            menu.Items.Add(dup);
            menu.Items.Add(new Separator());
            menu.Items.Add(up);
            menu.Items.Add(down);
            menu.Items.Add(new Separator());
            // Automation MIDI par canal (Pan/Expression/Modulation/Sustain/Réverbe/Chorus/Pitch bend). Volume
            // reste géré par la lane historique (toujours affichée), donc pas offert ici.
            menu.Items.Add(BuildAddAutomationMenu(track));
            menu.Items.Add(BuildRemoveAutomationMenu(track));

            // Convertir les blocs PolyChord de cette piste en une nouvelle piste melodique (une note
            // par tick, decoupee en riffs de N mesures — utile pour extraire une "melodie" du motif).
            bool hasPolyChord = false;
            if (track.Items != null)
                foreach (var it in track.Items)
                    if (it?.Module is Engine.Flow.PolyChordModule) { hasPolyChord = true; break; }
            if (hasPolyChord)
            {
                menu.Items.Add(new Separator());
                var conv = new MenuItem { Header = Loc.T("ConvertirAccordsMelodie") };
                foreach (int bpr in new[] { 1, 2, 4, 8 })
                {
                    var sub = new MenuItem { Header = string.Format(Loc.T("XMesuresParRiff"), bpr) };
                    int captured = bpr;
                    sub.Click += (s, e) => ConvertChordsToMelody(track, captured);
                    conv.Items.Add(sub);
                }
                menu.Items.Add(conv);
            }

            menu.Items.Add(new Separator());
            menu.Items.Add(del);
            menu.PlacementTarget = anchor;
            menu.IsOpen = true;
        }

        // Convertit tous les blocs PolyChord de <paramref name="source"/> en une NOUVELLE piste Instrument
        // (meme patch MIDI que la source), avec les notes decoupees en riffs de <paramref name="barsPerRiff"/>
        // mesures. Chaque note du riff genere par PolyChord.Generate est reportee dans le segment ou tombe
        // son Start absolu, avec sa longueur eventuellement clippee a la borne du segment.
        void ConvertChordsToMelody(TimelineTrack source, int barsPerRiff)
        {
            if (source == null || source.Items == null || source.Items.Count == 0) return;
            int beatsPerBar = Math.Max(1, TimelineHelper.RulerBeatsPerBar(project));
            int riffBeats = Math.Max(1, barsPerRiff * beatsPerBar);
            const int TargetSpq = 24;   // resolution des nouveaux riffs (1/16e triolet, tres fin)

            // 1) Collecter toutes les notes de tous les PolyChordModule en beats absolus.
            var abs = new System.Collections.Generic.List<(double startBeat, double lenBeat, int midi)>();
            double cursor = 0;
            foreach (var it in source.Items)
            {
                if (it == null) continue;
                cursor += it.SilenceBefore;
                if (it.Module is Engine.Flow.PolyChordModule pcm)
                {
                    var riff = Engine.Flow.PolyChord.Generate(pcm);
                    double beatsPerSlice = 1.0 / Math.Max(1, riff.SlicesPerQuarter);
                    foreach (var n in riff.Notes)
                    {
                        double sB = cursor + n.Start * beatsPerSlice;
                        double lB = Math.Max(beatsPerSlice, n.Length * beatsPerSlice);
                        int midi = n.Note + 12;   // row grille -> MIDI
                        abs.Add((sB, lB, midi));
                    }
                }
                double dur = it.Module != null ? Engine.Flow.ModuleDuration.Beats(it.Module, project.RiffById) : 0;
                cursor += dur;
            }
            if (abs.Count == 0) { MessageBox.Show(Loc.T("AucunAccordAConvertir")); return; }

            // 2) Nouvelle piste Instrument (meme patch) juste apres la source.
            PushUndo("track:chords2mel");
            int srcIdx = project.Tracks.IndexOf(source);
            var newTrack = new TimelineTrack
            {
                Name = source.Name + " (" + Loc.T("Melodie") + ")",
                Type = TimelineTrackType.Instrument,
                Instrument = source.Instrument,
            };
            project.Tracks.Insert(srcIdx + 1, newTrack);

            // 3) Decoupage en riffs de riffBeats. Chaque note appartient au segment qui contient son Start ;
            // sa longueur est clippee a la borne du segment (evite qu'un legato deborde sur le riff suivant).
            double totalBeats = cursor;
            int numSegs = Math.Max(1, (int)Math.Ceiling(totalBeats / riffBeats));
            double lastEnd = 0;
            for (int seg = 0; seg < numSegs; seg++)
            {
                double segStart = seg * riffBeats;
                double segEnd = segStart + riffBeats;
                var segNotes = new System.Collections.Generic.List<RiffNote>();
                foreach (var (sB, lB, midi) in abs)
                {
                    if (sB < segStart - 1e-6 || sB >= segEnd - 1e-6) continue;
                    double relStart = sB - segStart;
                    double relLen = Math.Min(lB, segEnd - sB);
                    int startSlice = (int)Math.Round(relStart * TargetSpq);
                    int lenSlice = Math.Max(1, (int)Math.Round(relLen * TargetSpq));
                    int row = midi - 12;
                    if (row < 0 || row > 95) continue;
                    segNotes.Add(new RiffNote(row, startSlice, lenSlice));
                }
                if (segNotes.Count == 0) continue;
                segNotes.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : a.Note.CompareTo(b.Note));

                var newRiff = new Riff
                {
                    Id = Guid.NewGuid(),
                    Name = source.Name + " mel " + (seg + 1),
                    Notes = segNotes,
                    SlicesPerQuarter = TargetSpq,
                    LengthSlices = riffBeats * TargetSpq,
                };
                project.Riffs.Add(newRiff);
                newTrack.Items.Add(new TimelineItem
                {
                    Module = new Engine.Flow.PlayRiffModule { RiffId = newRiff.Id },
                    SilenceBefore = Math.Max(0, segStart - lastEnd),
                });
                lastEnd = segStart + riffBeats;
            }

            Render();
        }

        // Paramètres offerts dans le menu "Ajouter automation" (Volume est déjà présent en permanence via la lane
        // dédiée). ORDRE : les plus utiles en tête, Pitch bend en dernier (spécialisé).
        static readonly Engine.Timeline.AutomationParam[] AddableParams = new[]
        {
            Engine.Timeline.AutomationParam.Pan,
            Engine.Timeline.AutomationParam.Expression,
            Engine.Timeline.AutomationParam.Modulation,
            Engine.Timeline.AutomationParam.Sustain,
            Engine.Timeline.AutomationParam.ReverbSend,
            Engine.Timeline.AutomationParam.ChorusSend,
            Engine.Timeline.AutomationParam.PitchBend,
        };

        MenuItem BuildAddAutomationMenu(TimelineTrack track)
        {
            var root = new MenuItem { Header = Loc.T("AjouterAutomation") };
            if (track.AutomationLanes == null) track.AutomationLanes = new List<Engine.Timeline.AutomationLane>();
            foreach (var p in AddableParams)
            {
                bool exists = false;
                foreach (var l in track.AutomationLanes) if (l != null && l.Param == p) { exists = true; break; }
                var mi = new MenuItem { Header = Controls.TimelineEditor.AutomationLaneControl.LaneLabel(p), IsEnabled = !exists };
                var pp = p;
                mi.Click += (s, e) =>
                {
                    PushUndo("track:autom+");
                    track.AutomationLanes.Add(new Engine.Timeline.AutomationLane { Param = pp, Enabled = true });
                    Render();
                };
                root.Items.Add(mi);
            }
            return root;
        }

        MenuItem BuildRemoveAutomationMenu(TimelineTrack track)
        {
            var root = new MenuItem { Header = Loc.T("SupprimerAutomation") };
            var lanes = track.AutomationLanes;
            if (lanes == null || lanes.Count == 0) { root.IsEnabled = false; return root; }
            // Copie défensive : les Click handlers vont muter la liste ; itérer directement dessus ne casserait rien
            // (les handlers ne s'exécutent pas pendant la construction) mais un tableau explicite documente l'intention.
            var snapshot = new List<Engine.Timeline.AutomationLane>(lanes);
            foreach (var l in snapshot)
            {
                var lane = l;
                var mi = new MenuItem { Header = Controls.TimelineEditor.AutomationLaneControl.LaneLabel(lane.Param) };
                mi.Click += (s, e) =>
                {
                    PushUndo("track:autom-");
                    track.AutomationLanes.Remove(lane);
                    Render();
                };
                root.Items.Add(mi);
            }
            return root;
        }

        /// <summary>« Dupliquer la piste » : une copie complète et INDÉPENDANTE, insérée juste en dessous, sélectionnée.</summary>
        void DuplicateTrack(TimelineTrack track)
        {
            if (track == null || track.Type == TimelineTrackType.Chord) return;
            CommitRiffEditor();                       // ce qui était en cours d'édition est validé d'abord
            // BeginUndo AVANT CloneTrack : l'instantané pré-duplication ne contient donc pas les riffs neufs, et
            // annuler les fait disparaître (ApplyDocument reconstruit project.Riffs depuis l'instantané) — le
            // fichier réenregistré après annulation n'est pas plus lourd qu'avant.
            string pre = BeginUndo();
            var copy = TimelineHelper.CloneTrack(project, track);
            if (copy == null) return;
            copy.Name = TimelineHelper.CopyName(project, track.Name, Loc.T("TrackCopySuffix"));
            project.Tracks.Insert(project.Tracks.IndexOf(track) + 1, copy);
            TimelineHelper.EnsureChordTrack(project);                 // la piste d'accords reste épinglée en bas
            if (scoreTracks.Contains(track)) scoreTracks.Add(copy);   // l'état ♫ suit la copie
            selectedTrack = copy;                                     // la copie devient la piste sélectionnée…
            // …mais selectedItem / editorHost restent sur le bloc d'ORIGINE : l'éditeur du bas continue de
            // l'afficher et reste éditable (rien de perdu).
            CommitUndo(pre, "track:dup");   // clé volontairement SANS préfixe insert:/move:/edit:/vol:/delete:
            Render();
            ScrollTrackIntoViewLater(copy);
            RefreshMixer();
            if (ScoreVisible) RefreshScore();
        }

        /// <summary>« Monter » / « Descendre » : échange avec la voisine. Neutre pour le son (mêmes blocs, mêmes
        /// positions) ; seul l'ordre d'affichage, du mixeur et des portées suit.</summary>
        void MoveTrackBy(TimelineTrack track, int delta)
        {
            if (!TimelineHelper.CanMoveTrack(project, track, delta)) return;
            CommitRiffEditor();
            PushUndo("track:move");         // une entrée par déplacement : clé NON préfixée « move: » → jamais fusionnée
            TimelineHelper.MoveTrack(project, track, delta);
            selectedTrack = track;          // la piste déplacée reste sélectionnée
            Render();
            ScrollTrackIntoViewLater(track);
            RefreshMixer();
            if (ScoreVisible) RefreshScore();   // l'ordre des portées suit l'ordre des pistes
        }

        /// <summary>« Supprimer la piste » — partagé par la croix ✕ de l'en-tête et le menu contextuel. DÉSORMAIS
        /// ANNULABLE (Ctrl+Z restitue la piste à sa place, avec ses blocs, ses réglages et sa case ♫).</summary>
        void DeleteTrack(TimelineTrack track)
        {
            if (track == null || track.Type == TimelineTrackType.Chord) return;
            CommitRiffEditor();
            PushUndo("track:del");          // capture AVANT le retrait ; clé non préfixée « delete: » → aucune neutralisation
            // Libère l'éventuelle instance VSTi cachée pour cette piste — sans ça, l'instance survivrait
            // dans VstInstrumentCache jusqu'à la fermeture de l'onglet, même si la piste n'existe plus
            // et ne sera jamais rejouée. Undo restaure la piste avec le même identifiant (référence) mais
            // le cache aura été vidé — le prochain Play recréera proprement une nouvelle instance.
            MusicTracker.Engine.Timeline.Effects.VstInstrumentCache.ReleaseTrack(track);
            project.Tracks.Remove(track);
            scoreTracks.Remove(track);
            if (selectedTrack == track) selectedTrack = null;
            // L'éditeur du bas peut être ouvert sur un bloc de la piste supprimée : le vider et débrancher
            // l'éditeur de riff, sinon RefreshEditedRiffBox re-calerait plus tard une piste qui n'existe plus.
            if (selectedItem != null && track.Items != null && track.Items.Contains(selectedItem))
            {
                selectedItem = null;
                riffEditItem = null; riffEditTrack = null; riffDirty = false;
                editorHost.Content = null;
                txtEditorTitle.Text = Loc.T("Editeur");
            }
            Render();
            RefreshMixer();
            RefreshScore();                 // retire la portée, ou ramène l'éditeur de module si plus aucune ♫
        }

        // ---- défilement vertical vers une piste ---------------------------------------------------------------

        /// <summary>Amène la ligne d'une piste dans la vue, VERTICALEMENT. Toujours piloter laneScroll :
        /// laneScroll_ScrollChanged recopie son offset sur headerScroll — faire défiler headerScroll directement
        /// désynchroniserait les deux moitiés.</summary>
        void ScrollTrackIntoView(TimelineTrack track)
        {
            if (track == null || laneScroll == null) return;
            if (track.Type == TimelineTrackType.Chord) return;              // la lane d'accords est dockée, toujours visible
            double y = TempoH + (IsComposedArrangement() ? ChordH : 0);     // lignes dessinées avant les pistes
            foreach (var t in project.Tracks)
            {
                if (t == track) break;
                if (t.Type == TimelineTrackType.Chord) continue;
                y += TrackRowH(t);
            }
            double h = TrackRowH(track), top = laneScroll.VerticalOffset, view = laneScroll.ViewportHeight;
            if (y < top) laneScroll.ScrollToVerticalOffset(y);
            else if (y + h > top + view) laneScroll.ScrollToVerticalOffset(Math.Max(0, y + h - view));
        }

        // Juste après Render(), l'étendue du ScrollViewer est encore l'ANCIENNE (la mise en page n'a pas tourné) et
        // une demande de défilement serait bornée à un maximum périmé. On diffère donc après la mise en page.
        void ScrollTrackIntoViewLater(TimelineTrack track)
            => Dispatcher.BeginInvoke(new Action(() => ScrollTrackIntoView(track)),
                                      System.Windows.Threading.DispatcherPriority.Loaded);

        /// <summary>Le mixeur est NON MODAL : ajout / retrait / réordonnancement de pistes peuvent survenir pendant
        /// qu'il est ouvert, et sa liste source est une List&lt;T&gt; nue (aucune notification de collection).</summary>
        void RefreshMixer() { try { mixerWindow?.RefreshTracks(); } catch { mixerWindow = null; } }

        // Insert a chord module — ALWAYS into the chords track (no need to select it first).
        void AppendChord(FlowModule m)
        {
            string pre = BeginUndo();
            TimelineHelper.EnsureChordTrack(project);
            var chord = TimelineHelper.ChordTrack(project);
            var item = new TimelineItem { Module = m };
            TimelineHelper.InsertTopLevel(chord, item);
            selectedTrack = chord; SelectItem(chord, item);
            CommitUndo(pre, "insert:" + Id(item));
            Render();
        }

        void AppendModule(FlowModule m)
        {
            if (selectedTrack == null) { MessageBox.Show(Loc.T("SelectionneDAbordUnePiste")); return; }
            // If a Repeat is selected, add INSIDE it (its sub-track); keep the Repeat selected so you
            string pre = BeginUndo();
            var item = new TimelineItem { Module = m }; // inserted after the block at the cursor (truncated to fit), else appended
            double len = Engine.Timeline.TimelineProject.ItemLength(item, project.RiffById);
            TimelineHelper.PlaceAtCursor(selectedTrack, item, len, startBeat, project.RiffById);
            SelectItem(selectedTrack, item);
            CommitUndo(pre, "insert:" + Id(item));
            Render(); // new box -> rebuild lanes
        }

        // +Riff: create a 1-measure empty riff and drop it in the first free slot AT/after the selected item
        // (just behind it if there's room, else the first later gap big enough, else at the end). Default = 1 bar.
        private void btnAddRiff_Click(object sender, RoutedEventArgs e)
        {
            if (selectedTrack == null) { MessageBox.Show(Loc.T("SelectionneDAbordUnePiste")); return; }
            var track = selectedTrack;
            string pre = BeginUndo();

            int temps = TimelineHelper.RulerBeatsPerBar(project);                // one bar in temps: num in /4, num/3 in /8
            const int spq = 24;                            // canonical resolution: 1 temps = 24 slices (like imports)
            var riff = new Riff { Name = "Riff " + (project.Riffs.Count + 1), LengthSlices = temps * spq, SlicesPerQuarter = spq };
            project.Riffs.Add(riff);
            var item = new TimelineItem { Module = new PlayRiffModule { RiffId = riff.Id } };

            TimelineHelper.PlaceAtCursor(track, item, temps, startBeat, project.RiffById);

            SelectItem(track, item); // open the riff editor on the new 1-measure riff
            CommitUndo(pre, "insert:" + Id(item));
            Render();
        }

        
        private void btnAddPattern_Click(object sender, RoutedEventArgs e)
        {
            TimelineHelper.EnsureChordTrack(project);
            var chord = TimelineHelper.ChordTrack(project);                                 // chords ALWAYS go to the dedicated chords track
            var lastItem = chord.Items.Count > 0 ? chord.Items[chord.Items.Count - 1] : null;
            // Cas particulier : si le dernier bloc est un module PolyChord, on APPEND l'accord à sa liste au lieu
            // d'insérer un nouveau bloc — c'est le comportement attendu quand l'utilisateur enchaîne les accords
            // sur un module d'accords polyrythmiques (voir plan). Le dialogue d'accord est le même.
            if (lastItem?.Module is Engine.Flow.PolyChordModule pcm)
            {
                var key0 = project.Key ?? new Engine.Score.KeySignature();
                ChordContext(chord, lastItem, out int[] pd, out int bi, out int pl);
                var lastCh = pcm.Chords.Count > 0 ? pcm.Chords[pcm.Chords.Count - 1] : null;
                int seedDeg = lastCh != null && lastCh.Degree >= 0 ? lastCh.Degree : 0;
                var newDegs = pd.Length > 0 ? pd : new[] { seedDeg };
                var dlg = new Dialogs.ChordSuggestionDialog(newDegs, bi, pl, key0, InstrumentCatalog.GetPreset(chord.Instrument)) { Owner = Window.GetWindow(this) };
                if (dlg.ShowDialog() != true) return;
                string pre = BeginUndo();
                int beats = lastCh != null ? lastCh.Beats : Math.Max(1, TimelineHelper.RulerBeatsPerBar(project));
                var it = new Engine.Flow.PolyChordItem { Beats = beats };
                if (dlg.ChosenIsDiatonic)
                {
                    var ch = Engine.Flow.MusicTheory.DiatonicChord(key0, dlg.ChosenDegree, dlg.ChosenColour, dlg.ChosenSuspension, dlg.ChosenMode);
                    it.Root = ch.root; it.Quality = ch.quality;
                    it.DiatonicColour = dlg.ChosenColour; it.Suspension = dlg.ChosenSuspension; it.ModeOverride = dlg.ChosenMode;
                    it.Degree = dlg.ChosenDegree;
                }
                else { it.Root = dlg.ChosenRoot; it.Quality = dlg.ChosenQuality; it.Degree = -1; }
                pcm.Chords.Add(it);
                Engine.Flow.ChordDegrees.Revoice(chord);
                CommitUndo(pre, "polychord-append");
                Render();
                return;
            }
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

        /// <summary>Pendant polyrythmique : ouvre le dialogue AI Poly et pose le résultat dans un NOUVEL onglet.</summary>
        public event Action ComposePolyInNewTabRequested;
        void btnAiPolyCompose_Click(object sender, RoutedEventArgs e) => ComposePolyInNewTabRequested?.Invoke();

        /// <summary>Raised by the toolbar "Enregistrer" button — the shell handles the save (file dialog + recent + title).</summary>
        /// <summary>Émis quand l'état « modifié » a pu changer, pour que l'onglet rafraîchisse son astérisque.
        /// Branché sur la pile d'annulation : elle bouge exactement quand le document est muté, ce qui évite de
        /// re-sérialiser le projet en boucle sur une minuterie.</summary>
        public event Action DirtyChanged;
        void RaiseDirtyChanged() => DirtyChanged?.Invoke();

        public event Action SaveRequested;
        void btnSaveMusic_Click(object sender, RoutedEventArgs e) => SaveRequested?.Invoke();

        /// <summary>Émis par « Enregistrer sous… » : la fenêtre demande toujours un chemin, même si le morceau
        /// en a déjà un.</summary>
        public event Action SaveAsRequested;
        void btnSaveMusicAs_Click(object sender, RoutedEventArgs e) => SaveAsRequested?.Invoke();


        // Right-click on a timeline box → a small context menu. Chord boxes get "Proposer la suite…" (the context-aware
        // diagram, inserting the choice right AFTER this chord).
        void ShowItemContextMenu(TimelineTrack track, TimelineItem item, FrameworkElement anchor)
        {
            if (item == null) return;
            var menu = new ContextMenu();
            bool isChord = item.Module is PatternGeneratorModule || item.Module is CadenceModule;
            if (isChord)
            {
                var mi = new MenuItem { Header = Loc.T("ProposerLaSuite") };
                mi.Click += (s, e) => SuggestNextAfter(track, item);
                menu.Items.Add(mi);
                var chain = new MenuItem { Header = Loc.T("Enchainer4Mesures") };
                chain.Click += (s, e) => ChainProgression(track, item, 4);
                menu.Items.Add(chain);
                menu.Items.Add(new Separator());
            }
            if (item.Module is PlayRiffModule prm)
            {
                var vary = new MenuItem { Header = Loc.T("VarierLeThemeAvecLIA") };
                vary.Click += (s, e) =>
                {
                    if (TimelineHelper.VaryThemeWithAi(Window.GetWindow(this), project, track, item))
                    {
                        selectedTrack = null; selectedItem = null; Render();
                    }
                };
                menu.Items.Add(vary);
                var toDrum = new MenuItem { Header = Loc.T("ConvertirEnBatterie2"), ToolTip = Loc.T("RemplaceCeRiffParUnModule") };
                toDrum.Click += (s, e) => ConvertRiffToDrums(track, item, prm);
                menu.Items.Add(toDrum);
                menu.Items.Add(new Separator());
            }
            if (item.Module is MelodicLineModule mlm)
            {
                var toRiff = new MenuItem { Header = Loc.T("ConvertirEnNotesEditables"), ToolTip = Loc.T("FigeLesHauteursQueLeMoteur") };
                toRiff.Click += (s, e) => ConvertMelodicLineToRiff(track, item, mlm);
                menu.Items.Add(toRiff);
                menu.Items.Add(new Separator());
            }
            // Merge (a riff OR a melodic line; a melodic line is frozen to riff notes → the result is a riff).
            if (TimelineHelper.IsMergeable(item))
            {
                int ci = track.Items.IndexOf(item);
                if (ci >= 0 && ci + 1 < track.Items.Count && TimelineHelper.IsMergeable(track.Items[ci + 1]))
                {
                    var merge = new MenuItem { Header = Loc.T("FusionnerAvecLeSuivant"), ToolTip = Loc.T("ConcateneCeRiffEtLeSuivant") };
                    merge.Click += (s, e) =>
                    {
                        CommitRiffEditor();
                        if (TimelineHelper.MergeWithNext(project, track, item)) { SelectItem(track, item); Render(); }
                    };
                    menu.Items.Add(merge);
                }
                if (TimelineHelper.MergeableCount(track) >= 2)
                {
                    var mergeAll = new MenuItem { Header = Loc.T("FusionnerTouteLaLigne"), ToolTip = Loc.T("FusionneTousLesBlocsDeLa") };
                    mergeAll.Click += (s, e) =>
                    {
                        CommitRiffEditor();
                        var head = TimelineHelper.MergeWholeLine(project, track);
                        if (head != null) { selectedTrack = track; SelectItem(track, head); Render(); }
                    };
                    menu.Items.Add(mergeAll);
                }
                menu.Items.Add(new Separator());
            }
            var copy = new MenuItem { Header = Loc.T("Copier") };
            copy.Click += (s, e) => CopyItem(item);
            menu.Items.Add(copy);
            if (clipModule != null)
            {
                var paste = new MenuItem { Header = Loc.T("CollerIci") };
                paste.Click += (s, e) => PasteAtCursor(track);
                menu.Items.Add(paste);
            }
            menu.Items.Add(new Separator());
            var del = new MenuItem { Header = Loc.T("Supprimer") };
            del.Click += (s, e) => DeleteItem(track, item);
            menu.Items.Add(del);
            menu.PlacementTarget = anchor; menu.IsOpen = true;
        }

        // ---- Copy / paste of a timeline module (via the box context menu) ----
        static FlowModule clipModule;   // a deep clone of the copied module (survives across tabs)
        static Riff clipRiff;           // the copied riff for a PlayRiff module (its notes) — null otherwise

        static FlowModule CloneModule(FlowModule m)
            => m == null ? null
             : System.Text.Json.JsonSerializer.Deserialize<FlowModule>(System.Text.Json.JsonSerializer.Serialize<FlowModule>(m, JsonOpts), JsonOpts);

        void CopyItem(TimelineItem item)
        {
            if (item?.Module == null) return;
            CommitRiffEditor();
            clipModule = CloneModule(item.Module);
            clipRiff = item.Module is PlayRiffModule pr ? project.RiffById(pr.RiffId)?.Clone() : null;
        }

        // Paste the clipboard module right after the block at the cursor on `track` (truncated to fit), as an
        // INDEPENDENT copy (a PlayRiff gets its own fresh riff, so editing it won't touch the original).
        void PasteAtCursor(TimelineTrack track)
        {
            if (clipModule == null || track == null) return;
            CommitRiffEditor();
            var m = CloneModule(clipModule);
            if (m is PlayRiffModule pr && clipRiff != null)
            {
                var r = clipRiff.Clone();
                r.Id = Guid.NewGuid();                       // Clone() preserves the Id → give the copy a new one
                r.Name = clipRiff.Name + " (copie)";
                project.Riffs.Add(r);
                pr.RiffId = r.Id;
            }
            var newItem = new TimelineItem { Module = m };
            double len = Engine.Timeline.TimelineProject.ItemLength(newItem, project.RiffById);
            TimelineHelper.PlaceAtCursor(track, newItem, len, startBeat, project.RiffById);
            selectedTrack = track;
            SelectItem(track, newItem);
            Render();
        }

        // "Convertir en batterie": swap a Play-riff module for a DRUM module carrying the SAME rhythm — each note's
        // start/length is kept as-is and its pitch is read as a GM percussion key (Note+12 → lane, so a drum-content
        // riff round-trips exactly). The item keeps its position and length. For the drum SOUND the item should sit
        // on a batterie (Drum) track — otherwise it plays the percussion rows on this track's instrument.
        void ConvertRiffToDrums(TimelineTrack track, TimelineItem item, PlayRiffModule prm)
        {
            CommitRiffEditor();
            PushUndo("convert:" + Id(item));
            TimelineHelper.ConvertRiffToDrums(project, track, item, prm);
            Render();

            if (track.Type != TimelineTrackType.Drum)
                MessageBox.Show(Loc.T("ConvertiEnModuleBatteriePourEntendre"),
                                Loc.T("ConvertirEnBatterie"), MessageBoxButton.OK, MessageBoxImage.Information);
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
            PushUndo("convert:" + Id(item));
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
            { MessageBox.Show(Loc.T("CetAccordNEstPasSur"), Loc.T("SuiteDAccord"), MessageBoxButton.OK, MessageBoxImage.Information); return; }
            var dlg = new Dialogs.ChordSuggestionDialog(prevDegs, barIdx, phraseLen, key, InstrumentCatalog.GetPreset(track.Instrument)) { Owner = Window.GetWindow(this) };
            if (dlg.ShowDialog() != true) return;
            var pg = NewChordLike(prev);
            TimelineHelper.ApplyChordChoice(pg, key, prev == null || prev.Degree >= 0, dlg);
            string pre = BeginUndo();
            var newItem = TimelineHelper.InsertChordAfter(track, item, pg);
            SelectItem(track, newItem);
            Engine.Flow.ChordDegrees.Revoice(track);
            CommitUndo(pre, "insert:" + Id(newItem));
            Render();
        }

        // "Enchaîner N mesures": append the top-ranked continuation `bars` times, re-reading the context each step.
        void ChainProgression(TimelineTrack track, TimelineItem item, int bars)
        {
            CommitRiffEditor();
            PushUndo("insert:chain");
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
                    else if (it.Module is Engine.Flow.PolyChordModule pcm && pcm.Chords != null && pcm.Chords.Count > 0)
                    { var lc = pcm.Chords[pcm.Chords.Count - 1]; degs.Add(lc.Degree >= 0 ? lc.Degree : Engine.Flow.MusicTheory.DegreeOf(key, ((lc.Root % 12) + 12) % 12)); }
                    beats += it.SilenceBefore +  ModuleDuration.Beats(it.Module, project.RiffById);
                    if (ReferenceEquals(it, upTo)) break;
                }
            int take = Math.Min(3, degs.Count);
            prevDegrees = take > 0 ? degs.GetRange(degs.Count - take, take).ToArray() : new int[0];
            barIndex = (int)(beats / bpb + 1e-6);
            phraseLen = 4;
        }
        private void btnAddDrum_Click(object sender, RoutedEventArgs e) => AppendModule(new DrumPatternModule());

        // Un module polyrythmique naît avec deux calques : seul, un calque n'a rien à déphaser, et l'intérêt du
        // module ne se voit qu'à partir de deux cycles de longueurs différentes. Si un module polyrythmique est
        // déjà sélectionné, on repart de ses réglages (calques, mesure, répétitions) plutôt que des valeurs par
        // défaut : on enchaîne en général plusieurs blocs du même groove.
        private void btnAddPolyDrum_Click(object sender, RoutedEventArgs e)
        {
            var m = new Engine.Flow.PolyDrumModule();
            if (selectedItem?.Module is Engine.Flow.PolyDrumModule src)
            {
                m.Kit = src.Kit;
                m.BeatsPerBar = src.BeatsPerBar;
                m.Repeats = src.Repeats;
                foreach (var l in src.Layers) if (l != null) m.Layers.Add(l.Clone());
            }
            else
            {
                m.Layers.Add(new Engine.Flow.EuclidLayer { Lane = 0, Hits = 3, Steps = 8 });
                m.Layers.Add(new Engine.Flow.EuclidLayer { Lane = 2, Hits = 7, Steps = 16 });
            }
            AppendModule(m);
        }

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
            txtEditorTitle.Text = Loc.T("EditeurLigneMelodique");
        }

        // "Insérer ▸ Ligne mélodique polyrythmique" : module dédié (comme la batterie polyrythmique), sur la piste
        // sélectionnée — un anneau = une voix. Si un tel module est déjà sélectionné, on repart de ses calques.
        private void btnAddMelodicPoly_Click(object sender, RoutedEventArgs e)
        {
            var m = new Engine.Flow.MelodicPolyModule();
            if (selectedItem?.Module is Engine.Flow.MelodicPolyModule src)
            {
                m.BeatsPerBar = src.BeatsPerBar;
                m.Repeats = src.Repeats;
                foreach (var v in src.Layers) if (v != null) m.Layers.Add(v.Clone());
            }
            else
            {
                m.Layers.Add(new Engine.Flow.EuclidVoice { Voice = 0, Hits = 3, Steps = 8 });
                m.Layers.Add(new Engine.Flow.EuclidVoice { Voice = 1, Hits = 5, Steps = 8 });
            }
            AppendModule(m);
        }

        UIElement BuildMelodicLineEditor(TimelineTrack track, TimelineItem item, MelodicLineModule ml)
        {
            double startBeat = project.ItemStartBeat(track, item);
            var grid = TwoColumns(out StackPanel left, out ContentControl host);
            Action refresh = null;
            refresh = () => { RefreshMelodicLineGrid(host, track, item, ml, startBeat); Render(); };

            left.Children.Add(EdLabel(Loc.T("Voix")));
            left.Children.Add(ParamCombo(TimelineHelper.MelodicVoiceNames, Math.Max(0, Math.Min(2, ml.VoiceCount - 1)), v => ml.VoiceCount = v + 1, refresh));
            left.Children.Add(EdLabel(Loc.T("NombreDeTempsDureeDeLa")));
            left.Children.Add(ParamNum(ml.BeatsPerBar, v => ml.BeatsPerBar = Math.Max(1, v), refresh));
            left.Children.Add(EdLabel(Loc.T("ContourAlgorithmeDeChoixDesNotes")));
            left.Children.Add(ParamCombo(Engine.Timeline.MelodicLineEngine.ContourNames, Math.Max(0, Math.Min(Engine.Timeline.MelodicLineEngine.ContourNames.Length - 1, ml.Contour)), v => ml.Contour = v, refresh));
            left.Children.Add(EdLabel(Loc.T("AncrageNoteDeDepartDeLa")));
            left.Children.Add(ParamCombo(Engine.Timeline.MelodicLineEngine.AnchorNames, Math.Max(0, Math.Min(Engine.Timeline.MelodicLineEngine.AnchorNames.Length - 1, ml.Anchor)), v => ml.Anchor = v, refresh));
            left.Children.Add(EdLabel(Loc.T("ContinuiteLissageVoiceLeading0100")));
            left.Children.Add(ParamNum(ml.Continuity, v => ml.Continuity = Math.Max(0, Math.Min(100, v)), refresh));
            left.Children.Add(EdLabel(Loc.T("VariationTransformationDuMotif")));
            left.Children.Add(ParamCombo(Engine.Timeline.MelodicLineEngine.VariationNames, Math.Max(0, Math.Min(Engine.Timeline.MelodicLineEngine.VariationNames.Length - 1, ml.Variation)), v => ml.Variation = v, refresh));
            left.Children.Add(EdLabel(Loc.T("TensionPenteDeRegistreDemiTons")));
            left.Children.Add(ParamNum(ml.TensionSlope, v => ml.TensionSlope = v, refresh));
            left.Children.Add(EdLabel(Loc.T("AmplitudeTessitureDemiTons224")));
            left.Children.Add(ParamNum(ml.Amplitude, v => ml.Amplitude = Math.Max(2, Math.Min(24, v)), refresh));
            left.Children.Add(EdLabel(Loc.T("OrnementationRetardsAppoggiatures0100")));
            left.Children.Add(ParamNum(ml.Ornaments, v => ml.Ornaments = Math.Max(0, Math.Min(100, v)), refresh));
            left.Children.Add(EdLabel(Loc.T("VagueNotesParArc0Auto")));
            left.Children.Add(ParamNum(ml.WaveLength, v => ml.WaveLength = Math.Max(0, Math.Min(32, v)), refresh));
            // ---- Décalage + génération euclidienne (rythme seul : le moteur choisit les hauteurs) ---------------
            int mlVoice = 0, mlK = 3, mlN = 8, mlRot = 0, mlUnit = 0;
            var mlStepNames = new[] { Loc.T("Croche"), Loc.T("DoubleCroche"), Loc.T("TrioletDeCroche") };
            var mlStepSlices = new[] { 12, 6, 8 };
            var voiceLabels = new string[MelodicLineModule.MaxVoices];
            for (int v = 0; v < voiceLabels.Length; v++) voiceLabels[v] = Loc.T("Voix2") + (v + 1);

            left.Children.Add(EdLabel(Loc.T("EuclidLigne")));
            var cboVoice = new ComboBox { Width = 180, HorizontalAlignment = HorizontalAlignment.Left, ItemsSource = voiceLabels, SelectedIndex = 0 };
            cboVoice.SelectionChanged += (s, e) => { if (cboVoice.SelectedIndex >= 0) mlVoice = cboVoice.SelectedIndex; };
            left.Children.Add(cboVoice);

            left.Children.Add(EdLabel(Loc.T("Decalage")));
            var mlShiftRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
            Action<int> mlShift = dir =>
            {
                PushUndo("euclid:rot");
                TimelineHelper.RotateMelodicVoice(ml, mlVoice, dir * mlStepSlices[Math.Max(0, mlUnit)]);
                editorHost.Content = BuildMelodicLineEditor(track, item, ml); Render(); RefreshScore();
            };
            foreach (var (glyph, dir) in new[] { ("◀", -1), ("▶", +1) })
            {
                var b = new Button { Content = glyph, Width = 34, Margin = new Thickness(0, 0, 6, 0), Padding = new Thickness(0, 2, 0, 2), Cursor = Cursors.Hand, ToolTip = Loc.T("DecalerCetteLigneDUnPas") };
                int d = dir; b.Click += (s, e) => mlShift(d);
                mlShiftRow.Children.Add(b);
            }
            left.Children.Add(mlShiftRow);

            var mlPanel = new StackPanel();
            var mlExp = new Expander { Header = Loc.T("RepartirRegulierement"), Foreground = Brushes.White, FontSize = 11, Margin = new Thickness(0, 4, 0, 6), Content = mlPanel };
            var mlPreview = new TextBlock { FontFamily = new FontFamily("Consolas"), FontSize = 13, Foreground = "#1FB6C3".ToBrush(), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 4) };
            Action mlRedraw = () =>
            {
                var pat = Engine.Flow.EuclideanRhythm.Rotate(Engine.Flow.EuclideanRhythm.Pattern(mlK, mlN), mlRot);
                var sb = new System.Text.StringBuilder();
                foreach (var on in pat) sb.Append(on ? '●' : '·');
                string nm = Engine.Flow.EuclideanRhythm.NameFor(pat);
                if (nm != null) sb.Append("   « ").Append(nm).Append(" »");
                // Grille métrique : le moteur ancre l'harmonie sur les coups qui tombent SUR UN TEMPS. Plus le motif
                // est syncopé, moins il produit d'ancrages — et plus la ligne flotte. Le décalage fait varier ce
                // compte sans changer le rythme perçu : c'est le réglage à manipuler quand la ligne sonne vague.
                int st = mlStepSlices[Math.Max(0, mlUnit)], onBeat = 0;
                sb.Append('\n');
                for (int i = 0; i < pat.Length; i++)
                {
                    bool beat = (i * st) % 24 == 0;
                    sb.Append(beat ? '▲' : ' ');
                    if (pat[i] && beat) onBeat++;
                }
                sb.Append('\n').Append(Loc.T("SurLesTemps")).Append(' ').Append(onBeat);
                mlPreview.Text = sb.ToString();
            };
            mlPanel.Children.Add(EdLabel(Loc.T("Coups"))); mlPanel.Children.Add(ParamNum(mlK, v => mlK = Math.Max(0, v), mlRedraw));
            mlPanel.Children.Add(EdLabel(Loc.T("Pas"))); mlPanel.Children.Add(ParamNum(mlN, v => mlN = Math.Max(1, v), mlRedraw));
            mlPanel.Children.Add(EdLabel(Loc.T("Decalage"))); mlPanel.Children.Add(ParamNum(mlRot, v => mlRot = v, mlRedraw));
            mlPanel.Children.Add(EdLabel(Loc.T("Unite"))); mlPanel.Children.Add(ParamCombo(mlStepNames, 0, v => mlUnit = v, mlRedraw));
            mlPanel.Children.Add(mlPreview);
            var mlApply = new Button { Content = Loc.T("Appliquer"), Margin = new Thickness(0, 2, 0, 2), Padding = new Thickness(10, 4, 10, 4), Cursor = Cursors.Hand, HorizontalAlignment = HorizontalAlignment.Left };
            mlApply.Click += (s, e) =>
            {
                PushUndo("euclid:gen");
                TimelineHelper.ApplyEuclideanMelodic(ml, mlVoice, mlK, mlN, mlRot, mlStepSlices[Math.Max(0, mlUnit)]);
                editorHost.Content = BuildMelodicLineEditor(track, item, ml); Render(); RefreshScore();
            };
            mlPanel.Children.Add(mlApply);
            mlRedraw();
            left.Children.Add(mlExp);

            var preserve = new CheckBox { Content = Loc.T("PreserverNonEcraseParAppliquer"), Foreground = Brushes.White, FontSize = 11, Margin = new Thickness(0, 8, 0, 0), IsChecked = ml.Preserve };
            preserve.Checked += (s, e) => ml.Preserve = true; preserve.Unchecked += (s, e) => ml.Preserve = false;
            left.Children.Add(preserve);

            // Motif picker (always shown): "Personnalisé…" (custom, no name) OR a saved motif. Picking a saved motif loads it.
            var savedLines = project.UserMelodicLines ?? (project.UserMelodicLines = new System.Collections.Generic.List<UserChordStyle>());
            left.Children.Add(EdLabel(Loc.T("Motif")));
            var cbMotif = new ComboBox { Margin = new Thickness(0, 2, 0, 0), MinWidth = 170, MaxWidth = 260, HorizontalAlignment = HorizontalAlignment.Left };
            foreach (var u in savedLines) cbMotif.Items.Add(u.Name);
            cbMotif.Items.Add(Loc.T("Personnalise"));
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
                Content = new TextBlock { Text = Loc.T("AppliquerLeMotifACeuxDu"), TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center },
                Margin = new Thickness(0, 4, 0, 0), Padding = new Thickness(10, 4, 10, 4),
                HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center,
                Cursor = Cursors.Hand, IsEnabled = !string.IsNullOrEmpty(ml.LineName),
            };
            btnApply.Click += (s, e) => { if (!string.IsNullOrEmpty(ml.LineName)) { TimelineHelper.PropagateMelodicLine(project,ml.LineName, ml); Render(); RefreshScore(); } };
            left.Children.Add(btnApply);
            left.Children.Add(new TextBlock { Text = Loc.T("DessineSeulementLeRYTHMEUneLigne"), Foreground = "#888888".ToBrush(), FontSize = 10, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), MaxWidth = 260 });

            RefreshMelodicLineGrid(host, track, item, ml, startBeat);
            return grid;
        }

        void RefreshMelodicLineGrid(ContentControl host, TimelineTrack track, TimelineItem item, MelodicLineModule ml, double startBeat)
        {
            int voices = Math.Max(1, Math.Min(MelodicLineModule.MaxVoices, ml.VoiceCount));
            var labels = new string[voices];
            for (int i = 0; i < voices; i++) labels[i] = Loc.T("Voix2") + (i + 1);
            var lines = project.UserMelodicLines ?? (project.UserMelodicLines = new System.Collections.Generic.List<UserChordStyle>());
            var rg = new Controls.RhythmGridControl();
            Func<SequencerSlice[], int, Riff> mk = (gr, gs) =>
            {
                var t = new MelodicLineModule { BeatsPerBar = ml.BeatsPerBar, VoiceCount = ml.VoiceCount };
                t.SetNotes(rg.CurrentNotes(), gs, rg.Beats * gs);
                return Engine.Timeline.MelodicLineEngine.GenerateLine(t, project, project.RiffById, project.Key, startBeat);
            };
            // The motif picker + "Appliquer" live in the LEFT panel now, so the grid keeps only "Enregistrer" (save-as).
            Action onSaveStyle = () =>
            {
                string name = TimelineHelper.PromptText(Loc.T("EnregistrerLeMotifMelodique"), string.IsNullOrEmpty(ml.LineName) ? Loc.T("MaLigne") : ml.LineName);
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
            if (selectedTrack == null) { MessageBox.Show(Loc.T("SelectionneDAbordUnePiste")); return; }
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
            var src = pr != null ? project.RiffById(pr.RiffId) : null;
            if (src == null || src.Notes == null || src.Notes.Count == 0)
            {
                MessageBox.Show(Loc.T("SelectionneDAbordUnRiffLe"), Loc.T("Variation"), MessageBoxButton.OK, MessageBoxImage.Information);
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
        void ExportAudio(string path, bool mp3, System.Collections.Generic.HashSet<TimelineTrack> selected = null)
        {
            if (!SoundFontGuard.EnsureReady(Window.GetWindow(this), "Export")) return;
            // Le player respecte Mute/Solo — on applique la selection via ces flags, restauree apres.
            using (WithTemporarySelection(selected))
            {
                var p = new Engine.Timeline.TimelinePlayer(project, project.RiffById, AudioFormat.SampleRate);
                long cap = p.EstimatedTotalSamples + 5L * AudioFormat.SampleRate; // + a few seconds of ring-out tail
                var dlg = new ExportProgressDialog((progress, token) =>
                    Engine.WaveExporter.RenderProvider(path, mp3, p, cap, AudioFormat.SampleRate, progress, token, p.Start, p.Stop))
                {
                    Owner = Window.GetWindow(this),
                };
                dlg.ShowDialog();
                if (!string.IsNullOrEmpty(dlg.Error)) MessageBox.Show("Export error : " + dlg.Error);
                else if (dlg.Success) MessageBox.Show(Loc.T("ExportTermine") + path);
            }
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
                prog.Set(0.05, Loc.T("LectureDuFichier"));
                var result = await System.Threading.Tasks.Task.Run(() =>
                {
                    var score = midi ? MidiImporter.Load(path) : MuseScoreImporter.Load(path);
                    return TimelineImporter.Build(score, mpr, spb, importVolume);
                });

                if (result.Project.Tracks.Count == 0) { MessageBox.Show(Loc.T("AucunePisteTrouveeDansLeFichier")); return; }

                // Confirm/correct the deduced key + time signature before applying. A declared 4/4 is the MIDI
                // default → ask (suggest the detected meter, e.g. 12/8 for a ternary rhythm).
                var detectedKey = result.Project.Key ?? new Engine.Score.KeySignature();
                string meterHint = result.MeterUncertain
                    ? (result.Ternary ? Loc.T("N44ParDefautRythmeTernaire") : Loc.T("N44ParDefautDansLe"))
                    : "";
                prog.Hide();
                var keyDlg = new KeySignatureDialog(detectedKey, result.Project.TimeSigNum, result.Project.TimeSigDen, meterHint) { Owner = Window.GetWindow(this) };
                bool ok = keyDlg.ShowDialog() == true;
                var chosenKey = ok ? keyDlg.Result : detectedKey;
                int chosenNum = ok ? keyDlg.ResultNum : result.Project.TimeSigNum;
                int chosenDen = ok ? keyDlg.ResultDen : result.Project.TimeSigDen;
                prog.Show();

                prog.Set(0.85, Loc.T("ConstructionDuSequenceur"));
                foreach (var r in result.Riffs) project.Riffs.Add(r);
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
                SetBpmText();
                await RenderBatched(prog); // add the lane controls in batches so the UI stays responsive
                prog.Set(1.0, Loc.T("Termine"));
            }
            catch (Exception ex) { MessageBox.Show(Loc.T("ErreurDImport") + ex.Message); }
            finally { prog.Close(); }
        }

        // Export the whole timeline to a Standard MIDI File.
        // ===== Export UNIQUE ====================================================================================
        // Un seul bouton, un seul dialogue : on choisit le format dans la liste (ou on tape l'extension), et le
        // format suit l'EXTENSION du nom donné — à la manière de GIMP. Cinq entrées de menu pour cinq formats
        // obligeaient à décider AVANT d'avoir vu le sélecteur.
        static readonly (string ext, string desc)[] ExportFormats =
        {
            (".wav",      "WAVE"),
            (".mp3",      "MP3"),
            (".mid",      "MIDI"),
            (".musicxml", "MusicXML"),
            (".mscx",     "MuseScore"),
            (".pdf",      "PDF"),
        };

        private void btnExportAny_Click(object sender, RoutedEventArgs e)
        {
            if (project.Tracks.Count == 0) { MessageBox.Show(Loc.T("AucunePisteAExporter")); return; }
            StopPlayback();

            var f = new System.Text.StringBuilder();
            foreach (var x in ExportFormats)
            {
                if (f.Length > 0) f.Append('|');
                f.Append(x.desc).Append(" (*").Append(x.ext).Append(")|*").Append(x.ext);
            }
            string title = string.IsNullOrEmpty(CurrentPath) ? Loc.T("Partition") : System.IO.Path.GetFileNameWithoutExtension(CurrentPath).Replace('_', ' ');
            var sfd = new Dialogs.FileBrowserDialog
            {
                SaveMode = true,
                Owner = Window.GetWindow(this),
                Filter = f.ToString(),
                DefaultExt = ".wav",
                FileName = title,
            };
            if (sfd.ShowDialog() != true) return;

            string path = sfd.FileName;
            string ext = (System.IO.Path.GetExtension(path) ?? "").ToLowerInvariant();

            // Dialog "Pistes a exporter" : cochees par defaut sauf les mutees. L'utilisateur peut
            // decocher ce qu'il ne veut pas exporter. Le meme dialog s'applique aux 5 formats
            // (WAV/MP3/MIDI/MusicXML/MSCX/PDF) pour une UX uniforme. Annuler = abandon complet.
            var tsel = new Dialogs.TrackSelectionDialog(project) { Owner = Window.GetWindow(this) };
            if (tsel.ShowDialog() != true || tsel.Result == null || tsel.Result.Count == 0) return;
            var selected = new System.Collections.Generic.HashSet<TimelineTrack>(tsel.Result);

            switch (ext)
            {
                case ".wav": case ".mp3": ExportAudio(path, ext == ".mp3", selected); break;
                case ".mid": case ".midi": ExportMidi(path, selected); break;
                case ".musicxml": case ".xml": ExportMusicXml(path, selected); break;
                case ".mscx": ExportMuseScore(path, selected); break;
                // Le PDF n'est pas écrit directement : l'application produit un aperçu imprimable, et c'est
                // « Microsoft Print to PDF » qui grave le fichier. On ouvre donc l'aperçu au lieu d'écrire.
                case ".pdf": ExportPdfPreview(selected); break;
                default:
                    MessageBox.Show(string.Format(Loc.T("FormatDExportInconnu"), ext));
                    break;
            }
        }

        void ExportMidi(string path, System.Collections.Generic.HashSet<TimelineTrack> selected = null)
        {
            try
            {
                using (WithTemporarySelection(selected))
                    Engine.Timeline.MidiTimelineExporter.Export(path, project, project.RiffById);
                MessageBox.Show(Loc.T("ExportMIDITermine") + path);
            }
            catch (Exception ex) { MessageBox.Show(Loc.T("ErreurDExportMIDI") + ex.Message); }
        }

        // Applique temporairement Mute=false pour les pistes cochees et Mute=true pour les non-cochees
        // (+ Solo=false partout pour eviter les interferences), pour que les exporters qui iterent sur
        // project.Tracks respectent naturellement la selection. Restauration a Dispose(). Si selected
        // est null (compat retro), pas de changement.
        IDisposable WithTemporarySelection(System.Collections.Generic.HashSet<TimelineTrack> selected)
        {
            if (selected == null) return new NoopScope();
            var backup = new System.Collections.Generic.Dictionary<TimelineTrack, (bool mute, bool solo)>();
            foreach (var t in project.Tracks)
            {
                backup[t] = (t.Mute, t.Solo);
                t.Mute = !selected.Contains(t);
                t.Solo = false;
            }
            return new RestoreScope(backup);
        }
        sealed class NoopScope : IDisposable { public void Dispose() { } }
        sealed class RestoreScope : IDisposable
        {
            readonly System.Collections.Generic.Dictionary<TimelineTrack, (bool mute, bool solo)> _b;
            public RestoreScope(System.Collections.Generic.Dictionary<TimelineTrack, (bool mute, bool solo)> b) { _b = b; }
            public void Dispose() { foreach (var kv in _b) { kv.Key.Mute = kv.Value.mute; kv.Key.Solo = kv.Value.solo; } }
        }

        // Export the score to a native MuseScore .mscx file (the checked ♫ tracks, else all instrument tracks;
        // drums skipped). One staff per part, with its clef + key + time signature.
        void ExportMuseScore(string path, System.Collections.Generic.HashSet<TimelineTrack> selected = null)
        {
            var src = new System.Collections.Generic.List<TimelineTrack>();
            if (selected != null)
            {
                foreach (var t in project.Tracks) if (selected.Contains(t)) src.Add(t);
            }
            else
            {
                foreach (var t in project.Tracks) if (scoreTracks.Contains(t)) src.Add(t);
                if (src.Count == 0) foreach (var t in project.Tracks) if (t.Type != TimelineTrackType.Drum) src.Add(t);
            }

            var parts = new System.Collections.Generic.List<Engine.Timeline.MuseScoreExporter.Part>();
            foreach (var t in src)
            {
                if (t.Type == TimelineTrackType.Drum) continue; // percussion needs a drum staff — not exported yet
                parts.Add(new Engine.Timeline.MuseScoreExporter.Part { Name = t.Name, Program = t.Instrument, Score = Engine.Score.ScoreBuilder.Build(project, t, project.RiffById) });
            }
            if (parts.Count == 0) { MessageBox.Show(Loc.T("AucunePisteMelodiqueAExporterCoche")); return; }

            string title = string.IsNullOrEmpty(CurrentPath) ? Loc.T("Partition") : System.IO.Path.GetFileNameWithoutExtension(CurrentPath).Replace('_', ' ');
            try
            {
                Engine.Timeline.MuseScoreExporter.Export(path, parts, project.TimeSigNum, project.TimeSigDen, title);
                MessageBox.Show(Loc.T("ExportMuseScoreTermine") + path);
            }
            catch (Exception ex) { MessageBox.Show(Loc.T("ErreurDExportMuseScore") + ex.Message); }
        }

        // Export the score to MusicXML — the interchange format every notation program reads. Same track rule as the
        // MuseScore export (the checked ♫ tracks, else all instrument tracks; drums skipped) so there is only one
        // convention to learn; unlike the .mscx, notes are tied over the bar lines instead of being truncated.
        void ExportMusicXml(string path, System.Collections.Generic.HashSet<TimelineTrack> selected = null)
        {
            var src = new System.Collections.Generic.List<TimelineTrack>();
            if (selected != null)
            {
                foreach (var t in project.Tracks) if (selected.Contains(t)) src.Add(t);
            }
            else
            {
                foreach (var t in project.Tracks) if (scoreTracks.Contains(t)) src.Add(t);
                if (src.Count == 0) foreach (var t in project.Tracks) if (t.Type != TimelineTrackType.Drum) src.Add(t);
            }

            var parts = new System.Collections.Generic.List<Engine.Timeline.MusicXmlExporter.Part>();
            foreach (var t in src)
            {
                if (t.Type == TimelineTrackType.Drum) continue; // percussion needs a drum staff — not exported yet
                parts.Add(new Engine.Timeline.MusicXmlExporter.Part { Name = t.Name, Program = t.Instrument, Score = Engine.Score.ScoreBuilder.Build(project, t, project.RiffById) });
            }
            if (parts.Count == 0) { MessageBox.Show(Loc.T("AucunePisteMelodiqueAExporterCoche")); return; }

            string title = string.IsNullOrEmpty(CurrentPath) ? Loc.T("Partition") : System.IO.Path.GetFileNameWithoutExtension(CurrentPath).Replace('_', ' ');
            try
            {
                Engine.Timeline.MusicXmlExporter.Export(path, parts, project.TimeSigNum, project.TimeSigDen,
                    project.TimeSigScale > 0 ? project.TimeSigScale : 1.0, project.MainBpm, title);
                MessageBox.Show(Loc.T("ExportMusicXMLTermine") + path);
            }
            catch (Exception ex) { MessageBox.Show(Loc.T("ErreurDExportMusicXML") + ex.Message); }
        }

        // Export the checked (♫) tracks as an A4 score, broken into lines of 2/4/8/16 measures, printed to PDF.
        void ExportPdfPreview(System.Collections.Generic.HashSet<TimelineTrack> selected = null)
        {
            var list = new System.Collections.Generic.List<Engine.Score.TrackScore>();
            if (selected != null)
            {
                foreach (var t in project.Tracks) if (selected.Contains(t)) list.Add(Engine.Score.ScoreBuilder.Build(project, t, project.RiffById));
            }
            else
            {
                foreach (var t in project.Tracks) if (scoreTracks.Contains(t)) list.Add(Engine.Score.ScoreBuilder.Build(project, t, project.RiffById));
            }
            if (list.Count == 0) { MessageBox.Show(Loc.T("CocheAuMoinsUnePistePour")); return; }

            // Title: the file name (no extension, '_' → space); fallback when unsaved.
            string title = string.IsNullOrEmpty(CurrentPath) ? Loc.T("Partition") : System.IO.Path.GetFileNameWithoutExtension(CurrentPath).Replace('_', ' ');

            var doc = Controls.Score.ScorePdfExporter.Build(list, project.TimeSigNum, project.TimeSigDen, project.TimeSigScale > 0 ? project.TimeSigScale : 1.0, title);
            // Preview window: a DocumentViewer (zoom + scroll + its own Print button → "Microsoft Print to PDF").
            var viewer = new System.Windows.Controls.DocumentViewer { Document = doc };
            var win = new Window
            {
                Title = Loc.T("Partition3") + title + Loc.T("ZoomDefilementBoutonImprimerPourLe"),
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
                SyncTempoReadout();
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

