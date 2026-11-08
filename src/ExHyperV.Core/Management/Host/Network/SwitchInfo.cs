namespace ExHyperV.Models
{
    /// <summary>Hyper-V 交换机的桥接、ICS NAT 和隔离配置模式。</summary>
    public enum SwitchMode
    {
        /// <summary>外部桥接模式，通过宿主物理网卡连接上游。</summary>
        Bridge,
        /// <summary>内部交换机配合 ICS 共享的模式。</summary>
        NAT,
        /// <summary>未识别到外部端口或经典 ICS 共享；不能据此排除 HCN、WinNAT 或其他转发配置。</summary>
        Isolated
    }

    /// <summary>虚拟交换机的连接模式、上游网卡与宿主接入状态。</summary>
    public class SwitchInfo
    {
        /// <summary>虚拟交换机的显示名称。</summary>
        /// <fielddoc>
        /// <meaning>虚拟交换机的显示名称。</meaning>
        /// </fielddoc>
        public string SwitchName { get; init; } = string.Empty;
        /// <summary>按物理上联和 ICS 共享关系识别的交换机模式。StateError 非空时表示本次识别未完整成功。</summary>
        /// <fielddoc>
        /// <meaning>按物理上联和 ICS 共享关系识别的交换机模式。StateError 非空时表示本次识别未完整成功。</meaning>
        /// </fielddoc>
        public SwitchMode SwitchType { get; init; }
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
