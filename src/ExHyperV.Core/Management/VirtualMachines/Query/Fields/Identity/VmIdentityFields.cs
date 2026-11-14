using ExHyperV.Models;
using ExHyperV.Tools;

namespace ExHyperV.Services;

/// <summary>基本信息的独立可查询字段；可以任意选择其中一项，不要求整组读取或返回。</summary>
public static class VmIdentityFields
{
    /// <summary>用于指定这台虚拟机的 GUID；修改显示名称不会改变它。</summary>
    /// <fielddoc>
    /// <meaning>这台虚拟机的唯一标识（GUID），用于查询、修改配置和电源操作时指定目标。创建或导入时确定，重命名不会改变它。</meaning>
    /// <values kind="format">
    /// <para>常用文本格式为 36 个字符：32 个十六进制字符（<c>0–9</c>、<c>a–f</c>，字母不区分大小写）与 4 个连字符，按 <c>8-4-4-4-12</c> 分组。</para>
    /// <para>例如 <c>6f0c2e91-8a45-4b37-9d60-c1257e3a8f42</c>。</para>
    /// </values>
    /// <source>读取 Msvm_SummaryInformation.Name，并解析为 Guid。</source>
    /// <references><see cref="VmQueryRow"/>；<see href="https://learn.microsoft.com/en-us/windows/win32/hyperv_v2/msvm-summaryinformation">微软的虚拟机摘要字段定义</see>。</references>
    /// <modification kind="readonly">在创建或导入时确定。</modification>
    /// </fielddoc>
    public static readonly VmQueryField<Guid> Id = new("Identity.Id", "身份", "基本信息", VmQueryCollection.VirtualMachine, "Summary", "Name", o => Guid.Parse(o.ToString()!));

    /// <summary>虚拟机在管理工具中显示的名称。</summary>
    /// <fielddoc>
    /// <meaning>Hyper-V 管理工具中的显示名称，可以修改，也允许重名。</meaning>
    /// <values kind="format">Unicode 文本，Hyper-V 规定名称最长 100 个字符；例如 <c>开发环境-01</c>。</values>
    /// <source>直接读取 Msvm_SummaryInformation.ElementName。</source>
    /// <references><see cref="Id"/>；<see cref="VmEditService.RenameVmAsync"/>。</references>
    /// <modification kind="supported" input="newName" submission="单项提交"><see cref="VmEditService.RenameVmAsync"/></modification>
    /// </fielddoc>
    public static readonly VmQueryField<string> Name = new("Identity.Name", "名称", "基本信息", VmQueryCollection.VirtualMachine, "Summary", "ElementName");

    /// <summary>查询时虚拟机的电源与执行状态。</summary>
    /// <fielddoc>
    /// <meaning>VMMS 维护的电源与执行状态，包括运行、关机、暂停、已保存及状态切换过程。</meaning>
    /// <source>读取 Msvm_SummaryInformation.EnabledState，由 Core 映射为 VmState。</source>
    /// <references><see cref="VmState"/>；<see cref="VmPowerAction"/>。</references>
    /// <modification kind="operation" submission="操作流程">可通过<see cref="M:ExHyperV.Services.VmPowerService.ExecuteControlActionAsync(System.Guid,ExHyperV.Models.VmPowerAction)">电源操作</see>以及虚拟机内部改变。</modification>
    /// </fielddoc>
    public static readonly VmQueryField<VmState> State = new("Identity.State", "运行状态", "基本信息", VmQueryCollection.VirtualMachine, "Summary", "EnabledState", o => VmMapper.MapState(Convert.ToUInt16(o)));

    /// <summary>Hyper-V 报告的虚拟机本次启动以来的时长。</summary>
    /// <fielddoc>
    /// <meaning>VMMS 维护的虚拟机运行计时，表示本次启动以来经过的时间。每次查询返回当时的计时值。</meaning>
    /// <values kind="format">非负时间间隔。例如 90 秒为 <c>00:01:30</c>，一天加 90 秒为 <c>1.00:01:30</c>。</values>
    /// <source>读取 Msvm_SummaryInformation.UpTime，并调用 TimeSpan.FromMilliseconds 转换。</source>
    /// <references><see cref="State"/>；<see href="https://learn.microsoft.com/en-us/windows/win32/hyperv_v2/msvm-summaryinformation">微软的 UpTime 定义</see>。</references>
    /// <modification kind="readonly" />
    /// </fielddoc>
    public static readonly VmQueryField<TimeSpan> Uptime = new("Identity.Uptime", "运行时长", "基本信息", VmQueryCollection.VirtualMachine, "Summary", "UpTime", o => TimeSpan.FromMilliseconds(Convert.ToUInt64(o)));

    /// <summary>创建虚拟机时选定的虚拟硬件代数。</summary>
    /// <fielddoc>
    /// <meaning>创建虚拟机时选定的硬件代数，决定虚拟固件、启动方式及可用设备类型。</meaning>
    /// <values kind="options">
    /// <list type="bullet">
    /// <item><c>1</c>：第一代，使用传统 BIOS 虚拟固件。</item>
    /// <item><c>2</c>：第二代，使用 UEFI 虚拟固件。</item>
    /// </list>
    /// </values>
    /// <source>读取当前 Msvm_VirtualSystemSettingData.VirtualSystemSubType，根据末尾的 :1 或 :2 得到代数。</source>
    /// <references><see cref="ConfigurationVersion"/>；<see href="https://learn.microsoft.com/en-us/windows-server/virtualization/hyper-v/plan/should-i-create-a-generation-1-or-2-virtual-machine-in-hyper-v">微软的虚拟机代数说明</see>。</references>
    /// <modification kind="readonly">创建时选定。</modification>
    /// </fielddoc>
    public static readonly VmQueryField<int> Generation = new("Identity.Generation", "虚拟机代数", "基本信息", VmQueryCollection.VirtualMachine, "Settings", "VirtualSystemSubType", o => o.ToString()!.EndsWith(":1") ? 1 : o.ToString()!.EndsWith(":2") ? 2 : throw new InvalidDataException("无法识别虚拟机代数。"));

    /// <summary>这台虚拟机使用的 Hyper-V 配置版本。</summary>
    /// <fielddoc>
    /// <meaning>这台虚拟机使用的配置版本。目标宿主需支持该版本才能使用此配置；下表列出各宿主支持的版本。</meaning>
    /// <values kind="format"><c>主版本号.次版本号</c>，两部分均为十进制整数，例如 <c>12.5</c>。</values>
    /// <source>直接读取当前 Msvm_VirtualSystemSettingData.Version。</source>
    /// <references><see cref="VmCreateService.GetSupportedVersionsAsync"/> 读取当前宿主支持的版本；<see cref="F:ExHyperV.Services.VmIdentityFields.ConfigurationFilePath">配置文件路径与格式</see>；<see href="https://learn.microsoft.com/en-us/windows-server/virtualization/hyper-v/deploy/upgrade-virtual-machine-version-in-hyper-v-on-windows-or-windows-server">微软的配置版本与升级说明</see>。</references>
    /// <modification kind="unavailable" />
    /// </fielddoc>
    /// <configurationversions />
    public static readonly VmQueryField<string> ConfigurationVersion = new("Identity.ConfigurationVersion", "配置版本", "基本信息", VmQueryCollection.VirtualMachine, "Settings", "Version");

    /// <summary>虚拟机 VMCX 配置文件（.vmcx）在宿主上的完整路径。</summary>
    /// <fielddoc>
    /// <meaning>虚拟机 VMCX 配置文件（.vmcx）在宿主上的完整路径。文件以二进制格式保存这台虚拟机的配置，包括处理器数量、内存设置、虚拟设备及磁盘挂接关系。Hyper-V 加载这些配置来构建虚拟机的硬件环境。</meaning>
    /// <values kind="format">Windows 完整文件路径，扩展名为 <c>.vmcx</c>。例如 <c>D:\VMs\Demo\Virtual Machines\example.vmcx</c>。</values>
    /// <source>读取 VMMS 的 ConfigurationFile；相对路径由 Core 结合 ConfigurationDataRoot 转换为完整路径。</source>
    /// <references><see cref="F:ExHyperV.Services.VmIdentityFields.ConfigurationVersion">配置版本与宿主 build</see>；<see cref="GuestStateFilePath"/>；<see cref="RuntimeStateFilePath"/>；<see href="https://learn.microsoft.com/en-us/windows/win32/hyperv_v2/msvm-virtualsystemsettingdata">微软的虚拟机配置字段定义</see>。</references>
    /// <modification kind="unavailable" />
    /// </fielddoc>
    public static readonly VmQueryField<string> ConfigurationFilePath = new("Identity.ConfigurationFilePath", "配置文件路径", "基本信息", VmQueryCollection.VirtualMachine, "VmFiles", "ConfigurationFilePath");

    /// <summary>虚拟机来宾状态文件（.vmgs）的完整路径。</summary>
    /// <fielddoc>
    /// <meaning>虚拟机 VMGS 来宾状态文件（.vmgs）在宿主上的完整路径。文件保存需要跨关机保留的固件与安全设备状态，例如 UEFI 固件变量、虚拟 TPM 的状态。虚拟机再次开机时会继续使用这些数据。</meaning>
    /// <values kind="format">Windows 完整文件路径，扩展名为 <c>.vmgs</c>。例如 <c>D:\VMs\Demo\Virtual Machines\example.vmgs</c>。</values>
    /// <source>读取 VMMS 的 GuestStateFile；相对路径由 Core 结合 GuestStateDataRoot 转换为完整路径。</source>
    /// <references><see cref="ConfigurationFilePath"/>；<see cref="RuntimeStateFilePath"/>。</references>
    /// <modification kind="unavailable" />
    /// </fielddoc>
    public static readonly VmQueryField<string> GuestStateFilePath = new("Identity.GuestStateFilePath", "来宾状态文件路径", "基本信息", VmQueryCollection.VirtualMachine, "VmFiles", "GuestStateFilePath");

    /// <summary>虚拟机运行状态文件（.vmrs）的完整路径。</summary>
    /// <fielddoc>
    /// <meaning>虚拟机 VMRS 运行状态文件（.vmrs）在宿主上的完整路径。文件用于保存虚拟机的执行现场，包括内存内容和虚拟设备状态。执行“保存”操作后，Hyper-V 通过这些状态恢复先前运行的系统和程序，让虚拟机从保存的位置继续运行。</meaning>
    /// <values kind="format">Windows 完整文件路径，扩展名为 <c>.vmrs</c>。例如 <c>D:\VMs\Demo\Virtual Machines\example.vmrs</c>。</values>
    /// <source>按 VMMS 的运行状态文件命名规则，由当前 .vmcx 配置文件的完整路径换用 .vmrs 扩展名。</source>
    /// <references><see cref="ConfigurationFilePath"/>；<see cref="GuestStateFilePath"/>。</references>
    /// <modification kind="unavailable" />
    /// </fielddoc>
    public static readonly VmQueryField<string> RuntimeStateFilePath = new("Identity.RuntimeStateFilePath", "运行状态文件路径", "基本信息", VmQueryCollection.VirtualMachine, "VmFiles", "RuntimeStateFilePath");

    internal static readonly VmQueryField[] Items = [Id, Name, State, Uptime, Generation, ConfigurationVersion, ConfigurationFilePath, GuestStateFilePath, RuntimeStateFilePath];
}
