using System;
using System.Collections.Generic;
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

namespace MusicTracker.Controls
{
    /// <summary>
    /// Interaction logic for WaveFunctionVisual.xaml
    /// </summary>
    public partial class WaveFunctionVisual : UserControl
    {
        
        public static readonly DependencyProperty WaveFunctionProperty =
            DependencyProperty.Register(
                "WaveFunction", 
                typeof(Engine.WaveFunction), 
                typeof(WaveFunctionVisual), 
                new PropertyMetadata(null, OnWaveFunctionChanged));

        public static readonly DependencyProperty FrequencyProperty = 
            DependencyProperty.Register(
                "Frequency", 
                typeof(double), 
                typeof(WaveFunctionVisual), 
                new PropertyMetadata(10d, OnFrequencyChanged));
        
        public Engine.WaveFunction WaveFunction
        {
            get => (Engine.WaveFunction)GetValue(WaveFunctionProperty);
            set => SetValue(WaveFunctionProperty, value);
        }

        public double Frequency
        {
            get => (double)GetValue(FrequencyProperty);
            set => SetValue(FrequencyProperty, value);
        }

        private static void OnWaveFunctionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var visual = (WaveFunctionVisual)d;
            visual.OnWaveFunctionChanged((Engine.WaveFunction)e.NewValue);
        }
        private void OnWaveFunctionChanged(Engine.WaveFunction waveFunction)
        {
            RedrawWave();
        }

        private static void OnFrequencyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var visual = (WaveFunctionVisual)d;
            visual.OnFrequencyChanged((double)e.NewValue);
        }
        

        private void OnFrequencyChanged(double frequency)
        {
            RedrawWave();
        }

        
        public void RedrawWave()
        {
            this.Background = Brushes.Black;
            root.Children.Clear();
            var path = new Path();

            path.HorizontalAlignment = HorizontalAlignment.Stretch;
            path.VerticalAlignment = VerticalAlignment.Stretch;
            path.Stretch = Stretch.Fill;
            path.Width = ActualWidth;
            path.Height = ActualHeight;
            path.Stroke = Brushes.Yellow;
            path.StrokeThickness = 2;

            var pathLine = new Path();
            pathLine.HorizontalAlignment = HorizontalAlignment.Stretch;
            pathLine.VerticalAlignment = VerticalAlignment.Stretch;
            pathLine.Stretch = Stretch.Fill;
            pathLine.Width = ActualWidth;
            pathLine.Height = ActualHeight;
            pathLine.Stroke = Brushes.Blue;
            pathLine.StrokeThickness = 2;

            var waveFunction = WaveFunction;
            if (waveFunction != null)
            {


                var samples = new double[1000];
                waveFunction.Reset();
                for (int i = 0; i < samples.Length; i++)
                {
                    if (i == samples.Length * 0.5)
                    {
                        waveFunction.TRelease = i;
                    }
                    samples[i] = waveFunction.GetNext(Frequency, samples.Length, 0);
                }

                double middle = path.Height / 2;
                double steps = path.Width/samples.Length;
                var points = new PointCollection();
                for (int i = 0; i < samples.Length; i++)
                {
                    double x = ((double)i) * steps;
                    double y = (samples[i] * middle) + middle;
                    points.Add(new Point(x, y));
                }

                var geometry = new PathGeometry();
                var figure = new PathFigure();
                figure.StartPoint = points[0];
                figure.Segments.Add(new PolyLineSegment(points.Skip(1), true));
                geometry.Figures.Add(figure);
                path.Data = geometry;


                var geometryLine = new PathGeometry();
                var figureLine = new PathFigure();
                figureLine.StartPoint = new Point(0, middle);
                figureLine.Segments.Add(new LineSegment(new Point(path.Width, middle), true));
                geometryLine.Figures.Add(figureLine);
                pathLine.Data = geometryLine;

            }
            root.Children.Add(path);
            root.Children.Add(pathLine);
        }
        public WaveFunctionVisual()
        {
            InitializeComponent();
        }

        private void UserControl_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            RedrawWave();
        }

        private void root_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            var f = Frequency * Math.Pow(1.1, (double)e.Delta / 120d);
            if (f<5) {
                f = 5;
            }

            Frequency = f;
        }
    }
}
