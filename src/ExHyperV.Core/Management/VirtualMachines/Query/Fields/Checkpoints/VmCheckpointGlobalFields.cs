using ExHyperV.Models;
using ExHyperV.Tools;

namespace ExHyperV.Services;

/// <summary>快照的独立可查询字段，可以任意选择其中一项，不要求整组读取或返回。</summary>
public static class VmCheckpointGlobalFields
{
    /// <summary>快照开关：是否允许为虚拟机创建快照。</summary>
    /// <fielddoc>
    /// <meaning>UserSnapshotType 不为 2 时返回 true，这是快照策略的简化开关视图，不区分具体快照类型。</meaning>
    /// <source>当前配置的 Msvm_VirtualSystemSettingData，读取或派生 UserSnapshotType。</source>
    /// <references><see cref="VmCheckpointService"/>。</references>
    /// <modification kind="supported" input="enabled" submission="单项提交">重新启用时开启虚拟机快照功能。<see cref="VmCheckpointService.SetCheckpointsEnabledAsync"/></modification>
    /// <values kind="options"><c>true</c>：允许创建快照，<c>false</c>：禁止创建快照。</values>
    /// </fielddoc>
    public static readonly VmQueryField<bool> Enabled = new("CheckpointGlobal.Enabled", "快照开关", "快照", VmQueryCollection.VirtualMachine, "Settings", "UserSnapshotType", o => Convert.ToByte(o) != 2);

    internal static readonly VmQueryField[] Items = [Enabled];
}
