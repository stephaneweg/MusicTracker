using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace MusicTracker.Controls
{
    /// <summary>
    /// A small read-only piano that LIGHTS the notes of a chord (MIDI numbers, as voiced: inversion, octave, open
    /// voicing). Four octaves from C2; the window slides by octaves when the voicing sits outside it.
    /// </summary>
    public sealed class ChordKeyboard : FrameworkElement
    {
        const int Octaves = 4, DefaultLowC = 36;   // C2 (C4 = 60)
        static readonly int[] WhitePc = { 0, 2, 4, 5, 7, 9, 11 };
        static readonly int[] BlackPc = { 1, 3, -1, 6, 8, 10, -1 };   // the black key to the RIGHT of each white key
        static readonly Brush White = Frozen(0xF2, 0xF2, 0xF2), Black = Frozen(0x1A, 0x1A, 0x1E), Label = Frozen(0x70, 0x70, 0x78);
        static readonly Pen KeyLine = FrozenPen(Frozen(0x9A, 0x9A, 0xA2), 1);
        static readonly Typeface Face = new Typeface("Segoe UI");

        static Brush Frozen(byte r, byte g, byte b) { var br = new SolidColorBrush(Color.FromRgb(r, g, b)); br.Freeze(); return br; }
        static Pen FrozenPen(Brush b, double t) { var p = new Pen(b, t); p.Freeze(); return p; }

        public static readonly DependencyProperty NotesProperty = DependencyProperty.Register(nameof(Notes), typeof(int[]), typeof(ChordKeyboard),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
        public static readonly DependencyProperty LitBrushProperty = DependencyProperty.Register(nameof(LitBrush), typeof(Brush), typeof(ChordKeyboard),
            new FrameworkPropertyMetadata(Frozen(0x1F, 0xB6, 0xC3), FrameworkPropertyMetadataOptions.AffectsRender));

        /// <summary>The lit notes, as MIDI numbers.</summary>
        public int[] Notes { get => (int[])GetValue(NotesProperty); set => SetValue(NotesProperty, value); }
        public Brush LitBrush { get => (Brush)GetValue(LitBrushProperty); set => SetValue(LitBrushProperty, value); }

        // First C of the window: C2, moved by octaves so the whole voicing shows when it can.
        int LowC()
        {
            var n = Notes;
            if (n == null || n.Length == 0) return DefaultLowC;
            int min = int.MaxValue, max = int.MinValue;
            foreach (int v in n) { if (v < min) min = v; if (v > max) max = v; }
            int low = DefaultLowC;
            while (max >= low + Octaves * 12) low += 12;
            while (min < low && low >= 12) low -= 12;
            return low;
        }

        bool Lit(int midi) { var n = Notes; return n != null && Array.IndexOf(n, midi) >= 0; }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            if (w <= 0 || h <= 0) return;
            int lowC = LowC(), whites = Octaves * 7;
            double kw = w / whites, bw = kw * 0.62, bh = h * 0.62;
            var lit = LitBrush ?? Brushes.Teal;
            double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

            for (int i = 0; i < whites; i++)
            {
                int midi = lowC + (i / 7) * 12 + WhitePc[i % 7];
                dc.DrawRectangle(Lit(midi) ? lit : White, KeyLine, new Rect(i * kw, 0, kw, h));
                if (i % 7 == 0)   // name the Cs, as a register landmark
                {
                    var ft = new FormattedText("C" + (midi / 12 - 1), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, 9, Label, dpi);
                    dc.DrawText(ft, new Point(i * kw + 2, h - ft.Height - 1));
                }
            }
            for (int i = 0; i < whites; i++)
            {
                int pc = BlackPc[i % 7];
                if (pc < 0) continue;
                int midi = lowC + (i / 7) * 12 + pc;
                dc.DrawRectangle(Lit(midi) ? lit : Black, KeyLine, new Rect((i + 1) * kw - bw / 2, 0, bw, bh));
            }
        }
    }
}
