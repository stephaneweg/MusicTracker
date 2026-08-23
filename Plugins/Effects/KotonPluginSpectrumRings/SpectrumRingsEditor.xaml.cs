using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using KotonStudio.Library;

namespace KotonPluginSpectrumRings
{
    /// <summary>Éditeur = viz plein cadre. Timer 60fps draine la file de « ring births » du plugin,
    /// spawne des anneaux qui contractent du bord vers le centre en s'estompant, et les rendre.</summary>
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

    internal sealed class RingsCanvas : UserControl
    {
        readonly Canvas _root;
        readonly SpectrumRingsPlugin _plugin;
        DispatcherTimer _timer;
        DateTime _tStart;

        // Position radiale de départ des anneaux selon la bande. Bass = grand rayon (proche du
        // bord), high = plus proche du centre. Convention "note grave = grand mouvement".
        static readonly double[] SpawnRadii = { 0.95, 0.75, 0.55, 0.38 };
        // Couleurs de base par bande (HSV hue en degrés). L'éditeur ajoute une rotation lente
        // (hueSpeed) pour animer les teintes globales.
        static readonly double[] BandHue = { 15, 55, 145, 240 };   // rouge-orange, jaune, teal, bleu

        // Liste des anneaux vivants dans l'éditeur (thread UI uniquement).
        sealed class LiveRing
        {
            public int BandIdx;
            public DateTime BirthTime;
            public float Intensity;
            public double HueOffset;   // pour distinguer 2 rings d'un même burst
        }
        readonly List<LiveRing> _rings = new List<LiveRing>();
        int _birthCounter;

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
            _timer.Tick += (s, e) => Tick();
            _timer.Start();
        }
        public void StopAnimation() { _timer?.Stop(); _timer = null; }

        void Tick()
        {
            // Draine la file de naissances envoyées par le thread audio.
            while (_plugin.TryDequeueBirth(out var b))
            {
                _birthCounter++;
                _rings.Add(new LiveRing
                {
                    BandIdx = b.BandIdx,
                    BirthTime = DateTime.UtcNow,
                    Intensity = b.Intensity,
                    HueOffset = (_birthCounter * 27) % 360,   // petit décalage de teinte par birth
                });
            }
            // Retire les rings morts.
            double life = _plugin.RingLifeSec;
            var now = DateTime.UtcNow;
            for (int i = _rings.Count - 1; i >= 0; i--)
                if ((now - _rings[i].BirthTime).TotalSeconds > life) _rings.RemoveAt(i);
            Render();
        }

        void Render()
        {
            _root.Children.Clear();
            double w = ActualWidth, h = ActualHeight;
            if (w < 40 || h < 40) return;
            double cx = w / 2, cy = h / 2;
            double baseR = Math.Min(w, h) * 0.45;
            double elapsedSec = (DateTime.UtcNow - _tStart).TotalSeconds;
            double hueBase = elapsedSec * _plugin.HueSpeed * 40.0;
            double glow = _plugin.GlowAmount;
            double life = _plugin.RingLifeSec;
            var now = DateTime.UtcNow;

            // Chaque anneau : contraction bord → centre + fade + amincissement stroke.
            foreach (var r in _rings)
            {
                double age = (now - r.BirthTime).TotalSeconds;
                double t = age / life;                             // 0..1
                if (t < 0) t = 0; else if (t > 1) t = 1;
                double spawnR = baseR * SpawnRadii[r.BandIdx];
                double curR = spawnR * (1 - t * 0.85);             // finit à ~15% du rayon initial
                if (curR < 2) continue;
                double alpha = Math.Pow(1 - t, 1.6);                // fade 1→0 non-linéaire (plus doux au début)
                double thickness = Math.Max(0.5, (1 + r.Intensity * 3) * (1 - t));
                double hue = (hueBase + BandHue[r.BandIdx] + r.HueOffset) % 360;
                var col = HsvToRgb(hue, 0.85, 1.0);
                byte a = (byte)Math.Max(0, Math.Min(255, alpha * 255));

                // Halo optionnel : anneau plus large, plus transparent, autour du principal.
                if (glow > 0.01)
                {
                    byte ha = (byte)Math.Max(0, Math.Min(255, alpha * 255 * glow * 0.5));
                    AddCircleStroke(cx, cy, curR + 4 * glow, thickness + 2 * glow,
                        Color.FromArgb(ha, col.R, col.G, col.B));
                }
                AddCircleStroke(cx, cy, curR, thickness, Color.FromArgb(a, col.R, col.G, col.B));
            }

            // Petit cœur central quasi-immobile pour donner un repère visuel.
            var coreCol = HsvToRgb(hueBase % 360, 0.4, 0.8);
            AddCircleFill(cx, cy, 5, Color.FromArgb(180, coreCol.R, coreCol.G, coreCol.B));

            // Debug overlay : nombre d'appels Process + env par bande + triggers par bande + rings.
            // Permet de voir OÙ le flow casse quand rien ne s'anime :
            //  - Process=0 : le hôte ne routes pas l'audio au plugin (pas d'audio, pas de son, effet mal branché)
            //  - Process>0 mais Env≈0 : audio arrive mais silencieux (piste vide)
            //  - Env>0 mais Trig=0 : sensibilité trop basse (seuil transient jamais atteint)
            //  - Trig>0 mais Rings=0 : bug UI (pas d'appel Tick, timer arrêté)
            int calls = _plugin.GetDebugProcessCalls();
            var dbg = string.Format(
                "Proc:{0}  Env B:{1:F3} L:{2:F3} M:{3:F3} H:{4:F3}  Trig B:{5} L:{6} M:{7} H:{8}  Rings:{9}",
                calls,
                _plugin.GetDebugEnv(0), _plugin.GetDebugEnv(1), _plugin.GetDebugEnv(2), _plugin.GetDebugEnv(3),
                _plugin.GetDebugTriggers(0), _plugin.GetDebugTriggers(1), _plugin.GetDebugTriggers(2), _plugin.GetDebugTriggers(3),
                _rings.Count);
            var lbl = new TextBlock
            {
                Text = dbg,
                Foreground = new SolidColorBrush(Color.FromArgb(200, 255, 255, 255)),
                FontSize = 10, FontFamily = new FontFamily("Consolas, Courier New"),
            };
            Canvas.SetLeft(lbl, 8); Canvas.SetTop(lbl, 6);
            _root.Children.Add(lbl);
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
