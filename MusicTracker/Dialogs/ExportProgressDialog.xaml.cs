using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace MusicTracker.Dialogs
{
    /// <summary>
    /// Themed modal dialog that runs an audio export on a background thread while showing a
    /// progress bar. Exposes Success / Error / Cancelled once closed.
    /// </summary>
    public partial class ExportProgressDialog : Window
    {
        readonly Action<IProgress<double>, CancellationToken> work;
        readonly CancellationTokenSource cts = new CancellationTokenSource();

        public bool Success { get; private set; }
        public bool Cancelled { get; private set; }
        public string Error { get; private set; }

        /// <summary>Generic: runs any export work that reports 0..1 progress and honours cancellation.</summary>
        public ExportProgressDialog(Action<IProgress<double>, CancellationToken> work)
        {
            InitializeComponent();
            this.work = work;
            Loaded += OnLoaded;
        }

        /// <summary>Convenience overload for the sequencer's IWaveProvider (fixed sample count).</summary>
        public ExportProgressDialog(string path, long count, bool mp3, Engine.IWaveProvider provider, int sampleRate)
            : this((progress, token) => Engine.WaveExporter.Render(path, count, mp3, provider, sampleRate, progress, token))
        {
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            // Progress<T> created on the UI thread -> its callback is marshalled back to the UI thread.
            var progress = new Progress<double>(p =>
            {
                bar.Value = p;
                txtPercent.Text = (int)(p * 100) + " %";
            });
            var token = cts.Token;

            Task.Run(() => work(progress, token))
                .ContinueWith(t =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        if (t.IsFaulted) Error = t.Exception?.GetBaseException().Message;
                        else if (token.IsCancellationRequested) Cancelled = true;
                        else Success = true;
                        DialogResult = Success; // closes the dialog
                    });
                });
        }

        private void btnCancel_Click(object sender, RoutedEventArgs e)
        {
            cts.Cancel();
            btnCancel.IsEnabled = false;
            txtPercent.Text = "Annulation…";
        }
    }
}

