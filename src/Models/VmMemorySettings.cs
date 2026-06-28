using CommunityToolkit.Mvvm.ComponentModel;

namespace ExHyperV.Models
{
    /// <summary>页面大小下拉项（与 VmMemorySettings.BackingPageSize 配套）。</summary>
    public class PageSizeItem
    {
        public string Description { get; set; } = string.Empty;
        public byte Value { get; set; }
    }

    /// <summary>
    /// VM 内存设置（绑定 Memory Settings 页面）。
    /// 含 Clone/Restore 用于"取消编辑"还原。
    /// </summary>
    public partial class VmMemorySettings : ObservableObject
    {
        [ObservableProperty] private long _startup;
        [ObservableProperty] private bool _dynamicMemoryEnabled;
        [ObservableProperty] private long _minimum;
        [ObservableProperty] private long _maximum;
        [ObservableProperty] private int _buffer;
        [ObservableProperty] private int _priority;
        [ObservableProperty] private byte? _backingPageSize;
        [ObservableProperty] private bool? _hugePagesEnabled;   // 巨页：独立 WMI 属性，与 BackingPageSize 并存(设 true 需 VM 内存按大页对齐)

        // --- 实验性功能 ---
        [ObservableProperty] private byte? _backingType;              // 内存后端类型
        [ObservableProperty] private uint? _dynMemOperationAlignment;  // 动态内存操作对齐
        [ObservableProperty] private byte? _memoryAccessTrackingPolicy; // 访问跟踪策略
        [ObservableProperty] private byte? _memoryAccessTrackingState;  // 访问跟踪状态
        [ObservableProperty] private bool? _sgxEnabled;                // SGX 开关
        [ObservableProperty] private double? _sgxSize;                  // SGX 大小
        [ObservableProperty] private uint? _sgxLaunchControlMode;      // SGX 启动模式
        [ObservableProperty] private bool? _enableGpaPinning;          // GPA 固定
        [ObservableProperty] private bool? _cxlEnabled;                // CXL 支持
        [ObservableProperty] private bool? _enableColdHint;
        [ObservableProperty] private bool? _enableHotHint;
        [ObservableProperty] private bool? _enableEpf;
        [ObservableProperty] private bool? _enablePrivateCompressionStore;
        [ObservableProperty] private ulong? _maxMemoryBlocksPerNumaNode;
        [ObservableProperty] private string? _sgxLaunchControlDefault;

        public List<PageSizeItem> AvailablePageSizes { get; } = new List<PageSizeItem>
        {
            new PageSizeItem { Description = Properties.Resources.Mem_Standard, Value = 0 },
            new PageSizeItem { Description = Properties.Resources.Mem_Large, Value = 1 },
            new PageSizeItem { Description = Properties.Resources.Mem_Huge, Value = 2 }
        };

        [ObservableProperty] private byte? _memoryEncryptionPolicy;

        // 开启 SGX 必须有 EPC 大小(≥2MB)，否则提交被 Hyper-V 拒（"SGX 内存无效，请分配至少 2 MB"）；无有效值时补合法默认。
        partial void OnSgxEnabledChanged(bool? value)
        {
            if (value == true && (SgxSize == null || SgxSize.Value < 2))
                SgxSize = 2;
        }

        public VmMemorySettings Clone() => (VmMemorySettings)this.MemberwiseClone();

        public void Restore(VmMemorySettings other)
        {
            if (other == null) return;
            Startup = other.Startup;
            DynamicMemoryEnabled = other.DynamicMemoryEnabled;
            Minimum = other.Minimum;
            Maximum = other.Maximum;
            Buffer = other.Buffer;
            Priority = other.Priority;
            BackingPageSize = other.BackingPageSize;
            HugePagesEnabled = other.HugePagesEnabled;
            MemoryEncryptionPolicy = other.MemoryEncryptionPolicy;

            // 实验性功能补齐
            BackingType = other.BackingType;
            DynMemOperationAlignment = other.DynMemOperationAlignment;
            MemoryAccessTrackingPolicy = other.MemoryAccessTrackingPolicy;
            MemoryAccessTrackingState = other.MemoryAccessTrackingState;
            SgxEnabled = other.SgxEnabled;
            SgxSize = other.SgxSize;
            SgxLaunchControlMode = other.SgxLaunchControlMode;
            EnableGpaPinning = other.EnableGpaPinning;
            CxlEnabled = other.CxlEnabled;
            EnableColdHint = other.EnableColdHint;
            EnableHotHint = other.EnableHotHint;
            EnableEpf = other.EnableEpf;
            EnablePrivateCompressionStore = other.EnablePrivateCompressionStore;
            MaxMemoryBlocksPerNumaNode = other.MaxMemoryBlocksPerNumaNode;
            SgxLaunchControlDefault = other.SgxLaunchControlDefault;
        }
    }
}
