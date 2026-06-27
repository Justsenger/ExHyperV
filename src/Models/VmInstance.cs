using System.Collections.ObjectModel;
using ExHyperV.Tools;

namespace ExHyperV.Models
{
    /// <summary>
    /// 虚拟机的根数据模型。
    /// Services 生产此类型；ViewModel 层用它构造或刷新 VmInstanceViewModel。
    ///
    /// 设计原则：
    /// - 不继承 ObservableObject、不持命令、不带 WPF 渲染类型（PointCollection/BitmapSource）
    /// - 集合用 <see cref="ObservableCollection{T}"/>——与 VM 共享同一实例，Services mutate 后 UI 自动刷新
    ///   （ObservableCollection 本身是 .NET BCL 类型，非 WPF-specific，Model 持有它不违背"纯数据"原则）
    /// - 叶子类型（VmDiskItem 等）本身可以是 ObservableObject——它们承担 leaf-level UI binding affinity
    /// - 不做 transient 状态机：直接持有 backend 给出的 StateText/RawUptime；transient 行为属于 VM 层
    /// - 计算属性 IsRunning / HasGpu 是静态派生，便于 Service 读取/过滤
    /// </summary>
    public class VmInstance
    {
        // ── 标识 ──────────────────────────────────────────────────
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Notes { get; set; } = string.Empty;

        // ── 配置 ──────────────────────────────────────────────────
        public int Generation { get; set; }
        public string Version { get; set; } = "0.0";
        public string OsType { get; set; } = "Windows";

        // ── 状态（raw，由 backend WMI 给出；VM 层负责 transient 处理）──
        /// <summary>Hyper-V 当前状态文本（已通过 VmMapper 映射，如 "Running"/"Off"/"Saved"…）</summary>
        public string StateText { get; set; } = string.Empty;

        /// <summary>Hyper-V 原始状态码（EnabledState）；IsRunning 据此判定，不依赖本地化文本</summary>
        public ushort StateCode { get; set; }

        /// <summary>VM 已运行时长（来自 Msvm_SummaryInformation.UpTime）</summary>
        public TimeSpan RawUptime { get; set; }

        // ── CPU/内存 facts ────────────────────────────────────────
        public int CpuCount { get; set; }
        public double MemoryGb { get; set; }
        public double AssignedMemoryGb { get; set; }
        public double DemandMemoryGb { get; set; }

        // ── 网络 facts ────────────────────────────────────────────
        public string IpAddress { get; set; } = "---";
        public string MacAddress { get; set; } = "00:00:00:00:00:00";

        // ── 存储 facts ────────────────────────────────────────────
        public double TotalDiskSizeGb { get; set; }
        public ObservableCollection<VmDiskItem> Disks { get; } = new();
        public ObservableCollection<VmStorageItem> StorageItems { get; } = new();
        public ObservableCollection<VmNetworkAdapter> NetworkAdapters { get; } = new();
        public ObservableCollection<BootOrderItem> BootOrderItems { get; } = new();

        // ── GPU facts ─────────────────────────────────────────────
        public string? GpuName { get; set; }
        public ObservableCollection<VmGpuAssignment> AssignedGpus { get; } = new();
        public bool IsGpuActive { get; set; }

        // ── 详细 settings（叶子 ObservableObject，作为 Model 的子对象）──
        public VmProcessorSettings? Processor { get; set; }
        public VmMemorySettings? MemorySettings { get; set; }

        // ── 计算派生 ──────────────────────────────────────────────

        /// <summary>是否处于活动状态（按原始状态码白名单判定，不考虑 transient 过渡态）</summary>
        public bool IsRunning => VmMapper.IsActiveState(StateCode);

        /// <summary>是否分配了 GPU（按 GpuName 或 AssignedGpus 任一非空判定）</summary>
        public bool HasGpu => !string.IsNullOrEmpty(GpuName) || AssignedGpus.Count > 0;

        // ── 构造 ──────────────────────────────────────────────────
        public VmInstance() { }

        public VmInstance(Guid id, string name)
        {
            Id = id;
            Name = name;
        }
    }
}
