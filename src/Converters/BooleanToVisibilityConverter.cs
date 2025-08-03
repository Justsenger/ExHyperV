using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace ExHyperV.Converters
{
    /// <summary>
    /// 将布尔值转换为WPF的Visibility枚举值，支持通过ConverterParameter="Invert"进行反向转换。
    /// </summary>
    public class BooleanToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            bool boolValue = value is bool b && b;

            // 如果ConverterParameter为"Invert"，则反转布尔值。
            if (parameter is string s && s.Equals("Invert", StringComparison.OrdinalIgnoreCase))
            {
                boolValue = !boolValue;
            }

            return boolValue ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            // 单向绑定，无需实现。
            throw new NotImplementedException();
        }
    }
}