using ExHyperV.Tools;

namespace ExHyperV.Services;

/// <summary>删除的实际完成情况；注销状态、恢复配置与文件清理分别报告。</summary>
/// <param name="Success">所请求的删除流程是否完成。</param>
/// <param name="RegistrationState">结束时已确认的注册状态；Unknown 不应解释为仍在册。</param>
/// <param name="RecoveryPath">可重新导入的配置目录或保留的临时恢复包；没有可用恢复位置时为 null。</param>
/// <param name="RetainedPaths">未删除或未能确认已删除的文件、目录；需要结合 CleanupError 处理。</param>
/// <param name="Error">主操作失败原因；成功时为 null。</param>
/// <param name="CleanupError">配置恢复或文件清理失败原因，不覆盖主操作错误。</param>
public sealed record VmDeletionResult(bool Success, VmRegistrationState RegistrationState,
    string? RecoveryPath, IReadOnlyList<string> RetainedPaths,
    string? Error = null, string? CleanupError = null)
{
    /// <summary>主操作失败的结构化信息；没有更具体来源时 Kind 为 Unspecified。</summary>
    public ApiFailure? Failure { get => failure ?? (Error is null ? null : new(ApiErrorKind.Unspecified, -1, ApiErrorSource.None, Error)); init => failure = value; }
    private readonly ApiFailure? failure;
    /// <summary>清理失败的结构化信息，与主操作失败分开；没有原始接口码时为 -1。</summary>
    public ApiFailure? CleanupFailure { get => cleanupFailure ?? (CleanupError is null ? null : new(ApiErrorKind.CleanupRequired, -1, ApiErrorSource.None, CleanupError)); init => cleanupFailure = value; }
    private readonly ApiFailure? cleanupFailure;
}
