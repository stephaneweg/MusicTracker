using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MusicTracker.Engine
{
    public struct SequencerSlice
    {
        UInt64 notesLow;
        UInt64 notesHigh;

        // Public accessors so a slice array can be serialized (e.g. when saving a Riff).
        public UInt64 NotesLow { get { return notesLow; } set { notesLow = value; } }
        public UInt64 NotesHigh { get { return notesHigh; } set { notesHigh = value; } }

        public void Reset()
        {
            notesLow = 0;
            notesHigh = 0;
        }
        public bool On(int note, bool on)
        {
            bool oldValue = On(note);
            if (note < 64)
            {
                UInt64 mask = (UInt64)1 << note;
                if (on)
                {
                    notesLow |= mask;
                }
                else
                {
                    notesLow &= ~mask;
                }
            }
            else
            {
                UInt64 mask = (UInt64)1 << (note - 64);
                if (on)
                {
                    notesHigh |= mask;
                }
                else
                {
                    notesHigh &= ~mask;
                }
            }
            return on != oldValue;
        }

        public Boolean On(int note)
        {
            if (note < 64)
            {
                return (notesLow & ((UInt64)1 << note)) != 0;
            }
            else
            {
                return (notesHigh & ((UInt64)1 << (note - 64))) != 0;
            }
        }
    }

}
