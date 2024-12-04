using MusicTracker.Controls;
using MusicTracker.Engine;
using MusicTracker.Screens;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Runtime.Remoting.Lifetime;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace MusicTracker
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
            leftAccordion.InitAccordion();
            MusicLoaded();
           
        }

        public void MusicLoaded()
        {
            listInstruments.ItemsSource = null;
            listInstruments.ItemsSource = UserData.Instance.InstrumentList;
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            /*
            editorScreen.SetWaveFunction(new Engine.FrequencyModulationWaveFunction
            {
                Carrier = new Engine.EnveloppeADSRWaveFunction
                {
                    Attack = 0.01,
                    Decay = 0.01,
                    Sustain = 0.5,
                    Release = 0.01,
                    WaveFunction = new Engine.SineWaveFunction()
                },
                Modulator = new Engine.EnveloppeADSRWaveFunction
                {
                    Attack = 0.01,
                    Decay = 0.01,
                    Sustain = 0.5,
                    Release = 0.01,
                    WaveFunction = new Engine.FrequencyModifierWaveFunction
                    {
                        FrequencyModifier = 1,
                        WaveFunction = new Engine.SineWaveFunction()
                    }
                }

            });
            */
        }

        private void btnNewTrack_Click(object sender, RoutedEventArgs e)
        {

        }

        private void btnRemoveTrack_Click(object sender, RoutedEventArgs e)
        {

        }

        private void btnTrack_Click(object sender, RoutedEventArgs e)
        {

        }

        private void btnNewInstrument_Click(object sender, RoutedEventArgs e)
        {
            Editor.Instrument instrument = new Editor.Instrument
            {
                ID = UserData.Instance.InstrumentList.Any() ? UserData.Instance.InstrumentList.Max(x => x.ID) + 1 : 1,
                Name = "New Instrument",
                WaveFunction = new Engine.SineWaveFunction()
            };
            UserData.Instance.InstrumentList.Add(instrument);

            InstrumentEditorScreen editor = new InstrumentEditorScreen();
            editor.SetWaveFunction(instrument.WaveFunction);
            editor.ShowDialog();
            instrument.WaveFunction = editor.WaveFunction;
            UserData.Instance.Save();

        }

        private void btnRemoveInstrument_Click(object sender, RoutedEventArgs e)
        {
            FrameworkElement frameworkElement = (FrameworkElement)sender;
            Editor.Instrument instrument = (Editor.Instrument)frameworkElement.DataContext;
            UserData.Instance.InstrumentList.Remove(instrument);
            UserData.Instance.Save();
        }

        private void btnInstrument_Click(object sender, RoutedEventArgs e)
        {
            FrameworkElement frameworkElement = (FrameworkElement)sender;
            Editor.Instrument instrument = (Editor.Instrument)frameworkElement.DataContext;

            InstrumentEditorScreen editor = new InstrumentEditorScreen();
            editor.SetWaveFunction(instrument.WaveFunction.Clone());
            editor.ShowDialog();
            instrument.WaveFunction = editor.WaveFunction.Clone();
            UserData.Instance.Save();
        }
    }
}
