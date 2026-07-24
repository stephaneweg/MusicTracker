using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MusicTracker.Engine.AI
{
    public class RiffAiResult
    {
        public System.Collections.Generic.List<Engine.AI.AiRiffNote> notes { get; set; }
        public System.Collections.Generic.List<Engine.AI.AiChord> chords { get; set; }
        public string articulation { get; set; }
    }
}
