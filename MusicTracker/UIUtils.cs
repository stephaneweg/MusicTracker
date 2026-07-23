using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MusicTracker
{
    public static class UIUtils
    {
        public static void Clear(UInt32[] backBuffer, int pixelWidth, int pixelHeight, Color color)
        {


            int cpt = pixelWidth * pixelHeight;
            uint colorasInteger = (uint)((color.A << 24) | (color.R << 16) | (color.G << 8) | color.B);
            for (int i = 0; i < cpt; i++)
            {
                backBuffer[i] = colorasInteger;
            }

        }
        public static void FillRectangle(UInt32[] backBuffer, int pixelWidth, int pixelHeight, int x1, int y1, int x2, int y2, Color color)
        {

            var firstX = Math.Max(0, x1);
            var firstY = Math.Max(0, y1);
            var lastX = Math.Min(pixelWidth, x2);
            var lastY = Math.Min(pixelHeight, y2);

            uint colorasInteger = (uint)((color.A << 24) | (color.R << 16) | (color.G << 8) | color.B);
            for (int y = firstY; y < lastY; y++)
            {
                int idx = y * pixelWidth + firstX;
                for (int x = firstX; x < lastX; x++)
                {
                    backBuffer[idx++] = colorasInteger;
                }
            }
        }
        static readonly GridLength fillLength = new GridLength(1, GridUnitType.Star);
        public static void InitAccordion(this Grid grid)
        {
            List<Expander> listExpanders = grid.Children.OfType<Expander>().ToList();
            while (grid.RowDefinitions.Count < listExpanders.Count)
            {
                grid.RowDefinitions.Add(new RowDefinition());
            }
            foreach (var exp in listExpanders)
            {
                exp.IsExpanded = false;
                exp.VerticalAlignment = VerticalAlignment.Stretch;
                grid.RowDefinitions[listExpanders.IndexOf(exp)].Height = GridLength.Auto;
                exp.Expanded += (oo, ee) =>
                {
                    Expander ex = (Expander)oo;
                    if (ex.IsExpanded)
                    {
                        foreach (var i in listExpanders.Where(ii => ii != ex))
                        {
                            i.IsExpanded = false;
                            grid.RowDefinitions[listExpanders.IndexOf(i)].Height = GridLength.Auto;
                        }
                        grid.RowDefinitions[listExpanders.IndexOf(ex)].Height = fillLength;
                    }
                };
                exp.Collapsed += (oo, ee) =>
                {
                    if (!listExpanders.Any(e => e.IsExpanded))
                    {
                        Expander ex = (Expander)oo;
                        ex.IsExpanded = true;
                    }
                };
            }
            if (listExpanders.Any())
            {
                listExpanders.FirstOrDefault().IsExpanded = true;
            }
        }

    }
}
