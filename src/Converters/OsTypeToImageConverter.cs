using System;
using System.Windows.Data;
using ExHyperV.Tools;

namespace ExHyperV.Converters
{
    public class OsTypeToImageConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            // 原生 WPF 矢量图标；尚未迁移的类型返回 null（暂不显示）。
            return VectorIcons.TryGet(OsImages.Canonical(value?.ToString()));
        }

        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
            => Binding.DoNothing;
    }
}
