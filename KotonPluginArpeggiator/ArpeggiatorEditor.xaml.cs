using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using KotonStudio.Library;

namespace KotonPluginArpeggiator
{
    /// <summary>
    /// Éditeur WPF de l'<see cref="Arpeggiator"/>. Deux-way binding manuel (on modifie directement les
    /// <c>KotonParameter</c> du plugin ; les sliders posent leur valeur à ValueChanged, l'éditeur
    /// souscrit à <see cref="KotonParameter.Changed"/> pour se mettre à jour si un autre appelant
    /// écrase les params (ex. LoadState d'un blob restauré).
    ///
    /// **Contexte-conscience** : implémente <see cref="IKotonEditor"/> — l'hôte notifie
    /// <see cref="OnContextUpdated"/> quand la métrique/tonalité/tempo change ; l'éditeur reconstruit
    /// le combo "notes/temps" (binaire → 1,2,3,4 ; ternaire → 1,3,6).
    ///
    /// Pas de bouton Preview ici : la barre Preview/Stop est fournie par l'hôte qui entoure ce
    /// UserControl (KotonHost.PreviewNotes).
    /// </summary>
    public partial class ArpeggiatorEditor : UserControl, IKotonEditor
    {
        readonly Arpeggiator _plugin;
        bool _updating;   // évite les boucles binding (setter param → Changed → setter UI → setter param)
        int[] _rateOptions;  // valeurs "notes par temps" affichées dans cboRate (dépend de la métrique)
        RhythmGridEditor _rhythmEditor;  // instancié à la 1re bascule vers "Personnalisé"

        public ArpeggiatorEditor(Arpeggiator plugin)
        {
            InitializeComponent();
            _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));

            // Peuple les combos statiques.
            foreach (var n in Arpeggiator.PatternNames) cboPattern.Items.Add(n);
            foreach (var n in Arpeggiator.ArticulationNames) cboArticulation.Items.Add(n);
            cboRhythmMode.Items.Add("Régulier");
            cboRhythmMode.Items.Add("Personnalisé");

            // Peuple le combo "notes par temps" selon la métrique actuelle (via KotonHost).
            RebuildRateOptions();

            // Charge l'état initial depuis les paramètres du plugin.
            SyncFromPlugin();

            // Ecoute les changements côté plugin (LoadState / autre appelant qui écraserait).
            var p = _plugin.Parameters;
            foreach (var kp in p) kp.Changed += _ => Dispatcher.BeginInvoke(new Action(SyncFromPlugin));

            // Wire les événements UI.
            cboPattern.SelectionChanged      += (s, e) => SetParam("pattern", cboPattern.SelectedIndex);
            cboRate.SelectionChanged         += (s, e) => { if (_updating) return; if (cboRate.SelectedIndex >= 0 && cboRate.SelectedIndex < _rateOptions.Length) SetParam("notes_per_beat", _rateOptions[cboRate.SelectedIndex]); };
            cboArticulation.SelectionChanged += (s, e) => SetParam("articulation", cboArticulation.SelectedIndex);
            sldExtend.ValueChanged           += (s, e) => { SetParam("extend", sldExtend.Value); lblExtend.Text = ((int)sldExtend.Value).ToString(); };
            sldVelocity.ValueChanged         += (s, e) => { SetParam("velocity", sldVelocity.Value); lblVelocity.Text = ((int)sldVelocity.Value).ToString(); };
            sldOctave.ValueChanged           += (s, e) => { SetParam("octave", sldOctave.Value); lblOctave.Text = ((int)sldOctave.Value).ToString(); };
            sldDuration.ValueChanged += (s, e) =>
            {
                if (_updating) return;
                _plugin.DurationBeats = sldDuration.Value;
                lblDuration.Text = sldDuration.Value.ToString("0.##", CultureInfo.InvariantCulture);
                // Notifier l'hôte pour qu'il propage la nouvelle durée au module timeline (source
                // de vérité pour la longueur du bloc) et re-render la vignette. Sans ça,
                // EnsureInstance re-écrase _plugin.DurationBeats par module.DurationBeats (défaut 4)
                // à la prochaine instanciation → la durée sauvegardée est perdue à l'audition.
                try { KotonHost.NotifyDurationChanged?.Invoke(sldDuration.Value); } catch { }
            };
            chkVoiceLeading.Checked   += (s, e) => SetParam("voice_leading", 1);
            chkVoiceLeading.Unchecked += (s, e) => SetParam("voice_leading", 0);
            sldSpread.ValueChanged    += (s, e) => { SetParam("spread", sldSpread.Value); lblSpread.Text = ((int)sldSpread.Value).ToString(); };
            cboRhythmMode.SelectionChanged += (s, e) => { SetParam("rhythm_mode", cboRhythmMode.SelectedIndex); UpdateRhythmVisibility(); };
        }

        void UpdateRhythmVisibility()
        {
            bool custom = cboRhythmMode.SelectedIndex == 1;
            rhythmGridHost.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
            cboRate.IsEnabled = !custom;   // notes/temps ignoré en mode Personnalisé
            if (custom)
            {
                if (_rhythmEditor == null)
                {
                    _rhythmEditor = new RhythmGridEditor();
                    _rhythmEditor.RhythmChanged += r => { _plugin.CustomRhythm = r; };
                    rhythmGridHost.Child = _rhythmEditor;
                }
                _rhythmEditor.Load(_plugin.CustomRhythm);
            }
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

        /// <summary>Reconstruit les items du combo "notes par temps" selon la métrique du projet.
        /// Binaire (4/4, 3/4, 2/4) : 1, 2, 3, 4. Ternaire (6/8, 9/8, 12/8) : 1, 3, 6. Appelé au
        /// démarrage et à chaque changement de métrique via <see cref="OnContextUpdated"/>.</summary>
        void RebuildRateOptions()
        {
            var ctx = KotonHost.CurrentContext?.Invoke();
            int tsNum = ctx?.TimeSigNum > 0 ? ctx.TimeSigNum : 4;
            int tsDen = ctx?.TimeSigDen > 0 ? ctx.TimeSigDen : 4;
            _rateOptions = Arpeggiator.IsTernary(tsNum, tsDen)
                ? Arpeggiator.TernaryNotesPerBeat
                : Arpeggiator.BinaryNotesPerBeat;

            _updating = true;
            try
            {
                cboRate.Items.Clear();
                foreach (var v in _rateOptions) cboRate.Items.Add(v + " / temps");
            }
            finally { _updating = false; }
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
                cboPattern.SelectedIndex = Math.Max(0, Math.Min(Arpeggiator.PatternNames.Length - 1, pat));

                // Sélectionne l'option de rate correspondant à la valeur du plugin (ou la plus proche
                // parmi les options disponibles pour la métrique courante).
                int nb = (int)GetV("notes_per_beat");
                int rateIdx = 0, rateBestDist = int.MaxValue;
                for (int i = 0; i < _rateOptions.Length; i++)
                {
                    int d = Math.Abs(_rateOptions[i] - nb);
                    if (d < rateBestDist) { rateBestDist = d; rateIdx = i; }
                }
                cboRate.SelectedIndex = rateIdx;

                int art = (int)GetV("articulation");
                cboArticulation.SelectedIndex = Math.Max(0, Math.Min(Arpeggiator.ArticulationNames.Length - 1, art));

                sldExtend.Value = GetV("extend");
                lblExtend.Text = ((int)sldExtend.Value).ToString();

                sldVelocity.Value = GetV("velocity");
                lblVelocity.Text = ((int)sldVelocity.Value).ToString();

                sldOctave.Value = GetV("octave");
                lblOctave.Text = ((int)sldOctave.Value).ToString();

                sldDuration.Value = _plugin.DurationBeats;
                lblDuration.Text = _plugin.DurationBeats.ToString("0.##", CultureInfo.InvariantCulture);

                chkVoiceLeading.IsChecked = GetV("voice_leading") >= 0.5;

                sldSpread.Value = GetV("spread");
                lblSpread.Text = ((int)sldSpread.Value).ToString();

                int mode = (int)GetV("rhythm_mode");
                cboRhythmMode.SelectedIndex = mode >= 1 ? 1 : 0;
                // Ne pas ré-updater visibilité ici (le SelectionChanged du combo va le faire) — sauf
                // au 1er sync où le combo n'a pas encore d'index. On garantit une passe :
                UpdateRhythmVisibility();
            }
            finally { _updating = false; }
        }

        // ---- IKotonEditor ----

        /// <summary>Appelé par l'hôte quand le contexte du projet change (métrique, tonalité, tempo).
        /// Reconstruit les options du combo "notes par temps" (binaire ↔ ternaire) et re-sélectionne
        /// la valeur courante ou la plus proche.</summary>
        public void OnContextUpdated(KotonRenderContext ctx)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                RebuildRateOptions();
                SyncFromPlugin();
            }));
        }
    }
}
