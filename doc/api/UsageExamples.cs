using ExHyperV.Models;
using ExHyperV.Services;

namespace ExHyperV.Documentation;

// 这些示例只参与编译检查，不会由生成器执行。
public static class UsageExamples
{
    // example:settings-snapshot:start
    // title: 从读取快照创建内存编辑值
    // description: 检查启动内存的本次读取状态，仅提交调用方选择的字段。
    public static async Task ChangeStartupMemoryAsync(Guid vmId, long startupMiB)
    {
        var response = await VmMemoryService.GetVmMemorySettingsAsync(vmId);
        if (!response.HasData) throw new InvalidOperationException(response.Error);
        var snapshot = response.Data!;
        if (snapshot.States[nameof(VmMemorySettings.Startup)] != VmQueryValueState.Available)
            throw new InvalidOperationException("本次未取得启动内存配置。");
        var draft = snapshot.Values;
        draft.Startup = startupMiB;
        var result = await VmMemoryService.SetVmMemorySettingsAsync(vmId, draft,
            [nameof(VmMemorySettings.Startup)]);
        if (!result.Success) throw new InvalidOperationException(result.Error);
    }
    // example:settings-snapshot:end

    // example:encrypted-export:start
    // title: 创建加密导出包
    // description: 对已经导出的目录打包，选择压缩级别、AES-256 和保留源目录。密码由调用方取得；跨工具交换建议使用 ASCII 密码。
    public static async Task PackageExportAsync(string exportDirectory, string archivePath, string password,
        CancellationToken cancellationToken = default)
    {
        var result = await VmExportPackagingService.CreatePackageAsync(exportDirectory, archivePath,
            new VmExportPackageOptions
            {
                CompressionLevel = VmExportCompressionLevel.Optimal,
                Encryption = VmExportEncryption.Aes256,
                Password = password,
                SourceHandling = VmExportSourceHandling.Keep
            }, cancellationToken: cancellationToken);
        if (!result.Success) throw new InvalidOperationException(result.Error, result.Exception);
        Console.WriteLine(result.Data!.ArchivePath);
    }
    // example:encrypted-export:end

    // example:archive-preview:start
    // title: 带密码和额度的导入预览
    // description: 从预览开始设置 ZIP 读取参数。示例额度只是调用方策略；正式导入继续使用同一批次，预览读取也计入累计额度。
    public static async Task PreviewEncryptedImportAsync(string archivePath, string password, string workDirectory,
        CancellationToken cancellationToken = default)
    {
        var prepared = await VmImportService.PreparePreviewsAsync(archivePath,
            new VmImportArchiveOptions
            {
                Password = password,
                TemporaryDirectory = workDirectory,
                MaxEntries = 10000,
                MaxEntryBytes = 1024L * 1024 * 1024 * 1024,
                MaxTotalBytes = 2L * 1024 * 1024 * 1024 * 1024
            }, VmImportPlacementMode.HostDirectories, cancellationToken: cancellationToken);
        await using var batch = prepared.Batch;
        if (!prepared.Success)
        {
            if (prepared.Result.Exception is VmImportArchiveException archiveError)
                Console.WriteLine(archiveError.Reason);
            throw new InvalidOperationException(prepared.Result.Error, prepared.Result.Exception);
        }
        foreach (var session in batch!.VirtualMachines)
            Console.WriteLine(session.Preview.Name);
        // 需要导入时，在释放 batch 之前提交 VmImportOptions 并调用 ImportAsync。
    }
    // example:archive-preview:end

    // example:list:start
    // title: 列举虚拟机
    // description: 不传 vmIds 即查询全部虚拟机。输出 GUID 和名称，GUID 可用于后续修改或连接。
    public static async Task ListVmsAsync()
    {
        await using var service = new VmQueryService();
        var response = await service.QueryAsync(new VmQueryRequest([VmIdentityFields.Name]));
        if (!response.HasData) throw new InvalidOperationException(response.Error);
        foreach (var vm in response.Data.VirtualMachines)
        {
            var name = vm.Get(VmIdentityFields.Name);
            if (name.HasValue) Console.WriteLine($"{vm.Key}  {name.Value}");
        }
    }
    // example:list:end

    // example:query:start
    // title: 读取内存和网卡
    // description: 一次请求组合不同领域的字段。虚拟机字段直接读取，网卡字段从网卡集合逐项读取。
    public static async Task<VmQueryResult> ReadSelectedFieldsAsync(Guid vmId)
    {
        await using var service = new VmQueryService();
        var request = new VmQueryRequest(
            [VmMemoryFields.Startup, VmNetworkFields.MacAddress], [vmId]);
        var response = await service.QueryAsync(request);
        if (!response.HasData) throw new InvalidOperationException(response.Error);
        foreach (var vm in response.Data.VirtualMachines)
        {
            var memory = vm.Get(VmMemoryFields.Startup);
            if (memory.HasValue) Console.WriteLine($"启动内存：{memory.Value} MiB");
            var adapters = vm.GetCollection(VmQueryCollection.NetworkAdapters);
            if (adapters.State == VmQueryValueState.Available)
                foreach (var adapter in adapters.Rows)
                {
                    var address = adapter.Get(VmNetworkFields.MacAddress);
                    if (address.HasValue) Console.WriteLine(address.Value);
                }
        }
        return response.Data;
    }
    // example:query:end

    // example:operation:start
    // title: 修改虚拟机名称
    // description: 将 GUID 和新名称交给修改接口，再检查执行结果。这里把失败转换为异常，应用也可以自行展示错误。
    public static async Task RenameAsync(Guid vmId, string newName)
    {
        var result = await VmEditService.RenameVmAsync(vmId, newName);
        if (!result.Success)
            throw new InvalidOperationException(result.Error);
    }
    // example:operation:end

    // example:processor-change:start
    // title: 修改处理器数量
    // description: 只提交 Count。模型中的其他属性不参与这次修改；调用前应按处理器接口要求关闭虚拟机。
    public static async Task SetProcessorCountAsync(Guid vmId, int count)
    {
        var result = await VmProcessorService.SetVmProcessorAsync(vmId,
            new VmProcessorSettings { Count = count }, [nameof(VmProcessorSettings.Count)]);
        if (!result.Success)
            throw new InvalidOperationException(result.Error, result.Exception);
    }
    // example:processor-change:end

    // example:create:start
    // title: 创建虚拟机
    // description: 在指定目录创建一台启动内存为 1024 MiB、暂不附带磁盘的虚拟机。返回实际名称、GUID 和创建结果，后续启动单独安排。
    public static async Task<VmCreationResult> CreateVmAsync(string configurationDirectory)
    {
        var result = await VmCreateService.CreateVirtualMachineAsync(new VmCreationParameters
        {
            Name = "Example", Path = configurationDirectory, DiskMode = 2,
            MemoryMiB = 1024 // MiB；此例稍后添加磁盘，创建不自动启动。
        });
        // Success=false 时也可能已注册或状态未知；先检查 RegistrationState 和 RetainedPaths。
        // 后续操作使用 result.VmId；NameAdjusted/Notice 报告自动避让，实际位置见 ConfigurationDirectory/DiskPath。
        return result;
    }
    // example:create:end

    // example:import:start
    // title: 导入并清理工作文件
    // description: 解析来源并检查预览，再为选中的虚拟机提交独立选项。options 必须明确保留或生成 GUID；可另选名称、目录和交换机映射。两个回调用于报告清理结果，以及保留尚未清理完成的会话。
    public static async Task<VmImportResult> ImportVmAsync(string sourceDirectory, Guid originalVmId, VmImportOptions options,
        Action<VmImportCleanupState, IReadOnlyList<string>, string?> reportCleanup,
        Action<VmImportBatchSession> retainForCleanupRetry)
    {
        var prepared = await VmImportService.PreparePreviewsAsync(
            sourceDirectory, VmImportPlacementMode.HostDirectories);
        var batch = prepared.Batch;
        try
        {
            if (!prepared.Success || batch == null)
                throw new InvalidOperationException(prepared.Result.Error);
            await using (batch)
            {
                // 实际应用展示来源预览，让用户确定 options；预览本身不接受修改。
                var selected = batch.VirtualMachines.Single(item => item.Preview.OriginalGuid == originalVmId);
                // 上层选择一个目标；多台时由上层决定执行顺序及失败后的安排。
                return await VmImportService.ImportAsync(selected, options);
            }
        }
        finally
        {
            if (batch != null)
            {
                // 预览失败时 Core 已尝试清理，也必须检查结果。
                reportCleanup(batch.CleanupState, batch.RetainedPaths, batch.CleanupError);
                // 宿主保留引用供用户重试 DisposeAsync；勿自行删除 RetainedPaths。
                if (batch.CleanupState != VmImportCleanupState.Completed)
                    retainForCleanupRetry(batch);
            }
        }
    }
    // example:import:end

    // example:export:start
    // title: 导出一台虚拟机
    // description: 导出磁盘，不导出检查点和运行状态。调用方提供一个已存在的目标根目录，并检查返回结果中的状态与保留文件。
    public static Task<VmExportResult> ExportVmAsync(Guid vmId, string destinationDirectory)
        => VmExportService.ExportAsync(vmId, destinationDirectory,
            includeVirtualHardDisks: true, excludedVirtualHardDisks: [],
            checkpointMode: VmExportCheckpointMode.None, selectedCheckpoint: null,
            includeRuntimeState: false);
    // 失败时检查 RetainedPaths；OperationStateUnknown=true 不表示后台导出已停止。
    // example:export:end

    // example:workflow:start
    // title: 推进 GPU 部署流程
    // description: 调用方提供已创建的部署会话，并通过 decide 回答本次待确认的问题。返回 null 表示取消；返回决定时使用当前请求的 Id。
    public static async Task<GpuDeploymentSnapshot> DeployGpuAsync(
        GpuDeploymentSession session,
        Func<GpuDecisionRequest, Task<GpuDeploymentDecision?>> decide)
    {
        try
        {
            var status = await session.StartAsync();
            while (status.State == GpuDeploymentState.WaitingForDecision)
            {
                var request = status.PendingDecision
                    ?? throw new InvalidOperationException("缺少待处理决定。");
                var decision = await decide(request);
                if (decision is null)
                    return await session.CancelAsync();
                status = await session.SubmitDecisionAsync(decision);
            }
            // 调用者须检查 Completed / Failed / Cancelled / CleanupRequired 及错误字段。
            // 只有 AllowedActions 包含 RetryCleanup 且 OutcomeUnknown=false 时，才能在处理原因后重试清理。
            // OutcomeUnknown 表示原生操作尚未确认结束，不能通过重试清理来猜测是否完成。
            // 允许 Reconcile 时可先只读核实；核实成功也不会继续部署。
            return status;
        }
        catch
        {
            // 收集决定或提交失败时先等待清理；清理状态仍可从 session.GetStatus() 读取。
            await session.CancelAsync();
            throw;
        }
    }
    // example:workflow:end

    // example:gpu-recovery:start
    // title: 核实和清理 GPU 部署
    // description: 使用原来的部署对象。先核实原任务；requestCleanup 表示调用方已经明确要求清理。方法返回后仍须检查状态和剩余资源，不循环重试写入。
    public static async Task<GpuDeploymentSnapshot> RecoverGpuAsync(GpuDeploymentSession session, bool requestCleanup)
    {
        var status = session.GetStatus();
        if (status.AllowedActions.Contains(GpuDeploymentAction.Reconcile))
            status = await session.ReconcileAsync();
        if (requestCleanup && !status.OutcomeUnknown && status.AllowedActions.Contains(GpuDeploymentAction.RetryCleanup))
            status = await session.RetryCleanupAsync();
        // CleanupRequired 时检查 status.Recovery.Resources 和 status.CleanupError。
        // Failed / Cancelled 表示这次部署结束；恢复不会把它变成 Completed。
        return status;
    }
    // example:gpu-recovery:end

    // example:resource:start
    // title: 接收控制台画面
    // description: 建立基本会话，把收到的画面交给 onFrame。取消等待或连接结束后，等待清理完成。onFrame 应快速处理；需要延后读取时自行 Retain。
    public static async Task<ConsoleDisconnectInfo> ObserveConsoleAsync(
        Guid vmId, Action<ConsoleFrame> onFrame, CancellationToken cancellationToken)
    {
        await using var connection = ConsoleService.CreateConnection(
            new ConsoleConnectionOptions { VmId = vmId }, ConsoleService.RdpBasicBackendId);
        var video = connection.Video ?? throw new NotSupportedException("所选连接方式不提供画面。");
        var disconnected = new TaskCompletionSource<ConsoleDisconnectInfo>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        video.FrameReceived += onFrame;
        connection.Disconnected += info => disconnected.TrySetResult(info);
        connection.Start(); // 异步启动；成功由 Connected 事件通知，错误由 Disconnected 报告。
        try
        {
            return await disconnected.Task.WaitAsync(cancellationToken);
        }
        finally
        {
            await connection.DisconnectAsync();
            video.FrameReceived -= onFrame;
        }
    }
    // example:resource:end

    // example:monitor:start
    // title: 更新处理器监控值
    // description: 只将有效的来宾处理器样本交给 update。缺测时不更新，由应用保留历史值；有效的零值仍会传出。
    public static void UpdateCpuValues(HyperVCpuMonitorService monitor, Action<Guid, int, float> update)
    {
        foreach (var sample in monitor.GetCpuUsage())
        {
            if (sample.IsHost || sample.VmId is not Guid id || !sample.Sample.IsValid) continue;
            update(id, sample.CoreId, sample.Usage);
        }
    }
    // example:monitor:end
}
