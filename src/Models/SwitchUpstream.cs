namespace ExHyperV.Models;

/// <summary>显示名称不参与设备匹配；连接 GUID 与 Hyper-V 外部端口分别对应各自 API 的身份。</summary>
public sealed record SwitchUpstream(Guid ConnectionId, string DeviceId, string Description, string Name,
    string ExternalPortPath, bool LinkUp)
{
    public string DisplayName => string.IsNullOrEmpty(Name) ? Description : $"{Description} ({Name})";
    public override string ToString() => DisplayName;
}
