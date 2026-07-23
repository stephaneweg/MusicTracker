using Microsoft.Win32;
using System;
using System.IO;
using System.Threading;

namespace MusicTracker.Engine
{
    public class WaveExporter
    {
        public static void Export(int NoteCount, IWaveProvider waveProvider)
        {
            var dlg = new Dialogs.FileBrowserDialog
            {
                SaveMode = true,
                Owner = System.Windows.Application.Current?.MainWindow,
                DefaultExt = ".wav",
                Filter = "WAVE files (*.wav)|*.wav|MP3 files (*.mp3)|*.mp3|All files (*.*)|*.*",
            };
            if (dlg.ShowDialog() != true) return;

            int sampleRate = AudioFormat.SampleRate;
            double secondsPerSlice = (60.0 / (waveProvider.BPM * waveProvider.SpeedFactor)) / 4.0;
            long count = (long)(NoteCount * secondsPerSlice * sampleRate); // total samples (long: no overflow)
            if (count <= 0)
            {
                System.Windows.MessageBox.Show("Rien à exporter (musique vide).");
                return;
            }

            bool mp3 = string.Equals(Path.GetExtension(dlg.FileName), ".mp3", StringComparison.OrdinalIgnoreCase);

            var progressDlg = new Dialogs.ExportProgressDialog(dlg.FileName, count, mp3, waveProvider, sampleRate)
            {
                Owner = System.Windows.Application.Current?.MainWindow,
            };
            progressDlg.ShowDialog();

            if (!string.IsNullOrEmpty(progressDlg.Error))
                System.Windows.MessageBox.Show("Export error : " + progressDlg.Error);
            else if (progressDlg.Success)
                System.Windows.MessageBox.Show("Export terminé :\n" + dlg.FileName);
        }

        /// <summary>
        /// Renders <paramref name="count"/> mono samples from the provider into a stereo 16-bit
        /// WAV or MP3 file. Headless and testable. Returns the number of samples written.
        /// </summary>
        public static long Render(string path, long count, bool mp3, IWaveProvider waveProvider, int sampleRate = 0,
            IProgress<double> progress = null, CancellationToken token = default(CancellationToken))
        {
            if (sampleRate <= 0) sampleRate = AudioFormat.SampleRate; // 0 = use the current engine rate
            var format = new NAudio.Wave.WaveFormat(sampleRate, 16, 2); // 16-bit stereo

            const int chunk = 1 << 16;            // mono samples processed per pass
            short[] mono = new short[chunk];
            short[] stereo = new short[chunk * 2];
            byte[] bytes = mp3 ? new byte[chunk * 2 * sizeof(short)] : null;
            long written = 0;

            try
            {
                waveProvider.Start();

                NAudio.Wave.WaveFileWriter wav = null;
                NAudio.Lame.LameMP3FileWriter mp3w = null;
                IDisposable writer = mp3
                    ? (IDisposable)(mp3w = new NAudio.Lame.LameMP3FileWriter(path, format, 192))
                    : (wav = new NAudio.Wave.WaveFileWriter(path, format));

                long reportStep = Math.Max(1, count / 200); // report ~ every 0.5%
                long lastReport = 0;
                using (writer)
                {
                    long remaining = count;
                    while (remaining > 0)
                    {
                        if (token.IsCancellationRequested) break;

                        int n = (int)Math.Min(remaining, chunk);
                        int read = waveProvider.Read(mono, 0, n);
                        if (read <= 0) break; // provider produced nothing -> stop (no infinite loop)

                        for (int i = 0; i < read; i++) { stereo[2 * i] = mono[i]; stereo[2 * i + 1] = mono[i]; }

                        if (mp3)
                        {
                            Buffer.BlockCopy(stereo, 0, bytes, 0, read * 2 * sizeof(short));
                            mp3w.Write(bytes, 0, read * 2 * sizeof(short));
                        }
                        else
                        {
                            wav.WriteSamples(stereo, 0, read * 2);
                        }
                        remaining -= read;
                        written += read;

                        if (progress != null && written - lastReport >= reportStep)
                        {
                            lastReport = written;
                            progress.Report((double)written / count);
                        }
                    }
                } // writer disposal finalizes the file (WAV header sizes / MP3 flush)

                if (token.IsCancellationRequested)
                {
                    try { File.Delete(path); } catch { } // discard the partial file
                }
                else
                {
                    progress?.Report(1.0);
                }
            }
            finally
            {
                try { waveProvider.Stop(); } catch { }
            }
            return written;
        }

        /// <summary>
        /// Renders any sample-engine provider (a <see cref="NAudio.Wave.WaveProvider16"/> exposing a
        /// short Read, e.g. the TimelinePlayer) to a stereo WAV/MP3 until it ends (Read returns
        /// 0) or <paramref name="maxSamples"/> is reached. <paramref name="start"/>/<paramref name="stop"/>
        /// begin and tear down the provider's clock.
        /// </summary>
        public static long RenderProvider(string path, bool mp3, NAudio.Wave.WaveProvider16 provider, long maxSamples, int sampleRate,
            IProgress<double> progress, CancellationToken token, Action start, Action stop)
        {
            if (sampleRate <= 0) sampleRate = AudioFormat.SampleRate; // 0 = use the current engine rate
            var format = new NAudio.Wave.WaveFormat(sampleRate, 16, 2);
            const int chunk = 1 << 16;
            short[] mono = new short[chunk];
            short[] stereo = new short[chunk * 2];
            byte[] bytes = mp3 ? new byte[chunk * 2 * sizeof(short)] : null;
            long written = 0;

            try
            {
                start?.Invoke();

                NAudio.Wave.WaveFileWriter wav = null;
                NAudio.Lame.LameMP3FileWriter mp3w = null;
                IDisposable writer = mp3
                    ? (IDisposable)(mp3w = new NAudio.Lame.LameMP3FileWriter(path, format, 192))
                    : (wav = new NAudio.Wave.WaveFileWriter(path, format));

                long reportStep = Math.Max(1, maxSamples / 200);
                long lastReport = 0;
                using (writer)
                {
                    while (written < maxSamples)
                    {
                        if (token.IsCancellationRequested) break;

                        int n = (int)Math.Min(maxSamples - written, chunk);
                        int read = provider.Read(mono, 0, n);
                        if (read <= 0) break; // the provider finished

                        for (int i = 0; i < read; i++) { stereo[2 * i] = mono[i]; stereo[2 * i + 1] = mono[i]; }

                        if (mp3)
                        {
                            Buffer.BlockCopy(stereo, 0, bytes, 0, read * 2 * sizeof(short));
                            mp3w.Write(bytes, 0, read * 2 * sizeof(short));
                        }
                        else
                        {
                            wav.WriteSamples(stereo, 0, read * 2);
                        }
                        written += read;

                        if (progress != null && written - lastReport >= reportStep)
                        {
                            lastReport = written;
                            progress.Report(Math.Min(1.0, (double)written / maxSamples));
                        }
                    }
                }

                if (token.IsCancellationRequested) { try { File.Delete(path); } catch { } }
                else progress?.Report(1.0);
            }
            finally
            {
                try { stop?.Invoke(); } catch { }
            }
            return written;
        }
    }
}
