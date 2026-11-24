using ExHyperV.Models;

namespace ExHyperV.Services;

/// <summary>宿主 PCIe 设备归属和可分配虚拟机的同次查询结果。</summary>
/// <param name="Devices">PCIe 设备列表及稳定归属身份。</param>
/// <param name="VirtualMachines">带 GUID 与显示名称的虚拟机目标；同名虚拟机作为不同目标保留。</param>
/// <remarks>设备与归属分步采集；执行转移时会重新核对设备身份和实际归属。</remarks>
public sealed record PcieHostInfo(IReadOnlyList<PcieDeviceInfo> Devices,
    IReadOnlyList<PcieAssignmentTarget> VirtualMachines);
