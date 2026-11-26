namespace ExHyperV.Models
{
    /// <summary>Hyper-V 交换机的桥接、NAT 和隔离配置模式。</summary>
    public enum SwitchMode
    {
        /// <summary>外部桥接模式，通过宿主物理网卡连接上游。</summary>
        Bridge,
        /// <summary>内部交换机配合宿主 L3 NAT（HCN / WinNAT / ICS）共享的模式。</summary>
        NAT,
        /// <summary>未连接外部端口且未启用宿主 NAT 共享的内部或专用网络。</summary>
        Isolated
    }

    /// <summary>Hyper-V 二层虚拟交换机的原生端口拓扑类型。</summary>
    public enum HyperVSwitchKind
    {
        /// <summary>外部交换机（绑定物理外部端口）。</summary>
        External,
        /// <summary>内部交换机（无外部端口，有宿主机 vEthernet 内部端口）。</summary>
        Internal,
        /// <summary>专用交换机（无外部端口，也无宿主机内部端口）。</summary>
        Private
    }

    /// <summary>附着在内部虚拟交换机宿主端口上的三层 NAT 转发引擎类型。</summary>
    public enum SwitchNatEngine
    {
        /// <summary>未启用三层 NAT 转发（纯内网或外部二层桥接）。</summary>
        None,
        /// <summary>由 HCN/HNS 托管的系统 NAT / 动态 ICS（例如 Default Switch）。</summary>
        HcnManaged,
        /// <summary>Windows 内核 WinNAT 引擎（MSFT_NetNat）。</summary>
        WinNat,
        /// <summary>传统 Internet 连接共享（HNetCfg.HNetShare COM）。</summary>
        IcsShared
    }

    /// <summary>虚拟交换机的连接模式、L2 类型、宿主 NAT 引擎、上游网卡与宿主接入状态。</summary>
    public class SwitchInfo
    {
        /// <summary>虚拟交换机的显示名称。</summary>
        /// <fielddoc>
        /// <meaning>虚拟交换机的显示名称。</meaning>
        /// </fielddoc>
        public string SwitchName { get; init; } = string.Empty;
        /// <summary>按物理上联和宿主 NAT 关系识别的交换机模式。StateError 非空时表示本次识别未完整成功。</summary>
        /// <fielddoc>
        /// <meaning>按物理上联和宿主 NAT 关系识别的交换机模式。StateError 非空时表示本次识别未完整成功。</meaning>
        /// </fielddoc>
        public SwitchMode SwitchType { get; init; }
        /// <summary>Hyper-V 原生二层交换机骨架类型（External / Internal / Private）。</summary>
        /// <fielddoc>
        /// <meaning>Hyper-V 原生二层虚拟交换机的端口拓扑类型。</meaning>
        /// <values kind="options">External：外部交换机；Internal：内部交换机；Private：专用交换机。</values>
        /// </fielddoc>
        public HyperVSwitchKind SwitchKind { get; init; }
        /// <summary>附着在 Internal 交换机宿主端口上的三层 NAT 引擎。</summary>
        /// <fielddoc>
        /// <meaning>附着在内部交换机宿主端口上的三层 NAT 转发引擎类型。</meaning>
        /// <values kind="options">None：未启用 NAT；HcnManaged：系统托管 HCN/HNS；WinNat：Windows 内核 WinNAT；IcsShared：传统 Internet 连接共享。</values>
        /// </fielddoc>
        public SwitchNatEngine NatEngine { get; init; }
        /// <summary>宿主 NAT 或内部网关对应的 IPv4 子网前缀（例如 172.30.240.0/20 或 192.168.137.0/24）。</summary>
        /// <fielddoc>
        /// <meaning>宿主 NAT 或内部网关对应的 IPv4 CIDR 子网前缀。</meaning>
        /// <values kind="format">IPv4 CIDR 前缀文本，例如 172.30.240.0/20；无关联子网或未解析时为 null。</values>
        /// </fielddoc>
        public string? SubnetPrefix { get; init; }
        /// <summary>宿主是否通过内部端口接入该交换机。</summary>
        /// <fielddoc>
        /// <meaning>宿主是否通过内部端口接入该交换机。</meaning>
        /// <values kind="options">true：已发现宿主端口；false：未发现。</values>
        /// </fielddoc>
        public bool AllowManagementOS { get; init; }            // 是否允许主机 OS 共享此交换机
        /// <summary>虚拟交换机的唯一标识，用于查询和修改交换机。</summary>
        /// <fielddoc>
        /// <meaning>虚拟交换机的唯一标识，用于查询和修改交换机。</meaning>
        /// <values kind="format">GUID 字符串。</values>
        /// </fielddoc>
        public string Id { get; init; } = string.Empty;
        /// <summary>交换机使用的宿主上游网卡。</summary>
        /// <fielddoc>
        /// <meaning>交换机使用的宿主上游网卡。</meaning>
        /// <values kind="format">网卡信息；无上游或未解析成功时为 null。</values>
        /// </fielddoc>
        public SwitchUpstreamAdapter? Upstream { get; init; }
        /// <summary>本次查询或识别交换机状态时的错误。</summary>
        /// <fielddoc>
        /// <meaning>本次查询或识别交换机状态时的错误。</meaning>
        /// <values kind="format">错误说明；未记录错误时为 null。</values>
        /// </fielddoc>
        public string? StateError { get; init; }
    }
}
