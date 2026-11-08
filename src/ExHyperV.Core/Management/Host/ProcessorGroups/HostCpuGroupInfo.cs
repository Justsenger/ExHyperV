using ExHyperV.Models;

namespace ExHyperV.Services;

/// <summary>绑定到 CPU 组的 Hyper-V 虚拟机。</summary>
/// <param name="VmId">虚拟机 GUID。</param>
/// <param name="Name">虚拟机显示名称；未取得时为 null。</param>
/// <param name="State">本次查询的虚拟机运行状态。</param>
public sealed record HostCpuGroupMember(Guid VmId, string? Name, VmState State);

/// <summary>宿主 CPU 组的处理器、调度策略与成员快照。</summary>
/// <remarks>组策略适用于 Classic/Core 调度器，修改会影响该组全部使用者。成员列表覆盖 WMI 可见的 Hyper-V 虚拟机；其他 HCS 使用者不在此列表中。修改时将最新快照的 Revision 原样传回 <see cref="HostCpuGroupService"/>。</remarks>
public sealed class HostCpuGroupInfo
{
    internal HostCpuGroupInfo(Guid groupId, uint[]? processors, uint? cpuCap, uint? priority, uint? reserve, uint? weight,
        HostCpuGroupMember[] members, bool complete, string? error, string? revision, DateTimeOffset observedAtUtc)
    {
        GroupId = groupId;
        LogicalProcessors = processors is null ? null : Array.AsReadOnly((uint[])processors.Clone());
        CpuCap = cpuCap; Priority = priority; IdleProcessorReserve = reserve; RelativeWeight = weight;
        Members = Array.AsReadOnly((HostCpuGroupMember[])members.Clone());
        MembersComplete = complete; MembersError = error; Revision = revision; ObservedAtUtc = observedAtUtc;
    }

    /// <summary>CPU 组的唯一标识，用于查询和修改该组。</summary>
    /// <fielddoc>
    /// <meaning>CPU 组的唯一标识，用于查询和修改该组。</meaning>
    /// <values kind="format">GUID 文本由 32 个十六进制字符和 4 个连字符组成，按 8-4-4-4-12 分组。</values>
    /// </fielddoc>
    public Guid GroupId { get; }
    /// <summary>该 CPU 组包含的宿主逻辑处理器。</summary>
    /// <fielddoc>
    /// <meaning>该 CPU 组包含的宿主逻辑处理器。</meaning>
    /// <values kind="format">HCS 逻辑处理器编号列表；未取得时为 null。</values>
    /// </fielddoc>
    public IReadOnlyList<uint>? LogicalProcessors { get; }
    /// <summary>CPU 组的总算力配额，由组内所有成员共同使用。</summary>
    /// <fielddoc>
    /// <meaning>CPU 组的总算力配额，由组内所有成员共同使用。</meaning>
    /// <values kind="format">0 至 65536；65536 为满额，32768 为 50%。未取得时为 null。</values>
    /// <modification kind="supported" input="cpuCap" submission="单字段修改并回读"><see cref="HostCpuGroupService.SetCpuCapAsync"/>。</modification>
    /// </fielddoc>
    public uint? CpuCap { get; }
    /// <summary>CPU 组在竞争调度时的优先级，数值越大优先级越高。</summary>
    /// <fielddoc>
    /// <meaning>CPU 组在竞争调度时的优先级，数值越大优先级越高。</meaning>
    /// <values kind="format">0 至 7；未取得时为 null。</values>
    /// <modification kind="supported" input="priority" submission="单字段修改并回读"><see cref="HostCpuGroupService.SetPriorityAsync"/>。</modification>
    /// </fielddoc>
    public uint? Priority { get; }
    /// <summary>组空闲时仍为本组保留、不出借给其他组的逻辑处理器数量。</summary>
    /// <fielddoc>
    /// <meaning>组空闲时仍为本组保留、不出借给其他组的逻辑处理器数量。</meaning>
    /// <values kind="format">单位为个；0 至组内逻辑处理器数量，最多 255。SMT 和资源域容量还可能限制可用数量。未取得时为 null。</values>
    /// <modification kind="supported" input="count" submission="单字段修改并回读"><see cref="HostCpuGroupService.SetIdleProcessorReserveAsync"/>。</modification>
    /// </fielddoc>
    public uint? IdleProcessorReserve { get; }
    /// <summary>CPU 组竞争处理器时间时使用的相对权重。此名称对应尚未公开命名的 HCS 属性 0x40000。</summary>
    /// <fielddoc>
    /// <meaning>CPU 组竞争处理器时间时使用的相对权重。此名称对应尚未公开命名的 HCS 属性 0x40000。</meaning>
    /// <values kind="format">1 至 65535；未取得时为 null。</values>
    /// <modification kind="supported" input="weight" submission="单字段修改并回读"><see cref="HostCpuGroupService.SetRelativeWeightAsync"/>。</modification>
    /// </fielddoc>
    public uint? RelativeWeight { get; }
    /// <summary>本次已确认绑定到该组的 Hyper-V 虚拟机。</summary>
    /// <fielddoc>
    /// <meaning>本次已确认绑定到该组的 Hyper-V 虚拟机。</meaning>
    /// <values kind="format">成员列表；MembersComplete 为 false 时可能不完整。</values>
    /// </fielddoc>
    public IReadOnlyList<HostCpuGroupMember> Members { get; }
    /// <summary>是否已读全 WMI 可见虚拟机的 CPU 组绑定。</summary>
    /// <fielddoc>
    /// <meaning>是否已读全 WMI 可见虚拟机的 CPU 组绑定。</meaning>
    /// <values kind="options">true：读取完整；false：读取不完整，原因见 MembersError。</values>
    /// </fielddoc>
    public bool MembersComplete { get; }
    /// <summary>成员列表未读全的原因。</summary>
    /// <fielddoc>
    /// <meaning>成员列表未读全的原因。</meaning>
    /// <values kind="format">说明文本；列表完整时为 null。</values>
    /// </fielddoc>
    public string? MembersError { get; }
    /// <summary>组配置与成员的版本摘要，用于在修改前检测变化。</summary>
    /// <fielddoc>
    /// <meaning>组配置与成员的版本摘要，用于在修改前检测变化。</meaning>
    /// <values kind="format">修改时原样传回；无法确认组身份、属性或成员时为 null。</values>
    /// </fielddoc>
    public string? Revision { get; }
    /// <summary>本次 CPU 组采集完成的时间。</summary>
    /// <fielddoc>
    /// <meaning>本次 CPU 组采集完成的时间。</meaning>
    /// <values kind="format">UTC 时间，偏移量为 +00:00。</values>
    /// </fielddoc>
    public DateTimeOffset ObservedAtUtc { get; }
}