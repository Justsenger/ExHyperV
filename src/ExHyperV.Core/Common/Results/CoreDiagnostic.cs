using System.Globalization;
using System.Management;
using System.Text.RegularExpressions;

namespace ExHyperV.Tools;

/// <summary>语言无关的设置修改前置条件原因码。</summary>
public enum ConditionReasonCode
{
    /// <summary>无限制，允许修改。</summary>
    None = 0,
    /// <summary>需要先关闭虚拟机后修改。</summary>
    PoweredOffRequired,
    /// <summary>开启动态内存时，启动内存仅在关机后可修改。</summary>
    DynamicMemoryRunningStartupLocked,
    /// <summary>当前宿主系统或硬件未提供此设置属性。</summary>
    HostSettingMissing,
    /// <summary>当前虚拟机配置版本不支持此功能。</summary>
    UnsupportedVmVersion,
    /// <summary>仅支持 x64 处理器架构。</summary>
    RequiresX64,
    /// <summary>仅支持 ARM64 处理器架构。</summary>
    RequiresArm64,
    /// <summary>仅支持 Intel 处理器。</summary>
    RequiresIntel,
    /// <summary>宿主硬件不支持硬件隔离扩展。</summary>
    RequiresHardwareIsolation,
    /// <summary>需要先开启硬件性能计数器（PMU）。</summary>
    RequiresPerfmonPmu,
    /// <summary>未知或不支持的设置成员。</summary>
    UnknownSetting,
    /// <summary>暂不支持全局写入混合后备方式。</summary>
    UnsupportedBackingTypeHybrid,
    /// <summary>暂不支持按 NUMA 节点配置内存访问跟踪。</summary>
    UnsupportedTrackingPerNuma
}

/// <summary>语言无关的核心错误归一化标识。</summary>
public enum CoreErrorCode
{
    /// <summary>未命中预设诊断码，请使用默认或原始错误信息。</summary>
    None = 0,
    /// <summary>写入 CXL 时 Hyper-V 服务停止，已自动恢复运行且配置未改变。</summary>
    VmmsCrashedCxlRecovered,
    /// <summary>写入内存设置时 Hyper-V 服务异常停止，已自动恢复运行且配置未改变。</summary>
    VmmsCrashedRecovered,
    /// <summary>服务已恢复且目标设置未改变，但临时关闭的虚拟 NUMA 未能自动恢复。</summary>
    VmmsCrashedVnumaUnrestored,
    /// <summary>Hyper-V 服务停止且自动拉起失败。</summary>
    VmmsRecoveryFailed,
    /// <summary>Hyper-V 服务已重启，但配置最终状态尚不确定。</summary>
    VmmsRecoveredOutcomeUnknown,
    /// <summary>Hyper-V 管理服务当前正在停止、重启或暂时不可用。</summary>
    VmmsUnavailableOrShuttingDown,
    /// <summary>调用方缺少执行操作所需的权限。</summary>
    AccessDenied,
    /// <summary>找不到目标虚拟机或配置实例。</summary>
    NotFound,
    /// <summary>当前宿主或对象不支持该操作。</summary>
    Unsupported,
    /// <summary>虚拟机当前状态不允许执行此修改。</summary>
    InvalidState,
    /// <summary>提交的参数无效或不符合宿主约束。</summary>
    InvalidParameter,
    /// <summary>等待异步作业或系统响应超时。</summary>
    OperationTimedOut,
    /// <summary>等待操作结果期间被取消。</summary>
    OperationCancelled,
    /// <summary>底层 WMI 方法或异步作业执行失败。</summary>
    MethodFailed,
    /// <summary>已关闭 vNUMA，但后续内存修改失败。</summary>
    PartialVnumaDisableFailed,
    /// <summary>内存已修改，但恢复 vNUMA 失败。</summary>
    PartialVnumaRestoreFailed,
    /// <summary>提交后回读配置失败，无法确认实际结果。</summary>
    ReadBackUnavailable,
    /// <summary>提交后回读到的配置与预期不一致。</summary>
    ReadBackMismatch,
    /// <summary>宿主机可用内存或系统资源不足（如 0x8007000E / 0x800705AA）。</summary>
    InsufficientMemory,
    /// <summary>目标虚拟磁盘、镜像或文件正被占用（如 0x80070020）。</summary>
    FileInUse,
    /// <summary>找不到启动或操作所需的磁盘、镜像或配置文件（如 0x80070002 / 0x80070003）。</summary>
    FileNotFound,
    /// <summary>宿主机磁盘空间不足（如 0x80070070 / 0x80070027 / 32794）。</summary>
    DiskFull,
    /// <summary>启动虚拟机因宿主机不支持被拒绝，并匹配到具体不兼容或高风险配置项。</summary>
    StartUnsupportedWithSuspects,
    /// <summary>启动虚拟机因宿主机不支持而被拒绝。</summary>
    StartFailedUnsupported,
    /// <summary>差分虚拟硬盘的父级磁盘缺失或磁盘链已断开（如 0xC03A000D）。</summary>
    DifferencingParentNotFound,
    /// <summary>虚拟机网卡绑定的虚拟交换机已不存在（如 32788 / 0x800705AA）。</summary>
    VirtualSwitchNotFound,
    /// <summary>启动虚拟机因处理器数量或拓扑超过宿主机物理上限而失败。</summary>
    StartFailedProcessorTopology,
    /// <summary>运行态修改静态内存大小失败（来宾未就绪或不支持热调整）。</summary>
    RuntimeMemoryResizeFailed,
    /// <summary>ARM64 宿主机未附带第 1 代虚拟机固件，不支持启动第 1 代虚拟机。</summary>
    StartFailedGen1OnArm64,
    /// <summary>授权此宿主需要补充 Guardian XML 以核对证书对。</summary>
    GuardianMetadataRequired
}

/// <summary>根据跨语言的原生错误码与失败类别归类标准错误。</summary>
public static class CoreErrorClassifier
{
    private const int RpcServerUnavailableHr = unchecked((int)0x800706BA);
    private const int RpcCallFailedHr = unchecked((int)0x800706BE);
    private const int RpcCallFailedDneHr = unchecked((int)0x800706BF);
    private const int RpcDisconnectedHr = unchecked((int)0x80010108);
    private const int ShutdownInProgressHr = unchecked((int)0x8007045B);
    private const int ServiceNotActiveHr = unchecked((int)0x80070426);
    private const int ServerExecFailureHr = unchecked((int)0x80080005);
    private const int AccessDeniedHr = unchecked((int)0x80070005);
    private const int OutOfMemoryHr = unchecked((int)0x8007000E);
    private const int NoSystemResourcesHr = unchecked((int)0x800705AA);
    private const int NotSupportedHr = unchecked((int)0x80070032);
    private const int HvInvalidParameterHr = unchecked((int)0xC0350005);
    private const int VhdChainBrokenHr = unchecked((int)0xC03A000D);
    private const int SharingViolationHr = unchecked((int)0x80070020);
    private const int LockViolationHr = unchecked((int)0x80070021);
    private const int FileNotFoundHr = unchecked((int)0x80070002);
    private const int PathNotFoundHr = unchecked((int)0x80070003);
    private const int DiskFullHr = unchecked((int)0x80070070);
    private const int HandleDiskFullHr = unchecked((int)0x80070027);
    private const int ElementNotFoundHr = unchecked((int)0x80070490);
    private const int InvalidArgHr = unchecked((int)0x80070057);
    private static readonly Regex HexCodePattern = new(@"0[xX]([0-9A-Fa-f]{8})", RegexOptions.Compiled);

    /// <summary>判断底层错误码或异常链是否属于 Hyper-V / WMI 服务正在停止或暂时不可达。</summary>
    /// <param name="code">底层错误码或 HRESULT。</param>
    /// <param name="source">错误码来源。</param>
    /// <param name="exception">可选的原始异常对象。</param>
    /// <returns>若属于服务正在停止或不可达，返回 true；否则返回 false。</returns>
    public static bool IsServiceUnavailable(int code, ApiErrorSource source, Exception? exception = null)
    {
        if (IsUnavailableCode(code, source)) return true;

        Exception? current = exception;
        for (int depth = 0; depth < 6 && current != null; depth++, current = current.InnerException)
        {
            if (current is ManagementException mgmt && IsUnavailableCode((int)mgmt.ErrorCode, ApiErrorSource.Wmi))
                return true;
            if (IsUnavailableCode(current.HResult, ApiErrorSource.Com))
                return true;
        }
        return false;
    }

    /// <summary>直接对未包装的底层异常进行语言无关的诊断归类。</summary>
    /// <param name="exception">待归类的底层异常对象。</param>
    /// <returns>归一化后的核心错误诊断码。</returns>
    public static CoreErrorCode ClassifyException(Exception exception)
    {
        var failure = ApiFailure.From(exception);
        return Classify(failure.Kind, failure.Source, failure.Code, exception, exception.Message);
    }

    internal static CoreErrorCode Classify(ApiErrorKind kind, ApiErrorSource source, int code, Exception? exception = null, string? description = null)
    {
        if (IsServiceUnavailable(code, source, exception))
            return CoreErrorCode.VmmsUnavailableOrShuttingDown;

        var embedded = ClassifyEmbeddedHResults(description, exception, code, source);
        if (embedded != CoreErrorCode.None)
            return embedded;

        if (kind == ApiErrorKind.Cancelled)
            return CoreErrorCode.OperationCancelled;

        if (kind == ApiErrorKind.AccessDenied
            || (source != ApiErrorSource.Wmi && code == 5)
            || code == AccessDeniedHr
            || (source == ApiErrorSource.Wmi && code is (int)ManagementStatus.AccessDenied or (int)ManagementStatus.PrivilegeNotHeld or 32769))
            return CoreErrorCode.AccessDenied;

        if (source == ApiErrorSource.Wmi)
        {
            switch (code)
            {
                case 32778:
                    return CoreErrorCode.InsufficientMemory;
                case 32788:
                    return CoreErrorCode.VirtualSwitchNotFound;
                case 32779:
                    return CoreErrorCode.FileNotFound;
                case 32774:
                    return CoreErrorCode.FileInUse;
                case 32794:
                    return CoreErrorCode.DiskFull;
                case 32771:
                    return CoreErrorCode.NotFound;
            }
        }

        if (kind == ApiErrorKind.NotFound
            || (source == ApiErrorSource.Wmi && code is (int)ManagementStatus.NotFound or (int)ManagementStatus.InvalidNamespace or (int)ManagementStatus.InvalidObject))
            return CoreErrorCode.NotFound;

        if (kind == ApiErrorKind.Unsupported
            || (source == ApiErrorSource.Wmi && code is (int)ManagementStatus.InvalidClass or (int)ManagementStatus.NotSupported or (int)ManagementStatus.MethodNotImplemented or (int)ManagementStatus.ProviderNotCapable or 1 or 32770))
            return CoreErrorCode.Unsupported;

        if (kind == ApiErrorKind.InvalidState
            || (source == ApiErrorSource.Wmi && code is 5 or 32775))
            return CoreErrorCode.InvalidState;

        if (source == ApiErrorSource.Wmi && code is 3 or (int)ManagementStatus.Timedout or 32772)
            return CoreErrorCode.OperationTimedOut;

        if (source == ApiErrorSource.Wmi && code is (int)ManagementStatus.InvalidParameter or (int)ManagementStatus.InvalidMethodParameters or (int)ManagementStatus.ValueOutOfRange or 4 or 6 or 32773 or 32776)
            return CoreErrorCode.InvalidParameter;

        if (source == ApiErrorSource.Wmi && code is 2 or 32768)
            return CoreErrorCode.MethodFailed;

        return CoreErrorCode.None;
    }

    private static CoreErrorCode ClassifyEmbeddedHResults(string? description, Exception? exception, int wmiCode, ApiErrorSource source)
    {
        var seen = new HashSet<int>();
        if (!string.IsNullOrWhiteSpace(description))
            CollectHexCodes(description, seen);
        Exception? cur = exception;
        for (int depth = 0; depth < 6 && cur != null; depth++, cur = cur.InnerException)
        {
            if (cur.HResult != 0) seen.Add(cur.HResult);
            if (!string.IsNullOrWhiteSpace(cur.Message)) CollectHexCodes(cur.Message, seen);
        }

        if (seen.Count == 0) return CoreErrorCode.None;
        foreach (int hr in seen)
        {
            if (IsUnavailableCode(hr, ApiErrorSource.Com))
                return CoreErrorCode.VmmsUnavailableOrShuttingDown;
        }
        if (seen.Contains(OutOfMemoryHr))
            return CoreErrorCode.InsufficientMemory;
        if (seen.Contains(NoSystemResourcesHr))
        {
            if (source == ApiErrorSource.Wmi && wmiCode == 32788)
                return CoreErrorCode.VirtualSwitchNotFound;
            if (source == ApiErrorSource.Wmi && wmiCode == 32778)
                return CoreErrorCode.InsufficientMemory;
        }
        if (seen.Contains(VhdChainBrokenHr))
            return CoreErrorCode.DifferencingParentNotFound;
        if (seen.Contains(NotSupportedHr) || seen.Contains(HvInvalidParameterHr))
            return CoreErrorCode.Unsupported;
        if (seen.Contains(AccessDeniedHr))
            return CoreErrorCode.AccessDenied;
        if (seen.Contains(SharingViolationHr) || seen.Contains(LockViolationHr))
            return CoreErrorCode.FileInUse;
        if (seen.Contains(FileNotFoundHr) || seen.Contains(PathNotFoundHr))
            return CoreErrorCode.FileNotFound;
        if (seen.Contains(DiskFullHr) || seen.Contains(HandleDiskFullHr))
            return CoreErrorCode.DiskFull;
        if (seen.Contains(ElementNotFoundHr))
            return CoreErrorCode.NotFound;
        if (seen.Contains(InvalidArgHr))
            return CoreErrorCode.InvalidParameter;
        return CoreErrorCode.None;
    }

    private static void CollectHexCodes(string text, HashSet<int> sink)
    {
        foreach (Match match in HexCodePattern.Matches(text))
        {
            if (uint.TryParse(match.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint value))
                sink.Add(unchecked((int)value));
        }
    }

    private static bool IsUnavailableCode(int code, ApiErrorSource source)
    {
        if (code is (int)ManagementStatus.ShuttingDown
            or (int)ManagementStatus.TransportFailure
            or (int)ManagementStatus.ServerTooBusy
            or (int)ManagementStatus.ProviderFailure
            or (int)ManagementStatus.ProviderLoadFailure
            or (int)ManagementStatus.InitializationFailure
            or RpcServerUnavailableHr
            or RpcCallFailedHr
            or RpcCallFailedDneHr
            or RpcDisconnectedHr
            or ShutdownInProgressHr
            or ServiceNotActiveHr
            or ServerExecFailureHr)
            return true;

        return source == ApiErrorSource.Win32 && code is 1062 or 1115 or 1722 or 1726 or 1727;
    }
}
