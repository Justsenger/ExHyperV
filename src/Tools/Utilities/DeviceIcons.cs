using Wpf.Ui.Controls;

namespace ExHyperV.Tools
{
    /// <summary>
    /// 把设备类型映射到 Segoe Fluent 图标 glyph，并构造对应的 FontIcon UI 元素。
    /// </summary>
    public static class DeviceIcons
    {
        /// <summary>根据设备类型/友好名返回单字符 Segoe Fluent glyph。</summary>
        public static string GetGlyph(string deviceType, string friendlyName)
        {
            switch (deviceType)
            {
                case "Switch":
                    return "\xF597";
                case "Upstream":
                    return "";
                case "Display":
                    return "\xF211";
                case "Net":
                    return "\xE839";
                case "USB":
                    return friendlyName.Contains("USB4")
                        ? "\xE945"
                        : "\xECF0";
                case "HIDClass":
                    return "\xE928";
                case "SCSIAdapter":
                case "HDC":
                    return "\xEDA2";
                default:
                    return friendlyName.Contains("Audio")
                        ? "\xE995"
                        : "\xE950";
            }
        }

        /// <summary>构造一个 FontIcon 控件，glyph 来自 <see cref="GetGlyph"/>。</summary>
        public static FontIcon CreateFontIcon(string classType, string friendlyName)
        {
            return new FontIcon
            {
                FontSize = 24,
                FontFamily = (FontFamily)Application.Current.Resources["SegoeFluentIcons"],
                Glyph = GetGlyph(classType, friendlyName)
            };
        }
    }
}
