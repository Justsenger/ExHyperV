using ExHyperV.Models;
using ExHyperV.Tools;

namespace ExHyperV.Services;

/// <summary>检查点的独立可查询字段；可以任意选择其中一项，不要求整组读取或返回。</summary>
public static class VmCheckpointGlobalFields
{
    /// <summary>检查点开关：是否允许为虚拟机创建检查点。</summary>
    /// <fielddoc>
    /// <meaning>UserSnapshotType 不为 2 时返回 true；这是检查点策略的有损开关视图，不区分生产、标准及后备策略。</meaning>
    /// <source>当前配置的 Msvm_VirtualSystemSettingData，读取或派生 UserSnapshotType。</source>
    /// <references><see cref="VmSpacetimeService"/>。</references>
    /// <modification kind="supported" input="enabled" submission="单项提交">重新启用采用标准检查点策略，不恢复关闭前的策略。此开关本身不会创建或恢复检查点。<see cref="VmSpacetimeService.SetCheckpointsEnabledAsync"/></modification>
    /// <values kind="options"><c>true</c>：允许创建检查点；<c>false</c>：禁止创建检查点。</values>
    /// </fielddoc>
    public static readonly VmQueryField<bool> Enabled = new("CheckpointGlobal.Enabled", "检查点开关", "检查点", VmQueryCollection.VirtualMachine, "Settings", "UserSnapshotType", o => Convert.ToByte(o) != 2);

    internal static readonly VmQueryField[] Items = [Enabled];
}
