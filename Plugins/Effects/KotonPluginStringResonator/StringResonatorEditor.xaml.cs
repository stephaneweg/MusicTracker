using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using KotonStudio.Library;

namespace KotonPluginStringResonator
{
    public partial class StringResonatorEditor : UserControl, IKotonEditor
    {
        static readonly string[] NoteNames = { "Do", "Do#", "Ré", "Mib", "Mi", "Fa", "Fa#", "Sol", "Sol#", "La", "Sib", "Si" };

        readonly StringResonatorPlugin _plugin;
        readonly DispatcherTimer _meter;
        bool _syncing, _loading = true;

        public StringResonatorEditor(StringResonatorPlugin plugin)
        {
            _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
            InitializeComponent();

            PresetCombo.Items.Clear();
            PresetCombo.Items.Add("— Custom —");
            foreach (var n in StringResonatorPlugin.PresetNames) PresetCombo.Items.Add(n);
            PresetCombo.SelectedIndex = 0;

            TuneCombo.Items.Add("Suivi de l'entrée (continu)");
            TuneCombo.Items.Add("Note fixe (bourdon)");

            Wire(DecaySlider, DecayValue, "decay", v => v.ToString("F2"));
            Wire(ToneSlider, ToneValue, "tone", v => v.ToString("F2"));
            Wire(DriveSlider, DriveValue, "drive", v => v.ToString("F2"));
            Wire(IntervalSlider, IntervalValue, "interval", v => (v > 0 ? "+" : "") + v.ToString("F0") + " demi-tons");
            Wire(SpreadSlider, SpreadValue, "spread", v => v.ToString("F0") + " cents");
            Wire(MaxLeapSlider, MaxLeapValue, "max_leap", v => v <= 0 ? "aucune limite" : v.ToString("F0") + " demi-tons");
            Wire(GuardSlider, GuardValue, "octave_guard", v => v <= 0 ? "désactivée" : v.ToString("F2"));
            Wire(MixSlider, MixValue, "mix", v => v.ToString("F2"));
            Wire(OutGainSlider, OutGainValue, "out_gain", v => v.ToString("F1") + " dB");
            Wire(FixedNoteSlider, FixedNoteValue, "fixed_note", NoteLabel);
            Wire(LowNoteSlider, LowNoteValue, "low_note", NoteLabel);

            Refresh();
            _loading = false;

            // Témoin d'accroche : voir la fréquence suivie en direct est ce qui permet de régler « note la
            // plus grave » à l'oreille, sans deviner.
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
                ? "String Resonator · en attente d'un signal"
                : "suivi : " + f.ToString("F1") + " Hz  (" + NoteLabel(69 + 12 * Math.Log(f / 440.0, 2)).Split('(')[0].Trim() + ")";
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
                    case "tune_mode": TuneCombo.SelectedIndex = kp.Value >= 0.5 ? 1 : 0; break;
                    case "fixed_note": FixedNoteSlider.Value = kp.Value; FixedNoteValue.Text = NoteLabel(kp.Value); break;
                    case "interval": IntervalSlider.Value = kp.Value; IntervalValue.Text = (kp.Value > 0 ? "+" : "") + kp.Value.ToString("F0") + " demi-tons"; break;
                    case "decay": DecaySlider.Value = kp.Value; DecayValue.Text = kp.Value.ToString("F2"); break;
                    case "tone": ToneSlider.Value = kp.Value; ToneValue.Text = kp.Value.ToString("F2"); break;
                    case "drive": DriveSlider.Value = kp.Value; DriveValue.Text = kp.Value.ToString("F2"); break;
                    case "spread": SpreadSlider.Value = kp.Value; SpreadValue.Text = kp.Value.ToString("F0") + " cents"; break;
                    case "low_note": LowNoteSlider.Value = kp.Value; LowNoteValue.Text = NoteLabel(kp.Value); break;
                    case "max_leap": MaxLeapSlider.Value = kp.Value; MaxLeapValue.Text = kp.Value <= 0 ? "aucune limite" : kp.Value.ToString("F0") + " demi-tons"; break;
                    case "octave_guard": GuardSlider.Value = kp.Value; GuardValue.Text = kp.Value <= 0 ? "désactivée" : kp.Value.ToString("F2"); break;
                    case "mix": MixSlider.Value = kp.Value; MixValue.Text = kp.Value.ToString("F2"); break;
                    case "out_gain": OutGainSlider.Value = kp.Value; OutGainValue.Text = kp.Value.ToString("F1") + " dB"; break;
                }
            }
            _syncing = false;
            UpdateTuneVisibility();
        }

        /// <summary>La note fixe n'a de sens qu'en mode bourdon : on la masque en mode suivi plutôt que de
        /// laisser un réglage sans effet à l'écran.</summary>
        void UpdateTuneVisibility()
        {
            bool fixedTune = TuneCombo.SelectedIndex == 1;
            var vis = fixedTune ? Visibility.Visible : Visibility.Collapsed;
            FixedNoteLabel.Visibility = vis;
            FixedNotePanel.Visibility = vis;
        }

        void TuneCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_syncing || _loading) return;
            _plugin.SetParam("tune_mode", TuneCombo.SelectedIndex == 1 ? 1.0 : 0.0);
            UpdateTuneVisibility();
            if (PresetCombo.SelectedIndex != 0) PresetCombo.SelectedIndex = 0;
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
