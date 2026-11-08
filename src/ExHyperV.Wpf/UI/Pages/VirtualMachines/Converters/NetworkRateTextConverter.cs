using System.Globalization;
using System.Windows.Data;

namespace ExHyperV.UI.Pages.VirtualMachines.Converters;

public sealed class NetworkRateTextConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 3 || values[0] is not ulong send || values[1] is not ulong receive) return "";
        // 合并层在缺测时保留最后一次有效值；有效性变化不使文字跳回零。
        return $"↑ {Format(send, culture)}   ↓ {Format(receive, culture)} ";
    }

    private static string Format(ulong bytes, CultureInfo culture)
    {
        string[] units = ["B/s", "KiB/s", "MiB/s", "GiB/s"];
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return (unit == 0 ? bytes.ToString(CultureInfo.InvariantCulture) : value.ToString("0.##", culture)) + " " + units[unit];
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => targetTypes.Select(_ => Binding.DoNothing).ToArray();
}
