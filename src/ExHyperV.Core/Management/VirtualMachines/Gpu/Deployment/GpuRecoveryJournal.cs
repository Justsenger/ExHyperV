using ExHyperV.Models;
using ExHyperV.Tools;

namespace ExHyperV.Services;

// Owned by one deployment. Closures retain original identities and values, never
// an open WMI object. Each retry reacquires the existing resource locks.
internal sealed class GpuRecoveryJournal
{
    private static readonly AsyncLocal<GpuRecoveryJournal?> current = new();
    internal static GpuRecoveryJournal? Current => current.Value;
    private readonly List<(string Stage, WmiOperationReceipt Receipt)> operations = [];
    private readonly List<GpuRecoveryWork> cleanup = [];
    private readonly Dictionary<string, Func<Task<ApiResponse>>> observations = [];
    private bool untrackedUnknown;
    private Func<Func<Task<ApiResponse>>, Task<ApiResponse>> recoveryScope = action => action();
    internal void SetScope(Func<Func<Task<ApiResponse>>, Task<ApiResponse>> scope) => recoveryScope = scope;
    internal Guid Id { get; } = Guid.NewGuid();
    internal DateTimeOffset StartedAtUtc { get; } = DateTimeOffset.UtcNow;
    internal bool HasRecords { get { lock (operations) return operations.Count != 0 || cleanup.Count != 0 || untrackedUnknown; } }
    internal bool HasUnknown { get { lock (operations) return untrackedUnknown || operations.Any(x => !x.Receipt.IsSettled); } }
    internal bool NeedsCleanup => cleanup.Any(x => !x.Finalized);

    internal async Task<T> RunAsync<T>(string stage, Func<Task<T>> action, Func<Task<ApiResponse>>? observe = null, bool restoring = false)
    {
        var previous = current.Value;
        current.Value = this;
        int started = 0;
        using var capture = new WmiOperationCapture(receipt =>
        { lock (operations) { operations.Add((stage, receipt)); started++; } });
        if (observe != null) observations[stage] = observe;
        bool inspected = false;
        try
        {
            var value = await action().ConfigureAwait(false);
            if (HasUnknown) throw new OperationOutcomeUnknownException(WmiJobWaiter.Unknown("部署操作仍有未确认结束的底层任务，请先核实原任务。"));
            await InspectResourcesAsync().ConfigureAwait(false);
            inspected = true;
            if (!restoring && NeedsCleanup) throw new GpuDeploymentCleanupException("临时资源尚未确认恢复，已停止后续部署。请核实后重试清理。");
            return value;
        }
        catch (OperationOutcomeUnknownException)
        {
            if (Volatile.Read(ref started) == 0) untrackedUnknown = true;
            throw;
        }
        finally
        {
            try { if (!inspected && !HasUnknown) await InspectResourcesAsync().ConfigureAwait(false); }
            finally { current.Value = previous; }
        }

    }

    internal WmiOperationReceipt? LastOperation(string method)
    { lock (operations) return operations.LastOrDefault(x => x.Receipt.Method == method).Receipt; }

    internal GpuRecoveryWork Register(string kind, string resource,
        IReadOnlyDictionary<string, string> original, Func<Task<ApiResponse<bool>>> inspect, Func<Task<ApiResponse>> restore)
    {
        var item = new GpuRecoveryWork(kind, resource, original, inspect, restore);
        cleanup.Add(item);
        return item;
    }

    internal async Task<ApiResponse> ReconcileAsync()
    {
        (string Stage, WmiOperationReceipt Receipt)[] receipts;
        lock (operations) receipts = operations.ToArray();
        foreach (var (_, receipt) in receipts) await receipt.ObserveAsync().ConfigureAwait(false);
        if (HasUnknown) return WmiJobWaiter.Unknown("原任务仍在执行或没有足够的结束证据；保留恢复记录，未执行写入。");
        foreach (var inspect in observations.Values)
        {
            var result = await inspect().ConfigureAwait(false);
            if (!result.Success) return WmiJobWaiter.Unknown("原任务已结束，但读取当前状态失败：" + result.Error, result.Code, result.ErrorSource);
        }
        await InspectResourcesAsync().ConfigureAwait(false);
        return ApiResponse.Ok();
    }

    private async Task InspectResourcesAsync()
    {
        var remaining = cleanup.Where(x => !x.Finalized).ToArray();
        foreach (var item in remaining) await item.InspectAsync().ConfigureAwait(false);
        // Keep a partial batch together: reattaching its disk may change an already
        // restored offline flag. Fully restored earlier batches must never reopen.
        if (remaining.All(x => x.Completed))
            foreach (var item in remaining) item.Finalized = true;
    }

    internal async Task<ApiResponse> RetryAsync()
    {
        try
        {
            var observed = await ReconcileAsync().ConfigureAwait(false);
            if (!observed.Success || !NeedsCleanup) return observed;
            return await recoveryScope(RetryCoreAsync).ConfigureAwait(false);
        }
        catch (Exception ex) { return ApiResponse.Fail(ex.Message, exception: ex, kind: ApiErrorKind.CleanupRequired); }
    }

    private async Task<ApiResponse> RetryCoreAsync()
    {
        var checkedResult = await ReconcileAsync().ConfigureAwait(false);
        if (!checkedResult.Success) return checkedResult;
        // Inner resources (drive letters, physical attachment) before outer image unmount.
        foreach (var item in cleanup.AsEnumerable().Reverse().Where(item => !item.Finalized).ToArray())
        {
            var before = await item.InspectAsync().ConfigureAwait(false);
            if (!before.HasData) return ApiResponse.Fail(before.Error, before.Code, before.ErrorSource, before.Exception, before.ErrorKind);
            if (before.Data) continue;
            ApiResponse restored;
            try { restored = await RunAsync("Recovery:" + item.Kind, item.Restore, restoring: true).ConfigureAwait(false); }
            catch (OperationOutcomeUnknownException ex) { restored = ex.Result; }
            catch (Exception ex) { restored = ApiResponse.Fail(ex.Message, exception: ex, kind: ApiErrorKind.CleanupRequired); }
            item.Record(restored);
            if (!restored.Success) return restored;
            var verified = await item.InspectAsync().ConfigureAwait(false);
            if (!verified.HasData || !verified.Data)
                return ApiResponse.Fail(verified.Success ? "恢复后未达到记录的原状态。" : verified.Error, kind: ApiErrorKind.CleanupRequired);
        }
        await InspectResourcesAsync().ConfigureAwait(false);
        if (NeedsCleanup) return ApiResponse.Fail("部分临时资源未恢复，保留恢复记录。", kind: ApiErrorKind.CleanupRequired);
        return ApiResponse.Ok();
    }

    internal IReadOnlyList<GpuRecoveryOperation> Operations
    {
        get
        {
            lock (operations) return Array.AsReadOnly(operations.Select(x => new GpuRecoveryOperation(
                x.Receipt.Id, x.Stage, x.Receipt.Method, x.Receipt.Target, x.Receipt.Job,
                x.Receipt.StartedAtUtc, x.Receipt.IsSettled,
                x.Receipt.Result is { } result ? ApiFailure.From(result) : null)).ToArray());
        }
    }
    internal IReadOnlyList<GpuRecoveryResource> Resources => Array.AsReadOnly(cleanup.Select(x => x.Snapshot()).ToArray());
    internal Dictionary<string, string> ObservedValues { get; } = [];
}

internal sealed class GpuRecoveryWork(string kind, string resource, IReadOnlyDictionary<string, string> original,
    Func<Task<ApiResponse<bool>>> inspect, Func<Task<ApiResponse>> restore)
{
    internal string Kind { get; } = kind;
    internal bool Completed { get; private set; }
    internal bool Finalized { get; set; }
    internal void Finish() { Completed = Finalized = true; failure = null; }
    private ApiFailure? failure;
    internal Func<Task<ApiResponse>> Restore { get; } = restore;
    internal void Record(ApiResponse result) { failure = ApiFailure.From(result); }
    internal async Task<ApiResponse<bool>> InspectAsync()
    {
        ApiResponse<bool> result;
        try { result = await inspect().ConfigureAwait(false); }
        catch (Exception ex) { result = ApiResponse<bool>.Fail(ex.Message, exception: ex, kind: ApiErrorKind.CleanupRequired); }
        Completed = result.HasData && result.Data;
        failure = ApiFailure.From(result);
        return result;
    }
    internal GpuRecoveryResource Snapshot() => new(Kind, resource,
        new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(new Dictionary<string, string>(original)), Completed, failure);
}
