internal static partial class ApiDocument
{
    // New public locations require an explicit capability assignment.
    // A null directory declares a section whose API assignments are not yet defined.
    private static readonly (string? Directory, string[] Path)[] NavigationCatalog =
    [
        ("Management/VirtualMachines/Query", ["管理层面", "虚拟机", "查询"]),
        ("Management/VirtualMachines/Query/Fields/Identity", ["管理层面", "虚拟机", "查询", "基本信息"]),
        ("Management/VirtualMachines/Query/Fields/Metadata", ["管理层面", "虚拟机", "查询", "额外元数据"]),
        ("Management/VirtualMachines/Query/Fields/Processor", ["管理层面", "虚拟机", "查询", "处理器"]),
        ("Management/VirtualMachines/Query/Fields/Memory", ["管理层面", "虚拟机", "查询", "内存"]),
        ("Management/VirtualMachines/Query/Fields/Storage", ["管理层面", "虚拟机", "查询", "存储"]),
        ("Management/VirtualMachines/Query/Fields/Network", ["管理层面", "虚拟机", "查询", "网络"]),
        ("Management/VirtualMachines/Query/Fields/Gpu", ["管理层面", "虚拟机", "查询", "GPU"]),
        ("Management/VirtualMachines/Query/Fields/Pcie", ["管理层面", "虚拟机", "查询", "PCIe"]),
        ("Management/VirtualMachines/Query/Fields/Boot", ["管理层面", "虚拟机", "查询", "引导"]),
        ("Management/VirtualMachines/Query/Fields/Security", ["管理层面", "虚拟机", "查询", "安全"]),
        ("Management/VirtualMachines/Query/Fields/Display", ["管理层面", "虚拟机", "查询", "显示"]),
        ("Management/VirtualMachines/Query/Fields/Mmio", ["管理层面", "虚拟机", "查询", "MMIO"]),
        ("Management/VirtualMachines/Query/Fields/Console", ["管理层面", "虚拟机", "查询", "控制台"]),
        ("Management/VirtualMachines/Query/Fields/Power", ["管理层面", "虚拟机", "查询", "电源"]),
        ("Management/VirtualMachines/Query/Fields/Checkpoints", ["管理层面", "虚拟机", "查询", "检查点"]),
        ("Management/VirtualMachines/Network", ["管理层面", "虚拟机", "修改", "网络"]),
        (null, ["管理层面", "虚拟机", "修改"]),
        ("Management/VirtualMachines/Identity", ["管理层面", "虚拟机", "修改", "基本信息"]),
        ("Management/VirtualMachines/Metadata", ["管理层面", "虚拟机", "修改", "额外元数据"]),
        ("Management/VirtualMachines/Processor", ["管理层面", "虚拟机", "修改", "处理器"]),
        ("Management/VirtualMachines/Memory", ["管理层面", "虚拟机", "修改", "内存"]),
        ("Management/VirtualMachines/Storage", ["管理层面", "虚拟机", "修改", "存储"]),
        ("Management/VirtualMachines/Gpu/Assignment", ["管理层面", "虚拟机", "修改", "GPU"]),
        ("Management/VirtualMachines/Gpu/Repair", ["管理层面", "虚拟机", "修改", "GPU"]),
        ("Management/VirtualMachines/Pcie", ["管理层面", "虚拟机", "修改", "PCIe"]),
        ("Management/VirtualMachines/Boot", ["管理层面", "虚拟机", "修改", "引导"]),
        ("Management/VirtualMachines/Security", ["管理层面", "虚拟机", "修改", "安全"]),
        ("Management/VirtualMachines/Display", ["管理层面", "虚拟机", "修改", "显示"]),
        ("Management/VirtualMachines/Mmio", ["管理层面", "虚拟机", "修改", "MMIO"]),
        ("Management/VirtualMachines/Console", ["管理层面", "虚拟机", "修改", "控制台"]),
        ("Management/VirtualMachines/Power", ["管理层面", "虚拟机", "修改", "电源"]),
        ("Management/VirtualMachines/Checkpoints", ["管理层面", "虚拟机", "修改", "检查点"]),
        ("Management/VirtualMachines/HistoryDisks", ["管理层面", "虚拟机", "修改", "检查点"]),
        ("Management/VirtualMachines/Gpu/Deployment", ["管理层面", "虚拟机", "修改", "GPU"]),
        (null, ["管理层面", "虚拟机", "实例"]),
        ("Management/VirtualMachines/Instances/Creation", ["管理层面", "虚拟机", "实例", "创建"]),
        ("Management/VirtualMachines/Instances/Deletion", ["管理层面", "虚拟机", "实例", "删除"]),
        ("Management/VirtualMachines/Instances/Import", ["管理层面", "虚拟机", "实例", "导入"]),
        ("Management/VirtualMachines/Instances/Export", ["管理层面", "虚拟机", "实例", "导出"]),
        ("Management/VirtualMachines/Files", ["管理层面", "虚拟机", "文件访问"]),
        (null, ["管理层面", "宿主", "查询"]),
        (null, ["管理层面", "宿主", "修改"]),
        (null, ["管理层面", "宿主", "查询", "基本信息"]),
        (null, ["管理层面", "宿主", "查询", "处理器"]),
        (null, ["管理层面", "宿主", "查询", "处理器组"]),
        (null, ["管理层面", "宿主", "查询", "内存"]),
        (null, ["管理层面", "宿主", "查询", "存储"]),
        (null, ["管理层面", "宿主", "查询", "网络"]),
        (null, ["管理层面", "宿主", "查询", "GPU"]),
        (null, ["管理层面", "宿主", "查询", "PCIe"]),
        (null, ["管理层面", "宿主", "查询", "调度器"]),
        (null, ["管理层面", "宿主", "查询", "电源"]),
        (null, ["管理层面", "宿主", "查询", "Hyper-V"]),
        (null, ["管理层面", "宿主", "查询", "控制台"]),
        (null, ["管理层面", "宿主", "查询", "分组"]),
        (null, ["管理层面", "宿主", "查询", "ARP 嗅探"]),
        (null, ["管理层面", "宿主", "查询", "OpenHCL"]),
        (null, ["管理层面", "宿主", "查询", "系统产品类型"]),
        (null, ["管理层面", "宿主", "修改", "处理器"]),
        (null, ["管理层面", "宿主", "修改", "处理器组"]),
        (null, ["管理层面", "宿主", "修改", "内存"]),
        (null, ["管理层面", "宿主", "修改", "存储"]),
        (null, ["管理层面", "宿主", "修改", "网络"]),
        (null, ["管理层面", "宿主", "修改", "GPU"]),
        (null, ["管理层面", "宿主", "修改", "PCIe"]),
        (null, ["管理层面", "宿主", "修改", "调度器"]),
        (null, ["管理层面", "宿主", "修改", "电源"]),
        (null, ["管理层面", "宿主", "修改", "Hyper-V"]),
        (null, ["管理层面", "宿主", "修改", "控制台"]),
        (null, ["管理层面", "宿主", "修改", "分组"]),
        (null, ["管理层面", "宿主", "修改", "ARP 嗅探"]),
        (null, ["管理层面", "宿主", "修改", "OpenHCL"]),
        (null, ["管理层面", "宿主", "修改", "系统产品类型"]),
        ("Management/Host/System", ["管理层面", "宿主", "查询", "基本信息"]),
        ("Management/Host/Processor", ["管理层面", "宿主", "查询", "处理器"]),
        ("Management/Host/ProcessorGroups", ["管理层面", "宿主", "查询", "处理器组"]),
        ("Management/Host/HyperV", ["管理层面", "宿主", "查询", "Hyper-V"]),
        ("Management/Host/Memory", ["管理层面", "宿主", "查询", "内存"]),
        ("Management/Host/Scheduler", ["管理层面", "宿主", "查询", "调度器"]),
        ("Management/Host/Power", ["管理层面", "宿主", "查询", "电源"]),
        ("Management/Host/Storage", ["管理层面", "宿主", "查询", "存储"]),
        ("Management/Host/Network", ["管理层面", "宿主", "查询", "网络"]),
        ("Management/Host/Gpu", ["管理层面", "宿主", "查询", "GPU"]),
        ("Management/Host/Pcie", ["管理层面", "宿主", "查询", "PCIe"]),
        ("Management/Host/Arp", ["管理层面", "宿主", "查询", "ARP 嗅探"]),
        ("Management/Host/OpenHcl", ["管理层面", "宿主", "查询", "OpenHCL"]),
        ("Management/Host/ProductType", ["管理层面", "宿主", "查询", "系统产品类型"]),
        ("Management/Host/Console", ["管理层面", "宿主", "查询", "控制台"]),
        ("Management/Host/Groups", ["管理层面", "宿主", "查询", "分组"]),
        ("Management/Tools/Vmcx", ["管理层面", "工具", "VMCX 文件"]),
        ("Management/Tools/Iso", ["管理层面", "工具", "ISO 构建"]),
        ("Management/Tools/Partitions", ["管理层面", "工具", "磁盘分区解析"]),
        ("Business/Connection", ["业务层面", "连接"]),
        ("Business/Backends/FreeRdp", ["业务层面", "连接", "FreeRDP 后端"]),
        ("Business/Video", ["业务层面", "视频"]),
        ("Business/Audio", ["业务层面", "音频"]),
        (null, ["业务层面", "控制"]),
        ("Business/Keyboard", ["业务层面", "控制", "键盘"]),
        ("Business/Pointer", ["业务层面", "控制", "鼠标"]),
        ("Business/Clipboard", ["业务层面", "剪贴板"]),
        ("Common/Results", ["使用说明", "结果、取消和资源释放"]),
        ("Common/Lifetime", ["使用说明", "结果、取消和资源释放"]),
    ];
    private static readonly (string Directory, string[] Path)[] NavigationLocations =
        NavigationCatalog.Where(entry => entry.Directory != null)
            .Select(entry => (Directory: entry.Directory!, entry.Path))
            .OrderByDescending(entry => entry.Directory.Length).ToArray();

    private static string[] NavigationPath(string path)
    {
        string normalized = path.Replace('\\', '/');
        foreach (var entry in NavigationLocations)
            if (normalized.StartsWith(entry.Directory + "/", StringComparison.Ordinal)) return entry.Path;
        throw new InvalidOperationException($"公开 API 源文件没有功能归属：{path}。请在 NavigationCatalog 中明确分类。");
    }

    private static string[][] NavigationPaths(TypeEntry type)
    {
        var paths = DeclaredNavigationPaths(type).Concat(type.Members.SelectMany(member => MemberNavigationPaths(type, member)))
            .DistinctBy(NavigationKey, StringComparer.Ordinal).ToArray();
        var catalog = NavigationCatalog.Select(entry => entry.Path).DistinctBy(NavigationKey, StringComparer.Ordinal).ToArray();
        foreach (var path in paths)
            if (!catalog.Any(item => NavigationKey(item) == NavigationKey(path)))
                throw new InvalidOperationException($"{type.Name} 的功能归属未列入目录：{NavigationKey(path)}。");
        return catalog
            .Where(path => paths.Any(item => NavigationKey(item) == NavigationKey(path))).ToArray();
    }

    private static string NavigationKey(string[] path) => string.Join(" / ", path);

    private static string[][] CategoriesFor(ScanResult scan)
    {
        var keys = scan.Types.SelectMany(NavigationPaths).Select(NavigationKey).ToHashSet(StringComparer.Ordinal);
        return NavigationCatalog.Where(entry => entry.Directory == null || keys.Contains(NavigationKey(entry.Path)))
            .Select(entry => entry.Path).DistinctBy(NavigationKey, StringComparer.Ordinal).ToArray();
    }
}
