using ExHyperV.Tools;

namespace ExHyperV.Models;

/// <summary>一项虚拟机设置在当前快照下的修改条件。</summary>
/// <remarks>查询条件不锁定虚拟机状态。修改入口使用同一规则重新检查，最终执行结果由 VMMS 返回。</remarks>
public sealed record VmModificationCondition
{
    /// <summary>是否通过已知的修改条件检查。</summary>
    public bool CanModify { get; }
    /// <summary>不允许修改的原因；允许时为空字符串。</summary>
    public string Reason { get; }
    /// <summary>失败类别；允许时为 None。</summary>
    public ApiErrorKind ErrorKind { get; }
    /// <summary>需要达到的电源状态；无电源状态要求时为 null。</summary>
    public VmState? RequiredState { get; }

    private VmModificationCondition(bool allowed, string reason, ApiErrorKind kind, VmState? requiredState = null)
        => (CanModify, Reason, ErrorKind, RequiredState) = (allowed, reason, kind, requiredState);

    internal static VmModificationCondition Allowed { get; } = new(true, "", ApiErrorKind.None);
    internal static VmModificationCondition Denied(string reason, ApiErrorKind kind = ApiErrorKind.InvalidState)
        => new(false, reason, kind);
    internal static VmModificationCondition Unsupported(string? reason = null)
        => Denied(reason ?? Properties.Resources.Error_VerNotSupport, ApiErrorKind.Unsupported);
    internal static VmModificationCondition PoweredOff(VmState state, string? reason = null) => state == VmState.PoweredOff
        ? Allowed : new(false, reason ?? Properties.Resources.Warn_VmOffRequired, ApiErrorKind.InvalidState, VmState.PoweredOff);
    internal ApiResponse ToResponse() => CanModify ? ApiResponse.Ok() : ApiResponse.Fail(Reason, kind: ErrorKind);
}
