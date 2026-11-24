
using CommunityToolkit.Mvvm.ComponentModel;
using ExHyperV.App.Localization;
using ExHyperV.Models;

namespace ExHyperV.App.UI.Pages.VirtualMachines;

public sealed partial class VmListItem(Guid id, string name, VmState? state, string uptime) : ObservableObject
{
    private string name = name;
    private VmState? state = state;
    private string uptime = uptime;
    public Guid Id { get; } = id;
    internal string? OsIconName { get; set; }
    public string Name { get => name; set => SetProperty(ref name, value); }
    public string State => state is { } value ? VirtualMachinesPageViewModel.StateText(value) : L10n.Get("VmList_StateUnavailable");
    public VmState? PowerState
    {
        get => state;
        set
        {
            var wasRunning = IsRunning;
            if (!SetProperty(ref state, value)) return;
            OnPropertyChanged(nameof(State));
            if (wasRunning == IsRunning) return;
            OnPropertyChanged(nameof(IsRunning));
            OnPropertyChanged(nameof(IsNotRunning));
        }
    }
    public string Uptime { get => uptime; set => SetProperty(ref uptime, value); }
    public bool IsRunning => state == VmState.Running;
    public bool IsNotRunning => !IsRunning;
    public string IconGlyph => "\uE7F4";
}
