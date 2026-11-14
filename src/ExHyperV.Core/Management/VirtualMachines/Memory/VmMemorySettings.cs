
namespace ExHyperV.Models
{
    /// <summary>
    /// 虚拟机内存的读取结果和修改请求值；通过 changedProperties 明确选择要提交的属性。
    /// </summary>
    /// <remarks>
    /// <para>标注“实验性”的字段继续支持读写，不需要额外的启用参数。字段含义、取值及联动说明见 <see cref="ExHyperV.Services.VmMemoryFields"/> 的对应字段。</para>
    /// <para>可空扩展属性读取为 null 表示未取得，写入 null 表示保留原值。非空请求对应的宿主属性缺失时，提交前返回 Unsupported。修改时只提交选中的字段；Clone/Restore 用于本地快照。</para>
    /// </remarks>
    public partial class VmMemorySettings
    {
        internal HashSet<string>? SupportedProps { get; set; }

        /// <summary>启动内存，单位为 MiB（1024² 字节）。</summary>
        /// <fielddoc>
        /// <meaning>虚拟机启动时使用的内存容量。启用动态内存后，运行期间实际分配量可以变化。</meaning>
        /// <unit>MiB，1 MiB = 1,048,576 字节。</unit>
        /// <values>当前配置的内存容量，单位 MiB。</values>
        /// <source>VmMemoryService.GetVmMemorySettingsAsync 经 MapMemory 读取 Msvm_MemorySettingData.VirtualQuantity；现有列表查询也读取同一底层属性。</source>
        /// <applicability>该值配置启动容量；运行期间的实际分配量见 VmMemoryFields.AssignedMiB。</applicability>
        /// <absence>读取快照的 States[Startup] 给出本次有效性。Available 时使用 Values.Startup。</absence>
        /// <references><see cref="ExHyperV.Services.VmMemoryService"/>；运行时分配量参见 <see cref="ExHyperV.Services.VmQueryService"/>。</references>
        /// </fielddoc>
        public long Startup { get => _startup; set => _startup = value; }
        private long _startup;
        /// <summary>是否启用动态内存。</summary>
        public bool DynamicMemoryEnabled { get => _dynamicMemoryEnabled; set => _dynamicMemoryEnabled = value; }
        private bool _dynamicMemoryEnabled;
        /// <summary>动态内存最小值，单位为 MiB。</summary>
        public long Minimum { get => _minimum; set => _minimum = value; }
        private long _minimum;
        /// <summary>动态内存最大值，单位为 MiB。</summary>
        public long Maximum { get => _maximum; set => _maximum = value; }
        private long _maximum;
        /// <summary>动态内存缓冲比例，单位为百分比。</summary>
        public int Buffer { get => _buffer; set => _buffer = value; }
        private int _buffer;
        /// <summary>宿主内存不足时的分配优先权重，数值越大优先级越高。读取有效性见快照的 States[Weight]。</summary>
        public uint Weight { get => _weight; set => _weight = value; }
        private uint _weight;
        /// <summary>统一页面大小编码：0 标准页、1 大页、2 巨页；null 表示未取得值。</summary>
        public byte? BackingPageSize { get => _backingPageSize; set => _backingPageSize = value; }
        private byte? _backingPageSize;

        // 页面统一使用 0/1/2 表示小页/大页/巨页。旧版 Hyper-V 没有 BackingPageSize，
        // 服务层会把 1/2 与 HugePagesEnabled=false/true 相互转换，因此旧系统只开放两项。
        /// <summary>宿主是否提供 BackingPageSize 属性；旧宿主通过 HugePagesEnabled 映射大页和巨页。</summary>
        public bool SupportsBackingPageSize { get; private set; }

        /// <summary>创建内存配置编辑值。</summary>
        public VmMemorySettings() { }

        internal void ConfigurePageSizeSupport(bool supportsBackingPageSize)
            => SupportsBackingPageSize = supportsBackingPageSize;

        // --- 实验性功能 ---
        /// <summary>实验性。选择承载虚拟机内存的后备方式。此项与后备页大小、冷热提示及压缩存储有联动。</summary>
        /// <fielddoc>
        /// <meaning>选择承载虚拟机内存的后备方式。此项与后备页大小、冷热提示及压缩存储有联动。</meaning>
        /// <unit>类型编码。</unit>
        /// <values>取值和联动规则见 <see cref="ExHyperV.Services.VmMemoryFields.BackingType"/>；该处为本字段的完整说明。</values>
        /// <source>VmMemoryService.GetVmMemorySettingsAsync 读取 Msvm_MemorySettingData.BackingType。</source>
        /// <applicability>实验性。使用条件见 <see cref="ExHyperV.Services.VmMemoryFields.BackingType"/>。</applicability>
        /// <absence>读取为 null 表示未取得；写入 null 保留原值。</absence>
        /// <references><see cref="ExHyperV.Services.VmMemoryFields.BackingType"/>；<see cref="M:ExHyperV.Services.VmMemoryService.SetVmMemorySettingsAsync(System.Guid,ExHyperV.Models.VmMemorySettings,System.Collections.Generic.IReadOnlyCollection{System.String})"/>。</references>
        /// <modification kind="supported" input="newSettings.BackingType" submission="明确选择字段">将 nameof(VmMemorySettings.BackingType) 加入 changedProperties。具体规范化和必要联动见 <see cref="ExHyperV.Services.VmMemoryFields.BackingType"/>。</modification>
        /// </fielddoc>
        public byte? BackingType { get => _backingType; set => _backingType = value; }
        private byte? _backingType;              // 内存后端类型
        /// <summary>实验性。动态内存调整所使用的对齐配置；与决定内存后备页面类型的 BackingPageSize 是不同字段。</summary>
        /// <fielddoc>
        /// <meaning>动态内存调整所使用的对齐配置；与决定内存后备页面类型的 BackingPageSize 是不同字段。</meaning>
        /// <unit>对齐选项编码。</unit>
        /// <values>取值和联动规则见 <see cref="ExHyperV.Services.VmMemoryFields.DynMemOperationAlignment"/>；该处为本字段的完整说明。</values>
        /// <source>VmMemoryService.GetVmMemorySettingsAsync 读取 Msvm_MemorySettingData.DynMemOperationAlignment。</source>
        /// <applicability>实验性。使用条件见 <see cref="ExHyperV.Services.VmMemoryFields.DynMemOperationAlignment"/>。</applicability>
        /// <absence>读取为 null 表示未取得；写入 null 保留原值。</absence>
        /// <references><see cref="ExHyperV.Services.VmMemoryFields.DynMemOperationAlignment"/>；<see cref="M:ExHyperV.Services.VmMemoryService.SetVmMemorySettingsAsync(System.Guid,ExHyperV.Models.VmMemorySettings,System.Collections.Generic.IReadOnlyCollection{System.String})"/>。</references>
        /// <modification kind="supported" input="newSettings.DynMemOperationAlignment" submission="明确选择字段">将 nameof(VmMemorySettings.DynMemOperationAlignment) 加入 changedProperties。具体规范化和必要联动见 <see cref="ExHyperV.Services.VmMemoryFields.DynMemOperationAlignment"/>。</modification>
        /// </fielddoc>
        public uint? DynMemOperationAlignment { get => _dynMemOperationAlignment; set => _dynMemOperationAlignment = value; }
        private uint? _dynMemOperationAlignment;  // 动态内存操作对齐
        /// <summary>实验性。配置内存访问跟踪的粒度策略。跟踪是否开启由 MemoryAccessTrackingState 单独配置。</summary>
        /// <fielddoc>
        /// <meaning>配置内存访问跟踪的粒度策略。跟踪是否开启由 MemoryAccessTrackingState 单独配置。</meaning>
        /// <unit>跟踪策略编码。</unit>
        /// <values>取值和联动规则见 <see cref="ExHyperV.Services.VmMemoryFields.MemoryAccessTrackingPolicy"/>；该处为本字段的完整说明。</values>
        /// <source>VmMemoryService.GetVmMemorySettingsAsync 读取 Msvm_MemorySettingData.MemoryAccessTrackingPolicy。</source>
        /// <applicability>实验性。使用条件见 <see cref="ExHyperV.Services.VmMemoryFields.MemoryAccessTrackingPolicy"/>。</applicability>
        /// <absence>读取为 null 表示未取得；写入 null 保留原值。</absence>
        /// <references><see cref="ExHyperV.Services.VmMemoryFields.MemoryAccessTrackingPolicy"/>；<see cref="M:ExHyperV.Services.VmMemoryService.SetVmMemorySettingsAsync(System.Guid,ExHyperV.Models.VmMemorySettings,System.Collections.Generic.IReadOnlyCollection{System.String})"/>。</references>
        /// <modification kind="supported" input="newSettings.MemoryAccessTrackingPolicy" submission="明确选择字段">将 nameof(VmMemorySettings.MemoryAccessTrackingPolicy) 加入 changedProperties。具体规范化和必要联动见 <see cref="ExHyperV.Services.VmMemoryFields.MemoryAccessTrackingPolicy"/>。</modification>
        /// </fielddoc>
        public byte? MemoryAccessTrackingPolicy { get => _memoryAccessTrackingPolicy; set => _memoryAccessTrackingPolicy = value; }
        private byte? _memoryAccessTrackingPolicy; // 访问跟踪策略
        /// <summary>实验性。配置是否启用内存访问跟踪，或交给逐 NUMA 节点配置决定。</summary>
        /// <fielddoc>
        /// <meaning>配置是否启用内存访问跟踪，或交给逐 NUMA 节点配置决定。</meaning>
        /// <unit>状态编码。</unit>
        /// <values>取值和联动规则见 <see cref="ExHyperV.Services.VmMemoryFields.MemoryAccessTrackingState"/>；该处为本字段的完整说明。</values>
        /// <source>VmMemoryService.GetVmMemorySettingsAsync 读取 Msvm_MemorySettingData.MemoryAccessTrackingState。</source>
        /// <applicability>实验性。使用条件见 <see cref="ExHyperV.Services.VmMemoryFields.MemoryAccessTrackingState"/>。</applicability>
        /// <absence>读取为 null 表示未取得；写入 null 保留原值。</absence>
        /// <references><see cref="ExHyperV.Services.VmMemoryFields.MemoryAccessTrackingState"/>；<see cref="M:ExHyperV.Services.VmMemoryService.SetVmMemorySettingsAsync(System.Guid,ExHyperV.Models.VmMemorySettings,System.Collections.Generic.IReadOnlyCollection{System.String})"/>。</references>
        /// <modification kind="supported" input="newSettings.MemoryAccessTrackingState" submission="明确选择字段">将 nameof(VmMemorySettings.MemoryAccessTrackingState) 加入 changedProperties。具体规范化和必要联动见 <see cref="ExHyperV.Services.VmMemoryFields.MemoryAccessTrackingState"/>。</modification>
        /// </fielddoc>
        public byte? MemoryAccessTrackingState { get => _memoryAccessTrackingState; set => _memoryAccessTrackingState = value; }
        private byte? _memoryAccessTrackingState;  // 访问跟踪状态
        /// <summary>实验性。请求为虚拟机启用 SGX 支持。SGX 内存容量由 SgxSize 配置。</summary>
        /// <fielddoc>
        /// <meaning>请求为虚拟机启用 SGX 支持。SGX 内存容量由 SgxSize 配置。</meaning>
        /// <values>取值和联动规则见 <see cref="ExHyperV.Services.VmMemoryFields.SgxEnabled"/>；该处为本字段的完整说明。</values>
        /// <source>VmMemoryService.GetVmMemorySettingsAsync 读取 Msvm_MemorySettingData.SgxEnabled。</source>
        /// <applicability>实验性。使用条件见 <see cref="ExHyperV.Services.VmMemoryFields.SgxEnabled"/>。</applicability>
        /// <absence>读取为 null 表示未取得；写入 null 保留原值。</absence>
        /// <references><see cref="ExHyperV.Services.VmMemoryFields.SgxEnabled"/>；<see cref="M:ExHyperV.Services.VmMemoryService.SetVmMemorySettingsAsync(System.Guid,ExHyperV.Models.VmMemorySettings,System.Collections.Generic.IReadOnlyCollection{System.String})"/>。</references>
        /// <modification kind="supported" input="newSettings.SgxEnabled" submission="明确选择字段">将 nameof(VmMemorySettings.SgxEnabled) 加入 changedProperties。具体规范化和必要联动见 <see cref="ExHyperV.Services.VmMemoryFields.SgxEnabled"/>。</modification>
        /// </fielddoc>
        public bool? SgxEnabled { get => _sgxEnabled; set => _sgxEnabled = value; }
        private bool? _sgxEnabled;                // SGX 开关
        /// <summary>实验性。为虚拟机配置的 SGX EPC 内存容量，与普通启动内存分开。</summary>
        /// <fielddoc>
        /// <meaning>为虚拟机配置的 SGX EPC 内存容量，与普通启动内存分开。</meaning>
        /// <unit>MiB；写入时使用整数容量。</unit>
        /// <values>取值和联动规则见 <see cref="ExHyperV.Services.VmMemoryFields.SgxSize"/>；该处为本字段的完整说明。</values>
        /// <source>VmMemoryService.GetVmMemorySettingsAsync 读取 Msvm_MemorySettingData.SgxSize。</source>
        /// <applicability>实验性。使用条件见 <see cref="ExHyperV.Services.VmMemoryFields.SgxSize"/>。</applicability>
        /// <absence>GetVmMemorySettingsAsync 未取得底层容量时保留 null，实际读到的 0 保留为 0；需要区分属性不存在、缺值与读取失败时，使用自由查询的对应字段状态。</absence>
        /// <references><see cref="ExHyperV.Services.VmMemoryFields.SgxSize"/>；<see cref="M:ExHyperV.Services.VmMemoryService.SetVmMemorySettingsAsync(System.Guid,ExHyperV.Models.VmMemorySettings,System.Collections.Generic.IReadOnlyCollection{System.String})"/>。</references>
        /// <modification kind="supported" input="newSettings.SgxSize" submission="明确选择字段">将 nameof(VmMemorySettings.SgxSize) 加入 changedProperties。具体规范化和必要联动见 <see cref="ExHyperV.Services.VmMemoryFields.SgxSize"/>。</modification>
        /// </fielddoc>
        public double? SgxSize { get => _sgxSize; set => _sgxSize = value; }
        private double? _sgxSize;                  // SGX 大小
        /// <summary>实验性。SGX 启动控制的访问模式配置，与虚拟机的普通引导顺序无关。</summary>
        /// <fielddoc>
        /// <meaning>SGX 启动控制的访问模式配置，与虚拟机的普通引导顺序无关。</meaning>
        /// <unit>模式编码。</unit>
        /// <values>取值和联动规则见 <see cref="ExHyperV.Services.VmMemoryFields.SgxLaunchControlMode"/>；该处为本字段的完整说明。</values>
        /// <source>VmMemoryService.GetVmMemorySettingsAsync 读取 Msvm_MemorySettingData.SgxLaunchControlMode。</source>
        /// <applicability>实验性。使用条件见 <see cref="ExHyperV.Services.VmMemoryFields.SgxLaunchControlMode"/>。</applicability>
        /// <absence>读取为 null 表示未取得；写入 null 保留原值。</absence>
        /// <references><see cref="ExHyperV.Services.VmMemoryFields.SgxLaunchControlMode"/>；<see cref="M:ExHyperV.Services.VmMemoryService.SetVmMemorySettingsAsync(System.Guid,ExHyperV.Models.VmMemorySettings,System.Collections.Generic.IReadOnlyCollection{System.String})"/>。</references>
        /// <modification kind="supported" input="newSettings.SgxLaunchControlMode" submission="明确选择字段">将 nameof(VmMemorySettings.SgxLaunchControlMode) 加入 changedProperties。具体规范化和必要联动见 <see cref="ExHyperV.Services.VmMemoryFields.SgxLaunchControlMode"/>。</modification>
        /// </fielddoc>
        public uint? SgxLaunchControlMode { get => _sgxLaunchControlMode; set => _sgxLaunchControlMode = value; }
        private uint? _sgxLaunchControlMode;      // SGX 启动模式
        /// <summary>实验性。来宾物理地址（GPA）固定配置的开关。</summary>
        /// <fielddoc>
        /// <meaning>来宾物理地址（GPA）固定配置的开关。</meaning>
        /// <values>取值和联动规则见 <see cref="ExHyperV.Services.VmMemoryFields.EnableGpaPinning"/>；该处为本字段的完整说明。</values>
        /// <source>VmMemoryService.GetVmMemorySettingsAsync 读取 Msvm_MemorySettingData.EnableGpaPinning。</source>
        /// <applicability>实验性。使用条件见 <see cref="ExHyperV.Services.VmMemoryFields.EnableGpaPinning"/>。</applicability>
        /// <absence>读取为 null 表示未取得；写入 null 保留原值。</absence>
        /// <references><see cref="ExHyperV.Services.VmMemoryFields.EnableGpaPinning"/>；<see cref="M:ExHyperV.Services.VmMemoryService.SetVmMemorySettingsAsync(System.Guid,ExHyperV.Models.VmMemorySettings,System.Collections.Generic.IReadOnlyCollection{System.String})"/>。</references>
        /// <modification kind="supported" input="newSettings.EnableGpaPinning" submission="明确选择字段">将 nameof(VmMemorySettings.EnableGpaPinning) 加入 changedProperties。具体规范化和必要联动见 <see cref="ExHyperV.Services.VmMemoryFields.EnableGpaPinning"/>。</modification>
        /// </fielddoc>
        public bool? EnableGpaPinning { get => _enableGpaPinning; set => _enableGpaPinning = value; }
        private bool? _enableGpaPinning;          // GPA 固定
        /// <summary>实验性 CXL 开关；已知部分 VMMS 版本在保存时可能崩溃。Core 不按研究风险拦截，由调用方提示，详见 VmMemoryFields.CxlEnabled。</summary>
        /// <fielddoc>
        /// <meaning>虚拟机内存配置中的 CXL 开关。此入口只修改 CxlEnabled，不枚举、分配或直通具体 CXL 设备。</meaning>
        /// <values>取值和联动规则见 <see cref="ExHyperV.Services.VmMemoryFields.CxlEnabled"/>；该处为本字段的完整说明。</values>
        /// <source>VmMemoryService.GetVmMemorySettingsAsync 读取 Msvm_MemorySettingData.CxlEnabled。</source>
        /// <applicability>已知版本存在 VMMS 崩溃风险。版本范围及关闭限制见 <see cref="ExHyperV.Services.VmMemoryFields.CxlEnabled"/>。</applicability>
        /// <absence>读取为 null 表示未取得；写入 null 保留原值。</absence>
        /// <references><see cref="ExHyperV.Services.VmMemoryFields.CxlEnabled"/>；<see cref="M:ExHyperV.Services.VmMemoryService.SetVmMemorySettingsAsync(System.Guid,ExHyperV.Models.VmMemorySettings,System.Collections.Generic.IReadOnlyCollection{System.String})"/>。</references>
        /// <modification kind="supported" input="newSettings.CxlEnabled" submission="明确选择字段">将 nameof(VmMemorySettings.CxlEnabled) 加入 changedProperties。具体规范化和必要联动见 <see cref="ExHyperV.Services.VmMemoryFields.CxlEnabled"/>。</modification>
        /// </fielddoc>
        public bool? CxlEnabled { get => _cxlEnabled; set => _cxlEnabled = value; }
        private bool? _cxlEnabled;                // CXL 支持
        /// <summary>实验性。控制内存配置中的冷提示开关。</summary>
        /// <fielddoc>
        /// <meaning>控制内存配置中的冷提示开关。</meaning>
        /// <values>取值和联动规则见 <see cref="ExHyperV.Services.VmMemoryFields.EnableColdHint"/>；该处为本字段的完整说明。</values>
        /// <source>VmMemoryService.GetVmMemorySettingsAsync 读取 Msvm_MemorySettingData.EnableColdHint。</source>
        /// <applicability>实验性。使用条件见 <see cref="ExHyperV.Services.VmMemoryFields.EnableColdHint"/>。</applicability>
        /// <absence>读取为 null 表示未取得；写入 null 保留原值。</absence>
        /// <references><see cref="ExHyperV.Services.VmMemoryFields.EnableColdHint"/>；<see cref="M:ExHyperV.Services.VmMemoryService.SetVmMemorySettingsAsync(System.Guid,ExHyperV.Models.VmMemorySettings,System.Collections.Generic.IReadOnlyCollection{System.String})"/>。</references>
        /// <modification kind="supported" input="newSettings.EnableColdHint" submission="明确选择字段">将 nameof(VmMemorySettings.EnableColdHint) 加入 changedProperties。具体规范化和必要联动见 <see cref="ExHyperV.Services.VmMemoryFields.EnableColdHint"/>。</modification>
        /// </fielddoc>
        public bool? EnableColdHint { get => _enableColdHint; set => _enableColdHint = value; }
        private bool? _enableColdHint;
        /// <summary>实验性。控制内存配置中的热提示开关。</summary>
        /// <fielddoc>
        /// <meaning>控制内存配置中的热提示开关。</meaning>
        /// <values>取值和联动规则见 <see cref="ExHyperV.Services.VmMemoryFields.EnableHotHint"/>；该处为本字段的完整说明。</values>
        /// <source>VmMemoryService.GetVmMemorySettingsAsync 读取 Msvm_MemorySettingData.EnableHotHint。</source>
        /// <applicability>实验性。使用条件见 <see cref="ExHyperV.Services.VmMemoryFields.EnableHotHint"/>。</applicability>
        /// <absence>读取为 null 表示未取得；写入 null 保留原值。</absence>
        /// <references><see cref="ExHyperV.Services.VmMemoryFields.EnableHotHint"/>；<see cref="M:ExHyperV.Services.VmMemoryService.SetVmMemorySettingsAsync(System.Guid,ExHyperV.Models.VmMemorySettings,System.Collections.Generic.IReadOnlyCollection{System.String})"/>。</references>
        /// <modification kind="supported" input="newSettings.EnableHotHint" submission="明确选择字段">将 nameof(VmMemorySettings.EnableHotHint) 加入 changedProperties。具体规范化和必要联动见 <see cref="ExHyperV.Services.VmMemoryFields.EnableHotHint"/>。</modification>
        /// </fielddoc>
        public bool? EnableHotHint { get => _enableHotHint; set => _enableHotHint = value; }
        private bool? _enableHotHint;
        /// <summary>实验性。保留 Hyper-V 的 EnableEpf 配置开关。相关机制暂不明确。</summary>
        /// <fielddoc>
        /// <meaning>保留 Hyper-V 的 EnableEpf 配置开关。相关机制暂不明确。</meaning>
        /// <values>取值和联动规则见 <see cref="ExHyperV.Services.VmMemoryFields.EnableEpf"/>；该处为本字段的完整说明。</values>
        /// <source>VmMemoryService.GetVmMemorySettingsAsync 读取 Msvm_MemorySettingData.EnableEpf。</source>
        /// <applicability>实验性。使用条件见 <see cref="ExHyperV.Services.VmMemoryFields.EnableEpf"/>。</applicability>
        /// <absence>读取为 null 表示未取得；写入 null 保留原值。</absence>
        /// <references><see cref="ExHyperV.Services.VmMemoryFields.EnableEpf"/>；<see cref="M:ExHyperV.Services.VmMemoryService.SetVmMemorySettingsAsync(System.Guid,ExHyperV.Models.VmMemorySettings,System.Collections.Generic.IReadOnlyCollection{System.String})"/>。</references>
        /// <modification kind="supported" input="newSettings.EnableEpf" submission="明确选择字段">将 nameof(VmMemorySettings.EnableEpf) 加入 changedProperties。具体规范化和必要联动见 <see cref="ExHyperV.Services.VmMemoryFields.EnableEpf"/>。</modification>
        /// </fielddoc>
        public bool? EnableEpf { get => _enableEpf; set => _enableEpf = value; }
        private bool? _enableEpf;
        /// <summary>实验性。内存配置中的专用压缩存储开关。</summary>
        /// <fielddoc>
        /// <meaning>内存配置中的专用压缩存储开关。</meaning>
        /// <values>取值和联动规则见 <see cref="ExHyperV.Services.VmMemoryFields.EnablePrivateCompressionStore"/>；该处为本字段的完整说明。</values>
        /// <source>VmMemoryService.GetVmMemorySettingsAsync 读取 Msvm_MemorySettingData.EnablePrivateCompressionStore。</source>
        /// <applicability>实验性。使用条件见 <see cref="ExHyperV.Services.VmMemoryFields.EnablePrivateCompressionStore"/>。</applicability>
        /// <absence>读取为 null 表示未取得；写入 null 保留原值。</absence>
        /// <references><see cref="ExHyperV.Services.VmMemoryFields.EnablePrivateCompressionStore"/>；<see cref="M:ExHyperV.Services.VmMemoryService.SetVmMemorySettingsAsync(System.Guid,ExHyperV.Models.VmMemorySettings,System.Collections.Generic.IReadOnlyCollection{System.String})"/>。</references>
        /// <modification kind="supported" input="newSettings.EnablePrivateCompressionStore" submission="明确选择字段">将 nameof(VmMemorySettings.EnablePrivateCompressionStore) 加入 changedProperties。具体规范化和必要联动见 <see cref="ExHyperV.Services.VmMemoryFields.EnablePrivateCompressionStore"/>。</modification>
        /// </fielddoc>
        public bool? EnablePrivateCompressionStore { get => _enablePrivateCompressionStore; set => _enablePrivateCompressionStore = value; }
        private bool? _enablePrivateCompressionStore;
        /// <summary>实验性。限制来宾可见的单个虚拟 NUMA 节点的内存容量。底层名称虽然包含 Blocks，调用时仍按内存容量理解。</summary>
        /// <fielddoc>
        /// <meaning>限制来宾可见的单个虚拟 NUMA 节点的内存容量。底层名称虽然包含 Blocks，调用时仍按内存容量理解。</meaning>
        /// <unit>MiB。</unit>
        /// <values>取值和联动规则见 <see cref="ExHyperV.Services.VmMemoryFields.MaxMemoryBlocksPerNumaNode"/>；该处为本字段的完整说明。</values>
        /// <source>VmMemoryService.GetVmMemorySettingsAsync 读取 Msvm_MemorySettingData.MaxMemoryBlocksPerNumaNode。</source>
        /// <applicability>实验性。使用条件见 <see cref="ExHyperV.Services.VmMemoryFields.MaxMemoryBlocksPerNumaNode"/>。</applicability>
        /// <absence>读取为 null 表示未取得；写入 null 保留原值。</absence>
        /// <references><see cref="ExHyperV.Services.VmMemoryFields.MaxMemoryBlocksPerNumaNode"/>；<see cref="M:ExHyperV.Services.VmMemoryService.SetVmMemorySettingsAsync(System.Guid,ExHyperV.Models.VmMemorySettings,System.Collections.Generic.IReadOnlyCollection{System.String})"/>。</references>
        /// <modification kind="supported" input="newSettings.MaxMemoryBlocksPerNumaNode" submission="明确选择字段">将 nameof(VmMemorySettings.MaxMemoryBlocksPerNumaNode) 加入 changedProperties。具体规范化和必要联动见 <see cref="ExHyperV.Services.VmMemoryFields.MaxMemoryBlocksPerNumaNode"/>。</modification>
        /// </fielddoc>
        public ulong? MaxMemoryBlocksPerNumaNode { get => _maxMemoryBlocksPerNumaNode; set => _maxMemoryBlocksPerNumaNode = value; }
        private ulong? _maxMemoryBlocksPerNumaNode;
        /// <summary>实验性。SGX 默认启动控制配置的原始文本。</summary>
        /// <fielddoc>
        /// <meaning>SGX 默认启动控制配置的原始文本。</meaning>
        /// <unit>原始字符串。</unit>
        /// <values>取值和联动规则见 <see cref="ExHyperV.Services.VmMemoryFields.SgxLaunchControlDefault"/>；该处为本字段的完整说明。</values>
        /// <source>VmMemoryService.GetVmMemorySettingsAsync 读取 Msvm_MemorySettingData.SgxLaunchControlDefault。</source>
        /// <applicability>实验性。使用条件见 <see cref="ExHyperV.Services.VmMemoryFields.SgxLaunchControlDefault"/>。</applicability>
        /// <absence>读取为 null 表示未取得；写入 null 保留原值。</absence>
        /// <references><see cref="ExHyperV.Services.VmMemoryFields.SgxLaunchControlDefault"/>；<see cref="M:ExHyperV.Services.VmMemoryService.SetVmMemorySettingsAsync(System.Guid,ExHyperV.Models.VmMemorySettings,System.Collections.Generic.IReadOnlyCollection{System.String})"/>。</references>
        /// <modification kind="supported" input="newSettings.SgxLaunchControlDefault" submission="明确选择字段">将 nameof(VmMemorySettings.SgxLaunchControlDefault) 加入 changedProperties。具体规范化和必要联动见 <see cref="ExHyperV.Services.VmMemoryFields.SgxLaunchControlDefault"/>。</modification>
        /// </fielddoc>
        public string? SgxLaunchControlDefault { get => _sgxLaunchControlDefault; set => _sgxLaunchControlDefault = value; }
        private string? _sgxLaunchControlDefault;

        /// <summary>实验性。虚拟机内存的加密策略。</summary>
        /// <fielddoc>
        /// <meaning>虚拟机内存的加密策略。</meaning>
        /// <unit>策略编码。</unit>
        /// <values>取值和联动规则见 <see cref="ExHyperV.Services.VmMemoryFields.MemoryEncryptionPolicy"/>；该处为本字段的完整说明。</values>
        /// <source>VmMemoryService.GetVmMemorySettingsAsync 读取 Msvm_MemorySettingData.MemoryEncryptionPolicy。</source>
        /// <applicability>实验性。使用条件见 <see cref="ExHyperV.Services.VmMemoryFields.MemoryEncryptionPolicy"/>。</applicability>
        /// <absence>读取为 null 表示未取得；写入 null 保留原值。</absence>
        /// <references><see cref="ExHyperV.Services.VmMemoryFields.MemoryEncryptionPolicy"/>；<see cref="M:ExHyperV.Services.VmMemoryService.SetVmMemorySettingsAsync(System.Guid,ExHyperV.Models.VmMemorySettings,System.Collections.Generic.IReadOnlyCollection{System.String})"/>。</references>
        /// <modification kind="supported" input="newSettings.MemoryEncryptionPolicy" submission="明确选择字段">将 nameof(VmMemorySettings.MemoryEncryptionPolicy) 加入 changedProperties。具体规范化和必要联动见 <see cref="ExHyperV.Services.VmMemoryFields.MemoryEncryptionPolicy"/>。</modification>
        /// </fielddoc>
        public byte? MemoryEncryptionPolicy { get => _memoryEncryptionPolicy; set => _memoryEncryptionPolicy = value; }
        private byte? _memoryEncryptionPolicy;

        /// <summary>复制全部配置值和宿主能力为独立模型。</summary>
        /// <returns>可独立修改的配置副本。</returns>
        public VmMemorySettings Clone() => new()
        {
            _startup = _startup,
            _dynamicMemoryEnabled = _dynamicMemoryEnabled,
            _minimum = _minimum,
            _maximum = _maximum,
            _buffer = _buffer,
            _weight = _weight,
            _backingPageSize = _backingPageSize,
            SupportsBackingPageSize = SupportsBackingPageSize,
            SupportedProps = SupportedProps == null ? null : new(SupportedProps, StringComparer.Ordinal),
            _backingType = _backingType,
            _dynMemOperationAlignment = _dynMemOperationAlignment,
            _memoryAccessTrackingPolicy = _memoryAccessTrackingPolicy,
            _memoryAccessTrackingState = _memoryAccessTrackingState,
            _sgxEnabled = _sgxEnabled,
            _sgxSize = _sgxSize,
            _sgxLaunchControlMode = _sgxLaunchControlMode,
            _enableGpaPinning = _enableGpaPinning,
            _cxlEnabled = _cxlEnabled,
            _enableColdHint = _enableColdHint,
            _enableHotHint = _enableHotHint,
            _enableEpf = _enableEpf,
            _enablePrivateCompressionStore = _enablePrivateCompressionStore,
            _maxMemoryBlocksPerNumaNode = _maxMemoryBlocksPerNumaNode,
            _sgxLaunchControlDefault = _sgxLaunchControlDefault,
            _memoryEncryptionPolicy = _memoryEncryptionPolicy
        };

        /// <summary>将备份值恢复到当前编辑模型。</summary>
        /// <param name="other">要恢复的设置快照；null 时不做操作。</param>
        public void Restore(VmMemorySettings other)
        {
            if (other == null) return;
            Startup = other.Startup;
            DynamicMemoryEnabled = other.DynamicMemoryEnabled;
            Minimum = other.Minimum;
            Maximum = other.Maximum;
            Buffer = other.Buffer;
            Weight = other.Weight;
            ConfigurePageSizeSupport(other.SupportsBackingPageSize);
            SupportedProps = other.SupportedProps == null ? null : new(other.SupportedProps, StringComparer.Ordinal);
            BackingPageSize = other.BackingPageSize;
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
