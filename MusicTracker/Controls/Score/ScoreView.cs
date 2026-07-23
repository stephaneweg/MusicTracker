using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using MusicTracker.Engine.Score;

namespace MusicTracker.Controls.Score
{
    /// <summary>
    /// Renders one or more tracks as a musical score: each track is ONE staff (its clef chosen by import /
    /// instrument / note range) stacked vertically into a system, sharing one horizontal time axis so the
    /// staves line up. Bravura (SMuFL) glyphs for clefs/accidentals/rests/key-sig/time-sig; drawn note heads,
    /// stems, beams, flags, ledger lines, bar lines. MuseScore-style spacing (space ∝ duration^0.6). A single
    /// playback cursor spans all staves. Reusable UserControl: Configure(tracks, beatsPerBar).
    /// </summary>
    public sealed class ScoreView : UserControl
    {
        // ---- layout ----
        const double StaffGap = 9.0;          // px between two staff lines
        const double Step = StaffGap / 2.0;   // one diatonic step (line→space)
        const double LeftPad = 14, RightPad = 80;
        const double TopPad = 70, BottomPad = 40, InterGap = 46; // top margin fits ~4 ledger lines + an 8va label
        const double MeasurePad = 14;
        const double AccGap = 5;
        const double AccReserve = 12;
        const double SpaceMin = StaffGap * 1.55;
        const double SpaceStretch = StaffGap * 2.5;
        const double SpaceExp = 0.6;
        const double HeadHalf = StaffGap * 0.7;
        const double ClefBoxW = 30, AccW = 11, TimeSigBoxW = 18;

        // Horizontal room for a column, by the duration to the next onset (MuseScore-style power law). FLOOR at an
        // eighth (0.5 beat) so sixteenths read like eighths, PLUS a little extra below the eighth so beamed
        // sixteenth/thirty-second stems get breathing room (heads + multiple beams don't crowd).
        static double SpaceFor(double beats)
        {
            double s = SpaceMin + SpaceStretch * Math.Pow(Math.Max(0.5, beats), SpaceExp);
            if (beats < 0.5) s += StaffGap * 0.7;   // 16ths/32nds: a touch wider than eighths
            return s;
        }

        const double MergeEps = 0.06;
        const double MinRest = 0.25;

        static readonly Brush Fg = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A));
        static readonly Brush LineBrush = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44));
        static readonly Brush BarBrush = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33));
        static readonly Brush RestBrush = new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x22));
        static readonly Brush CursorBrush = new SolidColorBrush(Color.FromArgb(0xCC, 0xCC, 0x22, 0x33));

        // Bravura — MuseScore's SMuFL music font (OFL). SMuFL: 1 staff space = 0.25 em → em = 4 spaces; glyph
        // origin (y=0) = baseline = staff attach point, so glyphs are placed by baseline (see PlaceGlyph).
        static readonly FontFamily Bravura = new FontFamily(new System.Uri("pack://application:,,,/"), "./Fonts/#Bravura");
        const double MusicFontSize = StaffGap * 4;
        double bravuraBaseline = -1;

        // SMuFL codepoints (Private Use Area), built from char codes to avoid invisible glyphs in source.
        static string Smufl(int code) => ((char)code).ToString();
        static readonly string GlyphGClef = Smufl(0xE050), GlyphFClef = Smufl(0xE062), GlyphCClef = Smufl(0xE05C);
        static readonly string GlyphRestWhole = Smufl(0xE4E3), GlyphRestHalf = Smufl(0xE4E4), GlyphRestQuarter = Smufl(0xE4E5), GlyphRest8 = Smufl(0xE4E6), GlyphRest16 = Smufl(0xE4E7);
        static readonly string GlyphSharp = Smufl(0xE262), GlyphFlat = Smufl(0xE260), GlyphNatural = Smufl(0xE261);

        double BravuraBaseline()
        {
            if (bravuraBaseline < 0)
            {
                var tf = new Typeface(Bravura, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
                var ft = new FormattedText(Smufl(0xE0A4), System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, tf, MusicFontSize, Brushes.Black, 1.0);
                bravuraBaseline = ft.Baseline;
            }
            return bravuraBaseline;
        }

        TextBlock MakeGlyph(string s, Brush brush = null)
        {
            var tb = new TextBlock { Text = s, FontFamily = Bravura, FontSize = MusicFontSize, Foreground = brush ?? Fg };
            tb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return tb;
        }

        void PlaceGlyph(TextBlock tb, double left, double baselineY)
        {
            Canvas.SetLeft(tb, left);
            Canvas.SetTop(tb, baselineY - BravuraBaseline());
            canvas.Children.Add(tb);
        }

        readonly ScrollViewer scroll;
        readonly Canvas canvas;
        Rectangle cursor; // playback cursor drawn on the canvas; the view scrolls to keep it centred during playback

        List<TrackScore> tracks = new List<TrackScore>();
        double beatsPerBar = 4;     // QUARTER-beats per measure (= num·4/den), drives bar lines
        int tsNum = 4, tsDen = 4;   // displayed time signature
        double cursorScale = 1.0;   // raw-beat → display-beat factor for the playback cursor (= TimeSigScale)
        double barPhase;            // anacrusis (levée): the incomplete first measure is this many display-beats long (0 = none)
        // Empty-measure runs (display-beat ranges) collapsed to ONE measure each (single-staff multi-measure rests).
        readonly List<(double a, double b)> collapsedRuns = new List<(double, double)>();
        double contentLeftX;
        string keyName = "";

        // Per-staff key spelling state (set while building/drawing each staff).
        readonly int[] keyAcc = new int[7];
        bool keyUseFlats;
        int keyCount;
        int leadingSharpPc = -1;

        static readonly int[] LetterPc = { 0, 2, 4, 5, 7, 9, 11 };

        struct Staff { public double TopLineY; public int BottomLineStep; }
        sealed class StaffData { public Staff Geom; public ScoreClefKind Clef; public bool UseFlats; public int Count; public int Transpose; public int[] KeyAcc; public List<Slot> Slots; public List<Slot> BassSlots; public List<Slot> SustainSlots; public List<(int m0, int m1, int oct)> Ottavas; }
        int curTranspose; // the transpose of the staff currently being drawn (for note-hit concert MIDI)

        // Steps a note may rise above the top line before an 8va/15ma kicks in. The TOPMOST staff tolerates more
        // (up to 4 ledger lines = 8 steps — it only spills into the top margin); inner staves stay tight (≈2 ledger
        // lines) so they don't collide with the staff above.
        const int OttavaHeadroomTop = 8, OttavaHeadroomInner = 4;
        List<StaffData> staffData = new List<StaffData>();
        double systemTop, systemBottom;

        List<double> barNatBeat, barNatX;
        double[] axBeat, axX; // column beat → x (also cursor anchors)
        double layoutEnd;

        public ScoreView()
        {
            canvas = new Canvas { Background = Brushes.White, SnapsToDevicePixels = true };
            scroll = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Background = Brushes.White,
                Content = canvas,
            };
            Background = Brushes.White;
            Content = scroll;
            canvas.MouseLeftButtonDown += OnCanvasClick;
        }

        /// <summary>Raised when the user clicks a measure: the RAW (unscaled) beat at that measure's start. The host
        /// can map it to the riff/module covering that beat and select+scroll to it.</summary>
        public event Action<double> MeasureClicked;

        Rectangle measureHi; // translucent highlight over the clicked measure (cleared on Redraw)
        static readonly Brush MeasureHiBrush = new SolidColorBrush(Color.FromArgb(40, 80, 140, 220));

        void OnCanvasClick(object sender, MouseButtonEventArgs e)
        {
            if (axBeat == null || axBeat.Length == 0) return;
            double beat = XToBeat(e.GetPosition(canvas).X);                  // display beat under the cursor
            double rawBeat = beat / (cursorScale > 0 ? cursorScale : 1.0);
            if (EditMode)
            {
                var pt = e.GetPosition(canvas);
                // A click on a note head selects it.
                foreach (var h in editHits)
                    if (h.rect.Contains(pt)) { NoteEditClicked?.Invoke(h.rawBeat, h.midi); return; }
                // A click on the staff PLACES a note at that pitch: the vertical position snaps to the nearest line/space
                // (½ staff-space steps — boundaries at ¼ and ¾ of each space), converted to a concert pitch via the clef
                // + key signature. A click well outside any staff just moves the cursor.
                if (StaffStepAt(pt.Y, out int si, out int step))
                { NotePlaceClicked?.Invoke(rawBeat, StepToConcert(staffData[si], step)); return; }
                EditPositionClicked?.Invoke(rawBeat);
                return;
            }
            double mStart = MeasureStartBeat(MeasureIndex(beat));
            HighlightMeasure(mStart);
            MeasureClicked?.Invoke(mStart / (cursorScale > 0 ? cursorScale : 1.0)); // → raw beat for the host
        }

        // ---- score EDITING (note input directly on the staff) ----
        /// <summary>When true, clicks place/select instead of navigating, and an EDIT cursor is drawn.</summary>
        public bool EditMode { get; set; }
        /// <summary>Click on empty staff (edit mode) → the raw timeline beat under the cursor.</summary>
        public event Action<double> EditPositionClicked;
        /// <summary>Click on a note head (edit mode) → its raw beat + concert MIDI (for selection).</summary>
        public event Action<double, int> NoteEditClicked;
        /// <summary>Click on empty staff (edit mode) → raw beat + the concert MIDI of the clicked line/space (mouse entry).</summary>
        public event Action<double, int> NotePlaceClicked;

        // The staff (index) + snapped diatonic STEP under a vertical pixel Y. Snaps to the nearest line/space (½ staff
        // space = one diatonic step; boundaries at ¼ and ¾ of a space, i.e. Math.Round to the nearest Step). Accepts a
        // click a few steps above/below the 5 lines (ledger range); returns false if far outside every staff.
        bool StaffStepAt(double y, out int staffIndex, out int step)
        {
            staffIndex = -1; step = 0;
            if (staffData == null) return false;
            const int ledger = 8; // ~4 ledger lines of reach above/below the staff
            double best = double.MaxValue;
            for (int i = 0; i < staffData.Count; i++)
            {
                var g = staffData[i].Geom;
                // step at Y (invert YFor): YFor = (TopLineY+4·Gap) − (step−BottomLineStep)·Step
                double sf = (g.TopLineY + 4 * StaffGap - y) / Step + g.BottomLineStep;
                int snapped = (int)Math.Round(sf);
                if (snapped < g.BottomLineStep - ledger || snapped > g.BottomLineStep + 8 + ledger) continue;
                double dist = Math.Abs(y - YFor(g, snapped));
                if (dist < best) { best = dist; staffIndex = i; step = snapped; }
            }
            return staffIndex >= 0;
        }

        // Diatonic STEP (7·octaveNat + letter) → concert MIDI, applying the staff's key accidental for that letter and
        // undoing the staff transpose (written → concert). Inverse of SpellRaw's step for a natural + armure.
        static readonly int[] LetterPcTable = { 0, 2, 4, 5, 7, 9, 11 };
        int StepToConcert(StaffData sd, int step)
        {
            int L = ((step % 7) + 7) % 7;
            int octaveNat = (step - L) / 7;
            int acc = sd.KeyAcc != null ? sd.KeyAcc[L] : 0;          // the sharp/flat the armure gives this letter
            int written = 12 * (octaveNat + 1) + LetterPcTable[L] + acc;
            return written - sd.Transpose;                          // concert pitch (riff stores concert)
        }

        static readonly Brush EditCursorBrush = new SolidColorBrush(Color.FromRgb(0x66, 0xCC, 0x88));
        static readonly Brush ReadonlyCursorBrush = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
        static readonly Brush NoteSelBrush = new SolidColorBrush(Color.FromArgb(90, 0x66, 0xCC, 0x88));
        Rectangle editCursor, noteSelHi;
        double editCursorDispBeat = -1; bool editCursorOk = true;
        // Note head hit-boxes for selection: (canvas rect, raw beat, concert MIDI). Rebuilt each Redraw.
        readonly List<(System.Windows.Rect rect, double rawBeat, int midi)> editHits = new List<(System.Windows.Rect, double, int)>();

        /// <summary>Place the edit cursor at a raw beat (green = editable, grey = read-only measure).</summary>
        public void SetEditCursor(double rawBeat, bool editable)
        {
            editCursorDispBeat = Math.Max(0, rawBeat) * (cursorScale > 0 ? cursorScale : 1.0);
            editCursorOk = editable;
            DrawEditCursor();
        }

        /// <summary>Highlight the selected note (raw beat + concert MIDI), or clear it (midi &lt; 0). Matches the NEAREST
        /// head of that pitch (the drawn beat is snapped to the display grid, so an exact match would often miss).</summary>
        public void SetSelectedNote(double rawBeat, int midi)
        {
            if (noteSelHi != null) { canvas.Children.Remove(noteSelHi); noteSelHi = null; }
            if (midi < 0) return;
            bool found = false; System.Windows.Rect bestRect = default; double bestd = 0.30;
            foreach (var h in editHits)
                if (h.midi == midi && Math.Abs(h.rawBeat - rawBeat) < bestd) { bestd = Math.Abs(h.rawBeat - rawBeat); bestRect = h.rect; found = true; }
            if (!found) return;
            noteSelHi = new Rectangle { Width = bestRect.Width + 6, Height = bestRect.Height + 6, Fill = NoteSelBrush, RadiusX = 3, RadiusY = 3, IsHitTestVisible = false };
            Canvas.SetLeft(noteSelHi, bestRect.X - 3); Canvas.SetTop(noteSelHi, bestRect.Y - 3);
            Panel.SetZIndex(noteSelHi, 55); canvas.Children.Add(noteSelHi);
        }

        void DrawEditCursor()
        {
            if (editCursor != null) { canvas.Children.Remove(editCursor); editCursor = null; }
            if (!EditMode || editCursorDispBeat < 0) return;
            editCursor = new Rectangle { Width = 2.5, Height = systemBottom - systemTop + StaffGap * 2, Fill = editCursorOk ? EditCursorBrush : ReadonlyCursorBrush, IsHitTestVisible = false };
            Canvas.SetTop(editCursor, systemTop - StaffGap);
            Canvas.SetLeft(editCursor, BeatToX(editCursorDispBeat));
            Panel.SetZIndex(editCursor, 60);
            canvas.Children.Add(editCursor);
        }

        void HighlightMeasure(double mStartDisplayBeat)
        {
            if (measureHi != null) canvas.Children.Remove(measureHi);
            double x0 = BeatToX(mStartDisplayBeat), x1 = BeatToX(MeasureStartBeat(MeasureIndex(mStartDisplayBeat) + 1));
            measureHi = new Rectangle
            {
                Width = Math.Max(2, x1 - x0),
                Height = Math.Max(StaffGap, systemBottom - systemTop + 2 * StaffGap),
                Fill = MeasureHiBrush,
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(measureHi, x0);
            Canvas.SetTop(measureHi, systemTop - StaffGap);
            canvas.Children.Insert(0, measureHi); // behind the staff/notes
        }

        // Inverse of BeatToX: the display beat at a canvas X (monotonic interpolation over the column anchors).
        double XToBeat(double x)
        {
            if (axX == null || axX.Length == 0) return 0;
            if (x <= axX[0]) return axBeat[0];
            if (x >= axX[axX.Length - 1]) return axBeat[axBeat.Length - 1];
            int lo = 0, hi = axX.Length - 1;
            while (lo + 1 < hi) { int mid = (lo + hi) >> 1; if (axX[mid] <= x) lo = mid; else hi = mid; }
            double x0 = axX[lo], x1 = axX[lo + 1];
            double f = x1 > x0 ? (x - x0) / (x1 - x0) : 0;
            return axBeat[lo] + (axBeat[lo + 1] - axBeat[lo]) * f;
        }

        public void Configure(TrackScore s, int num = 4, int den = 4, double scale = 1.0)
            => Configure(s == null ? new List<TrackScore>() : new List<TrackScore> { s }, num, den, scale);

        // num/den = the time signature; bar lines fall every num·4/den QUARTER-beats (e.g. 6/8 → 3, 3/4 → 3).
        // scale = TimeSigScale: notes are already laid out in scaled display-beats, but the playback cursor
        // reports RAW timeline beats, so SetCursorBeat multiplies by it.
        public void Configure(IList<TrackScore> trackScores, int num = 4, int den = 4, double scale = 1.0, double pickupBeats = 0)
        {
            tracks = (trackScores ?? new List<TrackScore>()).Where(t => t != null).ToList();
            tsNum = Math.Max(1, num); tsDen = Math.Max(1, den);
            beatsPerBar = Math.Max(0.5, tsNum * 4.0 / tsDen);
            cursorScale = scale > 0 ? scale : 1.0;
            barPhase = pickupBeats > 1e-6 ? (pickupBeats * scale) % beatsPerBar : 0; // display-beats (scaled), reduced into one bar
            Redraw();
        }

        double stretchTarget;      // >0: stretch the note-area so layoutEnd == this (PDF export, fill the line)
        public double LayoutWidth => layoutEnd;        // natural note-area right edge (before RightPad) — for sizing
        public double ContentLeftPx => contentLeftX;   // x where notes begin (clef + key + time-sig width)

        /// <summary>Build ONE system off-screen and return its detached canvas (for embedding in a printed page).
        /// targetWidth &gt; 0 stretches the note spacing so the canvas is exactly that wide (glyphs stay native).</summary>
        public Canvas RenderStandalone(IList<TrackScore> trackScores, int num, int den, double scale, double targetWidth)
        {
            stretchTarget = targetWidth > 0 ? targetWidth : 0; // final barline sits at the right edge (fully justified)
            Configure(trackScores, num, den, scale);
            stretchTarget = 0;
            if (targetWidth > 0) canvas.Width = targetWidth; // trim the RightPad trailing margin
            scroll.Content = null; // detach so the canvas can be parented onto a FixedPage
            return canvas;
        }

        // ---- geometry ----
        static int BottomLineStep(ScoreClefKind c)
        {
            switch (c)
            {
                case ScoreClefKind.Bass: return 18;
                case ScoreClefKind.Alto: return 24;
                case ScoreClefKind.Tenor: return 22;
                default: return 30; // treble
            }
        }

        static ScoreClefKind Normalize(ScoreClefKind c) => c == ScoreClefKind.GrandStaff ? ScoreClefKind.Treble : c;
        double YFor(Staff st, int step) => (st.TopLineY + 4 * StaffGap) - (step - st.BottomLineStep) * Step;
        double ContentLeft => contentLeftX;

        void Redraw()
        {
            canvas.Children.Clear();
            cursor = null;
            measureHi = null; editCursor = null; noteSelHi = null; editHits.Clear();
            staffData.Clear();
            if (tracks.Count == 0) { canvas.Width = 200; canvas.Height = 80; return; }

            double totalBeats = beatsPerBar;
            foreach (var t in tracks) totalBeats = Math.Max(totalBeats, t.TotalBeats);

            int maxCount = 0;
            for (int i = 0; i < tracks.Count; i++)
            {
                var t = tracks[i];
                var dk = Engine.Score.KeySig.Derive(t.Key, t.Transpose);
                Array.Copy(dk.Acc, keyAcc, 7); keyUseFlats = dk.Flats; keyCount = dk.Count; leadingSharpPc = dk.LeadingSharpPc;

                // Split the staff by VOICE: MAIN (Voice 0 = auto stems), the ODD voices (1,3) drawn stems-DOWN, the EVEN
                // voices (2,4) stems-UP — so explicit note-input voices AND the auto bass(1)/sustain(2) both render apart.
                var main = new List<ScoreNote>(); var bass = new List<ScoreNote>(); var sustain = new List<ScoreNote>();
                foreach (var n in t.Notes) { if (n.Voice == 1 || n.Voice == 3) bass.Add(n); else if (n.Voice == 2 || n.Voice == 4) sustain.Add(n); else main.Add(n); }

                var slots = BuildSlots(main, t.Transpose, totalBeats);
                // 2-voice notation: force the moving figure's stems AWAY from a held note it overlaps in time — DOWN under a
                // held TOP note (sustain voice), UP over a held BASS note (bass voice, e.g. "basse tenue") so the figure's
                // stems don't cut through the held pedal note.
                foreach (var ms in slots)
                    if (ms.Kind == SlotKind.Note)
                    {
                        bool Overlaps(ScoreNote hv) => ms.Start < hv.StartBeat + hv.Beats - 1e-6 && ms.Start + ms.Dur > hv.StartBeat + 1e-6;
                        bool underTop = false; foreach (var sv in sustain) if (Overlaps(sv)) { underTop = true; break; }
                        if (underTop) ms.ForceUp = false;                    // held note ABOVE → figure stems down
                        else { foreach (var bv in bass) if (Overlaps(bv)) { ms.ForceUp = true; break; } } // held note BELOW → figure stems up
                    }
                ComputeSpelling(slots);
                var bassSlots = BuildBassSlots(bass, t.Transpose);
                ComputeSpelling(bassSlots);
                var sustainSlots = SplitNotes(BuildBassSlots(sustain, t.Transpose), new HashSet<int>()); // barline-split + tie the held note
                ComputeSpelling(sustainSlots);

                var clef = Normalize(t.Clef);
                var geom = new Staff { TopLineY = TopPad + i * (4 * StaffGap + InterGap), BottomLineStep = BottomLineStep(clef) };
                var ottavas = ComputeOttava(slots, geom, i == 0); // shift very-high measures down + record 8va/15ma spans
                staffData.Add(new StaffData { Geom = geom, Clef = clef, UseFlats = dk.Flats, Count = dk.Count, Transpose = t.Transpose, KeyAcc = (int[])keyAcc.Clone(), Slots = slots, BassSlots = bassSlots, SustainSlots = sustainSlots, Ottavas = ottavas });
                maxCount = Math.Max(maxCount, dk.Count);
            }

            // Empty-measure runs → a multi-measure rest with the count above. SINGLE staff: collapse the X axis for
            // EVERY run of ≥2 empty measures (each becomes one compact measure). MULTIPLE staves: keep the NATURAL
            // width for the leading run only (no collapse) so the bars stay aligned with the other staves.
            collapsedRuns.Clear();
            int totalM = MeasureCount(totalBeats);
            if (staffData.Count == 1)
            {
                var sd = staffData[0];
                var occ = new bool[totalM]; // measures touched by a note (onset or held-over) — in ANY voice
                Action<List<Slot>> markOcc = list =>
                {
                    if (list == null) return;
                    foreach (var s in list)
                        if (s.Kind == SlotKind.Note)
                        {
                            int ma = MeasureIndex(s.Start);
                            int mb = MeasureIndex(s.Start + s.Dur - 1e-9) + 1;
                            for (int m = Math.Max(0, ma); m < Math.Min(totalM, Math.Max(ma + 1, mb)); m++) occ[m] = true;
                        }
                };
                // Count occupancy across ALL voices — a bass/sustain (or explicit) voice may hold notes in measures the
                // MAIN voice leaves empty; those must NOT collapse into a multi-rest (else the other voice's notes get
                // crushed into the narrow block).
                markOcc(sd.Slots); markOcc(sd.BassSlots); markOcc(sd.SustainSlots);
                int m0 = 0;
                while (m0 < totalM)
                {
                    if (occ[m0]) { m0++; continue; }
                    int m1 = m0; while (m1 < totalM && !occ[m1]) m1++;
                    if (m1 - m0 >= 2)
                    {
                        double a = MeasureStartBeat(m0), b = MeasureStartBeat(m1);
                        sd.Slots.RemoveAll(s => s.Kind == SlotKind.Rest && s.Start >= a - 1e-6 && s.Start < b - 1e-6);
                        sd.Slots.Add(new Slot { Start = a, Dur = b - a, Kind = SlotKind.MultiRest, Count = m1 - m0 });
                        collapsedRuns.Add((a, b));
                    }
                    m0 = m1;
                }
                sd.Slots.Sort((p, q) => p.Start.CompareTo(q.Start));
            }
            else
            {
                foreach (var sd in staffData) // natural width: leave the layout alone, only swap leading rests for a multirest
                {
                    double firstNote = double.MaxValue;
                    foreach (var s in sd.Slots) if (s.Kind == SlotKind.Note) { firstNote = s.Start; break; }
                    int lb = firstNote == double.MaxValue ? totalM : MeasureIndex(firstNote);
                    if (lb < 2) continue;
                    double end = MeasureStartBeat(lb);
                    sd.Slots.RemoveAll(s => s.Kind == SlotKind.Rest && s.Start < end - 1e-6);
                    sd.Slots.Insert(0, new Slot { Start = 0, Dur = end, Kind = SlotKind.MultiRest, Count = lb });
                }
            }

            systemTop = staffData[0].Geom.TopLineY;
            systemBottom = staffData[staffData.Count - 1].Geom.TopLineY + 4 * StaffGap;
            contentLeftX = LeftPad + ClefBoxW + maxCount * AccW + TimeSigBoxW;
            keyName = Engine.Score.KeySig.Derive(tracks[0].Key, 0).Name; // concert key name for the label

            // Give EVERY voice (main + bass + sustain) an onset column, so a bass/sustain/explicit voice that plays where
            // the main voice rests still gets proper column spacing (else its notes pile up on the sparse rest columns).
            var layoutLists = new List<List<Slot>>();
            foreach (var s in staffData) { layoutLists.Add(s.Slots); if (s.BassSlots != null) layoutLists.Add(s.BassSlots); if (s.SustainSlots != null) layoutLists.Add(s.SustainSlots); }
            BuildLayout(totalBeats, layoutLists.ToArray());
            canvas.Width = layoutEnd + RightPad;
            canvas.Height = systemBottom + BottomPad;

            DrawStaffLines();
            DrawKeyLabel();
            DrawBarLines(totalBeats);
            foreach (var sd in staffData)
            {
                keyUseFlats = sd.UseFlats; keyCount = sd.Count; // for the armure
                curTranspose = sd.Transpose;
                AddClef(sd.Clef, sd.Geom);
                DrawArmure(sd.Geom, sd.Clef);
                AddTimeSig(sd.Geom);
                DrawStaffSlots(sd.Geom, sd.Slots);
                DrawBassVoice(sd.Geom, sd.BassSlots);
                DrawSustainVoice(sd.Geom, sd.SustainSlots);
                DrawTuplets(sd.Geom, sd.Slots);
                DrawTies(sd.Geom, sd.Slots);
                DrawTies(sd.Geom, sd.SustainSlots);
                DrawOttavaSpans(sd.Geom, sd.Slots, sd.Ottavas);
            }
            DrawCursor();
            DrawEditCursor();
        }

        // ---- ottava (8va / 15ma): keep very-high passages off the staff above ----
        // For each measure whose highest note rises too far above the staff, shift that measure's notes DOWN by N
        // octaves (display only — the spelling/accidentals are kept) and record a span so an "8va"/"15ma" dashed
        // line is drawn above it. Adjacent measures with the same shift merge into one span.
        List<(int m0, int m1, int oct)> ComputeOttava(List<Slot> slots, Staff geom, bool isTopStaff)
        {
            var spans = new List<(int, int, int)>();
            int topComfort = geom.BottomLineStep + 8 + (isTopStaff ? OttavaHeadroomTop : OttavaHeadroomInner);

            var shift = new Dictionary<int, int>();
            foreach (var s in slots)
            {
                if (s.Kind != SlotKind.Note || s.Spelled == null || s.Spelled.Count == 0) continue;
                int m = MeasureIndex(s.Start);
                int hi = int.MinValue;
                foreach (var (step, _) in s.Spelled) if (step > hi) hi = step;
                if (hi <= topComfort) continue;
                int oct = Math.Min(2, (int)Math.Ceiling((hi - topComfort) / 7.0)); // cap at 15ma
                if (!shift.TryGetValue(m, out int cur) || oct > cur) shift[m] = oct;
            }
            if (shift.Count == 0) return spans;

            foreach (var s in slots) // apply the shift to the steps of notes in shifted measures
            {
                if (s.Kind != SlotKind.Note || s.Spelled == null) continue;
                int m = MeasureIndex(s.Start);
                if (!shift.TryGetValue(m, out int oct) || oct == 0) continue;
                for (int k = 0; k < s.Spelled.Count; k++) s.Spelled[k] = (s.Spelled[k].step - 7 * oct, s.Spelled[k].acc);
            }

            var measures = new List<int>(shift.Keys); measures.Sort(); // merge runs of equal shift
            int start = measures[0], prev = measures[0], o = shift[measures[0]];
            for (int idx = 1; idx < measures.Count; idx++)
            {
                int mm = measures[idx];
                if (mm == prev + 1 && shift[mm] == o) { prev = mm; continue; }
                spans.Add((start, prev, o)); start = mm; prev = mm; o = shift[mm];
            }
            spans.Add((start, prev, o));
            return spans;
        }

        void DrawOttavaSpans(Staff geom, List<Slot> slots, List<(int m0, int m1, int oct)> spans)
        {
            if (spans == null) return;
            foreach (var (m0, m1, oct) in spans)
            {
                double x0 = BeatToX(MeasureStartBeat(m0)), x1 = BeatToX(MeasureStartBeat(m1 + 1));
                // Sit just above the span's highest (already-shifted) note; clamp near the canvas top.
                int hi = geom.BottomLineStep + 8;
                foreach (var s in slots)
                {
                    if (s.Kind != SlotKind.Note || s.Spelled == null) continue;
                    int m = MeasureIndex(s.Start);
                    if (m < m0 || m > m1) continue;
                    foreach (var (step, _) in s.Spelled) if (step > hi) hi = step;
                }
                double y = Math.Max(StaffGap * 0.5, YFor(geom, hi) - StaffGap * 1.6);
                var lbl = new TextBlock { Text = oct >= 2 ? "15ma" : "8va", Foreground = Fg, FontStyle = FontStyles.Italic, FontWeight = FontWeights.Bold, FontFamily = new FontFamily("Times New Roman"), FontSize = StaffGap * 1.7 };
                lbl.Measure(new Size(1000, 1000));
                Canvas.SetLeft(lbl, x0);
                Canvas.SetTop(lbl, y - lbl.DesiredSize.Height * 0.75);
                canvas.Children.Add(lbl);

                double xs = x0 + lbl.DesiredSize.Width + 3;
                if (x1 > xs)
                    canvas.Children.Add(new Line { X1 = xs, Y1 = y, X2 = x1, Y2 = y, Stroke = Fg, StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 3, 2 } });
                canvas.Children.Add(new Line { X1 = x1, Y1 = y, X2 = x1, Y2 = y + StaffGap * 0.9, Stroke = Fg, StrokeThickness = 1 }); // end hook (down)
            }
        }

        // ---- pitch spelling ----
        (int L, int a, int step) SpellRaw(int midi)
        {
            int p = ((midi % 12) + 12) % 12;
            int L = -1, a = 0;
            for (int l = 0; l < 7; l++) { int kp = (((LetterPc[l] + keyAcc[l]) % 12) + 12) % 12; if (kp == p) { L = l; a = keyAcc[l]; break; } }
            if (L < 0) for (int l = 0; l < 7; l++) if (LetterPc[l] == p && keyAcc[l] != 0) { L = l; a = 0; break; }
            if (L < 0)
            {
                bool forceSharp = p == leadingSharpPc; // the minor sensible is always a sharp
                if (keyUseFlats && !forceSharp) { for (int l = 0; l < 7; l++) if (LetterPc[l] == (p + 1) % 12) { L = l; a = -1; break; } }
                else { for (int l = 0; l < 7; l++) if (LetterPc[l] == (p + 11) % 12) { L = l; a = +1; break; } }
            }
            if (L < 0) { L = 0; a = 0; }
            int octaveNat = (midi - a) / 12 - 1;
            return (L, a, 7 * octaveNat + L);
        }

        // Within-measure accidental rule: an accidental holds for the rest of the measure on that exact staff
        // position and isn't redrawn until a different one appears; state resets each bar from the key sig.
        void ComputeSpelling(List<Slot> slots)
        {
            var active = new Dictionary<int, int>();
            int curMeasure = int.MinValue;
            foreach (var s in slots)
            {
                int m = MeasureIndex(s.Start);
                if (m != curMeasure) { active.Clear(); curMeasure = m; }
                if (s.Kind == SlotKind.Rest || s.Written == null) continue;
                s.Spelled = new List<(int, string)>();
                foreach (int w in s.Written)
                {
                    var (L, a, step) = SpellRaw(w);
                    int eff = active.TryGetValue(step, out var v) ? v : keyAcc[L];
                    string acc = a != eff ? (a > 0 ? "♯" : a < 0 ? "♭" : "♮") : null;
                    if (acc != null) active[step] = a;
                    s.Spelled.Add((step, acc));
                }
                s.HasAcc = s.Spelled.Any(p => p.acc != null);
            }
        }

        static int[] ArmureOffsets(ScoreClefKind c, bool flats)
        {
            switch (c)
            {
                case ScoreClefKind.Bass: return flats ? new[] { 2, 5, 1, 4, 0, 3, -1 } : new[] { 6, 3, 7, 4, 1, 5, 2 };
                case ScoreClefKind.Alto:
                case ScoreClefKind.Tenor: return flats ? new[] { 3, 6, 2, 5, 1, 4, 0 } : new[] { 7, 4, 8, 5, 2, 6, 3 };
                default: return flats ? new[] { 4, 7, 3, 6, 2, 5, 1 } : new[] { 8, 5, 9, 6, 3, 7, 4 };
            }
        }

        struct Glyph { public bool Filled; public bool Stem; public int Flags; public bool Dotted; public bool Triplet; }

        static readonly (double v, bool f, bool s, int fl, bool d)[] NoteTable =
        {
            (4.0,   false, false, 0, false), (3.0, false, true, 0, true), (2.0, false, true, 0, false),
            (1.5,   true,  true,  0, true),  (1.0, true,  true, 0, false),
            (0.75,  true,  true,  1, true),  (0.5, true,  true, 1, false),
            (0.375, true,  true,  2, true),  (0.25, true, true, 2, false), (0.125, true, true, 3, false),
        };

        // Triplet durations (beats) → the base note value whose glyph they borrow (value × 1.5). A triplet-eighth
        // (1/3 beat) draws as an eighth, a triplet-quarter (2/3) as a quarter, etc. Used so ternary-feel notes shown
        // in a SIMPLE meter (no ×1.5 scale) render as triolets instead of being misclassified.
        static readonly (double v, double baseV)[] TripletTable =
        {
            (4.0 / 3, 2.0), (2.0 / 3, 1.0), (1.0 / 3, 0.5), (1.0 / 6, 0.25), (1.0 / 12, 0.125),
        };

        static Glyph GlyphForValue(double v)
        {
            int best = 0; double bd = double.MaxValue;
            for (int i = 0; i < NoteTable.Length; i++) { double d = Math.Abs(Math.Log(v / NoteTable[i].v)); if (d < bd) { bd = d; best = i; } }
            var t = NoteTable[best];
            return new Glyph { Filled = t.f, Stem = t.s, Flags = t.fl, Dotted = t.d };
        }

        static Glyph Classify(double beats)
        {
            double b = Math.Max(0.0625, beats);
            int best = 0; double bestd = double.MaxValue;
            for (int i = 0; i < NoteTable.Length; i++) { double d = Math.Abs(Math.Log(b / NoteTable[i].v)); if (d < bestd) { bestd = d; best = i; } }
            int tb = 0; double tbd = double.MaxValue;
            for (int i = 0; i < TripletTable.Length; i++) { double d = Math.Abs(Math.Log(b / TripletTable[i].v)); if (d < tbd) { tbd = d; tb = i; } }
            if (tbd < bestd) { var g = GlyphForValue(TripletTable[tb].baseV); g.Triplet = true; return g; } // closer to a triplet value
            var t = NoteTable[best];
            return new Glyph { Filled = t.f, Stem = t.s, Flags = t.fl, Dotted = t.d };
        }

        enum SlotKind { Note, Rest, MultiRest }
        sealed class Slot { public double Start, Dur; public SlotKind Kind; public List<int> Written; public Glyph G; public bool HasAcc; public List<(int step, string acc)> Spelled; public int Count; public bool TieStart, TieEnd; public bool Arp; public bool? ForceUp; }

        // ---- slot building (chords + rests in time order) ----
        List<Slot> BuildSlots(List<ScoreNote> notes, int transpose, double totalBeats)
        {
            var slots = new List<Slot>();
            var noteSlots = new List<Slot>();
            if (notes != null && notes.Count > 0)
            {
                noteSlots.AddRange(notes
                    .GroupBy(n => (long)Math.Round(n.StartBeat * 48))
                    .OrderBy(g => g.Key)
                    .Select(g => new Slot
                    {
                        Start = g.Min(n => n.StartBeat),
                        Dur = g.Max(n => n.Beats),
                        Kind = SlotKind.Note,
                        Written = g.Select(n => n.Midi + transpose).Distinct().OrderBy(p => p).ToList(),
                        G = Classify(g.Max(n => n.Beats)),
                        Arp = g.Any(n => n.Arpeggio),
                    }));
            }

            // Beats that carry a triplet (ternary) — their rests use triplet rest values/glyphs, not binary ones.
            var tern = new HashSet<int>();
            foreach (var sl in noteSlots) if (sl.G.Triplet) tern.Add((int)Math.Floor(sl.Start + 1e-9));

            // Split a note that overruns a triolet into a triolet member TIED to its continuation (so a note can't
            // straddle a triplet beat). The piece inside the ternary beat reclassifies as a triplet note.
            slots.AddRange(SplitNotes(noteSlots, tern));

            var iv = (notes ?? new List<ScoreNote>()).Select(n => new[] { n.StartBeat, n.StartBeat + n.Beats }).OrderBy(a => a[0]).ToList();
            var merged = new List<double[]>();
            foreach (var a in iv)
            {
                if (merged.Count > 0 && a[0] <= merged[merged.Count - 1][1] + MergeEps) merged[merged.Count - 1][1] = Math.Max(merged[merged.Count - 1][1], a[1]);
                else merged.Add(new[] { a[0], a[1] });
            }
            double pos = 0;
            foreach (var m in merged) { if (m[0] - pos >= MinRest) AddRests(slots, pos, m[0], tern); pos = Math.Max(pos, m[1]); }
            if (totalBeats - pos >= MinRest) AddRests(slots, pos, totalBeats, tern);

            slots.Sort((a, b) => a.Start.CompareTo(b.Start));
            return slots;
        }

        // Split each note at (a) the beat boundaries adjacent to a ternary beat, so no note straddles a triplet, and
        // (b) every BARLINE it crosses — a note longer than the measure fills the rest of the bar then continues in the
        // next bar(s) for the remaining value. Pieces are tied (TieStart/TieEnd) and each reclassifies by its own duration.
        List<Slot> SplitNotes(List<Slot> noteSlots, HashSet<int> tern)
        {
            double bpb = beatsPerBar > 0 ? beatsPerBar : 4;
            var outp = new List<Slot>();
            foreach (var s in noteSlots)
            {
                double start = s.Start, end = s.Start + s.Dur;
                var pts = new List<double> { start };
                for (int t = (int)Math.Floor(start + 1e-9) + 1; t < end - 1e-6; t++)
                    if (tern.Contains(t - 1) || tern.Contains(t)) pts.Add(t); // split around ternary beats
                for (double bl = NextBarline(start); bl < end - 1e-6; bl += bpb)
                    pts.Add(bl);                                               // split at every barline crossed
                pts.Add(end);
                pts.Sort();
                var clean = new List<double>();
                foreach (var p in pts) if (clean.Count == 0 || p - clean[clean.Count - 1] > 1e-6) clean.Add(p);
                pts = clean;
                if (pts.Count <= 2) { outp.Add(s); continue; }
                for (int k = 0; k < pts.Count - 1; k++)
                    outp.Add(new Slot
                    {
                        Start = pts[k], Dur = pts[k + 1] - pts[k], Kind = SlotKind.Note,
                        Written = s.Written, G = Classify(pts[k + 1] - pts[k]),
                        TieEnd = k > 0, TieStart = k < pts.Count - 2, Arp = s.Arp && k == 0,
                    });
            }
            return outp;
        }

        // Tie curves between consecutive split pieces (a slot flagged TieStart → the next note slot).
        void DrawTies(Staff st, List<Slot> slots)
        {
            double headW = StaffGap * 1.35;
            for (int i = 0; i < slots.Count; i++)
            {
                if (slots[i].Kind != SlotKind.Note || !slots[i].TieStart || slots[i].Spelled == null) continue;
                int j = i + 1; while (j < slots.Count && slots[j].Kind != SlotKind.Note) j++;
                if (j >= slots.Count) continue;
                double x1 = SlotX(slots[i]) + headW * 0.45, x2 = SlotX(slots[j]) - headW * 0.45;
                if (x2 <= x1) continue;
                foreach (var (step, _) in slots[i].Spelled)
                {
                    double y = YFor(st, step) + Step * 1.3;
                    var fig = new PathFigure { StartPoint = new Point(x1, y), IsClosed = false };
                    fig.Segments.Add(new QuadraticBezierSegment(new Point((x1 + x2) / 2, y + Step * 1.6), new Point(x2, y), true));
                    var geo = new PathGeometry(); geo.Figures.Add(fig);
                    canvas.Children.Add(new Path { Stroke = Fg, StrokeThickness = 1.3, Data = geo });
                }
            }
        }

        static readonly double[] TernRestVals = { 2.0 / 3, 1.0 / 3, 1.0 / 6 }; // triplet rest durations within a beat

        void AddRests(List<Slot> slots, double from, double to, HashSet<int> tern)
        {
            double s = from;
            while (s < to - 1e-6)
            {
                int beat = (int)Math.Floor(s + 1e-9);
                if (tern != null && tern.Contains(beat)) { double e = Math.Min(to, beat + 1.0); FillRest(slots, s, e, true); s = e; continue; }
                // binary run: up to the next measure boundary, but stop before any ternary beat
                double e2 = Math.Min(to, NextBarline(s));
                if (tern != null) for (double t = Math.Floor(s + 1e-9) + 1; t < e2 - 1e-6; t += 1) if (tern.Contains((int)t)) { e2 = t; break; }
                FillRest(slots, s, e2, false);
                s = e2;
            }
        }

        // Greedily fill [from,to) with rests on the binary (NoteTable) or ternary (TernRestVals) grid.
        void FillRest(List<Slot> slots, double from, double to, bool triplet)
        {
            double pos = from, rem = to - from; int guard = 0;
            while (rem >= MinRest - 1e-6 && guard++ < 64)
            {
                double pick = -1; bool dotted = false;
                if (triplet) { foreach (var v in TernRestVals) if (v <= rem + 1e-6) { pick = v; break; } }
                else for (int i = 0; i < NoteTable.Length; i++) if (NoteTable[i].v <= rem + 1e-6) { pick = NoteTable[i].v; dotted = NoteTable[i].d; break; }
                if (pick < 0) break;
                slots.Add(new Slot { Start = pos, Dur = pick, Kind = SlotKind.Rest, G = new Glyph { Dotted = dotted, Triplet = triplet } });
                pos += pick; rem -= pick;
            }
        }

        // ---- MuseScore-style layout: one X per onset column, width ∝ duration^0.6 ----
        void BuildLayout(double totalBeats, List<Slot>[] allSlots)
        {
            var cols = new List<double>();
            void AddCol(double b)
            {
                b = Math.Max(0, Math.Min(totalBeats, b));
                if (!cols.Any(v => Math.Abs(v - b) < 1.0 / 96)) cols.Add(b);
            }
            AddCol(0);
            // Bar lines (phased by the levée), but skip the ones INSIDE a collapsed multi-measure rest (it spans them as one block).
            for (double bar = barPhase > 1e-6 ? barPhase : 0; bar <= totalBeats + 1e-6; bar += beatsPerBar)
                if (!collapsedRuns.Any(r => bar > r.a + 1e-6 && bar < r.b - 1e-6)) AddCol(bar);
            foreach (var slots in allSlots) foreach (var s in slots) AddCol(s.Start);
            cols.Sort();

            var accCol = new HashSet<int>();
            foreach (var slots in allSlots)
                foreach (var s in slots)
                    if (s.Kind != SlotKind.Rest && s.HasAcc) { int ci = ColIndex(cols, s.Start); if (ci >= 0) accCol.Add(ci); }

            axBeat = cols.ToArray();
            axX = new double[cols.Count];
            barNatBeat = new List<double>(); barNatX = new List<double>();
            double x = ContentLeft;
            layoutEnd = x;
            for (int i = 0; i < cols.Count; i++)
            {
                double b = cols[i];
                // The final bar IS the right edge — no trailing space, no separate barline (avoids the end gap).
                if (i == cols.Count - 1 && IsBar(b) && b > 1e-6) { axX[i] = x; layoutEnd = x; break; }
                if (IsBar(b) && b > 1e-6) { barNatBeat.Add(b); barNatX.Add(x); x += MeasurePad; }
                double lead = accCol.Contains(i) ? AccReserve : 0;
                axX[i] = x + lead + HeadHalf;
                double dur = i + 1 < cols.Count ? cols[i + 1] - b : Math.Max(1.0 / beatsPerBar, totalBeats - b);
                // A collapsed multi-rest (the column at a run's start) takes ONE measure's width (then stretches like the rest).
                bool runStart = collapsedRuns.Any(r => Math.Abs(b - r.a) < 1e-6);
                double adv = runStart ? SpaceFor(beatsPerBar) : SpaceFor(dur);
                x += lead + adv;
                layoutEnd = x;
            }

            // PDF export: stretch (or squeeze) the note area so the system fills the target width, scaling the
            // spacing only — glyphs/noteheads stay native size (they're drawn at these anchors).
            if (stretchTarget > ContentLeft && layoutEnd > ContentLeft)
            {
                double x0 = ContentLeft, natural = layoutEnd - x0, f = (stretchTarget - x0) / natural;
                for (int i = 0; i < axX.Length; i++) axX[i] = x0 + (axX[i] - x0) * f;
                for (int i = 0; i < barNatX.Count; i++) barNatX[i] = x0 + (barNatX[i] - x0) * f;
                layoutEnd = stretchTarget;
            }
        }

        bool IsBar(double beat) { double m = (beat - barPhase) / beatsPerBar; return Math.Abs(m - Math.Round(m)) < 1e-6; }

        // ---- measure indexing (anacrusis-aware) ----
        // With a levée (barPhase>0) measure 0 is the short pickup bar, then 1,2,… are the full bars; full barlines fall at
        // barPhase + k·beatsPerBar. With barPhase==0 these all reduce to the plain floor(beat/beatsPerBar) grid.
        int MeasureIndex(double beat)
        {
            if (barPhase > 1e-6)
                return beat < barPhase - 1e-9 ? 0 : 1 + (int)Math.Floor((beat - barPhase) / beatsPerBar + 1e-9);
            return (int)Math.Floor(beat / beatsPerBar + 1e-9);
        }
        double MeasureStartBeat(int m)
        {
            if (barPhase > 1e-6) return m <= 0 ? 0 : barPhase + (m - 1) * beatsPerBar;
            return m * beatsPerBar;
        }
        int MeasureCount(double totalBeats)
        {
            if (barPhase > 1e-6) return 1 + Math.Max(1, (int)Math.Ceiling((totalBeats - barPhase) / beatsPerBar - 1e-6));
            return Math.Max(1, (int)Math.Ceiling(totalBeats / beatsPerBar - 1e-6));
        }
        // The first full barline strictly after `beat` (used to split notes/rests that cross a barline).
        double NextBarline(double beat) => MeasureStartBeat(MeasureIndex(beat) + 1);

        static int ColIndex(List<double> cols, double beat)
        {
            for (int i = 0; i < cols.Count; i++) if (Math.Abs(cols[i] - beat) < 1.0 / 96) return i;
            return -1;
        }

        // The drawn x of the bar line at a beat (BeatToX lands past it by MeasurePad+HeadHalf).
        double BarLineX(double beat)
        {
            if (barNatBeat != null) for (int i = 0; i < barNatBeat.Count; i++) if (Math.Abs(barNatBeat[i] - beat) < 1e-6) return barNatX[i];
            return BeatToX(beat);
        }

        double SlotX(Slot s)
        {
            if (axBeat != null) for (int i = 0; i < axBeat.Length; i++) if (Math.Abs(axBeat[i] - s.Start) < 1.0 / 96) return axX[i];
            return BeatToX(s.Start);
        }

        double BeatToX(double beat)
        {
            if (axBeat == null || axBeat.Length == 0) return ContentLeft;
            if (beat <= axBeat[0]) return axX[0];
            if (beat >= axBeat[axBeat.Length - 1]) return axX[axX.Length - 1];
            int lo = 0, hi = axBeat.Length - 1;
            while (lo + 1 < hi) { int mid = (lo + hi) >> 1; if (axBeat[mid] <= beat) lo = mid; else hi = mid; }
            double b0 = axBeat[lo], b1 = axBeat[lo + 1];
            double f = b1 > b0 ? (beat - b0) / (b1 - b0) : 0;
            return axX[lo] + (axX[lo + 1] - axX[lo]) * f;
        }

        // ---- staff chrome ----
        void DrawStaffLines()
        {
            foreach (var sd in staffData)
                for (int k = 0; k <= 4; k++)
                {
                    double y = sd.Geom.TopLineY + k * StaffGap;
                    canvas.Children.Add(new Line { X1 = LeftPad, Y1 = y, X2 = layoutEnd, Y2 = y, Stroke = LineBrush, StrokeThickness = 1 });
                }
            if (staffData.Count > 1) // a system bracket joining the staves
                canvas.Children.Add(new Line { X1 = LeftPad, Y1 = systemTop, X2 = LeftPad, Y2 = systemBottom, Stroke = Fg, StrokeThickness = 1.6 });
        }

        void DrawArmure(Staff st, ScoreClefKind clef)
        {
            if (keyCount <= 0) return;
            var offs = ArmureOffsets(clef, keyUseFlats);
            string g = keyUseFlats ? GlyphFlat : GlyphSharp;
            for (int i = 0; i < keyCount && i < 7; i++)
                PlaceGlyph(MakeGlyph(g), LeftPad + ClefBoxW + i * AccW, YFor(st, st.BottomLineStep + offs[i]));
        }

        void AddClef(ScoreClefKind c, Staff st)
        {
            string g; int refStep;
            switch (c)
            {
                case ScoreClefKind.Bass: g = GlyphFClef; refStep = 24; break;   // fa3 (4th line)
                case ScoreClefKind.Alto:
                case ScoreClefKind.Tenor: g = GlyphCClef; refStep = 28; break;  // do4 (middle line)
                default: g = GlyphGClef; refStep = 32; break;                   // sol4 (2nd line)
            }
            PlaceGlyph(MakeGlyph(g), LeftPad + 4, YFor(st, refStep));
        }

        void DrawKeyLabel()
        {
            if (string.IsNullOrEmpty(keyName)) return;
            var tb = new TextBlock { Text = keyName, Foreground = Fg, FontSize = 13, FontStyle = FontStyles.Italic };
            Canvas.SetLeft(tb, LeftPad);
            Canvas.SetTop(tb, Math.Max(2, systemTop - StaffGap * 4.2));
            canvas.Children.Add(tb);
        }

        void AddTimeSig(Staff st)
        {
            double cx = contentLeftX - TimeSigBoxW * 0.5; // aligned across staves, just left of the notes
            var top = MakeGlyph(TimeDigits(tsNum));
            var bot = MakeGlyph(TimeDigits(tsDen));
            PlaceGlyph(top, cx - top.DesiredSize.Width / 2, YFor(st, st.BottomLineStep + 6));
            PlaceGlyph(bot, cx - bot.DesiredSize.Width / 2, YFor(st, st.BottomLineStep + 2));
        }

        static string TimeDigits(int n)
        {
            var sb = new System.Text.StringBuilder();
            foreach (char ch in n.ToString()) sb.Append((char)(0xE080 + (ch - '0')));
            return sb.ToString();
        }

        void DrawBarLines(double totalBeats)
        {
            for (int i = 0; i < barNatX.Count; i++)
            {
                if (barNatBeat[i] < 1e-6) continue;
                double x = barNatX[i];
                canvas.Children.Add(new Line { X1 = x, Y1 = systemTop, X2 = x, Y2 = systemBottom, Stroke = BarBrush, StrokeThickness = 1 });
            }
            canvas.Children.Add(new Line { X1 = layoutEnd, Y1 = systemTop, X2 = layoutEnd, Y2 = systemBottom, Stroke = Fg, StrokeThickness = 2 });
        }

        // ---- notes / rests / beams ----
        void DrawStaffSlots(Staff st, List<Slot> slots)
        {
            int i = 0;
            while (i < slots.Count)
            {
                var s = slots[i];
                if (s.Kind == SlotKind.MultiRest) { DrawMultiRest(st, s); i++; continue; }
                if (s.Kind == SlotKind.Rest) { DrawRest(st, SlotX(s), s.Dur, s.G.Dotted, s.G.Triplet); i++; continue; }

                if (s.G.Flags >= 1) // beam a run of consecutive contiguous short notes
                {
                    // In compound (ternary), a run that STARTS on a beat — directly, or right after an on-beat rest
                    // (demi-soupir) — may beam across SEVERAL beats (6/9 eighths, 2 doubles + 4 croches, …), like
                    // MuseScore. Otherwise (off-beat run, or simple meter) beam within a single beat group.
                    bool acrossBeats = tsDen == 8 && AnchoredOnBeat(slots, i);
                    var group = new List<Slot> { s };
                    int j = i + 1;
                    while (j < slots.Count && slots[j].Kind == SlotKind.Note && slots[j].G.Flags >= 1
                        && slots[j].Start - (group[group.Count - 1].Start + group[group.Count - 1].Dur) <= 0.1 // contiguous
                        && SameMeasure(slots[j].Start, group[0].Start)                                          // within the bar
                        && (acrossBeats || SameBeamGroup(slots[j].Start, group[group.Count - 1].Start)))        // beat group
                    { group.Add(slots[j]); j++; }
                    if (group.Count >= 2) { DrawBeamGroup(st, group); i = j; continue; }
                }
                DrawSingleNote(st, s);
                i++;
            }
        }

        // A multi-measure rest: a thick bar centred on the staff (with end serifs) + the measure count above.
        void DrawMultiRest(Staff st, Slot s)
        {
            // A UNIFORM-length bar centred in its measure span, never wider than the measure (not stretched). Use the
            // actual bar-line x for the end (BeatToX overshoots it by MeasurePad+HeadHalf → the bar would cross it).
            double span0 = BeatToX(s.Start), span1 = BarLineX(s.Start + s.Dur), cx = (span0 + span1) / 2;
            double w = Math.Min(StaffGap * 5, (span1 - span0) - StaffGap * 1.4);
            if (w < StaffGap * 1.5) w = StaffGap * 1.5;
            double x0 = cx - w / 2, x1 = cx + w / 2;
            double midY = YFor(st, st.BottomLineStep + 4); // middle line
            double th = StaffGap * 1.1;
            var bar = new Rectangle { Width = x1 - x0, Height = th, Fill = Fg };
            Canvas.SetLeft(bar, x0); Canvas.SetTop(bar, midY - th / 2); canvas.Children.Add(bar);
            foreach (double ex in new[] { x0, x1 }) // end serifs
                canvas.Children.Add(new Line { X1 = ex, Y1 = midY - StaffGap, X2 = ex, Y2 = midY + StaffGap, Stroke = Fg, StrokeThickness = 1.6 });

            var num = new TextBlock { Text = s.Count.ToString(), Foreground = Fg, FontFamily = new FontFamily("Times New Roman"), FontWeight = FontWeights.Bold, FontSize = StaffGap * 2.3 };
            num.Measure(new Size(1000, 1000));
            Canvas.SetLeft(num, (x0 + x1) / 2 - num.DesiredSize.Width / 2);
            Canvas.SetTop(num, YFor(st, st.BottomLineStep + 8) - StaffGap * 1.6 - num.DesiredSize.Height * 0.7);
            canvas.Children.Add(num);
        }

        // Beam group span in display quarter-beats: a compound x/8 beams by the dotted-quarter beat (= 3 eighths),
        // a simple x/4 beams by the quarter beat (= 2 eighths). beatsPerBar is always a multiple of this, so beam
        // groups never cross a bar line.
        double BeamUnit => tsDen == 8 ? 1.5 : 1.0;
        bool SameBeamGroup(double a, double b) => (int)Math.Floor(a / BeamUnit + 1e-9) == (int)Math.Floor(b / BeamUnit + 1e-9);
        bool SameMeasure(double a, double b) => MeasureIndex(a) == MeasureIndex(b);
        bool OnBeat(double x) { double u = x / BeamUnit; return Math.Abs(u - Math.Round(u)) < 1e-6; }

        // A beamable run is "beat-anchored" (so it may extend across beats) when its first note falls on a beat,
        // or the slot just before it is an on-beat rest (e.g. an eighth-rest that starts the beat).
        bool AnchoredOnBeat(List<Slot> slots, int i)
        {
            if (OnBeat(slots[i].Start)) return true;
            return i > 0 && slots[i - 1].Kind == SlotKind.Rest && OnBeat(slots[i - 1].Start);
        }

        // One slot per bass note (a single low note, its own duration). Spelling is computed by the caller.
        List<Slot> BuildBassSlots(List<ScoreNote> notes, int transpose)
        {
            var outp = new List<Slot>();
            if (notes == null) return outp;
            foreach (var n in notes.OrderBy(x => x.StartBeat))
                outp.Add(new Slot { Start = n.StartBeat, Dur = n.Beats, Kind = SlotKind.Note, Written = new List<int> { n.Midi + transpose }, G = Classify(n.Beats) });
            return outp;
        }

        // The bass voice: each note drawn with a forced DOWNWARD stem (it's the lower voice), so it reads apart
        // from the chord above it. No beaming (the bass is a steady one-per-beat pulse).
        void DrawBassVoice(Staff st, List<Slot> slots)
        {
            if (slots == null) return;
            foreach (var s in slots)
            {
                if (s.Kind != SlotKind.Note || s.Spelled == null) continue;
                double x = SlotX(s);
                DrawHeads(st, x, s.Spelled, s.G, out int minStep, out int maxStep, s.Written, s.Start);
                if (!s.G.Stem) continue;
                double headW = StaffGap * 1.35, stemLen = StaffGap * 3.3;
                double stemX = x - headW / 2 + 0.7;                 // stem down on the left
                double baseY = YFor(st, maxStep), tipY = YFor(st, minStep) + stemLen;
                canvas.Children.Add(new Line { X1 = stemX, Y1 = baseY, X2 = stemX, Y2 = tipY, Stroke = Fg, StrokeThickness = 1.3 });
                for (int f = 0; f < s.G.Flags; f++) canvas.Children.Add(BuildFlag(stemX, tipY - f * (StaffGap * 1.0), false));
            }
        }

        // A held note over a quicker figure: its own voice, drawn with UPWARD stems (it sits above the figure), and
        // split/tied across barlines like the main voice. Mirrors DrawBassVoice (which forces stems DOWN).
        void DrawSustainVoice(Staff st, List<Slot> slots)
        {
            if (slots == null) return;
            foreach (var s in slots)
            {
                if (s.Kind != SlotKind.Note || s.Spelled == null) continue;
                double x = SlotX(s);
                DrawHeads(st, x, s.Spelled, s.G, out int minStep, out int maxStep, s.Written, s.Start);
                if (!s.G.Stem) continue;
                double headW = StaffGap * 1.35, stemLen = StaffGap * 3.3;
                double stemX = x + headW / 2 - 0.7;                 // stem up on the right
                double baseY = YFor(st, minStep), tipY = YFor(st, maxStep) - stemLen;
                canvas.Children.Add(new Line { X1 = stemX, Y1 = baseY, X2 = stemX, Y2 = tipY, Stroke = Fg, StrokeThickness = 1.3 });
                for (int f = 0; f < s.G.Flags; f++) canvas.Children.Add(BuildFlag(stemX, tipY + f * (StaffGap * 1.0), true));
            }
        }

        void DrawSingleNote(Staff st, Slot s)
        {
            double x = SlotX(s);
            DrawHeads(st, x, s.Spelled, s.G, out int minStep, out int maxStep, s.Written, s.Start);
            if (s.Arp) { double ya = YFor(st, maxStep), yb = YFor(st, minStep); DrawArpeggio(x - StaffGap * 1.05, Math.Min(ya, yb) - StaffGap * 0.6, Math.Max(ya, yb) + StaffGap * 0.6); }
            if (!s.G.Stem) return;

            int middle = st.BottomLineStep + 4;
            bool up = s.ForceUp ?? ((minStep + maxStep) / 2.0 < middle);
            double headW = StaffGap * 1.35, stemLen = StaffGap * 3.6, stemX, baseY, tipY;
            if (up) { stemX = x + headW / 2 - 0.7; baseY = YFor(st, minStep); tipY = YFor(st, maxStep) - stemLen; }
            else { stemX = x - headW / 2 + 0.7; baseY = YFor(st, maxStep); tipY = YFor(st, minStep) + stemLen; }
            canvas.Children.Add(new Line { X1 = stemX, Y1 = baseY, X2 = stemX, Y2 = tipY, Stroke = Fg, StrokeThickness = 1.3 });

            for (int f = 0; f < s.G.Flags; f++)
                canvas.Children.Add(BuildFlag(stemX, tipY + (up ? 1 : -1) * f * (StaffGap * 1.0), up));
        }

        // A vertical wavy line at the left of a chord = the ARPEGGIO (rolled-chord) mark.
        void DrawArpeggio(double x, double yTop, double yBot)
        {
            double amp = StaffGap * 0.42, seg = StaffGap * 0.72;
            var fig = new PathFigure { StartPoint = new Point(x, yTop) };
            double y = yTop; bool right = true; int guard = 0;
            while (y < yBot - 1e-6 && guard++ < 64)
            {
                double ny = Math.Min(y + seg, yBot);
                fig.Segments.Add(new QuadraticBezierSegment(new Point(x + (right ? amp : -amp), (y + ny) / 2), new Point(x, ny), true));
                y = ny; right = !right;
            }
            var geo = new PathGeometry(); geo.Figures.Add(fig);
            canvas.Children.Add(new Path { Stroke = Fg, StrokeThickness = 1.5, Data = geo });
        }

        Path BuildFlag(double stemX, double y0, bool up)
        {
            double w = StaffGap * 1.25, h = StaffGap * 2.3, s = up ? 1 : -1;
            var fig = new PathFigure { StartPoint = new Point(stemX, y0), IsClosed = true, IsFilled = true };
            fig.Segments.Add(new BezierSegment(
                new Point(stemX + w * 1.05, y0 + s * h * 0.28),
                new Point(stemX + w * 0.95, y0 + s * h * 0.70),
                new Point(stemX + w * 0.55, y0 + s * h * 1.00), true));
            fig.Segments.Add(new BezierSegment(
                new Point(stemX + w * 0.78, y0 + s * h * 0.60),
                new Point(stemX + w * 0.40, y0 + s * h * 0.48),
                new Point(stemX, y0 + s * h * 0.44), true));
            var geo = new PathGeometry(); geo.Figures.Add(fig);
            return new Path { Fill = Fg, Data = geo };
        }

        void DrawBeamGroup(Staff st, List<Slot> group)
        {
            int n = group.Count;
            var xs = new double[n]; var minS = new int[n]; var maxS = new int[n];
            double headW = StaffGap * 1.35;
            double sumStep = 0; int cnt = 0;
            for (int i = 0; i < n; i++)
            {
                xs[i] = SlotX(group[i]);
                DrawHeads(st, xs[i], group[i].Spelled, group[i].G, out minS[i], out maxS[i], group[i].Written, group[i].Start);
                sumStep += minS[i] + maxS[i]; cnt += 2;
            }
            int middle = st.BottomLineStep + 4;
            bool? forced = null; foreach (var g in group) if (g.ForceUp.HasValue) { forced = g.ForceUp; break; } // 2-voice: a held voice forces the figure's stems
            bool up = forced ?? (sumStep / cnt < middle);
            int maxFlags = 0; for (int i = 0; i < n; i++) maxFlags = Math.Max(maxFlags, group[i].G.Flags);

            double extreme = up ? double.MaxValue : double.MinValue;
            for (int i = 0; i < n; i++)
            {
                double hy = up ? YFor(st, maxS[i]) : YFor(st, minS[i]);
                extreme = up ? Math.Min(extreme, hy) : Math.Max(extreme, hy);
            }
            // Longer stems when there are 2nd/3rd beams (16ths/32nds) so the extra beams have room.
            double beamDist = StaffGap * (maxFlags >= 3 ? 4.0 : maxFlags >= 2 ? 3.6 : 3.1);
            double beamY = up ? extreme - beamDist : extreme + beamDist;
            var stemX = new double[n];
            for (int i = 0; i < n; i++)
            {
                stemX[i] = up ? xs[i] + headW / 2 - 0.7 : xs[i] - headW / 2 + 0.7;
                double baseY = up ? YFor(st, minS[i]) : YFor(st, maxS[i]);
                canvas.Children.Add(new Line { X1 = stemX[i], Y1 = baseY, X2 = stemX[i], Y2 = beamY, Stroke = Fg, StrokeThickness = 1.4 });
            }
            canvas.Children.Add(new Line { X1 = stemX[0], Y1 = beamY, X2 = stemX[n - 1], Y2 = beamY, Stroke = Fg, StrokeThickness = StaffGap * 0.55 });

            double beamGap = StaffGap * 0.92; // more separation so the 16th/32nd beams don't look merged
            for (int level = 2; level <= 3; level++)
            {
                double off = (up ? 1 : -1) * (level - 1) * beamGap;
                for (int i = 0; i < n; i++)
                {
                    if (group[i].G.Flags < level) continue;
                    bool rightPair = i < n - 1 && group[i + 1].G.Flags >= level;
                    bool leftPair = i > 0 && group[i - 1].G.Flags >= level;
                    if (rightPair)
                        canvas.Children.Add(new Line { X1 = stemX[i], Y1 = beamY + off, X2 = stemX[i + 1], Y2 = beamY + off, Stroke = Fg, StrokeThickness = StaffGap * 0.5 });
                    else if (!leftPair)
                    {
                        double dir = i > 0 ? -1 : 1;
                        canvas.Children.Add(new Line { X1 = stemX[i], Y1 = beamY + off, X2 = stemX[i] + dir * StaffGap * 0.9, Y2 = beamY + off, Stroke = Fg, StrokeThickness = StaffGap * 0.5 });
                    }
                }
            }
        }

        // Triolet decorations: a "3" (and a bracket when the notes aren't beamed) over each run of triplet notes,
        // chunked in 3s. Only fires in a SIMPLE meter where ternary-feel notes keep their 1/3-beat durations; in a
        // compound meter the ×1.5 scale turns them into plain eighths so nothing here matches.
        void DrawTuplets(Staff st, List<Slot> slots)
        {
            double headW = StaffGap * 1.35;
            // A triolet occupies ONE beat in a simple meter, whatever its note count (3 croches, noire+croche,
            // croche+noire, with a rest…). Group ALL slots (notes + rests) that start in the same beat and bracket
            // the beat whenever it contains a triplet note — so the "3" spans the right notes, not a fixed 3.
            int i = 0;
            while (i < slots.Count)
            {
                int beat = (int)Math.Floor(slots[i].Start + 1e-9);
                int j = i; var grp = new List<Slot>(); bool hasTrip = false;
                while (j < slots.Count && (int)Math.Floor(slots[j].Start + 1e-9) == beat)
                {
                    grp.Add(slots[j]);
                    if (slots[j].Kind == SlotKind.Note && slots[j].G.Triplet) hasTrip = true;
                    j++;
                }
                if (hasTrip) DrawTripletBracket(st, grp, headW);
                i = j;
            }
        }

        void DrawTripletBracket(Staff st, List<Slot> chunk, double headW)
        {
            if (chunk.Count == 0) return;
            double xL = SlotX(chunk[0]), xR = SlotX(chunk[chunk.Count - 1]) + headW;
            double topY = double.MaxValue;
            foreach (var s in chunk) if (s.Spelled != null) foreach (var (step, _) in s.Spelled) topY = Math.Min(topY, YFor(st, step));
            if (topY == double.MaxValue) topY = st.TopLineY;
            // Keep the "3" above the staff top even when the notes are low (e.g. a soupir + croche triolet).
            double y = Math.Min(topY - StaffGap * 3.4, YFor(st, st.BottomLineStep + 8) - StaffGap * 2.0), cx = (xL + xR) / 2;

            var t3 = new TextBlock { Text = "3", Foreground = Fg, FontStyle = FontStyles.Italic, FontWeight = FontWeights.Bold, FontFamily = new FontFamily("Times New Roman"), FontSize = StaffGap * 2.0 };
            t3.Measure(new Size(1000, 1000));
            Canvas.SetLeft(t3, cx - t3.DesiredSize.Width / 2);
            Canvas.SetTop(t3, y - t3.DesiredSize.Height / 2);
            canvas.Children.Add(t3);

            // A beam already groups beamed triplets visually → number only; otherwise draw a bracket with end ticks.
            bool beamed = chunk.Count >= 2 && chunk[0].G.Flags >= 1 && SameBeamGroup(chunk[0].Start, chunk[chunk.Count - 1].Start);
            if (!beamed)
            {
                double gap = Math.Max(8, t3.DesiredSize.Width + 2);
                canvas.Children.Add(new Line { X1 = xL, Y1 = y, X2 = cx - gap / 2, Y2 = y, Stroke = Fg, StrokeThickness = 1 });
                canvas.Children.Add(new Line { X1 = cx + gap / 2, Y1 = y, X2 = xR, Y2 = y, Stroke = Fg, StrokeThickness = 1 });
                canvas.Children.Add(new Line { X1 = xL, Y1 = y, X2 = xL, Y2 = y + StaffGap * 0.8, Stroke = Fg, StrokeThickness = 1 });
                canvas.Children.Add(new Line { X1 = xR, Y1 = y, X2 = xR, Y2 = y + StaffGap * 0.8, Stroke = Fg, StrokeThickness = 1 });
            }
        }

        void DrawHeads(Staff st, double x, List<(int step, string acc)> spelled, Glyph g, out int minStep, out int maxStep, List<int> written = null, double startBeat = -1)
        {
            double headW = StaffGap * 1.35, headH = StaffGap * 1.06;
            minStep = int.MaxValue; maxStep = int.MinValue;
            for (int i = 0; spelled != null && i < spelled.Count; i++)
            {
                var (step, accGlyph) = spelled[i];
                minStep = Math.Min(minStep, step); maxStep = Math.Max(maxStep, step);
                double y = YFor(st, step);
                DrawLedgers(st, step, x, headW);

                if (accGlyph != null)
                {
                    string sm = accGlyph == "♯" ? GlyphSharp : accGlyph == "♭" ? GlyphFlat : GlyphNatural;
                    var acc = MakeGlyph(sm);
                    PlaceGlyph(acc, x - headW / 2 - AccGap - acc.DesiredSize.Width, y);
                }

                var head = new Ellipse { Width = headW, Height = headH, Fill = g.Filled ? Fg : Brushes.White, Stroke = Fg, StrokeThickness = g.Filled ? 0 : 1.5 };
                Canvas.SetLeft(head, x - headW / 2); Canvas.SetTop(head, y - headH / 2);
                canvas.Children.Add(head);

                if (g.Dotted)
                {
                    bool onLine = (((step - st.BottomLineStep) % 2) + 2) % 2 == 0;
                    double dotY = onLine ? y + Step : y;
                    var dot = new Ellipse { Width = 3.4, Height = 3.4, Fill = Fg };
                    Canvas.SetLeft(dot, x + headW / 2 + 2); Canvas.SetTop(dot, dotY - 1.7);
                    canvas.Children.Add(dot);
                }

                if (EditMode && written != null && startBeat >= 0 && i < written.Count)
                    editHits.Add((new System.Windows.Rect(x - headW / 2 - 2, y - headH / 2 - 2, headW + 4, headH + 4),
                                  startBeat / (cursorScale > 0 ? cursorScale : 1.0), written[i] - curTranspose));
            }
            if (minStep == int.MaxValue) { minStep = st.BottomLineStep + 4; maxStep = minStep; }
        }

        void DrawLedgers(Staff st, int step, double x, double headW)
        {
            double w = headW * 0.9;
            int topStep = st.BottomLineStep + 8;
            if (step > topStep) for (int s = topStep + 2; s <= step; s += 2) AddLedger(YFor(st, s), x, w);
            else if (step < st.BottomLineStep) for (int s = st.BottomLineStep - 2; s >= step; s -= 2) AddLedger(YFor(st, s), x, w);
        }

        void AddLedger(double y, double x, double w)
            => canvas.Children.Add(new Line { X1 = x - w, Y1 = y, X2 = x + w, Y2 = y, Stroke = LineBrush, StrokeThickness = 1 });

        void DrawRest(Staff st, double x, double value, bool dotted, bool triplet = false)
        {
            if (triplet) value *= 1.5; // a triplet-eighth rest (1/3) draws as an eighth rest, a triplet-quarter (2/3) as a quarter
            string glyph; int refStep;
            if (value >= 3.5) { glyph = GlyphRestWhole; refStep = st.BottomLineStep + 6; }
            else if (value >= 1.75) { glyph = GlyphRestHalf; refStep = st.BottomLineStep + 4; }
            else if (value >= 0.875) { glyph = GlyphRestQuarter; refStep = st.BottomLineStep + 4; }
            else if (value >= 0.4) { glyph = GlyphRest8; refStep = st.BottomLineStep + 4; }
            else { glyph = GlyphRest16; refStep = st.BottomLineStep + 4; }

            var tb = MakeGlyph(glyph, RestBrush);
            PlaceGlyph(tb, x - tb.DesiredSize.Width / 2, YFor(st, refStep));

            if (dotted)
            {
                var dot = new Ellipse { Width = 3.4, Height = 3.4, Fill = RestBrush };
                Canvas.SetLeft(dot, x + tb.DesiredSize.Width / 2 + 1);
                Canvas.SetTop(dot, YFor(st, st.BottomLineStep + 4) + Step - 1.7);
                canvas.Children.Add(dot);
            }
        }

        void DrawCursor()
        {
            cursor = new Rectangle { Width = 2, Height = systemBottom - systemTop + StaffGap * 2, Fill = CursorBrush, IsHitTestVisible = false };
            Canvas.SetTop(cursor, systemTop - StaffGap);
            Canvas.SetLeft(cursor, ContentLeft);
            Panel.SetZIndex(cursor, 50);
            canvas.Children.Add(cursor);
        }

        /// <summary>Move the playback cursor to a beat position and keep it near the centre of the viewport by scrolling the
        /// score continuously. The view holds still until the cursor reaches the middle, then follows it so it stays centred,
        /// and lets it run out to the right edge once the end of the piece can no longer scroll further.</summary>
        public void SetCursorBeat(double beat)
        {
            if (cursor == null) return;
            double x = BeatToX(Math.Max(0, beat) * cursorScale); // cursor beat is raw; notes are in scaled space
            Canvas.SetLeft(cursor, x);

            double vw = scroll.ViewportWidth;
            if (vw <= 1) return;
            double maxOff = scroll.ScrollableWidth;
            if (maxOff <= 0.5) return; // whole piece fits — nothing to scroll

            double target = Math.Max(0, Math.Min(maxOff, x - vw * 0.5)); // desired offset that centres the cursor
            if (Math.Abs(scroll.HorizontalOffset - target) > 0.5) scroll.ScrollToHorizontalOffset(target);
        }
    }
}
