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
    /// <modification kind="supported" input="newSettings.Startup" submission="明确选择字段">内存容量会随当前内存模式和后备页要求进行必要的对齐。<see cref="VmMemoryService.SetVmMemorySettingsAsync"/></modification>
    /// <values kind="format">单位为 MiB；1 MiB = 1,048,576 字节。例如 <c>1024</c> 表示 1 GiB。</values>
    /// </fielddoc>
    public static readonly VmQueryField<long> Startup = new("Memory.Startup", "启动内存", "内存", VmQueryCollection.VirtualMachine, "Memory", "VirtualQuantity");

    /// <summary>动态内存开关：是否启用动态内存。启用后，Hyper-V 可以按需调整虚拟机的内存分配。</summary>
    /// <fielddoc>
    /// <meaning>是否启用动态内存。启用后，Hyper-V 可以按需调整虚拟机的内存分配。</meaning>
    /// <source>Msvm_MemorySettingData，读取或派生 DynamicMemoryEnabled。</source>
    /// <references><see cref="VmMemoryService"/>。</references>
    /// <modification kind="supported" input="newSettings.DynamicMemoryEnabled" submission="明确选择字段">动态内存与 vNUMA 互斥，切换时同步调整 vNUMA 配置。<see cref="VmMemoryService.SetVmMemorySettingsAsync"/></modification>
    /// <values kind="options"><c>true</c>：启用动态内存；<c>false</c>：使用静态内存。</values>
    /// </fielddoc>
    public static readonly VmQueryField<bool> DynamicMemoryEnabled = new("Memory.DynamicMemoryEnabled", "动态内存开关", "内存", VmQueryCollection.VirtualMachine, "Memory", "DynamicMemoryEnabled");

    /// <summary>最小内存：动态内存允许分配的最低内存大小，单位为 MiB。</summary>
    /// <fielddoc>
    /// <meaning>动态内存允许分配的最低内存大小。</meaning>
    /// <applicability>动态内存启用时参与内存分配；关闭时仍可读取保存的配置。</applicability>
    /// <source>Msvm_MemorySettingData，读取或派生 Reservation。</source>
    /// <references><see cref="VmMemoryService"/>；<see href="https://learn.microsoft.com/en-us/windows/win32/hyperv_v2/msvm-memorysettingdata">微软内存字段说明</see>。</references>
    /// <modification kind="supported" input="newSettings.Minimum" submission="明确选择字段"><see cref="VmMemoryService.SetVmMemorySettingsAsync"/></modification>
    /// <values kind="format">单位为 MiB；1 MiB = 1,048,576 字节。例如 <c>1024</c> 表示 1 GiB。</values>
    /// </fielddoc>
    public static readonly VmQueryField<long> Minimum = new("Memory.Minimum", "最小内存", "内存", VmQueryCollection.VirtualMachine, "Memory", "Reservation");

    /// <summary>最大内存：动态内存允许分配的最高内存大小，单位为 MiB。</summary>
    /// <fielddoc>
    /// <meaning>动态内存允许分配的最高内存大小。</meaning>
    /// <applicability>动态内存启用时作为内存分配上限。</applicability>
    /// <source>Msvm_MemorySettingData，读取或派生 Limit。</source>
    /// <references><see cref="VmMemoryService"/>；<see href="https://learn.microsoft.com/en-us/windows/win32/hyperv_v2/msvm-memorysettingdata">微软内存字段说明</see>。</references>
    /// <modification kind="supported" input="newSettings.Maximum" submission="明确选择字段"><see cref="VmMemoryService.SetVmMemorySettingsAsync"/></modification>
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
    /// <modification kind="supported" input="newSettings.Buffer" submission="明确选择字段"><see cref="VmMemoryService.SetVmMemorySettingsAsync"/></modification>
    /// </fielddoc>
    public static readonly VmQueryField<int> Buffer = new("Memory.Buffer", "内存缓冲比例", "内存", VmQueryCollection.VirtualMachine, "Memory", "TargetMemoryBuffer");

    /// <summary>内存权重：宿主内存发生竞争时，虚拟机参与内存分配的优先权重。</summary>
    /// <fielddoc>
    /// <meaning>宿主内存发生竞争时，虚拟机参与内存分配的优先权重。</meaning>
    /// <source>Msvm_MemorySettingData，读取或派生 Weight。</source>
    /// <references><see cref="VmMemoryService"/>；<see href="https://learn.microsoft.com/en-us/windows/win32/hyperv_v2/msvm-memorysettingdata">微软内存字段说明</see>。</references>
    /// <modification kind="supported" input="newSettings.Weight" submission="明确选择字段"><see cref="VmMemoryService.SetVmMemorySettingsAsync"/></modification>
    /// <values kind="format">无符号整数权重；数值越大，内存竞争时的相对优先级越高。</values>
    /// </fielddoc>
    public static readonly VmQueryField<uint> Weight = new("Memory.Weight", "内存权重", "内存", VmQueryCollection.VirtualMachine, "Memory", "Weight", o => Convert.ToUInt32(o));

    /// <summary>后备页大小：承载虚拟机内存所使用的页面类型：标准页、大页或巨页。</summary>
    /// <fielddoc>
    /// <meaning>承载虚拟机内存所使用的页面类型：标准页、大页或巨页。</meaning>
    /// <values kind="options">0：4 KiB 标准页；1：2 MiB 大页；2：1 GiB 巨页。0 是明确的标准页请求，不表示自动或未设置。</values>
    /// <source>优先读取 Msvm_MemorySettingData.BackingPageSize；仅提供 HugePagesEnabled 时，false 映射为 1，true 映射为 2。</source>
    /// <applicability>仅有 HugePagesEnabled 的宿主无法表达 4 KiB，Core 返回 Unsupported，不将 0 转成 false。高级页策略通常要求工作进程已退出；暂停、保存状态和重复提交同值都不能证明允许修改。配置读回不等于实际分配页粒度。</applicability>
    /// <verification>研究规则 memory:1—6 区分显式 0、省略值和旧组合开关。样本中的容量调整分支可能先完成 VirtualQuantity 调整而跳过伴随策略，不能凭整次调用成功断言每项策略生效；不同维护分支及后续独立映射页字段不能按 build 大小直接推广。</verification>
    /// <references><see cref="VmMemoryService"/>。</references>
    /// <modification kind="supported" input="newSettings.BackingPageSize" submission="明确选择字段">巨页要求内存容量按 1024 MiB 对齐；后备类型已知时联动为物理后备，并关闭已知开启的冷热提示、EPF 和专用压缩存储。每个 NUMA 节点的容量上限也会对齐。<see cref="VmMemoryService.SetVmMemorySettingsAsync"/></modification>
    /// </fielddoc>
    public static readonly VmQueryField<byte> BackingPageSize = new("Memory.BackingPageSize", "后备页大小", "内存", VmQueryCollection.VirtualMachine, "Memory", "BackingPageSize");

    /// <summary>内存后备类型：选择承载虚拟机内存的后备方式。此项与后备页大小、冷热提示及压缩存储有联动。（实验性）</summary>
    /// <fielddoc>
    /// <meaning>选择承载虚拟机内存的后备方式。此项与后备页大小、冷热提示及压缩存储有联动。</meaning>
    /// <unit>类型编码，不是内存大小。</unit>
    /// <values kind="options">现有选项映射为 0：物理映射；1：虚拟映射；2：混合映射。Core 保留原始编码，不为未识别值赋予含义。</values>
    /// <source>Msvm_MemorySettingData.BackingType；自由查询直接读取该属性。</source>
    /// <applicability>实验性读写能力。混合映射涉及逐 NUMA 节点的后备配置；当前入口没有提供这部分配套配置。保留编码读写不代表仅写入 2 就能完成混合映射。 修改前需使虚拟机退出活动状态；宿主最终决定是否接受配置。</applicability>
    /// <verification>研究规则 released-domain-09:1 在已核对的 28000 实现中确认：Virtual、Hybrid 要求第二代；1 GiB 后备页或映射页要求 Physical。保存态、已实现快照及工作进程还有限制；带 VirtualQuantity 的组合请求可能只调整容量。Core 保留现有规范化，不把配置读回视为实际分配或在线转换证明；其他版本需单独核对。</verification>
    /// <references><see cref="VmMemoryService.SetVmMemorySettingsAsync"/>；<see cref="VmMemorySettings.BackingType"/>。</references>
    /// <modification kind="supported" input="newSettings.BackingType" submission="明确选择字段">物理映射与冷热提示、EPF、专用压缩存储互斥。单独切换到非物理映射时，原有巨页配置改为大页；同时选择后备类型与巨页时，巨页优先，并采用物理映射。<see cref="VmMemoryService.SetVmMemorySettingsAsync"/></modification>
    /// </fielddoc>
    public static readonly VmQueryField<byte> BackingType = new("Memory.BackingType", "内存后备类型", "内存", VmQueryCollection.VirtualMachine, "Memory", "BackingType");

    /// <summary>动态内存操作对齐：动态内存调整所使用的对齐配置；与决定内存后备页面类型的 BackingPageSize 是不同字段。（实验性）</summary>
    /// <fielddoc>
    /// <meaning>动态内存调整所使用的对齐配置；与决定内存后备页面类型的 BackingPageSize 是不同字段。</meaning>
    /// <unit>对齐选项编码，不是直接以字节传入的对齐量。</unit>
    /// <values kind="options">现有选项映射为 0：禁用；1：4 KB；2：2 MB；3：1 GB。这些是项目已有选项的标签，Core 直接传递编码，不进行字节换算。</values>
    /// <source>Msvm_MemorySettingData.DynMemOperationAlignment；自由查询直接读取该属性。</source>
    /// <applicability>实验性读写能力。属于动态内存扩展配置。当前仅写入该属性，没有额外执行内存调整。 修改前需使虚拟机退出活动状态；宿主最终决定是否接受配置。</applicability>
    /// <verification>已核对当前读写映射和项目既有选项；0 对后续动态调整的具体影响、各档对齐的实际粒度及适用版本尚未验证。</verification>
    /// <references><see cref="VmMemoryService.SetVmMemorySettingsAsync"/>；<see cref="VmMemorySettings.DynMemOperationAlignment"/>。</references>
    /// <modification kind="supported" input="newSettings.DynMemOperationAlignment" submission="明确选择字段"><see cref="VmMemoryService.SetVmMemorySettingsAsync"/></modification>
    /// </fielddoc>
    public static readonly VmQueryField<uint> DynMemOperationAlignment = new("Memory.DynMemOperationAlignment", "动态内存操作对齐", "内存", VmQueryCollection.VirtualMachine, "Memory", "DynMemOperationAlignment");

    /// <summary>内存访问跟踪策略：配置内存访问跟踪的粒度策略。跟踪是否开启由 MemoryAccessTrackingState 单独配置。（实验性）</summary>
    /// <fielddoc>
    /// <meaning>配置内存访问跟踪的粒度策略。跟踪是否开启由 MemoryAccessTrackingState 单独配置。</meaning>
    /// <unit>跟踪策略编码；不能与 BackingPageSize 的编码互换。</unit>
    /// <values kind="options">现有选项映射为 0：最佳；1：4 KB；2：2 MB；3：1 GB。Core 原样传递编码，不主动开启跟踪。</values>
    /// <source>Msvm_MemorySettingData.MemoryAccessTrackingPolicy；自由查询直接读取该属性。</source>
    /// <applicability>实验性读写能力。应结合 MemoryAccessTrackingState 理解配置。读取到策略值本身不能说明跟踪已启用。 修改前需使虚拟机退出活动状态；宿主最终决定是否接受配置。</applicability>
    /// <verification>研究规则 released-domain-13:1：整体跟踪按引用计数管理，重复申请或释放一次不一定触发底层启停；配置读回不能证明资源准备或开启完成。此处是整机 MemorySettingData，不是同名的 NumaNodeTopology 字段，逐节点写入规则不能套用到这里。</verification>
    /// <references><see cref="VmMemoryService.SetVmMemorySettingsAsync"/>；<see cref="VmMemorySettings.MemoryAccessTrackingPolicy"/>。</references>
    /// <modification kind="supported" input="newSettings.MemoryAccessTrackingPolicy" submission="明确选择字段"><see cref="VmMemoryService.SetVmMemorySettingsAsync"/></modification>
    /// </fielddoc>
    public static readonly VmQueryField<byte> MemoryAccessTrackingPolicy = new("Memory.MemoryAccessTrackingPolicy", "内存访问跟踪策略", "内存", VmQueryCollection.VirtualMachine, "Memory", "MemoryAccessTrackingPolicy");

    /// <summary>内存访问跟踪状态：配置是否启用内存访问跟踪，或交给逐 NUMA 节点配置决定。此值是配置，不是一次访问采样结果。（实验性）</summary>
    /// <fielddoc>
    /// <meaning>配置是否启用内存访问跟踪，或交给逐 NUMA 节点配置决定。此值是配置，不是一次访问采样结果。</meaning>
    /// <values kind="options">现有选项映射为 0：关闭；1：开启；2：按 NUMA 节点配置。</values>
    /// <source>Msvm_MemorySettingData.MemoryAccessTrackingState；自由查询直接读取该属性。</source>
    /// <applicability>实验性读写能力。当前入口没有提供编码 2 所需的逐节点配置。保留该编码读写不表示配套能力完整。 修改前需使虚拟机退出活动状态；宿主最终决定是否接受配置。</applicability>
    /// <verification>研究规则 released-domain-13:1、13:2、147:4：整机跟踪配置与逐节点同名字段属于不同路径；后者显式 State=0 与省略不同，不能据此推断本字段的关闭行为。底层关闭有独立等待及状态检查，失败不表示已关闭或已回滚；配置读回也不证明所有来源的跟踪已停止。</verification>
    /// <references><see cref="VmMemoryService.SetVmMemorySettingsAsync"/>；<see cref="VmMemorySettings.MemoryAccessTrackingState"/>。</references>
    /// <modification kind="supported" input="newSettings.MemoryAccessTrackingState" submission="明确选择字段"><see cref="VmMemoryService.SetVmMemorySettingsAsync"/></modification>
    /// </fielddoc>
    public static readonly VmQueryField<byte> MemoryAccessTrackingState = new("Memory.MemoryAccessTrackingState", "内存访问跟踪状态", "内存", VmQueryCollection.VirtualMachine, "Memory", "MemoryAccessTrackingState");

    /// <summary>SGX 开关：请求为虚拟机启用 SGX 支持。SGX 内存容量由 SgxSize 配置；启用配置本身不表示来宾已能使用 SGX。（实验性）</summary>
    /// <fielddoc>
    /// <meaning>请求为虚拟机启用 SGX 支持。SGX 内存容量由 SgxSize 配置；启用配置本身不表示来宾已能使用 SGX。</meaning>
    /// <values kind="options">true：请求启用；false：请求关闭。设置模型中将它设为 true 时，若 SgxSize 为空或小于 2，会先补为 2 MiB。</values>
    /// <source>Msvm_MemorySettingData.SgxEnabled；自由查询直接读取该属性。</source>
    /// <applicability>实验性读写能力。微软文档记录该属性从 Windows 10 1703 加入。实际使用仍需宿主硬件、Hyper-V 配置和来宾共同支持。 修改前需使虚拟机退出活动状态；宿主最终决定是否接受配置。</applicability>
    /// <verification>已核对官方属性说明及当前容量联动；未验证不同硬件、Windows 版本和来宾中的 SGX 功能。</verification>
    /// <references><see cref="VmMemoryService.SetVmMemorySettingsAsync"/>；<see cref="VmMemorySettings.SgxEnabled"/>。<see href="https://learn.microsoft.com/en-us/windows/win32/hyperv_v2/msvm-memorysettingdata">微软内存配置说明</see>。</references>
    /// <modification kind="supported" input="newSettings.SgxEnabled" submission="明确选择字段">启用时同步设置 SGX 容量，最小 2 MiB，并按 2 MiB 向下对齐。关闭时保留原容量。<see cref="VmMemoryService.SetVmMemorySettingsAsync"/></modification>
    /// </fielddoc>
    public static readonly VmQueryField<bool> SgxEnabled = new("Memory.SgxEnabled", "SGX 开关", "内存", VmQueryCollection.VirtualMachine, "Memory", "SgxEnabled");

    /// <summary>SGX 内存大小：为虚拟机配置的 SGX EPC 内存容量，与普通启动内存分开。（实验性）</summary>
    /// <fielddoc>
    /// <meaning>为虚拟机配置的 SGX EPC 内存容量，与普通启动内存分开。</meaning>
    /// <unit>MiB；Core 的容量单位。底层属性为 UInt64，当前公开模型以 Double 表示，不提供小数 MiB 的写入精度。</unit>
    /// <values>提交时转换为 UInt64，最小 2 MiB，再按 2 MiB 向下对齐。例如 5 MiB 最终写为 4 MiB。需要精确请求时传入不小于 2 的偶数整数。</values>
    /// <source>Msvm_MemorySettingData.SgxSize；自由查询直接读取该属性。</source>
    /// <applicability>实验性读写能力。只有合并后的 SgxEnabled 为 true 时，单独选择此字段才会写入容量；关闭 SGX 时该分支不写。微软文档记录此属性从 Windows 10 1703 加入。 修改前需使虚拟机退出活动状态；宿主最终决定是否接受配置。</applicability>
    /// <absence>自由查询按字段状态区分缺值；GetVmMemorySettingsAsync 的设置模型未读到容量时返回 null，已读到的 0 原样保留，不能把两者混同。</absence>
    /// <verification>已核对官方容量说明和 Core 转换分支；未验证硬件可用容量、宿主允许的上限及来宾实际使用效果。</verification>
    /// <references><see cref="VmMemoryService.SetVmMemorySettingsAsync"/>；<see cref="VmMemorySettings.SgxSize"/>。<see href="https://learn.microsoft.com/en-us/windows/win32/hyperv_v2/msvm-memorysettingdata">微软内存配置说明</see>。</references>
    /// <modification kind="supported" input="newSettings.SgxSize" submission="明确选择字段">仅 SGX 启用时更新容量；容量按 2 MiB 向下对齐，最小 2 MiB。<see cref="VmMemoryService.SetVmMemorySettingsAsync"/></modification>
    /// </fielddoc>
    public static readonly VmQueryField<double> SgxSize = new("Memory.SgxSize", "SGX 内存大小", "内存", VmQueryCollection.VirtualMachine, "Memory", "SgxSize");

    /// <summary>SGX 启动控制模式：SGX 启动控制的访问模式配置，与虚拟机的普通引导顺序无关。（实验性）</summary>
    /// <fielddoc>
    /// <meaning>SGX 启动控制的访问模式配置，与虚拟机的普通引导顺序无关。</meaning>
    /// <values kind="options">现有选项映射为 0：禁止访问；1：仅限读取；2：允许读写。Core 原样传递该编码。</values>
    /// <source>Msvm_MemorySettingData.SgxLaunchControlMode；自由查询直接读取该属性。</source>
    /// <applicability>实验性读写能力。属于 SGX 扩展配置；当前入口不自动启用 SGX，也不补齐默认启动控制字符串。 修改前需使虚拟机退出活动状态；宿主最终决定是否接受配置。</applicability>
    /// <verification>已核对当前读写映射和项目既有选项；访问权限具体作用于哪一层、与 SgxLaunchControlDefault 的配合要求及适用版本尚未验证。</verification>
    /// <references><see cref="VmMemoryService.SetVmMemorySettingsAsync"/>；<see cref="VmMemorySettings.SgxLaunchControlMode"/>。</references>
    /// <modification kind="supported" input="newSettings.SgxLaunchControlMode" submission="明确选择字段"><see cref="VmMemoryService.SetVmMemorySettingsAsync"/></modification>
    /// </fielddoc>
    public static readonly VmQueryField<uint> SgxLaunchControlMode = new("Memory.SgxLaunchControlMode", "SGX 启动控制模式", "内存", VmQueryCollection.VirtualMachine, "Memory", "SgxLaunchControlMode");

    /// <summary>GPA 固定开关：来宾物理地址（GPA）固定配置的开关。Core 目前读取并提交对应属性，不提供指定 GPA 范围的独立操作。（实验性）</summary>
    /// <fielddoc>
    /// <meaning>来宾物理地址（GPA）固定配置的开关。Core 目前读取并提交对应属性，不提供指定 GPA 范围的独立操作。</meaning>
    /// <values kind="options">true：请求开启；false：请求关闭。当前没有由 Core 定义的宿主默认值。</values>
    /// <source>Msvm_MemorySettingData.EnableGpaPinning；自由查询直接读取该属性。</source>
    /// <applicability>实验性读写能力。宿主必须提供 EnableGpaPinning 属性；读取到开关不表示某个指定地址范围已经固定。 修改前需使虚拟机退出活动状态；宿主最终决定是否接受配置。</applicability>
    /// <verification>已核对当前读写映射和项目既有选项；固定范围、与动态内存和迁移的关系、硬件条件及版本边界尚未验证。</verification>
    /// <references><see cref="VmMemoryService.SetVmMemorySettingsAsync"/>；<see cref="VmMemorySettings.EnableGpaPinning"/>。</references>
    /// <modification kind="supported" input="newSettings.EnableGpaPinning" submission="明确选择字段"><see cref="VmMemoryService.SetVmMemorySettingsAsync"/></modification>
    /// </fielddoc>
    public static readonly VmQueryField<bool> EnableGpaPinning = new("Memory.EnableGpaPinning", "GPA 固定开关", "内存", VmQueryCollection.VirtualMachine, "Memory", "EnableGpaPinning");

    /// <summary>CXL 开关：实验性配置，已知部分 VMMS 版本在写入时可能崩溃；Core 保留写入入口，由调用方提示风险。</summary>
    /// <fielddoc>
    /// <meaning>虚拟机内存配置中的 CXL 开关。此入口只修改 CxlEnabled，不枚举、分配或直通具体 CXL 设备。</meaning>
    /// <values kind="options">true：请求开启；false：请求关闭。当前没有由 Core 定义的宿主默认值。</values>
    /// <source>Msvm_MemorySettingData.CxlEnabled；自由查询直接读取该属性。</source>
    /// <applicability>ARM64 VMMS 10.0.29671.1000 已实测写 true 导致服务崩溃，10.0.28000.2804 静态确认同类缺陷；其他版本的修复边界未确认。保存设置即可触发，无需启动来宾。已研究的 28000 路径要求第二代、配置版本至少 12.2 且没有工作进程；旧配置为 true 时还可能在处理新 false 前校验失败，关闭不能作为可靠恢复手段。Core 保留读写入口，不按研究版本封禁；满足前述门槛不证明 CXL 可用。</applicability>
    /// <verification>研究规则 released-domain-141:1：ARM64 VMMS 10.0.29671.1000 已实测从 false 写 true 导致服务崩溃；ARM64 10.0.28000.2804 静态确认同类错误报告表空指针缺陷。其他架构、维护版本和修复边界未确认，不能因此判断安全。cxl-operation:1 还确认旧配置为 true 时，普通 WMI 修改可能在处理新 false 前就因旧值校验失败，关闭不是可靠恢复手段。研究尚未找到普通虚拟机可完整启用的配置；设备容量、映射容量和来宾可用容量必须分别验证。</verification>
    /// <references><see cref="VmMemoryService.SetVmMemorySettingsAsync"/>；<see cref="VmMemorySettings.CxlEnabled"/>。</references>
    /// <modification kind="supported" input="newSettings.CxlEnabled" submission="明确选择字段"><see cref="VmMemoryService.SetVmMemorySettingsAsync"/></modification>
    /// </fielddoc>
    public static readonly VmQueryField<bool> CxlEnabled = new("Memory.CxlEnabled", "CXL 开关", "内存", VmQueryCollection.VirtualMachine, "Memory", "CxlEnabled");

    /// <summary>冷内存提示：控制内存配置中的冷提示开关。此入口不负责提交某段内存的冷热信息。（实验性）</summary>
    /// <fielddoc>
    /// <meaning>控制内存配置中的冷提示开关。此入口不负责提交某段内存的冷热信息。</meaning>
    /// <values kind="options">true：请求开启；false：请求关闭。现有物理后备规则会将 true 规范化为 false。</values>
    /// <source>Msvm_MemorySettingData.EnableColdHint；自由查询直接读取该属性。</source>
    /// <applicability>实验性读写能力。按当前 Core 规则，只在非物理后备配置下保留开启请求。 修改前需使虚拟机退出活动状态；宿主最终决定是否接受配置。</applicability>
    /// <verification>研究规则 released-domain-11:1、147:3：已核对的 28000 实现要求 Virtual 或 Hybrid 后备，不能与 1 GiB 页组合；工作进程、保存态和已实现快照还有修改限制。VID 对冷热提示分别授权，冷提示配置保存不证明授权或投递完成；带容量的组合请求可能只调整容量。Core 不新增研究版本拦截，具体行为仍由宿主决定。</verification>
    /// <references><see cref="VmMemoryService.SetVmMemorySettingsAsync"/>；<see cref="VmMemorySettings.EnableColdHint"/>。</references>
    /// <modification kind="supported" input="newSettings.EnableColdHint" submission="明确选择字段">物理映射后备下，此开关保持关闭；启用此开关不会自动切换后备类型。<see cref="VmMemoryService.SetVmMemorySettingsAsync"/></modification>
    /// </fielddoc>
    public static readonly VmQueryField<bool> EnableColdHint = new("Memory.EnableColdHint", "冷内存提示", "内存", VmQueryCollection.VirtualMachine, "Memory", "EnableColdHint");

    /// <summary>热内存提示：控制内存配置中的热提示开关。此入口不负责提交某段内存的冷热信息。（实验性）</summary>
    /// <fielddoc>
    /// <meaning>控制内存配置中的热提示开关。此入口不负责提交某段内存的冷热信息。</meaning>
    /// <values kind="options">true：请求开启；false：请求关闭。现有物理后备规则会将 true 规范化为 false。</values>
    /// <source>Msvm_MemorySettingData.EnableHotHint；自由查询直接读取该属性。</source>
    /// <applicability>实验性读写能力。按当前 Core 规则，只在非物理后备配置下保留开启请求。 修改前需使虚拟机退出活动状态；宿主最终决定是否接受配置。</applicability>
    /// <verification>研究规则 released-domain-11:1、147:3：热提示与冷提示有独立的 VID 授权位，打开其中一项不会自动开放另一项；配置保存不能证明实际提示已投递。已核对的 28000 配置路径要求非物理后备且不能与 1 GiB 页组合；工作进程和保存态仍有限制，带容量的组合请求可能只调整容量。</verification>
    /// <references><see cref="VmMemoryService.SetVmMemorySettingsAsync"/>；<see cref="VmMemorySettings.EnableHotHint"/>。</references>
    /// <modification kind="supported" input="newSettings.EnableHotHint" submission="明确选择字段">物理映射后备下，此开关保持关闭；启用此开关不会自动切换后备类型。<see cref="VmMemoryService.SetVmMemorySettingsAsync"/></modification>
    /// </fielddoc>
    public static readonly VmQueryField<bool> EnableHotHint = new("Memory.EnableHotHint", "热内存提示", "内存", VmQueryCollection.VirtualMachine, "Memory", "EnableHotHint");

    /// <summary>EPF 开关：保留 Hyper-V 的 EnableEpf 配置开关。缩写全称及功能用途尚未确认，因此不将它解释成某种已知内存优化。（实验性）</summary>
    /// <fielddoc>
    /// <meaning>保留 Hyper-V 的 EnableEpf 配置开关。缩写全称及功能用途尚未确认，因此不将它解释成某种已知内存优化。</meaning>
    /// <values kind="options">true、false。系统默认值和两种取值的实际行为尚未确认。</values>
    /// <source>Msvm_MemorySettingData.EnableEpf；自由查询直接读取该属性。</source>
    /// <applicability>实验性读写能力。现有 Core 规则将它与冷提示、热提示和专用压缩存储一起处理：物理后备时不保留开启请求。 修改前需使虚拟机退出活动状态；宿主最终决定是否接受配置。</applicability>
    /// <verification>研究规则 released-domain-11:1 确认已核对的 28000 初始化路径将此值写入独立 VID 请求位；非物理后备、页策略、工作进程和保存态限制与同组内存提示一起检查。保存或读回不证明实际效果，带容量的组合请求可能跳过该项。缩写含义及其他版本仍不作推定。</verification>
    /// <references><see cref="VmMemoryService.SetVmMemorySettingsAsync"/>；<see cref="VmMemorySettings.EnableEpf"/>。</references>
    /// <modification kind="supported" input="newSettings.EnableEpf" submission="明确选择字段">物理映射后备下，此开关保持关闭；启用此开关不会自动切换后备类型。<see cref="VmMemoryService.SetVmMemorySettingsAsync"/></modification>
    /// </fielddoc>
    public static readonly VmQueryField<bool> EnableEpf = new("Memory.EnableEpf", "EPF 开关", "内存", VmQueryCollection.VirtualMachine, "Memory", "EnableEpf");

    /// <summary>专用压缩存储：内存配置中的专用压缩存储开关。当前入口仅传递配置，不返回压缩量或压缩率。（实验性）</summary>
    /// <fielddoc>
    /// <meaning>内存配置中的专用压缩存储开关。当前入口仅传递配置，不返回压缩量或压缩率。</meaning>
    /// <values kind="options">true：请求开启；false：请求关闭。现有物理后备规则会将 true 规范化为 false。</values>
    /// <source>Msvm_MemorySettingData.EnablePrivateCompressionStore；自由查询直接读取该属性。</source>
    /// <applicability>实验性读写能力。按当前 Core 规则，只在非物理后备配置下保留开启请求。 修改前需使虚拟机退出活动状态；宿主最终决定是否接受配置。</applicability>
    /// <verification>研究规则 released-domain-11:1：已核对的 28000 配置路径要求非物理后备，不能与 1 GiB 页组合；单独提交仍受工作进程、保存态及已实现快照限制。保存 true 不证明压缩已开始，保存 false 不证明现有分区立即关闭该行为；资源回收、效果及跨版本范围未完成验证。</verification>
    /// <references><see cref="VmMemoryService.SetVmMemorySettingsAsync"/>；<see cref="VmMemorySettings.EnablePrivateCompressionStore"/>。</references>
    /// <modification kind="supported" input="newSettings.EnablePrivateCompressionStore" submission="明确选择字段">物理映射后备下，此开关保持关闭；启用此开关不会自动切换后备类型。<see cref="VmMemoryService.SetVmMemorySettingsAsync"/></modification>
    /// </fielddoc>
    public static readonly VmQueryField<bool> EnablePrivateCompressionStore = new("Memory.EnablePrivateCompressionStore", "私有压缩存储开关", "内存", VmQueryCollection.VirtualMachine, "Memory", "EnablePrivateCompressionStore");

    /// <summary>每 NUMA 节点内存块上限：限制来宾可见的单个虚拟 NUMA 节点的内存容量。底层名称虽然包含 Blocks，调用时仍按内存容量理解。（实验性）</summary>
    /// <fielddoc>
    /// <meaning>限制来宾可见的单个虚拟 NUMA 节点的内存容量。底层名称虽然包含 Blocks，调用时仍按内存容量理解。</meaning>
    /// <unit>MiB。表示容量，不是页面数，也不是 NUMA 节点数。</unit>
    /// <values>Core 按当前页设置向下对齐：巨页为 1024 MiB，其余为 2 MiB；结果至少达到按同一粒度向上对齐的 32 MiB。因此巨页模式的最小提交值为 1024 MiB。</values>
    /// <source>Msvm_MemorySettingData.MaxMemoryBlocksPerNumaNode；自由查询直接读取该属性。</source>
    /// <applicability>实验性读写能力。这是虚拟 NUMA 配置，不代表宿主物理节点容量，也不固定来宾内存到某个宿主节点。 修改前需使虚拟机退出活动状态；宿主最终决定是否接受配置。</applicability>
    /// <verification>已对照微软对单节点容量的定义、容量设置示例和 Core 对齐规则；多节点拓扑效果、动态内存组合及跨版本范围尚未验证。</verification>
    /// <references><see cref="VmMemoryService.SetVmMemorySettingsAsync"/>；<see cref="VmMemorySettings.MaxMemoryBlocksPerNumaNode"/>。<see href="https://learn.microsoft.com/en-us/windows/win32/hyperv_v2/msvm-memorysettingdata">微软内存配置说明</see>。</references>
    /// <modification kind="supported" input="newSettings.MaxMemoryBlocksPerNumaNode" submission="明确选择字段">切换巨页时，此容量上限也会联动对齐。<see cref="VmMemoryService.SetVmMemorySettingsAsync"/></modification>
    /// </fielddoc>
    public static readonly VmQueryField<ulong> MaxMemoryBlocksPerNumaNode = new("Memory.MaxMemoryBlocksPerNumaNode", "每 NUMA 节点内存块上限", "内存", VmQueryCollection.VirtualMachine, "Memory", "MaxMemoryBlocksPerNumaNode");

    /// <summary>SGX 默认启动控制：SGX 默认启动控制配置的原始文本。当前 Core 不解析、生成或改写其中的内容。（实验性）</summary>
    /// <fielddoc>
    /// <meaning>SGX 默认启动控制配置的原始文本。当前 Core 不解析、生成或改写其中的内容。</meaning>
    /// <values>宿主返回的文本原样保留。格式、允许的内容、空字符串是否有效及默认值尚未确认。</values>
    /// <source>Msvm_MemorySettingData.SgxLaunchControlDefault；自由查询直接读取该属性。</source>
    /// <applicability>实验性读写能力。属于 SGX 扩展配置；与 SgxLaunchControlMode 的配合要求尚未验证。 修改前需使虚拟机退出活动状态；宿主最终决定是否接受配置。</applicability>
    /// <verification>已核对原样读写路径；字符串结构、内容语义及支持版本尚未验证。不要把它当作可自由填写的说明文字。</verification>
    /// <references><see cref="VmMemoryService.SetVmMemorySettingsAsync"/>；<see cref="VmMemorySettings.SgxLaunchControlDefault"/>。</references>
    /// <modification kind="supported" input="newSettings.SgxLaunchControlDefault" submission="明确选择字段"><see cref="VmMemoryService.SetVmMemorySettingsAsync"/></modification>
    /// </fielddoc>
    public static readonly VmQueryField<string> SgxLaunchControlDefault = new("Memory.SgxLaunchControlDefault", "SGX 默认启动控制", "内存", VmQueryCollection.VirtualMachine, "Memory", "SgxLaunchControlDefault");

    /// <summary>内存加密策略：请求虚拟机内存采用的加密策略。此字段表示配置，不能用它单独证明运行中的内存已经加密。（实验性）</summary>
    /// <fielddoc>
    /// <meaning>请求虚拟机内存采用的加密策略。此字段表示配置，不能用它单独证明运行中的内存已经加密。</meaning>
    /// <values kind="options">现有选项映射为 0：禁用；1：支持时启用；2：始终启用。Core 原样传递编码。</values>
    /// <source>Msvm_MemorySettingData.MemoryEncryptionPolicy；自由查询直接读取该属性。</source>
    /// <applicability>实验性读写能力。需要宿主提供 MemoryEncryptionPolicy；当前入口不探测完整的机密计算能力，也不更改安全启动或 vTPM。 修改前需使虚拟机退出活动状态；宿主最终决定是否接受配置。</applicability>
    /// <verification>研究规则 released-domain-38:1：已核对的实现只接受 0、1、2；非零要求第二代及至少 10.0 配置版本，0 不豁免工作进程和保存态限制。策略 2 可在初始化时因不支持加密而失败；策略 1 或保存成功不证明实际加密。带容量的组合请求可能跳过策略，恢复 0 不证明现有内存立即解密。Core 不新增研究版本拦截，跨版本及迁移效果仍需单独验证。</verification>
    /// <references><see cref="VmMemoryService.SetVmMemorySettingsAsync"/>；<see cref="VmMemorySettings.MemoryEncryptionPolicy"/>。</references>
    /// <modification kind="supported" input="newSettings.MemoryEncryptionPolicy" submission="明确选择字段"><see cref="VmMemoryService.SetVmMemorySettingsAsync"/></modification>
    /// </fielddoc>
    public static readonly VmQueryField<byte> MemoryEncryptionPolicy = new("Memory.MemoryEncryptionPolicy", "内存加密策略", "内存", VmQueryCollection.VirtualMachine, "Memory", "MemoryEncryptionPolicy");

    /// <summary>运行时已分配内存：Hyper-V 当前分配给运行中虚拟机的内存大小，单位为 MiB。</summary>
    /// <fielddoc>
    /// <meaning>Hyper-V 当前分配给运行中虚拟机的内存大小。</meaning>
    /// <source>摘要运行状态、MemoryUsage、MemoryAvailable 与动态内存配置，读取或派生 AssignedMiB。</source>
    /// <references><see cref="VmMemoryService"/>。</references>
    /// <modification kind="readonly">Hyper-V 当前实际分配量；调整内存配置会影响它，但不能把采样结果作为配置值写回。 相关入口：<see cref="VmMemoryService.SetVmMemorySettingsAsync"/>。</modification>
    /// <values kind="format">单位为 MiB；1 MiB = 1,048,576 字节。例如 <c>1024</c> 表示 1 GiB。</values>
    /// </fielddoc>
    public static readonly VmQueryField<long> AssignedMiB = new("Memory.AssignedMiB", "运行时已分配内存", "内存", VmQueryCollection.VirtualMachine, "RuntimeMemory", "AssignedMiB");

    /// <summary>来宾可用内存比例：按来宾内存需求换算的可用内存比例，单位为 %。</summary>
    /// <fielddoc>
    /// <meaning>当前分配内存相对来宾内存需求的剩余比例，单位为 %。不等同于来宾任务管理器的进程工作集或空闲页比例。</meaning>
    /// <unit>%</unit>
    /// <values>可为负数，表示内存需求超过当前分配量。</values>
    /// <applicability>适用于运行中且能取得来宾内存需求的虚拟机。固定内存计数器必须在采样前后唯一对应同一 GUID，运行时间未倒退且分配量一致；同名、未报告需求或查询失败时返回缺测状态，不能当作零占用。</applicability>
    /// <source>动态内存使用摘要 MemoryAvailable。固定内存使用 Hyper-V Dynamic Memory VM 的 Current Pressure × Guest Visible Physical Memory / 100 推算需求，再以摘要 MemoryUsage 换算可用比例；不会采用对固定内存无效的摘要 MemoryAvailable。</source>
    /// <references><see cref="VmMemoryService"/>；<see cref="VmQueryService.GetVmRuntimeMemoryDataAsync"/>。</references>
    /// <modification kind="readonly">由来宾上报或监控推算的可用比例，不能直接设置。</modification>
    /// </fielddoc>
    public static readonly VmQueryField<int> AvailablePercent = new("Memory.AvailablePercent", "来宾可用内存比例", "内存", VmQueryCollection.VirtualMachine, "RuntimeMemory", "AvailablePercent");

    internal static readonly VmQueryField[] Items = [Startup, DynamicMemoryEnabled, Minimum, Maximum, Buffer, Weight, BackingPageSize, BackingType, DynMemOperationAlignment, MemoryAccessTrackingPolicy, MemoryAccessTrackingState, SgxEnabled, SgxSize, SgxLaunchControlMode, EnableGpaPinning, CxlEnabled, EnableColdHint, EnableHotHint, EnableEpf, EnablePrivateCompressionStore, MaxMemoryBlocksPerNumaNode, SgxLaunchControlDefault, MemoryEncryptionPolicy, AssignedMiB, AvailablePercent];
}
