using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using ExHyperV.Models; // 确保引用了 Models 命名空间

namespace ExHyperV.Converters
{
    // 必须是 public class
    public class CoreTypeToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            // 这里的 CoreType 必须是你 Models 里定义的枚举
            if (value is CoreType coreType && parameter is string targetTypeStr)
            {
                if (Enum.TryParse(targetTypeStr, true, out CoreType target))
                {
                    return coreType == target ? Visibility.Visible : Visibility.Collapsed;
                }
            }
            return Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}