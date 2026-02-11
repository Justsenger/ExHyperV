using ExHyperV.Models; // 确保引用了包含 VlanOperationMode 枚举的命名空间
using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace ExHyperV.Converters
{
    /// <summary>
    /// 值转换器：根据当前的 VLAN 模式 (VlanOperationMode) 和一个参数来决定控件是否应该可见 (Visibility)。
    /// 例如，当用户在下拉框中选择 "Access" 模式时，只有与 "Access" 相关的输入框才会被设为 Visible。
    /// </summary>
    public class VlanModeToVisibilityConverter : IValueConverter
    {
        /// <summary>
        /// 正向转换：从数据源 (VlanOperationMode) 转换为 UI 属性 (Visibility)。
        /// </summary>
        /// <param name="value">绑定的数据源，我们期望它是一个 VlanOperationMode 枚举值。</param>
        /// <param name="targetType">目标属性的类型，这里是 Visibility。</param>
        /// <param name="parameter">在 XAML 中指定的转换器参数，它是一个字符串，代表我们期望匹配的 VLAN 模式 (例如 "Access", "Trunk" 或 "Private")。</param>
        /// <param name="culture">区域性信息，此处未使用。</param>
        /// <returns>如果当前绑定的 VLAN 模式与参数中指定的模式相匹配，则返回 Visibility.Visible，否则返回 Visibility.Collapsed。</returns>
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            // 确保绑定的值是 VlanOperationMode 类型，并且参数是一个字符串
            if (value is VlanOperationMode currentMode && parameter is string targetModeString)
            {
                // 尝试将 XAML 中传入的字符串参数 (如 "Access") 解析成等效的 VlanOperationMode 枚举成员
                if (Enum.TryParse(targetModeString, out VlanOperationMode targetMode))
                {
                    // 比较当前虚拟机网卡的模式和我们期望的模式是否一致
                    // 如果一致，就让控件显示出来；否则就折叠隐藏。
                    return currentMode == targetMode ? Visibility.Visible : Visibility.Collapsed;
                }
            }

            // 如果绑定的值或参数类型不正确，或者参数无法解析，则默认隐藏控件，保证界面的健壮性。
            return Visibility.Collapsed;
        }

        /// <summary>
        /// 反向转换：从 UI (Visibility) 转回数据源 (VlanOperationMode)。
        /// 这个方向的转换在此场景下没有逻辑意义，因此我们不实现它。
        /// </summary>
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            // 这是一个单向转换器，所以反向转换会抛出异常。
            throw new NotImplementedException();
        }
    }
}