using System.Globalization;
using System.Windows.Data;

namespace ExHyperV.UI.Pages.VirtualMachines.Converters;

public sealed class IpAddressListConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not IReadOnlyList<string> addresses || addresses.Count == 0) return "---";
        return addresses.FirstOrDefault(ip => ip.Contains('.') && !ip.Contains(':')) ?? addresses[0];
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
