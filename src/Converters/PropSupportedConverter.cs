using System.Collections.Generic;
using System.Globalization;
using System.Windows.Data;

namespace ExHyperV.Converters
{
    /// <summary>属性存在性 → 门控 Tag：value 是 SupportedProps(schema 属性名集合)，parameter 是 WMI 属性名。
    /// 含则返回 true(非 null → 控件可用)，不含则返回 null(→ ShutdownRequired* 样式按 Tag==null 置灰"版本不支持")。
    /// 用于频率字段：值保持 null(显示空白、不误写默认)，而"支不支持"改看 HasProperty，不再看值。</summary>
    public class PropSupportedConverter : IValueConverter
    {
        public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value is HashSet<string> set && parameter is string name && set.Contains(name) ? (object)true : null;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => Binding.DoNothing;
    }
}
