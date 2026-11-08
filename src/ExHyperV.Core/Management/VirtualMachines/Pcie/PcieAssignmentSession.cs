using ExHyperV.Models;
using ExHyperV.Tools;

namespace ExHyperV.Services;

/// <summary>PCIe 分配流程当前执行或确认步骤。</summary>
public enum PcieAssignmentStep
{
    /// <summary>尚未推进的起始状态。</summary>
    Ready,
    /// <summary>等待确认是否移出宿主最后一块 GPU。</summary>
    ConfirmLastGpu,
    /// <summary>等待确认将控制器关联的数据盘脱机。</summary>
    ConfirmOfflineDisks,
    /// <summary>等待选择是否连同同卡伴随设备一起分配。</summary>
    ConfirmCompanions,
    /// <summary>主分配流程完成。</summary>
    Completed,
    /// <summary>用户取消了需要确认的操作。</summary>
    Cancelled,
    /// <summary>检查或执行失败，可能已经产生部分变更。</summary>
    Failed,
    /// <summary>等待确认关闭目标虚拟机、按需启停探测并修改 MMIO 配置。</summary>
    ConfirmMmio
}
/// <summary>PCIe 分配流程失败所在的阶段；具体细节见状态消息。</summary>
public enum PcieAssignmentError
{
    /// <summary>没有记录失败阶段。</summary>
    None,
    /// <summary>无法检查 MMIO 配置。</summary>
    MmioCheck,
    /// <summary>MMIO 扩展未成功。</summary>
    MmioUpdate,
    /// <summary>存储控制器关联系统或启动磁盘，禁止继续。</summary>
    SystemDisk,
    /// <summary>设备转移或伴随操作失败。</summary>
    Execution
}

/// <summary>对一次特定 PCIe 确认请求的决定，不能重用到其他步骤。</summary>
/// <param name="RequestId">从待确认状态读取的请求身份。</param>
/// <param name="Approve">是否同意该请求；伴随设备步骤中拒绝仅跳过伴随设备。</param>
public sealed record PcieAssignmentDecision(Guid RequestId, bool Approve);

/// <summary>PCIe 分配流程的不可变状态快照。</summary>
/// <param name="Step">当前步骤。</param>
/// <param name="Title">适合展示的本地化标题。</param>
/// <param name="Message">本地化说明或失败详情。</param>
/// <param name="Error">失败阶段；正常状态为 None。</param>
/// <param name="RefreshRequired">是否应重新读取设备/VM 状态；取消或失败也可能要求刷新。</param>
/// <param name="RequestId">当前确认请求的唯一身份；非待确认状态为 null。</param>
public sealed record PcieAssignmentState(
    PcieAssignmentStep Step, string Title = "", string Message = "",
    PcieAssignmentError Error = PcieAssignmentError.None, bool RefreshRequired = false, Guid? RequestId = null)
{
    /// <summary>本次流程已执行步骤的只读快照；ResourceId 区分主设备、伴随设备和磁盘，Failure 保留底层类别、来源及错误码。</summary>
    /// <remarks>步骤名为 CheckMmio、ExpandMmio、ReadControllerDisks、AssignDevice、ReadReturnedDisks、RestoreDiskOnline 或 UnhandledFailure。未执行的步骤不列出，不以空列表推断没有副作用。</remarks>
    public IReadOnlyList<OperationStepResult> Steps { get; init; } = Array.Empty<OperationStepResult>();
    /// <summary>需要优先处理的失败；结果未知优先于清理失败，其余取首个失败。全部失败详情仍保留在 Steps 中。</summary>
    public ApiFailure? Failure { get; init; }
    /// <summary>任一步骤的底层结果尚未确认；不得直接重试分配或反向清理，应先核实实际状态。</summary>
    public bool OutcomeUnknown => Failure?.Kind == ApiErrorKind.OutcomeUnknown || Steps.Any(s => s.State == OperationStepState.OutcomeUnknown);
    /// <summary>当前是否正在等待调用方提交用户决定。</summary>
    public bool RequiresDecision => Step is PcieAssignmentStep.ConfirmLastGpu or
        PcieAssignmentStep.ConfirmOfflineDisks or PcieAssignmentStep.ConfirmCompanions or PcieAssignmentStep.ConfirmMmio;
    /// <summary>当前是否已完成、取消或失败，不能再推进业务步骤。</summary>
    public bool IsFinished => Step is PcieAssignmentStep.Completed or PcieAssignmentStep.Cancelled or PcieAssignmentStep.Failed;
}

/// <summary>保存一次 PCIe 分配的检查、确认和执行状态，供界面或其他调用方逐步驱动。</summary>
/// <remarks>同一实例不能并发推进。创建实例不执行分配；推进可修改 MMIO、关闭 VM、脱机磁盘和转移设备，不是可整体回滚的事务。等待用户确认时不持有宿主资源锁；获准执行时，在控制器及关联磁盘锁内重新校验设备身份和系统盘标志，再连续执行脱机、转移及失败恢复。失败恢复以本次执行前的联机、只读状态为准，不保留期间外部状态修改；不能确认同一设备时停止恢复并报告失败。</remarks>
public sealed class PcieAssignmentSession
{
    private readonly PcieDeviceInfo _device;
    private readonly PcieDeviceInfo[] _devices;
    private readonly string _target;
    private readonly string _targetDisplay;
    private readonly PcieAssignmentBackend _backend;
    private List<ControllerDisk> _controllerDisks = [];
    private List<ControllerDisk> _onlineDisks = [];
    private List<PcieDeviceInfo> _companions = [];
    private int _phase;
    private int _advancing;
    private bool _refreshRequired;
    private bool _offlineApproved;
    private readonly List<OperationStepResult> _steps = [];

    /// <summary>最近一次推进产生的状态快照。</summary>
    public PcieAssignmentState State { get; private set; } = new(PcieAssignmentStep.Ready);

    /// <summary>为指定设备及目标创建分配流程，复制当前设备列表用于规划确认。</summary>
    /// <param name="device">选中的设备及当前归属。</param>
    /// <param name="devices">用于识别伴随设备、最后一块 GPU 等条件的当前设备快照。</param>
    /// <param name="target">目标宿主或虚拟机；未分配状态不能作为目标。</param>
    /// <exception cref="ArgumentNullException">目标为 null。</exception>
    /// <exception cref="ArgumentException">目标为 Unassigned。</exception>
    public PcieAssignmentSession(PcieDeviceInfo device, IEnumerable<PcieDeviceInfo> devices, PcieAssignmentTarget target)
        : this(device, devices, HostPcieService.GetAssignmentTargetKey(target), PcieAssignmentBackend.System, target.VirtualMachineName) { }

    internal PcieAssignmentSession(PcieDeviceInfo device, IEnumerable<PcieDeviceInfo> devices, string target, PcieAssignmentBackend backend, string? displayName = null)
    {
        _device = Snapshot(device);
        _devices = devices.Select(Snapshot).ToArray();
        _target = HostPcieService.NormalizeAssignmentTarget(target);
        var valid = HostPcieService.ValidateAssignmentKeys(_target, _device.Status);
        if (!valid.Success) throw new ArgumentException(valid.Error, nameof(target));
        _targetDisplay = displayName ?? HostPcieService.GetAssignmentDisplayName(_target);
        _backend = backend;
    }

    private static PcieDeviceInfo Snapshot(PcieDeviceInfo device) => new()
    {
        FriendlyName = device.FriendlyName, ClassType = device.ClassType,
        InstanceId = device.InstanceId, Path = device.Path,
        Assignment = device.Assignment
    };

    /// <summary>推进到下一次需要确认或结束的位置，必要时执行已获决定的操作。</summary>
    /// <remarks>MMIO 准备仅在本步骤获准后执行，可能强制关闭 VM、启停探测并修改配置；之后取消其他步骤不会撤销已完成的准备。拒绝过期或重复决定不会改变当前状态。取消或失败后按 RefreshRequired 重新查询实际状态，并检查 Steps、Failure 和 OutcomeUnknown。任一设备分配结果未知时，不再执行后续伴随设备。</remarks>
    /// <returns>最新状态；已结束时仍返回原状态，等待决定但未传 decision 时保持等待。</returns>
    /// <param name="decision">包含当前 RequestId 的用户决定；ConfirmCompanions 的拒绝只跳过伴随设备，其他拒绝会取消流程。启动或只读取等待状态时传 null。</param>
    /// <example><code>using System;
    /// using System.Collections.Generic;
    /// using System.Threading.Tasks;
    /// using ExHyperV.Models;
    /// using ExHyperV.Services;
    ///
    /// public static class PcieAssignmentExample
    /// {
    ///     // decide 由宿主提供：展示当前状态并取得用户决定，不能默认全部同意。
    ///     // 该方法会执行实际分配，调用前须具备权限并告知用户可能强制关闭 VM。
    ///     public static async Task&lt;PcieAssignmentState&gt; AssignAsync(
    ///         PcieDeviceInfo device,
    ///         IEnumerable&lt;PcieDeviceInfo&gt; snapshot,
    ///         PcieAssignmentTarget target,
    ///         Func&lt;PcieAssignmentState, Task&lt;bool&gt;&gt; decide)
    ///     {
    ///         var session = new PcieAssignmentSession(device, snapshot, target);
    ///         while (!session.State.IsFinished)
    ///         {
    ///             var pending = session.State;
    ///             PcieAssignmentDecision? answer = pending.RequiresDecision
    ///                 ? new(pending.RequestId!.Value, await decide(pending))
    ///                 : null;
    ///             await session.AdvanceAsync(answer);
    ///         }
    ///         // 调用方仍需按 Error 和 RefreshRequired 处理失败及部分变更。
    ///         return session.State;
    ///     }
    /// }</code></example>
    /// <exception cref="InvalidOperationException">同一实例上另一次推进尚未完成，或决定不属于当前待确认请求。</exception>
    /// <editentry label="PCIe 设备分配与归还" category="PCIe"/>
    public async Task<PcieAssignmentState> AdvanceAsync(PcieAssignmentDecision? decision = null)
    {
        if (Interlocked.Exchange(ref _advancing, 1) != 0)
            throw new InvalidOperationException("A PCIe assignment step is already running.");
        try
        {
            if (decision != null && (!State.RequiresDecision || State.RequestId != decision.RequestId))
                throw new InvalidOperationException("决定不属于当前待确认请求，可能已经过期或提交过。");
            if (State.IsFinished || (State.RequiresDecision && decision == null)) return State;
            try
            {
                if (State.RequiresDecision)
                {
                    var approvedStep = State.Step;
                    Publish(new(PcieAssignmentStep.Ready, RefreshRequired: _refreshRequired));
                    if (approvedStep == PcieAssignmentStep.ConfirmCompanions)
                    {
                        if (!decision!.Approve) _companions.Clear();
                    }
                    else if (!decision!.Approve)
                        return Publish(new(PcieAssignmentStep.Cancelled, RefreshRequired: _refreshRequired));
                    if (approvedStep == PcieAssignmentStep.ConfirmMmio)
                    {
                        _refreshRequired = true;
                        var expanded = await _backend.ExpandMmio(Guid.Parse(_target));
                        Record("ExpandMmio", _target, expanded);
                        if (!expanded.Success)
                            return Fail(PcieAssignmentError.MmioUpdate, expanded.Error);
                    }
                    if (approvedStep == PcieAssignmentStep.ConfirmOfflineDisks)
                        _offlineApproved = true;
                }

                while (true)
                {
                    switch (_phase++)
                    {
                        case 0:
                            if (string.Equals(_device.Status, _target, StringComparison.OrdinalIgnoreCase))
                                return Publish(new(PcieAssignmentStep.Completed));
                            if (_target != HostPcieService.HostKey)
                            {
                                var mmio = await _backend.CheckMmio(Guid.Parse(_target));
                                Record("CheckMmio", _target, QueryResult(mmio));
                                if (!mmio.HasData)
                                    return Fail(PcieAssignmentError.MmioCheck, mmio.Error);
                                if (mmio.Data == MmioCheckResultType.NeedsExpansion)
                                    return Ask(PcieAssignmentStep.ConfirmMmio, Properties.Resources.PCIePage_Title_MmioSpaceTooSmall,
                                        string.Format(Properties.Resources.PCIePage_Msg_ConfirmMmioUpdate, _targetDisplay));
                            }
                            break;
                        case 1:
                            if (LeavingHost && IsDisplay(_device) && _devices.Count(d => IsDisplay(d) && d.Status == HostPcieService.HostKey) <= 1)
                                return Ask(PcieAssignmentStep.ConfirmLastGpu,
                                    Properties.Resources.PCIePage_Title_LastGpuWarning,
                                    string.Format(Properties.Resources.PCIePage_Msg_LastGpuWarning, _device.FriendlyName));
                            break;
                        case 2:
                            if (LeavingHost && IsStorage(_device))
                            {
                                var disks = await _backend.GetDisks(_device.InstanceId);
                                Record("ReadControllerDisks", _device.InstanceId, QueryResult(disks));
                                if (!disks.HasData) return Fail(PcieAssignmentError.Execution, disks.Error);
                                var critical = disks.Data.Where(d => d.IsSystem || d.IsBoot).ToList();
                                if (critical.Count > 0)
                                    return Fail(PcieAssignmentError.SystemDisk,
                                        string.Format(Properties.Resources.PCIePage_Msg_StorageHasSystemDisk, DiskList(critical)));
                                _controllerDisks = disks.Data.ToList();
                                _onlineDisks = _controllerDisks.Where(d => !d.IsOffline).ToList();
                                if (_onlineDisks.Count > 0)
                                    return Ask(PcieAssignmentStep.ConfirmOfflineDisks,
                                        Properties.Resources.PCIePage_Title_StorageOffline,
                                        string.Format(Properties.Resources.PCIePage_Msg_StorageOffline, DiskList(_onlineDisks)));
                            }
                            break;
                        case 3:
                            if (IsDisplay(_device))
                            {
                                string cardKey = HostPcieService.CardKey(_device.Path);
                                if (!string.IsNullOrEmpty(cardKey))
                                    _companions = _devices.Where(d =>
                                        !string.Equals(d.InstanceId, _device.InstanceId, StringComparison.OrdinalIgnoreCase) &&
                                        d.Status == _device.Status && !string.IsNullOrEmpty(d.Path) &&
                                        HostPcieService.CardKey(d.Path) == cardKey).ToList();
                                if (_companions.Count > 0)
                                    return Ask(PcieAssignmentStep.ConfirmCompanions,
                                        Properties.Resources.PCIePage_Title_CompanionDevice,
                                        string.Format(Properties.Resources.PCIePage_Msg_CompanionDevice, _device.FriendlyName,
                                            string.Join("\n\n", _companions.Select(d => "• " + d.FriendlyName)),
                                            _targetDisplay));
                            }
                            break;
                        default:
                            return await ExecuteAsync();
                    }
                }
            }
            catch (Exception error)
            {
                // 已开始的业务步骤不可重放；错误的确认身份在进入此区间前就被拒绝。
                var failure = ApiFailure.From(error);
                Record("UnhandledFailure", _device.InstanceId, ApiResponse.Fail(failure.Message,
                    failure.Code, failure.Source, error, failure.Kind));
                Fail(PcieAssignmentError.Execution, error.Message);
                throw;
            }
        }
        finally { Volatile.Write(ref _advancing, 0); }
    }

    private async Task<PcieAssignmentState> ExecuteAsync()
    {
        _refreshRequired = true;
        var errors = new List<string>();
        var result = LeavingHost && IsStorage(_device)
            ? await ExecuteStorageAsync()
            : await ExecuteDeviceAsync(_device);
        Record("AssignDevice", _device.InstanceId, result);
        bool success = result.Success;
        if (!success)
        {
            errors.Add($"{_device.FriendlyName}: {result.Error}");
        }
        else
        {
            foreach (var companion in _companions)
            {
                var companionResult = await ExecuteDeviceAsync(companion);
                Record("AssignDevice", companion.InstanceId, companionResult);
                if (!companionResult.Success)
                    errors.Add($"{companion.FriendlyName}: {companionResult.Error}");
                if (companionResult.ErrorKind == ApiErrorKind.OutcomeUnknown) break;
            }
        }

        if (success && _target == HostPcieService.HostKey && IsStorage(_device))
        {
            List<ControllerDisk> disks = [];
            for (int i = 0; i < 4 && disks.Count == 0; i++)
            {
                await _backend.Delay(1500);
                var diskResponse = await _backend.GetDisks(_device.InstanceId);
                Record("ReadReturnedDisks", _device.InstanceId, QueryResult(diskResponse));
                if (!diskResponse.HasData) { errors.Add(diskResponse.Error); break; }
                disks = diskResponse.Data;
            }
            foreach (var disk in disks.Where(d => d.IsOffline && !d.IsSystem && !d.IsBoot))
            {
                var restored = _backend.OnlineReturnedDisk is { } online
                    ? await online(_device.InstanceId, disk)
                    : await _backend.SetDiskOffline(disk.Number, false);
                Record("RestoreDiskOnline", disk.Number.ToString(System.Globalization.CultureInfo.InvariantCulture), restored);
                if (!restored.Success) errors.Add($"磁盘 #{disk.Number} 恢复联机失败：{restored.Error}");
                if (restored.ErrorKind == ApiErrorKind.OutcomeUnknown) break;
            }
        }

        return errors.Count > 0
            ? Fail(PcieAssignmentError.Execution,
                string.Format(Properties.Resources.PCIePage_Error_ExecutionGeneric, string.Join("\n", errors)), true)
            : Publish(new(PcieAssignmentStep.Completed, RefreshRequired: true));
    }

    private Task<ApiResponse> ExecuteDeviceAsync(PcieDeviceInfo device) =>
        _backend.Execute(_target, device.Status, device.InstanceId, device.Path);

    private async Task<ApiResponse> ExecuteStorageAsync()
    {
        if (_backend.ExecuteStorage is { } execute)
            return await execute(_target, _device.Status, _device.InstanceId, _device.Path, _controllerDisks, _offlineApproved);

        // 保留既有测试替身构造约定；真实系统始终使用覆盖整个操作的协调器。
        var changed = new List<ControllerDisk>();
        ApiResponse outcome;
        try
        {
            outcome = ApiResponse.Ok();
            foreach (var disk in _onlineDisks)
            {
                changed.Add(disk); // 写入也可能先产生副作用再报告失败。
                outcome = await _backend.SetDiskOffline(disk.Number, true);
                if (!outcome.Success) break;
            }
            if (outcome.Success) outcome = await ExecuteDeviceAsync(_device);
        }
        catch (Exception error) { outcome = ApiResponse.Fail(error.Message, exception: error, kind: ApiErrorKind.OperationFailed); }
        if (outcome.Success || outcome.ErrorKind == ApiErrorKind.OutcomeUnknown) return outcome;
        var cleanupErrors = new List<string>();
        foreach (var disk in changed)
        {
            try
            {
                var restored = await _backend.SetDiskOffline(disk.Number, disk.IsOffline);
                if (!restored.Success) cleanupErrors.Add($"磁盘 #{disk.Number} 恢复失败：{restored.Error}");
            }
            catch (Exception error) { cleanupErrors.Add($"磁盘 #{disk.Number} 恢复失败：{error.Message}"); }
        }
        return cleanupErrors.Count == 0 ? outcome : ApiResponse.Fail(
            outcome.Error + "\n" + string.Join("\n", cleanupErrors), exception: outcome.Exception, kind: ApiErrorKind.CleanupRequired);
    }
    private bool LeavingHost => _target != HostPcieService.HostKey && _device.Status == HostPcieService.HostKey;
    private static bool IsDisplay(PcieDeviceInfo d) => string.Equals(d.ClassType, "Display", StringComparison.OrdinalIgnoreCase);
    private static bool IsStorage(PcieDeviceInfo d) => string.Equals(d.ClassType, "SCSIAdapter", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(d.ClassType, "HDC", StringComparison.OrdinalIgnoreCase);
    private static string DiskList(IEnumerable<ControllerDisk> disks) => string.Join("\n", disks.Select(d => $"• #{d.Number} {d.FriendlyName}"));
    private PcieAssignmentState Ask(PcieAssignmentStep step, string title, string message) =>
        Publish(new(step, title, message, RefreshRequired: _refreshRequired, RequestId: Guid.NewGuid()));
    private PcieAssignmentState Fail(PcieAssignmentError error, string message, bool refresh = false) => Publish(new(
        PcieAssignmentStep.Failed, error == PcieAssignmentError.Execution ? Properties.Resources.Dialog_Title_OperationFailed : Properties.Resources.Error_Title,
        message, error, refresh || _refreshRequired));

    private void Record(string name, string resourceId, ApiResponse result) =>
        _steps.Add(OperationStepResult.From(name, result) with { ResourceId = resourceId });

    private static ApiResponse QueryResult<T>(ApiResponse<T> result) => result.HasData ? ApiResponse.Ok()
        : ApiResponse.Fail(result.Success ? "查询没有提供所需数据。" : result.Error, result.Code,
            result.ErrorSource, result.Exception, result.Success ? ApiErrorKind.NotFound : result.ErrorKind);

    private PcieAssignmentState Publish(PcieAssignmentState state)
    {
        var failures = _steps.Where(s => s.Failure != null).Select(s => s.Failure!).ToArray();
        return State = state with
        {
            Steps = Array.AsReadOnly(_steps.ToArray()),
            Failure = failures.FirstOrDefault(f => f.Kind == ApiErrorKind.OutcomeUnknown)
                ?? failures.FirstOrDefault(f => f.Kind == ApiErrorKind.CleanupRequired)
                ?? failures.FirstOrDefault()
                ?? (state.Step == PcieAssignmentStep.Failed ? new ApiFailure(ApiErrorKind.InvalidState, -1, ApiErrorSource.None, state.Message) : null)
        };
    }
}

internal sealed record PcieAssignmentBackend(
    Func<Guid, Task<ApiResponse<MmioCheckResultType>>> CheckMmio,
    Func<Guid, Task<ApiResponse>> ExpandMmio,
    Func<string, Task<ApiResponse<List<ControllerDisk>>>> GetDisks,
    Func<int, bool, Task<ApiResponse>> SetDiskOffline,
    Func<string, string, string, string, Task<ApiResponse>> Execute,
    Func<int, Task> Delay)
{
    internal Func<string, string, string, string, IReadOnlyList<ControllerDisk>, bool, Task<ApiResponse>>? ExecuteStorage { get; init; }
    internal Func<string, ControllerDisk, Task<ApiResponse>>? OnlineReturnedDisk { get; init; }

    internal static PcieAssignmentBackend System { get; } = new(
        VmMmioService.CheckMmioSpaceAsync, VmMmioService.UpdateMmioSpaceAsync,
        HostPcieService.GetControllerDisksAsync, HostDiskService.SetDiskOfflineStatusAsync,
        (target, current, id, path) => HostPcieService.ExecutePCIeOperationAsync(target, current, id, path),
        milliseconds => Task.Delay(milliseconds))
    {
        ExecuteStorage = HostPcieService.ExecuteStorageAssignmentAsync,
        OnlineReturnedDisk = HostPcieService.OnlineReturnedControllerDiskAsync
    };
}
