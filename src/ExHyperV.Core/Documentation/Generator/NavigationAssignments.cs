internal static partial class ApiDocument
{
    private static string[] VmArea(params string[] parts) => ["管理层面", "虚拟机", .. parts];
    private static string[] HostArea(params string[] parts) => ["管理层面", "宿主", .. parts];

    // Some existing types serve several capabilities. Classify their members according to their query or modification role.
    private static string[][] DeclaredNavigationPaths(TypeEntry type) => HostDeclaredPaths(type) ?? (type.Symbol.Name switch
    {
        "VmRegistrationState" => [VmArea("实例")],
        "VmIpService" or "VmIpAddressResolution" => [VmArea("查询", "网络")],
        "VmCpuCoreMetric" => [VmArea("查询", "处理器")],
        "VmDiskStatus" => [VmArea("查询", "存储")],
        "VmConsoleState" => [VmArea("查询", "控制台")],
        "VmBatteryState" => [VmArea("查询", "电源")],
        "VmGroup" or "VmGroupKind" or "VmGroupSnapshot" => [HostArea("查询", "分组")],
        "SpacetimeNode" or "SpacetimeNodeType" => [VmArea("查询", "检查点")],
        "VmGroupMove" or "VmGroupMoveRules" => [HostArea("修改", "分组")],
        _ => [NavigationPath(type.Path)]
    });

    private static string[][] MemberNavigationPaths(TypeEntry type, MemberEntry member)
    {
        if (FindHostNavigation(type, member) is { } host) return [HostArea(host.Area, host.Category)];
        string owner = type.Symbol.Name, name = member.Name;
        string[][]? assigned = (owner, name) switch
        {
            ("VmMmioService", "CheckMmioSpaceAsync") => [VmArea("查询", "MMIO")],
            ("VmMmioService", "UpdateMmioSpaceAsync") => [VmArea("修改", "MMIO")],
            ("VmMetadataService", "SetOperatingSystemTypeAsync") => [VmArea("修改", "额外元数据")],
            ("VmGroupService", "ReadAsync") => [HostArea("查询", "分组")],
            ("VmGroupService", "MoveAsync") => [HostArea("修改", "分组")],
            ("VmSpacetimeService", "SetCheckpointsEnabledAsync") =>
                [VmArea("修改", "检查点")],
            ("VmSpacetimeService", "GetCheckpointsEnabledAsync" or "GetSpacetimeNodesAsync") =>
                [VmArea("查询", "检查点")],
            ("VmGpuService", "GetVmGpuAssignmentsAsync" or "CheckVmForGpuAsync") => [VmArea("查询", "GPU")],
            // Deployment preparation and progress belong to the workflow, not VM field reads.
            ("VmGpuService", "GetAvailableScriptsAsync" or "ProvisionLinuxGpuAsync" or
                "ScanVmPartitionsAsync" or "SyncWindowsDriversAsync") => [VmArea("修改", "GPU")],
            ("VmGpuRepairService", "FindStaleGpuPartitionsAsync") => [VmArea("查询", "GPU")],
            ("CpuAffinityService", "GetCpuAffinityAsync" or "ReadStateAsync") => [VmArea("查询", "处理器")],
            ("VmPowerService", "GetBatteryStateAsync") => [VmArea("查询", "电源")],
            ("VmBootService", "GetBootNumLockAsync" or "GetBootOrderAsync") => [VmArea("查询", "引导")],
            ("VmConsoleSupportService", "GetConsoleStateAsync") => [VmArea("查询", "控制台")],
            ("VmDisplaySettingsService", "GetResolutionAsync") => [VmArea("查询", "显示")],
            ("VmMemoryService", "GetVmMemorySettingsAsync") => [VmArea("查询", "内存")],
            ("VmMetadataService", "ReadAsync") => [VmArea("查询", "额外元数据")],
            ("VmMmioService", "GetSettingsAsync") => [VmArea("查询", "MMIO")],
            ("VmNetworkService", "GetNetworkAdaptersAsync") => [VmArea("查询", "网络")],
            ("VmPcieService", "GetSettingsAsync") => [VmArea("查询", "PCIe")],
            ("VmProcessorService", "GetVmProcessorAsync") => [VmArea("查询", "处理器")],
            ("VmSecurityService", "GetSecuritySettingsAsync") => [VmArea("查询", "安全")],
            ("VmStorageService", "GetVirtualDiskSizesAsync" or "GetVmStorageItemsAsync" or "FindStalePassthroughDisksAsync") =>
                [VmArea("查询", "存储")],
            _ => null
        };
        if (assigned != null) return assigned;

        return member.Path == type.Path ? DeclaredNavigationPaths(type) : [NavigationPath(member.Path)];
    }
}
