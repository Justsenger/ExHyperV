using ExHyperV.Models;
using ExHyperV.Tools;

namespace ExHyperV.Services;

/// <summary>PCIe的独立可查询字段；可以任意选择其中一项，不要求整组读取或返回。</summary>
public static class VmPcieDeviceFields
{
    /// <summary>PCIe 设备身份：已分配 PCIe 设备的宿主设备标识。</summary>
    /// <fielddoc>
    /// <meaning>已分配 PCIe 设备的宿主设备标识。</meaning>
    /// <source>PciExpressSettingData 与宿主 PCIe 设备身份清单，读取或派生 DeviceInstanceId。</source>
    /// <references><see cref="VmPcieService"/>。</references>
    /// <modification kind="operation" submission="操作流程">设备归属随 PCIe 分配流程变化，硬件身份本身不被覆盖。<see cref="PcieAssignmentSession.AdvanceAsync"/></modification>
    /// </fielddoc>
    public static readonly VmQueryField<string> DeviceInstanceId = new("PcieDevice.DeviceInstanceId", "PCIe 设备身份", "PCIe", VmQueryCollection.PcieDevices, "PcieDevices", "DeviceInstanceId");

    /// <summary>PCIe 设备名称：已分配 PCIe 设备的显示名称。</summary>
    /// <fielddoc>
    /// <meaning>已分配 PCIe 设备的显示名称。</meaning>
    /// <source>PciExpressSettingData 与宿主 PCIe 设备身份清单，读取或派生 Name。</source>
    /// <references><see cref="VmPcieService"/>。</references>
    /// <modification kind="readonly">宿主设备信息；虚拟机设置不会重命名、改变厂商或重写设备位置。</modification>
    /// </fielddoc>
    public static readonly VmQueryField<string> Name = new("PcieDevice.Name", "PCIe 设备名称", "PCIe", VmQueryCollection.PcieDevices, "PcieDevices", "Name");

    /// <summary>PCIe 设备类别：已分配 PCIe 设备的类别名称。</summary>
    /// <fielddoc>
    /// <meaning>已分配 PCIe 设备的类别名称。</meaning>
    /// <source>PciExpressSettingData 与宿主 PCIe 设备身份清单，读取或派生 ClassType。</source>
    /// <references><see cref="VmPcieService"/>。</references>
    /// <modification kind="readonly">宿主设备信息；虚拟机设置不会重命名、改变厂商或重写设备位置。</modification>
    /// </fielddoc>
    public static readonly VmQueryField<string> ClassType = new("PcieDevice.ClassType", "PCIe 设备类别", "PCIe", VmQueryCollection.PcieDevices, "PcieDevices", "ClassType");

    /// <summary>PCIe 设备厂商：已分配 PCIe 设备的厂商名称。</summary>
    /// <fielddoc>
    /// <meaning>已分配 PCIe 设备的厂商名称。</meaning>
    /// <source>PciExpressSettingData 与宿主 PCIe 设备身份清单，读取或派生 Vendor。</source>
    /// <references><see cref="VmPcieService"/>。</references>
    /// <modification kind="readonly">宿主设备信息；虚拟机设置不会重命名、改变厂商或重写设备位置。</modification>
    /// </fielddoc>
    public static readonly VmQueryField<string> Vendor = new("PcieDevice.Vendor", "PCIe 设备厂商", "PCIe", VmQueryCollection.PcieDevices, "PcieDevices", "Vendor");

    /// <summary>PCIe 设备位置：已分配 PCIe 设备所引用的宿主资源路径。</summary>
    /// <fielddoc>
    /// <meaning>已分配 PCIe 设备所引用的宿主资源路径。</meaning>
    /// <unit>设备资源路径</unit>
    /// <source>PciExpressSettingData 与宿主 PCIe 设备身份清单，读取或派生 Path。</source>
    /// <references><see cref="VmPcieService"/>。</references>
    /// <modification kind="readonly">宿主设备信息；虚拟机设置不会重命名、改变厂商或重写设备位置。</modification>
    /// </fielddoc>
    public static readonly VmQueryField<string> Path = new("PcieDevice.Path", "PCIe 设备位置", "PCIe", VmQueryCollection.PcieDevices, "PcieDevices", "Path");

    /// <summary>PCIe 来宾模式：PCIe 设备向来宾呈现的访问模式。</summary>
    /// <fielddoc>
    /// <meaning>PCIe 设备向来宾呈现的访问模式。</meaning>
    /// <source>PciExpressSettingData 与宿主 PCIe 设备身份清单，读取或派生 GuestMode。</source>
    /// <references><see cref="VmPcieService"/>。</references>
    /// <modification kind="supported" input="device、mode" submission="单设备提交">可用模式取决于设备能力和当前归属。<see cref="VmPcieService.SetDeviceModeAsync"/></modification>
    /// </fielddoc>
    public static readonly VmQueryField<VmPcieGuestMode> GuestMode = new("PcieDevice.GuestMode", "PCIe 来宾模式", "PCIe", VmQueryCollection.PcieDevices, "PcieDevices", "GuestMode");

    internal static readonly VmQueryField[] Items = [DeviceInstanceId, Name, ClassType, Vendor, Path, GuestMode];
}
