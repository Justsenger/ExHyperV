using System.ComponentModel;
using ExHyperV.Models;
using ExHyperV.Services;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace ExHyperV.Mcp.Tools;

[McpServerToolType]
public sealed class VmTools
{
    [McpServerTool(Name = "vm_memory_get", Title = "读取虚拟机内存配置", ReadOnly = true, OpenWorld = false)]
    [Description("读取内存配置和宿主支持情况。Startup、Minimum、Maximum 的单位为 MiB。")]
    public static Task<CallToolResult> MemoryGet(Guid vmId) => ToolReply.Run(async () =>
        ToolReply.From(await VmMemoryService.GetVmMemorySettingsAsync(ToolReply.VmId(vmId))));

    [McpServerTool(Name = "vm_memory_set", Title = "修改虚拟机内存配置", Destructive = true, OpenWorld = false)]
    [Description("仅修改本次填写的内存字段，其余字段由 Core 保留。容量使用 MiB；执行条件和字段联动由 Core 校验。")]
    public static Task<CallToolResult> MemorySet(Guid vmId,
        [Description("启动内存，MiB。")] long? startupMiB = null,
        [Description("动态内存开关。")] bool? dynamicMemory = null,
        [Description("动态内存下限，MiB。")] long? minimumMiB = null,
        [Description("动态内存上限，MiB。")] long? maximumMiB = null,
        [Description("动态内存缓冲百分比。")] int? bufferPercent = null,
        [Description("Hyper-V 原始内存权重。")] uint? weight = null) => ToolReply.Run(async () =>
    {
        var settings = new VmMemorySettings();
        var changed = new List<string>();
        if (startupMiB.HasValue) { settings.Startup = startupMiB.Value; changed.Add(nameof(settings.Startup)); }
        if (dynamicMemory.HasValue) { settings.DynamicMemoryEnabled = dynamicMemory.Value; changed.Add(nameof(settings.DynamicMemoryEnabled)); }
        if (minimumMiB.HasValue) { settings.Minimum = minimumMiB.Value; changed.Add(nameof(settings.Minimum)); }
        if (maximumMiB.HasValue) { settings.Maximum = maximumMiB.Value; changed.Add(nameof(settings.Maximum)); }
        if (bufferPercent.HasValue) { settings.Buffer = bufferPercent.Value; changed.Add(nameof(settings.Buffer)); }
        if (weight.HasValue) { settings.Weight = weight.Value; changed.Add(nameof(settings.Weight)); }
        if (changed.Count == 0) return ToolReply.Error("请至少选择一项内存字段。");
        // Core re-reads the actual power state; this parameter is retained in its current contract.
        return ToolReply.From(await VmMemoryService.SetVmMemorySettingsAsync(ToolReply.VmId(vmId), settings, isVmRunning: false, changedProperties: changed));
    });

    [McpServerTool(Name = "vm_processor_get", Title = "读取虚拟机处理器配置", ReadOnly = true, OpenWorld = false)]
    [Description("读取虚拟处理器配置。Reservation、Limit 使用 0.001% 原始单位，100000 表示 100%。")]
    public static Task<CallToolResult> ProcessorGet(Guid vmId) => ToolReply.Run(async () =>
        ToolReply.From(await VmProcessorService.GetVmProcessorAsync(ToolReply.VmId(vmId))));

    [McpServerTool(Name = "vm_processor_set", Title = "修改虚拟机处理器配置", Destructive = true, OpenWorld = false)]
    [Description("仅提交填写的处理器字段；数量、运行状态与宿主能力由 Core 校验。比例使用 0.001%，100000 表示 100%。")]
    public static Task<CallToolResult> ProcessorSet(Guid vmId,
        [Description("虚拟处理器数量。")] int? count = null,
        [Description("资源保留比例，0.001%。")] ulong? reservation = null,
        [Description("资源上限比例，0.001%。")] ulong? limit = null,
        [Description("CPU 资源相对权重。")] int? relativeWeight = null,
        [Description("向虚拟机公开嵌套虚拟化指令。")] bool? exposeVirtualizationExtensions = null,
        [Description("迁移兼容性开关。")] bool? compatibilityForMigrationEnabled = null) => ToolReply.Run(async () =>
    {
        var settings = new VmProcessorSettings();
        var changed = new List<string>();
        if (count.HasValue) { settings.Count = count.Value; changed.Add(nameof(settings.Count)); }
        if (reservation.HasValue) { settings.Reservation = reservation.Value; changed.Add(nameof(settings.Reservation)); }
        if (limit.HasValue) { settings.Limit = limit.Value; changed.Add(nameof(settings.Limit)); }
        if (relativeWeight.HasValue) { settings.RelativeWeight = relativeWeight.Value; changed.Add(nameof(settings.RelativeWeight)); }
        if (exposeVirtualizationExtensions.HasValue) { settings.ExposeVirtualizationExtensions = exposeVirtualizationExtensions; changed.Add(nameof(settings.ExposeVirtualizationExtensions)); }
        if (compatibilityForMigrationEnabled.HasValue) { settings.CompatibilityForMigrationEnabled = compatibilityForMigrationEnabled; changed.Add(nameof(settings.CompatibilityForMigrationEnabled)); }
        if (changed.Count == 0) return ToolReply.Error("请至少选择一项处理器字段。");
        return ToolReply.From(await VmProcessorService.SetVmProcessorAsync(ToolReply.VmId(vmId), settings, changed));
    });

    [McpServerTool(Name = "vm_power", Title = "执行虚拟机电源操作", Destructive = true, OpenWorld = false)]
    [Description("执行一项明确的电源操作。Start 启动；Stop 正常关机；Restart 正常重启；Save 保存；Suspend 暂停；TurnOff 强制断电。原样报告失败，后续修复由调用方另行决定。")]
    public static Task<CallToolResult> Power(Guid vmId, VmPowerAction action) => ToolReply.Run(async () =>
        ToolReply.From(await VmPowerService.ExecuteControlActionAsync(ToolReply.VmId(vmId), action)));

    [McpServerTool(Name = "vm_rename", Title = "修改虚拟机名称", Destructive = true, OpenWorld = false)]
    [Description("通过稳定 GUID 定位虚拟机并修改名称。")]
    public static Task<CallToolResult> Rename(Guid vmId, string newName) => ToolReply.Run(async () =>
        ToolReply.From(await VmEditService.RenameVmAsync(ToolReply.VmId(vmId), newName)));

    [McpServerTool(Name = "vm_create", Title = "创建虚拟机", Destructive = true, OpenWorld = false)]
    [Description("创建一台虚拟机，完成后保持 Core 的创建状态，开机使用 vm_power。先读取 vm_creation_options。Path 是配置根目录；MemoryMiB 为 MiB，DiskSizeGiB 为 GiB；DiskMode 为 0 新建、1 已有、2 稍后添加。SwitchId 填写交换机 GUID，null 表示保持断开。返回实际名称、路径及失败后的保留资源。")]
    public static Task<CallToolResult> Create(
        [Description("Core 的创建参数；Name、Path、Version 等值依据用户要求及宿主能力填写。")] VmCreationParameters parameters) => ToolReply.Run(async () =>
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ToolReply.RequireAdministrator();
        var result = await VmCreateService.CreateVirtualMachineAsync(parameters);
        return ToolReply.Result(result, !result.Success);
    });

    [McpServerTool(Name = "vm_delete_preview", Title = "预览虚拟机文件清理", ReadOnly = true, OpenWorld = false)]
    [Description("预览彻底删除的配置、磁盘和保留项。执行时 Core 会再次核对归属。")]
    public static Task<CallToolResult> DeletePreview(Guid vmId) => ToolReply.Run(async () =>
        ToolReply.From(await VmDeleteService.PreviewPurgeAsync(ToolReply.VmId(vmId))));

    [McpServerTool(Name = "vm_unregister", Title = "注销虚拟机并保留文件", Destructive = true, OpenWorld = false)]
    [Description("注销指定 GUID 的虚拟机，保留磁盘文件。返回真实注册状态、恢复位置及清理错误。displayName 用于操作说明，目标由 GUID 确定。")]
    public static Task<CallToolResult> Unregister(Guid vmId, string displayName) => ToolReply.Run(async () =>
    {
        var result = await VmDeleteService.DeleteVmAsync(displayName, ToolReply.VmId(vmId));
        return ToolReply.Result(result, !result.Success);
    });

    [McpServerTool(Name = "vm_purge", Title = "删除虚拟机及专属文件", Destructive = true, OpenWorld = false)]
    [Description("注销虚拟机并清理 Core 确认专属的配置和磁盘。先查看 vm_delete_preview 并取得用户对删除范围的决定；执行结果中 RetainedPaths 与 CleanupError 需要继续核对。")]
    public static Task<CallToolResult> Purge(Guid vmId, string displayName) => ToolReply.Run(async () =>
    {
        var result = await VmDeleteService.PurgeVmAsync(displayName, ToolReply.VmId(vmId));
        return ToolReply.Result(result, !result.Success);
    });

    [McpServerTool(Name = "vm_network_adapters", Title = "读取虚拟机网卡", ReadOnly = true, OpenWorld = false)]
    [Description("读取虚拟机网卡身份、连接与配置；后续查询可通过 vm_query 选择 Network 字段。")]
    public static Task<CallToolResult> Network(Guid vmId) => ToolReply.Run(async () =>
        ToolReply.From(await VmNetworkService.GetNetworkAdaptersAsync(ToolReply.VmId(vmId))));

    [McpServerTool(Name = "vm_checkpoints", Title = "读取虚拟机检查点", ReadOnly = true, OpenWorld = false)]
    [Description("读取检查点及当前状态节点。")]
    public static Task<CallToolResult> Checkpoints(Guid vmId) => ToolReply.Run(async () =>
        ToolReply.From(await VmSpacetimeService.GetSpacetimeNodesAsync(ToolReply.VmId(vmId))));
}
