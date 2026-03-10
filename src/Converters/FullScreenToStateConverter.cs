using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace ExHyperV.Converters
{
    public class FullScreenToStateConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return (bool)value ? WindowState.Maximized : WindowState.Normal;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            // 安全起见，永远不要在 Converter 里面 throw 异常
            return Binding.DoNothing;
        }
    }
}