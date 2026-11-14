
using ExHyperV.Tools;

namespace ExHyperV.Models
{
    /// <summary>
    /// 虚拟机清单及详情的可变模型；包含配置、查询时的状态和可独立刷新的性能样本。
    /// 设备集合保存查询结果，加载状态与采样有效性由对应字段给出。
    /// </summary>
    /// <remarks>本地可变的查询模型。刷新数据和提交修改通过相应服务完成。</remarks>
    public class VmInstance
    {
        // ── 标识 ──────────────────────────────────────────────────
        /// <summary>虚拟机 GUID。</summary>
        public Guid Id { get; init; }
        /// <summary>虚拟机名称。</summary>
        public string Name { get; set; } = string.Empty;

        // ── 配置 ──────────────────────────────────────────────────
        /// <summary>虚拟机代数，1 或 2；未取得或无法识别时为 0。</summary>
        public int Generation { get; set; }
        /// <summary>Hyper-V 配置版本字符串，未填充时为 0.0。</summary>
        public string Version { get; set; } = "0.0";
        /// <summary>HostOnlyItems 中保存的操作系统分类；缺少或无法识别时为 Windows。</summary>
        public string OsType { get; set; } = "Windows";

        // ── 状态（raw，由 backend WMI 给出；VM 层负责 transient 处理）──
        /// <summary>当前资源语言的状态显示文本；程序判断使用 State。</summary>
        internal string StateText { get; set; } = string.Empty;

        /// <summary>Hyper-V 原始状态码（EnabledState）；IsRunning 据此判定，不依赖本地化文本</summary>
        internal ushort StateCode { get; set; }

        /// <summary>当前运行状态；业务判断不依赖本地化文本或后端状态码。</summary>
        public VmState State => VmMapper.MapState(StateCode);

        /// <summary>VM 已运行时长（来自 Msvm_SummaryInformation.UpTime）</summary>
        public TimeSpan RawUptime { get; set; }

        // ── CPU/内存 facts ────────────────────────────────────────
        /// <summary>配置的虚拟处理器数量。</summary>
        public int CpuCount { get; set; }
        /// <summary>配置的启动内存，以 GiB（1024³ 字节）表示。</summary>
        public double MemoryGiB { get; set; }
        /// <summary>当前已分配内存，以 GiB 表示；本次读取是否有效见 AssignedMemorySample。</summary>
        public double AssignedMemoryGiB { get; set; }
        /// <summary>已分配内存的本次读取状态；未采集时为 null。</summary>
        public PerformanceSampleInfo? AssignedMemorySample { get; set; }
        /// <summary>来宾报告的内存需求，以 GiB 表示；本次读取是否有效见 DemandMemorySample。</summary>
        public double DemandMemoryGiB { get; set; }
        /// <summary>内存需求的本次读取状态；普通清单不采集该指标，未采集时为 null。</summary>
        public PerformanceSampleInfo? DemandMemorySample { get; set; }

        // ── 网络 facts ────────────────────────────────────────────
        /// <summary>按 Core 地址优先规则选出的 IPv4 地址；未取得时为空字符串。</summary>
        public string IpAddress { get; set; } = string.Empty;
        /// <summary>用于概要显示的网卡 MAC 地址；未填充时为全零地址。</summary>
        public string MacAddress { get; set; } = "00:00:00:00:00:00";

        // ── 存储 facts ────────────────────────────────────────────
        /// <summary>Disks 中 MaxSize 的合计，单位 GiB，包含光驱镜像；缺测项以默认值参与合计。</summary>
        internal double TotalDiskSizeGiB { get; set; }
        /// <summary>磁盘容量和速率的结果集合。</summary>
        public List<VmDiskStatus> Disks { get; } = new();
        /// <summary>当前存储连接的结果集合。</summary>
        public List<VmStorageAttachment> StorageItems { get; } = new();
        /// <summary>当前虚拟网卡的结果集合。</summary>
        public List<VmNetworkAdapter> NetworkAdapters { get; } = new();
        /// <summary>启动顺序的结果集合。</summary>
        public List<BootOrderItem> BootOrderItems { get; } = new();

        // ── GPU facts ─────────────────────────────────────────────
        /// <summary>概要显示的 GPU 名称；未取得时可为 null。</summary>
        public string? GpuName { get; set; }
        /// <summary>是否确认分配了 GPU 分区；null 表示本次没有取得可靠分配清单。此状态独立于设备显示名称是否可读。</summary>
        public bool? HasGpuAssignment { get; set; }
        /// <summary>已分配 GPU 适配器的结果集合。</summary>
        public List<VmGpuAssignment> AssignedGpus { get; } = new();
        /// <summary>查询结果中的 GPU 活动标记。</summary>
        public bool IsGpuActive { get; set; }

        // ── 详细 settings（配置值模型，作为 Model 的子对象）──
        /// <summary>处理器详细设置；尚未加载或查询不可用时为 null。</summary>
        public VmProcessorSettings? Processor { get; set; }
        /// <summary>内存详细设置；尚未加载或查询不可用时为 null。</summary>
        public VmMemorySettings? MemorySettings { get; set; }
        /// <summary>MMIO 详细设置；尚未加载或查询不可用时为 null。</summary>
        public VmMmioSettings? MmioSettings { get; set; }

        // ── 计算派生 ──────────────────────────────────────────────

        /// <summary>是否处于运行或选定的启动、关机、保存等过渡状态；暂停、已保存和已关机均为 false。</summary>
        public bool IsRunning => VmMapper.IsActiveState(StateCode);

        /// <summary>是否已完全关机；保存、挂起或过渡状态均不属于完全关机。</summary>
        public bool IsPoweredOff => State == VmState.PoweredOff;

        /// <summary>是否分配了 GPU；优先采用已确认的 HasGpuAssignment，未提供时以名称或分配集合推断。</summary>
        public bool HasGpu => HasGpuAssignment ?? (!string.IsNullOrEmpty(GpuName) || AssignedGpus.Count > 0);

        // ── 构造 ──────────────────────────────────────────────────
        /// <summary>创建尚未加载宿主数据的虚拟机模型。</summary>
        public VmInstance() { }

        /// <summary>创建带虚拟机身份的本地模型。</summary>
        /// <param name="id">虚拟机 GUID。</param>
        /// <param name="name">虚拟机名称。</param>
        public VmInstance(Guid id, string name)
        {
            Id = id;
            Name = name;
        }
    }
}
