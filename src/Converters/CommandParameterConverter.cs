using System.Globalization;
using System.Windows.Data;

namespace ExHyperV.Converters
{
    /// <summary>
    /// 一个多值转换器，它将多个绑定值“打包”成一个 object[] 数组。
    /// </summary>
    public class CommandParameterConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            return values.Clone();
        }
        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}