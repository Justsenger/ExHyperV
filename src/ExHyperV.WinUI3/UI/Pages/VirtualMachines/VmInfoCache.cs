using System.Collections.Concurrent;
using ExHyperV.Models;
using ExHyperV.Services;

namespace ExHyperV.WinUI3.UI.Pages.VirtualMachines;

// Page-owned snapshots. Runtime counters still belong to the selected VM's query session.
public sealed class VmInfoCache
{
    private sealed record Entry(VmQueryRow Row, DateTimeOffset ReadAt, int Revision);
    private readonly ConcurrentDictionary<Guid, Entry> basic = new();
    private readonly ConcurrentDictionary<Guid, Entry> details = new();
    private readonly ConcurrentDictionary<Guid, int> revisions = new();
    private readonly ConcurrentDictionary<(Guid Id, int Revision, CancellationToken Lifetime), Lazy<Task<VmQueryRow>>> pending = new();
    private readonly SemaphoreSlim readers = new(2, 2);
    private readonly object lifetimeGate = new();
    private readonly List<Task> preloads = [];
    private CancellationTokenSource lifetime = new();
    private Task preload = Task.CompletedTask;
    private static readonly TimeSpan Freshness = TimeSpan.FromSeconds(30);
    private static readonly VmQueryField[] BasicFields = [
        VmIdentityFields.Name, VmIdentityFields.State, VmIdentityFields.Uptime,
        VmIdentityFields.Generation, VmIdentityFields.ConfigurationVersion,
        VmProcessorFields.Count, VmMemoryFields.Startup, VmMetadataFields.Key, VmMetadataFields.Value];

    public event Action<Guid>? BasicReady;

    public VmQueryRow? GetBasic(Guid id) => basic.TryGetValue(id, out var item) && item.Revision == Revision(id) ? item.Row : null;
    // A stale snapshot can paint the selected VM immediately while LoadDetailsAsync refreshes it.
    // Explicit invalidation still removes the snapshot, so known edits never reuse old configuration.
    public VmQueryRow? GetSnapshot(Guid id) => details.TryGetValue(id, out var item)
        && item.Revision == Revision(id) ? item.Row : null;
    public VmQueryRow? GetDetails(Guid id) => details.TryGetValue(id, out var item)
        && item.Revision == Revision(id) && DateTimeOffset.UtcNow - item.ReadAt < Freshness ? item.Row : null;
    private int Revision(Guid id) => revisions.GetOrAdd(id, 0);

    public void Invalidate(Guid id)
    {
        revisions.AddOrUpdate(id, 1, (_, value) => value + 1);
        basic.TryRemove(id, out _);
        details.TryRemove(id, out _);
    }

    public void StartPreload(IReadOnlyList<Guid> ids)
    {
        var targets = ids.Distinct().ToArray();
        foreach (var id in details.Keys.Concat(basic.Keys).Distinct().Except(targets)) Invalidate(id);
        lock (lifetimeGate)
        {
            bool resumed = lifetime.IsCancellationRequested;
            if (resumed) lifetime = new CancellationTokenSource();
            // A canceled warm-up can still be draining an uncancelable provider call.
            // Returning to the page must start a new lifetime independently of that task.
            if (!resumed && !preload.IsCompleted) return;
            var token = lifetime.Token;
            preload = Task.Run(async () =>
            {
                try
                {
                    var observedRevisions = targets.ToDictionary(id => id, Revision);
                    await using var query = new VmQueryService();
                    var response = await query.QueryAsync(new VmQueryRequest(BasicFields, targets), token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    if (response.HasData)
                        foreach (var row in response.Data!.VirtualMachines)
                            if (Guid.TryParse(row.Key, out var id) && observedRevisions.TryGetValue(id, out var revision) && Revision(id) == revision)
                            {
                                var entry = new Entry(row, DateTimeOffset.UtcNow, revision);
                                basic.AddOrUpdate(id, entry, (_, previous) => previous.Revision > revision ? previous : entry);
                                BasicReady?.Invoke(id);
                            }
                    // Queue only one background read, leaving the second slot available to selection.
                    // Enqueuing every VM here would put an uncached foreground VM behind the whole list.
                    foreach (var id in targets)
                    {
                        token.ThrowIfCancellationRequested();
                        try { await LoadDetailsAsync(id, token).ConfigureAwait(false); }
                        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
                        catch { /* A failed warm-up is retried, with an error, when that VM is selected. */ }
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { }
                catch { /* Preloading must not turn a working VM list into a loading error. */ }
            }, token);
            preloads.RemoveAll(task => task.IsCompleted);
            preloads.Add(preload);
        }
    }

    public async Task<VmQueryRow> LoadDetailsAsync(Guid id, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (GetDetails(id) is { } cached) return cached;
            CancellationToken queryToken;
            lock (lifetimeGate) queryToken = lifetime.Token;
            queryToken.ThrowIfCancellationRequested();
            var key = (id, Revision(id), queryToken);
            var request = pending.GetOrAdd(key, _ => new Lazy<Task<VmQueryRow>>(
                () => ReadDetailsAsync(key), LazyThreadSafetyMode.ExecutionAndPublication));
            var row = await request.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            queryToken.ThrowIfCancellationRequested();
            // A setting may have changed while this shared request was reading.
            if (Revision(id) == key.Item2) return row;
        }
    }

    private async Task<VmQueryRow> ReadDetailsAsync((Guid Id, int Revision, CancellationToken Lifetime) key)
    {
        var token = key.Lifetime;
        bool acquired = false;
        try
        {
            await readers.WaitAsync(token).ConfigureAwait(false);
            acquired = true;
            await using var query = new VmQueryService();
            var result = await Task.Run(() => query.QueryAsync(new VmQueryRequest(VmDashboardView.ConfigurationFields, [key.Id]), token), token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (!result.HasData) throw new InvalidOperationException(result.Error ?? "无法读取虚拟机配置。");
            var row = result.Data!.VirtualMachines.FirstOrDefault() ?? throw new InvalidOperationException("此虚拟机已不在宿主清单中。");
            if (Revision(key.Id) == key.Revision)
            {
                var entry = new Entry(row, DateTimeOffset.UtcNow, key.Revision);
                details.AddOrUpdate(key.Id, entry, (_, previous) => previous.Revision > key.Revision ? previous : entry);
            }
            return row;
        }
        finally
        {
            if (acquired) readers.Release();
            pending.TryRemove(key, out _);
        }
    }

    public void Cancel()
    {
        lock (lifetimeGate) lifetime.Cancel();
    }
    public async Task StopAsync()
    {
        Cancel();
        Task[] warmups;
        lock (lifetimeGate) warmups = preloads.ToArray();
        var tasks = pending.Values.Where(value => value.IsValueCreated).Select(value => value.Value).Concat(warmups).ToArray();
        try { await Task.WhenAll(tasks); }
        catch { /* Individual callers report foreground failures; shutdown only drains them. */ }
    }
}
