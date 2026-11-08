using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExHyperV.Models;
using ExHyperV.Services;
using System.Collections.ObjectModel;
using System.ComponentModel;

namespace ExHyperV.WinUI3.UI.Pages.VirtualMachines;

public sealed class VirtualMachinesPageViewModel : ObservableObject
{
    private IReadOnlyList<VmListItem> all = [];
    private VmListItem? selected;
    private string filter = "", error = "";

    public VirtualMachinesPageViewModel() => RefreshCommand = new AsyncRelayCommand(RefreshAsync);

    public IAsyncRelayCommand RefreshCommand { get; }
    public VmInfoCache Cache { get; } = new();
    public ObservableCollection<VmListItem> Items { get; } = [];
    public VmListItem? Selected { get => selected; set => SetProperty(ref selected, value); }
    public string Error
    {
        get => error;
        private set { if (SetProperty(ref error, value)) OnPropertyChanged(nameof(HasError)); }
    }
    public bool HasError => Error.Length > 0;
    public bool IsFilterEmpty => Filter.Length == 0;
    public string Filter
    {
        get => filter;
        set
        {
            if (!SetProperty(ref filter, value)) return;
            OnPropertyChanged(nameof(IsFilterEmpty));
            ApplyFilter();
        }
    }

    public void Cancel() { RefreshCommand.Cancel(); Cache.Cancel(); }
    public void ResumePreload() => Cache.StartPreload(all.Select(vm => vm.Id).ToArray());

    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        Error = "";
        try
        {
            // Read only the list's fields. Hardware, disks and performance are loaded by their own pages later.
            var response = await Task.Run(async () =>
            {
                await using var query = new VmQueryService();
                return await query.QueryAsync(new VmQueryRequest(
                    [VmIdentityFields.Name, VmIdentityFields.State, VmIdentityFields.Uptime]), cancellationToken).ConfigureAwait(false);
            }, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!response.HasData) throw new InvalidOperationException(response.Error ?? "无法读取虚拟机列表。");
            var previous = all.ToDictionary(vm => vm.Id);
            var updated = new List<VmListItem>();
            int missingFields = 0;
            foreach (var row in response.Data!.VirtualMachines)
            {
                var id = Guid.Parse(row.Key);
                previous.TryGetValue(id, out var old);
                var name = row.Get(VmIdentityFields.Name);
                var state = row.Get(VmIdentityFields.State);
                var uptime = row.Get(VmIdentityFields.Uptime);
                if (!name.HasValue || !state.HasValue || !uptime.HasValue) missingFields++;
                // 同一虚拟机沿用原列表对象，刷新不应被视为切换虚拟机并结束设置流程。
                var item = old ?? new VmListItem(id, id.ToString(), "未取得状态", "未取得运行时长");
                if (name.HasValue) item.Name = name.Value!;
                if (state.HasValue) item.State = StateText(state.Value);
                if (uptime.HasValue) item.Uptime = uptime.Value.ToString(@"d\.hh\:mm\:ss");
                updated.Add(item);
            }
            foreach (var vm in all) vm.PropertyChanged -= OnVmPropertyChanged;
            all = updated;
            foreach (var vm in all) vm.PropertyChanged += OnVmPropertyChanged;
            Cache.StartPreload(all.Select(vm => vm.Id).ToArray());
            ApplyFilter();
            if (missingFields > 0) Error = $"{missingFields} 台虚拟机有字段暂时无法读取；已有有效值已保留。";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Error = exception.Message;
        }
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(VmListItem.IsRunning)) ApplyFilter();
    }

    private void ApplyFilter()
    {
        var selectedId = Selected?.Id;
        var visible = all.Where(vm => vm.Name.Contains(Filter, StringComparison.CurrentCultureIgnoreCase)
            || vm.Id.ToString().Contains(Filter, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(vm => vm.IsRunning)
            .ThenBy(vm => vm.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        for (var index = 0; index < visible.Length; index++)
        {
            if (index < Items.Count && ReferenceEquals(Items[index], visible[index])) continue;
            var currentIndex = Items.IndexOf(visible[index]);
            if (currentIndex >= 0) Items.Move(currentIndex, index);
            else Items.Insert(index, visible[index]);
        }
        while (Items.Count > visible.Length) Items.RemoveAt(Items.Count - 1);
        Selected = Items.FirstOrDefault(vm => vm.Id == selectedId) ?? Items.FirstOrDefault();
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
}
