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
    /// Interaction logic for NodeStart.xaml
    /// </summary>
    public partial class NodeStart : UserControl
    {
        public NodeLink NodeLink { get; set; }
        public bool DirectionLeft { get; set; }
        public NodeStart()
        {
            InitializeComponent();
        }
        private void UserControl_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (this.NodeLink == null)
            {
                var link = new NodeLink();
                link.IsDragging = true;
                Editor.EditorControl.Instance.GridRoot.Children.Add(link);
                link.NodeStart = this;
                link.DataContext = this.DataContext;
                Panel.SetZIndex(link, -1);
                Editor.EditorControl.AddingLink = link;
            }
            else
            {
                this.NodeLink.NodeEnd = null;
                this.NodeLink.IsDragging = true;
                Editor.EditorControl.AddingLink = this.NodeLink;

            }
        }
    }
}
