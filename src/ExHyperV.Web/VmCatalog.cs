using ExHyperV.Services;
using ExHyperV.Tools;

namespace ExHyperV.Web;

internal sealed record VmListItem(Guid Id, string Name, string State, bool IsRunning);
internal sealed record VmListIssue(string Component, string Error, string ErrorKind, string ErrorSource, int Code);
internal sealed record VmListSnapshot(VmListItem[] VirtualMachines, VmListIssue[] Issues);

internal sealed class VmCatalog(VmQueryService queryService)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private ApiResponse<VmListSnapshot>? snapshot;
    private long updated;

    public async Task<ApiResponse<VmListSnapshot>> GetAsync()
    {
        await gate.WaitAsync();
        try
        {
            if (snapshot is not null && Environment.TickCount64 - updated < 1500) return snapshot;
            var result = MapResponse(await queryService.GetVmListAsync().ConfigureAwait(false));
            if (!result.Success) return result;
            snapshot = result;
            updated = Environment.TickCount64;
            return snapshot;
        }
        finally { gate.Release(); }
    }

    internal static ApiResponse<VmListSnapshot> MapResponse(ApiResponse<VmInventorySnapshot> result)
    {
        if (!result.Success)
            return ApiResponse<VmListSnapshot>.Fail(result.Error, result.Code, result.ErrorSource,
                result.Exception, result.ErrorKind);

        if (!result.HasData) return ApiResponse<VmListSnapshot>.Ok(new([], []));
        var inventory = result.Data;
        var vms = inventory.VirtualMachines.Where(vm => vm.Id != Guid.Empty)
            .Select(vm => new VmListItem(vm.Id, vm.Name, vm.StateText, vm.IsRunning))
            .OrderByDescending(vm => vm.IsRunning).ThenBy(vm => vm.Name).ToArray();
        var issues = inventory.Issues.Select(issue => new VmListIssue(issue.Component, issue.Error,
            issue.ErrorKind.ToString(), issue.ErrorSource.ToString(), issue.Code)).ToArray();
        return ApiResponse<VmListSnapshot>.Ok(new(vms, issues));
    }
}

internal sealed class ConsoleSessions
{
    private readonly HashSet<Guid> active = [];
    public bool TryEnter(Guid id) { lock (active) return active.Count < 4 && active.Add(id); }
    public void Exit(Guid id) { lock (active) active.Remove(id); }
}
