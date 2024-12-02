using MusicTracker.Engine;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace MusicTracker.Editor
{
    public abstract class NodeControl:UserControl
    {
        bool dragging;
        double startX; double startY;

        public void Control_MouseDown(object sender, MouseButtonEventArgs e)
        {
            dragging = true;
            var pt = e.GetPosition(this.Parent as Grid);
            startX = pt.X - this.Margin.Left;
            startY = pt.Y - this.Margin.Top;
            var z = (this.Parent as Panel).Children.OfType<BaseWaveFunctionControl>().Max(i => Panel.GetZIndex(i) + 1);
            Panel.SetZIndex(this, z);
            EditorControl.Instance.MovingControl = this;
        }
        public void Control_MouseUp(object sender, MouseButtonEventArgs e)
        {
            EditorControl.Instance.MovingControl = null;
            dragging = false;
        }

        public void Control_MouseMove(object sender, MouseEventArgs e)
        {
            if (dragging)
            {
                var pt = e.GetPosition(this.Parent as Grid);

                var l = pt.X - startX;
                var t = pt.Y - startY;
                this.Margin = new Thickness(l, t, 0, 0);

                NodeItem.PositionX = (int)l;
                NodeItem.PositionY = (int)t;
                foreach (var link in EditorControl.Instance.GridRoot.Children.OfType<Controls.NodeLink>())
                {
                    link.NodeStart = link.NodeStart;
                    link.NodeEnd = link.NodeEnd;
                }

                e.Handled = true;
            }
        }

        public EditorControl EditorControl { get; set; }
        public Controls.NodeEnd NodeEnd { get; set; }


        public static readonly DependencyProperty NodeItemProperty =
            DependencyProperty.Register(
                "NodeItem",
                typeof(NodeItem),
                typeof(NodeControl),
                new PropertyMetadata(null, OnNodeItemChanged));

        public NodeItem NodeItem
        {
            get => (NodeItem)GetValue(NodeItemProperty);
            set => SetValue(NodeItemProperty, value);
        }

        private static void OnNodeItemChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var visual = (NodeControl)d;
            NodeItem nodeItem = (NodeItem)e.NewValue;
            visual.DataContext = null;
            if (nodeItem != null)
            {
                visual.DataContext = nodeItem.Item;
            }
        }

        public virtual void AddPrevControls(Grid gridRoot, int level)
        {

        }

        public virtual void LinkOutputToNext(Grid gridRoot, Controls.NodeEnd _inputNode)
        {

        }



        public void RemoveLink(Controls.NodeLink link)
        {
            if (link != null)
            {
                var ne = link.NodeEnd;
                var ns = link.NodeStart;

                link.NodeEnd = null;
                link.NodeStart = null;
                (link.Parent as Panel)?.Children.Remove(link);
                if (ne != null && ne.NodeLinks.Contains(link))
                    ne.NodeLinks.Remove(link);
                if (ns != null && ns.NodeLink == link)
                    ns.NodeLink = null;
            }
        }
        public void LinkOutputToInput(Grid gridRoot, Controls.NodeStart _outputNode, Controls.NodeEnd _inputNode)
        {

            var link = new Controls.NodeLink();
            Panel.SetZIndex(link, -1);
            link.DataContext = _outputNode.DataContext;
            gridRoot.Children.Add(link);

            Task.Run(async () =>
            {
                await Task.Delay(50);

                await Dispatcher.BeginInvoke((Action)(() =>
                {
                    link.NodeStart = _outputNode;
                    link.NodeEnd = _inputNode;
                }));
            });
        }

        public void GenerateWave(Panel container, Func<double, double> getNext)
        {
            this.Background = Brushes.Black;
            container.Children.Clear();
            var path = new Path();

            path.HorizontalAlignment = HorizontalAlignment.Stretch;
            path.VerticalAlignment = VerticalAlignment.Stretch;
            path.Stretch = Stretch.Fill;
            path.Width = container.ActualWidth;
            path.Height = container.ActualHeight;
            path.Stroke = Brushes.Yellow;
            path.StrokeThickness = 2;

            var pathLine = new Path();
            pathLine.HorizontalAlignment = HorizontalAlignment.Stretch;
            pathLine.VerticalAlignment = VerticalAlignment.Stretch;
            pathLine.Stretch = Stretch.Fill;
            pathLine.Width = container.ActualWidth;
            pathLine.Height = container.ActualHeight;
            pathLine.Stroke = Brushes.Blue;
            pathLine.StrokeThickness = 2;



            var samples = new double[500];
            for (int i = 0; i < samples.Length; i++)
            {
                samples[i] = getNext((double)i * 2 / (double)samples.Length);
            }

            double middle = path.Height / 2;
            double steps = path.Width / samples.Length;
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


            container.Children.Add(path);
            container.Children.Add(pathLine);
        }

        public void RemoveSelf()
        {
            (this.Parent as Panel).Children.Remove(this);
        }

        public virtual void DoRemove() { }
    }
    public abstract class BaseWaveFunctionControl: NodeControl
    {

        
        public static NodeControl CreateNew<T>() where T : Engine.WaveFunction
        {
            return Create(Activator.CreateInstance<T>());
        }

        public static NodeControl Create(Engine.WaveFunction wf)
        {
            BaseWaveFunctionControl result = null;

            if (wf is Engine.SineWaveFunction)
                result = new Controls.SineWaveFunctionControl();
            else if (wf is Engine.SquareWaveFunction)
                result = new Controls.SquareWaveFunctionControl();
            else if (wf is Engine.TriangleWaveFunction)
                result = new Controls.TriangleWaveFunctionControl();
            else if (wf is Engine.SawtoothWaveFunction)
                result = new Controls.SawtoothWaveFunctionControl();
            else if (wf is Engine.NoiseWaveFunction)
                result = new Controls.NoiseWaveFunctionControl();
            else if (wf is Engine.EnveloppeADSRWaveFunction)
                result = new Controls.EnveloppeADSRWaveFunctionControl();
            else if (wf is Engine.FrequencyModifierWaveFunction)
                result = new Controls.FrequencyModifierWaveFunctionControl();
            else if (wf is Engine.FrequencyModulationWaveFunction)
                result = new Controls.FrequencyModulationWaveFunctionControl();
            else if (wf is Engine.AddWaveFunction)
                result = new Controls.AddWaveFunctionControl();
            else if (wf is Engine.MultiplyWaveFunction)
                result = new Controls.MultiplyWaveFunctionControl();
            else if (wf is Engine.VibratoWaveFunction)
                result = new Controls.VibratoWaveFunctionControl();
            else if (wf is Engine.AudioPatchWaveFunction)
                result = new Controls.AudioPatchWaveFunctionControl();


            if (result!=null)
            {
                result.NodeItem = new NodeItem { Item = wf };
            }
            return result;
        }
        

        
    }
}
