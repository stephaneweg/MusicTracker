using NAudio.Wave;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MusicTracker.Engine
{
    public class TrackWaveProvider : WaveProvider16,INotifyPropertyChanged
    {
        int noteIndex;
        public Action<int> NoteSelected { get; set; }
        double Interval = 0;
        double T;
        double bpm = 60;
        double speedFactor = 1;

        public event PropertyChangedEventHandler PropertyChanged;

        public int NoteIndex
        {
            get { return noteIndex; }
            set
            {
                if (noteIndex != value)
                {
                    noteIndex = value;
                    NoteSelected?.Invoke(noteIndex);
                }
            }
        }
        public bool Playing { get; private set; }
        public double BPM
        {
            get
            {
                return bpm;
            }
            set
            {
                bpm = value;
                Interval = (60d / (BPM*speedFactor)) / 4 * 44100;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(BPM)));
            }
        }

        public double SpeedFactor
        {
            get {  return speedFactor; }
            set { speedFactor = value;
                
                Interval = (60d / (BPM * speedFactor)) / 4 * 44100;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SpeedFactor)));


            }
        }
        public ProjectTrack Track { get; set; }

        public TrackWaveProvider(ProjectTrack track)
        {
            Track = track;
            SpeedFactor = 1;
        }

        public void Stop()
        {
            Playing = false;
        }
        public void Start()
        {
            Interval = (60d / (BPM * speedFactor)) / 4 * 44100;
            T = 0;
            NoteIndex = 0;
            for (int i = 0; i < Track.Channels.Count; i++)
            {
                if (Track.Channels[i] != null)
                {
                    var channel = Track.Channels[i];
                    if (NoteIndex >= channel.notes.Length)
                    {
                        NoteIndex = 0;
                    }
                    var n = channel.notes[NoteIndex];
                    if (n > 0)
                    {
                        channel.CurrentFrequency = 32.7d * Math.Pow(2, (n - 1) / 12.0);
                        channel.WaveFunction.Reset();
                    }
                    else
                    {
                        channel.CurrentFrequency = 0;
                    }

                }
            }
            Playing = true;
        }

        public void NextNote()
        {
            T = 0;
            NoteIndex++;

            if (NoteIndex >= Track.NoteCount)
            {
                NoteIndex = 0;
            }
            for (int i = 0; i < Track.Channels.Count; i++)
            {
                if (Track.Channels[i] != null)
                {
                    var channel = Track.Channels[i];
                    
                    var n = channel.notes[NoteIndex];
                    if (n > 0)
                    {
                        channel.CurrentFrequency = 32.7d * Math.Pow(2, (n - 1) / 12.0);
                        channel.WaveFunction.Reset();
                    }
                    else if (n == -1)
                    {
                        if (channel.WaveFunction.DoRelease())
                        {
                            channel.CurrentFrequency = 0;
                        }
                    }

                }
            }
        }
        public override int Read(short[] buffer, int offset, int sampleCount)
        {
            if (Playing)
            {
                for (int index = 0; index < sampleCount; index++)
                {
                    double cpt = 0;
                    double total = 0;
                    for (int i = 0; i < Track.Channels.Count; i++)
                    {
                        if (Track.Channels[i] != null)
                        {
                            var channel = Track.Channels[i];


                            total += channel.WaveFunction.GetNext(channel.CurrentFrequency, 44100, 0);
                            cpt++;
                        }
                    }
                    if (cpt > 0)
                    {
                        buffer[offset + index] = (short)((total / cpt) * short.MaxValue);
                    }
                    else
                    {
                        buffer[offset + index] = 0;
                    }

                    T++;
                    if (T >= Interval)
                    {
                        NextNote();

                    }
                }
                return sampleCount;
            }
            return 0;
        }
    }

}
