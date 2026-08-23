using System;
using System.Globalization;
using System.Windows.Data;

namespace ExHyperV.Converters
{
    public sealed class IsolationTypeDisplayConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            string text = value?.ToString() ?? string.Empty;
            return string.Equals(text, "Disabled", StringComparison.Ordinal)
                ? Properties.Resources.Common_Disabled
                : text;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => Binding.DoNothing;
    }
}
