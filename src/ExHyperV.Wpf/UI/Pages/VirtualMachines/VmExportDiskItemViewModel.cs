using CommunityToolkit.Mvvm.ComponentModel;
using ExHyperV.Models;
using ExHyperV.Services;

namespace ExHyperV.UI.Pages.VirtualMachines;

public partial class VmExportDiskItemViewModel : ObservableObject
{
    public VmExportDiskItemViewModel(
        Guid vmId,
        string vmName,
        bool showVmName,
        VmExportService.VirtualHardDiskInfo model,
        VmDiskStatus disk,
        VmStorageItemViewModel? storageItem)
    {
        VmId = vmId;
        VmName = vmName;
        ShowVmName = showVmName;
        Model = model;
        Disk = disk;
        ControllerType = storageItem?.ControllerType ?? string.Empty;
        ControllerNumber = storageItem?.ControllerNumber ?? 0;
        ControllerLocation = storageItem?.ControllerLocation ?? 0;
    }

    public Guid VmId { get; }
    public string VmName { get; }
    public bool ShowVmName { get; }
    public VmExportService.VirtualHardDiskInfo Model { get; }
    public string SelectionKey => $"{VmId:D}|{ControllerType}|{ControllerNumber}|{ControllerLocation}|{Path}";
    public VmDiskStatus Disk { get; }
    public string ControllerType { get; }
    public int ControllerNumber { get; }
    public int ControllerLocation { get; }
    public bool HasControllerLocation => !string.IsNullOrWhiteSpace(ControllerType);
    public string ControllerDisplay => HasControllerLocation
        ? $"{ControllerType} {ControllerNumber}:{ControllerLocation}"
        : string.Empty;

    public string Name => Disk.Name;
    public string DisplayName => ShowVmName ? $"{VmName} · {Name}" : Name;
    public string Path => Disk.Path;
    public double SizeGB => Disk.MaxSize / 1073741824.0;

    [ObservableProperty]
    private bool _isIncluded = true;
}
