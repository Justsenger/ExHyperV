using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExHyperV.App.Localization;
using ExHyperV.App.UI.Pages.VirtualMachines.Dashboard;
using ExHyperV.Models;
using ExHyperV.Services;

namespace ExHyperV.App.UI.Pages.VirtualMachines;

public sealed partial class VirtualMachinesPageViewModel : ObservableObject
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
    internal void ReportError(string message) => Error = message;
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

    public void Cancel() => RefreshCommand.Cancel();

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
                    (VmQueryField[])[VmIdentityFields.Name, VmIdentityFields.State, VmIdentityFields.Uptime,
                    VmMetadataFields.Key, VmMetadataFields.Value]), cancellationToken).ConfigureAwait(false);
            }, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!response.HasData) throw new InvalidOperationException(
                response.Success ? L10n.Get("VmList_ReadFailed") : CoreMessageResolver.ResolveError(response));
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
                var item = old ?? new VmListItem(id, id.ToString(), null, L10n.Get("VmList_UptimeUnavailable"));
                if (name.HasValue) item.Name = name.Value!;
                if (state.HasValue) item.PowerState = state.Value;
                if (uptime.HasValue) item.Uptime = uptime.Value.ToString(@"d\.hh\:mm\:ss");
                if (OsIconSource.ReadName(row) is { } iconName) item.OsIconName = iconName;
                updated.Add(item);
            }
            foreach (var vm in all) vm.PropertyChanged -= OnVmPropertyChanged;
            all = updated;
            foreach (var vm in all) vm.PropertyChanged += OnVmPropertyChanged;
            Cache.Retain(all.Select(vm => vm.Id).ToHashSet());
            ApplyFilter();
            if (missingFields > 0) Error = L10n.Format("VmList_MissingFields", missingFields);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Error = CoreMessageResolver.ResolveException(exception);
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

    internal static string StateText(VmState state) => L10n.Get(state switch
    {
        VmState.Running => "VmState_Running",
        VmState.PoweredOff => "VmState_PoweredOff",
        VmState.Saved => "VmState_Saved",
        VmState.Suspended => "VmState_Suspended",
        VmState.Starting => "VmState_Starting",
        VmState.ShuttingDown => "VmState_ShuttingDown",
        VmState.Saving => "VmState_Saving",
        VmState.Stopping => "VmState_Stopping",
        VmState.Resuming => "VmState_Resuming",
        VmState.Suspending => "VmState_Suspending",
        VmState.MergingDisks => "VmState_MergingDisks",
        VmState.Processing => "VmState_Processing",
        VmState.WaitingToStart => "VmState_WaitingToStart",
        VmState.FastSaved => "VmState_FastSaved",
        VmState.FastSaving => "VmState_FastSaving",
        VmState.ForceShuttingDown => "VmState_ForceShuttingDown",
        VmState.ForceRestarting => "VmState_ForceRestarting",
        VmState.Hibernated => "VmState_Hibernated",
        VmState.InTest => "VmState_InTest",
        VmState.Deferred => "VmState_Deferred",
        VmState.NotApplicable => "VmState_NotApplicable",
        VmState.Other => "VmState_Other",
        _ => "VmState_Unknown"
    });
}
