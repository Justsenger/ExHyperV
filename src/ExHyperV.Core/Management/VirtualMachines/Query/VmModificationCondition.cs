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
    /// <summary>语言无关的前置条件原因码。</summary>
    public ConditionReasonCode ReasonCode { get; }
    /// <summary>配合 ReasonCode 使用的可选参数。</summary>
    public IReadOnlyList<string> ReasonArgs { get; }

    private VmModificationCondition(
        bool allowed,
        string reason,
        ApiErrorKind kind,
        VmState? requiredState = null,
        ConditionReasonCode reasonCode = ConditionReasonCode.None,
        IReadOnlyList<string>? reasonArgs = null)
        => (CanModify, Reason, ErrorKind, RequiredState, ReasonCode, ReasonArgs)
            = (allowed, reason, kind, requiredState, reasonCode, reasonArgs ?? Array.Empty<string>());

    internal static VmModificationCondition Allowed { get; } = new(true, "", ApiErrorKind.None);

    internal static VmModificationCondition Denied(
        string reason,
        ApiErrorKind kind = ApiErrorKind.InvalidState,
        ConditionReasonCode reasonCode = ConditionReasonCode.None,
        params string[] args)
        => new(false, reason, kind, reasonCode: reasonCode, reasonArgs: Array.AsReadOnly(args ?? Array.Empty<string>()));

    internal static VmModificationCondition Unsupported(
        string? reason = null,
        ConditionReasonCode reasonCode = ConditionReasonCode.UnsupportedVmVersion)
        => Denied(reason ?? Properties.Resources.Error_VerNotSupport, ApiErrorKind.Unsupported, reasonCode);

    internal static VmModificationCondition MissingOnHost()
        => Denied("Not provided on this host.", ApiErrorKind.Unsupported, ConditionReasonCode.HostSettingMissing);

    internal static VmModificationCondition RequiresX64()
        => Denied("Requires an x64 processor.", ApiErrorKind.Unsupported, ConditionReasonCode.RequiresX64);

    internal static VmModificationCondition RequiresArm64()
        => Denied("Requires an ARM64 processor.", ApiErrorKind.Unsupported, ConditionReasonCode.RequiresArm64);

    internal static VmModificationCondition RequiresIntel()
        => Denied("Requires an Intel processor.", ApiErrorKind.Unsupported, ConditionReasonCode.RequiresIntel);

    internal static VmModificationCondition RequiresHardwareIsolation()
        => Denied("Host lacks hardware isolation support.", ApiErrorKind.Unsupported, ConditionReasonCode.RequiresHardwareIsolation);

    internal static VmModificationCondition RequiresPerfmonPmu()
        => Denied(Properties.Resources.Cpu_PerfmonPmuRequired, ApiErrorKind.InvalidArgument, ConditionReasonCode.RequiresPerfmonPmu);

    internal static VmModificationCondition PoweredOff(
        VmState state,
        string? reason = null,
        ConditionReasonCode reasonCode = ConditionReasonCode.PoweredOffRequired)
        => state == VmState.PoweredOff
            ? Allowed
            : new(false, reason ?? Properties.Resources.Warn_VmOffRequired, ApiErrorKind.InvalidState, VmState.PoweredOff, reasonCode);

    internal ApiResponse ToResponse() => CanModify ? ApiResponse.Ok() : ApiResponse.Fail(Reason, kind: ErrorKind);
}
