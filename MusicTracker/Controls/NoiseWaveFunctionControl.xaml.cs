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
    /// Interaction logic for NoiseWaveFunctionControl.xaml
    /// </summary>
    public partial class NoiseWaveFunctionControl : Editor.BaseWaveFunctionControl
    {
        public NoiseWaveFunctionControl()
        {
            InitializeComponent();
            nodeNext.DataContext = this;
        }
        public override void LinkOutputToNext(Grid gridRoot, NodeEnd _inputNode)
        {
            this.LinkOutputToInput(gridRoot, nodeNext, _inputNode);
        }
        private void BaseWaveFunctionControl_Loaded(object sender, RoutedEventArgs e)
        {
            Random rnd = new Random();
            GenerateWave(contentPath, (t) => rnd.NextDouble() * 2 - 1);
        }


        private void btnRemoveNode_Click(object sender, RoutedEventArgs e)
        {
            DoRemove();
        }

        public override void DoRemove()
        {
            RemoveLink(nodeNext.NodeLink);
            RemoveSelf();
        }
    }
}
