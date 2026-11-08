using System.Management;

namespace ExHyperV.Tools;

/// <summary>可序列化的失败信息；分类和原始错误码用于程序判断，Message 用于显示。</summary>
/// <param name="Kind">业务错误类别。</param>
/// <param name="Code">原始接口错误码；没有原始码时为 -1。</param>
/// <param name="Source">错误码所属接口。</param>
/// <param name="Message">面向人的说明，不作为稳定匹配条件。</param>
public sealed record ApiFailure(ApiErrorKind Kind, int Code, ApiErrorSource Source, string Message)
{
    internal static ApiFailure? From(ApiResponse result) => result.Success ? null : new(result.ErrorKind, result.Code, result.ErrorSource, result.Error);
    internal static ApiFailure? From<T>(ApiResponse<T> result) => result.Success ? null : new(result.ErrorKind, result.Code, result.ErrorSource, result.Error);
    internal static ApiFailure From(Exception error) => error is CoreOperationException operation
        ? From(operation.Result)!
        : new(error is OperationCanceledException ? ApiErrorKind.Cancelled : error is UnauthorizedAccessException or System.ComponentModel.Win32Exception { NativeErrorCode: 5 } ? ApiErrorKind.AccessDenied
            : error is ArgumentException ? ApiErrorKind.InvalidArgument : ApiErrorKind.OperationFailed,
            error is ManagementException management ? (int)management.ErrorCode : error is System.ComponentModel.Win32Exception native ? native.NativeErrorCode : error is UnauthorizedAccessException ? 5 : error.HResult,
            error is ManagementException ? ApiErrorSource.Wmi : error is UnauthorizedAccessException or System.ComponentModel.Win32Exception ? ApiErrorSource.Win32 : ApiErrorSource.None, error.Message);
}

/// <summary>一个修改步骤的执行情况，区分未执行、明确结束和结果不明。</summary>
public enum OperationStepState
{
    /// <summary>未选择或尚未开始此步骤。</summary>
    NotStarted,
    /// <summary>已确认成功完成。</summary>
    Succeeded,
    /// <summary>已报告失败，之前步骤的副作用不因此撤销。</summary>
    Failed,
    /// <summary>请求可能仍在执行，不能据此重试或回滚。</summary>
    OutcomeUnknown
}

/// <summary>宿主操作已确认的重启原因；仅说明需要重启，不会触发重启。</summary>
public enum RestartReason
{
    /// <summary>本机 Windows 组件包登记返回了重启要求。</summary>
    PackageRegistration,
    /// <summary>Windows 可选功能操作或其映像会话收尾返回了重启要求。</summary>
    WindowsFeatureChange,
    /// <summary>已确认的启动配置尚需通过重启采用。</summary>
    BootConfiguration
}

/// <summary>组合操作中一个步骤的结果。</summary>
/// <param name="Name">稳定的步骤标识，具体值见使用此类型的接口说明。</param>
/// <param name="State">执行状态。</param>
/// <param name="Failure">失败详情；未执行或成功时为 null。</param>
public sealed record OperationStepResult(string Name, OperationStepState State, ApiFailure? Failure = null)
{
    /// <summary>本步骤针对的资源标识；同一步骤处理多个资源时用于区分。未提供时为 null。</summary>
    public string? ResourceId { get; init; }
    /// <summary>本步骤是否明确报告需要重启；步骤失败也可能保留此前已完成修改的重启要求。</summary>
    public bool RequiresRestart { get; internal init; }
    /// <summary>本步骤已确认的重启原因；只读且去重。旧入口只有布尔标记时可能为空。</summary>
    public IReadOnlyList<RestartReason> RestartReasons { get; internal init; } = Array.Empty<RestartReason>();
    internal static OperationStepResult From(string name, ApiResponse result) => new(name,
        result.Success ? OperationStepState.Succeeded : result.ErrorKind == ApiErrorKind.OutcomeUnknown ? OperationStepState.OutcomeUnknown : OperationStepState.Failed,
        ApiFailure.From(result))
    {
        RequiresRestart = result.RequiresRestart,
        RestartReasons = Array.AsReadOnly(result.RestartReasons.ToArray())
    };
}
