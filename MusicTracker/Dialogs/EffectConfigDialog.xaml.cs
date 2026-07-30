using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MusicTracker.Engine.Timeline.Effects;
using MusicTracker.Localization;

namespace MusicTracker.Dialogs
{
    /// <summary>
    /// Petit éditeur générique de paramètres d'effet : lit et écrit directement dans
    /// <see cref="TrackEffectData.Params"/>. On ne relit pas le player (le snapshot des inserts est pris
    /// au Start) — les changements sont audibles à la prochaine mise en lecture. C'est cohérent avec la
    /// façon dont l'app traite déjà l'automation, et évite d'avoir à re-synchroniser des effets vivants.
    /// </summary>
    public partial class EffectConfigDialog : Window
    {
        readonly TrackEffectData data;

        public EffectConfigDialog(TrackEffectData data, Window owner)
        {
            InitializeComponent();
            Owner = owner;
            this.data = data;
            titleText.Text = Loc.T(EffectFactory.LocKey(data.Kind));
            BuildFields();
        }

        void BuildFields()
        {
            // Spec par type d'effet : liste (clé, label, min, max, decimals).
            List<(string Key, string Label, double Min, double Max, int Dec)> spec;
            switch (data.Kind)
            {
                case "eq":
                    spec = new List<(string, string, double, double, int)>
                    {
                        ("LowFreq",   Loc.T("EqLowFreq"),   30,  400,   0),
                        ("LowGainDb", Loc.T("EqLowGain"),  -18,  18,    1),
                        ("MidFreq",   Loc.T("EqMidFreq"),  200, 6000,   0),
                        ("MidGainDb", Loc.T("EqMidGain"),  -18,  18,    1),
                        ("MidQ",      Loc.T("EqMidQ"),     0.2, 10.0,   2),
                        ("HighFreq",  Loc.T("EqHighFreq"),1000,16000,   0),
                        ("HighGainDb",Loc.T("EqHighGain"), -18,  18,    1),
                    };
                    break;
                case "comp":
                    spec = new List<(string, string, double, double, int)>
                    {
                        ("ThresholdDb", Loc.T("CompThreshold"), -60,   0, 1),
                        ("Ratio",       Loc.T("CompRatio"),      1,   20, 1),
                        ("AttackMs",    Loc.T("CompAttack"),     0.1,200, 1),
                        ("ReleaseMs",   Loc.T("CompRelease"),    5, 1000, 0),
                        ("MakeupDb",    Loc.T("CompMakeup"),     0,   24, 1),
                    };
                    break;
                case "delay":
                    spec = new List<(string, string, double, double, int)>
                    {
                        ("TimeMs",   Loc.T("DelayTime"),     10, 2000, 0),
                        ("Feedback", Loc.T("DelayFeedback"),  0,  0.95, 2),
                        ("Mix",      Loc.T("Mix"),            0,  1.0,  2),
                        ("PingPong", Loc.T("DelayPingPong"),  0,  1.0,  0),
                    };
                    break;
                case "sat":
                    spec = new List<(string, string, double, double, int)>
                    {
                        ("Drive", Loc.T("SatDrive"), 0, 1.0, 2),
                        ("Mix",   Loc.T("Mix"),     0, 1.0, 2),
                    };
                    break;
                default:
                    spec = new List<(string, string, double, double, int)>();
                    break;
            }

            var defaults = DefaultParams(data.Kind);
            foreach (var (key, label, min, max, dec) in spec)
            {
                if (!data.Params.TryGetValue(key, out var cur))
                {
                    cur = defaults.TryGetValue(key, out var def) ? def : min;
                    data.Params[key] = cur;
                }
                paramsHost.Children.Add(BuildRow(key, label, min, max, dec, cur));
            }
        }

        FrameworkElement BuildRow(string key, string label, double min, double max, int dec, double initial)
        {
            var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
            var lbl = new TextBlock { Text = label, Width = 140, Foreground = new SolidColorBrush(Color.FromRgb(0xBB, 0xBB, 0xBB)), VerticalAlignment = VerticalAlignment.Center, FontSize = 11 };
            var val = new TextBlock { MinWidth = 46, TextAlignment = TextAlignment.Right, Foreground = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99)), FontSize = 10, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(lbl, Dock.Left); DockPanel.SetDock(val, Dock.Right);
            row.Children.Add(lbl); row.Children.Add(val);
            var s = new Slider { Minimum = min, Maximum = max, Value = initial, SmallChange = Math.Max((max - min) / 200.0, Math.Pow(10, -dec)), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 6, 0) };
            val.Text = Math.Round(initial, dec).ToString("0." + new string('#', Math.Max(1, dec)));
            s.ValueChanged += (a, e) =>
            {
                double v = Math.Round(s.Value, dec);
                data.Params[key] = v;
                val.Text = v.ToString("0." + new string('#', Math.Max(1, dec)));
            };
            row.Children.Add(s);
            return row;
        }

        static Dictionary<string, double> DefaultParams(string kind)
        {
            var sr = 44100;
            var fx = EffectFactory.Create(kind, sr);
            return fx?.Save() ?? new Dictionary<string, double>();
        }

        void Close_Click(object sender, RoutedEventArgs e) => Close();

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.Key == Key.Escape) Close();
            base.OnKeyDown(e);
        }
    }
}
