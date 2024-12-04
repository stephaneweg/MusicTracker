using NAudio.Wave;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MusicTracker.Engine
{
    public class InstrumentWaveProvider : WaveProvider16
    {
        public WaveFunction WaveFunction { get; set; }
        public double Frequency { get; set; }
        public InstrumentWaveProvider(WaveFunction wave1)
        {
            WaveFunction = wave1;
            Frequency = 0;
        }

        public override int Read(short[] buffer, int offset, int sampleCount)
        {
            if (WaveFunction != null)
            {
                for (int index = 0; index < sampleCount; index++)
                {


                    buffer[offset + index] = (short)(WaveFunction.GetNext(Frequency, 44100, 0) * short.MaxValue);
                }

            }
            return sampleCount;
        }
    }

}
