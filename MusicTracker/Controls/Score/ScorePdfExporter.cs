using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Markup;
using System.Windows.Media;
using MusicTracker.Engine.Score;

namespace MusicTracker.Controls.Score
{
    /// <summary>
    /// Lays a score out as a printable A4 <see cref="FixedDocument"/>: a title at the top, then one system per
    /// line of N measures (N = the largest of 2/4/8/16 that fits the page width minus 2 cm margins), each system
    /// stretched to fill the width. Reuses <see cref="ScoreView"/> for the actual engraving. The caller prints it
    /// (e.g. via a PrintDialog → "Microsoft Print to PDF").
    /// </summary>
    public static class ScorePdfExporter
    {
        const double U = 96.0 / 25.4;          // device-independent units per millimetre
        const double Margin = 20 * U;          // 2 cm margins
        const double PageW = 210 * U, PageH = 297 * U; // A4 portrait
        const double SystemGap = 10;           // vertical gap between systems
        const double Scale = 0.5;              // render at half size (smaller, denser staves)

        public static FixedDocument Build(IList<TrackScore> tracks, int num, int den, double scale, string title)
        {
            double contentW = PageW - 2 * Margin;
            double renderW = contentW / Scale;            // render this wide, then scale down to fill contentW
            double bpb = Math.Max(0.5, num * 4.0 / den);  // display beats per bar
            double total = 0; foreach (var t in tracks) total = Math.Max(total, t.TotalBeats);
            int measures = Math.Max(1, (int)Math.Ceiling(total / bpb - 1e-6));

            // Estimate the natural width per measure (clef/key drawn once on the left), then choose N: the largest
            // of 2/4/8/16 whose estimated system width still fits the (pre-scale) render width.
            var probe = new ScoreView();
            probe.RenderStandalone(tracks, num, den, scale, 0);
            double cl = probe.ContentLeftPx, natural = probe.LayoutWidth;
            double avg = measures > 0 ? Math.Max(1, (natural - cl) / measures) : natural;
            int perLine = 2;
            foreach (int c in new[] { 2, 4, 8, 16, 24, 32 }) if (cl + c * avg <= renderW) perLine = c;

            var doc = new FixedDocument();
            doc.DocumentPaginator.PageSize = new Size(PageW, PageH);

            FixedPage page = null; double y = 0;
            void NewPage()
            {
                page = new FixedPage { Width = PageW, Height = PageH, Background = Brushes.White };
                var pc = new PageContent(); ((IAddChild)pc).AddChild(page); doc.Pages.Add(pc);
                y = Margin;
            }
            NewPage();

            if (!string.IsNullOrWhiteSpace(title))
            {
                var tb = new TextBlock { Text = title, FontFamily = new FontFamily("Times New Roman"), FontSize = 22, FontWeight = FontWeights.Bold, Foreground = Brushes.Black, TextAlignment = TextAlignment.Center, Width = contentW };
                FixedPage.SetLeft(tb, Margin); FixedPage.SetTop(tb, Margin); page.Children.Add(tb);
                y = Margin + 46;
            }

            // Line ranges: normally perLine measures, but for a single track a run of ≥2 silent measures counts as
            // ONE block (it collapses to a single multi-measure rest), so a long tacet doesn't eat the whole line.
            var lines = LineRanges(tracks, measures, bpb, perLine);
            foreach (var (sm, em) in lines)
            {
                var sv = new ScoreView();
                var canvas = sv.RenderStandalone(Slice(tracks, sm, em, bpb), num, den, scale, renderW);
                canvas.RenderTransform = new ScaleTransform(Scale, Scale); // shrink to half, filling contentW
                double h = (canvas.Height > 0 ? canvas.Height : 240) * Scale;
                if (y + h > PageH - Margin && y > Margin + 1) NewPage(); // overflow → next page
                FixedPage.SetLeft(canvas, Margin); FixedPage.SetTop(canvas, y); page.Children.Add(canvas);
                y += h + SystemGap;
            }
            return doc;
        }

        // Lines of measure ranges. Multi-track: perLine measures each. Single track: a run of ≥2 silent measures is
        // one block (collapses to a multi-measure rest), so each line packs perLine BLOCKS, not raw measures.
        static List<(int s, int e)> LineRanges(IList<TrackScore> tracks, int measures, double bpb, int perLine)
        {
            var lines = new List<(int, int)>();
            if (tracks.Count != 1)
            {
                for (int m0 = 0; m0 < measures; m0 += perLine) lines.Add((m0, Math.Min(measures, m0 + perLine)));
                return lines;
            }
            var silent = new bool[measures];
            for (int m = 0; m < measures; m++) silent[m] = true;
            foreach (var n in tracks[0].Notes)
            {
                int ma = (int)Math.Floor(n.StartBeat / bpb + 1e-9);
                int mb = (int)Math.Ceiling((n.StartBeat + n.Beats) / bpb - 1e-9);
                for (int m = Math.Max(0, ma); m < Math.Min(measures, Math.Max(ma + 1, mb)); m++) silent[m] = false;
            }
            var blocks = new List<(int s, int e)>(); // each block = 1 line-unit
            int mm = 0;
            while (mm < measures)
            {
                if (silent[mm]) { int e = mm; while (e < measures && silent[e]) e++; if (e - mm >= 2) { blocks.Add((mm, e)); mm = e; continue; } }
                blocks.Add((mm, mm + 1)); mm++;
            }
            for (int bk = 0; bk < blocks.Count; bk += perLine)
            {
                int last = Math.Min(blocks.Count, bk + perLine) - 1;
                lines.Add((blocks[bk].s, blocks[last].e));
            }
            return lines;
        }

        // The notes that start within measures [m0, m1), re-based so the system starts at beat 0.
        static List<TrackScore> Slice(IList<TrackScore> tracks, int m0, int m1, double bpb)
        {
            double a = m0 * bpb, b = m1 * bpb;
            var outp = new List<TrackScore>();
            foreach (var t in tracks)
            {
                var ns = new TrackScore { Clef = t.Clef, Transpose = t.Transpose, IsDrum = t.IsDrum, Key = t.Key, TotalBeats = (m1 - m0) * bpb };
                foreach (var n in t.Notes)
                    if (n.StartBeat >= a - 1e-6 && n.StartBeat < b - 1e-6)
                        ns.Notes.Add(new ScoreNote { StartBeat = n.StartBeat - a, Beats = n.Beats, Midi = n.Midi });
                outp.Add(ns);
            }
            return outp;
        }
    }
}
