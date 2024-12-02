using MusicTracker.Controls;
using NAudio.SoundFont;
using NAudio.Wave;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Interop;

namespace MusicTracker.Engine
{
   


    public class SingleWaveProvider:WaveProvider16
    {
        public WaveFunction WaveFunction { get; set; }
        public double Frequency { get; set; }
        public SingleWaveProvider(WaveFunction wave1)
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

    public class WaveProvider:WaveProvider16
    {
        int noteIndex;
        public Action<int> NoteSelected { get; set; }
        double Interval = 0;
        double T;
        double bmp;
        public int NoteIndex {
            get { return noteIndex; }
            set
            {
                if (noteIndex!=value)
                {
                    noteIndex = value;
                    NoteSelected?.Invoke(noteIndex);
                }
            }
        }
        public bool Playing { get; private set; }
        public double BMP {
            get
            {
                return bmp;
            }
            set
            {
                bmp = value;
                Interval = (60d / BMP) / 4 * 44100;
            }
        }
        public ProjectTrack Track { get; set; }
       
        public WaveProvider(ProjectTrack track)
        {
            Track = track;
        }

        public void Stop()
        {
            Playing = false;
        }
        public void Start()
        {
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
                        T = 0;
                        NoteIndex++;

                        if (NoteIndex>=Track.NoteCount)
                        {
                            NoteIndex = 0;
                        }
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
                }
                return sampleCount;
            }
            return 0;
        }
    }

    [System.Text.Json.Serialization.JsonDerivedType(typeof(SineWaveFunction), "Sine")]
    [System.Text.Json.Serialization.JsonDerivedType(typeof(SquareWaveFunction), "Square")]
    [System.Text.Json.Serialization.JsonDerivedType(typeof(TriangleWaveFunction), "Triangle")]
    [System.Text.Json.Serialization.JsonDerivedType(typeof(SawtoothWaveFunction), "Sawtooth")]
    [System.Text.Json.Serialization.JsonDerivedType(typeof(NoiseWaveFunction), "Noise")]
    [System.Text.Json.Serialization.JsonDerivedType(typeof(EnveloppeADSRWaveFunction), "ADSR")]
    [System.Text.Json.Serialization.JsonDerivedType(typeof(FrequencyModifierWaveFunction), "FrequencyModifier")]
    [System.Text.Json.Serialization.JsonDerivedType(typeof(FrequencyModulationWaveFunction), "FrequencyModulation")]
    [System.Text.Json.Serialization.JsonDerivedType(typeof(AddWaveFunction), "Add")]
    [System.Text.Json.Serialization.JsonDerivedType(typeof(VibratoWaveFunction), "Vibrato")]
    [System.Text.Json.Serialization.JsonDerivedType(typeof(MultiplyWaveFunction), "Multiply")]
    [System.Text.Json.Serialization.JsonDerivedType(typeof(AudioPatchWaveFunction), "AudioPatch")]
    public abstract class WaveFunction :INotifyPropertyChanged
    {

        public WaveFunction Clone()
        {
            return System.Text.Json.JsonSerializer.Deserialize<WaveFunction>(System.Text.Json.JsonSerializer.Serialize(this));
        }

        public bool Ended { get; set; }
        public double TRelease { get; set; }
        public double T { get; set; }

        public event PropertyChangedEventHandler PropertyChanged;
        public void OnPropertyChanged(string name)
        {
            this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        public virtual void Reset()
        {
            T = 0;
            Ended = false;
            TRelease = 0;
        }
        public abstract double GetNext(double frequency,double SampleRate, double modulatorValue);
        public abstract double maxLevels(double currentLevel);

        public virtual bool DoRelease()
        { 
            Ended = true;
            TRelease = T;
            return Ended;
        }

        
    }


    public class AudioPatchWaveFunction : WaveFunction
    {
        AudioPatch patch;
        string patchName;
        public string PatchName
        {
            get { return patchName; }
            set
            {
                if (patchName != value)
                {
                    patchName = value;
                    patch = AudioPatch.patchList.FirstOrDefault(p => p.Name == value);
                    OnPropertyChanged(nameof(PatchName));

                }
            }
        }

        public override double GetNext(double frequency, double SampleRate, double modulatorValue)
        {
            double result = 0;
            if (patch != null)
            {
                var part = patch.GetPart(frequency);
                if (part==null) part=patch.Parts[0];
                var doubles = part.getDoubles();
                double index = (T * ((frequency/part.BaseFrequency) * (SampleRate / part.SampleRate))) % doubles.Length;
                var dd = (double)doubles[(int)index];
                var d = dd / 30000.0;
                result = d;
            }
            T += 1;
            return result;
        }

        public override double maxLevels(double currentLevel)
        {
            return currentLevel;
        }
    }
    public class SineWaveFunction : WaveFunction
    {
        public override double GetNext(double frequency, double SampleRate, double modulatorValue)
        {
            double f = frequency * T / SampleRate;
            T += 1;
            return Math.Sin(2 * Math.PI * f + modulatorValue);
        }

        public override double maxLevels(double currentLevel)
        {
            return currentLevel;
        }
    }

    public class SquareWaveFunction : WaveFunction
    {
        public override double GetNext(double frequency, double SampleRate, double modulatorValue)
        {
            double f = frequency * T / SampleRate;
            T += 1;
            return f + modulatorValue - Math.Truncate(f + modulatorValue) > 0.5 ? 1 : -1;
        }
        public override double maxLevels(double currentLevel)
        {
            return currentLevel;
        }
    }

    public class TriangleWaveFunction : WaveFunction
    {
        public override double GetNext(double frequency, double SampleRate, double modulatorValue)
        {
            double f = frequency * T / SampleRate;
            T += 1;
            return (2 * Math.Abs(2 * (f + modulatorValue - Math.Truncate(f + modulatorValue + 0.5))) - 1);

        }

        public override double maxLevels(double currentLevel)
        {
            return currentLevel;
        }
    }

    public class SawtoothWaveFunction : WaveFunction
    {
        public override double GetNext(double frequency, double SampleRate, double modulatorValue)
        {
            double f = frequency * T / SampleRate;
            T += 1;
            return 2 * (f + modulatorValue - Math.Truncate(f + modulatorValue) - 0.5);
        }
        public override double maxLevels(double currentLevel)
        {
            return currentLevel;
        }
    }

    public class NoiseWaveFunction : WaveFunction
    {
        private Random random = new Random();
        public override double GetNext(double frequency, double SampleRate, double modulatorValue)
        {
            double f = frequency * T / SampleRate;
            T += 1;
            return random.NextDouble() * 2 - 1;
        }

        public override double maxLevels(double currentLevel)
        {
            return currentLevel;
        }
    }

    public class EnveloppeADSRWaveFunction : WaveFunction
    {
        double a;double d; double s; double r;
        WaveFunction waveFunction;

        public double Attack
        {
            get { return a; }
            set
            {
                if (a != value)
                {
                    a = value;
                    OnPropertyChanged(nameof(Attack));
                }
            }
        }
        public double Decay
        {
            get { return d; }
            set
            {
                if (d != value)
                {
                    d = value;
                    OnPropertyChanged(nameof(Decay));
                }
            }
        }

        public double Sustain
        {
            get { return s; }
            set
            {
                if (s != value)
                {
                    s = value;
                    OnPropertyChanged(nameof(Sustain));
                }
            }
        }

        public double Release
        {
            get { return r; }
            set
            {
                if (r != value)
                {
                    r = value;
                    OnPropertyChanged(nameof(Release));
                }
            }
        }


        
        public WaveFunction WaveFunction
        {
            get { return waveFunction; }
            set
            {
                if (waveFunction != value)
                {
                    if (waveFunction != null)
                    {
                        waveFunction.PropertyChanged -= WaveFunction_PropertyChanged;
                    }
                    waveFunction = value;
                    if (waveFunction != null)
                    {
                        waveFunction.PropertyChanged += WaveFunction_PropertyChanged;
                    }
                    OnPropertyChanged(nameof(WaveFunction));
                }
            }
        }

        private void WaveFunction_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            OnPropertyChanged(nameof(WaveFunction));
        }

        public override void Reset()
        {
            base.Reset();
            if (WaveFunction != null)
            {
                WaveFunction.Reset(); 
            }
        }

        public override bool DoRelease()
        {
            base.DoRelease();
            waveFunction.DoRelease();
            TRelease = T;
            Ended = false;
            return false;
        }

        public override double GetNext(double frequency, double SampleRate, double modulatorValue)
        {
            if (waveFunction != null)
            {
                double atk = a * SampleRate;
                double dec = d * SampleRate;
                double rel = r * SampleRate;
                double amplitude = 1;

                if (T < atk)
                    amplitude = T / atk;
                else if (T < atk + dec)
                    amplitude = 1 - (T - atk) / dec * (1 - Sustain);
                else if (Ended)
                    amplitude = 0;
                else if (TRelease > 0)
                {
                    if (rel > 0)
                    {
                        amplitude = s * (1 - ((T - TRelease) / rel));
                    }
                    else
                    {
                        amplitude = 0;
                    }
                    if (amplitude <= 0)
                    {
                        amplitude = 0;
                        Ended = true;
                    }
                }
                else
                    amplitude = s;

                T += 1;
                waveFunction.Ended = Ended;
                waveFunction.TRelease = TRelease;
                return waveFunction.GetNext(frequency,SampleRate, modulatorValue) * amplitude;
            }
            return 0;
        }

        public override double maxLevels(double currentLevel)
        {
            if (WaveFunction!=null) return WaveFunction.maxLevels(currentLevel+1);
            return currentLevel;
        }
    }

    public class FrequencyModifierWaveFunction : WaveFunction
    {
        double frequencyModifier;
        WaveFunction waveFunction;
        public double FrequencyModifier
        {
            get { return frequencyModifier; }
            set
            {
                if (frequencyModifier != value)
                {
                    frequencyModifier = value;
                    OnPropertyChanged(nameof(FrequencyModifier));
                }
            }
        }
        public WaveFunction WaveFunction
        {
            get { return waveFunction; }
            set
            {
                if (waveFunction != value)
                {
                    if (waveFunction!=null)
                    {
                        waveFunction.PropertyChanged -= WaveFunction_PropertyChanged;
                    }
                    waveFunction = value;
                    if (waveFunction != null)
                    {
                        waveFunction.PropertyChanged += WaveFunction_PropertyChanged;
                    }
                    OnPropertyChanged(nameof(WaveFunction));
                }
            }
        }

        private void WaveFunction_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            OnPropertyChanged(nameof(WaveFunction));
        }
        public override void Reset()
        {
            base.Reset();
            if (WaveFunction != null)
            {
                WaveFunction.Reset();
            }
        }
        public override double GetNext(double frequency, double SampleRate, double modulatorValue)
        {
            if (WaveFunction != null)
            {
                WaveFunction.Ended = Ended;
                WaveFunction.TRelease = TRelease;

                return WaveFunction.GetNext(frequency * FrequencyModifier,SampleRate, modulatorValue);
            }
            return 0;
        }

        public override double maxLevels(double currentLevel)
        {
            if (WaveFunction != null) return WaveFunction.maxLevels(currentLevel + 1);
            return currentLevel;
        }
        override public bool DoRelease()
        {
            bool ret = base.DoRelease();
            if (waveFunction != null)
            {
                ret = ret & waveFunction.DoRelease();
            }
            return ret;
        }
    }
    
    public class VibratoWaveFunction:WaveFunction
    {
        double vibratoFrequency;
        double vibratoSpeed;
        WaveFunction waveFunction;

        public double VibratoFrequency
        {
            get { return vibratoFrequency; }
            set
            {
                if (vibratoFrequency != value)
                {
                    vibratoFrequency = value;
                    OnPropertyChanged(nameof(VibratoFrequency));
                }
            }
        }

        public double VibratoSpeed
        {
            get { return vibratoSpeed; }
            set
            {
                if (vibratoSpeed != value)
                {
                    vibratoSpeed = value;
                    OnPropertyChanged(nameof(VibratoSpeed));
                }
            }
        }

        public WaveFunction WaveFunction
        {
            get { return waveFunction; }
            set
            {
                if (waveFunction != value)
                {
                    if (waveFunction != null)
                    {
                        waveFunction.PropertyChanged -= waveFunction_PropertyChanged;
                    }
                    waveFunction = value;
                    if (waveFunction != null)
                    {
                        waveFunction.PropertyChanged += waveFunction_PropertyChanged;
                    }
                    OnPropertyChanged(nameof(WaveFunction));
                }
            }
        }

        private void waveFunction_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            OnPropertyChanged(nameof(WaveFunction));
        }

        public override void Reset()
        {
            base.Reset();
            if (WaveFunction != null)
            {
                WaveFunction.Reset();
            }
        }
        override public double GetNext(double frequency, double SampleRate, double modulatorValue)
        {

            var vibrato_change = Math.Sin(2 * Math.PI * vibratoSpeed * T / SampleRate)*vibratoFrequency;

            double mFreq = frequency+ vibrato_change;
            double result = 0;
            if (waveFunction != null)
            {
                result = waveFunction.GetNext(frequency, SampleRate, vibrato_change);
            }
            T += 1;

            return result;
        }

        public override double maxLevels(double currentLevel)
        {
            double l1 = currentLevel;
            if (waveFunction != null) l1 = waveFunction.maxLevels(currentLevel + 1);
            return l1;
        }
        override public bool DoRelease()
        {
            bool ret = base.DoRelease();
            if (waveFunction != null)
            {
                ret = ret & waveFunction.DoRelease();
            }
            return ret;
        }
    }
    public class AddWaveFunction : WaveFunction
    {
        double v1;
        double v2;
        WaveFunction w1;
        WaveFunction w2;

        public WaveFunction Wave1
        {
            get { return w1; }
            set
            {
                if (w1 != value)
                {
                    if (w1!=null)
                    {
                        w1.PropertyChanged-= Wave1_PropertyChanged;
                    }
                    w1 = value;
                    if (w1 != null)
                    {
                        w1.PropertyChanged += Wave1_PropertyChanged;
                    }
                    OnPropertyChanged(nameof(Wave1));
                }
            }
        }
        public WaveFunction Wave2
        {
            get { return w2; }
            set
            {
                if (w2 != value)
                {
                    if (w2 != null)
                    {
                        w2.PropertyChanged -= Wave2_PropertyChanged;
                    }
                    w2 = value;
                    if (w2 != null)
                    {
                        w2.PropertyChanged += Wave2_PropertyChanged;
                    }
                    OnPropertyChanged(nameof(Wave2));
                }
            }
        }

        public double Volume1
        {
            get { return v1; }
            set { 
                if (v1 != value)
                {
                    v1 = value;
                    OnPropertyChanged(nameof(Volume1));
                }
            }
        }
        public double Volume2
        {
            get { return v2; }
            set
            {
                if (v2 != value)
                {
                    v2 = value;
                    OnPropertyChanged(nameof(Volume2));
                }
            }
        }

        private void Wave1_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            OnPropertyChanged(nameof(Wave1));
        }
        private void Wave2_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            OnPropertyChanged(nameof(Wave2));
        }

        public override void Reset()
        {
            base.Reset();
            if (Wave1 != null)
            {
                Wave1.Reset();
            }
            if (Wave2 != null)
            {
                Wave2.Reset();
            }
        }

        override public double GetNext(double frequency, double SampleRate, double modulatorValue)
        {
            double result = 0;
            double totalVolume = 0;

            if (Wave1 != null)
            {
                result += Wave1.GetNext(frequency, SampleRate, modulatorValue) * Volume1;
                totalVolume += Volume1;
            }
            if (Wave2 != null)
            {
                result += Wave2.GetNext(frequency, SampleRate, modulatorValue) * Volume2;
                totalVolume += Volume2;
            }

            if (totalVolume > 0) result /= totalVolume;
            return result;
        }

        public override double maxLevels(double currentLevel)
        {
            double l1 = currentLevel;
            double l2 = currentLevel;
            if (Wave1 != null) l1 = Wave1.maxLevels(currentLevel + 1);
            if (Wave2 != null) l2 = Wave2.maxLevels(currentLevel + 1);
            return Math.Max(l1, l2);
        }
        override public bool DoRelease()
        {
            bool ret = base.DoRelease();
            if (Wave1 != null)
            {
                ret = ret & Wave1.DoRelease();
            }
            if (Wave2 != null)
            {
                ret = ret & Wave2.DoRelease();
            }
            return ret;
        }
    }

    public class MultiplyWaveFunction:AddWaveFunction
    {
        override public double GetNext(double frequency, double SampleRate, double modulatorValue)
        {
            double result = 1;

            if (Wave1 != null)
            {
                result *= Wave1.GetNext(frequency, SampleRate, modulatorValue) * Volume1;
            }
            if (Wave2 != null)
            {
                result *= Wave2.GetNext(frequency, SampleRate, modulatorValue) * Volume2;
            }

            return result;
        }
    }
    public class FrequencyModulationWaveFunction : WaveFunction
    {
        WaveFunction modulator;
        WaveFunction carrier;
        public WaveFunction Modulator
        {
            get { return modulator; }
            set
            {
                if (modulator != value)
                {
                    if (modulator != null)
                    {
                        modulator.PropertyChanged -= Modulator_PropertyChanged;
                    }
                    modulator = value;
                    if (modulator != null)
                    {
                        modulator.PropertyChanged += Modulator_PropertyChanged;
                    }
                    OnPropertyChanged(nameof(Modulator));
                }
            }
        }
        public WaveFunction Carrier
        {
            get { return carrier; }
            set
            {
                if (carrier != value)
                {
                    if (carrier!=null)
                    {
                           carrier.PropertyChanged -= Carrier_PropertyChanged;
                    }
                    carrier = value;
                    if (carrier != null)
                    {
                        carrier.PropertyChanged += Carrier_PropertyChanged;
                    }
                    OnPropertyChanged(nameof(Carrier));
                }
            }
        }
        private void Modulator_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            OnPropertyChanged(nameof(Modulator));
        }
        private void Carrier_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            OnPropertyChanged(nameof(Carrier));
        }

        public override void Reset()
        {
            base.Reset();
            if (Modulator != null)
            {
                Modulator.Reset();
            }
            if (Carrier != null)
            {
                Carrier.Reset();
            }
        }
        override public double GetNext(double frequency, double SampleRate, double modulatorValue)
        {
            if (Modulator!=null && Carrier!=null)
            {
                Modulator.Ended = Carrier.Ended = Ended;
                Modulator.TRelease = Carrier.TRelease = TRelease;
                
                return Carrier.GetNext(frequency,SampleRate, Modulator.GetNext(frequency,SampleRate, modulatorValue));
            }
            return 0;
        }

        public override double maxLevels(double currentLevel)
        {
            double l1 = currentLevel;
            double l2 = currentLevel;
            if (Carrier != null) l1 =  Carrier.maxLevels(currentLevel + 1);
            if (Modulator != null) l2 = Modulator.maxLevels(currentLevel + 1);
            return Math.Max(l1, l2) ;
        }

        override public bool DoRelease()
        {
            bool ret = base.DoRelease();
            if (Carrier != null)
            {
                ret = ret & Carrier.DoRelease();
            }
            if (Modulator != null)
            {
                ret = ret & Modulator.DoRelease();
            }
            return ret;
        }
    }
}
