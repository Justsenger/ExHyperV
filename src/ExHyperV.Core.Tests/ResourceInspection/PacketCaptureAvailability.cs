using System.Text.RegularExpressions;

namespace ExHyperV.Core.Tests.ResourceInspection;

internal static class PacketCaptureAvailability
{
    internal static ResourceResult Describe(string status, int statusCode, string filters, int filterCode)
    {
        string[] evidence = ["状态：" + status.Trim(), "筛选器：" + filters.Trim()];
        if (statusCode != 0 || filterCode != 0)
            return new(InspectionStatus.Unavailable, "PktMon 查询失败，请查看明细。", evidence);
        if (IsIdle(status, statusCode, filters, filterCode)) return new(InspectionStatus.Ready, "抓包通道空闲");
        bool stopped = IsIdle(status, 0, "None", 0);
        bool empty = IsIdle("Stopped", 0, filters, 0);
        bool active = Regex.IsMatch(status, @"正在运行|收集的数据\s*[:：]|捕获类型\s*[:：]|\b(?:is running|Data collected|Capture type)\b", RegexOptions.IgnoreCase);
        bool listed = Regex.IsMatch(filters, @"(?m)^\s*\d+\s+\S") || Regex.IsMatch(filters, @"(?im)^\s*ARP\s*$");
        if (!stopped && !active || !empty && !listed)
            return new(InspectionStatus.Unavailable, "PktMon 返回了未识别的状态，请查看明细。", evidence);
        string filter = Regex.IsMatch(filters, @"\bARP\b", RegexOptions.IgnoreCase) ? " ARP 筛选器" : "筛选器";
        return new(InspectionStatus.Missing, active
            ? "PktMon 正在采集" + (empty ? "。" : "，已有" + filter + "。")
            : "PktMon 已停止，但仍保留" + filter + "。", evidence);
    }

    internal static bool IsIdle(string status, int statusCode, string filters, int filterCode)
        => statusCode == 0 && filterCode == 0
        && Regex.IsMatch(status.Trim(), @"\A(?:数据包监视器(?:没有运行|未运行|已停止)|(?:Packet\s+Monitor\s+is\s+)?(?:not running|stopped)|已停止|未运行)[。.!！]?\z",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
        && Regex.IsMatch(filters.Trim(), @"\A(?:(?:数据包筛选器|Packet\s+Filters)\s*[:：]\s*)?(?:无|None|No filters(?: (?:are )?configured)?|没有筛选器|无筛选器|没有过滤器)[。.!！]?\z",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}
