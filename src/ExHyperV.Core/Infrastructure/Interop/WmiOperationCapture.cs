using System.Management;

namespace ExHyperV.Tools;

// Opt-in, per async flow. Only workflows that retain this capture pay for receipts.
// A receipt already started can finish after the scope exits; a detached child
// cannot add new operations after the scope has been closed.
internal sealed class WmiOperationCapture : IDisposable
{
    private static readonly AsyncLocal<WmiOperationCapture?> current = new();
    private readonly WmiOperationCapture? previous;
    private readonly Action<WmiOperationReceipt> record;
    private bool active = true;
    internal WmiOperationCapture(Action<WmiOperationReceipt> record)
    { this.record = record; previous = current.Value; current.Value = this; }
    internal static WmiOperationReceipt? Start(string method, string target, string scope, WmiContext? context)
    {
        var capture = current.Value;
        if (capture is null) return null;
        lock (capture)
        {
            if (!capture.active) return null;
            var receipt = new WmiOperationReceipt(method, target, scope, context ?? WmiContext.Local);
            capture.record(receipt);
            return receipt;
        }
    }
    public void Dispose() { lock (this) active = false; current.Value = previous; }
}

internal sealed class WmiOperationReceipt(string method, string target, string scope, WmiContext context)
{
    internal Guid Id { get; } = Guid.NewGuid();
    internal DateTimeOffset StartedAtUtc { get; } = DateTimeOffset.UtcNow;
    internal string Method { get; } = method;
    internal string Target { get; } = target;
    private string? job;
    private ApiResponse? result;
    internal string? Job { get { lock (this) return job; } set { lock (this) job = value; } }
    internal ApiResponse? Result { get { lock (this) return result; } }
    internal bool IsSettled => Result is { ErrorKind: not ApiErrorKind.OutcomeUnknown };
    internal void Complete(ApiResponse value)
    {
        lock (this)
        {
            // A late waiter timeout must not overwrite an independently observed terminal result.
            if (result is null || result.ErrorKind == ApiErrorKind.OutcomeUnknown) result = value;
        }
    }
    internal async Task<ApiResponse> ObserveAsync(Func<Task<ApiResponse>>? read = null)
    {
        if (IsSettled) return Result!;
        if (read is null && string.IsNullOrWhiteSpace(Job))
            return WmiJobWaiter.Unknown("原调用尚未结束，或没有可查询的作业引用；保留现场，未重放操作。");
        try
        {
            var observed = read is null ? await Task.Run(() =>
            {
                var connection = WmiConnectionCache.GetManagementScope(scope, context);
                using var nativeJob = new ManagementObject(connection, new ManagementPath(Job!), null);
                nativeJob.Get();
                return WmiJobWaiter.Evaluate(WmiJobWaiter.Read(nativeJob), scope == WmiScope.HyperV)
                    ?? WmiJobWaiter.Unknown("原作业仍在执行，未开始恢复。");
            }).ConfigureAwait(false) : await read().ConfigureAwait(false);
            Complete(observed);
            return Result!;
        }
        catch (Exception ex) { return WmiJobWaiter.FromException(ex, submitted: true); }
    }
}
