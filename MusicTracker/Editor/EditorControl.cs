using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Windows;

namespace MusicTracker.Editor
{
    public class EditorControl
    {

        public static Controls.NodeLink AddingLink { get; set; }
        public static EditorControl Instance { get; set; }
        public Grid GridRoot { get; set; }
        public NodeControl MovingControl { get; set; }

        
        public EditorControl()
        {
        }

        public void AddControl(NodeControl control ,double px=200,double py=200)
        {
            if (control != null)
            {
                control.Margin = new Thickness(px, py, 0, 0);

                control.HorizontalAlignment = HorizontalAlignment.Left;
                control.VerticalAlignment = VerticalAlignment.Top;
                control.EditorControl = this;
                GridRoot.Children.Add(control);
                Panel.SetZIndex(control, GridRoot.Children.OfType<NodeControl>().Max(ed => Panel.GetZIndex(ed) + 1));

                
            }
        }
    }
}
