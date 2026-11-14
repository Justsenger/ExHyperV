using ExHyperV.Models;

namespace ExHyperV.Services;

/// <summary>单台虚拟机的导入工作状态；由预览入口创建，使用后必须异步释放。</summary>
public sealed class VmImportSession : IAsyncDisposable
{
    internal VmImportSession() { }
    internal VmImportLifetime Lifetime { get; } = new();
    internal bool ImportStarted { get; set; }
    internal bool PlannedSystemDestructionConfirmed { get; set; }
    internal ExHyperV.Tools.ImportArchiveContext? ArchiveContext { get; set; }

    /// <summary>用户提供的原始目录或 ZIP 路径。</summary>
    public required string SourcePath { get; init; }
    /// <summary>原始来源是目录还是 ZIP 归档。</summary>
    public required VmImportSourceKind SourceKind { get; init; }
    /// <summary>采用原目录还是新的目标目录放置导入文件。</summary>
    public required VmImportPlacementMode PlacementMode { get; init; }
    /// <summary>当前虚拟机在目录或解压工作区中的来源根目录。</summary>
    internal string SourceRoot { get; set; } = string.Empty;
    /// <summary>准备使用的主配置文件路径。</summary>
    internal string MainConfigurationPath { get; set; } = string.Empty;
    /// <summary>来源中的检查点配置目录。</summary>
    internal string SnapshotFolder { get; set; } = string.Empty;
    /// <summary>Hyper-V 计划系统的 WMI 路径；尚未准备或已释放时为空。</summary>
    internal string PlannedSystemPath { get; set; } = string.Empty;
    internal Guid PlannedGuid { get; set; }
    /// <summary>来源配置和兼容性问题的只读预览；导入选择通过 VmImportOptions 提交。</summary>
    public VmImportPreview Preview { get; internal set; } = new();
    /// <summary>本次导入使用的临时工作根目录；不需要时为 null。</summary>
    internal string? TemporaryRoot { get; set; }
    /// <summary>当前虚拟机在 ZIP 条目中的相对根前缀；非 ZIP 来源时可为 null。</summary>
    internal string? ZipRootPrefix { get; set; }
    internal string? MainConfigurationEntry { get; set; }
    internal IReadOnlyDictionary<string, long>? ArchiveEntries { get; set; }
    internal IReadOnlyDictionary<string, string>? ExtractedArchivePaths { get; set; }
    internal Dictionary<string, string> ResolvedSourceDisks { get; } = new(StringComparer.OrdinalIgnoreCase);
    internal IReadOnlyDictionary<Guid, Guid?> SwitchMappings { get; set; } = new Dictionary<Guid, Guid?>();
    internal string TargetName { get; set; } = string.Empty;
    internal string TargetConfigurationRoot { get; set; } = string.Empty;
    internal string TargetDiskRoot { get; set; } = string.Empty;
    internal string? ConfigurationStagingRoot { get; set; }
    internal string? DiskStagingRoot { get; set; }
    /// <summary>归档中本会话所需内容是否已完成完整解压。</summary>
    internal bool ArchiveFullyExtracted { get; set; }
    /// <summary>计划系统是否已成功实现为注册的虚拟机；释放会话不删除它。</summary>
    public bool IsRealized { get; internal set; }
    /// <summary>导入时已清除的不存在于当前宿主的分组引用数量。</summary>
    public int RemovedMissingGroupCount { get; internal set; }
    internal VmImportCleanupGuard CleanupGuard { get; } = new();
    internal bool OwnsTemporaryRoot { get; set; }
    internal bool GenerateNewGuid { get; set; }

    internal List<string> OwnedCreatedPaths { get; } = [];
    /// <summary>工作资源的当前清理状态；DisposeAsync 后也可读取。</summary>
    public VmImportCleanupState CleanupState { get; internal set; } = VmImportCleanupState.Pending;
    /// <summary>仍由会话持有或未确认删除的文件、目录；不包括已导入的虚拟机业务文件。</summary>
    public IReadOnlyList<string> RetainedPaths { get; internal set; } = [];
    /// <summary>清理失败或原生状态未确认时的说明；不覆盖导入失败原因。</summary>
    public string? CleanupError { get; internal set; }
    internal ExHyperV.Tools.ApiFailure? CleanupFailure { get; set; }
    /// <summary>是否仍不能确认原生操作结束或计划系统移除完成。</summary>
    public bool OperationStateUnknown => CleanupGuard.IsBlocked;

    /// <summary>释放本会话拥有的计划系统和工作文件，保留已实现的虚拟机。</summary>
    /// <remarks>请求取消后续步骤，等待正在进行的操作安全结束后再清理。已导入的虚拟机保留。原生任务未确认结束时记录 Deferred，清理失败记录 Failed；重复调用可重试清理，但不能重新开始导入。并发释放共用同一次清理结果；调用后检查 CleanupState 和 RetainedPaths。</remarks>
    public ValueTask DisposeAsync() => new(Lifetime.CleanupAsync(CleanupAndRecordAsync));

    private async Task CleanupAndRecordAsync()
    {
        try { await VmImportService.CleanupSessionAsync(this).ConfigureAwait(false); }
        catch (Exception ex) { RecordCleanupFailure(ex); }
    }

    internal void RecordCleanupFailure(Exception exception)
    {
        CleanupFailure = ExHyperV.Tools.ApiFailure.From(exception);
        RetainedPaths = Array.AsReadOnly(new[] { TemporaryRoot, ConfigurationStagingRoot, DiskStagingRoot }
            .Concat(IsRealized ? [] : RetainedPaths.Concat(OwnedCreatedPaths))
            .Where(path => !string.IsNullOrWhiteSpace(path)).Select(path => path!)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
        CleanupState = OperationStateUnknown ? VmImportCleanupState.Deferred : VmImportCleanupState.Failed;
        CleanupError = string.Join(Environment.NewLine, new[] { CleanupError, exception.Message }
            .Where(error => !string.IsNullOrWhiteSpace(error)).Distinct());
    }

}

/// <summary>共享来源和临时目录的一组导入会话；即使只查看预览也应释放。</summary>
/// <remarks>管理同一目录或归档的工作资源。从 VirtualMachines 选择会话，逐次调用 ImportAsync；顺序及失败后是否继续由调用方决定。</remarks>
public sealed class VmImportBatchSession : IAsyncDisposable
{
    internal VmImportBatchSession() { }
    internal VmImportLifetime Lifetime { get; } = new();
    internal ExHyperV.Tools.ImportArchiveContext? ArchiveContext { get; set; }
    internal VmImportBatchOperation EnterOperation(CancellationToken cancellationToken) => new(this, cancellationToken);

    /// <summary>该批次原始目录或 ZIP 的路径。</summary>
    public required string SourcePath { get; init; }
    /// <summary>批次来源类型。</summary>
    public required VmImportSourceKind SourceKind { get; init; }
    /// <summary>批次的文件放置策略。</summary>
    public required VmImportPlacementMode PlacementMode { get; init; }
    /// <summary>预览中识别到的单机导入会话，按识别顺序排列。</summary>
    public IReadOnlyList<VmImportSession> VirtualMachines { get; internal set; } = [];
    internal string? TemporaryRoot { get; set; }

    /// <summary>批次工作资源当前的清理状态。</summary>
    public VmImportCleanupState CleanupState { get; private set; } = VmImportCleanupState.Pending;
    /// <summary>尚未确认删除的工作文件、目录；释放后仍可读取。</summary>
    public IReadOnlyList<string> RetainedPaths { get; private set; } = [];
    /// <summary>批次释放时记录的清理错误；没有错误时为 null。</summary>
    public string? CleanupError { get; private set; }

    /// <summary>释放各单机会话，再清理共享解压目录；已导入的虚拟机予以保留。</summary>
    /// <remarks>先取消批次及所有子会话的后续步骤，等待在途操作安全结束，再释放资源。原生状态未确认时保留共享目录。清理失败后可重复调用重试；批次及子会话不再接受新导入。结果由 CleanupState、RetainedPaths 和 CleanupError 表达。</remarks>
    public ValueTask DisposeAsync() => new(Lifetime.CleanupAsync(CleanupAndRecordAsync,
        () => { foreach (var session in VirtualMachines) session.Lifetime.RequestStop(); }));

    private async Task CleanupAndRecordAsync()
    {
        try { await CleanupAsync().ConfigureAwait(false); }
        catch (Exception ex) { RecordCleanupFailure(ex); }
        finally { ArchiveContext?.ClearPassword(); }
    }

    internal void RecordCleanupFailure(Exception exception)
    {
        var paths = RetainedPaths.Concat(VirtualMachines.SelectMany(session => session.RetainedPaths))
            .Concat(VirtualMachines.SelectMany(session => session.IsRealized
                ? Enumerable.Empty<string>() : session.OwnedCreatedPaths)).ToList();
        if (TemporaryRoot != null) paths.Add(TemporaryRoot);
        foreach (var session in VirtualMachines)
        {
            if (session.ConfigurationStagingRoot != null) paths.Add(session.ConfigurationStagingRoot);
            if (session.DiskStagingRoot != null) paths.Add(session.DiskStagingRoot);
            if (session.TemporaryRoot != null) paths.Add(session.TemporaryRoot);
        }
        // 异常路径不再探测文件系统；未确认删除的材料全部留给调用方重试。
        RetainedPaths = Array.AsReadOnly(paths.Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
        CleanupState = VirtualMachines.Any(session => session.OperationStateUnknown
            || session.CleanupState == VmImportCleanupState.Deferred)
                ? VmImportCleanupState.Deferred : VmImportCleanupState.Failed;
        CleanupError = string.Join(Environment.NewLine, new[] { CleanupError, exception.Message }
            .Where(error => !string.IsNullOrWhiteSpace(error)).Distinct());
    }

    private async Task CleanupAsync()
    {
        foreach (VmImportSession session in VirtualMachines)
        {
            try { await session.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex)
            {
                session.CleanupState = session.OperationStateUnknown
                    ? VmImportCleanupState.Deferred : VmImportCleanupState.Failed;
                session.CleanupError = ex.Message;
                VmImportService.RefreshRetainedImportPaths(session);
            }
        }
        bool blocked = VirtualMachines.Any(session => session.OperationStateUnknown
            || session.CleanupState == VmImportCleanupState.Deferred);
        bool failed = VirtualMachines.Any(session => session.CleanupState != VmImportCleanupState.Completed);
        var paths = VirtualMachines.SelectMany(session => session.RetainedPaths).ToList();
        if (failed)
        {
            if (TemporaryRoot != null) paths.Add(TemporaryRoot);
        }
        else if (TemporaryRoot != null)
        {
            if (VmImportService.TryDeleteTemporaryRoot(TemporaryRoot)) TemporaryRoot = null;
            else paths.Add(TemporaryRoot);
        }
        foreach (var session in VirtualMachines) VmImportService.RefreshRetainedImportPaths(session);
        RetainedPaths = Array.AsReadOnly(paths.Where(path => !VmImportService.IsImportPathMissing(path))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
        CleanupState = blocked ? VmImportCleanupState.Deferred
            : failed || RetainedPaths.Count > 0 ? VmImportCleanupState.Failed : VmImportCleanupState.Completed;
        var errors = VirtualMachines.Select(session => session.CleanupError)
            .Where(error => !string.IsNullOrWhiteSpace(error)).Distinct().ToList();
        if (blocked) errors.Add("原生操作或计划系统清理结果未确认，相关工作资源已保留。");
        else if (failed || RetainedPaths.Count > 0) errors.Add("部分导入工作资源未能删除。");
        CleanupError = errors.Count > 0 ? string.Join(Environment.NewLine, errors) : null;
    }

}
