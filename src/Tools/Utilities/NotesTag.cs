namespace ExHyperV.Tools
{
    /// <summary>
    /// VM Notes 字段里的 `[TagName:Value]` 标签解析/写入。
    /// 用于把 ExHyperV 私有元数据（如 Affinity、OSType）持久化到 VM 的 Notes。
    /// </summary>
    public static class NotesTag
    {
        public static string Get(string text, string tagName)
        {
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;
            string prefix = $"[{tagName}:";
            int start = text.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
            if (start == -1) return string.Empty;
            start += prefix.Length;
            int end = text.IndexOf("]", start);
            return end == -1 ? string.Empty : text.Substring(start, end - start);
        }

        public static string Update(string text, string tagName, string newValue)
        {
            text = text ?? string.Empty;
            string tagPrefix = $"[{tagName}:";
            string newTag = $"[{tagName}:{newValue}]";
            int startIndex = text.IndexOf(tagPrefix, StringComparison.OrdinalIgnoreCase);
            if (startIndex != -1)
            {
                int endIndex = text.IndexOf("]", startIndex);
                if (endIndex != -1)
                    return text.Remove(startIndex, endIndex - startIndex + 1).Insert(startIndex, newTag);
            }
            return string.IsNullOrWhiteSpace(text) ? newTag : $"{text.Trim()} {newTag}";
        }
    }
}
