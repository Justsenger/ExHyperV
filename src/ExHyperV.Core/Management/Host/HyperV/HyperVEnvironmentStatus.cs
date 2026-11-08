using ExHyperV.Tools;

namespace ExHyperV.Services;

/// <summary>Hyper-V 运行检查的阶段，顺序也是首个问题的判定优先级。</summary>
public enum HyperVCheckStage
{
    /// <summary>当前系统的监控程序运行情况。</summary>
    Hypervisor,
    /// <summary>VMMS Windows 服务及其诊断信息。</summary>
    Vmms,
    /// <summary>Hyper-V WMI 管理服务实例是否可查询。</summary>
    Wmi
}

/// <summary>一个运行检查的结论；无法确认不等于明确不可用。</summary>
public enum HyperVCheckState
{
    /// <summary>此前阶段已遇到问题，本次未执行此检查。</summary>
    NotChecked,
    /// <summary>已确认满足本阶段要求。</summary>
    Passed,
    /// <summary>查询已完成，明确不满足本阶段要求。</summary>
    NotReady,
    /// <summary>查询失败、超时或信息不足，无法确定。</summary>
    Unknown
}

/// <summary>运行诊断的机器可读分类；原始错误另见检查结果的 Failure。</summary>
public enum HyperVDiagnosticKind
{
    /// <summary>没有诊断问题，或此阶段尚未检查。</summary>
    None,
    /// <summary>监控程序或服务没有运行。</summary>
    NotRunning,
    /// <summary>成功查询后没有找到目标服务或实例。</summary>
    NotFound,
    /// <summary>本次等待读取结果已超时，不表示底层调用已取消。</summary>
    Timeout,
    /// <summary>读取被拒绝或缺少所需特权。</summary>
    AccessDenied,
    /// <summary>WMI 命名空间不存在或不可用。</summary>
    InvalidNamespace,
    /// <summary>WMI 类不存在或不可用。</summary>
    InvalidClass,
    /// <summary>WMI Provider 报告执行失败。</summary>
    ProviderFailure,
    /// <summary>RPC 或 COM 连接失败。</summary>
    ConnectionFailure,
    /// <summary>查询数据缺失、格式异常或无法可靠解释。</summary>
    InvalidData,
    /// <summary>没有归入其他类别的读取失败，需查看原始错误。</summary>
    QueryFailed,
    /// <summary>VMMS 没有运行，且服务启动类型为 Disabled。</summary>
    ServiceDisabled,
    /// <summary>VMMS 没有运行，且已确认其程序文件路径不存在。</summary>
    ExecutableMissing
}

/// <summary>一个检查阶段的状态及失败信息，不包含修改系统的动作。</summary>
/// <param name="Stage">检查阶段。</param>
/// <param name="State">通过、未就绪、未知或未检查。</param>
/// <param name="DiagnosticKind">可供程序分支判断的诊断类别。</param>
/// <param name="Message">本阶段检查结论的文字说明。</param>
/// <param name="Failure">原始接口错误的类别、来源和数值；没有原始错误时为 null。</param>
public sealed record HyperVCheckResult(HyperVCheckStage Stage, HyperVCheckState State,
    HyperVDiagnosticKind DiagnosticKind, string Message, ApiFailure? Failure = null);

/// <summary>VMMS 程序路径的诊断状态。</summary>
public enum VmmsImageState
{
    /// <summary>本次没有检查程序路径。</summary>
    NotChecked,
    /// <summary>已解析为本机绝对路径，并确认该路径是文件。</summary>
    Exists,
    /// <summary>文件系统明确报告文件或所在目录不存在。</summary>
    Missing,
    /// <summary>路径含糊、格式无效或读取失败，不能认定文件缺失。</summary>
    Unknown
}

/// <summary>VMMS 程序文件的路径与存在状态。</summary>
/// <param name="State">路径检查结论。</param>
/// <param name="ResolvedPath">VMMS 程序文件在宿主上的完整路径；未能解析时为 null。</param>
/// <param name="Failure">读取失败的原始信息；路径无法解析且没有系统错误时为 null。</param>
public sealed record VmmsImageInfo(VmmsImageState State, string? ResolvedPath, ApiFailure? Failure = null);

/// <summary>VMMS Windows 服务的状态、启动配置与程序信息。</summary>
/// <param name="State">Windows 服务状态，例如 Running、Stopped；未取得时为 null。</param>
/// <param name="StartMode">启动类型，例如 Auto、Manual、Disabled。</param>
/// <param name="ExitCode">Win32_Service.ExitCode 原始值；未上报时为 null。</param>
/// <param name="ServiceSpecificExitCode">服务特定退出码；未上报时为 null。</param>
/// <param name="ProcessId">VMMS 进程编号；未取得时为 null。</param>
/// <param name="PathName">VMMS 服务配置的程序命令行，包含程序路径及启动参数。</param>
/// <param name="Image">程序路径解析及文件查询结果。</param>
public sealed record VmmsServiceInfo(string? State, string? StartMode, uint? ExitCode,
    uint? ServiceSpecificExitCode, uint? ProcessId, string? PathName, VmmsImageInfo Image);

/// <summary>Hyper-V 运行环境的结构化检查结果；按阶段顺序遇到首个问题后停止后续检查。</summary>
/// <remarks>检查顺序为监控程序、VMMS 服务、WMI 管理接口；遇到首个问题后，剩余阶段标为 NotChecked。</remarks>
public sealed class HyperVEnvironmentStatus
{
    /// <summary>本系统监控程序的检查结果。</summary>
    public HyperVCheckResult Hypervisor { get; }
    /// <summary>VMMS 服务检查结果。</summary>
    public HyperVCheckResult Vmms { get; }
    /// <summary>Hyper-V WMI 管理服务查询结果。</summary>
    public HyperVCheckResult Wmi { get; }
    /// <summary>成功查到 VMMS 时的服务信息；未检查、查询失败或不存在时为 null。</summary>
    public VmmsServiceInfo? VmmsService { get; }
    /// <summary>按监控程序、VMMS、WMI 顺序遇到的首个未通过结果；全部通过时为 null。</summary>
    public HyperVCheckResult? FirstIssue => Hypervisor.State is HyperVCheckState.Unknown or HyperVCheckState.NotReady ? Hypervisor
        : Vmms.State is HyperVCheckState.Unknown or HyperVCheckState.NotReady ? Vmms
        : Wmi.State is HyperVCheckState.Unknown or HyperVCheckState.NotReady ? Wmi : null;
    /// <summary>监控程序、VMMS 服务和 WMI 管理接口是否均已通过运行检查。</summary>
    /// <fielddoc>
    /// <meaning>监控程序、VMMS 服务和 WMI 管理接口是否均已通过运行检查。</meaning>
    /// <values kind="options">true：三项全部通过；false：至少一项未通过或未检查。</values>
    /// </fielddoc>
    public bool IsReady => Hypervisor.State == HyperVCheckState.Passed && Vmms.State == HyperVCheckState.Passed && Wmi.State == HyperVCheckState.Passed;
    /// <summary>是否已查到宿主的 VMMS 服务。</summary>
    /// <fielddoc>
    /// <meaning>是否已查到宿主的 VMMS 服务。</meaning>
    /// <values kind="options">true：服务存在；false：已确认服务不存在；null：未检查或查询失败。</values>
    /// </fielddoc>
    public bool? IsInstalled => VmmsService != null ? true
        : Vmms.State == HyperVCheckState.NotReady && Vmms.DiagnosticKind == HyperVDiagnosticKind.NotFound ? false : null;
    /// <summary>首个问题的显示说明；全部通过时为空。</summary>
    public string StatusText => FirstIssue?.Message ?? string.Empty;

    internal HyperVEnvironmentStatus(HyperVCheckResult hypervisor, HyperVCheckResult vmms,
        HyperVCheckResult wmi, VmmsServiceInfo? service)
    {
        Hypervisor = hypervisor;
        Vmms = vmms;
        Wmi = wmi;
        VmmsService = service;
    }
}
