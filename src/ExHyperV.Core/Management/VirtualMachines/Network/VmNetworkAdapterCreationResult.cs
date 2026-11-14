using ExHyperV.Tools;

namespace ExHyperV.Services;

/// <summary>本次添加网卡在返回时已确认的资源状态。</summary>
public enum VmNetworkAdapterCreationState
{
    /// <summary>未创建网卡，包括提交前失败或底层明确拒绝创建。</summary>
    NotCreated,
    /// <summary>已确认本次网卡仍存在；失败时查看清理错误。</summary>
    Present,
    /// <summary>曾创建网卡，但失败后的自动撤销已确认将其移除。</summary>
    Removed,
    /// <summary>无法确认创建、撤销或资源归属；不得据此直接重试添加。</summary>
    Unknown,
}

/// <summary>本次网卡连接资源初始化步骤的执行结果。</summary>
public enum VmNetworkAdapterInitializationState
{
    /// <summary>尚未提交连接资源初始化。</summary>
    NotStarted,
    /// <summary>连接资源初始化已完成。</summary>
    Completed,
    /// <summary>初始化已确认失败，网卡可能已自动撤销。</summary>
    Failed,
    /// <summary>初始化结果尚未确认，底层任务可能仍在执行。</summary>
    Unknown,
}

/// <summary>添加网卡的结果，分别报告初始化、自动撤销和剩余资源。</summary>
/// <param name="Success">网卡及连接资源是否均已确认创建成功。</param>
/// <param name="DeviceId">已确认属于本次创建的网卡 GUID；撤销后仍保留历史身份，无法唯一确认时为 null。</param>
/// <param name="AdapterState">返回时已确认的网卡资源状态。</param>
/// <param name="InitializationState">初始化步骤的执行结果。</param>
/// <param name="RetainedDeviceIds">本次创建后保留或待确认删除的网卡 GUID；归属未知时列表可能为空，须结合 Unknown 状态判断。</param>
/// <param name="Error">原始创建或初始化失败说明；成功时为 null。</param>
/// <param name="CleanupError">自动撤销失败或因结果不确定而保留资源的说明；不覆盖原始错误。</param>
/// <param name="ErrorKind">原始操作的错误类别；撤销问题另见 CleanupError 和 AdapterState。</param>
/// <remarks>失败且任务已结束时，撤销已核实属于本次创建的资源；任务状态、归属或配置待确认时保留现场。</remarks>
public sealed record VmNetworkAdapterCreationResult(
    bool Success, Guid? DeviceId, VmNetworkAdapterCreationState AdapterState,
    VmNetworkAdapterInitializationState InitializationState, IReadOnlyList<Guid> RetainedDeviceIds,
    string? Error = null, string? CleanupError = null, ApiErrorKind ErrorKind = ApiErrorKind.None);
