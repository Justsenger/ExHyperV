using CommunityToolkit.Mvvm.ComponentModel;

namespace ExHyperV.Models;

public enum VmPcieGuestMode : byte
{
    Paravirtualized = 0,
    Emulated = 1,
}

public sealed class VmPcieSettings
{
    public bool SystemSettingsAvailable { get; init; }
    public bool EmulationEnabled { get; init; }
    public ushort Topology { get; init; }
    public bool BootPciExpressAvailable { get; init; }
    public bool BootPciExpress { get; init; }
    public List<VmPcieDeviceSetting> Devices { get; init; } = [];
}

public partial class VmPcieDeviceSetting : ObservableObject
{
    public string WmiPath { get; init; } = string.Empty;
    public string WmiInstanceId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string HostResource { get; init; } = string.Empty;
    public bool GuestModeAvailable { get; init; }
    public string ClassType { get; init; } = string.Empty;
    public string DeviceInstanceId { get; init; } = string.Empty;
    public string Path { get; init; } = string.Empty;
    public string Vendor { get; init; } = string.Empty;
    public string IconGlyph { get; init; } = string.Empty;

    [ObservableProperty]
    private VmPcieGuestMode _guestMode;

    public VmPcieGuestMode AppliedGuestMode { get; set; }
}

public sealed record VmPcieOption<T>(T Value, string Name);
