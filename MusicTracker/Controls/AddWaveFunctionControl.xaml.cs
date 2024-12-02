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
    /// Interaction logic for AddWaveFunctionControl.xaml
    /// </summary>
    public partial class AddWaveFunctionControl : Editor.BaseWaveFunctionControl
    {
        public AddWaveFunctionControl()
        {
            InitializeComponent();

            nodeNext.DataContext = this;
            nodePrevWave1.SetPrev = (o) =>
            {
                if (o != null)
                {
                    Editor.BaseWaveFunctionControl ctrl = o as Editor.BaseWaveFunctionControl;
                    var srcWaveFunction = ctrl.NodeObject as Engine.WaveFunction;

                    (this.NodeObject as Engine.AddWaveFunction).Wave1 = srcWaveFunction;
                }
                else
                {
                    (this.NodeObject as Engine.AddWaveFunction).Wave1 = null;
                }
            };


            nodePrevWave2.SetPrev = (o) =>
            {
                if (o != null)
                {
                    Editor.BaseWaveFunctionControl ctrl = o as Editor.BaseWaveFunctionControl;
                    var srcWaveFunction = ctrl.NodeObject as Engine.WaveFunction;

                    (this.NodeObject as Engine.AddWaveFunction).Wave2 = srcWaveFunction;
                }
                else
                {
                    (this.NodeObject as Engine.AddWaveFunction).Wave2 = null;
                }
            };
        }

        public override void AddPrevControls(Grid gridRoot, int l)
        {
            if (this.NodeObject != null)
            {
                Engine.AddWaveFunction wv = this.NodeObject as Engine.AddWaveFunction;
                if (wv.Wave1 != null)
                {
                    Editor.NodeControl ctrl = Editor.BaseWaveFunctionControl.Create(wv.Wave1);
                    Editor.EditorControl.Instance.AddControl(ctrl, this.Margin.Left - 250, Margin.Top - 70);
                    ctrl.LinkOutputToNext(gridRoot, nodePrevWave1);
                    ctrl.AddPrevControls(gridRoot, 1 + 1);
                }
                if (wv.Wave2 != null)
                {
                    Editor.NodeControl ctrl = Editor.BaseWaveFunctionControl.Create(wv.Wave2);
                    Editor.EditorControl.Instance.AddControl(ctrl, this.Margin.Left - 250, Margin.Top + 150);
                    ctrl.LinkOutputToNext(gridRoot, nodePrevWave2);
                    ctrl.AddPrevControls(gridRoot, 1 + 1);
                }
            }
        }


        public override void LinkOutputToNext(Grid gridRoot, NodeEnd _inputNode)
        {
            this.LinkOutputToInput(gridRoot, nodeNext, _inputNode);
        }

        private void btnRemoveNode_Click(object sender, RoutedEventArgs e)
        {
            DoRemove();
        }

        public override void DoRemove()
        {
            base.DoRemove();
            RemoveLink(nodeNext.NodeLink);
            foreach (var nl in nodePrevWave1.NodeLinks.ToList()) RemoveLink(nl);
            foreach (var nl in nodePrevWave2.NodeLinks.ToList()) RemoveLink(nl);
            RemoveSelf();
            
        }
    }
}
