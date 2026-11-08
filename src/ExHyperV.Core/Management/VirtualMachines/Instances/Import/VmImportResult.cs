using ExHyperV.Tools;

namespace ExHyperV.Services;

/// <summary>单台导入结果；失败仍可能已经注册虚拟机或留下需要处理的工作资源。</summary>
/// <param name="Success">导入流程是否完成。</param>
/// <param name="VmId">已知的计划或已导入虚拟机 GUID；尚未取得时为 null。</param>
/// <param name="IsRealized">是否已经确认实现为注册虚拟机；原生状态未知时 false 不能证明未注册。</param>
/// <param name="OperationStateUnknown">是否仍不能确认原生操作结束或计划系统清理完成。</param>
/// <param name="CleanupState">返回时的工作资源清理状态；释放会话后读取会话属性取得最新状态。</param>
/// <param name="RetainedPaths">本次工作资源中保留或未确认删除的文件、目录，不包含原生对象路径。</param>
/// <param name="Error">导入失败原因；成功时为 null。</param>
/// <param name="CleanupError">资源清理失败或延期原因，不覆盖主操作错误。</param>
public sealed record VmImportResult(bool Success, Guid? VmId, bool IsRealized,
    bool OperationStateUnknown, VmImportCleanupState CleanupState,
    IReadOnlyList<string> RetainedPaths, string? Error = null, string? CleanupError = null)
{
    /// <summary>ZIP 密码、格式或额度方面的具体失败；其他原因或成功时为 null。Failure 仍提供整体失败信息。</summary>
    public VmImportArchiveFailure? ArchiveFailure { get; init; }
    /// <summary>主操作失败的结构化信息；没有更具体来源时 Kind 为 Unspecified。</summary>
    public ApiFailure? Failure { get => failure ?? (Error is null ? null : new(ApiErrorKind.Unspecified, -1, ApiErrorSource.None, Error)); init => failure = value; }
    private readonly ApiFailure? failure;
    /// <summary>清理失败的结构化信息，与主操作失败分开；没有原始接口码时为 -1。</summary>
    public ApiFailure? CleanupFailure { get => cleanupFailure ?? (CleanupError is null ? null : new(ApiErrorKind.CleanupRequired, -1, ApiErrorSource.None, CleanupError)); init => cleanupFailure = value; }
    private readonly ApiFailure? cleanupFailure;
}
