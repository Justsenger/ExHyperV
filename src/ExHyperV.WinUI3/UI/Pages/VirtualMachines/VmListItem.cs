using CommunityToolkit.Mvvm.ComponentModel;

namespace ExHyperV.WinUI3.UI.Pages.VirtualMachines;

public sealed class VmListItem(Guid id, string name, string state, string uptime) : ObservableObject
{
    private string name = name;
    private string state = state;
    private string uptime = uptime;
    public Guid Id { get; } = id;
    public string Name { get => name; set => SetProperty(ref name, value); }
    public string State
    {
        get => state;
        set
        {
            var wasRunning = IsRunning;
            if (!SetProperty(ref state, value)) return;
            if (wasRunning == IsRunning) return;
            OnPropertyChanged(nameof(IsRunning));
            OnPropertyChanged(nameof(IsNotRunning));
        }
    }
    public string Uptime { get => uptime; set => SetProperty(ref uptime, value); }
    public bool IsRunning => State == "正在运行";
    public bool IsNotRunning => !IsRunning;
    public string IconGlyph => "\uE7F4";
}
