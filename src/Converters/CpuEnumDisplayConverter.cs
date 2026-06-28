using System;
using System.Globalization;
using System.Windows.Data;
using ExHyperV.Models;

namespace ExHyperV.Converters
{
    /// <summary>
    /// 把 CPU 设置里的枚举（ApicMode / L3 分布策略 / 大页拆分）映射为本地化显示文本。
    /// 资源键约定：CpuEnum_{前缀}_{枚举名}，缺失则回退到枚举名本身。
    /// 仅用于 ComboBox.ItemTemplate 的单向展示。
    /// </summary>
    public class CpuEnumDisplayConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value == null) return string.Empty;

            string prefix = value switch
            {
                VmApicMode => "Apic",
                L3DistributionPolicy => "L3",
                PageShatterMode => "Shatter",
                _ => null
            };
            if (prefix == null) return value.ToString();

            string key = $"CpuEnum_{prefix}_{value}";
            string text = Properties.Resources.ResourceManager.GetString(key, Properties.Resources.Culture);
            return string.IsNullOrEmpty(text) ? value.ToString() : text;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => Binding.DoNothing;
    }
}
