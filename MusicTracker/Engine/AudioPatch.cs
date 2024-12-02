using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Markup;

namespace MusicTracker.Engine
{
    public class AudioPatch
    {
        public static List<AudioPatch> patchList;
        static AudioPatch()
        {
           patchList = new List<AudioPatch>();
            System.IO.Directory.GetFiles("AudioPatch").ToList().ForEach(f => {
                string text = System.IO.File.ReadAllText(f);
                AudioPatch patch = System.Text.Json.JsonSerializer.Deserialize<AudioPatch>(text);
                patch.Name = System.IO.Path.GetFileNameWithoutExtension(f);
                patchList.Add(patch);
            });
        }

        public string Name { get; set; }
        public class AudioPatchPart 
        {
            public AudioPatchPart()
            {
            }

            byte[] bytes;
            Int16[] doubles;
            string serialized;
            public Int16[] getDoubles() { return doubles; }

            public double BaseFrequency { get; set; }
            public double MaxFrequency { get; set; }
            public double MinFrequency { get; set; }
            public double SampleRate { get; set; }
            public string Serialized {
                get {  return serialized; }
                set {  
                    serialized = value; 
                    bytes = Convert.FromBase64String(value);
                    doubles = new Int16[((bytes.Length) / sizeof(Int16)) +1];
                    Buffer.BlockCopy(bytes, 0, doubles, 0, bytes.Length);
                }
            }

            
        }

        public List<AudioPatchPart> Parts { get; set; }

        public AudioPatchPart GetPart(double frequency)
        {
            return Parts.FirstOrDefault(p => p.MinFrequency <= frequency && p.MaxFrequency >= frequency);
        }

    }
}
