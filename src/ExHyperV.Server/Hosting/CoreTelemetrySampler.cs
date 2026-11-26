using System.Net.NetworkInformation;
using ExHyperV.Services;

namespace ExHyperV.Server.Hosting;

internal sealed class CoreTelemetrySampler : ITelemetrySampler
{
    private HyperVCpuMonitorService? cpu;
    private HostCpuThreadMonitorService? threads;
    private HostDeviceTelemetryService? disk;
    private HostDeviceTelemetryService? gpu;
    private HostNetworkTelemetryService? network;
    private ArpSnoopService? arp;
    private readonly Dictionary<string, VmQueryService> queries = [];

    public async Task<object?> HostAsync(string metric, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        switch (metric)
        {
            case "cpu":
                var cores = (cpu ??= new()).GetCpuUsage().Where(c => c.IsHost)
                    .Select(c => new { c.CoreId, usage = c.Sample.IsValid ? (float?)c.Usage : null, c.Sample }).ToArray();
                return cores.Length == 0 ? null : cores;
            case "memory":
                return HostSystemInfoService.TryReadHostMemoryMiB() is { } ram ? new { ram.AvailableMiB, ram.TotalMiB } : null;
            case "hostThreads":
                return (threads ??= new()).GetThreadUsage();
            case "disk":
                var disks = await HostDiskService.GetHostDisksAsync();
                if (!disks.HasData) return TelemetrySocket.Issue(disks.Error);
                token.ThrowIfCancellationRequested();
                var numbers = disks.Data.Select(d => d.Number).Distinct().ToArray();
                var activity = (disk ??= new()).Read(numbers).Disks;
                return numbers.ToDictionary(number => number, number => (object?)activity.GetValueOrDefault(number)
                    ?? TelemetrySocket.Issue("磁盘正在预热或本次缺测。"));
            case "gpu":
                var readings = (gpu ??= new()).Read([]).Gpus;
                return readings.Count > 0 ? readings : null;
            case "network":
                network ??= new();
                return NetworkInterface.GetAllNetworkInterfaces().ToDictionary(n => n.Id, n => new
                    { n.Name, reading = network.GetAdapterThroughput(n.Description) });
            default: throw new ArgumentException("未知的宿主遥测指标。");
        }
    }

    public async Task<IReadOnlyDictionary<Guid, object?>> VirtualMachinesAsync(string metric, Guid[] ids, CancellationToken token)
    {
        VmQueryField[] fields = metric switch
        {
            "cpu" => [VmProcessorFields.UsagePercent, VmProcessorMetricFields.UsagePercent],
            "memory" => [VmMemoryFields.AssignedMiB, VmMemoryFields.DemandMiB, VmMemoryFields.GuestAvailableMiB],
            "disk" => [VmStorageFields.ReadBytesPerSecond, VmStorageFields.WriteBytesPerSecond],
            "network" => [VmNetworkFields.MacAddress, VmNetworkFields.SendBytesPerSecond, VmNetworkFields.ReceiveBytesPerSecond],
            "gpu" => [VmGpuPerformanceFields.Gpu3d, VmGpuPerformanceFields.GpuCopy, VmGpuPerformanceFields.GpuEncode, VmGpuPerformanceFields.GpuDecode],
            _ => throw new ArgumentException("未知的虚拟机遥测指标。")
        };
        if (!queries.TryGetValue(metric, out var query)) queries[metric] = query = new();
        if (metric == "network" && arp == null) { arp = new(); arp.Start(); }
        var result = await query.QueryAsync(new(fields, ids), token);
        if (!result.HasData) throw new InvalidOperationException(result.Error);
        return result.Data.VirtualMachines.ToDictionary(row => Guid.Parse(row.Key), row => (object?)Project(row, Lookup));
    }

    private (string? Address, string? Issue) Lookup(string mac) =>
        (arp?.TryGetIp(mac, out var ip) == true ? ip : null, arp?.LastFailure?.Error);

    internal static object Project(VmQueryRow row, Func<string, (string? Address, string? Issue)> lookup)
    {
        var values = row.Values.ToDictionary(pair => pair.Key, pair => new { pair.Value.State, pair.Value.ObservedAtUtc,
            value = pair.Value.UntypedValue, issue = pair.Value.Error == null ? null : Api.DisplayText.Clean(pair.Value.Error) });
        var collections = row.Collections.ToDictionary(pair => pair.Key, pair => new { pair.Value.State, pair.Value.ObservedAtUtc,
            issue = pair.Value.Error == null ? null : Api.DisplayText.Clean(pair.Value.Error), rows = pair.Value.Rows.Select(child => Project(child, lookup)).ToArray() });
        var mac = row.Get(VmNetworkFields.MacAddress);
        var observation = mac.HasValue && mac.Value != null ? lookup(mac.Value) : default;
        return new { row.Key, values, collections, observedIPv4 = observation.Address,
            arpIssue = observation.Issue == null ? null : Api.DisplayText.Clean(observation.Issue) };
    }

    public async ValueTask DisposeAsync()
    {
        // Schedule each release independently so one failing native provider cannot skip the others.
        var resources = queries.Values.Cast<object?>().Concat([cpu, threads, disk, gpu, network, arp]);
        await Task.WhenAll(resources.Select(async resource =>
        {
            if (resource is IAsyncDisposable asyncResource) await asyncResource.DisposeAsync();
            else if (resource is IDisposable syncResource) syncResource.Dispose();
        }));
    }
}
