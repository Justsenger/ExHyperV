using System.Globalization;
using System.Windows.Data;

namespace ExHyperV.UI.Pages.VirtualMachines.Converters;

public sealed class DiskUsageTextConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 2 || values[0] is not long current || values[1] is not long maximum) return "";
        return $"{Format(current, culture)} / {Format(maximum, culture)}";
    }

    private static string Format(long bytes, CultureInfo culture)
    {
        if (bytes <= 0) return "0 B";
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return value.ToString("0.##", culture) + " " + units[unit];
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => targetTypes.Select(_ => Binding.DoNothing).ToArray();
}
