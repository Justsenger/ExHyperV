using System.Text.RegularExpressions;

namespace ExHyperV.Tools
{
    /// <summary>MAC 地址字符串格式化（清掉分隔符 → 重排为冒号分隔大写）。</summary>
    public static class MacAddress
    {
        public static string Format(string? rawMac)
        {
            // 空串回退默认 Hyper-V MAC 前缀（Microsoft OUI 00:15:5D + 全 0）
            if (string.IsNullOrEmpty(rawMac)) return "00:15:5D:00:00:00";
            string clean = Regex.Replace(rawMac.ToUpperInvariant(), "[^0-9A-F]", "");
            if (clean.Length != 12) return rawMac;
            return Regex.Replace(clean, ".{2}", "$0:").TrimEnd(':');
        }

        /// <summary>规范化为 12 位无分隔大写 hex（写入 WMI Address 用）。空串=动态(返回 "")；非 12 位=非法(返回 null)。</summary>
        public static string? Normalize(string? rawMac)
        {
            if (string.IsNullOrWhiteSpace(rawMac)) return string.Empty;
            string clean = Regex.Replace(rawMac.ToUpperInvariant(), "[^0-9A-F]", "");
            return clean.Length == 12 ? clean : null;
        }
    }
}
