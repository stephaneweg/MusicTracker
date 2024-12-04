using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MusicTracker.Graphics
{
    public class Font
    {
        public static readonly Font ML = new Font(FontResources.ML);

       
        public Font(byte[] data)
        {
            Data = data;
            FontWidth = 8;
            FontHeight = data.Length / 256 / (FontWidth/8);
            
        }

        public byte[] Data { get; set; }
        public int FontWidth { get; set; }
        public int FontHeight { get; set; }
    }
}
