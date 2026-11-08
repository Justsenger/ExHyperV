using System.Globalization;

namespace ExHyperV.WinUI3.UI.Pages.VirtualMachines.Settings;

/// <summary>用十进制进行界面步进，保留原始整数精度；业务范围仍由 Core 校验。</summary>
internal static class SettingsNumericStep
{
    public static bool TryGetValue(string text, Type propertyType, decimal scale, int direction,
        decimal minimum, decimal? maximum, out string value)
    {
        value = text;
        if (scale <= 0 || direction is not (-1 or 1)) return false;
        decimal current = 0;
        if (!string.IsNullOrWhiteSpace(text) && !decimal.TryParse(text, NumberStyles.Number & ~NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out current)) return false;
        try
        {
            decimal next = current + direction;
            if (next < minimum || maximum is { } max && next > max) return false;
            decimal raw = next * scale;
            if (decimal.Truncate(raw) != raw) return false;
            _ = Convert.ChangeType(raw, Nullable.GetUnderlyingType(propertyType) ?? propertyType, CultureInfo.InvariantCulture);
            value = next.ToString(CultureInfo.InvariantCulture);
            return true;
        }
        catch (Exception ex) when (ex is OverflowException or InvalidCastException or FormatException) { return false; }
    }
}
