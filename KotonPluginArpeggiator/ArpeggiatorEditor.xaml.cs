using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace KotonPluginArpeggiator
{
    /// <summary>
    /// Éditeur WPF de l'<see cref="Arpeggiator"/> : combos Pattern / Rate, sliders Octaves / Gate /
    /// Vélocité / Note base / Durée. Deux-way binding manuel (on modifie directement les
    /// <c>KotonParameter</c> du plugin ; les sliders posent leur valeur à ValueChanged, l'éditeur
    /// souscrit à l'événement <see cref="KotonStudio.Library.KotonParameter.Changed"/> pour se
    /// mettre à jour si un autre appelant écrase (par ex. LoadState d'un blob restauré).
    ///
    /// Pas de bouton Preview ICI : la barre Preview/Stop est fournie par l'HÔTE (Koton Studio) qui
    /// entoure ce UserControl — un plugin n'a pas à savoir comment jouer ses notes, l'hôte s'en
    /// charge via KotonHost.PreviewNotes. Si un plugin est utilisé dans un contexte sans hôte
    /// (test isolé), il peut choisir d'ajouter son propre bouton — mais pour respecter la séparation
    /// hôte/plugin, on laisse l'hôte gérer.
    /// </summary>
    public partial class ArpeggiatorEditor : UserControl
    {
        readonly Arpeggiator _plugin;
        bool _updating;   // évite les boucles binding (setter param → Changed → setter UI → setter param)

        public ArpeggiatorEditor(Arpeggiator plugin)
        {
            InitializeComponent();
            _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));

            // Peuple les combos.
            foreach (var n in Arpeggiator.PatternNames) cboPattern.Items.Add(n);
            foreach (var n in Arpeggiator.RateNames) cboRate.Items.Add(n);

            // Charge l'état initial depuis les paramètres du plugin.
            SyncFromPlugin();

            // Ecoute les changements côté plugin (LoadState / autre appelant qui écraserait).
            var p = _plugin.Parameters;
            foreach (var kp in p) kp.Changed += _ => Dispatcher.BeginInvoke(new Action(SyncFromPlugin));

            // Wire les événements UI.
            cboPattern.SelectionChanged += (s, e) => SetParam("pattern", cboPattern.SelectedIndex);
            cboRate.SelectionChanged += (s, e) => SetParam("rate", cboRate.SelectedIndex);
            sldOctaves.ValueChanged += (s, e) => { SetParam("octaves", sldOctaves.Value); lblOctaves.Text = ((int)sldOctaves.Value).ToString(); };
            sldGate.ValueChanged += (s, e) => { SetParam("gate", sldGate.Value / 100.0); lblGate.Text = ((int)sldGate.Value).ToString(); };
            sldVelocity.ValueChanged += (s, e) => { SetParam("velocity", sldVelocity.Value); lblVelocity.Text = ((int)sldVelocity.Value).ToString(); };
            sldBase.ValueChanged += (s, e) => { SetParam("base_midi", sldBase.Value); lblBase.Text = ((int)sldBase.Value).ToString(); };
            sldDuration.ValueChanged += (s, e) =>
            {
                if (_updating) return;
                _plugin.DurationBeats = sldDuration.Value;
                lblDuration.Text = sldDuration.Value.ToString("0.##", CultureInfo.InvariantCulture);
            };
        }

        void SetParam(string id, double value)
        {
            if (_updating) return;
            for (int i = 0; i < _plugin.Parameters.Count; i++)
                if (_plugin.Parameters[i].Id == id)
                {
                    _plugin.Parameters[i].Value = value;
                    return;
                }
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
                int pat = (int)GetV("pattern");
                int rate = (int)GetV("rate");
                cboPattern.SelectedIndex = Math.Max(0, Math.Min(Arpeggiator.PatternNames.Length - 1, pat));
                cboRate.SelectedIndex = Math.Max(0, Math.Min(Arpeggiator.RateNames.Length - 1, rate));

                sldOctaves.Value = GetV("octaves");
                lblOctaves.Text = ((int)sldOctaves.Value).ToString();

                sldGate.Value = GetV("gate") * 100.0;
                lblGate.Text = ((int)sldGate.Value).ToString();

                sldVelocity.Value = GetV("velocity");
                lblVelocity.Text = ((int)sldVelocity.Value).ToString();

                sldBase.Value = GetV("base_midi");
                lblBase.Text = ((int)sldBase.Value).ToString();

                sldDuration.Value = _plugin.DurationBeats;
                lblDuration.Text = _plugin.DurationBeats.ToString("0.##", CultureInfo.InvariantCulture);
            }
            finally { _updating = false; }
        }
    }
}
