namespace ExHyperV.Models
{
    /// <summary>
    /// 连接在某个虚拟交换机上的端点：虚拟机网卡，或主机管理 OS 网卡。仅用于交换机拓扑图。
    /// </summary>
    public class AdapterInfo
    {
        public string Name { get; set; } = string.Empty;       // VM 名，或主机管理 OS 显示名
        public string MacAddress { get; set; } = string.Empty;
        public string IpAddress { get; set; } = string.Empty;  // 主 IPv4（已由 Ipv4.SelectBest 择优）
    }
}