using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace KotonPluginArabesque
{
    /// <summary>
    /// Le moteur d'Arabesque (C:\Projects\arabesque, ARMPI src/apps/arabesque) réduit à ce dont le générateur a besoin :
    /// le générateur xorshift, un système de fonctions itérées (IFS, format Fractint / Art Song) et son cadre automatique,
    /// les tracés livrés, l'image (mandala calculé ou fichier). Mêmes nombres que l'application : un même tracé, une même
    /// graine et une même image donnent le même chemin.
    /// </summary>
    public struct Rng
    {
        public uint S;
        public Rng(uint seed) { S = seed != 0 ? seed : 1; }
        public uint Next() { S ^= S << 13; S ^= S >> 17; S ^= S << 5; return S; }
        public double Unit() => Next() / 4294967296.0;
        public int Below(int n) => (int)(Next() % (uint)n);
    }

    public struct Xform { public double A, B, C, D, E, F, P; }

    public sealed class Ifs
    {
        public const int Max = 20;
        public string Name = "";
        public Xform[] T = new Xform[Max];
        public int N;
        double[] _cum = new double[Max];
        public double X0, Y0, X1 = 1, Y1 = 1;

        public void Prepare()
        {
            double s = 0;
            for (int i = 0; i < N; i++) s += T[i].P > 0 ? T[i].P : 0;
            double c = 0;
            for (int i = 0; i < N; i++) { c += s > 0 ? (T[i].P > 0 ? T[i].P : 0) / s : 1.0 / N; _cum[i] = c; }
            if (N > 0) _cum[N - 1] = 1.01;
        }

        public void Step(ref double x, ref double y, ref Rng r)
        {
            double u = r.Unit();
            int i = 0;
            while (i < N - 1 && u >= _cum[i]) i++;
            ref Xform m = ref T[i];
            double nx = m.A * x + m.B * y + m.E, ny = m.C * x + m.D * y + m.F;
            if (nx > 1e9 || nx < -1e9 || ny > 1e9 || ny < -1e9) { nx = 0; ny = 0; }
            x = nx; y = ny;
        }

        /// <summary>Le cadre d'après le chemin : tout le tracé tient dans l'image.</summary>
        public void Limits()
        {
            if (N < 1) { X0 = Y0 = 0; X1 = Y1 = 1; return; }
            Prepare();
            var r = new Rng(0x5EED);
            double x = 0, y = 0;
            for (int i = 0; i < 64; i++) Step(ref x, ref y, ref r);
            X0 = X1 = x; Y0 = Y1 = y;
            for (int i = 0; i < 30000; i++)
            {
                Step(ref x, ref y, ref r);
                if (x < X0) X0 = x;
                if (x > X1) X1 = x;
                if (y < Y0) Y0 = y;
                if (y > Y1) Y1 = y;
            }
            double mx = (X1 - X0) * 0.02, my = (Y1 - Y0) * 0.02;
            if (mx < 1e-9) mx = 0.5;
            if (my < 1e-9) my = 0.5;
            X0 -= mx; X1 += mx; Y0 -= my; Y1 += my;
        }

        public void ToUnit(double x, double y, out double u, out double v)
        {
            u = (x - X0) / (X1 - X0); v = 1.0 - (y - Y0) / (Y1 - Y0);
            u = u < 0 ? 0 : u > 1 ? 1 : u; v = v < 0 ? 0 : v > 1 ? 1 : v;
        }

        /// <summary>Le premier code d'un texte .ifs (commentaires « ; » de Fractint ignorés).</summary>
        public static Ifs Parse(string s)
        {
            int o = s.IndexOf('{');
            if (o < 0) return null;
            int b = o;
            while (b > 0 && char.IsWhiteSpace(s[b - 1])) b--;
            int a = b;
            while (a > 0 && s[a - 1] != '\n' && s[a - 1] != '}') a--;
            var f = new Ifs { Name = s.Substring(a, Math.Min(b - a, 63)).Trim() };
            var v = new double[7];
            int k = 0, p = o + 1;
            while (p < s.Length && s[p] != '}')
            {
                if (s[p] == ';') { while (p < s.Length && s[p] != '\n') p++; continue; }
                int e = p;
                while (e < s.Length && "+-.0123456789eE".IndexOf(s[e]) >= 0) e++;
                if (e == p || !double.TryParse(s.AsSpan(p, e - p), NumberStyles.Float, CultureInfo.InvariantCulture, out double d)) { p = e == p ? p + 1 : e; continue; }
                p = e;
                v[k++] = d;
                if (k == 7)
                {
                    k = 0;
                    if (f.N < Max) f.T[f.N++] = new Xform { A = v[0], B = v[1], C = v[2], D = v[3], E = v[4], F = v[5], P = v[6] };
                }
            }
            if (f.N < 2) return null;
            f.Prepare();
            f.Limits();
            return f;
        }

        /// <summary>Les six tracés livrés avec Arabesque (des fractales classiques, du domaine public).</summary>
        public static readonly (string Name, string Code)[] Builtin =
        {
            ("Dragon",   "Dragon { 0.5 -0.5 0.5 0.5 0 0 0.5  0.5 0.5 -0.5 0.5 1 0 0.5 }"),
            ("Flocon",   "Flocon { 0.333 0 0 0.333 0 0 0.25  0.167 -0.289 0.289 0.167 0.333 0 0.25  0.167 0.289 -0.289 0.167 0.5 0.289 0.25  0.333 0 0 0.333 0.667 0 0.25 }"),
            ("Fougère",  "Fougere { 0 0 0 0.16 0 0 0.01  0.85 0.04 -0.04 0.85 0 1.6 0.85  0.2 -0.26 0.23 0.22 0 1.6 0.07  -0.15 0.28 0.26 0.24 0 0.44 0.07 }"),
            ("Tapis",    "Tapis { 0.333 0 0 0.333 0 0 0.125  0.333 0 0 0.333 0.333 0 0.125  0.333 0 0 0.333 0.667 0 0.125  0.333 0 0 0.333 0 0.333 0.125  0.333 0 0 0.333 0.667 0.333 0.125  0.333 0 0 0.333 0 0.667 0.125  0.333 0 0 0.333 0.333 0.667 0.125  0.333 0 0 0.333 0.667 0.667 0.125 }"),
            ("Triangle", "Triangle { 0.5 0 0 0.5 0 0 0.333  0.5 0 0 0.5 0.5 0 0.333  0.5 0 0 0.5 0.25 0.433 0.334 }"),
            ("Volute",   "Volute { 0.82 -0.38 0.38 0.82 0.1 0.05 0.86  0.2 0.1 -0.1 0.2 -1.1 0.4 0.07  -0.18 0.12 0.12 0.18 1.2 -0.6 0.07 }"),
        };
    }

    /// <summary>Une image : des pixels 0x00RRGGBB.</summary>
    public sealed class Picture
    {
        public uint[] Px = new uint[1];
        public int W = 1, H = 1;

        public uint At(double u, double v) => Px[(int)(v * (H - 1)) * W + (int)(u * (W - 1))];

        /// <summary>Le mandala d'Arabesque (image.h / Mandala.cs), même graine, mêmes pixels.</summary>
        public static Picture Mandala(int w, int h, uint seed)
        {
            var r = new Rng(unchecked(seed * 2654435761u + 1));
            var im = new Picture { W = w, H = h, Px = new uint[w * h] };
            int fold = 5 + r.Below(8);
            int rings = 5 + r.Below(6);
            double wob = 0.05 + r.Unit() * 0.12, b = r.Unit();
            const int NCOL = 7;
            var pal = new int[NCOL][];
            double hue = b, stepH = 0.09 + r.Unit() * 0.3;
            for (int i = 0; i < NCOL; i++)
            {
                double sat = 0.55 + r.Unit() * 0.45, val = 0.45 + r.Unit() * 0.55;
                pal[i] = Hsv(hue, sat, val);
                hue += stepH;
            }
            var petal = new double[16];
            for (int i = 0; i < 16; i++) petal[i] = r.Unit();
            const double PI2 = 6.283185307179586;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    double dx = (2.0 * x - w) / w, dy = (2.0 * y - h) / h;
                    double d = Math.Sqrt(dx * dx + dy * dy), a = Math.Atan2(dy, dx);
                    a = ((a + PI2) % (PI2 / fold)) * fold / PI2;
                    a = a > 0.5 ? 1 - a : a;
                    double rr = d * rings + wob * rings * Math.Sin(a * 6.283 * 2 + d * 9);
                    int ring = (int)rr;
                    double fr = rr - ring;
                    int cell = (int)(a * 2 * (2 + ring));
                    double pet = petal[(ring * 3 + cell) & 15];
                    int k = (ring * 2 + cell + (pet > 0.5 ? 1 : 0)) % NCOL;
                    int[] c = pal[k], c2 = pal[(k + 3) % NCOL];
                    double t = fr < 0.12 || fr > 0.88 ? 0.0 : pet;
                    double dark = fr < 0.06 || fr > 0.94 ? 0.25 : 1.0;
                    if (d > 1.0) dark = 0.45;
                    int R = (int)((c[0] * (1 - t) + c2[0] * t) * dark), G = (int)((c[1] * (1 - t) + c2[1] * t) * dark), B = (int)((c[2] * (1 - t) + c2[2] * t) * dark);
                    im.Px[y * w + x] = (uint)R << 16 | (uint)G << 8 | (uint)B;
                }
            return im;
        }

        static int[] Hsv(double h, double s, double v)
        {
            h = (h - Math.Floor(h)) * 6;
            int i = (int)h;
            double f = h - i, p = v * (1 - s), q = v * (1 - s * f), t = v * (1 - s * (1 - f)), r, g, b;
            switch (i)
            {
                case 0: r = v; g = t; b = p; break;
                case 1: r = q; g = v; b = p; break;
                case 2: r = p; g = v; b = t; break;
                case 3: r = p; g = q; b = v; break;
                case 4: r = t; g = p; b = v; break;
                default: r = v; g = p; b = q; break;
            }
            return new[] { (int)(r * 255), (int)(g * 255), (int)(b * 255) };
        }

        /// <summary>Une image de fichier, réduite à 512 px au plus (le chemin n'a pas besoin de plus).</summary>
        public static Picture Load(string path)
        {
            try
            {
                var src = new BitmapImage();
                src.BeginInit();
                src.CacheOption = BitmapCacheOption.OnLoad;
                src.UriSource = new Uri(path);
                src.DecodePixelWidth = 512;
                src.EndInit();
                src.Freeze();
                var bgr = new FormatConvertedBitmap(src, PixelFormats.Bgr32, null, 0);
                int w = bgr.PixelWidth, h = bgr.PixelHeight;
                var px = new uint[w * h];
                bgr.CopyPixels(px, w * 4, 0);
                for (int i = 0; i < px.Length; i++) px[i] &= 0xFFFFFF;
                return new Picture { W = w, H = h, Px = px };
            }
            catch { return null; }
        }
    }

    /// <summary>
    /// Les autres images calculées d'Arabesque (Fractals.cs) : Mandelbrot, Julia, Burning Ship, Lyapunov, Newton et
    /// Rayons. La graine choisit le cadrage, les paramètres et la palette ; même sorte, même graine, même taille : mêmes
    /// pixels que l'application (sans les réglages à la main ni le recadrage à la souris).
    /// </summary>
    public static class Fractals
    {
        public static readonly string[] Names = { "Mandelbrot", "Julia", "Burning Ship", "Lyapunov", "Newton", "Rayons" };

        /// <summary>L'image de cette sorte (0 Mandelbrot … 5 Rayons).</summary>
        public static Picture Make(int kind, int w, int h, uint seed) => kind switch
        {
            0 => Escape(w, h, seed, false, false),
            1 => Escape(w, h, seed, true, false),
            2 => Escape(w, h, seed, false, true),
            3 => Lyapunov(w, h, seed),
            4 => Newton(w, h, seed),
            _ => Rays(w, h, seed),
        };

        // ---- la palette : a + b cos (2 pi (c t + d)), une phase par composante ----
        sealed class Palette
        {
            readonly double[] a = new double[3], b = new double[3], c = new double[3], d = new double[3];
            public Palette(ref Rng r)
            {
                double freq = 0.6 + r.Unit() * 1.4;
                for (int k = 0; k < 3; k++)
                {
                    a[k] = 0.45 + r.Unit() * 0.1;
                    b[k] = 0.35 + r.Unit() * 0.15;
                    c[k] = freq * (0.6 + r.Unit() * 0.8);
                    d[k] = r.Unit();
                }
            }
            public uint At(double t, double light = 1)
            {
                uint o = 0;
                for (int k = 0; k < 3; k++)
                {
                    double v = (a[k] + b[k] * Math.Cos(6.283185307179586 * (c[k] * t + d[k]))) * light;
                    int q = (int)(v * 255);
                    o = o << 8 | (uint)(q < 0 ? 0 : q > 255 ? 255 : q);
                }
                return o;
            }
        }
        static Picture New(int w, int h) => new Picture { W = w, H = h, Px = new uint[w * h] };

        // ---- Mandelbrot, Julia, Burning Ship : le temps de fuite ----
        static int Iter(double zx, double zy, double cx, double cy, bool ship, int max, out double nx, out double ny, out double trap)
        {
            int n = 0;
            trap = 1e9;
            while (n < max)
            {
                if (ship) { zx = Math.Abs(zx); zy = Math.Abs(zy); }
                double x2 = zx * zx, y2 = zy * zy;
                if (x2 + y2 > 65536) break;
                if (n > 0 && x2 + y2 < trap) trap = x2 + y2;
                zy = 2 * zx * zy + cy; zx = x2 - y2 + cx;
                n++;
            }
            nx = zx; ny = zy;
            return n;
        }
        // un point au bord de l'ensemble : c'est là que l'image est riche
        static (double, double) Border(ref Rng r, bool ship, int lo, int max)
        {
            for (int tries = 0; tries < 4000; tries++)
            {
                double cx = ship ? -2.2 + r.Unit() * 3.2 : -2.1 + r.Unit() * 2.7, cy = -1.4 + r.Unit() * 2.8;
                int n = Iter(0, 0, cx, cy, ship, max, out _, out _, out _);
                if (n >= lo && n < max) return (cx, cy);
            }
            return (ship ? -1.76 : -0.75, ship ? -0.03 : 0.1);
        }
        static readonly (double, double, double)[] ShipSpots = {
            (-0.45, -0.5, 1.9), (-1.762, -0.028, 0.06), (-1.62, -0.01, 0.15), (-1.861, -0.004, 0.02), (-1.941, -0.003, 0.012), (-1.25, -0.4, 0.35),
        };
        static Picture Escape(int w, int h, uint seed, bool julia, bool ship)
        {
            var r = new Rng(unchecked(seed * 2246822519u + (julia ? 7u : ship ? 11u : 3u)));
            var pal = new Palette(ref r);
            double cx, cy, ox, oy, half;
            int max;
            if (julia)
            {
                (cx, cy) = Border(ref r, false, 25, 300);
                ox = oy = 0;
                half = 1.6 / Math.Pow(10, r.Unit() * 0.8);
                max = 300;
            }
            else
            {
                double depth = r.Unit() * 3.2;
                if (ship)
                {
                    var (sx, sy, sh) = ShipSpots[r.Below(ShipSpots.Length)];
                    double z = 0.6 + r.Unit() * 0.8;
                    (ox, oy, half) = (sx + (r.Unit() - 0.5) * sh * 0.4, sy + (r.Unit() - 0.5) * sh * 0.4, sh * z);
                    depth = Math.Log10(1.9 / half);
                }
                else
                {
                    (ox, oy) = depth < 0.35 ? (-0.6, 0.0) : Border(ref r, false, 40, 400);
                    half = 1.6 / Math.Pow(10, depth);
                }
                max = 250 + (int)(depth * 150);
                cx = cy = 0;
            }
            double rot = r.Unit() * 6.283185307179586, cs = Math.Cos(rot), sn = Math.Sin(rot);
            if (ship) { cs = 1; sn = 0; }
            double bands = 12 + r.Unit() * 30;
            var im = New(w, h);
            Parallel.For(0, h, y =>
            {
                for (int x = 0; x < w; x++)
                {
                    double u = (2.0 * x - w) / w * half, v = (2.0 * y - h) / w * half;
                    double px = ox + u * cs - v * sn, py = oy + u * sn + v * cs;
                    int n = julia ? Iter(px, py, cx, cy, false, max, out double zx, out double zy, out double trap) : Iter(0, 0, px, py, ship, max, out zx, out zy, out trap);
                    uint c;
                    if (n >= max) c = pal.At(0.35 + Math.Sqrt(Math.Sqrt(trap)) * 0.9, 0.6);
                    else
                    {
                        double nu = n + 1 - Math.Log(Math.Log(zx * zx + zy * zy) * 0.5 / Math.Log(2)) / Math.Log(2);
                        c = pal.At(nu / bands);
                    }
                    im.Px[y * w + x] = c;
                }
            });
            return im;
        }

        // ---- Lyapunov : la suite logistique, son taux r passant de a à b selon un motif ----
        static readonly string[] Seqs = { "AB", "AABAB", "ABBAB", "AAABB", "BBBBBBAAAAAA", "ABAAB", "AABB", "ABBBAA" };
        static Picture Lyapunov(int w, int h, uint seed)
        {
            var r = new Rng(unchecked(seed * 2246822519u + 13));
            var pal = new Palette(ref r);
            string seq = Seqs[r.Below(Seqs.Length)];
            double a0 = 2.0 + r.Unit() * 1.6, b0 = 2.0 + r.Unit() * 1.6;
            double size = 0.25 + r.Unit() * 1.2;
            double a1 = Math.Min(4.0, a0 + size), b1 = Math.Min(4.0, b0 + size);
            var im = New(w, h);
            Parallel.For(0, h, y =>
            {
                for (int x = 0; x < w; x++)
                {
                    double ra = a0 + (a1 - a0) * x / (w - 1), rb = b1 - (b1 - b0) * y / (h - 1);
                    double v = 0.5, sum = 0;
                    for (int i = 0; i < 40; i++) { double k = seq[i % seq.Length] == 'A' ? ra : rb; v = k * v * (1 - v); }
                    const int N = 120;
                    for (int i = 0; i < N; i++)
                    {
                        double k = seq[(i + 40) % seq.Length] == 'A' ? ra : rb;
                        v = k * v * (1 - v);
                        double dd = Math.Abs(k * (1 - 2 * v));
                        sum += Math.Log(dd < 1e-12 ? 1e-12 : dd);
                    }
                    double l = sum / N;
                    double t = l < 0 ? 0.5 * (1 - Math.Exp(l)) : 0.5 + 0.5 * (1 - Math.Exp(-2 * l));
                    im.Px[y * w + x] = pal.At(t * 1.7, l < 0 ? 1 : 0.6);
                }
            });
            return im;
        }

        // ---- Newton : z^n = 1, chaque racine sa teinte, le nombre de pas la lumière ----
        static Picture Newton(int w, int h, uint seed)
        {
            var r = new Rng(unchecked(seed * 2246822519u + 17));
            var pal = new Palette(ref r);
            int deg = 3 + r.Below(4);
            double relax = 1.0 + r.Unit() * 0.7;
            double half = 1.6 / Math.Pow(10, r.Unit() * 1.2);
            double ox = (r.Unit() - 0.5) * 0.8, oy = (r.Unit() - 0.5) * 0.8;
            double rot = r.Unit() * 6.283185307179586, cs = Math.Cos(rot), sn = Math.Sin(rot);
            var rx = new double[deg]; var ry = new double[deg];
            for (int k = 0; k < deg; k++) { rx[k] = Math.Cos(6.283185307179586 * k / deg); ry[k] = Math.Sin(6.283185307179586 * k / deg); }
            var im = New(w, h);
            const int Max = 60;
            Parallel.For(0, h, y =>
            {
                for (int x = 0; x < w; x++)
                {
                    double u = (2.0 * x - w) / w * half, v = (2.0 * y - h) / w * half;
                    double zx = ox + u * cs - v * sn, zy = oy + u * sn + v * cs;
                    int n = 0, root = -1;
                    for (; n < Max && root < 0; n++)
                    {
                        double px = 1, py = 0;
                        for (int k = 1; k < deg; k++) { double t = px * zx - py * zy; py = px * zy + py * zx; px = t; }
                        double fx = px * zx - py * zy - 1, fy = px * zy + py * zx;
                        double dx = deg * px, dy = deg * py, den = dx * dx + dy * dy;
                        if (den < 1e-300) break;
                        zx -= relax * (fx * dx + fy * dy) / den; zy -= relax * (fy * dx - fx * dy) / den;
                        for (int k = 0; k < deg; k++) { double ex = zx - rx[k], ey = zy - ry[k]; if (ex * ex + ey * ey < 1e-6) { root = k; break; } }
                    }
                    uint c = root < 0 ? pal.At(0.95, 0.3) : pal.At((double)root / deg + n * 0.013, 1.15 - Math.Min(0.75, n * 0.025));
                    im.Px[y * w + x] = c;
                }
            });
            return im;
        }

        // ---- Rayons : des bâtonnets de couleur qui partent d'un point, en anneaux, se divisant tous les deux anneaux ----
        static Picture Rays(int w, int h, uint seed)
        {
            var r = new Rng(unchecked(seed * 2246822519u + 19));
            int n = 24 + r.Below(40);
            int rings = 5 + r.Below(4);
            const double sat = 0.75, light = 0.725;
            int levels = (rings + 1) / 2;
            while (levels > 1 && (long)n << (levels - 1) > 1 << 16) levels--;
            var color = new uint[levels][];
            var c = new int[3];
            for (int s = 0; s < levels; s++)
            {
                color[s] = new uint[n << s];
                for (int j = 0; j < color[s].Length; j++)
                {
                    if (s > 0 && (j & 1) == ((j >> 1) & 1)) { color[s][j] = color[s - 1][j >> 1]; continue; }
                    RayHsv(r.Unit(), sat + (r.Unit() - 0.5) * 0.5, light + (r.Unit() - 0.5) * 0.55, c);
                    color[s][j] = (uint)c[0] << 16 | (uint)c[1] << 8 | (uint)c[2];
                }
            }
            double ring = (1.45 + r.Unit() * 0.1) / rings;
            double cx = 0.5 + (r.Unit() - 0.5) * 0.1, cy = 0.5 + (r.Unit() - 0.5) * 0.1, turn = r.Unit();
            var im = New(w, h);
            Parallel.For(0, h, y =>
            {
                for (int x = 0; x < w; x++)
                {
                    double dx = (double)x / w - cx, dy = (double)y / h - cy;
                    double a = Math.Atan2(dy, dx) / 6.283185307179586 + 1 + turn;
                    a -= Math.Floor(a);
                    double dist = Math.Sqrt(dx * dx + dy * dy) * 2;
                    int ri = Math.Min(rings - 1, (int)(dist / ring));
                    int s = Math.Min(levels - 1, ri / 2);
                    int j = Math.Min((n << s) - 1, (int)(a * (n << s)));
                    im.Px[y * w + x] = color[s][j];
                }
            });
            return im;
        }
        static void RayHsv(double h, double s, double v, int[] c)
        {
            s = Math.Clamp(s, 0, 1); v = Math.Clamp(v, 0.08, 1);
            h = (h - Math.Floor(h)) * 6;
            int i = (int)h;
            double f = h - i, p = v * (1 - s), q = v * (1 - s * f), t = v * (1 - s * (1 - f)), R, G, B;
            switch (i)
            {
                case 0: R = v; G = t; B = p; break;
                case 1: R = q; G = v; B = p; break;
                case 2: R = p; G = v; B = t; break;
                case 3: R = p; G = q; B = v; break;
                case 4: R = t; G = p; B = v; break;
                default: R = v; G = p; B = q; break;
            }
            c[0] = (int)(R * 255); c[1] = (int)(G * 255); c[2] = (int)(B * 255);
        }
    }
}
