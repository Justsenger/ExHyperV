using ExHyperV.Models;
using ExHyperV.Tools;

namespace ExHyperV.Services;

/// <summary>电源动作及启动修复确认流程的当前阶段。</summary>
public enum VmPowerOperationStage {
        /// <summary>尚未尝试电源动作。</summary>
        Created,
        /// <summary>发现可修复问题，正在等待用户决定。</summary>
        AwaitingConfirmation,
        /// <summary>已完成确认对应的修复，可再次执行电源动作。</summary>
        ReadyToRetry,
        /// <summary>电源动作成功结束。</summary>
        Completed,
        /// <summary>执行或修复失败，流程结束。</summary>
        Failed,
        /// <summary>用户拒绝修复，流程结束。</summary>
        Cancelled }
/// <summary>启动或电源动作失败后提出的修复类型。</summary>
public enum VmPowerRepairKind {
        /// <summary>需要修复 GPU 相关配置。</summary>
        Gpu,
        /// <summary>需要移除失效的物理直通磁盘挂接后重试电源动作。</summary>
        PassthroughDisk }
/// <summary>由 Core 生成的修复确认内容；调用者呈现后提交用户决定。</summary>
/// <param name="Kind">本次需要确认的修复类型。</param>
/// <param name="Title">可供调用者展示的确认标题。</param>
/// <param name="Message">修复原因和拟采取操作的确认正文。</param>
/// <param name="ConfirmText">确认按钮建议文案。</param>
public sealed record VmPowerRepairConfirmation(VmPowerRepairKind Kind, string Title, string Message, string ConfirmText)
{
    /// <summary>本次确认请求的唯一编号，提交决定时原样回传。</summary>
    public Guid RequestId { get; } = Guid.NewGuid();
    /// <summary>本次 GPU 修复清单及每个设备的候选；其他修复类型为空。列表只读，选择通过 SubmitDecisionAsync 提交。</summary>
    public IReadOnlyList<VmGpuRepairService.StaleGpuPartition> GpuTargets { get; internal init; } = Array.Empty<VmGpuRepairService.StaleGpuPartition>();
    /// <summary>是否有 GPU 设备需要明确选卡；为 true 时，单纯批准不能执行修复。</summary>
    public bool RequiresGpuSelection => GpuTargets.Any(target => target.RequiresSelection);
}

/// <summary>单台虚拟机的一次电源动作状态机，包含失败识别、修复确认及重试。</summary>
public sealed class VmPowerOperationSession
{
    private readonly IVmPowerOperationBackend _backend;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string? _unresolvedName;
    private List<VmGpuRepairService.StaleGpuPartition> _gpuTargets = new();
    private List<VmStorageAttachment> _diskTargets = new();

    /// <summary>本次电源操作定位的稳定 GUID；内部名称入口在首次推进时唯一解析。</summary>
    public Guid VmId { get; private set; }
    /// <summary>供确认消息显示的名称，不参与目标定位。</summary>
    public string VmName { get; }
    /// <summary>本次执行的类型化电源动作。</summary>
    public VmPowerAction Action { get; }
    /// <summary>当前电源、查询或修复失败的结构化结果；尚未失败或最终成功时为 null。</summary>
    public ApiResponse? Failure { get; private set; }
    /// <summary>当前流程阶段；终态不再接受继续推进。</summary>
    public VmPowerOperationStage Stage { get; private set; } = VmPowerOperationStage.Created;
    /// <summary>等待用户决定时的确认内容，其他阶段可为 null。</summary>
    public VmPowerRepairConfirmation? Confirmation { get; private set; }
    /// <summary>最终失败或当前电源动作失败的说明。</summary>
    public string Error { get; private set; } = string.Empty;
    /// <summary>修复过程提供的补充说明。</summary>
    public string RepairNotice { get; private set; } = string.Empty;
    /// <summary>是否已有修复步骤失败。</summary>
    public bool RepairFailed { get; private set; }

    /// <summary>为指定虚拟机创建电源与修复流程。</summary>
    /// <param name="vmId">目标虚拟机的非空 GUID；空值在推进时返回失败。</param>
    /// <param name="action">电源动作；未定义值在推进时返回失败。</param>
    /// <param name="displayName">可选显示名称，只用于消息；省略时显示 GUID。</param>
    public VmPowerOperationSession(Guid vmId, VmPowerAction action, string? displayName = null)
        : this(vmId, action, new NativeVmPowerOperationBackend(), displayName) { }

    internal VmPowerOperationSession(Guid vmId, VmPowerAction action, IVmPowerOperationBackend backend, string? displayName = null)
    { VmId = vmId; VmName = displayName ?? vmId.ToString("D"); Action = action; _backend = backend; }

    internal VmPowerOperationSession(string vmName, string action)
        : this(vmName, action, new NativeVmPowerOperationBackend()) { }

    internal VmPowerOperationSession(string vmName, string action, IVmPowerOperationBackend backend)
    {
        VmName = vmName;
        _unresolvedName = vmName;
        Action = VmPowerService.TryParseAction(action, out var parsed) ? parsed : (VmPowerAction)(-1);
        _backend = backend;
    }

    /// <summary>执行初始电源动作，或在修复完成后重试；必要时停在等待用户确认状态。</summary>
    /// <exception cref="InvalidOperationException">当前阶段不可推进，或同一实例已有正在执行的修改。</exception>
    /// <editentry label="电源控制与修复流程" category="电源"/>
    public Task AdvanceAsync() => ChangeAsync(async () =>
    {
        if (!Enum.IsDefined(Action))
        {
            Fail(ApiResponse.Fail("不支持指定的虚拟机电源动作。", kind: ApiErrorKind.InvalidArgument));
            return;
        }
        if (VmId == Guid.Empty)
        {
            if (_unresolvedName == null)
            {
                Fail(ApiResponse.Fail("虚拟机 GUID 不能为空。", kind: ApiErrorKind.InvalidArgument));
                return;
            }
            var identity = await _backend.ResolveIdentityAsync(_unresolvedName);
            if (!identity.HasData)
            {
                Fail(ApiResponse.Fail(identity.Error, identity.Code, identity.ErrorSource, identity.Exception, identity.ErrorKind));
                return;
            }
            VmId = identity.Data;
        }
        bool retry = Stage == VmPowerOperationStage.ReadyToRetry;
        var result = await _backend.ExecuteAsync(VmId, Action);
        if (result.Success) { Stage = VmPowerOperationStage.Completed; Error = string.Empty; Failure = null; return; }
        Failure = result;
        Error = result.Error;
        if (result.ErrorKind == ApiErrorKind.OutcomeUnknown || retry ||
            Action is not (VmPowerAction.Start or VmPowerAction.Restart)) { Stage = VmPowerOperationStage.Failed; return; }
        await PrepareRepairAsync();
    }, stage => stage is VmPowerOperationStage.Created or VmPowerOperationStage.ReadyToRetry);

    /// <summary>提交当前修复确认的决定；批准后执行修复并转为可重试，拒绝则结束为取消。</summary>
    /// <param name="requestId">用户实际确认的 Confirmation.RequestId；不匹配时保留待确认状态，Failure 返回 Conflict，不修复或取消当前请求。</param>
    /// <param name="approve">是否同意当前修复；false 取消会话。</param>
    /// <param name="gpuSelections">待修复设备 Instance GUID 到候选 Id 的映射；每个多候选设备必须提供一项。只提交本次清单内的选择，方法开始时复制映射。</param>
    /// <remarks>选卡缺失或无效时保留 AwaitingConfirmation，通过 Failure 和 Error 说明原因，调用方可以更正后再提交；此时没有执行任何修复。</remarks>
    /// <exception cref="InvalidOperationException">当前不在待确认阶段，或同一实例已有正在执行的修改。</exception>
    public Task SubmitDecisionAsync(Guid requestId, bool approve, IReadOnlyDictionary<Guid, string>? gpuSelections = null)
    {
        var selections = approve ? gpuSelections?.ToDictionary(pair => pair.Key, pair => pair.Value) : null;
        return ChangeAsync(async () =>
        {
            if (Confirmation is null || Confirmation.RequestId != requestId)
            {
                Failure = ApiResponse.Fail("确认请求已变化或不属于本次会话，请重新读取确认内容。", kind: ApiErrorKind.Conflict);
                Error = Failure.Error;
                return;
            }
            if (!approve) { Stage = VmPowerOperationStage.Cancelled; return; }

            if (Confirmation!.Kind == VmPowerRepairKind.Gpu)
            {
                var selection = VmGpuRepairService.SelectRepairTargets(VmId, _gpuTargets, selections);
                if (!selection.HasData)
                {
                    Failure = ApiResponse.Fail(selection.Error, kind: selection.ErrorKind);
                    Error = selection.Error;
                    return; // 保留待确认阶段；调用方可以补充或更正选卡后再次提交。
                }
                Confirmation = Confirmation with { GpuTargets = selection.Data };
                var result = await _backend.RepairGpuAsync(VmId, selection.Data);
                if (!result.HasData)
                {
                    Fail(ApiResponse.Fail(string.IsNullOrEmpty(result.Error) ? Properties.Resources.Gpu_StaleRepairFail : result.Error,
                        result.Code, result.ErrorSource, result.Exception, result.ErrorKind), repairFailed: true);
                    return;
                }
                var repair = result.Data;
                RepairNotice = repair.Rebound > 0 && repair.Removed == 0
                    ? string.Format(Properties.Resources.Gpu_StaleRebound, repair.Rebound)
                    : repair.Removed > 0 && repair.Rebound == 0
                        ? string.Format(Properties.Resources.Gpu_StaleRemovedMsg, repair.Removed)
                        : Properties.Resources.Gpu_StaleRepaired;
            }
            else
            {
                if (selections?.Count > 0)
                {
                    Failure = ApiResponse.Fail("本次不是 GPU 修复，不能提交显卡选择。", kind: ApiErrorKind.InvalidArgument);
                    Error = Failure.Error;
                    return;
                }
                int removed = 0;
                foreach (var disk in _diskTargets)
                {
                    var result = await _backend.RemoveDiskAsync(VmId, disk);
                    if (!result.Success)
                    {
                        Fail(ApiResponse.Fail($"已移除 {removed} 个失效磁盘挂接，但后续移除失败：{result.Error}",
                            result.Code, result.ErrorSource, result.Exception, result.ErrorKind), repairFailed: true);
                        return;
                    }
                    removed++;
                }
                if (removed == 0) { Fail(Properties.Resources.Storage_StaleDiskRemoveFail, repairFailed: true); return; }
                RepairNotice = string.Format(Properties.Resources.Storage_StaleDiskRemoved, removed);
            }
            Failure = null;
            Error = string.Empty;
            Stage = VmPowerOperationStage.ReadyToRetry;
        }, stage => stage == VmPowerOperationStage.AwaitingConfirmation);
    }

    /// <summary>连续推进电源流程；遇到确认时调用 decide，未提供决定回调则停在待确认状态。</summary>
    /// <param name="decide">没有选卡需求时，用此回调批准或拒绝修复；null 或 RequiresGpuSelection 为 true 时停在 AwaitingConfirmation。需要选卡时读取 Confirmation.GpuTargets，再调用 SubmitDecisionAsync 传入选择。</param>
    /// <param name="changed">每次推进后接收当前会话的通知，可为 null；调用者负责界面线程切换。</param>
    /// <remarks>同一实例依次推进和提交决定；每次新的电源操作创建新实例。决定或通知回调抛出的异常直接传播，会话保留当时状态。</remarks>
    public async Task RunAsync(Func<VmPowerRepairConfirmation, Task<bool>>? decide = null,
        Action<VmPowerOperationSession>? changed = null)
    {
        changed?.Invoke(this);
        while (Stage is VmPowerOperationStage.Created or VmPowerOperationStage.AwaitingConfirmation or VmPowerOperationStage.ReadyToRetry)
        {
            if (Stage == VmPowerOperationStage.AwaitingConfirmation)
            {
                if (decide == null || Confirmation!.RequiresGpuSelection) return;
                var confirmation = Confirmation!;
                await SubmitDecisionAsync(confirmation.RequestId, await decide(confirmation));
                if (Stage == VmPowerOperationStage.AwaitingConfirmation)
                {
                    changed?.Invoke(this);
                    return;
                }
            }
            else await AdvanceAsync();
            changed?.Invoke(this);
        }
    }

    private async Task PrepareRepairAsync()
    {
        var gpuQuery = await _backend.FindGpuAsync(VmId);
        if (!gpuQuery.HasData)
        {
            Fail(ApiResponse.Fail("检查 GPU 修复条件失败：" + gpuQuery.Error, gpuQuery.Code,
                gpuQuery.ErrorSource, gpuQuery.Exception, gpuQuery.ErrorKind));
            return;
        }
        var stale = gpuQuery.Data;
        var implicated = stale.Where(s => Error.IndexOf(s.Instance, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
        if (stale.Count > 0 && (Error.IndexOf("GPU Partition", StringComparison.OrdinalIgnoreCase) >= 0 || implicated.Count > 0))
        {
            _gpuTargets = implicated.Count > 0 ? implicated : stale.ToList();
            bool allRebind = _gpuTargets.All(s => s.CanRebind);
            bool needsSelection = _gpuTargets.Any(s => s.RequiresSelection);
            Confirmation = new(VmPowerRepairKind.Gpu,
                allRebind ? Properties.Resources.Gpu_StalePathTitle : Properties.Resources.Gpu_StaleTitle,
                needsSelection
                    ? $"存在多个匹配 GPU。请为清单中需要选择的设备指定显卡；本次修复将重绑 {_gpuTargets.Count(s => s.CanRebind)} 个设备、移除 {_gpuTargets.Count(s => !s.CanRebind)} 个失效设备。"
                    : string.Format(allRebind ? Properties.Resources.Gpu_StalePathMessage : Properties.Resources.Gpu_StaleMessage, VmName),
                needsSelection ? "确认修复" : allRebind ? Properties.Resources.Gpu_StaleRebindConfirm : Properties.Resources.Gpu_StaleRemoveConfirm)
            {
                GpuTargets = Array.AsReadOnly(_gpuTargets.ToArray())
            };
            Stage = VmPowerOperationStage.AwaitingConfirmation;
            return;
        }

        var diskQuery = await _backend.FindDisksAsync(VmId);
        if (!diskQuery.HasData)
        {
            Fail(ApiResponse.Fail("检查磁盘修复条件失败：" + diskQuery.Error, diskQuery.Code,
                diskQuery.ErrorSource, diskQuery.Exception, diskQuery.ErrorKind));
            return;
        }
        _diskTargets = diskQuery.Data;
        if (_diskTargets.Count > 0 && (Error.IndexOf("0x80070103", StringComparison.OrdinalIgnoreCase) >= 0 ||
            Error.IndexOf("failed to open", StringComparison.OrdinalIgnoreCase) >= 0))
        {
            string names = string.Join(", ", _diskTargets.Select(d => !string.IsNullOrEmpty(d.DiskModel) ? d.DiskModel
                : d.DiskNumber >= 0 ? string.Format(Properties.Resources.Storage_PhysicalDiskNumbered, d.DiskNumber)
                : Properties.Resources.Storage_PhysicalDisk));
            Confirmation = new(VmPowerRepairKind.PassthroughDisk, Properties.Resources.Storage_StaleDiskTitle,
                string.Format(Properties.Resources.Storage_StaleDiskMessage, VmName, names), Properties.Resources.Storage_StaleDiskConfirm);
            Stage = VmPowerOperationStage.AwaitingConfirmation;
            return;
        }
        Stage = VmPowerOperationStage.Failed;
    }

    private async Task ChangeAsync(Func<Task> change, Func<VmPowerOperationStage, bool> allowed)
    {
        if (!await _gate.WaitAsync(0)) throw new InvalidOperationException("A power operation is already in progress.");
        try
        {
            if (!allowed(Stage)) throw new InvalidOperationException("The power operation is not ready for this step.");
            try { await change(); }
            catch (Exception error) { Fail(error.GetBaseException().Message); }
        }
        finally { _gate.Release(); }
    }

    private void Fail(string error, bool repairFailed = false)
        => Fail(ApiResponse.Fail(error, kind: ApiErrorKind.OperationFailed), repairFailed);

    private void Fail(ApiResponse failure, bool repairFailed = false)
    { Failure = failure; Error = failure.Error; RepairFailed = repairFailed; Stage = VmPowerOperationStage.Failed; }
}

internal interface IVmPowerOperationBackend
{
    Task<ApiResponse<Guid>> ResolveIdentityAsync(string vmName) => VmIdentity.ResolveAsync(vmName);
    Task<ApiResponse> ExecuteAsync(Guid vmId, VmPowerAction action);
    Task<ApiResponse<IReadOnlyList<VmGpuRepairService.StaleGpuPartition>>> FindGpuAsync(Guid vmId);
    Task<ApiResponse<VmGpuRepairService.RepairResult>> RepairGpuAsync(Guid vmId, IEnumerable<VmGpuRepairService.StaleGpuPartition> targets);
    Task<ApiResponse<List<VmStorageAttachment>>> FindDisksAsync(Guid vmId);
    Task<ApiResponse> RemoveDiskAsync(Guid vmId, VmStorageAttachment disk);
}

internal sealed class NativeVmPowerOperationBackend : IVmPowerOperationBackend
{
    public Task<ApiResponse> ExecuteAsync(Guid vmId, VmPowerAction action) => VmPowerService.ExecuteControlActionAsync(vmId, action);
    public Task<ApiResponse<IReadOnlyList<VmGpuRepairService.StaleGpuPartition>>> FindGpuAsync(Guid vmId) => VmGpuRepairService.FindStaleGpuPartitionsAsync(vmId);
    public Task<ApiResponse<VmGpuRepairService.RepairResult>> RepairGpuAsync(Guid vmId, IEnumerable<VmGpuRepairService.StaleGpuPartition> targets) => VmGpuRepairService.RepairAsync(vmId, targets);
    public Task<ApiResponse<List<VmStorageAttachment>>> FindDisksAsync(Guid vmId) => VmStorageService.FindStalePassthroughDisksAsync(vmId);
    public Task<ApiResponse> RemoveDiskAsync(Guid vmId, VmStorageAttachment disk) => VmStorageService.RemoveDriveAsync(vmId, disk);
}
