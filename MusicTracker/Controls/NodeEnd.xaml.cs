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
    /// Interaction logic for NodeEnd.xaml
    /// </summary>
    public partial class NodeEnd : UserControl
    {
        public List<NodeLink> NodeLinks { get; set; }
        public Action<object> SetPrev { get; set; }

        public NodeEnd()
        {
            InitializeComponent();
            NodeLinks = new List<NodeLink>();
        }

        private void UserControl_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if ((Editor.EditorControl.AddingLink != null) && !NodeLinks.Contains(Editor.EditorControl.AddingLink))
            {
                NodeLinks.Add(Editor.EditorControl.AddingLink);
                Editor.EditorControl.AddingLink.NodeEnd = this;
                Editor.EditorControl.AddingLink.IsDragging = false;
                Editor.EditorControl.AddingLink = null;
            }
        }

        private void UserControl_MouseDown(object sender, MouseButtonEventArgs e)
        {
            var link = NodeLinks.LastOrDefault();
            if ((link != null) && (Editor.EditorControl.AddingLink == null))
            {
                var nl = link;
                var np = link.EndPoint;
                link.IsDragging = true;
                Editor.EditorControl.AddingLink = link;
                link.NodeEnd = null;
                nl.EndPoint = e.GetPosition(nl.Parent as Panel);
            }
        }
    }
}
