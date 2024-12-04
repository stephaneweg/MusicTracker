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
                result = new Controls.InstrumentEditor.SineWaveFunctionControl();
            else if (wf is Engine.SquareWaveFunction)
                result = new Controls.InstrumentEditor.SquareWaveFunctionControl();
            else if (wf is Engine.TriangleWaveFunction)
                result = new Controls.InstrumentEditor.TriangleWaveFunctionControl();
            else if (wf is Engine.SawtoothWaveFunction)
                result = new Controls.InstrumentEditor.SawtoothWaveFunctionControl();
            else if (wf is Engine.NoiseWaveFunction)
                result = new Controls.InstrumentEditor.NoiseWaveFunctionControl();
            else if (wf is Engine.EnveloppeADSRWaveFunction)
                result = new Controls.InstrumentEditor.EnveloppeADSRWaveFunctionControl();
            else if (wf is Engine.FrequencyModifierWaveFunction)
                result = new Controls.InstrumentEditor.FrequencyModifierWaveFunctionControl();
            else if (wf is Engine.FrequencyModulationWaveFunction)
                result = new Controls.InstrumentEditor.FrequencyModulationWaveFunctionControl();
            else if (wf is Engine.AddWaveFunction)
                result = new Controls.InstrumentEditor.AddWaveFunctionControl();
            else if (wf is Engine.MultiplyWaveFunction)
                result = new Controls.InstrumentEditor.MultiplyWaveFunctionControl();
            else if (wf is Engine.VibratoWaveFunction)
                result = new Controls.InstrumentEditor.VibratoWaveFunctionControl();
            else if (wf is Engine.AudioPatchWaveFunction)
                result = new Controls.InstrumentEditor.AudioPatchWaveFunctionControl();
            else if (wf is Engine.AmplitudeModifierWaveFunction)
                result = new Controls.InstrumentEditor.AmplitudeModifierWaveFunctionControl();


            if (result!=null)
            {
                result.NodeObject = wf;
            }
            return result;
        }
        

        
    }
}
