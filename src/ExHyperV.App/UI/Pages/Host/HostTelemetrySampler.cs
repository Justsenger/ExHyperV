
using ExHyperV.App.Localization;
using ExHyperV.Models;
using ExHyperV.Services;

namespace ExHyperV.App.UI.Pages.Host;

internal sealed class HostTelemetrySampler
{
    internal static TimeSpan Uptime => TimeSpan.FromMilliseconds(Environment.TickCount64);

    public static HostInventorySnapshot CreateInitialSnapshot()
    {
        var memory = HostSystemInfoService.TryReadHostMemoryMiB();
        return new HostInventorySnapshot
        {
            HostName = Environment.MachineName,
            Arch = HostProcessorPlatformService.GetArchitecture(),
            LogicalProcessorCount = Math.Max(1, Environment.ProcessorCount),
            TotalMemoryGiB = memory is { TotalMiB: > 0 } ? memory.Value.TotalMiB / 1024d : null
        };
    }

    public async Task<HostInventorySnapshot> LoadSlicesAsync(
        HostInventorySnapshot? existing,
        HostInventorySlices slices,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var baseSnap = existing ?? CreateInitialSnapshot();
        if (slices == HostInventorySlices.None) return baseSnap;

        bool needSys = (slices & (HostInventorySlices.Environment | HostInventorySlices.Compute)) != 0;
        bool needDiag = (slices & HostInventorySlices.Environment) != 0
            || ((slices & HostInventorySlices.Compute) != 0 && baseSnap.Diagnostics == null);
        bool needGpu = (slices & HostInventorySlices.Gpu) != 0;
        bool needDisk = (slices & HostInventorySlices.Storage) != 0;
        bool needNet = (slices & HostInventorySlices.Network) != 0;
        bool needHostSettings = (slices & HostInventorySlices.Settings) != 0;
        bool needNuma = (slices & (HostInventorySlices.Compute | HostInventorySlices.Settings)) != 0;
        bool needPowerPlan = (slices & HostInventorySlices.Compute) != 0;
        bool needEnvFlags = (slices & HostInventorySlices.Environment) != 0;
        bool needPendingSwitch = (slices & (HostInventorySlices.Environment | HostInventorySlices.Settings)) != 0;
        bool needSched = (slices & (HostInventorySlices.Compute | HostInventorySlices.Settings)) != 0;
        bool needDiskPolicy = (slices & (HostInventorySlices.Storage | HostInventorySlices.Settings)) != 0;
        bool needGpuPolicy = (slices & (HostInventorySlices.Gpu | HostInventorySlices.Settings)) != 0;
        bool needOpenHcl = (slices & HostInventorySlices.Settings) != 0;
        bool needServerFlag = (slices & (HostInventorySlices.Environment | HostInventorySlices.Settings)) != 0;

        var sysTask = needSys ? HostSystemInfoService.GetSystemInfoAsync() : null;
        var diagTask = needDiag ? HyperVHostService.GetHyperVDiagnosticsAsync() : null;
        var gpuTask = needGpu ? SafeGetGpusAsync() : null;
        var diskTask = needDisk ? SafeGetDisksAsync() : null;
        var netTask = needNet ? SafeGetNetworkAsync() : null;
        var settingsTask = needHostSettings ? SafeGetHostSettingsAsync() : null;
        var numaTask = needNuma ? SafeGetNumaAsync() : null;

        bool cpuVirtOk = baseSnap.CpuVirtOk;
        bool gpuPv = baseSnap.GpuPvSupported;
        bool iommuOk = baseSnap.IommuOk;
        bool isServer = baseSnap.IsServerSystem;
        string? productSwitchReadError = baseSnap.ProductSwitchReadError;
        HostProductTypePendingSwitch? pendingSwitch = baseSnap.PendingProductSwitch;
        HyperVSchedulerType runSched = baseSnap.RunningScheduler;
        HyperVSchedulerType cfgSched = baseSnap.ConfiguredScheduler;
        string powerPlanName = baseSnap.ActivePowerPlanName;
        bool? autoMount = baseSnap.VolumeAutoMountEnabled;
        bool? nvmeEnabled = baseSnap.NativeNvmeEnabled;
        bool nvmeSupported = baseSnap.NativeNvmeSupported;
        bool? gpuRelaxed = baseSnap.GpuRestrictionsRelaxed;
        bool? openHcl = baseSnap.OpenHclFromFileEnabled;

        var syncTask = Task.Run(() =>
        {
            if (needEnvFlags)
            {
                cpuVirtOk = HyperVHostService.IsVirtualizationEnabled();
                gpuPv = HostGpuService.IsGpuPartitioningVersionSupported();
                iommuOk = HyperVHostService.IsIommuEnabled();
            }
            if (needServerFlag)
                isServer = HostProductTypeService.IsServerSystem();
            if (needSched)
            {
                runSched = HyperVSchedulerService.GetSchedulerType();
                if (needHostSettings)
                    cfgSched = HyperVSchedulerService.GetConfiguredSchedulerType();
            }
            if (needPowerPlan)
            {
                try
                {
                    var plans = HostPowerPlanService.GetPowerPlans();
                    var active = HostPowerPlanService.GetActivePowerPlanId();
                    if (active.HasValue)
                        powerPlanName = plans.FirstOrDefault(p => p.Id == active.Value)?.Name ?? string.Empty;
                }
                catch (System.ComponentModel.Win32Exception)
                {
                    // The active power plan is optional when the host power API is unavailable.
                }
            }
            if (needPendingSwitch)
            {
                try
                {
                    var pending = HostProductTypeService.GetPendingSwitch();
                    if (!pending.Success)
                    {
                        productSwitchReadError = CoreMessageResolver.ResolveError(pending);
                    }
                    else
                    {
                        productSwitchReadError = null;
                        pendingSwitch = pending.HasData ? pending.Data : null;
                    }
                }
                catch (Exception ex) { productSwitchReadError = CoreMessageResolver.ResolveException(ex); }
            }
            if (needDiskPolicy)
            {
                nvmeSupported = HostNvmeService.IsNativeNvmeSupported();
                var mount = HostVolumeAutoMountService.GetState();
                if (mount.HasData) autoMount = mount.Data;
                if (nvmeSupported)
                {
                    var nvme = HostNvmeService.IsNativeNvmeEnabled();
                    if (nvme.HasData) nvmeEnabled = nvme.Data;
                }
            }
            if (needGpuPolicy)
            {
                var gpu = HostGpuService.GetAssignmentRestrictionsRelaxed();
                if (gpu.HasData) gpuRelaxed = gpu.Data;
            }
            if (needOpenHcl)
            {
                try { openHcl = HostOpenHclService.IsFirmwareLoadFromFileEnabled(); }
                catch (Exception error) when (error is UnauthorizedAccessException or System.Security.SecurityException or IOException)
                {
                    // A restricted or unavailable registry key leaves the setting unknown.
                }
            }
        }, cancellationToken);

        var tasks = new List<Task> { syncTask };
        if (sysTask != null) tasks.Add(sysTask);
        if (diagTask != null) tasks.Add(diagTask);
        if (gpuTask != null) tasks.Add(gpuTask);
        if (diskTask != null) tasks.Add(diskTask);
        if (netTask != null) tasks.Add(netTask);
        if (settingsTask != null) tasks.Add(settingsTask);
        if (numaTask != null) tasks.Add(numaTask);

        await Task.WhenAll(tasks).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        var sysInfo = sysTask != null ? await sysTask : null;
        var diag = diagTask != null ? await diagTask : baseSnap.Diagnostics;
        var gpus = gpuTask != null ? await gpuTask : baseSnap.Gpus;
        var disks = diskTask != null ? await diskTask : baseSnap.Disks;
        var netTuple = netTask != null ? await netTask : default;
        var switches = netTask != null ? (IReadOnlyList<SwitchInfo>)netTuple.Switches : baseSnap.Switches;
        var adapters = netTask != null ? (IReadOnlyList<SwitchUpstreamAdapter>)netTuple.Adapters : baseSnap.PhysicalAdapters;
        var hostSettings = settingsTask != null ? await settingsTask : baseSnap.HostSettings;
        var numa = numaTask != null ? await numaTask : baseSnap.NumaSpanningEnabled;

        var nicAddresses = needNet ? HostDeviceTelemetryService.ReadAdapterAddresses() : baseSnap.AdapterAddresses;
        var endpoints = needNet ? await ReadSwitchEndpointsAsync(switches, cancellationToken).ConfigureAwait(false) : baseSnap.SwitchEndpoints;
        var memory = HostSystemInfoService.TryReadHostMemoryMiB();
        cancellationToken.ThrowIfCancellationRequested();

        return new HostInventorySnapshot
        {
            LoadedSlices = baseSnap.LoadedSlices | slices,
            HostName = Environment.MachineName,
            OsCaption = sysInfo != null ? sysInfo.Caption : baseSnap.OsCaption,
            CpuModel = sysInfo != null ? sysInfo.CpuModel : baseSnap.CpuModel,
            Arch = HostProcessorPlatformService.GetArchitecture(),
            LogicalProcessorCount = Math.Max(1, Environment.ProcessorCount),
            TotalMemoryGiB = memory is { TotalMiB: > 0 } ? memory.Value.TotalMiB / 1024d : baseSnap.TotalMemoryGiB,
            CpuVirtOk = cpuVirtOk,
            GpuPvSupported = gpuPv,
            IommuOk = iommuOk,
            IsServerSystem = isServer,
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
            VmSummary = baseSnap.VmSummary,
            Gpus = gpus,
            Disks = disks,
            Switches = switches,
            PhysicalAdapters = adapters,
            AdapterAddresses = nicAddresses,
            SwitchEndpoints = endpoints
        };
    }

    public static HostLiveSample SampleProcessorAndMemory(HostCpuThreadMonitorService cpu)
    {
        var report = cpu.GetThreadUsage();
        double? totalCpu = report.Sample.IsValid && report.Threads.Count > 0 && report.Threads.All(t => t.Sample.IsValid)
            ? Math.Clamp(report.Threads.Average(t => (double)t.TotalUsagePercent), 0, 100) : null;
        var memory = HostSystemInfoService.TryReadHostMemoryMiB();
        return new HostLiveSample
        {
            CpuPercent = totalCpu, CpuReport = report,
            Memory = memory is { TotalMiB: > 0 } m && m.AvailableMiB <= m.TotalMiB
                ? new((m.TotalMiB - m.AvailableMiB) / 1024d, m.TotalMiB / 1024d, m.AvailableMiB / 1024d,
                    (int)Math.Round(100d * (m.TotalMiB - m.AvailableMiB) / m.TotalMiB)) : null,
        };
    }

    private sealed record VmScopeData(
        HostVmSummary? Summary,
        IReadOnlyList<VmGpuUsageEntry>? Gpus,
        IReadOnlyList<VmPassthroughDiskEntry>? Storage,
        IReadOnlyList<VmNetworkUsageEntry>? Networks);

    public static async Task<HostLiveSample> SampleDevicesAsync(
        HostInventorySnapshot inventory,
        HostDeviceTelemetryService devices,
        HostNetworkTelemetryService network,
        HostTelemetryScope scope,
        bool refreshVms,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        double? freq = null;
        IReadOnlyDictionary<int, HostDiskActivity> diskStats = new Dictionary<int, HostDiskActivity>();
        IReadOnlyDictionary<string, HostGpuActivity> gpuStats = new Dictionary<string, HostGpuActivity>();
        IReadOnlyDictionary<Guid, NetworkThroughputSample> adapterStats = new Dictionary<Guid, NetworkThroughputSample>();
        IReadOnlyDictionary<string, NetworkThroughputSample> switchStats =
            new Dictionary<string, NetworkThroughputSample>(StringComparer.OrdinalIgnoreCase);

        if (scope.IncludeDevices)
        {
            var metrics = devices.Read(inventory.Disks.Select(d => d.Number).ToArray());
            freq = metrics.CpuGHz;
            diskStats = metrics.Disks;
            gpuStats = metrics.Gpus;
            adapterStats = ReadAdapterMetrics(network, inventory.PhysicalAdapters);
            switchStats = ReadSwitchMetrics(network, inventory.Switches);
        }

        var vmResult = refreshVms && scope.RequiresVmQuery
            ? await ReadVmScopeAsync(scope, token).ConfigureAwait(false)
            : null;
        return new HostLiveSample
        {
            CpuFreqGHz = freq,
            Disks = diskStats,
            Gpus = gpuStats,
            Adapters = adapterStats,
            Switches = switchStats,
            VmSummary = vmResult?.Summary,
            VmGpus = vmResult?.Gpus,
            VmStorage = vmResult?.Storage,
            VmNetworks = vmResult?.Networks
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

    private static async Task<HostVmSummary?> ReadVmSummaryAsync(CancellationToken cancellationToken) =>
        (await ReadVmScopeAsync(new HostTelemetryScope(false, false, true, false, false, false), cancellationToken)
            .ConfigureAwait(false))?.Summary;

    private static async Task<VmScopeData?> ReadVmScopeAsync(HostTelemetryScope scope, CancellationToken cancellationToken)
    {
        try
        {
            var fields = new List<VmQueryField>
            {
                VmIdentityFields.Id,
                VmIdentityFields.Name,
                VmIdentityFields.State
            };
            if (scope.IncludeVmMemory)
            {
                fields.Add(VmMemoryFields.DynamicMemoryEnabled);
                fields.Add(VmMemoryFields.AssignedMiB);
            }
            if (scope.IncludeVmGpu)
            {
                fields.Add(VmGpuFields.HostDeviceInstanceId);
                fields.Add(VmGpuPerformanceFields.Gpu3d);
                fields.Add(VmGpuPerformanceFields.GpuCopy);
                fields.Add(VmGpuPerformanceFields.GpuEncode);
                fields.Add(VmGpuPerformanceFields.GpuDecode);
                fields.Add(VmGpuPerformanceFields.HasGpuEngineInstance);
            }
            if (scope.IncludeVmStorage)
            {
                fields.Add(VmStorageFields.DiskType);
                fields.Add(VmStorageFields.DiskNumber);
                fields.Add(VmStorageFields.ReadBytesPerSecond);
                fields.Add(VmStorageFields.WriteBytesPerSecond);
                fields.Add(VmStorageFields.ReadOperationsPerSecond);
                fields.Add(VmStorageFields.WriteOperationsPerSecond);
                fields.Add(VmStorageFields.QueueLength);
            }
            if (scope.IncludeVmNetwork)
            {
                fields.Add(VmNetworkFields.Name);
                fields.Add(VmNetworkFields.IsConnected);
                fields.Add(VmNetworkFields.SwitchName);
                fields.Add(VmNetworkFields.SendBytesPerSecond);
                fields.Add(VmNetworkFields.ReceiveBytesPerSecond);
                fields.Add(VmNetworkFields.IpAddresses);
            }

            await using var query = new VmQueryService();
            var res = await query.QueryAsync(new VmQueryRequest(fields.ToArray()), cancellationToken)
                .ConfigureAwait(false);
            if (!res.HasData) return null;

            int total = res.Data!.VirtualMachines.Count;
            int running = 0;
            bool stateValid = true;
            bool allocationComplete = true;
            var allocations = scope.IncludeVmMemory ? new List<VmMemoryAllocationEntry>() : null;
            var gpuList = scope.IncludeVmGpu ? new List<VmGpuUsageEntry>() : null;
            var storageList = scope.IncludeVmStorage ? new List<VmPassthroughDiskEntry>() : null;
            var netList = scope.IncludeVmNetwork ? new List<VmNetworkUsageEntry>() : null;

            foreach (var row in res.Data.VirtualMachines)
            {
                var state = row.Get(VmIdentityFields.State);
                if (!state.HasValue) { stateValid = false; continue; }

                Guid id = row.Get(VmIdentityFields.Id) is { HasValue: true } idVal ? idVal.Value : Guid.Empty;
                string? rawName = row.Get(VmIdentityFields.Name) is { HasValue: true } nameVal ? nameVal.Value : null;
                string name = string.IsNullOrWhiteSpace(rawName)
                    ? (id != Guid.Empty ? id.ToString("D") : "VM")
                    : rawName;

                bool isRunning = state.Value == VmState.Running;
                if (isRunning) running++;

                bool holdsMemory = isRunning || (state.Value is
                    VmState.Suspended or VmState.Starting or VmState.ShuttingDown or
                    VmState.Saving or VmState.Stopping or VmState.Suspending or VmState.Resuming);

                if (allocations != null && holdsMemory)
                {
                    var assigned = row.Get(VmMemoryFields.AssignedMiB);
                    if (!assigned.HasValue || assigned.Value < 0) allocationComplete = false;
                    else if (assigned.Value > 0)
                    {
                        bool? isDynamic = row.Get(VmMemoryFields.DynamicMemoryEnabled) is { HasValue: true } dyn ? dyn.Value : null;
                        allocations.Add(new VmMemoryAllocationEntry(id, name, assigned.Value / 1024.0, isDynamic));
                    }
                }

                if (gpuList != null)
                {
                    var gpuAssign = row.GetCollection(VmQueryCollection.GpuAssignments);
                    var assignedIds = gpuAssign.Rows
                        .Select(r => r.Get(VmGpuFields.HostDeviceInstanceId))
                        .Where(v => v.HasValue && !string.IsNullOrWhiteSpace(v.Value))
                        .Select(v => v.Value!)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                    bool hasEngine = row.Get(VmGpuPerformanceFields.HasGpuEngineInstance) is { HasValue: true } eng && eng.Value;
                    if (assignedIds.Length > 0 || (isRunning && hasEngine))
                    {
                        double? ReadPct(VmQueryField<double> f) =>
                            isRunning && row.Get(f) is { HasValue: true } val && double.IsFinite(val.Value) ? val.Value : null;
                        gpuList.Add(new VmGpuUsageEntry(
                            id, name, isRunning, assignedIds,
                            ReadPct(VmGpuPerformanceFields.Gpu3d),
                            ReadPct(VmGpuPerformanceFields.GpuCopy),
                            ReadPct(VmGpuPerformanceFields.GpuEncode),
                            ReadPct(VmGpuPerformanceFields.GpuDecode),
                            hasEngine));
                    }
                }

                if (storageList != null && holdsMemory)
                {
                    var disks = row.GetCollection(VmQueryCollection.Storage);
                    foreach (var dRow in disks.Rows)
                    {
                        var diskType = dRow.Get(VmStorageFields.DiskType);
                        var diskNum = dRow.Get(VmStorageFields.DiskNumber);
                        if (!diskType.HasValue || !string.Equals(diskType.Value, "Physical", StringComparison.OrdinalIgnoreCase) || !diskNum.HasValue)
                            continue;
                        double? ReadLongDouble(VmQueryField<long> f) => dRow.Get(f) is { HasValue: true } v ? v.Value : null;
                        long? ReadLong(VmQueryField<long> f) => dRow.Get(f) is { HasValue: true } v ? v.Value : null;
                        storageList.Add(new VmPassthroughDiskEntry(
                            id, name, diskNum.Value,
                            ReadLongDouble(VmStorageFields.ReadBytesPerSecond),
                            ReadLongDouble(VmStorageFields.WriteBytesPerSecond),
                            ReadLong(VmStorageFields.ReadOperationsPerSecond),
                            ReadLong(VmStorageFields.WriteOperationsPerSecond),
                            ReadLong(VmStorageFields.QueueLength)));
                    }
                }

                if (netList != null)
                {
                    var nics = row.GetCollection(VmQueryCollection.NetworkAdapters);
                    var adapters = new List<VmNetworkAdapterUsage>();
                    foreach (var nRow in nics.Rows)
                    {
                        string? swName = nRow.Get(VmNetworkFields.SwitchName) is { HasValue: true } sw ? sw.Value : null;
                        bool connected = nRow.Get(VmNetworkFields.IsConnected) is { HasValue: true } c && c.Value;
                        if (!connected || string.IsNullOrWhiteSpace(swName)) continue;
                        string nicName = nRow.Get(VmNetworkFields.Name) is { HasValue: true } nn && !string.IsNullOrWhiteSpace(nn.Value) ? nn.Value! : "NIC";
                        ulong? tx = isRunning && nRow.Get(VmNetworkFields.SendBytesPerSecond) is { HasValue: true } sVal ? sVal.Value : null;
                        ulong? rx = isRunning && nRow.Get(VmNetworkFields.ReceiveBytesPerSecond) is { HasValue: true } rVal ? rVal.Value : null;
                        string? ip = nRow.Get(VmNetworkFields.IpAddresses) is { HasValue: true } ips ? ips.Value?.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) : null;
                        adapters.Add(new VmNetworkAdapterUsage(nicName, swName!, true, tx, rx, ip));
                    }
                    if (adapters.Count > 0)
                        netList.Add(new VmNetworkUsageEntry(id, name, isRunning, adapters));
                }
            }

            HostVmSummary? summary = null;
            if (allocations != null && stateValid)
            {
                allocations.Sort((a, b) => b.AllocatedGiB.CompareTo(a.AllocatedGiB));
                summary = new(total, running, allocationComplete ? allocations : null);
            }

            return new VmScopeData(summary, gpuList, storageList, netList);
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
        if (switches.Count == 0) return map;

        var results = await Task.WhenAll(switches.Select(async sw =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var list = await HyperVSwitchService.GetFullSwitchNetworkStateAsync(sw.SwitchName).ConfigureAwait(false);
                return (sw.SwitchName, List: (IReadOnlyList<SwitchEndpointInfo>)list);
            }
            catch
            {
                return (sw.SwitchName, List: (IReadOnlyList<SwitchEndpointInfo>)[]);
            }
        })).ConfigureAwait(false);

        foreach (var (name, list) in results)
            map[name] = list;
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
