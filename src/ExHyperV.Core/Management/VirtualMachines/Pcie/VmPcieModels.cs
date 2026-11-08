using CommunityToolkit.Mvvm.ComponentModel;

namespace ExHyperV.Models;

/// <summary>来宾访问 PCIe 设备的呈现模式。</summary>
public enum VmPcieGuestMode : byte
{
    /// <summary>半虚拟化设备模式。</summary>
    Paravirtualized = 0,
    /// <summary>仿真设备模式。</summary>
    Emulated = 1,
}

/// <summary>虚拟机 PCIe 系统设置及已分配设备的查询快照；Devices 列表仍可修改，修改不会自动写回 Hyper-V。</summary>
public sealed class VmPcieSettings
{
    /// <summary>宿主是否提供 PCIe 系统级设置对象及 EmulationMode、Topology 属性；不表示当前值可读、已开启仿真或已建立拓扑。</summary>
    public bool SystemSettingsAvailable { get; init; }
    /// <summary>当前是否启用 PCIe 仿真。</summary>
    public bool EmulationEnabled { get; init; }
    /// <summary>Hyper-V 返回的 PCIe 拓扑编码。</summary>
    public ushort Topology { get; init; }
    /// <summary>宿主是否提供 BootPciExpress 属性；不表示已验证具体控制器可以引导。</summary>
    public bool BootPciExpressAvailable { get; init; }
    /// <summary>是否配置启用启动阶段 vPCI；固件与设备限制见 VmPcieService.SetBootPciExpressAsync，true 不表示已经引导成功。</summary>
    public bool BootPciExpress { get; init; }
    /// <summary>已分配 PCIe 设备设置的可变列表。</summary>
    public List<VmPcieDeviceSetting> Devices { get; init; } = [];
}

/// <summary>单个已分配 PCIe 设备的身份和来宾模式设置；编辑 GuestMode 后由服务应用，AppliedGuestMode 保存已应用值。</summary>
public partial class VmPcieDeviceSetting : ObservableObject
{
    /// <summary>设备分配设置对象的 WMI 路径。</summary>
    internal string WmiPath { get; init; } = string.Empty;
    /// <summary>设备分配设置对象的 InstanceID。</summary>
    internal string WmiInstanceId { get; init; } = string.Empty;
    /// <summary>设备的显示名称。</summary>
    public string Name { get; init; } = string.Empty;
    /// <summary>设备分配引用的宿主资源路径。</summary>
    internal string HostResource { get; init; } = string.Empty;
    /// <summary>该设备是否提供来宾模式属性。</summary>
    public bool GuestModeAvailable { get; init; }
    /// <summary>设备 PnP 类别。</summary>
    public string ClassType { get; init; } = string.Empty;
    /// <summary>宿主 PnP 设备实例 ID。</summary>
    public string DeviceInstanceId { get; init; } = string.Empty;
    /// <summary>宿主 PCIe 设备位置路径。</summary>
    public string Path { get; init; } = string.Empty;
    /// <summary>设备制造商的显示名称。</summary>
    public string Vendor { get; init; } = string.Empty;
    /// <summary>当前编辑的来宾设备呈现模式，修改不会自动提交。</summary>
    [ObservableProperty]
    private VmPcieGuestMode _guestMode;

    /// <summary>最后读取或成功应用的来宾模式，用于判断尚未提交的修改。</summary>
    public VmPcieGuestMode AppliedGuestMode { get; set; }
}
