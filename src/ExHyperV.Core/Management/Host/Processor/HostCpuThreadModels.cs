using ExHyperV.Services;

namespace ExHyperV.Models;

/// <summary>宿主逻辑处理器占用拆分的采集来源；Root 模式下优先使用内核上下文切换事件，降级或 Classic/Core 模式使用性能计数器。</summary>
public enum HostCpuAttributionSource
{
    /// <summary>当前未取得可用的逻辑处理器占用数据源。</summary>
    Unavailable = 0,
    /// <summary>通过 Windows 内核上下文切换事件（ETW CSwitch）精确按逻辑处理器归属宿主机与每台虚拟机。</summary>
    KernelContextSwitch = 1,
    /// <summary>通过 Hyper-V 性能计数器读取每逻辑处理器总占用与宿主 Root VP 占用；仅在调度范围唯一时归属虚拟机，共享时保留为未细分来宾占用。</summary>
    PerformanceCounter = 2
}

/// <summary>单个宿主逻辑处理器上的单个占用主体（宿主机或某台虚拟机）使用率份额。</summary>
public sealed class HostCpuThreadConsumerUsage
{
    /// <summary>所属虚拟机的稳定 GUID；宿主机占用为 null。</summary>
    /// <fielddoc>
    ///   <meaning>占用该逻辑处理器时间片的虚拟机稳定 GUID；当 IsHost 为 true 时为 null。</meaning>
    ///   <values kind="format">RFC 4122 GUID 字符串，或宿主机行固定为 null。</values>
    ///   <modification kind="readonly">运行时只读监控指标，不可直接写入。</modification>
    /// </fielddoc>
    public Guid? VmId { get; init; }

    /// <summary>是否为宿主机（Root Partition 及宿主系统线程）占用。</summary>
    /// <fielddoc>
    ///   <meaning>标识该份额是否归属于宿主机系统与根分区（Host），用以区分来宾虚拟机，不通过名称字符串判断。</meaning>
    ///   <values kind="options">true（宿主机）／ false（来宾虚拟机）</values>
    ///   <modification kind="readonly">运行时只读监控指标，不可直接写入。</modification>
    /// </fielddoc>
    public bool IsHost { get; init; }

    /// <summary>占用主体的显示名称；宿主机固定为 Host，虚拟机为其 WMI ElementName。</summary>
    /// <fielddoc>
    ///   <meaning>用于界面或报表展示的主体名称；虚拟机改名后随最新 WMI 查询结果更新，程序内部关联应使用 VmId。</meaning>
    ///   <modification kind="readonly">来自宿主标识或虚拟机配置显示名，监控接口不直接修改。</modification>
    /// </fielddoc>
    public string Name { get; init; } = string.Empty;

    /// <summary>该主体在当前采样窗口内占用此逻辑处理器的时间百分比。</summary>
    /// <fielddoc>
    ///   <meaning>采样窗口内该主体在该物理逻辑处理器上运行时间占窗口总时长的百分比。</meaning>
    ///   <unit>%（百分比，0 至 100）。</unit>
    ///   <values kind="format">有限非负浮点数，范围为 0 至 100，同核心各主体及未细分来宾之和不超过 100。</values>
    ///   <modification kind="readonly">运行时只读监控指标，不可直接写入。</modification>
    /// </fielddoc>
    public float UsagePercent { get; init; }
}

/// <summary>单个物理逻辑处理器在最近采样窗口内的总占用及按宿主／虚拟机划分的明细。</summary>
public sealed class HostCpuThreadSnapshot
{
    /// <summary>宿主全局逻辑处理器编号（从 0 开始，与 Hyper-V Hv LP 及 ETW ProcessorIndex 一致）。</summary>
    /// <fielddoc>
    ///   <meaning>按 Windows 活动处理器组顺序连续排布的全局 0 基逻辑处理器索引。</meaning>
    ///   <unit>从 0 开始的整数索引。</unit>
    ///   <modification kind="readonly">硬件拓扑只读索引，不可写入。</modification>
    /// </fielddoc>
    public int LogicalProcessorIndex { get; init; }

    /// <summary>该逻辑处理器所属的 Windows 处理器组编号。</summary>
    /// <fielddoc>
    ///   <meaning>多处理器组宿主机上的 Windows 处理器组号；单组宿主机固定为 0。</meaning>
    ///   <unit>从 0 开始的处理器组号。</unit>
    ///   <modification kind="readonly">硬件拓扑只读属性，不可写入。</modification>
    /// </fielddoc>
    public int ProcessorGroup { get; init; }

    /// <summary>该逻辑处理器在其所属处理器组内的位号。</summary>
    /// <fielddoc>
    ///   <meaning>Windows 处理器组掩码中的 0 至 63 位索引。</meaning>
    ///   <values kind="format">0 至 63 整数。</values>
    ///   <modification kind="readonly">硬件拓扑只读属性，不可写入。</modification>
    /// </fielddoc>
    public int GroupProcessorIndex { get; init; }

    /// <summary>混合架构处理器下的核心性能类别。</summary>
    /// <fielddoc>
    ///   <meaning>根据宿主 EfficiencyClass 识别的性能核或能效核类型；同构核或未识别时为 Unknown。</meaning>
    ///   <modification kind="readonly">硬件拓扑只读属性，不可写入。</modification>
    /// </fielddoc>
    public CpuCoreType CoreType { get; init; }

    /// <summary>该逻辑处理器在采样窗口内的总使用率百分比（0 至 100）。</summary>
    /// <fielddoc>
    ///   <meaning>宿主机占比、已归属虚拟机占比及未细分来宾占比之和，经严格归一不超过 100。</meaning>
    ///   <unit>%（百分比，0 至 100）。</unit>
    ///   <modification kind="readonly">运行时只读监控指标，不可直接写入。</modification>
    /// </fielddoc>
    public float TotalUsagePercent { get; init; }

    /// <summary>宿主机系统与根分区在该逻辑处理器上的占用百分比（0 至 100）。</summary>
    /// <fielddoc>
    ///   <meaning>已确认的宿主时间占比。无法分离宿主与来宾时为 null。</meaning>
    ///   <unit>%（百分比，0 至 100）。</unit>
    ///   <modification kind="readonly">运行时只读监控指标，不可直接写入。</modification>
    /// </fielddoc>
    public float? HostUsagePercent { get; init; }

    /// <summary>已测得活动但无法确定属于宿主还是来宾的占比。</summary>
    public float UnattributedUsagePercent { get; init; }

    /// <summary>无法唯一拆分到单台虚拟机的来宾占用百分比（例如 Classic/Core 多虚拟机共享同一逻辑处理器时）。</summary>
    /// <fielddoc>
    ///   <meaning>性能计数器降级模式下可确定属于来宾虚拟机、但因多虚拟机共享核心而无法无歧义按虚拟机拆分的时间百分比。</meaning>
    ///   <unit>%（百分比，0 至 100）。</unit>
    ///   <applicability>使用 KernelContextSwitch 精确采集时通常为 0；Classic/Core 多机并发竞争同一核心时非零。</applicability>
    ///   <modification kind="readonly">运行时只读监控指标，不可直接写入。</modification>
    /// </fielddoc>
    public float UnattributedGuestUsagePercent { get; init; }

    /// <summary>该逻辑处理器上宿主机与各活跃虚拟机的占用明细列表。</summary>
    /// <fielddoc>
    ///   <meaning>包含宿主机与各已归属虚拟机的占用份额条目，所有条目 UsagePercent 与 UnattributedGuestUsagePercent 之和不超过 100。</meaning>
    ///   <modification kind="readonly">运行时只读采样集合，不可写入。</modification>
    /// </fielddoc>
    public IReadOnlyList<HostCpuThreadConsumerUsage> Consumers { get; init; } = [];

    /// <summary>该逻辑处理器本次采样的有效性、观测时间与问题说明。</summary>
    /// <fielddoc>
    ///   <meaning>指示本核心样本是否已完成基线预热且数据有效；仅当 Sample.IsValid 为 true 时数值参与界面展示或统计。</meaning>
    ///   <modification kind="readonly">运行时只读采样元数据，不可写入。</modification>
    /// </fielddoc>
    public PerformanceSampleInfo Sample { get; init; } = new(PerformanceSampleState.NotSampled, default);
}

/// <summary>宿主全量物理逻辑处理器线程的占用归属采样帧，包含调度器模式与数据源说明。</summary>
public sealed class HostCpuThreadReport
{
    /// <summary>本次采样观测到的宿主当前 Hyper-V 调度器模式。</summary>
    /// <fielddoc>
    ///   <meaning>当前正在运行的 Hyper-V 调度器（Root、Classic、Core 或 Unknown），决定是否具备上下文切换精确归属条件。</meaning>
    ///   <modification kind="readonly">当前运行时状态只读反馈，切换需通过 HyperVSchedulerService。</modification>
    /// </fielddoc>
    public HyperVSchedulerType SchedulerType { get; init; }

    /// <summary>本次采样帧实际采用的逻辑处理器归属数据来源。</summary>
    /// <fielddoc>
    ///   <meaning>反映本次报告是由内核 ETW 上下文切换生成、性能计数器降级／经典模式生成还是尚未可用。</meaning>
    ///   <modification kind="readonly">运行时只读采集状态，不可写入。</modification>
    /// </fielddoc>
    public HostCpuAttributionSource AttributionSource { get; init; }

    /// <summary>完成本次采样并归一化结果的 UTC 时间戳。</summary>
    /// <fielddoc>
    ///   <meaning>该帧占用采样帧对应的世界协调时间观测点。</meaning>
    ///   <modification kind="readonly">运行时采样时间戳，不可写入。</modification>
    /// </fielddoc>
    public DateTimeOffset ObservedAtUtc { get; init; }

    /// <summary>本次占用采样帧所统计的时间窗口长度。</summary>
    /// <fielddoc>
    ///   <meaning>相邻两次采样或等待窗口的实际跨度；预热或无效帧可能为零。</meaning>
    ///   <unit>TimeSpan 时间跨度。</unit>
    ///   <modification kind="readonly">运行时采样间隔元数据，不可写入。</modification>
    /// </fielddoc>
    public TimeSpan WindowDuration { get; init; }

    /// <summary>归属来源降级或受限时的附加说明；精确归属且无提示时为 null。</summary>
    /// <fielddoc>
    ///   <meaning>用于向调用方说明为何处于性能计数器回退模式、为何仅保留未细分来宾占比或权限受限原因。</meaning>
    ///   <absence>ETW 精确归属正常工作或尚未采样时可能为 null。</absence>
    ///   <modification kind="readonly">运行时只读诊断信息，不可写入。</modification>
    /// </fielddoc>
    public string? AttributionNote { get; init; }

    /// <summary>本次整体报告的采样健康状态。</summary>
    /// <fielddoc>
    ///   <meaning>指示本次报告是处于预热、有效、权限／能力受限还是失败状态；应先检查 Sample.IsValid。</meaning>
    ///   <modification kind="readonly">运行时只读采样元数据，不可写入。</modification>
    /// </fielddoc>
    public PerformanceSampleInfo Sample { get; init; } = new(PerformanceSampleState.NotSampled, default);

    /// <summary>宿主全部活动逻辑处理器的占用与归属明细，按物理逻辑编号升序排列。</summary>
    /// <fielddoc>
    ///   <meaning>每个逻辑处理器对应一项，包含总数、宿主占比、已归属虚拟机份额及未细分来宾占比。</meaning>
    ///   <modification kind="readonly">运行时只读监控集合，不可写入。</modification>
    /// </fielddoc>
    public IReadOnlyList<HostCpuThreadSnapshot> Threads { get; init; } = [];
}
