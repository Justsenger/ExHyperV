using System.Globalization;
using System.Windows.Data;

namespace ExHyperV.Converters
{
    public class ByteToBooleanConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value is byte b && b == 1;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => value is bool b && b ? (byte)1 : (byte)0;
    }
}