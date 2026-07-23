using System;
using System.Windows;
using System.Windows.Controls;
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
    /// Reusable rhythm grid editor (rows × slices): draw cells, set a play cursor on the ruler, loop
    /// preview, copy from a built-in style, change beats/resolution. Used by the chord/drum rhythm
    /// editor (dialog) and embedded in the timeline's bottom editor. Raises <see cref="GridChanged"/>
    /// so the host can persist live.
    /// </summary>
    public partial class RhythmGridControl : UserControl
    {
        const double CellW = 26, CellH = 28, LabelW = 110, RulerH = 16;

        string[] rowLabels = { "1" };
        int voiceCount = 1;
        Func<int, int, SequencerSlice[]> seedFunc;
        Func<int, System.Collections.Generic.List<RiffNote>> seedNotesFunc; // optional (note mode): exact note-list of a style
                                                                            // — used instead of FromSlices so back-to-back
                                                                            // notes keep their individual durations (not merged)
        Func<int, int> seedSpbFunc;        // optional: per-style slices-per-beat (user styles can differ from seedNativeSpb)
        Action onSaveStyle;                // optional: host saves the current grid as a named user style (button hidden when null)
        Action onApplyToSection;           // optional: host pushes this motif to every chord of the section (button hidden when null)
        int seedNativeSpb = 24;
        Func<SequencerSlice[], int, Riff> makeRiff;
        Preset previewInstrument;

        int beats = 4, spb = 4;
        bool[,] cells;
        // The grid is drawn into ONE retained-visual surface (OFF background tiled once + ON cells/notes redrawn
        // only when they change) instead of one Rectangle per cell — cheap even for the 47-lane drum grid.
        GridSurface gridSurface;
        // Optional per-row colouring (drum mode): when set, each row's ON cell + tinted empty background come
        // from this lane→colour map (percussion families). Null = the default single green ON colour (chords).
        Func<int, Color> rowColor;
        Brush[] rowOnBrush, rowOffBrush, rowOffBeatBrush;
        // NOTE-LIST mode (chords): notes carry a LENGTH (rectangles) and adjacent same-voice notes stay distinct.
        bool noteMode;
        readonly System.Collections.Generic.List<RiffNote> notes = new System.Collections.Generic.List<RiffNote>(); // Note = voice row
        // Gesture (mirrors the riff editor): press an OFF cell → DRAW (grows on drag, merges same-voice on release);
        // press an ON cell → ERASE the touched SLICE (shrink/split), not the whole note.
        enum NoteDrag { None, Draw, Erase }
        NoteDrag noteDrag = NoteDrag.None;
        int dragRow, drawMin, drawMax, lastDragCol, drawIdx = -1;
        bool painting, paintValue;
        int startSlice;
        Rectangle cursorLine;
        Polygon startMarker;       // blue down-pointing handle on the ruler: drag to set the play start
        bool draggingMarker;

        WaveOutEvent waveOut;
        LoopingRiffProvider provider;
        System.Windows.Threading.DispatcherTimer playTimer;

        /// <summary>Raised whenever the grid content/resolution changes (cell, regrid, seed).</summary>
        public event Action GridChanged;

        public int Spb => spb;
        public int Beats => beats;

        public RhythmGridControl() { InitializeComponent(); }

        public void Configure(string[] rowLabels, int beats, int slicesPerBeat, SequencerSlice[] existing,
                              string[] seedStyleNames, Func<int, int, SequencerSlice[]> seedFunc, int seedNativeSpb,
                              Func<SequencerSlice[], int, Riff> makeRiff, Preset previewInstrument,
                              Func<int, int> seedSpbFunc = null, Action onSaveStyle = null,
                              bool noteList = false, System.Collections.Generic.List<RiffNote> existingNotes = null,
                              Action onApplyToSection = null, Func<int, System.Collections.Generic.List<RiffNote>> seedNotesFunc = null,
                              Func<int, Color> rowColor = null)
        {
            this.seedNotesFunc = seedNotesFunc;
            this.rowColor = rowColor;
            this.rowLabels = (rowLabels != null && rowLabels.Length > 0) ? rowLabels : new[] { "1" };
            this.voiceCount = this.rowLabels.Length;
            this.beats = Math.Max(1, beats);
            this.spb = Math.Max(1, slicesPerBeat);
            this.seedFunc = seedFunc;
            this.seedSpbFunc = seedSpbFunc;
            this.onSaveStyle = onSaveStyle;
            this.onApplyToSection = onApplyToSection;
            this.seedNativeSpb = Math.Max(1, seedNativeSpb);
            this.makeRiff = makeRiff;
            this.previewInstrument = previewInstrument;
            this.noteMode = noteList;
            btnSaveStyle.Visibility = onSaveStyle != null ? Visibility.Visible : Visibility.Collapsed;
            btnApplySection.Visibility = onApplyToSection != null ? Visibility.Visible : Visibility.Collapsed;

            txtBeats.Text = this.beats.ToString();
            cboSpb.Text = this.spb.ToString();
            cboSeedStyle.Items.Clear();
            if (seedStyleNames != null) foreach (var n in seedStyleNames) cboSeedStyle.Items.Add(n);
            // Hide the "Copier :" seed row when no seed styles are supplied (e.g. the drum editor drives copying itself).
            var seedVis = (seedStyleNames != null && seedStyleNames.Length > 0) ? Visibility.Visible : Visibility.Collapsed;
            seedSep.Visibility = lblSeed.Visibility = cboSeedStyle.Visibility = btnSeed.Visibility = seedVis;

            int cols = this.beats * this.spb;
            cells = new bool[this.voiceCount, cols];
            notes.Clear();
            if (noteList)
            {
                if (existingNotes != null) foreach (var n in existingNotes) notes.Add(n);
                else if (existing != null) notes.AddRange(RiffNotes.FromSlices(existing)); // convert an old slice motif
            }
            else if (existing != null)
            {
                int n = Math.Min(cols, existing.Length);
                for (int c = 0; c < n; c++)
                    for (int v = 0; v < this.voiceCount; v++)
                        cells[v, c] = existing[c].On(v);
            }
            BuildRowBrushes();
            Build();
        }

        // Pre-freeze one ON / OFF / OFF-beat brush per row from the lane→colour map (drum mode). No-op (and
        // clears the caches) when no rowColor is set, so chord/note grids keep the default single green colour.
        void BuildRowBrushes()
        {
            if (rowColor == null) { rowOnBrush = rowOffBrush = rowOffBeatBrush = null; return; }
            rowOnBrush = new Brush[voiceCount];
            rowOffBrush = new Brush[voiceCount];
            rowOffBeatBrush = new Brush[voiceCount];
            for (int v = 0; v < voiceCount; v++)
            {
                Color c = rowColor(v);
                rowOnBrush[v] = Frozen(c);
                rowOffBrush[v] = Frozen(DrumColors.Dim(c, false));
                rowOffBeatBrush[v] = Frozen(DrumColors.Dim(c, true));
            }
        }

        static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

        /// <summary>Note-list form of the grid (Note = voice row), valid in note mode. Distinguishes adjacent same-voice notes.</summary>
        public System.Collections.Generic.List<RiffNote> CurrentNotes()
        {
            var l = new System.Collections.Generic.List<RiffNote>();
            foreach (var n in notes) l.Add(n);
            return l;
        }

        public SequencerSlice[] CurrentGrid()
        {
            int cols = Cols;
            if (noteMode) return RiffNotes.ToSlices(notes, cols);   // OR-merged grid (length carrier / thumbnails / compat)
            var slices = new SequencerSlice[cols];
            for (int c = 0; c < cols; c++)
                for (int v = 0; v < voiceCount; v++)
                    if (cells[v, c]) slices[c].On(v, true);
            return slices;
        }

        int Cols => beats * spb;
        double RowY(int voice) => (voiceCount - 1 - voice) * CellH;

        void Build()
        {
            canvasGrid.Children.Clear();
            int cols = Cols;
            canvasGrid.Width = LabelW + cols * CellW;
            canvasGrid.Height = RulerH + voiceCount * CellH;

            var ruler = new Rectangle { Width = cols * CellW, Height = RulerH, Fill = new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x28)) };
            Canvas.SetLeft(ruler, LabelW); canvasGrid.Children.Add(ruler);
            for (int c = 0; c <= cols; c += spb)
            {
                var tick = new Rectangle { Width = 1, Height = RulerH, Fill = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x70)) };
                Canvas.SetLeft(tick, LabelW + c * CellW); canvasGrid.Children.Add(tick);
            }
            for (int v = 0; v < voiceCount; v++)
            {
                double y = RulerH + RowY(v);
                var label = new TextBlock { Text = rowLabels[v], Foreground = new SolidColorBrush(Color.FromRgb(0xBB, 0xBB, 0xBB)), FontSize = 11, Width = LabelW - 6, TextTrimming = TextTrimming.CharacterEllipsis };
                Canvas.SetLeft(label, 4); Canvas.SetTop(label, y + CellH / 2 - 8); canvasGrid.Children.Add(label);
            }

            // Retained-visual grid surface (cells drawn into DrawingVisuals, not UIElements): sits under the
            // note-name column offset (LabelW) and below the ruler (RulerH). Hit-testing is coordinate math.
            gridSurface = new GridSurface { Width = cols * CellW, Height = voiceCount * CellH };
            Canvas.SetLeft(gridSurface, LabelW); Canvas.SetTop(gridSurface, RulerH);
            canvasGrid.Children.Add(gridSurface);
            DrawOffGrid();     // background: drawn once (tiled)
            RedrawContent();   // ON cells / notes

            cursorLine = new Rectangle { Width = 2, Height = canvasGrid.Height, Fill = new SolidColorBrush(Color.FromRgb(0xFF, 0xDD, 0x33)), IsHitTestVisible = false };
            canvasGrid.Children.Add(cursorLine);

            // Draggable blue start handle (down-pointing triangle) sitting in the ruler band.
            startMarker = new Polygon
            {
                Points = new PointCollection { new Point(-7, 0), new Point(7, 0), new Point(0, RulerH) },
                Fill = MarkerBrush,
                Stroke = MarkerStroke,
                StrokeThickness = 1,
                Cursor = Cursors.SizeWE,
                ToolTip = "Glisser pour définir le point de départ de la lecture",
            };
            startMarker.MouseLeftButtonDown += startMarker_MouseLeftButtonDown;
            startMarker.MouseMove += startMarker_MouseMove;
            startMarker.MouseLeftButtonUp += startMarker_MouseLeftButtonUp;
            canvasGrid.Children.Add(startMarker);

            if (startSlice >= cols) startSlice = 0;
            MoveCursor(startSlice);
        }

        // OFF background: a regular pattern (identical every beat horizontally, rows differ by family tint), so we
        // rasterise ONE beat × all-rows tile and let a tiled ImageBrush fill the whole grid in a single draw.
        void DrawOffGrid()
        {
            if (gridSurface == null) return;
            using (var dc = gridSurface.OpenOff())
                dc.DrawRectangle(BuildOffTile(), null, new Rect(0, 0, Cols * CellW, voiceCount * CellH));
        }

        Brush BuildOffTile()
        {
            double uw = spb * CellW, uh = Math.Max(1, voiceCount) * CellH;
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
                for (int v = 0; v < voiceCount; v++)
                {
                    double y = RowY(v) + 1;
                    for (int c = 0; c < spb; c++)
                        dc.DrawRoundedRectangle(OffCellBrush(v, c == 0), null, new Rect(c * CellW + 1, y, CellW - 2, CellH - 2), 3, 3);
                }
            var rtb = new RenderTargetBitmap((int)Math.Ceiling(uw), (int)Math.Ceiling(uh), 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv); rtb.Freeze();
            var brush = new ImageBrush(rtb) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, uw, uh), ViewportUnits = BrushMappingMode.Absolute, Stretch = Stretch.Fill };
            brush.Freeze();
            return brush;
        }

        Brush OffCellBrush(int v, bool beat)
        {
            if (rowOffBrush != null) return beat ? rowOffBeatBrush[v] : rowOffBrush[v];
            return beat ? OffBeatBrush : OffBrush;
        }

        // ON content: cells (cell mode) or notes (note mode) drawn into the retained ON visual — redrawn only when
        // the content changes, so a drag repaints one lightweight visual instead of recreating a UIElement per cell.
        void RedrawContent()
        {
            if (gridSurface == null) return;
            using (var dc = gridSurface.OpenOn())
            {
                if (noteMode)
                {
                    foreach (var n in notes)
                    {
                        if (n.Note < 0 || n.Note >= voiceCount) continue;
                        Brush fill = rowOnBrush != null ? rowOnBrush[n.Note] : OnBrush;   // family colour (drums) else green
                        double x = n.Start * CellW + 1, y = RowY(n.Note) + 1, w = Math.Max(2, n.Length * CellW - 2);
                        dc.DrawRoundedRectangle(fill, null, new Rect(x, y, w, CellH - 2), 3, 3);
                    }
                }
                else
                {
                    int cols = Cols;
                    for (int v = 0; v < voiceCount; v++)
                        for (int c = 0; c < cols; c++)
                            if (cells[v, c])
                            {
                                Brush fill = rowOnBrush != null ? rowOnBrush[v] : OnBrush;
                                dc.DrawRoundedRectangle(fill, null, new Rect(c * CellW + 1, RowY(v) + 1, CellW - 2, CellH - 2), 3, 3);
                            }
                }
            }
        }

        int NoteAt(int v, int c)
        {
            for (int i = notes.Count - 1; i >= 0; i--) { var n = notes[i]; if (n.Note == v && c >= n.Start && c < n.Start + n.Length) return i; }
            return -1;
        }

        // Erase ONE slice of a note (shrink / split / delete the touched cell), like the riff editor's erase.
        void EraseCell(int v, int c)
        {
            int idx = NoteAt(v, c);
            if (idx < 0) return;
            var n = notes[idx];
            notes.RemoveAt(idx);
            if (c > n.Start) notes.Add(new RiffNote(v, n.Start, c - n.Start));                 // keep the left part
            if (c + 1 < n.End) notes.Add(new RiffNote(v, c + 1, n.End - (c + 1)));              // keep the right part
        }

        // Add a drawn note, FUSING it with any same-voice note it overlaps.
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

        void MoveCursor(int slice)
        {
            if (slice < 0) slice = 0; else if (slice >= Cols) slice = Cols - 1;
            double left = LabelW + slice * CellW;
            if (cursorLine != null) Canvas.SetLeft(cursorLine, left);
            if (startMarker != null) Canvas.SetLeft(startMarker, left + 1); // centre on the 2px line
        }

        static readonly Brush MarkerBrush = new SolidColorBrush(Color.FromRgb(0x1F, 0xB6, 0xC3));   // teal start-handle (app accent)
        static readonly Brush MarkerStroke = new SolidColorBrush(Color.FromRgb(0xCF, 0xEE, 0xF2));
        static readonly Brush OnBrush = new SolidColorBrush(Color.FromRgb(0x55, 0xCC, 0x88));
        static readonly Brush OffBrush = new SolidColorBrush(Color.FromRgb(0x2C, 0x2C, 0x36));
        static readonly Brush OffBeatBrush = new SolidColorBrush(Color.FromRgb(0x39, 0x39, 0x46));
        void SetCell(int v, int c, bool on)
        {
            if (v < 0 || v >= voiceCount || c < 0 || c >= Cols || cells[v, c] == on) return;
            cells[v, c] = on; RedrawContent();
            GridChanged?.Invoke();
        }

        bool HitTest(Point p, out int v, out int c)
        {
            v = -1; c = -1;
            double x = p.X - LabelW, y = p.Y - RulerH;
            if (x < 0 || y < 0) return false;
            c = (int)(x / CellW);
            v = voiceCount - 1 - (int)(y / CellH);
            return c >= 0 && c < Cols && v >= 0 && v < voiceCount;
        }

        private void canvasGrid_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            var p = e.GetPosition(canvasGrid);
            if (p.Y < RulerH) { SetStartFromX(p.X); return; } // click the ruler -> set play start
            if (!HitTest(p, out int vv, out int cc)) return;
            if (noteMode)
            {
                dragRow = vv; lastDragCol = cc;
                if (NoteAt(vv, cc) >= 0) { noteDrag = NoteDrag.Erase; EraseCell(vv, cc); }              // ON cell → erase this slice
                else { noteDrag = NoteDrag.Draw; drawMin = drawMax = cc; drawIdx = notes.Count; notes.Add(new RiffNote(vv, cc, 1)); } // OFF → draw
                canvasGrid.CaptureMouse(); RedrawContent(); GridChanged?.Invoke();
                return;
            }
            paintValue = !cells[vv, cc]; painting = true; SetCell(vv, cc, paintValue); canvasGrid.CaptureMouse();
        }

        void SetStartFromX(double x)
        {
            int c = (int)Math.Round((x - LabelW) / CellW);
            if (c < 0) c = 0; else if (c >= Cols) c = Cols - 1;
            startSlice = c;
            MoveCursor(c);
            if (provider != null) provider.StartSlice = c;
        }

        private void startMarker_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            draggingMarker = true; startMarker.CaptureMouse();
            SetStartFromX(e.GetPosition(canvasGrid).X);
            e.Handled = true;
        }

        private void startMarker_MouseMove(object sender, MouseEventArgs e)
        {
            if (draggingMarker) { SetStartFromX(e.GetPosition(canvasGrid).X); e.Handled = true; }
        }

        private void startMarker_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (draggingMarker) { draggingMarker = false; startMarker.ReleaseMouseCapture(); e.Handled = true; }
        }

        private void canvasGrid_MouseMove(object sender, MouseEventArgs e)
        {
            if (noteMode)
            {
                if (noteDrag == NoteDrag.None) return;
                int c = (int)((e.GetPosition(canvasGrid).X - LabelW) / CellW);
                if (c == lastDragCol) return;   // act only when the cell changes
                lastDragCol = c;
                if (noteDrag == NoteDrag.Erase) { if (c >= 0 && c < Cols) EraseCell(dragRow, c); }
                else   // Draw: the note spans the whole [min..max] reached (only grows — going back doesn't shrink)
                {
                    if (c < 0) c = 0; else if (c >= Cols) c = Cols - 1;
                    if (c < drawMin) drawMin = c; if (c > drawMax) drawMax = c;
                    if (drawIdx >= 0 && drawIdx < notes.Count) notes[drawIdx] = new RiffNote(dragRow, drawMin, drawMax - drawMin + 1);
                }
                RedrawContent(); GridChanged?.Invoke();
                return;
            }
            if (painting && HitTest(e.GetPosition(canvasGrid), out int v, out int c2)) SetCell(v, c2, paintValue);
        }

        private void canvasGrid_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (noteMode)
            {
                if (noteDrag == NoteDrag.Draw && drawIdx >= 0 && drawIdx < notes.Count)
                { var dn = notes[drawIdx]; notes.RemoveAt(drawIdx); MergeAdd(dn); }   // fuse with any same-voice note it overlaps
                noteDrag = NoteDrag.None; drawIdx = -1;
                canvasGrid.ReleaseMouseCapture(); RedrawContent(); GridChanged?.Invoke();
                return;
            }
            painting = false; canvasGrid.ReleaseMouseCapture();
        }

        private void btnApply_Click(object sender, RoutedEventArgs e)
        {
            if (!int.TryParse(txtBeats.Text, out int nb) || nb < 1) nb = beats;
            int ns = ParseSpb();
            int oldSpb = spb, newCols = nb * ns;
            if (noteMode)
            {
                // rescale note positions/lengths by the resolution ratio; keep notes that still fit the new grid
                double f = (double)ns / oldSpb;
                var kept = new System.Collections.Generic.List<RiffNote>();
                foreach (var n in notes)
                {
                    int st = (int)Math.Round(n.Start * f), ln = Math.Max(1, (int)Math.Round(n.Length * f));
                    if (st < newCols) kept.Add(new RiffNote(n.Note, st, Math.Min(ln, newCols - st)));
                }
                notes.Clear(); notes.AddRange(kept);
                beats = nb; spb = ns; cells = new bool[voiceCount, newCols];
                txtBeats.Text = beats.ToString(); cboSpb.Text = spb.ToString();
                Build(); GridChanged?.Invoke();
                return;
            }
            int oldCols = Cols;
            var old = cells;
            var dst = new bool[voiceCount, newCols];
            for (int c = 0; c < newCols; c++)
            {
                int oc = (int)Math.Round((double)c / ns * oldSpb);
                if (oc >= 0 && oc < oldCols) for (int v = 0; v < voiceCount; v++) dst[v, c] = old[v, oc];
            }
            beats = nb; spb = ns; cells = dst;
            txtBeats.Text = beats.ToString(); cboSpb.Text = spb.ToString();
            Build(); GridChanged?.Invoke();
        }

        private void btnSeed_Click(object sender, RoutedEventArgs e)
        {
            int idx = cboSeedStyle.SelectedIndex;
            if (idx < 0 || seedFunc == null) return;
            if (!int.TryParse(txtBeats.Text, out int nb) || nb < 1) nb = beats;
            var grid = seedFunc(idx, nb);
            int gridSpb = seedSpbFunc != null ? Math.Max(1, seedSpbFunc(idx)) : seedNativeSpb;
            // Note mode: prefer the style's exact note-list (keeps individual durations; FromSlices would merge
            // back-to-back notes into one long note → "fills everything ignoring durations").
            if (noteMode && seedNotesFunc != null)
            {
                var sn = seedNotesFunc(idx);
                if (sn != null)
                {
                    int cols = grid != null ? grid.Length : nb * gridSpb;
                    LoadNotes(sn, gridSpb, cols); GridChanged?.Invoke(); return;
                }
            }
            if (grid != null) { LoadGrid(grid, gridSpb); GridChanged?.Invoke(); }
        }

        // Load an exact note-list (note mode) at the given resolution/length — preserves each note's duration.
        void LoadNotes(System.Collections.Generic.List<RiffNote> src, int gridSpb, int cols)
        {
            spb = Math.Max(1, gridSpb);
            cols = Math.Max(1, cols);
            beats = Math.Max(1, cols / spb);
            cells = new bool[voiceCount, cols];
            notes.Clear();
            if (src != null)
                foreach (var n in src)
                    if (n.Note >= 0 && n.Note < voiceCount && n.Start >= 0 && n.Start < cols)
                        notes.Add(new RiffNote(n.Note, n.Start, Math.Max(1, Math.Min(n.Length, cols - n.Start))));
            txtBeats.Text = beats.ToString(); cboSpb.Text = spb.ToString();
            Build();
        }

        // Save the CURRENT grid as a named user style (the host persists it into the project and refreshes the dropdown).
        private void btnSaveStyle_Click(object sender, RoutedEventArgs e) => onSaveStyle?.Invoke();

        // Push this motif to every chord of the section (same handler as the left-panel "Appliquer à la section" button).
        private void btnApplySection_Click(object sender, RoutedEventArgs e) => onApplyToSection?.Invoke();

        void LoadGrid(SequencerSlice[] grid, int gridSpb)
        {
            spb = Math.Max(1, gridSpb);
            int cols = grid.Length;
            beats = Math.Max(1, cols / spb);
            cells = new bool[voiceCount, cols];
            if (noteMode) { notes.Clear(); notes.AddRange(RiffNotes.FromSlices(grid)); }   // a seeded builtin style → notes
            else
                for (int c = 0; c < cols; c++)
                    for (int v = 0; v < voiceCount; v++)
                        cells[v, c] = grid[c].On(v);
            txtBeats.Text = beats.ToString(); cboSpb.Text = spb.ToString();
            Build();
        }

        int ParseSpb()
        {
            string s = (cboSpb.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? cboSpb.Text;
            return (int.TryParse((s ?? "").Trim(), out int v) && v >= 1 && v <= 48) ? v : spb;
        }

        private void btnPlay_Click(object sender, RoutedEventArgs e)
        {
            if (waveOut != null) { StopPreview(); return; }
            if (makeRiff == null) return;
            if (!SoundFontGuard.EnsureReady(Window.GetWindow(this), "Lecture")) return;
            try
            {
                // Drum kits are identified by the SF2 BANK (128), not the patch number — a kit's PatchNumber is
                // 0 (Standard), 8 (Room), 16 (Power)… so testing PatchNumber == 128 was never true and the preview
                // fell through to channel 0 + program 0 (= Grand Piano).
                var ctx = new FlowContext { GmProgram = previewInstrument?.PatchNumber ?? 0, Drum = previewInstrument?.BankNumber == InstrumentCatalog.DrumIndex, Bpm = 100 };
                provider = new LoopingRiffProvider(() => makeRiff(CurrentGrid(), spb), ctx) { StartSlice = startSlice };
                waveOut = new WaveOutEvent { DesiredLatency = 120 };
                waveOut.Init(provider); waveOut.Play();
                btnPlay.Content = "■ Stop";
                playTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
                playTimer.Tick += (s, ev) => { if (provider != null) MoveCursor(provider.CurrentSlice); };
                playTimer.Start();
            }
            catch { StopPreview(); }
        }

        public void StopPreview()
        {
            if (playTimer != null) { playTimer.Stop(); playTimer = null; }
            if (waveOut != null) { try { waveOut.Stop(); waveOut.Dispose(); } catch { } waveOut = null; }
            provider = null;
            if (btnPlay != null) btnPlay.Content = "▶ Écouter";
            MoveCursor(startSlice);
        }

        private void UserControl_Unloaded(object sender, RoutedEventArgs e) => StopPreview();

        // A lightweight surface that draws the grid with two retained DrawingVisuals (OFF background + ON cells/notes)
        // instead of one UIElement per cell — far cheaper to build, redraw and scroll (47-lane drum grid).
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
