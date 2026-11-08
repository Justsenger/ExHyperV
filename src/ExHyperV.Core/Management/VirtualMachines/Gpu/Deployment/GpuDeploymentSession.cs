using ExHyperV.Models;
using ExHyperV.Tools;
using R = ExHyperV.Properties.Resources;

namespace ExHyperV.Services;

/// <summary>管理一次进程内 GPU 部署；Core 推进各步骤，并在需要确认时暂停，等待带版本标识的明确决定。</summary>
/// <remarks>每个实例管理一次 GPU 部署，串行执行写操作；GetStatus 可随时读取已发布快照。执行失败返回 Failed 或 CleanupRequired；错误状态、过期决定和无效输入抛出异常，保留有效的待确认事项。失败或取消后保留已完成的策略、MMIO、文件和电源变化，恢复本次临时磁盘资源，并清理创建回执明确归属于本次操作的 GPU 适配器。原任务是否结束或资源归属无法确认时保留现场；按 AllowedActions 调用 ReconcileAsync 核实和 RetryCleanupAsync 清理。恢复仅限当前进程内的此实例，不提供驱动文件回滚或重启后的恢复。</remarks>
public sealed class GpuDeploymentSession
{
    private readonly IGpuDeploymentBackend backend;
    private readonly GpuDeploymentMode mode;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly CancellationTokenSource cancellation = new();
    private readonly Guid sessionId = Guid.NewGuid();
    private readonly List<GpuDeploymentStep> steps;
    private readonly Dictionary<string, DiskPartitionInfo> partitions = new();
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
    private string? addedAdapterId, cleanupAdapterId, error, cleanupError;
    private string? retainedDiskCleanupError;
    private bool outcomeUnknown;
    private bool assignmentNeedsReconciliation;

    /// <summary>创建尚未开始执行的单次 GPU 部署会话。</summary>
    /// <param name="vmId">目标虚拟机 GUID；后续会按身份核对目标。</param>
    /// <param name="vmName">目标虚拟机名称，应与 vmId 对应。</param>
    /// <param name="gpu">目标宿主 GPU 及可分区资源信息。</param>
    /// <param name="gpuService">执行 GPU 查询与配置的服务。</param>
    /// <param name="queryService">读取目标虚拟机信息的服务；由调用方持有和释放，须在部署及后续核实、清理期间保持可用。</param>
    /// <param name="mode">本次部署范围，默认新增 GPU 并安装驱动；未定义的枚举值会被拒绝。</param>
    /// <exception cref="ArgumentOutOfRangeException">mode 不是定义的部署模式。</exception>
    public GpuDeploymentSession(Guid vmId, string vmName, HostGpuInfo gpu, VmGpuService gpuService,
        VmQueryService queryService, GpuDeploymentMode mode = GpuDeploymentMode.AddWithDrivers)
        : this(new NativeGpuDeploymentBackend(vmId, vmName, gpu, gpuService, queryService), mode) { }

    internal GpuDeploymentSession(IGpuDeploymentBackend backend, GpuDeploymentMode mode = GpuDeploymentMode.AddWithDrivers)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        this.backend = backend;
        this.mode = mode;
        steps = BuildSteps(mode);
        Publish();
    }

    // Polling never waits for a native operation. Returned values cannot mutate this session.
    /// <summary>读取最近发布的状态，不等待正在进行的底层操作。</summary>
    /// <returns>当前快照；读取不会推进流程或修改会话。</returns>
    public GpuDeploymentSnapshot GetStatus() => Volatile.Read(ref snapshot);
    /// <summary>仅发送协作式取消请求，立即返回；正在执行的不可取消底层调用仍需自行结束。</summary>
    public void RequestCancellation() => cancellation.Cancel();

    /// <summary>开始部署并执行到等待用户决定或结束；同一会话只能从 Created 状态启动。</summary>
    /// <param name="progress">可选的快照通知，在执行流程所在的上下文同步触发。回调应快速返回；界面更新需调度到界面线程。阻塞等待会话写操作会导致死锁；回调异常会被隔离。</param>
    /// <returns>停止推进时的最新快照，可能仍需用户决定。</returns>
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

    /// <summary>校验当前请求 ID 和输入种类后推进部署；过期或错误的提交不会替代正在等待的合法请求。</summary>
    /// <param name="decision">与 PendingDecision 对应的确认、分区、Linux 输入或返回决定；不能为空。</param>
    /// <returns>执行至下一次等待或结束后的快照。</returns>
    /// <remarks>选择分区后会重新核对磁盘状态。确认 PowerOff 会直接 TurnOff；确认 Windows 安装会修改离线分区，Linux 部署可能启动或重启来宾。</remarks>
    /// <exception cref="ArgumentNullException">decision 为 null。</exception>
    /// <exception cref="InvalidOperationException">没有待确认事项，或请求 ID、输入种类与当前事项不匹配。</exception>
    /// <exception cref="ArgumentException">分区不在当前列表、分区不受支持或已加密，或 Linux 脚本、地址、端口、凭据不符合要求。</exception>
    public async Task<GpuDeploymentSnapshot> SubmitDecisionAsync(GpuDeploymentDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        decision = CopyDecision(decision);
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
                            if (afterPowerOff == AfterPowerOff.Windows) await PrepareSelectedPartitionAsync();
                            else if (afterPowerOff == AfterPowerOff.Scan) await ScanPartitionsAsync();
                            else { Succeed(R.Msg_Gpu_Off); index++; await AdvanceAsync(); }
                        }
                        else if (kind == GpuDecisionKind.InstallWindows)
                        {
                            if (!(await ReadPowerAsync()).IsOff)
                            {
                                AskPowerOff(forWindows: true);
                                return;
                            }
                            await VerifyPartitionAsync();
                            cancellation.Token.ThrowIfCancellationRequested();
                            var synced = await backend.SyncWindowsAsync(selected!, DriverProgress);
                            if (!synced.Success) throw new InvalidOperationException(synced.Message);
                            CompleteDrivers(R.Msg_Gpu_DriverOk);
                        }
                        else if (kind == GpuDecisionKind.StartLinux)
                        {
                            if ((await ReadPowerAsync()).IsOff)
                            {
                                await VerifyPartitionAsync();
                                cancellation.Token.ThrowIfCancellationRequested();
                                await backend.PowerAsync("Start");
                                await Task.Delay(3000, cancellation.Token);
                            }
                            await AskLinuxCredentialsAsync();
                        }
                        break;
                    case GpuPartitionSelection choice:
                        selected = ClonePartition(partitions[choice.PartitionId]);
                        await PrepareSelectedPartitionAsync();
                        break;
                    case GpuBackToPartitions:
                        selected = null;
                        Ask(GpuDecisionKind.SelectPartition, R.Label_SelectPart, R.Msg_Gpu_ManualSelect);
                        break;
                    case GpuLinuxInput input:
                        await VerifyTargetAsync();
                        if ((await ReadPowerAsync()).IsOff)
                        {
                            Ask(GpuDecisionKind.StartLinux, R.Task_Gpu_Driver, R.Gpu_ConfirmLinuxStart, canGoBack: true);
                            return;
                        }
                        var result = await backend.ProvisionLinuxAsync(input.Script!, input.Credentials!, text =>
                        {
                            var match = System.Text.RegularExpressions.Regex.Match(text, @"\[STEP:\s*(.*?)\]");
                            SetStep(GpuDeploymentStepStatus.Running, match.Success ? match.Groups[1].Value : null, text);
                        }, cancellation.Token);
                        if (!(result == "OK" || (result.Contains("successfully") && result.Contains("signing"))))
                            throw new InvalidOperationException(result);
                        CompleteDrivers(R.Msg_Gpu_LinuxDeployDone);
                        break;
                }
            });
        }
        finally { gate.Release(); }
    }

    /// <summary>请求取消，等待当前操作退出后尝试清理本次新增的适配器。</summary>
    /// <returns>取消或既有终态快照；清理未完成时返回 CleanupRequired。</returns>
    /// <remarks>不是立即中断或完整回滚。已完成、已失败、已取消或已经等待清理的会话直接返回当前状态；仅当 AllowedActions 包含 RetryCleanup 时才能重试清理。OutcomeUnknown 为 true 时保留资源；允许 Reconcile 时可调用 ReconcileAsync 只读核实原操作。</remarks>
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
    /// <remarks>先再次核实原任务，再在原资源锁内按盘符、物理盘挂接、磁盘原状态或映像卸载的顺序恢复。只恢复本次改变的资源，借用的已有挂载不卸载；磁盘身份、分区或控制器变化时停止，不操作替代对象。磁盘恢复要求 VM 已关闭，本方法不会自动关机。原来的联机和只读状态会覆盖期间的外部状态修改。恢复失败后保留原始记录；恢复本身结果未知时，下一步只能先 Reconcile。不会重新分配 GPU、复制驱动、继续部署或撤回已完成的电源和 MMIO 修改。</remarks>
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
    /// <returns>更新后的快照。原任务结束且资源已恢复时进入原定的 Failed 或 Cancelled；仍需清理时停在 CleanupRequired，并更新 AllowedActions。</returns>
    /// <remarks>覆盖适配器创建和移除、电源、MMIO、分区扫描与 Windows 驱动部署中的临时磁盘操作。只观察已保存的任务，不重新执行写入；当前状态恰好符合预期不能单独证明旧任务已结束。作业仍运行、无法读取或回执不足时保留 OutcomeUnknown。Recovery.Operations 提供任务结果，Resources 提供原状态和恢复项，ObservedValues 提供本次核实到的电源与 MMIO 值。核实后不自动继续部署或清理；尚需恢复时由调用方明确调用 RetryCleanupAsync。作业已回收且没有终态证据、进程重启、驱动文件回滚和 Linux 在线部署仍需另行处理。</remarks>
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
                    addedAdapterId = cleanupAdapterId = result.CreatedAdapterId;
                    unidentifiedAssignment = assignmentNeedsReconciliation = false;
                }
                var observed = await backend.ReconcileRecoveryAsync();
                outcomeUnknown = !observed.Success;
                if (!observed.Success) cleanupError = observed.Error;
                else
                {
                    retainedDiskCleanupError = backend.HasPendingRecovery ? "临时磁盘资源尚未恢复，请调用 RetryCleanupAsync。" : null;
                    cleanupError = retainedDiskCleanupError;
                    // A completed removal may already have removed the exact owned adapter.
                    // This observation never issues a new removal or resumes deployment.
                    if (cleanupAdapterId != null)
                    {
                        await backend.VerifyTargetAsync();
                        var adapters = await backend.GetAdaptersAsync();
                        if (!adapters.Success) throw new InvalidOperationException(R.Gpu_CleanupRequired);
                        if (!adapters.Adapters.Any(a => string.Equals(a.Id, cleanupAdapterId, StringComparison.OrdinalIgnoreCase)))
                            cleanupAdapterId = null;
                    }
                    if (cleanupAdapterId == null && !unidentifiedAssignment && !backend.HasPendingRecovery)
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
                cleanupAdapterId = null;
                unidentifiedAssignment = false;
                state = GpuDeploymentState.Completed;
                Publish();
            }
        }
        catch (Exception exception)
        {
            outcomeUnknown |= exception is OperationOutcomeUnknownException;
            if (outcomeUnknown) retainedDiskCleanupError = exception.Message;
            if (exception is GpuDeploymentCleanupException) retainedDiskCleanupError = exception.Message;
            error = cancellation.IsCancellationRequested ? null : exception.Message;
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
                case GpuDeploymentStepKind.Prepare:
                    await backend.PrepareHostAsync();
                    description = R.Msg_Gpu_Policy;
                    break;
                case GpuDeploymentStepKind.ConfigCheck:
                    needConfig = !await backend.CheckConfigurationAsync();
                    description = needConfig ? R.Msg_Gpu_ConfigNeeded : R.Msg_Gpu_ConfigOk;
                    break;
                case GpuDeploymentStepKind.PowerCheck:
                    if (mode != GpuDeploymentMode.AddWithoutDrivers && !(await ReadPowerAsync()).IsOff)
                    {
                        AskPowerOff(forWindows: false);
                        return;
                    }
                    description = mode == GpuDeploymentMode.AddWithoutDrivers ? R.Msg_Skip : R.Msg_Gpu_Off;
                    break;
                case GpuDeploymentStepKind.Optimization:
                    description = needConfig && (await ReadPowerAsync()).IsOff
                        ? (await backend.OptimizeAsync() ? R.Msg_Gpu_MmioOk : R.Error_Gpu_OptFail) : R.Msg_Skip;
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
            cancellation.Token.ThrowIfCancellationRequested();
            Succeed(description);
            index++;
        }
    }

    private async Task AssignAsync()
    {
        cancellation.Token.ThrowIfCancellationRequested();
        // 收到创建回执前，异常仍可能发生在底层已经写入之后。
        unidentifiedAssignment = true;
        assignmentNeedsReconciliation = true;
        var assigned = await backend.AssignAsync();
        addedAdapterId = cleanupAdapterId = string.IsNullOrWhiteSpace(assigned.CreatedAdapterId) ? null : assigned.CreatedAdapterId;
        unidentifiedAssignment = assigned.OwnershipUncertain;
        assignmentNeedsReconciliation = assigned.OwnershipUncertain || assigned.Response.ErrorKind == ApiErrorKind.OutcomeUnknown;
        OperationOutcomeUnknownException.ThrowIfUnknown(assigned.Response);
        if (!assigned.Response.Success) throw new InvalidOperationException(assigned.Response.Error, assigned.Response.Exception);
        if (unidentifiedAssignment) throw new InvalidOperationException(R.Warn_Gpu_RollbackAdapterNotIdentified);
        if (cleanupAdapterId == null)
        {
            unidentifiedAssignment = true;
            throw new InvalidOperationException(R.Warn_Gpu_RollbackAdapterNotIdentified);
        }
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

    private void AskPowerOff(bool forWindows)
    {
        afterPowerOff = forWindows ? AfterPowerOff.Windows : AfterPowerOff.Advance;
        Ask(GpuDecisionKind.PowerOff, R.Task_Gpu_Power, R.Gpu_ConfirmPowerOff);
    }

    private async Task ScanPartitionsAsync()
    {
        await VerifyTargetAsync();
        if (!(await ReadPowerAsync()).IsOff)
        {
            AskPowerOff(forWindows: false);
            afterPowerOff = AfterPowerOff.Scan;
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
        if (found.Count == 1 && IsSupported(found[0]))
        {
            selected = ClonePartition(found[0]);
            await PrepareSelectedPartitionAsync();
        }
        else Ask(GpuDecisionKind.SelectPartition, R.Label_SelectPart, R.Msg_Gpu_ManualSelect);
    }

    private async Task PrepareSelectedPartitionAsync()
    {
        await VerifyTargetAsync();
        if (selected!.OsType == OperatingSystemType.Windows)
        {
            if (!(await ReadPowerAsync()).IsOff) AskPowerOff(forWindows: true);
            else
            {
                Ask(GpuDecisionKind.InstallWindows, R.Task_Gpu_Driver,
                    string.Format(R.Gpu_ConfirmWindowsInstall, selected.DisplayName), canGoBack: true);
            }
        }
        else if ((await ReadPowerAsync()).IsOff)
            Ask(GpuDecisionKind.StartLinux, R.Task_Gpu_Driver, R.Gpu_ConfirmLinuxStart, canGoBack: true);
        else await AskLinuxCredentialsAsync();
    }

    private async Task AskLinuxCredentialsAsync()
    {
        DriverProgress(R.Msg_Gpu_IpScanning);
        string ip = await backend.FindIpAsync();
        cancellation.Token.ThrowIfCancellationRequested();
        Ask(GpuDecisionKind.LinuxCredentials, R.Label_ConnectToLinuxVm, R.Msg_Gpu_SshConfirm,
            Ipv4.SelectBest(ip), canGoBack: true);
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
            GpuLinuxInput => pending.Input == GpuDecisionInput.LinuxCredentials,
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
        if (decision is GpuLinuxInput input)
        {
            var c = input.Credentials;
            if (input.Script == null || string.IsNullOrWhiteSpace(input.Script.SourceUrl) || string.IsNullOrWhiteSpace(input.Script.FileName)
                || c == null || string.IsNullOrWhiteSpace(c.Host) || string.IsNullOrWhiteSpace(c.Username))
                throw new ArgumentException(R.VmPage_GpuCheckDeploy);
            if (string.IsNullOrEmpty(Ipv4.SelectBest(c.Host))) throw new ArgumentException(R.Error_Gpu_NoIpv4);
            if (c.Port is < 1 or > 65535) throw new ArgumentException(R.Gpu_InvalidSshPort);
            if (c.UseProxy && (string.IsNullOrWhiteSpace(c.ProxyHost) || c.ProxyPort is null or < 1 or > 65535))
                throw new ArgumentException(R.Validation_ProxyIpAndPortMismatch);
        }
    }

    private static bool IsSupported(DiskPartitionInfo p) =>
        !p.IsBitLocker && p.OsType is OperatingSystemType.Windows or OperatingSystemType.Linux;

    private void Ask(GpuDecisionKind kind, string title, string message, string host = "", bool canGoBack = false)
    {
        cancellation.Token.ThrowIfCancellationRequested();
        var options = Array.AsReadOnly(partitions.Select(p => new GpuDeploymentPartition(p.Key, p.Value.PartitionNumber,
            p.Value.SizeInBytes, p.Value.OsType, p.Value.TypeDescription, p.Value.DiskPath, p.Value.DiskDisplayName,
            p.Value.IsPhysicalDisk, p.Value.IsBitLocker)).ToArray());
        pending = new(Guid.NewGuid(), kind, title, message, options, host, canGoBack);
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
            if (cleanupAdapterId != null)
            {
                await backend.VerifyTargetAsync();
                var adapters = await backend.GetAdaptersAsync();
                if (!adapters.Success) throw new InvalidOperationException(R.Gpu_CleanupRequired);
                if (adapters.Adapters.Any(a => string.Equals(a.Id, cleanupAdapterId, StringComparison.OrdinalIgnoreCase))
                    && !await backend.RemoveAsync(cleanupAdapterId)) throw new InvalidOperationException(R.Gpu_CleanupRequired);
                cleanupAdapterId = null;
            }
            state = retainedDiskCleanupError == null && !backend.HasPendingRecovery ? destination : GpuDeploymentState.CleanupRequired;
            if (backend.HasPendingRecovery && cleanupError == null) cleanupError = "临时磁盘资源尚未恢复，请核实后重试清理。";
        }
        catch (Exception exception)
        {
            outcomeUnknown |= exception is OperationOutcomeUnknownException;
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
            Array.AsReadOnly(actions), addedAdapterId, cleanupAdapterId, error, cleanupError, log)
            { OutcomeUnknown = outcomeUnknown, Recovery = backend.GetRecovery() };
        Volatile.Write(ref snapshot, next);
        try { report?.Invoke(next); }
        catch (Exception exception) { System.Diagnostics.Debug.WriteLine(exception); }
    }

    private static DiskPartitionInfo ClonePartition(DiskPartitionInfo p) => new(p.PartitionNumber, p.SizeInBytes, p.OsType, p.TypeDescription)
        { DiskPath = p.DiskPath, DiskDisplayName = p.DiskDisplayName, IsPhysicalDisk = p.IsPhysicalDisk, IsBitLocker = p.IsBitLocker, ScannedVmId = p.ScannedVmId, ScannedDiskPath = p.ScannedDiskPath, ScannedPhysical = p.ScannedPhysical, ScannedDiskIdentity = p.ScannedDiskIdentity };

    private static GpuDeploymentDecision CopyDecision(GpuDeploymentDecision decision)
    {
        if (decision is not GpuLinuxInput input) return decision;
        var s = input.Script;
        var c = input.Credentials;
        return new GpuLinuxInput(input.RequestId,
            s == null ? null : new LinuxGpuDeploymentScript { Name = s.Name, Description = s.Description, Author = s.Author,
                Version = s.Version, SourceUrl = s.SourceUrl, FileName = s.FileName },
            c == null ? null : new SshCredentials { Host = c.Host, Port = c.Port, Username = c.Username, Password = c.Password,
                UseProxy = c.UseProxy, ProxyHost = c.ProxyHost, ProxyPort = c.ProxyPort, InstallGraphics = c.InstallGraphics });
    }

    private static List<GpuDeploymentStep> BuildSteps(GpuDeploymentMode mode)
    {
        var result = new List<GpuDeploymentStep>();
        void Add(GpuDeploymentStepKind type, string name, string description) => result.Add(new(type, name, description, GpuDeploymentStepStatus.Pending));
        if (mode != GpuDeploymentMode.UpdateDrivers)
        {
            Add(GpuDeploymentStepKind.Prepare, R.Task_Gpu_Prepare, R.Msg_Gpu_PreparingHost);
            Add(GpuDeploymentStepKind.ConfigCheck, R.Task_Gpu_Config, R.Msg_Gpu_CheckingConfig);
        }
        Add(GpuDeploymentStepKind.PowerCheck, R.Task_Gpu_Power, R.Msg_Gpu_CheckingPower);
        if (mode != GpuDeploymentMode.UpdateDrivers)
        {
            Add(GpuDeploymentStepKind.Optimization, R.Task_Gpu_Opt, R.Msg_Gpu_Mmio);
            Add(GpuDeploymentStepKind.Assign, R.Task_Gpu_Assign, R.Msg_Gpu_Creating);
        }
        if (mode != GpuDeploymentMode.AddWithoutDrivers) Add(GpuDeploymentStepKind.Driver, R.Task_Gpu_Driver, R.Msg_Gpu_WaitingScan);
        return result;
    }
}

internal interface IGpuDeploymentBackend
{
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
    Task<bool> RemoveAsync(string adapterId);
    Task<List<DiskPartitionInfo>> GetPartitionsAsync();
    Task<(bool Success, string Message)> SyncWindowsAsync(DiskPartitionInfo partition, Action<string> log);
    Task<string> FindIpAsync();
    Task<string> ProvisionLinuxAsync(LinuxGpuDeploymentScript script, SshCredentials credentials, Action<string> log, CancellationToken token);
}

internal sealed class GpuDeploymentCleanupException(string message) : Exception(message);

internal sealed class NativeGpuDeploymentBackend(Guid vmId, string vmName, HostGpuInfo selectedGpu, VmGpuService gpu, VmQueryService query) : IGpuDeploymentBackend
{
    private readonly GpuRecoveryJournal recovery = new();
    public bool HasRecovery => recovery.HasRecords;
    public bool HasPendingRecovery => recovery.NeedsCleanup;
    public Task<ApiResponse> ReconcileRecoveryAsync() => recovery.ReconcileAsync();
    public Task<ApiResponse> RetryRecoveryAsync() => recovery.RetryAsync();
    private WmiCreationTrace? assignmentTrace;
    public GpuDeploymentRecovery? GetRecovery()
    {
        if (assignmentTrace is null && !recovery.HasRecords) return null;
        var trace = assignmentTrace;
        return new(recovery.Id, vmId, recovery.StartedAtUtc, trace?.JobPath,
            Array.AsReadOnly((trace?.Result?.CreatedPaths.Length > 0 ? trace.Result.CreatedPaths : trace?.DirectPaths ?? []).ToArray()))
        {
            Operations = recovery.Operations, Resources = recovery.Resources,
            ObservedValues = new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(new Dictionary<string, string>(recovery.ObservedValues))
        };
    }
    public Task<GpuAssignmentAttempt> ReconcileAssignmentAsync() => assignmentTrace is { } trace
        ? gpu.ReconcileAssignmentAsync(vmId, trace)
        : Task.FromResult(new GpuAssignmentAttempt(ApiResponse.Fail("未留下创建回执。", kind: ApiErrorKind.OutcomeUnknown), OwnershipUncertain: true));
    private readonly string gpuPath = selectedGpu.PartitionableGpuPath;
    private readonly string instanceId = selectedGpu.InstanceId;
    private readonly string manufacturer = selectedGpu.Manu;

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
    public Task<(bool Success, List<(string Id, string InstancePath)> Adapters)> GetAdaptersAsync() => gpu.TryGetVmGpuAdaptersAsync(vmId);
    public Task<GpuAssignmentAttempt> AssignAsync() =>
        gpu.AssignGpuPartitionWithOwnershipAsync(vmId, string.IsNullOrEmpty(gpuPath) ? instanceId : gpuPath,
            assignmentTrace = new("Msvm_GpuPartitionSettingData"));
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
        var result = await gpu.SyncWindowsDriversAsync(vmId, gpuPath, manufacturer, partition, log);
        OperationOutcomeUnknownException.ThrowIfUnknown(result);
        if (result.ErrorKind is ApiErrorKind.CleanupRequired or ApiErrorKind.OutcomeUnknown) throw new GpuDeploymentCleanupException(result.Error);
        return (result.Success, result.Error);
    });
    public async Task<string> ProvisionLinuxAsync(LinuxGpuDeploymentScript script, SshCredentials credentials, Action<string> log, CancellationToken token)
    {
        // Linux 部署入口按名称执行，提交前必须核对名称仍指向所选 GUID。
        var target = await VmIdentity.ResolveAsync(vmName);
        if (!target.HasData || target.Data != vmId)
            throw new InvalidOperationException("Linux 部署目标名称已变化或不唯一，请重新选择目标。");
        return await gpu.ProvisionLinuxGpuAsync(vmName, string.IsNullOrEmpty(gpuPath) ? instanceId : gpuPath, manufacturer, script, credentials, log, token);
    }
    public async Task<string> FindIpAsync()
    {
        var adapters = await VmNetworkService.GetNetworkAdaptersAsync(vmId);
        if (!adapters.HasData) throw new InvalidOperationException(adapters.Error, adapters.Exception);
        string mac = adapters.Data!.FirstOrDefault()?.MacAddress ?? string.Empty;
        if (!string.IsNullOrEmpty(mac))
            for (int i = 0; i < 3; i++)
            {
                var found = await VmIpService.LookupAsync(vmId, mac);
                if (!found.Success) throw new InvalidOperationException(found.Error, found.Exception);
                string ip = found.Data?.FirstOrDefault()?.Address ?? string.Empty;
                if (!string.IsNullOrEmpty(ip)) return ip;
                await Task.Delay(2000);
            }
        return string.Empty;
    }
}
