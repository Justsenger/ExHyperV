using ExHyperV.Models;
using ExHyperV.Tools;

namespace ExHyperV.Services;

/// <summary>处理器亲和性的独立可查询字段；可以任意选择其中一项，不要求整组读取或返回。</summary>
public static class VmAffinityFields
{
    /// <summary>实际处理器选择：亲和性后端当前应用的宿主逻辑处理器编号列表。</summary>
    /// <fielddoc>
    /// <meaning>亲和性后端当前应用的宿主逻辑处理器编号列表。</meaning>
    /// <unit>处理器编号列表</unit>
    /// <values>编号采用当前亲和性后端的编号约定；HCS 处理器编号与 Windows 处理器组中的掩码位应分别解释。</values>
    /// <source>CpuAffinityService.ReadStateAsync 的只读结果，读取或派生 AppliedProcessors。</source>
    /// <references><see cref="CpuAffinityService"/>。Classic/Core 处理器组的总配额、调度优先级、空闲逻辑处理器保留数量和相对调度权重由 <see cref="HostCpuGroupService"/> 查询及修改；它们不属于 Root 进程亲和性。</references>
    /// <modification kind="readonly" />
    /// </fielddoc>
    public static readonly VmQueryField<IReadOnlyList<int>> AppliedProcessors = new("Affinity.AppliedProcessors", "实际处理器选择", "处理器", VmQueryCollection.VirtualMachine, "Affinity", "AppliedProcessors");

    /// <summary>保存的处理器选择：为虚拟机保存的宿主逻辑处理器选择列表。</summary>
    /// <fielddoc>
    /// <meaning>为虚拟机保存的宿主逻辑处理器选择列表。</meaning>
    /// <applicability>保存的选择保留原编号，环境匹配状态见 SavedPreferenceStatus；当前生效状态见 AppliedProcessors。</applicability>
    /// <source>CpuAffinityService.ReadStateAsync 的只读结果，读取或派生 SavedProcessors。</source>
    /// <references><see cref="CpuAffinityService"/>。可选逻辑处理器及插槽、核心、NUMA 信息见 <see cref="HostProcessorService.GetAffinityProcessorsAsync"/>；组策略见 <see cref="HostCpuGroupService"/>。</references>
    /// <modification kind="supported" input="coreIndices" submission="应用并保存"><see cref="M:ExHyperV.Services.CpuAffinityService.SaveAsync(System.Guid,System.Collections.Generic.List{System.Int32})"/></modification>
    /// <values kind="format">宿主逻辑处理器编号的只读列表，例如 <c>[0, 1, 2, 3]</c>。</values>
    /// </fielddoc>
    public static readonly VmQueryField<IReadOnlyList<int>> SavedProcessors = new("Affinity.SavedProcessors", "保存的处理器选择", "处理器", VmQueryCollection.VirtualMachine, "Affinity", "SavedProcessors");

    /// <summary>保存配置的适用状态：保存的处理器选择是否仍属于当前宿主、后端和编号映射。</summary>
    /// <fielddoc>
    /// <meaning>判断以前保存的处理器选择能否在当前宿主继续使用。宿主、调度器或处理器编号发生变化时，需要重新选择；当前实际使用哪些处理器由“实际处理器选择”提供。</meaning>
    /// <source>CpuAffinityService.ReadStateAsync 比较元数据与当前宿主 UUID、亲和性后端和处理器拓扑摘要。</source>
    /// <applicability>保存记录缺少宿主或处理器编号信息时，适用状态为无法核实。</applicability>
    /// <references><see cref="CpuAffinityService.ReadStateAsync"/>；<see cref="M:ExHyperV.Services.CpuAffinityService.SaveAsync(System.Guid,System.Collections.Generic.List{System.Int32})"/>。</references>
    /// <modification kind="readonly" />
    /// </fielddoc>
    public static readonly VmQueryField<CpuAffinityService.PreferenceStatus> SavedPreferenceStatus = new("Affinity.SavedPreferenceStatus", "保存配置的适用状态", "处理器", VmQueryCollection.VirtualMachine, "Affinity", "SavedPreferenceStatus");

    /// <summary>需要重新选择的原因：无法复用保存的处理器选择时的具体原因。</summary>
    /// <fielddoc>
    /// <meaning>说明缺少保存环境、后端不同、宿主或编号映射变化等情况，供调用方提示重新选择。</meaning>
    /// <unit>说明文本；程序判断使用 SavedPreferenceStatus。</unit>
    /// <source>CpuAffinityService.ReadStateAsync.SavedPreferenceIssue。</source>
    /// <absence>无需说明时为空，自由查询返回 Unavailable；结合 SavedPreferenceStatus 判断是否需要处理。</absence>
    /// <references><see cref="CpuAffinityService.ReadStateAsync"/>。</references>
    /// <modification kind="readonly" />
    /// </fielddoc>
    public static readonly VmQueryField<string> SavedPreferenceIssue = new("Affinity.SavedPreferenceIssue", "需要重新选择的原因", "处理器", VmQueryCollection.VirtualMachine, "Affinity", "SavedPreferenceIssue");

    internal static readonly VmQueryField[] Items = [AppliedProcessors, SavedProcessors, SavedPreferenceStatus, SavedPreferenceIssue];
}
