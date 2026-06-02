using System.Globalization;
using System.Windows.Data;

namespace ExHyperV.Converters
{
    /// <summary>
    /// 比较绑定值与参数（按 ToString，不区分大小写）是否相等 → bool。
    /// 支持 TwoWay：选中时回填参数；当目标属性为枚举时自动 Enum.Parse
    /// （已合并原 EnumToBoolConverter 的枚举单选用途）。
    /// </summary>
    public class EqualityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.OrdinalIgnoreCase);

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool b && b && parameter != null)
            {
                return targetType.IsEnum
                    ? Enum.Parse(targetType, parameter.ToString(), ignoreCase: true)
                    : parameter;
            }
            return Binding.DoNothing;
        }
    }
}
