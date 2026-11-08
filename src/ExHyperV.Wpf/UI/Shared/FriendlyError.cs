using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace ExHyperV.UI.Shared
{
    /// <summary>整理面向人的错误文本，不作为稳定的错误码或异常分类接口。</summary>
    internal static class FriendlyError
    {
        // 括号(全/半角)内含 8-4-4-4-12 序列号的技术注解;[^()（）]* 禁止跨括号
        private static readonly Regex GuidAnnotation = new(
            @"\s*[\(（][^\(\)（）]*[0-9a-fA-F]{8}-(?:[0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}[^\(\)（）]*[\)）]",
            RegexOptions.Compiled);

        /// <summary>以兼容旧调用名的方式清理错误文本，保留可读信息而非只截取最后一句。</summary>
        /// <returns>清理后的错误文本。</returns>
        /// <param name="rawMessage">原始错误说明。</param>
        public static string LastSentence(string rawMessage)
            => Clean(rawMessage, Properties.Resources.Error_Unknown);

        /// <summary>去除错误文本中带 GUID 的括号注解、空行和重复行，保留其余内容。</summary>
        /// <returns>整理后的文本；没有可提取行时使用原始文本的回退。</returns>
        /// <param name="rawMessage">原始错误说明，可包含多行。</param>
        public static string CleanLines(string rawMessage)
            => Clean(rawMessage, string.Empty);

        private static string Clean(string raw, string emptyFallback)
        {
            if (string.IsNullOrWhiteSpace(raw)) return emptyFallback;

            var lines = raw
                .Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries)
                .Select(l => GuidAnnotation.Replace(l, ""))
                .Select(l => l.Trim())
                .Where(l => l.Length > 0)
                .Distinct(StringComparer.Ordinal);

            string result = string.Join(Environment.NewLine, lines);
            return string.IsNullOrWhiteSpace(result) ? raw.Trim() : result;
        }
    }
}
