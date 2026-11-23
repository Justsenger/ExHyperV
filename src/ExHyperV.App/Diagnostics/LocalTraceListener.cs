using System.Diagnostics;
using ExHyperV.Tools;

namespace ExHyperV.App.Diagnostics;

internal sealed class LocalTraceListener(RollingLog log) : TraceListener
{
    internal void OnCoreEvent(CoreDiagnosticEvent item)
    {
        string fields = $"caller={item.Caller}";
        if (item.VmId is Guid vm) fields += $" vmId={vm:D}";
        if (item.Details != null) fields += " " + item.Details;
        if (item.ErrorKind.HasValue)
            fields += $" errorKind={item.ErrorKind} errorSource={item.ErrorSource} errorCode={item.ErrorCode} diagnostic={item.Diagnostic}";
        if (item.ExceptionType != null) fields += $" exception={item.ExceptionType} hresult=0x{item.HResult:X8}";
        if (!string.IsNullOrWhiteSpace(item.Stack)) fields += $" stack={item.Stack.TrimEnd()}";
        log.Write(item.Level, item.Category, item.EventName, fields, item.Timestamp);
    }

    internal void Failure(string category, string name, Exception? error, TraceEventType level = TraceEventType.Error)
    {
        string? fields = null;
        // Exception.Message, Data, Source and ToString() may all contain arbitrary user data.
        for (int depth = 0; error != null && depth < 8; depth++, error = error.InnerException)
            fields += $" exception={error.GetType().FullName} hresult=0x{error.HResult:X8} stack={new StackTrace(error, false).ToString().TrimEnd()}";
        log.Write(level, category, name, fields?.TrimStart());
    }

    // Trace accepts arbitrary payloads from libraries. Only project-owned templates
    // can contribute text. Unknown events retain severity and ID without their body.
    public override void TraceEvent(TraceEventCache? cache, string source, TraceEventType type, int id, string? format, params object?[]? args)
    {
        string name = format switch
        {
            "UI operation canceled: {0}" => "UiOperationCancelled",
            "UI event failed: {0}" => "UiEventFailed",
            "Could not display UI event failure: {0}" => "UiFeedbackFailed",
            "Host telemetry loop stopped: {0}" => "HostTelemetryStopped",
            "VM page cleanup failed: {0}" => "VmPageCleanupFailed",
            "Host page cleanup failed: {0}" => "HostPageCleanupFailed",
            _ => "TraceEvent"
        };
        if (args is [Exception error]) Failure("Trace", name, error, type);
        else log.Write(type, "Trace", name, $"id={id}");
    }

    public override void TraceEvent(TraceEventCache? cache, string source, TraceEventType type, int id, string? message) =>
        TraceEvent(cache, source, type, id, message, null);
    public override void Write(string? message) => log.Write(TraceEventType.Information, "Trace", "Write");
    public override void WriteLine(string? message) => log.Write(TraceEventType.Information, "Trace", "WriteLine");
}
