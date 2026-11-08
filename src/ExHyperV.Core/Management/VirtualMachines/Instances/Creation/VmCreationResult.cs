using ExHyperV.Tools;

namespace ExHyperV.Services;

/// <summary>单台创建结果，分别保留业务完成、真实身份与失败后的资源状态。</summary>
/// <param name="Success">创建及所需配置是否完成，不包含后续启动。</param>
/// <param name="VmId">已取得的本次创建 GUID；回滚成功后仍保留该历史身份，未取得时为 null。</param>
/// <param name="ActualName">已创建虚拟机的实际名称；尚未确认创建时为 null。</param>
/// <param name="RegistrationState">本次创建对象当前已确认的注册状态。</param>
/// <param name="RetainedPaths">失败后仍保留或未确认删除的本次操作文件、目录，不包含 WMI 路径。</param>
/// <param name="Error">创建失败原因；成功时为 null。</param>
/// <param name="CleanupError">回滚或清理失败原因；不覆盖创建失败原因。</param>
public sealed record VmCreationResult(bool Success, Guid? VmId, string? ActualName,
    VmRegistrationState RegistrationState, IReadOnlyList<string> RetainedPaths,
    string? Error = null, string? CleanupError = null)
{
    /// <summary>调用方请求的原始名称；可与 ActualName 对照。</summary>
    public string? RequestedName { get; init; }
    /// <summary>实际名称是否因名称或输出路径冲突发生调整；这种调整不使创建失败。</summary>
    public bool NameAdjusted => RequestedName != null && ActualName != null
        && !string.Equals(RequestedName, ActualName, StringComparison.Ordinal);
    /// <summary>创建成功后的实际配置目录；失败时资源去向由 RetainedPaths 和注册状态表达。</summary>
    public string? ConfigurationDirectory { get; init; }
    /// <summary>创建成功后的实际磁盘文件路径；未挂接磁盘或创建失败时为 null。</summary>
    public string? DiskPath { get; init; }
    /// <summary>成功时的非阻塞调整说明；名称和目录未发生冲突时为 null。程序判断使用 NameAdjusted。</summary>
    public string? Notice => Success && NameAdjusted
        ? $"名称或输出路径已被占用，已自动将“{RequestedName}”调整为“{ActualName}”。" : null;
    /// <summary>主操作失败类别；成功时为 None。不能仅凭失败判断虚拟机未创建。</summary>
    public ApiErrorKind ErrorKind { get; init; } = Success ? ApiErrorKind.None : ApiErrorKind.OperationFailed;
    /// <summary>至少一个已提交操作的结果尚未确认，可能仍在执行；不得盲目重试创建或清理保留资源。</summary>
    public bool OutcomeUnknown { get; init; }
    /// <summary>回滚或清理失败类别；没有清理错误时为 None，与主操作错误分别保留。</summary>
    public ApiErrorKind CleanupErrorKind { get; init; } = CleanupError == null ? ApiErrorKind.None : ApiErrorKind.CleanupRequired;
    /// <summary>可选配置的失败响应；目前用于 ISO 优先引导，创建成功不表示这些可选配置已经完成。</summary>
    public IReadOnlyList<ApiResponse> ConfigurationIssues { get; init; } = [];
}
