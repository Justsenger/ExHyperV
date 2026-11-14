using ExHyperV.Models;
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

    internal static string StateText(VmState state) => state switch
    {
        VmState.Running => "正在运行", VmState.PoweredOff => "已关机", VmState.Saved => "已保存",
        VmState.Suspended => "已暂停", VmState.Starting => "正在启动", VmState.ShuttingDown => "正在关机",
        VmState.Saving => "正在保存", VmState.Stopping => "正在停止", VmState.Resuming => "正在恢复",
        VmState.Suspending => "正在暂停", VmState.MergingDisks => "正在合并磁盘", VmState.Processing => "处理中",
        VmState.WaitingToStart => "等待启动", VmState.FastSaved => "已快速保存", VmState.FastSaving => "正在快速保存",
        VmState.ForceShuttingDown => "正在强制关机", VmState.ForceRestarting => "正在强制重启",
        VmState.Hibernated => "已休眠", VmState.InTest => "测试中", VmState.Deferred => "已延期",
        VmState.NotApplicable => "不适用", VmState.Other => "其他状态", _ => "未知状态"
    };

    internal static ApiResponse<VmListSnapshot> MapResponse(ApiResponse<VmInventorySnapshot> result)
    {
        if (!result.Success)
            return ApiResponse<VmListSnapshot>.Fail(result.Error, result.Code, result.ErrorSource,
                result.Exception, result.ErrorKind);

        if (!result.HasData) return ApiResponse<VmListSnapshot>.Ok(new([], []));
        var inventory = result.Data;
        var vms = inventory.VirtualMachines.Where(vm => vm.Id != Guid.Empty)
            .Select(vm => new VmListItem(vm.Id, vm.Name, StateText(vm.State), vm.IsRunning))
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
