using System;
using System.Windows.Controls;
using KotonStudio.Library;

namespace KotonPluginMallets
{
    /// <summary>Éditeur du plugin Mallets. Combo instrument (Marimba/Xylo/Vibraphone/…) qui charge
    /// les defaults du preset (mallet hardness, damping, tremolo) tout en gardant les valeurs
    /// spécifiques à l'utilisateur pour position, brightness, stereo spread, volume.</summary>
    public partial class MalletsEditor : UserControl, IKotonEditor
    {
        readonly MalletsPlugin _plugin;
        bool _syncing;
        bool _loading = true;

        public MalletsEditor(MalletsPlugin plugin)
        {
            _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
            InitializeComponent();
            InitCombo();
            WireSliders();
            RefreshFromPlugin();
            _loading = false;
        }

        void InitCombo()
        {
            InstrumentCombo.Items.Clear();
            foreach (var n in MalletsPlugin.InstrumentNames) InstrumentCombo.Items.Add(n);
        }

        void WireSliders()
        {
            Wire(MalletHardnessSlider, MalletHardnessValue, "mallet_hardness", v => v.ToString("F2"));
            Wire(PositionSlider,       PositionValue,       "position",        v => v.ToString("F2"));
            Wire(DampingSlider,        DampingValue,        "damping",         v => v.ToString("F2") + " ×");
            Wire(BrightnessSlider,     BrightnessValue,     "brightness",      v => v.ToString("F2"));
            Wire(TremRateSlider,       TremRateValue,       "trem_rate",       v => v.ToString("F1") + " Hz");
            Wire(TremDepthSlider,      TremDepthValue,      "trem_depth",      v => v.ToString("F2"));
            Wire(StereoSpreadSlider,   StereoSpreadValue,   "stereo_spread",   v => v.ToString("F2"));
            Wire(VolumeSlider,         VolumeValue,         "volume",          v => v.ToString("F1") + " dB");
        }

        void Wire(Slider slider, TextBlock label, string paramId, Func<double, string> fmt)
        {
            slider.ValueChanged += (s, e) =>
            {
                if (_syncing || _loading) return;
                _plugin.SetParam(paramId, e.NewValue);
                label.Text = fmt(e.NewValue);
            };
        }

        void RefreshFromPlugin()
        {
            _syncing = true;
            foreach (var kp in _plugin.Parameters)
            {
                switch (kp.Id)
                {
                    case "instrument":       InstrumentCombo.SelectedIndex = (int)kp.Value; break;
                    case "mallet_hardness":  MalletHardnessSlider.Value = kp.Value; MalletHardnessValue.Text = kp.Value.ToString("F2"); break;
                    case "position":         PositionSlider.Value       = kp.Value; PositionValue.Text       = kp.Value.ToString("F2"); break;
                    case "damping":          DampingSlider.Value        = kp.Value; DampingValue.Text        = kp.Value.ToString("F2") + " ×"; break;
                    case "brightness":       BrightnessSlider.Value     = kp.Value; BrightnessValue.Text     = kp.Value.ToString("F2"); break;
                    case "trem_rate":        TremRateSlider.Value       = kp.Value; TremRateValue.Text       = kp.Value.ToString("F1") + " Hz"; break;
                    case "trem_depth":       TremDepthSlider.Value      = kp.Value; TremDepthValue.Text      = kp.Value.ToString("F2"); break;
                    case "stereo_spread":    StereoSpreadSlider.Value   = kp.Value; StereoSpreadValue.Text   = kp.Value.ToString("F2"); break;
                    case "volume":           VolumeSlider.Value         = kp.Value; VolumeValue.Text         = kp.Value.ToString("F1") + " dB"; break;
                }
            }
            _syncing = false;
        }

        void InstrumentCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || _syncing) return;
            int idx = InstrumentCombo.SelectedIndex;
            _plugin.SetParam("instrument", idx);
            _plugin.ApplyInstrumentDefaults(idx);
            RefreshFromPlugin();
        }

        public void OnContextUpdated(KotonRenderContext ctx) { }
    }
}
