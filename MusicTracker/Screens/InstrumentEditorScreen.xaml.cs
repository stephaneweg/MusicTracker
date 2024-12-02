using Microsoft.Win32;
using MusicTracker.Controls;
using MusicTracker.Engine;
using NAudio.Wave;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
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

namespace MusicTracker.Screens
{
    /// <summary>
    /// Interaction logic for InstrumentEditorScreen.xaml
    /// </summary>
    public partial class InstrumentEditorScreen : Window
    {

        bool playing;



        Editor.EditorControl editor = new Editor.EditorControl();
        WaveOut waveOut = new WaveOut();
        SingleWaveProvider waveProvider = new SingleWaveProvider(null);
        public Engine.WaveFunction WaveFunction
        {
            get { return waveProvider.WaveFunction; }
            set
            {
                if (waveProvider.WaveFunction != null)
                {
                    waveProvider.WaveFunction.PropertyChanged -= WaveFunction_PropertyChanged;
                }
                waveProvider.WaveFunction = value;
                if (value != null)
                {
                    waveProvider.WaveFunction.Reset();
                    waveProvider.WaveFunction.PropertyChanged += WaveFunction_PropertyChanged;
                }
            }
        }



        void WaveFunction_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            waveVisual.RedrawWave();
        }

        public InstrumentEditorScreen()
        {
            waveProvider.Frequency = 440;
            waveOut.Init(waveProvider);

            InitializeComponent(); editor = new Editor.EditorControl();
            editor.GridRoot = gridRoot;

            Editor.EditorControl.Instance = editor;


            headerPressets.ItemsSource = UserData.Instance.InstrumentList;
            endNode.SetPrev = (o) =>
            {
                if (o != null)
                {
                    Editor.BaseWaveFunctionControl ctrl = o as Editor.BaseWaveFunctionControl;
                    SetWaveFunction(ctrl.NodeObject as Engine.WaveFunction);
                }
                else
                {
                    SetWaveFunction(null);
                }
                waveVisual.RedrawWave();
            };
        }

        public void SetWaveFunction(Engine.WaveFunction waveFunction)
        {

            this.WaveFunction = waveFunction;
            waveVisual.WaveFunction = waveFunction;


        }

        private void GridRoot_MouseMove(object sender, MouseEventArgs e)
        {
            if (Editor.EditorControl.AddingLink != null)
            {
                Editor.EditorControl.AddingLink.EndPoint = e.GetPosition(gridRoot);
            }

            if (draggingOutput)
            {
                ctrlOutput_MouseMove(sender, e);
            }
            else if (Editor.EditorControl.Instance.MovingControl != null)
            {
                Editor.EditorControl.Instance.MovingControl.Control_MouseMove(sender, e);
            }
            e.Handled = true;
        }

        private void AddItem_Click(object sender, RoutedEventArgs e)
        {
            Editor.NodeControl control = null;
            if (sender == addWaveFunction_Sinus)
                control = Editor.BaseWaveFunctionControl.CreateNew<Engine.SineWaveFunction>();
            else if (sender == addWaveFunction_Square)
                control = Editor.BaseWaveFunctionControl.CreateNew<Engine.SquareWaveFunction>();
            else if (sender == addWaveFunction_Triangle)
                control = Editor.BaseWaveFunctionControl.CreateNew<Engine.TriangleWaveFunction>();
            else if (sender == addWaveFunction_SawTooth)
                control = Editor.BaseWaveFunctionControl.CreateNew<Engine.SawtoothWaveFunction>();
            else if (sender == addWaveFunction_Noise)
                control = Editor.BaseWaveFunctionControl.CreateNew<Engine.NoiseWaveFunction>();
            else if (sender == addWaveFunction_FrequencyModulator)
                control = Editor.BaseWaveFunctionControl.CreateNew<Engine.FrequencyModulationWaveFunction>();
            else if (sender == addWaveFunction_FrequencyModifier)
                control = Editor.BaseWaveFunctionControl.CreateNew<Engine.FrequencyModifierWaveFunction>();
            else if (sender == addWaveFunction_EnveloppeADSR)
                control = Editor.BaseWaveFunctionControl.CreateNew<Engine.EnveloppeADSRWaveFunction>();
            else if (sender == addWaveFunction_Add)
                control = Editor.BaseWaveFunctionControl.CreateNew<Engine.AddWaveFunction>();
            else if (sender == addWaveFunction_Mul)
                control = Editor.BaseWaveFunctionControl.CreateNew<Engine.MultiplyWaveFunction>();
            else if (sender == addWaveFunction_Vibrato)
                control = Editor.BaseWaveFunctionControl.CreateNew<Engine.VibratoWaveFunction>();
            else if (sender == addWaveFunction_AudioPatch)
                control = Editor.BaseWaveFunctionControl.CreateNew<Engine.AudioPatchWaveFunction>();
            editor.AddControl(control);


        }


        bool draggingOutput;
        double startXOutput;
        double startYOutput;





        public void ctrlOutput_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (!draggingOutput)
            {
                draggingOutput = true;
                var pt = e.GetPosition(gridRoot);
                startXOutput = pt.X - endControl.Margin.Left;
                startYOutput = pt.Y - endControl.Margin.Top;
                var z = gridRoot.Children.OfType<FrameworkElement>().Max(i => Panel.GetZIndex(i) + 1);
                Panel.SetZIndex(endControl, z);
            }
        }
        public void ctrlOutput_MouseUp(object sender, MouseButtonEventArgs e)
        {
            draggingOutput = false;
        }

        public void ctrlOutput_MouseMove(object sender, MouseEventArgs e)
        {
            if (draggingOutput)
            {
                var pt = e.GetPosition(gridRoot);

                var l = pt.X - startXOutput;
                var t = pt.Y - startYOutput;
                endControl.Margin = new Thickness(l, t, 0, 0);

                foreach (var link in gridRoot.Children.OfType<NodeLink>())
                {
                    link.NodeStart = link.NodeStart;
                    link.NodeEnd = link.NodeEnd;
                }

                e.Handled = true;
            }
        }

        private void scrollDetails_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            foreach (var item in endNode.NodeLinks.ToList())
            {
                item.NodeStart = item.NodeStart;
            }
        }

        private void UserControl_Loaded(object sender, RoutedEventArgs e)
        {
            // endControl.Margin = new Thickness(gridRoot.ActualWidth - endControl.ActualWidth - 10, (gridRoot.ActualHeight - endControl.ActualHeight) / 2, 0, 0);
            if (this.WaveFunction != null)
            {

                endControl.Margin = new Thickness(this.WaveFunction.maxLevels(2) * 250, (gridRoot.ActualHeight - endControl.ActualHeight) / 2, 0, 0);


                gridRoot.Children.OfType<Editor.BaseWaveFunctionControl>().ToList().ForEach(c => gridRoot.Children.Remove(c));
                gridRoot.Children.OfType<NodeLink>().ToList().ForEach(NodeLink => gridRoot.Children.Remove(NodeLink));

                Editor.NodeControl ctrl = Editor.BaseWaveFunctionControl.Create(this.WaveFunction);

                double positionX = endControl.Margin.Left - 250;
                double positionY = endControl.Margin.Top;
                Editor.EditorControl.Instance.AddControl(ctrl, positionX, positionY);

                ctrl.LinkOutputToNext(gridRoot, endNode);
                ctrl.AddPrevControls(gridRoot, 1);
            }
        }

        private void menuSave_Click(object sender, RoutedEventArgs e)
        {
            string str = System.Text.Json.JsonSerializer.Serialize(WaveFunction);
            WaveFunction wv = System.Text.Json.JsonSerializer.Deserialize<Engine.WaveFunction>(str);
            Debug.WriteLine($"deserialized {wv.GetType().Name}");
        }

        private void menuPlay_Click(object sender, RoutedEventArgs e)
        {
            playing = !playing;

            menuPlay.Header = playing ? "Stop" : "Play";
            if (playing)
            {
                waveProvider.WaveFunction.Reset();
                waveOut.Play();
            }
            else
            {
                waveOut.Stop();
            }

        }

        private void gridRoot_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            var w = scrollDetails.ActualWidth * 2;
            var h = scrollDetails.ActualHeight * 2;
            brushBG.Viewport = new Rect(0, 0,
            brushBG.ImageSource.Width / w,
            brushBG.ImageSource.Height / h);
        }

        private void Window_Closed(object sender, EventArgs e)
        {
            playing = false;
            waveOut.Stop();
        }

        private void headerPressets_Click(object sender, RoutedEventArgs e)
        {
            editor.GridRoot.Children.OfType<Editor.BaseWaveFunctionControl>().ToList().ForEach(control => control.DoRemove());
            Editor.Instrument instrument =( (e.OriginalSource as MenuItem).DataContext as Editor.Instrument);
            SetWaveFunction(instrument.WaveFunction);
            UserControl_Loaded(null, null);
        }

        private void menuExport_Click(object sender, RoutedEventArgs e)
        {
            InstrumentSelect select = new InstrumentSelect();
            if (select.ShowDialog() == true)
            {
                select.SelectedInstrument.WaveFunction = WaveFunction.Clone();
                UserData.Instance.Save();
            }
        }
    }
}
