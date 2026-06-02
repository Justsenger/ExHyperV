using System.Globalization;
using System.Windows.Data;

namespace ExHyperV.Converters
{
    public class DiskTypeToTextConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is string type)
            {
                return type.ToUpper() switch
                {
                    "DYNAMIC" => Properties.Resources.Disk_Dynamic,
                    "FIXED" => Properties.Resources.Disk_Fixed,
                    "DIFFERENCING" => Properties.Resources.Disk_Differencing,
                    "ISO" => Properties.Resources.Disk_IsoImage,
                    "PHYSICAL" => Properties.Resources.Disk_Physical,
                    "DVDDRIVE" => Properties.Resources.Disk_PhysicalDvd,
                    _ => type // 如果没匹配上，返回原样
                };
            }
            return value;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return Binding.DoNothing;
        }
    }
}