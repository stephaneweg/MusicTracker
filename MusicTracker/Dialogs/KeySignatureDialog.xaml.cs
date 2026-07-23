using System.Windows;
using System.Windows.Controls;
using MusicTracker.Engine.Score;

namespace MusicTracker.Dialogs
{
    /// <summary>Confirm/correct the key signature AND the time signature deduced at import.</summary>
    public partial class KeySignatureDialog : Window
    {
        public KeySignature Result { get; private set; }
        public int ResultNum { get; private set; } = 4;
        public int ResultDen { get; private set; } = 4;

        public KeySignatureDialog(KeySignature detected, int num, int den, string meterHint)
        {
            InitializeComponent();
            foreach (var t in new[] { "Do", "Ré", "Mi", "Fa", "Sol", "La", "Si" }) cboTonic.Items.Add(t);
            cboMode.Items.Add("Majeur"); cboMode.Items.Add("Mineur");

            var k = detected ?? new KeySignature();
            cboTonic.SelectedIndex = System.Math.Max(0, System.Math.Min(6, k.TonicLetter));
            tglSharp.IsChecked = k.Accidental > 0;
            tglFlat.IsChecked = k.Accidental < 0;
            cboMode.SelectedIndex = k.Mode == 1 ? 1 : 0;

            txtNum.Text = System.Math.Max(1, num).ToString();
            SelectDen(den);
            if (!string.IsNullOrEmpty(meterHint)) { txtMeterHint.Text = meterHint; txtMeterHint.Visibility = Visibility.Visible; }
        }

        void SelectDen(int den)
        {
            foreach (ComboBoxItem it in cboDen.Items) if (it.Content?.ToString() == den.ToString()) { cboDen.SelectedItem = it; return; }
            cboDen.SelectedIndex = 1; // /4
        }

        private void TglSharp_Click(object sender, RoutedEventArgs e) { if (tglSharp.IsChecked == true) tglFlat.IsChecked = false; }
        private void TglFlat_Click(object sender, RoutedEventArgs e) { if (tglFlat.IsChecked == true) tglSharp.IsChecked = false; }

        private void btnOk_Click(object sender, RoutedEventArgs e)
        {
            Result = new KeySignature
            {
                TonicLetter = System.Math.Max(0, cboTonic.SelectedIndex),
                Accidental = tglSharp.IsChecked == true ? 1 : tglFlat.IsChecked == true ? -1 : 0,
                Mode = cboMode.SelectedIndex == 1 ? 1 : 0,
            };
            ResultNum = int.TryParse(txtNum.Text, out int n) && n > 0 ? n : 4;
            ResultDen = int.TryParse((cboDen.SelectedItem as ComboBoxItem)?.Content?.ToString(), out int d) && d > 0 ? d : 4;
            DialogResult = true;
        }

        private void btnCancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    }
}
