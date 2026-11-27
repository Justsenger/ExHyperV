using ExHyperV.Models;
using ExHyperV.Tools;
using R = ExHyperV.Properties.Resources;

namespace ExHyperV.Services;

/// <summary>管理一次进程内 GPU 部署；Core 推进各步骤，并在需要确认时暂停，等待带版本标识的明确决定。</summary>
/// <remarks>每个实例管理一次 GPU 部署，写操作依次执行，GetStatus 返回当前状态。失败或取消后恢复临时磁盘资源，清理已确认属于本次操作的 GPU 适配器，已完成的策略、MMIO、文件及电源变化保留。结果或归属待确认时保留现场，按 AllowedActions 调用 ReconcileAsync 核实、RetryCleanupAsync 清理。恢复材料由当前进程中的会话实例持有。</remarks>
public sealed partial class GpuDeploymentSession
{
    private readonly IGpuDeploymentBackend backend;
    private readonly GpuDeploymentMode mode;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly CancellationTokenSource cancellation = new();
    private readonly Guid sessionId = Guid.NewGuid();
    private readonly List<GpuDeploymentStep> steps;
    private readonly Dictionary<string, DiskPartitionInfo> partitions = new();
    private readonly List<string> addedAdapterIds = new();
    private readonly List<string> cleanupAdapterIds = new();
    private Action<GpuDeploymentSnapshot>? report;
    private GpuDeploymentSnapshot snapshot = null!;
    private GpuDeploymentState state = GpuDeploymentState.Created;
    private GpuDeploymentState cleanupDestination;
    private GpuDecisionRequest? pending;
    private DiskPartitionInfo? selected;
    private int index;
    private long revision;
    private enum AfterPowerOff { Advance, Scan, Windows }
    private AfterPowerOff afterPowerOff;
    private bool needConfig, unidentifiedAssignment;
    private string? error, cleanupError;
    private string? retainedDiskCleanupError;
    private bool isDriverSkipped;
    private bool outcomeUnknown;
    private bool assignmentNeedsReconciliation;
    private int? stepProgressPercent;
    private string? guestSetupCommand, linuxIsoPath, linuxDriverVersion, bundleError;

    /// <summary>创建尚未开始执行的单张 GPU 部署会话。</summary>
    /// <param name="vmId">目标虚拟机 GUID；后续会按身份核对目标。</param>
    /// <param name="vmName">目标虚拟机名称，应与 vmId 对应。</param>
    /// <param name="gpu">目标宿主 GPU 及可分区资源信息。</param>
    /// <param name="gpuService">执行 GPU 查询与配置的服务。</param>
    /// <param name="queryService">读取目标虚拟机信息的服务；由调用方持有和释放，须在部署及后续核实、清理期间保持可用。</param>
    /// <param name="mode">本次部署范围，默认新增 GPU 并安装驱动；未定义的枚举值会被拒绝。</param>
    /// <exception cref="ArgumentNullException">gpu 为 null。</exception>
    /// <exception cref="ArgumentOutOfRangeException">mode 不是定义的部署模式。</exception>
    public GpuDeploymentSession(Guid vmId, string vmName, HostGpuInfo gpu, VmGpuService gpuService,
        VmQueryService queryService, GpuDeploymentMode mode = GpuDeploymentMode.AddWithDrivers)
        : this(vmId, vmName, [gpu ?? throw new ArgumentNullException(nameof(gpu))], gpuService, queryService, mode) { }

    /// <summary>创建尚未开始执行的多卡或单卡批量 GPU 部署会话。</summary>
    /// <param name="vmId">目标虚拟机 GUID；后续会按身份核对目标。</param>
    /// <param name="vmName">目标虚拟机名称，应与 vmId 对应。</param>
    /// <param name="gpus">目标宿主 GPU 列表，至少包含一张有效显卡。</param>
    /// <param name="gpuService">执行 GPU 查询与配置的服务。</param>
    /// <param name="queryService">读取目标虚拟机信息的服务；由调用方持有和释放，须在部署及后续核实、清理期间保持可用。</param>
    /// <param name="mode">本次部署范围，默认新增 GPU 并安装驱动；未定义的枚举值会被拒绝。</param>
    /// <exception cref="ArgumentNullException">gpus 为 null。</exception>
    /// <exception cref="ArgumentException">gpus 为空或包含 null 项。</exception>
    /// <exception cref="ArgumentOutOfRangeException">mode 不是定义的部署模式。</exception>
    public GpuDeploymentSession(Guid vmId, string vmName, IReadOnlyList<HostGpuInfo> gpus, VmGpuService gpuService,
        VmQueryService queryService, GpuDeploymentMode mode = GpuDeploymentMode.AddWithDrivers)
        : this(new NativeGpuDeploymentBackend(vmId, ValidateGpuList(gpus), gpuService), mode)
    {
        ArgumentNullException.ThrowIfNull(queryService);
    }

    internal GpuDeploymentSession(IGpuDeploymentBackend backend, GpuDeploymentMode mode = GpuDeploymentMode.AddWithDrivers)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        this.backend = backend;
        this.mode = mode;
        steps = BuildSteps(mode);
        Publish();
    }

    private static IReadOnlyList<HostGpuInfo> ValidateGpuList(IReadOnlyList<HostGpuInfo> gpus)
    {
        ArgumentNullException.ThrowIfNull(gpus);
        if (gpus.Count == 0 || gpus.Any(g => g == null))
            throw new ArgumentException("请至少选择一张有效的宿主显卡。", nameof(gpus));
        return gpus.ToArray();
    }

    // Polling never waits for a native operation. Returned values cannot mutate this session.
    /// <summary>读取最近发布的状态，不等待正在进行的底层操作。</summary>
    /// <returns>当前部署状态。</returns>
    public GpuDeploymentSnapshot GetStatus() => Volatile.Read(ref snapshot);
    /// <summary>仅发送协作式取消请求，立即返回；正在执行的不可取消底层调用仍需自行结束。</summary>
    public void RequestCancellation() => cancellation.Cancel();

    /// <summary>开始部署并执行到等待用户决定或结束；同一会话只能从 Created 状态启动。</summary>
    /// <param name="progress">可选的状态通知，在执行流程所在的上下文同步触发。回调应快速返回，界面更新需调度到界面线程。阻塞等待会话写操作会导致死锁，回调异常会被隔离。</param>
    /// <returns>停止推进时的最新状态，可能仍需用户决定。</returns>
    /// <exception cref="InvalidOperationException">会话已经离开 Created 状态。</exception>
    /// <editentry label="GPU 驱动部署流程" category="GPU"/>
    public async Task<GpuDeploymentSnapshot> StartAsync(Action<GpuDeploymentSnapshot>? progress = null)
    {
        await gate.WaitAsync();
        try
        {
            if (state != GpuDeploymentState.Created) throw new InvalidOperationException(R.Gpu_DecisionExpired);
            report = progress;
            return await ExecuteAsync(AdvanceAsync);
        }
        finally { gate.Release(); }
    }

    /// <summary>校验当前请求 ID 及输入类型，然后推进部署。</summary>
    /// <param name="decision">与 PendingDecision 对应的确认、分区、回退动作或返回决定；不能为空。</param>
    /// <returns>执行至下一次等待或结束后的状态。</returns>
    /// <remarks>选择非加密 Windows 分区后会直接执行离线驱动写入，确认 PowerOff 会直接 TurnOff，提交 SkipDriver 会跳过驱动安装。</remarks>
    /// <exception cref="ArgumentNullException">decision 为 null。</exception>
    /// <exception cref="InvalidOperationException">没有待确认事项，或请求 ID、输入种类与当前事项不匹配。</exception>
    /// <exception cref="ArgumentException">分区不在当前列表、分区不受支持（如 Linux）或已开启 BitLocker。</exception>
    public async Task<GpuDeploymentSnapshot> SubmitDecisionAsync(GpuDeploymentDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        await gate.WaitAsync();
        try
        {
            ValidateDecision(decision); // A bad/stale submission must not abort the legitimate pending operation.
            return await ExecuteAsync(async () =>
            {
                var kind = pending!.Kind;
                pending = null;
                switch (decision)
                {
                    case GpuConfirmation { Accepted: false }:
                        RequestCancellation();
                        cancellation.Token.ThrowIfCancellationRequested();
                        break;
                    case GpuConfirmation:
                        await VerifyTargetAsync();
                        if (kind == GpuDecisionKind.PowerOff)
                        {
                            await PowerOffAsync();
                            if (afterPowerOff == AfterPowerOff.Windows) await ExecuteWindowsDriverSyncAsync();
                            else if (afterPowerOff == AfterPowerOff.Scan) await ScanPartitionsAsync();
                            else { Succeed(R.Msg_Gpu_Off); index++; await AdvanceAsync(); }
                        }
                        break;
                    case GpuPartitionSelection choice:
                        selected = ClonePartition(partitions[choice.PartitionId]);
                        if (selected.OsType == OperatingSystemType.Linux)
                            EnterLinuxBundleSelection();
                        else
                        {
                            SetDriverStepAsWindowsInstall();
                            await PrepareSelectedPartitionAsync();
                        }
                        break;
                    case GpuLinuxBundleSelection bundleChoice:
                        await ExecuteLinuxBundleAsync(bundleChoice);
                        break;
                    case GpuDriverFallbackSelection fallback:
                        await VerifyTargetAsync();
                        if (fallback.Action != GpuDriverFallbackAction.SkipDriver)
                            throw new ArgumentException(R.Gpu_DecisionExpired);
                        isDriverSkipped = true;
                        selected = null;
                        CompleteDrivers(R.Msg_Gpu_DriverSkipped);
                        break;
                    case GpuBackToPartitions:
                        HandleBackToPartitions();
                        break;
                }
            });
        }
        finally { gate.Release(); }
    }

    /// <summary>请求取消，等待当前操作退出后尝试清理本次新增的全部适配器。</summary>
    /// <returns>取消或既有终态，清理未完成时返回 CleanupRequired。</returns>
    /// <remarks>等待当前步骤结束后取消，已完成的修改保留。会话已终止或等待清理时返回当前状态。OutcomeUnknown 时先按 AllowedActions 调用 ReconcileAsync 核实，再决定是否调用 RetryCleanupAsync。</remarks>
    public async Task<GpuDeploymentSnapshot> CancelAsync()
    {
        // Finish active native calls first, including identifying an adapter just added.
        RequestCancellation();
        await gate.WaitAsync();
        try
        {
            if (state is GpuDeploymentState.Completed or GpuDeploymentState.Cancelled or GpuDeploymentState.Failed
                or GpuDeploymentState.CleanupRequired) return GetStatus();
            await EndAsync(GpuDeploymentState.Cancelled);
            return GetStatus();
        }
        finally { gate.Release(); }
    }

    /// <summary>在允许 RetryCleanup 且 OutcomeUnknown 为 false 时，恢复临时磁盘资源并清理本次新增的 GPU 适配器。</summary>
    /// <returns>清理完成后返回原定的 Failed 或 Cancelled；仍需处理的资源保留在 Recovery.Resources，状态为 CleanupRequired。</returns>
    /// <remarks>先核实原任务，再在资源锁内恢复本次临时修改。借用的挂载保留；磁盘、分区或控制器身份变化时停止。磁盘恢复要求 VM 已关闭，原联机和只读状态会覆盖期间的外部修改。恢复失败保留记录；结果未知时先调用 ReconcileAsync。</remarks>
    /// <exception cref="InvalidOperationException">会话不在 CleanupRequired 状态，或 OutcomeUnknown 为 true。</exception>
    public async Task<GpuDeploymentSnapshot> RetryCleanupAsync()
    {
        await gate.WaitAsync();
        try
        {
            if (state != GpuDeploymentState.CleanupRequired || outcomeUnknown) throw new InvalidOperationException(R.Gpu_DecisionExpired);
            if (backend.HasRecovery)
            {
                var recovered = await backend.RetryRecoveryAsync();
                if (!recovered.Success)
                {
                    outcomeUnknown = recovered.ErrorKind == ApiErrorKind.OutcomeUnknown;
                    cleanupError = recovered.Error;
                    Publish();
                    return GetStatus();
                }
                retainedDiskCleanupError = null;
            }
            await EndAsync(cleanupDestination);
            return GetStatus();
        }
        finally { gate.Release(); }
    }

    /// <summary>只读核实本次部署和清理的原生任务、GPU 适配器及临时磁盘资源。</summary>
    /// <returns>更新后的状态。原任务结束且资源已恢复时进入原定的 Failed 或 Cancelled，仍需清理时停在 CleanupRequired，并更新 AllowedActions。</returns>
    /// <remarks>核实已记录的适配器、电源、MMIO 和临时磁盘任务。仍在运行、读取失败或缺少最终回执时保留 OutcomeUnknown。Recovery 中的 Operations、Resources、ObservedValues 分别提供任务结果、恢复资源和本次观测值；后续清理由调用方按 AllowedActions 发起。</remarks>
    /// <exception cref="InvalidOperationException">当前 AllowedActions 不包含 Reconcile。</exception>
    public async Task<GpuDeploymentSnapshot> ReconcileAsync()
    {
        await gate.WaitAsync();
        try
        {
            if (state != GpuDeploymentState.CleanupRequired || !(assignmentNeedsReconciliation || backend.HasRecovery))
                throw new InvalidOperationException("当前没有可核实的部署操作。");
            try
            {
                if (assignmentNeedsReconciliation)
                {
                    var result = await backend.ReconcileAssignmentAsync();
                    if (result.OwnershipUncertain || result.Response.ErrorKind == ApiErrorKind.OutcomeUnknown
                        || string.IsNullOrWhiteSpace(result.CreatedAdapterId))
                    {
                        cleanupError = result.Response.Error;
                        Publish();
                        return GetStatus();
                    }
                    TrackAddedAdapter(result.CreatedAdapterId);
                    unidentifiedAssignment = assignmentNeedsReconciliation = false;
                }
                var observed = await backend.ReconcileRecoveryAsync();
                outcomeUnknown = !observed.Success;
                if (!observed.Success) cleanupError = observed.Error;
                else
                {
                    retainedDiskCleanupError = backend.HasPendingRecovery ? "临时磁盘资源尚未恢复，请调用 RetryCleanupAsync。" : null;
                    cleanupError = retainedDiskCleanupError;
                    // A completed removal may already have removed owned adapters.
                    // This observation never issues a new removal or resumes deployment.
                    if (cleanupAdapterIds.Count > 0)
                    {
                        await backend.VerifyTargetAsync();
                        var adapters = await backend.GetAdaptersAsync();
                        if (!adapters.Success) throw new InvalidOperationException(R.Gpu_CleanupRequired);
                        cleanupAdapterIds.RemoveAll(id => !adapters.Adapters.Any(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase)));
                    }
                    if (cleanupAdapterIds.Count == 0 && !unidentifiedAssignment && !backend.HasPendingRecovery)
                        state = cleanupDestination;
                }
            }
            catch (Exception ex) { cleanupError = ex.Message; }
            Publish();
            return GetStatus();
        }
        finally { gate.Release(); }
    }

    private async Task<GpuDeploymentSnapshot> ExecuteAsync(Func<Task> operation)
    {
        try
        {
            cancellation.Token.ThrowIfCancellationRequested();
            state = GpuDeploymentState.Running;
            Publish();
            await operation();
            cancellation.Token.ThrowIfCancellationRequested();
            if (state == GpuDeploymentState.Running && index == steps.Count)
            {
                cleanupAdapterIds.Clear();
                unidentifiedAssignment = false;
                state = GpuDeploymentState.Completed;
                Publish();
            }
        }
        catch (Exception exception)
        {
            outcomeUnknown |= exception is CoreOperationException { Result.ErrorKind: ApiErrorKind.OutcomeUnknown };
            if (outcomeUnknown) retainedDiskCleanupError = exception.Message;
            if (exception is GpuDeploymentCleanupException) retainedDiskCleanupError = exception.Message;
            error = cancellation.IsCancellationRequested && !outcomeUnknown ? null : exception.Message;
            SetStep(GpuDeploymentStepStatus.Failed, error ?? R.Btn_Cancel, error);
            await EndAsync(cancellation.IsCancellationRequested ? GpuDeploymentState.Cancelled : GpuDeploymentState.Failed);
        }
        return GetStatus();
    }

    private async Task AdvanceAsync()
    {
        while (index < steps.Count)
        {
            cancellation.Token.ThrowIfCancellationRequested();
            await VerifyTargetAsync();
            SetStep(GpuDeploymentStepStatus.Running, null, string.Format(R.Msg_Gpu_ExecTask, steps[index].Name));
            cancellation.Token.ThrowIfCancellationRequested();
            string description;
            switch (steps[index].Type)
            {
                case GpuDeploymentStepKind.PowerCheck:
                    if (mode != GpuDeploymentMode.AddWithoutDrivers && !(await ReadPowerAsync()).IsOff)
                    {
                        AskPowerOff(AfterPowerOff.Advance);
                        return;
                    }
                    description = mode == GpuDeploymentMode.AddWithoutDrivers ? R.Msg_Skip : R.Msg_Gpu_Off;
                    break;
                case GpuDeploymentStepKind.Optimization:
                    await backend.PrepareHostAsync();
                    needConfig = !await backend.CheckConfigurationAsync();
                    description = needConfig && (await ReadPowerAsync()).IsOff
                        ? (await backend.OptimizeAsync() ? R.Msg_Gpu_MmioOk : R.Error_Gpu_OptFail)
                        : (needConfig ? R.Msg_Skip : R.Msg_Gpu_ConfigOk);
                    break;
                case GpuDeploymentStepKind.Assign:
                    await AssignAsync();
                    description = R.Msg_Gpu_AssignOk;
                    break;
                case GpuDeploymentStepKind.Driver:
                    await ScanPartitionsAsync();
                    return;
                default: throw new InvalidOperationException();
            }
            Succeed(description);
            index++;
            cancellation.Token.ThrowIfCancellationRequested();
        }
    }

    private async Task AssignAsync()
    {
        int total = backend.GpuCount;
        for (int i = 0; i < total; i++)
        {
            cancellation.Token.ThrowIfCancellationRequested();
            // 收到创建回执前，异常仍可能发生在底层已经写入之后。
            unidentifiedAssignment = true;
            assignmentNeedsReconciliation = true;
            var assigned = await backend.AssignAtAsync(i);
            string? createdId = string.IsNullOrWhiteSpace(assigned.CreatedAdapterId) ? null : assigned.CreatedAdapterId;
            if (createdId != null) TrackAddedAdapter(createdId);
            unidentifiedAssignment = assigned.OwnershipUncertain;
            assignmentNeedsReconciliation = assigned.OwnershipUncertain || assigned.Response.ErrorKind == ApiErrorKind.OutcomeUnknown;
            OperationOutcomeUnknownException.ThrowIfUnknown(assigned.Response);
            if (!assigned.Response.Success) throw new InvalidOperationException(assigned.Response.Error, assigned.Response.Exception);
            if (unidentifiedAssignment) throw new InvalidOperationException(R.Warn_Gpu_RollbackAdapterNotIdentified);
            if (createdId == null)
            {
                unidentifiedAssignment = true;
                throw new InvalidOperationException(R.Warn_Gpu_RollbackAdapterNotIdentified);
            }
        }
    }

    private void TrackAddedAdapter(string adapterId)
    {
        if (!addedAdapterIds.Contains(adapterId, StringComparer.OrdinalIgnoreCase))
            addedAdapterIds.Add(adapterId);
        if (!cleanupAdapterIds.Contains(adapterId, StringComparer.OrdinalIgnoreCase))
            cleanupAdapterIds.Add(adapterId);
    }

    private async Task PowerOffAsync()
    {
        bool off = (await ReadPowerAsync()).IsOff;
        cancellation.Token.ThrowIfCancellationRequested();
        if (!off) await backend.PowerAsync("TurnOff");
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!(await ReadPowerAsync()).IsOff)
        {
            if (DateTime.UtcNow > deadline) throw new InvalidOperationException(R.Error_Gpu_PowerOffTimeout);
            await Task.Delay(100, cancellation.Token);
        }
    }

    private void AskPowerOff(AfterPowerOff nextAction)
    {
        afterPowerOff = nextAction;
        Ask(GpuDecisionKind.PowerOff, R.Task_Gpu_Power, R.Gpu_ConfirmPowerOff);
    }

    private async Task ScanPartitionsAsync()
    {
        await VerifyTargetAsync();
        if (!(await ReadPowerAsync()).IsOff)
        {
            AskPowerOff(AfterPowerOff.Scan);
            return;
        }
        cancellation.Token.ThrowIfCancellationRequested();
        DriverProgress(R.Msg_Gpu_Scanning);
        cancellation.Token.ThrowIfCancellationRequested();
        var found = await backend.GetPartitionsAsync();
        cancellation.Token.ThrowIfCancellationRequested();
        if (found.Count == 0) throw new InvalidOperationException(R.Error_Gpu_NoPartFound);
        partitions.Clear();
        foreach (var partition in found) partitions.Add(Guid.NewGuid().ToString("N"), ClonePartition(partition));
        selected = null;

        var usable = found.Where(IsSupported).ToList();
        bool hasBitLocker = found.Any(p => p.IsBitLocker);
        bool hasLinux = found.Any(p => p.OsType == OperatingSystemType.Linux);
        if (usable.Count == 1 && !hasBitLocker)
        {
            selected = ClonePartition(usable[0]);
            if (selected.OsType == OperatingSystemType.Linux)
                EnterLinuxBundleSelection();
            else
            {
                SetDriverStepAsWindowsInstall();
                await PrepareSelectedPartitionAsync();
            }
        }
        else Ask(GpuDecisionKind.SelectPartition, R.Label_SelectPart, BuildPartitionSelectMessage(usable.Count, hasLinux, hasBitLocker));
    }

    private static string BuildPartitionSelectMessage(int usableCount, bool hasLinux, bool hasBitLocker) => (usableCount, hasLinux, hasBitLocker) switch
    {
        (0, true, true) => R.Msg_Gpu_LinuxAndBitLockerUnsupported,
        (0, true, false) => R.Msg_Gpu_LinuxUnsupported,
        (0, false, true) => R.Msg_Gpu_BitLockerRequired,
        (> 0, true, true) => R.Msg_Gpu_SelectPartWithLinuxAndBitLocker,
        (> 0, true, false) => R.Msg_Gpu_SelectPartWithLinux,
        (> 0, false, true) => R.Msg_Gpu_SelectPartWithBitLocker,
        (0, false, false) => R.Msg_Gpu_NoUsableWindowsPart,
        _ => R.Msg_Gpu_ManualSelect
    };

    private async Task PrepareSelectedPartitionAsync()
    {
        SetDriverStepAsWindowsInstall();
        await VerifyTargetAsync();
        if (!(await ReadPowerAsync()).IsOff) AskPowerOff(AfterPowerOff.Windows);
        else await ExecuteWindowsDriverSyncAsync();
    }

    private async Task ExecuteWindowsDriverSyncAsync()
    {
        if (!(await ReadPowerAsync()).IsOff)
        {
            AskPowerOff(AfterPowerOff.Windows);
            return;
        }
        await VerifyPartitionAsync();
        cancellation.Token.ThrowIfCancellationRequested();
        var synced = await backend.SyncWindowsAsync(selected!, DriverProgress);
        if (!synced.Success)
        {
            if (IsPartitionFallbackReason(synced.Message))
            {
                selected = null;
                Ask(GpuDecisionKind.DriverFallback, R.Task_Gpu_Driver, synced.Message!, canGoBack: partitions.Count > 1);
                return;
            }
            throw new InvalidOperationException(synced.Message);
        }
        await backend.RecordGuestDriverVersionsAsync();
        CompleteDrivers(R.Msg_Gpu_DriverOk);
    }

    private async Task VerifyPartitionAsync()
    {
        cancellation.Token.ThrowIfCancellationRequested();
        var found = await backend.GetPartitionsAsync();
        if (selected == null || !found.Any(p => p.PartitionNumber == selected.PartitionNumber
            && string.Equals(p.DiskPath, selected.DiskPath, StringComparison.OrdinalIgnoreCase)
            && p.SizeInBytes == selected.SizeInBytes && p.OsType == selected.OsType
            && p.IsPhysicalDisk == selected.IsPhysicalDisk && p.IsBitLocker == selected.IsBitLocker))
            throw new InvalidOperationException(R.Gpu_InvalidPartition);
    }

    private async Task<(bool IsOff, string State)> ReadPowerAsync()
    {
        var power = await backend.IsPoweredOffAsync();
        cancellation.Token.ThrowIfCancellationRequested();
        return power;
    }

    private async Task VerifyTargetAsync()
    {
        await backend.VerifyTargetAsync();
        cancellation.Token.ThrowIfCancellationRequested();
    }

    private void ValidateDecision(GpuDeploymentDecision decision)
    {
        if (state != GpuDeploymentState.WaitingForDecision || pending == null || pending.Id != decision.RequestId)
            throw new InvalidOperationException(R.Gpu_DecisionExpired);
        bool valid = decision switch
        {
            GpuConfirmation => pending.Input == GpuDecisionInput.Confirmation,
            GpuPartitionSelection => pending.Input == GpuDecisionInput.Partition,
            GpuDriverFallbackSelection => pending.Input is GpuDecisionInput.Partition or GpuDecisionInput.DriverFallback,
            GpuLinuxBundleSelection => pending.Input == GpuDecisionInput.LinuxBundle,
            GpuBackToPartitions => pending.CanGoBack,
            _ => false
        };
        if (!valid) throw new InvalidOperationException(R.Gpu_DecisionExpired);
        if (decision is GpuPartitionSelection choice)
        {
            if (!partitions.TryGetValue(choice.PartitionId, out var partition)) throw new ArgumentException(R.Gpu_InvalidPartition);
            if (partition.IsBitLocker) throw new ArgumentException(R.Error_Gpu_BitLocker);
            if (!IsSupported(partition)) throw new ArgumentException(R.Gpu_InvalidPartition);
        }
        if (decision is GpuLinuxBundleSelection bundleChoice)
        {
            if (!Enum.IsDefined(bundleChoice.Source))
                throw new ArgumentException(R.Error_Gpu_LinuxInvalidBundleDecision);
            if (bundleChoice.Source == GpuLinuxBundleSource.LocalFile)
            {
                string? localPath = bundleChoice.LocalBundlePath?.Trim();
                if (string.IsNullOrWhiteSpace(localPath)
                    || !localPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException(R.Error_Gpu_LinuxInvalidBundleDecision);
            }
        }
        if (decision is GpuDriverFallbackSelection fallback && !Enum.IsDefined(fallback.Action))
        {
            throw new ArgumentException(R.Gpu_DecisionExpired);
        }
    }

    private static bool IsSupported(DiskPartitionInfo p) =>
        !p.IsBitLocker && p.OsType is OperatingSystemType.Windows or OperatingSystemType.Linux;

    private static bool IsPartitionFallbackReason(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return false;
        if (message.Contains(R.Error_Gpu_BitLocker, StringComparison.OrdinalIgnoreCase)) return true;

        var parts = R.Error_Gpu_InvalidSystemPart.Split("{0}", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length > 0 && parts.All(part => message.Contains(part, StringComparison.OrdinalIgnoreCase));
    }

    private void Ask(GpuDecisionKind kind, string title, string message, bool canGoBack = false)
    {
        cancellation.Token.ThrowIfCancellationRequested();
        var options = Array.AsReadOnly(partitions.Select(p => new GpuDeploymentPartition(p.Key, p.Value.PartitionNumber,
            p.Value.SizeInBytes, p.Value.OsType, p.Value.TypeDescription, p.Value.DiskPath, p.Value.DiskDisplayName,
            p.Value.IsPhysicalDisk, p.Value.IsBitLocker)).ToArray());
        pending = new(Guid.NewGuid(), kind, title, message, options, canGoBack);
        state = GpuDeploymentState.WaitingForDecision;
        SetStep(GpuDeploymentStepStatus.Running, message);
    }

    private void CompleteDrivers(string description)
    {
        cancellation.Token.ThrowIfCancellationRequested();
        Succeed(description);
        index = steps.Count;
    }

    private void DriverProgress(string text) => SetStep(GpuDeploymentStepStatus.Running, text, text);
    private void Succeed(string text) => SetStep(GpuDeploymentStepStatus.Success, text,
        string.Format(R.Msg_Gpu_TaskOk, steps[index].Name, text));

    private void SetStep(GpuDeploymentStepStatus status, string? description, string? log = null)
    {
        stepProgressPercent = null;
        if (index < steps.Count)
            steps[index] = steps[index] with { Status = status, Description = description ?? steps[index].Description };
        Publish(log);
    }

    private async Task EndAsync(GpuDeploymentState destination)
    {
        pending = null;
        cleanupDestination = destination;
        cleanupError = retainedDiskCleanupError;
        state = GpuDeploymentState.Running;
        Publish();
        try
        {
            if (outcomeUnknown) throw new InvalidOperationException("底层操作结果尚未确认，已保留资源，未执行后续清理。");
            if (unidentifiedAssignment) throw new InvalidOperationException(R.Warn_Gpu_RollbackAdapterNotIdentified);
            if (cleanupAdapterIds.Count > 0)
            {
                await backend.VerifyTargetAsync();
                var adapters = await backend.GetAdaptersAsync();
                if (!adapters.Success) throw new InvalidOperationException(R.Gpu_CleanupRequired);
                foreach (string id in cleanupAdapterIds.ToArray())
                {
                    if (adapters.Adapters.Any(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase))
                        && !await backend.RemoveAsync(id))
                        throw new InvalidOperationException(R.Gpu_CleanupRequired);
                    cleanupAdapterIds.Remove(id);
                }
            }
            state = retainedDiskCleanupError == null && !backend.HasPendingRecovery ? destination : GpuDeploymentState.CleanupRequired;
            if (backend.HasPendingRecovery && cleanupError == null) cleanupError = "临时磁盘资源尚未恢复，请核实后重试清理。";
        }
        catch (Exception exception)
        {
            outcomeUnknown |= exception is CoreOperationException { Result.ErrorKind: ApiErrorKind.OutcomeUnknown };
            cleanupError = retainedDiskCleanupError == null ? exception.Message
                : retainedDiskCleanupError + Environment.NewLine + exception.Message;
            state = GpuDeploymentState.CleanupRequired;
        }
        Publish();
    }

    private void Publish(string? log = null)
    {
        var actions = state switch
        {
            GpuDeploymentState.Created => new[] { GpuDeploymentAction.Start, GpuDeploymentAction.Cancel },
            GpuDeploymentState.Running => new[] { GpuDeploymentAction.Cancel },
            GpuDeploymentState.WaitingForDecision when pending!.CanGoBack =>
                new[] { GpuDeploymentAction.SubmitDecision, GpuDeploymentAction.BackToPartitions, GpuDeploymentAction.Cancel },
            GpuDeploymentState.WaitingForDecision => new[] { GpuDeploymentAction.SubmitDecision, GpuDeploymentAction.Cancel },
            GpuDeploymentState.CleanupRequired when !outcomeUnknown && backend.HasRecovery => new[] { GpuDeploymentAction.Reconcile, GpuDeploymentAction.RetryCleanup },
            GpuDeploymentState.CleanupRequired when !outcomeUnknown => new[] { GpuDeploymentAction.RetryCleanup },
            GpuDeploymentState.CleanupRequired when assignmentNeedsReconciliation || backend.HasRecovery => new[] { GpuDeploymentAction.Reconcile },
            _ => Array.Empty<GpuDeploymentAction>()
        };
        var next = new GpuDeploymentSnapshot(sessionId, ++revision, state, Array.AsReadOnly(steps.ToArray()), pending,
            Array.AsReadOnly(actions), addedAdapterIds.Count > 0, cleanupAdapterIds.Count > 0, error, cleanupError, log)
        {
            OutcomeUnknown = outcomeUnknown,
            AddedAdapterId = addedAdapterIds.LastOrDefault(),
            AddedAdapterIds = Array.AsReadOnly(addedAdapterIds.ToArray()),
            PendingCleanupAdapterId = cleanupAdapterIds.FirstOrDefault(),
            PendingCleanupAdapterIds = Array.AsReadOnly(cleanupAdapterIds.ToArray()),
            IsDriverSkipped = isDriverSkipped,
            StepProgressPercent = stepProgressPercent,
            GuestSetupCommand = guestSetupCommand,
            LinuxIsoPath = linuxIsoPath,
            LinuxDriverVersion = linuxDriverVersion,
            BundleError = bundleError,
            Recovery = backend.GetRecovery()
        };
        Volatile.Write(ref snapshot, next);
        try { report?.Invoke(next); }
        catch (Exception exception) { CoreDiagnostics.Report("DeploymentObserverFailed", error: exception); }
    }

    private static DiskPartitionInfo ClonePartition(DiskPartitionInfo p) => new(p.PartitionNumber, p.SizeInBytes, p.OsType, p.TypeDescription)
        { DiskPath = p.DiskPath, DiskDisplayName = p.DiskDisplayName, IsPhysicalDisk = p.IsPhysicalDisk, IsBitLocker = p.IsBitLocker, ScannedVmId = p.ScannedVmId, ScannedDiskPath = p.ScannedDiskPath, ScannedPhysical = p.ScannedPhysical, ScannedDiskIdentity = p.ScannedDiskIdentity };

    private static List<GpuDeploymentStep> BuildSteps(GpuDeploymentMode mode)
    {
        var result = new List<GpuDeploymentStep>();
        void Add(GpuDeploymentStepKind type, string name, string description) => result.Add(new(type, name, description, GpuDeploymentStepStatus.Pending));
        if (mode != GpuDeploymentMode.AddWithoutDrivers)
            Add(GpuDeploymentStepKind.PowerCheck, R.Task_Gpu_Power, R.Msg_Gpu_CheckingPower);
        if (mode != GpuDeploymentMode.UpdateDrivers)
        {
            Add(GpuDeploymentStepKind.Optimization, R.Task_Gpu_Opt, R.Msg_Gpu_Mmio);
            Add(GpuDeploymentStepKind.Assign, R.Task_Gpu_Assign, R.Msg_Gpu_Creating);
        }
        if (mode != GpuDeploymentMode.AddWithoutDrivers)
            Add(GpuDeploymentStepKind.Driver, R.Task_Gpu_SelectPartition, R.Msg_Gpu_WaitingScan);
        return result;
    }
}

internal interface IGpuDeploymentBackend
{
    int GpuCount => 1;
    bool HasRecovery => false;
    bool HasPendingRecovery => false;
    Task<ApiResponse> ReconcileRecoveryAsync() => Task.FromResult(ApiResponse.Ok());
    Task<ApiResponse> RetryRecoveryAsync() => Task.FromResult(ApiResponse.Ok());
    GpuDeploymentRecovery? GetRecovery() => null;
    Task<GpuAssignmentAttempt> ReconcileAssignmentAsync() => Task.FromResult(new GpuAssignmentAttempt(
        ApiResponse.Fail("本次调用没有可核实的创建回执。", kind: ApiErrorKind.OutcomeUnknown), OwnershipUncertain: true));
    Task VerifyTargetAsync();
    Task PrepareHostAsync();
    Task<bool> CheckConfigurationAsync();
    Task<(bool IsOff, string State)> IsPoweredOffAsync();
    Task PowerAsync(string action);
    Task<bool> OptimizeAsync();
    Task<(bool Success, List<(string Id, string InstancePath)> Adapters)> GetAdaptersAsync();
    Task<GpuAssignmentAttempt> AssignAsync();
    Task<GpuAssignmentAttempt> AssignAtAsync(int index) => AssignAsync();
    Task<bool> RemoveAsync(string adapterId);
    Task<List<DiskPartitionInfo>> GetPartitionsAsync();
    Task<(bool Success, string Message)> SyncWindowsAsync(DiskPartitionInfo partition, Action<string> log);
    Task RecordGuestDriverVersionsAsync() => Task.CompletedTask;
    string? GetPrimaryDriverVersion() => null;
    Task<string> DownloadLinuxBundleAsync(Action<int, string> progress, CancellationToken cancellationToken)
        => throw new NotSupportedException();
    Task<string> SynthesizeLinuxIsoAsync(string? bundlePath, Action<int, string> progress, CancellationToken cancellationToken)
        => throw new NotSupportedException();
    Task MountLinuxIsoAsync(string isoPath, CancellationToken cancellationToken)
        => throw new NotSupportedException();
}

internal sealed class GpuDeploymentCleanupException(string message) : Exception(message);

internal sealed class NativeGpuDeploymentBackend(
    Guid vmId,
    IReadOnlyList<HostGpuInfo> selectedGpus,
    VmGpuService gpu) : IGpuDeploymentBackend
{
    private readonly GpuRecoveryJournal recovery = new();
    private readonly List<WmiCreationTrace> assignmentTraces = new();
    private WmiCreationTrace? lastAssignmentTrace;

    public int GpuCount => selectedGpus.Count;
    public bool HasRecovery => recovery.HasRecords;
    public bool HasPendingRecovery => recovery.NeedsCleanup;
    public Task<ApiResponse> ReconcileRecoveryAsync() => recovery.ReconcileAsync();
    public Task<ApiResponse> RetryRecoveryAsync() => recovery.RetryAsync();

    public GpuDeploymentRecovery? GetRecovery()
    {
        if (assignmentTraces.Count == 0 && !recovery.HasRecords) return null;
        string[] createdPaths = assignmentTraces
            .SelectMany(t => t.Result?.CreatedPaths.Length > 0 ? t.Result.CreatedPaths : t.DirectPaths)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new(recovery.Id, vmId, recovery.StartedAtUtc, lastAssignmentTrace?.JobPath, Array.AsReadOnly(createdPaths))
        {
            Operations = recovery.Operations,
            Resources = recovery.Resources,
            ObservedValues = new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(new Dictionary<string, string>(recovery.ObservedValues))
        };
    }

    public Task<GpuAssignmentAttempt> ReconcileAssignmentAsync() => lastAssignmentTrace is { } trace
        ? gpu.ReconcileAssignmentAsync(vmId, trace)
        : Task.FromResult(new GpuAssignmentAttempt(ApiResponse.Fail("未留下创建回执。", kind: ApiErrorKind.OutcomeUnknown), OwnershipUncertain: true));

    private static string ResolveGpuPath(HostGpuInfo info)
        => string.IsNullOrEmpty(info.PartitionableGpuPath) ? info.InstanceId : info.PartitionableGpuPath;

    public async Task VerifyTargetAsync()
    {
        var response = await VmConfigurationState.ReadAsync(vmId);
        if (!response.HasData)
            throw new InvalidOperationException(response.Success ? "目标虚拟机已不存在。" : response.Error);
    }

    public async Task PrepareHostAsync()
    {
        var result = await Task.Run(() => HostGpuService.SetAssignmentRestrictionsRelaxed(true));
        if (!result.Success) throw new InvalidOperationException(result.Error, result.Exception);
    }

    public async Task<bool> CheckConfigurationAsync()
    {
        var result = await gpu.CheckVmForGpuAsync(vmId);
        if (!result.HasData) throw new InvalidOperationException(result.Error, result.Exception);
        return result.Data;
    }

    public async Task<(bool IsOff, string State)> IsPoweredOffAsync()
    {
        var result = await VmConfigurationState.ReadAsync(vmId);
        if (!result.HasData) throw new InvalidOperationException(result.Error, result.Exception);
        return (result.Data!.IsPoweredOff, result.Data.State.ToString());
    }

    public Task PowerAsync(string action) => recovery.RunAsync("Power:" + action, async () =>
    {
        var result = await VmPowerService.ExecuteControlActionAsync(vmId, action);
        OperationOutcomeUnknownException.ThrowIfUnknown(result);
        if (!result.Success) throw new InvalidOperationException(result.Error, result.Exception);
        return true;
    }, ObservePowerAsync);

    public Task<bool> OptimizeAsync() => recovery.RunAsync("Mmio", async () =>
    {
        var result = await gpu.OptimizeVmForGpuAsync(vmId);
        OperationOutcomeUnknownException.ThrowIfUnknown(result);
        if (!result.Success) throw new InvalidOperationException(result.Error, result.Exception);
        return true;
    }, ObserveMmioAsync);

    private async Task<ApiResponse> ObservePowerAsync()
    {
        var result = await VmConfigurationState.ReadAsync(vmId);
        if (!result.HasData) return ApiResponse.Fail(result.Error, result.Code, result.ErrorSource, result.Exception, result.ErrorKind);
        recovery.ObservedValues["Power.State"] = result.Data.State.ToString();
        return ApiResponse.Ok();
    }

    private async Task<ApiResponse> ObserveMmioAsync()
    {
        var power = await ObservePowerAsync();
        if (!power.Success) return power;
        var settings = await VmMmioService.GetSettingsAsync(vmId);
        if (!settings.HasData) return ApiResponse.Fail(settings.Error, settings.Code, settings.ErrorSource, settings.Exception, settings.ErrorKind);
        recovery.ObservedValues["Mmio.LowSizeMiB"] = settings.Data.LowSizeMiB?.ToString() ?? "Unavailable";
        recovery.ObservedValues["Mmio.HighSizeMiB"] = settings.Data.HighSizeMiB?.ToString() ?? "Unavailable";
        recovery.ObservedValues["Mmio.HighBaseMiB"] = settings.Data.HighBaseMiB?.ToString() ?? "Unavailable";
        return ApiResponse.Ok();
    }

    public Task<(bool Success, List<(string Id, string InstancePath)> Adapters)> GetAdaptersAsync()
        => gpu.TryGetVmGpuAdaptersAsync(vmId);

    public Task<GpuAssignmentAttempt> AssignAsync() => AssignAtAsync(0);

    public Task<GpuAssignmentAttempt> AssignAtAsync(int index)
    {
        var trace = new WmiCreationTrace("Msvm_GpuPartitionSettingData");
        assignmentTraces.Add(trace);
        lastAssignmentTrace = trace;
        return gpu.AssignGpuPartitionWithOwnershipAsync(vmId, ResolveGpuPath(selectedGpus[index]), trace);
    }

    public Task<bool> RemoveAsync(string adapterId) => recovery.RunAsync("RemoveAdapter", async () =>
    {
        var result = await gpu.RemoveGpuPartitionAsync(vmId, adapterId);
        OperationOutcomeUnknownException.ThrowIfUnknown(result);
        return result.Success;
    });

    public Task<List<DiskPartitionInfo>> GetPartitionsAsync() => recovery.RunAsync("Partitions", async () =>
    {
        var result = await gpu.ScanVmPartitionsAsync(vmId);
        if (result.OutcomeUnknown) throw new OperationOutcomeUnknownException(ApiResponse.Fail(
            string.Join(Environment.NewLine, result.Errors.Concat(result.CleanupErrors)), kind: ApiErrorKind.OutcomeUnknown));
        if (result.CleanupRequired || result.OutcomeUnknown) throw new GpuDeploymentCleanupException(string.Join(Environment.NewLine, result.Errors.Concat(result.CleanupErrors)));
        if (!result.Success) throw new InvalidOperationException(string.Join(Environment.NewLine, result.Errors.Concat(result.CleanupErrors)));
        return result.Partitions.ToList();
    });

    public Task<(bool Success, string Message)> SyncWindowsAsync(DiskPartitionInfo partition, Action<string> log) => recovery.RunAsync("WindowsDrivers", async () =>
    {
        var result = await gpu.SyncWindowsDriversCoreAsync(vmId, selectedGpus, partition, log);
        OperationOutcomeUnknownException.ThrowIfUnknown(result);
        if (result.ErrorKind is ApiErrorKind.CleanupRequired or ApiErrorKind.OutcomeUnknown) throw new GpuDeploymentCleanupException(result.Error);
        return (result.Success, result.Error);
    });

    public Task RecordGuestDriverVersionsAsync() => recovery.RunAsync("GpuDriverVersions", async () =>
    {
        var updates = selectedGpus
            .Where(g => !string.IsNullOrWhiteSpace(g.InstanceId) && !string.IsNullOrWhiteSpace(g.DriverVersion))
            .Select(g => (g.InstanceId, g.DriverVersion));
        var result = await VmMetadataService.UpsertGpuDriverVersionsAsync(vmId, updates).ConfigureAwait(false);
        OperationOutcomeUnknownException.ThrowIfUnknown(result);
        if (!result.Success) throw new CoreOperationException(result);
        return true;
    });

    public string? GetPrimaryDriverVersion() => selectedGpus
        .Select(g => g.DriverVersion)
        .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))
        ?.Trim();

    public Task<string> DownloadLinuxBundleAsync(Action<int, string> progress, CancellationToken cancellationToken) =>
        recovery.RunAsync("LinuxBundleDownload",
            () => LinuxGpuPvBundleService.DownloadLatestAsync(progress, cancellationToken));

    public Task<string> SynthesizeLinuxIsoAsync(string? bundlePath, Action<int, string> progress, CancellationToken cancellationToken) =>
        recovery.RunAsync("LinuxIsoSynthesize",
            () => LinuxGpuPvRuntimeService.SynthesizeIsoAsync(vmId, selectedGpus, bundlePath ?? string.Empty, progress, cancellationToken));

    public Task MountLinuxIsoAsync(string isoPath, CancellationToken cancellationToken) =>
        recovery.RunAsync("LinuxMountIso", async () =>
        {
            await LinuxGpuPvRuntimeService.MountIsoToVmAsync(vmId, isoPath, cancellationToken).ConfigureAwait(false);
            return true;
        });
}
