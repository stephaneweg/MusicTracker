using System;
using System.Linq;
using System.Windows;

namespace MusicTracker.Dialogs
{
    /// <summary>
    /// Global settings: pick the SoundFont (.sf2) and the engine sample rate. Applying writes the
    /// settings and hot-reloads the SoundFont (see <see cref="AppSettings.Apply"/>), so a piece played
    /// right afterwards uses the new font/rate without restarting the app.
    /// </summary>
    public partial class SettingsDialog : Window
    {
        // Sentinel shown when the chosen font isn't in the folder (e.g. an absolute path / missing file).
        const string DefaultLabel = "(par défaut)";

        // Riff-editor entry snap precision (fraction of a beat; 0 = none).
        static readonly string[] SnapLabels = { "Aucune", "1/4", "1/8", "1/16", "1/3 (triolet)", "1/6" };
        static readonly double[] SnapFractions = { 0, 0.25, 0.125, 0.0625, 1.0 / 3, 1.0 / 6 };

        // Audio analysis window (MPM frame, samples). Smaller = lower latency, worse on low pitches.
        static readonly int[] FrameSizes = { 512, 1024, 2048, 4096 };

        public SettingsDialog()
        {
            InitializeComponent();

            var settings = AppSettings.Instance;

            // SoundFonts: the bundled .sf2 files, plus a "(par défaut)" entry for the engine default.
            var fonts = AppSettings.AvailableSoundFonts();
            cboSoundFont.Items.Add(DefaultLabel);
            foreach (var f in fonts) cboSoundFont.Items.Add(f);
            cboSoundFont.SelectedItem = !string.IsNullOrWhiteSpace(settings.SoundFont) && fonts.Contains(settings.SoundFont)
                ? (object)settings.SoundFont
                : DefaultLabel;

            // Sample rates: the standard set (label the engine default).
            foreach (var r in AppSettings.StandardSampleRates)
                cboSampleRate.Items.Add(r == Engine.AudioFormat.DefaultSampleRate ? r + " Hz (défaut)" : r + " Hz");
            int idx = Array.IndexOf(AppSettings.StandardSampleRates, settings.SampleRate);
            cboSampleRate.SelectedIndex = idx >= 0 ? idx : Array.IndexOf(AppSettings.StandardSampleRates, Engine.AudioFormat.DefaultSampleRate);

            txtRiffSpeed.Text = settings.RiffInputSpeed.ToString();

            foreach (var l in SnapLabels) cboRiffSnap.Items.Add(l);
            int si = 0; double best = double.MaxValue; // pick the closest preset to the stored fraction
            for (int i = 0; i < SnapFractions.Length; i++)
            {
                double d = Math.Abs(SnapFractions[i] - settings.RiffSnapFraction);
                if (d < best) { best = d; si = i; }
            }
            cboRiffSnap.SelectedIndex = si;

            chkAudioScaleSnap.IsChecked = settings.RiffAudioScaleSnap;
            sldOnset.Value = Math.Min(100, Math.Max(0, settings.RiffAudioOnsetSensitivity * 100));

            // Analysis window: label each frame size with its latency at the engine sample rate.
            int sr = settings.SampleRate > 0 ? settings.SampleRate : Engine.AudioFormat.DefaultSampleRate;
            int fi = 0; double fbest = double.MaxValue;
            for (int i = 0; i < FrameSizes.Length; i++)
            {
                int ms = (int)Math.Round(1000.0 * FrameSizes[i] / sr);
                string note = FrameSizes[i] == 512 ? " — violon/aigus, latence mini"
                            : FrameSizes[i] == 1024 ? " — défaut"
                            : FrameSizes[i] == 4096 ? " — graves" : " — équilibré";
                cboFrameSize.Items.Add($"{FrameSizes[i]} (~{ms} ms{note})");
                double d = Math.Abs(FrameSizes[i] - settings.RiffAudioFrameSize);
                if (d < fbest) { fbest = d; fi = i; }
            }
            cboFrameSize.SelectedIndex = fi;
        }

        private void btnOk_Click(object sender, RoutedEventArgs e)
        {
            var settings = AppSettings.Instance;

            string font = cboSoundFont.SelectedItem as string;
            settings.SoundFont = (font == null || font == DefaultLabel) ? "" : font;

            if (cboSampleRate.SelectedIndex >= 0 && cboSampleRate.SelectedIndex < AppSettings.StandardSampleRates.Length)
                settings.SampleRate = AppSettings.StandardSampleRates[cboSampleRate.SelectedIndex];

            if (double.TryParse(txtRiffSpeed.Text, out double rs) && rs >= 0.25 && rs <= 50)
                settings.RiffInputSpeed = rs;

            if (cboRiffSnap.SelectedIndex >= 0 && cboRiffSnap.SelectedIndex < SnapFractions.Length)
                settings.RiffSnapFraction = SnapFractions[cboRiffSnap.SelectedIndex];

            settings.RiffAudioScaleSnap = chkAudioScaleSnap.IsChecked == true;
            settings.RiffAudioOnsetSensitivity = sldOnset.Value / 100.0;

            if (cboFrameSize.SelectedIndex >= 0 && cboFrameSize.SelectedIndex < FrameSizes.Length)
                settings.RiffAudioFrameSize = FrameSizes[cboFrameSize.SelectedIndex];

            settings.Save();

            try { settings.Apply(); } // hot-reload the SoundFont at the (possibly new) sample rate
            catch (Exception ex) { MessageBox.Show("Erreur lors du rechargement du SoundFont : " + ex.Message); }

            DialogResult = true;
        }

        private void btnApiKeys_Click(object sender, RoutedEventArgs e)
        {
            new ApiKeysDialog { Owner = this }.ShowDialog();
        }

        private void btnBoost_Click(object sender, RoutedEventArgs e)
        {
            new InstrumentBoostDialog { Owner = this }.ShowDialog();
        }

        private void btnCancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    }
}

