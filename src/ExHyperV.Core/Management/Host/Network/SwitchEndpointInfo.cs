namespace ExHyperV.Models
{
    /// <summary>
    /// 交换机端点的显示摘要，包含虚拟机网卡或宿主网卡。
    /// </summary>
    public class SwitchEndpointInfo
    {
        /// <summary>接入交换机的虚拟机名称或宿主管理操作系统名称。</summary>
        /// <fielddoc>
        /// <meaning>接入交换机的虚拟机名称或宿主管理操作系统名称。</meaning>
        /// </fielddoc>
        public string Name { get; set; } = string.Empty;       // VM 名，或主机管理 OS 显示名
        /// <summary>交换机端点的 MAC 地址。</summary>
        /// <fielddoc>
        /// <meaning>交换机端点的 MAC 地址。</meaning>
        /// </fielddoc>
        public string MacAddress { get; set; } = string.Empty;
        /// <summary>从已取得的地址中选出的端点 IPv4 地址。</summary>
        /// <fielddoc>
        /// <meaning>从已取得的地址中选出的端点 IPv4 地址。</meaning>
        /// <values kind="format">IPv4 地址文本，例如 192.168.1.10；未取得时为空字符串。</values>
        /// </fielddoc>
        public string IpAddress { get; set; } = string.Empty;  // 主 IPv4（已由 Ipv4.SelectBest 择优）
    }
}