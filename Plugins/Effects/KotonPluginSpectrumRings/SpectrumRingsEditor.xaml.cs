using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using KotonStudio.Library;

namespace KotonPluginSpectrumRings
{
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

            sldHue.ValueChanged   += (s, e) => SetParam("hue_speed",   sldHue.Value);
            sldReact.ValueChanged += (s, e) => SetParam("sensitivity", sldReact.Value);
            sldGlow.ValueChanged  += (s, e) => SetParam("glow",        sldGlow.Value);

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
                sldReact.Value = GetV("sensitivity");
                sldGlow.Value = GetV("glow");
            }
            finally { _updating = false; }
        }
    }

    /// <summary>4 anneaux concentriques permanents (bass au bord → high au centre). Chaque
    /// anneau : rayon = base + niveau*pulse, stroke = 1.5 + niveau*épaisseur, wobble sinusoïdal
    /// autour du périmètre proportionnel au niveau. Couleurs HSV cyclant lentement dans le temps.
    /// Anim toujours visible même sans audio (anneaux minces au repos), grandit et se déforme
    /// dès qu'un son arrive dans sa bande.</summary>
    internal sealed class RingsCanvas : UserControl
    {
        readonly Canvas _root;
        readonly SpectrumRingsPlugin _plugin;
        DispatcherTimer _timer;
        DateTime _tStart;

        // Rayons de base (fraction du min(w,h)/2). Bass à l'extérieur, high à l'intérieur.
        static readonly double[] BaseRadii = { 0.90, 0.72, 0.54, 0.36 };
        // Couleurs de base (hue HSV) par bande.
        static readonly double[] BandHue = { 15, 55, 145, 240 };

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
            double baseR = Math.Min(w, h) * 0.45;
            double elapsed = (DateTime.UtcNow - _tStart).TotalSeconds;
            double hueBase = elapsed * _plugin.HueSpeed * 40.0;
            double sens = _plugin.Sensitivity;
            double glow = _plugin.GlowAmount;

            // Dessine chaque anneau. Nombre de modes du wobble différent par bande pour un effet
            // organique (bass = 3 lobes lents, high = 7 lobes rapides).
            int[] wobbleModes = { 3, 4, 5, 7 };
            double[] wobbleSpeed = { 1.4, 2.1, 2.8, 3.6 };
            for (int i = 0; i < 4; i++)
            {
                float lvl = _plugin.GetLevel(i);
                double amp = Math.Min(2.0, lvl * sens * 25);   // 0..2 selon la puissance de la bande
                double rBase = baseR * BaseRadii[i] + amp * baseR * 0.05;
                double thickness = 1.5 + amp * 6;
                double wobbleAmp = amp * 8;
                int modes = wobbleModes[i];
                double phase = elapsed * wobbleSpeed[i];

                double hue = (hueBase + BandHue[i]) % 360;
                var col = HsvToRgb(hue, 0.85, 1.0);
                DrawWobblyRing(cx, cy, rBase, wobbleAmp, modes, phase, thickness, col);

                // Halo optionnel : un 2e anneau plus large et plus transparent autour.
                if (glow > 0.01 && amp > 0.05)
                {
                    var haloCol = Color.FromArgb((byte)(140 * glow), col.R, col.G, col.B);
                    DrawWobblyRing(cx, cy, rBase + 6 * glow, wobbleAmp * 1.2, modes, phase, thickness * 0.6, haloCol);
                }
            }

            // Cœur central : petit cercle qui bat au max des 4 bandes.
            float maxLvl = 0;
            for (int i = 0; i < 4; i++) { var l = _plugin.GetLevel(i); if (l > maxLvl) maxLvl = l; }
            double coreR = 4 + Math.Min(20, maxLvl * sens * 40);
            var coreCol = HsvToRgb(hueBase % 360, 0.6, 1.0);
            AddCircleFill(cx, cy, coreR, coreCol);

            // HUD debug discret.
            int calls = _plugin.GetDebugProcessCalls();
            var lbl = new TextBlock
            {
                Text = string.Format("Proc:{0}  B:{1:F3} L:{2:F3} M:{3:F3} H:{4:F3}",
                    calls, _plugin.GetLevel(0), _plugin.GetLevel(1), _plugin.GetLevel(2), _plugin.GetLevel(3)),
                Foreground = new SolidColorBrush(Color.FromArgb(120, 255, 255, 255)),
                FontSize = 10, FontFamily = new FontFamily("Consolas, Courier New"),
            };
            Canvas.SetLeft(lbl, 8); Canvas.SetTop(lbl, 6);
            _root.Children.Add(lbl);
        }

        /// <summary>Dessine un anneau ondulé : polyline circulaire dont le rayon est modulé par
        /// une sinusoïde de N lobes + phase. wobbleAmp=0 → cercle parfait. Sinon → ondulations
        /// autour du périmètre qui donnent un effet "vibrant".</summary>
        void DrawWobblyRing(double cx, double cy, double baseR, double wobbleAmp, int modes, double phase, double thickness, Color col)
        {
            if (baseR < 2) return;
            int segments = 96;
            var pts = new PointCollection(segments + 1);
            for (int i = 0; i <= segments; i++)
            {
                double a = i * 2 * Math.PI / segments;
                double r = baseR + wobbleAmp * Math.Sin(a * modes + phase);
                pts.Add(new Point(cx + Math.Cos(a) * r, cy + Math.Sin(a) * r));
            }
            _root.Children.Add(new Polyline
            {
                Points = pts,
                Stroke = new SolidColorBrush(col),
                StrokeThickness = thickness,
                StrokeLineJoin = PenLineJoin.Round,
                IsHitTestVisible = false,
            });
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
