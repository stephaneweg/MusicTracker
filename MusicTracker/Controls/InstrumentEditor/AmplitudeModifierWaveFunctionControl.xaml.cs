﻿using MusicTracker.Engine;
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
    /// Interaction logic for FrequencyModifierWaveFunctionControl.xaml
    /// </summary>
    public partial class AmplitudeModifierWaveFunctionControl : Editor.BaseWaveFunctionControl
    {
        public AmplitudeModifierWaveFunctionControl()
        {
            InitializeComponent();
            
            nodeNext.DataContext = this;
            nodePrev.SetPrev = (o) =>
            {
                if (o != null)
                {
                    Editor.BaseWaveFunctionControl ctrl = o as Editor.BaseWaveFunctionControl;
                    var srcWaveFunction = ctrl.NodeObject as Engine.WaveFunction;

                    (this.NodeObject as Engine.AmplitudeModifierWaveFunction).WaveFunction = srcWaveFunction;
                }
                else
                {
                    (this.NodeObject as Engine.AmplitudeModifierWaveFunction).WaveFunction = null;
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
                Engine.AmplitudeModifierWaveFunction wv = this.NodeObject as Engine.AmplitudeModifierWaveFunction;
                if (wv.WaveFunction != null)
                {
                    Editor.NodeControl ctrl = Editor.BaseWaveFunctionControl.Create(wv.WaveFunction);
                    Editor.EditorControl.Instance.AddControl(ctrl, this.Margin.Left - 250, maxY);
                    ctrl.LinkOutputToNext(gridRoot, nodePrev);
                    ctrl.AddPrevControls(gridRoot, 1+1);
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
            foreach (var nl in nodePrev.NodeLinks.ToList()) RemoveLink(nl);
            RemoveSelf();

        }
    }
}
