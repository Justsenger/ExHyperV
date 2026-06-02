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
        /// 从原始消息里取最后一句（按 。或 . 切）；若含 "Storage_Error_X" / "Storage_Msg_X" 资源 key 模式则直接返回 key 名。
        /// </summary>
        public static string LastSentence(string rawMessage)
        {
            if (string.IsNullOrWhiteSpace(rawMessage)) return "Storage_Error_Unknown";
            var match = Regex.Match(rawMessage, @"Storage_(Error|Msg)_[A-Za-z0-9_]+");
            if (match.Success) return match.Value;
            string cleanMsg = Regex.Replace(rawMessage.Trim(), @"[\(\（].*?ID\s+[a-fA-F0-9-]{36}.*?[\)\）]", "")
                                   .Replace("\r", "").Replace("\n", " ");
            var parts = cleanMsg.Split(new[] { '。', '.' }, StringSplitOptions.RemoveEmptyEntries)
                                .Select(s => s.Trim())
                                .Where(s => !string.IsNullOrWhiteSpace(s))
                                .ToList();
            return (parts.Count >= 2 && parts.Last().Length > 2) ? parts.Last() + "。" : cleanMsg;
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
