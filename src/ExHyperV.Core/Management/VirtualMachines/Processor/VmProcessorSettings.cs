using ExHyperV.Services;
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

    /// <summary>虚拟处理器的 APIC 中断控制器模式：自动、Legacy、x2APIC 或 xAPIC。</summary>
    public enum VmApicMode : byte
    {
        /// <summary>自动选择。Hyper-V 根据虚拟机配置、兼容性、处理器拓扑及宿主能力确定实际模式。</summary>
        Default = 0,
        /// <summary>传统兼容模式（Legacy），使用 Hyper-V 的旧式 APIC 兼容路径。</summary>
        Legacy = 1,
        /// <summary>x2APIC 模式，支持扩展的 APIC ID 编号空间。</summary>
        X2Apic = 2,
        /// <summary>xAPIC 模式。WMI 名称为 Apic，虚拟机工作进程中对应 XApic。</summary>
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
    /// <para>读取结果：SupportedCapabilities 和 Supports 表示宿主提供哪些设置项。可空字段的 null 表示未取得当前值或未设置。</para>
    /// <para>频率上限、下限、期望频率、能效偏好、自主活动窗口、硬件隔离来宾上限、每插槽簇数量、每 L3 域处理器数量和物理地址位宽仍将底层 0xFFFFFFFF 未设置编码归一为 null。其他实际读到的 false、0 和空字符串保留原值。</para>
    /// <para>写入时，仅提交 changedProperties 选中的属性。选中属性的 null 按以下规则处理：</para>
    /// <list type="bullet">
    /// <item><c>CpuBrandString</c>：清为空字符串。</item>
    /// <item><c>PerfCpuFreqDesiredMhz</c>、<c>PerfCpuEnergyPerformancePreference</c>、<c>PerfCpuAutonomousActivityWindow</c>、<c>MaxClusterCountPerSocket</c>、<c>MaxProcessorCountPerL3</c>：恢复默认，写入底层未设置值。</item>
    /// <item>其他可空属性：跳过该项，保留原值。</item>
    /// </list>
    /// </remarks>
    public partial class VmProcessorSettings
    {
        /// <summary>虚拟处理器数量。</summary>
        public int Count { get => _count; set => _count = value; }
        private int _count;
        /// <summary>保留处理器资源的原始值，单位为 0.001%；100000 表示 100%。</summary>
        public ulong Reservation { get => _reservation; set => _reservation = value; }
        private ulong _reservation;
        /// <summary>处理器资源上限的原始值，单位为 0.001%；100000 表示 100%。</summary>
        public ulong Limit { get => _limit; set => _limit = value; }
        private ulong _limit;
        /// <summary>处理器竞争时的相对权重。</summary>
        public int RelativeWeight { get => _relativeWeight; set => _relativeWeight = value; }
        private int _relativeWeight;
        /// <summary>是否向来宾公开虚拟化扩展以支持嵌套虚拟化；null 表示未取得。</summary>
        public bool? ExposeVirtualizationExtensions { get => _exposeVirtualizationExtensions; set => _exposeVirtualizationExtensions = value; }
        private bool? _exposeVirtualizationExtensions;
        /// <summary>参见 <see cref="VmProcessorFields.EnableHostResourceProtection"/>；null 表示未取得当前值。</summary>
        public bool? EnableHostResourceProtection { get => _enableHostResourceProtection; set => _enableHostResourceProtection = value; }
        private bool? _enableHostResourceProtection;
        /// <summary>是否启用处理器迁移兼容；null 表示未取得。</summary>
        public bool? CompatibilityForMigrationEnabled { get => _compatibilityForMigrationEnabled; set => _compatibilityForMigrationEnabled = value; }
        private bool? _compatibilityForMigrationEnabled;
        /// <summary>处理器迁移兼容所采用的功能集模式；null 表示未取得。</summary>
        public VmMigrationCompatibilityMode? CompatibilityForMigrationMode { get => _compatibilityForMigrationMode; set => _compatibilityForMigrationMode = value; }
        private VmMigrationCompatibilityMode? _compatibilityForMigrationMode;
        /// <summary>是否启用旧操作系统兼容；null 表示未取得。</summary>
        public bool? CompatibilityForOlderOperatingSystemsEnabled { get => _compatibilityForOlderOperatingSystemsEnabled; set => _compatibilityForOlderOperatingSystemsEnabled = value; }
        private bool? _compatibilityForOlderOperatingSystemsEnabled;
        /// <summary>向来宾呈现的同时多线程模式；null 表示未取得。</summary>
        public SmtMode? SmtMode { get => _smtMode; set => _smtMode = value; }
        private SmtMode? _smtMode;
        /// <summary>是否向来宾隐藏推测执行侧信道漏洞的缓解控制能力；null 表示未取得。</summary>
        public bool? DisableSpeculationControls { get => _disableSpeculationControls; set => _disableSpeculationControls = value; }
        private bool? _disableSpeculationControls;
        /// <summary>参见 <see cref="VmProcessorFields.EnablePerfmonArchPmu"/>；null 表示未取得当前值。</summary>
        public bool? EnablePerfmonArchPmu { get => _enablePerfmonArchPmu; set => _enablePerfmonArchPmu = value; }
        private bool? _enablePerfmonArchPmu;
        /// <summary>参见 <see cref="VmProcessorFields.AllowAcountMcount"/>；null 表示未取得当前值。</summary>
        public bool? AllowAcountMcount { get => _allowAcountMcount; set => _allowAcountMcount = value; }
        private bool? _allowAcountMcount;
        /// <summary>向来宾公开的处理器品牌字符串；null 表示未取得。</summary>
        public string? CpuBrandString { get => _cpuBrandString; set => _cpuBrandString = value; }
        private string? _cpuBrandString;

        // ── 新增：CPUID 视图类（改 guest 所见，多为迁移/兼容向）──
        /// <summary>来宾 APIC 呈现模式；null 表示未取得。</summary>
        public VmApicMode? ApicMode { get => _apicMode; set => _apicMode = value; }
        private VmApicMode? _apicMode;                        // ApicMode
        /// <summary>来宾看到的 L3 缓存相联路数，0 表示默认；不改变物理缓存，null 表示未取得。</summary>
        public uint? L3CacheWays { get => _l3CacheWays; set => _l3CacheWays = value; }
        private uint? _l3CacheWays;                           // L3CacheWays（0=默认；仅改 guest 视图，不动真缓存）
        /// <summary>虚拟处理器在虚拟 L3 缓存域间的分布策略；null 表示未取得。</summary>
        public L3DistributionPolicy? L3DistributionPolicy { get => _l3DistributionPolicy; set => _l3DistributionPolicy = value; }
        private L3DistributionPolicy? _l3DistributionPolicy;  // L3ProcessorDistributionPolicy
        /// <summary>二级地址转换的大页拆分缓解策略；null 表示未取得。</summary>
        public PageShatterMode? PageShatterMode { get => _pageShatterMode; set => _pageShatterMode = value; }
        private PageShatterMode? _pageShatterMode;            // EnablePageShattering（SLAT 内部）

        // ── 新增：每-VM 调频/能效（需主机 HWP；消费 Intel 多被拒，应做能力门控）──
        /// <summary>每虚拟机处理器频率上限，单位为 MHz；null 表示未取得。</summary>
        public uint? PerfCpuFreqCapMhz { get => _perfCpuFreqCapMhz; set => _perfCpuFreqCapMhz = value; }
        private uint? _perfCpuFreqCapMhz;
        /// <summary>每虚拟机处理器频率下限，单位为 MHz；null 表示未取得。</summary>
        public uint? PerfCpuFreqMinMhz { get => _perfCpuFreqMinMhz; set => _perfCpuFreqMinMhz = value; }
        private uint? _perfCpuFreqMinMhz;
        /// <summary>向宿主请求的处理器目标频率，单位为 MHz；0 表示自主性能选择，null 表示未设置或未取得。</summary>
        public uint? PerfCpuFreqDesiredMhz { get => _perfCpuFreqDesiredMhz; set => _perfCpuFreqDesiredMhz = value; }
        private uint? _perfCpuFreqDesiredMhz;
        /// <summary>自主调频的能耗性能偏好（EPP），范围为 0～255；0 最偏性能，255 最偏节能。期望频率须为 0；null 表示未设置或未取得。</summary>
        public uint? PerfCpuEnergyPerformancePreference { get => _perfCpuEnergyPerformancePreference; set => _perfCpuEnergyPerformancePreference = value; }
        private uint? _perfCpuEnergyPerformancePreference;
        /// <summary>宿主自主调频活动窗口的原始值，时间单位和编码尚未确认。期望频率须为 0；null 表示未设置或未取得。</summary>
        public uint? PerfCpuAutonomousActivityWindow { get => _perfCpuAutonomousActivityWindow; set => _perfCpuAutonomousActivityWindow = value; }
        private uint? _perfCpuAutonomousActivityWindow;
        /// <summary>是否忽略宿主最大频率限制；null 表示未取得。</summary>
        public bool? PerfCpuIgnoreHostMaxFrequency { get => _perfCpuIgnoreHostMaxFrequency; set => _perfCpuIgnoreHostMaxFrequency = value; }
        private bool? _perfCpuIgnoreHostMaxFrequency;

        // ── 新增：性能监控透传（需主机 vPMU；消费 Intel 开启会拒启 0xC0350005）──
        /// <summary>参见 <see cref="VmProcessorFields.EnablePerfmonPmu"/>；null 表示未取得当前值。</summary>
        public bool? EnablePerfmonPmu { get => _enablePerfmonPmu; set => _enablePerfmonPmu = value; }
        private bool? _enablePerfmonPmu;
        /// <summary>参见 <see cref="VmProcessorFields.EnablePerfmonLbr"/>；null 表示未取得当前值。</summary>
        public bool? EnablePerfmonLbr { get => _enablePerfmonLbr; set => _enablePerfmonLbr = value; }
        private bool? _enablePerfmonLbr;
        /// <summary>参见 <see cref="VmProcessorFields.EnablePerfmonPebs"/>；null 表示未取得当前值。</summary>
        public bool? EnablePerfmonPebs { get => _enablePerfmonPebs; set => _enablePerfmonPebs = value; }
        private bool? _enablePerfmonPebs;
        /// <summary>参见 <see cref="VmProcessorFields.EnablePerfmonIpt"/>；null 表示未取得当前值。</summary>
        public bool? EnablePerfmonIpt { get => _enablePerfmonIpt; set => _enablePerfmonIpt = value; }
        private bool? _enablePerfmonIpt;

        // ── 新增：硬件隔离（需 Intel TDX / AMD SEV-SNP，消费级无此硅片）──
        /// <summary>是否启用硬件隔离扩展；null 表示未取得。</summary>
        public bool? HardwareIsolationExtensionsEnabled { get => _hardwareIsolationExtensionsEnabled; set => _hardwareIsolationExtensionsEnabled = value; }
        private bool? _hardwareIsolationExtensionsEnabled;   // WMI: 0=Disabled, 1=HardwareIsolation
        /// <summary>允许的硬件隔离来宾最大数量；null 表示未取得。</summary>
        public uint? MaxHwIsolatedGuests { get => _maxHwIsolatedGuests; set => _maxHwIsolatedGuests = value; }
        private uint? _maxHwIsolatedGuests;

        // ── 处理器簇与 L3 缓存域拓扑 ──
        /// <summary>每个虚拟处理器插槽的最大簇数量；null 表示未取得。</summary>
        public uint? MaxClusterCountPerSocket { get => _maxClusterCountPerSocket; set => _maxClusterCountPerSocket = value; }
        private uint? _maxClusterCountPerSocket;
        /// <summary>每个虚拟 L3 缓存域的最大处理器数量；null 表示未取得。</summary>
        public uint? MaxProcessorCountPerL3 { get => _maxProcessorCountPerL3; set => _maxProcessorCountPerL3 = value; }
        private uint? _maxProcessorCountPerL3;

        // vNUMA, guest physical address space, and interrupt topology.
        /// <summary>每个虚拟 NUMA 节点的最大处理器数量；null 表示未取得。</summary>
        public ulong? MaxProcessorsPerNumaNode { get => _maxProcessorsPerNumaNode; set => _maxProcessorsPerNumaNode = value; }
        private ulong? _maxProcessorsPerNumaNode;
        /// <summary>每个虚拟插槽的最大 NUMA 节点数量；null 表示未取得。</summary>
        public ulong? MaxNumaNodesPerSocket { get => _maxNumaNodesPerSocket; set => _maxNumaNodesPerSocket = value; }
        private ulong? _maxNumaNodesPerSocket;
        /// <summary>参见 <see cref="VmProcessorFields.PhysicalAddressWidth"/>；null 表示未取得当前值。</summary>
        public uint? PhysicalAddressWidth { get => _physicalAddressWidth; set => _physicalAddressWidth = value; }
        private uint? _physicalAddressWidth;
        /// <summary>局部外设中断模式；null 表示未取得。</summary>
        public LpiMode? LpiMode { get => _lpiMode; set => _lpiMode = value; }
        private LpiMode? _lpiMode;

        /// <summary>本次处理器设置查询中实际存在的属性名集合；与各字段当前值是否为空独立保存。</summary>
        internal HashSet<string> SupportedProps { get; set; } = new(System.StringComparer.OrdinalIgnoreCase);

        /// <summary>本次查询中宿主提供的处理器设置项，只读且不随设置值是否为 null 改变。修改是否成功仍受运行状态、配置版本和硬件条件约束。</summary>
        public IReadOnlySet<VmProcessorCapability> SupportedCapabilities => CapabilityProperties
            .Where(entry => SupportedProps.Contains(entry.Value))
            .Select(entry => entry.Key).ToFrozenSet();

        /// <summary>查询本次读取结果中是否包含指定处理器设置。</summary>
        /// <param name="capability">要判断的业务设置项。</param>
        /// <returns>本次查询包含该设置项时为 true；未查询或未知能力值返回 false。</returns>
        public bool Supports(VmProcessorCapability capability) =>
            CapabilityProperties.TryGetValue(capability, out var property) && SupportedProps.Contains(property);

        /// <summary>复制处理器配置及查询返回的支持信息。</summary>
        /// <returns>可独立编辑的配置副本。</returns>
        public VmProcessorSettings Clone()
        {
            var copy = new VmProcessorSettings();
            foreach (var property in typeof(VmProcessorSettings).GetProperties().Where(p => p.SetMethod?.IsPublic == true))
                property.SetValue(copy, property.GetValue(this));
            copy.SupportedProps = new(SupportedProps, StringComparer.OrdinalIgnoreCase);
            return copy;
        }

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
