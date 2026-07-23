using System.Windows;
using MusicTracker.Engine.Score;

namespace MusicTracker.Dialogs
{
    /// <summary>Pick a target key (tonic + sharp/flat + mode) to transpose the piece to. Defaults to the
    /// current key. <see cref="Result"/> is the chosen key; the caller computes the nearest interval.</summary>
    public partial class TransposeDialog : Window
    {
        public KeySignature Result { get; private set; }
        /// <summary>Index into <see cref="MusicalMode"/> of the chosen target mode (full mode list).</summary>
        public int ResultMode { get; private set; }
        /// <summary>0 = nearest, 1 = up, 2 = down.</summary>
        public int ResultDirection { get; private set; }

        public TransposeDialog(KeySignature current)
        {
            InitializeComponent();
            foreach (var t in new[] { "Do", "Ré", "Mi", "Fa", "Sol", "La", "Si" }) cboTonic.Items.Add(t);
            foreach (var n in MusicalMode.Names) cboMode.Items.Add(n);

            var k = current ?? new KeySignature();
            cboTonic.SelectedIndex = System.Math.Max(0, System.Math.Min(6, k.TonicLetter));
            tglSharp.IsChecked = k.Accidental > 0;
            tglFlat.IsChecked = k.Accidental < 0;
            cboMode.SelectedIndex = MusicalMode.Effective(k); // remembers a previously-applied mode (e.g. dorien)
            tglNearest.IsChecked = true; // default direction
        }

        private void TglSharp_Click(object sender, RoutedEventArgs e) { if (tglSharp.IsChecked == true) tglFlat.IsChecked = false; }
        private void TglFlat_Click(object sender, RoutedEventArgs e) { if (tglFlat.IsChecked == true) tglSharp.IsChecked = false; }

        // The three direction toggles are mutually exclusive (radio-like): the clicked one stays on, others off.
        private void Dir_Click(object sender, RoutedEventArgs e)
        {
            tglNearest.IsChecked = sender == tglNearest;
            tglUp.IsChecked = sender == tglUp;
            tglDown.IsChecked = sender == tglDown;
        }

        private void btnOk_Click(object sender, RoutedEventArgs e)
        {
            ResultMode = System.Math.Max(0, cboMode.SelectedIndex);
            ResultDirection = tglUp.IsChecked == true ? 1 : tglDown.IsChecked == true ? 2 : 0;
            Result = new KeySignature
            {
                TonicLetter = System.Math.Max(0, cboTonic.SelectedIndex),
                Accidental = tglSharp.IsChecked == true ? 1 : tglFlat.IsChecked == true ? -1 : 0,
                Mode = MusicalMode.IsMinorish(ResultMode) ? 1 : 0, // nearest major/minor for the armure
            };
            DialogResult = true;
        }

        private void btnCancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    }
}
