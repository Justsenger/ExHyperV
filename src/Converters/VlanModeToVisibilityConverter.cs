using ExHyperV.Models;
using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace ExHyperV.Converters
{
    /// <summary>仅显示与转换器参数匹配的 VLAN 模式区域。</summary>
    public class VlanModeToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is VlanOperationMode currentMode && parameter is string targetModeString)
            {
                if (Enum.TryParse(targetModeString, out VlanOperationMode targetMode))
                {
                    return currentMode == targetMode ? Visibility.Visible : Visibility.Collapsed;
                }
            }

            return Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return Binding.DoNothing;
        }
    }
}
