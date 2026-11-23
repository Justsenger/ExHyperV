namespace ExHyperV.Models;

/// <summary>宿主出口网络适配器的底层介质分类。</summary>
public enum AdapterMediaKind
{
    /// <summary>有线以太网（802.3）。</summary>
    Wired,
    /// <summary>无线局域网（802.11 Wi-Fi）。</summary>
    WiFi,
    /// <summary>蜂窝移动网络（4G / 5G WWAN MBIM）。</summary>
    Cellular,
    /// <summary>三层虚拟隧道网卡（如 Wintun / WireGuard）。</summary>
    Tunnel,
    /// <summary>其他或不明确介质。</summary>
    Other
}

/// <summary>可用的上游网络适配器；使用连接 GUID 和设备标识匹配，不依赖显示名称。</summary>
/// <param name="ConnectionId">宿主网络连接 GUID，供连接及共享接口识别。</param>
/// <param name="DeviceId">宿主网络设备标识。</param>
/// <param name="Description">网卡设备描述。</param>
/// <param name="Name">用户可修改的连接显示名称。</param>
/// <param name="LinkUp">查询时物理或隧道链路是否已连接。</param>
/// <remarks>桥接修改须使用当前查询返回的实例，其中还保存了桥接所需的端口身份；通过公开构造器重建或反序列化的实例不包含该信息。</remarks>
public sealed record SwitchUpstreamAdapter(Guid ConnectionId, string DeviceId, string Description, string Name,
    bool LinkUp)
{
    internal string ExternalPortPath { get; init; } = string.Empty;

    internal SwitchUpstreamAdapter(Guid connectionId, string deviceId, string description, string name,
        string externalPortPath, bool linkUp)
        : this(connectionId, deviceId, description, name, linkUp) => ExternalPortPath = externalPortPath;

    /// <summary>适配器底层介质类型。</summary>
    /// <fielddoc>
    /// <meaning>宿主上游网络适配器的底层物理或隧道介质类型。</meaning>
    /// <values kind="options">Wired：有线以太网；WiFi：无线局域网；Cellular：蜂窝移动网络；Tunnel：三层虚拟隧道；Other：其他或不明确介质。</values>
    /// </fielddoc>
    public AdapterMediaKind MediaKind { get; init; } = AdapterMediaKind.Wired;
    /// <summary>是否具备 Hyper-V 外部虚拟交换机二层桥接端口。</summary>
    /// <fielddoc>
    /// <meaning>是否已关联可用于 Hyper-V 外部虚拟交换机二层桥接的系统端口。</meaning>
    /// <values kind="options">true：具备外部桥接端口；false：不支持二层桥接（例如纯三层隧道或未发现外部端口）。</values>
    /// </fielddoc>
    public bool CanBridge => !string.IsNullOrEmpty(ExternalPortPath);
    /// <summary>是否为按流量计费的蜂窝移动连接。</summary>
    /// <fielddoc>
    /// <meaning>指示该适配器是否归类为按流量计费的蜂窝移动连接。</meaning>
    /// <values kind="options">true：蜂窝移动网卡；false：非蜂窝连接。</values>
    /// </fielddoc>
    public bool IsMetered => MediaKind == AdapterMediaKind.Cellular;
    /// <summary>当前连接网络配置文件名称（例如 Wi-Fi SSID 或蜂窝运营商名称）。</summary>
    /// <fielddoc>
    /// <meaning>Windows 网络连接配置文件名称，例如 Wi-Fi SSID 或蜂窝运营商名称。</meaning>
    /// <values kind="format">网络配置文件名称文本；无激活配置或未读取到时为 null。</values>
    /// </fielddoc>
    public string? ConnectionProfileName { get; init; }
    /// <summary>系统当前检测到的 IPv4 或 IPv6 外网连通状态。</summary>
    /// <fielddoc>
    /// <meaning>系统网络连接感知服务检测到的 IPv4 或 IPv6 互联网连通状态。</meaning>
    /// <values kind="options">true：已检测到互联网连通性；false：仅本地网络或未连通。</values>
    /// </fielddoc>
    public bool HasInternetAccess { get; init; }
    /// <summary>网卡当前链路速率（bps，来自 MSFT_NetAdapter.Speed；未知时为 null）。</summary>
    /// <fielddoc>
    /// <meaning>适配器当前物理或逻辑链路标称速率，读取自系统网络适配器属性。</meaning>
    /// <unit>bps（比特每秒）。</unit>
    /// <values kind="format">无符号 64 位整数；未能读取速率时为 null。</values>
    /// </fielddoc>
    public ulong? LinkSpeed { get; init; }

    /// <summary>设备描述与连接名称组合的显示文本，不用于设备匹配。</summary>
    public string DisplayName => string.IsNullOrEmpty(Name) ? Description : $"{Description} ({Name})";
    /// <summary>取得上游网卡的显示名称。</summary>
    /// <returns>当前 DisplayName。</returns>
    public override string ToString() => DisplayName;
}
