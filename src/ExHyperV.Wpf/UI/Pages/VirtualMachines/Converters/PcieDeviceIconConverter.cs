using System.Globalization;
using System.Windows.Data;
using ExHyperV.Models;
using ExHyperV.UI.Shared.Imaging;

namespace ExHyperV.UI.Pages.VirtualMachines.Converters;

public sealed class PcieDeviceIconConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is VmPcieDeviceSetting device ? DeviceIcons.GetGlyph(device.ClassType, device.Name) : "\uE950";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
