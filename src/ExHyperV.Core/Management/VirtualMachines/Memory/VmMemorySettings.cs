using CommunityToolkit.Mvvm.ComponentModel;

namespace ExHyperV.Models
{
    /// <summary>页面大小下拉项（与 VmMemorySettings.BackingPageSize 配套）。</summary>
    public class PageSizeItem
    {
        /// <summary>页面大小选项的本地化显示说明。</summary>
        public string Description { get; set; } = string.Empty;
        /// <summary>统一页面大小编码：0 标准页、1 大页、2 巨页。</summary>
        public byte Value { get; set; }
    }

    /// <summary>
    /// 虚拟机内存的读取结果和修改请求值；通过 changedProperties 明确选择要提交的属性。
    /// </summary>
    /// <remarks>
    /// <para>标注“实验性”的字段继续支持读写，不需要额外的启用参数。已知编码、现有联动和未验证范围统一说明于 <see cref="ExHyperV.Services.VmMemoryFields"/> 的对应字段。</para>
    /// <para>可空扩展属性未取得值时为 null；null 不表示关闭，也不是“重置默认”的请求。修改时忽略值为 null 的选择；非空选择对应的宿主属性不存在时，在任何修改前返回 Unsupported。修改前通过服务读取配置；只提交本次选中的字段。Clone/Restore 用于本地快照，不提交宿主。</para>
    /// </remarks>
    public partial class VmMemorySettings : ObservableObject
    {
        /// <summary>启动内存，单位为 MiB（1024² 字节）。</summary>
        /// <fielddoc>
        /// <meaning>虚拟机配置的启动内存。动态内存启用后，运行时分配量可以改变；此字段不表示当前已分配内存，也不表示来宾程序已经使用的内存。</meaning>
        /// <unit>MiB，1 MiB = 1,048,576 字节。</unit>
        /// <values>读取当前配置值。可接受的设置范围及版本差异待核实；不能将模型初始值 0 解释为有效配置。</values>
        /// <source>VmMemoryService.GetVmMemorySettingsAsync 经 MapMemory 读取 Msvm_MemorySettingData.VirtualQuantity；现有列表查询也读取同一底层属性。</source>
        /// <applicability>这是配置量，不随动态内存的每次分配变化。具体版本、代数及运行状态组合的验证范围待补充。</applicability>
        /// <absence>当前 MapMemory 在底层值为空时回退到 0；此设置模型的本属性没有独立有效性标记，读取失败由服务响应报告。需要字段级缺测状态时，使用 VmQueryService.QueryAsync 选择 VmMemoryFields.Startup。</absence>
        /// <verification>已对照当前源码的映射与单位换算；本说明不代表完成跨版本实机验证。</verification>
        /// <references><see cref="ExHyperV.Services.VmMemoryService"/>；运行时分配量参见 <see cref="ExHyperV.Services.VmQueryService"/>。</references>
        /// </fielddoc>
        [ObservableProperty] private long _startup;
        /// <summary>是否启用动态内存。</summary>
        [ObservableProperty] private bool _dynamicMemoryEnabled;
        /// <summary>动态内存最小值，单位为 MiB。</summary>
        [ObservableProperty] private long _minimum;
        /// <summary>动态内存最大值，单位为 MiB。</summary>
        [ObservableProperty] private long _maximum;
        /// <summary>动态内存缓冲比例，单位为百分比。</summary>
        [ObservableProperty] private int _buffer;
        /// <summary>内存竞争时的原始权重，对应 Hyper-V Weight；不进行归一化换算，读取缺少值时沿用 5000。</summary>
        [ObservableProperty] private uint _weight;
        /// <summary>统一页面大小编码：0 标准页、1 大页、2 巨页；null 表示未取得值。</summary>
        [ObservableProperty] private byte? _backingPageSize;

        // 页面统一使用 0/1/2 表示小页/大页/巨页。旧版 Hyper-V 没有 BackingPageSize，
        // 服务层会把 1/2 与 HugePagesEnabled=false/true 相互转换，因此旧系统只开放两项。
        /// <summary>宿主是否提供 BackingPageSize 属性；旧宿主通过 HugePagesEnabled 映射大页和巨页。</summary>
        public bool SupportsBackingPageSize { get; private set; }

        /// <summary>根据宿主能力构造的可变页面大小选项列表。</summary>
        public List<PageSizeItem> AvailablePageSizes { get; private set; } = new();

        /// <summary>创建内存设置模型，先提供标准页、大页、巨页三个选项；查询服务随后按实际宿主能力收窄。</summary>
        public VmMemorySettings()
        {
            // 尚未读取主机能力时沿用新系统的三项列表；查询完成后服务层会按实际 WMI 属性收窄。
            ConfigurePageSizeSupport(true);
        }

        /// <summary>更新本地页面大小选项，并发送 AvailablePageSizes 属性变化通知；不修改 Hyper-V。</summary>
        /// <param name="supportsBackingPageSize">true 提供 0、1、2 三项；false 只提供旧宿主可映射的 1、2。</param>
        public void ConfigurePageSizeSupport(bool supportsBackingPageSize)
        {
            SupportsBackingPageSize = supportsBackingPageSize;
            AvailablePageSizes = new List<PageSizeItem>();

            if (supportsBackingPageSize)
                AvailablePageSizes.Add(new PageSizeItem { Description = Properties.Resources.Mem_Standard, Value = 0 });

            AvailablePageSizes.Add(new PageSizeItem { Description = Properties.Resources.Mem_Large, Value = 1 });
            AvailablePageSizes.Add(new PageSizeItem { Description = Properties.Resources.Mem_Huge, Value = 2 });
            OnPropertyChanged(nameof(AvailablePageSizes));
        }

        // --- 实验性功能 ---
        /// <summary>实验性。选择承载虚拟机内存的后备方式。此项与后备页大小、冷热提示及压缩存储有联动。</summary>
        /// <fielddoc>
        /// <meaning>选择承载虚拟机内存的后备方式。此项与后备页大小、冷热提示及压缩存储有联动。</meaning>
        /// <unit>类型编码，不是内存大小。</unit>
        /// <values>取值和联动规则见 <see cref="ExHyperV.Services.VmMemoryFields.BackingType"/>；该处为本字段的完整说明。</values>
        /// <source>VmMemoryService.GetVmMemorySettingsAsync 读取 Msvm_MemorySettingData.BackingType。</source>
        /// <applicability>实验性读写能力；宿主和功能条件见 <see cref="ExHyperV.Services.VmMemoryFields.BackingType"/>。没有额外的实验功能总开关。</applicability>
        /// <absence>读取未取得值时为 null，不能当作关闭或默认值；提交 null 时忽略该选择，不表示清空，也不触发该字段的联动；其他非空选择的必要联动仍按对应字段说明执行。</absence>
        /// <verification>已核对当前映射；未验证范围逐项列在 <see cref="ExHyperV.Services.VmMemoryFields.BackingType"/>，不能把属性存在视为功能验证通过。</verification>
        /// <references><see cref="ExHyperV.Services.VmMemoryFields.BackingType"/>；<see cref="ExHyperV.Services.VmMemoryService.SetVmMemorySettingsAsync"/>。</references>
        /// <modification kind="supported" input="newSettings.BackingType" submission="明确选择字段">将 nameof(VmMemorySettings.BackingType) 加入 changedProperties。具体规范化和必要联动见 <see cref="ExHyperV.Services.VmMemoryFields.BackingType"/>。</modification>
        /// </fielddoc>
        [ObservableProperty] private byte? _backingType;              // 内存后端类型
        /// <summary>实验性。动态内存调整所使用的对齐配置；与决定内存后备页面类型的 BackingPageSize 是不同字段。</summary>
        /// <fielddoc>
        /// <meaning>动态内存调整所使用的对齐配置；与决定内存后备页面类型的 BackingPageSize 是不同字段。</meaning>
        /// <unit>对齐选项编码，不是直接以字节传入的对齐量。</unit>
        /// <values>取值和联动规则见 <see cref="ExHyperV.Services.VmMemoryFields.DynMemOperationAlignment"/>；该处为本字段的完整说明。</values>
        /// <source>VmMemoryService.GetVmMemorySettingsAsync 读取 Msvm_MemorySettingData.DynMemOperationAlignment。</source>
        /// <applicability>实验性读写能力；宿主和功能条件见 <see cref="ExHyperV.Services.VmMemoryFields.DynMemOperationAlignment"/>。没有额外的实验功能总开关。</applicability>
        /// <absence>读取未取得值时为 null，不能当作关闭或默认值；提交 null 时忽略该选择，不表示清空，也不触发该字段的联动；其他非空选择的必要联动仍按对应字段说明执行。</absence>
        /// <verification>已核对当前映射；未验证范围逐项列在 <see cref="ExHyperV.Services.VmMemoryFields.DynMemOperationAlignment"/>，不能把属性存在视为功能验证通过。</verification>
        /// <references><see cref="ExHyperV.Services.VmMemoryFields.DynMemOperationAlignment"/>；<see cref="ExHyperV.Services.VmMemoryService.SetVmMemorySettingsAsync"/>。</references>
        /// <modification kind="supported" input="newSettings.DynMemOperationAlignment" submission="明确选择字段">将 nameof(VmMemorySettings.DynMemOperationAlignment) 加入 changedProperties。具体规范化和必要联动见 <see cref="ExHyperV.Services.VmMemoryFields.DynMemOperationAlignment"/>。</modification>
        /// </fielddoc>
        [ObservableProperty] private uint? _dynMemOperationAlignment;  // 动态内存操作对齐
        /// <summary>实验性。配置内存访问跟踪的粒度策略。跟踪是否开启由 MemoryAccessTrackingState 单独配置。</summary>
        /// <fielddoc>
        /// <meaning>配置内存访问跟踪的粒度策略。跟踪是否开启由 MemoryAccessTrackingState 单独配置。</meaning>
        /// <unit>跟踪策略编码；不能与 BackingPageSize 的编码互换。</unit>
        /// <values>取值和联动规则见 <see cref="ExHyperV.Services.VmMemoryFields.MemoryAccessTrackingPolicy"/>；该处为本字段的完整说明。</values>
        /// <source>VmMemoryService.GetVmMemorySettingsAsync 读取 Msvm_MemorySettingData.MemoryAccessTrackingPolicy。</source>
        /// <applicability>实验性读写能力；宿主和功能条件见 <see cref="ExHyperV.Services.VmMemoryFields.MemoryAccessTrackingPolicy"/>。没有额外的实验功能总开关。</applicability>
        /// <absence>读取未取得值时为 null，不能当作关闭或默认值；提交 null 时忽略该选择，不表示清空，也不触发该字段的联动；其他非空选择的必要联动仍按对应字段说明执行。</absence>
        /// <verification>已核对当前映射；未验证范围逐项列在 <see cref="ExHyperV.Services.VmMemoryFields.MemoryAccessTrackingPolicy"/>，不能把属性存在视为功能验证通过。</verification>
        /// <references><see cref="ExHyperV.Services.VmMemoryFields.MemoryAccessTrackingPolicy"/>；<see cref="ExHyperV.Services.VmMemoryService.SetVmMemorySettingsAsync"/>。</references>
        /// <modification kind="supported" input="newSettings.MemoryAccessTrackingPolicy" submission="明确选择字段">将 nameof(VmMemorySettings.MemoryAccessTrackingPolicy) 加入 changedProperties。具体规范化和必要联动见 <see cref="ExHyperV.Services.VmMemoryFields.MemoryAccessTrackingPolicy"/>。</modification>
        /// </fielddoc>
        [ObservableProperty] private byte? _memoryAccessTrackingPolicy; // 访问跟踪策略
        /// <summary>实验性。配置是否启用内存访问跟踪，或交给逐 NUMA 节点配置决定。此值是配置，不是一次访问采样结果。</summary>
        /// <fielddoc>
        /// <meaning>配置是否启用内存访问跟踪，或交给逐 NUMA 节点配置决定。此值是配置，不是一次访问采样结果。</meaning>
        /// <unit>状态编码。</unit>
        /// <values>取值和联动规则见 <see cref="ExHyperV.Services.VmMemoryFields.MemoryAccessTrackingState"/>；该处为本字段的完整说明。</values>
        /// <source>VmMemoryService.GetVmMemorySettingsAsync 读取 Msvm_MemorySettingData.MemoryAccessTrackingState。</source>
        /// <applicability>实验性读写能力；宿主和功能条件见 <see cref="ExHyperV.Services.VmMemoryFields.MemoryAccessTrackingState"/>。没有额外的实验功能总开关。</applicability>
        /// <absence>读取未取得值时为 null，不能当作关闭或默认值；提交 null 时忽略该选择，不表示清空，也不触发该字段的联动；其他非空选择的必要联动仍按对应字段说明执行。</absence>
        /// <verification>已核对当前映射；未验证范围逐项列在 <see cref="ExHyperV.Services.VmMemoryFields.MemoryAccessTrackingState"/>，不能把属性存在视为功能验证通过。</verification>
        /// <references><see cref="ExHyperV.Services.VmMemoryFields.MemoryAccessTrackingState"/>；<see cref="ExHyperV.Services.VmMemoryService.SetVmMemorySettingsAsync"/>。</references>
        /// <modification kind="supported" input="newSettings.MemoryAccessTrackingState" submission="明确选择字段">将 nameof(VmMemorySettings.MemoryAccessTrackingState) 加入 changedProperties。具体规范化和必要联动见 <see cref="ExHyperV.Services.VmMemoryFields.MemoryAccessTrackingState"/>。</modification>
        /// </fielddoc>
        [ObservableProperty] private byte? _memoryAccessTrackingState;  // 访问跟踪状态
        /// <summary>实验性。请求为虚拟机启用 SGX 支持。SGX 内存容量由 SgxSize 配置；启用配置本身不表示来宾已能使用 SGX。</summary>
        /// <fielddoc>
        /// <meaning>请求为虚拟机启用 SGX 支持。SGX 内存容量由 SgxSize 配置；启用配置本身不表示来宾已能使用 SGX。</meaning>
        /// <values>取值和联动规则见 <see cref="ExHyperV.Services.VmMemoryFields.SgxEnabled"/>；该处为本字段的完整说明。</values>
        /// <source>VmMemoryService.GetVmMemorySettingsAsync 读取 Msvm_MemorySettingData.SgxEnabled。</source>
        /// <applicability>实验性读写能力；宿主和功能条件见 <see cref="ExHyperV.Services.VmMemoryFields.SgxEnabled"/>。没有额外的实验功能总开关。</applicability>
        /// <absence>读取未取得值时为 null，不能当作关闭或默认值；提交 null 时忽略该选择，不表示清空，也不触发该字段的联动；其他非空选择的必要联动仍按对应字段说明执行。</absence>
        /// <verification>已核对当前映射；未验证范围逐项列在 <see cref="ExHyperV.Services.VmMemoryFields.SgxEnabled"/>，不能把属性存在视为功能验证通过。</verification>
        /// <references><see cref="ExHyperV.Services.VmMemoryFields.SgxEnabled"/>；<see cref="ExHyperV.Services.VmMemoryService.SetVmMemorySettingsAsync"/>。</references>
        /// <modification kind="supported" input="newSettings.SgxEnabled" submission="明确选择字段">将 nameof(VmMemorySettings.SgxEnabled) 加入 changedProperties。具体规范化和必要联动见 <see cref="ExHyperV.Services.VmMemoryFields.SgxEnabled"/>。</modification>
        /// </fielddoc>
        [ObservableProperty] private bool? _sgxEnabled;                // SGX 开关
        /// <summary>实验性。为虚拟机配置的 SGX EPC 内存容量，与普通启动内存分开。</summary>
        /// <fielddoc>
        /// <meaning>为虚拟机配置的 SGX EPC 内存容量，与普通启动内存分开。</meaning>
        /// <unit>MiB；Core 的容量单位。底层属性为 UInt64，当前公开模型以 Double 表示，不提供小数 MiB 的写入精度。</unit>
        /// <values>取值和联动规则见 <see cref="ExHyperV.Services.VmMemoryFields.SgxSize"/>；该处为本字段的完整说明。</values>
        /// <source>VmMemoryService.GetVmMemorySettingsAsync 读取 Msvm_MemorySettingData.SgxSize。</source>
        /// <applicability>实验性读写能力；宿主和功能条件见 <see cref="ExHyperV.Services.VmMemoryFields.SgxSize"/>。没有额外的实验功能总开关。</applicability>
        /// <absence>GetVmMemorySettingsAsync 未取得底层容量时保留 null，实际读到的 0 保留为 0；需要区分属性不存在、缺值与读取失败时，使用自由查询的对应字段状态。</absence>
        /// <verification>已核对当前映射；未验证范围逐项列在 <see cref="ExHyperV.Services.VmMemoryFields.SgxSize"/>，不能把属性存在视为功能验证通过。</verification>
        /// <references><see cref="ExHyperV.Services.VmMemoryFields.SgxSize"/>；<see cref="ExHyperV.Services.VmMemoryService.SetVmMemorySettingsAsync"/>。</references>
        /// <modification kind="supported" input="newSettings.SgxSize" submission="明确选择字段">将 nameof(VmMemorySettings.SgxSize) 加入 changedProperties。具体规范化和必要联动见 <see cref="ExHyperV.Services.VmMemoryFields.SgxSize"/>。</modification>
        /// </fielddoc>
        [ObservableProperty] private double? _sgxSize;                  // SGX 大小
        /// <summary>实验性。SGX 启动控制的访问模式配置，与虚拟机的普通引导顺序无关。</summary>
        /// <fielddoc>
        /// <meaning>SGX 启动控制的访问模式配置，与虚拟机的普通引导顺序无关。</meaning>
        /// <unit>模式编码。</unit>
        /// <values>取值和联动规则见 <see cref="ExHyperV.Services.VmMemoryFields.SgxLaunchControlMode"/>；该处为本字段的完整说明。</values>
        /// <source>VmMemoryService.GetVmMemorySettingsAsync 读取 Msvm_MemorySettingData.SgxLaunchControlMode。</source>
        /// <applicability>实验性读写能力；宿主和功能条件见 <see cref="ExHyperV.Services.VmMemoryFields.SgxLaunchControlMode"/>。没有额外的实验功能总开关。</applicability>
        /// <absence>读取未取得值时为 null，不能当作关闭或默认值；提交 null 时忽略该选择，不表示清空，也不触发该字段的联动；其他非空选择的必要联动仍按对应字段说明执行。</absence>
        /// <verification>已核对当前映射；未验证范围逐项列在 <see cref="ExHyperV.Services.VmMemoryFields.SgxLaunchControlMode"/>，不能把属性存在视为功能验证通过。</verification>
        /// <references><see cref="ExHyperV.Services.VmMemoryFields.SgxLaunchControlMode"/>；<see cref="ExHyperV.Services.VmMemoryService.SetVmMemorySettingsAsync"/>。</references>
        /// <modification kind="supported" input="newSettings.SgxLaunchControlMode" submission="明确选择字段">将 nameof(VmMemorySettings.SgxLaunchControlMode) 加入 changedProperties。具体规范化和必要联动见 <see cref="ExHyperV.Services.VmMemoryFields.SgxLaunchControlMode"/>。</modification>
        /// </fielddoc>
        [ObservableProperty] private uint? _sgxLaunchControlMode;      // SGX 启动模式
        /// <summary>实验性。来宾物理地址（GPA）固定配置的开关。Core 目前读取并提交对应属性，不提供指定 GPA 范围的独立操作。</summary>
        /// <fielddoc>
        /// <meaning>来宾物理地址（GPA）固定配置的开关。Core 目前读取并提交对应属性，不提供指定 GPA 范围的独立操作。</meaning>
        /// <values>取值和联动规则见 <see cref="ExHyperV.Services.VmMemoryFields.EnableGpaPinning"/>；该处为本字段的完整说明。</values>
        /// <source>VmMemoryService.GetVmMemorySettingsAsync 读取 Msvm_MemorySettingData.EnableGpaPinning。</source>
        /// <applicability>实验性读写能力；宿主和功能条件见 <see cref="ExHyperV.Services.VmMemoryFields.EnableGpaPinning"/>。没有额外的实验功能总开关。</applicability>
        /// <absence>读取未取得值时为 null，不能当作关闭或默认值；提交 null 时忽略该选择，不表示清空，也不触发该字段的联动；其他非空选择的必要联动仍按对应字段说明执行。</absence>
        /// <verification>已核对当前映射；未验证范围逐项列在 <see cref="ExHyperV.Services.VmMemoryFields.EnableGpaPinning"/>，不能把属性存在视为功能验证通过。</verification>
        /// <references><see cref="ExHyperV.Services.VmMemoryFields.EnableGpaPinning"/>；<see cref="ExHyperV.Services.VmMemoryService.SetVmMemorySettingsAsync"/>。</references>
        /// <modification kind="supported" input="newSettings.EnableGpaPinning" submission="明确选择字段">将 nameof(VmMemorySettings.EnableGpaPinning) 加入 changedProperties。具体规范化和必要联动见 <see cref="ExHyperV.Services.VmMemoryFields.EnableGpaPinning"/>。</modification>
        /// </fielddoc>
        [ObservableProperty] private bool? _enableGpaPinning;          // GPA 固定
        /// <summary>实验性 CXL 开关；已知部分 VMMS 版本在保存时可能崩溃。Core 不按研究风险拦截，由调用方提示，详见 VmMemoryFields.CxlEnabled。</summary>
        /// <fielddoc>
        /// <meaning>虚拟机内存配置中的 CXL 开关。此入口只修改 CxlEnabled，不枚举、分配或直通具体 CXL 设备。</meaning>
        /// <values>取值和联动规则见 <see cref="ExHyperV.Services.VmMemoryFields.CxlEnabled"/>；该处为本字段的完整说明。</values>
        /// <source>VmMemoryService.GetVmMemorySettingsAsync 读取 Msvm_MemorySettingData.CxlEnabled。</source>
        /// <applicability>保留显式写入，不以试写探测支持，也不按研究风险版本拦截。调用方应先展示 <see cref="ExHyperV.Services.VmMemoryFields.CxlEnabled"/> 中的服务崩溃风险、版本范围及关闭操作可能失败的限制。</applicability>
        /// <absence>读取未取得值时为 null，不能当作关闭或默认值；提交 null 时忽略该选择，不表示清空，也不触发该字段的联动；其他非空选择的必要联动仍按对应字段说明执行。</absence>
        /// <verification>已核对当前映射；未验证范围逐项列在 <see cref="ExHyperV.Services.VmMemoryFields.CxlEnabled"/>，不能把属性存在视为功能验证通过。</verification>
        /// <references><see cref="ExHyperV.Services.VmMemoryFields.CxlEnabled"/>；<see cref="ExHyperV.Services.VmMemoryService.SetVmMemorySettingsAsync"/>。</references>
        /// <modification kind="supported" input="newSettings.CxlEnabled" submission="明确选择字段">将 nameof(VmMemorySettings.CxlEnabled) 加入 changedProperties。具体规范化和必要联动见 <see cref="ExHyperV.Services.VmMemoryFields.CxlEnabled"/>。</modification>
        /// </fielddoc>
        [ObservableProperty] private bool? _cxlEnabled;                // CXL 支持
        /// <summary>实验性。控制内存配置中的冷提示开关。此入口不负责提交某段内存的冷热信息。</summary>
        /// <fielddoc>
        /// <meaning>控制内存配置中的冷提示开关。此入口不负责提交某段内存的冷热信息。</meaning>
        /// <values>取值和联动规则见 <see cref="ExHyperV.Services.VmMemoryFields.EnableColdHint"/>；该处为本字段的完整说明。</values>
        /// <source>VmMemoryService.GetVmMemorySettingsAsync 读取 Msvm_MemorySettingData.EnableColdHint。</source>
        /// <applicability>实验性读写能力；宿主和功能条件见 <see cref="ExHyperV.Services.VmMemoryFields.EnableColdHint"/>。没有额外的实验功能总开关。</applicability>
        /// <absence>读取未取得值时为 null，不能当作关闭或默认值；提交 null 时忽略该选择，不表示清空，也不触发该字段的联动；其他非空选择的必要联动仍按对应字段说明执行。</absence>
        /// <verification>已核对当前映射；未验证范围逐项列在 <see cref="ExHyperV.Services.VmMemoryFields.EnableColdHint"/>，不能把属性存在视为功能验证通过。</verification>
        /// <references><see cref="ExHyperV.Services.VmMemoryFields.EnableColdHint"/>；<see cref="ExHyperV.Services.VmMemoryService.SetVmMemorySettingsAsync"/>。</references>
        /// <modification kind="supported" input="newSettings.EnableColdHint" submission="明确选择字段">将 nameof(VmMemorySettings.EnableColdHint) 加入 changedProperties。具体规范化和必要联动见 <see cref="ExHyperV.Services.VmMemoryFields.EnableColdHint"/>。</modification>
        /// </fielddoc>
        [ObservableProperty] private bool? _enableColdHint;
        /// <summary>实验性。控制内存配置中的热提示开关。此入口不负责提交某段内存的冷热信息。</summary>
        /// <fielddoc>
        /// <meaning>控制内存配置中的热提示开关。此入口不负责提交某段内存的冷热信息。</meaning>
        /// <values>取值和联动规则见 <see cref="ExHyperV.Services.VmMemoryFields.EnableHotHint"/>；该处为本字段的完整说明。</values>
        /// <source>VmMemoryService.GetVmMemorySettingsAsync 读取 Msvm_MemorySettingData.EnableHotHint。</source>
        /// <applicability>实验性读写能力；宿主和功能条件见 <see cref="ExHyperV.Services.VmMemoryFields.EnableHotHint"/>。没有额外的实验功能总开关。</applicability>
        /// <absence>读取未取得值时为 null，不能当作关闭或默认值；提交 null 时忽略该选择，不表示清空，也不触发该字段的联动；其他非空选择的必要联动仍按对应字段说明执行。</absence>
        /// <verification>已核对当前映射；未验证范围逐项列在 <see cref="ExHyperV.Services.VmMemoryFields.EnableHotHint"/>，不能把属性存在视为功能验证通过。</verification>
        /// <references><see cref="ExHyperV.Services.VmMemoryFields.EnableHotHint"/>；<see cref="ExHyperV.Services.VmMemoryService.SetVmMemorySettingsAsync"/>。</references>
        /// <modification kind="supported" input="newSettings.EnableHotHint" submission="明确选择字段">将 nameof(VmMemorySettings.EnableHotHint) 加入 changedProperties。具体规范化和必要联动见 <see cref="ExHyperV.Services.VmMemoryFields.EnableHotHint"/>。</modification>
        /// </fielddoc>
        [ObservableProperty] private bool? _enableHotHint;
        /// <summary>实验性。保留 Hyper-V 的 EnableEpf 配置开关。缩写全称及功能用途尚未确认，因此不将它解释成某种已知内存优化。</summary>
        /// <fielddoc>
        /// <meaning>保留 Hyper-V 的 EnableEpf 配置开关。缩写全称及功能用途尚未确认，因此不将它解释成某种已知内存优化。</meaning>
        /// <values>取值和联动规则见 <see cref="ExHyperV.Services.VmMemoryFields.EnableEpf"/>；该处为本字段的完整说明。</values>
        /// <source>VmMemoryService.GetVmMemorySettingsAsync 读取 Msvm_MemorySettingData.EnableEpf。</source>
        /// <applicability>实验性读写能力；宿主和功能条件见 <see cref="ExHyperV.Services.VmMemoryFields.EnableEpf"/>。没有额外的实验功能总开关。</applicability>
        /// <absence>读取未取得值时为 null，不能当作关闭或默认值；提交 null 时忽略该选择，不表示清空，也不触发该字段的联动；其他非空选择的必要联动仍按对应字段说明执行。</absence>
        /// <verification>已核对当前映射；未验证范围逐项列在 <see cref="ExHyperV.Services.VmMemoryFields.EnableEpf"/>，不能把属性存在视为功能验证通过。</verification>
        /// <references><see cref="ExHyperV.Services.VmMemoryFields.EnableEpf"/>；<see cref="ExHyperV.Services.VmMemoryService.SetVmMemorySettingsAsync"/>。</references>
        /// <modification kind="supported" input="newSettings.EnableEpf" submission="明确选择字段">将 nameof(VmMemorySettings.EnableEpf) 加入 changedProperties。具体规范化和必要联动见 <see cref="ExHyperV.Services.VmMemoryFields.EnableEpf"/>。</modification>
        /// </fielddoc>
        [ObservableProperty] private bool? _enableEpf;
        /// <summary>实验性。内存配置中的专用压缩存储开关。当前入口仅传递配置，不返回压缩量或压缩率。</summary>
        /// <fielddoc>
        /// <meaning>内存配置中的专用压缩存储开关。当前入口仅传递配置，不返回压缩量或压缩率。</meaning>
        /// <values>取值和联动规则见 <see cref="ExHyperV.Services.VmMemoryFields.EnablePrivateCompressionStore"/>；该处为本字段的完整说明。</values>
        /// <source>VmMemoryService.GetVmMemorySettingsAsync 读取 Msvm_MemorySettingData.EnablePrivateCompressionStore。</source>
        /// <applicability>实验性读写能力；宿主和功能条件见 <see cref="ExHyperV.Services.VmMemoryFields.EnablePrivateCompressionStore"/>。没有额外的实验功能总开关。</applicability>
        /// <absence>读取未取得值时为 null，不能当作关闭或默认值；提交 null 时忽略该选择，不表示清空，也不触发该字段的联动；其他非空选择的必要联动仍按对应字段说明执行。</absence>
        /// <verification>已核对当前映射；未验证范围逐项列在 <see cref="ExHyperV.Services.VmMemoryFields.EnablePrivateCompressionStore"/>，不能把属性存在视为功能验证通过。</verification>
        /// <references><see cref="ExHyperV.Services.VmMemoryFields.EnablePrivateCompressionStore"/>；<see cref="ExHyperV.Services.VmMemoryService.SetVmMemorySettingsAsync"/>。</references>
        /// <modification kind="supported" input="newSettings.EnablePrivateCompressionStore" submission="明确选择字段">将 nameof(VmMemorySettings.EnablePrivateCompressionStore) 加入 changedProperties。具体规范化和必要联动见 <see cref="ExHyperV.Services.VmMemoryFields.EnablePrivateCompressionStore"/>。</modification>
        /// </fielddoc>
        [ObservableProperty] private bool? _enablePrivateCompressionStore;
        /// <summary>实验性。限制来宾可见的单个虚拟 NUMA 节点的内存容量。底层名称虽然包含 Blocks，调用时仍按内存容量理解。</summary>
        /// <fielddoc>
        /// <meaning>限制来宾可见的单个虚拟 NUMA 节点的内存容量。底层名称虽然包含 Blocks，调用时仍按内存容量理解。</meaning>
        /// <unit>MiB。表示容量，不是页面数，也不是 NUMA 节点数。</unit>
        /// <values>取值和联动规则见 <see cref="ExHyperV.Services.VmMemoryFields.MaxMemoryBlocksPerNumaNode"/>；该处为本字段的完整说明。</values>
        /// <source>VmMemoryService.GetVmMemorySettingsAsync 读取 Msvm_MemorySettingData.MaxMemoryBlocksPerNumaNode。</source>
        /// <applicability>实验性读写能力；宿主和功能条件见 <see cref="ExHyperV.Services.VmMemoryFields.MaxMemoryBlocksPerNumaNode"/>。没有额外的实验功能总开关。</applicability>
        /// <absence>读取未取得值时为 null，不能当作关闭或默认值；提交 null 时忽略该选择，不表示清空，也不触发该字段的联动；其他非空选择的必要联动仍按对应字段说明执行。</absence>
        /// <verification>已核对当前映射；未验证范围逐项列在 <see cref="ExHyperV.Services.VmMemoryFields.MaxMemoryBlocksPerNumaNode"/>，不能把属性存在视为功能验证通过。</verification>
        /// <references><see cref="ExHyperV.Services.VmMemoryFields.MaxMemoryBlocksPerNumaNode"/>；<see cref="ExHyperV.Services.VmMemoryService.SetVmMemorySettingsAsync"/>。</references>
        /// <modification kind="supported" input="newSettings.MaxMemoryBlocksPerNumaNode" submission="明确选择字段">将 nameof(VmMemorySettings.MaxMemoryBlocksPerNumaNode) 加入 changedProperties。具体规范化和必要联动见 <see cref="ExHyperV.Services.VmMemoryFields.MaxMemoryBlocksPerNumaNode"/>。</modification>
        /// </fielddoc>
        [ObservableProperty] private ulong? _maxMemoryBlocksPerNumaNode;
        /// <summary>实验性。SGX 默认启动控制配置的原始文本。当前 Core 不解析、生成或改写其中的内容。</summary>
        /// <fielddoc>
        /// <meaning>SGX 默认启动控制配置的原始文本。当前 Core 不解析、生成或改写其中的内容。</meaning>
        /// <unit>原始字符串。</unit>
        /// <values>取值和联动规则见 <see cref="ExHyperV.Services.VmMemoryFields.SgxLaunchControlDefault"/>；该处为本字段的完整说明。</values>
        /// <source>VmMemoryService.GetVmMemorySettingsAsync 读取 Msvm_MemorySettingData.SgxLaunchControlDefault。</source>
        /// <applicability>实验性读写能力；宿主和功能条件见 <see cref="ExHyperV.Services.VmMemoryFields.SgxLaunchControlDefault"/>。没有额外的实验功能总开关。</applicability>
        /// <absence>读取未取得值时为 null，不能当作关闭或默认值；提交 null 时忽略该选择，不表示清空，也不触发该字段的联动；其他非空选择的必要联动仍按对应字段说明执行。</absence>
        /// <verification>已核对当前映射；未验证范围逐项列在 <see cref="ExHyperV.Services.VmMemoryFields.SgxLaunchControlDefault"/>，不能把属性存在视为功能验证通过。</verification>
        /// <references><see cref="ExHyperV.Services.VmMemoryFields.SgxLaunchControlDefault"/>；<see cref="ExHyperV.Services.VmMemoryService.SetVmMemorySettingsAsync"/>。</references>
        /// <modification kind="supported" input="newSettings.SgxLaunchControlDefault" submission="明确选择字段">将 nameof(VmMemorySettings.SgxLaunchControlDefault) 加入 changedProperties。具体规范化和必要联动见 <see cref="ExHyperV.Services.VmMemoryFields.SgxLaunchControlDefault"/>。</modification>
        /// </fielddoc>
        [ObservableProperty] private string? _sgxLaunchControlDefault;

        /// <summary>实验性。请求虚拟机内存采用的加密策略。此字段表示配置，不能用它单独证明运行中的内存已经加密。</summary>
        /// <fielddoc>
        /// <meaning>请求虚拟机内存采用的加密策略。此字段表示配置，不能用它单独证明运行中的内存已经加密。</meaning>
        /// <unit>策略编码。</unit>
        /// <values>取值和联动规则见 <see cref="ExHyperV.Services.VmMemoryFields.MemoryEncryptionPolicy"/>；该处为本字段的完整说明。</values>
        /// <source>VmMemoryService.GetVmMemorySettingsAsync 读取 Msvm_MemorySettingData.MemoryEncryptionPolicy。</source>
        /// <applicability>实验性读写能力；宿主和功能条件见 <see cref="ExHyperV.Services.VmMemoryFields.MemoryEncryptionPolicy"/>。没有额外的实验功能总开关。</applicability>
        /// <absence>读取未取得值时为 null，不能当作关闭或默认值；提交 null 时忽略该选择，不表示清空，也不触发该字段的联动；其他非空选择的必要联动仍按对应字段说明执行。</absence>
        /// <verification>已核对当前映射；未验证范围逐项列在 <see cref="ExHyperV.Services.VmMemoryFields.MemoryEncryptionPolicy"/>，不能把属性存在视为功能验证通过。</verification>
        /// <references><see cref="ExHyperV.Services.VmMemoryFields.MemoryEncryptionPolicy"/>；<see cref="ExHyperV.Services.VmMemoryService.SetVmMemorySettingsAsync"/>。</references>
        /// <modification kind="supported" input="newSettings.MemoryEncryptionPolicy" submission="明确选择字段">将 nameof(VmMemorySettings.MemoryEncryptionPolicy) 加入 changedProperties。具体规范化和必要联动见 <see cref="ExHyperV.Services.VmMemoryFields.MemoryEncryptionPolicy"/>。</modification>
        /// </fielddoc>
        [ObservableProperty] private byte? _memoryEncryptionPolicy;

        // 开启 SGX 必须有 EPC 大小(≥2MB)，否则提交被 Hyper-V 拒（"SGX 内存无效，请分配至少 2 MB"）；无有效值时补合法默认。
        partial void OnSgxEnabledChanged(bool? value)
        {
            if (value == true && (SgxSize == null || SgxSize.Value < 2))
                SgxSize = 2;
        }

        // Copy snapshot fields directly: setters may normalize values, and MemberwiseClone
        // would carry the source object's notification subscribers into the new instance.
        /// <summary>复制当前值为独立模型，不复制属性变化事件订阅者。</summary>
        /// <returns>新的设置模型；内存页面选项也会复制。</returns>
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
            AvailablePageSizes = AvailablePageSizes.Select(item => new PageSizeItem
            {
                Description = item.Description,
                Value = item.Value
            }).ToList(),
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

        /// <summary>通过属性设置器恢复本地模型并触发相关通知；不提交 Hyper-V 配置。</summary>
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
