using ExHyperV.Models;
using ExHyperV.Services;

namespace ExHyperV.Documentation;

// 这些示例都会参与编译检查，确保文档里的代码与当前 Core API 一致。
public static class UsageExamples
{
    // example:settings-snapshot:start
    // title: 先读后改：调整启动内存
    // description: 先读取当前内存配置，确认成功取到值之后，只把改过的 Startup 字段提交回去，避免误改其他配置。
    public static async Task ChangeStartupMemoryAsync(Guid vmId, long startupMiB)
    {
        var response = await VmMemoryService.GetVmMemorySettingsAsync(vmId);
        if (!response.HasData) throw new InvalidOperationException(response.Error);

        var snapshot = response.Data!;
        if (snapshot.States[nameof(VmMemorySettings.Startup)] != VmQueryValueState.Available)
            throw new InvalidOperationException("当前未能读取到启动内存配置。");

        var draft = snapshot.Values;
        draft.Startup = startupMiB;

        // 第二个参数只传你想改的字段名，其余字段会自动保持原样。
        var result = await VmMemoryService.SetVmMemorySettingsAsync(
            vmId, draft, [nameof(VmMemorySettings.Startup)]);
        if (!result.Success) throw new InvalidOperationException(result.Error);
    }
    // example:settings-snapshot:end

    // example:encrypted-export:start
    // title: 把导出目录打包成加密 ZIP
    // description: 先用 VmExportService 导出虚拟机目录，再调用打包接口压缩并设置 AES-256 密码。跨工具解压时建议使用纯 ASCII 密码。
    public static async Task PackageExportAsync(string exportDirectory, string archivePath, string password,
        CancellationToken cancellationToken = default)
    {
        var result = await VmExportPackagingService.CreatePackageAsync(exportDirectory, archivePath,
            new VmExportPackageOptions
            {
                CompressionLevel = VmExportCompressionLevel.Optimal,
                Encryption = VmExportEncryption.Aes256,
                Password = password,
                SourceHandling = VmExportSourceHandling.Keep // 打包完成后保留原导出目录
            }, cancellationToken: cancellationToken);
        if (!result.Success) throw new InvalidOperationException(result.Error, result.Exception);
        Console.WriteLine(result.Data!.ArchivePath);
    }
    // example:encrypted-export:end

    // example:archive-preview:start
    // title: 从加密压缩包预览可导入的虚拟机
    // description: 如果导入来源是带密码的 ZIP，在预览阶段就要传入密码、临时目录和解压上限。预览和正式导入共用同一个 batch 会话。
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
                Console.WriteLine($"压缩包读取失败：{archiveError.Reason}");
            throw new InvalidOperationException(prepared.Result.Error, prepared.Result.Exception);
        }

        foreach (var session in batch!.VirtualMachines)
            Console.WriteLine(session.Preview.Name);
        // 确认要导入哪台后，在释放 batch 之前直接调用 VmImportService.ImportAsync(session, options)。
    }
    // example:archive-preview:end

    // example:list:start
    // title: 列出所有虚拟机
    // description: 最短的入门示例：不传 vmIds 就会查出本机全部虚拟机，拿到 GUID（vm.Key）后就能用来开关机或改配置。
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
    // title: 按需查询多个领域的字段
    // description: 可以把内存、网卡等不同领域的字段混在同一次请求里。虚拟机自身属性直接读，网卡/磁盘这类一对多设备从集合里遍历读取。
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
            {
                foreach (var adapter in adapters.Rows)
                {
                    var address = adapter.Get(VmNetworkFields.MacAddress);
                    if (address.HasValue) Console.WriteLine(address.Value);
                }
            }
        }
        return response.Data;
    }
    // example:query:end

    // example:operation:start
    // title: 重命名虚拟机
    // description: 把虚拟机 GUID 和新名字交给服务即可。通过 result.Success 判断是否成功，失败时可以直接向用户展示 result.Error。
    public static async Task RenameAsync(Guid vmId, string newName)
    {
        var result = await VmEditService.RenameVmAsync(vmId, newName);
        if (!result.Success)
            throw new InvalidOperationException(result.Error);
    }
    // example:operation:end

    // example:processor-change:start
    // title: 直接修改虚拟处理器核心数
    // description: 只提交 Count 字段，其余属性不会被覆盖。修改处理器数量前需要先把虚拟机关机。
    public static async Task SetProcessorCountAsync(Guid vmId, int count)
    {
        var result = await VmProcessorService.SetVmProcessorAsync(vmId,
            new VmProcessorSettings { Count = count }, [nameof(VmProcessorSettings.Count)]);
        if (!result.Success)
            throw new InvalidOperationException(result.Error, result.Exception);
    }
    // example:processor-change:end

    // example:create:start
    // title: 创建一台虚拟机
    // description: 在指定目录创建一台 1024 MiB 内存、暂不挂载虚拟磁盘的虚拟机。创建后不会自动开机，需要单独调用电源控制。
    public static async Task<VmCreationResult> CreateVmAsync(string configurationDirectory)
    {
        var result = await VmCreateService.CreateVirtualMachineAsync(new VmCreationParameters
        {
            Name = "Example",
            Path = configurationDirectory,
            DiskMode = 2, // 暂不创建磁盘，稍后按需挂载
            MemoryMiB = 1024
        });
        // 如果名称或目录撞车，result.NameAdjusted / result.Notice 会告诉你实际采用的命名和位置。
        // 如果失败，可以通过 RegistrationState 和 RetainedPaths 确认是否留下半成品需要清理。
        return result;
    }
    // example:create:end

    // example:import:start
    // title: 预览并导入虚拟机（含临时文件清理检查）
    // description: 先解析导入目录拿到预览信息，再选中要导入的虚拟机提交 VmImportOptions。即使中途失败，也要把未清理完的会话状态通知上层。
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
                // 通常先在界面把 batch.VirtualMachines 展示给用户，选定后构造 options 再导入。
                var selected = batch.VirtualMachines.Single(item => item.Preview.OriginalGuid == originalVmId);
                return await VmImportService.ImportAsync(selected, options);
            }
        }
        finally
        {
            if (batch != null)
            {
                // 如果解压临时目录没能一次删干净，保留 batch 引用以便用户稍后重试 DisposeAsync，别直接硬删 RetainedPaths。
                reportCleanup(batch.CleanupState, batch.RetainedPaths, batch.CleanupError);
                if (batch.CleanupState != VmImportCleanupState.Completed)
                    retainForCleanupRetry(batch);
            }
        }
    }
    // example:import:end

    // example:export:start
    // title: 导出一台虚拟机
    // description: 把虚拟机配置和虚拟磁盘导出到指定目录（跳过检查点和运行状态）。
    public static Task<VmExportResult> ExportVmAsync(Guid vmId, string destinationDirectory)
        => VmExportService.ExportAsync(vmId, destinationDirectory,
            includeVirtualHardDisks: true,
            excludedVirtualHardDisks: [],
            checkpointMode: VmExportCheckpointMode.None,
            selectedCheckpoint: null,
            includeRuntimeState: false);
    // example:export:end

    // example:workflow:start
    // title: 驱动 GPU 分区与驱动部署流程
    // description: GPU 部署会在需要用户拍板时暂停（例如选分区或确认关机）。每次循环读取 PendingDecision，将有效决定交给 SubmitDecisionAsync。示例中的 decide 回调返回 null 时，由调用方调用 CancelAsync。
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
                    ?? throw new InvalidOperationException("部署流程处于等待状态，但缺少待确认事项。");
                var decision = await decide(request);
                if (decision is null)
                    return await session.CancelAsync();
                status = await session.SubmitDecisionAsync(decision);
            }
            // 循环结束后，根据 status.State（Completed / Failed / Cancelled / CleanupRequired）展示最终结果。
            return status;
        }
        catch
        {
            await session.CancelAsync();
            throw;
        }
    }
    // example:workflow:end

    // example:gpu-recovery:start
    // title: 处理中断或未清理完的 GPU 部署
    // description: 如果上次部署卡在结果未知或需要清理，直接对原 session 实例先核实状态（ReconcileAsync），确认安全后再触发清理（RetryCleanupAsync）。
    public static async Task<GpuDeploymentSnapshot> RecoverGpuAsync(GpuDeploymentSession session, bool requestCleanup)
    {
        var status = session.GetStatus();

        // 底层任务结果还没确认时，先只读核实，别急着清理。
        if (status.AllowedActions.Contains(GpuDeploymentAction.Reconcile))
            status = await session.ReconcileAsync();

        // 核实完毕、确认安全且允许重试清理时，再把临时挂载的磁盘和多余适配器清掉。
        if (requestCleanup && !status.OutcomeUnknown && status.AllowedActions.Contains(GpuDeploymentAction.RetryCleanup))
            status = await session.RetryCleanupAsync();

        return status;
    }
    // example:gpu-recovery:end

    // example:resource:start
    // title: 连接虚拟机桌面并接收画面帧
    // description: 建立基本会话连接，通过 FrameReceived 回调持续接收 BGRA32 画面。结束或取消时调用 DisconnectAsync 释放连接。
    public static async Task<ConsoleDisconnectInfo> ObserveConsoleAsync(
        Guid vmId, Action<ConsoleFrame> onFrame, CancellationToken cancellationToken)
    {
        await using var connection = ConsoleService.CreateConnection(
            new ConsoleConnectionOptions { VmId = vmId }, ConsoleService.RdpBasicBackendId);
        var video = connection.Video ?? throw new NotSupportedException("当前连接方式不提供画面输出。");
        var disconnected = new TaskCompletionSource<ConsoleDisconnectInfo>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        // 一定要先订阅事件，再调用 Start()。
        video.FrameReceived += onFrame;
        connection.Disconnected += info => disconnected.TrySetResult(info);
        connection.Start();

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
    // title: 持续采集虚拟机的 CPU 使用率
    // description: 过滤掉宿主机采样和还没建立好基线的无效采样。遇到无效采样时跳过即可，界面上继续保留上一帧数值。
    public static void UpdateCpuValues(HyperVCpuMonitorService monitor, Action<Guid, int, float> update)
    {
        foreach (var sample in monitor.GetCpuUsage())
        {
            if (sample.IsHost || sample.VmId is not Guid id || !sample.Sample.IsValid) continue;
            update(id, sample.CoreId, sample.Usage);
        }
    }
    // example:monitor:end

    // example:host-thread-monitor:start
    // title: 按宿主逻辑线程拆分宿主机与虚拟机占用
    // description: 持续每秒采样一次每个物理逻辑处理器的总占用、宿主机占比及各虚拟机时间片占比。若处于性能计数器回退或 Classic/Core 模式，先检查 Sample.IsValid 并留意 UnattributedGuestUsagePercent。
    public static async Task SampleHostThreadsAsync(CancellationToken cancellationToken)
    {
        await using var monitor = new HostCpuThreadMonitorService();
        monitor.GetThreadUsage();
        await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);

        var report = monitor.GetThreadUsage();
        if (!report.Sample.IsValid) return;

        foreach (var lp in report.Threads)
        {
            if (!lp.Sample.IsValid) continue;
            string host = lp.HostUsagePercent is float percent ? $"{percent:F1}%" : "未知";
            Console.WriteLine($"线程 {lp.LogicalProcessorIndex} 总占用 {lp.TotalUsagePercent:F1}%，宿主 {host}，未归属 {lp.UnattributedUsagePercent:F1}%");
        }
    }
    // example:host-thread-monitor:end
}
