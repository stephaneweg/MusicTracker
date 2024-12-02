using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace MusicTracker
{
    public class ItemIndexCornerRadiusConverter : IValueConverter
    {

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            DependencyObject item = (DependencyObject)value;
            ItemsControl ic = ItemsControl.ItemsControlFromItemContainer(item);

            if (ic != null)
            {
                var idx = ic.ItemContainerGenerator.IndexFromContainer(item);
                if (idx == 0)
                {
                    return new CornerRadius(3, 0, 0, 3);
                }
                if (idx == ic.Items.Count - 1)
                {
                    return new CornerRadius(0, 3, 3, 0);
                }
                return new CornerRadius(0, 0, 0, 0);
            }
            else
                return new CornerRadius(0, 0, 0, 0);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
    public class IsLastItemInContainerConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter,
                              CultureInfo culture)
        {
            DependencyObject item = (DependencyObject)value;
            ItemsControl ic = ItemsControl.ItemsControlFromItemContainer(item);

            if (ic != null)
            {
                var idx = ic.ItemContainerGenerator.IndexFromContainer(item);
                return idx == ic.Items.Count - 1;
            }
            else
                return false;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }


    public class IsFirstItemInContainerConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter,
                              CultureInfo culture)
        {
            DependencyObject item = (DependencyObject)value;
            ItemsControl ic = ItemsControl.ItemsControlFromItemContainer(item);

            if (ic != null)
            {
                var idx = ic.ItemContainerGenerator.IndexFromContainer(item);
                return idx == 0;
            }
            else
                return false;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
