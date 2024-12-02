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
                result.NodeObject = wf;
            }
            return result;
        }
        

        
    }
}
