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
    /// Interaction logic for NodeLink.xaml
    /// </summary>
    public partial class NodeLink : UserControl
    {
        NodeStart _nodeStart;
        NodeEnd _nodeEnd;
        Point startPoint;
        Point endPoint;
        public NodeStart NodeStart
        {
            get { return _nodeStart; }
            set
            {
                if (_nodeStart != value)
                {
                    if (_nodeStart != null)
                    {
                        _nodeStart.NodeLink = null;
                    }
                    _nodeStart = value;
                    if (_nodeStart != null)
                    {
                        _nodeStart.NodeLink = this;
                    }
                    NodeEnd = NodeEnd;

                }

                if (_nodeStart!=null)
                {
                    StartPoint = NodeStart.TransformToAncestor(Editor.EditorControl.Instance.GridRoot).Transform(new Point(10, 10));
                }
                else
                {
                    StartPoint = new Point(0, 0);
                }
            }
        }

        public NodeEnd NodeEnd
        {
            get { return _nodeEnd; }
            set
            {
                if (_nodeEnd != value)
                {

                    if (_nodeStart != null)
                    {
                        object nextId = null;
                        if (value != null)
                        {
                            nextId = value.DataContext;
                        }

                    }
                    if (_nodeEnd != null)
                    {
                        if (_nodeEnd.NodeLinks.Contains(this))
                        {
                            _nodeEnd.SetPrev?.Invoke(null);
                            _nodeEnd.NodeLinks.Remove(this);
                        }
                    }
                    _nodeEnd = value;
                    if (_nodeEnd != null)
                    {
                        if (!_nodeEnd.NodeLinks.Contains(this)) _nodeEnd.NodeLinks.Add(this);
                        IsDragging = false;
                       
                        _nodeEnd.SetPrev?.Invoke((_nodeStart?.DataContext));
                    }
                   

                }

                if (_nodeEnd!=null)
                {
                    EndPoint = NodeEnd.TransformToAncestor(this.Parent as Panel).Transform(new Point(10, 10));
                }
                else
                {
                    if (endPoint.X == -1 && endPoint.Y == -1)
                        EndPoint = StartPoint;
                }
            }
        }

        public bool IsDragging
        {
            get; set;
        }
        public Point StartPoint
        {
            get { return startPoint; }
            set
            {
                startPoint = value;
                updateLink();
            }
        }
        public Point EndPoint
        {
            get { return endPoint; }
            set
            {
                endPoint = value;
                updateLink();
            }
        }

        public NodeLink()
        {
            InitializeComponent();
            endPoint = new Point(-1, -1);
        }

        public void updateLink()
        {

            figureLine.StartPoint = startPoint;

            var xMid = (endPoint.X + startPoint.X) / 2;
            var yMid = (endPoint.Y + startPoint.Y) / 2;
            var yDiff = Math.Abs(endPoint.Y - startPoint.Y);

            figureLine.Segments.Clear();
            if (endPoint.X > startPoint.X)
            {
                var dy = Math.Sign(EndPoint.Y - startPoint.Y);
                var x1 = Math.Max(startPoint.X, xMid - 25);
                var x2 = Math.Min(endPoint.X, xMid + 25);
                var y1 = (yDiff >= 50) ? startPoint.Y + (25 * dy) : yMid;

                var yy1 = (yDiff >= 50) ? y1 + (25 * dy) : yMid;
                var y2 = (yDiff >= 50) ? endPoint.Y - (25 * dy) : yMid;

                figureLine.Segments.Add(new LineSegment(new Point(x1, startPoint.Y), true));
                figureLine.Segments.Add(new QuadraticBezierSegment(new Point(xMid, startPoint.Y), new Point(xMid, y1), true));
                figureLine.Segments.Add(new LineSegment(new Point(xMid, y2), true));
                figureLine.Segments.Add(new QuadraticBezierSegment(new Point(xMid, endPoint.Y), new Point(x2, endPoint.Y), true));
                figureLine.Segments.Add(new LineSegment(endPoint, true));
            }
            else
            {
                if (Math.Abs(startPoint.X - endPoint.X) <= 25)
                {
                    figureLine.Segments.Add(new LineSegment(endPoint, true)); ;
                }
                else
                {
                    var y2 = endPoint.Y - 50;
                    yMid = (startPoint.Y + y2) / 2;
                    yDiff = Math.Abs(y2 - startPoint.Y);

                    var dy = Math.Sign(y2 - startPoint.Y);

                    var xx = dy == -1 ? Math.Max(endPoint.X + 175, StartPoint.X + 25) : startPoint.X + 25;


                    var y1 = (yDiff >= 50) ? startPoint.Y + (25 * dy) : yMid;
                    var yy = endPoint.Y - (50 * dy);
                    figureLine.Segments.Add(new LineSegment(new Point(xx, StartPoint.Y), true));
                    figureLine.Segments.Add(new QuadraticBezierSegment(new Point(xx + 25, startPoint.Y), new Point(xx + 25, y1), true));
                    if (Math.Sign((y2 - (25 * dy)) - y1) == dy)
                        figureLine.Segments.Add(new LineSegment(new Point(xx + 25, y2 - (25 * dy)), true));
                    figureLine.Segments.Add(new QuadraticBezierSegment(new Point(xx + 25, y2), new Point(xx, y2), true));
                    figureLine.Segments.Add(new LineSegment(new Point(endPoint.X + 25, y2), true));
                    figureLine.Segments.Add(new QuadraticBezierSegment(new Point(endPoint.X, y2), new Point(endPoint.X, y2 + 25), true));
                    figureLine.Segments.Add(new LineSegment(endPoint, true));
                }
            }

            nodeStart.Margin = new Thickness(startPoint.X - 10, startPoint.Y - 10, 0, 0);
            nodeEnd.Margin = new Thickness(endPoint.X - 10, endPoint.Y - 10, 0, 0);
        }
        private void Grid_MouseMove(object sender, MouseEventArgs e)
        {
            if (IsDragging)
            {
                NodeEnd = null;
                EndPoint = e.GetPosition(this.Parent as Panel);
            }
        }

        private void Grid_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (Editor.EditorControl.AddingLink == this)
            {
                Editor.EditorControl.AddingLink = null;
            }
            if (NodeEnd==null)
            {
                if (NodeStart!=null)
                {
                    NodeStart = null;
                }
                (this.Parent as Panel).Children.Remove(this);
            }
            IsDragging = false;
        }

        private void NodeEnd_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (Editor.EditorControl.AddingLink == null)
            {
                IsDragging = true;
                NodeEnd = null;
                Editor.EditorControl.AddingLink = this;
            }
        }
    }
}
