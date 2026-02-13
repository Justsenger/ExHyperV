using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.Generic;
using System.Linq;

namespace ExHyperV.Models
{
    #region Enums for Network Adapter Properties

    /// <summary>
    /// 定义 VLAN 的操作模式。
    /// WMI: Msvm_EthernetSwitchPortVlanSettingData.OperationMode
    /// </summary>
    public enum VlanOperationMode
    {
        Unknown = 0,
        Access = 1,
        Trunk = 2,
        Private = 3
    }

    /// <summary>
    /// 定义专用 VLAN (PVLAN) 的子模式。
    /// WMI: Msvm_EthernetSwitchPortVlanSettingData.PvlanMode
    /// </summary>
    public enum PvlanMode
    {
        None = 0,
        Isolated = 1,
        Community = 2,
        Promiscuous = 3
    }

    /// <summary>
    /// 定义端口镜像（监控）的模式。
    /// WMI: Msvm_EthernetSwitchPortSecuritySettingData.MonitorMode
    /// </summary>
    public enum PortMonitorMode
    {
        None = 0,
        Destination = 1,
        Source = 2
    }

    #endregion

    /// <summary>
    /// 表示一个虚拟机的网络适配器及其所有可配置的属性。
    /// 这是一个聚合模型，数据源自多个 WMI 类。
    /// </summary>
    public partial class VmNetworkAdapter : ObservableObject
    {
        // ==========================================
        // 1. 基础配置与连接 (Basic & Connection)
        // ==========================================

        /// <summary>
        /// 供界面显示的 IP 地址摘要（优先显示第一个 IPv4）。
        /// </summary>
        public string IpAddressDisplay => (IpAddresses != null && IpAddresses.Count > 0)
            ? IpAddresses.FirstOrDefault(ip => ip.Contains(".") && !ip.Contains(":")) ?? IpAddresses[0]
            : "正在获取...";

        /// <summary>
        /// WMI 实例的唯一标识符 (InstanceID)。
        /// </summary>
        [ObservableProperty]
        private string _id;

        /// <summary>
        /// 用户在 Hyper-V 中设置的适配器名称 (ElementName)。
        /// </summary>
        [ObservableProperty]
        private string _name;

        /// <summary>
        /// 指示网卡是否已连接（模拟网线插拔）。
        /// WMI: Msvm_EthernetPortAllocationSettingData.EnabledState (2=true, 3=false)
        /// </summary>
        [ObservableProperty]
        private bool _isConnected;

        /// <summary>
        /// 当前连接的虚拟交换机的名称。
        /// WMI: Msvm_EthernetPortAllocationSettingData.HostResource
        /// </summary>
        [ObservableProperty]
        private string _switchName;

        /// <summary>
        /// 网卡的 MAC 地址。
        /// </summary>
        [ObservableProperty]
        private string _macAddress;

        /// <summary>
        /// 指示 MAC 地址是否为静态配置。
        /// WMI: Msvm_SyntheticEthernetPortSettingData.StaticMacAddress
        /// </summary>
        [ObservableProperty]
        private bool _isStaticMac;

        /// <summary>
        /// 当虚拟机作为副本进行故障转移测试时，将连接到的备用交换机名称。
        /// WMI: Msvm_EthernetPortAllocationSettingData.TestReplicaSwitchName
        /// </summary>
        [ObservableProperty]
        private string _testReplicaSwitchName;

        /// <summary>
        /// 指示此网络适配器是否受故障转移群集监控。
        /// WMI: Msvm_SyntheticEthernetPortSettingData.ClusterMonitored
        /// </summary>
        [ObservableProperty]
        private bool _clusterMonitored;

        /// <summary>
        /// 指示是否启用一致性设备命名 (CDN)，以防止 Guest OS 内网卡名称混乱。
        /// WMI: Msvm_SyntheticEthernetPortSettingData.DeviceNamingEnabled
        /// </summary>
        [ObservableProperty]
        private bool _deviceNamingEnabled;


        // ==========================================
        // 2. 运行时状态 (Guest Runtime Info)
        // (数据源: Msvm_GuestNetworkAdapterConfiguration)
        // ==========================================

        /// <summary>
        /// 从 Guest OS 内部获取的 IP 地址列表 (IPv4 和 IPv6)。
        /// </summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IpAddressDisplay))]
        private List<string> _ipAddresses = new List<string>();

        /// <summary>
        /// 对应的子网掩码列表。
        /// </summary>
        [ObservableProperty]
        private List<string> _subnets = new List<string>();

        /// <summary>
        /// 默认网关列表。
        /// </summary>
        [ObservableProperty]
        private List<string> _gateways = new List<string>();

        /// <summary>
        /// DNS 服务器列表。
        /// </summary>
        [ObservableProperty]
        private List<string> _dnsServers = new List<string>();

        /// <summary>
        /// 指示 Guest OS 内部是否已启用 DHCP。
        /// </summary>
        [ObservableProperty]
        private bool _isDhcpEnabled;


        // ==========================================
        // 3. 安全防护 (Security & Guard)
        // (数据源: Msvm_EthernetSwitchPortSecuritySettingData)
        // ==========================================

        /// <summary>
        /// 是否允许 MAC 地址欺骗。
        /// </summary>
        [ObservableProperty]
        private bool _macSpoofingAllowed;

        /// <summary>
        /// 是否启用 DHCP 守护，防止虚拟机成为非法的 DHCP 服务器。
        /// </summary>
        [ObservableProperty]
        private bool _dhcpGuardEnabled;

        /// <summary>
        /// 是否启用路由器守护，防止虚拟机发送非法的路由通告。
        /// </summary>
        [ObservableProperty]
        private bool _routerGuardEnabled;

        /// <summary>
        /// 是否允许在 Guest OS 内部对此网卡进行 NIC Teaming (绑定)。
        /// </summary>
        [ObservableProperty]
        private bool _teamingAllowed;

        /// <summary>
        /// 广播或多播风暴抑制的阈值（数据包/秒）。0 表示禁用。
        /// </summary>
        [ObservableProperty]
        private uint _stormLimit;

        /// <summary>
        /// 端口的监控（镜像）模式。
        /// </summary>
        [ObservableProperty]
        private PortMonitorMode _monitorMode;


        // ==========================================
        // 4. 硬件加速与性能 (Offload & Acceleration)
        // (数据源: Msvm_EthernetSwitchPortOffloadSettingData)
        // ==========================================

        /// <summary>
        /// 是否启用虚拟机队列 (VMQ)。
        /// </summary>
        [ObservableProperty]
        private bool _vmqEnabled;

        /// <summary>
        /// 是否启用 IPsec 任务卸载。
        /// </summary>
        [ObservableProperty]
        private bool _ipsecOffloadEnabled;

        /// <summary>
        /// 是否启用单根 I/O 虚拟化 (SR-IOV)。
        /// </summary>
        [ObservableProperty]
        private bool _sriovEnabled;

        /// <summary>
        /// [现代加速] 是否启用虚拟接收端缩放 (vRSS)。
        /// </summary>
        [ObservableProperty]
        private bool _vrssEnabled;

        /// <summary>
        /// [现代加速] 是否启用虚拟多队列 (VMMQ)，vRSS 的演进版。
        /// </summary>
        [ObservableProperty]
        private bool _vmmqEnabled;

        /// <summary>
        /// [现代加速] 是否启用接收段合并 (RSC)，可显著降低 CPU 占用。
        /// </summary>
        [ObservableProperty]
        private bool _rscEnabled;

        /// <summary>
        /// [现代加速] 是否启用 PacketDirect，提供极低延迟网络路径。
        /// </summary>
        [ObservableProperty]
        private bool _packetDirectEnabled;


        // ==========================================
        // 5. VLAN 与 隔离 (VLAN & Isolation)
        // (数据源: Msvm_EthernetSwitchPortVlanSettingData)
        // ==========================================

        /// <summary>
        /// VLAN 的操作模式。
        /// </summary>
        [ObservableProperty]
        private VlanOperationMode _vlanMode;

        /// <summary>
        /// 当模式为 Access 时，指定的 VLAN ID。
        /// </summary>
        [ObservableProperty]
        private int _accessVlanId;

        /// <summary>
        /// 当模式为 Trunk 时，未标记流量所属的 Native VLAN ID。
        /// </summary>
        [ObservableProperty]
        private int _nativeVlanId;

        /// <summary>
        /// 当模式为 Trunk 时，允许通过的 VLAN ID 列表。
        /// </summary>
        [ObservableProperty]
        private List<int> _trunkAllowedVlanIds = new List<int>();

        /// <summary>
        /// 当模式为 Private 时，使用的主 VLAN ID。
        /// </summary>
        [ObservableProperty]
        private int _pvlanPrimaryId;

        /// <summary>
        /// 当模式为 Private 时，使用的辅助 VLAN ID。
        /// </summary>
        [ObservableProperty]
        private int _pvlanSecondaryId;

        /// <summary>
        /// 当模式为 Private 时，使用的 PVLAN 模式。
        /// </summary>
        [ObservableProperty]
        private PvlanMode _pvlanMode;


        // ==========================================
        // 6. 流量控制 (QoS)
        // (数据源: Msvm_EthernetSwitchPortBandwidthSettingData)
        // ==========================================

        /// <summary>
        /// 最小保障带宽（单位：bps）。
        /// </summary>
        [ObservableProperty]
        private ulong _bandwidthReservation;

        /// <summary>
        /// 最大限制带宽（单位：bps）。
        /// </summary>
        [ObservableProperty]
        private ulong _bandwidthLimit;


        // ==========================================
        // 7. 访问控制列表 (ACLs - Port Firewall)
        // (数据源: Msvm_EthernetSwitchPortAclSettingData)
        // ==========================================

        /// <summary>
        /// 应用于此端口的访问控制规则列表。
        /// </summary>
        [ObservableProperty]
        private List<NetworkAclRule> _aclRules = new List<NetworkAclRule>();
    }

    /// <summary>
    /// 表示一条应用于虚拟网络端口的访问控制规则。
    /// </summary>
    public class NetworkAclRule
    {
        public string Name { get; set; }
        public string Direction { get; set; } // "Incoming" or "Outgoing"
        public string Action { get; set; }    // "Allow", "Deny", or "Meter"
        public string RemoteAddress { get; set; } // IP or MAC address
        public string LocalAddress { get; set; }
    }
}