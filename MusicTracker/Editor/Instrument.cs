using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MusicTracker.Editor
{
    public class Instrument:INotifyPropertyChanged
    {
        string name;
        Engine.WaveFunction waveFunction;
        public event PropertyChangedEventHandler PropertyChanged;
        public int ID { get; set; }
        public string Name
        {
            get { return name; }
            set
            {
                if (name == value) return;
                name = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Name)));
            }
        }
        public Engine.WaveFunction WaveFunction
        {
            get { return waveFunction; }
            set
            {
                if (waveFunction == value) return;
                waveFunction = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(WaveFunction)));
            }
        }


        public Instrument Clone()
        {
            return System.Text.Json.JsonSerializer.Deserialize<Instrument>(System.Text.Json.JsonSerializer.Serialize(this));
        }
    }
}
