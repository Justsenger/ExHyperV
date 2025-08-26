using System.Globalization;
using System.Windows.Data;
using ExHyperV.Tools; // 确保引用了Utils所在的命名空间

namespace ExHyperV.Converters
{
    public class MemoryImagePathConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is string manufacturer)
            {
                return Utils.GetMemoryImagePath(manufacturer);
            }
            return Utils.GetMemoryImagePath("Default"); // 返回默认图片路径
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}