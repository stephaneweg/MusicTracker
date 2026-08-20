using System;
using System.Windows.Controls;
using KotonStudio.Library;

namespace KotonPluginUnderwater
{
    public partial class UnderwaterEditor : UserControl, IKotonEditor
    {
        readonly UnderwaterPlugin _plugin;
        bool _syncing, _loading = true;

        public UnderwaterEditor(UnderwaterPlugin plugin)
        {
            _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
            InitializeComponent();
            PresetCombo.Items.Clear();
            PresetCombo.Items.Add("— Custom —");
            foreach (var n in UnderwaterPlugin.PresetNames) PresetCombo.Items.Add(n);
            PresetCombo.SelectedIndex = 0;
            Wire(DepthSlider,    DepthValue,    "depth",     v => v.ToString("F2"));
            Wire(MovementSlider, MovementValue, "movement",  v => v.ToString("F2"));
            Wire(BubblesSlider,  BubblesValue,  "bubbles",   v => v.ToString("F2"));
            Wire(HpFilterSlider, HpFilterValue, "hp_filter", v => v.ToString("F0") + " Hz");
            Wire(MixSlider,      MixValue,      "mix",       v => v.ToString("F2"));
            Wire(OutGainSlider,  OutGainValue,  "out_gain",  v => v.ToString("F1") + " dB");
            Refresh();
            _loading = false;
        }
        void Wire(Slider s, TextBlock lbl, string id, Func<double, string> fmt)
        {
            s.ValueChanged += (x, e) => {
                if (_syncing || _loading) return;
                _plugin.SetParam(id, e.NewValue); lbl.Text = fmt(e.NewValue);
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
                    case "depth":     DepthSlider.Value    = kp.Value; DepthValue.Text    = kp.Value.ToString("F2"); break;
                    case "movement":  MovementSlider.Value = kp.Value; MovementValue.Text = kp.Value.ToString("F2"); break;
                    case "bubbles":   BubblesSlider.Value  = kp.Value; BubblesValue.Text  = kp.Value.ToString("F2"); break;
                    case "hp_filter": HpFilterSlider.Value = kp.Value; HpFilterValue.Text = kp.Value.ToString("F0") + " Hz"; break;
                    case "mix":       MixSlider.Value      = kp.Value; MixValue.Text      = kp.Value.ToString("F2"); break;
                    case "out_gain":  OutGainSlider.Value  = kp.Value; OutGainValue.Text  = kp.Value.ToString("F1") + " dB"; break;
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
