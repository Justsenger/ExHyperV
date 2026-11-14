using ExHyperV.Models;
using ExHyperV.Tools;

namespace ExHyperV.Services;

/// <summary>内存的独立可查询字段；可以任意选择其中一项，不要求整组读取或返回。</summary>
public static class VmMemoryFields
{
    /// <summary>启动内存：为虚拟机启动配置的内存大小，单位为 MiB。</summary>
    /// <fielddoc>
    /// <meaning>虚拟机启动时使用的内存容量。启用动态内存后，运行期间实际分配的内存可以变化。</meaning>
    /// <source>Msvm_MemorySettingData，读取或派生 VirtualQuantity。</source>
    /// <references><see cref="VmMemoryService"/>；<see href="https://learn.microsoft.com/en-us/windows/win32/hyperv_v2/msvm-memorysettingdata">微软内存字段说明</see>。</references>
    /// <modification kind="supported" input="newSettings.Startup" submission="明确选择字段">内存容量会随当前内存模式和后备页要求进行必要的对齐。<see cref="M:ExHyperV.Services.VmMemoryService.SetVmMemorySettingsAsync(System.Guid,ExHyperV.Models.VmMemorySettings,System.Collections.Generic.IReadOnlyCollection{System.String})"/></modification>
    /// <values kind="format">单位为 MiB；1 MiB = 1,048,576 字节。例如 <c>1024</c> 表示 1 GiB。</values>
    /// </fielddoc>
    public static readonly VmQueryField<long> Startup = new("Memory.Startup", "启动内存", "内存", VmQueryCollection.VirtualMachine, "Memory", "VirtualQuantity");

    /// <summary>动态内存开关：是否启用动态内存。启用后，Hyper-V 可以按需调整虚拟机的内存分配。</summary>
    /// <fielddoc>
    /// <meaning>是否启用动态内存。启用后，Hyper-V 可以按需调整虚拟机的内存分配。</meaning>
    /// <source>Msvm_MemorySettingData，读取或派生 DynamicMemoryEnabled。</source>
    /// <references><see cref="VmMemoryService"/>。</references>
    /// <modification kind="supported" input="newSettings.DynamicMemoryEnabled" submission="明确选择字段">动态内存与 vNUMA 互斥，切换时同步调整 vNUMA 配置。<see cref="M:ExHyperV.Services.VmMemoryService.SetVmMemorySettingsAsync(System.Guid,ExHyperV.Models.VmMemorySettings,System.Collections.Generic.IReadOnlyCollection{System.String})"/></modification>
    /// <values kind="options"><c>true</c>：启用动态内存；<c>false</c>：使用静态内存。</values>
    /// </fielddoc>
    public static readonly VmQueryField<bool> DynamicMemoryEnabled = new("Memory.DynamicMemoryEnabled", "动态内存开关", "内存", VmQueryCollection.VirtualMachine, "Memory", "DynamicMemoryEnabled");

    /// <summary>最小内存：动态内存允许分配的最低内存大小，单位为 MiB。</summary>
    /// <fielddoc>
    /// <meaning>动态内存允许分配的最低内存大小。</meaning>
    /// <applicability>动态内存启用时参与内存分配；关闭时仍可读取保存的配置。</applicability>
    /// <source>Msvm_MemorySettingData，读取或派生 Reservation。</source>
    /// <references><see cref="VmMemoryService"/>；<see href="https://learn.microsoft.com/en-us/windows/win32/hyperv_v2/msvm-memorysettingdata">微软内存字段说明</see>。</references>
    /// <modification kind="supported" input="newSettings.Minimum" submission="明确选择字段"><see cref="M:ExHyperV.Services.VmMemoryService.SetVmMemorySettingsAsync(System.Guid,ExHyperV.Models.VmMemorySettings,System.Collections.Generic.IReadOnlyCollection{System.String})"/></modification>
    /// <values kind="format">单位为 MiB；1 MiB = 1,048,576 字节。例如 <c>1024</c> 表示 1 GiB。</values>
    /// </fielddoc>
    public static readonly VmQueryField<long> Minimum = new("Memory.Minimum", "最小内存", "内存", VmQueryCollection.VirtualMachine, "Memory", "Reservation");

    /// <summary>最大内存：动态内存允许分配的最高内存大小，单位为 MiB。</summary>
    /// <fielddoc>
    /// <meaning>动态内存允许分配的最高内存大小。</meaning>
    /// <applicability>动态内存启用时作为内存分配上限。</applicability>
    /// <source>Msvm_MemorySettingData，读取或派生 Limit。</source>
    /// <references><see cref="VmMemoryService"/>；<see href="https://learn.microsoft.com/en-us/windows/win32/hyperv_v2/msvm-memorysettingdata">微软内存字段说明</see>。</references>
    /// <modification kind="supported" input="newSettings.Maximum" submission="明确选择字段"><see cref="M:ExHyperV.Services.VmMemoryService.SetVmMemorySettingsAsync(System.Guid,ExHyperV.Models.VmMemorySettings,System.Collections.Generic.IReadOnlyCollection{System.String})"/></modification>
    /// <values kind="format">单位为 MiB；1 MiB = 1,048,576 字节。例如 <c>1024</c> 表示 1 GiB。</values>
    /// </fielddoc>
    public static readonly VmQueryField<long> Maximum = new("Memory.Maximum", "最大内存", "内存", VmQueryCollection.VirtualMachine, "Memory", "Limit");

    /// <summary>内存缓冲比例：动态内存的目标额外缓冲量，占来宾内存需求的百分比。</summary>
    /// <fielddoc>
    /// <meaning>动态内存的目标额外缓冲量，占来宾内存需求的百分比。</meaning>
    /// <unit>%</unit>
    /// <values>例如 20 表示以额外提供来宾需求量的 20% 作为缓冲目标。</values>
    /// <applicability>适用于动态内存。</applicability>
    /// <source>Msvm_MemorySettingData，读取或派生 TargetMemoryBuffer。</source>
    /// <references><see cref="VmMemoryService"/>；<see href="https://learn.microsoft.com/en-us/windows/win32/hyperv_v2/msvm-memorysettingdata">微软内存字段说明</see>。</references>
    /// <modification kind="supported" input="newSettings.Buffer" submission="明确选择字段"><see cref="M:ExHyperV.Services.VmMemoryService.SetVmMemorySettingsAsync(System.Guid,ExHyperV.Models.VmMemorySettings,System.Collections.Generic.IReadOnlyCollection{System.String})"/></modification>
    /// </fielddoc>
    public static readonly VmQueryField<int> Buffer = new("Memory.Buffer", "内存缓冲比例", "内存", VmQueryCollection.VirtualMachine, "Memory", "TargetMemoryBuffer");

    /// <summary>内存权重：宿主可用内存不足、无法同时满足所有虚拟机的需求时，决定这台虚拟机获得内存的相对优先级。数值越大优先级越高。</summary>
    /// <fielddoc>
    /// <meaning>宿主可用内存不足、无法同时满足所有虚拟机的需求时，决定这台虚拟机获得内存的相对优先级。数值越大优先级越高。</meaning>
    /// <source>Msvm_MemorySettingData，读取或派生 Weight。</source>
    /// <references><see cref="VmMemoryService"/>；<see href="https://learn.microsoft.com/en-us/windows/win32/hyperv_v2/msvm-memorysettingdata">微软内存字段说明</see>。</references>
    /// <modification kind="supported" input="newSettings.Weight" submission="明确选择字段"><see cref="M:ExHyperV.Services.VmMemoryService.SetVmMemorySettingsAsync(System.Guid,ExHyperV.Models.VmMemorySettings,System.Collections.Generic.IReadOnlyCollection{System.String})"/></modification>
    /// <values kind="format">无符号整数权重；数值越大，内存竞争时的相对优先级越高。</values>
    /// </fielddoc>
    public static readonly VmQueryField<uint> Weight = new("Memory.Weight", "内存权重", "内存", VmQueryCollection.VirtualMachine, "Memory", "Weight", o => Convert.ToUInt32(o));

    /// <summary>后备页大小：承载虚拟机内存所使用的页面类型：标准页、大页或巨页。</summary>
    /// <fielddoc>
    /// <meaning>承载虚拟机内存所使用的页面类型：标准页、大页或巨页。</meaning>
    /// <values kind="options">0：4 KiB 标准页；1：2 MiB 大页；2：1 GiB 巨页。</values>
    /// <source>优先读取 Msvm_MemorySettingData.BackingPageSize；仅提供 HugePagesEnabled 时，false 映射为 1，true 映射为 2。</source>
    /// <applicability>只有 HugePagesEnabled 的宿主支持 2 MiB 或 1 GiB 页；请求 4 KiB 时返回 Unsupported。修改高级页策略前关闭虚拟机并退出工作进程。</applicability>
    /// <references><see cref="VmMemoryService"/>。</references>
    /// <modification kind="supported" input="newSettings.BackingPageSize" submission="明确选择字段">巨页要求内存容量按 1024 MiB 对齐；后备类型已知时联动为物理后备，并关闭已知开启的冷热提示、EPF 和专用压缩存储。每个 NUMA 节点的容量上限也会对齐。<see cref="M:ExHyperV.Services.VmMemoryService.SetVmMemorySettingsAsync(System.Guid,ExHyperV.Models.VmMemorySettings,System.Collections.Generic.IReadOnlyCollection{System.String})"/></modification>
    /// </fielddoc>
    public static readonly VmQueryField<byte> BackingPageSize = new("Memory.BackingPageSize", "后备页大小", "内存", VmQueryCollection.VirtualMachine, "Memory", "BackingPageSize");

    /// <summary>内存后备类型：选择承载虚拟机内存的后备方式。此项与后备页大小、冷热提示及压缩存储有联动。（实验性）</summary>
    /// <fielddoc>
    /// <meaning>选择承载虚拟机内存的后备方式。此项与后备页大小、冷热提示及压缩存储有联动。</meaning>
    /// <unit>类型编码。</unit>
    /// <values kind="options">0：物理映射；1：虚拟映射；2：混合映射。Core 保留原始编码，不为未识别值赋予含义。</values>
    /// <source>Msvm_MemorySettingData.BackingType；自由查询直接读取该属性。</source>
    /// <applicability>实验性。混合映射需要逐 NUMA 节点配置。修改前退出活动状态。</applicability>
    /// <references><see cref="M:ExHyperV.Services.VmMemoryService.SetVmMemorySettingsAsync(System.Guid,ExHyperV.Models.VmMemorySettings,System.Collections.Generic.IReadOnlyCollection{System.String})"/>；<see cref="VmMemorySettings.BackingType"/>。</references>
    /// <modification kind="supported" input="newSettings.BackingType" submission="明确选择字段">物理映射与冷热提示、EPF、专用压缩存储互斥。单独切换到非物理映射时，原有巨页配置改为大页；同时选择后备类型与巨页时，巨页优先，并采用物理映射。<see cref="M:ExHyperV.Services.VmMemoryService.SetVmMemorySettingsAsync(System.Guid,ExHyperV.Models.VmMemorySettings,System.Collections.Generic.IReadOnlyCollection{System.String})"/></modification>
    /// </fielddoc>
    public static readonly VmQueryField<byte> BackingType = new("Memory.BackingType", "内存后备类型", "内存", VmQueryCollection.VirtualMachine, "Memory", "BackingType");

    /// <summary>动态内存操作对齐：动态内存调整所使用的对齐配置；与决定内存后备页面类型的 BackingPageSize 是不同字段。（实验性）</summary>
    /// <fielddoc>
    /// <meaning>动态内存调整所使用的对齐配置；与决定内存后备页面类型的 BackingPageSize 是不同字段。</meaning>
    /// <unit>对齐选项编码。</unit>
    /// <values kind="options">0：禁用；1：4 KB；2：2 MB；3：1 GB。这些是项目已有选项的标签，Core 直接传递编码，不进行字节换算。</values>
    /// <source>Msvm_MemorySettingData.DynMemOperationAlignment；自由查询直接读取该属性。</source>
    /// <applicability>实验性动态内存配置。修改前退出活动状态。 相关机制暂不明确。</applicability>
    /// <references><see cref="M:ExHyperV.Services.VmMemoryService.SetVmMemorySettingsAsync(System.Guid,ExHyperV.Models.VmMemorySettings,System.Collections.Generic.IReadOnlyCollection{System.String})"/>；<see cref="VmMemorySettings.DynMemOperationAlignment"/>。</references>
    /// <modification kind="supported" input="newSettings.DynMemOperationAlignment" submission="明确选择字段"><see cref="M:ExHyperV.Services.VmMemoryService.SetVmMemorySettingsAsync(System.Guid,ExHyperV.Models.VmMemorySettings,System.Collections.Generic.IReadOnlyCollection{System.String})"/></modification>
    /// </fielddoc>
    public static readonly VmQueryField<uint> DynMemOperationAlignment = new("Memory.DynMemOperationAlignment", "动态内存操作对齐", "内存", VmQueryCollection.VirtualMachine, "Memory", "DynMemOperationAlignment");

    /// <summary>内存访问跟踪策略：配置内存访问跟踪的粒度策略。跟踪是否开启由 MemoryAccessTrackingState 单独配置。（实验性）</summary>
    /// <fielddoc>
    /// <meaning>配置内存访问跟踪的粒度策略。跟踪是否开启由 MemoryAccessTrackingState 单独配置。</meaning>
    /// <unit>跟踪策略编码。</unit>
    /// <values kind="options">0：最佳；1：4 KB；2：2 MB；3：1 GB。</values>
    /// <source>Msvm_MemorySettingData.MemoryAccessTrackingPolicy；自由查询直接读取该属性。</source>
    /// <applicability>实验性。配合 MemoryAccessTrackingState 使用，修改前退出活动状态。 整体内存访问跟踪按引用计数管理，底层在引用数变化满足启停条件时执行相应操作。</applicability>
    /// <references><see cref="M:ExHyperV.Services.VmMemoryService.SetVmMemorySettingsAsync(System.Guid,ExHyperV.Models.VmMemorySettings,System.Collections.Generic.IReadOnlyCollection{System.String})"/>；<see cref="VmMemorySettings.MemoryAccessTrackingPolicy"/>。</references>
    /// <modification kind="supported" input="newSettings.MemoryAccessTrackingPolicy" submission="明确选择字段"><see cref="M:ExHyperV.Services.VmMemoryService.SetVmMemorySettingsAsync(System.Guid,ExHyperV.Models.VmMemorySettings,System.Collections.Generic.IReadOnlyCollection{System.String})"/></modification>
    /// </fielddoc>
    public static readonly VmQueryField<byte> MemoryAccessTrackingPolicy = new("Memory.MemoryAccessTrackingPolicy", "内存访问跟踪策略", "内存", VmQueryCollection.VirtualMachine, "Memory", "MemoryAccessTrackingPolicy");

    /// <summary>内存访问跟踪状态：配置是否启用内存访问跟踪，或交给逐 NUMA 节点配置决定。（实验性）</summary>
    /// <fielddoc>
    /// <meaning>配置是否启用内存访问跟踪，或交给逐 NUMA 节点配置决定。</meaning>
    /// <values kind="options">0：关闭；1：开启；2：按 NUMA 节点配置。</values>
    /// <source>Msvm_MemorySettingData.MemoryAccessTrackingState；自由查询直接读取该属性。</source>
    /// <applicability>实验性。编码 2 依赖逐 NUMA 节点配置。修改前退出活动状态。 此字段配置整机跟踪状态。关闭流程等待底层任务结束并检查状态；失败时通过返回结果确认已完成的步骤。</applicability>
    /// <references><see cref="M:ExHyperV.Services.VmMemoryService.SetVmMemorySettingsAsync(System.Guid,ExHyperV.Models.VmMemorySettings,System.Collections.Generic.IReadOnlyCollection{System.String})"/>；<see cref="VmMemorySettings.MemoryAccessTrackingState"/>。</references>
    /// <modification kind="supported" input="newSettings.MemoryAccessTrackingState" submission="明确选择字段"><see cref="M:ExHyperV.Services.VmMemoryService.SetVmMemorySettingsAsync(System.Guid,ExHyperV.Models.VmMemorySettings,System.Collections.Generic.IReadOnlyCollection{System.String})"/></modification>
    /// </fielddoc>
    public static readonly VmQueryField<byte> MemoryAccessTrackingState = new("Memory.MemoryAccessTrackingState", "内存访问跟踪状态", "内存", VmQueryCollection.VirtualMachine, "Memory", "MemoryAccessTrackingState");

    /// <summary>SGX 开关：请求为虚拟机启用 SGX 支持。SGX 内存容量由 SgxSize 配置。（实验性）</summary>
    /// <fielddoc>
    /// <meaning>请求为虚拟机启用 SGX 支持。SGX 内存容量由 SgxSize 配置。</meaning>
    /// <values kind="options">true：请求启用；false：请求关闭。启用时，合并后的配置容量为空或不足 2 MiB 时按 2 MiB 配置；已有更大容量继续保留。</values>
    /// <source>Msvm_MemorySettingData.SgxEnabled；自由查询直接读取该属性。</source>
    /// <applicability>实验性。Windows 10 1703 起提供，需宿主和来宾支持 SGX。修改前退出活动状态。</applicability>
    /// <references><see cref="M:ExHyperV.Services.VmMemoryService.SetVmMemorySettingsAsync(System.Guid,ExHyperV.Models.VmMemorySettings,System.Collections.Generic.IReadOnlyCollection{System.String})"/>；<see cref="VmMemorySettings.SgxEnabled"/>。<see href="https://learn.microsoft.com/en-us/windows/win32/hyperv_v2/msvm-memorysettingdata">微软内存配置说明</see>。</references>
    /// <modification kind="supported" input="newSettings.SgxEnabled" submission="明确选择字段">启用时同步设置 SGX 容量，最小 2 MiB，并按 2 MiB 向下对齐。关闭时保留原容量。<see cref="M:ExHyperV.Services.VmMemoryService.SetVmMemorySettingsAsync(System.Guid,ExHyperV.Models.VmMemorySettings,System.Collections.Generic.IReadOnlyCollection{System.String})"/></modification>
    /// </fielddoc>
    public static readonly VmQueryField<bool> SgxEnabled = new("Memory.SgxEnabled", "SGX 开关", "内存", VmQueryCollection.VirtualMachine, "Memory", "SgxEnabled");

    /// <summary>SGX 内存大小：为虚拟机配置的 SGX EPC 内存容量，与普通启动内存分开。（实验性）</summary>
    /// <fielddoc>
    /// <meaning>为虚拟机配置的 SGX EPC 内存容量，与普通启动内存分开。</meaning>
    /// <unit>MiB；写入时使用整数容量。</unit>
    /// <values>最小 2 MiB，按 2 MiB 向下对齐。例如 5 MiB 最终写为 4 MiB。需要精确请求时传入不小于 2 的偶数整数。</values>
    /// <source>Msvm_MemorySettingData.SgxSize；自由查询直接读取该属性。</source>
    /// <applicability>实验性。Windows 10 1703 起提供；SGX 开启时写入容量。修改前退出活动状态。 未取得容量时返回缺测状态；设置模型使用 null 表示缺值，0 表示已读到的容量值。</applicability>
    /// <references><see cref="M:ExHyperV.Services.VmMemoryService.SetVmMemorySettingsAsync(System.Guid,ExHyperV.Models.VmMemorySettings,System.Collections.Generic.IReadOnlyCollection{System.String})"/>；<see cref="VmMemorySettings.SgxSize"/>。<see href="https://learn.microsoft.com/en-us/windows/win32/hyperv_v2/msvm-memorysettingdata">微软内存配置说明</see>。</references>
    /// <modification kind="supported" input="newSettings.SgxSize" submission="明确选择字段">仅 SGX 启用时更新容量；容量按 2 MiB 向下对齐，最小 2 MiB。<see cref="M:ExHyperV.Services.VmMemoryService.SetVmMemorySettingsAsync(System.Guid,ExHyperV.Models.VmMemorySettings,System.Collections.Generic.IReadOnlyCollection{System.String})"/></modification>
    /// </fielddoc>
    public static readonly VmQueryField<double> SgxSize = new("Memory.SgxSize", "SGX 内存大小", "内存", VmQueryCollection.VirtualMachine, "Memory", "SgxSize");

    /// <summary>SGX 启动控制模式：SGX 启动控制的访问模式配置，与虚拟机的普通引导顺序无关。（实验性）</summary>
    /// <fielddoc>
    /// <meaning>SGX 启动控制的访问模式配置，与虚拟机的普通引导顺序无关。</meaning>
    /// <values kind="options">0：禁止访问；1：仅限读取；2：允许读写。</values>
    /// <source>Msvm_MemorySettingData.SgxLaunchControlMode；自由查询直接读取该属性。</source>
    /// <applicability>实验性 SGX 配置。修改前退出活动状态。 相关机制暂不明确。</applicability>
    /// <references><see cref="M:ExHyperV.Services.VmMemoryService.SetVmMemorySettingsAsync(System.Guid,ExHyperV.Models.VmMemorySettings,System.Collections.Generic.IReadOnlyCollection{System.String})"/>；<see cref="VmMemorySettings.SgxLaunchControlMode"/>。</references>
    /// <modification kind="supported" input="newSettings.SgxLaunchControlMode" submission="明确选择字段"><see cref="M:ExHyperV.Services.VmMemoryService.SetVmMemorySettingsAsync(System.Guid,ExHyperV.Models.VmMemorySettings,System.Collections.Generic.IReadOnlyCollection{System.String})"/></modification>
    /// </fielddoc>
    public static readonly VmQueryField<uint> SgxLaunchControlMode = new("Memory.SgxLaunchControlMode", "SGX 启动控制模式", "内存", VmQueryCollection.VirtualMachine, "Memory", "SgxLaunchControlMode");

    /// <summary>GPA 固定开关：来宾物理地址（GPA）固定配置的开关。（实验性）</summary>
    /// <fielddoc>
    /// <meaning>来宾物理地址（GPA）固定配置的开关。</meaning>
    /// <values kind="options">true：开启；false：关闭。</values>
    /// <source>Msvm_MemorySettingData.EnableGpaPinning；自由查询直接读取该属性。</source>
    /// <applicability>实验性。修改前退出活动状态。 相关机制暂不明确。</applicability>
    /// <references><see cref="M:ExHyperV.Services.VmMemoryService.SetVmMemorySettingsAsync(System.Guid,ExHyperV.Models.VmMemorySettings,System.Collections.Generic.IReadOnlyCollection{System.String})"/>；<see cref="VmMemorySettings.EnableGpaPinning"/>。</references>
    /// <modification kind="supported" input="newSettings.EnableGpaPinning" submission="明确选择字段"><see cref="M:ExHyperV.Services.VmMemoryService.SetVmMemorySettingsAsync(System.Guid,ExHyperV.Models.VmMemorySettings,System.Collections.Generic.IReadOnlyCollection{System.String})"/></modification>
    /// </fielddoc>
    public static readonly VmQueryField<bool> EnableGpaPinning = new("Memory.EnableGpaPinning", "GPA 固定开关", "内存", VmQueryCollection.VirtualMachine, "Memory", "EnableGpaPinning");

    /// <summary>CXL 开关：实验性配置，已知部分 VMMS 版本在写入时可能崩溃；Core 保留写入入口，由调用方提示风险。</summary>
    /// <fielddoc>
    /// <meaning>虚拟机内存配置中的 CXL 开关。此入口只修改 CxlEnabled，不枚举、分配或直通具体 CXL 设备。</meaning>
    /// <values kind="options">true：开启；false：关闭。</values>
    /// <source>Msvm_MemorySettingData.CxlEnabled；自由查询直接读取该属性。</source>
    /// <applicability>存在缺陷，开启会导致vmms崩溃。</applicability>
    /// <references><see cref="M:ExHyperV.Services.VmMemoryService.SetVmMemorySettingsAsync(System.Guid,ExHyperV.Models.VmMemorySettings,System.Collections.Generic.IReadOnlyCollection{System.String})"/>；<see cref="VmMemorySettings.CxlEnabled"/>。</references>
    /// <modification kind="supported" input="newSettings.CxlEnabled" submission="明确选择字段"><see cref="M:ExHyperV.Services.VmMemoryService.SetVmMemorySettingsAsync(System.Guid,ExHyperV.Models.VmMemorySettings,System.Collections.Generic.IReadOnlyCollection{System.String})"/></modification>
    /// </fielddoc>
    public static readonly VmQueryField<bool> CxlEnabled = new("Memory.CxlEnabled", "CXL 开关", "内存", VmQueryCollection.VirtualMachine, "Memory", "CxlEnabled");

    /// <summary>冷内存提示：控制内存配置中的冷提示开关。（实验性）</summary>
    /// <fielddoc>
    /// <meaning>控制内存配置中的冷提示开关。</meaning>
    /// <values kind="options">true：请求开启；false：请求关闭。现有物理后备规则会将 true 规范化为 false。</values>
    /// <source>Msvm_MemorySettingData.EnableColdHint；自由查询直接读取该属性。</source>
    /// <applicability>实验性。适用于非物理后备配置，修改前退出活动状态。</applicability>
    /// <references><see cref="M:ExHyperV.Services.VmMemoryService.SetVmMemorySettingsAsync(System.Guid,ExHyperV.Models.VmMemorySettings,System.Collections.Generic.IReadOnlyCollection{System.String})"/>；<see cref="VmMemorySettings.EnableColdHint"/>。</references>
    /// <modification kind="supported" input="newSettings.EnableColdHint" submission="明确选择字段">物理后备配置下保持关闭。<see cref="M:ExHyperV.Services.VmMemoryService.SetVmMemorySettingsAsync(System.Guid,ExHyperV.Models.VmMemorySettings,System.Collections.Generic.IReadOnlyCollection{System.String})"/></modification>
    /// </fielddoc>
    public static readonly VmQueryField<bool> EnableColdHint = new("Memory.EnableColdHint", "冷内存提示", "内存", VmQueryCollection.VirtualMachine, "Memory", "EnableColdHint");

    /// <summary>热内存提示：控制内存配置中的热提示开关。（实验性）</summary>
    /// <fielddoc>
    /// <meaning>控制内存配置中的热提示开关。</meaning>
    /// <values kind="options">true：请求开启；false：请求关闭。现有物理后备规则会将 true 规范化为 false。</values>
    /// <source>Msvm_MemorySettingData.EnableHotHint；自由查询直接读取该属性。</source>
    /// <applicability>实验性。适用于非物理后备配置，修改前退出活动状态。</applicability>
    /// <references><see cref="M:ExHyperV.Services.VmMemoryService.SetVmMemorySettingsAsync(System.Guid,ExHyperV.Models.VmMemorySettings,System.Collections.Generic.IReadOnlyCollection{System.String})"/>；<see cref="VmMemorySettings.EnableHotHint"/>。</references>
    /// <modification kind="supported" input="newSettings.EnableHotHint" submission="明确选择字段">物理后备配置下保持关闭。<see cref="M:ExHyperV.Services.VmMemoryService.SetVmMemorySettingsAsync(System.Guid,ExHyperV.Models.VmMemorySettings,System.Collections.Generic.IReadOnlyCollection{System.String})"/></modification>
    /// </fielddoc>
    public static readonly VmQueryField<bool> EnableHotHint = new("Memory.EnableHotHint", "热内存提示", "内存", VmQueryCollection.VirtualMachine, "Memory", "EnableHotHint");

    /// <summary>EPF 开关：保留 Hyper-V 的 EnableEpf 配置开关。相关机制暂不明确。（实验性）</summary>
    /// <fielddoc>
    /// <meaning>保留 Hyper-V 的 EnableEpf 配置开关。相关机制暂不明确。</meaning>
    /// <values kind="options">true、false。相关机制暂不明确。</values>
    /// <source>Msvm_MemorySettingData.EnableEpf；自由查询直接读取该属性。</source>
    /// <applicability>实验性。适用于非物理后备配置，修改前退出活动状态。</applicability>
    /// <references><see cref="M:ExHyperV.Services.VmMemoryService.SetVmMemorySettingsAsync(System.Guid,ExHyperV.Models.VmMemorySettings,System.Collections.Generic.IReadOnlyCollection{System.String})"/>；<see cref="VmMemorySettings.EnableEpf"/>。</references>
    /// <modification kind="supported" input="newSettings.EnableEpf" submission="明确选择字段">物理后备配置下保持关闭。<see cref="M:ExHyperV.Services.VmMemoryService.SetVmMemorySettingsAsync(System.Guid,ExHyperV.Models.VmMemorySettings,System.Collections.Generic.IReadOnlyCollection{System.String})"/></modification>
    /// </fielddoc>
    public static readonly VmQueryField<bool> EnableEpf = new("Memory.EnableEpf", "EPF 开关", "内存", VmQueryCollection.VirtualMachine, "Memory", "EnableEpf");

    /// <summary>专用压缩存储：内存配置中的专用压缩存储开关。（实验性）</summary>
    /// <fielddoc>
    /// <meaning>内存配置中的专用压缩存储开关。</meaning>
    /// <values kind="options">true：请求开启；false：请求关闭。现有物理后备规则会将 true 规范化为 false。</values>
    /// <source>Msvm_MemorySettingData.EnablePrivateCompressionStore；自由查询直接读取该属性。</source>
    /// <applicability>实验性。适用于非物理后备配置，修改前退出活动状态。</applicability>
    /// <references><see cref="M:ExHyperV.Services.VmMemoryService.SetVmMemorySettingsAsync(System.Guid,ExHyperV.Models.VmMemorySettings,System.Collections.Generic.IReadOnlyCollection{System.String})"/>；<see cref="VmMemorySettings.EnablePrivateCompressionStore"/>。</references>
    /// <modification kind="supported" input="newSettings.EnablePrivateCompressionStore" submission="明确选择字段">物理后备配置下保持关闭。<see cref="M:ExHyperV.Services.VmMemoryService.SetVmMemorySettingsAsync(System.Guid,ExHyperV.Models.VmMemorySettings,System.Collections.Generic.IReadOnlyCollection{System.String})"/></modification>
    /// </fielddoc>
    public static readonly VmQueryField<bool> EnablePrivateCompressionStore = new("Memory.EnablePrivateCompressionStore", "私有压缩存储开关", "内存", VmQueryCollection.VirtualMachine, "Memory", "EnablePrivateCompressionStore");

    /// <summary>每 NUMA 节点内存块上限：限制来宾可见的单个虚拟 NUMA 节点的内存容量。底层名称虽然包含 Blocks，调用时仍按内存容量理解。（实验性）</summary>
    /// <fielddoc>
    /// <meaning>限制来宾可见的单个虚拟 NUMA 节点的内存容量。底层名称虽然包含 Blocks，调用时仍按内存容量理解。</meaning>
    /// <unit>MiB。</unit>
    /// <values>Core 按当前页设置向下对齐：巨页为 1024 MiB，其余为 2 MiB；结果至少达到按同一粒度向上对齐的 32 MiB。因此巨页模式的最小提交值为 1024 MiB。</values>
    /// <source>Msvm_MemorySettingData.MaxMemoryBlocksPerNumaNode；自由查询直接读取该属性。</source>
    /// <applicability>实验性虚拟 NUMA 配置。修改前退出活动状态。 相关机制暂不明确。</applicability>
    /// <references><see cref="M:ExHyperV.Services.VmMemoryService.SetVmMemorySettingsAsync(System.Guid,ExHyperV.Models.VmMemorySettings,System.Collections.Generic.IReadOnlyCollection{System.String})"/>；<see cref="VmMemorySettings.MaxMemoryBlocksPerNumaNode"/>。<see href="https://learn.microsoft.com/en-us/windows/win32/hyperv_v2/msvm-memorysettingdata">微软内存配置说明</see>。</references>
    /// <modification kind="supported" input="newSettings.MaxMemoryBlocksPerNumaNode" submission="明确选择字段">切换巨页时，此容量上限也会联动对齐。<see cref="M:ExHyperV.Services.VmMemoryService.SetVmMemorySettingsAsync(System.Guid,ExHyperV.Models.VmMemorySettings,System.Collections.Generic.IReadOnlyCollection{System.String})"/></modification>
    /// </fielddoc>
    public static readonly VmQueryField<ulong> MaxMemoryBlocksPerNumaNode = new("Memory.MaxMemoryBlocksPerNumaNode", "每 NUMA 节点内存块上限", "内存", VmQueryCollection.VirtualMachine, "Memory", "MaxMemoryBlocksPerNumaNode");

    /// <summary>SGX 默认启动控制：SGX 默认启动控制配置的原始文本。（实验性）</summary>
    /// <fielddoc>
    /// <meaning>SGX 默认启动控制配置的原始文本。</meaning>
    /// <values>宿主保存的原始文本。相关机制暂不明确。</values>
    /// <source>Msvm_MemorySettingData.SgxLaunchControlDefault；自由查询直接读取该属性。</source>
    /// <applicability>实验性 SGX 配置。修改前退出活动状态。 相关机制暂不明确。</applicability>
    /// <references><see cref="M:ExHyperV.Services.VmMemoryService.SetVmMemorySettingsAsync(System.Guid,ExHyperV.Models.VmMemorySettings,System.Collections.Generic.IReadOnlyCollection{System.String})"/>；<see cref="VmMemorySettings.SgxLaunchControlDefault"/>。</references>
    /// <modification kind="supported" input="newSettings.SgxLaunchControlDefault" submission="明确选择字段"><see cref="M:ExHyperV.Services.VmMemoryService.SetVmMemorySettingsAsync(System.Guid,ExHyperV.Models.VmMemorySettings,System.Collections.Generic.IReadOnlyCollection{System.String})"/></modification>
    /// </fielddoc>
    public static readonly VmQueryField<string> SgxLaunchControlDefault = new("Memory.SgxLaunchControlDefault", "SGX 默认启动控制", "内存", VmQueryCollection.VirtualMachine, "Memory", "SgxLaunchControlDefault");

    /// <summary>内存加密策略：虚拟机内存的加密策略。（实验性）</summary>
    /// <fielddoc>
    /// <meaning>虚拟机内存的加密策略。</meaning>
    /// <values kind="options">0：禁用；1：支持时启用；2：始终启用。</values>
    /// <source>Msvm_MemorySettingData.MemoryEncryptionPolicy；自由查询直接读取该属性。</source>
    /// <applicability>实验性。修改前退出活动状态。</applicability>
    /// <references><see cref="M:ExHyperV.Services.VmMemoryService.SetVmMemorySettingsAsync(System.Guid,ExHyperV.Models.VmMemorySettings,System.Collections.Generic.IReadOnlyCollection{System.String})"/>；<see cref="VmMemorySettings.MemoryEncryptionPolicy"/>。</references>
    /// <modification kind="supported" input="newSettings.MemoryEncryptionPolicy" submission="明确选择字段"><see cref="M:ExHyperV.Services.VmMemoryService.SetVmMemorySettingsAsync(System.Guid,ExHyperV.Models.VmMemorySettings,System.Collections.Generic.IReadOnlyCollection{System.String})"/></modification>
    /// </fielddoc>
    public static readonly VmQueryField<byte> MemoryEncryptionPolicy = new("Memory.MemoryEncryptionPolicy", "内存加密策略", "内存", VmQueryCollection.VirtualMachine, "Memory", "MemoryEncryptionPolicy");

    /// <summary>运行时已分配内存：Hyper-V 当前分配给运行中虚拟机的内存大小，单位为 MiB。</summary>
    /// <fielddoc>
    /// <meaning>Hyper-V 当前分配给运行中虚拟机的内存大小。</meaning>
    /// <source>摘要运行状态、MemoryUsage、MemoryAvailable 与动态内存配置，读取或派生 AssignedMiB。</source>
    /// <references><see cref="VmMemoryService"/>。</references>
    /// <modification kind="readonly" />
    /// <values kind="format">单位为 MiB；1 MiB = 1,048,576 字节。例如 <c>1024</c> 表示 1 GiB。</values>
    /// </fielddoc>
    public static readonly VmQueryField<long> AssignedMiB = new("Memory.AssignedMiB", "运行时已分配内存", "内存", VmQueryCollection.VirtualMachine, "RuntimeMemory", "AssignedMiB");

    /// <summary>来宾可用内存比例：按来宾内存需求换算的可用内存比例，单位为 %。</summary>
    /// <fielddoc>
    /// <meaning>当前分配内存相对来宾内存需求的剩余比例，单位为 %。</meaning>
    /// <unit>%</unit>
    /// <values>可为负数，表示内存需求超过当前分配量。</values>
    /// <applicability>适用于运行中且能取得来宾内存需求的虚拟机；同名、缺测或读取失败时返回相应字段状态。</applicability>
    /// <source>动态内存使用摘要 MemoryAvailable。固定内存使用 Hyper-V Dynamic Memory VM 的 Current Pressure × Guest Visible Physical Memory / 100 推算需求，再以摘要 MemoryUsage 换算可用比例。</source>
    /// <references><see cref="VmMemoryService"/>；<see cref="VmQueryService.GetVmRuntimeMemoryDataAsync"/>。</references>
    /// <modification kind="readonly" />
    /// </fielddoc>
    public static readonly VmQueryField<int> AvailablePercent = new("Memory.AvailablePercent", "来宾可用内存比例", "内存", VmQueryCollection.VirtualMachine, "RuntimeMemory", "AvailablePercent");

    internal static readonly VmQueryField[] Items = [Startup, DynamicMemoryEnabled, Minimum, Maximum, Buffer, Weight, BackingPageSize, BackingType, DynMemOperationAlignment, MemoryAccessTrackingPolicy, MemoryAccessTrackingState, SgxEnabled, SgxSize, SgxLaunchControlMode, EnableGpaPinning, CxlEnabled, EnableColdHint, EnableHotHint, EnableEpf, EnablePrivateCompressionStore, MaxMemoryBlocksPerNumaNode, SgxLaunchControlDefault, MemoryEncryptionPolicy, AssignedMiB, AvailablePercent];
}
