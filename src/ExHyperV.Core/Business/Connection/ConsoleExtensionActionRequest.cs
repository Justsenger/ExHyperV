namespace ExHyperV.Services;

/// <summary>宿主按主体和请求 ID 去重，执行前复核目标、权限及版本。Version-bound action request.</summary>
/// <param name="requestId">请求去重标识。</param>
/// <param name="snapshotVersion">动作所依据的快照版本。</param>
/// <param name="action">请求执行的固定动作。</param>
/// <param name="vmId">当前虚拟机目标，宿主级动作可为空。</param>
/// <param name="componentId">部署时协商的组件标识，执行方按本地白名单解析。</param>
/// <param name="viewerContextId">不透明观众关联标识，仅供匹配上下文。</param>
public sealed class ConsoleExtensionActionRequest(Guid requestId,
    ulong snapshotVersion,
    ConsoleExtensionActionId action,
    Guid? vmId,
    string? componentId,
    string? viewerContextId)
{
    /// <summary>请求去重标识。</summary>
    /// <fielddoc><meaning>请求去重标识。</meaning></fielddoc>
    public Guid RequestId { get; } = requestId != Guid.Empty ? requestId : throw new ArgumentException("需要请求标识。", nameof(requestId));

    /// <summary>动作所依据的快照版本。</summary>
    /// <fielddoc><meaning>动作所依据的快照版本。</meaning></fielddoc>
    public ulong SnapshotVersion { get; } = snapshotVersion;

    /// <summary>请求执行的固定动作。</summary>
    /// <fielddoc><meaning>请求执行的固定动作。</meaning></fielddoc>
    public ConsoleExtensionActionId Action { get; } = Enum.IsDefined(action) && action != ConsoleExtensionActionId.Unknown ? action : throw new ArgumentOutOfRangeException(nameof(action));

    /// <summary>当前虚拟机目标，宿主级动作可为空。</summary>
    /// <fielddoc><meaning>当前虚拟机目标，宿主级动作可为空。</meaning></fielddoc>
    public Guid? VmId { get; } = vmId == Guid.Empty || (vmId is null &&
        action is ConsoleExtensionActionId.DeployGuestComponent or ConsoleExtensionActionId.RetryConnection)
        ? throw new ArgumentException("需要有效的目标虚拟机标识。", nameof(vmId)) : vmId;

    /// <summary>部署时协商的组件标识，执行方按本地白名单解析。</summary>
    /// <fielddoc><meaning>部署时协商的组件标识，执行方按本地白名单解析。</meaning></fielddoc>
    public string? ComponentId { get; } = action switch
    {
        ConsoleExtensionActionId.DeployGuestComponent when string.IsNullOrWhiteSpace(componentId) =>
            throw new ArgumentException("部署需要组件标识。", nameof(componentId)),
        not ConsoleExtensionActionId.DeployGuestComponent when componentId is not null =>
            throw new ArgumentException("只有部署动作接受组件标识。", nameof(componentId)),
        _ => componentId
    };

    /// <summary>不透明观众关联标识，仅供匹配上下文。</summary>
    /// <fielddoc><meaning>不透明观众关联标识，仅供匹配上下文。</meaning></fielddoc>
    public string? ViewerContextId { get; } = viewerContextId;
}
