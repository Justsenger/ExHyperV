using System;
using System.Globalization;
using System.Windows.Data;

namespace ExHyperV.Converters
{
    public class EnumToBooleanConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value == null || parameter == null)
                return false;

            string enumValue = value.ToString();
            string targetValue = parameter.ToString();
            return enumValue.Equals(targetValue, StringComparison.OrdinalIgnoreCase);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool isChecked && isChecked)
            {
                if (parameter is string parameterString)
                {
                    return Enum.Parse(targetType, parameterString, true);
                }
            }
            return Binding.DoNothing;
        }
    }
}