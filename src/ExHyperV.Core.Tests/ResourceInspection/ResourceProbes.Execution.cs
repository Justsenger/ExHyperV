using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using ExHyperV.Core.Tests.TestExecution;
using ExHyperV.Tools;

namespace ExHyperV.Core.Tests.ResourceInspection;

internal static partial class ResourceProbes
{
    private static ResourceCheck[] ExecutionChecks =>
    [
        new("template-copy", "虚拟机", "完整副本条件", "模板关机、磁盘链可读取、缓存容量够用", "依赖系统模板的测试"),
        new("vm-credentials", "虚拟机", "虚拟机登录凭据", "当前账户保存的加密凭据可读取", "磁盘与网络负载、Linux SSH"),
        new("config-isolation", "执行条件", "配置文件独占", "共用配置的桌面程序已退出", "配置写入与恢复"),
        new("sharing-idle", "执行条件", "网络共享空闲", "宿主当前无 ICS 共享", "交换机创建、修改与删除"),
        new("network-upstream", "执行条件", "闲置网络上游", "断链、无有效 IP、无默认路由且未绑定交换机的物理网卡", "桥接与 NAT"),
        new("native-console", "执行条件", "控制台原生组件", "发布包包含本机架构 FreeRDP 桥接库", "真实控制台连接"),
        new("console-audio", "执行条件", "控制台音频能力", "Core 提供音频通道", "音频数据发送"),
        new("capture-idle", "执行条件", "抓包资源", "PktMon 停止且筛选器为空", "ARP 实际报文捕获"),
        new("online-resources", "执行条件", "在线脚本来源", "脚本索引可访问", "Linux 部署脚本"),
        new("nvme-hardware", "真实设备", "NVMe 磁盘", "存在 NVMe 总线磁盘", "原生 NVMe 开关与重启"),
        new("reboot-resume", "执行条件", "重启续跑", "管理员账户、计划任务服务与本机持久结果目录", "宿主重启测试")
    ];

    private static async Task<ResourceResult> CheckExecutionConditionAsync(string id, InspectionConfiguration config)
    {
        switch (id)
        {
            case "template-copy":
                if (!Guid.TryParse(config.TemplateVmId, out var vmId)) return Pending("先选择测试虚拟机模板。");
                var facts = await TemporaryVm.ReadTemplateFactsAsync(vmId);
                if (facts.GetProperty("state").GetString() != "Off") return Missing("请先正常关闭模板，测试将复制完整磁盘。");
                if (facts.GetProperty("disks").GetInt32() == 0) return Missing("模板尚未挂接系统磁盘。");
                if (new[] { "physical", "dda", "gpu" }.Any(key => facts.GetProperty(key).GetInt32() > 0)) return Missing("模板已挂接物理盘、PCIe 或 GPU；请选择干净模板，设备由各项测试分配。");
                long required = checked(facts.GetProperty("bytes").GetInt64() * 2 + 2L * 1024 * 1024 * 1024);
                long free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(config.WorkDirectory!))!).AvailableFreeSpace;
                return free >= required ? Ready($"完整复制需要约 {required / 1073741824d:F1} GiB，缓存可用 {free / 1073741824d:F1} GiB")
                    : Missing($"完整复制需要约 {required / 1073741824d:F1} GiB，缓存现有 {free / 1073741824d:F1} GiB。");
            case "vm-credentials":
                if (!Guid.TryParse(config.TemplateVmId, out var template)) return Pending("先选择测试虚拟机模板。");
                string credential = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ExHyperV.Core.Tests", "Credentials", template.ToString("D") + ".clixml");
                if (!File.Exists(credential)) return Missing("请以测试执行账户在本机保存模板登录凭据：Get-Credential | Export-Clixml -LiteralPath '" + credential.Replace("'", "''") + "'（先创建 Credentials 目录）。");
                var secret = await WindowsTestCommand.RunAsync("$v=Import-Clixml -LiteralPath $a.path;@{valid=($v -is [PSCredential] -and $v.UserName.Length -gt 0 -and $v.Password.Length -gt 0)}|ConvertTo-Json", new { path = credential }, 10);
                return secret.GetProperty("valid").GetBoolean() ? Ready("加密凭据可读取；连接将在完整副本内核验") : Missing("凭据文件应为当前执行账户导出的 PSCredential。");
            case "config-isolation":
                foreach (var process in Process.GetProcessesByName("ExHyperV")) using (process) return Missing("请先退出 ExHyperV 桌面程序，测试期间独占当前账户配置。");
                return Ready("桌面程序已退出；每项写入前备份，结束后恢复");
            case "sharing-idle":
                var sharing = await ComApi.GetConnectionsAsync();
                if (!sharing.HasData) return new(InspectionStatus.Unavailable, sharing.Error);
                return sharing.Data!.Any(v => v.Enabled) ? Missing("宿主已有 ICS 共享；待共享资源空闲后测试交换机写入。") : Ready("宿主当前无 ICS 共享");
            case "network-upstream":
                try { var upstream = await SwitchApiCases.SafeUpstreamAsync(); return Ready(upstream.Name); }
                catch (ApiCheckSkippedException e) { return Missing(e.Message); }
            case "native-console":
                string rid = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "win-arm64" : "win-x64";
                using (var package = typeof(ExHyperV.Services.ConsoleService).Assembly.GetManifestResourceStream($"ExHyperV.ConsoleNative.{rid}.zip"))
                {
                    if (package is null) return Missing("Core 发布包缺少 " + rid + " 控制台组件。");
                    using var zip = new System.IO.Compression.ZipArchive(package, System.IO.Compression.ZipArchiveMode.Read);
                    return zip.Entries.Any(v => v.Name == "ExHyperV.FreeRdp.dll" && v.Length > 0)
                        ? Ready(rid + " 桥接库已打包；建立连接时验证加载与协议") : Missing("原生组件压缩包缺少桥接库。");
                }
            case "console-audio":
                return Missing("当前 Core 的两个 RDP 后端均未提供音频输入通道。");
            case "capture-idle":
                var packet = await WindowsTestCommand.RunAsync("$s=pktmon status 2>&1;$ec=$LASTEXITCODE;$f=pktmon filter list 2>&1;@{status=($s -join \"`n\");filters=($f -join \"`n\");code=$ec;filterCode=$LASTEXITCODE}|ConvertTo-Json", timeoutSeconds: 10);
                bool idle = PacketCaptureAvailability.IsIdle(packet.GetProperty("status").GetString()!, packet.GetProperty("code").GetInt32(),
                    packet.GetProperty("filters").GetString()!, packet.GetProperty("filterCode").GetInt32());
                return idle ? Ready("PktMon 空闲且筛选器为空") : Missing("PktMon 已有捕获、筛选器，或状态读取失败；留待空闲后测试。");
            case "online-resources":
                using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) })
                using (var response = await client.GetAsync("https://raw.githubusercontent.com/Justsenger/ExHyperV/main/src/Linux/script/index.json", HttpCompletionOption.ResponseHeadersRead))
                    return response.IsSuccessStatusCode ? Ready("脚本索引可访问") : Missing("脚本索引返回 HTTP " + (int)response.StatusCode);
            case "nvme-hardware":
                if (Environment.OSVersion.Version.Build < 26100) return Missing("原生 NVMe 开关要求 Build 26100 及以上。");
                var nvme = await WindowsTestCommand.RunAsync("@(Get-Disk|Where-Object BusType -eq 'NVMe').Count|ConvertTo-Json", timeoutSeconds: 15, mayMutate: false);
                return nvme.GetInt32() > 0 ? Ready($"{nvme.GetInt32()} 个 NVMe 磁盘") : Missing("宿主当前缺少 NVMe 总线磁盘。");
            case "reboot-resume":
                if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true") return Missing("当前 Actions 作业会随宿主重启中断；请从本机页面执行重启项，持久续跑会保存完整结果。Actions 跨重启汇报需外部调度端接管。");
                using (var identity = System.Security.Principal.WindowsIdentity.GetCurrent())
                    if (!new System.Security.Principal.WindowsPrincipal(identity).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator)) return Missing("重启续跑需要管理员身份。");
                return Query("root/cimv2", "SELECT State FROM Win32_Service WHERE Name='Schedule'").Any(v => v["State"] == "Running")
                    ? Ready("开机续跑可用；每次宿主重启都需你确认并手动执行。") : Missing("计划任务服务尚未运行。");
            default: throw new ArgumentException("未知的资源检查项：" + id);
        }
    }
}
