using System.Runtime.InteropServices;
using ExHyperV.Models;
using ExHyperV.Services;

namespace ExHyperV.App.UI.Pages.Host;

internal sealed record VmMemoryAllocationEntry(Guid VmId, string Name, double AllocatedGiB, bool? IsDynamic);

internal sealed record HostVmSummary(int Total, int Running, IReadOnlyList<VmMemoryAllocationEntry>? Allocations);

internal sealed record HostMemoryUsage(double UsedGiB, double TotalGiB, double AvailableGiB, int Percent);

internal sealed class HostInventorySnapshot
{
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
    public bool? UsbipdInstalled { get; init; }
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
}
