using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using MusicTracker.Engine.Timeline;
using MusicTracker.Engine.Score;

namespace MusicTracker.Controls.TimelineEditor
{
    /// <summary>
    /// The editable chord "trame" lane (above the tracks): one DEGREE label per chord of the arrangement.
    /// Double-click a label → a dropdown of the mode's diatonic degrees; picking one raises <see cref="ChordChanged"/>
    /// (the engine then chooses the flavour and rebuilds the parts). Read-only display when there is no arrangement.
    /// </summary>
    public partial class ChordLaneControl : UserControl
    {
        static readonly Brush ChordBrush = new SolidColorBrush(Color.FromRgb(0x99, 0xCC, 0xFF));
        static readonly Brush TickBrush = new SolidColorBrush(Color.FromRgb(0x2C, 0x2C, 0x38));
        static readonly Brush MarkBrush = new SolidColorBrush(Color.FromRgb(0x44, 0x66, 0x88));
        static readonly string[] RomanU = { "I", "II", "III", "IV", "V", "VI", "VII" };
        static readonly string[] RomanL = { "i", "ii", "iii", "iv", "v", "vi", "vii" };

        ComposedArrangement arr;
        int keyTonicPc, keyFullMode;     // the CURRENT project key (toolbar) — degrees are read/written relative to THIS
        double laneW, laneH, pxPerBeat;
        double pickup;                   // anacrusis (levée) in beats: shifts the whole grid + its barlines right
        int editingIndex = -1;

        /// <summary>Raised when the user edits chord #index: (index, degree 0..6, colour 0..6 = triade/7e/9e/11e/13e/sus2/sus4).</summary>
        public event Action<int, int, int> ChordEdited;
        static readonly string[] ColorNames = { "triade", "7", "9", "11", "13", "sus2", "sus4" };

        public ChordLaneControl() { InitializeComponent(); }

        public void Configure(double width, double height, double pxPerBeat, ComposedArrangement arrangement, int keyTonicPc, int keyFullMode, double pickupBeats = 0)
        {
            this.laneW = width; this.laneH = height; this.pxPerBeat = pxPerBeat; this.arr = arrangement;
            this.keyTonicPc = ((keyTonicPc % 12) + 12) % 12; this.keyFullMode = keyFullMode;
            this.pickup = pickupBeats > 1e-6 ? pickupBeats : 0;
            canvas.Width = width; canvas.Height = height;
            editingIndex = -1;
            Redraw();
        }

        double BeatOfChord(int i) => pickup + ((arr != null && arr.SlicesPerQuarter > 0) ? (double)i * arr.ChordSlices / arr.SlicesPerQuarter : i);

        // The scale-degree (0..6) of a chromatic pitch-class in the CURRENT project key, or -1 if chromatic.
        int DegreeOf(int rootPc)
        {
            int rel = (((rootPc - keyTonicPc) % 12) + 12) % 12;
            int[] sc = MusicalMode.Scale(keyFullMode);
            for (int d = 0; d < 7; d++) if (sc[d] == rel) return d;
            return -1;
        }

        static bool IsMinorish(int q) => q == 1 || q == 7 || q == 14 || q == 17 || q == 2 || q == 9 || q == 10;
        static bool IsDim(int q) => q == 2 || q == 9 || q == 10;

        string Label(int degree, int quality)
        {
            if (degree < 0) return "?";
            string r = IsMinorish(quality) ? RomanL[degree] : RomanU[degree];
            return IsDim(quality) ? r + "°" : r;
        }

        // The chord COLOUR suffix shown after the degree (triad → none, 7/9/11/13/sus2/sus4/6 from the quality index).
        static string ColorName(int q)
        {
            if (q == 4) return "sus2"; if (q == 5) return "sus4";
            if (q == 11 || q == 12) return "6";
            if ((q >= 6 && q <= 10) || q == 22) return "7";
            if (q >= 13 && q <= 19) return "9";
            if (q == 20) return "11"; if (q == 21) return "13";
            return "";
        }
        // quality index → colour combo index (0 triade · 1 7e · 2 9e · 3 11e · 4 13e · 5 sus2 · 6 sus4).
        static int ColorIndexOf(int q)
        {
            if (q == 4) return 5; if (q == 5) return 6;
            if ((q >= 6 && q <= 12) || q == 22) return 1;
            if (q >= 13 && q <= 19) return 2;
            if (q == 20) return 3; if (q == 21) return 4;
            return 0;
        }

        // The roman label of a degree as the CURRENT key would voice it (engine's diatonic flavour).
        string DegreeLabel(int d)
        {
            var ch = ArrangementEngine.DiatonicChord(keyTonicPc, keyFullMode, d);
            return Label(d, ch.quality);
        }

        void Redraw()
        {
            canvas.Children.Clear();
            if (arr == null || arr.Chords == null) return;
            double barBeats = (arr.SlicesPerQuarter > 0) ? (double)arr.BarSlices / arr.SlicesPerQuarter : 4;
            double phase = barBeats > 0 ? pickup % barBeats : 0; // partial pickup bar, then full bars
            if (phase > 1e-6) { var t0 = new Rectangle { Width = 1, Height = laneH, Fill = TickBrush }; Canvas.SetLeft(t0, 0); canvas.Children.Add(t0); }
            for (double b = phase; b * pxPerBeat < laneW && barBeats > 0; b += barBeats)
            {
                var tick = new Rectangle { Width = 1, Height = laneH, Fill = TickBrush };
                Canvas.SetLeft(tick, b * pxPerBeat); canvas.Children.Add(tick);
            }
            for (int i = 0; i < arr.Chords.Count; i++) DrawChord(i);
        }

        void DrawChord(int i)
        {
            var c = arr.Chords[i];
            double x = BeatOfChord(i) * pxPerBeat;
            var mark = new Rectangle { Width = 1, Height = laneH, Fill = MarkBrush };
            Canvas.SetLeft(mark, x); canvas.Children.Add(mark);

            if (i == editingIndex)
            {
                int deg0 = Math.Max(0, DegreeOf(c.Root)), col0 = ColorIndexOf(c.Quality);
                var degCombo = new ComboBox { Width = 46, FontSize = 11, ToolTip = "Degré" };
                for (int d = 0; d < 7; d++) degCombo.Items.Add(DegreeLabel(d));
                degCombo.SelectedIndex = deg0;
                var colCombo = new ComboBox { Width = 58, FontSize = 11, ToolTip = "Couleur (goût de l'accord)" };
                foreach (var cn in ColorNames) colCombo.Items.Add(cn);
                colCombo.SelectedIndex = col0;
                SelectionChangedEventHandler fire = (s, e) =>
                {
                    if (editingIndex != i || degCombo.SelectedIndex < 0 || colCombo.SelectedIndex < 0) return;
                    int nd = degCombo.SelectedIndex, nc = colCombo.SelectedIndex;
                    if (nd == deg0 && nc == col0) return;          // ignore the programmatic initial set
                    editingIndex = -1; ChordEdited?.Invoke(i, nd, nc);
                };
                degCombo.SelectionChanged += fire; colCombo.SelectionChanged += fire;
                Canvas.SetLeft(degCombo, x + 2); Canvas.SetTop(degCombo, 1); canvas.Children.Add(degCombo);
                Canvas.SetLeft(colCombo, x + 50); Canvas.SetTop(colCombo, 1); canvas.Children.Add(colCombo);
                Dispatcher.BeginInvoke((Action)(() => degCombo.IsDropDownOpen = true), System.Windows.Threading.DispatcherPriority.Loaded);
                return;
            }

            var txt = new TextBlock { Text = Label(DegreeOf(c.Root), c.Quality) + ColorName(c.Quality), Foreground = ChordBrush, FontSize = 12, FontWeight = FontWeights.Bold, Cursor = Cursors.Hand, ToolTip = "Double-clic pour changer le degré et la couleur" };
            txt.MouseLeftButtonDown += (s, e) => { if (e.ClickCount == 2) { e.Handled = true; editingIndex = i; Redraw(); } };
            Canvas.SetLeft(txt, x + 3); Canvas.SetTop(txt, 3); canvas.Children.Add(txt);
        }
    }
}
