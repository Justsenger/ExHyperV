using System.Runtime.InteropServices;
using ExHyperV.Models;
using ExHyperV.Services;

namespace ExHyperV.App.UI.Pages.Host;

internal enum HostOverviewSection
{
    Env,
    Cpu,
    Memory,
    Gpu,
    Storage,
    Network
}

internal readonly record struct HostTelemetryScope(
    bool IncludeProcessor,
    bool IncludeDevices,
    bool IncludeVmMemory,
    bool IncludeVmGpu,
    bool IncludeVmStorage,
    bool IncludeVmNetwork)
{
    public bool RequiresVmQuery => IncludeVmMemory || IncludeVmGpu || IncludeVmStorage || IncludeVmNetwork;
}

internal sealed record VmMemoryAllocationEntry(Guid VmId, string Name, double AllocatedGiB, bool? IsDynamic);

internal sealed record HostVmSummary(int Total, int Running, IReadOnlyList<VmMemoryAllocationEntry>? Allocations);

internal sealed record VmGpuUsageEntry(
    Guid VmId,
    string Name,
    bool IsRunning,
    IReadOnlyList<string> AssignedDeviceInstanceIds,
    double? Gpu3dPct,
    double? GpuCopyPct,
    double? GpuEncPct,
    double? GpuDecPct,
    bool HasEngineInstance);

internal sealed record VmPassthroughDiskEntry(
    Guid VmId,
    string Name,
    int DiskNumber,
    double? ReadBytesPerSec,
    double? WriteBytesPerSec,
    long? ReadOpsPerSec,
    long? WriteOpsPerSec,
    long? QueueLength);

internal sealed record VmNetworkAdapterUsage(
    string Name,
    string SwitchName,
    bool IsConnected,
    ulong? SendBytesPerSec,
    ulong? ReceiveBytesPerSec,
    string? PrimaryIp);

internal sealed record VmNetworkUsageEntry(
    Guid VmId,
    string Name,
    bool IsRunning,
    IReadOnlyList<VmNetworkAdapterUsage> Adapters);

internal sealed record HostMemoryUsage(double UsedGiB, double TotalGiB, double AvailableGiB, int Percent);

[Flags]
internal enum HostInventorySlices
{
    None = 0,
    Environment = 1 << 0,
    Compute = 1 << 1,
    Gpu = 1 << 2,
    Storage = 1 << 3,
    Network = 1 << 4,
    Settings = 1 << 5
}

internal sealed class HostInventorySnapshot
{
    public HostInventorySlices LoadedSlices { get; init; }
    public bool HasSlices(HostInventorySlices slices) => (LoadedSlices & slices) == slices;
    public string HostName { get; init; } = Environment.MachineName;
    public string OsCaption { get; init; } = string.Empty;
    public string CpuModel { get; init; } = string.Empty;
    public Architecture Arch { get; init; }
    public int LogicalProcessorCount { get; init; } = Math.Max(1, Environment.ProcessorCount);
    public double? TotalMemoryGiB { get; init; }
    public bool CpuVirtOk { get; init; }
    public bool GpuPvSupported { get; init; }
    public bool IommuOk { get; init; }
    public bool IsServerSystem { get; init; }
    public string? ProductSwitchReadError { get; init; }
    public HostProductTypePendingSwitch? PendingProductSwitch { get; init; }
    public HyperVEnvironmentStatus? Diagnostics { get; init; }
    public HyperVSchedulerType RunningScheduler { get; init; }
    public HyperVSchedulerType ConfiguredScheduler { get; init; }
    public string ActivePowerPlanName { get; init; } = string.Empty;
    public HyperVHostSettings? HostSettings { get; init; }
    public bool? NumaSpanningEnabled { get; init; }
    public bool? VolumeAutoMountEnabled { get; init; }
    public bool? NativeNvmeEnabled { get; init; }
    public bool NativeNvmeSupported { get; init; }
    public bool? GpuRestrictionsRelaxed { get; init; }
    public bool? OpenHclFromFileEnabled { get; init; }
    public HostVmSummary? VmSummary { get; init; }
    public IReadOnlyList<HostGpuInfo> Gpus { get; init; } = [];
    public IReadOnlyList<HostDiskInfo> Disks { get; init; } = [];
    public IReadOnlyList<SwitchInfo> Switches { get; init; } = [];
    public IReadOnlyList<SwitchUpstreamAdapter> PhysicalAdapters { get; init; } = [];
    public IReadOnlyDictionary<Guid, HostAdapterAddress> AdapterAddresses { get; init; } =
        new Dictionary<Guid, HostAdapterAddress>();
    public IReadOnlyDictionary<string, IReadOnlyList<SwitchEndpointInfo>> SwitchEndpoints { get; init; } =
        new Dictionary<string, IReadOnlyList<SwitchEndpointInfo>>(StringComparer.OrdinalIgnoreCase);
}

internal sealed class HostLiveSample
{
    public double? CpuPercent { get; init; }
    public double? CpuFreqGHz { get; init; }
    public HostCpuThreadReport? CpuReport { get; init; }
    public HostMemoryUsage? Memory { get; init; }
    public HostVmSummary? VmSummary { get; init; }
    public IReadOnlyDictionary<int, HostDiskActivity> Disks { get; init; } = new Dictionary<int, HostDiskActivity>();
    public IReadOnlyDictionary<Guid, NetworkThroughputSample> Adapters { get; init; } = new Dictionary<Guid, NetworkThroughputSample>();
    public IReadOnlyDictionary<string, NetworkThroughputSample> Switches { get; init; } =
        new Dictionary<string, NetworkThroughputSample>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyDictionary<string, HostGpuActivity> Gpus { get; init; } = new Dictionary<string, HostGpuActivity>();
    public IReadOnlyList<VmGpuUsageEntry>? VmGpus { get; init; }
    public IReadOnlyList<VmPassthroughDiskEntry>? VmStorage { get; init; }
    public IReadOnlyList<VmNetworkUsageEntry>? VmNetworks { get; init; }
}
