using NAudio.SoundFont;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Threading.Tasks;

namespace MusicTracker
{
    public class MusicProject
    {
        public static MusicProject CurrentProject { get; set; } = new MusicProject();
        public ObservableCollection<ProjectTrack> Tracks { get; set; } = new ObservableCollection<ProjectTrack>();
    }

    

    public class ProjectTrack
    {
        public int NoteCount { get; set; } = 100;
        public string Name { get; set; }
        public int ID { get; set; }


        public List<TrackChannel> Channels { get; set; } = new List<TrackChannel>();

        public int note(int channel, int num)
        {
            int result = 0;
            if (channel >= 0 && channel < Channels.Count)
            {
                if (num>=0 && num<Channels[channel].notes.Length)
                {
                    result = Channels[channel].notes[num];
                }
            }

            return result;
        }

        public void setNote(int channel,int num, int value)
        {
            if (channel >= 0 && channel < Channels.Count)
            {
                if (num >= 0 && num < Channels[channel].notes.Length)
                {
                    Channels[channel].notes[num] = value;
                }
            }
        }

        public void AddChannel()
        {
            Channels.Add(new TrackChannel());
            NoteCount = Channels.Max(n => n.notes.Length);
        }



    }

    public class TrackChannel
    {
        public double CurrentFrequency { get; set; }

        public string Name { get; set; }
        public Engine.WaveFunction WaveFunction { get; set; }
        public int[] notes { get; set; } = new int[100];

    }
}
