using ExHyperV.UI.Shared.Imaging;
using System.Globalization;
using System.Windows.Data;
using ExHyperV.Models;

namespace ExHyperV.UI.Pages.VirtualMachines.Converters;

public sealed class PartitionIconConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        OperatingSystemType.Windows => VectorIcons.TryGet("Windows"),
        OperatingSystemType.Linux => VectorIcons.TryGet("Linux"),
        _ => null
    };
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
