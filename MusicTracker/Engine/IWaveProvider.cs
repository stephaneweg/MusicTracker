using MusicTracker;
using NAudio.Wave;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Runtime.Remoting.Channels;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Interop;

namespace MusicTracker.Engine
{
    public interface IWaveProvider
    {
        double BPM { get; set; }
        double SpeedFactor { get; set; }
        void Start();
        void Stop();
        int Read(short[] buffer, int offset, int sampleCount);
    }
   

}
