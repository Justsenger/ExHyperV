namespace ExHyperV.Models;

/// <summary>宿主为虚拟机提供的处理器设置项；可用不代表当前电源状态和硬件条件一定允许修改。</summary>
public enum VmProcessorCapability
{
    /// <summary>虚拟处理器数量。</summary>
    ProcessorCount,
    /// <summary>处理器资源保留比例。</summary>
    ResourceReservation,
    /// <summary>处理器资源使用上限。</summary>
    ResourceLimit,
    /// <summary>处理器资源竞争权重。</summary>
    SchedulingWeight,
    /// <summary>向来宾提供嵌套虚拟化。</summary>
    NestedVirtualization,
    /// <summary>宿主资源保护。</summary>
    HostResourceProtection,
    /// <summary>处理器迁移兼容开关。</summary>
    MigrationCompatibility,
    /// <summary>迁移兼容采用的功能集。</summary>
    MigrationFeatureSet,
    /// <summary>旧操作系统兼容。</summary>
    LegacyOperatingSystemCompatibility,
    /// <summary>来宾同时多线程拓扑。</summary>
    SimultaneousMultithreading,
    /// <summary>推测执行控制策略。</summary>
    SpeculationControlPolicy,
    /// <summary>体系结构性能监控。</summary>
    ArchitecturalPerformanceMonitoring,
    /// <summary>ACNT/MCNT 活动计数器。</summary>
    ActivityCounters,
    /// <summary>来宾可见的处理器品牌字符串。</summary>
    ProcessorBrand,
    /// <summary>来宾中断控制器模式。</summary>
    InterruptControllerMode,
    /// <summary>来宾可见的 L3 缓存相联路数。</summary>
    L3CacheAssociativity,
    /// <summary>虚拟处理器在 L3 域之间的分布策略。</summary>
    L3ProcessorDistribution,
    /// <summary>大页拆分缓解策略。</summary>
    LargePageMitigation,
    /// <summary>处理器频率上限。</summary>
    FrequencyMaximum,
    /// <summary>处理器频率下限。</summary>
    FrequencyMinimum,
    /// <summary>期望处理器频率。</summary>
    DesiredFrequency,
    /// <summary>性能与能效偏好。</summary>
    EnergyPerformancePreference,
    /// <summary>自主频率活动窗口。</summary>
    AutonomousActivityWindow,
    /// <summary>忽略宿主最大频率限制。</summary>
    IgnoreHostFrequencyMaximum,
    /// <summary>性能监控单元透传。</summary>
    PerformanceMonitoring,
    /// <summary>最后分支记录。</summary>
    LastBranchRecording,
    /// <summary>精确事件采样。</summary>
    PreciseEventSampling,
    /// <summary>处理器指令跟踪。</summary>
    InstructionTracing,
    /// <summary>硬件隔离扩展。</summary>
    HardwareIsolation,
    /// <summary>硬件隔离来宾数量上限。</summary>
    HardwareIsolatedGuestLimit,
    /// <summary>每插槽处理器簇数量上限。</summary>
    ProcessorClustersPerSocket,
    /// <summary>每个 L3 域的处理器数量上限。</summary>
    ProcessorsPerL3Domain,
    /// <summary>每个 NUMA 节点的处理器数量上限。</summary>
    ProcessorsPerNumaNode,
    /// <summary>每插槽 NUMA 节点数量上限。</summary>
    NumaNodesPerSocket,
    /// <summary>来宾物理地址位宽。</summary>
    PhysicalAddressWidth,
    /// <summary>局部外设中断模式。</summary>
    LocalPeripheralInterruptMode
}
