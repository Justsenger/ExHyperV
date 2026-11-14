using ExHyperV.Models;
using ExHyperV.Tools;

namespace ExHyperV.Services;

/// <summary>处理器的独立可查询字段；可以任意选择其中一项，不要求整组读取或返回。</summary>
public static class VmProcessorFields
{
    /// <summary>处理器数量：分配给虚拟机的虚拟处理器数量。</summary>
    /// <fielddoc>
    /// <meaning>分配给虚拟机的虚拟处理器数量。</meaning>
    /// <source>Msvm_ProcessorSettingData，读取或派生 VirtualQuantity。</source>
    /// <references><see cref="VmProcessorService"/>；<see href="https://learn.microsoft.com/en-us/windows/win32/hyperv_v2/msvm-processorsettingdata">微软处理器字段说明</see>。</references>
    /// <modification kind="supported" input="newSettings.Count" submission="明确选择字段"><see cref="M:ExHyperV.Services.VmProcessorService.SetVmProcessorAsync(System.Guid,ExHyperV.Models.VmProcessorSettings,System.Collections.Generic.IReadOnlyCollection{System.String})"/></modification>
    /// <values kind="format">正整数，例如 <c>4</c> 表示 4 个虚拟处理器。</values>
    /// </fielddoc>
    public static readonly VmQueryField<int> Count = new("Processor.Count", "处理器数量", "处理器", VmQueryCollection.VirtualMachine, "Processor", "VirtualQuantity");

    /// <summary>处理器保留比例：为虚拟机保留的处理器资源比例，单位为 0.001%；100000 表示 100%。</summary>
    /// <fielddoc>
    /// <meaning>为虚拟机保留的处理器资源比例。</meaning>
    /// <source>Msvm_ProcessorSettingData，读取或派生 Reservation。</source>
    /// <references><see cref="VmProcessorService"/>；<see href="https://learn.microsoft.com/en-us/windows/win32/hyperv_v2/msvm-processorsettingdata">微软处理器字段说明</see>。</references>
    /// <modification kind="supported" input="newSettings.Reservation" submission="明确选择字段"><see cref="M:ExHyperV.Services.VmProcessorService.SetVmProcessorAsync(System.Guid,ExHyperV.Models.VmProcessorSettings,System.Collections.Generic.IReadOnlyCollection{System.String})"/></modification>
    /// <values kind="format">单位为 0.001%；<c>100000</c> 表示 100%，<c>50000</c> 表示 50%。</values>
    /// </fielddoc>
    public static readonly VmQueryField<ulong> Reservation = new("Processor.Reservation", "处理器保留比例", "处理器", VmQueryCollection.VirtualMachine, "Processor", "Reservation", o => Convert.ToUInt64(o));

    /// <summary>处理器限制比例：虚拟机可使用的处理器资源上限，单位为 0.001%；100000 表示 100%。</summary>
    /// <fielddoc>
    /// <meaning>虚拟机可使用的处理器资源上限。</meaning>
    /// <source>Msvm_ProcessorSettingData，读取或派生 Limit。</source>
    /// <references><see cref="VmProcessorService"/>；<see href="https://learn.microsoft.com/en-us/windows/win32/hyperv_v2/msvm-processorsettingdata">微软处理器字段说明</see>。</references>
    /// <modification kind="supported" input="newSettings.Limit" submission="明确选择字段"><see cref="M:ExHyperV.Services.VmProcessorService.SetVmProcessorAsync(System.Guid,ExHyperV.Models.VmProcessorSettings,System.Collections.Generic.IReadOnlyCollection{System.String})"/></modification>
    /// <values kind="format">单位为 0.001%；<c>100000</c> 表示 100%，<c>50000</c> 表示 50%。</values>
    /// </fielddoc>
    public static readonly VmQueryField<ulong> Limit = new("Processor.Limit", "处理器限制比例", "处理器", VmQueryCollection.VirtualMachine, "Processor", "Limit", o => Convert.ToUInt64(o));

    /// <summary>处理器相对权重：处理器资源发生竞争时，虚拟机参与资源分配的相对权重。</summary>
    /// <fielddoc>
    /// <meaning>处理器资源发生竞争时，虚拟机参与资源分配的相对权重。</meaning>
    /// <source>Msvm_ProcessorSettingData，读取或派生 Weight。</source>
    /// <references><see cref="VmProcessorService"/>。</references>
    /// <modification kind="supported" input="newSettings.RelativeWeight" submission="明确选择字段"><see cref="M:ExHyperV.Services.VmProcessorService.SetVmProcessorAsync(System.Guid,ExHyperV.Models.VmProcessorSettings,System.Collections.Generic.IReadOnlyCollection{System.String})"/></modification>
    /// <values kind="format">整数权重；数值越大，资源竞争时的相对优先级越高。</values>
    /// </fielddoc>
    public static readonly VmQueryField<int> RelativeWeight = new("Processor.RelativeWeight", "处理器相对权重", "处理器", VmQueryCollection.VirtualMachine, "Processor", "Weight");

    /// <summary>嵌套虚拟化：是否向来宾公开硬件虚拟化扩展，用于运行嵌套虚拟机。</summary>
    /// <fielddoc>
    /// <meaning>是否向来宾公开硬件虚拟化扩展，用于运行嵌套虚拟机。</meaning>
    /// <source>Msvm_ProcessorSettingData，读取或派生 ExposeVirtualizationExtensions。</source>
    /// <references><see cref="VmProcessorService"/>；<see href="https://learn.microsoft.com/en-us/windows/win32/hyperv_v2/msvm-processorsettingdata">微软处理器字段说明</see>。</references>
    /// <modification kind="supported" input="newSettings.ExposeVirtualizationExtensions" submission="明确选择字段"><see cref="M:ExHyperV.Services.VmProcessorService.SetVmProcessorAsync(System.Guid,ExHyperV.Models.VmProcessorSettings,System.Collections.Generic.IReadOnlyCollection{System.String})"/></modification>
    /// <values kind="options"><c>true</c>：公开硬件虚拟化扩展；<c>false</c>：不公开。</values>
    /// </fielddoc>
    public static readonly VmQueryField<bool> ExposeVirtualizationExtensions = new("Processor.ExposeVirtualizationExtensions", "嵌套虚拟化", "处理器", VmQueryCollection.VirtualMachine, "Processor", "ExposeVirtualizationExtensions");

    /// <summary>当虚拟机的设备模拟等操作持续占用过多宿主处理器时间时，Hyper-V 对相关处理进行节流，减轻它对其他虚拟机和宿主任务的影响。</summary>
    /// <fielddoc>
    /// <meaning>当虚拟机的设备模拟等操作持续占用过多宿主处理器时间时，Hyper-V 对相关处理进行节流，减轻它对其他虚拟机和宿主任务的影响。</meaning>
    /// <source>Msvm_ProcessorSettingData，读取或派生 EnableHostResourceProtection。</source>
    /// <references><see cref="VmProcessorService"/>；<see href="https://learn.microsoft.com/en-us/windows/win32/hyperv_v2/msvm-processorsettingdata">微软处理器字段说明</see>。</references>
    /// <modification kind="supported" input="newSettings.EnableHostResourceProtection" submission="明确选择字段"><see cref="M:ExHyperV.Services.VmProcessorService.SetVmProcessorAsync(System.Guid,ExHyperV.Models.VmProcessorSettings,System.Collections.Generic.IReadOnlyCollection{System.String})"/></modification>
    /// <values kind="options"><c>true</c>：启用宿主资源保护；<c>false</c>：关闭。</values>
    /// </fielddoc>
    public static readonly VmQueryField<bool> EnableHostResourceProtection = new("Processor.EnableHostResourceProtection", "宿主资源保护", "处理器", VmQueryCollection.VirtualMachine, "Processor", "EnableHostResourceProtection");

    /// <summary>启用处理器迁移兼容：开启后限制向来宾公开的处理器功能，以提高不同处理器宿主之间迁移的兼容性。</summary>
    /// <fielddoc label="启用处理器迁移兼容">
    /// <meaning>开启后限制向来宾公开的处理器功能，以提高不同处理器宿主之间迁移的兼容性。</meaning>
    /// <source>Msvm_ProcessorSettingData，读取或派生 LimitProcessorFeatures。</source>
    /// <references><see cref="VmProcessorService"/>。</references>
    /// <modification kind="supported" input="newSettings.CompatibilityForMigrationEnabled" submission="明确选择字段"><see cref="M:ExHyperV.Services.VmProcessorService.SetVmProcessorAsync(System.Guid,ExHyperV.Models.VmProcessorSettings,System.Collections.Generic.IReadOnlyCollection{System.String})"/></modification>
    /// <values kind="options"><c>true</c>：开启；<c>false</c>：关闭。</values>
    /// </fielddoc>
    public static readonly VmQueryField<bool> CompatibilityForMigrationEnabled = new("Processor.CompatibilityForMigrationEnabled", "迁移兼容", "处理器", VmQueryCollection.VirtualMachine, "Processor", "LimitProcessorFeatures");

    /// <summary>迁移兼容模式：迁移兼容采用的处理器功能集模式。</summary>
    /// <fielddoc>
    /// <meaning>迁移兼容采用的处理器功能集模式。</meaning>
    /// <source>Msvm_ProcessorSettingData，读取或派生 LimitProcessorFeaturesMode。</source>
    /// <references><see cref="VmProcessorService"/>。</references>
    /// <modification kind="supported" input="newSettings.CompatibilityForMigrationMode" submission="明确选择字段"><see cref="M:ExHyperV.Services.VmProcessorService.SetVmProcessorAsync(System.Guid,ExHyperV.Models.VmProcessorSettings,System.Collections.Generic.IReadOnlyCollection{System.String})"/></modification>
    /// </fielddoc>
    public static readonly VmQueryField<VmMigrationCompatibilityMode> CompatibilityForMigrationMode = new("Processor.CompatibilityForMigrationMode", "迁移兼容模式", "处理器", VmQueryCollection.VirtualMachine, "Processor", "LimitProcessorFeaturesMode");

    /// <summary>旧系统兼容：是否限制来宾可见的 CPUID 功能，以兼容较旧的操作系统。</summary>
    /// <fielddoc>
    /// <meaning>是否限制来宾可见的 CPUID 功能，以兼容较旧的操作系统。</meaning>
    /// <source>Msvm_ProcessorSettingData，读取或派生 LimitCPUID。</source>
    /// <references><see cref="VmProcessorService"/>。</references>
    /// <modification kind="supported" input="newSettings.CompatibilityForOlderOperatingSystemsEnabled" submission="明确选择字段"><see cref="M:ExHyperV.Services.VmProcessorService.SetVmProcessorAsync(System.Guid,ExHyperV.Models.VmProcessorSettings,System.Collections.Generic.IReadOnlyCollection{System.String})"/></modification>
    /// <values kind="options"><c>true</c>：启用旧系统兼容限制；<c>false</c>：关闭。</values>
    /// </fielddoc>
    public static readonly VmQueryField<bool> CompatibilityForOlderOperatingSystemsEnabled = new("Processor.CompatibilityForOlderOperatingSystemsEnabled", "旧系统兼容", "处理器", VmQueryCollection.VirtualMachine, "Processor", "LimitCPUID");

    /// <summary>同时多线程模式：向来宾呈现的同时多线程模式。</summary>
    /// <fielddoc>
    /// <meaning>向来宾呈现的同时多线程模式。</meaning>
    /// <source>Msvm_ProcessorSettingData，读取或派生 HwThreadsPerCore。</source>
    /// <references><see cref="VmProcessorService"/>。</references>
    /// <modification kind="supported" input="newSettings.SmtMode" submission="明确选择字段"><see cref="M:ExHyperV.Services.VmProcessorService.SetVmProcessorAsync(System.Guid,ExHyperV.Models.VmProcessorSettings,System.Collections.Generic.IReadOnlyCollection{System.String})"/></modification>
    /// </fielddoc>
    public static readonly VmQueryField<SmtMode> SmtMode = new("Processor.SmtMode", "同时多线程模式", "处理器", VmQueryCollection.VirtualMachine, "Processor", "HwThreadsPerCore", o => Convert.ToUInt64(o) switch { 0 => Models.SmtMode.Inherit, 1 => Models.SmtMode.SingleThread, _ => Models.SmtMode.MultiThread });

    /// <summary>隐藏推测执行缓解控制：是否向来宾隐藏用于缓解推测执行侧信道漏洞的处理器控制能力。</summary>
    /// <fielddoc label="隐藏推测执行缓解控制">
    /// <meaning>开启后，Hyper-V 向来宾隐藏用于缓解推测执行侧信道漏洞的处理器控制能力，例如 IBRS、IBPB 和 STIBP。来宾可能据此采用不同的缓解策略，具体影响取决于来宾系统和工作负载。</meaning>
    /// <source>Msvm_ProcessorSettingData，读取或派生 DisableSpeculationControls。</source>
    /// <references><see cref="VmProcessorService"/>；<see href="https://learn.microsoft.com/en-us/virtualization/api/hcs/schemareference#virtualmachine_processor">微软处理器控制能力说明</see>。</references>
    /// <modification kind="supported" input="newSettings.DisableSpeculationControls" submission="明确选择字段"><see cref="M:ExHyperV.Services.VmProcessorService.SetVmProcessorAsync(System.Guid,ExHyperV.Models.VmProcessorSettings,System.Collections.Generic.IReadOnlyCollection{System.String})"/></modification>
    /// <values kind="options"><c>true</c>：隐藏这些控制能力；<c>false</c>：不额外隐藏。</values>
    /// </fielddoc>
    public static readonly VmQueryField<bool> DisableSpeculationControls = new("Processor.DisableSpeculationControls", "禁用推测控制", "处理器", VmQueryCollection.VirtualMachine, "Processor", "DisableSpeculationControls");

    /// <summary>报告虚拟机的架构化性能监测配置。它与通用 PMU 配置分别记录，具体开放的计数器和事件取决于宿主处理器。</summary>
    /// <fielddoc>
    /// <meaning>报告虚拟机的架构化性能监测配置。它与通用 PMU 配置分别记录，具体开放的计数器和事件取决于宿主处理器。</meaning>
    /// <source>Msvm_ProcessorSettingData，读取或派生 EnablePerfmonArchPmu。</source>
    /// <references><see cref="VmProcessorService"/>。</references>
    /// <modification kind="supported" input="newSettings.EnablePerfmonArchPmu" submission="明确选择字段"><see cref="M:ExHyperV.Services.VmProcessorService.SetVmProcessorAsync(System.Guid,ExHyperV.Models.VmProcessorSettings,System.Collections.Generic.IReadOnlyCollection{System.String})"/></modification>
    /// <values kind="options"><c>true</c>：开放体系结构 PMU；<c>false</c>：不开放。</values>
    /// </fielddoc>
    public static readonly VmQueryField<bool> EnablePerfmonArchPmu = new("Processor.EnablePerfmonArchPmu", "架构性能监测", "处理器", VmQueryCollection.VirtualMachine, "Processor", "EnablePerfmonArchPmu");

    /// <summary>允许来宾读取 ACNT/MCNT（APERF/MPERF）频率反馈计数器。来宾可用两者在一段时间内的增量比值估算处理器有效运行频率。</summary>
    /// <fielddoc>
    /// <meaning>允许来宾读取 ACNT/MCNT（APERF/MPERF）频率反馈计数器。来宾可用两者在一段时间内的增量比值估算处理器有效运行频率。</meaning>
    /// <source>Msvm_ProcessorSettingData，读取或派生 AllowAcountMcount。</source>
    /// <references><see cref="VmProcessorService"/>。</references>
    /// <modification kind="supported" input="newSettings.AllowAcountMcount" submission="明确选择字段"><see cref="M:ExHyperV.Services.VmProcessorService.SetVmProcessorAsync(System.Guid,ExHyperV.Models.VmProcessorSettings,System.Collections.Generic.IReadOnlyCollection{System.String})"/></modification>
    /// <values kind="options"><c>true</c>：允许访问 ACNT/MCNT；<c>false</c>：不允许。</values>
    /// </fielddoc>
    public static readonly VmQueryField<bool> AllowAcountMcount = new("Processor.AllowAcountMcount", "活动计数器", "处理器", VmQueryCollection.VirtualMachine, "Processor", "AllowAcountMcount");

    /// <summary>处理器品牌字符串：向来宾公开的处理器品牌名称字符串。</summary>
    /// <fielddoc>
    /// <meaning>向来宾公开的处理器品牌名称字符串。</meaning>
    /// <source>Msvm_ProcessorSettingData，读取或派生 CpuBrandString。</source>
    /// <references><see cref="VmProcessorService"/>。</references>
    /// <modification kind="supported" input="newSettings.CpuBrandString" submission="明确选择字段"><see cref="M:ExHyperV.Services.VmProcessorService.SetVmProcessorAsync(System.Guid,ExHyperV.Models.VmProcessorSettings,System.Collections.Generic.IReadOnlyCollection{System.String})"/></modification>
    /// </fielddoc>
    public static readonly VmQueryField<string> CpuBrandString = new("Processor.CpuBrandString", "处理器品牌字符串", "处理器", VmQueryCollection.VirtualMachine, "Processor", "CpuBrandString");

    /// <summary>中断控制器模式：向来宾呈现的 APIC 中断控制器模式。</summary>
    /// <fielddoc>
    /// <meaning>虚拟处理器的 APIC 中断控制器模式，影响中断投递、处理器编号和拓扑容量。该字段保存配置选项；自动模式由 Hyper-V 在启动时根据虚拟机配置与宿主能力选择实际模式。</meaning>
    /// <source>Msvm_ProcessorSettingData，读取或派生 ApicMode。</source>
    /// <references><see cref="VmProcessorService"/>。</references>
    /// <modification kind="supported" input="newSettings.ApicMode" submission="明确选择字段"><see cref="M:ExHyperV.Services.VmProcessorService.SetVmProcessorAsync(System.Guid,ExHyperV.Models.VmProcessorSettings,System.Collections.Generic.IReadOnlyCollection{System.String})"/></modification>
    /// </fielddoc>
    public static readonly VmQueryField<VmApicMode> ApicMode = new("Processor.ApicMode", "中断控制器模式", "处理器", VmQueryCollection.VirtualMachine, "Processor", "ApicMode");

    /// <summary>三级缓存路数：向来宾呈现的三级缓存相联路数；0 表示采用默认配置。</summary>
    /// <fielddoc>
    /// <meaning>向来宾呈现的三级缓存相联路数。</meaning>
    /// <source>Msvm_ProcessorSettingData，读取或派生 L3CacheWays。</source>
    /// <references><see cref="VmProcessorService"/>。</references>
    /// <modification kind="supported" input="newSettings.L3CacheWays" submission="明确选择字段"><see cref="M:ExHyperV.Services.VmProcessorService.SetVmProcessorAsync(System.Guid,ExHyperV.Models.VmProcessorSettings,System.Collections.Generic.IReadOnlyCollection{System.String})"/></modification>
    /// <values kind="format">整数，单位为路；<c>0</c> 表示采用默认配置。</values>
    /// </fielddoc>
    public static readonly VmQueryField<uint> L3CacheWays = new("Processor.L3CacheWays", "三级缓存路数", "处理器", VmQueryCollection.VirtualMachine, "Processor", "L3CacheWays");

    /// <summary>三级缓存处理器分布策略：虚拟处理器在虚拟三级缓存域之间的分布策略。</summary>
    /// <fielddoc>
    /// <meaning>虚拟处理器在虚拟三级缓存域之间的分布策略。</meaning>
    /// <source>Msvm_ProcessorSettingData，读取或派生 L3ProcessorDistributionPolicy。</source>
    /// <references><see cref="VmProcessorService"/>。</references>
    /// <modification kind="supported" input="newSettings.L3DistributionPolicy" submission="明确选择字段"><see cref="M:ExHyperV.Services.VmProcessorService.SetVmProcessorAsync(System.Guid,ExHyperV.Models.VmProcessorSettings,System.Collections.Generic.IReadOnlyCollection{System.String})"/></modification>
    /// </fielddoc>
    public static readonly VmQueryField<L3DistributionPolicy> L3DistributionPolicy = new("Processor.L3DistributionPolicy", "三级缓存处理器分布策略", "处理器", VmQueryCollection.VirtualMachine, "Processor", "L3ProcessorDistributionPolicy");

    /// <summary>大页拆分策略：二级地址转换使用的大页拆分缓解策略。</summary>
    /// <fielddoc>
    /// <meaning>二级地址转换使用的大页拆分缓解策略。</meaning>
    /// <source>Msvm_ProcessorSettingData，读取或派生 EnablePageShattering。</source>
    /// <references><see cref="VmProcessorService"/>。</references>
    /// <modification kind="supported" input="newSettings.PageShatterMode" submission="明确选择字段"><see cref="M:ExHyperV.Services.VmProcessorService.SetVmProcessorAsync(System.Guid,ExHyperV.Models.VmProcessorSettings,System.Collections.Generic.IReadOnlyCollection{System.String})"/></modification>
    /// </fielddoc>
    public static readonly VmQueryField<PageShatterMode> PageShatterMode = new("Processor.PageShatterMode", "大页拆分策略", "处理器", VmQueryCollection.VirtualMachine, "Processor", "EnablePageShattering");

    /// <summary>处理器频率上限：为虚拟机配置的处理器频率上限，单位为 MHz。</summary>
    /// <fielddoc>
    /// <meaning>为虚拟机配置的处理器频率上限。</meaning>
    /// <source>Msvm_ProcessorSettingData，读取或派生 PerfCpuFreqCapMhz。</source>
    /// <references><see cref="VmProcessorService"/>。</references>
    /// <modification kind="supported" input="newSettings.PerfCpuFreqCapMhz" submission="明确选择字段"><see cref="M:ExHyperV.Services.VmProcessorService.SetVmProcessorAsync(System.Guid,ExHyperV.Models.VmProcessorSettings,System.Collections.Generic.IReadOnlyCollection{System.String})"/></modification>
    /// <values kind="format">整数，单位为 MHz；例如 <c>2400</c> 表示 2.4 GHz。</values>
    /// </fielddoc>
    public static readonly VmQueryField<uint> PerfCpuFreqCapMhz = new("Processor.PerfCpuFreqCapMhz", "处理器频率上限", "处理器", VmQueryCollection.VirtualMachine, "Processor", "PerfCpuFreqCapMhz");

    /// <summary>处理器频率下限：为虚拟机配置的处理器频率下限，单位为 MHz。</summary>
    /// <fielddoc>
    /// <meaning>为虚拟机配置的处理器频率下限。</meaning>
    /// <source>Msvm_ProcessorSettingData，读取或派生 PerfCpuFreqMinMhz。</source>
    /// <references><see cref="VmProcessorService"/>。</references>
    /// <modification kind="supported" input="newSettings.PerfCpuFreqMinMhz" submission="明确选择字段"><see cref="M:ExHyperV.Services.VmProcessorService.SetVmProcessorAsync(System.Guid,ExHyperV.Models.VmProcessorSettings,System.Collections.Generic.IReadOnlyCollection{System.String})"/></modification>
    /// <values kind="format">整数，单位为 MHz；例如 <c>2400</c> 表示 2.4 GHz。</values>
    /// </fielddoc>
    public static readonly VmQueryField<uint> PerfCpuFreqMinMhz = new("Processor.PerfCpuFreqMinMhz", "处理器频率下限", "处理器", VmQueryCollection.VirtualMachine, "Processor", "PerfCpuFreqMinMhz");

    /// <summary>处理器期望频率：为虚拟机配置的期望处理器频率，单位为 MHz。</summary>
    /// <fielddoc>
    /// <meaning>向宿主请求的处理器目标频率。设为 0 时使用自主性能选择，能耗性能偏好和自主活动窗口参与这一模式。</meaning>
    /// <source>Msvm_ProcessorSettingData，读取或派生 PerfCpuFreqDesiredMhz。</source>
    /// <references><see cref="VmProcessorService"/>。</references>
    /// <modification kind="supported" input="newSettings.PerfCpuFreqDesiredMhz" submission="明确选择字段"><see cref="M:ExHyperV.Services.VmProcessorService.SetVmProcessorAsync(System.Guid,ExHyperV.Models.VmProcessorSettings,System.Collections.Generic.IReadOnlyCollection{System.String})"/></modification>
    /// <values kind="format"><c>0</c>：自主性能选择；正整数：目标频率，单位为 MHz，例如 <c>2400</c> 表示 2.4 GHz。</values>
    /// </fielddoc>
    public static readonly VmQueryField<uint> PerfCpuFreqDesiredMhz = new("Processor.PerfCpuFreqDesiredMhz", "处理器期望频率", "处理器", VmQueryCollection.VirtualMachine, "Processor", "PerfCpuFreqDesiredMhz");

    /// <summary>能耗性能偏好：处理器自主调频时，在性能与能效之间取舍的偏好。</summary>
    /// <fielddoc>
    /// <meaning>处理器自主调频时，在性能与能效之间取舍的偏好（EPP）。由支持自主性能控制（CPPC）的宿主采用，处理器期望频率须设为 0。</meaning>
    /// <values kind="format"><c>0</c>～<c>255</c> 的整数。<c>0</c> 最偏向性能，<c>255</c> 最偏向节能；中间值表示两者之间的取舍，数值越大越偏向节能。</values>
    /// <source>Msvm_ProcessorSettingData，读取或派生 PerfCpuEnergyPerformancePreference。</source>
    /// <verification>COM 研究 25-frequency-epp.md：29639.1000 的 Windows PPM 将 0～255 换算为 0～100；未设置时采用宿主默认 EPP。完整 CPPC 参数组通过 VMWP 的 Azure Feature Set 分支提交。架构范围为 0～255，可见 WMI/VMWP 入口未实施同等范围校验。</verification>
    /// <references><see cref="VmProcessorService"/>。</references>
    /// <modification kind="supported" input="newSettings.PerfCpuEnergyPerformancePreference" submission="明确选择字段"><see cref="M:ExHyperV.Services.VmProcessorService.SetVmProcessorAsync(System.Guid,ExHyperV.Models.VmProcessorSettings,System.Collections.Generic.IReadOnlyCollection{System.String})"/></modification>
    /// </fielddoc>
    public static readonly VmQueryField<uint> PerfCpuEnergyPerformancePreference = new("Processor.PerfCpuEnergyPerformancePreference", "能耗性能偏好", "处理器", VmQueryCollection.VirtualMachine, "Processor", "PerfCpuEnergyPerformancePreference");

    /// <summary>自主活动窗口：向宿主提交的自主调频活动窗口提示。</summary>
    /// <fielddoc>
    /// <meaning>向宿主提交的自主调频活动窗口提示，用于表达性能选择时观察负载活动的时间尺度。处理器期望频率须设为 0，具体解释由宿主的处理器性能控制实现决定。</meaning>
    /// <values kind="format">32 位无符号原始值。时间单位、编码和有效范围尚未确认。显式 <c>0</c> 作为独立请求值传递；未设置时采用宿主默认窗口。</values>
    /// <source>Msvm_ProcessorSettingData，读取或派生 PerfCpuAutonomousActivityWindow。</source>
    /// <verification>COM 研究 26-frequency-activity-window.md 与 frequency-document-review-2026-08-08.md：29639.1000 的 VMWP、VID 和可见 PPM 路径原样传递窗口值；最终平台回调尚未闭环。完整参数组通过 Azure Feature Set 分支提交。Windows 宿主电源设置 PerfAutonomousWindow 的微秒单位，以及硬件 HWP 的 10 位格式，均不能直接作为此 VM 字段的输入契约。</verification>
    /// <references><see cref="VmProcessorService"/>。</references>
    /// <modification kind="supported" input="newSettings.PerfCpuAutonomousActivityWindow" submission="明确选择字段"><see cref="M:ExHyperV.Services.VmProcessorService.SetVmProcessorAsync(System.Guid,ExHyperV.Models.VmProcessorSettings,System.Collections.Generic.IReadOnlyCollection{System.String})"/></modification>
    /// </fielddoc>
    public static readonly VmQueryField<uint> PerfCpuAutonomousActivityWindow = new("Processor.PerfCpuAutonomousActivityWindow", "自主活动窗口", "处理器", VmQueryCollection.VirtualMachine, "Processor", "PerfCpuAutonomousActivityWindow");

    /// <summary>忽略宿主最大频率：虚拟机的处理器频率策略是否忽略宿主最大频率限制。</summary>
    /// <fielddoc>
    /// <meaning>虚拟机的处理器频率策略是否忽略宿主最大频率限制。</meaning>
    /// <source>Msvm_ProcessorSettingData，读取或派生 PerfCpuIgnoreHostMaxFrequency。</source>
    /// <references><see cref="VmProcessorService"/>。</references>
    /// <modification kind="supported" input="newSettings.PerfCpuIgnoreHostMaxFrequency" submission="明确选择字段"><see cref="M:ExHyperV.Services.VmProcessorService.SetVmProcessorAsync(System.Guid,ExHyperV.Models.VmProcessorSettings,System.Collections.Generic.IReadOnlyCollection{System.String})"/></modification>
    /// <values kind="options"><c>true</c>：忽略宿主最大频率限制；<c>false</c>：遵循限制。</values>
    /// </fielddoc>
    public static readonly VmQueryField<bool> PerfCpuIgnoreHostMaxFrequency = new("Processor.PerfCpuIgnoreHostMaxFrequency", "忽略宿主最大频率", "处理器", VmQueryCollection.VirtualMachine, "Processor", "PerfCpuIgnoreHostMaxFrequency");

    /// <summary>向来宾开放处理器性能计数器，供来宾内的分析工具统计指令、周期和缓存事件，定位程序性能瓶颈。PEBS 和 IPT 需要同时启用 PMU。</summary>
    /// <fielddoc>
    /// <meaning>向来宾开放处理器性能计数器，供来宾内的分析工具统计指令、周期和缓存事件，定位程序性能瓶颈。PEBS 和 IPT 需要同时启用 PMU。</meaning>
    /// <source>Msvm_ProcessorSettingData，读取或派生 EnablePerfmonPmu。</source>
    /// <references><see cref="VmProcessorService"/>。</references>
    /// <modification kind="supported" input="newSettings.EnablePerfmonPmu" submission="明确选择字段"><see cref="M:ExHyperV.Services.VmProcessorService.SetVmProcessorAsync(System.Guid,ExHyperV.Models.VmProcessorSettings,System.Collections.Generic.IReadOnlyCollection{System.String})"/></modification>
    /// <values kind="options"><c>true</c>：开放 PMU；<c>false</c>：不开放。</values>
    /// </fielddoc>
    public static readonly VmQueryField<bool> EnablePerfmonPmu = new("Processor.EnablePerfmonPmu", "性能监测单元", "处理器", VmQueryCollection.VirtualMachine, "Processor", "EnablePerfmonPmu");

    /// <summary>向来宾开放最后分支记录（LBR），保存最近执行的分支跳转，供性能分析和调试工具还原代码执行路径。</summary>
    /// <fielddoc>
    /// <meaning>向来宾开放最后分支记录（LBR），保存最近执行的分支跳转，供性能分析和调试工具还原代码执行路径。</meaning>
    /// <source>Msvm_ProcessorSettingData，读取或派生 EnablePerfmonLbr。</source>
    /// <references><see cref="VmProcessorService"/>。</references>
    /// <modification kind="supported" input="newSettings.EnablePerfmonLbr" submission="明确选择字段"><see cref="M:ExHyperV.Services.VmProcessorService.SetVmProcessorAsync(System.Guid,ExHyperV.Models.VmProcessorSettings,System.Collections.Generic.IReadOnlyCollection{System.String})"/></modification>
    /// <values kind="options"><c>true</c>：开放 LBR；<c>false</c>：不开放。</values>
    /// </fielddoc>
    public static readonly VmQueryField<bool> EnablePerfmonLbr = new("Processor.EnablePerfmonLbr", "最近分支记录", "处理器", VmQueryCollection.VirtualMachine, "Processor", "EnablePerfmonLbr");

    /// <summary>向来宾开放精确事件采样（PEBS），使性能分析工具把计数器采样关联到具体指令位置。需要同时启用 PMU。</summary>
    /// <fielddoc>
    /// <meaning>向来宾开放精确事件采样（PEBS），使性能分析工具把计数器采样关联到具体指令位置。需要同时启用 PMU。</meaning>
    /// <source>Msvm_ProcessorSettingData，读取或派生 EnablePerfmonPebs。</source>
    /// <references><see cref="VmProcessorService"/>。</references>
    /// <modification kind="supported" input="newSettings.EnablePerfmonPebs" submission="明确选择字段"><see cref="M:ExHyperV.Services.VmProcessorService.SetVmProcessorAsync(System.Guid,ExHyperV.Models.VmProcessorSettings,System.Collections.Generic.IReadOnlyCollection{System.String})"/></modification>
    /// <values kind="options"><c>true</c>：开放 PEBS；<c>false</c>：不开放。</values>
    /// </fielddoc>
    public static readonly VmQueryField<bool> EnablePerfmonPebs = new("Processor.EnablePerfmonPebs", "精确事件采样", "处理器", VmQueryCollection.VirtualMachine, "Processor", "EnablePerfmonPebs");

    /// <summary>向来宾开放 Intel Processor Trace（IPT），记录程序控制流，供跟踪和调试工具重建执行过程。需要同时启用 PMU。</summary>
    /// <fielddoc>
    /// <meaning>向来宾开放 Intel Processor Trace（IPT），记录程序控制流，供跟踪和调试工具重建执行过程。需要同时启用 PMU。</meaning>
    /// <source>Msvm_ProcessorSettingData，读取或派生 EnablePerfmonIpt。</source>
    /// <references><see cref="VmProcessorService"/>。</references>
    /// <modification kind="supported" input="newSettings.EnablePerfmonIpt" submission="明确选择字段"><see cref="M:ExHyperV.Services.VmProcessorService.SetVmProcessorAsync(System.Guid,ExHyperV.Models.VmProcessorSettings,System.Collections.Generic.IReadOnlyCollection{System.String})"/></modification>
    /// <values kind="options"><c>true</c>：开放 IPT；<c>false</c>：不开放。</values>
    /// </fielddoc>
    public static readonly VmQueryField<bool> EnablePerfmonIpt = new("Processor.EnablePerfmonIpt", "处理器指令跟踪", "处理器", VmQueryCollection.VirtualMachine, "Processor", "EnablePerfmonIpt");

    /// <summary>硬件隔离扩展：是否为虚拟机启用硬件隔离扩展。</summary>
    /// <fielddoc>
    /// <meaning>是否为虚拟机启用硬件隔离扩展。</meaning>
    /// <source>Msvm_ProcessorSettingData，读取或派生 ExtendedVirtualizationExtensions。</source>
    /// <references><see cref="VmProcessorService"/>。</references>
    /// <modification kind="supported" input="newSettings.HardwareIsolationExtensionsEnabled" submission="明确选择字段"><see cref="M:ExHyperV.Services.VmProcessorService.SetVmProcessorAsync(System.Guid,ExHyperV.Models.VmProcessorSettings,System.Collections.Generic.IReadOnlyCollection{System.String})"/></modification>
    /// <values kind="options"><c>true</c>：启用硬件隔离扩展；<c>false</c>：关闭。</values>
    /// </fielddoc>
    public static readonly VmQueryField<bool> HardwareIsolationExtensionsEnabled = new("Processor.HardwareIsolationExtensionsEnabled", "硬件隔离扩展", "处理器", VmQueryCollection.VirtualMachine, "Processor", "ExtendedVirtualizationExtensions", o => Convert.ToUInt32(o) != 0);

    /// <summary>硬件隔离来宾数量上限：配置允许的硬件隔离来宾数量上限。</summary>
    /// <fielddoc>
    /// <meaning>配置允许的硬件隔离来宾数量上限。</meaning>
    /// <source>Msvm_ProcessorSettingData，读取或派生 MaxHwIsolatedGuests。</source>
    /// <references><see cref="VmProcessorService"/>。</references>
    /// <modification kind="supported" input="newSettings.MaxHwIsolatedGuests" submission="明确选择字段"><see cref="M:ExHyperV.Services.VmProcessorService.SetVmProcessorAsync(System.Guid,ExHyperV.Models.VmProcessorSettings,System.Collections.Generic.IReadOnlyCollection{System.String})"/></modification>
    /// <values kind="format">整数数量，单位为个。</values>
    /// </fielddoc>
    public static readonly VmQueryField<uint> MaxHwIsolatedGuests = new("Processor.MaxHwIsolatedGuests", "硬件隔离来宾数量上限", "处理器", VmQueryCollection.VirtualMachine, "Processor", "MaxHwIsolatedGuests");

    /// <summary>每插槽处理器簇上限：每个虚拟处理器插槽可包含的处理器簇数量上限。</summary>
    /// <fielddoc>
    /// <meaning>每个虚拟处理器插槽可包含的处理器簇数量上限。</meaning>
    /// <source>Msvm_ProcessorSettingData，读取或派生 MaxClusterCountPerSocket。</source>
    /// <references><see cref="VmProcessorService"/>。</references>
    /// <modification kind="supported" input="newSettings.MaxClusterCountPerSocket" submission="明确选择字段"><see cref="M:ExHyperV.Services.VmProcessorService.SetVmProcessorAsync(System.Guid,ExHyperV.Models.VmProcessorSettings,System.Collections.Generic.IReadOnlyCollection{System.String})"/></modification>
    /// <values kind="format">整数数量，单位为个。</values>
    /// </fielddoc>
    public static readonly VmQueryField<uint> MaxClusterCountPerSocket = new("Processor.MaxClusterCountPerSocket", "每插槽处理器簇上限", "处理器", VmQueryCollection.VirtualMachine, "Processor", "MaxClusterCountPerSocket");

    /// <summary>每三级缓存域处理器上限：每个虚拟三级缓存域可包含的虚拟处理器数量上限。</summary>
    /// <fielddoc>
    /// <meaning>每个虚拟三级缓存域可包含的虚拟处理器数量上限。</meaning>
    /// <source>Msvm_ProcessorSettingData，读取或派生 MaxProcessorCountPerL3。</source>
    /// <references><see cref="VmProcessorService"/>。</references>
    /// <modification kind="supported" input="newSettings.MaxProcessorCountPerL3" submission="明确选择字段"><see cref="M:ExHyperV.Services.VmProcessorService.SetVmProcessorAsync(System.Guid,ExHyperV.Models.VmProcessorSettings,System.Collections.Generic.IReadOnlyCollection{System.String})"/></modification>
    /// <values kind="format">整数数量，单位为个。</values>
    /// </fielddoc>
    public static readonly VmQueryField<uint> MaxProcessorCountPerL3 = new("Processor.MaxProcessorCountPerL3", "每三级缓存域处理器上限", "处理器", VmQueryCollection.VirtualMachine, "Processor", "MaxProcessorCountPerL3");

    /// <summary>每 NUMA 节点处理器上限：每个虚拟 NUMA 节点可包含的虚拟处理器数量上限。</summary>
    /// <fielddoc>
    /// <meaning>每个虚拟 NUMA 节点可包含的虚拟处理器数量上限。</meaning>
    /// <source>Msvm_ProcessorSettingData，读取或派生 MaxProcessorsPerNumaNode。</source>
    /// <references><see cref="VmProcessorService"/>。</references>
    /// <modification kind="supported" input="newSettings.MaxProcessorsPerNumaNode" submission="明确选择字段"><see cref="M:ExHyperV.Services.VmProcessorService.SetVmProcessorAsync(System.Guid,ExHyperV.Models.VmProcessorSettings,System.Collections.Generic.IReadOnlyCollection{System.String})"/></modification>
    /// <values kind="format">整数数量，单位为个。</values>
    /// </fielddoc>
    public static readonly VmQueryField<ulong> MaxProcessorsPerNumaNode = new("Processor.MaxProcessorsPerNumaNode", "每 NUMA 节点处理器上限", "处理器", VmQueryCollection.VirtualMachine, "Processor", "MaxProcessorsPerNumaNode");

    /// <summary>每插槽 NUMA 节点上限：每个虚拟处理器插槽可包含的 NUMA 节点数量上限。</summary>
    /// <fielddoc>
    /// <meaning>每个虚拟处理器插槽可包含的 NUMA 节点数量上限。</meaning>
    /// <source>Msvm_ProcessorSettingData，读取或派生 MaxNumaNodesPerSocket。</source>
    /// <references><see cref="VmProcessorService"/>。</references>
    /// <modification kind="supported" input="newSettings.MaxNumaNodesPerSocket" submission="明确选择字段"><see cref="M:ExHyperV.Services.VmProcessorService.SetVmProcessorAsync(System.Guid,ExHyperV.Models.VmProcessorSettings,System.Collections.Generic.IReadOnlyCollection{System.String})"/></modification>
    /// <values kind="format">整数数量，单位为个。</values>
    /// </fielddoc>
    public static readonly VmQueryField<ulong> MaxNumaNodesPerSocket = new("Processor.MaxNumaNodesPerSocket", "每插槽 NUMA 节点上限", "处理器", VmQueryCollection.VirtualMachine, "Processor", "MaxNumaNodesPerSocket");

    /// <summary>来宾可使用的物理地址位数，限制来宾物理地址空间的范围。</summary>
    /// <fielddoc>
    /// <meaning>来宾可使用的物理地址位数，限制来宾物理地址空间的范围。</meaning>
    /// <source>Msvm_ProcessorSettingData，读取或派生 PhysicalAddressWidth。</source>
    /// <references><see cref="VmProcessorService"/>。</references>
    /// <modification kind="supported" input="newSettings.PhysicalAddressWidth" submission="明确选择字段"><see cref="M:ExHyperV.Services.VmProcessorService.SetVmProcessorAsync(System.Guid,ExHyperV.Models.VmProcessorSettings,System.Collections.Generic.IReadOnlyCollection{System.String})"/></modification>
    /// <values kind="format">整数，单位为位（bit）。</values>
    /// </fielddoc>
    public static readonly VmQueryField<uint> PhysicalAddressWidth = new("Processor.PhysicalAddressWidth", "物理地址宽度", "处理器", VmQueryCollection.VirtualMachine, "Processor", "PhysicalAddressWidth");

    /// <summary>本地外设中断模式：虚拟机使用的本地外设中断（LPI）模式。</summary>
    /// <fielddoc>
    /// <meaning>虚拟机使用的本地外设中断（LPI）模式。</meaning>
    /// <source>Msvm_ProcessorSettingData，读取或派生 LpiMode。</source>
    /// <references><see cref="VmProcessorService"/>。</references>
    /// <modification kind="supported" input="newSettings.LpiMode" submission="明确选择字段"><see cref="M:ExHyperV.Services.VmProcessorService.SetVmProcessorAsync(System.Guid,ExHyperV.Models.VmProcessorSettings,System.Collections.Generic.IReadOnlyCollection{System.String})"/></modification>
    /// </fielddoc>
    public static readonly VmQueryField<LpiMode> LpiMode = new("Processor.LpiMode", "本地外设中断模式", "处理器", VmQueryCollection.VirtualMachine, "Processor", "LpiMode");

    /// <summary>处理器使用率：本次取得的各虚拟处理器使用率的算术平均值，100 表示 100%。</summary>
    /// <fielddoc>
    /// <meaning>本次采样中各虚拟处理器使用率的算术平均值。</meaning>
    /// <applicability>适用于运行中的虚拟机；全部预期虚拟处理器样本有效时生成平均值。</applicability>
    /// <source>Windows 格式化虚拟处理器性能提供者，按唯一虚拟机名称与处理器编号关联，读取或派生 UsagePercent。</source>
    /// <absence>计数器名称对应多个同名虚拟机时返回 Ambiguous；其他缺测状态随样本返回。</absence>
    /// <references><see cref="VmProcessorService"/>。</references>
    /// <modification kind="readonly" />
    /// <values kind="format">百分数；例如 <c>25</c> 表示 25%。</values>
    /// </fielddoc>
    public static readonly VmQueryField<double> UsagePercent = new("Processor.UsagePercent", "处理器使用率", "处理器", VmQueryCollection.VirtualMachine, "ProcessorPerformance", "UsagePercent");

    internal static readonly VmQueryField[] Items = [Count, Reservation, Limit, RelativeWeight, ExposeVirtualizationExtensions, EnableHostResourceProtection, CompatibilityForMigrationEnabled, CompatibilityForMigrationMode, CompatibilityForOlderOperatingSystemsEnabled, SmtMode, DisableSpeculationControls, EnablePerfmonArchPmu, AllowAcountMcount, CpuBrandString, ApicMode, L3CacheWays, L3DistributionPolicy, PageShatterMode, PerfCpuFreqCapMhz, PerfCpuFreqMinMhz, PerfCpuFreqDesiredMhz, PerfCpuEnergyPerformancePreference, PerfCpuAutonomousActivityWindow, PerfCpuIgnoreHostMaxFrequency, EnablePerfmonPmu, EnablePerfmonLbr, EnablePerfmonPebs, EnablePerfmonIpt, HardwareIsolationExtensionsEnabled, MaxHwIsolatedGuests, MaxClusterCountPerSocket, MaxProcessorCountPerL3, MaxProcessorsPerNumaNode, MaxNumaNodesPerSocket, PhysicalAddressWidth, LpiMode, UsagePercent];
}
