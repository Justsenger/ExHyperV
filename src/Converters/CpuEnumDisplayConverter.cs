using System;
using System.Globalization;
using System.Windows.Data;
using ExHyperV.Models;

namespace ExHyperV.Converters
{
    /// <summary>
    /// 把 CPU 设置里的枚举（超线程 / ApicMode / L3 分布策略 / 大页拆分）映射为本地化显示文本。
    /// 资源键约定：CpuEnum_{前缀}_{枚举名}，缺失则回退到枚举名本身。
    /// 仅用于 ComboBox.ItemTemplate 的单向展示。
    /// </summary>
    public class CpuEnumDisplayConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value == null) return string.Empty;

            return value switch
            {
                SmtMode.Inherit => Properties.Resources.CpuEnum_Smt_Inherit,
                SmtMode.SingleThread => Properties.Resources.CpuEnum_Smt_SingleThread,
                SmtMode.MultiThread => Properties.Resources.CpuEnum_Smt_MultiThread,
                VmMigrationCompatibilityMode.MinimumFeatureSet => Properties.Resources.CpuEnum_Migration_MinimumFeatureSet,
                VmMigrationCompatibilityMode.CommonClusterFeatureSet => Properties.Resources.CpuEnum_Migration_CommonClusterFeatureSet,
                VmApicMode.Default => Properties.Resources.CpuEnum_Apic_Default,
                VmApicMode.Legacy => Properties.Resources.CpuEnum_Apic_Legacy,
                VmApicMode.Apic => Properties.Resources.CpuEnum_Apic_Apic,
                VmApicMode.X2Apic => Properties.Resources.CpuEnum_Apic_X2Apic,
                L3DistributionPolicy.SmallToLarge => Properties.Resources.CpuEnum_L3_SmallToLarge,
                L3DistributionPolicy.LargeToSmall => Properties.Resources.CpuEnum_L3_LargeToSmall,
                L3DistributionPolicy.EvenSmallToLarge => Properties.Resources.CpuEnum_L3_EvenSmallToLarge,
                L3DistributionPolicy.EvenLargeToSmall => Properties.Resources.CpuEnum_L3_EvenLargeToSmall,
                PageShatterMode.Default => Properties.Resources.CpuEnum_Shatter_Default,
                PageShatterMode.AlwaysEnabled => Properties.Resources.CpuEnum_Shatter_AlwaysEnabled,
                PageShatterMode.AlwaysDisabled => Properties.Resources.CpuEnum_Shatter_AlwaysDisabled,
                LpiMode.Default => Properties.Resources.CpuEnum_Lpi_Default,
                LpiMode.Disabled => Properties.Resources.CpuEnum_Lpi_Disabled,
                LpiMode.Enabled => Properties.Resources.CpuEnum_Lpi_Enabled,
                _ => value.ToString() ?? string.Empty
            };
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => Binding.DoNothing;
    }
}
