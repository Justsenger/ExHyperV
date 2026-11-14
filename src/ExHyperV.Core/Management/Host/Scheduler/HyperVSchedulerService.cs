using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Xml.Linq;
using ExHyperV.Tools;
using Microsoft.Win32;

namespace ExHyperV.Services;

/// <summary>Hyper-V 调度器模式；Unknown 表示未识别的结果。</summary>
public enum HyperVSchedulerType
{
    /// <summary>Classic 传统调度器，由监控程序调度来宾虚拟处理器。</summary>
    Classic,
    /// <summary>Core 核心调度器，按物理核心的 SMT 拓扑协调同一虚拟机的虚拟处理器。</summary>
    Core,
    /// <summary>Root 根分区调度器，由根分区的 Windows 调度器安排处理器工作。</summary>
    Root,
    /// <summary>无法确定调度器类型。</summary>
    Unknown
}

/// <summary>分别读取宿主当前运行的调度器和引导配置，并设置后续启动的调度器选择。</summary>
public static class HyperVSchedulerService
{
    /// <summary>读取宿主当前运行的 Hyper-V 调度器。</summary>
    /// <remarks>优先查询内核运行信息，无法识别时读取本次启动的事件日志。内核调度器字段未文档化；两种来源均无法确认时返回 Unknown。下次启动配置见 GetConfiguredSchedulerType。</remarks>
    /// <returns>当前识别出的调度器；未运行或无法确定时为 Unknown。</returns>
    public static HyperVSchedulerType GetSchedulerType()
    {
        var kernel = HypervisorSchedulerInfo.Read();
        if (kernel.Present == false) return HyperVSchedulerType.Unknown;
        var running = kernel.Present == true ? MapRuntimeType(kernel.Scheduler) : HyperVSchedulerType.Unknown;
        return running != HyperVSchedulerType.Unknown ? running : TryGetFromEventLog();
    }

    /// <summary>读取下次启动采用的调度器配置。</summary>
    /// <remarks>读取当前启动项的 BCD 配置；未配置时按当前系统产品类型和构建号确定默认值，无法确定时返回 Unknown。此配置可能与正在运行的调度器不同。</remarks>
    /// <returns>明确配置或按当前系统推定的启动调度器；无法确定时为 Unknown。</returns>
    public static HyperVSchedulerType GetConfiguredSchedulerType()
    {
        var configured = BcdStoreClient.Read(BcdSetting.HypervisorSchedulerType);
        if (!configured.Success) return HyperVSchedulerType.Unknown;
        return configured.IsEmpty ? GetDefaultSchedulerByProductType() : MapConfiguredType(configured.Data);
    }

    // 仅在内核查询不可用/无法识别时读取日志。启动标记、记录顺序及 uptime 同时吻合才接受。
    // 若日志被清理、时钟跳变或提供者记录顺序不同，宁可返回 Unknown，不借用更早启动的值。
    private static HyperVSchedulerType TryGetFromEventLog()
    {
        try
        {
            var bootQuery = new EventLogQuery("System", PathType.LogName,
                "*[System[Provider[@Name='Microsoft-Windows-Kernel-General'] and (EventID=12)]]")
            { ReverseDirection = true };
            using var bootReader = new EventLogReader(bootQuery);
            using var boot = bootReader.ReadEvent();
            if (boot?.RecordId is not long bootId || bootId <= 0) return HyperVSchedulerType.Unknown;

            XNamespace ns = "http://schemas.microsoft.com/win/2004/08/events/event";
            string? startTime = XDocument.Parse(boot.ToXml()).Root?.Element(ns + "EventData")?
                .Elements(ns + "Data").SingleOrDefault(element => (string?)element.Attribute("Name") == "StartTime")?.Value;
            if (!DateTimeOffset.TryParse(startTime, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var bootTime))
                return HyperVSchedulerType.Unknown;
            // 使用 Win32 uptime（含休眠），不依赖不同 .NET 版本的 TickCount64 休眠口径。
            ulong uptime = GetTickCount64();
            DateTimeOffset now = DateTimeOffset.UtcNow;
            var estimatedBoot = now - TimeSpan.FromMilliseconds(uptime);
            if (bootTime > now || Math.Abs((bootTime - estimatedBoot).TotalSeconds) > 2)
                return HyperVSchedulerType.Unknown;

            string query = "*[System[Provider[@Name='Microsoft-Windows-Hyper-V-Hypervisor'] and (EventID=2)]]";
            var eventQuery = new EventLogQuery("System", PathType.LogName, query)
            {
                ReverseDirection = true
            };

            using var logReader = new EventLogReader(eventQuery);
            using var record = logReader.ReadEvent();

            if (record?.RecordId is long eventId && eventId > bootId && record.TimeCreated is DateTime created
                && created.ToUniversalTime() >= bootTime.UtcDateTime && created.ToUniversalTime() <= now.UtcDateTime
                && record.Properties.Count > 0)
            {
                // 最新记录无效时停止；不继续寻找更早的已知值。
                return MapRuntimeType(Convert.ToUInt16(record.Properties[0].Value));
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine(string.Format(Properties.Resources.HyperVScheduler_LogEventLogQueryFail, ex.Message));
        }

        return HyperVSchedulerType.Unknown;
    }

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern ulong GetTickCount64();

    // 运行值与 BCD 值使用不同编号，不能混用。
    internal static HyperVSchedulerType MapRuntimeType(ushort value) => value switch
    {
        1 or 2 => HyperVSchedulerType.Classic,
        3 => HyperVSchedulerType.Core,
        4 => HyperVSchedulerType.Root,
        _ => HyperVSchedulerType.Unknown,
    };

    // BCD 的存储值与启动事件中的调度器编号不同。
    internal static HyperVSchedulerType MapConfiguredType(ulong value)
        => value switch
        {
            0 => HyperVSchedulerType.Classic,
            1 => HyperVSchedulerType.Core,
            2 => HyperVSchedulerType.Root,
            _ => HyperVSchedulerType.Unknown,
        };

    // 仅用于未显式设置的启动配置。失败不经 IsServerSystem 的 false 值误推为客户端。
    private static HyperVSchedulerType GetDefaultSchedulerByProductType()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\ProductOptions");
            string? type = key?.GetValue("ProductType") as string;
            if (string.Equals(type, "WinNT", StringComparison.OrdinalIgnoreCase)) return HyperVSchedulerType.Root;
            if (!string.Equals(type, "ServerNT", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(type, "LanmanNT", StringComparison.OrdinalIgnoreCase))
                return HyperVSchedulerType.Unknown;
            int build = Environment.OSVersion.Version.Build;
            return build <= 0 ? HyperVSchedulerType.Unknown
                : build >= 17763 ? HyperVSchedulerType.Core : HyperVSchedulerType.Classic;
        }
        catch { return HyperVSchedulerType.Unknown; }
    }

    /// <summary>写入宿主引导配置中的 Hyper-V 调度器选择。</summary>
    /// <remarks>通过 BCD WMI Provider 写入并回读，需要管理员及备份、还原特权。宿主重启后生效。</remarks>
    /// <returns>引导配置写入及读回结果；Unknown 或未定义的枚举值不会提交。失败信息区分写入失败、结果未知和写入成功后读回失败。</returns>
    /// <param name="type">要写入的调度器模式。</param>
    public static Task<ApiResponse> SetSchedulerTypeAsync(HyperVSchedulerType type)
        => type is HyperVSchedulerType.Classic or HyperVSchedulerType.Core or HyperVSchedulerType.Root
            ? BcdStoreClient.SetAsync(BcdSetting.HypervisorSchedulerType, type switch
            {
                HyperVSchedulerType.Classic => 0,
                HyperVSchedulerType.Core => 1,
                _ => 2ul,
            })
            : Task.FromResult(ApiResponse.Fail("未指定可写入的 Hyper-V 调度器模式。", kind: ApiErrorKind.InvalidArgument));
}
