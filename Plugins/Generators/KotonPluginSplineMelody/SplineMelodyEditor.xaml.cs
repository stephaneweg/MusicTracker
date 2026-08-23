using System;
using System.Globalization;
using System.Windows.Controls;
using KotonStudio.Library;

namespace KotonPluginSplineMelody
{
    /// <summary>
    /// P1 : éditeur minimal — juste les options globales pour valider que le plugin se charge et que
    /// le moteur produit des notes. Le canvas multi-voix (drag/drop des points), le sous-éditeur
    /// rythme par voix, et l'onglet couleur arrivent en P2.
    /// </summary>
    public partial class SplineMelodyEditor : UserControl, IKotonEditor
    {
        readonly SplineMelodyGenerator _plugin;
        bool _updating;

        public SplineMelodyEditor(SplineMelodyGenerator plugin)
        {
            _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
            InitializeComponent();

            for (int i = 1; i <= SplineMelodyGenerator.MaxVoices; i++) cboVoiceCount.Items.Add(i.ToString());
            for (int i = 1; i <= 16; i++) cboBars.Items.Add(i.ToString(CultureInfo.InvariantCulture));
            cboStartMode.Items.Add("Fixe");
            cboStartMode.Items.Add("Auto");
            cboInterp.Items.Add("Linéaire");
            cboInterp.Items.Add("Spline");
            cboArticulation.Items.Add("Legato");
            cboArticulation.Items.Add("Normal");
            cboArticulation.Items.Add("Détaché");
            cboArticulation.Items.Add("Staccato");

            SyncFromPlugin();

            var ps = _plugin.Parameters;
            foreach (var kp in ps) kp.Changed += _ => Dispatcher.BeginInvoke(new Action(SyncFromPlugin));

            cboVoiceCount.SelectionChanged += (s, e) => SetParam("voice_count", cboVoiceCount.SelectedIndex + 1);
            cboBars.SelectionChanged += (s, e) => { if (_updating) return; _plugin.SetDurationBars(cboBars.SelectedIndex + 1); };
            cboStartMode.SelectionChanged += (s, e) => SetParam("start_mode", cboStartMode.SelectedIndex);
            txtStartMidi.LostFocus += (s, e) => { if (int.TryParse(txtStartMidi.Text, out int v)) SetParam("start_midi", Math.Max(0, Math.Min(127, v))); };
            sldAmbitus.ValueChanged += (s, e) => { SetParam("ambitus_semis", sldAmbitus.Value); lblAmbitus.Text = ((int)sldAmbitus.Value).ToString(CultureInfo.InvariantCulture) + " st"; };
            cboInterp.SelectionChanged += (s, e) => SetParam("interpolation", cboInterp.SelectedIndex);
            sldVelocity.ValueChanged += (s, e) => { SetParam("velocity", sldVelocity.Value); lblVelocity.Text = ((int)sldVelocity.Value).ToString(CultureInfo.InvariantCulture); };
            cboArticulation.SelectionChanged += (s, e) => SetParam("articulation", cboArticulation.SelectedIndex);
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
                int vc = (int)GetV("voice_count");
                cboVoiceCount.SelectedIndex = Math.Max(0, Math.Min(SplineMelodyGenerator.MaxVoices - 1, vc - 1));
                int bars = (int)_plugin.DurationBarsValue;
                cboBars.SelectedIndex = Math.Max(0, Math.Min(15, bars - 1));
                int sm = (int)GetV("start_mode");
                cboStartMode.SelectedIndex = Math.Max(0, Math.Min(1, sm));
                txtStartMidi.Text = ((int)GetV("start_midi")).ToString(CultureInfo.InvariantCulture);
                sldAmbitus.Value = GetV("ambitus_semis");
                lblAmbitus.Text = ((int)sldAmbitus.Value).ToString(CultureInfo.InvariantCulture) + " st";
                int it = (int)GetV("interpolation");
                cboInterp.SelectedIndex = Math.Max(0, Math.Min(1, it));
                sldVelocity.Value = GetV("velocity");
                lblVelocity.Text = ((int)sldVelocity.Value).ToString(CultureInfo.InvariantCulture);
                int ar = (int)GetV("articulation");
                cboArticulation.SelectedIndex = Math.Max(0, Math.Min(3, ar));
            }
            finally { _updating = false; }
        }

        public void OnContextUpdated(KotonRenderContext ctx)
        {
            Dispatcher.BeginInvoke(new Action(SyncFromPlugin));
        }
    }
}
