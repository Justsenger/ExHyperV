using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace ExHyperV.Tools
{
    /// <summary>
    /// 把 raw 错误消息净化成用户可读文本。设计原则(2026-07 抓取 vmms 真实错误语料实测后定)：
    /// 引擎错误是"概述\n\n中层\n\n根因"的分层结构,每行都是完整句子——因此**不改写、不删行,只去噪**：
    /// ① 剥 GUID 技术注解——括号内含 36 位 GUID 才剥。锚定 GUID 形态而非 "ID" 字样(跨系统语言稳定)；
    ///    括号内禁再含括号,避免跨括号吞掉两括号之间的正文;错误码 "(0x…)" 不含 GUID 天然保留。
    /// ② 空行与重复行去掉(引擎多行错误常逐行重复同一句)。
    /// ③ 净化只能减噪不能丢因：全洗没了(整条消息就是一个注解)回退原文。
    /// ④ 空输入回通用文案(曾误用存储专用文案,网卡等报错被说成存储错误)。
    /// </summary>
    public static class FriendlyError
    {
        // 括号(全/半角)内含 8-4-4-4-12 序列号的技术注解;[^()（）]* 禁止跨括号
        private static readonly Regex GuidAnnotation = new(
            @"\s*[\(（][^\(\)（）]*[0-9a-fA-F]{8}-(?:[0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}[^\(\)（）]*[\)）]",
            RegexOptions.Compiled);

        /// <summary>
        /// 短提示(Snackbar 正文)。历史上是"截取最后一句",实测重设计后与 CleanLines 统一为
        /// "全行保留、只去噪"——引擎行数克制(去空后至多三行),零信息丢失优先于最短。
        /// </summary>
        public static string LastSentence(string rawMessage)
            => Clean(rawMessage, Properties.Resources.Error_Unknown);

        /// <summary>多行详情净化(与短提示同一实现;空输入回空串,由调用方决定展示)。</summary>
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
