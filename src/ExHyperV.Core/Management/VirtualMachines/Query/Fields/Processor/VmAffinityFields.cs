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
    /// <modification kind="readonly">实际生效结果。SaveAsync 同时涉及应用和保存，应读取返回结果确认；不能独立给此观测字段赋值。 相关入口：<see cref="CpuAffinityService.SaveAsync"/>。</modification>
    /// </fielddoc>
    public static readonly VmQueryField<IReadOnlyList<int>> AppliedProcessors = new("Affinity.AppliedProcessors", "实际处理器选择", "处理器", VmQueryCollection.VirtualMachine, "Affinity", "AppliedProcessors");

    /// <summary>保存的处理器选择：为虚拟机保存的宿主逻辑处理器选择列表。</summary>
    /// <fielddoc>
    /// <meaning>为虚拟机保存的宿主逻辑处理器选择列表。</meaning>
    /// <applicability>两种后端均返回保存的选择。旧宿主或旧后端的编号也保留，使用前检查 SavedPreferenceStatus；不能把它直接当成当前宿主的可选编号。当前应用状态由 AppliedProcessors 提供。</applicability>
    /// <source>CpuAffinityService.ReadStateAsync 的只读结果，读取或派生 SavedProcessors。</source>
    /// <references><see cref="CpuAffinityService"/>。可选逻辑处理器及插槽、核心、NUMA 信息见 <see cref="HostProcessorService.GetAffinityProcessorsAsync"/>；组策略见 <see cref="HostCpuGroupService"/>。</references>
    /// <modification kind="supported" input="coreIndices" submission="应用并保存">所选处理器属于宿主。空集合表示不限制；Root 调度器下，停机时仅保存选择，启动后恢复应用。保存状态与实际应用状态分别记录。<see cref="CpuAffinityService.SaveAsync"/><see cref="CpuAffinityService.ApplySavedAffinityAfterStartAsync"/></modification>
    /// <values kind="format">宿主逻辑处理器编号的只读列表，例如 <c>[0, 1, 2, 3]</c>。</values>
    /// </fielddoc>
    public static readonly VmQueryField<IReadOnlyList<int>> SavedProcessors = new("Affinity.SavedProcessors", "保存的处理器选择", "处理器", VmQueryCollection.VirtualMachine, "Affinity", "SavedProcessors");

    /// <summary>亲和性环境匹配：保存的处理器选择是否仍属于当前宿主、后端和编号映射。</summary>
    /// <fielddoc>
    /// <meaning>判断保存偏好能否复用；与实际是否已经应用亲和性分开。</meaning>
    /// <source>CpuAffinityService.ReadStateAsync 比较元数据与当前宿主 UUID、亲和性后端和处理器拓扑摘要。</source>
    /// <applicability>旧编号数组和版本 1 非空记录缺少环境摘要，需要重新选择并保存。当前查询不自动应用或删除选择。</applicability>
    /// <references><see cref="CpuAffinityService.ReadStateAsync"/>；<see cref="CpuAffinityService.SaveAsync"/>。</references>
    /// <modification kind="readonly">由 Core 核实。调用方通过 SaveAsync 明确保存当前宿主上的新选择，不直接改此状态。</modification>
    /// </fielddoc>
    public static readonly VmQueryField<CpuAffinityService.PreferenceStatus> SavedPreferenceStatus = new("Affinity.SavedPreferenceStatus", "亲和性环境匹配", "处理器", VmQueryCollection.VirtualMachine, "Affinity", "SavedPreferenceStatus");

    /// <summary>亲和性环境说明：无法复用保存的处理器选择时的具体原因。</summary>
    /// <fielddoc>
    /// <meaning>说明缺少保存环境、后端不同、宿主或编号映射变化等情况，供调用方提示重新选择。</meaning>
    /// <unit>说明文本；程序判断使用 SavedPreferenceStatus。</unit>
    /// <source>CpuAffinityService.ReadStateAsync.SavedPreferenceIssue。</source>
    /// <absence>无需说明时为空，自由查询返回 Unavailable；结合 SavedPreferenceStatus 判断是否需要处理。</absence>
    /// <references><see cref="CpuAffinityService.ReadStateAsync"/>。</references>
    /// <modification kind="readonly">由环境核实结果生成，不提供文本修改入口。</modification>
    /// </fielddoc>
    public static readonly VmQueryField<string> SavedPreferenceIssue = new("Affinity.SavedPreferenceIssue", "亲和性环境说明", "处理器", VmQueryCollection.VirtualMachine, "Affinity", "SavedPreferenceIssue");

    /// <summary>关联进程编号：Root 亲和性后端关联的虚拟机工作进程编号。</summary>
    /// <fielddoc>
    /// <meaning>Root 亲和性后端关联的虚拟机工作进程编号。</meaning>
    /// <applicability>适用于 Root 亲和性后端，与 ProcessCreationTime 一起标识工作进程。</applicability>
    /// <source>CpuAffinityService.ReadStateAsync 的只读结果，读取或派生 ProcessId。</source>
    /// <absence>未关联工作进程时返回 Unavailable。</absence>
    /// <references><see cref="CpuAffinityService"/>。</references>
    /// <modification kind="readonly">由系统分配的关联进程编号。</modification>
    /// <values kind="format">Windows 进程编号（PID），正整数。</values>
    /// </fielddoc>
    public static readonly VmQueryField<int> ProcessId = new("Affinity.ProcessId", "关联进程编号", "处理器", VmQueryCollection.VirtualMachine, "Affinity", "ProcessId");

    /// <summary>关联进程创建时间：关联工作进程的创建时间，与进程编号共同标识该进程实例。</summary>
    /// <fielddoc>
    /// <meaning>关联工作进程的创建时间，与进程编号共同标识该进程实例。</meaning>
    /// <unit>UTC FILETIME，100 纳秒为一单位</unit>
    /// <values>自 1601-01-01 UTC 起累计的 100 纳秒间隔数。</values>
    /// <applicability>适用于 Root 亲和性后端。</applicability>
    /// <source>CpuAffinityService.ReadStateAsync 的只读结果，读取或派生 ProcessCreationTime。</source>
    /// <references><see cref="CpuAffinityService"/>。</references>
    /// <modification kind="readonly">关联进程的创建时间，用于区别进程编号重用。</modification>
    /// </fielddoc>
    public static readonly VmQueryField<long> ProcessCreationTime = new("Affinity.ProcessCreationTime", "关联进程创建时间", "处理器", VmQueryCollection.VirtualMachine, "Affinity", "ProcessCreationTime");

    internal static readonly VmQueryField[] Items = [AppliedProcessors, SavedProcessors, SavedPreferenceStatus, SavedPreferenceIssue, ProcessId, ProcessCreationTime];
}
