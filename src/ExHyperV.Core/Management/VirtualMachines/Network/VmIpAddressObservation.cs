namespace ExHyperV.Services;

/// <summary>地址信息的来源，按宿主嗅探、来宾上报、系统邻居缓存的优先级使用。</summary>
public enum VmIpAddressSource
{
    /// <summary>宿主捕获到的 ARP 报文。</summary>
    ArpCapture,
    /// <summary>来宾集成服务上报的网络配置。</summary>
    GuestIntegration,
    /// <summary>Windows 的邻居缓存。</summary>
    NeighborCache
}

/// <summary>地址与请求目标之间的关联依据。</summary>
public enum VmIpAddressAssociation
{
    /// <summary>按 MAC 匹配得到；同 MAC 的其他设备或过期缓存仍可能影响结果。</summary>
    MacAddress,
    /// <summary>通过 Hyper-V 的网卡关联取得，并核对了该网卡的 MAC。</summary>
    GuestAdapter,
    /// <summary>地址已关联到虚拟机，具体网卡待确认。</summary>
    VirtualMachine
}

/// <summary>一个 IPv4 地址及来源信息；多个地址分别返回，不拼成一个连接地址。</summary>
/// <param name="Address">经过解析和规范化的单个 IPv4 文本。</param>
/// <param name="AdapterId">确认关联的虚拟网卡身份；只能确认 MAC 或整台 VM 时为 null。</param>
/// <param name="MacAddress">已用于关联的 MAC；整台 VM 的后备数据为 null。</param>
/// <param name="Source">地址来源。</param>
/// <param name="Association">与所请求目标的关联依据。</param>
/// <param name="ObservedAtUtc">有来源观测时间时返回；来宾上报和邻居缓存未提供可靠时间时为 null。</param>
/// <param name="ReadAtUtc">本次读取该来源的 UTC 时间。</param>
public sealed record VmIpAddressObservation(string Address, string? AdapterId, string? MacAddress,
    VmIpAddressSource Source, VmIpAddressAssociation Association, DateTimeOffset? ObservedAtUtc, DateTimeOffset ReadAtUtc);
