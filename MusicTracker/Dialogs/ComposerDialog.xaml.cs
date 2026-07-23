using System.Windows;
using MusicTracker.Engine.Timeline;

namespace MusicTracker.Dialogs
{
    /// <summary>Pick a compositional style + number of measures for the auto-composer.</summary>
    public partial class ComposerDialog : Window
    {
        public int StyleIndex { get; private set; }
        public int Measures { get; private set; } = 8;
        public int MelodyVoices { get; private set; } = 1;
        public int RhythmProfile { get; private set; } = -1; // -1 Auto, 1 balanced, 2 stately, 3 florid
        public int Breathing { get; private set; } = -1;     // -1 Auto, 0 none, 1 light, 2 marked
        public int Virtuosity { get; private set; } = -1;    // -1 Auto, 0 none, 1 light, 2 medium, 3 high (32nd-run density)
        public int Form { get; private set; }                // 0 Libre, 1 Sonate, 2 Rondeau, 3 Thème+variations, 4 Fugue, 5 Contrepoint
        public int Mode { get; private set; }                // 0 Auto, 1 Majeur, 2 Lydien, 3 Mineur, 4 Éolien, 5 Dorien (Hisaishi scale)

        // Index → Composer profile id. Auto (0) lets the style decide.
        static readonly int[] RhythmIds = { -1, 1, 2, 3 };
        static readonly int[] BreathIds = { -1, 0, 1, 2 };
        static readonly int[] VirtuosityIds = { -1, 0, 1, 2, 3 };

        public ComposerDialog()
        {
            InitializeComponent();
            foreach (var s in Composer.StyleNames) cboStyle.Items.Add(s);
            cboStyle.SelectionChanged += cboStyle_SelectionChanged;
            cboStyle.SelectedIndex = 0;
            for (int v = 1; v <= 4; v++) cboVoices.Items.Add(v);
            cboVoices.SelectedIndex = 0; // 1 voice
            foreach (var r in new[] { "Auto (selon le style)", "Équilibré (clavier bien tempéré)", "Posé (art de la fugue)", "Virtuose (variations Goldberg)" }) cboRhythm.Items.Add(r);
            cboRhythm.SelectedIndex = 0; // Auto
            foreach (var br in new[] { "Auto", "Aucune (legato)", "Légère (détaché + respirations)", "Marquée" }) cboBreath.Items.Add(br);
            cboBreath.SelectedIndex = 0; // Auto
            foreach (var vt in new[] { "Auto (selon le style)", "Aucune (pas de 32e)", "Légère", "Moyenne", "Élevée (32e brillantes)" }) cboVirtuosity.Items.Add(vt);
            cboVirtuosity.SelectedIndex = 0; // Auto
            foreach (var fm in Composer.FormNames) cboForm.Items.Add(fm);
            cboForm.SelectedIndex = 0; // Libre (the form, when ≠ Libre, drives the bar count/key plan)
            foreach (var md in Composer.ModeNames) cboMode.Items.Add(md);
            cboMode.SelectedIndex = 0; // Auto (only used by the Hisaishi / Ghibli style)
        }

        // Some styles sing best with several voices: Bach (imitative counterpoint) → 3; Ballade/film (doubled
        // layers) → 2. Bumped only if the user hasn't already chosen more.
        private void cboStyle_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (cboStyle.SelectedIndex == 1 && cboVoices.SelectedIndex < 2) cboVoices.SelectedIndex = 2;      // Bach → 3 voices
            else if (cboStyle.SelectedIndex == 7 && cboVoices.SelectedIndex < 1) cboVoices.SelectedIndex = 1; // Ballade → 2 voices
        }

        private void btnOk_Click(object sender, RoutedEventArgs e)
        {
            StyleIndex = System.Math.Max(0, cboStyle.SelectedIndex);
            Measures = int.TryParse(txtMeasures.Text, out int m) ? System.Math.Max(2, m) : 8;
            MelodyVoices = System.Math.Max(1, cboVoices.SelectedIndex + 1);
            int ri = System.Math.Max(0, System.Math.Min(RhythmIds.Length - 1, cboRhythm.SelectedIndex));
            RhythmProfile = RhythmIds[ri];
            int bi = System.Math.Max(0, System.Math.Min(BreathIds.Length - 1, cboBreath.SelectedIndex));
            Breathing = BreathIds[bi];
            int vi = System.Math.Max(0, System.Math.Min(VirtuosityIds.Length - 1, cboVirtuosity.SelectedIndex));
            Virtuosity = VirtuosityIds[vi];
            Form = System.Math.Max(0, cboForm.SelectedIndex);
            Mode = System.Math.Max(0, cboMode.SelectedIndex);
            DialogResult = true;
        }

        private void btnCancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    }
}
