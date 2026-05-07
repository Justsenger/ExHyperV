using System.Globalization;
using System.Windows.Data;

namespace ExHyperV.Converters;

public class EnumToBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value?.ToString() == parameter?.ToString();

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => (bool)value ? Enum.Parse(targetType, parameter!.ToString()!) : Binding.DoNothing;
}