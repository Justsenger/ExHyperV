using ExHyperV.Models;
using ExHyperV.Tools;

namespace ExHyperV.Services;

/// <summary>PCIe的独立可查询字段；可以任意选择其中一项，不要求整组读取或返回。</summary>
public static class VmPcieFields
{
    /// <summary>PCIe 模拟开关：是否为虚拟机启用 PCIe 仿真。</summary>
    /// <fielddoc>
    /// <meaning>是否为虚拟机启用 PCIe 仿真。</meaning>
    /// <source>Msvm_VirtualSystemPciExpressSettingData，读取或派生 EmulationMode。</source>
    /// <verification>研究规则 released-domain-145:3：系统级 PCIe 设置对象可以在枚举阶段直接构造；对象存在不证明仿真已启用。应读取实际模式与拓扑，读取失败保留失败状态。</verification>
    /// <references><see cref="VmPcieService"/>。</references>
    /// <modification kind="supported" input="enableEmulation、topology" submission="只支持开启">此配置支持开启模拟；false 保持现有模拟状态，不关闭已经开启的模拟。拓扑配置会同时更新。<see cref="M:ExHyperV.Services.VmPcieService.SetSystemSettingsAsync(System.Guid,System.Boolean,System.UInt16)"/>、<see cref="VmPcieChangeSession.ApplyAsync"/></modification>
    /// <values kind="options"><c>true</c>：启用 PCIe 仿真；<c>false</c>：关闭。</values>
    /// </fielddoc>
    public static readonly VmQueryField<bool> EmulationEnabled = new("Pcie.EmulationEnabled", "PCIe 模拟开关", "PCIe", VmQueryCollection.VirtualMachine, "PcieSystem", "EmulationMode", o => Convert.ToUInt16(o) == 1);

    /// <summary>PCIe 拓扑：虚拟机使用的 PCIe 拓扑编码。相关机制暂不明确。</summary>
    /// <fielddoc>
    /// <meaning>虚拟机使用的 PCIe 拓扑编码。相关机制暂不明确。</meaning>
    /// <unit>原始编码</unit>
    /// <source>Msvm_VirtualSystemPciExpressSettingData，读取或派生 Topology。</source>
    /// <references><see cref="VmPcieService"/>。</references>
    /// <modification kind="supported" input="topology、enableEmulation" submission="拓扑与模拟请求一起提交">拓扑与模拟开关共同配置；模拟请求为 false 时保留当前模拟状态，为 true 时开启模拟。<see cref="M:ExHyperV.Services.VmPcieService.SetSystemSettingsAsync(System.Guid,System.Boolean,System.UInt16)"/></modification>
    /// </fielddoc>
    public static readonly VmQueryField<ushort> Topology = new("Pcie.Topology", "PCIe 拓扑", "PCIe", VmQueryCollection.VirtualMachine, "PcieSystem", "Topology");

    /// <summary>PCIe 启动支持：是否启用通过 PCI Express 设备启动的支持。</summary>
    /// <fielddoc>
    /// <meaning>是否启用通过 PCI Express 设备启动的支持。</meaning>
    /// <source>当前配置的 Msvm_VirtualSystemSettingData，读取或派生 BootPciExpress。</source>
    /// <applicability>ARM64 固件 26100.4202、28000.1 对小于 4 KiB 的有效 64 位内存 BAR 存在零页计算缺陷；28000.1450 样本已修正。设备启动还受 NVMe 类码、其他 BAR 条件及实际固件版本影响。</applicability>
    /// <verification>研究确认 ARM64 固件 26100.4202、28000.1 对小于 4 KiB 的有效 64 位内存 BAR 存在零页计算缺陷；28000.1450 样本修正此项，不等于所有 NVMe 均可启动。范围、其他 BAR 限制及证据见 <see cref="M:ExHyperV.Services.VmPcieService.SetBootPciExpressAsync(System.Guid,System.Boolean)"/>。</verification>
    /// <references><see cref="VmPcieService"/>。</references>
    /// <modification kind="supported" input="enabled" submission="单项提交">此启动开关独立于 PCIe 仿真、拓扑和设备模式。<see cref="M:ExHyperV.Services.VmPcieService.SetBootPciExpressAsync(System.Guid,System.Boolean)"/></modification>
    /// <values kind="options"><c>true</c>：启用 PCIe 启动；<c>false</c>：关闭。</values>
    /// </fielddoc>
    public static readonly VmQueryField<bool> BootPciExpress = new("Pcie.BootPciExpress", "PCIe 启动支持", "PCIe", VmQueryCollection.VirtualMachine, "Settings", "BootPciExpress");

    internal static readonly VmQueryField[] Items = [EmulationEnabled, Topology, BootPciExpress];
}
