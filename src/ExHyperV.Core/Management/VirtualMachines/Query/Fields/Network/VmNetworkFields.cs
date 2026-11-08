using ExHyperV.Models;
using ExHyperV.Tools;

namespace ExHyperV.Services;

/// <summary>网络的独立可查询字段；可以任意选择其中一项，不要求整组读取或返回。</summary>
public static class VmNetworkFields
{
    /// <summary>网卡身份：虚拟网卡的标识，与虚拟机标识共同定位该网卡。</summary>
    /// <fielddoc>
    /// <meaning>虚拟网卡的标识，与虚拟机标识共同定位该网卡。</meaning>
    /// <source>合成或仿真网卡的 EthernetPortSettingData，读取或派生 InstanceID。</source>
    /// <references><see cref="VmNetworkService"/>。</references>
    /// <modification kind="readonly">虚拟网卡的身份，不能原地修改；创建另一块网卡会产生另一身份。</modification>
    /// <values kind="options">GUID，常用文本格式为 36 个字符，按 <c>8-4-4-4-12</c> 分组。</values>
    /// </fielddoc>
    public static readonly VmQueryField<Guid> DeviceId = new("Network.DeviceId", "网卡身份", "网络", VmQueryCollection.NetworkAdapters, "Ports", "InstanceID", o => Guid.Parse(o.ToString()!.Split('\\')[1]));

    /// <summary>网卡名称：虚拟网卡的显示名称。</summary>
    /// <fielddoc>
    /// <meaning>虚拟网卡的显示名称。</meaning>
    /// <source>合成或仿真网卡的 EthernetPortSettingData，读取或派生 ElementName。</source>
    /// <references><see cref="VmNetworkService"/>。</references>
    /// <modification kind="unavailable">当前未提供虚拟网卡重命名功能。</modification>
    /// </fielddoc>
    public static readonly VmQueryField<string> Name = new("Network.Name", "网卡名称", "网络", VmQueryCollection.NetworkAdapters, "Ports", "ElementName");

    /// <summary>MAC 地址：虚拟网卡当前配置的 MAC 地址。</summary>
    /// <fielddoc>
    /// <meaning>虚拟网卡当前配置的 MAC 地址。</meaning>
    /// <source>合成或仿真网卡的 EthernetPortSettingData，读取或派生 Address。</source>
    /// <references><see cref="VmNetworkService"/>。</references>
    /// <modification kind="supported" input="adapter、newMac" submission="地址与模式联动">虚拟机未运行时可修改；非空地址启用静态 MAC，空地址恢复动态分配。<see cref="VmNetworkService.SetMacAddressAsync"/></modification>
    /// </fielddoc>
    public static readonly VmQueryField<string> MacAddress = new("Network.MacAddress", "MAC 地址", "网络", VmQueryCollection.NetworkAdapters, "Ports", "Address", o => ExHyperV.Tools.MacAddress.Format(o.ToString()));

    /// <summary>静态 MAC 开关：虚拟网卡是否使用手动指定的静态 MAC 地址。</summary>
    /// <fielddoc>
    /// <meaning>虚拟网卡是否使用手动指定的静态 MAC 地址。</meaning>
    /// <source>合成或仿真网卡的 EthernetPortSettingData，读取或派生 StaticMacAddress。</source>
    /// <references><see cref="VmNetworkService"/>。</references>
    /// <modification kind="supported" input="adapter、newMac" submission="地址与模式联动">MAC 地址配置决定分配模式：指定地址时为静态模式，空地址恢复动态分配。<see cref="VmNetworkService.SetMacAddressAsync"/></modification>
    /// <values kind="options"><c>true</c>：静态 MAC；<c>false</c>：动态 MAC。</values>
    /// </fielddoc>
    public static readonly VmQueryField<bool> IsStaticMac = new("Network.IsStaticMac", "静态 MAC 开关", "网络", VmQueryCollection.NetworkAdapters, "Ports", "StaticMacAddress");

    /// <summary>连接开关：虚拟网卡连接资源是否处于启用状态。</summary>
    /// <fielddoc>
    /// <meaning>虚拟网卡连接资源的启用状态。网络可达性需要调用方另行探测。</meaning>
    /// <source>网卡的 EthernetPortAllocationSettingData 与虚拟交换机清单，读取或派生 IsConnected。</source>
    /// <references><see cref="VmNetworkService"/>。</references>
    /// <modification kind="supported" input="adapter.IsConnected、switchId" submission="连接与交换机一起提交">连接状态与目标交换机共同决定虚拟网卡的接入位置。<see cref="VmNetworkService.UpdateConnectionAsync"/></modification>
    /// <values kind="options"><c>true</c>：连接资源已启用；<c>false</c>：未启用。</values>
    /// </fielddoc>
    public static readonly VmQueryField<bool> IsConnected = new("Network.IsConnected", "连接开关", "网络", VmQueryCollection.NetworkAdapters, "Connections", "IsConnected");

    /// <summary>连接交换机身份：虚拟网卡连接的交换机标识。</summary>
    /// <fielddoc>
    /// <meaning>虚拟网卡连接的交换机标识。</meaning>
    /// <source>网卡的 EthernetPortAllocationSettingData 与虚拟交换机清单，读取或派生 SwitchId。</source>
    /// <absence>未配置目标交换机时返回 NotApplicable。</absence>
    /// <references><see cref="VmNetworkService"/>。</references>
    /// <modification kind="supported" input="switchId、adapter.IsConnected" submission="选择连接目标">连接目标由交换机 GUID 标识。<see cref="VmNetworkService.UpdateConnectionAsync"/></modification>
    /// <values kind="options">GUID，常用文本格式为 36 个字符，按 <c>8-4-4-4-12</c> 分组。</values>
    /// </fielddoc>
    public static readonly VmQueryField<Guid> SwitchId = new("Network.SwitchId", "连接交换机身份", "网络", VmQueryCollection.NetworkAdapters, "Connections", "SwitchId");

    /// <summary>连接交换机名称：虚拟网卡连接的交换机显示名称。</summary>
    /// <fielddoc>
    /// <meaning>虚拟网卡连接的交换机显示名称。</meaning>
    /// <source>网卡的 EthernetPortAllocationSettingData 与虚拟交换机清单，读取或派生 SwitchName。</source>
    /// <references><see cref="VmNetworkService"/>。</references>
    /// <modification kind="supported" input="switchId、adapter.IsConnected" submission="选择连接目标">显示名称不用于识别连接目标；连接目标由交换机 GUID 标识。<see cref="VmNetworkService.UpdateConnectionAsync"/></modification>
    /// </fielddoc>
    public static readonly VmQueryField<string> SwitchName = new("Network.SwitchName", "连接交换机名称", "网络", VmQueryCollection.NetworkAdapters, "Connections", "SwitchName");

    /// <summary>MAC 欺骗：是否允许来宾使用与 Hyper-V 配置不同的源 MAC 地址发送数据。</summary>
    /// <fielddoc>
    /// <meaning>是否允许来宾使用与 Hyper-V 配置不同的源 MAC 地址发送数据。</meaning>
    /// <source>Msvm_EthernetSwitchPortSecuritySettingData，读取或派生 AllowMacSpoofing。</source>
    /// <references><see cref="VmNetworkService"/>；<see href="https://learn.microsoft.com/en-us/windows/win32/hyperv_v2/msvm-ethernetswitchportsecuritysettingdata">微软端口安全字段说明</see>。</references>
    /// <modification kind="supported" input="adapter.MacSpoofingAllowed" submission="明确选择字段"><see cref="VmNetworkService.ApplySecuritySettingsAsync"/></modification>
    /// <values kind="options"><c>true</c>：允许不同的源 MAC；<c>false</c>：禁止。</values>
    /// </fielddoc>
    public static readonly VmQueryField<bool> MacSpoofingAllowed = new("Network.MacSpoofingAllowed", "MAC 欺骗", "网络", VmQueryCollection.NetworkAdapters, "NetSecurity", "AllowMacSpoofing");

    /// <summary>DHCP 防护：是否阻止来宾通过此网卡发送 DHCP Offer 报文。</summary>
    /// <fielddoc>
    /// <meaning>是否阻止来宾通过此网卡发送 DHCP Offer 报文。</meaning>
    /// <source>Msvm_EthernetSwitchPortSecuritySettingData，读取或派生 EnableDhcpGuard。</source>
    /// <references><see cref="VmNetworkService"/>；<see href="https://learn.microsoft.com/en-us/windows/win32/hyperv_v2/msvm-ethernetswitchportsecuritysettingdata">微软端口安全字段说明</see>。</references>
    /// <modification kind="supported" input="adapter.DhcpGuardEnabled" submission="明确选择字段"><see cref="VmNetworkService.ApplySecuritySettingsAsync"/></modification>
    /// <values kind="options"><c>true</c>：阻止 DHCP Offer；<c>false</c>：不启用此保护。</values>
    /// </fielddoc>
    public static readonly VmQueryField<bool> DhcpGuardEnabled = new("Network.DhcpGuardEnabled", "DHCP 防护", "网络", VmQueryCollection.NetworkAdapters, "NetSecurity", "EnableDhcpGuard");

    /// <summary>路由器防护：是否阻止来宾通过此网卡发送路由器通告及重定向报文。</summary>
    /// <fielddoc>
    /// <meaning>是否阻止来宾通过此网卡发送路由器通告及重定向报文。</meaning>
    /// <source>Msvm_EthernetSwitchPortSecuritySettingData，读取或派生 EnableRouterGuard。</source>
    /// <references><see cref="VmNetworkService"/>；<see href="https://learn.microsoft.com/en-us/windows/win32/hyperv_v2/msvm-ethernetswitchportsecuritysettingdata">微软端口安全字段说明</see>。</references>
    /// <modification kind="supported" input="adapter.RouterGuardEnabled" submission="明确选择字段"><see cref="VmNetworkService.ApplySecuritySettingsAsync"/></modification>
    /// <values kind="options"><c>true</c>：阻止路由器通告和重定向；<c>false</c>：不启用此保护。</values>
    /// </fielddoc>
    public static readonly VmQueryField<bool> RouterGuardEnabled = new("Network.RouterGuardEnabled", "路由器防护", "网络", VmQueryCollection.NetworkAdapters, "NetSecurity", "EnableRouterGuard");

    /// <summary>网卡组合：是否允许来宾将此网卡加入网卡组合。</summary>
    /// <fielddoc>
    /// <meaning>是否允许来宾将此网卡加入网卡组合。</meaning>
    /// <source>Msvm_EthernetSwitchPortSecuritySettingData，读取或派生 AllowTeaming。</source>
    /// <references><see cref="VmNetworkService"/>；<see href="https://learn.microsoft.com/en-us/windows/win32/hyperv_v2/msvm-ethernetswitchportsecuritysettingdata">微软端口安全字段说明</see>。</references>
    /// <modification kind="supported" input="adapter.TeamingAllowed" submission="明确选择字段"><see cref="VmNetworkService.ApplySecuritySettingsAsync"/></modification>
    /// <values kind="options"><c>true</c>：允许来宾网卡组合；<c>false</c>：禁止。</values>
    /// </fielddoc>
    public static readonly VmQueryField<bool> TeamingAllowed = new("Network.TeamingAllowed", "网卡组合", "网络", VmQueryCollection.NetworkAdapters, "NetSecurity", "AllowTeaming");

    /// <summary>广播风暴限制：广播和组播流量每秒允许通过的包数量上限。</summary>
    /// <fielddoc>
    /// <meaning>广播和组播流量每秒允许通过的包数量上限。</meaning>
    /// <values kind="format">单位为包/秒；<c>0</c> 表示未配置风暴速率限制。</values>
    /// <source>Msvm_EthernetSwitchPortSecuritySettingData，读取或派生 StormLimit。</source>
    /// <references><see cref="VmNetworkService"/>；<see href="https://learn.microsoft.com/en-us/windows/win32/hyperv_v2/msvm-ethernetswitchportsecuritysettingdata">微软端口安全字段说明</see>。</references>
    /// <modification kind="supported" input="adapter.StormLimit" submission="明确选择字段"><see cref="VmNetworkService.ApplySecuritySettingsAsync"/></modification>
    /// </fielddoc>
    public static readonly VmQueryField<uint> StormLimit = new("Network.StormLimit", "广播风暴限制", "网络", VmQueryCollection.NetworkAdapters, "NetSecurity", "StormLimit");

    /// <summary>端口镜像模式：虚拟网卡在端口镜像中的角色：关闭、来源或接收端。</summary>
    /// <fielddoc>
    /// <meaning>虚拟网卡在端口镜像中的角色：关闭、来源或接收端。</meaning>
    /// <source>Msvm_EthernetSwitchPortSecuritySettingData，读取或派生 MonitorMode。</source>
    /// <references><see cref="VmNetworkService"/>；<see href="https://learn.microsoft.com/en-us/windows/win32/hyperv_v2/msvm-ethernetswitchportsecuritysettingdata">微软端口安全字段说明</see>。</references>
    /// <modification kind="supported" input="adapter.MonitorMode" submission="明确选择字段"><see cref="VmNetworkService.ApplySecuritySettingsAsync"/></modification>
    /// </fielddoc>
    public static readonly VmQueryField<PortMonitorMode> MonitorMode = new("Network.MonitorMode", "端口镜像模式", "网络", VmQueryCollection.NetworkAdapters, "NetSecurity", "MonitorMode");

    /// <summary>虚拟机队列：是否配置启用虚拟机队列（VMQ）卸载。</summary>
    /// <fielddoc>
    /// <meaning>是否配置启用虚拟机队列（VMQ）卸载。</meaning>
    /// <values>VMQOffloadWeight 大于 0 时返回 true，表示配置启用。</values>
    /// <source>Msvm_EthernetSwitchPortOffloadSettingData，读取或派生 VMQOffloadWeight。</source>
    /// <references><see cref="VmNetworkService"/>。</references>
    /// <modification kind="supported" input="adapter.VmqEnabled" submission="明确选择字段"><see cref="VmNetworkService.ApplyOffloadSettingsAsync"/></modification>
    /// </fielddoc>
    public static readonly VmQueryField<bool> VmqEnabled = new("Network.VmqEnabled", "虚拟机队列", "网络", VmQueryCollection.NetworkAdapters, "NetOffload", "VMQOffloadWeight", o => Convert.ToUInt32(o) > 0);

    /// <summary>IPsec 卸载：是否配置启用 IPsec 硬件卸载。</summary>
    /// <fielddoc>
    /// <meaning>是否配置启用 IPsec 硬件卸载。</meaning>
    /// <values>IPSecOffloadLimit 大于 0 时返回 true，表示配置启用。</values>
    /// <source>Msvm_EthernetSwitchPortOffloadSettingData，读取或派生 IPSecOffloadLimit。</source>
    /// <references><see cref="VmNetworkService"/>。</references>
    /// <modification kind="supported" input="adapter.IpsecOffloadEnabled" submission="明确选择字段"><see cref="VmNetworkService.ApplyOffloadSettingsAsync"/></modification>
    /// </fielddoc>
    public static readonly VmQueryField<bool> IpsecOffloadEnabled = new("Network.IpsecOffloadEnabled", "IPsec 卸载", "网络", VmQueryCollection.NetworkAdapters, "NetOffload", "IPSecOffloadLimit", o => Convert.ToUInt32(o) > 0);

    /// <summary>SR-IOV 开关：是否配置启用 SR-IOV 卸载。</summary>
    /// <fielddoc>
    /// <meaning>是否配置启用 SR-IOV 卸载。</meaning>
    /// <values>IOVOffloadWeight 大于 0 时返回 true，表示配置启用。</values>
    /// <source>Msvm_EthernetSwitchPortOffloadSettingData，读取或派生 IOVOffloadWeight。</source>
    /// <references><see cref="VmNetworkService"/>。</references>
    /// <modification kind="supported" input="adapter.SriovEnabled" submission="明确选择字段"><see cref="VmNetworkService.ApplyOffloadSettingsAsync"/></modification>
    /// </fielddoc>
    public static readonly VmQueryField<bool> SriovEnabled = new("Network.SriovEnabled", "SR-IOV 开关", "网络", VmQueryCollection.NetworkAdapters, "NetOffload", "IOVOffloadWeight", o => Convert.ToUInt32(o) > 0);

    /// <summary>VLAN 模式：网卡端口使用的 VLAN 工作模式：接入、干线或专用 VLAN。</summary>
    /// <fielddoc>
    /// <meaning>网卡端口使用的 VLAN 工作模式：接入、干线或专用 VLAN。</meaning>
    /// <values>底层编码 0 在当前接口中归为 Access。</values>
    /// <source>Msvm_EthernetSwitchPortVlanSettingData，读取或派生 OperationMode。</source>
    /// <references><see cref="VmNetworkService"/>。</references>
    /// <modification kind="supported" input="adapter.VlanMode" submission="明确选择字段">切换 VLAN 模式时，旧模式中不兼容的配置会被清除。<see cref="VmNetworkService.ApplyVlanSettingsAsync"/></modification>
    /// </fielddoc>
    public static readonly VmQueryField<VlanOperationMode> VlanMode = new("Network.VlanMode", "VLAN 模式", "网络", VmQueryCollection.NetworkAdapters, "NetVlan", "OperationMode", o => Convert.ToUInt32(o) == 0 ? VlanOperationMode.Access : (VlanOperationMode)Convert.ToUInt32(o));

    /// <summary>访问 VLAN 编号：接入模式下，网卡端口所属的 VLAN 编号。</summary>
    /// <fielddoc>
    /// <meaning>接入模式下，网卡端口所属的 VLAN 编号。</meaning>
    /// <applicability>适用于 Access 模式。</applicability>
    /// <source>Msvm_EthernetSwitchPortVlanSettingData，读取或派生 AccessVlanId。</source>
    /// <references><see cref="VmNetworkService"/>。</references>
    /// <modification kind="supported" input="adapter.AccessVlanId" submission="明确选择字段">切换 VLAN 模式时，旧模式中不兼容的配置会被清除。<see cref="VmNetworkService.ApplyVlanSettingsAsync"/></modification>
    /// <values kind="format">VLAN 的整数编号。</values>
    /// </fielddoc>
    public static readonly VmQueryField<int> AccessVlanId = new("Network.AccessVlanId", "访问 VLAN 编号", "网络", VmQueryCollection.NetworkAdapters, "NetVlan", "AccessVlanId");

    /// <summary>本征 VLAN 编号：干线模式下，用于未标记流量的本征 VLAN 编号。</summary>
    /// <fielddoc>
    /// <meaning>干线模式下，用于未标记流量的本征 VLAN 编号。</meaning>
    /// <applicability>适用于 Trunk 模式。</applicability>
    /// <source>Msvm_EthernetSwitchPortVlanSettingData，读取或派生 NativeVlanId。</source>
    /// <references><see cref="VmNetworkService"/>。</references>
    /// <modification kind="supported" input="adapter.NativeVlanId" submission="明确选择字段">切换 VLAN 模式时，旧模式中不兼容的配置会被清除。<see cref="VmNetworkService.ApplyVlanSettingsAsync"/></modification>
    /// <values kind="format">VLAN 的整数编号。</values>
    /// </fielddoc>
    public static readonly VmQueryField<int> NativeVlanId = new("Network.NativeVlanId", "本征 VLAN 编号", "网络", VmQueryCollection.NetworkAdapters, "NetVlan", "NativeVlanId");

    /// <summary>允许的干线 VLAN 编号：干线模式下，网卡端口允许通过的 VLAN 编号列表。</summary>
    /// <fielddoc>
    /// <meaning>干线模式下，网卡端口允许通过的 VLAN 编号列表。</meaning>
    /// <applicability>适用于 Trunk 模式。</applicability>
    /// <source>Msvm_EthernetSwitchPortVlanSettingData，读取或派生 TrunkVlanIdArray。</source>
    /// <references><see cref="VmNetworkService"/>。</references>
    /// <modification kind="supported" input="adapter.TrunkAllowedVlanIds" submission="明确选择字段">切换 VLAN 模式时，旧模式中不兼容的配置会被清除。<see cref="VmNetworkService.ApplyVlanSettingsAsync"/></modification>
    /// <values kind="format">VLAN 整数编号的只读列表，例如 <c>[10, 20]</c>。</values>
    /// </fielddoc>
    public static readonly VmQueryField<IReadOnlyList<int>> TrunkAllowedVlanIds = new("Network.TrunkAllowedVlanIds", "允许的干线 VLAN 编号", "网络", VmQueryCollection.NetworkAdapters, "NetVlan", "TrunkVlanIdArray", o => Array.AsReadOnly(((Array)o).Cast<object>().Select(Convert.ToInt32).ToArray()));

    /// <summary>专用 VLAN 主编号：专用 VLAN 的主 VLAN 编号。</summary>
    /// <fielddoc>
    /// <meaning>专用 VLAN 的主 VLAN 编号。</meaning>
    /// <applicability>适用于 Private 模式。</applicability>
    /// <source>Msvm_EthernetSwitchPortVlanSettingData，读取或派生 PrimaryVlanId。</source>
    /// <references><see cref="VmNetworkService"/>。</references>
    /// <modification kind="supported" input="adapter.PvlanPrimaryId" submission="明确选择字段">切换 VLAN 模式时，旧模式中不兼容的配置会被清除。<see cref="VmNetworkService.ApplyVlanSettingsAsync"/></modification>
    /// <values kind="format">VLAN 的整数编号。</values>
    /// </fielddoc>
    public static readonly VmQueryField<int> PvlanPrimaryId = new("Network.PvlanPrimaryId", "专用 VLAN 主编号", "网络", VmQueryCollection.NetworkAdapters, "NetVlan", "PrimaryVlanId");

    /// <summary>专用 VLAN 次编号：专用 VLAN 的次 VLAN 编号。</summary>
    /// <fielddoc>
    /// <meaning>专用 VLAN 的次 VLAN 编号。</meaning>
    /// <applicability>适用于 Private 模式。</applicability>
    /// <source>Msvm_EthernetSwitchPortVlanSettingData，读取或派生 SecondaryVlanId。</source>
    /// <references><see cref="VmNetworkService"/>。</references>
    /// <modification kind="supported" input="adapter.PvlanSecondaryId" submission="明确选择字段">切换 VLAN 模式时，旧模式中不兼容的配置会被清除。<see cref="VmNetworkService.ApplyVlanSettingsAsync"/></modification>
    /// <values kind="format">VLAN 的整数编号。</values>
    /// </fielddoc>
    public static readonly VmQueryField<int> PvlanSecondaryId = new("Network.PvlanSecondaryId", "专用 VLAN 次编号", "网络", VmQueryCollection.NetworkAdapters, "NetVlan", "SecondaryVlanId");

    /// <summary>专用 VLAN 模式：网卡端口在专用 VLAN 中的隔离角色。</summary>
    /// <fielddoc>
    /// <meaning>网卡端口在专用 VLAN 中的隔离角色。</meaning>
    /// <applicability>适用于 Private 模式。</applicability>
    /// <source>Msvm_EthernetSwitchPortVlanSettingData，读取或派生 PvlanMode。</source>
    /// <references><see cref="VmNetworkService"/>。</references>
    /// <modification kind="supported" input="adapter.PvlanMode" submission="明确选择字段">切换 VLAN 模式时，旧模式中不兼容的配置会被清除。<see cref="VmNetworkService.ApplyVlanSettingsAsync"/></modification>
    /// </fielddoc>
    public static readonly VmQueryField<PvlanMode> PvlanMode = new("Network.PvlanMode", "专用 VLAN 模式", "网络", VmQueryCollection.NetworkAdapters, "NetVlan", "PvlanMode");

    /// <summary>带宽保留：为虚拟网卡配置的带宽保留量，单位为 bit/s。</summary>
    /// <fielddoc>
    /// <meaning>为虚拟网卡配置的带宽保留量。</meaning>
    /// <source>读取 Msvm_EthernetSwitchPortBandwidthSettingData.Reservation，直接保留 bit/s 原始值。</source>
    /// <references><see cref="VmNetworkService"/>。</references>
    /// <modification kind="supported" input="adapter.BandwidthReservationBitsPerSecond" submission="明确选择字段"><see cref="VmNetworkService.ApplyBandwidthSettingsAsync"/></modification>
    /// <values kind="format">单位为 bit/s；例如 <c>1000000000</c> 表示 1 Gbit/s。</values>
    /// </fielddoc>
    public static readonly VmQueryField<ulong> BandwidthReservationBitsPerSecond = new("Network.BandwidthReservationBitsPerSecond", "带宽保留", "网络", VmQueryCollection.NetworkAdapters, "NetBandwidth", "Reservation", o => Convert.ToUInt64(o));

    /// <summary>带宽上限：为虚拟网卡配置的带宽上限，单位为 bit/s。</summary>
    /// <fielddoc>
    /// <meaning>为虚拟网卡配置的带宽上限。</meaning>
    /// <source>读取 Msvm_EthernetSwitchPortBandwidthSettingData.Limit，直接保留 bit/s 原始值。</source>
    /// <references><see cref="VmNetworkService"/>。</references>
    /// <modification kind="supported" input="adapter.BandwidthLimitBitsPerSecond" submission="明确选择字段"><see cref="VmNetworkService.ApplyBandwidthSettingsAsync"/></modification>
    /// <values kind="format">单位为 bit/s；例如 <c>1000000000</c> 表示 1 Gbit/s。</values>
    /// </fielddoc>
    public static readonly VmQueryField<ulong> BandwidthLimitBitsPerSecond = new("Network.BandwidthLimitBitsPerSecond", "带宽上限", "网络", VmQueryCollection.NetworkAdapters, "NetBandwidth", "Limit", o => Convert.ToUInt64(o));

    /// <summary>IP 地址：关联到虚拟网卡的 IP 地址列表，优先采用已有宿主 ARP 嗅探结果。</summary>
    /// <fielddoc>
    /// <meaning>关联到虚拟网卡的 IP 地址列表，优先采用已有宿主 ARP 嗅探结果。</meaning>
    /// <values>按宿主 ARP 嗅探、来宾上报、系统邻居缓存的顺序，采用首个有有效地址的来源。</values>
    /// <applicability>使用宿主嗅探结果前，由宿主程序启动 ARP 嗅探服务。</applicability>
    /// <source>已存在的 ARP 嗅探缓存、来宾网络配置与系统邻居缓存，读取或派生 IpAddresses。</source>
    /// <references><see cref="VmNetworkService"/>。</references>
    /// <modification kind="readonly">这是观测到的来宾 IP 地址；当前 Core 不提供来宾系统内的 IP 配置接口。</modification>
    /// </fielddoc>
    public static readonly VmQueryField<IReadOnlyList<string>> IpAddresses = new("Network.IpAddresses", "IP 地址", "网络", VmQueryCollection.NetworkAdapters, "IpAddresses", "IpAddresses");

    /// <summary>IP 地址来源：本次 IP 地址列表采用的数据来源。</summary>
    /// <fielddoc>
    /// <meaning>本次 IP 地址列表采用的数据来源。</meaning>
    /// <values kind="options">HostArp：宿主嗅探；Guest：来宾上报；NeighborCache：系统邻居缓存；None：暂无来源。</values>
    /// <source>已存在的 ARP 嗅探缓存、来宾网络配置与系统邻居缓存，读取或派生 IpAddressSource。</source>
    /// <references><see cref="VmNetworkService"/>。</references>
    /// <modification kind="readonly">查询引擎实际采用的地址来源，不是调用方可写的网卡配置。</modification>
    /// </fielddoc>
    public static readonly VmQueryField<string> IpAddressSource = new("Network.IpAddressSource", "IP 地址来源", "网络", VmQueryCollection.NetworkAdapters, "IpAddresses", "IpAddressSource");

    /// <summary>网卡发送速率：虚拟网卡当前的发送速率，单位为字节每秒。</summary>
    /// <fielddoc>
    /// <meaning>虚拟网卡当前的发送速率。</meaning>
    /// <source>由 Windows 格式化性能提供者批量读取 BytesSentPersec，按网卡 GUID 唯一关联，提供 SendBytesPerSecond；同源字段合并读取，不是一字段一次调用。</source>
    /// <references><see cref="VmNetworkService"/>。</references>
    /// <modification kind="readonly">网络流量采样值，不是带宽上限或保留量。</modification>
    /// <values kind="format">单位为字节/秒；例如 <c>1048576</c> 表示 1 MiB/s。</values>
    /// </fielddoc>
    public static readonly VmQueryField<ulong> SendBytesPerSecond = new("Network.SendBytesPerSecond", "网卡发送速率", "网络", VmQueryCollection.NetworkAdapters, "NetworkPerformance", "SendBytesPerSecond");

    /// <summary>网卡接收速率：虚拟网卡当前的接收速率，单位为字节每秒。</summary>
    /// <fielddoc>
    /// <meaning>虚拟网卡当前的接收速率。</meaning>
    /// <source>由 Windows 格式化性能提供者批量读取 BytesReceivedPersec，按网卡 GUID 唯一关联，提供 ReceiveBytesPerSecond；同源字段合并读取，不是一字段一次调用。</source>
    /// <references><see cref="VmNetworkService"/>。</references>
    /// <modification kind="readonly">网络流量采样值，不是带宽上限或保留量。</modification>
    /// <values kind="format">单位为字节/秒；例如 <c>1048576</c> 表示 1 MiB/s。</values>
    /// </fielddoc>
    public static readonly VmQueryField<ulong> ReceiveBytesPerSecond = new("Network.ReceiveBytesPerSecond", "网卡接收速率", "网络", VmQueryCollection.NetworkAdapters, "NetworkPerformance", "ReceiveBytesPerSecond");

    internal static readonly VmQueryField[] Items = [DeviceId, Name, MacAddress, IsStaticMac, IsConnected, SwitchId, SwitchName, MacSpoofingAllowed, DhcpGuardEnabled, RouterGuardEnabled, TeamingAllowed, StormLimit, MonitorMode, VmqEnabled, IpsecOffloadEnabled, SriovEnabled, VlanMode, AccessVlanId, NativeVlanId, TrunkAllowedVlanIds, PvlanPrimaryId, PvlanSecondaryId, PvlanMode, BandwidthReservationBitsPerSecond, BandwidthLimitBitsPerSecond, IpAddresses, IpAddressSource, SendBytesPerSecond, ReceiveBytesPerSecond];
}
