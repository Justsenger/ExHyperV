using System.Collections.Concurrent;
using ExHyperV.App.Localization;
using ExHyperV.App.UI.Pages.VirtualMachines.Dashboard;
using ExHyperV.Services;

namespace ExHyperV.App.UI.Pages.VirtualMachines;

public sealed class VmInfoCache
{
    private sealed record Entry(VmQueryRow Row, DateTimeOffset ReadAt, int Revision);
    private readonly ConcurrentDictionary<Guid, Entry> details = new();
    private readonly ConcurrentDictionary<Guid, int> revisions = new();
    private readonly SemaphoreSlim reader = new(1, 1);
    private static readonly TimeSpan Freshness = TimeSpan.FromSeconds(30);

    public VmQueryRow? GetSnapshot(Guid id) => details.TryGetValue(id, out var entry)
        && entry.Revision == revisions.GetValueOrDefault(id) ? entry.Row : null;

    public void Invalidate(Guid id)
    {
        revisions.AddOrUpdate(id, 1, (_, version) => version + 1);
        details.TryRemove(id, out _);
    }

    public void Retain(IReadOnlySet<Guid> ids)
    {
        foreach (var id in details.Keys.Where(id => !ids.Contains(id))) Invalidate(id);
    }

    public async Task<VmQueryRow> LoadDetailsAsync(Guid id, CancellationToken token)
    {
        await reader.WaitAsync(token).ConfigureAwait(false);
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                int revision = revisions.GetValueOrDefault(id);
                if (details.TryGetValue(id, out var entry) && entry.Revision == revision &&
                    DateTimeOffset.UtcNow - entry.ReadAt < Freshness) return entry.Row;
                await using var query = new VmQueryService();
                var result = await Task.Run(() => query.QueryAsync(new VmQueryRequest(VmDashboardView.ConfigurationFields, (Guid[])[id]), token), token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (!result.HasData) throw new InvalidOperationException(CoreMessageResolver.ResolveError(result));
                var row = result.Data!.VirtualMachines.FirstOrDefault() ?? throw new InvalidOperationException(L10n.Get("Dash_VmNotInHost"));
                if (revisions.GetValueOrDefault(id) != revision) continue;
                details[id] = new(row, DateTimeOffset.UtcNow, revision);
                return row;
            }
        }
        finally { reader.Release(); }
    }
}
