using System.Text.RegularExpressions;

namespace ExHyperV.Tools
{
    /// <summary>提供显示用 MAC 格式化及 WMI 写入用规范化，二者空值语义不同。</summary>
    internal static class MacAddress
    {
        /// <summary>把可识别的 12 位 MAC 格式化为冒号分隔的大写文本。</summary>
        /// <remarks>用于地址显示；空输入返回占位值。</remarks>
        /// <returns>空输入回退 00:15:5D:00:00:00；清理后不足 12 位时保留原输入。</returns>
        /// <param name="rawMac">MAC 原始文本；会去除非十六进制字符。</param>
        public static string Format(string? rawMac)
        {
            // 空串回退默认 Hyper-V MAC 前缀（Microsoft OUI 00:15:5D + 全 0）
            if (string.IsNullOrEmpty(rawMac)) return "00:15:5D:00:00:00";
            string clean = Regex.Replace(rawMac.ToUpperInvariant(), "[^0-9A-F]", "");
            if (clean.Length != 12) return rawMac;
            return Regex.Replace(clean, ".{2}", "$0:").TrimEnd(':');
        }

        /// <summary>去掉非十六进制字符并生成 WMI 使用的无分隔大写 MAC。</summary>
        /// <remarks>这不是严格的原始格式验证；任意非十六进制字符都会被剔除。</remarks>
        /// <returns>空白输入返回空字符串表示动态 MAC；12 位输入返回规范值，其他长度返回 null。</returns>
        /// <param name="rawMac">原始 MAC 文本。</param>
        public static string? Normalize(string? rawMac)
        {
            if (string.IsNullOrWhiteSpace(rawMac)) return string.Empty;
            string clean = Regex.Replace(rawMac.ToUpperInvariant(), "[^0-9A-F]", "");
            return clean.Length == 12 ? clean : null;
        }
    }
}
