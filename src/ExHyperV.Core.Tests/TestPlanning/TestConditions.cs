using ExHyperV.Core.Tests.ResourceInspection;

namespace ExHyperV.Core.Tests.TestPlanning;

internal sealed record ConditionIssue(string Detail, bool CheckPending = false);

internal static class TestConditions
{
    private sealed record Condition(string Name, params string[] Checks);
    private static readonly HashSet<string> ImplementedChecks = ResourceProbes.Checks.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);

    // Presence/read access and dedicated test ownership are separate evidence.
    // An absent check stays pending; inventory text is never parsed as consent.
    private static readonly Dictionary<string, Condition> Rules = new(StringComparer.Ordinal)
    {
        ["inputs"] = new("固定输入"),
        ["lifecycle"] = new("测试对象与独立进程"),
        ["windows"] = new("Windows 系统接口", "platform"),
        ["hyperv"] = new("Hyper-V 管理接口", "platform", "vmms", "hyperv"),
        ["hostwrite"] = new("系统写入权限", "platform", "identity"),
        ["files"] = new("可写测试目录", "workspace"),
        ["iso"] = new("ISO 生成与挂载组件", "platform", "iso-support", "identity"),
        ["vmcx"] = new("自动导出专属虚拟机的 VMCX 文件", "test-vm"),
        ["config"] = new("共享配置读取与字段恢复", "config-access"),
        ["vms"] = new("现有虚拟机", "vms"),
        ["vm"] = new("临时虚拟机创建条件", "test-vm"),
        ["vmstate"] = new("执行时准备所需代数和状态的临时虚拟机", "test-vm"),
        ["group"] = new("自动创建专属虚拟机分组", "test-vm"),
        ["vnet"] = new("自动准备临时测试网络", "test-vm"),
        ["upstream"] = new("自动选择闲置物理网卡", "network-upstream", "sharing-idle"),
        ["sharing"] = new("网络共享空闲", "sharing-idle"),
        ["traffic"] = new("自动创建隔离网络并产生已知报文", "test-vm"),
        ["media"] = new("自动生成专属测试磁盘", "workspace", "identity", "hyperv"),
        ["physical"] = new("选定物理测试盘", "disks"),
        ["optical"] = new("选定测试光驱", "optical"),
        ["checkpoint"] = new("在专属普通虚拟机上创建检查点", "test-vm"),
        ["import"] = new("自动导出专属虚拟机作为导入样本", "test-vm"),
        ["gpupv"] = new("可分区 GPU", "gpu-partitions"),
        ["driver"] = new("宿主 GPU 驱动；执行时核验包内文件", "gpu"),
        ["guestdisk"] = new("完整副本的离线系统磁盘", "template-copy"),
        ["template"] = new("共用完整虚拟机模板", "templates", "template-copy"),
        ["pcie"] = new("选定 PCIe 设备与宿主准入", "pcie-host", "test-pcie"),
        ["pcie-inventory"] = new("选定 PCIe 设备", "test-pcie"),
        ["guest"] = new("虚拟机登录凭据；执行时验证连接", "vm-credentials"),
        ["nativeconsole"] = new("FreeRDP 原生库", "native-console"),
        ["audio"] = new("音频来源实现", "console-audio"),
        ["console"] = new("专属虚拟机基本控制台", "test-vm", "native-console"),
        ["enhanced"] = new("模板副本；执行时确认增强会话就绪", "template-copy", "native-console"),
        ["ssh"] = new("虚拟机 SSH 凭据；执行时校验 Linux 身份", "vm-credentials", "template-copy"),
        ["internet"] = new("在线资源可达", "online-resources"),
        ["scheduler"] = new("运行期调度器；执行时选择 CPU 组或进程后端", "platform", "identity", "hyperv"),
        ["counters"] = new("系统性能计数器；执行时预热并采样", "platform", "hyperv"),
        ["capture"] = new("独立 ARP 捕获接口与权限", "platform", "identity", "capture-api"),
        ["native"] = new("Windows 内置系统组件", "platform"),
        ["nvme"] = new("NVMe 磁盘与受支持的宿主版本", "nvme-hardware"),
        ["reboot"] = new("宿主重启、恢复与自动续跑", "reboot-resume"),
        ["capacity"] = new("测试目录与容量信息", "workspace", "memory", "volumes"),
        ["build"] = new("编译环境", "dotnet", "msbuild"),
        ["inventory"] = new("资源选择与持久保存", "resource-selection"),
        ["recovery-fixture"] = new("进程内受控故障样本"),
        ["test-scheduler"] = new("当前测试调度器")
    };

    internal static string Name(string key) => Rules.TryGetValue(key, out var rule)
        ? rule.Name : throw new InvalidDataException($"测试条件尚未定义：{key}");

    internal static IEnumerable<string> GetBlockers(InspectionReport report, string key) =>
        GetIssues(report, key).Select(issue => issue.Detail);

    internal static IEnumerable<ConditionIssue> GetIssues(InspectionReport report, string key)
    {
        if (!Rules.TryGetValue(key, out var rule))
        {
            yield return new($"测试条件尚未定义：{key}", true);
            yield break;
        }
        foreach (var id in rule.Checks)
        {
            var matches = report.Items.Where(i => i.Check.Id == id).ToArray();
            if (matches.Length == 0)
            {
                var pending = !ImplementedChecks.Contains(id);
                yield return new(pending ? $"检查项定义缺失：{id}" : $"{rule.Name}：检查结果缺失，请重新检查", pending);
                continue;
            }
            if (matches.Length > 1) { yield return new($"{rule.Name}：检查记录重复"); continue; }
            var item = matches[0];
            if (item.Result.Status == InspectionStatus.Ready) continue;
            var status = item.Result.Status switch
            {
                InspectionStatus.Missing => "条件待补齐",
                InspectionStatus.Pending => "待选择",
                InspectionStatus.Unavailable => "无法检查",
                _ => "尚未完成检查"
            };
            yield return new($"{item.Check.Name}：{status}（{item.Result.Detail}）");
        }
    }
}
