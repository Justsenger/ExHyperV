using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace ExHyperV.Tools;

/// <summary>向进程内宿主报告 Core 诊断事件。</summary>
/// <remarks>宿主订阅后可按等级保存结构化事件。回调在产生事件的线程运行，结束订阅时移除处理程序。</remarks>
public static class CoreDiagnostics
{
    /// <summary>操作、探测或错误发生时通知订阅者，单个订阅者异常不会影响操作或其他订阅者。</summary>
    public static event Action<CoreDiagnosticEvent>? Reported;

    [ThreadStatic] private static bool publishing;

    // All details at this boundary must be selected from numbers, enums and fixed labels.
    // In particular, API error strings and exception messages can contain caller data.
    internal static void Report(string eventName, TraceEventType level = TraceEventType.Warning,
        Exception? error = null, string? details = null, Guid? vmId = null,
        ApiErrorKind? errorKind = null, ApiErrorSource? errorSource = null, int? errorCode = null, CoreErrorCode? diagnostic = null,
        [CallerMemberName] string caller = "", [CallerFilePath] string file = "")
    {
        var subscribers = Reported;
        if (subscribers == null || publishing) return;
        publishing = true;
        try
        {
            var stack = error == null ? null : new StackTrace(error, false).ToString();
            if (errorKind.HasValue && string.IsNullOrWhiteSpace(stack))
                stack = new StackTrace(2, false).ToString();
            var item = new CoreDiagnosticEvent
            {
                Timestamp = DateTimeOffset.UtcNow, Level = level, Category = Path.GetFileNameWithoutExtension(file),
                EventName = eventName, Caller = caller, Details = details, VmId = vmId,
                ErrorKind = errorKind, ErrorSource = errorSource, ErrorCode = errorCode, Diagnostic = diagnostic,
                ExceptionType = error?.GetType().FullName, HResult = error?.HResult, Stack = stack
            };
            foreach (var subscriber in Delegate.EnumerateInvocationList(subscribers))
            {
                try { subscriber(item); }
                catch (Exception) { /* Host observers are isolated from the operation and each other. */ }
            }
        }
        finally { publishing = false; }
    }
}

/// <summary>一条本地诊断事件的只读快照。内容仅包含操作元数据和经过选择的诊断字段。</summary>
public sealed class CoreDiagnosticEvent
{
    internal CoreDiagnosticEvent() { }
    /// <summary>事件产生的 UTC 时间。</summary>
    public DateTimeOffset Timestamp { get; internal init; }
    /// <summary>诊断等级。Verbose 表示详细采样。</summary>
    public TraceEventType Level { get; internal init; }
    /// <summary>产生事件的 Core 模块名。</summary>
    public string Category { get; internal init; } = "";
    /// <summary>语言无关的操作或事件标识。</summary>
    public string EventName { get; internal init; } = "";
    /// <summary>产生事件的成员名。</summary>
    public string Caller { get; internal init; } = "";
    /// <summary>经过选择的数值、状态和分类字段。</summary>
    public string? Details { get; internal init; }
    /// <summary>已知的虚拟机标识。</summary>
    public Guid? VmId { get; internal init; }
    /// <summary>失败响应归一化后的错误类别。</summary>
    public ApiErrorKind? ErrorKind { get; internal init; }
    /// <summary>失败响应数值码的来源。</summary>
    public ApiErrorSource? ErrorSource { get; internal init; }
    /// <summary>失败响应携带的原生或业务错误码。</summary>
    public int? ErrorCode { get; internal init; }
    /// <summary>失败响应的语言无关诊断分类。</summary>
    public CoreErrorCode? Diagnostic { get; internal init; }
    /// <summary>发生异常时的托管异常类型。</summary>
    public string? ExceptionType { get; internal init; }
    /// <summary>发生异常时的 HRESULT。</summary>
    public int? HResult { get; internal init; }
    /// <summary>不含源文件路径的调用栈。</summary>
    public string? Stack { get; internal init; }
}
