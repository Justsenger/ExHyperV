using System.Runtime.InteropServices;
using ExHyperV.Models;
using ExHyperV.Services;
using ExHyperV.WinUI3.Localization;

namespace ExHyperV.WinUI3.UI.Pages.Host;

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

internal sealed class HostTelemetrySampler
{
    internal static TimeSpan Uptime => TimeSpan.FromMilliseconds(Environment.TickCount64);

    public async Task<HostInventorySnapshot> LoadInventoryAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var sysTask = HostSystemInfoService.GetSystemInfoAsync();
        var diagTask = HyperVHostService.GetHyperVDiagnosticsAsync();
        var gpuTask = SafeGetGpusAsync();
        var diskTask = SafeGetDisksAsync();
        var netTask = SafeGetNetworkAsync();
        var settingsTask = SafeGetHostSettingsAsync();
        var numaTask = SafeGetNumaAsync();
        var vmSummaryTask = ReadVmSummaryAsync(cancellationToken);

        await Task.WhenAll(sysTask, diagTask, gpuTask, diskTask, netTask, settingsTask, numaTask, vmSummaryTask)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        var sysInfo = await sysTask;
        var diag = await diagTask;
        var gpus = await gpuTask;
        var disks = await diskTask;
        var (switches, adapters) = await netTask;
        var hostSettings = await settingsTask;
        var numa = await numaTask;
        var vmMem = await vmSummaryTask;

        bool cpuVirtOk = false, gpuPv = false, iommuOk = false, isServer = false;
        bool? usbipd = null;
        string? productSwitchReadError = null;
        HyperVSchedulerType runSched = HyperVSchedulerType.Unknown;
        HyperVSchedulerType cfgSched = HyperVSchedulerType.Unknown;
        string powerPlanName = string.Empty;
        HostProductTypePendingSwitch? pendingSwitch = null;
        bool? autoMount = null, nvmeEnabled = null, gpuRelaxed = null, openHcl = null;
        bool nvmeSupported = HostNvmeService.IsNativeNvmeSupported();

        await Task.Run(() =>
        {
            cpuVirtOk = HyperVHostService.IsVirtualizationEnabled();
            gpuPv = HostGpuService.IsGpuPartitioningVersionSupported();
            iommuOk = HyperVHostService.IsIommuEnabled();
            isServer = HostProductTypeService.IsServerSystem();
            usbipd = HostDeviceTelemetryService.IsUsbipdInstalled();
            runSched = HyperVSchedulerService.GetSchedulerType();
            cfgSched = HyperVSchedulerService.GetConfiguredSchedulerType();
            try
            {
                var plans = HostPowerPlanService.GetPowerPlans();
                var active = HostPowerPlanService.GetActivePowerPlanId();
                if (active.HasValue)
                    powerPlanName = plans.FirstOrDefault(p => p.Id == active.Value)?.Name ?? string.Empty;
            }
            catch { }
            try
            {
                var pending = HostProductTypeService.GetPendingSwitch();
                if (!pending.Success) productSwitchReadError = CoreMessageResolver.ResolveError(pending);
                else if (pending.HasData) pendingSwitch = pending.Data;
            }
            catch (Exception ex) { productSwitchReadError = CoreMessageResolver.ResolveException(ex); }
            try { var s = HostVolumeAutoMountService.GetState(); if (s.HasData) autoMount = s.Data; } catch { }
            if (nvmeSupported)
            {
                try { var n = HostNvmeService.IsNativeNvmeEnabled(); if (n.HasData) nvmeEnabled = n.Data; } catch { }
            }
            try { var g = HostGpuService.GetAssignmentRestrictionsRelaxed(); if (g.HasData) gpuRelaxed = g.Data; } catch { }
            try { openHcl = HostOpenHclService.IsFirmwareLoadFromFileEnabled(); } catch { }
        }, cancellationToken).ConfigureAwait(false);

        var nicAddresses = HostDeviceTelemetryService.ReadAdapterAddresses();
        var endpoints = await ReadSwitchEndpointsAsync(switches, cancellationToken).ConfigureAwait(false);
        var memory = HostSystemInfoService.TryReadHostMemoryMiB();
        cancellationToken.ThrowIfCancellationRequested();

        return new HostInventorySnapshot
        {
            HostName = Environment.MachineName,
            OsCaption = sysInfo.Caption,
            CpuModel = sysInfo.CpuModel,
            Arch = HostProcessorPlatformService.GetArchitecture(),
            LogicalProcessorCount = Math.Max(1, Environment.ProcessorCount),
            TotalMemoryGiB = memory?.TotalMiB / 1024d,
            CpuVirtOk = cpuVirtOk,
            GpuPvSupported = gpuPv,
            IommuOk = iommuOk,
            IsServerSystem = isServer,
            UsbipdInstalled = usbipd,
            PendingProductSwitch = pendingSwitch,
            ProductSwitchReadError = productSwitchReadError,
            Diagnostics = diag,
            RunningScheduler = runSched,
            ConfiguredScheduler = cfgSched,
            ActivePowerPlanName = powerPlanName,
            HostSettings = hostSettings,
            NumaSpanningEnabled = numa,
            VolumeAutoMountEnabled = autoMount,
            NativeNvmeEnabled = nvmeEnabled,
            NativeNvmeSupported = nvmeSupported,
            GpuRestrictionsRelaxed = gpuRelaxed,
            OpenHclFromFileEnabled = openHcl,
            VmSummary = vmMem,
            Gpus = gpus,
            Disks = disks,
            Switches = switches,
            PhysicalAdapters = adapters,
            AdapterAddresses = nicAddresses,
            SwitchEndpoints = endpoints
        };
    }

    public static async Task<HostLiveSample> SampleLiveAsync(HostInventorySnapshot inventory,
        HostCpuThreadMonitorService cpu, HostDeviceTelemetryService devices, HostNetworkTelemetryService network,
        bool refreshVms, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var report = cpu.GetThreadUsage();
        double? totalCpu = report.Sample.IsValid && report.Threads.Count > 0 && report.Threads.All(t => t.Sample.IsValid)
            ? Math.Clamp(report.Threads.Average(t => (double)t.TotalUsagePercent), 0, 100) : null;
        var memory = HostSystemInfoService.TryReadHostMemoryMiB();
        var metrics = devices.Read(inventory.Disks.Select(d => d.Number).ToArray());
        return new HostLiveSample
        {
            CpuPercent = totalCpu, CpuReport = report, CpuFreqGHz = metrics.CpuGHz,
            Memory = memory is { TotalMiB: > 0 } m && m.AvailableMiB <= m.TotalMiB
                ? new((m.TotalMiB - m.AvailableMiB) / 1024d, m.TotalMiB / 1024d, m.AvailableMiB / 1024d,
                    (int)Math.Round(100d * (m.TotalMiB - m.AvailableMiB) / m.TotalMiB)) : null,
            Disks = metrics.Disks, Gpus = metrics.Gpus,
            Adapters = ReadAdapterMetrics(network, inventory.PhysicalAdapters),
            Switches = ReadSwitchMetrics(network, inventory.Switches),
            VmSummary = refreshVms ? await ReadVmSummaryAsync(token).ConfigureAwait(false) : null
        };
    }

    private static async Task<IReadOnlyList<HostGpuInfo>> SafeGetGpusAsync()
    {
        try
        {
            var res = await HostGpuService.GetHostGpusAsync().ConfigureAwait(false);
            return res.HasData ? res.Data! : [];
        }
        catch { return []; }
    }

    private static async Task<IReadOnlyList<HostDiskInfo>> SafeGetDisksAsync()
    {
        try
        {
            var res = await HostDiskService.GetHostDisksAsync().ConfigureAwait(false);
            return res.HasData ? res.Data! : [];
        }
        catch { return []; }
    }

    private static async Task<(List<SwitchInfo> Switches, List<SwitchUpstreamAdapter> Adapters)> SafeGetNetworkAsync()
    {
        try { return await HyperVSwitchService.GetNetworkInfoAsync().ConfigureAwait(false); }
        catch { return ([], []); }
    }

    private static async Task<HyperVHostSettings?> SafeGetHostSettingsAsync()
    {
        try { return await HyperVHostSettingsService.GetAsync().ConfigureAwait(false); }
        catch { return null; }
    }

    private static async Task<bool?> SafeGetNumaAsync()
    {
        try
        {
            var res = await HyperVNumaService.GetNumaSpanningEnabledAsync().ConfigureAwait(false);
            return res.HasData ? res.Data : null;
        }
        catch { return null; }
    }

    private static async Task<HostVmSummary?> ReadVmSummaryAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var query = new VmQueryService();
            var res = await query.QueryAsync(new VmQueryRequest(
                (VmQueryField[])[
                    VmIdentityFields.Id,
                    VmIdentityFields.Name,
                    VmIdentityFields.State,
                    VmMemoryFields.DynamicMemoryEnabled,
                    VmMemoryFields.AssignedMiB
                ]), cancellationToken)
                .ConfigureAwait(false);
            if (!res.HasData) return null;

            int total = res.Data!.VirtualMachines.Count;
            int running = 0;
            bool allocationComplete = true;
            var allocations = new List<VmMemoryAllocationEntry>();

            foreach (var row in res.Data.VirtualMachines)
            {
                var state = row.Get(VmIdentityFields.State);
                if (!state.HasValue) return null;
                bool isRunning = state.Value == VmState.Running;
                bool holdsMemory = isRunning || (state.HasValue && state.Value is
                    VmState.Suspended or VmState.Starting or VmState.ShuttingDown or
                    VmState.Saving or VmState.Stopping or VmState.Suspending or VmState.Resuming);
                if (!holdsMemory) continue;

                var assigned = row.Get(VmMemoryFields.AssignedMiB);
                if (isRunning) running++;
                if (!assigned.HasValue || assigned.Value < 0) { allocationComplete = false; continue; }
                double vmMiB = assigned.Value;
                if (vmMiB == 0) continue;

                Guid id = row.Get(VmIdentityFields.Id) is { HasValue: true } idVal ? idVal.Value : Guid.Empty;
                string? rawName = row.Get(VmIdentityFields.Name) is { HasValue: true } nameVal ? nameVal.Value : null;
                string name = string.IsNullOrWhiteSpace(rawName)
                    ? (id != Guid.Empty ? id.ToString("D") : "VM")
                    : rawName;
                bool? isDynamic = row.Get(VmMemoryFields.DynamicMemoryEnabled) is { HasValue: true } dyn ? dyn.Value : null;
                allocations.Add(new VmMemoryAllocationEntry(id, name, vmMiB / 1024.0, isDynamic));
            }

            allocations.Sort((a, b) => b.AllocatedGiB.CompareTo(a.AllocatedGiB));
            return new(total, running, allocationComplete ? allocations : null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch
        {
            return null;
        }
    }

    private static async Task<Dictionary<string, IReadOnlyList<SwitchEndpointInfo>>> ReadSwitchEndpointsAsync(IReadOnlyList<SwitchInfo> switches, CancellationToken cancellationToken)
    {
        var map = new Dictionary<string, IReadOnlyList<SwitchEndpointInfo>>(StringComparer.OrdinalIgnoreCase);
        foreach (var sw in switches)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var list = await HyperVSwitchService.GetFullSwitchNetworkStateAsync(sw.SwitchName).ConfigureAwait(false);
                map[sw.SwitchName] = list;
            }
            catch
            {
                map[sw.SwitchName] = [];
            }
        }
        return map;
    }

    private static Dictionary<Guid, NetworkThroughputSample> ReadAdapterMetrics(HostNetworkTelemetryService networkTelemetry, IReadOnlyList<SwitchUpstreamAdapter> adapters)
    {
        var map = new Dictionary<Guid, NetworkThroughputSample>();
        foreach (var adapter in adapters)
        {
            if (!adapter.LinkUp || string.IsNullOrWhiteSpace(adapter.Description)) continue;
            var sample = networkTelemetry.GetAdapterThroughput(adapter.Description);
            if (sample.Throughput is { } value) map[adapter.ConnectionId] = value;
        }
        return map;
    }

    private static Dictionary<string, NetworkThroughputSample> ReadSwitchMetrics(HostNetworkTelemetryService networkTelemetry, IReadOnlyList<SwitchInfo> switches)
    {
        var map = new Dictionary<string, NetworkThroughputSample>(StringComparer.OrdinalIgnoreCase);
        foreach (var sw in switches)
        {
            var sample = networkTelemetry.GetSwitchThroughput(sw.SwitchName);
            if (sample.Throughput is { } value) map[sw.SwitchName] = value;
        }
        return map;
    }

}
