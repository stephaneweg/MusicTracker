using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;

namespace MusicTracker
{
    public static class UIUtils
    {
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
