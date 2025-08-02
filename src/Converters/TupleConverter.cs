// /Converters/TupleConverter.cs
using System;
using System.Globalization;
using System.Windows.Data;

namespace ExHyperV.Converters
{
    public class TupleConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            // 关键修正：返回数组的克隆，而不是原始数组的引用。
            // 这可以防止 WPF 在后续绑定更新中重用同一个数组实例，确保每次命令执行时参数都是独立的。
            return values.Clone();
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}