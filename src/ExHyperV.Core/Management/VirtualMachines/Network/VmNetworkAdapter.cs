using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;

namespace ExHyperV.Models
{

    // WMI: Msvm_EthernetSwitchPortVlanSettingData.OperationMode
    /// <summary>虚拟交换机端口的 VLAN 工作模式，对应 Hyper-V OperationMode。</summary>
    public enum VlanOperationMode
    {
        /// <summary>尚未指定或识别模式。</summary>
        Unknown = 0,
        /// <summary>接入模式，端口使用单个接入 VLAN。</summary>
        Access = 1,
        /// <summary>中继模式，使用本征 VLAN 和允许 VLAN 列表。</summary>
        Trunk = 2,
        /// <summary>专用 VLAN 模式。</summary>
        Private = 3
    }

    // WMI: Msvm_EthernetSwitchPortVlanSettingData.PvlanMode
    /// <summary>专用 VLAN 中的端口隔离角色。</summary>
    public enum PvlanMode
    {
        /// <summary>不使用专用 VLAN 角色。</summary>
        None = 0,
        /// <summary>隔离端口。</summary>
        Isolated = 1,
        /// <summary>社区端口。</summary>
        Community = 2,
        /// <summary>混杂端口。</summary>
        Promiscuous = 3
    }

    // WMI: Msvm_EthernetSwitchPortSecuritySettingData.MonitorMode
    /// <summary>虚拟交换机端口镜像的角色。</summary>
    public enum PortMonitorMode
    {
        /// <summary>不参与端口镜像。</summary>
        None = 0,
        /// <summary>接收镜像流量的目标端口。</summary>
        Destination = 1,
        /// <summary>提供被镜像流量的源端口。</summary>
        Source = 2
    }


    /// <summary>聚合多个 Hyper-V 网络 WMI 类的网卡设置。</summary>
    /// <remarks>包含网卡设置与采样数据，修改通过相应服务提交。设备和交换机分别用 DeviceId、SwitchId 标识，名称仅供显示。</remarks>
    public partial class VmNetworkAdapter : ObservableObject
    {
        /// <summary>当前发送速率，单位为字节每秒。</summary>
        [ObservableProperty]
        private ulong _sendBytesPerSecond;

        /// <summary>当前接收速率，单位为字节每秒。</summary>
        [ObservableProperty]
        private ulong _receiveBytesPerSecond;

        /// <summary>是否已取得有效速率采样；false 时速率字段不能作为有效测量值。</summary>
        [ObservableProperty]
        private bool _hasNetworkSpeedSample;

        /// <summary>本次收发速率采样状态与时间；无效时保留此前速率字段，但它们不是本次新样本。</summary>
        [ObservableProperty]
        private PerformanceSampleInfo _performanceSample = new(PerformanceSampleState.NotSampled, default);

        internal string Id { get; set; } = string.Empty;

        /// <summary>虚拟网卡的设备 GUID；尚未从服务取得有效身份时为 Guid.Empty。</summary>
        public Guid DeviceId
        {
            get
            {
                // 合成网卡为 Microsoft:VMGUID\DEVICEGUID；模拟网卡末尾另有 \0。
                var segments = Id.Split('\\');
                return segments.Length > 1 && Guid.TryParse(segments[1], out var id) ? id : Guid.Empty;
            }
        }

        /// <summary>虚拟网卡名称。</summary>
        [ObservableProperty]
        private string _name = string.Empty;

        // 指示网卡是否已连接（模拟网线插拔）。 WMI: Msvm_EthernetPortAllocationSettingData.EnabledState (2=true, 3=false)
        /// <summary>网卡连接开关，表示模拟网线是否接通。</summary>
        [ObservableProperty]
        private bool _isConnected;

        // 原始交换机身份：空串为未连接，null 为查询未能确定名称；文案不参与业务判定。
        // WMI: Msvm_EthernetPortAllocationSettingData.HostResource
        private string? _switchIdentity = string.Empty;
        /// <summary>原始交换机名称；空字符串表示未连接，null 表示查询未确定。已有选择会拒绝空白或 WMI_ 占位覆盖。</summary>
        internal string? SwitchIdentity
        {
            get => _switchIdentity;
            set
            {
                // 保留已有有效选择，避免一次不完整刷新覆盖它。
                if (_switchIdentity != string.Empty &&
                    (string.IsNullOrWhiteSpace(value) || value.StartsWith("WMI_"))) return;
                if (_switchIdentity == value) return;
                _switchIdentity = value;
                OnPropertyChanged(nameof(SwitchIdentity));
                OnPropertyChanged(nameof(SwitchName));
            }
        }

        /// <summary>查询得到的目标交换机 GUID；无连接目标或无法确认时为 null。修改连接时显式向 UpdateConnectionAsync 传入目标 GUID。</summary>
        public Guid? SwitchId { get; internal set; }

        /// <summary>交换机的显示名称；修改此值不改变连接目标，连接操作仅使用显式传入的交换机 GUID。</summary>
        public string SwitchName
        {
            get => _switchIdentity == null ? Properties.Resources.Common_UnknownSwitch
                : _switchIdentity.Length == 0 ? Properties.Resources.Status_Unconnected : _switchIdentity;
            set
            {
                SwitchIdentity = IsLegacySwitchLabel(value, "Status_Unconnected") ? string.Empty
                    : IsLegacySwitchLabel(value, "Common_UnknownSwitch") ? null : value;
            }
        }

        private static bool IsLegacySwitchLabel(string value, string resourceName) =>
            value == Properties.Resources.ResourceManager.GetString(resourceName, CultureInfo.GetCultureInfo("en-US")) ||
            value == Properties.Resources.ResourceManager.GetString(resourceName, CultureInfo.GetCultureInfo("zh-CN"));

        /// <summary>虚拟网卡 MAC 地址。</summary>
        [ObservableProperty]
        private string _macAddress = string.Empty;

        // 指示 MAC 地址是否为静态配置。 WMI: Msvm_SyntheticEthernetPortSettingData.StaticMacAddress
        /// <summary>是否使用静态 MAC 地址配置。</summary>
        [ObservableProperty]
        private bool _isStaticMac;

        /// <summary>网卡 IP 地址的可变列表。</summary>
        [ObservableProperty]
        private List<string> _ipAddresses = new List<string>();

        /// <summary>是否允许 MAC 地址欺骗。</summary>
        [ObservableProperty]
        private bool _macSpoofingAllowed;

        /// <summary>是否启用 DHCP 防护。</summary>
        [ObservableProperty]
        private bool _dhcpGuardEnabled;

        /// <summary>是否启用路由器通告防护。</summary>
        [ObservableProperty]
        private bool _routerGuardEnabled;

        /// <summary>是否允许来宾对该网卡进行组队。</summary>
        [ObservableProperty]
        private bool _teamingAllowed;

        /// <summary>广播、组播和未知单播的风暴限制，采用 Hyper-V StormLimit 原始值。</summary>
        [ObservableProperty]
        private uint _stormLimit;

        /// <summary>端口镜像的源、目标或禁用角色。</summary>
        [ObservableProperty]
        private PortMonitorMode _monitorMode;


        /// <summary>是否启用虚拟机队列 VMQ。</summary>
        [ObservableProperty]
        private bool _vmqEnabled;

        /// <summary>是否启用 IPsec 任务卸载。</summary>
        [ObservableProperty]
        private bool _ipsecOffloadEnabled;

        /// <summary>是否启用单根 I/O 虚拟化 SR-IOV。</summary>
        [ObservableProperty]
        private bool _sriovEnabled;

        /// <summary>VLAN 工作模式。</summary>
        [ObservableProperty]
        private VlanOperationMode _vlanMode;

        /// <summary>接入模式下的 VLAN ID。</summary>
        [ObservableProperty]
        private int _accessVlanId;

        /// <summary>中继模式下的本征 VLAN ID。</summary>
        [ObservableProperty]
        private int _nativeVlanId;

        /// <summary>中继模式下允许通过的 VLAN ID 可变列表。</summary>
        [ObservableProperty]
        private List<int> _trunkAllowedVlanIds = new List<int>();

        /// <summary>专用 VLAN 的主 VLAN ID。</summary>
        [ObservableProperty]
        private int _pvlanPrimaryId;

        /// <summary>专用 VLAN 的辅助 VLAN ID。</summary>
        [ObservableProperty]
        private int _pvlanSecondaryId;

        /// <summary>专用 VLAN 端口角色。</summary>
        [ObservableProperty]
        private PvlanMode _pvlanMode;


        /// <summary>最低保留带宽，单位为 bit/s；保留 Hyper-V 原始精度，不作整数 Mbit/s 换算。</summary>
        [ObservableProperty]
        private ulong _bandwidthReservationBitsPerSecond;

        /// <summary>最大带宽限制，单位为 bit/s；保留 Hyper-V 原始精度，不作整数 Mbit/s 换算。</summary>
        [ObservableProperty]
        private ulong _bandwidthLimitBitsPerSecond;


    }
}
