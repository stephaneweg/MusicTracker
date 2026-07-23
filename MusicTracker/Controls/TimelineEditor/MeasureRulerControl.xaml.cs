using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace MusicTracker.Controls.TimelineEditor
{
    /// <summary>
    /// The measure ruler shown above the timeline lanes: a thicker tick + measure number at every bar,
    /// a lighter half-tick at every beat. Sized to the full lane width and scrolled horizontally in sync
    /// with the lanes (kept fixed vertically by the host layout).
    /// </summary>
    public partial class MeasureRulerControl : UserControl
    {
        static readonly Brush MeasureTick = new SolidColorBrush(Color.FromRgb(0x72, 0x72, 0x86));
        static readonly Brush BeatTick = new SolidColorBrush(Color.FromRgb(0x40, 0x40, 0x4C));
        static readonly Brush NumberFg = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC));

        public MeasureRulerControl() { InitializeComponent(); }

        // pickupBeats = anacrusis (levée) length in RULER-beats: the incomplete first bar. Barlines/numbers shift right by
        // it (bar "1" starts after the pickup), matching the score's phased grid. 0 = none.
        public void Configure(double width, double height, double pxPerBeat, int beatsPerBar, double pickupBeats = 0)
        {
            if (beatsPerBar < 1) beatsPerBar = 4;
            canvas.Width = width; canvas.Height = height;
            canvas.Children.Clear();

            double phase = pickupBeats > 1e-6 ? pickupBeats % beatsPerBar : 0; // fold a >1-bar levée into one bar

            void Tick(double atBeat, bool measure)
            {
                double x = atBeat * pxPerBeat;
                var tick = new Rectangle
                {
                    Width = measure ? 1.5 : 1,
                    Height = measure ? height : height / 2,
                    Fill = measure ? MeasureTick : BeatTick,
                };
                Canvas.SetLeft(tick, x); Canvas.SetTop(tick, measure ? 0 : height / 2);
                canvas.Children.Add(tick);
            }

            // The pickup bar (opening barline at 0, no number) + its interior beat ticks before the first full barline.
            if (phase > 1e-6)
            {
                Tick(0, true);
                for (int j = 1; j < phase - 1e-9; j++) Tick(j, false);
            }

            // Full bars: measure tick + number (1,2,3,…) at phase + m·bpb, beat ticks in between.
            for (int m = 0; (phase + m * beatsPerBar) * pxPerBeat < width; m++)
            {
                double barStart = phase + m * beatsPerBar;
                Tick(barStart, true);
                var num = new TextBlock { Text = (m + 1).ToString(), Foreground = NumberFg, FontSize = 10 };
                Canvas.SetLeft(num, barStart * pxPerBeat + 3); Canvas.SetTop(num, 1);
                canvas.Children.Add(num);
                for (int j = 1; j < beatsPerBar && (barStart + j) * pxPerBeat < width; j++) Tick(barStart + j, false);
            }
        }
    }
}
