﻿using System;
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
    /// Interaction logic for EnveloppeADSRWaveFunctionControl.xaml
    /// </summary>
    public partial class EnveloppeADSRWaveFunctionControl : Editor.BaseWaveFunctionControl
    {
        public EnveloppeADSRWaveFunctionControl()
        {
            InitializeComponent();
            nodeNext.DataContext = this;
            nodePrev.SetPrev = (o) =>
            {
                if (o != null)
                {
                    Editor.BaseWaveFunctionControl ctrl = o as Editor.BaseWaveFunctionControl;
                    var srcWaveFunction = ctrl.NodeItem.Item as Engine.WaveFunction;

                    (this.NodeItem.Item as Engine.EnveloppeADSRWaveFunction).WaveFunction = srcWaveFunction;
                }
                else
                {
                    (this.NodeItem.Item as Engine.EnveloppeADSRWaveFunction).WaveFunction = null;
                }
            };
        }



        public override void AddPrevControls(Grid gridRoot, int l)
        {
            if (this.NodeItem != null && this.NodeItem.Item != null)
            {
                Engine.EnveloppeADSRWaveFunction wv = this.NodeItem.Item as Engine.EnveloppeADSRWaveFunction; 
                if (wv.WaveFunction!=null)
                {
                    Editor.NodeControl ctrl = Editor.BaseWaveFunctionControl.Create(wv.WaveFunction);
                    Editor.EditorControl.Instance.AddControl(ctrl, this.Margin.Left-250,this.Margin.Top);
                    ctrl.LinkOutputToNext(gridRoot,nodePrev);
                    ctrl.AddPrevControls(gridRoot,l+1);
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