using System.Windows;
using MusicTracker.Engine.Flow;

namespace MusicTracker.Dialogs
{
    /// <summary>Options for generating a chord cadence in the project key. The screen reads the result
    /// properties, builds the progression with <see cref="MusicTheory.Cadence"/> + voice-leading, and
    /// inserts the chords into the selected instrument track.</summary>
    public partial class CadenceDialog : Window
    {
        static readonly string[] DegreeNames = { "I (tonique)", "ii", "iii", "IV", "V", "vi", "vii" };

        public int Measures { get; private set; }
        public int ChordsPerMeasure { get; private set; }
        public int StartDegree { get; private set; }
        public int StyleIndex { get; private set; } // index into MusicTheory.CadenceStyles (not WPF's Style)
        public int RhythmStyle { get; private set; } // index into PatternGenerator.StyleNames (how each chord is played)
        public bool Bass { get; private set; }
        public bool VoiceLead { get; private set; }
        public bool OpenVoicing { get; private set; }

        public CadenceDialog(int startDegree = 0, bool bass = false, int rhythmStyle = -1)
        {
            InitializeComponent();
            foreach (var d in DegreeNames) cboDegree.Items.Add(d);
            foreach (var s in MusicTheory.CadenceStyles) cboStyle.Items.Add(s);
            // Rhythm/articulation: "Auto" (index 0 → -1, derived from the cadence style) then all built-ins EXCEPT
            // the last ("Personnalisé…", which needs a hand-drawn grid).
            cboRhythm.Items.Add("Auto (selon le style de cadence)");
            for (int i = 0; i < PatternGenerator.CustomStyle; i++) cboRhythm.Items.Add(PatternGenerator.StyleNames[i]);
            cboRhythm.Items.Add("Personnalisé (dessiner le motif)…"); // last → RhythmStyle == CustomStyle, edited after OK
            cboDegree.SelectedIndex = System.Math.Max(0, System.Math.Min(6, startDegree));
            cboStyle.SelectedIndex = 0;
            cboRhythm.SelectedIndex = rhythmStyle < 0 ? 0 : System.Math.Min(PatternGenerator.CustomStyle, rhythmStyle + 1);
            chkBass.IsChecked = bass;
        }

        private void btnOk_Click(object sender, RoutedEventArgs e)
        {
            Measures = int.TryParse(txtMeasures.Text, out int m) ? System.Math.Max(1, m) : 4;
            ChordsPerMeasure = int.TryParse(txtChordsPerMeasure.Text, out int c) ? System.Math.Max(1, c) : 1;
            StartDegree = System.Math.Max(0, cboDegree.SelectedIndex);
            StyleIndex = System.Math.Max(0, cboStyle.SelectedIndex);
            RhythmStyle = cboRhythm.SelectedIndex <= 0 ? -1 : cboRhythm.SelectedIndex - 1; // -1 = Auto
            Bass = chkBass.IsChecked == true;
            VoiceLead = chkVoiceLead.IsChecked == true;
            OpenVoicing = chkOpen.IsChecked == true;
            DialogResult = true;
        }

        private void btnCancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    }
}
