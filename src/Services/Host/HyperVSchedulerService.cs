using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using Microsoft.Win32;

namespace ExHyperV.Services;

public enum HyperVSchedulerType
{
    Classic,
    Core,
    Root,
    Unknown
}

public static class HyperVSchedulerService
{
    /// <summary>
    /// 获取当前 Hyper-V 调度器类型。user-mode 无直接查询接口，按可靠性降序三层兜底，前面命中即返回：
    ///   1. 事件日志 EventID=2 —— 运行值，唯一能反映"配了 Core 但无 SMT 时实际按 Classic 跑"的回退；会随日志滚动老化。
    ///   2. bcdedit hypervisorschedulertype —— 配置值，显式设过才有。
    ///   3. 按 ProductType 推默认 —— 估计值，仅在①老化且②从没设过时用。
    /// 顺序不可颠倒：无 SMT 时 bcdedit 写着 Core 而实际跑 Classic，只有①反映真相。
    /// </summary>
    public static HyperVSchedulerType GetSchedulerType()
    {
        var running = TryGetFromEventLog();
        if (running != HyperVSchedulerType.Unknown) return running;

        var configured = TryGetFromBcdedit();
        if (configured != HyperVSchedulerType.Unknown) return configured;

        return GetDefaultSchedulerByProductType();
    }

    // 层 1：最近一次 hypervisor 启动事件（EventID=2）报告的运行值。
    // 值映射见微软文档：1=Classic(SMT关) / 2=Classic / 3=Core / 4=Root。
    private static HyperVSchedulerType TryGetFromEventLog()
    {
        try
        {
            string query = "*[System[Provider[@Name='Microsoft-Windows-Hyper-V-Hypervisor'] and (EventID=2)]]";
            var eventQuery = new EventLogQuery("System", PathType.LogName, query)
            {
                ReverseDirection = true
            };

            using var logReader = new EventLogReader(eventQuery);
            var record = logReader.ReadEvent();

            if (record != null && record.Properties.Count > 0)
            {
                ushort code = Convert.ToUInt16(record.Properties[0].Value);
                return code switch
                {
                    1 => HyperVSchedulerType.Classic,
                    2 => HyperVSchedulerType.Classic,
                    3 => HyperVSchedulerType.Core,
                    4 => HyperVSchedulerType.Root,
                    _ => HyperVSchedulerType.Unknown,
                };
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine(string.Format(Properties.Resources.HyperVScheduler_LogEventLogQueryFail, ex.Message));
        }

        return HyperVSchedulerType.Unknown;
    }

    // 层 2：读 bcdedit 的 hypervisorschedulertype 配置值（键名与 Classic/Core/Root 均不本地化，解析安全）。
    // 需管理员权限读取；未提权时会失败，自动落到层 3。RedirectStandardOutput 与 runas 互斥，故不提权。
    private static HyperVSchedulerType TryGetFromBcdedit()
    {
        const string key = "hypervisorschedulertype";
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "bcdedit.exe",
                Arguments = "/enum {current}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            };

            using var process = Process.Start(psi);
            if (process == null) return HyperVSchedulerType.Unknown;

            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();

            foreach (var line in output.Split('\n'))
            {
                var trimmed = line.Trim();
                if (!trimmed.StartsWith(key, StringComparison.OrdinalIgnoreCase)) continue;

                var value = trimmed.Substring(key.Length).Trim();
                return value.ToLowerInvariant() switch
                {
                    "classic" => HyperVSchedulerType.Classic,
                    "core" => HyperVSchedulerType.Core,
                    "root" => HyperVSchedulerType.Root,
                    _ => HyperVSchedulerType.Unknown,
                };
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine(string.Format(Properties.Resources.HyperVScheduler_LogBcdeditFail, ex.Message));
        }

        return HyperVSchedulerType.Unknown;
    }

    // 层 3：与 hypervisor 同源的默认推断。hvloader 判 ProductType：==WinNT(客户端)→Root，否则→种子默认；
    // 种子默认在 Server 2019+ (build≥17763) 是 Core、Server 2016 是 Classic。
    // 用 ProductType（而非 EditionID）是有意的：它正是 hypervisor 定默认的依据，"切换服务器版本"改了它 hypervisor 默认也同步变。
    private static HyperVSchedulerType GetDefaultSchedulerByProductType()
    {
        if (!HyperVHostService.IsServerSystem())
            return HyperVSchedulerType.Root;

        int build = GetOsBuildNumber();
        return (build == 0 || build >= 17763)
            ? HyperVSchedulerType.Core
            : HyperVSchedulerType.Classic;
    }

    private static int GetOsBuildNumber()
    {
        try
        {
            using var key = Registry.LocalMachine
                .OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            var build = key?.GetValue("CurrentBuildNumber")?.ToString();
            return int.TryParse(build, out int n) ? n : 0;
        }
        catch { return 0; }
    }

    public static async Task<bool> SetSchedulerTypeAsync(HyperVSchedulerType type)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "bcdedit.exe",
                Arguments = $"/set hypervisorschedulertype {type.ToString().ToLower()}",
                Verb = "runas",          // �Թ���Ա�������
                UseShellExecute = true,             // Verb=runas ����Ϊ true
                WindowStyle = ProcessWindowStyle.Hidden,
            };

            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start bcdedit.exe");

            await process.WaitForExitAsync();
            return process.ExitCode == 0;
        }
        catch (Exception ex)
        {
            Debug.WriteLine(string.Format(Properties.Resources.HyperVScheduler_LogBcdeditFail, ex.Message));
            return false;
        }
    }
}