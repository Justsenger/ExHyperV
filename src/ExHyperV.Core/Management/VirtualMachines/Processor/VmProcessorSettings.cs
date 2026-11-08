using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.Frozen;

namespace ExHyperV.Models
{
    /// <summary>SMT（同时多线程）模式：继承主机 / 单线程 / 多线程。</summary>
    public enum SmtMode
    {
        /// <summary>继承宿主的每核心线程策略。</summary>
        Inherit,
        /// <summary>每个核心呈现一个线程。</summary>
        SingleThread,
        /// <summary>启用每核心多线程呈现。</summary>
        MultiThread
    }

    /// <summary>APIC 模式（Msvm_ProcessorSettingData.ApicMode，u8）：自动 / 强制 xAPIC / 强制 x2APIC / Apic。改 guest CPUID 0x01 ECX[21] x2APIC 位。</summary>
    public enum VmApicMode : byte
    {
        /// <summary>使用 Hyper-V 默认 APIC 模式。</summary>
        Default = 0,
        /// <summary>请求传统 xAPIC 模式。</summary>
        Legacy = 1,
        /// <summary>请求 x2APIC 模式。</summary>
        X2Apic = 2,
        /// <summary>使用 Hyper-V Apic 编码 3。</summary>
        Apic = 3
    }

    /// <summary>L3 处理器分布策略（L3ProcessorDistributionPolicy，u8）：VP 在各虚拟 L3 缓存域之间的分布次序。</summary>
    public enum L3DistributionPolicy : byte
    {
        /// <summary>按小到大顺序填充分布域。</summary>
        SmallToLarge = 0,
        /// <summary>按大到小顺序填充分布域。</summary>
        LargeToSmall = 1,
        /// <summary>均匀分布，按小到大顺序选择域。</summary>
        EvenSmallToLarge = 2,
        /// <summary>均匀分布，按大到小顺序选择域。</summary>
        EvenLargeToSmall = 3
    }

    /// <summary>大页拆分缓解策略（EnablePageShattering，u8）：Default 使用主机策略，另外两个值明确请求启用或禁用缓解。</summary>
    public enum PageShatterMode : byte
    {
        /// <summary>继承宿主的大页拆分缓解策略。</summary>
        Default = 0,
        /// <summary>明确启用大页拆分缓解。</summary>
        AlwaysEnabled = 1,
        /// <summary>明确禁用大页拆分缓解。</summary>
        AlwaysDisabled = 2
    }

    /// <summary>局部外设中断（LPI）模式。</summary>
    public enum LpiMode : byte
    {
        /// <summary>使用默认的局部外设中断策略。</summary>
        Default = 0,
        /// <summary>禁用局部外设中断模式，编码为 2。</summary>
        Disabled = 2,
        /// <summary>启用局部外设中断模式，编码为 3。</summary>
        Enabled = 3
    }

    /// <summary>处理器迁移兼容模式（LimitProcessorFeaturesMode，u8）。</summary>
    public enum VmMigrationCompatibilityMode : byte
    {
        /// <summary>使用最小处理器功能集的迁移兼容模式。</summary>
        MinimumFeatureSet = 0,
        /// <summary>使用群集共同处理器功能集的迁移兼容模式。</summary>
        CommonClusterFeatureSet = 1
    }

    /// <summary>虚拟机处理器设置的可变模型。属性变化可用于绑定；提交配置由 VmProcessorService 执行。</summary>
    /// <remarks>
    /// <para>可空字段未取得当前值时保留 null，不补为 false、0 或空字符串。字段是否存在由 SupportedCapabilities 或 Supports 单独表示：支持且值为 null 表示本次没有可用的当前值，不支持且值为 null 表示本次查询未提供该字段。未查询的模型也不包含支持信息。</para>
    /// <para>频率上限、下限、期望频率、能效偏好、自主活动窗口、硬件隔离来宾上限、每插槽簇数量、每 L3 域处理器数量和物理地址位宽仍将底层 0xFFFFFFFF 未设置编码归一为 null；不能据 null 推断有效默认值。其他实际读到的 false、0 和空字符串保留原值。</para>
    /// <para>字段存在不保证硬件支持或当前状态下可修改；修改仍由服务校验，并由底层返回执行结果。读取的 null 不等于写入请求，写入语义见 SetVmProcessorAsync 的 changedProperties 和空值约定。</para>
    /// </remarks>
    public partial class VmProcessorSettings : ObservableObject
    {
        /// <summary>虚拟处理器数量。</summary>
        [ObservableProperty] private int _count;
        /// <summary>保留处理器资源的原始值，单位为 0.001%；100000 表示 100%。</summary>
        [ObservableProperty] private ulong _reservation;
        /// <summary>处理器资源上限的原始值，单位为 0.001%；100000 表示 100%。</summary>
        [ObservableProperty] private ulong _limit;
        /// <summary>处理器竞争时的相对权重。</summary>
        [ObservableProperty] private int _relativeWeight;
        /// <summary>是否向来宾公开虚拟化扩展以支持嵌套虚拟化；null 表示未取得。</summary>
        [ObservableProperty] private bool? _exposeVirtualizationExtensions;
        /// <summary>是否启用宿主资源保护；null 表示未取得。</summary>
        [ObservableProperty] private bool? _enableHostResourceProtection;
        /// <summary>是否启用处理器迁移兼容；null 表示未取得。</summary>
        [ObservableProperty] private bool? _compatibilityForMigrationEnabled;
        /// <summary>处理器迁移兼容所采用的功能集模式；null 表示未取得。</summary>
        [ObservableProperty] private VmMigrationCompatibilityMode? _compatibilityForMigrationMode;
        /// <summary>是否启用旧操作系统兼容；null 表示未取得。</summary>
        [ObservableProperty] private bool? _compatibilityForOlderOperatingSystemsEnabled;
        /// <summary>向来宾呈现的同时多线程模式；null 表示未取得。</summary>
        [ObservableProperty] private SmtMode? _smtMode;
        /// <summary>是否禁用推测执行控制；null 表示未取得。</summary>
        [ObservableProperty] private bool? _disableSpeculationControls;
        /// <summary>是否启用体系结构性能监控单元；null 表示未取得。</summary>
        [ObservableProperty] private bool? _enablePerfmonArchPmu;
        /// <summary>是否允许访问 ACNT/MCNT 计数器，对应 AllowACountMCount；null 表示未取得。</summary>
        [ObservableProperty] private bool? _allowAcountMcount;
        /// <summary>向来宾公开的处理器品牌字符串；null 表示未取得。</summary>
        [ObservableProperty] private string? _cpuBrandString;

        // ── 新增：CPUID 视图类（改 guest 所见，多为迁移/兼容向）──
        /// <summary>来宾 APIC 呈现模式；null 表示未取得。</summary>
        [ObservableProperty] private VmApicMode? _apicMode;                        // ApicMode
        /// <summary>来宾看到的 L3 缓存相联路数，0 表示默认；不改变物理缓存，null 表示未取得。</summary>
        [ObservableProperty] private uint? _l3CacheWays;                           // L3CacheWays（0=默认；仅改 guest 视图，不动真缓存）
        /// <summary>虚拟处理器在虚拟 L3 缓存域间的分布策略；null 表示未取得。</summary>
        [ObservableProperty] private L3DistributionPolicy? _l3DistributionPolicy;  // L3ProcessorDistributionPolicy
        /// <summary>二级地址转换的大页拆分缓解策略；null 表示未取得。</summary>
        [ObservableProperty] private PageShatterMode? _pageShatterMode;            // EnablePageShattering（SLAT 内部）

        // ── 新增：每-VM 调频/能效（需主机 HWP；消费 Intel 多被拒，应做能力门控）──
        /// <summary>每虚拟机处理器频率上限，单位为 MHz；null 表示未取得。</summary>
        [ObservableProperty] private uint? _perfCpuFreqCapMhz;
        /// <summary>每虚拟机处理器频率下限，单位为 MHz；null 表示未取得。</summary>
        [ObservableProperty] private uint? _perfCpuFreqMinMhz;
        /// <summary>每虚拟机期望处理器频率，单位为 MHz；null 表示未取得。</summary>
        [ObservableProperty] private uint? _perfCpuFreqDesiredMhz;
        /// <summary>宿主性能与能效偏好的原始编码；null 表示未取得。</summary>
        [ObservableProperty] private uint? _perfCpuEnergyPerformancePreference;
        /// <summary>宿主自主活动窗口的原始编码，不在模型中换算时间单位；null 表示未取得。</summary>
        [ObservableProperty] private uint? _perfCpuAutonomousActivityWindow;
        /// <summary>是否忽略宿主最大频率限制；null 表示未取得。</summary>
        [ObservableProperty] private bool? _perfCpuIgnoreHostMaxFrequency;

        // ── 新增：性能监控透传（需主机 vPMU；消费 Intel 开启会拒启 0xC0350005）──
        /// <summary>是否透传 PMU 性能监控；null 表示未取得，切换还需满足相关依赖规则。</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanTogglePerfmonPmu))]
        [NotifyPropertyChangedFor(nameof(CanTogglePerfmonPebs))]
        [NotifyPropertyChangedFor(nameof(CanTogglePerfmonIpt))]
        private bool? _enablePerfmonPmu;
        /// <summary>是否透传最后分支记录 LBR；null 表示未取得。</summary>
        [ObservableProperty] private bool? _enablePerfmonLbr;
        /// <summary>是否启用精确事件采样 PEBS，依赖 PMU；null 表示未取得。</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanTogglePerfmonPmu))]
        [NotifyPropertyChangedFor(nameof(CanTogglePerfmonPebs))]
        private bool? _enablePerfmonPebs;
        /// <summary>是否启用处理器跟踪 IPT，依赖 PMU；null 表示未取得。</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanTogglePerfmonPmu))]
        [NotifyPropertyChangedFor(nameof(CanTogglePerfmonIpt))]
        private bool? _enablePerfmonIpt;

        /// <summary>按当前 PMU、PEBS 和 IPT 状态判断是否允许切换 PMU。</summary>
        public bool CanTogglePerfmonPmu => Services.VmProcessorService.CanTogglePerfmonPmu(EnablePerfmonPmu, EnablePerfmonPebs, EnablePerfmonIpt);
        /// <summary>按当前 PEBS 和 PMU 状态判断是否允许切换 PEBS。</summary>
        public bool CanTogglePerfmonPebs => Services.VmProcessorService.CanTogglePerfmonDependent(EnablePerfmonPebs, EnablePerfmonPmu);
        /// <summary>按当前 IPT 和 PMU 状态判断是否允许切换 IPT。</summary>
        public bool CanTogglePerfmonIpt => Services.VmProcessorService.CanTogglePerfmonDependent(EnablePerfmonIpt, EnablePerfmonPmu);

        // ── 新增：硬件隔离（需 Intel TDX / AMD SEV-SNP，消费级无此硅片）──
        /// <summary>是否启用硬件隔离扩展；null 表示未取得。</summary>
        [ObservableProperty] private bool? _hardwareIsolationExtensionsEnabled;   // WMI: 0=Disabled, 1=HardwareIsolation
        /// <summary>允许的硬件隔离来宾最大数量；null 表示未取得。</summary>
        [ObservableProperty] private uint? _maxHwIsolatedGuests;

        // ── 处理器簇与 L3 缓存域拓扑 ──
        /// <summary>每个虚拟处理器插槽的最大簇数量；null 表示未取得。</summary>
        [ObservableProperty] private uint? _maxClusterCountPerSocket;
        /// <summary>每个虚拟 L3 缓存域的最大处理器数量；null 表示未取得。</summary>
        [ObservableProperty] private uint? _maxProcessorCountPerL3;

        // vNUMA, guest physical address space, and interrupt topology.
        /// <summary>每个虚拟 NUMA 节点的最大处理器数量；null 表示未取得。</summary>
        [ObservableProperty] private ulong? _maxProcessorsPerNumaNode;
        /// <summary>每个虚拟插槽的最大 NUMA 节点数量；null 表示未取得。</summary>
        [ObservableProperty] private ulong? _maxNumaNodesPerSocket;
        /// <summary>来宾物理地址位宽，单位为位；null 表示未取得。</summary>
        [ObservableProperty] private uint? _physicalAddressWidth;
        /// <summary>局部外设中断模式；null 表示未取得。</summary>
        [ObservableProperty] private LpiMode? _lpiMode;

        /// <summary>本次处理器设置查询中实际存在的属性名集合；与各字段当前值是否为空独立保存。</summary>
        internal HashSet<string> SupportedProps { get; set; } = new(System.StringComparer.OrdinalIgnoreCase);

        /// <summary>本次查询中宿主提供的处理器设置项，只读且不随设置值是否为 null 改变。修改是否成功仍受运行状态、配置版本和硬件条件约束。</summary>
        public IReadOnlySet<VmProcessorCapability> SupportedCapabilities => CapabilityProperties
            .Where(entry => SupportedProps.Contains(entry.Value))
            .Select(entry => entry.Key).ToFrozenSet();

        /// <summary>判断本次查询是否提供某项处理器设置字段；与当前值是否为 null 无关，不执行宿主查询或写入。</summary>
        /// <param name="capability">要判断的业务设置项。</param>
        /// <returns>本次查询包含该设置项时为 true；未查询或未知能力值返回 false。</returns>
        public bool Supports(VmProcessorCapability capability) =>
            CapabilityProperties.TryGetValue(capability, out var property) && SupportedProps.Contains(property);

        private static readonly FrozenDictionary<VmProcessorCapability, string> CapabilityProperties =
            new Dictionary<VmProcessorCapability, string>
            {
                [VmProcessorCapability.ProcessorCount] = "VirtualQuantity",
                [VmProcessorCapability.ResourceReservation] = "Reservation",
                [VmProcessorCapability.ResourceLimit] = "Limit",
                [VmProcessorCapability.SchedulingWeight] = "Weight",
                [VmProcessorCapability.NestedVirtualization] = "ExposeVirtualizationExtensions",
                [VmProcessorCapability.HostResourceProtection] = "EnableHostResourceProtection",
                [VmProcessorCapability.MigrationCompatibility] = "LimitProcessorFeatures",
                [VmProcessorCapability.MigrationFeatureSet] = "LimitProcessorFeaturesMode",
                [VmProcessorCapability.LegacyOperatingSystemCompatibility] = "LimitCPUID",
                [VmProcessorCapability.SimultaneousMultithreading] = "HwThreadsPerCore",
                [VmProcessorCapability.SpeculationControlPolicy] = "DisableSpeculationControls",
                [VmProcessorCapability.ArchitecturalPerformanceMonitoring] = "EnablePerfmonArchPmu",
                [VmProcessorCapability.ActivityCounters] = "AllowAcountMcount",
                [VmProcessorCapability.ProcessorBrand] = "CpuBrandString",
                [VmProcessorCapability.InterruptControllerMode] = "ApicMode",
                [VmProcessorCapability.L3CacheAssociativity] = "L3CacheWays",
                [VmProcessorCapability.L3ProcessorDistribution] = "L3ProcessorDistributionPolicy",
                [VmProcessorCapability.LargePageMitigation] = "EnablePageShattering",
                [VmProcessorCapability.FrequencyMaximum] = "PerfCpuFreqCapMhz",
                [VmProcessorCapability.FrequencyMinimum] = "PerfCpuFreqMinMhz",
                [VmProcessorCapability.DesiredFrequency] = "PerfCpuFreqDesiredMhz",
                [VmProcessorCapability.EnergyPerformancePreference] = "PerfCpuEnergyPerformancePreference",
                [VmProcessorCapability.AutonomousActivityWindow] = "PerfCpuAutonomousActivityWindow",
                [VmProcessorCapability.IgnoreHostFrequencyMaximum] = "PerfCpuIgnoreHostMaxFrequency",
                [VmProcessorCapability.PerformanceMonitoring] = "EnablePerfmonPmu",
                [VmProcessorCapability.LastBranchRecording] = "EnablePerfmonLbr",
                [VmProcessorCapability.PreciseEventSampling] = "EnablePerfmonPebs",
                [VmProcessorCapability.InstructionTracing] = "EnablePerfmonIpt",
                [VmProcessorCapability.HardwareIsolation] = "ExtendedVirtualizationExtensions",
                [VmProcessorCapability.HardwareIsolatedGuestLimit] = "MaxHwIsolatedGuests",
                [VmProcessorCapability.ProcessorClustersPerSocket] = "MaxClusterCountPerSocket",
                [VmProcessorCapability.ProcessorsPerL3Domain] = "MaxProcessorCountPerL3",
                [VmProcessorCapability.ProcessorsPerNumaNode] = "MaxProcessorsPerNumaNode",
                [VmProcessorCapability.NumaNodesPerSocket] = "MaxNumaNodesPerSocket",
                [VmProcessorCapability.PhysicalAddressWidth] = "PhysicalAddressWidth",
                [VmProcessorCapability.LocalPeripheralInterruptMode] = "LpiMode"
            }.ToFrozenDictionary();
    }
}
