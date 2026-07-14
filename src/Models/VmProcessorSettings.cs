using CommunityToolkit.Mvvm.ComponentModel;

namespace ExHyperV.Models
{
    /// <summary>SMT（同时多线程）模式：继承宿主 / 单线程 / 多线程。</summary>
    public enum SmtMode { Inherit, SingleThread, MultiThread }

    /// <summary>APIC 模式（Msvm_ProcessorSettingData.ApicMode，u8）：自动 / 强制 xAPIC / 强制 x2APIC / Apic。改 guest CPUID 0x01 ECX[21] x2APIC 位。</summary>
    public enum VmApicMode : byte { Default = 0, Legacy = 1, X2Apic = 2, Apic = 3 }

    /// <summary>L3 处理器分布策略（L3ProcessorDistributionPolicy，u8）：VP 往各 L3 域铺核次序。仅多 L3/AMD 平台有真实效果。</summary>
    public enum L3DistributionPolicy : byte { SmallToLarge = 0, LargeToSmall = 1, EvenSmallToLarge = 2, EvenLargeToSmall = 3 }

    /// <summary>页碎裂策略（EnablePageShattering，u8）：SLAT 把大页(1G/2M)强制碎成 4K。Default 由平台/隔离模式决定。</summary>
    public enum PageShatterMode : byte { Default = 0, AlwaysEnabled = 1, AlwaysDisabled = 2 }

    /// <summary>
    /// VM 处理器设置（绑定 CPU Settings 页面）。
    /// 含 Clone/Restore 用于"取消编辑"还原。
    /// </summary>
    public partial class VmProcessorSettings : ObservableObject
    {
        [ObservableProperty] private int _count;
        [ObservableProperty] private int _reserve;
        [ObservableProperty] private int _maximum;
        [ObservableProperty] private int _relativeWeight;
        [ObservableProperty] private bool? _exposeVirtualizationExtensions;
        [ObservableProperty] private bool? _enableHostResourceProtection;
        [ObservableProperty] private bool? _compatibilityForMigrationEnabled;
        [ObservableProperty] private bool? _compatibilityForOlderOperatingSystemsEnabled;
        [ObservableProperty] private SmtMode? _smtMode;
        [ObservableProperty] private bool? _disableSpeculationControls;
        [ObservableProperty] private bool? _hideHypervisorPresent;
        [ObservableProperty] private bool? _enablePerfmonArchPmu;
        [ObservableProperty] private bool? _allowAcountMcount;
        [ObservableProperty] private bool? _enableSocketTopology;
        [ObservableProperty] private string? _cpuBrandString;

        // ── 新增：CPUID 视图类（改 guest 所见，多为迁移/兼容向）──
        [ObservableProperty] private VmApicMode? _apicMode;                        // ApicMode
        [ObservableProperty] private uint? _l3CacheWays;                           // L3CacheWays（0=默认；仅改 guest 视图，不动真缓存）
        [ObservableProperty] private L3DistributionPolicy? _l3DistributionPolicy;  // L3ProcessorDistributionPolicy（仅多 L3/AMD 有效）
        [ObservableProperty] private PageShatterMode? _pageShatterMode;            // EnablePageShattering（SLAT 内部）

        // ── 新增：每-VM 调频/能效（需宿主 HWP；消费 Intel 多被拒，应做能力门控）──
        [ObservableProperty] private uint? _perfCpuFreqCapMhz;
        [ObservableProperty] private uint? _perfCpuFreqMinMhz;
        [ObservableProperty] private uint? _perfCpuFreqDesiredMhz;
        [ObservableProperty] private uint? _perfCpuEnergyPerformancePreference;
        [ObservableProperty] private uint? _perfCpuAutonomousActivityWindow;
        [ObservableProperty] private bool? _perfCpuIgnoreHostMaxFrequency;

        // ── 新增：性能监控透传（需宿主 vPMU；消费 Intel 开启会拒启 0xC0350005）──
        [ObservableProperty] private bool? _enablePerfmonPmu;
        [ObservableProperty] private bool? _enablePerfmonLbr;
        [ObservableProperty] private bool? _enablePerfmonPebs;
        [ObservableProperty] private bool? _enablePerfmonIpt;

        // ── 新增：硬件隔离（需 Intel TDX / AMD SEV-SNP，消费级无此硅片）──
        [ObservableProperty] private uint? _extendedVirtualizationExtensions;      // 1=HardwareIsolation
        [ObservableProperty] private uint? _maxHwIsolatedGuests;

        // ── 新增：AMD CCX 拓扑（仅 AMD；Intel 上设值 VM 拒启）──
        [ObservableProperty] private uint? _maxClusterCountPerSocket;
        [ObservableProperty] private uint? _maxProcessorCountPerL3;

        /// <summary>宿主 Msvm_ProcessorSettingData 实际存在的属性名集合(schema)。频率字段的 UI 门控据此判"支持"(HasProperty)，
        /// 不看值——因为高版本新属性可能"存在但默认值 null"，按值会把支持的字段误灰。</summary>
        public HashSet<string> SupportedProps { get; set; } = new(System.StringComparer.OrdinalIgnoreCase);
    }
}
