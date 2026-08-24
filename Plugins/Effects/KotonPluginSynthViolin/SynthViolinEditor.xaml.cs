using System;
using System.Windows.Controls;
using System.Windows.Threading;
using KotonStudio.Library;

namespace KotonPluginSynthViolin
{
    public partial class SynthViolinEditor : UserControl, IKotonEditor
    {
        static readonly string[] NoteNames = { "Do", "Do#", "Ré", "Mib", "Mi", "Fa", "Fa#", "Sol", "Sol#", "La", "Sib", "Si" };

        readonly SynthViolinPlugin _plugin;
        readonly DispatcherTimer _meter;
        bool _syncing, _loading = true;

        public SynthViolinEditor(SynthViolinPlugin plugin)
        {
            _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
            InitializeComponent();

            PresetCombo.Items.Clear();
            PresetCombo.Items.Add("— Custom —");
            foreach (var n in SynthViolinPlugin.PresetNames) PresetCombo.Items.Add(n);
            PresetCombo.SelectedIndex = 0;

            Wire(BowSlider, BowValue, "bow", v => v.ToString("F2"));
            Wire(BodySlider, BodyValue, "body", v => v.ToString("F2"));
            Wire(NoiseSlider, NoiseValue, "noise", v => v.ToString("F2"));
            Wire(IntervalSlider, IntervalValue, "interval", v => (v > 0 ? "+" : "") + v.ToString("F0") + " demi-tons");
            Wire(AttackSlider, AttackValue, "attack", v => v.ToString("F0") + " ms");
            Wire(ReleaseSlider, ReleaseValue, "release", v => v.ToString("F0") + " ms");
            Wire(LowNoteSlider, LowNoteValue, "low_note", NoteLabel);
            Wire(MaxLeapSlider, MaxLeapValue, "max_leap", v => v <= 0 ? "aucune limite" : v.ToString("F0") + " demi-tons");
            Wire(GuardSlider, GuardValue, "octave_guard", v => v <= 0 ? "désactivée" : v.ToString("F2"));
            Wire(MixSlider, MixValue, "mix", v => v.ToString("F2"));
            Wire(OutGainSlider, OutGainValue, "out_gain", v => v.ToString("F1") + " dB");

            Refresh();
            _loading = false;

            _meter = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(80) };
            _meter.Tick += (s, e) => UpdateTrackLabel();
            Loaded += (s, e) => _meter.Start();
            Unloaded += (s, e) => _meter.Stop();
        }

        static string NoteLabel(double midi)
        {
            int m = (int)Math.Round(midi);
            double hz = 440.0 * Math.Pow(2.0, (m - 69) / 12.0);
            return NoteNames[((m % 12) + 12) % 12] + (m / 12 - 1) + "  (" + hz.ToString("F0") + " Hz)";
        }

        void UpdateTrackLabel()
        {
            double f = _plugin.TrackedFrequency;
            TrackLabel.Text = !_plugin.Locked || f <= 0
                ? "Synth Violin · en attente d'un signal"
                : "suivi : " + f.ToString("F1") + " Hz";
        }

        void Wire(Slider s, TextBlock lbl, string id, Func<double, string> fmt)
        {
            s.ValueChanged += (x, e) =>
            {
                if (_syncing || _loading) return;
                _plugin.SetParam(id, e.NewValue);
                lbl.Text = fmt(e.NewValue);
                if (PresetCombo.SelectedIndex != 0) PresetCombo.SelectedIndex = 0;
            };
        }

        void Refresh()
        {
            _syncing = true;
            foreach (var kp in _plugin.Parameters)
            {
                switch (kp.Id)
                {
                    case "bow": BowSlider.Value = kp.Value; BowValue.Text = kp.Value.ToString("F2"); break;
                    case "body": BodySlider.Value = kp.Value; BodyValue.Text = kp.Value.ToString("F2"); break;
                    case "noise": NoiseSlider.Value = kp.Value; NoiseValue.Text = kp.Value.ToString("F2"); break;
                    case "interval": IntervalSlider.Value = kp.Value; IntervalValue.Text = (kp.Value > 0 ? "+" : "") + kp.Value.ToString("F0") + " demi-tons"; break;
                    case "attack": AttackSlider.Value = kp.Value; AttackValue.Text = kp.Value.ToString("F0") + " ms"; break;
                    case "release": ReleaseSlider.Value = kp.Value; ReleaseValue.Text = kp.Value.ToString("F0") + " ms"; break;
                    case "low_note": LowNoteSlider.Value = kp.Value; LowNoteValue.Text = NoteLabel(kp.Value); break;
                    case "max_leap": MaxLeapSlider.Value = kp.Value; MaxLeapValue.Text = kp.Value <= 0 ? "aucune limite" : kp.Value.ToString("F0") + " demi-tons"; break;
                    case "octave_guard": GuardSlider.Value = kp.Value; GuardValue.Text = kp.Value <= 0 ? "désactivée" : kp.Value.ToString("F2"); break;
                    case "mix": MixSlider.Value = kp.Value; MixValue.Text = kp.Value.ToString("F2"); break;
                    case "out_gain": OutGainSlider.Value = kp.Value; OutGainValue.Text = kp.Value.ToString("F1") + " dB"; break;
                }
            }
            _syncing = false;
        }

        void PresetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || _syncing) return;
            int idx = PresetCombo.SelectedIndex;
            if (idx <= 0) return;
            _plugin.LoadPreset(idx - 1);
            Refresh();
        }

        public void OnContextUpdated(KotonRenderContext ctx) { }
    }
}
