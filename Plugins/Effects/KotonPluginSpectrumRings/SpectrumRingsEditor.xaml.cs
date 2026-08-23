using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using KotonStudio.Library;

namespace KotonPluginSpectrumRings
{
    /// <summary>Éditeur = juste la viz. Un DispatcherTimer 60 fps lit les niveaux volatile du
    /// plugin et repeint 4 anneaux concentriques (bass → high) + un cœur au centre. Couleurs qui
    /// cyclent en HSV au fil du temps, halo optionnel. Petits sliders en bas pour affiner.</summary>
    public partial class SpectrumRingsEditor : UserControl
    {
        readonly SpectrumRingsPlugin _plugin;
        readonly RingsCanvas _canvas;
        bool _updating;

        public SpectrumRingsEditor(SpectrumRingsPlugin plugin)
        {
            _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
            InitializeComponent();
            _canvas = new RingsCanvas(_plugin);
            canvasHost.Content = _canvas;

            SyncFromPlugin();
            var ps = _plugin.Parameters;
            foreach (var kp in ps) kp.Changed += _ => Dispatcher.BeginInvoke(new Action(SyncFromPlugin));

            sldHue.ValueChanged   += (s, e) => SetParam("hue_speed",  sldHue.Value);
            sldReact.ValueChanged += (s, e) => SetParam("reactivity", sldReact.Value);
            sldGlow.ValueChanged  += (s, e) => SetParam("glow",       sldGlow.Value);

            Unloaded += (s, e) => _canvas.StopAnimation();
        }

        void SetParam(string id, double value)
        {
            if (_updating) return;
            for (int i = 0; i < _plugin.Parameters.Count; i++)
                if (_plugin.Parameters[i].Id == id) { _plugin.Parameters[i].Value = value; return; }
        }
        void SyncFromPlugin()
        {
            _updating = true;
            try
            {
                double GetV(string id)
                {
                    for (int i = 0; i < _plugin.Parameters.Count; i++)
                        if (_plugin.Parameters[i].Id == id) return _plugin.Parameters[i].Value;
                    return 0;
                }
                sldHue.Value = GetV("hue_speed");
                sldReact.Value = GetV("reactivity");
                sldGlow.Value = GetV("glow");
            }
            finally { _updating = false; }
        }
    }

    internal sealed class RingsCanvas : UserControl
    {
        readonly Canvas _root;
        readonly SpectrumRingsPlugin _plugin;
        DispatcherTimer _timer;
        DateTime _tStart;

        // Rayons de base des 4 anneaux (fraction du min(width, height) / 2). Bass = grand anneau
        // extérieur, high = petit anneau intérieur. Convention "grave = large, aigu = étroit".
        static readonly double[] BaseRadii = { 0.85, 0.68, 0.50, 0.32 };

        public RingsCanvas(SpectrumRingsPlugin plugin)
        {
            _plugin = plugin;
            _root = new Canvas
            {
                Background = new SolidColorBrush(Color.FromRgb(0x08, 0x0A, 0x0F)),
                ClipToBounds = true,
            };
            Content = _root;
            _tStart = DateTime.UtcNow;
            Loaded += (s, e) => EnsureTimer();
            SizeChanged += (s, e) => Render();
        }

        void EnsureTimer()
        {
            if (_timer != null) return;
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };   // 60 fps
            _timer.Tick += (s, e) => Render();
            _timer.Start();
        }
        public void StopAnimation() { _timer?.Stop(); _timer = null; }

        void Render()
        {
            _root.Children.Clear();
            double w = ActualWidth, h = ActualHeight;
            if (w < 40 || h < 40) return;
            double cx = w / 2, cy = h / 2;
            double baseR = Math.Min(w, h) * 0.42;

            double elapsedSec = (DateTime.UtcNow - _tStart).TotalSeconds;
            double hueBase = elapsedSec * _plugin.HueSpeed * 40.0;   // deg/s
            double react = _plugin.Reactivity;
            double glow = _plugin.GlowAmount;

            // Cœur central : cercle plein qui pulse avec le niveau global, teinte animée.
            float globLevel = _plugin.GetLevel(4);
            double coreLevel = Math.Min(1.5, globLevel * react * 8);   // scale visuel
            double coreR = 8 + coreLevel * 25;
            var coreCol = HsvToRgb((hueBase) % 360, 0.9, 1.0);
            if (glow > 0.01)
            {
                // Halo doux : 2-3 cercles concentriques translucides pour l'effet lueur.
                for (int g = 0; g < 3; g++)
                {
                    double r = coreR + (g + 1) * 12 * glow;
                    byte a = (byte)Math.Max(0, 60 - g * 22);
                    AddCircleFill(cx, cy, r, Color.FromArgb(a, coreCol.R, coreCol.G, coreCol.B));
                }
            }
            AddCircleFill(cx, cy, coreR, coreCol);

            // 4 anneaux concentriques : rayon = baseR * BaseRadii[i] + modulation par le niveau,
            // couleur = HSV(hueBase + i*90).
            for (int i = 0; i < 4; i++)
            {
                float lvl = _plugin.GetLevel(i);
                double amp = Math.Min(1.5, lvl * react * 10);
                double r = baseR * BaseRadii[i] + amp * baseR * 0.10;
                double stroke = 3 + amp * 12;   // épaisseur pulsée
                double hue = (hueBase + i * 78) % 360;
                var col = HsvToRgb(hue, 0.85, 1.0);
                // Anneau principal
                AddCircleStroke(cx, cy, r, stroke, col);
                // Halo interne / externe si glow
                if (glow > 0.01)
                {
                    byte a = (byte)(80 * glow);
                    AddCircleStroke(cx, cy, r + 8 * glow, 2, Color.FromArgb(a, col.R, col.G, col.B));
                    AddCircleStroke(cx, cy, r - 8 * glow, 2, Color.FromArgb(a, col.R, col.G, col.B));
                }
            }

            // Petites particules qui tournent autour, réactives au niveau global.
            int particleCount = 12;
            double rotSpeed = 0.6 + globLevel * react * 4;
            double rotAngle = elapsedSec * rotSpeed;
            for (int p = 0; p < particleCount; p++)
            {
                double a = rotAngle + p * (2 * Math.PI / particleCount);
                // Distance modulée par la bande la plus proche.
                int band = p % 4;
                float lvl = _plugin.GetLevel(band);
                double rr = baseR * BaseRadii[band] + 20 + Math.Sin(elapsedSec * 3 + p) * 6 + lvl * react * 30;
                double px = cx + Math.Cos(a) * rr;
                double py = cy + Math.Sin(a) * rr;
                double d = 3 + lvl * react * 8;
                var col = HsvToRgb((hueBase + band * 78 + 40) % 360, 0.6, 1.0);
                AddCircleFill(px, py, d, Color.FromArgb(180, col.R, col.G, col.B));
            }
        }

        void AddCircleFill(double cx, double cy, double r, Color col)
        {
            if (r < 0.5) return;
            var e = new Ellipse
            {
                Width = r * 2, Height = r * 2,
                Fill = new SolidColorBrush(col),
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(e, cx - r); Canvas.SetTop(e, cy - r);
            _root.Children.Add(e);
        }
        void AddCircleStroke(double cx, double cy, double r, double thickness, Color col)
        {
            if (r < 0.5) return;
            var e = new Ellipse
            {
                Width = r * 2, Height = r * 2,
                Stroke = new SolidColorBrush(col),
                StrokeThickness = thickness,
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(e, cx - r); Canvas.SetTop(e, cy - r);
            _root.Children.Add(e);
        }

        /// <summary>Conversion HSV → RGB, h en degrés [0,360), s & v en [0,1].</summary>
        static Color HsvToRgb(double h, double s, double v)
        {
            h = ((h % 360) + 360) % 360;
            double c = v * s;
            double x = c * (1 - Math.Abs((h / 60.0) % 2 - 1));
            double m = v - c;
            double r = 0, g = 0, b = 0;
            if (h < 60)        { r = c; g = x; }
            else if (h < 120)  { r = x; g = c; }
            else if (h < 180)  { g = c; b = x; }
            else if (h < 240)  { g = x; b = c; }
            else if (h < 300)  { r = x; b = c; }
            else               { r = c; b = x; }
            return Color.FromRgb((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
        }
    }
}
