using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using MusicTracker.Engine;

namespace MusicTracker.Dialogs
{
    /// <summary>Per-instrument volume boost editor: a slider (−10..+10) per GM instrument (+ drum kit) that scales
    /// its MeltySynth playback level. −10 = ÷<see cref="AppSettings.MaxBoostFactor"/>, 0 = ×1, +10 = ×factor.</summary>
    public partial class InstrumentBoostDialog : Window
    {
        public sealed class BoostRow : INotifyPropertyChanged
        {
            public int Program { get; set; }
            public string Name { get; set; }
            public string ProgramLabel => Program >= 128 ? "" : Program.ToString();

            int slider;
            public int Slider
            {
                get => slider;
                set { if (slider != value) { slider = value; OnChanged(nameof(Slider)); OnChanged(nameof(FactorText)); } }
            }

            public string FactorText
            {
                get
                {
                    if (slider == 0) return "×1";
                    double f = Math.Pow(AppSettings.MaxBoostFactor, Math.Abs(slider) / 10.0);
                    return (slider > 0 ? "×" : "÷") + f.ToString("0.0");
                }
            }

            public event PropertyChangedEventHandler PropertyChanged;
            void OnChanged(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
        }

        readonly ObservableCollection<BoostRow> rows = new ObservableCollection<BoostRow>();

        public InstrumentBoostDialog()
        {
            InitializeComponent();

            var boosts = AppSettings.Instance.InstrumentBoost ?? new Dictionary<int, int>();
            var names = InstrumentCatalog.Names();
            for (int p = 0; p < names.Count; p++)
                rows.Add(new BoostRow { Program = p, Name = names[p], Slider = boosts.TryGetValue(p, out int s) ? s : 0 });
            // Drum kit (program 128 in the app's convention).
            rows.Add(new BoostRow { Program = InstrumentCatalog.DrumIndex, Name = "🥁 Batterie", Slider = boosts.TryGetValue(InstrumentCatalog.DrumIndex, out int sd) ? sd : 0 });

            list.ItemsSource = rows;
        }

        void btnReset_Click(object sender, RoutedEventArgs e)
        {
            foreach (var r in rows) r.Slider = 0;
        }

        void btnCancel_Click(object sender, RoutedEventArgs e) { DialogResult = false; Close(); }

        void btnOK_Click(object sender, RoutedEventArgs e)
        {
            var dict = new Dictionary<int, int>();
            foreach (var r in rows) if (r.Slider != 0) dict[r.Program] = r.Slider;
            AppSettings.Instance.InstrumentBoost = dict;
            AppSettings.Instance.Save();
            DialogResult = true;
            Close();
        }
    }
}
