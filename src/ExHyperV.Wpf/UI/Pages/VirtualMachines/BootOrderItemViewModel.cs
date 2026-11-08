using CommunityToolkit.Mvvm.ComponentModel;
using ExHyperV.Models;

namespace ExHyperV.UI.Pages.VirtualMachines;

public sealed partial class BootOrderItemViewModel(BootOrderItem model) : ObservableObject
{
    public BootOrderItem Model { get; } = model;
    public string Name => Model.Name;
    public string Description => Model.Description;
    public string Icon => Model.Kind switch
    {
        BootDeviceKind.Floppy => "\uE74E",
        BootDeviceKind.OpticalDrive => "\uE958",
        BootDeviceKind.HardDisk => "\uEDA2",
        BootDeviceKind.Network => "\uE774",
        BootDeviceKind.FirmwareFile => "\uE74C",
        BootDeviceKind.Firmware => "\uE713",
        _ => "\uE9CE"
    };
    [ObservableProperty] private bool _isLast;
}
