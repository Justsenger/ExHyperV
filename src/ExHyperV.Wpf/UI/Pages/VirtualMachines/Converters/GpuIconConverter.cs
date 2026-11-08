using ExHyperV.UI.Pages.VirtualMachines.Imaging;
using ExHyperV.UI.Shared.Imaging;
using System.Globalization;
using System.Windows.Data;

namespace ExHyperV.UI.Pages.VirtualMachines.Converters
{
    public class GpuIconConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values == null || values.Length < 2) return null;
            string manu = values[0] as string ?? "";
            string name = values[1] as string ?? "";
            return VectorIcons.TryGet(GpuImages.GetResourceKey(manu, name));
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            return null;
        }
    }
}
