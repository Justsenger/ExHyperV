namespace ExHyperV.Models;

/// <summary>可用的上游物理网卡；使用连接 GUID 和设备标识匹配，不依赖显示名称。</summary>
/// <param name="ConnectionId">宿主网络连接 GUID，供连接及共享接口识别。</param>
/// <param name="DeviceId">宿主网络设备标识。</param>
/// <param name="Description">网卡设备描述。</param>
/// <param name="Name">用户可修改的连接显示名称。</param>
/// <param name="LinkUp">查询时物理链路是否已连接。</param>
/// <remarks>桥接修改须使用当前查询返回的实例，其中还保存了桥接所需的端口身份；通过公开构造器重建或反序列化的实例不包含该信息。</remarks>
public sealed record SwitchUpstreamAdapter(Guid ConnectionId, string DeviceId, string Description, string Name,
    bool LinkUp)
{
    internal string ExternalPortPath { get; init; } = string.Empty;

    internal SwitchUpstreamAdapter(Guid connectionId, string deviceId, string description, string name,
        string externalPortPath, bool linkUp)
        : this(connectionId, deviceId, description, name, linkUp) => ExternalPortPath = externalPortPath;

    /// <summary>设备描述与连接名称组合的显示文本，不用于设备匹配。</summary>
    public string DisplayName => string.IsNullOrEmpty(Name) ? Description : $"{Description} ({Name})";
    /// <summary>取得上游网卡的显示名称。</summary>
    /// <returns>当前 DisplayName。</returns>
    public override string ToString() => DisplayName;
}
