using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using KotonStudio.Library;

namespace KotonPluginInstrumentMorph
{
    public partial class InstrumentMorphEditor : UserControl, IKotonEditor
    {
        readonly InstrumentMorphPlugin _plugin;
        bool _syncing, _loading = true;

        // Items du dropdown : on stocke KotonInstrumentDescriptor pour recuperer l'Id sur SelectionChanged.
        // Un item "— aucun —" en tete permet de laisser le canal muet.
        sealed class ComboItem
        {
            public string Id { get; set; }
            public string Label { get; set; }
            public override string ToString() => Label;
        }

        public InstrumentMorphEditor(InstrumentMorphPlugin plugin)
        {
            _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
            InitializeComponent();

            PopulateInstrumentCombos();

            Wire(MorphSlider, MorphValue, "morph", v => v.ToString("F2"));
            Wire(LfoRateSlider, LfoRateValue, "lfo_rate", v => v.ToString("F2") + " Hz");
            Wire(LfoDepthSlider, LfoDepthValue, "lfo_depth", v => v.ToString("F2"));
            Wire(EnvMorphSlider, EnvMorphValue, "env_morph", v => v.ToString("F2"));
            Wire(EnvMsSlider, EnvMsValue, "env_ms", v => v.ToString("F0") + " ms");
            Wire(GainASlider, null, "gain_a", v => v.ToString("F1") + " dB");
            Wire(GainBSlider, null, "gain_b", v => v.ToString("F1") + " dB");
            Wire(VolumeSlider, VolumeValue, "volume", v => v.ToString("F1") + " dB");

            Refresh();
            _loading = false;
        }

        void PopulateInstrumentCombos()
        {
            ComboA.Items.Clear();
            ComboB.Items.Clear();
            ComboA.Items.Add(new ComboItem { Id = "", Label = "— aucun —" });
            ComboB.Items.Add(new ComboItem { Id = "", Label = "— aucun —" });

            var lister = KotonHost.ListInstruments;
            if (lister != null)
            {
                var all = lister();
                if (all != null)
                {
                    foreach (var i in all.OrderBy(x => x.DisplayName, StringComparer.CurrentCultureIgnoreCase))
                    {
                        // Auto-exclusion pour eviter la recursion morph-dans-morph
                        if (i.Id == _plugin.Id) continue;
                        ComboA.Items.Add(new ComboItem { Id = i.Id, Label = i.DisplayName });
                        ComboB.Items.Add(new ComboItem { Id = i.Id, Label = i.DisplayName });
                    }
                }
            }
        }

        void Wire(Slider s, TextBlock lbl, string id, Func<double, string> fmt)
        {
            s.ValueChanged += (x, e) =>
            {
                if (_syncing || _loading) return;
                _plugin.SetParam(id, e.NewValue);
                if (lbl != null) lbl.Text = fmt(e.NewValue);
            };
        }

        void Refresh()
        {
            _syncing = true;
            foreach (var kp in _plugin.Parameters)
            {
                switch (kp.Id)
                {
                    case "morph": MorphSlider.Value = kp.Value; MorphValue.Text = kp.Value.ToString("F2"); break;
                    case "lfo_rate": LfoRateSlider.Value = kp.Value; LfoRateValue.Text = kp.Value.ToString("F2") + " Hz"; break;
                    case "lfo_depth": LfoDepthSlider.Value = kp.Value; LfoDepthValue.Text = kp.Value.ToString("F2"); break;
                    case "env_morph": EnvMorphSlider.Value = kp.Value; EnvMorphValue.Text = kp.Value.ToString("F2"); break;
                    case "env_ms": EnvMsSlider.Value = kp.Value; EnvMsValue.Text = kp.Value.ToString("F0") + " ms"; break;
                    case "gain_a": GainASlider.Value = kp.Value; break;
                    case "gain_b": GainBSlider.Value = kp.Value; break;
                    case "volume": VolumeSlider.Value = kp.Value; VolumeValue.Text = kp.Value.ToString("F1") + " dB"; break;
                }
            }
            SelectComboByPluginId(ComboA, _plugin.InstrumentAId);
            SelectComboByPluginId(ComboB, _plugin.InstrumentBId);
            _syncing = false;
        }
        static void SelectComboByPluginId(ComboBox cb, string id)
        {
            for (int i = 0; i < cb.Items.Count; i++)
            {
                if (cb.Items[i] is ComboItem ci && ci.Id == id) { cb.SelectedIndex = i; return; }
            }
            cb.SelectedIndex = 0;
        }

        void ComboA_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || _syncing) return;
            if (ComboA.SelectedItem is ComboItem ci) _plugin.InstrumentAId = ci.Id;
        }
        void ComboB_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || _syncing) return;
            if (ComboB.SelectedItem is ComboItem ci) _plugin.InstrumentBId = ci.Id;
        }

        void EditA_Click(object sender, RoutedEventArgs e) => OpenChildEditor(_plugin.InstrumentA, "A : " + (ComboA.SelectedItem as ComboItem)?.Label);
        void EditB_Click(object sender, RoutedEventArgs e) => OpenChildEditor(_plugin.InstrumentB, "B : " + (ComboB.SelectedItem as ComboItem)?.Label);

        void OpenChildEditor(IKotonInstrument inst, string title)
        {
            if (inst == null) return;
            if (!inst.HasEditor) return;
            UserControl uc;
            try { uc = inst.CreateEditor(); } catch { return; }
            if (uc == null) return;

            var w = new Window
            {
                Title = title,
                Owner = Window.GetWindow(this),
                Width = Math.Max(680, uc.MinWidth + 40),
                Height = Math.Max(420, uc.MinHeight + 60),
                Content = uc,
                Background = System.Windows.Media.Brushes.Transparent,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
            };
            w.Closed += (s, ev) => { try { _plugin.CaptureChildStates(); } catch { } };
            w.Show();
        }

        public void OnContextUpdated(KotonRenderContext ctx) { }
    }
}
