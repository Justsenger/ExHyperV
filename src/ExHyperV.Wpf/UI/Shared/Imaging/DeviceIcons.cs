using ExHyperV.Tools;
using Wpf.Ui.Controls;

namespace ExHyperV.UI.Shared.Imaging
{
    /// <summary>
    /// 把设备类型映射到 Segoe Fluent 图标 glyph，并构造对应的 FontIcon UI 元素。
    /// </summary>
    public static class DeviceIcons
    {
        /// <summary>根据设备类型/友好名返回单字符 Segoe Fluent glyph。</summary>
        public static string GetGlyph(string deviceType, string friendlyName) => deviceType switch
        {
            "Switch" => "\uF597",
            "Upstream" => "\uE774",
            "Display" => "\uF211",
            "Net" => "\uE839",
            "USB" => friendlyName.Contains("USB4") ? "\uE945" : "\uECF0",
            "HIDClass" => "\uE928",
            "SCSIAdapter" or "HDC" => "\uEDA2",
            "ComputeAccelerator" => "\uEEA1",
            _ => friendlyName.Contains("Audio") ? "\uE995" : "\uE950"
        };

        /// <summary>构造一个 FontIcon 控件，glyph 来自 <see cref="GetGlyph"/>。</summary>
        public static FontIcon CreateFontIcon(string classType, string friendlyName)
        {
            return new FontIcon
            {
                FontSize = 24,
                FontFamily = (FontFamily)WpfApplication.Current.Resources["SegoeFluentIcons"],
                Glyph = GetGlyph(classType, friendlyName)
            };
        }
    }
}
