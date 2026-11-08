using Microsoft.CodeAnalysis;

internal static partial class ApiDocument
{
    // Navigation describes existing members. Signatures, types and explanations still
    // come from their declarations; this is not a second host query API.
    private sealed record HostEntry(string Owner, string Member, string Area, string Category,
        string Label, string? Reader = null, string? Related = null);

    // Share the source type's remarks with its catalog section and member pages.
    private sealed record HostUsageSection(string Owner, string Area, string Category, string Title);
    private static readonly HostUsageSection[] HostUsageSections =
    [
        new("HyperVCpuMonitorService", "查询", "处理器", "采样器的使用与释放"),
        new("HostCpuGroupService", "查询", "处理器组", "读取方式与结果说明"),
        new("HostCpuGroupService", "修改", "处理器组", "读取方式与结果说明"),
        new("ArpSnoopService", "查询", "ARP 嗅探", "捕获资源的使用与释放"),
    ];

    private static readonly HostEntry[] HostEntries =
    [
        new("SystemInfo", "Caption", "查询", "基本信息", "操作系统名称", "HostSystemInfoService.GetSystemInfoAsync"),
        new("SystemInfo", "OSArchitecture", "查询", "基本信息", "系统位数", "HostSystemInfoService.GetSystemInfoAsync"),
        new("SystemInfo", "CpuModel", "查询", "处理器", "型号", "HostSystemInfoService.GetSystemInfoAsync"),
        new("SystemInfo", "MemCap", "查询", "内存", "已安装容量", "HostSystemInfoService.GetSystemInfoAsync"),
        new("HostSystemInfoService", "GetSystemInfoAsync", "查询", "基本信息", "基本信息汇总"),
        new("HostProcessorPlatformService", "GetArchitecture", "查询", "处理器", "架构"),
        new("HostProcessorPlatformService", "GetManufacturersAsync", "查询", "处理器", "厂商"),
        new("HostProcessorService", "GetAffinityProcessorsAsync", "查询", "处理器", "亲和性可选逻辑处理器"),
        new("HyperVCpuMonitorService", "GetCoreType", "查询", "处理器", "核心类型"),
        new("HyperVCpuMonitorService", "GetCpuUsage", "查询", "处理器", "宿主与虚拟机处理器使用率"),
        new("HostCpuGroupInfo", "GroupId", "查询", "处理器组", "身份", "HostCpuGroupService.GetAsync"),
        new("HostCpuGroupInfo", "LogicalProcessors", "查询", "处理器组", "逻辑处理器", "HostCpuGroupService.GetAsync"),
        new("HostCpuGroupInfo", "CpuCap", "查询", "处理器组", "总配额", "HostCpuGroupService.GetAsync", "HostCpuGroupService.SetCpuCapAsync"),
        new("HostCpuGroupInfo", "Priority", "查询", "处理器组", "调度优先级", "HostCpuGroupService.GetAsync", "HostCpuGroupService.SetPriorityAsync"),
        new("HostCpuGroupInfo", "IdleProcessorReserve", "查询", "处理器组", "空闲逻辑处理器保留数", "HostCpuGroupService.GetAsync", "HostCpuGroupService.SetIdleProcessorReserveAsync"),
        new("HostCpuGroupInfo", "RelativeWeight", "查询", "处理器组", "相对调度权重", "HostCpuGroupService.GetAsync", "HostCpuGroupService.SetRelativeWeightAsync"),
        new("HostCpuGroupInfo", "Members", "查询", "处理器组", "虚拟机成员", "HostCpuGroupService.GetAllAsync"),
        new("HostCpuGroupService", "SetCpuCapAsync", "修改", "处理器组", "总配额", Related: "HostCpuGroupInfo.CpuCap"),
        new("HostCpuGroupService", "SetPriorityAsync", "修改", "处理器组", "调度优先级", Related: "HostCpuGroupInfo.Priority"),
        new("HostCpuGroupService", "SetIdleProcessorReserveAsync", "修改", "处理器组", "空闲逻辑处理器保留数", Related: "HostCpuGroupInfo.IdleProcessorReserve"),
        new("HostCpuGroupService", "SetRelativeWeightAsync", "修改", "处理器组", "相对调度权重", Related: "HostCpuGroupInfo.RelativeWeight"),
        new("HyperVNumaService", "GetNumaSpanningEnabledAsync", "查询", "内存", "跨 NUMA 节点分配", Related: "HyperVNumaService.SetNumaSpanningEnabledAsync"),
        new("HyperVNumaService", "SetNumaSpanningEnabledAsync", "修改", "内存", "跨 NUMA 节点分配", Related: "HyperVNumaService.GetNumaSpanningEnabledAsync"),
        new("HostDiskService", "GetHostDisksAsync", "查询", "存储", "磁盘"),
        new("HostDiskService", "GetDiskReferenceAsync", "查询", "存储", "磁盘身份"),
        new("HostDiskService", "GetPartitionReferenceAsync", "查询", "存储", "分区身份"),
        new("HostDiskService", "GetHostOpticalDrivesAsync", "查询", "存储", "光驱"),
        new("HyperVHostSettings", "DefaultVirtualHardDiskPath", "查询", "存储", "默认虚拟磁盘目录", "HyperVHostSettingsService.GetAsync", "HostDiskService.SetDefaultVirtualHardDiskPathAsync"),
        new("HostVolumeAutoMountService", "GetState", "查询", "存储", "卷自动挂载", Related: "HostVolumeAutoMountService.SetEnabledAsync"),
        new("HostNvmeService", "IsNativeNvmeEnabled", "查询", "存储", "原生 NVMe", Related: "HostNvmeService.SetNativeNvmeEnabled"),
        new("HostNvmeService", "SetNativeNvmeEnabled", "修改", "存储", "原生 NVMe", Related: "HostNvmeService.IsNativeNvmeEnabled"),
        new("HostDiskService", "SetDiskOfflineStatusAsync", "修改", "存储", "磁盘联机状态"),
        new("HostDiskService", "SetDiskReadOnlyAsync", "修改", "存储", "磁盘只读状态"),
        new("HostDiskService", "MountDiskImageAsync", "修改", "存储", "挂载磁盘映像"),
        new("HostDiskService", "MountVhdxAsync", "修改", "存储", "挂载虚拟磁盘"),
        new("HostDiskService", "DismountVhdxAsync", "修改", "存储", "卸载虚拟磁盘"),
        new("HostDiskService", "AssignPartitionDriveLetterAsync", "修改", "存储", "添加分区盘符"),
        new("HostDiskService", "RemovePartitionAccessPathAsync", "修改", "存储", "移除分区盘符"),
        new("HostDiskService", "SetDefaultVirtualHardDiskPathAsync", "修改", "存储", "默认虚拟磁盘目录", Related: "HyperVHostSettings.DefaultVirtualHardDiskPath"),
        new("HostVolumeAutoMountService", "SetEnabledAsync", "修改", "存储", "卷自动挂载", Related: "HostVolumeAutoMountService.GetState"),
        new("HyperVSwitchService", "GetNetworkInfoAsync", "查询", "网络", "虚拟交换机与上游网卡"),
        new("HyperVSwitchService", "GetBridgeableAdaptersAsync", "查询", "网络", "外部交换机候选网卡"),
        new("HyperVSwitchService", "GetFullSwitchNetworkStateAsync", "查询", "网络", "交换机连接的网卡"),
        new("HyperVHostSettings", "MinimumMacAddress", "查询", "网络", "动态 MAC 地址池起始地址", "HyperVHostSettingsService.GetAsync", "HostNetworkService.SetDynamicMacAddressRangeAsync"),
        new("HyperVHostSettings", "MaximumMacAddress", "查询", "网络", "动态 MAC 地址池结束地址", "HyperVHostSettingsService.GetAsync", "HostNetworkService.SetDynamicMacAddressRangeAsync"),
        new("HyperVSwitchService", "CreateSwitchAsync", "修改", "网络", "创建虚拟交换机"),
        new("HyperVSwitchService", "UpdateSwitchConfigurationAsync", "修改", "网络", "修改虚拟交换机配置"),
        new("HyperVSwitchService", "DeleteSwitchAsync", "修改", "网络", "删除虚拟交换机"),
        new("HostNetworkService", "SetDynamicMacAddressRangeAsync", "修改", "网络", "动态 MAC 地址池", Related: "HyperVHostSettings.MinimumMacAddress"),
        new("HostGpuService", "GetHostGpusAsync", "查询", "GPU", "设备清单"),
        new("HostGpuService", "GetAssignmentRestrictionsRelaxed", "查询", "GPU", "放宽分配限制", Related: "HostGpuService.SetAssignmentRestrictionsRelaxed"),
        new("HostGpuService", "SetAssignmentRestrictionsRelaxed", "修改", "GPU", "放宽分配限制", Related: "HostGpuService.GetAssignmentRestrictionsRelaxed"),
        new("HostPcieService", "GetPCIeInfoAsync", "查询", "PCIe", "设备与归属"),
        new("HostPcieService", "GetControllerDisksAsync", "查询", "PCIe", "控制器关联磁盘"),
        new("HostPcieService", "ExecutePCIeOperationAsync", "修改", "PCIe", "设备分配与归还"),
        new("HyperVSchedulerService", "GetSchedulerType", "查询", "调度器", "当前调度器"),
        new("HyperVSchedulerService", "GetConfiguredSchedulerType", "查询", "调度器", "启动调度器配置", Related: "HyperVSchedulerService.SetSchedulerTypeAsync"),
        new("HyperVSchedulerService", "SetSchedulerTypeAsync", "修改", "调度器", "启动调度器配置", Related: "HyperVSchedulerService.GetConfiguredSchedulerType"),
        new("HostPowerPlanService", "GetPowerPlans", "查询", "电源", "电源计划清单"),
        new("HostPowerPlanService", "GetActivePowerPlanId", "查询", "电源", "当前电源计划", Related: "HostPowerPlanService.SetActivePowerPlan"),
        new("HostPowerPlanService", "SetActivePowerPlan", "修改", "电源", "当前电源计划", Related: "HostPowerPlanService.GetActivePowerPlanId"),
        new("HyperVHostService", "GetHyperVStatusAsync", "查询", "Hyper-V", "状态摘要"),
        new("HyperVHostService", "GetHyperVDiagnosticsAsync", "查询", "Hyper-V", "运行环境诊断"),
        new("HyperVHostService", "GetVmmsStatus", "查询", "Hyper-V", "VMMS 服务状态"),
        new("HyperVHostService", "IsHyperVWmiNamespaceAvailable", "查询", "Hyper-V", "WMI 管理接口可用性"),
        new("HyperVHostService", "IsHypervisorPresent", "查询", "Hyper-V", "是否存在虚拟机监控程序"),
        new("HyperVHostService", "IsIommuEnabled", "查询", "Hyper-V", "IOMMU 可用性"),
        new("HyperVHostService", "IsVirtualizationEnabled", "查询", "Hyper-V", "虚拟化扩展可用性"),
        new("HyperVHostSettingsService", "GetAsync", "查询", "Hyper-V", "Hyper-V 宿主配置"),
        new("HyperVHostSettings", "DefaultVirtualMachinePath", "查询", "Hyper-V", "默认虚拟机配置目录", "HyperVHostSettingsService.GetAsync", "HyperVHostSettingsService.SetDefaultVirtualMachinePathAsync"),
        new("HyperVHostService", "SetHyperVEnabledAsync", "修改", "Hyper-V", "启用 Hyper-V"),
        new("HyperVHostSettingsService", "SetDefaultVirtualMachinePathAsync", "修改", "Hyper-V", "默认虚拟机配置目录", Related: "HyperVHostSettings.DefaultVirtualMachinePath"),
        new("HyperVHostSettings", "EnhancedSessionModeEnabled", "查询", "控制台", "允许增强会话", "HyperVHostSettingsService.GetAsync", "HostConsoleService.SetEnhancedSessionModeEnabledAsync"),
        new("HostConsoleService", "SetEnhancedSessionModeEnabledAsync", "修改", "控制台", "允许增强会话", Related: "HyperVHostSettings.EnhancedSessionModeEnabled"),
        new("VmGroupService", "ReadAsync", "查询", "分组", "分组及成员关系"),
        new("VmGroupFields", "Id", "查询", "分组", "虚拟机所属分组身份", "VmQueryService.QueryAsync"),
        new("VmGroupFields", "Name", "查询", "分组", "虚拟机所属分组名称", "VmQueryService.QueryAsync"),
        new("VmGroupService", "CreateAsync", "修改", "分组", "创建分组"),
        new("VmGroupService", "RenameAsync", "修改", "分组", "分组名称"),
        new("VmGroupService", "MoveAsync", "修改", "分组", "移动成员或分组"),
        new("VmGroupService", "DeleteAsync", "修改", "分组", "删除分组"),
        new("ArpSnoopService", "IsAvailable", "查询", "ARP 嗅探", "捕获状态"),
        new("ArpSnoopService", "LastFailure", "查询", "ARP 嗅探", "捕获错误"),
        new("ArpSnoopService", "TryGetIp", "查询", "ARP 嗅探", "MAC 对应的 IPv4 地址"),
        new("HostOpenHclService", "IsFirmwareLoadFromFileEnabled", "查询", "OpenHCL", "允许从文件加载固件", Related: "HostOpenHclService.SetFirmwareLoadFromFileEnabled"),
        new("HostOpenHclService", "SetFirmwareLoadFromFileEnabled", "修改", "OpenHCL", "允许从文件加载固件", Related: "HostOpenHclService.IsFirmwareLoadFromFileEnabled"),
        new("HostProductTypeService", "IsServerSystem", "查询", "系统产品类型", "是否为服务器类型"),
        new("HostProductTypeService", "GetPendingSwitch", "查询", "系统产品类型", "待处理切换"),
        new("HostProductTypeService", "ApplySwitch", "修改", "系统产品类型", "切换系统产品类型"),
    ];

    // These are helpers or alternate lifetime forms, reachable from their owning
    // type references. Keep their classification explicit without extra menu rows.
    private static readonly HostEntry[] HostHelpers =
    [
        new("HostDiskMountLease", "ReleaseAsync", "修改", "存储", "结束映像使用并清理"),
        new("HostDiskMountLease", "DisposeAsync", "修改", "存储", "异步释放映像使用期"),
        new("HyperVCpuMonitorService", ".ctor", "查询", "处理器", "创建采样器"),
        new("HyperVCpuMonitorService", "DisposeAsync", "查询", "处理器", "异步释放采样器"),
        new("HostCpuGroupService", "GetAllAsync", "查询", "处理器组", "读取全部处理器组"),
        new("HostCpuGroupService", "GetAsync", "查询", "处理器组", "读取指定处理器组"),
        new("HostCpuGroupInfo", "MembersComplete", "查询", "处理器组", "MembersComplete"),
        new("HostCpuGroupInfo", "MembersError", "查询", "处理器组", "MembersError"),
        new("HostCpuGroupInfo", "Revision", "查询", "处理器组", "Revision"),
        new("HostCpuGroupInfo", "ObservedAtUtc", "查询", "处理器组", "ObservedAtUtc"),
        new("HostGpuService", "IsGpuPartitioningVersionSupported", "查询", "GPU", "GPU 分区的版本条件"),
        new("ArpSnoopService", "Start", "查询", "ARP 嗅探", "启动捕获"),
        new("ArpSnoopService", "Dispose", "查询", "ARP 嗅探", "释放捕获资源"),
        new("HostNvmeService", "IsNativeNvmeSupported", "查询", "存储", "原生 NVMe 开关的版本条件"),
        new("SwitchUpstreamAdapter", "ToString", "查询", "网络", "上游网卡名称格式化"),
        new("VmGroupSnapshot", "CanContain", "修改", "分组", "校验分组包含关系"),
        new("VmGroupSnapshot", "GetVmIds", "查询", "分组", "从快照收集虚拟机成员"),
        new("HostProductTypeService", "CanApplySwitch", "修改", "系统产品类型", "校验切换准备状态"),
        new("HyperVCpuMonitorService", "Dispose", "查询", "处理器", "同步释放采样器"),
        new("HostNetworkService", "NormalizeDynamicMacAddress", "修改", "网络", "规范化动态 MAC 地址"),
        new("HostNetworkService", "ValidateDynamicMacAddressRange", "修改", "网络", "校验动态 MAC 地址池"),
        new("SwitchConfigurationRules", "GetAvailableCreationAdapters", "修改", "网络", "筛选创建交换机的上游"),
        new("SwitchConfigurationRules", "GetCreationError", "修改", "网络", "校验交换机创建参数"),
        new("SwitchConfigurationRules", "GetDeletionError", "修改", "网络", "校验交换机删除目标"),
        new("SwitchConfigurationRules", "GetUpdateConflict", "修改", "网络", "校验交换机修改冲突"),
        new("SwitchConfigurationRules", "IsConfigurationComplete", "修改", "网络", "校验交换机配置完整性"),
        new("SwitchConfigurationRules", "IsDefaultSwitch", "查询", "网络", "判别默认交换机"),
        new("SwitchConfigurationRules", "RequiresUpstream", "修改", "网络", "判别交换机上游要求"),
        new("VmGroupMoveRules", "CanMove", "修改", "分组", "校验分组移动"),
    ];

    private static HostEntry? FindHostEntry(TypeEntry type, MemberEntry member) =>
        HostEntries.SingleOrDefault(entry => entry.Owner == type.Symbol.Name && entry.Member == member.Name);

    private static HostEntry? FindHostNavigation(TypeEntry type, MemberEntry member) =>
        FindHostEntry(type, member) ?? HostHelpers.SingleOrDefault(entry => entry.Owner == type.Symbol.Name && entry.Member == member.Name);

    private static string[][]? HostDeclaredPaths(TypeEntry type)
    {
        if (!type.Path.StartsWith("Management/Host/", StringComparison.Ordinal)) return null;
        var paths = HostEntries.Concat(HostHelpers).Where(entry => entry.Owner == type.Symbol.Name)
            .Select(entry => HostArea(entry.Area, entry.Category)).DistinctBy(NavigationKey).ToArray();
        return paths.Length == 0 ? null : paths;
    }

    private static object? HostEntryMetadata(TypeEntry type, MemberEntry member, ScanResult scan)
    {
        var entry = FindHostEntry(type, member);
        if (entry == null) return null;
        var symbol = type.Symbol.GetMembers(member.Name).FirstOrDefault();
        string valueType = symbol switch
        {
            IPropertySymbol property => property.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
            IFieldSymbol { Type: INamedTypeSymbol { Name: "VmQueryField", Arity: 1 } fieldType } =>
                fieldType.TypeArguments[0].ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
            IFieldSymbol field => field.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
            IMethodSymbol method when method.MethodKind == MethodKind.Constructor => type.Symbol.Name,
            IMethodSymbol method => method.ReturnType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
            _ => ""
        };
        object? Reference(string? key)
        {
            if (key == null) return null;
            var parts = key.Split('.');
            var owner = scan.Types.Single(candidate => candidate.Symbol.Name == parts[0]);
            var target = owner.Members.Single(candidate => candidate.Name == parts[1]);
            return new { id = Anchor(target.Id), expression = key };
        }
        return new { area = entry.Area, category = entry.Category, label = entry.Label, order = Array.IndexOf(HostEntries, entry), valueType,
            reader = Reference(entry.Reader), related = Reference(entry.Related) };
    }

    private static void ValidateHostEntries(ScanResult scan)
    {
        foreach (var usage in HostUsageSections)
        {
            var owner = scan.Types.SingleOrDefault(type => type.Symbol.Name == usage.Owner);
            if (owner == null || string.IsNullOrWhiteSpace(owner.Doc.Element("remarks")?.Value)
                || !HostEntries.Any(entry => entry.Area == usage.Area && entry.Category == usage.Category))
                scan.Errors.Add($"宿主调用说明 {usage.Owner} 缺少源说明或对应目录。");
        }
        foreach (var entry in HostEntries.Concat(HostHelpers))
        {
            var candidates = scan.Types.Where(type => type.Symbol.Name == entry.Owner)
                .SelectMany(type => type.Members.Where(member => member.Name == entry.Member)).ToArray();
            if (candidates.Length == 0 || (candidates.Length > 1 && candidates.Any(member => member.Kind != "方法")))
                scan.Errors.Add($"宿主目录 {entry.Owner}.{entry.Member} 必须对应公开成员或同名方法重载。");
            foreach (string reference in new[] { entry.Reader, entry.Related }.OfType<string>())
            {
                var parts = reference.Split('.');
                if (parts.Length != 2 || scan.Types.Where(type => type.Symbol.Name == parts[0])
                    .SelectMany(type => type.Members.Where(member => member.Name == parts[1])).Count() != 1)
                    scan.Errors.Add($"宿主目录引用无效：{reference}。");
            }
        }
        if (HostEntries.Concat(HostHelpers).GroupBy(entry => (entry.Owner, entry.Member)).Any(group => group.Count() > 1))
            scan.Errors.Add("宿主目录存在重复成员。");
        foreach (var type in scan.Types)
        foreach (var member in type.Members.Where(member => member.Kind == "方法"))
            if (MemberNavigationPaths(type, member).Any(path => path[1] == "宿主") && FindHostNavigation(type, member) == null)
                scan.Errors.Add($"宿主成员 {type.Symbol.Name}.{member.Name} 尚未明确归入目录或辅助方法。");
    }
}
