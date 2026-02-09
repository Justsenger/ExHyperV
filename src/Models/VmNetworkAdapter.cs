namespace ExHyperV.Models
{
    #region Enums for Network Adapter Properties

    /// <summary>
    /// 定义 VLAN 的操作模式。
    /// WMI: Msvm_EthernetSwitchPortVlanSettingData.OperationMode
    /// </summary>
    public enum VlanOperationMode
    {
        /// <summary>
        /// 未知或未设置。
        /// </summary>
        Unknown = 0,
        /// <summary>
        /// 接入模式，仅允许单个 VLAN ID 通过。
        /// </summary>
        Access = 1,
        /// <summary>
        /// 中继模式，允许多个标记的 VLAN ID 通过。
        /// </summary>
        Trunk = 2,
        /// <summary>
        /// 专用网络模式，用于实现二层隔离。
        /// </summary>
        Private = 3
    }

    /// <summary>
    /// 定义专用 VLAN (PVLAN) 的子模式。
    /// WMI: Msvm_EthernetSwitchPortVlanSettingData.PvlanMode
    /// </summary>
    public enum PvlanMode
    {
        /// <summary>
        /// 未设置。
        /// </summary>
        None = 0,
        /// <summary>
        /// 隔离端口，只能与混杂端口通信。
        /// </summary>
        Isolated = 1,
        /// <summary>
        /// 社区端口，可以与同一社区及混杂端口通信。
        /// </summary>
        Community = 2,
        /// <summary>
        /// 混杂端口，可以与所有端口通信。
        /// </summary>
        Promiscuous = 3
    }

    /// <summary>
    /// 定义端口镜像（监控）的模式。
    /// WMI: Msvm_EthernetSwitchPortSecuritySettingData.MonitorMode
    /// </summary>
    public enum PortMonitorMode
    {
        /// <summary>
        /// 未启用监控。
        /// </summary>
        None = 0,
        /// <summary>
        /// 目标模式，此端口接收来自源端口的流量副本。
        /// </summary>
        Destination = 1,
        /// <summary>
        /// 源模式，此端口的流量将被复制到目标端口。
        /// </summary>
        Source = 2
    }

    #endregion

    /// <summary>
    /// 表示一个虚拟机的网络适配器及其所有可配置的属性。
    /// 这是一个聚合模型，数据源自多个 WMI 类。
    /// </summary>
    public class VmNetworkAdapter
    {
        // ==========================================
        // 1. 基础配置与连接 (Basic & Connection)
        // ==========================================

        /// <summary>
        /// WMI 实例的唯一标识符 (InstanceID)。
        /// </summary>
        public string Id { get; set; }

        /// <summary>
        /// 用户在 Hyper-V 中设置的适配器名称 (ElementName)。
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        /// 指示网卡是否已连接（模拟网线插拔）。
        /// WMI: Msvm_EthernetPortAllocationSettingData.EnabledState (2=true, 3=false)
        /// </summary>
        public bool IsConnected { get; set; }

        /// <summary>
        /// 当前连接的虚拟交换机的名称。
        /// WMI: Msvm_EthernetPortAllocationSettingData.HostResource
        /// </summary>
        public string SwitchName { get; set; }

        /// <summary>
        /// 网卡的 MAC 地址。
        /// </summary>
        public string MacAddress { get; set; }

        /// <summary>
        /// 指示 MAC 地址是否为静态配置。
        /// WMI: Msvm_SyntheticEthernetPortSettingData.StaticMacAddress
        /// </summary>
        public bool IsStaticMac { get; set; }

        /// <summary>
        /// 当虚拟机作为副本进行故障转移测试时，将连接到的备用交换机名称。
        /// WMI: Msvm_EthernetPortAllocationSettingData.TestReplicaSwitchName
        /// </summary>
        public string TestReplicaSwitchName { get; set; }

        /// <summary>
        /// 指示此网络适配器是否受故障转移群集监控。
        /// WMI: Msvm_SyntheticEthernetPortSettingData.ClusterMonitored
        /// </summary>
        public bool ClusterMonitored { get; set; }

        /// <summary>
        /// 指示是否启用一致性设备命名 (CDN)，以防止 Guest OS 内网卡名称混乱。
        /// WMI: Msvm_SyntheticEthernetPortSettingData.DeviceNamingEnabled
        /// </summary>
        public bool DeviceNamingEnabled { get; set; }


        // ==========================================
        // 2. 运行时状态 (Guest Runtime Info)
        // (数据源: Msvm_GuestNetworkAdapterConfiguration)
        // ==========================================

        /// <summary>
        /// 从 Guest OS 内部获取的 IP 地址列表 (IPv4 和 IPv6)。
        /// </summary>
        public List<string> IpAddresses { get; set; } = new List<string>();

        /// <summary>
        /// 对应的子网掩码列表。
        /// </summary>
        public List<string> Subnets { get; set; } = new List<string>();

        /// <summary>
        /// 默认网关列表。
        /// </summary>
        public List<string> Gateways { get; set; } = new List<string>();

        /// <summary>
        /// DNS 服务器列表。
        /// </summary>
        public List<string> DnsServers { get; set; } = new List<string>();

        /// <summary>
        /// 指示 Guest OS 内部是否已启用 DHCP。
        /// </summary>
        public bool IsDhcpEnabled { get; set; }


        // ==========================================
        // 3. 安全防护 (Security & Guard)
        // (数据源: Msvm_EthernetSwitchPortSecuritySettingData)
        // ==========================================

        /// <summary>
        /// 是否允许 MAC 地址欺骗。
        /// </summary>
        public bool MacSpoofingAllowed { get; set; }

        /// <summary>
        /// 是否启用 DHCP 守护，防止虚拟机成为非法的 DHCP 服务器。
        /// </summary>
        public bool DhcpGuardEnabled { get; set; }

        /// <summary>
        /// 是否启用路由器守护，防止虚拟机发送非法的路由通告。
        /// </summary>
        public bool RouterGuardEnabled { get; set; }

        /// <summary>
        /// 是否允许在 Guest OS 内部对此网卡进行 NIC Teaming (绑定)。
        /// </summary>
        public bool TeamingAllowed { get; set; }

        /// <summary>
        /// 广播或多播风暴抑制的阈值（数据包/秒）。0 表示禁用。
        /// </summary>
        public uint StormLimit { get; set; }

        /// <summary>
        /// 端口的监控（镜像）模式。
        /// </summary>
        public PortMonitorMode MonitorMode { get; set; }


        // ==========================================
        // 4. 硬件加速与性能 (Offload & Acceleration)
        // (数据源: Msvm_EthernetSwitchPortOffloadSettingData)
        // ==========================================

        /// <summary>
        /// 是否启用虚拟机队列 (VMQ)。
        /// </summary>
        public bool VmqEnabled { get; set; }

        /// <summary>
        /// 是否启用 IPsec 任务卸载。
        /// </summary>
        public bool IpsecOffloadEnabled { get; set; }

        /// <summary>
        /// 是否启用单根 I/O 虚拟化 (SR-IOV)。
        /// </summary>
        public bool SriovEnabled { get; set; }

        /// <summary>
        /// [现代加速] 是否启用虚拟接收端缩放 (vRSS)。
        /// </summary>
        public bool VrssEnabled { get; set; }

        /// <summary>
        /// [现代加速] 是否启用虚拟多队列 (VMMQ)，vRSS 的演进版。
        /// </summary>
        public bool VmmqEnabled { get; set; }

        /// <summary>
        /// [现代加速] 是否启用接收段合并 (RSC)，可显著降低 CPU 占用。
        /// </summary>
        public bool RscEnabled { get; set; }

        /// <summary>
        /// [现代加速] 是否启用 PacketDirect，提供极低延迟网络路径。
        /// </summary>
        public bool PacketDirectEnabled { get; set; }


        // ==========================================
        // 5. VLAN 与 隔离 (VLAN & Isolation)
        // (数据源: Msvm_EthernetSwitchPortVlanSettingData)
        // ==========================================

        /// <summary>
        /// VLAN 的操作模式。
        /// </summary>
        public VlanOperationMode VlanMode { get; set; }

        /// <summary>
        /// 当模式为 Access 时，指定的 VLAN ID。
        /// </summary>
        public int AccessVlanId { get; set; }

        /// <summary>
        /// 当模式为 Trunk 时，未标记流量所属的 Native VLAN ID。
        /// </summary>
        public int NativeVlanId { get; set; }

        /// <summary>
        /// 当模式为 Trunk 时，允许通过的 VLAN ID 列表。
        /// </summary>
        public List<int> TrunkAllowedVlanIds { get; set; } = new List<int>();

        /// <summary>
        /// 当模式为 Private 时，使用的主 VLAN ID。
        /// </summary>
        public int PvlanPrimaryId { get; set; }

        /// <summary>
        /// 当模式为 Private 时，使用的辅助 VLAN ID。
        /// </summary>
        public int PvlanSecondaryId { get; set; }

        /// <summary>
        /// 当模式为 Private 时，使用的 PVLAN 模式。
        /// </summary>
        public PvlanMode PvlanMode { get; set; }


        // ==========================================
        // 6. 流量控制 (QoS)
        // (数据源: Msvm_EthernetSwitchPortBandwidthSettingData)
        // ==========================================

        /// <summary>
        /// 最小保障带宽（单位：bps）。
        /// </summary>
        public ulong BandwidthReservation { get; set; }

        /// <summary>
        /// 最大限制带宽（单位：bps）。
        /// </summary>
        public ulong BandwidthLimit { get; set; }


        // ==========================================
        // 7. 访问控制列表 (ACLs - Port Firewall)
        // (数据源: Msvm_EthernetSwitchPortAclSettingData)
        // ==========================================

        /// <summary>
        /// 应用于此端口的访问控制规则列表。
        /// </summary>
        public List<NetworkAclRule> AclRules { get; set; } = new List<NetworkAclRule>();
    }

    /// <summary>
    /// 表示一条应用于虚拟网络端口的访问控制规则。
    /// </summary>
    public class NetworkAclRule
    {
        public string Name { get; set; }
        public string Direction { get; set; } // "Incoming" or "Outgoing"
        public string Action { get; set; }    // "Allow", "Deny", or "Meter"
        public string RemoteAddress { get; set; } // IP (e.g., "192.168.1.10/24") or MAC address
        public string LocalAddress { get; set; }
    }
}