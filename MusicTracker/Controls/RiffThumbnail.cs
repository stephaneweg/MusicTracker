using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MusicTracker.Engine;
using MusicTracker.Engine.Flow;

namespace MusicTracker.Controls
{
    /// <summary>
    /// Renders a tiny piano-roll preview of a riff's melodic line: notes are drawn within the pitch
    /// range actually used and vertically centred on that range (so the preview "zooms" onto where the
    /// notes really are). Results are cached per riff <see cref="Riff.Id"/> and re-rendered only when the
    /// riff content changes (a cheap content signature) — e.g. after the riff editor is closed.
    /// </summary>
    public static class RiffThumbnail
    {
        // Drawn at the timeline's own scale: each slice is PxPerBeat/SlicesPerQuarter px wide (so a note's
        // length matches the lane grid exactly) and each note row is 1px tall. Hosts render it 1:1 (Stretch=None).
        const double PxPerBeat = 60; // must match TimelineScreen.PxPerBeat

        // Note colours per module kind.
        public static readonly Color Melody = Color.FromRgb(0xFF, 0xA5, 0x3D); // Play-riff (orange)
        public static readonly Color Chords = Color.FromRgb(0xE2, 0x55, 0x55); // chord pattern (red)
        public static readonly Color Rhythm = Color.FromRgb(0xF2, 0xCB, 0x45); // drum pattern (yellow)
        public static readonly Color Melodic = Color.FromRgb(0x4D, 0x9B, 0xFF); // a chord's melodic cell (blue overlay)

        // Keyed by (content signature × colour) so generated pattern riffs (which have no stable Id)
        // still hit the cache: same content + same colour → same key.
        static readonly Dictionary<long, ImageSource> cache = new Dictionary<long, ImageSource>();
        // Drum previews use their own cache (keyed on content only): they render a small beat-grid coloured by
        // percussion family, not a single-colour piano-roll, so they can't share the colour-keyed cache above.
        static readonly Dictionary<long, ImageSource> drumCache = new Dictionary<long, ImageSource>();

        /// <summary>Melodic (orange) preview — for Play-riff modules.</summary>
        public static ImageSource Get(Riff riff) => Get(riff, Melody);

        /// <summary>Preview of a riff in a given colour (red for chords, yellow for drums, …). Null when empty.</summary>
        public static ImageSource Get(Riff riff, Color color)
        {
            if (riff?.Slices == null || riff.Slices.Length == 0) return null;
            long key = unchecked(Signature(riff) * 31 + ((color.R << 16) | (color.G << 8) | color.B));
            if (cache.TryGetValue(key, out var img)) return img;
            img = Render(riff, color);
            cache[key] = img;
            return img;
        }

        // ---- drums: a mini beat-grid, one thick row per used percussion lane, coloured by family ----------

        const int DrumRowH = 3;   // thick cells (was 1px per semitone) so hits read clearly
        const int DrumGap = 1;    // 1px seam between lane rows

        /// <summary>Drum preview: a compact beat-grid with one thick, family-coloured row per used lane. Null when empty.</summary>
        public static ImageSource GetDrums(Riff riff)
        {
            if (riff?.Slices == null || riff.Slices.Length == 0) return null;
            long key = Signature(riff);
            if (drumCache.TryGetValue(key, out var img)) return img;
            img = RenderDrums(riff);
            if (img != null) drumCache[key] = img;
            return img;
        }

        static ImageSource RenderDrums(Riff riff)
        {
            var s = riff.Slices;
            int spq = riff.SlicesPerQuarter > 0 ? riff.SlicesPerQuarter : 4;

            // Which lanes are actually hit? (fold each GM key to its lane)
            var used = new SortedSet<int>();
            for (int i = 0; i < s.Length; i++)
                for (int n = 0; n < 128; n++)
                    if (s[i].On(n)) used.Add(DrumPattern.LaneForKey(n + 12));
            if (used.Count == 0) return null;

            // Order the used lanes top→bottom (higher GM key = higher on screen; kick at the bottom) and give each
            // a row index + a frozen family brush.
            var lanes = new List<int>(used);
            lanes.Sort((a, b) => DrumPattern.KeyForLane(b) - DrumPattern.KeyForLane(a));
            var rowOf = new Dictionary<int, int>();
            var brushOf = new Dictionary<int, Brush>();
            for (int r = 0; r < lanes.Count; r++)
            {
                rowOf[lanes[r]] = r;
                var b = new SolidColorBrush(DrumColors.ForLane(lanes[r])); b.Freeze();
                brushOf[lanes[r]] = b;
            }

            double pps = PxPerBeat / spq, rw = Math.Max(1.0, pps);
            int stride = DrumRowH + DrumGap;
            int w = Math.Max(1, (int)Math.Ceiling(s.Length * pps));
            int h = Math.Max(1, lanes.Count * stride - DrumGap);

            var dv = new DrawingVisual();
            RenderOptions.SetEdgeMode(dv, EdgeMode.Aliased);
            using (var dc = dv.RenderOpen())
            {
                for (int i = 0; i < s.Length; i++)
                {
                    double x = i * pps;
                    for (int n = 0; n < 128; n++)
                        if (s[i].On(n))
                        {
                            int lane = DrumPattern.LaneForKey(n + 12);
                            dc.DrawRectangle(brushOf[lane], null, new Rect(x, rowOf[lane] * stride, rw, DrumRowH));
                        }
                }
            }
            var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv); rtb.Freeze();
            return rtb;
        }

        /// <summary>Preview of a chord riff (main colour) with its MELODIC CELL overlaid in blue — both drawn on the same
        /// pitch range/scale so they align. Falls back to the plain riff if there is no melody.</summary>
        public static ImageSource GetCombined(Riff main, Color mainColor, Riff overlay, Color overlayColor)
        {
            bool hasMain = main?.Slices != null && main.Slices.Length > 0;
            bool hasOver = overlay?.Slices != null && overlay.Slices.Length > 0;
            if (!hasMain && !hasOver) return null;
            if (!hasOver) return Get(main, mainColor);
            long key = unchecked((Signature(main) * 31 + Signature(overlay)) * 131
                                 + ((mainColor.R << 16) | (mainColor.G << 8) | mainColor.B) * 7
                                 + ((overlayColor.R << 16) | (overlayColor.G << 8) | overlayColor.B));
            if (cache.TryGetValue(key, out var img)) return img;
            img = RenderCombined(main, mainColor, overlay, overlayColor);
            if (img != null) cache[key] = img;
            return img;
        }

        static ImageSource RenderCombined(Riff a, Color ca, Riff b, Color cb)
        {
            int min = 128, max = -1;
            void Scan(Riff r) { if (r?.Slices == null) return; var s = r.Slices; for (int i = 0; i < s.Length; i++) for (int n = 0; n < 128; n++) if (s[i].On(n)) { if (n < min) min = n; if (n > max) max = n; } }
            Scan(a); Scan(b);
            if (max < 0) return null;
            double wpx = 1;
            void Wid(Riff r) { if (r?.Slices == null) return; int spq = r.SlicesPerQuarter > 0 ? r.SlicesPerQuarter : 4; wpx = Math.Max(wpx, r.Slices.Length * (PxPerBeat / spq)); }
            Wid(a); Wid(b);
            int w = Math.Max(1, (int)Math.Ceiling(wpx)), h = (max - min) + 1;
            var dv = new DrawingVisual();
            RenderOptions.SetEdgeMode(dv, EdgeMode.Aliased);
            using (var dc = dv.RenderOpen())
            {
                DrawRiffInto(dc, a, ca, min, max);
                DrawRiffInto(dc, b, cb, min, max);   // melody drawn LAST → on top
            }
            var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv); rtb.Freeze();
            return rtb;
        }

        static void DrawRiffInto(DrawingContext dc, Riff r, Color color, int min, int max)
        {
            if (r?.Slices == null) return;
            var brush = new SolidColorBrush(color); brush.Freeze();
            var s = r.Slices;
            int spq = r.SlicesPerQuarter > 0 ? r.SlicesPerQuarter : 4;
            double pps = PxPerBeat / spq, rw = Math.Max(1.0, pps);
            for (int i = 0; i < s.Length; i++)
            {
                double x = i * pps;
                for (int n = min; n <= max; n++) if (s[i].On(n)) dc.DrawRectangle(brush, null, new Rect(x, max - n, rw, 1));
            }
        }

        // FNV-1a over the slice bitmasks: changes whenever any note is added/removed.
        static long Signature(Riff riff)
        {
            if (riff?.Slices == null) return 0;
            unchecked
            {
                long h = unchecked((long)1469598103934665603UL);
                var s = riff.Slices;
                for (int i = 0; i < s.Length; i++)
                {
                    h = (h ^ (long)s[i].NotesLow) * 1099511628211L;
                    h = (h ^ (long)s[i].NotesHigh) * 1099511628211L;
                }
                return h ^ s.Length ^ ((long)riff.SlicesPerQuarter << 40); // spq changes the horizontal scale
            }
        }

        static ImageSource Render(Riff riff, Color color)
        {
            var brush = new SolidColorBrush(color); brush.Freeze();
            var s = riff.Slices;
            int spq = riff.SlicesPerQuarter > 0 ? riff.SlicesPerQuarter : 4;

            int min = 128, max = -1;
            for (int i = 0; i < s.Length; i++)
                for (int n = 0; n < 128; n++)
                    if (s[i].On(n)) { if (n < min) min = n; if (n > max) max = n; }
            if (max < 0) return null; // no notes defined

            double pxPerSlice = PxPerBeat / spq;                       // a slice's width = the lane's slice width
            int w = Math.Max(1, (int)Math.Ceiling(s.Length * pxPerSlice));
            int h = (max - min) + 1;                                   // 1px per semitone, highest note on top
            double rw = Math.Max(1.0, pxPerSlice);

            var dv = new DrawingVisual();
            RenderOptions.SetEdgeMode(dv, EdgeMode.Aliased); // crisp rectangle edges, no anti-aliasing
            using (var dc = dv.RenderOpen())
            {
                for (int i = 0; i < s.Length; i++)
                {
                    double x = i * pxPerSlice;
                    for (int n = min; n <= max; n++)
                        if (s[i].On(n)) dc.DrawRectangle(brush, null, new Rect(x, max - n, rw, 1));
                }
            }

            var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);
            rtb.Freeze();
            return rtb;
        }
    }
}
