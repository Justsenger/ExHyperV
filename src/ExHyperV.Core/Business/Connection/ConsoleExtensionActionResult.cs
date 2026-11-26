namespace ExHyperV.Services;

/// <summary>动作结果用于重新查询权威快照。Result correlated with the original request.</summary>
/// <param name="RequestId">原请求标识。
/// <fielddoc><meaning>原请求标识。</meaning></fielddoc></param>
/// <param name="Accepted">宿主是否接受动作。
/// <fielddoc><meaning>宿主是否接受动作。</meaning></fielddoc></param>
/// <param name="SnapshotVersion">响应对应的权威快照版本。
/// <fielddoc><meaning>响应对应的权威快照版本。</meaning></fielddoc></param>
/// <param name="Reason">已脱敏的拒绝或重试原因。
/// <fielddoc><meaning>已脱敏的拒绝或重试原因。</meaning></fielddoc></param>
/// <param name="DiagnosticId">用于关联诊断的非敏感编号。
/// <fielddoc><meaning>用于关联诊断的非敏感编号。</meaning></fielddoc></param>
public sealed record ConsoleExtensionActionResult(Guid RequestId,
    bool Accepted,
    ulong SnapshotVersion,
    string? Reason,
    string? DiagnosticId);
