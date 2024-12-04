using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using static System.Net.Mime.MediaTypeNames;

namespace MusicTracker
{
    public static class WriteableBitmapExtension
    {
        public static void DrawText(this WriteableBitmap bmp, string text, int x, int y, Color c, Graphics.Font font)
        {
            int fontWidth = font.FontWidth;
            int fontHeight = font.FontHeight;
            byte[] fontData = font.Data;
            int stride = bmp.BackBufferStride;
            int bytesPerPixel = (bmp.Format.BitsPerPixel + 7) / 8;

            for (int cpt = 0; cpt < text.Length; cpt++)
            {
                char asciiCode = text[cpt];
                for (int rowNum = 0; rowNum < font.FontHeight; rowNum++)
                {
                    byte bdata = font.Data[asciiCode * fontHeight + rowNum];
                    for (int colNum = 0; colNum < fontWidth; colNum++)
                    {
                        if (((bdata >> colNum) & 0x1) == 0x1)
                        {
                            var xx = x + cpt * fontWidth + fontWidth - 1 - colNum;
                            var yy = y + rowNum;
                            if (xx>= 0 && xx < bmp.PixelWidth && yy >= 0 && yy < bmp.PixelHeight)
                            {
                                bmp.SetPixel(xx, yy, c);
                            }
                            
                        }
                    }
                }
            }



        }
    }
}
