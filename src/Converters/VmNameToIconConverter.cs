using System;
using System.Globalization;
using System.Windows.Data;

namespace ExHyperV.Converters
{
    public class VmNameToIconConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is string name)
            {
                return name.Equals("Host", StringComparison.OrdinalIgnoreCase) ? "\uE977" : "\uE7F8";
            }
            return "\uE7F8";
        }
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}