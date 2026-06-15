using System.Globalization;
using System.Windows.Data;
using ExHyperV.Tools;

namespace ExHyperV.Converters
{
    public class GpuImagePathConverter : IMultiValueConverter
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
