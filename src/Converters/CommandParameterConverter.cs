using System;
using System.Globalization;
using System.Windows.Data;

namespace ExHyperV.Converters
{
    /// <summary>
    /// 一个多值转换器，它将多个绑定值“打包”成一个 object[] 数组。
    /// </summary>
    public class CommandParameterConverter : IMultiValueConverter
    {
        /// <summary>
        /// “打包”过程：接收多个值，返回一个包含这些值的数组。
        /// </summary>
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            // 直接返回包含所有输入值的数组的克隆。
            return values.Clone();
        }

        /// <summary>
        /// “解包”过程：我们不需要这个功能，所以保持未实现状态。
        /// </summary>
        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}