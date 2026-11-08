using System.Text.RegularExpressions;

namespace ExHyperV.Core.Tests.ResourceInspection;

internal static class PacketCaptureAvailability
{
    internal static bool IsIdle(string status, int statusCode, string filters, int filterCode)
        => statusCode == 0 && filterCode == 0
        && Regex.IsMatch(status.Trim(), @"\A(?:数据包监视器(?:没有运行|未运行|已停止)|(?:Packet\s+Monitor\s+is\s+)?(?:not running|stopped)|已停止|未运行)[。.!！]?\z",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
        && Regex.IsMatch(filters.Trim(), @"\A(?:(?:数据包筛选器|Packet\s+Filters)\s*[:：]\s*)?(?:无|None|No filters(?: (?:are )?configured)?|没有筛选器|无筛选器|没有过滤器)[。.!！]?\z",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}
