using System.Text.RegularExpressions;

namespace ExHyperV.Tools
{
    /// <summary>
    /// 把 raw 错误消息清洗成对用户友好的展示文本。
    /// 两种粒度：截最后一句 / 多行去重清洗。
    /// </summary>
    public static class FriendlyError
    {
        /// <summary>
        /// 从原始消息里取最后一句（按 。或 . 切）。
        /// </summary>
        public static string LastSentence(string rawMessage)
        {
            if (string.IsNullOrWhiteSpace(rawMessage)) return Properties.Resources.Error_Storage_Unknown;
            string cleanMsg = Regex.Replace(rawMessage.Trim(), @"[\(\（].*?ID\s+[a-fA-F0-9-]{36}.*?[\)\）]", "")
                                   .Replace("\r", " ").Replace("\n", " ").Trim();
            // 断句只认中文句号/分号、以及"英文句点后接空白或结尾"——不能裸按 '.' 切：Windows 路径/文件名(.vhdx)、
            // 版本号里全是点，裸切会把 "F:\x.vhdx" 劈碎，末段常只剩个 "(0x….)" 错误码，真正原因(如差异盘父盘丢失)反被丢掉。
            var parts = Regex.Split(cleanMsg, @"[。；;]|\.(?=\s|$)")
                             .Select(s => s.Trim())
                             .Where(s => !string.IsNullOrWhiteSpace(s))
                             .ToList();
            if (parts.Count == 0) return cleanMsg;
            // 从后往前取第一句有实质内容的：剥掉引号/括号/0x 错误码后仍有文字才算原因句，跳过 "(0x….)" 这种纯尾巴
            for (int i = parts.Count - 1; i >= 0; i--)
            {
                string stripped = Regex.Replace(parts[i], @"0x[0-9a-fA-F]+|[""“”'（）()\s]", "");
                if (stripped.Length > 2) return parts[i] + "。";
            }
            return cleanMsg;
        }

        /// <summary>
        /// 多行清洗：剥掉 GUID 括号注解、统一去引号去句号、按内容去重，最后换行拼回。
        /// </summary>
        public static string CleanLines(string rawMessage)
        {
            if (string.IsNullOrWhiteSpace(rawMessage)) return string.Empty;

            var guidInParensRegex = new Regex(@"\s*[\(（].*?[a-fA-F0-9]{8}-(?:[a-fA-F0-9]{4}-){3}[a-fA-F0-9]{12}.*?[\)）]");
            string[] lines = rawMessage.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);

            var distinctLines = lines
                .Select(line => guidInParensRegex.Replace(line, ""))
                .Select(line => line.Trim().Trim('"', '“', '”').TrimEnd('.', '。'))
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .Distinct(StringComparer.Ordinal);

            string finalMessage = string.Join(Environment.NewLine, distinctLines);
            return string.IsNullOrWhiteSpace(finalMessage) ? rawMessage.Trim() : finalMessage;
        }
    }
}
