using System.ComponentModel;
using ExHyperV.Models;
using ExHyperV.Services;
using ExHyperV.Tools;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace ExHyperV.Mcp.Tools;

[McpServerToolType]
public sealed class GpuTools(GpuSessions sessions, VmGpuService gpu, VmQueryService query)
{
    private static CallToolResult Snapshot(GpuDeploymentSnapshot status) => ToolReply.Result(status,
        status.State is GpuDeploymentState.Failed or GpuDeploymentState.CleanupRequired);

    private Task<CallToolResult> Change(Guid sessionId, Func<GpuDeploymentSession, Task<GpuDeploymentSnapshot>> operation,
        CancellationToken cancellationToken) => ToolReply.Run(async () =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var lease = sessions.Use(sessionId);
        var session = sessions.Get(sessionId);
        using var registration = cancellationToken.Register(session.RequestCancellation);
        return Snapshot(await operation(session));
    });

    [McpServerTool(Name = "vm_gpu_assignments", Title = "读取虚拟机 GPU 分配", ReadOnly = true, OpenWorld = false)]
    [Description("读取目标虚拟机当前的 GPU 分区分配。性能指标可通过 vm_query 中的 GPU 字段读取。")]
    public Task<CallToolResult> Assignments(Guid vmId) => ToolReply.Run(async () =>
        ToolReply.From(await gpu.GetVmGpuAssignmentsAsync(ToolReply.VmId(vmId))));

    [McpServerTool(Name = "gpu_deployment_create", Title = "建立 GPU 部署流程", Destructive = false, OpenWorld = false)]
    [Description("为指定虚拟机和显卡建立进程内流程编号，返回 Created 状态；实际部署由 gpu_deployment_start 开始。显卡身份来自 host_gpus。")]
    public Task<CallToolResult> Create(Guid vmId,
        [Description("host_gpus 返回的显卡 InstanceId。")] string gpuInstanceId,
        [Description("AddWithDrivers 分配并部署驱动；AddWithoutDrivers 仅分配；UpdateDrivers 更新现有驱动。")] GpuDeploymentMode mode,
        CancellationToken cancellationToken = default) => ToolReply.Run(async () =>
    {
        ToolReply.VmId(vmId);
        cancellationToken.ThrowIfCancellationRequested();
        var devices = await HostGpuService.GetHostGpusAsync();
        if (!devices.HasData) return ToolReply.From(devices);
        var selected = devices.Data.SingleOrDefault(device => string.Equals(device.InstanceId, gpuInstanceId, StringComparison.OrdinalIgnoreCase));
        if (selected == null) return ToolReply.Error("请从 host_gpus 返回的清单中选择显卡身份。", ApiErrorKind.NotFound);
        var target = await query.QueryAsync(new VmQueryRequest([VmIdentityFields.Name], [vmId]), cancellationToken);
        if (!target.HasData) return ToolReply.From(target);
        var name = target.Data.VirtualMachines.SingleOrDefault()?.Get(VmIdentityFields.Name);
        if (name?.HasValue != true) return ToolReply.Error("目标虚拟机名称读取失败，请重新查询。", ApiErrorKind.NotFound);
        cancellationToken.ThrowIfCancellationRequested();
        return Snapshot(sessions.Add(new GpuDeploymentSession(vmId, name.Value!, selected, gpu, query, mode)));
    });

    [McpServerTool(Name = "gpu_deployment_status", Title = "查看 GPU 部署进度", ReadOnly = true, OpenWorld = false)]
    [Description("读取流程快照。PendingDecision 给出当前确认内容，AllowedActions 给出允许的下一步。")]
    public Task<CallToolResult> Status(Guid sessionId) => ToolReply.Run(() => Task.FromResult(Snapshot(sessions.Get(sessionId).GetStatus())));

    [McpServerTool(Name = "gpu_deployment_start", Title = "开始 GPU 部署", Destructive = true, OpenWorld = false)]
    [Description("推进 Core 部署流程直到等待决定或结束；可能修改宿主策略和虚拟机配置。调用耗时取决于当前步骤。")]
    public Task<CallToolResult> Start(Guid sessionId, CancellationToken cancellationToken = default) =>
        Change(sessionId, session => session.StartAsync(), cancellationToken);

    [McpServerTool(Name = "gpu_deployment_confirm", Title = "提交 GPU 部署确认", Destructive = true, OpenWorld = false)]
    [Description("将 PendingDecision 中的具体内容交给用户决定，再传回 Accepted。requestId 必须为当前 PendingDecision.Id。PowerOff 确认会直接断电，InstallWindows 确认会写入离线系统分区。")]
    public Task<CallToolResult> Confirm(Guid sessionId, Guid requestId, bool accepted, CancellationToken cancellationToken = default) =>
        Change(sessionId, session => session.SubmitDecisionAsync(new GpuConfirmation(requestId, accepted)), cancellationToken);

    [McpServerTool(Name = "gpu_deployment_partition", Title = "选择 GPU 驱动目标分区", Destructive = true, OpenWorld = false)]
    [Description("使用当前 PendingDecision.Partitions 中的 Id 选择分区，并由 Core 继续判定。")]
    public Task<CallToolResult> Partition(Guid sessionId, Guid requestId, string partitionId, CancellationToken cancellationToken = default) =>
        Change(sessionId, session => session.SubmitDecisionAsync(new GpuPartitionSelection(requestId, partitionId)), cancellationToken);

    [McpServerTool(Name = "gpu_deployment_back", Title = "返回 GPU 分区选择", Destructive = true, OpenWorld = false)]
    [Description("当前决定的 CanGoBack 为 true 时，请求 Core 返回分区选择。")]
    public Task<CallToolResult> Back(Guid sessionId, Guid requestId, CancellationToken cancellationToken = default) =>
        Change(sessionId, session => session.SubmitDecisionAsync(new GpuBackToPartitions(requestId)), cancellationToken);

    [McpServerTool(Name = "gpu_linux_scripts", Title = "列出 Linux GPU 部署脚本", ReadOnly = true, OpenWorld = true)]
    [Description("从 Core 配置的脚本目录查询可用 Linux GPU 部署脚本。")]
    public Task<CallToolResult> LinuxScripts() => ToolReply.Run(async () =>
        ToolReply.Result(new { success = true, scripts = await gpu.GetAvailableScriptsAsync() }));

    [McpServerTool(Name = "gpu_deployment_linux", Title = "提交 Linux GPU 部署参数", Destructive = true, OpenWorld = true)]
    [Description("在等待 LinuxCredentials 时提交已选脚本和 SSH 参数。脚本名称来自 gpu_linux_scripts；密码从本机 MCP 进程环境变量 EXHYPERV_SSH_PASSWORD 读取。此调用会通过 SSH 修改指定虚拟机。")]
    public Task<CallToolResult> Linux(Guid sessionId, Guid requestId, string scriptName, string host, string username,
        int port = 22, bool installGraphics = true, CancellationToken cancellationToken = default) =>
        Change(sessionId, async session =>
        {
            string password = Environment.GetEnvironmentVariable("EXHYPERV_SSH_PASSWORD") ?? "";
            if (string.IsNullOrEmpty(password)) throw new ArgumentException("请在启动 MCP 前配置 EXHYPERV_SSH_PASSWORD。");
            var scripts = await gpu.GetAvailableScriptsAsync();
            var script = scripts.SingleOrDefault(item => item.Name == scriptName)
                ?? throw new ArgumentException("请从 gpu_linux_scripts 选择脚本名称。");
            return await session.SubmitDecisionAsync(new GpuLinuxInput(requestId, script,
                new SshCredentials { Host = host, Port = port, Username = username, Password = password, InstallGraphics = installGraphics }));
        }, cancellationToken);

    [McpServerTool(Name = "gpu_deployment_cancel", Title = "取消 GPU 部署", Destructive = true, OpenWorld = false)]
    [Description("请求取消并等待 Core 收尾。已完成的策略、配置和驱动修改依照 Core 规则保留；结果中仍可能需要核对清理状态。")]
    public Task<CallToolResult> Cancel(Guid sessionId, CancellationToken cancellationToken = default) =>
        Change(sessionId, session => session.CancelAsync(), cancellationToken);

    [McpServerTool(Name = "gpu_deployment_retry_cleanup", Title = "重试 GPU 部署清理", Destructive = true, OpenWorld = false)]
    [Description("仅在 AllowedActions 包含 RetryCleanup 时，由 Core 重试清理。OutcomeUnknown 的处理遵循 Core 限制。")]
    public Task<CallToolResult> RetryCleanup(Guid sessionId, CancellationToken cancellationToken = default) =>
        Change(sessionId, session => session.RetryCleanupAsync(), cancellationToken);

    [McpServerTool(Name = "gpu_deployment_release", Title = "释放已结束的 GPU 流程编号", Destructive = false, OpenWorld = false)]
    [Description("释放已结束且清理状态明确的流程编号。虚拟机实际配置保持原样。")]
    public Task<CallToolResult> Release(Guid sessionId) => ToolReply.Run(async () =>
        ToolReply.Result(new { success = await sessions.ReleaseAsync(sessionId), sessionId }));
}
