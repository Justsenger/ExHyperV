using CommunityToolkit.Mvvm.ComponentModel;
using ExHyperV.Models;

namespace ExHyperV.ViewModels;

public partial class VmExportDiskItemViewModel : ObservableObject
{
    public VmExportDiskItemViewModel(
        Guid vmId,
        string vmName,
        bool showVmName,
        string instanceId,
        VmDiskItem disk,
        VmStorageItem? storageItem)
    {
        VmId = vmId;
        VmName = vmName;
        ShowVmName = showVmName;
        InstanceId = instanceId;
        Disk = disk;
        ControllerType = storageItem?.ControllerType ?? string.Empty;
        ControllerNumber = storageItem?.ControllerNumber ?? 0;
        ControllerLocation = storageItem?.ControllerLocation ?? 0;
    }

    public Guid VmId { get; }
    public string VmName { get; }
    public bool ShowVmName { get; }
    public string InstanceId { get; }
    public string SelectionKey => $"{VmId:D}|{InstanceId}";
    public VmDiskItem Disk { get; }
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
