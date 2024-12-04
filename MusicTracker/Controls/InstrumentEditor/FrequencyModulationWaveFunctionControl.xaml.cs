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

namespace MusicTracker.Controls.InstrumentEditor
{
    /// <summary>
    /// Interaction logic for FrequencyModulationWaveFunctionControl.xaml
    /// </summary>
    public partial class FrequencyModulationWaveFunctionControl : Editor.BaseWaveFunctionControl
    {
        public FrequencyModulationWaveFunctionControl()
        {
            InitializeComponent();

            nodeNext.DataContext = this;
            nodePrevCarrier.SetPrev = (o) =>
            {
                if (o != null)
                {
                    Editor.BaseWaveFunctionControl ctrl = o as Editor.BaseWaveFunctionControl;
                    var srcWaveFunction = ctrl.NodeObject as Engine.WaveFunction;

                    (this.NodeObject as Engine.FrequencyModulationWaveFunction).Carrier = srcWaveFunction;
                }
                else
                {
                    (this.NodeObject as Engine.FrequencyModulationWaveFunction).Carrier = null;
                }
            };


            nodePrevModulator.SetPrev = (o) =>
            {
                if (o != null)
                {
                    Editor.BaseWaveFunctionControl ctrl = o as Editor.BaseWaveFunctionControl;
                    var srcWaveFunction = ctrl.NodeObject as Engine.WaveFunction;

                    (this.NodeObject as Engine.FrequencyModulationWaveFunction).Modulator = srcWaveFunction;
                }
                else
                {
                    (this.NodeObject as Engine.FrequencyModulationWaveFunction).Modulator = null;
                }
            };
        }


        double maxY;
        public override double MaxY()
        {
            return maxY;
        }
        public override void AddPrevControls(Grid gridRoot, int l)
        {
            maxY = this.Margin.Top;
            if (this.NodeObject != null)
            {
                Engine.FrequencyModulationWaveFunction wv = this.NodeObject as Engine.FrequencyModulationWaveFunction;
                if (wv.Carrier != null)
                {
                    Editor.NodeControl ctrl = Editor.BaseWaveFunctionControl.Create(wv.Carrier);
                    Editor.EditorControl.Instance.AddControl(ctrl, this.Margin.Left - 250,maxY-70);
                    ctrl.LinkOutputToNext(gridRoot, nodePrevCarrier);
                    ctrl.AddPrevControls(gridRoot, 1+1);
                    maxY = ctrl.MaxY();
                }
                if (wv.Modulator!=null)
                {
                    Editor.NodeControl ctrl = Editor.BaseWaveFunctionControl.Create(wv.Modulator);
                    Editor.EditorControl.Instance.AddControl(ctrl, this.Margin.Left - 250, maxY+150);
                    ctrl.LinkOutputToNext(gridRoot, nodePrevModulator);
                    ctrl.AddPrevControls(gridRoot, 1 + 1);
                    maxY = ctrl.MaxY();
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
            RemoveLink(nodeNext.NodeLink);
            foreach (var nl in nodePrevCarrier.NodeLinks.ToList()) RemoveLink(nl);
            foreach (var nl in nodePrevModulator.NodeLinks.ToList()) RemoveLink(nl);
            RemoveSelf();

        }
    }
}
