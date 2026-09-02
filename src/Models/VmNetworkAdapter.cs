using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.Generic;
using System.Linq;

namespace ExHyperV.Models
{

    // WMI: Msvm_EthernetSwitchPortVlanSettingData.OperationMode
    public enum VlanOperationMode
    {
        Unknown = 0,
        Access = 1,
        Trunk = 2,
        Private = 3
    }

    // WMI: Msvm_EthernetSwitchPortVlanSettingData.PvlanMode
    public enum PvlanMode
    {
        None = 0,
        Isolated = 1,
        Community = 2,
        Promiscuous = 3
    }

    // WMI: Msvm_EthernetSwitchPortSecuritySettingData.MonitorMode
    public enum PortMonitorMode
    {
        None = 0,
        Destination = 1,
        Source = 2
    }


    /// <summary>聚合多个 Hyper-V 网络 WMI 类的网卡设置。</summary>
    public partial class VmNetworkAdapter : ObservableObject
    {
        public string IpAddressDisplay => (IpAddresses != null && IpAddresses.Count > 0)
            ? IpAddresses.FirstOrDefault(ip => ip.Contains(".") && !ip.Contains(":")) ?? IpAddresses[0]
            : "---";

        [ObservableProperty]
        private string _id = string.Empty;

        [ObservableProperty]
        private string _name = string.Empty;

        // 指示网卡是否已连接（模拟网线插拔）。 WMI: Msvm_EthernetPortAllocationSettingData.EnabledState (2=true, 3=false)
        [ObservableProperty]
        private bool _isConnected;

        // 当前连接的虚拟交换机的名称。 WMI: Msvm_EthernetPortAllocationSettingData.HostResource
        private string _switchName = Properties.Resources.Status_Unconnected;
        public string SwitchName
        {
            get => _switchName;
            set
            {
                if (!string.IsNullOrEmpty(_switchName) && _switchName != Properties.Resources.Status_Unconnected)
                {
                    if (string.IsNullOrWhiteSpace(value) || value == Properties.Resources.Status_Unconnected || value.StartsWith("WMI_"))
                    {
                        return;
                    }
                }

                if (_switchName != value)
                {
                    _switchName = value;
                    OnPropertyChanged(nameof(SwitchName));
                }
            }
        }

        [ObservableProperty]
        private string _macAddress = string.Empty;

        // 指示 MAC 地址是否为静态配置。 WMI: Msvm_SyntheticEthernetPortSettingData.StaticMacAddress
        [ObservableProperty]
        private bool _isStaticMac;

        // 当虚拟机作为副本进行故障转移测试时，将连接到的备用交换机名称。 WMI: Msvm_EthernetPortAllocationSettingData.TestReplicaSwitchName
        [ObservableProperty]
        private string _testReplicaSwitchName = string.Empty;

        // 指示此网络适配器是否受故障转移群集监控。 WMI: Msvm_SyntheticEthernetPortSettingData.ClusterMonitored
        [ObservableProperty]
        private bool _clusterMonitored;

        // 指示是否启用一致性设备命名 (CDN)，以防止 Guest OS 内网卡名称混乱。 WMI: Msvm_SyntheticEthernetPortSettingData.DeviceNamingEnabled
        [ObservableProperty]
        private bool _deviceNamingEnabled;


        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IpAddressDisplay))]
        private List<string> _ipAddresses = new List<string>();

        [ObservableProperty]
        private List<string> _subnets = new List<string>();

        [ObservableProperty]
        private List<string> _gateways = new List<string>();

        [ObservableProperty]
        private List<string> _dnsServers = new List<string>();

        [ObservableProperty]
        private bool _isDhcpEnabled;


        [ObservableProperty]
        private bool _macSpoofingAllowed;

        [ObservableProperty]
        private bool _dhcpGuardEnabled;

        [ObservableProperty]
        private bool _routerGuardEnabled;

        [ObservableProperty]
        private bool _teamingAllowed;

        [ObservableProperty]
        private uint _stormLimit;

        [ObservableProperty]
        private PortMonitorMode _monitorMode;


        [ObservableProperty]
        private bool _vmqEnabled;

        [ObservableProperty]
        private bool _ipsecOffloadEnabled;

        [ObservableProperty]
        private bool _sriovEnabled;

        [ObservableProperty]
        private bool _vrssEnabled;

        [ObservableProperty]
        private bool _vmmqEnabled;

        [ObservableProperty]
        private bool _rscEnabled;

        [ObservableProperty]
        private bool _packetDirectEnabled;


        [ObservableProperty]
        private VlanOperationMode _vlanMode;

        [ObservableProperty]
        private int _accessVlanId;

        [ObservableProperty]
        private int _nativeVlanId;

        [ObservableProperty]
        private List<int> _trunkAllowedVlanIds = new List<int>();

        [ObservableProperty]
        private int _pvlanPrimaryId;

        [ObservableProperty]
        private int _pvlanSecondaryId;

        [ObservableProperty]
        private PvlanMode _pvlanMode;


        [ObservableProperty]
        private ulong _bandwidthReservation;

        [ObservableProperty]
        private ulong _bandwidthLimit;


    }
}
