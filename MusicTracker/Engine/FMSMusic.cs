using NAudio.SoundFont;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.ComTypes;
using System.Security.Policy;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using static System.Net.Mime.MediaTypeNames;

namespace MusicTracker.Engine
{
    public class FMSMusic
    {
        public string[] InstrumentNames { get; set; }
        public FrequencyModulationWaveFunction[] Instruments;
        public List<int>[] Notes;
        public int[] noteIdx;
        public FMSMusic()
        {
            InstrumentNames = new string[8];
            Notes = new List<int>[8] {new List<int>(), new List<int>(), new List<int>(), new List<int>(), new List<int>(), new List<int>(), new List<int>(), new List<int>()};
            noteIdx = new int[8] { 0,0,0,0,0,0,0,0};
            InitInstruments();
        }

        public void InitInstruments()
        {
            Instruments = new FrequencyModulationWaveFunction[8];
            for (int i = 0; i < Instruments.Length; i++)
            {
                Instruments[i] = new FrequencyModulationWaveFunction
                {
                    Carrier = new EnveloppeADSRWaveFunction
                    {
                        Attack = 0.01,
                        Decay = 0.01,
                        Sustain = 0.5,
                        Release = 0.01,
                        WaveFunction = new FrequencyModifierWaveFunction
                        {
                            FrequencyModifier = 1,
                            WaveFunction = new SineWaveFunction()
                        }
                    },
                    Modulator = new EnveloppeADSRWaveFunction
                    {
                        Attack = 0.01,
                        Decay = 0.01,
                        Sustain = 0.5,
                        Release = 0.01,
                        WaveFunction = new FrequencyModifierWaveFunction
                        {
                            FrequencyModifier = 1,
                            WaveFunction = new SineWaveFunction()
                        }
                    }
                };
            }
        }
        
        void SetWaveFunction(int instrument, WaveFunction carrier, WaveFunction modulator)
        {
           ( (Instruments[instrument].Carrier as EnveloppeADSRWaveFunction).WaveFunction as FrequencyModifierWaveFunction).WaveFunction = carrier;
            ((Instruments[instrument].Modulator as EnveloppeADSRWaveFunction).WaveFunction as FrequencyModifierWaveFunction).WaveFunction = modulator;
        }

        void SetFrequencyMultiplier1(int instrument,double mul)
        {
            ((Instruments[instrument].Carrier as EnveloppeADSRWaveFunction).WaveFunction as FrequencyModifierWaveFunction).FrequencyModifier = mul;
        }
        void SetFrequencyMultiplier2(int instrument, double mul)
        {
            ((Instruments[instrument].Modulator as EnveloppeADSRWaveFunction).WaveFunction as FrequencyModifierWaveFunction).FrequencyModifier = mul;
        }

        void SetEnvelope1(int instrument , double attack,double decay,double sustain, double release)
        {
            (Instruments[instrument].Carrier as EnveloppeADSRWaveFunction).Attack = attack;
            (Instruments[instrument].Carrier as EnveloppeADSRWaveFunction).Decay = decay;
            (Instruments[instrument].Carrier as EnveloppeADSRWaveFunction).Sustain = sustain;
            (Instruments[instrument].Carrier as EnveloppeADSRWaveFunction).Release = release;
        }
        void SetEnvelope2(int instrument, double attack, double decay, double sustain, double release)
        {
            (Instruments[instrument].Modulator as EnveloppeADSRWaveFunction).Attack = attack;
            (Instruments[instrument].Modulator as EnveloppeADSRWaveFunction).Decay = decay;
            (Instruments[instrument].Modulator as EnveloppeADSRWaveFunction).Sustain = sustain;
            (Instruments[instrument].Modulator as EnveloppeADSRWaveFunction).Release = release;
        }


        static WaveFunction getWaveFunction(int num)
        {
            switch(num)
            {
                case 0:
                    return new SineWaveFunction();
                case 1:
                    return new SquareWaveFunction();
                case 2:
                    return new TriangleWaveFunction();
                case 3:
                    return new SawtoothWaveFunction();
                case 4:
                    return new NoiseWaveFunction();
            }
            return new SineWaveFunction();
        }
        public static FMSMusic Import(string path)
        {
            if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)) return null;

            FMSMusic music = new FMSMusic();

            System.IO.BinaryReader reader =new System.IO.BinaryReader(System.IO.File.OpenRead(path));

            var header = reader.ReadBytes(15);
            var pname = reader.ReadBytes(20);
            var auteur = reader.ReadBytes(20);
            var comment = reader.ReadBytes(50);
            var maxTrack = reader.ReadInt16();

            for (int i=0;i<8;i++)
            {
                var instrumentName = reader.ReadBytes(8);
                music.InstrumentNames[i] = Encoding.ASCII.GetString(instrumentName);

                double attack1 = reader.ReadByte();
                double attack2 = reader.ReadByte();
                double decay1 = reader.ReadByte();
                double decay2 = reader.ReadByte();
                double sustain1 = reader.ReadByte();
                double sustain2 = reader.ReadByte();
                double released1 = reader.ReadByte();
                double released2 = reader.ReadByte();

                double outp1 = reader.ReadByte();
                double outp2 = reader.ReadByte();
                double scaling1 = reader.ReadByte();
                double scaling2 = reader.ReadByte();
                double amplitudeVibrato1 = reader.ReadByte();
                double amplitudeVibrato2 = reader.ReadByte();
                double pitchVibrato1 = reader.ReadByte();
                double pitchVibrato2 = reader.ReadByte();
                double freqMultiplier1 = reader.ReadByte();
                double freqMultiplier2 = reader.ReadByte();
                double envelopeScaling1 = reader.ReadByte();
                double envelopeScaling2 = reader.ReadByte();
                byte waveSelect1 = reader.ReadByte();
                byte waveSelect2 = reader.ReadByte();
                double feedback = reader.ReadByte();
                double connection = reader.ReadByte();
                double sustaininglevel1 = reader.ReadByte();
                double sustaininglevel2 = reader.ReadByte();

                WaveFunction waveCarrier = getWaveFunction(waveSelect1);
                WaveFunction waveModulator = getWaveFunction(waveSelect2);
               
                music.SetWaveFunction(i,waveCarrier,waveModulator);
                music.SetEnvelope1(i, 1 - attack1 / 15d, 1 - decay1 / 15d, sustain1/15d, 1 - released1 / 15d);
                music.SetEnvelope2(i, 1 - attack2 / 15d, 1 - decay2 / 15d, sustain2/15d, 1 - released2 / 15d);
                music.SetFrequencyMultiplier1(i, 1);
                music.SetFrequencyMultiplier2(i, 1);
            }


            for(int i=0;i<maxTrack && i<4;i++)
            {
                short mNotes = reader.ReadInt16();
                short trackDelay = reader.ReadInt16();
                for(int cn=0;cn<8;cn++)
                {
                    byte chanelEnabled = reader.ReadByte();
                }

                for (int k=0;k<8;k++)
                {
                    for (int j = 0; j < mNotes ; j++)
                    {
                        int note = 0;
                        byte varnote = reader.ReadByte();
                        byte octave = (byte)((varnote & 56) / 8);
                        byte anote = (byte)(varnote & 7);
                        byte diese = (byte)((varnote & 64) / 64);
                        byte no = (byte)((varnote & 128) / 128);

                        if (no == 1)
                        {
                            note = -1;
                        }
                        else
                        {
                            switch (anote)
                            {
                                case 1://do
                                    note = (1 + diese) + 12 * octave;
                                    break;
                                case 2://ré
                                    note = (3 + diese) + 12 * octave;
                                    break;
                                case 3://mi
                                    note = (5) + 12 * octave;
                                    break;
                                case 4://fa
                                    note = (6 + diese) + 12 * octave;
                                    break;
                                case 5://sol
                                    note = (8 + diese) + 12 * octave;
                                    break;
                                case 6://la
                                    note = (10 + diese) + 12 * octave;
                                    break;
                                case 7://si
                                    note = (12 + diese) + 12 * octave;
                                    break;
                            }
                        }

                        music.Notes[k].Add(note);
                        music.noteIdx[k] += 1;
                    }
                }

            }






            reader.Dispose();

            return music;
        }
    }
}
